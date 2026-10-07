use super::store::{
    Page as StoredPage, ReadLease, ReadRegistry, Root, Store, DIRTY, OBJECTS, PAGES, PORTABLE,
};
use super::tree::Storage;
use super::*;
use std::collections::{HashMap, VecDeque};
use std::sync::atomic::AtomicU64;
use zeroize::Zeroizing;

const DIRTY_LIMIT: usize = 64 * 1024 * 1024;
const DIRTY_TRIGGER: usize = 16 * 1024 * 1024;
const CACHE_NODES: usize = 4096;
type Bytes = Arc<super::crypto_work::PlainPage>;
type Changes = BTreeMap<u64, Option<Bytes>>;

#[derive(Default)]
struct Views {
    dirty: Changes,
    inflight: Option<Arc<Changes>>,
    revision: u64,
    failure: Option<String>,
}
#[derive(Default)]
struct ReadCache {
    nodes: HashMap<MetaRef, Arc<Vec<u8>>>,
    order: VecDeque<MetaRef>,
}
impl ReadCache {
    fn insert(&mut self, key: MetaRef, value: Vec<u8>) {
        if self.nodes.contains_key(&key) {
            return;
        }
        while self.nodes.len() >= CACHE_NODES {
            if let Some(old) = self.order.pop_front() {
                self.nodes.remove(&old);
            }
        }
        self.order.push_back(key);
        self.nodes.insert(key, Arc::new(value));
    }
}
pub(super) struct Shared {
    pub store: Mutex<Store>,
    pub flush_lock: Mutex<()>,
    pub restore_source: Mutex<Option<Arc<Shared>>>,
    pub provider: super::lazy::ProviderRegistry,
    pub cache_runtime: Arc<super::cache::Runtime>,
    pub read_only: AtomicBool,
    pub identity: Mutex<Vec<super::replica::IdentityRegion>>,
    pub device: Arc<Device>,
    pub crypto: Arc<Crypto>,
    pub readers: Arc<Mutex<ReadRegistry>>,
    views: Mutex<Views>,
    cache: Arc<Mutex<ReadCache>>,
    wake: Condvar,
    stop: AtomicBool,
    capacity: u64,
    id: Uuid,
    encrypted: bool,
    read_bytes: AtomicU64,
    write_bytes: AtomicU64,
    changed: AtomicU64,
    deduplicated: AtomicU64,
}
pub struct Volume {
    pub(super) shared: Arc<Shared>,
    worker: Mutex<Option<JoinHandle<()>>>,
}

pub(super) struct Reader {
    pub device: Arc<Device>,
    pub crypto: Arc<Crypto>,
    pub root: Root,
    cache: Arc<Mutex<ReadCache>>,
    pub cache_runtime: Arc<super::cache::Runtime>,
}
impl Reader {
    pub(super) fn geometry(&self) -> Geometry {
        self.crypto.geometry
    }
    pub(super) fn object_size(&self) -> u64 {
        self.crypto.geometry.object_size
    }

    pub(super) fn blob(&mut self, reference: MetaRef) -> Result<Vec<u8>> {
        store::read_blob(&self.device, &self.crypto, reference)
    }
    pub(super) fn device_len(&self) -> Result<u64> {
        self.device.len()
    }
    pub(super) fn page(&mut self, index: u64) -> Result<Option<StoredPage>> {
        tree::get(self, &PAGES, self.root.index, index)?
            .map(|b| StoredPage::decode(&b))
            .transpose()
    }
    pub(super) fn object(&mut self, oid: u64) -> Result<store::Object> {
        let value = tree::get(self, &OBJECTS, self.root.objects, oid)?
            .ok_or_else(|| Error::Integrity("read view object absent".into()))?;
        store::Object::decode(oid, &value)
    }
    pub(super) fn read_page(&mut self, index: u64) -> Result<Zeroizing<[u8; PAGE]>> {
        let mut bytes = Zeroizing::new([0; PAGE]);
        if let Some(p) = self.page(index)? {
            let object = self.object(p.reference.object)?;
            if object.missing {
                return Err(Error::Missing(RemoteObject::from_object(
                    &object,
                    self.object_size(),
                )));
            }
            if object.kind != 1 || p.reference.slot >= object.used as u64 {
                return Err(Error::Integrity("read view slot outside object".into()));
            }
            self.cache_runtime
                .touch(object.oid, index * PAGE as u64, PAGE as u64);
            self.device.read(
                object.extent * self.object_size()
                    + (self.geometry().payload_pages + p.reference.slot) * PAGE as u64,
                bytes.as_mut(),
            )?;
            self.crypto.decode_page(index, p.reference, &mut bytes)?;
            if codec::hash(bytes.as_ref()) != p.digest {
                return Err(Error::Integrity("read view content hash".into()));
            }
        }
        Ok(bytes)
    }
    fn read(&mut self, offset: u64, out: &mut [u8]) -> Result<()> {
        let mut done = 0;
        while done < out.len() {
            let at = offset + done as u64;
            let within = at as usize % PAGE;
            let take = (PAGE - within).min(out.len() - done);
            let page = self.read_page(at / PAGE as u64)?;
            out[done..done + take].copy_from_slice(&page[within..within + take]);
            done += take;
        }
        Ok(())
    }
}
impl Storage for Reader {
    fn read_node(&mut self, reference: MetaRef) -> Result<Vec<u8>> {
        if let Some(bytes) = self
            .cache
            .lock()
            .map_err(|_| Error::Poisoned)?
            .nodes
            .get(&reference)
            .cloned()
        {
            return Ok((*bytes).clone());
        }
        let offset = if reference.offset & PORTABLE != 0 {
            let address = reference.offset & !PORTABLE;
            let object = self.object(address / self.object_size())?;
            super::portable_validation::validate_node_reference(reference, &object, self.crypto.geometry)?;
            if object.missing {
                return Err(Error::Missing(RemoteObject::from_object(
                    &object,
                    self.object_size(),
                )));
            }
            object.extent * self.object_size() + address % self.object_size()
        } else {
            reference.offset
        };
        let mut frame = [0; PAGE];
        self.device.read(offset, &mut frame)?;
        if codec::hash(&frame) != reference.hash {
            return Err(Error::Integrity("read view node hash".into()));
        }
        let bytes = self.crypto.unframe(codec::NODE, reference.offset, &frame)?;
        self.cache
            .lock()
            .map_err(|_| Error::Poisoned)?
            .insert(reference, bytes.clone());
        Ok(bytes)
    }
    fn write_node(&mut self, _: &[u8]) -> Result<MetaRef> {
        Err(Error::Invalid("immutable read view".into()))
    }
}

impl Volume {
    pub fn object_size(&self) -> u64 {
        self.shared.crypto.geometry.object_size
    }
    pub(super) fn geometry(&self) -> Geometry {
        self.shared.crypto.geometry
    }

    pub(super) fn cached_page_indices(
        &self,
        first: u64,
        end: u64,
    ) -> Result<std::collections::BTreeSet<u64>> {
        let views = self.shared.views.lock().map_err(|_| Error::Poisoned)?;
        check_views(&views)?;
        let mut keys = std::collections::BTreeSet::new();
        if let Some(inflight) = &views.inflight {
            keys.extend(inflight.range(first..end).map(|(p, _)| *p));
        }
        keys.extend(views.dirty.range(first..end).map(|(p, _)| *p));
        Ok(keys)
    }
    pub(super) fn read_context(&self) -> Result<(ReadLease, Reader)> {
        let lease = ReadLease::acquire(self.shared.readers.clone())?;
        let reader = self.shared.reader(&lease.root);
        Ok((lease, reader))
    }
    pub fn create(path: impl AsRef<Path>, capacity: u64, password: Option<&str>) -> Result<()> {
        Self::create_sized(path, capacity, password, OBJECT)
    }
    pub fn create_sized(
        path: impl AsRef<Path>,
        capacity: u64,
        password: Option<&str>,
        object_size: u64,
    ) -> Result<()> {
        if !(64 * 1024 * 1024..=MAX_CAPACITY).contains(&capacity) || !capacity.is_multiple_of(512) {
            return Err(Error::Invalid(
                "capacity must be aligned and between 64 MiB and 8 TiB".into(),
            ));
        }
        let (config, crypto) = Config::create_sized(capacity, password, object_size)?;
        let device = Device::open(path.as_ref(), true)?;
        Store::create(device, config, crypto)?;
        Ok(())
    }
    pub fn open(path: impl AsRef<Path>, password: Option<&str>) -> Result<Self> {
        let device = Device::open(path.as_ref(), false)?;
        let config = read_config(&device)?;
        let crypto = config.unlock(password)?;
        Self::from_store(Store::open(device, config, crypto)?)
    }
    pub fn inspect(path: impl AsRef<Path>) -> Result<Info> {
        use std::io::{Read, Seek, SeekFrom};
        let mut file = std::fs::File::open(path)?;
        let mut config = None;
        for offset in [0, 3 * PAGE as u64] {
            let mut bytes = [0; PAGE];
            if file.seek(SeekFrom::Start(offset)).is_ok() && file.read_exact(&mut bytes).is_ok() {
                if let Ok(value) = Config::decode(&bytes) {
                    if value.format_version == 4 {
                        config = Some(value);
                        break;
                    }
                }
            }
        }
        let c = config.ok_or_else(|| {
            Error::Invalid(
                "not a format-4 container; open old volumes with the previous release".into(),
            )
        })?;
        Ok(Info {
            id: c.id,
            capacity_bytes: c.capacity_bytes,
            encrypted: c.encrypted,
            page_size: PAGE,
            segment_size: c.object_size,
            object_size: c.object_size,
            format_version: 4,
            dirty_bytes: 0,
            allocated_pages: 0,
            segment_count: file.metadata()?.len() / c.object_size,
            authenticated: false,
            snapshot_count: 0,
            background_error: None,
            data_generation: 0,
            published_generation: 0,
            restore_incomplete: c.restoring,
        })
    }
    pub(super) fn from_store(mut store: Store) -> Result<Self> {
        let cache_runtime = Arc::new(super::cache::Runtime::new(
            store.device.allocated_bytes().unwrap_or(0),
        ));
        store.cache_runtime = Some(cache_runtime.clone());
        let shared = Arc::new(Shared {
            identity: Mutex::new(store.cloud.replica.as_ref().map(|r|r.identity.clone()).unwrap_or_default()),
            device: store.device.clone(),
            crypto: store.crypto.clone(),
            readers: store.readers.clone(),
            capacity: store.config.capacity_bytes,
            id: store.config.id,
            encrypted: store.config.encrypted,
            store: Mutex::new(store),
            flush_lock: Mutex::new(()),
            restore_source: Mutex::new(None),
            provider: super::lazy::ProviderRegistry::default(),
            cache_runtime,
            read_only: AtomicBool::new(false),
            views: Mutex::new(Views::default()),
            cache: Arc::new(Mutex::new(ReadCache::default())),
            wake: Condvar::new(),
            stop: AtomicBool::new(false),
            read_bytes: AtomicU64::new(0),
            write_bytes: AtomicU64::new(0),
            changed: AtomicU64::new(0),
            deduplicated: AtomicU64::new(0),
        });
        let background = shared.clone();
        let worker = thread::Builder::new()
            .name("overlaydisk-v4-commit".into())
            .spawn(move || loop {
                let state = match background.views.lock() {
                    Ok(s) => s,
                    Err(_) => return,
                };
                let state = if state.dirty.len() * PAGE >= DIRTY_TRIGGER {
                    state
                } else {
                    match background
                        .wake
                        .wait_timeout(state, Duration::from_millis(250))
                    {
                        Ok((s, _)) => s,
                        Err(_) => return,
                    }
                };
                if background.stop.load(Ordering::Acquire) {
                    return;
                }
                let persist = !state.dirty.is_empty() && state.failure.is_none();
                drop(state);
                if persist {
                    let _ = background.flush();
                }
            })?;
        Ok(Self {
            shared,
            worker: Mutex::new(Some(worker)),
        })
    }
    pub fn capacity(&self) -> u64 {
        self.shared.capacity
    }
    fn bounds(&self, offset: u64, length: usize) -> Result<()> {
        if !offset.is_multiple_of(512)
            || !length.is_multiple_of(512)
            || offset
                .checked_add(length as u64)
                .is_none_or(|end| end > self.capacity())
        {
            return Err(Error::Invalid(
                "I/O must be 512-byte aligned and within the volume".into(),
            ));
        }
        Ok(())
    }
    pub fn read(&self, offset: u64, output: &mut [u8]) -> Result<()> {
        self.with_hydration(|| self.read_impl(offset, output, false))
    }
    pub fn read_persistent(&self, offset: u64, output: &mut [u8]) -> Result<()> {
        self.with_hydration(|| self.read_impl(offset, output, true))
    }
    fn read_impl(&self, offset: u64, output: &mut [u8], persistent: bool) -> Result<()> {
        self.bounds(offset, output.len())?;
        if output.is_empty() {
            return Ok(());
        }
        let (lease, changes) = self.shared.capture(offset, output.len(), persistent)?;
        if lease.root.restore_required {
            return Err(Error::Invalid("restore is not complete".into()));
        }
        let mut reader = self.shared.reader(&lease.root);
        let first = offset / PAGE as u64;
        let end = (offset + output.len() as u64).div_ceil(PAGE as u64);
        let mut page = first;
        while page < end {
            let stop = (page + 256).min(end);
            let mut plan = Vec::new();
            for index in page..stop {
                if let Some(value) = changes.get(&index) {
                    if let Some(bytes) = value {
                        copy_to_output(offset, output, index, bytes);
                    } else {
                        copy_to_output(offset, output, index, &[0; PAGE]);
                    }
                    plan.push(None);
                } else if let Some(reference) = reader.page(index)? {
                    let object = reader.object(reference.reference.object)?;
                    if object.missing {
                        return Err(Error::Missing(RemoteObject::from_object(
                            &object,
                            self.object_size(),
                        )));
                    }
                    if object.kind != 1 || reference.reference.slot >= object.used as u64 {
                        return Err(Error::Integrity("read slot outside object".into()));
                    }
                    let physical = object.extent * self.object_size()
                        + (self.geometry().payload_pages + reference.reference.slot) * PAGE as u64;
                    plan.push(Some((reference, physical, object.origin_backed)));
                } else {
                    copy_to_output(offset, output, index, &[0; PAGE]);
                    plan.push(None);
                }
            }
            let mut at = 0;
            while at < plan.len() {
                let Some((_, physical, _)) = &plan[at] else {
                    at += 1;
                    continue;
                };
                let mut until = at + 1;
                while until < plan.len()
                    && plan[until]
                        .as_ref()
                        .is_some_and(|(_, p, _)| *p == *physical + (until - at) as u64 * PAGE as u64)
                {
                    until += 1;
                }
                let mut raw = Zeroizing::new(vec![0u8; (until - at) * PAGE]);
                reader.cache_runtime.touch(
                    plan[at].as_ref().unwrap().0.reference.object,
                    (page + at as u64) * PAGE as u64,
                    raw.len() as u64,
                );
                reader.device.read(*physical, &mut raw)?;
                for (i, bytes) in raw.chunks_exact_mut(PAGE).enumerate() {
                    let index = page + (at + i) as u64;
                    let reference = &plan[at + i].as_ref().unwrap().0;
                    let bytes: &mut [u8; PAGE] = bytes.try_into().unwrap();
                    reader
                        .crypto
                        .decode_page(index, reference.reference, bytes)?;
                    if codec::hash(bytes) != reference.digest {
                        return Err(Error::Integrity("read content digest".into()));
                    }
                    if plan[at+i].as_ref().unwrap().2 {
                        self.apply_identity(index * PAGE as u64,bytes)?;
                    }
                    copy_to_output(offset, output, index, bytes);
                }
                at = until;
            }
            page = stop;
        }
        self.shared
            .read_bytes
            .fetch_add(output.len() as u64, Ordering::Relaxed);
        Ok(())
    }
    pub fn write(&self, offset: u64, input: &[u8]) -> Result<()> {
        self.with_hydration(|| self.write_impl(offset, input))
    }
    fn write_impl(&self, offset: u64, input: &[u8]) -> Result<()> {
        if self.shared.read_only.load(Ordering::Acquire) {
            return Err(Error::Invalid("volume is read-only".into()));
        }
        self.bounds(offset, input.len())?;
        if input.is_empty() {
            return Ok(());
        }
        if (offset % PAGE as u64 + input.len() as u64).div_ceil(PAGE as u64)
            > (DIRTY_LIMIT / PAGE) as u64
        {
            return Err(Error::Invalid("single write exceeds cache capacity".into()));
        }
        self.modify(offset, input.len(), Some(input))?;
        self.shared
            .write_bytes
            .fetch_add(input.len() as u64, Ordering::Relaxed);
        Ok(())
    }
    pub fn trim(&self, offset: u64, length: u64) -> Result<()> {
        self.with_hydration(|| self.trim_impl(offset, length))
    }
    fn trim_impl(&self, offset: u64, length: u64) -> Result<()> {
        if self.shared.read_only.load(Ordering::Acquire) {
            return Err(Error::Invalid("volume is read-only".into()));
        }
        let length = usize::try_from(length).map_err(|_| Error::Invalid("trim length".into()))?;
        self.bounds(offset, length)?;
        if length == 0 {
            return Ok(());
        }
        let end = offset + length as u64;
        let first_full = offset.div_ceil(PAGE as u64);
        let last_full = end / PAGE as u64;
        if first_full > last_full || offset / PAGE as u64 == (end - 1) / PAGE as u64 {
            return self.modify(offset, length, None);
        }
        if !offset.is_multiple_of(PAGE as u64) {
            self.modify(offset, (first_full * PAGE as u64 - offset) as usize, None)?;
        }
        let (lease, cached) = self.shared.capture(
            first_full * PAGE as u64,
            ((last_full - first_full) * PAGE as u64) as usize,
            false,
        )?;
        if lease.root.restore_required {
            return Err(Error::Invalid("restore is not complete".into()));
        }
        let mut reader = self.shared.reader(&lease.root);
        let mut cursor = first_full;
        // Enumerate allocated and cached pages, never each page of a large unallocated range.
        while cursor < last_full {
            let root = reader.root.index;
            let mut keys = tree::scan_after(&mut reader, &PAGES, root, cursor, 256)?
                .into_iter()
                .map(|(p, _)| p)
                .take_while(|p| *p < last_full)
                .collect::<std::collections::BTreeSet<_>>();
            keys.extend(
                cached
                    .range(cursor..last_full)
                    .take(256)
                    .filter_map(|(p, v)| v.as_ref().map(|_| *p)),
            );
            let keys = keys.into_iter().take(256).collect::<Vec<_>>();
            if keys.is_empty() {
                break;
            }
            let mut at = 0;
            while at < keys.len() {
                let mut stop = at + 1;
                while stop < keys.len() && keys[stop] == keys[stop - 1] + 1 {
                    stop += 1;
                }
                self.modify(keys[at] * PAGE as u64, (stop - at) * PAGE, None)?;
                at = stop;
            }
            cursor = keys.last().unwrap() + 1;
        }
        if !end.is_multiple_of(PAGE as u64) {
            self.modify(last_full * PAGE as u64, (end % PAGE as u64) as usize, None)?;
        }
        Ok(())
    }
    fn modify(&self, offset: u64, length: usize, input: Option<&[u8]>) -> Result<()> {
        loop {
            let (lease, visible, revision) = {
                let views = self.shared.views.lock().map_err(|_| Error::Poisoned)?;
                check_views(&views)?;
                let first = offset / PAGE as u64;
                let end = (offset + length as u64).div_ceil(PAGE as u64);
                let additional = (first..end)
                    .filter(|p| !views.dirty.contains_key(p))
                    .count();
                if (views.dirty.len() + views.inflight.as_ref().map_or(0, |v| v.len()) + additional)
                    * PAGE
                    > DIRTY_LIMIT
                {
                    drop(views);
                    self.flush()?;
                    continue;
                }
                let lease = ReadLease::acquire(self.shared.readers.clone())?;
                if lease.root.restore_required {
                    return Err(Error::Invalid("restore is not complete".into()));
                }
                let mut visible = Changes::new();
                if let Some(frozen) = &views.inflight {
                    visible.extend(frozen.range(first..end).map(|(p, v)| (*p, v.clone())));
                }
                visible.extend(views.dirty.range(first..end).map(|(p, v)| (*p, v.clone())));
                (lease, visible, views.revision)
            };
            let mut reader = self.shared.reader(&lease.root);
            let mut updates = Changes::new();
            let mut done = 0;
            let mut same = 0;
            while done < length {
                let at = offset + done as u64;
                let index = at / PAGE as u64;
                let within = at as usize % PAGE;
                let take = (PAGE - within).min(length - done);
                let inherited_identity = self.shared.identity_intersects(index * PAGE as u64, PAGE)?
                    && !visible.contains_key(&index)
                    && match reader.page(index)? {
                        Some(p) => reader.object(p.reference.object)?.origin_backed,
                        None => false,
                    };
                let old_digest = if inherited_identity {
                    // Even a write equal to the raw source must consume the
                    // presentation overlay. Persist a local COW page for it.
                    None
                } else if let Some(value) = visible.get(&index) {
                    value.as_ref().map(|v| v.digest)
                } else {
                    reader.page(index)?.map(|p| p.digest)
                };
                let mut page = if within == 0 && take == PAGE {
                    Zeroizing::new([0; PAGE])
                } else if let Some(value) = visible.get(&index) {
                    value
                        .as_ref()
                        .map(|v| Zeroizing::new(**v.as_ref()))
                        .unwrap_or_else(|| Zeroizing::new([0; PAGE]))
                } else {
                    reader.read_page(index)?
                };
                if inherited_identity && !(within == 0 && take == PAGE) {
                    self.apply_identity(index * PAGE as u64,page.as_mut())?;
                }
                if let Some(bytes) = input {
                    page[within..within + take].copy_from_slice(&bytes[done..done + take]);
                } else {
                    page[within..within + take].fill(0);
                }
                let zero = page.iter().all(|b| *b == 0);
                let digest = if zero {
                    None
                } else {
                    Some(codec::hash(page.as_ref()))
                };
                if old_digest == digest && !inherited_identity {
                    same += 1;
                } else {
                    updates.insert(
                        index,
                        if zero {
                            None
                        } else {
                            Some(Arc::new(super::crypto_work::PlainPage::verified(
                                page,
                                digest.unwrap(),
                            )))
                        },
                    );
                }
                done += take;
            }
            let mut views = self.shared.views.lock().map_err(|_| Error::Poisoned)?;
            check_views(&views)?;
            if views.revision != revision {
                drop(views);
                continue;
            }
            self.shared.deduplicated.fetch_add(same, Ordering::Relaxed);
            if !updates.is_empty() {
                self.shared
                    .changed
                    .fetch_add(updates.len() as u64, Ordering::Relaxed);
                views.dirty.extend(updates);
                views.revision = views.revision.wrapping_add(1);
            }
            if views.dirty.len() * PAGE >= DIRTY_TRIGGER {
                self.shared.wake.notify_one();
            }
            return Ok(());
        }
    }
    pub fn flush(&self) -> Result<()> {
        self.shared.flush()
    }
    pub(super) fn has_local_dirty(&self) -> bool {
        self.shared
            .views
            .lock()
            .map(|v| !v.dirty.is_empty() || v.inflight.is_some())
            .unwrap_or(true)
    }
    pub(super) fn local_dirty_pages(&self) -> u64 {
        self.shared
            .views
            .lock()
            .map(|v| (v.dirty.len() + v.inflight.as_ref().map_or(0, |f| f.len())) as u64)
            .unwrap_or(0)
    }
    pub fn info(&self) -> Result<Info> {
        let views = self.shared.views.lock().map_err(|_| Error::Poisoned)?;
        let dirty = (views.dirty.len() + views.inflight.as_ref().map_or(0, |v| v.len())) * PAGE;
        let failure = views.failure.clone();
        drop(views);
        let lease = ReadLease::acquire(self.shared.readers.clone())?;
        Ok(Info {
            id: self.shared.id,
            capacity_bytes: self.capacity(),
            encrypted: self.shared.encrypted,
            page_size: PAGE,
            segment_size: self.object_size(),
            object_size: self.object_size(),
            format_version: 4,
            dirty_bytes: dirty as u64,
            allocated_pages: lease.root.allocated_pages,
            segment_count: self.shared.device.len()? / self.object_size(),
            authenticated: true,
            snapshot_count: lease.root.snapshot_count,
            background_error: failure,
            data_generation: lease.root.data_generation,
            published_generation: lease.root.cached_published_generation,
            restore_incomplete: lease.root.restore_required,
        })
    }
    pub(super) fn frontend_diagnostics(&self) -> BTreeMap<String, u64> {
        BTreeMap::from([
            (
                "foreground_read_bytes".into(),
                self.shared.read_bytes.load(Ordering::Relaxed),
            ),
            (
                "foreground_write_bytes".into(),
                self.shared.write_bytes.load(Ordering::Relaxed),
            ),
            (
                "changed_pages".into(),
                self.shared.changed.load(Ordering::Relaxed),
            ),
            (
                "deduplicated_pages".into(),
                self.shared.deduplicated.load(Ordering::Relaxed),
            ),
        ])
    }
}
impl Shared {
    pub(super) fn identity_intersects(&self,offset:u64,length:usize)->Result<bool> {
        if offset >= 65536 && offset+length as u64 <= self.capacity.saturating_sub(65536) {return Ok(false);}
        Ok(self.identity.lock().map_err(|_|Error::Poisoned)?.iter().any(|r|offset<r.offset+r.bytes.len()as u64 && r.offset<offset+length as u64))
    }
    pub(super) fn geometry(&self) -> Geometry {
        self.crypto.geometry
    }

    /// Caller must hold a durable snapshot pin for Index. The lease protects physical locations.
    pub(super) fn read_snapshot(&self, index: MetaRef) -> Result<(ReadLease, Reader)> {
        let lease = ReadLease::acquire(self.readers.clone())?;
        let mut reader = self.reader(&lease.root);
        reader.root.index = index;
        Ok((lease, reader))
    }
    pub(super) fn reader(&self, root: &Root) -> Reader {
        Reader {
            device: self.device.clone(),
            crypto: self.crypto.clone(),
            root: root.clone(),
            cache: self.cache.clone(),
            cache_runtime: self.cache_runtime.clone(),
        }
    }
    fn capture(
        &self,
        offset: u64,
        length: usize,
        persistent: bool,
    ) -> Result<(ReadLease, Changes)> {
        let views = self.views.lock().map_err(|_| Error::Poisoned)?;
        check_views(&views)?;
        let lease = ReadLease::acquire(self.readers.clone())?;
        let mut changes = Changes::new();
        if !persistent {
            let first = offset / PAGE as u64;
            let end = (offset + length as u64).div_ceil(PAGE as u64);
            if let Some(frozen) = &views.inflight {
                changes.extend(frozen.range(first..end).map(|(p, v)| (*p, v.clone())));
            }
            changes.extend(views.dirty.range(first..end).map(|(p, v)| (*p, v.clone())));
        }
        Ok((lease, changes))
    }
    fn flush(&self) -> Result<()> {
        let _commit = self.flush_lock.lock().map_err(|_| Error::Poisoned)?;
        let frozen = {
            let mut views = self.views.lock().map_err(|_| Error::Poisoned)?;
            check_views(&views)?;
            if views.dirty.is_empty() {
                drop(views);
                ReadLease::acquire(self.readers.clone())?;
                return Ok(());
            }
            let frozen = Arc::new(std::mem::take(&mut views.dirty));
            views.inflight = Some(frozen.clone());
            views.revision = views.revision.wrapping_add(1);
            frozen
        };
        let result: Result<()> = (|| {
            let mut store = self.store.lock().map_err(|_| Error::Poisoned)?;
            store.check()?;
            let refresh = !store.classifier.recognized
                || frozen
                    .keys()
                    .any(|p| store.classifier.needs_refresh(p * PAGE as u64, PAGE as u64));
            let mut effective = Vec::new();
            let keys = frozen.keys().copied().collect::<Vec<_>>();
            let root = store.root.index;
            let previous = super::tree::get_many(&mut *store, &PAGES, root, &keys)?;
            for ((index, value), old) in frozen.iter().zip(previous) {
                let old = old.as_deref().map(StoredPage::decode).transpose()?;
                let digest = value.as_ref().map(|v| v.digest);
                let materialize_identity = self.identity_intersects(*index * PAGE as u64, PAGE)?
                    && old.as_ref().map(|p|store.object(p.reference.object).map(|o|o.origin_backed)).transpose()?.unwrap_or(false);
                if old.as_ref().map(|p| p.digest) != digest || materialize_identity {
                    effective.push((*index, value.clone(), old.is_some()));
                }
            }
            if !effective.is_empty() {
                let plain = effective
                    .iter()
                    .filter_map(|(index, value, _)| value.as_ref().map(|v| (*index, v.clone())))
                    .collect::<Vec<_>>();
                let encoded = super::crypto_work::encode_batch(
                    store.crypto.clone(),
                    store.root.seq + 1,
                    &plain,
                )?;
                *store.diagnostics.entry("crypto_pages".into()).or_default() +=
                    encoded.pages.len() as u64;
                if encoded.parallel {
                    *store
                        .diagnostics
                        .entry("crypto_parallel_pages".into())
                        .or_default() += encoded.pages.len() as u64;
                }
                *store.diagnostics.entry("crypto_tasks".into()).or_default() += encoded.tasks;
                *store
                    .diagnostics
                    .entry("crypto_nonce_batches".into())
                    .or_default() += encoded.tasks;
                store
                    .diagnostics
                    .insert("crypto_worker_limit".into(), encoded.workers);
                let mut encoded = encoded.pages.into_iter();
                store.transaction(|tx| {
                    let mut pages = Vec::new();
                    let mut dirty = Vec::new();
                    let keys = effective.iter().map(|e| e.0).collect::<Vec<_>>();
                    let previous_dirty = super::tree::get_many(tx, &DIRTY, tx.root.dirty, &keys)?;
                    for ((index, value, existed), old_dirty) in effective.iter().zip(previous_dirty)
                    {
                        let new = if let Some(bytes) = value {
                            let pool = match tx
                                .store
                                .classifier
                                .classify(index * PAGE as u64, PAGE as u64)
                            {
                                "ntfs_mft" => 1,
                                "ntfs_log" => 2,
                                "ntfs_usn" => 3,
                                "ntfs_metadata" => 4,
                                _ => 0,
                            };
                            let _ = bytes;
                            Some(
                                tx.append_encoded(
                                    encoded.next().ok_or_else(|| {
                                        Error::Integrity("encoded page missing".into())
                                    })?,
                                    pool,
                                )?
                                .encode(),
                            )
                        } else {
                            None
                        };
                        if *existed && new.is_none() {
                            tx.root.allocated_pages =
                                tx.root.allocated_pages.checked_sub(1).ok_or_else(|| {
                                    Error::Integrity("allocated page underflow".into())
                                })?;
                        } else if !*existed && new.is_some() {
                            tx.root.allocated_pages += 1;
                        }
                        if old_dirty.is_none() {
                            tx.root.changed_pages += 1;
                            dirty.push((*index, Some(vec![1])));
                        }
                        pages.push((*index, new));
                    }
                    tx.root.index = tx.set(&PAGES, tx.root.index, &pages)?;
                    tx.root.dirty = tx.set(&DIRTY, tx.root.dirty, &dirty)?;
                    tx.root.data_generation = tx
                        .root
                        .data_generation
                        .checked_add(1)
                        .ok_or_else(|| Error::Invalid("data generation exhausted".into()))?;
                    Ok(())
                })?;
            }
            if refresh {
                let mut reader = self.reader(&store.root);
                let classifier =
                    ntfs::Classifier::probe(self.capacity, |offset, out| reader.read(offset, out))
                        .unwrap_or_default();
                if classifier.ranges != store.classifier.ranges
                    || classifier.watch != store.classifier.watch
                    || classifier.recognized != store.classifier.recognized
                {
                    store.transaction(|tx| {
                        tx.root.classifier =
                            tx.replace_blob(tx.root.classifier, &serde_json::to_vec(&classifier)?)?;
                        Ok(())
                    })?;
                    store.classifier = classifier;
                }
            }
            Ok(())
        })();
        // Store is released before touching the frontend view: reads take views then the short registry lease.
        let mut views = self.views.lock().map_err(|_| Error::Poisoned)?;
        if let Err(error) = &result {
            views.failure = Some(error.to_string());
        } else {
            views.inflight = None;
        }
        views.revision = views.revision.wrapping_add(1);
        self.wake.notify_all();
        result
    }
}
fn copy_to_output(offset: u64, out: &mut [u8], index: u64, bytes: &[u8; PAGE]) {
    let start = (index * PAGE as u64).max(offset);
    let end = ((index + 1) * PAGE as u64).min(offset + out.len() as u64);
    if end > start {
        out[(start - offset) as usize..(end - offset) as usize].copy_from_slice(
            &bytes[(start - index * PAGE as u64) as usize..(end - index * PAGE as u64) as usize],
        );
    }
}
fn check_views(views: &Views) -> Result<()> {
    if let Some(error) = &views.failure {
        Err(Error::Background(error.clone()))
    } else {
        Ok(())
    }
}
fn read_config(device: &Device) -> Result<Config> {
    for offset in [0, 3 * PAGE as u64] {
        let mut bytes = [0; PAGE];
        if device.read(offset, &mut bytes).is_ok() {
            if let Ok(c) = Config::decode(&bytes) {
                if c.format_version == 4 {
                    return Ok(c);
                }
            }
        }
    }
    Err(Error::Invalid("not a format-4 container".into()))
}
impl Drop for Volume {
    fn drop(&mut self) {
        self.shared.stop.store(true, Ordering::Release);
        self.shared.wake.notify_all();
        if let Ok(mut thread) = self.worker.lock() {
            if let Some(handle) = thread.take() {
                let _ = handle.join();
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn volume() -> (tempfile::TempDir, Volume) {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("test.odv4");
        Volume::create(&path, 64 * 1024 * 1024, None).unwrap();
        let disk = Volume::open(path, None).unwrap();
        (directory, disk)
    }
    #[test]
    fn pure_reads_and_identical_writes_do_not_dirty_or_read_old_payload() {
        let (_temp, disk) = volume();
        let bytes = [71u8; PAGE];
        disk.write(0, &bytes).unwrap();
        disk.flush().unwrap();
        let before = disk.info().unwrap();
        let payload = {
            let mut store = disk.shared.store.lock().unwrap();
            let root = store.root.index;
            let page = store.page(root, 0).unwrap().unwrap();
            let object = store.object(page.reference.object).unwrap();
            object.extent * OBJECT + (17 + page.reference.slot) * PAGE as u64
        };
        let mut read = [0; PAGE];
        disk.read(0, &mut read).unwrap();
        assert_eq!(read, bytes);
        assert!(!disk.has_local_dirty());
        disk.shared.device.events.lock().unwrap().clear();
        disk.write(0, &bytes).unwrap();
        disk.flush().unwrap();
        assert!(!disk.has_local_dirty());
        assert_eq!(disk.info().unwrap().data_generation, before.data_generation);
        assert!(!disk
            .shared
            .device
            .events
            .lock()
            .unwrap()
            .iter()
            .any(|(write, at, len)| !write && *at <= payload && payload < *at + *len as u64));
    }
    #[test]
    fn contiguous_payload_reads_are_coalesced_and_survive_reopen() {
        let (temp, disk) = volume();
        let bytes = vec![0x5b; 1024 * 1024];
        disk.write(0, &bytes).unwrap();
        disk.flush().unwrap();
        let payload = {
            let mut s = disk.shared.store.lock().unwrap();
            let root = s.root.index;
            let p = s.page(root, 0).unwrap().unwrap();
            s.object(p.reference.object).unwrap().extent * OBJECT + 17 * PAGE as u64
        };
        disk.shared.device.events.lock().unwrap().clear();
        let mut actual = vec![0; bytes.len()];
        disk.read(0, &mut actual).unwrap();
        assert_eq!(actual, bytes);
        let reads = disk
            .shared
            .device
            .events
            .lock()
            .unwrap()
            .iter()
            .filter(|(write, at, _)| {
                !*write && *at >= payload && *at < payload + bytes.len() as u64
            })
            .count();
        assert_eq!(
            reads, 1,
            "contiguous 1 MiB payload should use one positioned read"
        );
        drop(disk);
        let disk = Volume::open(temp.path().join("test.odv4"), None).unwrap();
        disk.read(0, &mut actual).unwrap();
        assert_eq!(actual, bytes);
    }
    #[test]
    fn foreground_io_and_info_do_not_wait_for_background_store_mutex() {
        let (_temp, disk) = volume();
        let disk = Arc::new(disk);
        disk.write(0, &[0x21; PAGE]).unwrap();
        disk.flush().unwrap();
        let held = disk.shared.store.lock().unwrap();
        let other = disk.clone();
        let (send, receive) = std::sync::mpsc::channel();
        let task = thread::spawn(move || {
            let mut out = [0; PAGE];
            other.read(0, &mut out).unwrap();
            assert_eq!(out, [0x21; PAGE]);
            other.write(PAGE as u64, &[0x42; PAGE]).unwrap();
            other.info().unwrap();
            send.send(()).unwrap();
        });
        let result = receive.recv_timeout(Duration::from_secs(3));
        drop(held);
        task.join().unwrap();
        assert!(
            result.is_ok(),
            "foreground was serialized behind Store mutex"
        );
        disk.flush().unwrap();
    }
    #[test]
    fn overlapping_sector_writes_and_partial_trim_preserve_other_bytes() {
        let (_temp, disk) = volume();
        let disk = Arc::new(disk);
        let mut tasks = Vec::new();
        for sector in 0..8 {
            let disk = disk.clone();
            tasks.push(thread::spawn(move || {
                disk.write(sector * 512, &[sector as u8 + 1; 512]).unwrap()
            }));
        }
        for task in tasks {
            task.join().unwrap();
        }
        disk.trim(512, 512).unwrap();
        disk.flush().unwrap();
        let mut out = [0; PAGE];
        disk.read(0, &mut out).unwrap();
        for sector in 0..8 {
            assert_eq!(
                &out[sector * 512..(sector + 1) * 512],
                &[if sector == 1 { 0 } else { sector as u8 + 1 }; 512]
            );
        }
    }
    #[test]
    fn persistence_failure_is_sticky_and_empty_flush_does_not_hide_it() {
        let (_temp, disk) = volume();
        disk.write(0, &[23; PAGE]).unwrap();
        disk.shared.device.fail_sync.store(true, Ordering::Relaxed);
        assert!(disk.flush().is_err());
        assert!(disk.write(PAGE as u64, &[42; PAGE]).is_err());
        assert!(disk.read(0, &mut [0; PAGE]).is_err());
        assert!(disk.flush().is_err());
    }
    #[test]
    fn large_unallocated_trim_skips_holes() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("large.odv4");
        Volume::create(&path, 64 * 1024 * 1024 * 1024, None).unwrap();
        let disk = Volume::open(path, None).unwrap();
        let before = disk.shared.device.diagnostics()["physical_read_calls"];
        disk.trim(0, disk.capacity()).unwrap();
        assert!(disk.shared.device.diagnostics()["physical_read_calls"] - before < 16);
        assert!(!disk.has_local_dirty());
        disk.write(1024 * 1024 * 1024, &[9; PAGE]).unwrap();
        disk.flush().unwrap();
        disk.trim(0, disk.capacity()).unwrap();
        disk.flush().unwrap();
        assert_eq!(disk.info().unwrap().allocated_pages, 0);
    }
}
