//! Durable upload confirmations in the existing control extent. Append-only
//! authenticated pages avoid a COW tree transaction for each uploaded object.
//! A COW checkpoint changes the epoch BEFORE any old journal page is reused.
use super::{codec, cloud, store::*, tree, *};
use std::collections::BTreeSet;

const FIRST_PAGE: u64 = 4; // Config mirrors at 0/3, committed roots at 1/2.
const FRAME_KIND: u8 = 8;
const PREFIX: usize = 72;
const MAX_PENDING: usize = 256;

#[derive(Clone, Default)]
pub(super) struct View {
    pub job_id: String,
    pub epoch: u64,
    pub objects: BTreeSet<u64>,
}
impl View {
    pub fn matches(&self, job: &cloud::Job) -> bool {
        self.job_id == job.id && self.epoch == job.receipt_epoch && job.phase == "ready"
    }
    pub fn contains(&self, job: &cloud::Job, oid: u64) -> bool {
        self.matches(job) && self.objects.contains(&oid)
    }
    pub fn count(&self, job: &cloud::Job) -> u64 {
        if self.matches(job) { self.objects.len() as u64 } else { 0 }
    }
}
#[derive(Default)]
pub(super) struct Log {
    pub view: Arc<View>,
    next: u64,
    previous: [u8; 32],
    uncertain_tail: bool,
}
impl Log {
    fn new(job: &cloud::Job) -> Self {
        Self { view: Arc::new(View { job_id: job.id.clone(), epoch: job.receipt_epoch,
            objects: BTreeSet::new() }), ..Self::default() }
    }
}

impl Store {
    pub fn receipt_view(&self) -> Arc<View> { self.receipts.view.clone() }

    pub fn recover_receipts(&mut self) -> Result<()> {
        let Some(job) = self.cloud.job.clone().filter(|j| j.phase == "ready") else { return Ok(()); };
        let id = Uuid::parse_str(&job.id).map_err(|_| Error::Integrity("receipt job identity".into()))?;
        let mut log = Log::new(&job);
        for index in 0..self.geometry().pages - FIRST_PAGE {
            let offset = (FIRST_PAGE + index) * PAGE as u64;
            let mut frame = [0; PAGE];
            self.device.read(offset, &mut frame)?;
            if frame.iter().all(|b| *b == 0) { break; }
            let Ok(payload) = self.crypto.unframe(FRAME_KIND, offset, &frame) else {
                log.uncertain_tail = true; break;
            };
            if payload.len() < PREFIX { log.uncertain_tail = true; break; }
            // Valid pages from an older job/epoch are the unwritten tail.
            if payload[..16] != *id.as_bytes() || number(&payload, 16) != job.receipt_epoch { break; }
            let count = number(&payload, 64) as usize;
            if number(&payload, 24) != index || payload[32..64] != log.previous
                || !(1..=128).contains(&count) || payload.len() != PREFIX + count * 8 {
                log.uncertain_tail = true; break;
            }
            let mut ids = BTreeSet::new();
            for record in payload[PREFIX..].chunks_exact(8) {
                let oid = number(record, 0);
                if !ids.insert(oid) || log.view.objects.contains(&oid)
                    || tree::get(self, &SET, job.add, oid)?.is_none()
                    || tree::get(self, &SET, job.receipts, oid)?.is_some() {
                    return Err(Error::Integrity("receipt journal membership".into()));
                }
            }
            if log.view.objects.len() + ids.len() > MAX_PENDING {
                return Err(Error::Integrity("receipt journal exceeds checkpoint bound".into()));
            }
            Arc::make_mut(&mut log.view).objects.extend(ids);
            log.previous = codec::hash(&frame);
            log.next += 1;
        }
        if log.uncertain_tail {
            *self.diagnostics.entry("receipt_recovered_uncertain_tails".into()).or_default() += 1;
        }
        self.receipts = log;
        self.publish_receipts()
    }

    fn publish_receipts(&mut self) -> Result<()> {
        let mut registry = self.readers.lock().map_err(|_| Error::Poisoned)?;
        registry.receipts = self.receipts.view.clone();
        registry.volatile_revision += 1;
        for (key, value) in &self.diagnostics {
            if key.starts_with("receipt_") { registry.metrics.insert(key.clone(), *value); }
        }
        Ok(())
    }

    pub fn append_receipts(&mut self, mut objects: BTreeMap<u64, Object>) -> Result<()> {
        let job = self.cloud.job.clone().ok_or_else(|| Error::Invalid("no receipt job".into()))?;
        if !self.receipts.view.matches(&job) { self.receipts = Log::new(&job); }
        for oid in objects.keys().copied().collect::<Vec<_>>() {
            if self.receipts.view.contains(&job, oid) || tree::get(self, &SET, job.receipts, oid)?.is_some() {
                objects.remove(&oid);
            }
        }
        if objects.is_empty() { return Ok(()); }
        if self.receipts.uncertain_tail || self.receipts.next >= self.geometry().pages - FIRST_PAGE
            || self.receipts.view.objects.len() + objects.len() > MAX_PENDING {
            self.checkpoint_receipts()?;
        }
        let job = self.cloud.job.as_ref().unwrap();
        let id = Uuid::parse_str(&job.id).map_err(|_| Error::Integrity("receipt job identity".into()))?;
        let mut payload = Vec::with_capacity(PREFIX + objects.len() * 8);
        payload.extend_from_slice(id.as_bytes());
        payload.extend_from_slice(&job.receipt_epoch.to_le_bytes());
        payload.extend_from_slice(&self.receipts.next.to_le_bytes());
        payload.extend_from_slice(&self.receipts.previous);
        payload.extend_from_slice(&(objects.len() as u64).to_le_bytes());
        for oid in objects.keys() { payload.extend_from_slice(&oid.to_le_bytes()); }
        let offset = (FIRST_PAGE + self.receipts.next) * PAGE as u64;
        let frame = self.crypto.frame(FRAME_KIND, self.root.seq, offset, &payload)?;
        let persisted = self.device.write(offset, &frame).and_then(|_| self.device.sync());
        if let Err(error) = persisted {
            // Never reuse an uncertain append in this process, and never report
            // confirmation or allow publication after a failed durability barrier.
            self.failure = Some(error.to_string());
            if let Ok(mut registry) = self.readers.lock() { registry.failure = self.failure.clone(); }
            return Err(error);
        }
        #[cfg(test)]
        receipt_crash_point("receipt_after_sync");
        self.receipts.previous = codec::hash(&frame);
        self.receipts.next += 1;
        Arc::make_mut(&mut self.receipts.view).objects.extend(objects.keys().copied());
        *self.diagnostics.entry("receipt_log_pages".into()).or_default() += 1;
        *self.diagnostics.entry("receipt_log_bytes".into()).or_default() += PAGE as u64;
        *self.diagnostics.entry("receipt_log_flushes".into()).or_default() += 1;
        *self.diagnostics.entry("receipt_log_objects".into()).or_default() += objects.len() as u64;
        self.publish_receipts()
    }

    pub fn checkpoint_receipts(&mut self) -> Result<()> {
        let mut cloud = self.cloud.clone();
        let job = cloud.job.as_mut().ok_or_else(|| Error::Invalid("no receipt checkpoint job".into()))?;
        if !self.receipts.view.matches(job) { return Ok(()); }
        let ids = self.receipts.view.objects.iter().copied().collect::<Vec<_>>();
        let rows = ids.iter().map(|oid| (*oid, Some(vec![1]))).collect::<Vec<_>>();
        job.receipt_epoch = job.receipt_epoch.checked_add(1)
            .ok_or_else(|| Error::Invalid("receipt epoch exhausted".into()))?;
        self.transaction(|tx| {
            let job = cloud.job.as_mut().unwrap();
            job.receipts = tx.set(&SET, job.receipts, &rows)?;
            for oid in &ids {
                let mut object = tx.object(*oid)?;
                object.sync_state = 2;
                tx.objects.insert(*oid, object);
            }
            tx.save_cloud(&cloud)
        })?;
        self.cloud = cloud;
        #[cfg(test)]
        receipt_crash_point("receipt_after_checkpoint");
        self.receipts = Log::new(self.cloud.job.as_ref().unwrap());
        *self.diagnostics.entry("receipt_checkpoints".into()).or_default() += 1;
        self.publish_receipts()
    }
}
fn number(bytes: &[u8], at: usize) -> u64 {
    u64::from_le_bytes(bytes[at..at + 8].try_into().unwrap())
}
#[cfg(test)]
fn receipt_crash_point(point: &str) {
    if std::env::var("OD_V4_RECEIPT_CRASH").as_deref() == Ok(point) {
        std::process::exit(77);
    }
}
