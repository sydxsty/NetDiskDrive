//! Canonical immutable object bytes. Unused local extent bytes are not part of
//! an object: readers synthesize their zero padding without reading/writing it.
//! Network input is never normalized before authentication.
use super::codec::{self, hash, hex, Crypto};
use super::store::{Object, PORTABLE};
use super::{Device, Error, Geometry, RemoteObject, Result, PAGE};
use serde_json::Value;
use uuid::Uuid;

fn integrity(message: &str) -> Error {
    Error::Integrity(message.into())
}

pub(super) fn payload_end(g: Geometry, kind: u64, used: u64) -> Result<usize> {
    if !matches!(kind, 1 | 2) || used > g.slots || (kind == 2 && used == 0) {
        return Err(integrity("object payload boundary"));
    }
    Ok((g.payload_pages + used) as usize * PAGE)
}

/// Check authenticated header fields before trusting any padding boundary.
pub(super) fn header_end(g: Geometry, header: &Value) -> Result<usize> {
    let kind = header["kind"]
        .as_u64()
        .ok_or_else(|| integrity("object kind"))?;
    let used = header["used"]
        .as_u64()
        .ok_or_else(|| integrity("object used slots"))?;
    let external = header["external_count"]
        .as_u64()
        .ok_or_else(|| integrity("object external count"))?;
    if external > g.external_limit || (kind == 1 && external != 0) {
        return Err(integrity("object dependency boundary"));
    }
    payload_end(g, kind, used)
}

pub(super) fn header(crypto: &Crypto, oid: u64, frame: &[u8; PAGE]) -> Result<Value> {
    let g = crypto.geometry;
    if oid == 0 || oid >= PORTABLE / g.object_size {
        return Err(integrity("object ordinal"));
    }
    let bytes = crypto.unframe(
        codec::OBJECT_HEADER,
        PORTABLE | (oid * g.object_size),
        frame,
    )?;
    let value: Value = serde_json::from_slice(&bytes)?;
    let id = value["id"]
        .as_str()
        .and_then(|s| Uuid::parse_str(s).ok())
        .ok_or_else(|| integrity("object authenticated identity"))?;
    if value["oid"].as_u64() != Some(oid)
        || u64::from_le_bytes(id.as_bytes()[8..].try_into().unwrap()) != oid
    {
        return Err(integrity("object authenticated ordinal"));
    }
    header_end(g, &value)?;
    Ok(value)
}

fn catalogue(g: Geometry, object: &Object) -> Result<(u64, usize)> {
    if object.missing {
        return Err(Error::Missing(RemoteObject::from_object(
            object,
            g.object_size,
        )));
    }
    if !object.sealed {
        return Err(Error::Invalid("object is not sealed".into()));
    }
    let end = payload_end(g, object.kind as u64, object.used as u64)?;
    let base = object
        .extent
        .checked_mul(g.object_size)
        .filter(|at| {
            object.extent != 0
                && at
                    .checked_add(g.object_size)
                    .is_some_and(|end| end <= i64::MAX as u64)
        })
        .ok_or_else(|| integrity("object extent range"))?;
    Ok((base, end))
}

fn match_catalogue(crypto: &Crypto, object: &Object, frame: &[u8; PAGE]) -> Result<Value> {
    let header = header(crypto, object.oid, frame)?;
    if header["id"].as_str() != Some(object.id.to_string().as_str())
        || header["kind"].as_u64() != Some(object.kind as u64)
        || header["used"].as_u64() != Some(object.used as u64)
        || header["external_count"].as_u64() != Some(object.external_count as u64)
    {
        return Err(integrity(
            "object header differs from authenticated catalogue",
        ));
    }
    Ok(header)
}

/// A complete export reads the used prefix exactly once. Partial export requests
/// authenticate the header separately when it is not already in their buffer.
/// The returned count is synthesized bytes, not physical I/O.
pub(super) fn read_range(
    device: &Device,
    crypto: &Crypto,
    object: &Object,
    offset: u64,
    output: &mut [u8],
) -> Result<(Value, u64)> {
    let g = crypto.geometry;
    let requested_end = offset
        .checked_add(output.len() as u64)
        .filter(|end| *end <= g.object_size)
        .ok_or_else(|| Error::Invalid("object range".into()))?;
    let (base, used_end) = catalogue(g, object)?;
    if offset == 0 && output.len() >= PAGE {
        let length = output.len().min(used_end);
        device.read(base, &mut output[..length])?;
        let head = match_catalogue(crypto, object, output[..PAGE].try_into().unwrap())?;
        output[length..].fill(0);
        return Ok((head, (output.len() - length) as u64));
    }
    let mut frame = [0; PAGE];
    device.read(base, &mut frame)?;
    let head = match_catalogue(crypto, object, &frame)?;
    output.fill(0);
    let mut current = offset;
    if current < PAGE as u64 {
        let end = requested_end.min(PAGE as u64);
        let length = (end - current) as usize;
        output[..length].copy_from_slice(&frame[current as usize..end as usize]);
        current = end;
    }
    let end = requested_end.min(used_end as u64);
    if current < end {
        device.read(
            base + current,
            &mut output[(current - offset) as usize..(end - offset) as usize],
        )?;
    }
    let zeros = requested_end.saturating_sub(offset.max(used_end as u64));
    Ok((head, zeros))
}

/// All local whole-object consumers (restore, compaction, snapshot export) use
/// this path, so ignored extent garbage cannot influence their object digest.
pub(super) fn read_verified(device: &Device, crypto: &Crypto, object: &Object) -> Result<Vec<u8>> {
    let mut raw = vec![0; crypto.geometry.object_size as usize];
    let (header, _) = read_range(device, crypto, object, 0, &mut raw)?;
    if hash(&raw) != object.sha
        || header["body_sha256"].as_str() != Some(hex(&hash(&raw[PAGE..])).as_str())
    {
        return Err(integrity("canonical sealed object checksum"));
    }
    Ok(raw)
}

/// Imported bytes have already passed their canonical SHA/header validation.
/// Persist only the authenticated prefix, leaving all physical tail bytes alone.
pub(super) fn write_verified_prefix(
    device: &Device,
    g: Geometry,
    extent: u64,
    raw: &[u8],
    header: &Value,
) -> Result<()> {
    if raw.len() != g.object_size as usize {
        return Err(integrity("object length"));
    }
    let end = header_end(g, header)?;
    if raw[end..].iter().any(|byte| *byte != 0) {
        return Err(integrity("noncanonical imported object padding"));
    }
    let at = extent
        .checked_mul(g.object_size)
        .filter(|at| {
            extent != 0
                && at
                    .checked_add(g.object_size)
                    .is_some_and(|end| end <= i64::MAX as u64)
        })
        .ok_or_else(|| integrity("object extent range"))?;
    device.write(at, &raw[..end])
}
