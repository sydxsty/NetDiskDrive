use super::codec::hex;
use super::store::*;
use super::*;
use serde_json::{json, Value};
use std::collections::{BTreeSet, HashMap};
#[derive(Clone, Serialize, Deserialize, Default)]
pub(super) struct Compact {
    pub phase: String,
    pub mode: String,
    pub cursor: u64,
    pub reclaimed_bytes: u64,
    pub moved_bytes: u64,
    pub processed_objects: u64,
    pub end_oid: u64,
    pub paused: bool,
    pub truncated_bytes: u64,
    pub reclaim_cutoff: u64,
    pub metadata_cursor: u64,
    pub metadata_end: u64,
}
impl Compact {
    pub fn public(&self) -> Value {
        json!({"state":if self.phase=="done"{"done"}else if self.phase=="cancelled"{"cancelled"}else if self.paused{"paused"}else{"running"},"phase":self.phase,"mode":self.mode,"cursor":self.cursor,"processed_objects":self.processed_objects,"total_objects":self.end_oid.saturating_sub(1),"reclaimed_bytes":self.reclaimed_bytes,"moved_bytes":self.moved_bytes,"truncated_bytes":self.truncated_bytes,"scanned_pages":self.cursor.saturating_sub(1).min(self.end_oid.saturating_sub(1)),"total_pages":self.end_oid.saturating_sub(1),"progress_unit":"objects"})
    }
}
#[derive(Clone, Serialize, Deserialize)]
pub(super) struct Snapshot {
    pub id: String,
    pub name: String,
    pub index: MetaRef,
    pub generation: u64,
    pub created_utc: String,
    pub pin: String,
    pub target_volume_id: Option<Uuid>,
}
#[derive(Clone, Serialize, Deserialize)]
pub(super) struct Job {
    pub id: String,
    pub phase: String,
    pub generation: u64,
    pub snapshot: MetaRef,
    pub pending: MetaRef,
    pub cursor: u64,
    pub index: MetaRef,
    pub counts: MetaRef,
    pub add: MetaRef,
    pub remove: MetaRef,
    pub receipts: MetaRef,
    pub meta_tail: u64,
    pub root_oid: u64,
    pub root_sha256: String,
    pub processed_pages: u64,
    pub changed_pages: u64,
    pub seal_tails: Vec<u64>,
    pub seal_cursor: usize,
    pub seed_base: bool,
    pub seed_cursor: u64,
}
#[derive(Clone, Serialize, Deserialize, Default)]
pub(super) struct Cloud {
    pub binding: Option<Value>,
    pub paused: bool,
    pub published_generation: u64,
    pub published_counts: MetaRef,
    pub published_index: MetaRef,
    pub published_root: u64,
    pub published_commit: Option<Value>,
    pub job: Option<Job>,
    pub last_job: Option<Job>,
    pub compact: Option<Compact>,
    pub restore: Option<Restore>,
    #[serde(default)]
    pub replica: Option<super::replica::Replica>,
    pub lazy_backing: Option<Value>,
    pub base_index: MetaRef,
    pub base_counts: MetaRef,
    pub base_root: u64,
    pub cache: super::cache::Policy,
    #[serde(skip)]
    pub transfer: HashMap<u64, String>,
}
#[derive(Clone, Serialize, Deserialize)]
pub(super) struct Need {
    pub id: String,
    pub oid: u64,
    pub sha256: String,
    pub kind: String,
}
#[derive(Clone, Serialize, Deserialize)]
pub(super) struct Restore {
    pub lazy: bool,
    pub mode: String,
    pub baseline_counts: MetaRef,
    pub verified_nodes: u64,
    pub data_cursor: u64,
    pub publication: Option<Value>,
    pub kind: String,
    pub phase: String,
    pub root_id: String,
    pub root_sha256: String,
    pub source_volume_id: Uuid,
    pub source_snapshot_id: Option<String>,
    pub generation: u64,
    pub index: MetaRef,
    pub queue: MetaRef,
    pub queue_tail: u64,
    pub processed: MetaRef,
    pub received: u64,
    pub total: u64,
    pub completed_pages: u64,
    pub expected_pages: u64,
    pub cursor: u64,
    pub source_pin: Option<String>,
    pub source_pin_cleanup_pending: bool,
}

fn string(v: &Value, k: &str) -> Result<String> {
    v[k].as_str()
        .map(str::to_owned)
        .ok_or_else(|| Error::Invalid(format!("missing {k}")))
}
fn number(v: &Value, k: &str, default: u64) -> u64 {
    v[k].as_u64().unwrap_or(default)
}
fn prepare_stage(j: &Job) -> &'static str {
    if j.phase != "preparing" {
        "ready"
    } else if j.seal_cursor < j.seal_tails.len() {
        "sealing"
    } else if j.seed_base || j.processed_pages < j.changed_pages {
        "indexing"
    } else {
        "root"
    }
}
fn job_public(s: &mut Store, j: &Job) -> Result<Value> {
    let g = s.geometry();
    let total = tree::len(s, &SET, j.add)?;
    let uploaded = tree::len(s, &SET, j.receipts)?;
    let root = if j.root_oid == 0 {
        None
    } else {
        Some(s.object(j.root_oid)?.id.to_string())
    };
    Ok(
        json!({"object_size":g.object_size,"id":j.id,"phase":j.phase,"generation":j.generation,"root_object_id":root,"root_sha256":j.root_sha256,"root_slot":0,"total_objects":total,"uploaded_objects":uploaded,"estimated_bytes":total*g.object_size,"processed_pages":j.processed_pages,"changed_pages":j.changed_pages,"prepare_stage":prepare_stage(j),"sealed_objects":j.seal_cursor,"total_tail_objects":j.seal_tails.len()}),
    )
}
fn status(s: &mut Store) -> Result<Value> {
    let job = s.cloud.job.clone().map(|j| job_public(s, &j)).transpose()?;
    Ok(
        json!({"object_size":s.object_size(),"format_version":4,"binding":s.cloud.binding,"bound":s.cloud.binding.is_some(),"enabled":s.cloud.binding.as_ref().and_then(|v|v["enabled"].as_bool()).unwrap_or(true),"paused":s.cloud.paused,"data_generation":s.root.data_generation,"published_generation":s.cloud.published_generation,"published_commit":s.cloud.published_commit,"local_dirty":s.root.changed_pages!=0,"pending_generation":s.root.data_generation,"changed_pages":s.root.changed_pages,"job":job}),
    )
}
fn reconcile_delta(
    tx: &mut Txn<'_>,
    j: &mut Job,
    baseline: MetaRef,
    keys: impl IntoIterator<Item = u64>,
) -> Result<()> {
    let mut add = Vec::new();
    let mut remove = Vec::new();
    for oid in keys {
        let old = tree::get(tx, &COUNTS, baseline, oid)?.is_some();
        let new = tree::get(tx, &COUNTS, j.counts, oid)?.is_some();
        if new && !old {
            let mut o = tx.object(oid)?;
            o.sync_state = 1;
            tx.objects.insert(oid, o);
        }
        add.push((oid, if new && !old { Some(vec![1]) } else { None }));
        remove.push((
            oid,
            if old && !new {
                let o = tx.object(oid)?;
                let mut d = vec![0; 64];
                d[..16].copy_from_slice(o.id.as_bytes());
                d[16..48].copy_from_slice(&o.sha);
                d[48] = o.kind;
                Some(d)
            } else {
                None
            },
        ));
    }
    add.sort_by_key(|v| v.0);
    add.dedup_by_key(|v| v.0);
    remove.sort_by_key(|v| v.0);
    remove.dedup_by_key(|v| v.0);
    j.add = tx.set(&SET, j.add, &add)?;
    j.remove = tx.set(&DELTA, j.remove, &remove)?;
    Ok(())
}
impl Volume {
    pub fn control(&self, r: &Value) -> Result<Value> {
        let cmd = string(r, "cmd")?;
        if cmd.starts_with("replica.") { return self.replica_control(r); }
        if cmd.starts_with("cache.") || cmd == "cloud.gc_candidates" {
            return self.cache_control(r);
        }
        if cmd == "volume.read_only" {
            let enabled = r["enabled"]
                .as_bool()
                .ok_or_else(|| Error::Invalid("read-only requires boolean".into()))?;
            self.flush()?;
            self.shared.read_only.store(enabled, Ordering::Release);
            return Ok(json!({"read_only":enabled}));
        }
        if cmd.starts_with("lazy.") {
            return self.lazy_control(r);
        }
        if cmd != "debug.pages" && (cmd.starts_with("debug.") || cmd.starts_with("blocks.")) {
            return self.debug_control(r);
        }
        if let Some(value) = self.readonly_control(r, &cmd)? {
            return Ok(value);
        }
        if cmd == "snapshot.create" {
            self.flush()?;
        }
        if cmd == "cloud.prepare" {
            self.materialize_replica_counts()?;
            let begin = {
                let s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
                s.cloud.job.is_none()
            };
            if begin {
                self.flush()?;
            }
        }
        if cmd.starts_with("snapshot.restore") || cmd.starts_with("restore.") {
            return self.restore_control(r);
        }
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        s.check()?;
        if s.root.restore_required && !matches!(cmd.as_str(), "restore.status" | "sync.diagnostics")
        {
            return Err(Error::Invalid("restore is incomplete".into()));
        }
        let result = match cmd.as_str() {
            "cloud.bind" => {
                let binding = r.get("binding").cloned().unwrap_or_else(|| {
                    let mut v = r.clone();
                    v.as_object_mut().unwrap().remove("cmd");
                    v
                });
                if s.cloud.binding.as_ref() == Some(&binding) {
                    return Ok(json!({"status":status(&mut s)?}));
                }
                if s.cloud.job.is_some() {
                    return Err(Error::Invalid(
                        "finish the current job before changing cloud binding".into(),
                    ));
                }
                if s.cloud.binding.is_some() && s.cloud.published_root != 0 {
                    return Err(Error::Invalid(
                        "published volume binding cannot be replaced in place".into(),
                    ));
                }
                let mut c = s.cloud.clone();
                c.binding = Some(binding);
                s.transaction(|tx| tx.save_cloud(&c))?;
                s.cloud = c;
                Ok(json!({"status":status(&mut s)?}))
            }
            "cloud.status" => status(&mut s),
            "cloud.pause" => {
                let mut c = s.cloud.clone();
                c.paused = r["paused"].as_bool().unwrap_or(true);
                if c.paused != s.cloud.paused {
                    s.transaction(|tx| tx.save_cloud(&c))?;
                    s.cloud = c;
                }
                Ok(json!({"paused":s.cloud.paused}))
            }
            "cloud.prepare" => cloud_prepare(&mut s, r),
            "cloud.list" | "cloud.delta" | "cloud.published_objects" => cloud_list(&mut s, r, &cmd),
            "cloud.receipt" => cloud_receipt(&mut s, r),
            "cloud.transfer" => {
                let j = s
                    .cloud
                    .job
                    .as_ref()
                    .ok_or_else(|| Error::Invalid("no active cloud job".into()))?;
                if j.id != string(r, "job_id")? {
                    return Err(Error::Invalid("cloud job mismatch".into()));
                }
                let o = s.object_id(&string(r, "object_id")?)?;
                let state = string(r, "state")?;
                if !["pending", "uploading", "failed"].contains(&state.as_str()) {
                    return Err(Error::Invalid("invalid transfer state".into()));
                }
                if state == "pending" {
                    s.cloud.transfer.remove(&o.oid);
                } else {
                    s.cloud.transfer.insert(o.oid, state.clone());
                }
                let mut registry = s.readers.lock().map_err(|_| Error::Poisoned)?;
                let transfers = Arc::make_mut(&mut registry.transfer);
                if state == "pending" {
                    transfers.remove(&o.oid);
                } else {
                    transfers.insert(o.oid, state);
                }
                registry.volatile_revision += 1;
                Ok(json!({"ok":true}))
            }
            "cloud.commit" => cloud_commit(&mut s, r),
            "snapshot.list" => {
                let start = number(r, "cursor", 0) as usize;
                let limit = number(r, "limit", 128).clamp(1, 128) as usize;
                let items = s
                    .snapshots
                    .iter()
                    .skip(start)
                    .take(limit)
                    .map(snapshot_public)
                    .collect::<Vec<_>>();
                let next = start + items.len();
                Ok(
                    json!({"items":items,"total_count":s.snapshots.len(),"next_cursor":if next<s.snapshots.len(){Some(next)}else{None}}),
                )
            }
            "snapshot.create" => {
                let name = r["name"].as_str().unwrap_or("Snapshot").to_string();
                if name.trim().is_empty() || name.chars().count() > 64 {
                    return Err(Error::Invalid(
                        "snapshot name must contain 1–64 characters".into(),
                    ));
                }
                let snapshot = Snapshot {
                    id: Uuid::new_v4().to_string(),
                    name,
                    index: s.root.index,
                    generation: s.root.data_generation,
                    created_utc: std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .unwrap_or_default()
                        .as_secs()
                        .to_string(),
                    pin: "user".into(),
                    target_volume_id: None,
                };
                let mut list = s.snapshots.clone();
                list.push(snapshot.clone());
                s.transaction(|tx| {
                    tx.replace(&PAGES, MetaRef::default(), snapshot.index);
                    tx.save_snapshots(&list)
                })?;
                s.snapshots = list;
                Ok(json!({"snapshot":snapshot_public(&snapshot)}))
            }
            "snapshot.rename" | "snapshot.delete" => {
                let id = string(r, "id")?;
                let mut list = s.snapshots.clone();
                let index = list
                    .iter()
                    .position(|v| v.id == id && v.pin == "user")
                    .ok_or_else(|| Error::Invalid("user snapshot not found".into()))?;
                let old = list[index].clone();
                if cmd == "snapshot.rename" {
                    let name = string(r, "name")?;
                    if name.trim().is_empty() || name.chars().count() > 64 {
                        return Err(Error::Invalid("invalid snapshot name".into()));
                    }
                    list[index].name = name;
                } else {
                    list.remove(index);
                }
                s.transaction(|tx| {
                    if cmd == "snapshot.delete" {
                        tx.replace(&PAGES, old.index, MetaRef::default());
                    }
                    tx.save_snapshots(&list)
                })?;
                s.snapshots = list;
                Ok(json!({"ok":true}))
            }
            "compact.start" => s.compact_start(r["mode"].as_str().unwrap_or("normal")),
            "compact.cancel" => s.compact_cancel(),
            "compact.step" => s.compact_step(number(r, "max_objects", 8).clamp(1, 32) as usize),
            "compact.pause" => s.compact_pause(r["paused"].as_bool().unwrap_or(true)),
            "compact.status" => s.compact_status(),
            "debug.summary" | "blocks.summary" => s.debug_summary(),
            "debug.query" | "blocks.query" | "debug.blocks" => s.debug_query(r),
            "debug.changes" | "blocks.changes" => {
                Ok(s.debug_changes(number(r, "since_revision", 0)))
            }
            "sync.diagnostics" => Ok(
                json!({"format_version":4,"data_generation":s.root.data_generation,"metadata_generation":s.root.seq,"changed_pages":s.root.changed_pages,"counters":s.diagnostics,"incremental":true,"sealed_bytes_are_remote_bytes":true}),
            ),
            _ => Err(Error::Invalid(format!("unsupported control command {cmd}"))),
        };
        drop(s);
        let mut result = result?;
        if let Some(status) = result.get_mut("status") {
            if self.has_local_dirty() {
                status["local_dirty"] = json!(true);
            }
        }
        Ok(result)
    }
    pub fn read_export(&self, job: &str, object: &str, offset: u64, out: &mut [u8]) -> Result<()> {
        self.with_hydration(|| self.read_export_inner(job, object, offset, out))
    }
    fn read_export_inner(
        &self,
        job: &str,
        object: &str,
        offset: u64,
        out: &mut [u8],
    ) -> Result<()> {
        let g = self.geometry();
        if offset
            .checked_add(out.len() as u64)
            .is_none_or(|end| end > g.object_size)
        {
            return Err(Error::Invalid("export range".into()));
        }
        let (_lease, mut reader) = self.read_context()?;
        let cloud: Cloud = if reader.root.cloud.empty() {
            Cloud::default()
        } else {
            serde_json::from_slice(&reader.blob(reader.root.cloud)?)?
        };
        let j = cloud
            .job
            .as_ref()
            .filter(|j| j.id == job)
            .or_else(|| cloud.last_job.as_ref().filter(|j| j.id == job))
            .ok_or_else(|| Error::Invalid("export job not found".into()))?;
        let uuid = Uuid::parse_str(object).map_err(|_| Error::Invalid("object id".into()))?;
        let oid = u64::from_le_bytes(uuid.as_bytes()[8..].try_into().unwrap());
        let o = reader.object(oid)?;
        if o.id != uuid || !o.sealed || tree::get(&mut reader, &COUNTS, j.counts, oid)?.is_none() {
            return Err(Error::Invalid("object is not pinned by export job".into()));
        }
        if o.missing {
            return Err(Error::Missing(RemoteObject::from_object(
                &o,
                self.object_size(),
            )));
        }
        // Expected SHA comes from authenticated object directory. The prepared upload factory
        // checks this raw buffer and computes provider MD5 in the same single pass.
        reader.device.read(o.extent * g.object_size + offset, out)?;
        if let Ok(mut registry) = self.shared.readers.lock() {
            *registry
                .metrics
                .entry("upload_read_bytes".into())
                .or_default() += out.len() as u64;
        }
        Ok(())
    }
    pub fn snapshot_create(&self) -> Result<String> {
        Ok(
            self.control(&json!({"cmd":"snapshot.create","name":"Snapshot"}))?["snapshot"]["id"]
                .as_str()
                .unwrap()
                .to_string(),
        )
    }
    pub fn snapshot_release(&self, id: &str) -> Result<()> {
        self.control(&json!({"cmd":"snapshot.delete","id":id}))?;
        Ok(())
    }
    pub fn snapshot_manifest(&self, id: &str) -> Result<Value> {
        self.with_hydration(||self.snapshot_manifest_inner(id))
    }
    fn snapshot_manifest_inner(&self, id: &str) -> Result<Value> {
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        let snapshot = s
            .snapshots
            .iter()
            .find(|v| v.id == id)
            .cloned()
            .ok_or_else(|| Error::Invalid("snapshot not found".into()))?;
        let mut objects = BTreeSet::new();
        let mut cursor = 0;
        loop {
            let rows = tree::scan_after(&mut *s, &PAGES, snapshot.index, cursor, 128)?;
            if rows.is_empty() {
                break;
            }
            cursor = rows.last().unwrap().0 + 1;
            for (_, v) in rows {
                objects.insert(Page::decode(&v)?.reference.object);
            }
        }
        s.transaction(|tx| {
            for oid in &objects {
                tx.seal(*oid, None)?;
                for tail in &mut tx.root.tails {
                    if *tail == *oid {
                        *tail = 0;
                    }
                }
            }
            Ok(())
        })?;
        let mut desc = Vec::new();
        for oid in objects {
            desc.push(s.object(oid)?.desc(s.object_size()));
        }
        Ok(json!({"format_version":4,"snapshot_id":id,"objects":desc}))
    }
    pub fn object_read(
        &self,
        snapshot: &str,
        object: &str,
        offset: u64,
        out: &mut [u8],
    ) -> Result<()> {
        self.with_hydration(|| self.object_read_inner(snapshot, object, offset, out))
    }
    fn object_read_inner(
        &self,
        snapshot: &str,
        object: &str,
        offset: u64,
        out: &mut [u8],
    ) -> Result<()> {
        let g = self.geometry();
        let manifest = self.snapshot_manifest(snapshot)?;
        if !manifest["objects"]
            .as_array()
            .unwrap()
            .iter()
            .any(|v| v["id"].as_str() == Some(object))
        {
            return Err(Error::Invalid("snapshot object not found".into()));
        }
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        let o = s.object_id(object)?;
        if o.missing {
            return Err(Error::Missing(RemoteObject::from_object(
                &o,
                self.object_size(),
            )));
        }
        let raw = s.verify_object(&o)?;
        let end = offset
            .checked_add(out.len() as u64)
            .filter(|v| *v <= g.object_size)
            .ok_or_else(|| Error::Invalid("object read range".into()))?;
        out.copy_from_slice(&raw[offset as usize..end as usize]);
        Ok(())
    }
    pub fn compact(&self) -> Result<()> {
        self.control(&json!({"cmd":"compact.start","mode":"normal"}))?;
        loop {
            let v = self.control(&json!({"cmd":"compact.step","max_objects":32}))?;
            if v["state"] == "done" || v["phase"] == "done" {
                return Ok(());
            }
        }
    }
}
fn snapshot_public(s: &Snapshot) -> Value {
    json!({"id":s.id,"name":s.name,"generation":s.generation,"created_utc":s.created_utc,"pin":s.pin,"target_volume_id":s.target_volume_id})
}
fn cloud_prepare(s: &mut Store, r: &Value) -> Result<Value> {
    let g = s.geometry();
    if s.cloud.job.is_none() {
        if s.root.changed_pages == 0
            && s.cloud.published_root != 0
            && s.root.data_generation == s.cloud.published_generation
        {
            return Ok(json!({"job":null,"up_to_date":true}));
        }
        let mut c = s.cloud.clone();
        let mut j = Job {
            id: Uuid::new_v4().to_string(),
            phase: "preparing".into(),
            generation: s.root.data_generation,
            snapshot: s.root.index,
            pending: s.root.dirty,
            cursor: 0,
            index: if c.base_root != 0 {
                c.base_index
            } else {
                c.published_index
            },
            counts: if c.base_root != 0 {
                c.base_counts
            } else {
                c.published_counts
            },
            add: MetaRef::default(),
            remove: MetaRef::default(),
            receipts: MetaRef::default(),
            meta_tail: 0,
            root_oid: 0,
            root_sha256: String::new(),
            processed_pages: 0,
            changed_pages: s.root.changed_pages,
            seal_tails: s.root.tails.iter().copied().filter(|n| *n != 0).collect(),
            seal_cursor: 0,
            seed_base: c.base_root != 0,
            seed_cursor: 0,
        };
        let oldroot = if c.base_root != 0 {
            c.base_root
        } else {
            c.published_root
        };
        s.transaction(|tx| {
            tx.replace(&PAGES, MetaRef::default(), j.snapshot);
            tx.replace(&COUNTS, MetaRef::default(), j.counts);
            tx.root.dirty = MetaRef::default();
            tx.root.changed_pages = 0;
            tx.root.tails = [0; 5];
            if oldroot != 0 {
                tx.cloud_deltas.insert(oldroot, -1);
                j.counts = tx.apply_cloud_deltas(j.counts)?;
                reconcile_delta(tx, &mut j, c.published_counts, [oldroot])?;
            }
            c.job = Some(j.clone());
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        return Ok(json!({"job":job_public(s,&j)?}));
    }
    let mut c = s.cloud.clone();
    let mut j = c.job.clone().unwrap();
    if let Some(id) = r["job_id"].as_str() {
        if j.id != id {
            return Err(Error::Invalid("cloud job mismatch".into()));
        }
    }
    if j.phase != "preparing" {
        return Ok(json!({"job":job_public(s,&j)?}));
    }
    if j.seal_cursor < j.seal_tails.len() {
        let end = (j.seal_cursor + number(r, "max_objects", 2).clamp(1, 4) as usize)
            .min(j.seal_tails.len());
        s.transaction(|tx| {
            for oid in &j.seal_tails[j.seal_cursor..end] {
                tx.seal(*oid, None)?;
            }
            j.seal_cursor = end;
            c.job = Some(j.clone());
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        return Ok(json!({"job":job_public(s,&j)?}));
    }
    if j.seed_base {
        let rows = tree::scan_after(s, &COUNTS, j.counts, j.seed_cursor, 256)?;
        s.transaction(|tx| {
            let changes = rows
                .iter()
                .map(|(id, _)| (*id, Some(vec![1])))
                .collect::<Vec<_>>();
            j.add = tx.set(&SET, j.add, &changes)?;
            for (id, _) in &rows {
                let mut object = tx.object(*id)?;
                object.sync_state = 1;
                tx.objects.insert(*id, object);
            }
            if let Some((key, _)) = rows.last() {
                j.seed_cursor = key + 1;
            }
            if rows.len() < 256 {
                j.seed_base = false;
            }
            c.job = Some(j.clone());
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        return Ok(json!({"job":job_public(s,&j)?}));
    }
    let limit = number(r, "max_pages", 256).clamp(1, 4096) as usize;
    let mut rows = tree::scan_after(s, &DIRTY, j.pending, j.cursor, limit)?;
    // A larger sequential checkpoint must not turn a sparse workload into an
    // unbounded lock hold. At most 128 different leaf groups enter this batch.
    let mut groups = 0;
    let mut last = None;
    let mut keep = 0;
    for (key, _) in &rows {
        let leaf = *key >> PAGES.leaf_bits;
        if last != Some(leaf) {
            if groups == 128 {
                break;
            }
            groups += 1;
            last = Some(leaf);
        }
        keep += 1;
    }
    rows.truncate(keep);
    if !rows.is_empty() {
        let keys = rows.iter().map(|(key, _)| *key).collect::<Vec<_>>();
        let values = tree::get_many(s, &PAGES, j.snapshot, &keys)?;
        let changes = keys.into_iter().zip(values).collect::<Vec<_>>();
        *s.diagnostics
            .entry("cloud_index_lookup_batches".into())
            .or_default() += 1;
        let next = rows.last().unwrap().0 + 1;
        s.transaction(|tx| {
            tx.portable = Some(j.meta_tail);
            j.index = tree::set_many(tx, &PORTMAP, j.index, &changes)?;
            j.meta_tail = tx.portable.take().unwrap_or(0);
            let touched = tx.cloud_deltas.keys().copied().collect::<Vec<_>>();
            j.counts = tx.apply_cloud_deltas(j.counts)?;
            reconcile_delta(tx, &mut j, c.published_counts, touched)?;
            j.cursor = next;
            j.processed_pages += rows.len() as u64;
            c.job = Some(j.clone());
            tx.save_cloud(&c)
        })?;
        *s.diagnostics
            .entry("prepare_changed_pages".into())
            .or_default() += rows.len() as u64;
        s.cloud = c;
        return Ok(json!({"job":job_public(s,&j)?}));
    }
    s.transaction(|tx|{let mut object=if j.meta_tail==0{let mut o=tx.allocate_object(2,0)?;o.used=1;tx.objects.insert(o.oid,o.clone());o}else{tx.object(j.meta_tail)?};let child=if j.index.empty(){None}else{Some((j.index.offset&!PORTABLE)/g.object_size)};if object.external_count as u64>=g.external_limit&&child.is_some_and(|v|v!=object.oid){tx.seal(object.oid,None)?;object=tx.allocate_object(2,0)?;object.used=1;tx.objects.insert(object.oid,object.clone());}
if let Some(child)=child{tx.ensure_external(object.oid,child)?;}let descriptor=json!({"format_version":4,"container_id":tx.store.config.id,"crypto_id":tx.store.crypto.id,"capacity_bytes":tx.store.config.capacity_bytes,"generation":j.generation,"index":j.index,"index_depth":tree::root_level(tx,&PORTMAP,j.index)?,"page_size":PAGE,"object_size":g.object_size});let object=tx.seal(object.oid,Some(&serde_json::to_vec(&descriptor)?))?;j.root_oid=object.oid;j.root_sha256=hex(&object.sha);j.meta_tail=0;tx.cloud_deltas.insert(object.oid,1);j.counts=tx.apply_cloud_deltas(j.counts)?;reconcile_delta(tx,&mut j,c.published_counts,[object.oid])?;j.phase="ready".into();c.job=Some(j.clone());tx.save_cloud(&c)})?;
    s.cloud = c;
    Ok(json!({"job":job_public(s,&j)?}))
}
fn cloud_list(s: &mut Store, r: &Value, cmd: &str) -> Result<Value> {
    let g = s.geometry();
    let job = if cmd == "cloud.published_objects" {
        None
    } else {
        let id = string(r, "job_id")?;
        Some(
            s.cloud
                .job
                .as_ref()
                .filter(|j| j.id == id)
                .or_else(|| s.cloud.last_job.as_ref().filter(|j| j.id == id))
                .cloned()
                .ok_or_else(|| Error::Invalid("cloud job not found".into()))?,
        )
    };
    let (spec, root) = if cmd == "cloud.published_objects" {
        (COUNTS, s.cloud.published_counts)
    } else {
        let j = job.as_ref().unwrap();
        if cmd == "cloud.delta" && r["side"] == "remove" {
            (DELTA, j.remove)
        } else {
            (SET, j.add)
        }
    };
    let cursor = number(r, "cursor", 0);
    let limit = number(r, "limit", 128).clamp(1, 128) as usize;
    let rows = tree::scan_after(s, &spec, root, cursor, limit + 1)?;
    let has_next = rows.len() > limit;
    let mut items = Vec::new();
    let mut last = cursor;
    for (oid, row) in rows.into_iter().take(limit) {
        let mut value = if spec.tag == DELTA.tag {
            json!({"id":Uuid::from_slice(&row[..16]).map_err(|_|Error::Integrity("delta id".into()))?,"sha256":hex(&row[16..48]),"kind":if row[48]==1{"data"}else{"metadata"},"length":g.object_size})
        } else {
            s.object(oid)?.desc(s.object_size())
        };
        if let Some(j) = &job {
            value["uploaded"] = json!(tree::get(s, &SET, j.receipts, oid)?.is_some());
            value["is_root"] = json!(oid == j.root_oid);
        }
        items.push(value);
        last = oid + 1;
    }
    Ok(
        json!({"items":items,"next_cursor":if has_next{Some(last)}else{None},"total_count":tree::len(s,&spec,root)?}),
    )
}
fn cloud_receipt(s: &mut Store, r: &Value) -> Result<Value> {
    let g = s.geometry();
    let id = string(r, "job_id")?;
    let mut c = s.cloud.clone();
    let mut j = c
        .job
        .clone()
        .filter(|j| j.id == id)
        .ok_or_else(|| Error::Invalid("active cloud job not found".into()))?;
    if j.phase != "ready" {
        return Err(Error::Invalid("job is still preparing".into()));
    }
    let records = r["records"]
        .as_array()
        .cloned()
        .unwrap_or_else(|| vec![r.clone()]);
    if records.is_empty() || records.len() > 128 {
        return Err(Error::Invalid(
            "receipt batch must contain 1–128 entries".into(),
        ));
    }
    let mut objects = BTreeMap::new();
    for item in records {
        let o = s.object_id(&string(&item, "object_id")?)?;
        if !o.sealed
            || string(&item, "sha256")?.to_lowercase() != hex(&o.sha)
            || number(&item, "length", 0) != g.object_size
            || tree::get(s, &SET, j.add, o.oid)?.is_none()
        {
            return Err(Error::Invalid(
                "receipt does not match a prepared object".into(),
            ));
        }
        objects.insert(o.oid, o);
    }
    s.transaction(|tx| {
        let rows = objects
            .keys()
            .map(|id| (*id, Some(vec![1])))
            .collect::<Vec<_>>();
        j.receipts = tx.set(&SET, j.receipts, &rows)?;
        for o in objects.values() {
            let mut o = o.clone();
            o.sync_state = 2;
            tx.objects.insert(o.oid, o);
        }
        c.job = Some(j.clone());
        tx.save_cloud(&c)
    })?;
    for oid in objects.keys() {
        c.transfer.remove(oid);
    }
    if let Ok(mut registry) = s.readers.lock() {
        let transfer = Arc::make_mut(&mut registry.transfer);
        for oid in objects.keys() {
            transfer.remove(oid);
        }
        registry.volatile_revision += 1;
    }
    s.cloud = c;
    Ok(json!({"job":job_public(s,&j)?}))
}
fn cloud_commit(s: &mut Store, r: &Value) -> Result<Value> {
    let id = string(r, "job_id")?;
    if s.cloud.job.is_none() {
        if s.cloud.last_job.as_ref().is_some_and(|j| j.id == id) {
            return Ok(json!({"status":status(s)?}));
        }
        return Err(Error::Invalid("cloud job not found".into()));
    }
    let mut c = s.cloud.clone();
    let mut j = c
        .job
        .clone()
        .filter(|j| j.id == id)
        .ok_or_else(|| Error::Invalid("cloud job mismatch".into()))?;
    let root = s.object(j.root_oid)?;
    if j.phase != "ready"
        || r["root_object_id"].as_str() != Some(&root.id.to_string())
        || r["root_sha256"].as_str() != Some(&j.root_sha256)
    {
        return Err(Error::Invalid("commit root mismatch".into()));
    }
    if tree::len(s, &SET, j.add)? != tree::len(s, &SET, j.receipts)? {
        return Err(Error::Invalid(
            "all prepared objects including root require durable receipts".into(),
        ));
    }
    // Delta is retained after publication for a crashed external cache to recover its deletion plan.
    s.transaction(|tx|{tx.replace(&COUNTS,c.published_counts,MetaRef::default());tx.replace(&COUNTS,c.base_counts,MetaRef::default());c.base_counts=MetaRef::default();c.base_index=MetaRef::default();c.base_root=0;if let Some(old)=&c.last_job{for r in [old.add,old.receipts]{tx.replace(&SET,r,MetaRef::default());}tx.replace(&DELTA,old.remove,MetaRef::default());}
 tx.replace(&PAGES,j.snapshot,MetaRef::default());tx.replace(&DIRTY,j.pending,MetaRef::default());j.snapshot=MetaRef::default();j.pending=MetaRef::default();let mut cursor=0;loop{let rows=tree::scan_after(tx,&SET,j.add,cursor,128)?;if rows.is_empty(){break}cursor=rows.last().unwrap().0+1;for(oid,_)in rows{let mut o=tx.object(oid)?;o.sync_state=3;if o.kind==1 {o.remote=true;o.remote_source=2;if c.cache.backing.is_some(){o.cache_backed=true;}}tx.objects.insert(oid,o);}}
 cursor=0;loop{let rows=tree::scan_after(tx,&DELTA,j.remove,cursor,128)?;if rows.is_empty(){break}cursor=rows.last().unwrap().0+1;for(oid,_)in rows{let mut o=tx.object(oid)?;o.sync_state=0;if o.kind==1&&c.cache.backing.is_some(){o.remote=true;o.remote_source=2;o.cache_backed=true;}tx.objects.insert(oid,o);}}
 c.published_generation=j.generation;c.published_counts=j.counts;c.published_index=j.index;c.published_root=j.root_oid;c.published_commit=Some(json!({"id":j.id,"delta_id":j.id,"root_object_id":root.id,"root_sha256":j.root_sha256,"root_slot":0,"generation":j.generation,"receipt":r["receipt"]}));j.phase="committed".into();c.last_job=Some(j.clone());c.job=None;c.transfer.clear();tx.save_cloud(&c)})?;
    if let Ok(mut registry) = s.readers.lock() {
        registry.transfer = Arc::new(BTreeMap::new());
        registry.volatile_revision += 1;
    }
    s.cloud = c;
    Ok(json!({"status":status(s)?}))
}
impl Volume {
    fn readonly_control(&self, r: &Value, cmd: &str) -> Result<Option<Value>> {
        let g = self.geometry();
        if !matches!(
            cmd,
            "cloud.status"
                | "cloud.list"
                | "cloud.delta"
                | "cloud.published_objects"
                | "snapshot.list"
                | "compact.status"
                | "sync.diagnostics"
                | "debug.pages"
        ) {
            return Ok(None);
        }
        let (_lease, mut reader) = self.read_context()?;
        if cmd == "debug.pages" {
            // The immutable lease exposes only persisted references. No cache flush,
            // data-page read, key material or store mutation is performed by this query.
            let start = r["start_page"].as_u64().ok_or_else(|| {
                Error::Invalid("start_page must be an unsigned page index".into())
            })?;
            let limit = r["limit"]
                .as_u64()
                .filter(|v| (1..=128).contains(v))
                .ok_or_else(|| Error::Invalid("debug.pages limit must be 1–128".into()))?;
            let total = self.capacity().div_ceil(PAGE as u64);
            if start > total {
                return Err(Error::Invalid(
                    "debug.pages start is outside the volume".into(),
                ));
            }
            let end = start.saturating_add(limit).min(total);
            let index = reader.root.index;
            let mut items = Vec::with_capacity((end - start) as usize);
            for page in start..end {
                let value = tree::get(&mut reader, &PAGES, index, page)?;
                if let Some(value) = value {
                    let reference = store::Page::decode(&value)?;
                    let object = reader.object(reference.reference.object)?;
                    if object.kind != 1 || reference.reference.slot >= object.used as u64 {
                        return Err(Error::Integrity(
                            "debug.pages reference is outside its object".into(),
                        ));
                    }
                    items.push(json!({"page":page,"object_id":object.id,"slot":reference.reference.slot,"digest":hex(&reference.digest)}));
                } else {
                    items.push(json!({"page":page,"object_id":null,"slot":null,"digest":null}));
                }
            }
            return Ok(Some(
                json!({"items":items,"next_page":if end<total{Some(end)}else{None}}),
            ));
        }
        let cloud: Cloud = if reader.root.cloud.empty() {
            Default::default()
        } else {
            serde_json::from_slice(&reader.blob(reader.root.cloud)?)?
        };
        let public_job = |reader: &mut volume::Reader, j: &Job| -> Result<Value> {
            let total = tree::len(reader, &SET, j.add)?;
            let uploaded = tree::len(reader, &SET, j.receipts)?;
            let root = if j.root_oid == 0 {
                None
            } else {
                Some(reader.object(j.root_oid)?.id.to_string())
            };
            Ok(
                json!({"object_size":g.object_size,"id":j.id,"phase":j.phase,"generation":j.generation,"root_object_id":root,"root_sha256":j.root_sha256,"root_slot":0,"total_objects":total,"uploaded_objects":uploaded,"estimated_bytes":total*g.object_size,"processed_pages":j.processed_pages,"changed_pages":j.changed_pages,"prepare_stage":prepare_stage(j),"sealed_objects":j.seal_cursor,"total_tail_objects":j.seal_tails.len()}),
            )
        };
        Ok(Some(match cmd {
            "cloud.status" => {
                let job = cloud
                    .job
                    .as_ref()
                    .map(|j| public_job(&mut reader, j))
                    .transpose()?;
                json!({"object_size":g.object_size,"format_version":4,"binding":cloud.binding,"bound":cloud.binding.is_some(),"enabled":cloud.binding.as_ref().and_then(|v|v["enabled"].as_bool()).unwrap_or(true),"paused":cloud.paused,"data_generation":reader.root.data_generation,"published_generation":cloud.published_generation,"published_commit":cloud.published_commit,"local_dirty":reader.root.changed_pages!=0||self.has_local_dirty(),"changed_pages":reader.root.changed_pages,"job":job})
            }
            "snapshot.list" => {
                let snapshots: Vec<Snapshot> = if reader.root.snapshots.empty() {
                    Vec::new()
                } else {
                    serde_json::from_slice(&reader.blob(reader.root.snapshots)?)?
                };
                let start = number(r, "cursor", 0) as usize;
                let limit = number(r, "limit", 128).clamp(1, 128) as usize;
                let items = snapshots
                    .iter()
                    .skip(start)
                    .take(limit)
                    .map(snapshot_public)
                    .collect::<Vec<_>>();
                let next = start + items.len();
                json!({"items":items,"total_count":snapshots.len(),"next_cursor":if next<snapshots.len(){Some(next)}else{None}})
            }
            "compact.status" => match cloud.compact {
                None => json!({"state":"idle","phase":"idle"}),
                Some(c) => c.public(),
            },
            "sync.diagnostics" => {
                let mut fields = reader.device.diagnostics();
                fields.extend(self.frontend_diagnostics());
                if let Ok(registry) = self.shared.readers.lock() {
                    fields.extend(registry.metrics.clone());
                }
                fields.insert("data_generation".into(), reader.root.data_generation);
                fields.insert("metadata_generation".into(), reader.root.seq);
                fields.insert("pending_changed_pages".into(), reader.root.changed_pages);
                fields.insert("buffered_changed_pages".into(), self.local_dirty_pages());
                let mut result = serde_json::to_value(fields)?;
                result["format_version"] = json!(4);
                result["incremental"] = json!(true);
                result["sealed_bytes_are_remote_bytes"] = json!(true);
                result
            }
            _ => {
                let j = if cmd == "cloud.published_objects" {
                    None
                } else {
                    let id = string(r, "job_id")?;
                    Some(
                        cloud
                            .job
                            .as_ref()
                            .filter(|j| j.id == id)
                            .or_else(|| cloud.last_job.as_ref().filter(|j| j.id == id))
                            .ok_or_else(|| Error::Invalid("cloud job not found".into()))?,
                    )
                };
                let (spec, root) = if let Some(j) = j {
                    if cmd == "cloud.delta" && r["side"] == "remove" {
                        (DELTA, j.remove)
                    } else {
                        (SET, j.add)
                    }
                } else {
                    (COUNTS, cloud.published_counts)
                };
                let start = number(r, "cursor", 0);
                let limit = number(r, "limit", 128).clamp(1, 128) as usize;
                let rows = tree::scan_after(&mut reader, &spec, root, start, limit + 1)?;
                let more = rows.len() > limit;
                let mut cursor = start;
                let mut items = Vec::new();
                for (oid, row) in rows.into_iter().take(limit) {
                    let mut v = if spec.tag == DELTA.tag {
                        json!({"id":Uuid::from_slice(&row[..16]).map_err(|_|Error::Integrity("delta UUID".into()))?,"sha256":hex(&row[16..48]),"length":g.object_size,"kind":if row[48]==1{"data"}else{"metadata"}})
                    } else {
                        reader.object(oid)?.desc(reader.object_size())
                    };
                    if let Some(j) = j {
                        v["uploaded"] =
                            json!(tree::get(&mut reader, &SET, j.receipts, oid)?.is_some());
                        v["is_root"] = json!(oid == j.root_oid);
                    }
                    items.push(v);
                    cursor = oid + 1;
                }
                json!({"items":items,"next_cursor":if more{Some(cursor)}else{None},"total_count":tree::len(&mut reader,&spec,root)?})
            }
        }))
    }
}

#[cfg(test)]
mod page_query_tests {
    use super::*;

    #[test]
    fn bounded_page_query_is_readonly_and_reports_exact_persisted_references() {
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("page-query.odv4");
        Volume::create(&path, 64 * 1024 * 1024, None).unwrap();
        let volume = Volume::open(&path, None).unwrap();
        volume.write(0, &[43; PAGE]).unwrap();
        volume.flush().unwrap();
        let before = volume.shared.device.diagnostics();
        let root = volume.shared.store.lock().unwrap().root.clone();
        let held = volume.shared.store.lock().unwrap();
        let result = volume
            .control(&json!({"cmd":"debug.pages","start_page":0,"limit":2}))
            .unwrap();
        assert_eq!(result["items"].as_array().unwrap().len(), 2);
        assert_eq!(result["items"][0]["page"], 0);
        assert!(Uuid::parse_str(result["items"][0]["object_id"].as_str().unwrap()).is_ok());
        assert!(result["items"][0]["slot"].is_u64());
        assert_eq!(result["items"][0]["digest"], hex(&codec::hash(&[43; PAGE])));
        assert_eq!(result["items"][0].as_object().unwrap().len(), 4);
        assert!(result["items"][1]["object_id"].is_null());
        assert_eq!(result["next_page"], 2);
        drop(held);
        for request in [
            json!({"cmd":"debug.pages","start_page":0,"limit":0}),
            json!({"cmd":"debug.pages","start_page":0,"limit":129}),
            json!({"cmd":"debug.pages","start_page":-1,"limit":1}),
            json!({"cmd":"debug.pages","start_page":u64::MAX,"limit":1}),
            json!({"cmd":"debug.pages","limit":1}),
        ] {
            assert!(volume.control(&request).is_err());
        }
        let last = volume.capacity() / PAGE as u64;
        let end = volume
            .control(&json!({"cmd":"debug.pages","start_page":last-1,"limit":128}))
            .unwrap();
        assert_eq!(end["items"].as_array().unwrap().len(), 1);
        assert!(end["next_page"].is_null());
        assert!(volume
            .control(&json!({"cmd":"debug.pages","start_page":last,"limit":1}))
            .unwrap()["items"]
            .as_array()
            .unwrap()
            .is_empty());
        assert_eq!(
            volume.shared.device.diagnostics()["physical_write_bytes"],
            before["physical_write_bytes"]
        );
        let after = volume.shared.store.lock().unwrap();
        assert_eq!(after.root.seq, root.seq);
        assert_eq!(after.root.data_generation, root.data_generation);
        assert_eq!(after.root.changed_pages, root.changed_pages);
    }
}
