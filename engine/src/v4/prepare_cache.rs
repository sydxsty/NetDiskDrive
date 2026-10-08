//! Volatile, authenticated preparation state. Cache residency never establishes
//! durability: transaction epochs become visible only after both roots commit.
use super::codec::MetaRef;
use super::tree::{Node, Spec};
use super::{Error, Result, PAGE};
use std::collections::{BTreeMap, BTreeSet};

const MIB: usize = 1024 * 1024;
const IDLE_LIMIT: usize = 16 * MIB;
// Covers the two B-tree entries, allocator overhead and inline enum storage.
// Heap capacities (including decoded leaf values) are accounted separately.
const ENTRY_OVERHEAD: usize = 512;
// Reserve the cache's fixed bookkeeping and bounded diagnostic counters too.
const BOOKKEEPING: usize = 4096;

#[derive(Clone, PartialEq, Eq, PartialOrd, Ord)]
enum Key {
    Node(u64, [u8; 32]),
    Dependencies(u64),
    Cipher(u64, u64, u64),
}
impl Key {
    fn node(r: MetaRef) -> Self {
        Self::Node(r.offset, r.hash)
    }
}

#[derive(Default)]
pub(super) struct Dependencies {
    pub rows: Vec<[u8; 48]>,
    pub positions: BTreeMap<u64, usize>,
}
impl Dependencies {
    pub fn push(&mut self, row: [u8; 48]) -> Result<()> {
        let oid = u64::from_le_bytes(row[8..16].try_into().unwrap());
        if self.positions.insert(oid, self.rows.len()).is_some() {
            return Err(Error::Integrity(
                "duplicate active metadata dependency".into(),
            ));
        }
        self.rows.push(row);
        Ok(())
    }
    fn heap_bytes(&self) -> usize {
        self.rows.capacity() * 48 + self.positions.len() * 96
    }
}

enum Value {
    Node {
        payload: Box<[u8]>,
        decoded: Option<(Spec, Node)>,
    },
    Dependencies(Dependencies),
    Cipher(Box<[u8; PAGE]>),
}
fn node_heap(node: &Node) -> usize {
    match node {
        Node::Branch {
            children, counts, ..
        } => {
            children.capacity() * std::mem::size_of::<(u8, MetaRef)>()
                + counts.capacity() * std::mem::size_of::<u64>()
        }
        Node::Leaf { values, .. } => {
            values.capacity() * std::mem::size_of::<(u8, Vec<u8>)>()
                + values
                    .iter()
                    .map(|(_, bytes)| bytes.capacity())
                    .sum::<usize>()
        }
    }
}
impl Value {
    fn bytes(&self) -> usize {
        ENTRY_OVERHEAD
            + match self {
                Self::Node { payload, decoded } => {
                    payload.len()
                        + decoded
                            .as_ref()
                            .map(|(_, node)| node_heap(node))
                            .unwrap_or(0)
                }
                Self::Dependencies(table) => table.heap_bytes(),
                Self::Cipher(_) => PAGE,
            }
    }
}
struct Entry {
    epoch: u64,
    clock: u64,
    value: Value,
}

pub(super) struct Cache {
    entries: BTreeMap<Key, Entry>,
    lru: BTreeSet<(u64, Key)>,
    limit: usize,
    reported_limit: usize,
    used: usize,
    clock: u64,
    active: bool,
    counters: BTreeMap<String, u64>,
}
impl Default for Cache {
    fn default() -> Self {
        Self {
            entries: BTreeMap::new(),
            lru: BTreeSet::new(),
            limit: IDLE_LIMIT,
            reported_limit: 64 * MIB,
            used: 0,
            clock: 0,
            active: false,
            counters: BTreeMap::new(),
        }
    }
}
impl Cache {
    pub fn configure(&mut self, mib: u64) -> Result<()> {
        if !(16..=1024).contains(&mib) {
            return Err(Error::Invalid(
                "preparation cache must be 16..1024 MiB".into(),
            ));
        }
        if !self.active {
            self.counters.clear();
        }
        self.active = true;
        self.limit = mib as usize * MIB;
        self.reported_limit = self.limit;
        self.make_room(0);
        Ok(())
    }
    pub fn end(&mut self) {
        self.active = false;
        self.limit = IDLE_LIMIT;
        // Shared authenticated nodes still serve ordinary foreground reads.
        // Keep a small normal LRU instead of making every completed sync cold.
        let preparation = self
            .entries
            .keys()
            .filter(|key| !matches!(key, Key::Node(..)))
            .cloned()
            .collect::<Vec<_>>();
        for key in preparation {
            self.take(&key);
        }
        self.make_room(0);
    }
    pub fn invalidate(&mut self) {
        self.entries.clear();
        self.lru.clear();
        self.used = 0;
    }
    pub fn count(&mut self, name: &str, amount: u64) {
        if self.active {
            *self.counters.entry(name.into()).or_default() += amount;
        }
    }
    pub fn metrics(&self) -> BTreeMap<String, u64> {
        let mut values = self.counters.clone();
        for name in [
            "prepare_cache_hits",
            "prepare_cache_misses",
            "prepare_cache_evictions",
            "prepare_dependency_loads",
            "prepare_seal_cached_pages",
            "prepare_skipped_metadata_write_pages",
        ] {
            values.entry(name.into()).or_default();
        }
        values.insert(
            "prepare_cache_limit_bytes".into(),
            self.reported_limit as u64,
        );
        values.insert(
            "prepare_cache_used_bytes".into(),
            (if !self.active {
                0
            } else {
                self.used
                    + if self.entries.is_empty() {
                        0
                    } else {
                        BOOKKEEPING
                    }
            }) as u64,
        );
        values
    }
    fn take(&mut self, key: &Key) -> Option<Entry> {
        let entry = self.entries.remove(key)?;
        self.lru.remove(&(entry.clock, key.clone()));
        self.used -= entry.value.bytes();
        Some(entry)
    }
    fn make_room(&mut self, bytes: usize) {
        while self.used + bytes > self.limit - BOOKKEEPING {
            let Some((_, key)) = self.lru.first().cloned() else {
                break;
            };
            self.take(&key);
            self.count("prepare_cache_evictions", 1);
        }
    }
    fn put(&mut self, key: Key, epoch: u64, value: Value) {
        self.take(&key);
        let bytes = value.bytes();
        if bytes > self.limit - BOOKKEEPING {
            return;
        }
        self.make_room(bytes);
        self.clock += 1;
        self.used += bytes;
        self.lru.insert((self.clock, key.clone()));
        self.entries.insert(
            key,
            Entry {
                epoch,
                clock: self.clock,
                value,
            },
        );
        debug_assert!(self.used + BOOKKEEPING <= self.limit);
    }
    fn touch(&mut self, key: &Key, epoch: u64) -> bool {
        let valid = self
            .entries
            .get(key)
            .is_some_and(|entry| entry.epoch <= epoch);
        self.count(
            if valid {
                "prepare_cache_hits"
            } else {
                "prepare_cache_misses"
            },
            1,
        );
        if valid {
            let entry = self.entries.get_mut(key).unwrap();
            self.lru.remove(&(entry.clock, key.clone()));
            self.clock += 1;
            entry.clock = self.clock;
            self.lru.insert((entry.clock, key.clone()));
        }
        valid
    }
    pub fn contains_node(&self, r: MetaRef, epoch: u64) -> bool {
        self.entries
            .get(&Key::node(r))
            .is_some_and(|entry| entry.epoch <= epoch)
    }
    pub fn node(&mut self, r: MetaRef, epoch: u64) -> Option<Vec<u8>> {
        let key = Key::node(r);
        if !self.touch(&key, epoch) {
            return None;
        }
        match &self.entries.get(&key)?.value {
            Value::Node { payload, .. } => Some(payload.to_vec()),
            _ => unreachable!(),
        }
    }
    pub fn put_node(&mut self, r: MetaRef, epoch: u64, payload: Vec<u8>) {
        self.put(
            Key::node(r),
            epoch,
            Value::Node {
                payload: payload.into_boxed_slice(),
                decoded: None,
            },
        );
    }
    pub fn decoded(&mut self, r: MetaRef, spec: &Spec, epoch: u64) -> Option<Node> {
        let key = Key::node(r);
        if !self.touch(&key, epoch) {
            return None;
        }
        match &self.entries.get(&key)?.value {
            Value::Node {
                decoded: Some((cached_spec, node)),
                ..
            } if spec == cached_spec => Some(node.clone()),
            _ => None,
        }
    }
    pub fn put_decoded(
        &mut self,
        r: MetaRef,
        spec: Spec,
        epoch: u64,
        payload: Vec<u8>,
        node: Node,
    ) {
        self.put(
            Key::node(r),
            epoch,
            Value::Node {
                payload: payload.into_boxed_slice(),
                decoded: Some((spec, node)),
            },
        );
    }
    pub fn dependencies(&mut self, oid: u64, count: usize, epoch: u64) -> Option<Dependencies> {
        let key = Key::Dependencies(oid);
        if !self.touch(&key, epoch) {
            return None;
        }
        match self.take(&key)?.value {
            Value::Dependencies(table) if table.rows.len() == count => Some(table),
            _ => None,
        }
    }
    pub fn put_dependencies(&mut self, oid: u64, epoch: u64, table: Dependencies) {
        if self.active {
            self.put(Key::Dependencies(oid), epoch, Value::Dependencies(table));
        }
    }
    pub fn cipher(&mut self, oid: u64, extent: u64, slot: u64, epoch: u64) -> Option<[u8; PAGE]> {
        let key = Key::Cipher(oid, extent, slot);
        if !self.touch(&key, epoch) {
            return None;
        }
        match &self.entries.get(&key)?.value {
            Value::Cipher(bytes) => Some(**bytes),
            _ => unreachable!(),
        }
    }
    pub fn has_cipher(&self, oid: u64, extent: u64, slot: u64, epoch: u64) -> bool {
        self.entries
            .get(&Key::Cipher(oid, extent, slot))
            .is_some_and(|entry| entry.epoch <= epoch)
    }
    pub fn put_cipher(&mut self, oid: u64, extent: u64, slot: u64, epoch: u64, bytes: [u8; PAGE]) {
        if self.active {
            self.put(
                Key::Cipher(oid, extent, slot),
                epoch,
                Value::Cipher(Box::new(bytes)),
            );
        }
    }
    pub fn remove_object(&mut self, oid: u64, extent: u64, used: u64) {
        self.take(&Key::Dependencies(oid));
        for slot in 0..used {
            self.take(&Key::Cipher(oid, extent, slot));
        }
    }
}
