//! Self-contained v2 storage. No SQLite or persistent side files.
mod cloud;
mod codec;
mod io;
mod persistence;
mod tree;

use codec::{Config, Crypto, MetaRef, PageRef};
use io::Device;
use rand::{rngs::OsRng, RngCore};
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeMap, BTreeSet, HashMap, VecDeque},
    path::Path,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Condvar, Mutex, RwLock,
    },
    thread::{self, JoinHandle},
    time::Duration,
};
use tree::Node;
use uuid::Uuid;
use zeroize::Zeroizing;

pub const PAGE: usize = 4096;
pub const OBJECT: u64 = 4 * 1024 * 1024;
const SLOTS: u64 = 1007;
const DIRTY_LIMIT: usize = 64 * 1024 * 1024;
const DIRTY_TRIGGER: usize = 16 * 1024 * 1024;
const META_CACHE: usize = 1024;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
    #[error("format error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("{0}")]
    Invalid(String),
    #[error("authentication or integrity failure: {0}")]
    Integrity(String),
    #[error("wrong password or damaged key envelope")]
    Password,
    #[error("volume already open")]
    Locked,
    #[error("background persistence failed; reopen volume: {0}")]
    Background(String),
    #[error("internal lock poisoned")]
    Poisoned,
}
pub type Result<T> = std::result::Result<T, Error>;

#[derive(Clone, Serialize, Debug)]
pub struct Info {
    pub id: Uuid,
    pub capacity_bytes: u64,
    pub encrypted: bool,
    pub page_size: usize,
    pub segment_size: u64,
    pub format_version: u32,
    pub dirty_bytes: u64,
    pub allocated_pages: u64,
    pub segment_count: u64,
    pub authenticated: bool,
    pub snapshot_count: usize,
    pub background_error: Option<String>,
    pub data_generation: u64,
    pub published_generation: u64,
    pub restore_incomplete: bool,
}
#[derive(Clone, Copy, Serialize, Deserialize, Debug)]
struct Tail {
    number: u64,
    generation: u64,
    slot: u64,
}
#[derive(Clone, Serialize, Deserialize, Debug)]
struct Root {
    seq: u64,
    index: MetaRef,
    depth: u8,
    allocated_pages: u64,
    next_generation: u64,
    data_tail: Option<Tail>,
    meta_tail: Option<Tail>,
    catalog: MetaRef,
    journal: MetaRef,
    seals: MetaRef,
    #[serde(default)]
    data_generation: u64,
    #[serde(default)]
    cloud_checkpoint: MetaRef,
    #[serde(default)]
    cloud_log: MetaRef,
    #[serde(default)]
    restore_required: bool,
}
#[derive(Clone, Serialize, Deserialize, Debug)]
struct Snapshot {
    id: String,
    index: MetaRef,
    depth: u8,
    seq: u64,
    allocated_pages: u64,
    seals: MetaRef,
}
#[derive(Serialize, Deserialize)]
struct Catalog {
    next: MetaRef,
    items: Vec<Snapshot>,
}
#[derive(Clone, Serialize, Deserialize)]
struct Seal {
    header: ObjectHeader,
    nonce: [u8; 24],
    seq: u64,
}
#[derive(Serialize, Deserialize)]
struct SealCatalog {
    next: MetaRef,
    items: Vec<Seal>,
}
#[derive(Serialize, Deserialize, Clone, Copy)]
struct ObjectHeader {
    id: Uuid,
    number: u64,
    generation: u64,
    kind: u8,
    sealed: bool,
    used_slots: u64,
    descriptor_nonce: [u8; 24],
    descriptor_tag: [u8; 16],
}

type DirtyMap = BTreeMap<u64, Zeroizing<[u8; PAGE]>>;
#[derive(Clone)]
struct Frozen {
    dirty: Arc<DirtyMap>,
    trims: BTreeMap<u64, u64>,
}
struct State {
    current: Root,
    roots: [Option<Root>; 2],
    dirty: Arc<DirtyMap>,
    trims: BTreeMap<u64, u64>,
    inflight: Option<Frozen>,
    free: Vec<u64>,
    snapshots: Vec<Snapshot>,
    seal_updates: Vec<Seal>,
    pending_snapshot: Option<String>,
    failure: Option<String>,
    cloud: cloud::CloudDb,
    maintenance: bool,
}
struct Cache {
    nodes: HashMap<MetaRef, Arc<Node>>,
    order: VecDeque<MetaRef>,
}
struct Shared {
    file: Device,
    config: Config,
    crypto: Crypto,
    state: Mutex<State>,
    commit: Mutex<()>,
    epoch: RwLock<()>,
    cache: Mutex<Cache>,
    wake: Condvar,
    stop: AtomicBool,
    restore_source: Mutex<Option<cloud::LocalRestoreSource>>,
    #[cfg(test)]
    pause_after_data: Mutex<Option<Arc<std::sync::Barrier>>>,
}
pub struct Volume {
    shared: Arc<Shared>,
    worker: Option<JoinHandle<()>>,
}

fn invalid(s: impl Into<String>) -> Error {
    Error::Invalid(s.into())
}
fn integrity(s: impl Into<String>) -> Error {
    Error::Integrity(s.into())
}
fn depth(capacity: u64) -> u8 {
    let mut n = capacity.div_ceil(PAGE as u64).div_ceil(32);
    let mut d = 0;
    while n > 1 {
        n = n.div_ceil(64);
        d += 1;
    }
    d
}
fn object_id(h: ObjectHeader) -> String {
    h.id.to_string()
}
fn parse_object_id(value: &str) -> Result<Uuid> {
    Uuid::parse_str(value).map_err(|_| invalid("invalid object ID"))
}

impl Drop for Volume {
    fn drop(&mut self) {
        self.shared.stop.store(true, Ordering::Release);
        self.shared.wake.notify_all();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

impl Volume {
    pub fn create(path: impl AsRef<Path>, capacity: u64, password: Option<&str>) -> Result<Self> {
        Self::create_format(path, capacity, password, 2, false)
    }
    pub fn create_v3(
        path: impl AsRef<Path>,
        capacity: u64,
        password: Option<&str>,
    ) -> Result<Self> {
        if capacity > 1024 * 1024 * 1024 * 1024 {
            return Err(invalid("V3 capacity is limited to 1 TiB"));
        }
        Self::create_format(path, capacity, password, 3, false)
    }
    fn create_format(
        path: impl AsRef<Path>,
        capacity: u64,
        password: Option<&str>,
        format: u32,
        restoring: bool,
    ) -> Result<Self> {
        if capacity < 64 * 1024 * 1024 || !capacity.is_multiple_of(512) || capacity > 1u64 << 60 {
            return Err(invalid(
                "capacity must be 512-byte aligned, 64 MiB to 1 EiB",
            ));
        }
        let (mut config, crypto) = Config::create(capacity, password)?;
        config.format_version = format;
        config.restoring = restoring;
        let file = Device::open(path.as_ref(), true)?;
        file.grow(OBJECT)?;
        let bytes = config.encode()?;
        file.write(0, &bytes)?;
        file.write(3 * PAGE as u64, &bytes)?;
        let root = Root {
            seq: 1,
            index: MetaRef::default(),
            depth: depth(capacity),
            allocated_pages: 0,
            next_generation: 1,
            data_tail: None,
            meta_tail: None,
            catalog: MetaRef::default(),
            journal: MetaRef::default(),
            seals: MetaRef::default(),
            data_generation: 0,
            cloud_checkpoint: MetaRef::default(),
            cloud_log: MetaRef::default(),
            restore_required: restoring,
        };
        for slot in 0..2 {
            let offset = (slot + 1) * PAGE as u64;
            file.write(
                offset,
                &crypto.frame(codec::ROOT, 1, offset, &serde_json::to_vec(&root)?)?,
            )?;
        }
        file.sync()?;
        let free = (1..file.len()? / OBJECT).rev().collect();
        Self::start(
            file,
            config,
            crypto,
            root.clone(),
            [Some(root.clone()), Some(root)],
            free,
            Vec::new(),
        )
    }
    pub fn open(path: impl AsRef<Path>, password: Option<&str>) -> Result<Self> {
        let file = Device::open(path.as_ref(), false)?;
        let config = read_config(&file)?;
        let crypto = config.unlock(password)?;
        if file.len()? < OBJECT || !file.len()?.is_multiple_of(OBJECT) {
            return Err(integrity(
                "container length is not a complete object multiple",
            ));
        }
        let mut roots: [Option<Root>; 2] = [None, None];
        for (i, slot) in roots.iter_mut().enumerate() {
            let offset = (i as u64 + 1) * PAGE as u64;
            let mut bytes = [0; PAGE];
            if file.read(offset, &mut bytes).is_ok() {
                if let Ok(data) = crypto.unframe(codec::ROOT, offset, &bytes) {
                    if let Ok(root) = serde_json::from_slice::<Root>(&data) {
                        if root.seq > 0 && root.depth == depth(config.capacity_bytes) {
                            *slot = Some(root);
                        }
                    }
                }
            }
        }
        let current = roots
            .iter()
            .flatten()
            .max_by_key(|r| r.seq)
            .cloned()
            .ok_or_else(|| integrity("both roots invalid"))?;
        // Root selection is complete here. Child failures never cause fallback.
        let shared = Arc::new(Shared {
            file,
            config,
            crypto,
            state: Mutex::new(State {
                current: current.clone(),
                roots,
                dirty: Arc::new(BTreeMap::new()),
                trims: BTreeMap::new(),
                inflight: None,
                free: Vec::new(),
                snapshots: Vec::new(),
                seal_updates: Vec::new(),
                pending_snapshot: None,
                failure: None,
                cloud: cloud::CloudDb::default(),
                maintenance: false,
            }),
            commit: Mutex::new(()),
            epoch: RwLock::new(()),
            cache: Mutex::new(Cache {
                nodes: HashMap::new(),
                order: VecDeque::new(),
            }),
            wake: Condvar::new(),
            stop: AtomicBool::new(false),
            restore_source: Mutex::new(None),
            #[cfg(test)]
            pause_after_data: Mutex::new(None),
        });
        {
            let mut state = shared.state.lock().map_err(|_| Error::Poisoned)?;
            state.snapshots = shared.read_catalog(current.catalog)?;
            state.cloud = shared.load_cloud(&current)?;
            shared.validate_journal(current.journal)?;
            let live = shared.reachable_roots(&state.roots)?;
            state.free = (1..shared.file.len()? / OBJECT)
                .rev()
                .filter(|i| !live.contains(i))
                .collect();
            recover_tail(&shared, &mut state.current.data_tail)?;
            recover_tail(&shared, &mut state.current.meta_tail)?;
        }
        Ok(Self::spawn(shared))
    }
    fn start(
        file: Device,
        config: Config,
        crypto: Crypto,
        current: Root,
        roots: [Option<Root>; 2],
        free: Vec<u64>,
        snapshots: Vec<Snapshot>,
    ) -> Result<Self> {
        Ok(Self::spawn(Arc::new(Shared {
            file,
            config,
            crypto,
            state: Mutex::new(State {
                current,
                roots,
                dirty: Arc::new(BTreeMap::new()),
                trims: BTreeMap::new(),
                inflight: None,
                free,
                snapshots,
                seal_updates: Vec::new(),
                pending_snapshot: None,
                failure: None,
                cloud: cloud::CloudDb::default(),
                maintenance: false,
            }),
            commit: Mutex::new(()),
            epoch: RwLock::new(()),
            cache: Mutex::new(Cache {
                nodes: HashMap::new(),
                order: VecDeque::new(),
            }),
            wake: Condvar::new(),
            stop: AtomicBool::new(false),
            restore_source: Mutex::new(None),
            #[cfg(test)]
            pause_after_data: Mutex::new(None),
        })))
    }
    fn spawn(shared: Arc<Shared>) -> Self {
        let worker_shared = Arc::downgrade(&shared);
        let worker = thread::spawn(move || {
            while let Some(s) = worker_shared.upgrade() {
                if s.stop.load(Ordering::Acquire) {
                    break;
                }
                let state = match s.state.lock() {
                    Ok(state) => state,
                    Err(_) => break,
                };
                let wait = if state.dirty.len() * PAGE >= DIRTY_TRIGGER {
                    Duration::ZERO
                } else {
                    Duration::from_millis(100)
                };
                let (state, _) = match s.wake.wait_timeout(state, wait) {
                    Ok(pair) => pair,
                    Err(_) => break,
                };
                let dirty =
                    (!state.dirty.is_empty() || !state.trims.is_empty()) && state.failure.is_none();
                drop(state);
                if s.stop.load(Ordering::Acquire) {
                    break;
                }
                if dirty {
                    let _ = s.flush();
                }
            }
        });
        Self {
            shared,
            worker: Some(worker),
        }
    }
    pub fn capacity(&self) -> u64 {
        self.shared.config.capacity_bytes
    }
    fn bounds(&self, offset: u64, length: usize) -> Result<()> {
        if !offset.is_multiple_of(512)
            || !length.is_multiple_of(512)
            || offset
                .checked_add(length as u64)
                .filter(|e| *e <= self.capacity())
                .is_none()
        {
            return Err(invalid("I/O must be 512-byte aligned and within capacity"));
        }
        Ok(())
    }
    pub fn read(&self, offset: u64, output: &mut [u8]) -> Result<()> {
        self.read_impl(offset, output, false)
    }
    pub fn read_persistent(&self, offset: u64, output: &mut [u8]) -> Result<()> {
        self.read_impl(offset, output, true)
    }
    fn read_impl(&self, offset: u64, output: &mut [u8], persistent: bool) -> Result<()> {
        self.bounds(offset, output.len())?;
        self.require_restored()?;
        if output.is_empty() {
            return Ok(());
        }
        let _epoch = self.shared.epoch.read().map_err(|_| Error::Poisoned)?;
        // Only short cache/index-root access uses the mutex; positioned disk
        // reads and authentication run concurrently with other readers.
        let (root, dirty, trims) = {
            let state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
            let first = offset / PAGE as u64;
            let end = (offset + output.len() as u64).div_ceil(PAGE as u64);
            let mut dirty = DirtyMap::new();
            let mut trims = BTreeMap::new();
            if !persistent {
                if let Some(frozen) = &state.inflight {
                    dirty.extend(frozen.dirty.range(first..end).map(|(p, b)| (*p, b.clone())));
                    trims = frozen.trims.clone();
                }
                for (start, end) in &state.trims {
                    dirty.retain(|p, _| *p < *start || *p >= *end);
                    insert_trim(&mut trims, *start, *end);
                }
                dirty.extend(state.dirty.range(first..end).map(|(p, b)| (*p, b.clone())));
            }
            (state.current.clone(), dirty, trims)
        };
        let first = offset / PAGE as u64;
        let end = (offset + output.len() as u64).div_ceil(PAGE as u64);
        let mut page = first;
        while page < end {
            let stop = (page + 256).min(end);
            let mut plan = Vec::with_capacity((stop - page) as usize);
            for logical in page..stop {
                if let Some(bytes) = dirty.get(&logical) {
                    copy_page(offset, output, logical, bytes);
                    plan.push(None);
                } else if trimmed(&trims, logical) {
                    copy_page(offset, output, logical, &[0; PAGE]);
                    plan.push(None);
                } else {
                    let reference = self.shared.lookup(&root, logical)?;
                    if reference.is_none() {
                        copy_page(offset, output, logical, &[0; PAGE]);
                    }
                    plan.push(reference);
                }
            }
            let mut index = 0;
            while index < plan.len() {
                let Some(reference) = plan[index] else {
                    index += 1;
                    continue;
                };
                let mut last = index + 1;
                while last < plan.len()
                    && plan[last].is_some_and(|r| {
                        r.offset() == reference.offset() + ((last - index) * PAGE) as u64
                    })
                {
                    last += 1;
                }
                let mut bytes = Zeroizing::new(vec![0u8; (last - index) * PAGE]);
                self.shared.file.read(reference.offset(), &mut bytes)?;
                for (i, block) in bytes.chunks_exact_mut(PAGE).enumerate() {
                    let logical = page + (index + i) as u64;
                    let record = plan[index + i].unwrap();
                    if record.slot >= SLOTS {
                        return Err(integrity("invalid data slot"));
                    }
                    let block: &mut [u8; PAGE] = block.try_into().unwrap();
                    self.shared.crypto.decode_page(logical, record, block)?;
                    copy_page(offset, output, logical, block);
                }
                index = last;
            }
            page = stop;
        }
        Ok(())
    }
    pub fn write(&self, offset: u64, input: &[u8]) -> Result<()> {
        self.bounds(offset, input.len())?;
        self.require_restored()?;
        if input.is_empty() {
            return Ok(());
        }
        if (offset % PAGE as u64 + input.len() as u64).div_ceil(PAGE as u64)
            > DIRTY_LIMIT as u64 / PAGE as u64
        {
            return Err(invalid("one write may not touch more than 64 MiB of pages"));
        }
        loop {
            let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
            check_failure(&state)?;
            let first = offset / PAGE as u64;
            let count = (offset % PAGE as u64 + input.len() as u64).div_ceil(PAGE as u64) as usize;
            let additional = (first..first + count as u64)
                .filter(|p| !state.dirty.contains_key(p))
                .count();
            if dirty_size(&state) + additional * PAGE > DIRTY_LIMIT {
                drop(state);
                self.flush()?;
                continue;
            }
            let mut done = 0;
            while done < input.len() {
                let at = offset + done as u64;
                let page = at / PAGE as u64;
                let within = (at % PAGE as u64) as usize;
                let amount = (PAGE - within).min(input.len() - done);
                if !state.dirty.contains_key(&page) {
                    let old = if within == 0 && amount == PAGE {
                        Zeroizing::new([0; PAGE])
                    } else {
                        visible_page(&self.shared, &state, page)?
                    };
                    Arc::make_mut(&mut state.dirty).insert(page, old);
                }
                Arc::make_mut(&mut state.dirty).get_mut(&page).unwrap()[within..within + amount]
                    .copy_from_slice(&input[done..done + amount]);
                done += amount;
            }
            if state.dirty.len() * PAGE >= DIRTY_TRIGGER {
                self.shared.wake.notify_one();
            }
            return Ok(());
        }
    }
    pub fn trim(&self, offset: u64, length: u64) -> Result<()> {
        if length > usize::MAX as u64 {
            return Err(invalid("trim too large"));
        }
        self.bounds(offset, length as usize)?;
        self.require_restored()?;
        if length == 0 {
            return Ok(());
        }
        loop {
            let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
            check_failure(&state)?;
            if dirty_size(&state) > DIRTY_LIMIT - 2 * PAGE || state.trims.len() >= 4096 {
                drop(state);
                self.flush()?;
                continue;
            }
            if offset == 0 && length == self.capacity() {
                Arc::make_mut(&mut state.dirty).clear();
                state.trims.clear();
                state.trims.insert(0, self.capacity().div_ceil(PAGE as u64));
                return Ok(());
            }
            let end = offset + length;
            let full_start = offset.div_ceil(PAGE as u64);
            let full_end = end / PAGE as u64;
            if full_start < full_end {
                Arc::make_mut(&mut state.dirty).retain(|p, _| *p < full_start || *p >= full_end);
                insert_trim(&mut state.trims, full_start, full_end);
            }
            let first = offset / PAGE as u64;
            let last = (end - 1) / PAGE as u64;
            for page in [first, last].into_iter().collect::<BTreeSet<_>>() {
                let start = offset.saturating_sub(page * PAGE as u64).min(PAGE as u64) as usize;
                let stop = (end - page * PAGE as u64).min(PAGE as u64) as usize;
                if start == 0 && stop == PAGE {
                    continue;
                }
                let mut bytes = visible_page(&self.shared, &state, page)?;
                bytes[start..stop].fill(0);
                Arc::make_mut(&mut state.dirty).insert(page, bytes);
            }
            return Ok(());
        }
    }
    pub fn flush(&self) -> Result<()> {
        self.shared.flush()
    }
    pub fn info(&self) -> Result<Info> {
        let state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        Ok(info(&self.shared.config, &state, self.shared.file.len()?))
    }
    pub fn inspect(path: impl AsRef<Path>) -> Result<Info> {
        // Inspect must not instantiate a worker or mutate/recover anything.
        use std::io::{Read, Seek, SeekFrom};
        let mut file = std::fs::File::open(path.as_ref())?;
        let len = file.metadata()?.len();
        let mut read_copy = |offset: u64| -> Result<Config> {
            let mut bytes = [0u8; PAGE];
            file.seek(SeekFrom::Start(offset))?;
            file.read_exact(&mut bytes)?;
            Config::decode(&bytes)
        };
        let config = read_copy(0).or_else(|_| read_copy(3 * PAGE as u64))?;
        Ok(Info {
            id: config.id,
            capacity_bytes: config.capacity_bytes,
            encrypted: config.encrypted,
            page_size: PAGE,
            segment_size: OBJECT,
            format_version: config.format_version,
            dirty_bytes: 0,
            allocated_pages: 0,
            segment_count: (len / OBJECT).saturating_sub(1),
            authenticated: false,
            snapshot_count: 0,
            background_error: None,
            data_generation: 0,
            published_generation: 0,
            restore_incomplete: config.restoring,
        })
    }
    pub fn snapshot_create(&self) -> Result<String> {
        if self.shared.config.format_version == 3 {
            return Err(invalid("use V3 snapshot/compaction controls"));
        }
        self.flush()?;
        let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
        let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        check_failure(&state)?;
        // Capture all writes accepted between the initial flush and acquiring
        // maintenance ownership. No frontend mutation can pass this state lock.
        if !state.dirty.is_empty() || !state.trims.is_empty() {
            self.shared.persist(&mut state, false)?;
        }
        if state.snapshots.len() >= 1024 {
            return Err(invalid("snapshot limit is 1024"));
        }
        let snapshot = Snapshot {
            id: Uuid::new_v4().to_string(),
            index: state.current.index,
            depth: state.current.depth,
            seq: state.current.seq,
            allocated_pages: state.current.allocated_pages,
            seals: state.current.seals,
        };
        state.seal_updates = self.shared.seal_snapshot(&snapshot, &state.current)?;
        state.current.data_tail = None;
        state.current.meta_tail = None;
        let id = snapshot.id.clone();
        state.pending_snapshot = Some(id.clone());
        state.snapshots.push(snapshot);
        self.shared.persist(&mut state, true)?;
        Ok(id)
    }
    pub fn snapshot_release(&self, id: &str) -> Result<()> {
        if self.shared.config.format_version == 3 {
            return Err(invalid("use V3 snapshot/compaction controls"));
        }
        let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
        let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        check_failure(&state)?;
        let index = state
            .snapshots
            .iter()
            .position(|s| s.id == id)
            .ok_or_else(|| invalid("snapshot not found"))?;
        state.snapshots.remove(index);
        self.shared.persist(&mut state, true)?;
        self.shared.persist(&mut state, false)?;
        Ok(())
    }
    pub fn snapshot_manifest(&self, id: &str) -> Result<serde_json::Value> {
        if self.shared.config.format_version == 3 {
            return Err(invalid("use V3 snapshot/compaction controls"));
        }
        let _epoch = self.shared.epoch.read().map_err(|_| Error::Poisoned)?;
        let snapshot = self.snapshot(id)?;
        let objects = self.shared.snapshot_objects(&snapshot)?;
        let headers: HashMap<u64, ObjectHeader> = objects
            .into_iter()
            .map(|number| Ok((number, self.shared.object_header(number)?)))
            .collect::<Result<_>>()?;
        let mut list:Vec<_>=headers.values().map(|header|serde_json::json!({"id":object_id(*header),"length":OBJECT,"kind":if header.kind==0{"data"}else{"metadata"}})).collect();
        list.sort_by(|a, b| a["id"].as_str().cmp(&b["id"].as_str()));
        let mut logical_pages = Vec::new();
        self.shared.walk(snapshot.index,snapshot.depth,0,&mut|page,r|{let header=headers.get(&r.object).ok_or_else(||integrity("snapshot logical object missing"))?;logical_pages.push(serde_json::json!({"logical_page":page,"object_id":object_id(*header),"object_offset":17*PAGE as u64+r.slot*PAGE as u64,"length":PAGE,"version":r.version,"nonce":codec::hex(&r.nonce),"tag":codec::hex(&r.tag)}));Ok(())})?;
        let checkpoint = if snapshot.index.empty() {
            serde_json::Value::Null
        } else {
            let h = self.shared.object_header(snapshot.index.offset / OBJECT)?;
            serde_json::json!({"object_id":object_id(h),"object_offset":snapshot.index.offset%OBJECT,"hash":codec::hex(&snapshot.index.hash)})
        };
        let payload = serde_json::json!({"id":snapshot.id,"logical_pages":logical_pages,"checkpoint":checkpoint,"depth":snapshot.depth,"seq":snapshot.seq,"capacity_bytes":self.capacity(),"allocated_pages":snapshot.allocated_pages,"objects":list});
        Ok(
            serde_json::json!({"format_version":2,"volume_id":self.shared.config.id,"snapshot_id":id,"objects":list,"configuration":codec::hex(&self.shared.config.encode()?),"authenticated_manifest":self.shared.crypto.authenticate_manifest(id,&serde_json::to_vec(&payload)?)?}),
        )
    }
    pub fn object_read(
        &self,
        snapshot: &str,
        id: &str,
        offset: u64,
        output: &mut [u8],
    ) -> Result<()> {
        if offset
            .checked_add(output.len() as u64)
            .filter(|end| *end <= OBJECT)
            .is_none()
        {
            return Err(invalid("object range exceeds 4 MiB"));
        }
        let _epoch = self.shared.epoch.read().map_err(|_| Error::Poisoned)?;
        let snapshot = self.snapshot(snapshot)?;
        let logical_id = parse_object_id(id)?;
        let seal = self
            .shared
            .find_seal_id(snapshot.seals, logical_id)?
            .ok_or_else(|| integrity("snapshot seal absent"))?;
        let header = seal.header;
        let number = header.number;
        if !self.shared.snapshot_objects(&snapshot)?.contains(&number) {
            return Err(invalid("object does not belong to snapshot"));
        }
        if self.shared.object_header(number)?.id != logical_id {
            return Err(integrity("object identity"));
        }
        self.shared.file.read(number * OBJECT + offset, output)?;
        if offset < PAGE as u64 {
            let frame = self.shared.crypto.frame_nonce(
                codec::OBJECT_HEADER,
                seal.seq,
                number * OBJECT,
                &serde_json::to_vec(&header)?,
                seal.nonce,
            )?;
            let amount = output.len().min(PAGE - offset as usize);
            output[..amount].copy_from_slice(&frame[offset as usize..offset as usize + amount]);
        }
        let used = 17 * PAGE as u64 + header.used_slots * PAGE as u64;
        if offset < used {
            let count = ((used - offset) as usize).min(output.len());
            output[count..].fill(0);
        } else {
            output.fill(0);
        }
        Ok(())
    }
    fn snapshot(&self, id: &str) -> Result<Snapshot> {
        self.shared
            .state
            .lock()
            .map_err(|_| Error::Poisoned)?
            .snapshots
            .iter()
            .find(|s| s.id == id)
            .cloned()
            .ok_or_else(|| invalid("snapshot not found"))
    }
    pub fn compact(&self) -> Result<()> {
        if self.shared.config.format_version == 3 {
            return Err(invalid("use V3 snapshot/compaction controls"));
        }
        self.flush()?;
        let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
        let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        check_failure(&state)?;
        if !state.dirty.is_empty() || !state.trims.is_empty() {
            self.shared.persist(&mut state, false)?;
        }
        let source = state.current.clone();
        state.current.data_tail = None;
        state.current.meta_tail = None;
        self.shared
            .walk(source.index, source.depth, 0, &mut |page, _| {
                let data = self.shared.read_page(&source, page)?;
                Arc::make_mut(&mut state.dirty).insert(page, data);
                if state.dirty.len() * PAGE >= DIRTY_TRIGGER {
                    self.shared.persist(&mut state, false)?;
                }
                Ok(())
            })?;
        if state.snapshots.is_empty() {
            state.current.seals = MetaRef::default();
        }
        self.shared.persist(&mut state, false)?;
        self.shared.persist(&mut state, false)?;
        let live = self.shared.reachable_roots(&state.roots)?;
        let end = live.iter().next_back().copied().unwrap_or(0) + 1;
        state.free = (1..end).rev().filter(|n| !live.contains(n)).collect();
        let result = self
            .shared
            .file
            .resize(end * OBJECT)
            .and_then(|()| self.shared.file.sync());
        if let Err(error) = &result {
            state.failure = Some(error.to_string());
        }
        result
    }
}

fn check_failure(state: &State) -> Result<()> {
    if let Some(error) = &state.failure {
        return Err(Error::Background(error.clone()));
    }
    Ok(())
}
fn info(config: &Config, state: &State, len: u64) -> Info {
    Info {
        id: config.id,
        capacity_bytes: config.capacity_bytes,
        encrypted: config.encrypted,
        page_size: PAGE,
        segment_size: OBJECT,
        format_version: config.format_version,
        dirty_bytes: (dirty_size(state) + state.trims.len() * 16) as u64,
        allocated_pages: state.current.allocated_pages,
        segment_count: (len / OBJECT).saturating_sub(1),
        authenticated: true,
        snapshot_count: state.snapshots.len(),
        background_error: state.failure.clone(),
        data_generation: state.current.data_generation,
        published_generation: state.cloud.number("published_generation"),
        restore_incomplete: state.current.restore_required
            || state.cloud.restore_incomplete()
            || config.restoring && state.cloud.get("restore_finished") != true,
    }
}
fn dirty_size(state: &State) -> usize {
    (state.dirty.len() + state.inflight.as_ref().map(|f| f.dirty.len()).unwrap_or(0)) * PAGE
}
fn visible_page(shared: &Shared, state: &State, page: u64) -> Result<Zeroizing<[u8; PAGE]>> {
    if let Some(data) = state.dirty.get(&page) {
        return Ok(data.clone());
    }
    if trimmed(&state.trims, page) {
        return Ok(Zeroizing::new([0; PAGE]));
    }
    if let Some(frozen) = &state.inflight {
        if let Some(data) = frozen.dirty.get(&page) {
            return Ok(data.clone());
        }
        if trimmed(&frozen.trims, page) {
            return Ok(Zeroizing::new([0; PAGE]));
        }
    }
    shared.read_page(&state.current, page)
}
fn read_config(file: &Device) -> Result<Config> {
    let read_copy = |offset: u64| -> Result<Config> {
        let mut bytes = [0; PAGE];
        file.read(offset, &mut bytes)?;
        Config::decode(&bytes)
    };
    read_copy(0).or_else(|_| read_copy(3 * PAGE as u64))
}

fn recover_tail(shared: &Shared, tail: &mut Option<Tail>) -> Result<()> {
    if let Some(t) = tail {
        let header = shared.object_header(t.number)?;
        if header.generation != t.generation {
            return Err(integrity("active object generation"));
        }
        if header.sealed {
            *tail = None;
            return Ok(());
        }
        if t.slot > SLOTS {
            return Err(integrity("active object slot"));
        }
        // Only committed slots are allocated. An interrupted, unpublished tail may
        // be overwritten with a fresh nonce; no committed page is ever overwritten.
    }
    Ok(())
}
fn trimmed(ranges: &BTreeMap<u64, u64>, page: u64) -> bool {
    ranges
        .range(..=page)
        .next_back()
        .is_some_and(|(_, end)| page < *end)
}
fn copy_page(offset: u64, output: &mut [u8], page: u64, bytes: &[u8; PAGE]) {
    let begin = (page * PAGE as u64).max(offset);
    let end = ((page + 1) * PAGE as u64).min(offset + output.len() as u64);
    if begin < end {
        output[(begin - offset) as usize..(end - offset) as usize].copy_from_slice(
            &bytes[(begin - page * PAGE as u64) as usize..(end - page * PAGE as u64) as usize],
        );
    }
}
fn insert_trim(ranges: &mut BTreeMap<u64, u64>, mut start: u64, mut end: u64) {
    let overlaps: Vec<_> = ranges
        .range(..=end)
        .filter(|(_, e)| **e >= start)
        .map(|(s, e)| (*s, *e))
        .collect();
    for (s, e) in overlaps {
        ranges.remove(&s);
        start = start.min(s);
        end = end.max(e);
    }
    ranges.insert(start, end);
}

#[cfg(test)]
mod tests;

#[cfg(test)]
mod cloud_tests;
