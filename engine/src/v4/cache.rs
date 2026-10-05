//! Physical cache policy and authenticated per-object source routing. No guest writes or cloud deletion.
use super::cloud::Cloud;
use super::store::{Object, COUNTS, OBJECTS};
use super::*;
use serde_json::{json, Value};
use std::collections::{HashMap, VecDeque};
use std::sync::atomic::AtomicU64;
use std::time::Instant;
#[derive(Clone, Serialize, Deserialize)]
pub(super) struct Policy {
    pub max_bytes: u64,
    pub policy: String,
    pub backing: Option<Value>,
    pub evicted_objects: u64,
    pub evicted_bytes: u64,
    pub pending: Vec<(u64, u64)>,
}
impl Default for Policy {
    fn default() -> Self {
        Self {
            max_bytes: 0,
            policy: "lru".into(),
            backing: None,
            evicted_objects: 0,
            evicted_bytes: 0,
            pending: Vec::new(),
        }
    }
}
#[derive(Clone, Default)]
struct Heat {
    last: u64,
    freq: u64,
    sequential: bool,
}
#[derive(Default)]
struct State {
    clock: u64,
    heat: HashMap<u64, Heat>,
    order: VecDeque<(u64, u64)>,
    last_oid: u64,
    last_end: u64,
    pins: HashMap<u64, usize>,
    cursor: u64,
    blocked: Option<String>,
    more: bool,
    exhausted: Option<(MetaRef, Instant)>,
}
pub(super) struct Runtime {
    state: Mutex<State>,
    pub online: AtomicBool,
    pub allocated: AtomicU64,
    last_measure: Mutex<Instant>,
}
impl Default for Runtime {
    fn default() -> Self {
        Self::new(0)
    }
}
impl Runtime {
    pub fn new(allocated: u64) -> Self {
        Self {
            state: Mutex::new(State::default()),
            online: AtomicBool::new(false),
            allocated: AtomicU64::new(allocated),
            last_measure: Mutex::new(Instant::now()),
        }
    }
    pub fn touch(&self, oid: u64, offset: u64, length: u64) {
        if let Ok(mut s) = self.state.lock() {
            s.clock = s.clock.wrapping_add(1);
            let tick = s.clock;
            let seq = length > 0 && s.last_end == offset;
            let same = seq && s.last_oid == oid;
            let entry = s.heat.entry(oid).or_default();
            entry.last = tick;
            if !same {
                entry.freq = entry.freq.saturating_add(1);
            }
            entry.sequential = seq;
            s.last_oid = oid;
            s.last_end = offset.saturating_add(length);
            s.order.push_back((oid, tick));
            while s.order.len() > 65536 {
                if let Some((old, seen)) = s.order.pop_front() {
                    if s.heat.get(&old).is_some_and(|h| h.last == seen) {
                        s.heat.remove(&old);
                    }
                }
            }
        }
    }
    pub fn is_pinned(&self, oid: u64) -> bool {
        self.state
            .lock()
            .map(|s| s.pins.contains_key(&oid))
            .unwrap_or(true)
    }
    pub fn pin(self: &Arc<Self>, oid: u64) -> Pin {
        if let Ok(mut s) = self.state.lock() {
            *s.pins.entry(oid).or_default() += 1;
        }
        Pin {
            runtime: self.clone(),
            oid,
        }
    }
    fn measure(&self, device: &Device, force: bool) -> Result<u64> {
        let mut at = self.last_measure.lock().map_err(|_| Error::Poisoned)?;
        if force || at.elapsed() >= Duration::from_secs(1) {
            self.allocated
                .store(device.allocated_bytes()?, Ordering::Release);
            *at = Instant::now();
        }
        Ok(self.allocated.load(Ordering::Acquire))
    }
    fn note(&self, blocked: Option<&str>, more: bool) {
        if let Ok(mut s) = self.state.lock() {
            s.blocked = blocked.map(str::to_owned);
            s.more = more;
        }
    }
}
pub(super) struct Pin {
    runtime: Arc<Runtime>,
    oid: u64,
}
impl Drop for Pin {
    fn drop(&mut self) {
        if let Ok(mut s) = self.runtime.state.lock() {
            if let Some(n) = s.pins.get_mut(&self.oid) {
                *n -= 1;
                if *n == 0 {
                    s.pins.remove(&self.oid);
                }
            }
        }
    }
}
fn invalid(text: &str) -> Error {
    Error::Invalid(text.into())
}
fn source_from_origin(c: &Cloud, volume_id: Uuid) -> Option<Value> {
    c.lazy_backing.as_ref().map(|b|json!({"backend_id":b["provider_id"],"account_id":b["account_id"],"remote_root":b["remote_root"],"volume_id":b.get("source_volume_id").cloned().unwrap_or(json!(volume_id)),"reader_pin":b["reader_pin"]}))
}
fn same_root(a: &Value, b: &Value) -> bool {
    a["backend_id"] == b["backend_id"]
        && a["account_id"] == b["account_id"]
        && a["remote_root"] == b["remote_root"]
}
fn source(
    c: &Cloud,
    o: &Object,
    volume_id: Uuid,
    own_published: bool,
) -> Result<(&'static str, Value)> {
    let origin = source_from_origin(c, volume_id);
    if let Some(own) = &c.cache.backing {
        if o.cache_backed
            || own_published
            || (o.origin_backed || o.remote_source == 1)
                && origin.as_ref().is_some_and(|old| same_root(old, own))
        {
            return Ok(("own", own.clone()));
        }
    }
    if o.remote && (o.origin_backed || o.remote_source == 1) {
        if let Some(origin) = origin {
            return Ok(("origin", origin));
        }
    }
    Err(invalid("object has no confirmed remote source"))
}
impl Volume {
    fn cache_view(&self) -> Result<(store::ReadLease, volume::Reader, Cloud)> {
        let (lease, mut reader) = self.read_context()?;
        let c = if reader.root.cloud.empty() {
            Cloud::default()
        } else {
            serde_json::from_slice(&reader.blob(reader.root.cloud)?)?
        };
        Ok((lease, reader, c))
    }
    fn cache_status(&self) -> Result<Value> {
        let g = self.geometry();
        let (_lease, reader, c) = self.cache_view()?;
        let rt = &self.shared.cache_runtime;
        let allocated = rt.measure(&self.shared.device, false)?;
        let s = rt.state.lock().map_err(|_| Error::Poisoned)?;
        let ready = c.cache.backing.is_some() && c.published_root != 0 && reader.root.cache_capable;
        Ok(
            json!({"object_size":g.object_size,"max_bytes":c.cache.max_bytes,"policy":c.cache.policy,"online":rt.online.load(Ordering::Acquire),"source_ready":ready,"origin_ready":c.lazy_backing.is_some(),"eviction_ready":ready||c.lazy_backing.is_some(),"origin_backing":source_from_origin(&c,reader.root.id),"backing":c.cache.backing,"allocated_bytes":allocated,"cached_bytes":reader.root.lazy_cached_objects*g.object_size,"over_limit_bytes":if c.cache.max_bytes==0{0}else{allocated.saturating_sub(c.cache.max_bytes)},"missing_objects":reader.root.lazy_missing_objects,"evicted_objects":c.cache.evicted_objects,"evicted_bytes":c.cache.evicted_bytes,"pending_reclaims":c.cache.pending.len(),"blocked_reason":s.blocked,"more_work":s.more}),
        )
    }
    pub(super) fn cache_control(&self, r: &Value) -> Result<Value> {
        let g = self.geometry();
        match r["cmd"].as_str().unwrap_or("") {
            "cache.status" => self.cache_status(),
            "cache.source" => {
                let id = r["object_id"]
                    .as_str()
                    .ok_or_else(|| invalid("cache.source object_id"))?;
                let uuid = Uuid::parse_str(id).map_err(|_| invalid("object ID"))?;
                let (_lease, mut reader, c) = self.cache_view()?;
                let oid = u64::from_le_bytes(uuid.as_bytes()[8..].try_into().unwrap());
                let o = reader.object(oid)?;
                if o.id != uuid {
                    return Err(invalid("object identity mismatch"));
                }
                let published = tree::get(&mut reader, &COUNTS, c.published_counts, oid)?.is_some();
                let (kind, backing) = source(&c, &o, reader.root.id, published)?;
                Ok(
                    json!({"object_id":o.id,"sha256":codec::hex(&o.sha),"length":g.object_size,"source":kind,"backing":backing}),
                )
            }
            "cloud.gc_candidates" => {
                let ids = r["object_ids"]
                    .as_array()
                    .filter(|v| v.len() <= 64)
                    .ok_or_else(|| invalid("GC candidates require at most 64 IDs"))?;
                let (_lease, mut reader, c) = self.cache_view()?;
                let old_readers = self
                    .shared
                    .readers
                    .lock()
                    .map_err(|_| Error::Poisoned)?
                    .active
                    .keys()
                    .any(|seq| *seq < reader.root.seq);
                let pins = self
                    .shared
                    .cache_runtime
                    .state
                    .lock()
                    .map_err(|_| Error::Poisoned)?
                    .pins
                    .clone();
                let mut allowed = Vec::new();
                let mut protected = Vec::new();
                for value in ids {
                    let id = value.as_str().ok_or_else(|| invalid("GC object ID"))?;
                    let uuid = Uuid::parse_str(id).map_err(|_| invalid("GC object ID"))?;
                    let oid = u64::from_le_bytes(uuid.as_bytes()[8..].try_into().unwrap());
                    let root = reader.root.objects;
                    let row = tree::get(&mut reader, &OBJECTS, root, oid)?;
                    let mut protect = old_readers || pins.contains_key(&oid);
                    if let Some(row) = row {
                        let o = Object::decode(oid, &row)?;
                        if o.id == uuid {
                            protect |= o.refs > 0
                                || o.current_refs > 0
                                || tree::get(&mut reader, &COUNTS, c.published_counts, oid)?
                                    .is_some()
                                || tree::get(&mut reader, &COUNTS, c.base_counts, oid)?.is_some();
                            if let Some(j) = &c.job {
                                protect |= j.meta_tail == oid
                                    || j.seal_tails
                                        .get(j.seal_cursor..)
                                        .is_some_and(|tail| tail.contains(&oid))
                                    || tree::get(&mut reader, &COUNTS, j.counts, oid)?.is_some();
                            }
                        }
                    }
                    if protect {
                        protected.push(id);
                    } else {
                        allowed.push(id);
                    }
                }
                Ok(json!({"allowed":allowed,"protected":protected,"cache_pin":c.cache.backing}))
            }
            "cache.online" => {
                let available = r["available"]
                    .as_bool()
                    .ok_or_else(|| invalid("cache online requires boolean"))?;
                self.shared
                    .cache_runtime
                    .online
                    .store(available, Ordering::Release);
                self.shared
                    .cache_runtime
                    .measure(&self.shared.device, true)?;
                self.cache_status()
            }
            "cache.configure" => {
                let max = r["max_bytes"]
                    .as_u64()
                    .ok_or_else(|| invalid("cache max_bytes"))?;
                let policy = r["policy"].as_str().unwrap_or("lru");
                if !matches!(policy, "lru" | "lfu" | "sequential") {
                    return Err(invalid("unknown cache policy"));
                }
                self.shared
                    .cache_runtime
                    .measure(&self.shared.device, true)?;
                {
                    let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
                    if max > 0 {
                        s.promote_cache_capability()?;
                    }
                    let mut c = s.cloud.clone();
                    c.cache.max_bytes = max;
                    c.cache.policy = policy.into();
                    s.transaction(|tx| tx.save_cloud(&c))?;
                    s.cloud = c;
                }
                self.cache_status()
            }
            "cache.bind" => {
                let backing = r["backing"].clone();
                let replaced;
                {
                    let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
                    let b = s
                        .cloud
                        .binding
                        .as_ref()
                        .ok_or_else(|| invalid("disk is not bound"))?;
                    if s.cloud.published_root == 0
                        || backing["volume_id"] != s.config.id.to_string()
                        || ["backend_id", "account_id", "remote_root", "device_id"]
                            .iter()
                            .any(|k| backing[*k] != b[*k])
                        || backing["reader_pin"]
                            != format!(
                                "{}/readers/{}.json",
                                b["remote_root"].as_str().unwrap_or(""),
                                s.config.id
                            )
                    {
                        return Err(invalid(
                            "cache pin does not match the confirmed publication binding",
                        ));
                    }
                    replaced = source_from_origin(&s.cloud, s.config.id)
                        .filter(|old| same_root(old, &backing))
                        .map(|b| b["reader_pin"].clone());
                    s.promote_cache_capability()?;
                    let mut c = s.cloud.clone();
                    c.cache.backing = Some(backing);
                    s.transaction(|tx| tx.save_cloud(&c))?;
                    s.cloud = c;
                }
                let mut result = self.cache_status()?;
                result["replaces_origin_pin"] = replaced.unwrap_or(Value::Null);
                Ok(result)
            }
            "cache.step" => {
                self.cache_step(r["max_objects"].as_u64().unwrap_or(4).clamp(1, 8) as usize)?;
                self.cache_status()
            }
            _ => Err(invalid("unknown cache command")),
        }
    }
    fn cache_step(&self, limit: usize) -> Result<()> {
        let g = self.geometry();
        let rt = &self.shared.cache_runtime;
        let mut allocated = rt.measure(&self.shared.device, false)?;
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        s.check()?;
        let mut c = s.cloud.clone();
        // Reclaim only extents durably retired by this cache. Reader leases can defer punching.
        if !c.cache.pending.is_empty() {
            let pending = c.cache.pending.clone();
            let (freed, remaining) = s.decommit_cache_extents(&pending)?;
            if remaining != pending || freed > 0 {
                c.cache.pending = remaining;
                c.cache.evicted_bytes = c.cache.evicted_bytes.saturating_add(freed);
                s.transaction(|tx| tx.save_cloud(&c))?;
                s.cloud = c.clone();
                allocated = rt.measure(&self.shared.device, true)?;
            }
        }
        let reason = if c.cache.max_bytes == 0 {
            Some("unlimited")
        } else if !rt.online.load(Ordering::Acquire) {
            Some("offline")
        } else if (c.cache.backing.is_none() && c.lazy_backing.is_none()) || !s.root.cache_capable {
            Some("source_not_ready")
        } else if !self.shared.provider.available() {
            Some("provider_unavailable")
        } else if s.root.restore_required {
            Some("restore_incomplete")
        } else if allocated <= c.cache.max_bytes {
            Some("within_limit")
        } else if c.cache.pending.len() >= 32 {
            Some("active_readers")
        } else {
            None
        };
        if let Some(reason) = reason {
            rt.note(Some(reason), false);
            return Ok(());
        }
        let cursor = {
            let state = rt.state.lock().map_err(|_| Error::Poisoned)?;
            if state.exhausted.as_ref().is_some_and(|(root, at)| {
                *root == s.root.objects && at.elapsed() < Duration::from_secs(30)
            }) {
                return Ok(());
            }
            state.cursor.max(1)
        };
        let root = s.root.objects;
        let rows = tree::scan_after(&mut *s, &OBJECTS, root, cursor, 128)?;
        let has_more = rows.len() == 128;
        // A step examines only its bounded window; never clone the full access-history map.
        let (heat, pins) = {
            let state = rt.state.lock().map_err(|_| Error::Poisoned)?;
            let heat = rows
                .iter()
                .filter_map(|(id, _)| state.heat.get(id).map(|h| (*id, h.clone())))
                .collect::<HashMap<_, _>>();
            let pins = rows
                .iter()
                .filter_map(|(id, _)| state.pins.contains_key(id).then_some(*id))
                .collect::<std::collections::BTreeSet<_>>();
            (heat, pins)
        };
        let next = rows.last().map(|(id, _)| id + 1).unwrap_or(1);
        if let Ok(mut state) = rt.state.lock() {
            state.cursor = if has_more { next } else { 1 };
        }
        let mut candidates = Vec::new();
        for (oid, row) in rows {
            let o = Object::decode(oid, &row)?;
            if o.kind != 1
                || !o.sealed
                || o.missing
                || pins.contains(&oid)
                || s.root.tails.contains(&oid)
                || c.transfer.get(&oid).is_some_and(|v| v == "uploading")
            {
                continue;
            }
            let own_published = s.count(c.published_counts, oid)? > 0;
            let Ok((kind, backing)) = source(&c, &o, s.config.id, own_published) else {
                continue;
            };
            let online_backing = c
                .cache
                .backing
                .as_ref()
                .cloned()
                .or_else(|| source_from_origin(&c, s.config.id));
            if online_backing.as_ref().is_none_or(|online| {
                online["backend_id"] != backing["backend_id"]
                    || online["account_id"] != backing["account_id"]
            }) {
                continue;
            }
            let h = heat.get(&oid).cloned().unwrap_or_default();
            let score = match c.cache.policy.as_str() {
                "lfu" => (h.freq, h.last),
                "sequential" => (u64::from(!h.sequential), h.last),
                _ => (0, h.last),
            };
            candidates.push((score, o, kind == "own"));
        }
        candidates.sort_by_key(|(score, _, _)| *score);
        let count = allocated
            .saturating_sub(c.cache.max_bytes)
            .div_ceil(g.object_size)
            .max(1)
            .min(limit as u64)
            .min((32 - c.cache.pending.len()) as u64) as usize;
        candidates.truncate(count);
        if candidates.is_empty() {
            if !has_more {
                if let Ok(mut state) = rt.state.lock() {
                    state.exhausted = Some((root, Instant::now()));
                }
            }
            rt.note(
                Some(if has_more {
                    "scanning"
                } else {
                    "protected_or_metadata"
                }),
                has_more,
            );
            return Ok(());
        }
        {
            let guard = rt.state.lock().map_err(|_| Error::Poisoned)?;
            candidates.retain(|(_, o, _)| !guard.pins.contains_key(&o.oid));
            if candidates.is_empty() {
                return Ok(());
            }
            s.transaction(|tx| {
                for (_, o, own) in &candidates {
                    let p = tx.evict_object(o.oid, *own)?;
                    c.cache.pending.push(p);
                    c.cache.evicted_objects += 1;
                }
                tx.save_cloud(&c)
            })?;
        }
        s.cloud = c.clone();
        #[cfg(test)]
        store::crash_point("cache_after_eviction");
        let (freed, pending) = s.decommit_cache_extents(&c.cache.pending)?;
        c.cache.pending = pending;
        c.cache.evicted_bytes = c.cache.evicted_bytes.saturating_add(freed);
        s.transaction(|tx| tx.save_cloud(&c))?;
        s.cloud = c;
        drop(s);
        let actual = rt.measure(&self.shared.device, true)?;
        rt.note(None, actual > self.cache_view()?.2.cache.max_bytes);
        Ok(())
    }
}
