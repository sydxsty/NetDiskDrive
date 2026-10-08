//! Bounded nodes for persistent sparse radix trees. The storage implementation
//! authenticates MetaRef hashes and frames; this layer validates their payload.
//! All callbacks belong to the caller's root transaction: retired nodes must
//! not be physically reused until that transaction commits durably. A failed
//! operation may have produced unreachable new nodes/callbacks; the caller
//! rolls back their accounting together with its unpublished root.

use super::codec::MetaRef;
use super::{Error, Result};
use std::collections::BTreeMap;

const HEADER: usize = 32;
const PAYLOAD: usize = 4016;
const MAGIC: &[u8; 4] = b"ODTR";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Spec {
    pub tag: u8,
    pub leaf_bits: u8,
    /// Number of branch levels. Depth zero is a single leaf.
    pub depth: u8,
    pub value_size: usize,
    /// With tracked trees, root reference accounting retires shared subtrees.
    pub tracked: bool,
}

impl Spec {
    pub fn validate(&self) -> Result<()> {
        if !matches!(self.leaf_bits, 3..=5)
            || self.depth > 9
            || self.value_size == 0
            || self.value_size > (PAYLOAD - HEADER) / (1usize << self.leaf_bits)
        {
            return Err(invalid("invalid tree specification or oversized leaf"));
        }
        Ok(())
    }

    pub fn capacity(&self) -> Result<u64> {
        self.validate()?;
        Ok(span(self, self.depth))
    }
}

pub trait Storage {
    /// Must verify the referenced node's authenticated hash before returning.
    fn read_node(&mut self, reference: MetaRef) -> Result<Vec<u8>>;
    /// Prefetch only the requested authenticated nodes; implementations may
    /// combine adjacent physical reads without visiting additional subtrees.
    fn prefetch_nodes(&mut self, _references: &[MetaRef]) -> Result<()> {
        Ok(())
    }
    fn read_decoded(&mut self, spec: &Spec, reference: MetaRef) -> Result<Node> {
        decode_at(spec, reference, &self.read_node(reference)?)
    }
    fn write_node(&mut self, payload: &[u8]) -> Result<MetaRef>;
    /// Every new node is reported once, after its bytes have been written.
    fn created(
        &mut self,
        _reference: MetaRef,
        _tag: u8,
        _children: &[MetaRef],
        _leaf_values: &[(u64, Vec<u8>)],
    ) -> Result<()> {
        Ok(())
    }
    /// Stage retirement; do not free storage before publishing the new root.
    fn retired(&mut self, _reference: MetaRef) -> Result<()> {
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Node {
    Branch {
        level: u8,
        base: u64,
        children: Vec<(u8, MetaRef)>,
        counts: Vec<u64>,
    },
    Leaf {
        base: u64,
        values: Vec<(u8, Vec<u8>)>,
    },
}

fn invalid(message: &str) -> Error {
    Error::Invalid(message.into())
}

fn span(spec: &Spec, level: u8) -> u64 {
    1u64 << (spec.leaf_bits as u32 + 6 * level as u32)
}

// Page maps and their dirty-key tree originally used height four. The larger
// specification accepts those authenticated roots in place, and only grows a
// root when a nonempty write actually needs the extended address range.
fn extensible_pages(spec: &Spec) -> bool {
    spec.depth == 5
        && spec.leaf_bits == 5
        && matches!(
            (spec.tag, spec.value_size, spec.tracked),
            (1, 104, true) | (2, 1, true) | (4, 104, false)
        )
}

pub fn root_level<S: Storage>(store: &mut S, spec: &Spec, root: MetaRef) -> Result<u8> {
    spec.validate()?;
    check_reference(root)?;
    if !extensible_pages(spec) {
        return Ok(spec.depth);
    }
    if root.empty() {
        return Ok(4);
    }
    match visit_node(store, spec, root)? {
        Node::Branch {
            level: level @ 4..=5,
            base: 0,
            ..
        } => Ok(level),
        _ => Err(invalid("page tree root has an invalid level or base")),
    }
}

fn check_reference(reference: MetaRef) -> Result<()> {
    if reference.empty() && reference.hash != [0; 32] {
        return Err(invalid("noncanonical empty tree reference"));
    }
    Ok(())
}

/// Decode one payload for reference accounting. The caller authenticates its
/// containing frame first. Empty nodes are represented only by empty MetaRefs.
pub fn decode(spec: &Spec, payload: &[u8]) -> Result<Node> {
    spec.validate()?;
    if payload.len() < HEADER
        || payload.len() > PAYLOAD
        || &payload[..4] != MAGIC
        || payload[4] != 2
        || payload[5] != spec.tag
        || payload[6] != spec.leaf_bits
        || payload[7] > spec.depth
        || u16::from_le_bytes([payload[8], payload[9]]) as usize != spec.value_size
        || payload[10..16].iter().any(|byte| *byte != 0)
    {
        return Err(invalid("tree node header does not match its specification"));
    }
    let level = payload[7];
    let base = u64::from_le_bytes(payload[16..24].try_into().unwrap());
    let bitmap = u64::from_le_bytes(payload[24..32].try_into().unwrap());
    let width = span(spec, level);
    if bitmap == 0
        || !base.is_multiple_of(width)
        || base
            .checked_add(width)
            .is_none_or(|end| end > span(spec, spec.depth))
    {
        return Err(invalid("tree node key range or bitmap is invalid"));
    }
    let unit = if level == 0 { spec.value_size } else { 48 };
    if payload.len() != HEADER + bitmap.count_ones() as usize * unit {
        return Err(invalid("tree node payload length is invalid"));
    }
    if level == 0 && bitmap >> (1u32 << spec.leaf_bits) != 0 {
        return Err(invalid("leaf bitmap exceeds leaf fanout"));
    }
    let mut at = HEADER;
    if level == 0 {
        let mut values = Vec::with_capacity(bitmap.count_ones() as usize);
        for slot in 0..(1u8 << spec.leaf_bits) {
            if bitmap & (1u64 << slot) != 0 {
                values.push((slot, payload[at..at + unit].to_vec()));
                at += unit;
            }
        }
        Ok(Node::Leaf { base, values })
    } else {
        let mut children = Vec::with_capacity(bitmap.count_ones() as usize);
        let mut counts = Vec::with_capacity(bitmap.count_ones() as usize);
        for slot in 0..64u8 {
            if bitmap & (1u64 << slot) != 0 {
                let reference = MetaRef::get(&payload[at..at + 40]);
                if reference.empty() {
                    return Err(invalid("branch bitmap contains an empty child"));
                }
                let count = u64::from_le_bytes(payload[at + 40..at + 48].try_into().unwrap());
                if count == 0 || count > span(spec, level - 1) {
                    return Err(invalid("branch item count is out of range"));
                }
                children.push((slot, reference));
                counts.push(count);
                at += 48;
            }
        }
        Ok(Node::Branch {
            level,
            base,
            children,
            counts,
        })
    }
}

/// Local page COW trees may share authenticated immutable portable page nodes.
/// The alias is directional and applies only to portable addresses with the
/// exact page-map geometry; ordinary decode remains strict for every other use.
pub fn decode_at(spec: &Spec, reference: MetaRef, payload: &[u8]) -> Result<Node> {
    check_reference(reference)?;
    if reference.offset & (1u64 << 63) != 0
        && spec.tag == 1
        && spec.leaf_bits == 5
        && matches!(spec.depth, 4 | 5)
        && spec.value_size == 104
        && spec.tracked
        && payload.get(5) == Some(&4)
    {
        return decode(
            &Spec {
                tag: 4,
                tracked: false,
                ..*spec
            },
            payload,
        );
    }
    decode(spec, payload)
}

pub fn visit_node<S: Storage>(store: &mut S, spec: &Spec, reference: MetaRef) -> Result<Node> {
    check_reference(reference)?;
    if reference.empty() {
        return Err(invalid("cannot visit an empty tree"));
    }
    store.read_decoded(spec, reference)
}

fn load<S: Storage>(
    store: &mut S,
    spec: &Spec,
    reference: MetaRef,
    level: u8,
    base: u64,
) -> Result<Node> {
    let node = visit_node(store, spec, reference)?;
    let matches = match &node {
        Node::Leaf { base: actual, .. } => level == 0 && *actual == base,
        Node::Branch {
            level: actual_level,
            base: actual,
            ..
        } => *actual_level == level && *actual == base,
    };
    if !matches {
        return Err(invalid("tree child occupies the wrong key range or level"));
    }
    Ok(node)
}

/// Sorted unique multi-key lookup. Shared subtrees are traversed once per batch;
/// extensible root geometry is checked separately. Authentication and mixed
/// portable/local legacy-height page roots follow the same rules as single gets.
pub fn get_many<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    keys: &[u64],
) -> Result<Vec<Option<Vec<u8>>>> {
    spec.validate()?;
    check_reference(root)?;
    if keys.windows(2).any(|w| w[0] >= w[1])
        || keys
            .last()
            .is_some_and(|k| *k >= spec.capacity().unwrap_or(0))
    {
        return Err(invalid("batch keys must be sorted unique and in range"));
    }
    let mut out = vec![None; keys.len()];
    if root.empty() || keys.is_empty() {
        return Ok(out);
    }
    let level = root_level(store, spec, root)?;
    let end = keys.partition_point(|k| *k < span(spec, level));
    get_many_at(store, spec, root, level, 0, &keys[..end], &mut out[..end])?;
    Ok(out)
}
fn get_many_at<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    level: u8,
    base: u64,
    keys: &[u64],
    out: &mut [Option<Vec<u8>>],
) -> Result<()> {
    if keys.is_empty() {
        return Ok(());
    }
    match load(store, spec, root, level, base)? {
        Node::Leaf { values, .. } => {
            for (key, result) in keys.iter().zip(out) {
                let slot = (*key - base) as u8;
                if let Ok(i) = values.binary_search_by_key(&slot, |v| v.0) {
                    *result = Some(values[i].1.clone());
                }
            }
        }
        Node::Branch { children, .. } => {
            let width = span(spec, level - 1);
            let requested = children.iter().filter_map(|(slot, reference)| {
                let child_base = base + *slot as u64 * width;
                let first = keys.partition_point(|key| *key < child_base);
                (first < keys.len() && keys[first] < child_base + width).then_some(*reference)
            }).collect::<Vec<_>>();
            store.prefetch_nodes(&requested)?;
            let mut start = 0;
            while start < keys.len() {
                let slot = ((keys[start] - base) / width) as u8;
                let child_base = base + slot as u64 * width;
                let stop = start + keys[start..].partition_point(|k| *k < child_base + width);
                if let Ok(i) = children.binary_search_by_key(&slot, |v| v.0) {
                    get_many_at(
                        store,
                        spec,
                        children[i].1,
                        level - 1,
                        child_base,
                        &keys[start..stop],
                        &mut out[start..stop],
                    )?;
                }
                start = stop;
            }
        }
    }
    Ok(())
}

fn node_count(node: &Node) -> u64 {
    match node {
        Node::Leaf { values, .. } => values.len() as u64,
        Node::Branch { counts, .. } => counts.iter().sum(),
    }
}

fn write<S: Storage>(store: &mut S, spec: &Spec, node: Node) -> Result<(MetaRef, u64)> {
    let total = node_count(&node);
    let (level, base, bitmap, bytes, children, leaf_values) = match node {
        Node::Leaf { base, values } => {
            let mut bitmap = 0;
            let mut bytes = Vec::with_capacity(values.len() * spec.value_size);
            let mut leaf_values = Vec::with_capacity(values.len());
            for (slot, value) in values {
                bitmap |= 1u64 << slot;
                bytes.extend_from_slice(&value);
                leaf_values.push((base + slot as u64, value));
            }
            (0, base, bitmap, bytes, Vec::new(), leaf_values)
        }
        Node::Branch {
            level,
            base,
            children,
            counts,
        } => {
            if children.len() != counts.len() {
                return Err(invalid("branch count vector length"));
            }
            let mut bitmap = 0;
            let mut bytes = vec![0; children.len() * 48];
            let mut references = Vec::with_capacity(children.len());
            for (index, ((slot, child), count)) in children.into_iter().zip(counts).enumerate() {
                bitmap |= 1u64 << slot;
                child.put(&mut bytes[index * 48..index * 48 + 40]);
                bytes[index * 48 + 40..index * 48 + 48].copy_from_slice(&count.to_le_bytes());
                references.push(child);
            }
            (level, base, bitmap, bytes, references, Vec::new())
        }
    };
    let mut payload = vec![0; HEADER];
    payload[..4].copy_from_slice(MAGIC);
    payload[4] = 2;
    payload[5] = spec.tag;
    payload[6] = spec.leaf_bits;
    payload[7] = level;
    payload[8..10].copy_from_slice(&(spec.value_size as u16).to_le_bytes());
    payload[16..24].copy_from_slice(&base.to_le_bytes());
    payload[24..32].copy_from_slice(&bitmap.to_le_bytes());
    payload.extend(bytes);
    decode(spec, &payload)?;
    let reference = store.write_node(&payload)?;
    if reference.empty() {
        return Err(invalid(
            "storage returned an empty reference for a new node",
        ));
    }
    store.created(reference, spec.tag, &children, &leaf_values)?;
    Ok((reference, total))
}

pub fn get<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    key: u64,
) -> Result<Option<Vec<u8>>> {
    let capacity = spec.capacity()?;
    check_reference(root)?;
    if key >= capacity {
        return Err(invalid("tree key exceeds its configured range"));
    }
    let depth = root_level(store, spec, root)?;
    if key >= span(spec, depth) {
        return Ok(None);
    }
    let (mut reference, mut level, mut base) = (root, depth, 0);
    while !reference.empty() {
        match load(store, spec, reference, level, base)? {
            Node::Leaf { values, .. } => {
                return Ok(values
                    .into_iter()
                    .find(|(slot, _)| *slot as u64 == key - base)
                    .map(|(_, value)| value));
            }
            Node::Branch { children, .. } => {
                let width = span(spec, level - 1);
                let slot = ((key - base) / width) as u8;
                reference = children
                    .into_iter()
                    .find(|(actual, _)| *actual == slot)
                    .map(|(_, child)| child)
                    .unwrap_or_default();
                base += slot as u64 * width;
                level -= 1;
            }
        }
    }
    Ok(None)
}

/// Updates must be strictly increasing and unique. Validation happens before
/// any I/O or callback. Only affected paths are visited; identical values do
/// not create new nodes. None removes a key.
pub fn set_many<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    updates: &[(u64, Option<Vec<u8>>)],
) -> Result<MetaRef> {
    let capacity = spec.capacity()?;
    check_reference(root)?;
    if updates.windows(2).any(|pair| pair[0].0 >= pair[1].0)
        || updates.iter().any(|(key, value)| {
            *key >= capacity
                || value
                    .as_ref()
                    .is_some_and(|value| value.len() != spec.value_size)
        })
    {
        return Err(invalid(
            "tree updates must be sorted, unique, in range, and fixed length",
        ));
    }
    if updates.is_empty() {
        return Ok(root);
    }
    let depth = root_level(store, spec, root)?;
    let old_capacity = span(spec, depth);
    let split = updates.partition_point(|(key, _)| *key < old_capacity);
    if depth < spec.depth && updates[split..].iter().any(|(_, value)| value.is_some()) {
        let previous = if root.empty() {
            None
        } else {
            let count = len(store, spec, root)?;
            Some(Node::Branch {
                level: depth + 1,
                base: 0,
                children: vec![(0, root)],
                counts: vec![count],
            })
        };
        // The virtual parent has not been published or allocated. Its old child
        // stays shared; only the new high-address path and final parent are written.
        edit(
            store,
            spec,
            MetaRef::default(),
            depth + 1,
            0,
            updates,
            previous,
        )
        .map(|result| result.0)
    } else {
        if split == 0 {
            return Ok(root);
        }
        edit(store, spec, root, depth, 0, &updates[..split], None).map(|result| result.0)
    }
}

fn edit<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    level: u8,
    base: u64,
    updates: &[(u64, Option<Vec<u8>>)],
    existing_override: Option<Node>,
) -> Result<(MetaRef, u64)> {
    if root.empty()
        && existing_override.is_none()
        && updates.iter().all(|(_, value)| value.is_none())
    {
        return Ok((root, 0));
    }
    let existing = if existing_override.is_some() {
        existing_override
    } else if root.empty() {
        None
    } else {
        Some(load(store, spec, root, level, base)?)
    };
    let old_count = existing.as_ref().map(node_count).unwrap_or(0);
    let node = if level == 0 {
        let mut values: BTreeMap<_, _> = match existing {
            Some(Node::Leaf { values, .. }) => values.into_iter().collect(),
            None => BTreeMap::new(),
            _ => unreachable!(),
        };
        let mut changed = false;
        for (key, value) in updates {
            let slot = (key - base) as u8;
            match value {
                Some(value) if values.get(&slot) != Some(value) => {
                    values.insert(slot, value.clone());
                    changed = true;
                }
                None => {
                    changed |= values.remove(&slot).is_some();
                }
                _ => {}
            }
        }
        if !changed {
            return Ok((root, old_count));
        }
        if values.is_empty() {
            None
        } else {
            Some(Node::Leaf {
                base,
                values: values.into_iter().collect(),
            })
        }
    } else {
        let mut children: BTreeMap<u8, (MetaRef, u64)> = match existing {
            Some(Node::Branch {
                children, counts, ..
            }) => children
                .into_iter()
                .zip(counts)
                .map(|((slot, r), count)| (slot, (r, count)))
                .collect(),
            None => BTreeMap::new(),
            _ => unreachable!(),
        };
        let width = span(spec, level - 1);
        let mut at = 0;
        let mut changed = false;
        while at < updates.len() {
            let slot = ((updates[at].0 - base) / width) as u8;
            let child_base = base + slot as u64 * width;
            let count = updates[at..].partition_point(|(key, _)| *key < child_base + width);
            let previous = children.get(&slot).copied().unwrap_or_default();
            let next = edit(
                store,
                spec,
                previous.0,
                level - 1,
                child_base,
                &updates[at..at + count],
                None,
            )?;
            if next != previous {
                changed = true;
                if next.0.empty() {
                    children.remove(&slot);
                } else {
                    children.insert(slot, next);
                }
            }
            at += count;
        }
        if !changed {
            return Ok((root, old_count));
        }
        if children.is_empty() {
            None
        } else {
            let (children, counts) = children
                .into_iter()
                .map(|(slot, (r, count))| ((slot, r), count))
                .unzip();
            Some(Node::Branch {
                level,
                base,
                children,
                counts,
            })
        }
    };
    let result = match node {
        Some(node) => write(store, spec, node)?,
        None => (MetaRef::default(), 0),
    };
    if !spec.tracked && !root.empty() {
        store.retired(root)?;
    }
    Ok(result)
}

/// Return at most Limit entries with key >= Start. Traversal skips earlier
/// branches structurally and never materializes the remaining whole tree.
pub fn scan_after<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    start: u64,
    limit: usize,
) -> Result<Vec<(u64, Vec<u8>)>> {
    let capacity = spec.capacity()?;
    check_reference(root)?;
    let mut output = Vec::new();
    if limit != 0 && start < capacity && !root.empty() {
        let depth = root_level(store, spec, root)?;
        if start < span(spec, depth) {
            scan(store, spec, root, depth, 0, start, limit, &mut output)?;
        }
    }
    Ok(output)
}

/// Bound traversal at the leaves themselves. Unlike reading Limit rows and
/// truncating afterwards, this never visits the discarded remainder of a batch.
pub fn scan_after_leaves<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    start: u64,
    limit: usize,
    max_leaves: usize,
) -> Result<Vec<(u64, Vec<u8>)>> {
    let capacity = spec.capacity()?;
    check_reference(root)?;
    let mut output = Vec::new();
    let mut remaining = max_leaves;
    if limit != 0 && remaining != 0 && start < capacity && !root.empty() {
        let depth = root_level(store, spec, root)?;
        if start < span(spec, depth) {
            scan_leaves(store, spec, root, depth, 0, start, limit, &mut remaining, &mut output)?;
        }
    }
    Ok(output)
}

#[allow(clippy::too_many_arguments)]
fn scan_leaves<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    level: u8,
    base: u64,
    start: u64,
    limit: usize,
    remaining: &mut usize,
    output: &mut Vec<(u64, Vec<u8>)>,
) -> Result<()> {
    if *remaining == 0 || output.len() == limit {
        return Ok(());
    }
    match load(store, spec, root, level, base)? {
        Node::Leaf { values, .. } => {
            if !values.iter().any(|(slot, _)| base + *slot as u64 >= start) {
                return Ok(());
            }
            *remaining -= 1;
            for (slot, value) in values {
                let key = base + slot as u64;
                if key >= start {
                    output.push((key, value));
                    if output.len() == limit { break; }
                }
            }
        }
        Node::Branch { children, .. } => {
            let width = span(spec, level - 1);
            for (slot, child) in children {
                let child_base = base + slot as u64 * width;
                if child_base + width <= start { continue; }
                scan_leaves(store, spec, child, level - 1, child_base, start, limit, remaining, output)?;
                if *remaining == 0 || output.len() == limit { break; }
            }
        }
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn scan<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    level: u8,
    base: u64,
    start: u64,
    limit: usize,
    output: &mut Vec<(u64, Vec<u8>)>,
) -> Result<()> {
    match load(store, spec, root, level, base)? {
        Node::Leaf { values, .. } => {
            for (slot, value) in values {
                let key = base + slot as u64;
                if key >= start {
                    output.push((key, value));
                    if output.len() == limit {
                        break;
                    }
                }
            }
        }
        Node::Branch { children, .. } => {
            let width = span(spec, level - 1);
            for (slot, child) in children {
                let child_base = base + slot as u64 * width;
                if child_base + width <= start {
                    continue;
                }
                scan(
                    store,
                    spec,
                    child,
                    level - 1,
                    child_base,
                    start,
                    limit,
                    output,
                )?;
                if output.len() == limit {
                    break;
                }
            }
        }
    }
    Ok(())
}

/// Remove [Start, End), clamped to the tree's configured key space. Tracked
/// trees discard fully covered subtrees without reading descendants. Untracked
/// trees visit only removed descendants to stage every node's retirement.
pub fn remove_range<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    start: u64,
    end: u64,
) -> Result<MetaRef> {
    let capacity = spec.capacity()?;
    check_reference(root)?;
    if start > end {
        return Err(invalid("tree removal range is reversed"));
    }
    if start == end || start >= capacity || root.empty() {
        return Ok(root);
    }
    let depth = root_level(store, spec, root)?;
    let capacity = span(spec, depth);
    if start >= capacity {
        return Ok(root);
    }
    remove(store, spec, root, depth, 0, 0, start, end.min(capacity)).map(|result| result.0)
}

fn retire_subtree<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    level: u8,
    base: u64,
) -> Result<()> {
    if let Node::Branch { children, .. } = load(store, spec, root, level, base)? {
        let width = span(spec, level - 1);
        for (slot, child) in children {
            retire_subtree(store, spec, child, level - 1, base + slot as u64 * width)?;
        }
    }
    store.retired(root)
}

#[allow(clippy::too_many_arguments)]
fn remove<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    level: u8,
    base: u64,
    known_count: u64,
    start: u64,
    end: u64,
) -> Result<(MetaRef, u64)> {
    let bound = base + span(spec, level);
    if end <= base || start >= bound {
        return Ok((root, known_count));
    }
    if start <= base && end >= bound {
        if !spec.tracked {
            retire_subtree(store, spec, root, level, base)?;
        }
        return Ok((MetaRef::default(), 0));
    }
    let old = load(store, spec, root, level, base)?;
    let old_count = node_count(&old);
    let node = match old {
        Node::Leaf { mut values, .. } => {
            let old_len = values.len();
            values.retain(|(slot, _)| {
                let key = base + *slot as u64;
                key < start || key >= end
            });
            if values.len() == old_len {
                return Ok((root, old_count));
            }
            if values.is_empty() {
                None
            } else {
                Some(Node::Leaf { base, values })
            }
        }
        Node::Branch {
            children, counts, ..
        } => {
            let width = span(spec, level - 1);
            let mut changed = false;
            let mut next_children = Vec::with_capacity(children.len());
            let mut next_counts = Vec::with_capacity(children.len());
            for ((slot, previous), count) in children.into_iter().zip(counts) {
                let next = remove(
                    store,
                    spec,
                    previous,
                    level - 1,
                    base + slot as u64 * width,
                    count,
                    start,
                    end,
                )?;
                changed |= next != (previous, count);
                if !next.0.empty() {
                    next_children.push((slot, next.0));
                    next_counts.push(next.1);
                }
            }
            if !changed {
                return Ok((root, old_count));
            }
            if next_children.is_empty() {
                None
            } else {
                Some(Node::Branch {
                    level,
                    base,
                    children: next_children,
                    counts: next_counts,
                })
            }
        }
    };
    let result = match node {
        Some(node) => write(store, spec, node)?,
        None => (MetaRef::default(), 0),
    };
    if !spec.tracked {
        store.retired(root)?;
    }
    Ok(result)
}

/// The exact number of entries is available from one authenticated node.
pub fn len<S: Storage>(store: &mut S, spec: &Spec, root: MetaRef) -> Result<u64> {
    spec.validate()?;
    check_reference(root)?;
    if root.empty() {
        Ok(0)
    } else {
        let depth = root_level(store, spec, root)?;
        Ok(node_count(&load(store, spec, root, depth, 0)?))
    }
}

/// Exact rank (number of keys strictly below Key) in O(depth) node reads.
pub fn rank<S: Storage>(store: &mut S, spec: &Spec, root: MetaRef, key: u64) -> Result<u64> {
    let capacity = spec.capacity()?;
    check_reference(root)?;
    if root.empty() {
        return Ok(0);
    }
    let depth = root_level(store, spec, root)?;
    if key >= capacity || key >= span(spec, depth) {
        return len(store, spec, root);
    }
    let (mut at, mut level, mut base, mut result) = (root, depth, 0, 0);
    loop {
        match load(store, spec, at, level, base)? {
            Node::Leaf { values, .. } => {
                return Ok(result
                    + values
                        .into_iter()
                        .filter(|(slot, _)| base + (*slot as u64) < key)
                        .count() as u64);
            }
            Node::Branch {
                children, counts, ..
            } => {
                let width = span(spec, level - 1);
                let target = ((key - base) / width) as u8;
                let mut found = None;
                for ((slot, child), count) in children.into_iter().zip(counts) {
                    if slot < target {
                        result += count;
                    } else if slot == target {
                        found = Some(child);
                        break;
                    } else {
                        break;
                    }
                }
                let Some(child) = found else {
                    return Ok(result);
                };
                at = child;
                base += target as u64 * width;
                level -= 1;
            }
        }
    }
}

/// Fetch a page by ordinal rank, skipping entire subtrees using persisted counts.
pub fn scan_rank<S: Storage>(
    store: &mut S,
    spec: &Spec,
    root: MetaRef,
    skip: u64,
    limit: usize,
) -> Result<Vec<(u64, Vec<u8>)>> {
    spec.validate()?;
    check_reference(root)?;
    let mut out = Vec::new();
    if !root.empty() && limit > 0 {
        let depth = root_level(store, spec, root)?;
        let node = load(store, spec, root, depth, 0)?;
        if skip < node_count(&node) {
            scan_rank_node(store, spec, node, skip, limit, &mut out)?;
        }
    }
    Ok(out)
}
fn scan_rank_node<S: Storage>(
    store: &mut S,
    spec: &Spec,
    node: Node,
    mut skip: u64,
    limit: usize,
    out: &mut Vec<(u64, Vec<u8>)>,
) -> Result<()> {
    match node {
        Node::Leaf { base, values } => {
            for (slot, value) in values.into_iter().skip(skip as usize) {
                out.push((base + slot as u64, value));
                if out.len() == limit {
                    break;
                }
            }
        }
        Node::Branch {
            level,
            base,
            children,
            counts,
        } => {
            let width = span(spec, level - 1);
            for ((slot, child), count) in children.into_iter().zip(counts) {
                if skip >= count {
                    skip -= count;
                    continue;
                }
                let node = load(store, spec, child, level - 1, base + slot as u64 * width)?;
                if node_count(&node) != count {
                    return Err(invalid(
                        "child item count differs from authenticated parent",
                    ));
                }
                scan_rank_node(store, spec, node, skip, limit, out)?;
                skip = 0;
                if out.len() == limit {
                    break;
                }
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use sha2::{Digest, Sha256};
    use std::collections::BTreeSet;

    type Creation = (MetaRef, Vec<MetaRef>, Vec<(u64, Vec<u8>)>);

    #[derive(Default)]
    struct Memory {
        nodes: BTreeMap<u64, Vec<u8>>,
        reads: usize,
        writes: usize,
        created: Vec<Creation>,
        retired: Vec<MetaRef>,
        fail_write: Option<usize>,
        portable_writes: bool,
    }
    impl Storage for Memory {
        fn read_node(&mut self, reference: MetaRef) -> Result<Vec<u8>> {
            self.reads += 1;
            let bytes = self
                .nodes
                .get(&reference.offset)
                .ok_or_else(|| invalid("missing node"))?;
            let digest: [u8; 32] = Sha256::digest(bytes).into();
            if digest != reference.hash {
                return Err(invalid("node hash mismatch"));
            }
            Ok(bytes.clone())
        }
        fn write_node(&mut self, payload: &[u8]) -> Result<MetaRef> {
            self.writes += 1;
            if self.fail_write == Some(self.writes) {
                return Err(invalid("injected write failure"));
            }
            assert!(payload.len() <= PAYLOAD);
            let reference = MetaRef {
                offset: ((self.nodes.len() as u64 + 1) * 4096)
                    | if self.portable_writes { 1u64 << 63 } else { 0 },
                hash: Sha256::digest(payload).into(),
            };
            self.nodes.insert(reference.offset, payload.to_vec());
            Ok(reference)
        }
        fn created(
            &mut self,
            reference: MetaRef,
            _tag: u8,
            children: &[MetaRef],
            values: &[(u64, Vec<u8>)],
        ) -> Result<()> {
            self.created
                .push((reference, children.to_vec(), values.to_vec()));
            Ok(())
        }
        fn retired(&mut self, reference: MetaRef) -> Result<()> {
            self.retired.push(reference);
            Ok(())
        }
    }
    fn spec(bits: u8, tracked: bool) -> Spec {
        Spec {
            tag: 37,
            leaf_bits: bits,
            depth: 2,
            value_size: 8,
            tracked,
        }
    }
    fn value(number: u64) -> Vec<u8> {
        number.to_le_bytes().to_vec()
    }
    #[test]
    fn leaf_budget_stops_before_unconsumed_changes_and_resumes_exactly() {
        let mut store = Memory::default();
        let specification = spec(5, false);
        let changes = (0..4096).map(|n| (n * 32, Some(value(n)))).collect::<Vec<_>>();
        let root = set_many(&mut store, &specification, MetaRef::default(), &changes).unwrap();
        store.reads = 0;
        let all = scan_after(&mut store, &specification, root, 0, 4096).unwrap();
        let overread = store.reads;
        store.reads = 0;
        let first = scan_after_leaves(&mut store, &specification, root, 0, 4096, 128).unwrap();
        assert_eq!(first.len(), 128);
        assert!(store.reads < overread / 16, "leaf budget still reads the discarded tail");
        let mut actual = first;
        loop {
            let cursor = actual.last().map_or(0, |(key, _)| key + 1);
            let next = scan_after_leaves(&mut store, &specification, root, cursor, 4096, 128).unwrap();
            if next.is_empty() { break; }
            actual.extend(next);
        }
        assert_eq!(actual, all);
        // A cursor inside an exhausted leaf must not spend the only leaf budget
        // and mistakenly report end-of-tree before the following leaf.
        let one = scan_after_leaves(&mut store, &specification, root, 1, 4096, 1).unwrap();
        assert_eq!(one, vec![(32, value(1))]);
        assert!(scan_after_leaves(&mut store, &specification, root, 0, 4096, 0).unwrap().is_empty());
    }
    #[test]
    fn dense_leaf_page_limits_resume_without_missing_or_repeating_rows() {
        let mut store = Memory::default();
        let specification = spec(5, false);
        let capacity = specification.capacity().unwrap();
        // Dense leaves, a gap followed by a branch boundary, and the final key
        // exercise both partial-leaf batches and an exhausted key space.
        let expected = (0..129)
            .chain(2045..2082)
            .chain(capacity - 35..capacity)
            .map(|key| (key, value(key * 17)))
            .collect::<Vec<_>>();
        let changes = expected.iter().map(|(key, row)| (*key, Some(row.clone()))).collect::<Vec<_>>();
        let root = set_many(&mut store, &specification, MetaRef::default(), &changes).unwrap();
        for max_pages in [1, 31, 33] {
            for max_leaves in [1, 2, 128] {
                for start in [0, 1, 31, 32, 33, 128, 129, 2047, 2048, capacity - 33, capacity - 1, capacity] {
                    let wanted = expected.iter().filter(|(key, _)| *key >= start).cloned().collect::<Vec<_>>();
                    let mut actual = Vec::new();
                    let mut cursor = start;
                    loop {
                        let batch = scan_after_leaves(&mut store, &specification, root, cursor, max_pages, max_leaves).unwrap();
                        if batch.is_empty() { break; }
                        assert!(batch.len() <= max_pages);
                        assert!(batch.iter().map(|(key, _)| key >> specification.leaf_bits).collect::<BTreeSet<_>>().len() <= max_leaves);
                        assert!(batch.first().unwrap().0 >= cursor);
                        assert!(batch.windows(2).all(|rows| rows[0].0 < rows[1].0));
                        cursor = batch.last().unwrap().0 + 1;
                        actual.extend(batch);
                        assert!(actual.len() <= wanted.len(), "scan repeated rows: pages={max_pages}, leaves={max_leaves}, start={start}");
                    }
                    assert_eq!(actual, wanted, "scan lost or changed rows: pages={max_pages}, leaves={max_leaves}, start={start}");
                }
            }
        }
    }
    fn random(seed: &mut u64) -> u64 {
        *seed ^= *seed << 13;
        *seed ^= *seed >> 7;
        *seed ^= *seed << 17;
        *seed
    }

    #[test]
    fn page_roots_grow_for_high_addresses_without_rebuilding_legacy_roots() {
        for (tag, size, tracked) in [(1, 104, true), (2, 1, true), (4, 104, false)] {
            let legacy = Spec {
                tag,
                leaf_bits: 5,
                depth: 4,
                value_size: size,
                tracked,
            };
            let large = Spec { depth: 5, ..legacy };
            let mut store = Memory::default();
            let old_limit = legacy.capacity().unwrap();
            let high = (8u64 << 40) / 4096 - 1;
            let values = vec![
                (0, Some(vec![1; size])),
                (31, Some(vec![2; size])),
                (old_limit - 1, Some(vec![3; size])),
            ];
            let old = set_many(&mut store, &legacy, MetaRef::default(), &values).unwrap();
            let writes = store.writes;
            assert_eq!(root_level(&mut store, &large, old).unwrap(), 4);
            assert_eq!(set_many(&mut store, &large, old, &values).unwrap(), old);
            assert_eq!(
                set_many(&mut store, &large, old, &[(high, None)]).unwrap(),
                old
            );
            assert_eq!(
                remove_range(&mut store, &large, old, old_limit, high + 1).unwrap(),
                old
            );
            assert_eq!(get(&mut store, &large, old, high).unwrap(), None);
            assert_eq!(rank(&mut store, &large, old, high).unwrap(), 3);
            assert!(scan_after(&mut store, &large, old, old_limit, 4)
                .unwrap()
                .is_empty());
            assert_eq!(store.writes, writes);

            let grown = set_many(&mut store, &large, old, &[(high, Some(vec![8; size]))]).unwrap();
            assert_eq!(store.writes - writes, 6, "one new high path plus its root");
            assert_eq!(root_level(&mut store, &large, grown).unwrap(), 5);
            let Node::Branch { children, .. } = visit_node(&mut store, &large, grown).unwrap()
            else {
                panic!("branch");
            };
            assert_eq!(children[0], (0, old), "all old nodes stay shared");
            assert!(!store.retired.contains(&old));
            assert_eq!(len(&mut store, &large, grown).unwrap(), 4);
            assert_eq!(
                get(&mut store, &large, grown, high).unwrap(),
                Some(vec![8; size])
            );
            assert_eq!(
                scan_rank(&mut store, &large, grown, 2, 4).unwrap(),
                vec![(old_limit - 1, vec![3; size]), (high, vec![8; size])]
            );
            assert_eq!(rank(&mut store, &large, grown, high).unwrap(), 3);
            assert_eq!(len(&mut store, &legacy, old).unwrap(), 3);
            assert_eq!(
                get(&mut store, &legacy, old, 31).unwrap(),
                Some(vec![2; size])
            );
            let trimmed = remove_range(&mut store, &large, grown, old_limit, high + 1).unwrap();
            assert_eq!(len(&mut store, &large, trimmed).unwrap(), 3);
            assert_eq!(get(&mut store, &large, trimmed, high).unwrap(), None);
            assert_eq!(
                get(&mut store, &large, grown, high).unwrap(),
                Some(vec![8; size])
            );
            let shorter = Spec { depth: 3, ..legacy };
            let malformed_root = set_many(
                &mut store,
                &shorter,
                MetaRef::default(),
                &[(0, Some(vec![1; size]))],
            )
            .unwrap();
            assert!(root_level(&mut store, &large, malformed_root).is_err());
        }
    }

    #[test]
    fn portable_large_page_alias_keeps_legacy_baseline_and_high_snapshot() {
        let portable = Spec {
            tag: 4,
            leaf_bits: 5,
            depth: 4,
            value_size: 104,
            tracked: false,
        };
        let pages = Spec {
            tag: 1,
            depth: 5,
            tracked: true,
            ..portable
        };
        let mut store = Memory {
            portable_writes: true,
            ..Default::default()
        };
        let old = set_many(
            &mut store,
            &portable,
            MetaRef::default(),
            &[(7, Some(vec![7; 104]))],
        )
        .unwrap();
        store.portable_writes = false;
        let high = (5u64 << 40) / 4096;
        let grown = set_many(&mut store, &pages, old, &[(high, Some(vec![5; 104]))]).unwrap();
        let next = set_many(
            &mut store,
            &pages,
            grown,
            &[(7, Some(vec![9; 104])), (high, None)],
        )
        .unwrap();
        assert_eq!(get(&mut store, &pages, old, 7).unwrap(), Some(vec![7; 104]));
        assert_eq!(
            get(&mut store, &pages, grown, high).unwrap(),
            Some(vec![5; 104])
        );
        assert_eq!(
            get(&mut store, &pages, next, 7).unwrap(),
            Some(vec![9; 104])
        );
        assert_eq!(get(&mut store, &pages, next, high).unwrap(), None);
        assert_eq!(len(&mut store, &pages, next).unwrap(), 1);
    }

    #[test]
    fn portable_baseline_reuses_unchanged_nodes_and_cows_local_page_paths() {
        let pages = Spec {
            tag: 1,
            leaf_bits: 5,
            depth: 4,
            value_size: 104,
            tracked: true,
        };
        let portable = Spec {
            tag: 4,
            tracked: false,
            ..pages
        };
        let mut store = Memory {
            portable_writes: true,
            ..Default::default()
        };
        let page_value = |n: u64| {
            let mut bytes = vec![0; 104];
            bytes[..8].copy_from_slice(&n.to_le_bytes());
            bytes
        };
        let distant = (1u64 << 23) + 13;
        let updates = (0..8192)
            .chain([distant])
            .map(|n| (n, Some(page_value(n))))
            .collect::<Vec<_>>();
        let baseline = set_many(&mut store, &portable, MetaRef::default(), &updates).unwrap();
        assert_ne!(baseline.offset & (1u64 << 63), 0);
        assert_eq!(len(&mut store, &pages, baseline).unwrap(), 8193);
        let Node::Branch {
            children: old_children,
            ..
        } = visit_node(&mut store, &portable, baseline).unwrap()
        else {
            panic!("expected branch");
        };
        store.portable_writes = false;
        let writes = store.writes;
        let current = set_many(
            &mut store,
            &pages,
            baseline,
            &[(4097, Some(page_value(99)))],
        )
        .unwrap();
        assert_eq!(store.writes - writes, pages.depth as usize + 1);
        assert_eq!(current.offset & (1u64 << 63), 0);
        let Node::Branch {
            children: new_children,
            ..
        } = visit_node(&mut store, &pages, current).unwrap()
        else {
            panic!("expected branch");
        };
        assert_eq!(
            old_children.iter().find(|(slot, _)| *slot == 1),
            new_children.iter().find(|(slot, _)| *slot == 1)
        );
        assert_eq!(
            get(&mut store, &pages, baseline, 4097).unwrap(),
            Some(page_value(4097))
        );
        assert_eq!(
            get(&mut store, &pages, current, 4097).unwrap(),
            Some(page_value(99))
        );
        let trimmed = remove_range(&mut store, &pages, current, 4096, 4128).unwrap();
        assert_eq!(get(&mut store, &pages, trimmed, 4097).unwrap(), None);
        assert_eq!(
            get(&mut store, &pages, trimmed, distant).unwrap(),
            Some(page_value(distant))
        );
        assert_eq!(len(&mut store, &pages, trimmed).unwrap(), 8161);
        assert_eq!(rank(&mut store, &pages, trimmed, distant).unwrap(), 8160);
        assert_eq!(
            scan_after(&mut store, &pages, trimmed, 4095, 3).unwrap(),
            vec![
                (4095, page_value(4095)),
                (4128, page_value(4128)),
                (4129, page_value(4129))
            ]
        );
        assert_eq!(
            scan_rank(&mut store, &pages, baseline, 8192, 2).unwrap(),
            vec![(distant, page_value(distant))]
        );
    }

    #[test]
    fn portable_page_alias_requires_address_exact_spec_and_direction() {
        let pages = Spec {
            tag: 1,
            leaf_bits: 5,
            depth: 4,
            value_size: 104,
            tracked: true,
        };
        let portable = Spec {
            tag: 4,
            tracked: false,
            ..pages
        };
        let mut store = Memory {
            portable_writes: true,
            ..Default::default()
        };
        let root = set_many(
            &mut store,
            &portable,
            MetaRef::default(),
            &[(1, Some(vec![7; 104]))],
        )
        .unwrap();
        let payload = store.nodes[&root.offset].clone();
        assert!(decode(&pages, &payload).is_err());
        assert!(decode_at(&pages, root, &payload).is_ok());
        assert!(decode_at(
            &pages,
            MetaRef {
                offset: root.offset & !(1u64 << 63),
                ..root
            },
            &payload
        )
        .is_err());
        for wrong in [
            Spec {
                tracked: false,
                ..pages
            },
            Spec { tag: 2, ..pages },
            Spec { depth: 3, ..pages },
            Spec {
                leaf_bits: 4,
                ..pages
            },
            Spec {
                value_size: 8,
                ..pages
            },
        ] {
            assert!(decode_at(&wrong, root, &payload).is_err());
        }
        store.portable_writes = false;
        let local = set_many(&mut store, &pages, root, &[(1, Some(vec![9; 104]))]).unwrap();
        assert!(decode_at(&portable, local, &store.nodes[&local.offset]).is_err());
        let mut corrupt = payload;
        corrupt[6] = 4;
        assert!(decode_at(&pages, root, &corrupt).is_err());
    }

    #[test]
    fn random_model_put_remove_ranges_and_pagination() {
        for bits in [3, 4, 5] {
            for tracked in [false, true] {
                let mut store = Memory::default();
                let spec = spec(bits, tracked);
                let mut root = MetaRef::default();
                let mut model = BTreeMap::new();
                let mut seed = 0x8188_a107_cade_4201;
                for step in 0..600 {
                    let key = random(&mut seed) % 2400;
                    if step % 7 == 0 {
                        let end = key + random(&mut seed) % 90;
                        root = remove_range(&mut store, &spec, root, key, end).unwrap();
                        model.retain(|item, _| *item < key || *item >= end);
                    } else {
                        let item = if step % 5 == 0 {
                            None
                        } else {
                            Some(value(random(&mut seed)))
                        };
                        root = set_many(&mut store, &spec, root, &[(key, item.clone())]).unwrap();
                        if let Some(item) = item {
                            model.insert(key, item);
                        } else {
                            model.remove(&key);
                        }
                    }
                    assert_eq!(
                        get(&mut store, &spec, root, key).unwrap(),
                        model.get(&key).cloned()
                    );
                    let after = random(&mut seed) % 2400;
                    let expected: Vec<_> = model
                        .range(after..)
                        .take(13)
                        .map(|(k, v)| (*k, v.clone()))
                        .collect();
                    assert_eq!(
                        scan_after(&mut store, &spec, root, after, 13).unwrap(),
                        expected
                    );
                }
                assert_eq!(len(&mut store, &spec, root).unwrap(), model.len() as u64);
                for skip in [0, 1, 17, model.len().saturating_sub(1), model.len()] {
                    let expected: Vec<_> = model
                        .iter()
                        .skip(skip)
                        .take(11)
                        .map(|(key, value)| (*key, value.clone()))
                        .collect();
                    assert_eq!(
                        scan_rank(&mut store, &spec, root, skip as u64, 11).unwrap(),
                        expected
                    );
                }
                for key in [0, 1, 700, 2399, 3000] {
                    assert_eq!(
                        rank(&mut store, &spec, root, key).unwrap(),
                        model.range(..key).count() as u64
                    );
                }
                assert_eq!(
                    scan_after(&mut store, &spec, root, 0, usize::MAX).unwrap(),
                    model.into_iter().collect::<Vec<_>>()
                );
                if tracked {
                    assert!(store.retired.is_empty());
                } else {
                    assert_eq!(
                        store.retired.len(),
                        store
                            .retired
                            .iter()
                            .map(|reference| reference.offset)
                            .collect::<BTreeSet<_>>()
                            .len()
                    );
                }
            }
        }
    }

    #[test]
    fn updates_touch_only_changed_paths_and_old_snapshot_survives() {
        let mut store = Memory::default();
        let spec = spec(4, true);
        let updates: Vec<_> = (0..8192).map(|key| (key, Some(value(key)))).collect();
        let old = set_many(&mut store, &spec, MetaRef::default(), &updates).unwrap();
        let old_reads = store.reads;
        let old_writes = store.writes;
        let new = set_many(&mut store, &spec, old, &[(4097, Some(value(99)))]).unwrap();
        assert_eq!(store.reads - old_reads, spec.depth as usize + 1);
        assert_eq!(store.writes - old_writes, spec.depth as usize + 1);
        assert_eq!(
            get(&mut store, &spec, old, 4097).unwrap(),
            Some(value(4097))
        );
        assert_eq!(get(&mut store, &spec, new, 4097).unwrap(), Some(value(99)));
        let writes = store.writes;
        assert_eq!(
            set_many(&mut store, &spec, new, &[(4097, Some(value(99)))]).unwrap(),
            new
        );
        assert_eq!(store.writes, writes);
        let reads = store.reads;
        assert_eq!(
            scan_after(&mut store, &spec, new, 8000, 1).unwrap(),
            vec![(8000, value(8000))]
        );
        assert_eq!(store.reads - reads, spec.depth as usize + 1);
        let reads = store.reads;
        assert_eq!(
            scan_rank(&mut store, &spec, new, 8000, 1).unwrap(),
            vec![(8000, value(8000))]
        );
        assert_eq!(store.reads - reads, spec.depth as usize + 1);
        assert_eq!(len(&mut store, &spec, new).unwrap(), 8192);
        assert_eq!(rank(&mut store, &spec, new, 8000).unwrap(), 8000);
    }

    #[test]
    fn full_subtree_removal_is_structural_and_reference_accounted() {
        for tracked in [false, true] {
            let mut store = Memory::default();
            let spec = spec(4, tracked);
            let updates: Vec<_> = (0..2048).map(|key| (key, Some(value(key)))).collect();
            let old = set_many(&mut store, &spec, MetaRef::default(), &updates).unwrap();
            let reads = store.reads;
            let written = store.writes;
            let new = remove_range(&mut store, &spec, old, 0, 1024).unwrap();
            if tracked {
                assert_eq!(store.reads - reads, 1);
                assert!(store.retired.is_empty());
            } else {
                assert_eq!(store.retired.len(), 1 + 1 + 64);
            }
            assert_eq!(store.writes - written, 1);
            assert!(get(&mut store, &spec, new, 17).unwrap().is_none());
            assert_eq!(
                get(&mut store, &spec, new, 1030).unwrap(),
                Some(value(1030))
            );
            let reads = store.reads;
            assert!(remove_range(&mut store, &spec, new, 0, u64::MAX)
                .unwrap()
                .empty());
            if tracked {
                assert_eq!(store.reads, reads);
            }
            assert_eq!(store.created.len(), store.writes);
            for (reference, children, values) in &store.created {
                match decode(&spec, &store.nodes[&reference.offset]).unwrap() {
                    Node::Branch {
                        children: actual, ..
                    } => assert_eq!(
                        *children,
                        actual
                            .into_iter()
                            .map(|(_, child)| child)
                            .collect::<Vec<_>>()
                    ),
                    Node::Leaf {
                        base,
                        values: actual,
                    } => assert_eq!(
                        *values,
                        actual
                            .into_iter()
                            .map(|(slot, item)| (base + slot as u64, item))
                            .collect::<Vec<_>>()
                    ),
                }
            }
        }
    }

    #[test]
    fn malformed_input_and_corrupt_nodes_never_publish_a_root() {
        let mut store = Memory::default();
        let spec = spec(5, true);
        for changes in [
            vec![(8, Some(value(1))), (8, None)],
            vec![(9, None), (8, None)],
            vec![(spec.capacity().unwrap(), Some(value(1)))],
            vec![(1, Some(vec![0; 7]))],
        ] {
            assert!(set_many(&mut store, &spec, MetaRef::default(), &changes).is_err());
        }
        assert_eq!((store.reads, store.writes), (0, 0));
        let root = set_many(
            &mut store,
            &spec,
            MetaRef::default(),
            &[(1, Some(value(1)))],
        )
        .unwrap();
        store.nodes.get_mut(&root.offset).unwrap()[HEADER] ^= 1;
        assert!(get(&mut store, &spec, root, 1).is_err());
        assert!(scan_after(&mut store, &spec, root, 0, 1).is_err());
        assert!(get(
            &mut store,
            &spec,
            MetaRef {
                offset: 0,
                hash: [1; 32]
            },
            1
        )
        .is_err());
        assert!(Spec {
            value_size: 192,
            ..spec
        }
        .validate()
        .is_err());
        assert!(Spec {
            leaf_bits: 4,
            value_size: 192,
            ..spec
        }
        .validate()
        .is_ok());
    }

    #[test]
    fn failed_copy_on_write_keeps_the_previous_root_readable() {
        let mut store = Memory::default();
        let spec = spec(4, true);
        let root = set_many(
            &mut store,
            &spec,
            MetaRef::default(),
            &[(1, Some(value(17))), (4096, Some(value(31)))],
        )
        .unwrap();
        for failure in 1..=3 {
            store.fail_write = Some(store.writes + failure);
            assert!(set_many(&mut store, &spec, root, &[(1, Some(value(90)))]).is_err());
            assert_eq!(get(&mut store, &spec, root, 1).unwrap(), Some(value(17)));
            assert_eq!(get(&mut store, &spec, root, 4096).unwrap(), Some(value(31)));
        }
        store.fail_write = None;
        assert!(store.retired.is_empty());
    }

    #[test]
    fn leaf_only_and_maximum_depth_bounds() {
        let mut store = Memory::default();
        for depth in [0, 9] {
            let spec = Spec {
                depth,
                value_size: 192,
                ..spec(4, true)
            };
            let last = spec.capacity().unwrap() - 1;
            let root = set_many(
                &mut store,
                &spec,
                MetaRef::default(),
                &[(0, Some(vec![3; 192])), (last, Some(vec![7; 192]))],
            )
            .unwrap();
            assert_eq!(
                get(&mut store, &spec, root, last).unwrap(),
                Some(vec![7; 192])
            );
            assert_eq!(
                scan_after(&mut store, &spec, root, last, 3).unwrap(),
                vec![(last, vec![7; 192])]
            );
            assert!(scan_after(&mut store, &spec, root, u64::MAX, 1)
                .unwrap()
                .is_empty());
            let root = remove_range(&mut store, &spec, root, last, last + 1).unwrap();
            assert_eq!(
                scan_after(&mut store, &spec, root, 0, 3).unwrap(),
                vec![(0, vec![3; 192])]
            );
        }
    }
}
