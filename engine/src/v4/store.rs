use super::codec::{hash, hex, PageRef};
use super::prepare_cache::{Cache as PrepareCache, Dependencies};
use super::tree::{Node, Spec, Storage};
use super::*;
use std::collections::BTreeSet;
pub(super) const PORTABLE: u64 = 1 << 63;
/// Source writers use 0*, their copies 10*, copies of copies 110*, etc.
/// Siblings never share an import source identity. Reserving the suffix for
/// descendants prevents local COW allocations from colliding with future
/// generations of a source, without renumbering any immutable object.
pub(super) fn allocation_range(config: &Config) -> Result<std::ops::Range<u64>> {
    let g = Geometry::new(config.object_size)?;
    let limit = OBJECTS.capacity()?.min(SLOTSPEC.capacity()? / g.pages)
        .min(EXTERNALS.capacity()? / g.external_stride).min(PORTABLE / g.object_size);
    let remaining = limit.checked_shr(config.allocation_depth as u32).unwrap_or(0);
    if remaining < 2048 {
        return Err(Error::Invalid("copy ancestry has exhausted the object namespace".into()));
    }
    Ok((limit - remaining).max(1)..limit - remaining / 2)
}
pub(super) const PAGES: Spec = Spec {
    tag: 1,
    leaf_bits: 5,
    depth: 5,
    value_size: 104,
    tracked: true,
};
pub(super) const DIRTY: Spec = Spec {
    tag: 2,
    leaf_bits: 5,
    depth: 5,
    value_size: 1,
    tracked: true,
};
pub(super) const COUNTS: Spec = Spec {
    tag: 3,
    leaf_bits: 5,
    depth: 6,
    value_size: 8,
    tracked: true,
};
pub(super) const OBJECTS: Spec = Spec {
    tag: 8,
    leaf_bits: 3,
    depth: 6,
    value_size: 256,
    tracked: false,
};
pub(super) const REFS: Spec = Spec {
    tag: 9,
    leaf_bits: 5,
    depth: 7,
    value_size: 16,
    tracked: false,
};
pub(super) const PORTREFS: Spec = Spec {
    tag: 17,
    leaf_bits: 5,
    depth: 8,
    value_size: 16,
    tracked: false,
};
pub(super) const BLOCKS: Spec = Spec {
    tag: 10,
    leaf_bits: 5,
    depth: 6,
    value_size: 8,
    tracked: false,
};
pub(super) const SLOTSPEC: Spec = Spec {
    tag: 11,
    leaf_bits: 5,
    depth: 7,
    value_size: 112,
    tracked: false,
};
pub(super) const EXTERNALS: Spec = Spec {
    tag: 12,
    leaf_bits: 5,
    depth: 7,
    value_size: 48,
    tracked: false,
};
pub(super) const PORTMAP: Spec = Spec {
    tag: 4,
    leaf_bits: 5,
    depth: 5,
    value_size: 104,
    tracked: false,
};
pub(super) const QUEUE: Spec = Spec {
    tag: 16,
    leaf_bits: 4,
    depth: 7,
    value_size: 192,
    tracked: true,
};
pub(super) const DELTA: Spec = Spec {
    tag: 6,
    leaf_bits: 5,
    depth: 6,
    value_size: 64,
    tracked: true,
};
pub(super) const SET: Spec = Spec {
    tag: 5,
    leaf_bits: 5,
    depth: 6,
    value_size: 1,
    tracked: true,
};
pub(super) fn spec(tag: u8) -> Result<Spec> {
    match tag {
        1 => Ok(PAGES),
        2 => Ok(DIRTY),
        3 => Ok(COUNTS),
        5 => Ok(SET),
        6 => Ok(DELTA),
        16 => Ok(QUEUE),
        _ => Err(Error::Integrity("unknown tracked node".into())),
    }
}
#[derive(Clone, Serialize, Deserialize, Debug)]
pub(super) struct Root {
    pub seq: u64,
    pub id: Uuid,
    pub allocation_nonce: [u8; 16],
    pub cache_capable: bool,
    pub index: MetaRef,
    pub dirty: MetaRef,
    pub objects: MetaRef,
    pub refs: MetaRef,
    pub portable_refs: MetaRef,
    pub blocks: MetaRef,
    pub slots: MetaRef,
    pub externals: MetaRef,
    pub free: MetaRef,
    pub free_events: u64,
    pub snapshots: MetaRef,
    pub cloud: MetaRef,
    pub classifier: MetaRef,
    pub stats: MetaRef,
    pub next_oid: u64,
    pub next_extent: u64,
    pub meta_extent: u64,
    pub meta_slot: u64,
    pub tails: [u64; 5],
    pub allocated_pages: u64,
    pub data_generation: u64,
    pub changed_pages: u64,
    pub restore_required: bool,
    #[serde(default)]
    pub deferred_index: bool,
    pub revision: u64,
    pub cached_published_generation: u64,
    pub snapshot_count: usize,
    pub lazy_total_objects: u64,
    pub lazy_cached_objects: u64,
    pub lazy_missing_objects: u64,
}
impl Root {
    pub fn new(id: Uuid) -> Self {
        Self {
            seq: 0,
            id,
            allocation_nonce: *Uuid::new_v4().as_bytes(),
            cache_capable: false,
            index: MetaRef::default(),
            dirty: MetaRef::default(),
            objects: MetaRef::default(),
            refs: MetaRef::default(),
            portable_refs: MetaRef::default(),
            blocks: MetaRef::default(),
            slots: MetaRef::default(),
            externals: MetaRef::default(),
            free: MetaRef::default(),
            free_events: 0,
            snapshots: MetaRef::default(),
            cloud: MetaRef::default(),
            classifier: MetaRef::default(),
            stats: MetaRef::default(),
            next_oid: 1,
            next_extent: 1,
            meta_extent: 0,
            meta_slot: u64::MAX,
            tails: [0; 5],
            allocated_pages: 0,
            data_generation: 0,
            changed_pages: 0,
            restore_required: false,
            deferred_index: false,
            revision: 0,
            cached_published_generation: 0,
            snapshot_count: 0,
            lazy_total_objects: 0,
            lazy_cached_objects: 0,
            lazy_missing_objects: 0,
        }
    }
}
#[derive(Clone, Debug)]
pub(super) struct Object {
    pub oid: u64,
    pub id: Uuid,
    pub extent: u64,
    pub kind: u8,
    pub pool: u8,
    pub used: u16,
    pub sealed: bool,
    pub missing: bool,
    pub remote: bool,
    pub remote_source: u8,
    pub cache_backed: bool,
    pub origin_backed: bool,
    pub sync_state: u8,
    pub refs: u64,
    pub current_refs: u64,
    pub cloud_refs: u64,
    pub sha: [u8; 32],
    pub external_count: u16,
}
impl Object {
    pub fn encode(&self) -> Vec<u8> {
        let mut b = vec![0; 256];
        b[..16].copy_from_slice(self.id.as_bytes());
        b[16..24].copy_from_slice(&self.extent.to_le_bytes());
        b[24] = self.kind;
        b[25] = self.pool;
        b[26..28].copy_from_slice(&self.used.to_le_bytes());
        b[28] = u8::from(self.sealed);
        b[29] = self.sync_state;
        b[30] = u8::from(self.missing);
        b[31] = u8::from(self.remote);
        b[96] = self.remote_source;
        b[97] = u8::from(self.cache_backed);
        b[98] = u8::from(self.origin_backed);
        b[32..40].copy_from_slice(&self.refs.to_le_bytes());
        b[40..72].copy_from_slice(&self.sha);
        b[72..74].copy_from_slice(&self.external_count.to_le_bytes());
        b[80..88].copy_from_slice(&self.current_refs.to_le_bytes());
        b[88..96].copy_from_slice(&self.cloud_refs.to_le_bytes());
        b
    }
    pub fn decode(oid: u64, b: &[u8]) -> Result<Self> {
        if b.len() != 256 {
            return Err(Error::Integrity("object entry length".into()));
        }
        Ok(Self {
            oid,
            id: Uuid::from_slice(&b[..16]).map_err(|_| Error::Integrity("object id".into()))?,
            extent: u64::from_le_bytes(b[16..24].try_into().unwrap()),
            kind: b[24],
            pool: b[25],
            used: u16::from_le_bytes(b[26..28].try_into().unwrap()),
            sealed: b[28] != 0,
            missing: b[30] != 0,
            remote: b[31] != 0,
            remote_source: b[96],
            cache_backed: b[97] != 0,
            origin_backed: b[98] != 0,
            sync_state: b[29],
            refs: u64::from_le_bytes(b[32..40].try_into().unwrap()),
            current_refs: u64::from_le_bytes(b[80..88].try_into().unwrap()),
            cloud_refs: u64::from_le_bytes(b[88..96].try_into().unwrap()),
            sha: b[40..72].try_into().unwrap(),
            external_count: u16::from_le_bytes(b[72..74].try_into().unwrap()),
        })
    }
    pub fn desc(&self, object_size: u64) -> serde_json::Value {
        serde_json::json!({"id":self.id,"kind":if self.kind==1{"data"}else{"metadata"},"length":object_size,"sha256":hex(&self.sha)})
    }
}
#[derive(Clone, Debug)]
pub(super) struct Page {
    pub reference: PageRef,
    pub digest: [u8; 32],
}
impl Page {
    pub fn encode(&self) -> Vec<u8> {
        let mut v = vec![0; 104];
        self.reference.put(&mut v);
        v[72..104].copy_from_slice(&self.digest);
        v
    }
    pub fn decode(v: &[u8]) -> Result<Self> {
        if v.len() != 104 {
            return Err(Error::Integrity("page entry".into()));
        }
        Ok(Self {
            reference: PageRef::get(v)
                .ok_or_else(|| Error::Integrity("empty page entry".into()))?,
            digest: v[72..104].try_into().unwrap(),
        })
    }
}
#[derive(Default, Clone, Serialize, Deserialize)]
struct Free {
    pages: BTreeMap<u64, u64>,
    extents: BTreeMap<u64, u64>,
    punched: BTreeSet<u64>,
}
#[derive(Default, Serialize, Deserialize)]
struct FreeLog {
    previous: MetaRef,
    reset: bool,
    add: Free,
    remove_pages: BTreeSet<u64>,
    remove_extents: BTreeSet<u64>,
    remove_punched: BTreeSet<u64>,
}
pub(super) struct ReadRegistry {
    pub root: Root,
    pub active: BTreeMap<u64, usize>,
    pub failure: Option<String>,
    pub transfer: Arc<BTreeMap<u64, String>>,
    pub receipts: Arc<super::receipts::View>,
    pub volatile_revision: u64,
    pub metrics: BTreeMap<String, u64>,
}
pub(super) struct ReadLease {
    pub root: Root,
    pub transfer: Arc<BTreeMap<u64, String>>,
    pub receipts: Arc<super::receipts::View>,
    pub volatile_revision: u64,
    registry: Arc<Mutex<ReadRegistry>>,
}
impl ReadLease {
    pub fn acquire(registry: Arc<Mutex<ReadRegistry>>) -> Result<Self> {
        let mut r = registry.lock().map_err(|_| Error::Poisoned)?;
        if let Some(error) = &r.failure {
            return Err(Error::Background(error.clone()));
        }
        let root = r.root.clone();
        *r.active.entry(root.seq).or_default() += 1;
        let transfer = r.transfer.clone();
        let receipts = r.receipts.clone();
        let volatile_revision = r.volatile_revision;
        drop(r);
        Ok(Self {
            root,
            transfer,
            receipts,
            volatile_revision,
            registry,
        })
    }
}
impl Drop for ReadLease {
    fn drop(&mut self) {
        if let Ok(mut r) = self.registry.lock() {
            if let Some(n) = r.active.get_mut(&self.root.seq) {
                *n -= 1;
                if *n == 0 {
                    r.active.remove(&self.root.seq);
                }
            }
        }
    }
}
pub(super) struct Store {
    pub device: Arc<Device>,
    pub config: Config,
    pub crypto: Arc<Crypto>,
    pub readers: Arc<Mutex<ReadRegistry>>,
    prepare_cache: PrepareCache,
    pub root: Root,
    free: Free,
    free_chain: Vec<MetaRef>,
    pub failure: Option<String>,
    pub classifier: ntfs::Classifier,
    pub cloud: cloud::Cloud,
    pub receipts: super::receipts::Log,
    pub snapshots: Vec<cloud::Snapshot>,
    pub diagnostics: BTreeMap<String, u64>,
    pub restore_cache: std::collections::VecDeque<Arc<super::restore::CachedObject>>,
    pub cache_runtime: Option<Arc<super::cache::Runtime>>,
}
impl Store {
    pub fn configure_prepare_cache(&mut self, mib: u64) -> Result<()> {
        self.prepare_cache.configure(mib)
    }
    pub fn end_prepare_cache(&mut self) {
        self.prepare_cache.end();
    }
    pub fn prepare_cache_metrics(&self) -> BTreeMap<String, u64> {
        self.prepare_cache.metrics()
    }
    pub fn geometry(&self) -> Geometry {
        self.crypto.geometry
    }
    pub fn object_size(&self) -> u64 {
        self.crypto.geometry.object_size
    }

    pub fn transaction<T>(&mut self, f: impl FnOnce(&mut Txn<'_>) -> Result<T>) -> Result<T> {
        self.check()?;
        let root = self.root.clone();
        let free = std::mem::take(&mut self.free);
        let chain = std::mem::take(&mut self.free_chain);
        let mut tx = Txn {
            store: self,
            root,
            free,
            chain,
            freelog: FreeLog::default(),
            retired: BTreeSet::new(),
            refs: BTreeMap::new(),
            objects: BTreeMap::new(),
            deleted: BTreeMap::new(),
            pending_slots: BTreeMap::new(),
            pending_externals: BTreeMap::new(),
            pending_data: BTreeMap::new(),
            pending_metadata: BTreeMap::new(),
            fresh_nodes: BTreeMap::new(),
            pending_blocks: Vec::new(),
            portable: None,
            cloud_deltas: BTreeMap::new(),
        };
        let result = f(&mut tx).and_then(|v| {
            tx.finish()?;
            Ok(v)
        });
        if let Err(e) = &result {
            {
                // No cache entry produced by an unpublished transaction may be
                // used after a failed data write, root write or flush.
                tx.store.prepare_cache.invalidate();
                tx.store.failure = Some(e.to_string());
                if let Ok(mut r) = tx.store.readers.lock() {
                    r.failure = Some(e.to_string());
                }
            }
        }
        result
    }
    pub fn check(&self) -> Result<()> {
        if let Some(e) = &self.failure {
            Err(Error::Background(e.clone()))
        } else {
            Ok(())
        }
    }
    pub fn object(&mut self, oid: u64) -> Result<Object> {
        let root = self.root.objects;
        let value = tree::get(self, &OBJECTS, root, oid)?
            .ok_or_else(|| Error::Integrity(format!("missing object {oid}")))?;
        Object::decode(oid, &value)
    }
    pub fn object_id(&mut self, id: &str) -> Result<Object> {
        let uuid = Uuid::parse_str(id).map_err(|_| Error::Invalid("invalid object id".into()))?;
        let oid = u64::from_le_bytes(uuid.as_bytes()[8..].try_into().unwrap());
        let object = self.object(oid)?;
        if object.id != uuid {
            return Err(Error::Invalid("object id does not belong to volume".into()));
        }
        Ok(object)
    }
    pub fn page(&mut self, root: MetaRef, index: u64) -> Result<Option<Page>> {
        tree::get(self, &PAGES, root, index)?
            .map(|v| Page::decode(&v))
            .transpose()
    }
    #[cfg(test)]
    pub fn read_page(&mut self, root: MetaRef, index: u64) -> Result<[u8; PAGE]> {
        let g = self.geometry();
        let mut bytes = [0; PAGE];
        if let Some(p) = self.page(root, index)? {
            let o = self.object(p.reference.object)?;
            if o.missing {
                return Err(Error::Missing(RemoteObject::from_object(
                    &o,
                    self.object_size(),
                )));
            }
            if o.kind != 1 || o.used as u64 > g.slots || p.reference.slot >= o.used as u64 {
                return Err(Error::Integrity("page outside object".into()));
            }
            self.device.read(
                o.extent * g.object_size + (g.payload_pages + p.reference.slot) * PAGE as u64,
                &mut bytes,
            )?;
            self.crypto.decode_page(index, p.reference, &mut bytes)?;
            if hash(&bytes) != p.digest {
                return Err(Error::Integrity("page content digest".into()));
            }
        }
        Ok(bytes)
    }
    pub fn blob(&mut self, r: MetaRef) -> Result<Vec<u8>> {
        read_blob(&self.device, &self.crypto, r)
    }
    pub fn count(&mut self, r: MetaRef, key: u64) -> Result<u64> {
        Ok(tree::get(self, &COUNTS, r, key)?
            .map(|v| u64::from_le_bytes(v.try_into().unwrap()))
            .unwrap_or(0))
    }
    pub fn open(device: Device, config: Config, crypto: Crypto) -> Result<Self> {
        device.set_control_region(crypto.geometry.object_size)?;
        let mut choices = Vec::new();
        for i in [1, 2] {
            let mut b = [0; PAGE];
            if device.read(i * PAGE as u64, &mut b).is_ok() {
                if let Ok(v) = crypto.unframe(codec::ROOT, i * PAGE as u64, &b) {
                    if let Ok(root) = serde_json::from_slice::<Root>(&v) {
                        if root.id == config.id {
                            choices.push(root)
                        }
                    }
                }
            }
        }
        let root = choices
            .into_iter()
            .max_by_key(|r| r.seq)
            .ok_or_else(|| Error::Integrity("no valid committed root".into()))?;
        let (free, free_chain) = load_free(&device, &crypto, root.free)?;
        let cloud: cloud::Cloud = if root.cloud.empty() {
            Default::default()
        } else {
            serde_json::from_slice(&read_blob(&device, &crypto, root.cloud)?)?
        };
        let snapshots = if root.snapshots.empty() {
            Vec::new()
        } else {
            serde_json::from_slice(&read_blob(&device, &crypto, root.snapshots)?)?
        };
        let classifier = if root.classifier.empty() {
            ntfs::Classifier::default()
        } else {
            serde_json::from_slice(&read_blob(&device, &crypto, root.classifier)?)?
        };
        let readers = Arc::new(Mutex::new(ReadRegistry {
            root: root.clone(),
            active: BTreeMap::new(),
            failure: None,
            transfer: Arc::new(BTreeMap::new()),
            receipts: Arc::default(),
            volatile_revision: 0,
            metrics: BTreeMap::new(),
        }));
        let mut s = Self {
            device: Arc::new(device),
            config,
            crypto: Arc::new(crypto),
            root,
            free,
            free_chain,
            readers,
            prepare_cache: PrepareCache::default(),
            failure: None,
            classifier,
            cloud,
            receipts: Default::default(),
            snapshots,
            diagnostics: BTreeMap::new(),
            restore_cache: std::collections::VecDeque::new(),
            cache_runtime: None,
        };
        if s.root.cache_capable && !s.config.cache_capable {
            return Err(Error::Integrity("cache capability header mismatch".into()));
        }
        // Repair both root mirrors before any upgrade transaction can reuse pages
        // freed by the newer selected root after an interrupted publication.
        s.mirror()?;
        super::maintenance::validate_statistics(&mut s)?;
        s.recover_receipts()?;
        if s.config.cache_capable {
            s.promote_cache_capability()?;
        }
        if s.cloud
            .compact
            .as_ref()
            .is_some_and(|c| !matches!(c.phase.as_str(), "done" | "cancelled") && !c.paused)
        {
            let mut cloud = s.cloud.clone();
            cloud.compact.as_mut().unwrap().paused = true;
            s.transaction(|tx| tx.save_cloud(&cloud))?;
            s.cloud = cloud;
        }
        Ok(s)
    }
    pub fn create(device: Device, config: Config, crypto: Crypto) -> Result<Self> {
        let g = crypto.geometry;
        device.set_control_region(g.object_size)?;
        device.grow(g.object_size)?;
        let bytes = config.encode()?;
        device.write(0, &bytes)?;
        device.write(3 * PAGE as u64, &bytes)?;
        device.sync()?;
        let mut root = Root::new(config.id);
        root.restore_required = config.restoring;
        root.cache_capable = config.cache_capable;
        let readers = Arc::new(Mutex::new(ReadRegistry {
            root: root.clone(),
            active: BTreeMap::new(),
            failure: None,
            transfer: Arc::new(BTreeMap::new()),
            receipts: Arc::default(),
            volatile_revision: 0,
            metrics: BTreeMap::new(),
        }));
        let mut s = Self {
            device: Arc::new(device),
            root,
            config,
            crypto: Arc::new(crypto),
            readers,
            prepare_cache: PrepareCache::default(),
            free: Free::default(),
            free_chain: Vec::new(),
            failure: None,
            classifier: Default::default(),
            cloud: Default::default(),
            receipts: Default::default(),
            snapshots: Vec::new(),
            diagnostics: BTreeMap::new(),
            restore_cache: std::collections::VecDeque::new(),
            cache_runtime: None,
        };
        s.transaction(|_| Ok(()))?;
        Ok(s)
    }
    fn mirror(&self) -> Result<()> {
        let data = serde_json::to_vec(&self.root)?;
        for i in [1, 2] {
            self.device.write(
                i * PAGE as u64,
                &self
                    .crypto
                    .frame(codec::ROOT, self.root.seq, i * PAGE as u64, &data)?,
            )?;
            self.device.sync()?;
        }
        Ok(())
    }
    pub fn verify_object(&mut self, o: &Object) -> Result<Vec<u8>> {
        let raw = super::object_bytes::read_verified(&self.device, &self.crypto, o)?;
        *self.diagnostics.entry("export_raw_bytes".into()).or_default() += self.object_size();
        Ok(raw)
    }

}
impl Storage for Store {
    fn read_node(&mut self, r: MetaRef) -> Result<Vec<u8>> {
        let g = self.geometry();
        if let Some(v) = self.prepare_cache.node(r, self.root.seq) {
            return Ok(v);
        }
        let mut b = [0; PAGE];
        if r.offset & PORTABLE != 0 {
            let p = r.offset & !PORTABLE;
            let o = self.object(p / g.object_size)?;
            super::portable_validation::validate_node_reference(r, &o, g)?;
            if o.missing { return Err(Error::Missing(super::RemoteObject::from_object(&o, g.object_size))); }
            self.device
                .read(o.extent * g.object_size + p % g.object_size, &mut b)?;
        } else {
            self.device.read(r.offset, &mut b)?;
        }
        if hash(&b) != r.hash {
            return Err(Error::Integrity("index node digest".into()));
        }
        let v = self.crypto.unframe(codec::NODE, r.offset, &b)?;
        self.prepare_cache.put_node(r, self.root.seq, v.clone());
        Ok(v)
    }
    fn read_decoded(&mut self, spec: &Spec, r: MetaRef) -> Result<Node> {
        if let Some(node) = self.prepare_cache.decoded(r, spec, self.root.seq) {
            return Ok(node);
        }
        let payload = self.read_node(r)?;
        let node = tree::decode_at(spec, r, &payload)?;
        self.prepare_cache.put_decoded(r, *spec, self.root.seq, payload, node.clone());
        Ok(node)
    }
    fn prefetch_nodes(&mut self, references: &[MetaRef]) -> Result<()> {
        let g = self.geometry();
        let references = unique_cache_misses(&self.prepare_cache, references, self.root.seq);
        let ids = references.iter().filter(|r| r.offset & PORTABLE != 0)
            .map(|r| (r.offset & !PORTABLE) / g.object_size).collect::<BTreeSet<_>>();
        let objects = self.objects_many(&ids.into_iter().collect::<Vec<_>>())?;
        let mut positions = Vec::with_capacity(references.len());
        for reference in references {
            let physical = node_position(reference, &objects, g)?;
            positions.push((physical, reference));
        }
        prefetch_frames(&mut self.prepare_cache, &self.device, &self.crypto, self.root.seq, positions)
    }
    fn write_node(&mut self, _: &[u8]) -> Result<MetaRef> {
        Err(Error::Invalid("read-only tree context".into()))
    }
}
pub(super) struct Txn<'a> {
    pub store: &'a mut Store,
    pub root: Root,
    free: Free,
    chain: Vec<MetaRef>,
    freelog: FreeLog,
    retired: BTreeSet<u64>,
    refs: BTreeMap<u64, (i64, u8)>,
    pub objects: BTreeMap<u64, Object>,
    deleted: BTreeMap<u64, Object>,
    pending_slots: BTreeMap<u64, Option<Vec<u8>>>,
    pending_externals: BTreeMap<u64, Option<Vec<u8>>>,
    pending_data: BTreeMap<u64, [u8; PAGE]>,
    pending_metadata: BTreeMap<u64, [u8; PAGE]>,
    fresh_nodes: BTreeMap<u64, MetaRef>,
    pending_blocks: Vec<(u64, u64)>,
    pub portable: Option<u64>,
    pub cloud_deltas: BTreeMap<u64, i64>,
}

fn unique_cache_misses(cache: &PrepareCache, references: &[MetaRef], epoch: u64) -> Vec<MetaRef> {
    let mut seen = BTreeSet::new();
    references.iter().copied().filter(|r| !r.empty()
        && !cache.contains_node(*r, epoch) && seen.insert((r.offset, r.hash))).collect()
}

fn node_position(r: MetaRef, objects: &BTreeMap<u64, Object>, g: Geometry) -> Result<u64> {
    if r.offset & PORTABLE == 0 { return Ok(r.offset); }
    let logical = r.offset & !PORTABLE;
    let object = objects.get(&(logical / g.object_size))
        .ok_or_else(|| Error::Integrity("prefetch object missing".into()))?;
    super::portable_validation::validate_node_reference(r, object, g)?;
    if object.missing {
        return Err(Error::Missing(RemoteObject::from_object(object, g.object_size)));
    }
    Ok(object.extent * g.object_size + logical % g.object_size)
}

fn prefetch_frames(cache: &mut PrepareCache, device: &Device, crypto: &Crypto,
    epoch: u64, mut positions: Vec<(u64, MetaRef)>) -> Result<()> {
    positions.sort_by_key(|(offset, _)| *offset);
    let mut first = 0;
    while first < positions.len() {
        let mut end = first + 1;
        // Never fill holes: every page in the request is needed by this batch.
        while end < positions.len() && end - first < 64
            && positions[end].0 == positions[end - 1].0 + PAGE as u64 { end += 1; }
        let mut frames = vec![0; (end - first) * PAGE];
        device.read(positions[first].0, &mut frames)?;
        cache.count("prepare_index_prefetch_batches", 1);
        cache.count("prepare_index_prefetch_pages", (end - first) as u64);
        for ((_, reference), frame) in positions[first..end].iter().zip(frames.chunks_exact(PAGE)) {
            if hash(frame) != reference.hash { return Err(Error::Integrity("index node digest".into())); }
            let payload = crypto.unframe(codec::NODE, reference.offset, frame.try_into().unwrap())?;
            cache.put_node(*reference, epoch, payload);
        }
        first = end;
    }
    Ok(())
}

impl Store {
    fn objects_many(&mut self, ids: &[u64]) -> Result<BTreeMap<u64, Object>> {
        let ids = ids.iter().copied().collect::<BTreeSet<_>>().into_iter().collect::<Vec<_>>();
        if ids.is_empty() { return Ok(BTreeMap::new()); }
        self.prepare_cache.count("prepare_object_lookup_batches", 1);
        let values = tree::get_many(self, &OBJECTS, self.root.objects, &ids)?;
        ids.into_iter().zip(values).map(|(id, value)| {
            let value = value.ok_or_else(|| Error::Integrity(format!("missing object {id}")))?;
            Ok((id, Object::decode(id, &value)?))
        }).collect()
    }
}

impl Txn<'_> {
    pub fn geometry(&self) -> Geometry {
        self.store.crypto.geometry
    }

    pub fn object(&mut self, oid: u64) -> Result<Object> {
        if let Some(o) = self.objects.get(&oid) {
            return Ok(o.clone());
        }
        let r = self.root.objects;
        let v = tree::get(self, &OBJECTS, r, oid)?
            .ok_or_else(|| Error::Integrity(format!("missing object {oid}")))?;
        Object::decode(oid, &v)
    }
    fn objects_many(&mut self, ids: &[u64]) -> Result<BTreeMap<u64, Object>> {
        let ids = ids.iter().copied().collect::<BTreeSet<_>>();
        let mut result = BTreeMap::new();
        let mut missing = Vec::new();
        for id in ids {
            if let Some(object) = self.objects.get(&id) { result.insert(id, object.clone()); }
            else { missing.push(id); }
        }
        if !missing.is_empty() {
            self.store.prepare_cache.count("prepare_object_lookup_batches", 1);
            let values = tree::get_many(self, &OBJECTS, self.root.objects, &missing)?;
            for (id, value) in missing.into_iter().zip(values) {
                let value = value.ok_or_else(|| Error::Integrity(format!("missing object {id}")))?;
                result.insert(id, Object::decode(id, &value)?);
            }
        }
        Ok(result)
    }
    pub fn set(
        &mut self,
        spec: &Spec,
        old: MetaRef,
        changes: &[(u64, Option<Vec<u8>>)],
    ) -> Result<MetaRef> {
        if spec.tag == PAGES.tag && old == self.root.index {
            let mut deltas = BTreeMap::<u64, i64>::new();
            let keys = changes.iter().map(|(key, _)| *key).collect::<Vec<_>>();
            let prior = tree::get_many(self, &PAGES, old, &keys)?;
            for ((_, value), before) in changes.iter().zip(prior) {
                if let Some(before) = before {
                    *deltas
                        .entry(Page::decode(&before)?.reference.object)
                        .or_default() -= 1;
                }
                if let Some(after) = value {
                    *deltas
                        .entry(Page::decode(after)?.reference.object)
                        .or_default() += 1;
                }
            }
            deltas.retain(|_, delta| *delta != 0);
            let mut objects = self.objects_many(&deltas.keys().copied().collect::<Vec<_>>())?;
            for (oid, delta) in deltas {
                let mut o = objects.remove(&oid).unwrap();
                // Imported immutable subtrees are protected by their source root, not
                // by a guessed zero reference count before their leaves are visited.
                if self.root.deferred_index && o.origin_backed { continue; }
                o.current_refs = o
                    .current_refs
                    .checked_add_signed(delta)
                    .ok_or_else(|| Error::Integrity("current object reference underflow".into()))?;
                self.objects.insert(oid, o);
            }
        }
        let new = tree::set_many(self, spec, old, changes)?;
        self.replace(spec, old, new);
        Ok(new)
    }
    pub fn replace(&mut self, spec: &Spec, old: MetaRef, new: MetaRef) {
        if spec.tracked && old != new {
            self.refdelta(new, 1, spec.tag);
            self.refdelta(old, -1, spec.tag)
        }
    }
    fn refdelta(&mut self, r: MetaRef, n: i64, tag: u8) {
        if self.root.deferred_index && r.offset & PORTABLE != 0 { return; }
        if !r.empty() {
            let e = self.refs.entry(r.offset).or_insert((0, tag));
            e.0 += n;
        }
    }
    fn fresh_page(&mut self) -> Result<u64> {
        let g = self.geometry();
        if self.root.meta_slot >= g.pages {
            self.root.meta_extent = self.root.next_extent;
            self.root.next_extent += 1;
            self.root.meta_slot = 0;
            self.pending_blocks.push((self.root.meta_extent, 0));
            self.store
                .device
                .grow(self.root.next_extent * g.object_size)?;
        }
        let p = self.root.meta_extent * g.object_size + self.root.meta_slot * PAGE as u64;
        self.root.meta_slot += 1;
        Ok(p)
    }
    fn alloc_page(&mut self) -> Result<u64> {
        let min = self
            .store
            .readers
            .lock()
            .map_err(|_| Error::Poisoned)?
            .active
            .keys()
            .next()
            .copied()
            .unwrap_or(u64::MAX);
        let candidate = self
            .free
            .pages
            .iter()
            .find(|(_, seq)| **seq < min && **seq <= self.store.root.seq)
            .map(|(p, _)| *p);
        if let Some(p) = candidate {
            self.free.pages.remove(&p);
            self.freelog.remove_pages.insert(p);
            Ok(p)
        } else {
            self.fresh_page()
        }
    }
    pub fn blob(&mut self, data: &[u8]) -> Result<MetaRef> {
        let mut next = MetaRef::default();
        for part in data.chunks(codec::PAYLOAD - 40).rev() {
            let offset = self.alloc_page()?;
            let mut payload = vec![0; 40];
            next.put(&mut payload);
            payload.extend_from_slice(part);
            let bytes =
                self.store
                    .crypto
                    .frame(codec::CLOUD_BLOB, self.root.seq + 1, offset, &payload)?;
            self.buffer_metadata(offset, bytes)?;
            next = MetaRef {
                offset,
                hash: hash(&bytes),
            };
        }
        Ok(next)
    }
    pub fn replace_blob(&mut self, old: MetaRef, data: &[u8]) -> Result<MetaRef> {
        self.retire_blob(old)?;
        self.blob(data)
    }
    pub fn read_blob(&self, r: MetaRef) -> Result<Vec<u8>> {
        read_blob_frames(&self.store.crypto, r, |offset, frame| self.read_frame(offset, frame))
    }
    fn retire_blob(&mut self, mut r: MetaRef) -> Result<()> {
        let mut visited = BTreeSet::new();
        while !r.empty() {
            if !visited.insert(r.offset) { return Err(Error::Integrity("blob cycle".into())); }
            let mut b = [0; PAGE];
            self.read_frame(r.offset, &mut b)?;
            if hash(&b) != r.hash {
                return Err(Error::Integrity("blob hash".into()));
            }
            let p = self.store.crypto.unframe(codec::CLOUD_BLOB, r.offset, &b)?;
            if p.len() < 40 {
                return Err(Error::Integrity("blob framing".into()));
            }
            self.retire_page(r.offset);
            r = MetaRef::get(&p);
        }
        Ok(())
    }
    pub fn save_cloud(&mut self, cloud: &cloud::Cloud) -> Result<()> {
        self.root.cached_published_generation = cloud.published_generation;
        self.root.cloud = self.replace_blob(self.root.cloud, &serde_json::to_vec(cloud)?)?;
        Ok(())
    }
    pub fn save_snapshots(&mut self, snapshots: &[cloud::Snapshot]) -> Result<()> {
        self.root.snapshot_count = snapshots.len();
        self.root.snapshots =
            self.replace_blob(self.root.snapshots, &serde_json::to_vec(snapshots)?)?;
        Ok(())
    }
    pub fn allocate_object(&mut self, kind: u8, pool: u8) -> Result<Object> {
        let g = self.geometry();
        let range = allocation_range(&self.store.config)?;
        self.root.next_oid = self.root.next_oid.max(range.start);
        if self.root.next_oid >= range.end {
            return Err(Error::Invalid(
                "volume object identity space exhausted".into(),
            ));
        }
        let min = self
            .store
            .readers
            .lock()
            .map_err(|_| Error::Poisoned)?
            .active
            .keys()
            .next()
            .copied()
            .unwrap_or(u64::MAX);
        let reusable = self
            .free
            .extents
            .iter()
            .find(|(_, seq)| **seq < min && **seq <= self.store.root.seq)
            .map(|(e, _)| *e);
        let extent = if let Some(e) = reusable {
            self.free.extents.remove(&e);
            self.freelog.remove_extents.insert(e);
            self.free.punched.remove(&e);
            self.freelog.remove_punched.insert(e);
            e
        } else {
            let e = self.root.next_extent;
            self.root.next_extent += 1;
            e
        };
        self.store.device.grow((extent + 1) * g.object_size)?;
        let oid = self.root.next_oid;
        self.root.next_oid += 1;
        // Keep the ordinal decodable while distributing cloud directory prefixes.
        // The complete UUID is persisted; old identities are never reconstructed.
        let mut seed = [0; 24];
        seed[..16].copy_from_slice(self.root.id.as_bytes());
        seed[16..].copy_from_slice(&oid.to_le_bytes());
        let mut id = [0; 16];
        let mut scoped = seed.to_vec();
        scoped.extend_from_slice(&self.root.allocation_nonce);
        let digest = hash(&scoped);
        id[..8].copy_from_slice(&digest[..8]);
        id[8..].copy_from_slice(&oid.to_le_bytes());
        let o = Object {
            oid,
            id: Uuid::from_bytes(id),
            extent,
            kind,
            pool,
            used: 0,
            sealed: false,
            missing: false,
            remote: false,
            remote_source: 0,
            cache_backed: false,
            origin_backed: false,
            sync_state: if self.store.cloud.binding.is_some() {
                1
            } else {
                0
            },
            refs: 0,
            current_refs: 0,
            cloud_refs: 0,
            sha: [0; 32],
            external_count: 0,
        };
        self.objects.insert(oid, o.clone());
        self.pending_blocks.push((extent, oid));
        Ok(o)
    }
    pub fn append_page(&mut self, index: u64, bytes: &[u8; PAGE], pool: usize) -> Result<Page> {
        let encoded =
            super::crypto_work::encode_one(&self.store.crypto, index, self.root.seq + 1, bytes)?;
        *self
            .store
            .diagnostics
            .entry("crypto_pages".into())
            .or_default() += 1;
        *self
            .store
            .diagnostics
            .entry("crypto_nonce_batches".into())
            .or_default() += 1;
        self.append_encoded(encoded, pool)
    }
    pub fn append_encoded(
        &mut self,
        mut encoded: super::crypto_work::EncodedPage,
        pool: usize,
    ) -> Result<Page> {
        let g = self.geometry();
        let mut o = if self.root.tails[pool] == 0 {
            let o = self.allocate_object(1, pool as u8)?;
            self.root.tails[pool] = o.oid;
            o
        } else {
            self.object(self.root.tails[pool])?
        };
        if encoded.reference.version != self.root.seq + 1 {
            return Err(Error::Integrity("encoded page transaction version".into()));
        }
        self.pending_data.insert(
            o.extent * g.object_size + (g.payload_pages + o.used as u64) * PAGE as u64,
            *encoded.cipher,
        );
        encoded.reference.object = o.oid;
        encoded.reference.slot = o.used as u64;
        let p = Page {
            reference: encoded.reference,
            digest: encoded.digest,
        };
        let mut descriptor = encoded.index.to_le_bytes().to_vec();
        descriptor.extend_from_slice(&p.encode());
        self.pending_slots
            .insert(o.oid * g.pages + o.used as u64, Some(descriptor));
        o.used += 1;
        self.objects.insert(o.oid, o.clone());
        if o.used as u64 == g.slots {
            self.seal(o.oid, None)?;
            self.root.tails[pool] = 0;
        }
        Ok(p)
    }
    pub fn seal(&mut self, oid: u64, root_descriptor: Option<&[u8]>) -> Result<Object> {
        let g = self.geometry();
        let mut o = self.object(oid)?;
        if o.sealed {
            return Ok(o);
        }
        let used_end = super::object_bytes::payload_end(g, o.kind as u64, o.used as u64)?;
        if o.external_count as u64 > g.external_limit || (o.kind == 1 && o.external_count != 0) {
            return Err(Error::Integrity("active object dependency boundary".into()));
        }
        if root_descriptor.is_some() && o.kind != 2 {
            return Err(Error::Invalid("root descriptor requires a metadata object".into()));
        }
        // Fresh frames are still immutable writer-owned bytes. Do not write them
        // merely to reread/decrypt them here; they join the transaction's one data write.
        let mut raw = vec![0; g.object_size as usize];
        let mut fresh = vec![false; o.used as usize];
        // Metadata reserves slot zero for an optional root descriptor. It is
        // initialized explicitly below, never inherited from a recycled extent.
        let mut slot = usize::from(o.kind == 2);
        while slot < o.used as usize {
            let physical = o.extent * g.object_size + (g.payload_pages + slot as u64) * PAGE as u64;
            if let Some(bytes) = self.pending_data.get(&physical) {
                raw[(g.payload_pages as usize + slot) * PAGE
                    ..(g.payload_pages as usize + 1 + slot) * PAGE]
                    .copy_from_slice(bytes);
                fresh[slot] = true;
                slot += 1;
            } else if let Some(bytes) = (o.kind == 2).then(||
                self.store.prepare_cache.cipher(oid, o.extent, slot as u64, self.root.seq + 1)).flatten() {
                raw[(g.payload_pages as usize + slot) * PAGE
                    ..(g.payload_pages as usize + 1 + slot) * PAGE].copy_from_slice(&bytes);
                fresh[slot] = true;
                self.store.prepare_cache.count("prepare_seal_cached_pages", 1);
                slot += 1;
            } else {
                let first = slot;
                slot += 1;
                while slot < o.used as usize
                    && !self.pending_data.contains_key(
                        &(o.extent * g.object_size + (g.payload_pages + slot as u64) * PAGE as u64),
                    )
                    && !(o.kind == 2 && self.store.prepare_cache.has_cipher(
                        oid, o.extent, slot as u64, self.root.seq + 1))
                {
                    slot += 1;
                }
                self.store.device.read(
                    o.extent * g.object_size + (g.payload_pages + first as u64) * PAGE as u64,
                    &mut raw[(g.payload_pages as usize + first) * PAGE
                        ..(g.payload_pages as usize + slot) * PAGE],
                )?;
                *self
                    .store
                    .diagnostics
                    .entry("seal_disk_pages".into())
                    .or_default() += (slot - first) as u64;
            }
        }
        *self
            .store
            .diagnostics
            .entry("seal_fresh_pages".into())
            .or_default() += fresh.iter().filter(|v| **v).count() as u64;
        if o.kind == 1 {
            let keys = (0..o.used as u64)
                .map(|slot| oid * g.pages + slot)
                .collect::<Vec<_>>();
            let missing = keys
                .iter()
                .filter(|key| !self.pending_slots.contains_key(key))
                .copied()
                .collect::<Vec<_>>();
            let persisted = tree::get_many(self, &SLOTSPEC, self.root.slots, &missing)?;
            let mut descriptors = missing
                .into_iter()
                .zip(persisted)
                .collect::<BTreeMap<_, _>>();
            for slot in 0..o.used as u64 {
                let key = oid * g.pages + slot;
                let v = if let Some(v) = self.pending_slots.get(&key) {
                    v.clone()
                } else {
                    descriptors.remove(&key).flatten()
                }
                .ok_or_else(|| Error::Integrity("active object descriptor missing".into()))?;
                let lba = u64::from_le_bytes(v[..8].try_into().unwrap());
                let p = Page::decode(&v[8..])?;
                let start = (g.payload_pages as usize + slot as usize) * PAGE;
                if !fresh[slot as usize] {
                    let mut plain: [u8; PAGE] = raw[start..start + PAGE].try_into().unwrap();
                    self.store
                        .crypto
                        .decode_page(lba, p.reference, &mut plain)?;
                    if hash(&plain) != p.digest {
                        return Err(Error::Integrity("unsealed page digest".into()));
                    }
                    *self
                        .store
                        .diagnostics
                        .entry("seal_validated_pages".into())
                        .or_default() += 1;
                }
                let d = PAGE + slot as usize * 64;
                raw[d..d + 8].copy_from_slice(&lba.to_le_bytes());
                raw[d + 8..d + 16].copy_from_slice(&p.reference.version.to_le_bytes());
                raw[d + 16..d + 40].copy_from_slice(&p.reference.nonce);
                raw[d + 40..d + 56].copy_from_slice(&p.reference.tag);
            }
        } else {
            for slot in 1..o.used as u64 {
                let at = (g.payload_pages as usize + slot as usize) * PAGE;
                let logical = PORTABLE | (oid * g.object_size + at as u64);
                if !fresh[slot as usize] {
                    self.store.crypto.unframe(
                        codec::NODE,
                        logical,
                        raw[at..at + PAGE].try_into().unwrap(),
                    )?;
                    *self
                        .store
                        .diagnostics
                        .entry("seal_validated_pages".into())
                        .or_default() += 1;
                }
            }
            for (i, v) in self
                .dependencies(oid, o.external_count as usize)?
                .rows.into_iter()
                .enumerate()
            {
                let at = PAGE + i * 48;
                raw[at..at + 48].copy_from_slice(&v);
            }
            if let Some(root) = root_descriptor {
                let logical = PORTABLE | (oid * g.object_size + g.payload_pages * PAGE as u64);
                let frame =
                    self.store
                        .crypto
                        .frame(codec::ROOT, self.root.seq + 1, logical, root)?;
                raw[g.header_bytes()..(g.payload_pages as usize + 1) * PAGE]
                    .copy_from_slice(&frame);
            }
        }
        let config_bytes = serde_json::to_vec(&self.store.config)?;
        if config_bytes.len() > 980 {
            return Err(Error::Invalid("portable configuration too large".into()));
        }
        let c = g.header_bytes() - 1024;
        raw[c..c + 8].copy_from_slice(b"ODV4PUB1");
        raw[c + 8..c + 12].copy_from_slice(&(config_bytes.len() as u32).to_le_bytes());
        raw[c + 12..c + 12 + config_bytes.len()].copy_from_slice(&config_bytes);
        let config_hash = hash(&raw[c..c + 992]);
        raw[c + 992..c + 1024].copy_from_slice(&config_hash);
        let mut table = raw[PAGE..g.header_bytes() - 1024].to_vec();
        let aad = format!("OverlayDisk v4 object table {}", o.id);
        let (nonce, tag) = self.store.crypto.seal_blob(aad.as_bytes(), &mut table)?;
        raw[PAGE..g.header_bytes() - 1024].copy_from_slice(&table);
        let head = serde_json::json!({"id":o.id,"oid":oid,"kind":o.kind,"used":o.used,"external_count":o.external_count,"body_sha256":hex(&hash(&raw[PAGE..])),"table_nonce":hex(&nonce),"table_tag":hex(&tag),"config":self.store.config});
        let header = self.store.crypto.frame(
            codec::OBJECT_HEADER,
            self.root.seq + 1,
            PORTABLE | (oid * g.object_size),
            &serde_json::to_vec(&head)?,
        )?;
        raw[..PAGE].copy_from_slice(&header);
        // Only headers and the reserved root slot change at sealing. The unused
        // suffix remains logical zeros even if this physical extent holds junk.
        let header_end = g.header_bytes() + if o.kind == 2 { PAGE } else { 0 };
        self.store.device.write(o.extent * g.object_size, &raw[..header_end])?;
        *self.store.diagnostics.entry("seal_logical_zero_bytes".into()).or_default()
            += (raw.len() - used_end) as u64;
        o.sha = hash(&raw);
        o.sealed = true;
        self.store.prepare_cache.remove_object(oid, o.extent, o.used as u64);
        self.objects.insert(oid, o.clone());
        let slots = self.root.slots;
        self.root.slots =
            tree::remove_range(self, &SLOTSPEC, slots, oid * g.pages, (oid + 1) * g.pages)?;
        self.pending_slots.retain(|key, _| *key / g.pages != oid);
        let ext = self.root.externals;
        self.root.externals = tree::remove_range(
            self,
            &EXTERNALS,
            ext,
            oid * g.external_stride,
            (oid + 1) * g.external_stride,
        )?;
        self.pending_externals
            .retain(|key, _| *key / g.external_stride != oid);
        *self
            .store
            .diagnostics
            .entry("sealed_objects".into())
            .or_default() += 1;
        Ok(o)
    }
    pub fn free_object(&mut self, oid: u64) -> Result<u64> {
        let g = self.geometry();
        let o = self.object(oid)?;
        if o.refs != 0 {
            return Err(Error::Invalid("object still has local references".into()));
        }
        self.store.prepare_cache.remove_object(oid, o.extent, o.used as u64);
        self.deleted.insert(oid, o.clone());
        self.objects.remove(&oid);
        if o.missing {
            return Ok(0);
        }
        self.root.blocks = self.set(
            &BLOCKS,
            self.root.blocks,
            &[(o.extent, Some(u64::MAX.to_le_bytes().to_vec()))],
        )?;
        self.free.extents.insert(o.extent, self.root.seq + 1);
        self.freelog.add.extents.insert(o.extent, self.root.seq + 1);
        self.free.punched.remove(&o.extent);
        self.freelog.remove_punched.insert(o.extent);
        Ok(g.object_size)
    }
    fn apply_refs(&mut self) -> Result<()> {
        let g = self.geometry();
        while !self.refs.is_empty() {
            let batch = std::mem::take(&mut self.refs);
            let mut local = Vec::new();
            let mut portable = Vec::new();
            let local_keys = batch.iter().filter(|(offset, (delta, _))| **offset & PORTABLE == 0 && *delta != 0)
                .map(|(offset, _)| *offset / PAGE as u64).collect::<Vec<_>>();
            let portable_keys = batch.iter().filter(|(offset, (delta, _))| **offset & PORTABLE != 0 && *delta != 0)
                .map(|(offset, _)| (*offset & !PORTABLE) / PAGE as u64).collect::<Vec<_>>();
            let values = tree::get_many(self, &REFS, self.root.refs, &local_keys)?;
            let mut local_before = local_keys.into_iter().zip(values).collect::<BTreeMap<_, _>>();
            let values = tree::get_many(self, &PORTREFS, self.root.portable_refs, &portable_keys)?;
            let mut portable_before = portable_keys.into_iter().zip(values).collect::<BTreeMap<_, _>>();
            self.store.prepare_cache.count("prepare_reference_lookup_batches",
                u64::from(!local_before.is_empty()) + u64::from(!portable_before.is_empty()));
            for (offset, (delta, tag)) in batch {
                if delta == 0 {
                    continue;
                }
                let remote = offset & PORTABLE != 0;
                let key = (offset & !PORTABLE) / PAGE as u64;
                let before = if remote { portable_before.remove(&key) } else { local_before.remove(&key) }
                    .flatten().map(|b| u64::from_le_bytes(b[..8].try_into().unwrap())).unwrap_or(0);
                let after = before
                    .checked_add_signed(delta)
                    .ok_or_else(|| Error::Integrity("node reference underflow".into()))?;
                let value = if after == 0 {
                    let payload = if let Some(reference) = self.fresh_nodes.get(&offset).copied() {
                        self.read_node(reference)?
                    } else if remote {
                        let object = self.object((offset & !PORTABLE) / g.object_size)?;
                        let mut b = [0; PAGE];
                        self.read_frame(
                            object.extent * g.object_size + (offset & !PORTABLE) % g.object_size,
                            &mut b,
                        )?;
                        self.store.crypto.unframe(codec::NODE, offset, &b)?
                    } else {
                        let mut b = [0; PAGE];
                        self.read_frame(offset, &mut b)?;
                        self.store.crypto.unframe(codec::NODE, offset, &b)?
                    };
                    match tree::decode_at(
                        &spec(tag)?,
                        MetaRef {
                            offset,
                            hash: [0; 32],
                        },
                        &payload,
                    )? {
                        Node::Branch { children, .. } => {
                            for (_, child) in children {
                                self.refdelta(child, -1, tag)
                            }
                        }
                        Node::Leaf { values, .. } => {
                            if tag == PAGES.tag {
                                let mut counts = BTreeMap::<u64, u64>::new();
                                for (_, v) in values {
                                    let p = Page::decode(&v)?;
                                    *counts.entry(p.reference.object).or_default() += 1;
                                }
                                let mut objects = self.objects_many(&counts.keys().copied().collect::<Vec<_>>())?;
                                for (oid, count) in counts {
                                    let mut o = objects.remove(&oid).unwrap();
                                    o.refs = o.refs.checked_sub(count).ok_or_else(|| {
                                        Error::Integrity("data reference underflow".into())
                                    })?;
                                    self.objects.insert(o.oid, o);
                                }
                            }
                        }
                    }
                    if remote {
                        let mut object = self.object((offset & !PORTABLE) / g.object_size)?;
                        object.refs = object.refs.checked_sub(1).ok_or_else(|| {
                            Error::Integrity("portable object reference underflow".into())
                        })?;
                        self.objects.insert(object.oid, object);
                    } else {
                        self.retire_page(offset);
                    }
                    None
                } else {
                    let mut v = vec![0; 16];
                    v[..8].copy_from_slice(&after.to_le_bytes());
                    v[8] = tag;
                    Some(v)
                };
                if remote {
                    portable.push((key, value));
                } else {
                    local.push((key, value));
                }
            }
            if !local.is_empty() {
                self.root.refs = tree::set_many(self, &REFS, self.root.refs, &local)?;
            }
            if !portable.is_empty() {
                self.root.portable_refs =
                    tree::set_many(self, &PORTREFS, self.root.portable_refs, &portable)?;
            }
        }
        Ok(())
    }
    fn finish(&mut self) -> Result<()> {
        self.flush_data()?;
        if !self.pending_slots.is_empty() {
            let changes = std::mem::take(&mut self.pending_slots)
                .into_iter()
                .collect::<Vec<_>>();
            self.root.slots = self.set(&SLOTSPEC, self.root.slots, &changes)?;
        }
        if !self.pending_externals.is_empty() {
            let changes = std::mem::take(&mut self.pending_externals)
                .into_iter()
                .collect::<Vec<_>>();
            self.root.externals = self.set(&EXTERNALS, self.root.externals, &changes)?;
        }
        self.apply_refs()?;
        while !self.objects.is_empty()
            || !self.deleted.is_empty()
            || !self.pending_blocks.is_empty()
        {
            let pending = std::mem::take(&mut self.objects);
            let deleted = std::mem::take(&mut self.deleted);
            let mut stats = Vec::new();
            let mut changes = Vec::new();
            let ids = pending.keys().copied().collect::<Vec<_>>();
            if !ids.is_empty() { self.store.prepare_cache.count("prepare_object_lookup_batches", 1); }
            let prior = tree::get_many(self, &OBJECTS, self.root.objects, &ids)?;
            let mut prior = ids.into_iter().zip(prior).collect::<BTreeMap<_, _>>();
            for (oid, o) in pending {
                let before = prior.remove(&oid).flatten().map(|v| Object::decode(oid, &v)).transpose()?;
                stats.push((before, Some(o.clone())));
                changes.push((oid, Some(o.encode())));
            }
            for (oid, old) in deleted {
                stats.push((Some(old), None));
                changes.push((oid, None));
            }
            changes.sort_by_key(|v| v.0);
            if !stats.is_empty() {
                for (before, after) in &stats {
                    for (o, add) in before
                        .iter()
                        .map(|o| (o, false))
                        .chain(after.iter().map(|o| (o, true)))
                    {
                        if o.remote {
                            let adjust = |v: &mut u64, present: bool| -> Result<()> {
                                if present {
                                    *v = if add {
                                        v.checked_add(1)
                                    } else {
                                        v.checked_sub(1)
                                    }
                                    .ok_or_else(|| {
                                        Error::Integrity("lazy counter overflow".into())
                                    })?;
                                }
                                Ok(())
                            };
                            adjust(&mut self.root.lazy_total_objects, true)?;
                            adjust(&mut self.root.lazy_cached_objects, !o.missing)?;
                            adjust(&mut self.root.lazy_missing_objects, o.missing && o.refs > 0)?;
                        }
                    }
                }
                super::maintenance::apply_object_changes(self, &stats)?;
            }
            if !changes.is_empty() {
                let r = self.root.objects;
                self.root.objects = tree::set_many(self, &OBJECTS, r, &changes)?;
            }
            let mut blocks = BTreeMap::new();
            for (k, v) in std::mem::take(&mut self.pending_blocks) {
                blocks.insert(k, Some(v.to_le_bytes().to_vec()));
            }
            if !blocks.is_empty() {
                let r = self.root.blocks;
                self.root.blocks =
                    tree::set_many(self, &BLOCKS, r, &blocks.into_iter().collect::<Vec<_>>())?;
            }
        }
        let checkpoint = self.root.free.empty()
            || self.root.free_events
                >= 4096u64.max((self.free.pages.len() + self.free.extents.len()) as u64);
        if checkpoint {
            for r in std::mem::take(&mut self.chain) {
                self.retire_blob(r)?;
            }
        }
        let mut reserved = Vec::new();
        let data = loop {
            for p in std::mem::take(&mut self.retired) {
                self.free.pages.insert(p, self.root.seq + 1);
                self.freelog.add.pages.insert(p, self.root.seq + 1);
            }
            let data = if checkpoint {
                serde_json::to_vec(&FreeLog {
                    reset: true,
                    add: self.free.clone(),
                    ..Default::default()
                })?
            } else {
                self.freelog.previous = self.root.free;
                serde_json::to_vec(&self.freelog)?
            };
            let needed = data.len().div_ceil(codec::PAYLOAD - 40).max(1);
            while reserved.len() < needed {
                reserved.push(self.fresh_page()?);
            }
            if self.pending_blocks.is_empty() {
                break data;
            }
            let mut blocks = BTreeMap::new();
            for (k, v) in std::mem::take(&mut self.pending_blocks) {
                blocks.insert(k, Some(v.to_le_bytes().to_vec()));
            }
            let r = self.root.blocks;
            self.root.blocks =
                tree::set_many(self, &BLOCKS, r, &blocks.into_iter().collect::<Vec<_>>())?;
        };
        let mut next = MetaRef::default();
        for (i, offset) in reserved.into_iter().enumerate().rev() {
            let start = i * (codec::PAYLOAD - 40);
            let part = if start < data.len() {
                &data[start..(start + codec::PAYLOAD - 40).min(data.len())]
            } else {
                &[]
            };
            let mut payload = vec![0; 40];
            next.put(&mut payload);
            payload.extend_from_slice(part);
            let b =
                self.store
                    .crypto
                    .frame(codec::CLOUD_BLOB, self.root.seq + 1, offset, &payload)?;
            self.buffer_metadata(offset, b)?;
            next = MetaRef {
                offset,
                hash: hash(&b),
            };
        }
        self.chain.push(next);
        self.root.free_events = if checkpoint {
            0
        } else {
            self.root.free_events
                + self.freelog.add.pages.len() as u64
                + self.freelog.remove_pages.len() as u64
                + self.freelog.add.extents.len() as u64
                + self.freelog.remove_extents.len() as u64
                + 1
        };
        self.root.free = next;
        self.root.seq += 1;
        self.root.revision += 1;
        self.flush_metadata()?;
        self.store.device.sync()?;
        #[cfg(test)]
        crash_point("before_root");
        let data = serde_json::to_vec(&self.root)?;
        let first = 1 + self.root.seq % 2;
        for slot in [first, 3 - first] {
            let offset = slot * PAGE as u64;
            let b = self
                .store
                .crypto
                .frame(codec::ROOT, self.root.seq, offset, &data)?;
            self.store.device.write(offset, &b)?;
            self.store.device.sync()?;
            #[cfg(test)]
            if slot == first {
                crash_point("after_first_root");
            }
        }
        self.store.root = self.root.clone();
        self.store.free = std::mem::take(&mut self.free);
        self.store.free_chain = std::mem::take(&mut self.chain);
        let mut registry = self.store.readers.lock().map_err(|_| Error::Poisoned)?;
        registry.root = self.root.clone();
        for (k, v) in &self.store.diagnostics {
            registry.metrics.insert(k.clone(), *v);
        }
        Ok(())
    }
}
impl Storage for Txn<'_> {
    fn read_node(&mut self, r: MetaRef) -> Result<Vec<u8>> {
        let g = self.geometry();
        if let Some(v) = self.store.prepare_cache.node(r, self.root.seq + 1) {
            return Ok(v);
        }
        let mut b = [0; PAGE];
        if r.offset & PORTABLE != 0 {
            let p = r.offset & !PORTABLE;
            let o = self.object(p / g.object_size)?;
            super::portable_validation::validate_node_reference(r, &o, g)?;
            if o.missing { return Err(Error::Missing(super::RemoteObject::from_object(&o, g.object_size))); }
            self.read_frame(o.extent * g.object_size + p % g.object_size, &mut b)?;
        } else {
            self.read_frame(r.offset, &mut b)?;
        }
        if hash(&b) != r.hash {
            return Err(Error::Integrity("index node digest".into()));
        }
        let v = self.store.crypto.unframe(codec::NODE, r.offset, &b)?;
        self.store.prepare_cache.put_node(r, self.root.seq + 1, v.clone());
        Ok(v)
    }
    fn read_decoded(&mut self, spec: &Spec, r: MetaRef) -> Result<Node> {
        if let Some(node) = self.store.prepare_cache.decoded(r, spec, self.root.seq + 1) {
            return Ok(node);
        }
        let payload = self.read_node(r)?;
        let node = tree::decode_at(spec, r, &payload)?;
        self.store.prepare_cache.put_decoded(r, *spec, self.root.seq + 1, payload, node.clone());
        Ok(node)
    }
    fn prefetch_nodes(&mut self, references: &[MetaRef]) -> Result<()> {
        let g = self.geometry();
        let references = unique_cache_misses(&self.store.prepare_cache, references, self.root.seq + 1);
        let ids = references.iter().filter(|r| r.offset & PORTABLE != 0)
            .map(|r| (r.offset & !PORTABLE) / g.object_size).collect::<BTreeSet<_>>();
        let objects = self.objects_many(&ids.into_iter().collect::<Vec<_>>())?;
        let mut positions = Vec::with_capacity(references.len());
        for reference in references {
            let physical = node_position(reference, &objects, g)?;
            if !self.pending_metadata.contains_key(&physical) && !self.pending_data.contains_key(&physical) {
                positions.push((physical, reference));
            }
        }
        prefetch_frames(&mut self.store.prepare_cache, &self.store.device, &self.store.crypto,
            self.root.seq + 1, positions)
    }
    fn write_node(&mut self, payload: &[u8]) -> Result<MetaRef> {
        if self.portable.is_some() {
            return self.write_portable(payload);
        }
        let offset = self.alloc_page()?;
        let b = self
            .store
            .crypto
            .frame(codec::NODE, self.root.seq + 1, offset, payload)?;
        let reference = MetaRef {
            offset,
            hash: hash(&b),
        };
        self.cache_fresh_node(reference, payload);
        self.buffer_metadata(offset, b)?;
        Ok(reference)
    }
    fn created(
        &mut self,
        r: MetaRef,
        tag: u8,
        children: &[MetaRef],
        values: &[(u64, Vec<u8>)],
    ) -> Result<()> {
        let g = self.geometry();
        if r.offset & PORTABLE != 0 {
            let oid = (r.offset & !PORTABLE) / g.object_size;
            *self.cloud_deltas.entry(oid).or_default() += 1;
            if tag == PORTMAP.tag {
                for (_, v) in values {
                    *self
                        .cloud_deltas
                        .entry(Page::decode(v)?.reference.object)
                        .or_default() += 1;
                }
            }
            return Ok(());
        }
        if ![1, 2, 3, 5, 6, 16].contains(&tag) {
            return Ok(());
        }
        for c in children {
            self.refdelta(*c, 1, tag)
        }
        if tag == PAGES.tag {
            let mut counts = BTreeMap::<u64, u64>::new();
            for (_, v) in values {
                let p = Page::decode(v)?;
                *counts.entry(p.reference.object).or_default() += 1;
            }
            let mut objects = self.objects_many(&counts.keys().copied().collect::<Vec<_>>())?;
            for (oid, count) in counts {
                let mut o = objects.remove(&oid).unwrap();
                o.refs = o.refs.checked_add(count)
                    .ok_or_else(|| Error::Integrity("data reference overflow".into()))?;
                self.objects.insert(o.oid, o);
            }
        }
        Ok(())
    }
    fn retired(&mut self, r: MetaRef) -> Result<()> {
        let g = self.geometry();
        if r.offset & PORTABLE != 0 {
            let oid = (r.offset & !PORTABLE) / g.object_size;
            *self.cloud_deltas.entry(oid).or_default() -= 1;
            let data = self.read_node(r)?;
            if let Node::Leaf { values, .. } = tree::decode(&PORTMAP, &data)? {
                for (_, v) in values {
                    *self
                        .cloud_deltas
                        .entry(Page::decode(&v)?.reference.object)
                        .or_default() -= 1;
                }
            }
        } else {
            self.retire_page(r.offset);
        }
        Ok(())
    }
}
pub(super) fn read_blob(device: &Device, crypto: &Crypto, r: MetaRef) -> Result<Vec<u8>> {
    read_blob_frames(crypto, r, |offset, frame| device.read(offset, frame))
}
fn read_blob_frames(crypto: &Crypto, mut r: MetaRef,
    mut read: impl FnMut(u64, &mut [u8; PAGE]) -> Result<()>) -> Result<Vec<u8>> {
    let mut out = Vec::new();
    let mut visited = BTreeSet::new();
    while !r.empty() {
        if !visited.insert(r.offset) {
            return Err(Error::Integrity("blob cycle".into()));
        }
        let mut b = [0; PAGE];
        read(r.offset, &mut b)?;
        if hash(&b) != r.hash {
            return Err(Error::Integrity("blob checksum".into()));
        }
        let data = crypto.unframe(codec::CLOUD_BLOB, r.offset, &b)?;
        if data.len() < 40 {
            return Err(Error::Integrity("blob length".into()));
        }
        r = MetaRef::get(&data);
        out.extend_from_slice(&data[40..]);
    }
    Ok(out)
}
pub(super) fn decode_header(crypto: &Crypto, oid: u64, raw: &[u8]) -> Result<serde_json::Value> {
    let g = crypto.geometry;
    if raw.len() != g.object_size as usize {
        return Err(Error::Invalid("object length does not match volume geometry".into()));
    }
    let header = super::object_bytes::header(crypto, oid, raw[..PAGE].try_into().unwrap())?;
    let end = super::object_bytes::header_end(g, &header)?;
    if raw[end..].iter().any(|byte| *byte != 0)
        || header["body_sha256"].as_str() != Some(&hex(&hash(&raw[PAGE..]))) {
        return Err(Error::Integrity("object body authentication or padding".into()));
    }
    Ok(header)
}

impl Txn<'_> {
    pub fn write_portable(&mut self, payload: &[u8]) -> Result<MetaRef> {
        let g = self.geometry();
        let node = tree::decode(&PORTMAP, payload)?;
        let dependencies: BTreeSet<u64> = match &node {
            Node::Branch { children, .. } => children.iter()
                .map(|(_, r)| (r.offset & !PORTABLE) / g.object_size).collect(),
            Node::Leaf { values, .. } => values.iter()
                .map(|(_, v)| Page::decode(v).map(|p| p.reference.object))
                .collect::<Result<_>>()?,
        };
        let mut o = match self.portable {
            Some(0) | None => {
                let mut o = self.allocate_object(2, 0)?;
                o.used = 1;
                self.objects.insert(o.oid, o.clone());
                self.portable = Some(o.oid);
                o
            }
            Some(oid) => self.object(oid)?,
        };
        let mut table = self.dependencies(o.oid, o.external_count as usize)?;
        let extra = dependencies.iter().filter(|id| **id != o.oid && !table.positions.contains_key(id)).count();
        if o.used as u64 >= g.slots || table.rows.len() + extra > g.external_limit as usize {
            self.store.prepare_cache.put_dependencies(o.oid, self.root.seq + 1, table);
            self.seal(o.oid, None)?;
            o = self.allocate_object(2, 0)?;
            o.used = 1;
            self.objects.insert(o.oid, o.clone());
            self.portable = Some(o.oid);
            table = Dependencies::default();
        }
        let missing = dependencies.into_iter().filter(|id| *id != o.oid && !table.positions.contains_key(id)).collect::<Vec<_>>();
        for (id, child) in self.objects_many(&missing)? {
            if !child.sealed {
                return Err(Error::Integrity("portable dependency must already be sealed".into()));
            }
            let mut row = [0; 48];
            row[..16].copy_from_slice(child.id.as_bytes());
            row[16..48].copy_from_slice(&child.sha);
            self.pending_externals.insert(o.oid * g.external_stride + table.rows.len() as u64, Some(row.to_vec()));
            debug_assert!(!table.positions.contains_key(&id));
            table.push(row)?;
        }
        o.external_count = table.rows.len() as u16;
        self.store.prepare_cache.put_dependencies(o.oid, self.root.seq + 1, table);
        let local = (g.payload_pages + o.used as u64) * PAGE as u64;
        let offset = PORTABLE | (o.oid * g.object_size + local);
        let bytes = self.store.crypto.frame(codec::NODE, self.root.seq + 1, offset, payload)?;
        self.pending_data.insert(o.extent * g.object_size + local, bytes);
        self.store.prepare_cache.put_cipher(o.oid, o.extent, o.used as u64, self.root.seq + 1, bytes);
        o.used += 1;
        self.objects.insert(o.oid, o);
        let reference = MetaRef { offset, hash: hash(&bytes) };
        self.cache_fresh_node(reference, payload);
        Ok(reference)
    }
    pub fn apply_cloud_deltas(&mut self, root: MetaRef) -> Result<MetaRef> {
        let mut changes = Vec::new();
        let save = self.portable.take();
        let deltas = std::mem::take(&mut self.cloud_deltas).into_iter().filter(|(_, delta)| *delta != 0).collect::<Vec<_>>();
        let ids = deltas.iter().map(|(oid, _)| *oid).collect::<Vec<_>>();
        let prior = tree::get_many(self, &COUNTS, root, &ids)?;
        let mut objects = self.objects_many(&ids)?;
        for ((oid, delta), before) in deltas.into_iter().zip(prior) {
            let before = before.map(|b| u64::from_le_bytes(b.try_into().unwrap())).unwrap_or(0);
            let after = before.checked_add_signed(delta)
                .ok_or_else(|| Error::Integrity("cloud reference underflow".into()))?;
            let mut object = objects.remove(&oid).unwrap();
            object.cloud_refs = after;
            self.objects.insert(oid, object);
            changes.push((oid, if after == 0 { None } else { Some(after.to_le_bytes().to_vec()) }));
        }
        let next = self.set(&COUNTS, root, &changes)?;
        self.portable = save;
        Ok(next)
    }
    pub fn move_object(&mut self, oid: u64) -> Result<bool> {
        let g = self.geometry();
        let mut o = self.object(oid)?;
        if o.missing || !o.sealed {
            return Ok(false);
        }
        let min = self
            .store
            .readers
            .lock()
            .map_err(|_| Error::Poisoned)?
            .active
            .keys()
            .next()
            .copied()
            .unwrap_or(u64::MAX);
        let target = self
            .free
            .extents
            .iter()
            .find(|(extent, seq)| {
                **extent < o.extent && **seq < min && **seq <= self.store.root.seq
            })
            .map(|(e, _)| *e);
        let Some(target) = target else {
            return Ok(false);
        };
        let raw = super::object_bytes::read_verified(&self.store.device, &self.store.crypto, &o)?;
        let end = super::object_bytes::payload_end(g, o.kind as u64, o.used as u64)?;
        self.store.device.write(target * g.object_size, &raw[..end])?;
        self.free.extents.remove(&target);
        self.freelog.remove_extents.insert(target);
        self.free.punched.remove(&target);
        self.freelog.remove_punched.insert(target);
        self.free.extents.insert(o.extent, self.root.seq + 1);
        self.freelog.add.extents.insert(o.extent, self.root.seq + 1);
        self.free.punched.remove(&o.extent);
        self.freelog.remove_punched.insert(o.extent);
        self.pending_blocks.push((o.extent, u64::MAX));
        self.pending_blocks.push((target, oid));
        o.extent = target;
        self.objects.insert(oid, o);
        Ok(true)
    }
}
impl Store {
    pub fn reclaim_tail(&mut self) -> Result<u64> {
        let g = self.geometry();
        self.check()?;
        let min = self
            .readers
            .lock()
            .map_err(|_| Error::Poisoned)?
            .active
            .keys()
            .next()
            .copied()
            .unwrap_or(u64::MAX);
        let mut end = self.root.next_extent;
        while end > 1
            && self
                .free
                .extents
                .get(&(end - 1))
                .is_some_and(|seq| *seq < min && *seq <= self.root.seq)
        {
            end -= 1;
        }
        if end == self.root.next_extent {
            return Ok(0);
        }
        let before = self.device.len()?;
        self.transaction(|tx| {
            for extent in end..tx.root.next_extent {
                tx.free.extents.remove(&extent);
                tx.freelog.remove_extents.insert(extent);
                tx.free.punched.remove(&extent);
                tx.freelog.remove_punched.insert(extent);
                let r = tx.root.blocks;
                tx.root.blocks = tx.set(&BLOCKS, r, &[(extent, None)])?;
            }
            tx.root.next_extent = end;
            Ok(())
        })?;
        let desired = self.root.next_extent * g.object_size;
        if let Err(e) = self.device.resize(desired).and_then(|_| self.device.sync()) {
            self.failure = Some(e.to_string());
            return Err(e);
        }
        Ok(before.saturating_sub(desired))
    }
}
pub(super) fn public_config(raw: &[u8]) -> Result<Config> {
    let g = Geometry::new(raw.len() as u64)?;
    let start = g.header_bytes() - 1024;
    if raw.len() != g.object_size as usize || &raw[start..start + 8] != b"ODV4PUB1" {
        return Err(Error::Integrity("portable public configuration".into()));
    }
    let length = u32::from_le_bytes(raw[start + 8..start + 12].try_into().unwrap()) as usize;
    if length > 980 || hash(&raw[start..start + 992])[..] != raw[start + 992..start + 1024] {
        return Err(Error::Integrity("portable configuration checksum".into()));
    }
    let config: Config = serde_json::from_slice(&raw[start + 12..start + 12 + length])?;
    if config.object_size != g.object_size {
        return Err(Error::Integrity(
            "public configuration object size mismatch".into(),
        ));
    }
    Ok(config)
}
pub(super) fn parse_hex<const N: usize>(v: &str) -> Result<[u8; N]> {
    if v.len() != N * 2 || !v.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(Error::Invalid("invalid hexadecimal string".into()));
    }
    let mut out = [0; N];
    for (i, p) in v.as_bytes().chunks_exact(2).enumerate() {
        let digit = |b: u8| {
            if b <= b'9' {
                b - b'0'
            } else {
                b.to_ascii_lowercase() - b'a' + 10
            }
        };
        out[i] = digit(p[0]) * 16 + digit(p[1]);
    }
    Ok(out)
}
pub(super) fn external_table(
    crypto: &Crypto,
    oid: u64,
    raw: &[u8],
) -> Result<BTreeMap<u64, (Uuid, [u8; 32])>> {
    let g = crypto.geometry;
    let h = decode_header(crypto, oid, raw)?;
    let id = h["id"]
        .as_str()
        .ok_or_else(|| Error::Integrity("object id missing".into()))?;
    let n = h["external_count"]
        .as_u64()
        .ok_or_else(|| Error::Integrity("object external count missing".into()))?;
    if n > g.external_limit {
        return Err(Error::Integrity("too many external references".into()));
    }
    let nonce = parse_hex::<24>(h["table_nonce"].as_str().unwrap_or(""))?;
    let tag = parse_hex::<16>(h["table_tag"].as_str().unwrap_or(""))?;
    let mut table = raw[PAGE..g.header_bytes() - 1024].to_vec();
    crypto.open_blob(
        format!("OverlayDisk v4 object table {id}").as_bytes(),
        &nonce,
        &tag,
        &mut table,
    )?;
    let mut refs = BTreeMap::new();
    for i in 0..n as usize {
        let row = &table[i * 48..i * 48 + 48];
        let key = u64::from_le_bytes(row[8..16].try_into().unwrap());
        let uuid = Uuid::from_slice(&row[..16])
            .map_err(|_| Error::Integrity("external object id".into()))?;
        if refs
            .insert(key, (uuid, row[16..48].try_into().unwrap()))
            .is_some()
        {
            return Err(Error::Integrity("duplicate portable dependency".into()));
        }
    }
    super::portable_validation::validate_object(crypto, oid, raw, &h, &refs)?;
    Ok(refs)
}
impl Txn<'_> {
    pub fn ensure_external(&mut self, oid: u64, child: u64) -> Result<()> {
        let g = self.geometry();
        if oid == child { return Ok(()); }
        let mut o = self.object(oid)?;
        let mut table = self.dependencies(oid, o.external_count as usize)?;
        if table.positions.contains_key(&child) {
            self.store.prepare_cache.put_dependencies(oid, self.root.seq + 1, table);
            return Ok(());
        }
        if o.external_count as u64 >= g.external_limit {
            return Err(Error::Invalid("metadata dependency table is full".into()));
        }
        let dependency = self.object(child)?;
        if !dependency.sealed { return Err(Error::Integrity("unsealed root dependency".into())); }
        let mut value = [0; 48];
        value[..16].copy_from_slice(dependency.id.as_bytes());
        value[16..].copy_from_slice(&dependency.sha);
        self.pending_externals.insert(oid * g.external_stride + o.external_count as u64, Some(value.to_vec()));
        table.push(value)?;
        self.store.prepare_cache.put_dependencies(oid, self.root.seq + 1, table);
        o.external_count += 1;
        self.objects.insert(oid, o);
        Ok(())
    }
}
impl Txn<'_> {
    pub fn adopt_portable_node(
        &mut self,
        reference: MetaRef,
        node: &Node,
        rows: &mut BTreeMap<u64, Option<Vec<u8>>>,
    ) -> Result<()> {
        let g = self.geometry();
        let key = (reference.offset & !PORTABLE) / PAGE as u64;
        if rows.contains_key(&key)
            || tree::get(self, &PORTREFS, self.root.portable_refs, key)?.is_some()
        {
            return Err(Error::Integrity("duplicate portable base node".into()));
        }
        let mut value = vec![0; 16];
        value[..8].copy_from_slice(&1u64.to_le_bytes());
        value[8] = PAGES.tag;
        rows.insert(key, Some(value));
        let oid = (reference.offset & !PORTABLE) / g.object_size;
        let mut object = self.object(oid)?;
        object.refs = object
            .refs
            .checked_add(1)
            .ok_or_else(|| Error::Integrity("base node count overflow".into()))?;
        self.objects.insert(oid, object);
        *self.cloud_deltas.entry(oid).or_default() += 1;
        if let Node::Leaf { values, .. } = node {
            for (_, bytes) in values {
                let page = Page::decode(bytes)?;
                let mut object = self.object(page.reference.object)?;
                object.refs = object
                    .refs
                    .checked_add(1)
                    .ok_or_else(|| Error::Integrity("base page count overflow".into()))?;
                object.current_refs = object
                    .current_refs
                    .checked_add(1)
                    .ok_or_else(|| Error::Integrity("base page count overflow".into()))?;
                self.objects.insert(object.oid, object);
                *self.cloud_deltas.entry(page.reference.object).or_default() += 1;
            }
        }
        Ok(())
    }
    pub fn remote_object(&mut self, id: Uuid, sha: [u8; 32], used: u16) -> Result<Object> {
        let g = self.geometry();
        let oid = u64::from_le_bytes(id.as_bytes()[8..].try_into().unwrap());
        if oid == 0 || oid >= PORTABLE / g.object_size || used == 0 || used as u64 > g.slots {
            return Err(Error::Integrity("remote object range".into()));
        }
        let existing = if let Some(o) = self.objects.get(&oid) {
            Some(o.clone())
        } else {
            tree::get(self, &OBJECTS, self.root.objects, oid)?
                .map(|v| Object::decode(oid, &v))
                .transpose()?
        };
        if let Some(mut o) = existing {
            if o.id != id
                || o.sha != sha
                || o.kind != 1
                || !o.sealed
                || (!o.missing && used > o.used)
            {
                return Err(Error::Integrity("remote object collision".into()));
            }
            o.used = o.used.max(used);
            self.objects.insert(oid, o.clone());
            return Ok(o);
        }
        let o = Object {
            oid,
            id,
            extent: 0,
            kind: 1,
            pool: 0,
            used,
            sealed: true,
            missing: true,
            remote: true,
            remote_source: 1,
            cache_backed: false,
            origin_backed: true,
            sync_state: 0,
            refs: 0,
            current_refs: 0,
            cloud_refs: 0,
            sha,
            external_count: 0,
        };
        self.root.next_oid = self.root.next_oid.max(oid + 1);
        self.objects.insert(oid, o.clone());
        Ok(o)
    }
    pub fn hydrate_object(
        &mut self,
        oid: u64,
        raw: &[u8],
        header: &serde_json::Value,
    ) -> Result<()> {
        let g = self.geometry();
        let mut o = self.object(oid)?;
        if !o.missing {
            return Ok(());
        }
        let extent = self.root.next_extent;
        self.root.next_extent += 1;
        self.store.device.grow((extent + 1) * g.object_size)?;
        super::object_bytes::write_verified_prefix(&self.store.device, g, extent, raw, header)?;
        o.extent = extent;
        o.missing = false;
        o.kind = header["kind"].as_u64().unwrap() as u8;
        o.external_count = header["external_count"].as_u64().unwrap_or(0) as u16;
        o.used = header["used"].as_u64().unwrap() as u16;
        self.objects.insert(oid, o);
        self.pending_blocks.push((extent, oid));
        Ok(())
    }
    pub fn import_object(&mut self, id: Uuid, expected: [u8; 32], raw: &[u8]) -> Result<Object> {
        let g = self.geometry();
        let oid = u64::from_le_bytes(id.as_bytes()[8..].try_into().unwrap());
        if oid == 0 || oid >= PORTABLE / g.object_size || hash(raw) != expected {
            return Err(Error::Integrity("import object identity or digest".into()));
        }
        let h = decode_header(&self.store.crypto, oid, raw)?;
        if h["id"].as_str() != Some(&id.to_string()) {
            return Err(Error::Integrity("import object id authentication".into()));
        }
        let existing = self.root.objects;
        if let Some(v) = tree::get(self, &OBJECTS, existing, oid)? {
            let o = Object::decode(oid, &v)?;
            if o.id == id && o.sha == expected {
                if o.missing { self.hydrate_object(oid,raw,&h)?; return self.object(oid); }
                return Ok(o);
            }
            return Err(Error::Integrity(
                "import object identifier collision".into(),
            ));
        }
        let kind = h["kind"]
            .as_u64()
            .filter(|v| *v == 1 || *v == 2)
            .ok_or_else(|| Error::Integrity("import kind".into()))? as u8;
        let used = h["used"]
            .as_u64()
            .filter(|v| *v <= g.slots)
            .ok_or_else(|| Error::Integrity("import slots".into()))? as u16;
        let extent = self.root.next_extent;
        self.root.next_extent += 1;
        self.root.next_oid = self.root.next_oid.max(oid + 1);
        self.store.device.grow((extent + 1) * g.object_size)?;
        super::object_bytes::write_verified_prefix(&self.store.device, g, extent, raw, &h)?;
        let o = Object {
            oid,
            id,
            extent,
            kind,
            pool: 0,
            used,
            sealed: true,
            missing: false,
            remote: false,
            remote_source: 0,
            cache_backed: false,
            origin_backed: false,
            sync_state: 0,
            refs: 0,
            current_refs: 0,
            cloud_refs: 0,
            sha: expected,
            external_count: h["external_count"]
                .as_u64()
                .ok_or_else(|| Error::Integrity("object external count missing".into()))?
                as u16,
        };
        self.objects.insert(oid, o.clone());
        self.pending_blocks.push((extent, oid));
        Ok(o)
    }
}
impl Store {
    pub fn truncate_free_tail(&mut self) -> Result<u64> {
        self.reclaim_tail()
    }
}
impl Txn<'_> {
    fn retire_page(&mut self, offset: u64) {
        if self.pending_metadata.remove(&offset).is_some() {
            self.store.prepare_cache.count("prepare_skipped_metadata_write_pages", 1);
        }
        self.retired.insert(offset);
    }
    fn buffer_metadata(&mut self, offset: u64, frame: [u8; PAGE]) -> Result<()> {
        self.pending_metadata.insert(offset, frame);
        if self.pending_metadata.len() >= 256 { self.flush_metadata()?; }
        Ok(())
    }
    fn cache_fresh_node(&mut self, r: MetaRef, payload: &[u8]) {
        self.store.prepare_cache.put_node(r, self.root.seq + 1, payload.to_vec());
        self.fresh_nodes.insert(r.offset, r);
    }
    fn read_frame(&self, offset: u64, out: &mut [u8; PAGE]) -> Result<()> {
        if let Some(bytes) = self
            .pending_metadata
            .get(&offset)
            .or_else(|| self.pending_data.get(&offset))
        {
            *out = *bytes;
            Ok(())
        } else {
            self.store.device.read(offset, out)
        }
    }
    fn flush_metadata(&mut self) -> Result<()> {
        let mut start = 0;
        let mut bytes = Vec::new();
        let pages = std::mem::take(&mut self.pending_metadata);
        for (offset, page) in pages {
            if !bytes.is_empty()
                && (offset != start + bytes.len() as u64 || bytes.len() >= 1024 * 1024)
            {
                self.store.device.write(start, &bytes)?;
                *self
                    .store
                    .diagnostics
                    .entry("metadata_write_batches".into())
                    .or_default() += 1;
                bytes.clear();
            }
            if bytes.is_empty() {
                start = offset;
            }
            bytes.extend_from_slice(&page);
            *self
                .store
                .diagnostics
                .entry("metadata_write_pages".into())
                .or_default() += 1;
        }
        if !bytes.is_empty() {
            self.store.device.write(start, &bytes)?;
            *self
                .store
                .diagnostics
                .entry("metadata_write_batches".into())
                .or_default() += 1;
        }
        Ok(())
    }
    fn flush_data(&mut self) -> Result<()> {
        let mut start = 0;
        let mut bytes = Vec::new();
        for (offset, page) in std::mem::take(&mut self.pending_data) {
            if !bytes.is_empty()
                && (offset != start + bytes.len() as u64 || bytes.len() >= 1024 * 1024)
            {
                self.store.device.write(start, &bytes)?;
                bytes.clear();
            }
            if bytes.is_empty() {
                start = offset;
            }
            bytes.extend_from_slice(&page);
        }
        if !bytes.is_empty() {
            self.store.device.write(start, &bytes)?;
        }
        Ok(())
    }
}
fn load_free(
    device: &Device,
    crypto: &Crypto,
    mut reference: MetaRef,
) -> Result<(Free, Vec<MetaRef>)> {
    let mut logs = Vec::new();
    let mut chain = Vec::new();
    let mut seen = BTreeSet::new();
    while !reference.empty() {
        if !seen.insert(reference.offset) {
            return Err(Error::Integrity("free space log cycle".into()));
        }
        let log: FreeLog = serde_json::from_slice(&read_blob(device, crypto, reference)?)?;
        chain.push(reference);
        reference = log.previous;
        let reset = log.reset;
        logs.push(log);
        if reset {
            break;
        }
    }
    let mut free = Free::default();
    for log in logs.into_iter().rev() {
        if log.reset {
            free = Free::default()
        }
        for p in log.remove_pages {
            free.pages.remove(&p);
        }
        for e in log.remove_extents {
            free.extents.remove(&e);
        }
        for e in log.remove_punched {
            free.punched.remove(&e);
        }
        free.pages.extend(log.add.pages);
        free.extents.extend(log.add.extents);
        free.punched.extend(log.add.punched);
    }
    chain.reverse();
    Ok((free, chain))
}
impl Store {
    pub fn decommit_free_before(&mut self, limit: usize, cutoff: u64) -> Result<u64> {
        let g = self.geometry();
        self.check()?;
        let min = self
            .readers
            .lock()
            .map_err(|_| Error::Poisoned)?
            .active
            .keys()
            .next()
            .copied()
            .unwrap_or(u64::MAX);
        let extents = self
            .free
            .extents
            .iter()
            .filter(|(extent, seq)| {
                **seq <= self.root.seq
                    && **seq <= cutoff
                    && **seq < min
                    && !self.free.punched.contains(extent)
            })
            .take(limit.clamp(1, 32))
            .map(|(extent, _)| *extent)
            .collect::<Vec<_>>();
        if extents.is_empty() {
            return Ok(0);
        }
        let result: Result<u64> = (|| {
            let mut freed = 0;
            for extent in &extents {
                freed += self
                    .device
                    .deallocate(extent * g.object_size, g.object_size)?;
            }
            self.transaction(|tx| {
                for extent in &extents {
                    tx.free.punched.insert(*extent);
                    tx.freelog.add.punched.insert(*extent);
                }
                Ok(())
            })?;
            Ok(freed)
        })();
        if let Err(error) = &result {
            self.failure = Some(error.to_string());
            if let Ok(mut registry) = self.readers.lock() {
                registry.failure = Some(error.to_string());
            }
        }
        result
    }
}
impl Store {
    #[cfg(test)]
    pub fn has_decommit_pending(&self) -> Result<bool> {
        self.has_decommit_pending_before(u64::MAX)
    }
    pub fn has_decommit_pending_before(&self, cutoff: u64) -> Result<bool> {
        Ok(self
            .free
            .extents
            .iter()
            .any(|(extent, seq)| *seq <= cutoff && !self.free.punched.contains(extent)))
    }
}
#[cfg(test)]
pub(super) fn crash_point(stage: &str) {
    if std::env::var("OVERLAYDISK_V4_CRASH_STAGE").ok().as_deref() == Some(stage) {
        if let Ok(path) = std::env::var("OVERLAYDISK_V4_CRASH_READY") {
            std::fs::write(path, stage).unwrap();
            loop {
                std::thread::park_timeout(std::time::Duration::from_secs(60));
            }
        }
    }
}
#[cfg(test)]
impl Txn<'_> {
    pub fn seal_tails(&mut self) -> Result<()> {
        for i in 0..self.root.tails.len() {
            if self.root.tails[i] != 0 {
                self.seal(self.root.tails[i], None)?;
                self.root.tails[i] = 0;
            }
        }
        Ok(())
    }
}

impl Txn<'_> {
    fn dependencies(&mut self, oid: u64, count: usize) -> Result<Dependencies> {
        if let Some(table) = self.store.prepare_cache.dependencies(oid, count, self.root.seq + 1) {
            return Ok(table);
        }
        let g = self.geometry();
        let missing = (0..count).map(|slot| oid * g.external_stride + slot as u64)
            .filter(|key| !self.pending_externals.contains_key(key)).collect::<Vec<_>>();
        if !missing.is_empty() { self.store.prepare_cache.count("prepare_dependency_loads", 1); }
        let prior = tree::get_many(self, &EXTERNALS, self.root.externals, &missing)?;
        let mut prior = missing.into_iter().zip(prior).collect::<BTreeMap<_, _>>();
        let mut table = Dependencies::default();
        for slot in 0..count {
            let key = oid * g.external_stride + slot as u64;
            let value = if let Some(v) = self.pending_externals.get(&key) { v.clone() }
                else { prior.remove(&key).flatten() };
            let value = value.ok_or_else(|| Error::Integrity("metadata external reference missing".into()))?;
            table.push(value.as_slice().try_into().map_err(|_| Error::Integrity("metadata external reference length".into()))?)?;
        }
        Ok(table)
    }
}

#[cfg(test)]
mod identity_tests {
    use super::*;
    #[test]
    fn consecutive_object_ids_distribute_prefixes_and_survive_reopen() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("object-identities.odv4");
        let (config, crypto) = Config::create(64 << 20, None).unwrap();
        let mut store =
            Store::create(Device::open(&path, true).unwrap(), config.clone(), crypto).unwrap();
        let identities = store
            .transaction(|tx| {
                (0..256)
                    .map(|_| tx.allocate_object(1, 0).map(|object| object.id))
                    .collect::<Result<Vec<_>>>()
            })
            .unwrap();
        let prefixes = identities
            .iter()
            .map(|id| id.as_bytes()[0])
            .collect::<BTreeSet<_>>();
        assert!(
            prefixes.len() >= 64,
            "one volume must use many cloud directory prefixes"
        );
        assert_eq!(identities.iter().collect::<BTreeSet<_>>().len(), 256);
        for (index, id) in identities.iter().enumerate() {
            assert_eq!(
                u64::from_le_bytes(id.as_bytes()[8..].try_into().unwrap()),
                index as u64 + 1
            );
        }
        drop(store);
        let mut reopened = Store::open(
            Device::open(&path, false).unwrap(),
            config.clone(),
            config.unlock(None).unwrap(),
        )
        .unwrap();
        for (index, identity) in identities.iter().enumerate() {
            assert_eq!(reopened.object(index as u64 + 1).unwrap().id, *identity);
        }
    }
}

impl Store {
    pub fn promote_cache_capability(&mut self) -> Result<()> {
        self.check()?;
        if self.config.cache_capable && self.root.cache_capable {
            return Ok(());
        }
        let result: Result<()> = (|| {
            let mut config = self.config.clone();
            config.cache_capable = true;
            let bytes = config.encode()?;
            // Both old-writer gates must be durable before publishing any missing own-cache object.
            for offset in [0, 3 * PAGE as u64] {
                self.device.write(offset, &bytes)?;
                self.device.sync()?;
                #[cfg(test)]
                if offset == 0 {
                    crash_point("cache_first_config");
                }
            }
            #[cfg(test)]
            crash_point("cache_both_configs");
            self.config = config;
            self.transaction(|tx| {
                tx.root.cache_capable = true;
                Ok(())
            })
        })();
        if let Err(e) = &result {
            self.failure = Some(e.to_string());
            if let Ok(mut r) = self.readers.lock() {
                r.failure = Some(e.to_string());
            }
        }
        result
    }
    pub fn decommit_cache_extents(
        &mut self,
        pending: &[(u64, u64)],
    ) -> Result<(u64, Vec<(u64, u64)>)> {
        let g = self.geometry();
        self.check()?;
        let min = self
            .readers
            .lock()
            .map_err(|_| Error::Poisoned)?
            .active
            .keys()
            .next()
            .copied()
            .unwrap_or(u64::MAX);
        let mut freed = 0;
        let mut remaining = Vec::new();
        let mut punched = Vec::new();
        for &(extent, seq) in pending {
            if self.free.extents.get(&extent) != Some(&seq) {
                continue;
            }
            if self.free.punched.contains(&extent) {
                continue;
            }
            if seq >= min || seq > self.root.seq {
                remaining.push((extent, seq));
                continue;
            }
            freed += self
                .device
                .deallocate(extent * g.object_size, g.object_size)?;
            punched.push(extent);
        }
        if !punched.is_empty() {
            self.transaction(|tx| {
                for e in &punched {
                    tx.free.punched.insert(*e);
                    tx.freelog.add.punched.insert(*e);
                }
                Ok(())
            })?;
        }
        Ok((freed, remaining))
    }
}
impl Txn<'_> {
    pub fn evict_object(&mut self, oid: u64, own: bool) -> Result<(u64, u64)> {
        let mut o = self.object(oid)?;
        if o.kind != 1 || !o.sealed || o.missing || !self.root.cache_capable {
            return Err(Error::Invalid("object cannot be evicted".into()));
        }
        let extent = o.extent;
        let seq = self.root.seq + 1;
        o.extent = 0;
        o.missing = true;
        o.remote = true;
        if own {
            o.remote_source = 2;
            o.cache_backed = true;
        } else {
            o.remote_source = 1;
            o.origin_backed = true;
        }
        self.objects.insert(oid, o);
        self.pending_blocks.push((extent, u64::MAX));
        self.free.extents.insert(extent, seq);
        self.freelog.add.extents.insert(extent, seq);
        self.free.punched.remove(&extent);
        self.freelog.remove_punched.insert(extent);
        Ok((extent, seq))
    }
}

impl Store {
    /// Retired private COW pages are already unreachable in both durable roots.
    /// Scan a bounded window and punch only whole 64 KiB groups proved free at the task cut.
    /// Cursor publication may lag the punch after a crash; repeating a free-range punch is safe.
    pub fn decommit_metadata_step(
        &mut self,
        cursor: u64,
        end: u64,
        cutoff: u64,
    ) -> Result<(u64, u64, bool)> {
        let g = self.geometry();
        self.check()?;
        const GROUP: u64 = 64 * 1024;
        let first = cursor.max(g.object_size);
        if first >= end {
            return Ok((0, end, true));
        }
        let pages = self
            .free
            .pages
            .range(first..end)
            .take(512)
            .map(|(p, _)| *p)
            .collect::<Vec<_>>();
        if pages.is_empty() {
            return Ok((0, end, true));
        }
        let min = self
            .readers
            .lock()
            .map_err(|_| Error::Poisoned)?
            .active
            .keys()
            .next()
            .copied()
            .unwrap_or(u64::MAX);
        let mut groups = pages
            .iter()
            .map(|p| p / GROUP * GROUP)
            .collect::<BTreeSet<_>>();
        groups.retain(|base| *base >= first && base + GROUP <= end);
        let mut freed = 0;
        let mut next = pages.last().unwrap() / GROUP * GROUP + GROUP;
        for base in groups {
            let retired = (0..16)
                .map(|n| self.free.pages.get(&(base + n * PAGE as u64)).copied())
                .collect::<Option<Vec<_>>>();
            let Some(retired) = retired else {
                continue;
            };
            if retired
                .iter()
                .any(|seq| *seq > cutoff || *seq > self.root.seq)
            {
                continue;
            }
            if retired.iter().any(|seq| *seq >= min) {
                next = next.min(base);
                break;
            }
            match self.device.deallocate(base, GROUP) {
                Ok(n) => freed += n,
                Err(e) => {
                    self.failure = Some(e.to_string());
                    if let Ok(mut r) = self.readers.lock() {
                        r.failure = Some(e.to_string());
                    }
                    return Err(e);
                }
            }
        }
        Ok((freed, next.min(end), next >= end))
    }
}

#[cfg(test)]
mod metadata_reclaim_tests {
    use super::*;
    #[test]
    fn free_private_metadata_is_punched_only_after_old_view_expires_and_reopens() {
        let d = tempfile::tempdir().unwrap();
        let path = d.path().join("metadata-punch.odv4");
        let (config, crypto) = Config::create(64 << 20, None).unwrap();
        let mut s =
            Store::create(Device::open(&path, true).unwrap(), config.clone(), crypto).unwrap();
        s.transaction(|tx| {
            let page = tx.append_page(0, &[93; PAGE], 0)?;
            tx.root.index = tx.set(&PAGES, tx.root.index, &[(0, Some(page.encode()))])?;
            tx.root.allocated_pages = 1;
            Ok(())
        })
        .unwrap();
        let lease = ReadLease::acquire(s.readers.clone()).unwrap();
        let (start, end) = s
            .transaction(|tx| {
                while !tx.root.meta_slot.is_multiple_of(16) {
                    tx.fresh_page()?;
                }
                let mut start = 0;
                let mut end = 0;
                for _ in 0..64 {
                    let offset = tx.fresh_page()?;
                    if start == 0 {
                        start = offset;
                    }
                    end = offset + PAGE as u64;
                    tx.store.device.write(offset, &[173; PAGE])?;
                    tx.retired.insert(offset);
                }
                Ok((start, end))
            })
            .unwrap();
        let cutoff = s.root.seq;
        let before = s.device.allocated_bytes().unwrap();
        let blocked = s.decommit_metadata_step(start, end, cutoff).unwrap();
        assert_eq!(blocked, (0, start, false));
        drop(lease);
        let (freed, cursor, done) = s.decommit_metadata_step(start, end, cutoff).unwrap();
        assert!(done);
        assert_eq!(cursor, end);
        assert!(freed > 0);
        assert!(s.device.allocated_bytes().unwrap() < before);
        let mut zeros = vec![1; (end - start) as usize];
        s.device.read(start, &mut zeros).unwrap();
        assert!(zeros.iter().all(|v| *v == 0));
        assert_eq!(s.read_page(s.root.index, 0).unwrap(), [93; PAGE]);
        // New free pages after the cut are deliberately outside this collection's proof.
        s.transaction(|tx| {
            let p = tx.fresh_page()?;
            tx.store.device.write(p, &[5; PAGE])?;
            tx.retired.insert(p);
            Ok(())
        })
        .unwrap();
        drop(s);
        let mut s = Store::open(
            Device::open(&path, false).unwrap(),
            config.clone(),
            config.unlock(None).unwrap(),
        )
        .unwrap();
        assert_eq!(s.read_page(s.root.index, 0).unwrap(), [93; PAGE]);
    }
}
