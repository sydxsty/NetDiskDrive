//! Persisted remote placeholders. Network callbacks never run while holding a storage/read lock.
use super::cloud::Cloud;
use super::store::{Object, PAGES};
use super::*;
use serde_json::{json, Value};

#[derive(Clone, Debug, Serialize)]
pub struct RemoteObject {
    pub id: String,
    pub sha256: String,
    pub length: u64,
}
impl RemoteObject {
    pub(super) fn from_object(o: &Object, object_size: u64) -> Self {
        Self {
            id: o.id.to_string(),
            sha256: codec::hex(&o.sha),
            length: object_size,
        }
    }
}
pub type ObjectProvider = dyn Fn(&RemoteObject, &mut [u8]) -> Result<()> + Send + Sync;
#[derive(Default)]
struct ProviderState {
    provider: Option<Arc<ObjectProvider>>,
    active: usize,
    replacing: bool,
}
#[derive(Default)]
pub(super) struct ProviderRegistry {
    state: Mutex<ProviderState>,
    changed: Condvar,
}
struct Invocation<'a>(&'a ProviderRegistry);
impl Drop for Invocation<'_> {
    fn drop(&mut self) {
        if let Ok(mut s) = self.0.state.lock() {
            s.active -= 1;
            self.0.changed.notify_all();
        }
    }
}
impl ProviderRegistry {
    pub(super) fn available(&self) -> bool {
        self.state
            .lock()
            .is_ok_and(|s| s.provider.is_some() && !s.replacing)
    }
    fn replace(&self, provider: Option<Arc<ObjectProvider>>) -> Result<()> {
        let mut s = self.state.lock().map_err(|_| Error::Poisoned)?;
        while s.replacing {
            s = self.changed.wait(s).map_err(|_| Error::Poisoned)?;
        }
        s.replacing = true;
        s.provider = None;
        while s.active != 0 {
            s = self.changed.wait(s).map_err(|_| Error::Poisoned)?;
        }
        s.provider = provider;
        s.replacing = false;
        self.changed.notify_all();
        Ok(())
    }
    fn fetch(&self, object: &RemoteObject, bytes: &mut [u8]) -> Result<()> {
        let provider = {
            let mut s = self.state.lock().map_err(|_| Error::Poisoned)?;
            let p = s
                .provider
                .clone()
                .ok_or_else(|| Error::Missing(object.clone()))?;
            s.active += 1;
            p
        };
        let _invocation = Invocation(self);
        provider(object, bytes)
    }
}
impl volume::Shared {
    pub(super) fn hydrate_missing(&self, object: &RemoteObject) -> Result<()> {
        let g = self.geometry();
        // A second in-flight read may already have installed this immutable object.
        let id =
            Uuid::parse_str(&object.id).map_err(|_| Error::Invalid("remote object ID".into()))?;
        let ordinal = u64::from_le_bytes(id.as_bytes()[8..].try_into().unwrap());
        {
            let lease = store::ReadLease::acquire(self.readers.clone())?;
            let mut reader = self.reader(&lease.root);
            let o = reader.object(ordinal)?;
            if o.id != id || codec::hex(&o.sha) != object.sha256 {
                return Err(Error::Integrity("remote object identity changed".into()));
            }
            if !o.missing {
                return Ok(());
            }
        }
        let mut bytes = vec![0; g.object_size as usize];
        if let Err(e) = self.provider.fetch(object, &mut bytes) {
            self.cache_runtime.online.store(false, Ordering::Release);
            return Err(e);
        }
        import(self, &object.id, &bytes)
    }
}
fn import(shared: &volume::Shared, object: &str, bytes: &[u8]) -> Result<()> {
    let g = shared.crypto.geometry;
    if bytes.len() != g.object_size as usize {
        return Err(Error::Invalid("lazy object length differs from disk geometry".into()));
    }
    let id = Uuid::parse_str(object).map_err(|_| Error::Invalid("lazy object ID".into()))?;
    let ordinal = u64::from_le_bytes(id.as_bytes()[8..].try_into().unwrap());
    // Validate untrusted network bytes outside the mutating transaction. A retryable bad response
    // must not latch persistence failure and disable unrelated resident I/O.
    let expected = {
        let lease = store::ReadLease::acquire(shared.readers.clone())?;
        let mut reader = shared.reader(&lease.root);
        let o = reader.object(ordinal)?;
        if o.id != id || !o.remote {
            return Err(Error::Invalid("object is not a lazy source object".into()));
        }
        o
    };
    if codec::hash(bytes) != expected.sha {
        return Err(Error::Integrity("lazy object SHA-256".into()));
    }
    let header = store::decode_header(&shared.crypto, ordinal, bytes)?;
    if header["id"] != object
        || !matches!(header["kind"].as_u64(),Some(1|2))
        || (expected.kind != 0 && header["kind"].as_u64() != Some(expected.kind as u64))
        || header["used"]
            .as_u64()
            .is_none_or(|n| n < expected.used as u64 || n > g.slots)
    {
        return Err(Error::Integrity(
            "lazy object authenticated descriptor".into(),
        ));
    }
    let mut store = shared.store.lock().map_err(|_| Error::Poisoned)?;
    store.check()?;
    let current = store.object(ordinal)?;
    if current.id != id || current.sha != expected.sha {
        return Err(Error::Integrity(
            "lazy identity changed during download".into(),
        ));
    }
    if !current.missing {
        return Ok(());
    }
    let deferred=store.root.deferred_index;
    // Validate dependency records before beginning a persistence transaction.
    if deferred && header["kind"] == 2 { store.validate_remote_dependencies(bytes,ordinal)?; }
    store.transaction(|tx| {
        tx.hydrate_object(ordinal, bytes, &header)?;
        if deferred { tx.register_dependencies(bytes,ordinal)?; }
        Ok(())
    })
}
impl Volume {
    pub fn set_object_provider(&self, provider: Option<Arc<ObjectProvider>>) -> Result<()> {
        self.shared.provider.replace(provider)
    }
    pub fn lazy_import(&self, object: &str, bytes: &[u8]) -> Result<()> {
        import(&self.shared, object, bytes)
    }
    pub(super) fn with_hydration<T>(&self, mut run: impl FnMut() -> Result<T>) -> Result<T> {
        let mut pins = Vec::new();
        loop {
            // Keep the old logical view protected until its missing object has a runtime pin.
            // Otherwise GC could delete the remote source between Missing and callback dispatch.
            let handoff = store::ReadLease::acquire(self.shared.readers.clone())?;
            match run() {
                Err(Error::Missing(object)) => {
                    let id = Uuid::parse_str(&object.id)
                        .map_err(|_| Error::Invalid("object id".into()))?;
                    let oid = u64::from_le_bytes(id.as_bytes()[8..].try_into().unwrap());
                    pins.push(self.shared.cache_runtime.pin(oid));
                    drop(handoff);
                    self.shared.hydrate_missing(&object)?;
                }
                result => return result,
            }
        }
    }
    pub(super) fn lazy_control(&self, r: &Value) -> Result<Value> {
        let g = self.geometry();
        let (_lease, mut reader) = self.read_context()?;
        let cloud: Cloud = if reader.root.cloud.empty() {
            Cloud::default()
        } else {
            serde_json::from_slice(&reader.blob(reader.root.cloud)?)?
        };
        match r["cmd"].as_str().unwrap_or("") {
            "lazy.status" => Ok(
                json!({"object_size":g.object_size,"enabled":cloud.lazy_backing.is_some()||cloud.cache.backing.is_some(),"index_complete":cloud.replica.as_ref().is_none_or(|r|r.counts_complete),"total_objects":reader.root.lazy_total_objects,"data_objects":reader.root.lazy_total_objects,"cached_objects":reader.root.lazy_cached_objects,"cached_bytes":reader.root.lazy_cached_objects*g.object_size,"missing_objects":reader.root.lazy_missing_objects,"missing_bytes":reader.root.lazy_missing_objects*g.object_size,"backing":cloud.lazy_backing}),
            ),
            "lazy.needs" => {
                let offset = r["offset"]
                    .as_u64()
                    .ok_or_else(|| Error::Invalid("lazy.needs offset".into()))?;
                let length = r["length"]
                    .as_u64()
                    .ok_or_else(|| Error::Invalid("lazy.needs length".into()))?;
                let end = offset
                    .checked_add(length)
                    .filter(|end| *end <= self.capacity())
                    .ok_or_else(|| Error::Invalid("lazy.needs range".into()))?;
                let limit = r["limit"].as_u64().unwrap_or(16).clamp(1, 16) as usize;
                let write = r["operation"] == "write";
                let mut items = BTreeMap::new();
                let first = offset / PAGE as u64;
                let last = end.div_ceil(PAGE as u64);
                let cached = self.cached_page_indices(first, last)?;
                let root = reader.root.index;
                let mut cursor = first;
                let mut scanned = 0;
                while cursor < last && scanned < 4096 && items.len() < limit {
                    let rows = tree::scan_after(&mut reader, &PAGES, root, cursor, 128)?;
                    if rows.is_empty() {
                        cursor = last;
                        break;
                    }
                    for (lba, row) in rows {
                        if lba >= last {
                            cursor = last;
                            break;
                        }
                        cursor = lba + 1;
                        scanned += 1;
                        if cached.contains(&lba) {
                            continue;
                        }
                        if write && offset <= lba * PAGE as u64 && end >= (lba + 1) * PAGE as u64 {
                            continue;
                        }
                        let p = store::Page::decode(&row)?;
                        let o = reader.object(p.reference.object)?;
                        if o.missing {
                            items.insert(o.oid,json!({"id":o.id,"sha256":codec::hex(&o.sha),"length":g.object_size,"kind":"data"}));
                        }
                        if items.len() >= limit || scanned >= 4096 {
                            break;
                        }
                    }
                }
                Ok(
                    json!({"items":items.into_values().collect::<Vec<_>>(),"next_offset":if cursor<last{Some(cursor*PAGE as u64)}else{None}}),
                )
            }
            _ => Err(Error::Invalid("unknown lazy command".into())),
        }
    }
}
