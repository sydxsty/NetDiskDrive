//! Validate the graph inside one downloaded metadata object. This never opens
//! another object: later reads authenticate each missing child on demand.
use super::codec::{self, hash, Crypto, MetaRef};
use super::store::{self, Object, Page, PORTABLE, PORTMAP};
use super::tree::{self, Node};
use super::{Error, Geometry, Result, MAX_CAPACITY, PAGE};
use serde_json::Value;
use std::collections::BTreeMap;
use uuid::Uuid;

fn integrity(message: &str) -> Error {
    Error::Integrity(message.into())
}

fn node_address(reference: MetaRef, g: Geometry) -> Result<(u64, u64)> {
    if reference.empty() || reference.offset & PORTABLE == 0 || reference.hash == [0; 32] {
        return Err(integrity("portable index contains a nonportable reference"));
    }
    let address = reference.offset & !PORTABLE;
    let oid = address / g.object_size;
    let within = address % g.object_size;
    if oid == 0
        || !within.is_multiple_of(PAGE as u64)
        || within < (g.payload_pages + 1) * PAGE as u64
        || within + PAGE as u64 > g.object_size
    {
        return Err(integrity("portable index slot is invalid"));
    }
    Ok((oid, within / PAGE as u64 - g.payload_pages))
}

/// Used by all mixed local/portable readers before dereferencing an object.
/// A lazy identity placeholder deliberately has no kind/used information yet.
pub(super) fn validate_node_reference(
    reference: MetaRef,
    object: &Object,
    g: Geometry,
) -> Result<()> {
    let (oid, slot) = node_address(reference, g)?;
    if object.oid != oid {
        return Err(integrity("portable index object identity mismatch"));
    }
    if object.missing && object.kind == 0 {
        return Ok(());
    }
    if object.kind != 2 || object.used as u64 > g.slots || slot >= object.used as u64 {
        return Err(integrity("portable index slot outside metadata object"));
    }
    Ok(())
}

fn authenticated_reference(
    reference: MetaRef,
    oid: u64,
    used: u64,
    raw: &[u8],
    refs: &BTreeMap<u64, (Uuid, [u8; 32])>,
    g: Geometry,
) -> Result<Option<usize>> {
    let (child, slot) = node_address(reference, g)?;
    if child != oid {
        if !refs.contains_key(&child) {
            return Err(integrity("portable dependency is not authenticated"));
        }
        return Ok(None);
    }
    if slot >= used {
        return Err(integrity("portable index refers beyond the sealed payload"));
    }
    let at = (g.payload_pages + slot) as usize * PAGE;
    if hash(&raw[at..at + PAGE]) != reference.hash {
        return Err(integrity("portable child digest disagrees with its object"));
    }
    Ok(Some(slot as usize - 1))
}

fn shape(node: &Node) -> (u8, u64, u64) {
    match node {
        Node::Branch {
            level,
            base,
            counts,
            ..
        } => (*level, *base, counts.iter().sum()),
        Node::Leaf { base, values } => (0, *base, values.len() as u64),
    }
}

/// The caller has authenticated the object header/body and decrypted its table.
/// Validate every contained node once, while those bytes are already in memory.
pub(super) fn validate_object(
    crypto: &Crypto,
    oid: u64,
    raw: &[u8],
    header: &Value,
    refs: &BTreeMap<u64, (Uuid, [u8; 32])>,
) -> Result<()> {
    if header["kind"] != 2 {
        return Ok(());
    }
    let g = crypto.geometry;
    let used = header["used"]
        .as_u64()
        .filter(|n| *n > 0 && *n <= g.slots)
        .ok_or_else(|| integrity("portable metadata payload size"))?;
    let id = header["id"]
        .as_str()
        .and_then(|s| Uuid::parse_str(s).ok())
        .ok_or_else(|| integrity("portable metadata object identity"))?;
    if oid == 0 || u64::from_le_bytes(id.as_bytes()[8..].try_into().unwrap()) != oid {
        return Err(integrity(
            "portable metadata UUID disagrees with its ordinal",
        ));
    }
    let config = store::public_config(raw)?;
    let namespace_end = store::allocation_range(&config)?.end;
    if header["config"] != serde_json::to_value(&config)?
        || config.format_version != 4 || oid >= namespace_end
        || config.crypto_id.unwrap_or(config.id) != crypto.id
        || config.encrypted != crypto.key.is_some()
        || config.capacity_bytes < 64 << 20
        || config.capacity_bytes > MAX_CAPACITY
        || !config.capacity_bytes.is_multiple_of(512)
    {
        return Err(integrity("portable metadata configuration"));
    }
    for (&child, (_, digest)) in refs {
        if child == 0 || child == oid || child >= namespace_end || *digest == [0; 32] {
            return Err(integrity("portable dependency identity is invalid"));
        }
    }
    let mut nodes = Vec::with_capacity(used as usize - 1);
    for slot in 1..used {
        let at = (g.payload_pages + slot) as usize * PAGE;
        let address = PORTABLE | (oid * g.object_size + at as u64);
        let payload =
            crypto.unframe(codec::NODE, address, raw[at..at + PAGE].try_into().unwrap())?;
        nodes.push(tree::decode(&PORTMAP, &payload)?);
    }
    for node in &nodes {
        match node {
            Node::Branch {
                level,
                base,
                children,
                counts,
            } => {
                let width = 1u64 << (PORTMAP.leaf_bits as u32 + 6 * (*level as u32 - 1));
                for (i, (slot, reference)) in children.iter().enumerate() {
                    if let Some(child) =
                        authenticated_reference(*reference, oid, used, raw, refs, g)?
                    {
                        if shape(&nodes[child])
                            != (*level - 1, *base + *slot as u64 * width, counts[i])
                        {
                            return Err(integrity(
                                "portable child range, level or count is invalid",
                            ));
                        }
                    }
                }
            }
            Node::Leaf { base, values } => {
                for (slot, row) in values {
                    let page = Page::decode(row)?;
                    if *base + *slot as u64 >= config.capacity_bytes.div_ceil(PAGE as u64)
                        || page.reference.slot >= g.slots
                        || page.reference.object == oid
                        || !refs.contains_key(&page.reference.object)
                    {
                        return Err(integrity(
                            "portable data page is outside its authenticated dependencies",
                        ));
                    }
                }
            }
        }
    }
    let at = g.header_bytes();
    let root_frame: &[u8; PAGE] = raw[at..at + PAGE].try_into().unwrap();
    if root_frame.iter().any(|byte| *byte != 0) {
        let address = PORTABLE | (oid * g.object_size + at as u64);
        let root: Value =
            serde_json::from_slice(&crypto.unframe(codec::ROOT, address, root_frame)?)?;
        let depth = root["index_depth"]
            .as_u64()
            .filter(|depth| (4..=5).contains(depth))
            .ok_or_else(|| integrity("portable root index depth"))?;
        if root["format_version"] != 4
            || root["container_id"] != config.id.to_string()
            || root["crypto_id"] != crypto.id.to_string()
            || root["capacity_bytes"].as_u64() != Some(config.capacity_bytes)
            || root["page_size"] != PAGE
            || root["object_size"] != g.object_size
            || root["generation"].as_u64().is_none()
        {
            return Err(integrity("portable root descriptor identity"));
        }
        let reference: MetaRef = serde_json::from_value(root["index"].clone())?;
        if reference.empty() {
            if reference.hash != [0; 32] {
                return Err(integrity("portable root has a noncanonical empty index"));
            }
        } else if let Some(child) = authenticated_reference(reference, oid, used, raw, refs, g)? {
            let (level, base, _) = shape(&nodes[child]);
            if level != depth as u8 || base != 0 {
                return Err(integrity("portable root index range or level is invalid"));
            }
        }
    }
    Ok(())
}
