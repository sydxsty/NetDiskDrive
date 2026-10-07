//! Durable bounded restore. Status is read-only; explicit steps validate/rebuild
//! a fixed tree before the authenticated root permits any block-device access.
use super::cloud::{Need, Restore, Snapshot};
use super::codec::{hash, hex};
use super::store::{
    self, Page as StoredPage, Store, Txn, DIRTY, OBJECTS, PAGES, PORTABLE, PORTMAP, QUEUE, SET,
};
use super::tree::{self, Node};
use super::*;
use serde_json::{json, Value};
use std::collections::BTreeSet;
use zeroize::Zeroizing;

pub(super) struct CachedObject {
    pub id: Uuid,
    pub sha: [u8; 32],
    pub raw: Vec<u8>,
    pub table: BTreeMap<u64, (Uuid, [u8; 32])>,
}
fn cached_object(s: &mut Store, o: &store::Object) -> Result<Arc<CachedObject>> {
    if let Some(at) = s
        .restore_cache
        .iter()
        .position(|c| c.id == o.id && c.sha == o.sha)
    {
        *s.diagnostics
            .entry("restore_metadata_cache_hits".into())
            .or_default() += 1;
        let entry = s.restore_cache.remove(at).unwrap();
        s.restore_cache.push_back(entry.clone());
        return Ok(entry);
    }
    let raw = s.verify_object(o)?;
    *s.diagnostics
        .entry("restore_metadata_object_reads".into())
        .or_default() += 1;
    cache_verified(s, o, &raw)
}
fn cache_verified(s: &mut Store, o: &store::Object, raw: &[u8]) -> Result<Arc<CachedObject>> {
    *s.diagnostics
        .entry("restore_verified_metadata_objects".into())
        .or_default() += 1;
    let entry = Arc::new(CachedObject {
        id: o.id,
        sha: o.sha,
        raw: raw.to_vec(),
        table: store::external_table(&s.crypto, o.oid, raw)?,
    });
    s.restore_cache.retain(|c| c.id != o.id);
    while s.restore_cache.len() >= ((32 * 1024 * 1024) / s.object_size()).max(1) as usize {
        s.restore_cache.pop_front();
    }
    s.restore_cache.push_back(entry.clone());
    Ok(entry)
}
fn validate_publication(
    p: &Value,
    source: &Config,
    root: Uuid,
    sha: &str,
    generation: u64,
    backing: Option<&Value>,
) -> Result<()> {
    let c = &p["commit"];
    let b = &p["binding"];
    let writer = c["writerId"]
        .as_str()
        .ok_or_else(|| invalid("original publication writer"))?;
    if c["formatVersion"] != 4
        || c["volumeId"] != source.id.to_string()
        || c["generation"].as_u64() != Some(generation)
        || c["rootObjectId"] != root.to_string()
        || !c["rootSha256"]
            .as_str()
            .is_some_and(|v| v.eq_ignore_ascii_case(sha))
        || c["capacityBytes"].as_u64() != Some(source.capacity_bytes)
        || c["encrypted"].as_bool() != Some(source.encrypted)
        || c["objectSizeBytes"].as_u64() != Some(source.object_size)
        || c["rootSlot"].as_u64() != Some(0)
        || Uuid::parse_str(writer).is_err()
    {
        return Err(integrity(
            "original publication differs from authenticated root",
        ));
    }
    if b["device_id"] != writer
        || b["remote_root"] != format!("/OverlayDisk/{}", source.id)
        || b["enabled"] != true
        || b["account_id"].as_str().is_none_or(|v| v.is_empty())
        || b["backend_id"].as_str().is_none_or(|v| v.is_empty())
    {
        return Err(invalid("original binding identity"));
    }
    if let Some(backing) = backing {
        if b["account_id"] != backing["account_id"]
            || b["backend_id"] != backing["provider_id"]
            || b["remote_root"] != backing["remote_root"]
        {
            return Err(integrity("original binding differs from pinned backing"));
        }
    }
    Ok(())
}

fn verify_base_data(s: &mut Store, oid: u64, raw: &[u8], header: &Value) -> Result<()> {
    let g = s.geometry();
    let object = s.object(oid)?;
    let used = header["used"]
        .as_u64()
        .filter(|n| *n <= g.slots && *n >= object.used as u64)
        .ok_or_else(|| integrity("base data slots"))?;
    let mut table = raw[PAGE..g.header_bytes() - 1024].to_vec();
    s.crypto.open_blob(
        format!("OverlayDisk v4 object table {}", object.id).as_bytes(),
        &store::parse_hex::<24>(header["table_nonce"].as_str().unwrap_or(""))?,
        &store::parse_hex::<16>(header["table_tag"].as_str().unwrap_or(""))?,
        &mut table,
    )?;
    let mut verified = 0;
    for slot in 0..used {
        let at = slot as usize * 64;
        let lba = u64::from_le_bytes(table[at..at + 8].try_into().unwrap());
        if lba >= s.config.capacity_bytes / PAGE as u64 {
            return Err(integrity("base data logical address"));
        }
        if let Some(page) = s.page(s.root.index, lba)? {
            if page.reference.object == oid && page.reference.slot == slot {
                let start = (g.payload_pages as usize + slot as usize) * PAGE;
                let mut plain = Zeroizing::new([0; PAGE]);
                plain.copy_from_slice(&raw[start..start + PAGE]);
                s.crypto.decode_page(lba, page.reference, &mut plain)?;
                if hash(plain.as_ref()) != page.digest {
                    return Err(integrity("base data plaintext digest"));
                }
                verified += 1;
            }
        }
    }
    if verified != object.current_refs {
        return Err(integrity(
            "base data references disagree with object descriptor table",
        ));
    }
    Ok(())
}

fn invalid(message: &str) -> Error {
    Error::Invalid(message.into())
}
fn integrity(message: &str) -> Error {
    Error::Integrity(message.into())
}
fn number(value: &Value, key: &str, default: u64) -> u64 {
    value[key].as_u64().unwrap_or(default)
}
fn restore_state(s: &Store) -> Result<Restore> {
    s.cloud.restore.clone().ok_or_else(|| {
        invalid("restore state missing; resume initialization with its verified source")
    })
}
fn oid(id: Uuid) -> Result<u64> {
    let n = u64::from_le_bytes(id.as_bytes()[8..].try_into().unwrap());
    if n == 0 || n >= OBJECTS.capacity()? {
        return Err(integrity(
            "portable object number is outside the supported range",
        ));
    }
    Ok(n)
}
fn empty_bootstrap(s: &Store) -> bool {
    s.root.restore_required
        && s.cloud.restore.is_none()
        && s.root.index.empty()
        && s.root.objects.empty()
        && s.root.allocated_pages == 0
        && s.snapshots.is_empty()
        && s.root.tails.iter().all(|tail| *tail == 0)
        && s.root.data_generation == 0
}
fn initial(kind: &str, source: Uuid, index: MetaRef, generation: u64) -> Restore {
    Restore {
        lazy: false,
        mode: "copy".into(),
        baseline_counts: MetaRef::default(),
        verified_nodes: 0,
        data_cursor: 0,
        publication: None,
        kind: kind.into(),
        phase: if kind == "local" {
            "copying"
        } else {
            "building"
        }
        .into(),
        root_id: String::new(),
        root_sha256: String::new(),
        source_volume_id: source,
        source_snapshot_id: None,
        generation,
        index,
        queue: MetaRef::default(),
        queue_tail: 0,
        processed: MetaRef::default(),
        received: 0,
        total: 0,
        completed_pages: 0,
        expected_pages: 0,
        cursor: 0,
        source_pin: None,
        source_pin_cleanup_pending: false,
    }
}
fn save(s: &mut Store, restore: Restore) -> Result<()> {
    let mut cloud = s.cloud.clone();
    cloud.restore = Some(restore);
    s.transaction(|tx| tx.save_cloud(&cloud))?;
    s.cloud = cloud;
    Ok(())
}

#[derive(Clone)]
struct Work {
    id: Uuid,
    sha: [u8; 32],
    node: MetaRef,
    base: u64,
    level: u8,
    count: u64,
}
impl Work {
    fn encode(&self) -> Vec<u8> {
        let mut row = vec![0; 192];
        row[0] = 1;
        row[8..24].copy_from_slice(self.id.as_bytes());
        row[24..56].copy_from_slice(&self.sha);
        self.node.put(&mut row[56..96]);
        row[160..168].copy_from_slice(&self.base.to_le_bytes());
        row[168] = self.level;
        row[176..184].copy_from_slice(&self.count.to_le_bytes());
        row
    }
    fn decode(row: &[u8]) -> Result<Self> {
        if row.len() != 192 || row[0] != 1 {
            return Err(integrity("restore queue record"));
        }
        let id = Uuid::from_slice(&row[8..24]).map_err(|_| integrity("restore queue object id"))?;
        oid(id)?;
        Ok(Self {
            id,
            sha: row[24..56].try_into().unwrap(),
            node: MetaRef::get(&row[56..96]),
            base: u64::from_le_bytes(row[160..168].try_into().unwrap()),
            level: row[168],
            count: u64::from_le_bytes(row[176..184].try_into().unwrap()),
        })
    }
    fn need(&self) -> Need {
        Need {
            id: self.id.to_string(),
            oid: oid(self.id).expect("validated queue ID"),
            sha256: hex(&self.sha),
            kind: "metadata".into(),
        }
    }
}
fn imported(s: &mut Store, work: &Work) -> Result<bool> {
    let r = s.root.objects;
    let Some(row) = tree::get(s, &OBJECTS, r, oid(work.id)?)? else {
        return Ok(false);
    };
    let o = store::Object::decode(oid(work.id)?, &row)?;
    if o.missing && o.id == work.id && o.sha == work.sha { return Ok(false); }
    if o.id != work.id || o.sha != work.sha || !o.sealed || o.kind != 2 {
        return Err(integrity("restore object identity changed"));
    }
    Ok(true)
}
fn pending(s: &mut Store, r: &Restore, limit: usize) -> Result<Vec<Need>> {
    if r.phase == "data" {
        let root = s.root.objects;
        return Ok(tree::scan_after(s, &OBJECTS, root, r.data_cursor, 128)?
            .into_iter()
            .map(|(id, v)| store::Object::decode(id, &v))
            .collect::<Result<Vec<_>>>()?
            .into_iter()
            .filter(|o| o.kind == 1 && o.missing && o.refs > 0)
            .take(limit)
            .map(|o| Need {
                id: o.id.to_string(),
                oid: o.oid,
                sha256: hex(&o.sha),
                kind: "data".into(),
            })
            .collect());
    }

    let rows = tree::scan_after(s, &QUEUE, r.queue, r.queue_tail.saturating_sub(128), 128)?;
    let mut seen = BTreeSet::new();
    let mut out = Vec::new();
    for (_, row) in rows.into_iter().rev() {
        let w = Work::decode(&row)?;
        if !imported(s, &w)? && seen.insert(w.id) {
            out.push(w.need());
            if out.len() >= limit {
                break;
            }
        }
    }
    Ok(out)
}
fn status(s: &mut Store, limit: usize, source_attached: bool) -> Result<Value> {
    let g = s.geometry();
    let Some(r) = s.cloud.restore.clone() else {
        return if empty_bootstrap(s) {
            Ok(
                json!({"object_size":g.object_size,"kind":"uninitialized","phase":"initializing","capacity_bytes":s.config.capacity_bytes,"encrypted":s.config.encrypted,"needed":[]}),
            )
        } else {
            Err(invalid("not a restoring container"))
        };
    };
    let needed = if r.kind == "cloud" && r.phase != "complete" {
        pending(s, &r, limit.min(16))?
    } else {
        Vec::new()
    };
    let phase = if r.kind == "local" && r.phase != "complete" && !source_attached {
        "needs_source"
    } else if !needed.is_empty() {
        if needed.iter().any(|n| n.kind == "metadata") {
            "metadata"
        } else {
            "data"
        }
    } else if r.kind == "cloud" && r.phase == "data" {
        // The public protocol uses `building` whenever the next step is local.
        // After the final download, the durable object cursor still needs one
        // bounded step before it can declare the fully materialized tree ready.
        "building"
    } else {
        &r.phase
    };
    let items: Vec<_> = needed
        .iter()
        .map(
            |n| json!({"id":n.id,"sha256":n.sha256,"kind":n.kind,"length":g.object_size,"uploaded":false}),
        )
        .collect();
    Ok(
        json!({"object_size":g.object_size,"kind":r.kind,"phase":phase,"work_phase":r.phase,"mode":r.mode,"lazy":r.lazy,"index_mode":if r.kind=="cloud"{"portable_base"}else{"local_copy"},"verified_nodes":r.verified_nodes,"completed_nodes":r.verified_nodes,"verified_metadata_objects":s.diagnostics.get("restore_verified_metadata_objects").copied().unwrap_or(0),"metadata_cache_hits":s.diagnostics.get("restore_metadata_cache_hits").copied().unwrap_or(0),"queued_page_records":0,"metadata_object_reads":s.diagnostics.get("restore_metadata_object_reads").copied().unwrap_or(0),"root_object_id":r.root_id,"root_sha256":r.root_sha256,"source_volume_id":r.source_volume_id,"source_snapshot_id":r.source_snapshot_id,"generation":r.generation,
        "capacity_bytes":s.config.capacity_bytes,"encrypted":s.config.encrypted,"completed_objects":r.received,"total_objects":r.total.max(r.received+items.len()as u64),"estimated":true,
        "completed_pages":r.completed_pages,"expected_pages":r.expected_pages,"total_pages":r.expected_pages,"source_pin_cleanup_pending":r.source_pin_cleanup_pending,"needed":items,"items":items,"next_cursor":Value::Null}),
    )
}
fn reference_work(
    reference: MetaRef,
    level: u8,
    base: u64,
    count: u64,
    parent: &store::Object,
    table: &BTreeMap<u64, (Uuid, [u8; 32])>,
    g: Geometry,
) -> Result<Work> {
    if reference.empty() || reference.offset & PORTABLE == 0 {
        return Err(integrity("restore index must contain portable references"));
    }
    let address = reference.offset & !PORTABLE;
    let object = address / g.object_size;
    let within = address % g.object_size;
    if within < (g.payload_pages + 1) * PAGE as u64
        || !within.is_multiple_of(PAGE as u64)
        || within + PAGE as u64 > g.object_size
    {
        return Err(integrity("portable index slot is invalid"));
    }
    let (id, sha) = if object == parent.oid {
        (parent.id, parent.sha)
    } else {
        *table
            .get(&object)
            .ok_or_else(|| integrity("portable dependency is not authenticated"))?
    };
    if oid(id)? != object {
        return Err(integrity("portable object UUID disagrees with reference"));
    }
    Ok(Work {
        id,
        sha,
        node: reference,
        base,
        level,
        count,
    })
}
fn push(tx: &mut Txn<'_>, r: &mut Restore, works: &[Work]) -> Result<()> {
    let mut changes = Vec::new();
    for work in works.iter().rev() {
        if r.queue_tail >= 4096 {
            return Err(integrity("restore traversal exceeds bounded stack"));
        }
        changes.push((r.queue_tail, Some(work.encode())));
        r.queue_tail += 1;
    }
    if !changes.is_empty() {
        r.queue = tx.set(&QUEUE, r.queue, &changes)?;
    }
    Ok(())
}

impl Volume {
    pub fn restore_begin_v4(
        path: impl AsRef<Path>,
        raw: &[u8],
        password: Option<&str>,
    ) -> Result<Self> {
        Self::restore_begin_options(
            path,
            raw,
            password,
            serde_json::json!({"mode":"copy","lazy":false}),
        )
    }
    pub fn lazy_begin(
        path: impl AsRef<Path>,
        raw: &[u8],
        password: Option<&str>,
        backing: Value,
    ) -> Result<Self> {
        Self::restore_begin_options(
            path,
            raw,
            password,
            serde_json::json!({"mode":"copy","lazy":true,"backing":backing}),
        )
    }
    pub fn restore_begin_options(
        path: impl AsRef<Path>,
        raw: &[u8],
        password: Option<&str>,
        options: Value,
    ) -> Result<Self> {
        let mode = options["mode"].as_str().unwrap_or("copy");
        if !matches!(mode, "copy" | "original") {
            return Err(invalid("restore mode must be copy or original"));
        }
        let lazy = options["lazy"].as_bool().unwrap_or(false);
        let backing = options.get("backing").filter(|v| !v.is_null()).cloned();
        let publication = options.get("publication").filter(|v| !v.is_null()).cloned();
        if lazy && backing.is_none() {
            return Err(invalid("lazy restore requires pinned backing"));
        }
        if (mode == "original") != publication.is_some() {
            return Err(invalid(
                "original mode requires its confirmed cloud publication",
            ));
        }

        let g = Geometry::new(raw.len() as u64)?;
        let source = store::public_config(raw)?;
        if source.format_version != 4
            || source.capacity_bytes < 64 << 20
            || source.capacity_bytes > MAX_CAPACITY
            || source.capacity_bytes % 512 != 0
        {
            return Err(integrity("root configuration"));
        }
        let crypto = source.unlock(password)?;
        let address = u64::from_le_bytes(raw[24..32].try_into().unwrap());
        if address & PORTABLE == 0 || address % g.object_size != 0 {
            return Err(integrity("root header address"));
        }
        let root_oid = (address & !PORTABLE) / g.object_size;
        let header = store::decode_header(&crypto, root_oid, raw)?;
        let root_id = Uuid::parse_str(
            header["id"]
                .as_str()
                .ok_or_else(|| integrity("root object identity"))?,
        )
        .map_err(|_| integrity("root object identity"))?;
        if oid(root_id)? != root_oid
            || header["kind"] != 2
            || header["used"]
                .as_u64()
                .is_none_or(|n| n == 0 || n > g.slots)
            || header["config"] != serde_json::to_value(&source)?
        {
            return Err(integrity("root authenticated configuration"));
        }
        let descriptor: Value = serde_json::from_slice(
            &crypto.unframe(
                codec::ROOT,
                PORTABLE | (root_oid * g.object_size + g.payload_pages * PAGE as u64),
                raw[g.header_bytes()..(g.payload_pages as usize + 1) * PAGE]
                    .try_into()
                    .unwrap(),
            )?,
        )?;
        if descriptor["format_version"] != 4
            || descriptor["container_id"] != source.id.to_string()
            || descriptor["crypto_id"] != crypto.id.to_string()
            || descriptor["capacity_bytes"].as_u64() != Some(source.capacity_bytes)
            || descriptor["page_size"] != PAGE
            || descriptor["object_size"] != g.object_size
        {
            return Err(integrity("root descriptor identity"));
        }
        let index: MetaRef = serde_json::from_value(descriptor["index"].clone())?;
        let generation = descriptor["generation"]
            .as_u64()
            .ok_or_else(|| integrity("root generation"))?;
        if let Some(backing) = &backing {
            for key in ["provider_id", "account_id", "remote_root", "reader_pin"] {
                if backing[key].as_str().is_none_or(|v| v.is_empty()) {
                    return Err(invalid("lazy source backing is incomplete"));
                }
            }
            if backing["source_volume_id"] != source.id.to_string()
                || backing["root_object_id"] != root_id.to_string()
                || backing["root_sha256"] != hex(&hash(raw))
            {
                return Err(integrity("lazy backing differs from authenticated root"));
            }
        }
        if let Some(expected) = options.get("source_volume_id") {
            if expected.as_str() != Some(&source.id.to_string()) {
                return Err(integrity(
                    "prechecked source volume differs from authenticated root",
                ));
            }
        }
        if let Some(p) = &publication {
            validate_publication(
                p,
                &source,
                root_id,
                &hex(&hash(raw)),
                generation,
                backing.as_ref(),
            )?;
        }
        let index_level = descriptor["index_depth"]
            .as_u64()
            .ok_or_else(|| integrity("root index depth missing"))?;
        if !(4..=5).contains(&index_level) {
            return Err(integrity("restore root tree depth"));
        }
        let table = store::external_table(&crypto, root_oid, raw)?;
        let mut s = if path.as_ref().exists() {
            let opened = Self::open(path.as_ref(), password)?;
            let store = opened.shared.store.lock().map_err(|_| Error::Poisoned)?;
            if !empty_bootstrap(&store)
                || store.config.capacity_bytes != source.capacity_bytes
                || store.config.object_size != source.object_size
                || store.crypto.id != crypto.id
                || store.config.encrypted != source.encrypted
                || store.config.lazy != lazy
                || (mode == "original"
                    && (store.config.id != source.id || !store.config.direct_base))
                || (mode == "copy" && store.config.id == source.id)
            {
                return Err(invalid(
                    "existing target is not an authenticated empty cloud-restore bootstrap",
                ));
            }
            drop(store);
            return seed_cloud(
                opened,
                raw,
                root_id,
                index,
                generation,
                &table,
                source.id,
                if lazy { backing } else { None },
                mode,
                publication,
                index_level as u8,
            );
        } else {
            let mut target_config = source.clone();
            target_config.lazy = lazy;
            target_config.direct_base = true;
            let config = target_config.fork_identity(
                &crypto,
                password,
                if mode == "original" {
                    source.id
                } else {
                    Uuid::new_v4()
                },
            )?;
            Store::create(Device::open(path.as_ref(), true)?, config, crypto)?
        };
        let mut r = initial("cloud", source.id, index, generation);
        r.lazy = lazy;
        r.mode = mode.into();
        r.publication = publication;
        r.root_id = root_id.to_string();
        r.root_sha256 = hex(&hash(raw));
        r.received = 1;
        r.total = 1;
        let mut c = s.cloud.clone();
        c.lazy_backing = if lazy { backing } else { None };
        if let Some(p) = &r.publication {
            c.binding = Some(p["binding"].clone());
        }
        s.transaction(|tx| {
            let root = tx.import_object(root_id, hash(raw), raw)?;
            tx.cloud_deltas.insert(root.oid, 1);
            r.baseline_counts = tx.apply_cloud_deltas(r.baseline_counts)?;
            r.processed = tx.set(&SET, r.processed, &[(root_oid, Some(vec![1]))])?;
            if index.empty() {
                r.phase = "ready".into();
            } else {
                let work = reference_work(index, index_level as u8, 0, u64::MAX, &root, &table, g)?;
                push(tx, &mut r, &[work])?;
            }
            c.restore = Some(r.clone());
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        let imported = s.object(root_oid)?;
        cache_verified(&mut s, &imported, raw)?;
        Self::from_store(s)
    }
    pub fn restore_accept(&self, object: &str, raw: &[u8]) -> Result<()> {
        let g = self.geometry();
        if raw.len() != g.object_size as usize {
            return Err(invalid(
                "restore object length differs from volume geometry",
            ));
        }
        let id = Uuid::parse_str(object).map_err(|_| invalid("invalid restore object ID"))?;
        let object_oid = oid(id)?;
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        s.check()?;
        let mut r = restore_state(&s)?;
        if r.kind != "cloud" || !s.root.restore_required {
            return Err(invalid("not an incomplete cloud restore"));
        }
        let processed = r.processed;
        if tree::get(&mut *s, &SET, processed, object_oid)?.is_some() {
            let o = s.object(object_oid)?;
            if o.id != id || hash(raw) != o.sha {
                return Err(integrity("duplicate restore object differs"));
            }
            return Ok(());
        }
        let expected = pending(&mut s, &r, 16)?
            .into_iter()
            .find(|n| n.id == object)
            .ok_or_else(|| {
                invalid("object was not requested by the authenticated restore frontier")
            })?;
        let digest = store::parse_hex::<32>(&expected.sha256)?;
        if hash(raw) != digest {
            return Err(integrity("downloaded restore object checksum"));
        }
        let header = store::decode_header(&s.crypto, object_oid, raw)?;
        if header["id"] != id.to_string()
            || header["kind"] != if expected.kind == "data" { 1 } else { 2 }
        {
            return Err(integrity("restore object kind or identity"));
        }
        store::external_table(&s.crypto, object_oid, raw)?;
        let quick = s.cloud.replica.is_some();
        if quick { s.validate_remote_identity(id,digest)?; s.validate_remote_dependencies(raw,object_oid)?; }
        if expected.kind == "data" {
            verify_base_data(&mut s, object_oid, raw, &header)?;
        }
        let mut c = s.cloud.clone();
        s.transaction(|tx| {
            if expected.kind == "data" {
                tx.hydrate_object(object_oid, raw, &header)?;
            } else {
                tx.import_object(id, digest, raw)?;
            }
            if quick { tx.register_remote(id,digest)?; tx.register_dependencies(raw,object_oid)?; }
            r.processed = tx.set(&SET, r.processed, &[(object_oid, Some(vec![1]))])?;
            r.received += 1;
            r.total = r.total.max(r.received);
            c.restore = Some(r.clone());
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        if expected.kind == "metadata" {
            let imported = s.object(object_oid)?;
            cache_verified(&mut s, &imported, raw)?;
        }
        Ok(())
    }
    pub fn restore_snapshot_begin_v4(
        source: &Volume,
        snapshot: &str,
        path: impl AsRef<Path>,
        password: Option<&str>,
    ) -> Result<Self> {
        let (source_id, capacity, object_size) = {
            let s = source.shared.store.lock().map_err(|_| Error::Poisoned)?;
            s.check()?;
            if s.root.restore_required {
                return Err(invalid("source restore is incomplete"));
            }
            (s.config.id, s.config.capacity_bytes, s.object_size())
        };
        let target = if path.as_ref().exists() {
            let target = Self::open(path.as_ref(), password)?;
            let s = target.shared.store.lock().map_err(|_| Error::Poisoned)?;
            if !empty_bootstrap(&s)
                || s.config.capacity_bytes != capacity
                || s.object_size() != object_size
            {
                return Err(invalid(
                    "existing target is not an authenticated empty local-restore bootstrap",
                ));
            }
            drop(s);
            target
        } else {
            let (mut config, crypto) = Config::create_sized(capacity, password, object_size)?;
            config.restoring = true;
            Self::from_store(Store::create(
                Device::open(path.as_ref(), true)?,
                config,
                crypto,
            )?)?
        };
        let target_id = target.info()?.id;
        let pin = {
            let mut s = source.shared.store.lock().map_err(|_| Error::Poisoned)?;
            let existing: Vec<_> = s
                .snapshots
                .iter()
                .filter(|p| p.pin == "restore" && p.target_volume_id == Some(target_id))
                .cloned()
                .collect();
            if existing.len() > 1 {
                return Err(integrity("duplicate restore source pins"));
            }
            if let Some(p) = existing.into_iter().next() {
                p
            } else {
                let original = s
                    .snapshots
                    .iter()
                    .find(|p| p.id == snapshot && p.pin == "user")
                    .cloned()
                    .ok_or_else(|| invalid("source snapshot not found"))?;
                let pin = Snapshot {
                    id: Uuid::new_v4().to_string(),
                    name: "Local restore".into(),
                    pin: "restore".into(),
                    target_volume_id: Some(target_id),
                    ..original
                };
                let mut snapshots = s.snapshots.clone();
                snapshots.push(pin.clone());
                s.transaction(|tx| {
                    tx.replace(&PAGES, MetaRef::default(), pin.index);
                    tx.save_snapshots(&snapshots)
                })?;
                s.snapshots = snapshots;
                pin
            }
        };
        let total = {
            let mut s = source.shared.store.lock().map_err(|_| Error::Poisoned)?;
            tree::len(&mut *s, &PAGES, pin.index)?
        };
        let mut r = initial("local", source_id, pin.index, pin.generation);
        r.source_snapshot_id = Some(snapshot.into());
        r.source_pin = Some(pin.id);
        r.expected_pages = total;
        {
            let mut s = target.shared.store.lock().map_err(|_| Error::Poisoned)?;
            save(&mut s, r)?;
        }
        *target
            .shared
            .restore_source
            .lock()
            .map_err(|_| Error::Poisoned)? = Some(source.shared.clone());
        Ok(target)
    }
    pub fn restore_snapshot_resume_v4(&self, source: &Volume) -> Result<()> {
        let (target_id, mut r, bootstrap) = {
            let s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            (s.config.id, s.cloud.restore.clone(), empty_bootstrap(&s))
        };
        let source_id = source.info()?.id;
        if bootstrap {
            let (pin, total) = {
                let mut s = source.shared.store.lock().map_err(|_| Error::Poisoned)?;
                let pins: Vec<_> = s
                    .snapshots
                    .iter()
                    .filter(|p| p.pin == "restore" && p.target_volume_id == Some(target_id))
                    .cloned()
                    .collect();
                if pins.len() != 1 {
                    return Err(invalid("bootstrap needs its original snapshot identity"));
                }
                let pin = pins[0].clone();
                let total = tree::len(&mut *s, &PAGES, pin.index)?;
                (pin, total)
            };
            let mut initialized = initial("local", source_id, pin.index, pin.generation);
            initialized.source_pin = Some(pin.id);
            initialized.expected_pages = total;
            let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            save(&mut s, initialized.clone())?;
            r = Some(initialized);
        }
        let r = r.ok_or_else(|| invalid("not a local restore"))?;
        if r.kind != "local"
            || r.source_volume_id != source_id
            || self.object_size() != source.object_size()
        {
            return Err(invalid("restore source identity mismatch"));
        }
        if r.phase == "complete" && !r.source_pin_cleanup_pending {
            return Ok(());
        }
        if r.phase != "complete" {
            let s = source.shared.store.lock().map_err(|_| Error::Poisoned)?;
            if !s.snapshots.iter().any(|p| {
                Some(&p.id) == r.source_pin.as_ref()
                    && p.target_volume_id == Some(target_id)
                    && p.index == r.index
                    && p.pin == "restore"
            }) {
                return Err(integrity("durable restore source pin missing"));
            }
        }
        *self
            .shared
            .restore_source
            .lock()
            .map_err(|_| Error::Poisoned)? = Some(source.shared.clone());
        if r.source_pin_cleanup_pending {
            self.cleanup_source_pin()?;
        }
        Ok(())
    }
    pub(super) fn restore_control(&self, request: &Value) -> Result<Value> {
        let command = request["cmd"]
            .as_str()
            .ok_or_else(|| invalid("restore command missing"))?;
        let attached = self
            .shared
            .restore_source
            .lock()
            .map_err(|_| Error::Poisoned)?
            .is_some();
        if command == "restore.status" {
            let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            return status(
                &mut s,
                number(request, "limit", 128).clamp(1, 128) as usize,
                attached,
            );
        }
        if command == "restore.finish" {
            self.finish_restore()?;
            let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            return status(&mut s, 16, attached);
        }
        let kind = {
            let s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            restore_state(&s)?.kind
        };
        let limit = number(request, "max_pages", 128).clamp(1, 256) as usize;
        match (command, kind.as_str()) {
            ("restore.step", "cloud") => {
                if !self.quick_restore_step()? { self.step_base(
                    number(request, "max_nodes", number(request, "max_pages", 64)).clamp(1, 256)
                        as usize,
                    number(request, "max_objects", 2).clamp(1, 4) as usize,
                )?; }
            }
            ("snapshot.restore_step", "local") | ("restore.step", "local") => {
                self.step_local(limit)?
            }
            _ => return Err(invalid("unsupported restore operation")),
        }
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        status(&mut s, 16, attached)
    }
    fn step_local(&self, limit: usize) -> Result<()> {
        let source = self
            .shared
            .restore_source
            .lock()
            .map_err(|_| Error::Poisoned)?
            .clone()
            .ok_or_else(|| invalid("reattach restore source"))?;
        let mut pins = Vec::new();
        loop {
            // A quota worker must not evict a just-hydrated source again before
            // this bounded restore batch has copied it into the independent target.
            let handoff = super::store::ReadLease::acquire(source.readers.clone())?;
            match self.step_local_inner(limit) {
                Err(Error::Missing(object)) => {
                    let id = Uuid::parse_str(&object.id)
                        .map_err(|_| invalid("source object identity"))?;
                    pins.push(source.cache_runtime.pin(oid(id)?));
                    drop(handoff);
                    source.hydrate_missing(&object)?;
                }
                result => return result,
            }
        }
    }
    fn step_local_inner(&self, limit: usize) -> Result<()> {
        let source = self
            .shared
            .restore_source
            .lock()
            .map_err(|_| Error::Poisoned)?
            .clone()
            .ok_or_else(|| invalid("unlock and reattach the original disk to resume"))?;
        let r = {
            let s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            restore_state(&s)?
        };
        if r.phase == "ready" || r.phase == "complete" {
            return Ok(());
        }
        let (_lease, mut reader) = source.read_snapshot(r.index)?;
        let rows = tree::scan_after(&mut reader, &PAGES, r.index, r.cursor, limit)?;
        let mut pages = Vec::with_capacity(rows.len());
        for (lba, _) in &rows {
            pages.push((*lba, reader.read_page(*lba)?));
        }
        drop(reader);
        drop(_lease);
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        let mut current = restore_state(&s)?;
        if current.cursor != r.cursor || current.index != r.index {
            return Err(invalid("restore progress changed; retry step"));
        }
        let mut c = s.cloud.clone();
        s.transaction(|tx| {
            let mut changes = Vec::new();
            let mut dirty = Vec::new();
            for (lba, bytes) in &pages {
                let page = tx.append_page(*lba, bytes, 0)?;
                changes.push((*lba, Some(page.encode())));
                dirty.push((*lba, Some(vec![1])));
            }
            let old = tx.root.index;
            tx.root.index = tx.set(&PAGES, old, &changes)?;
            tx.root.dirty = tx.set(&DIRTY, tx.root.dirty, &dirty)?;
            tx.root.allocated_pages += pages.len() as u64;
            tx.root.changed_pages += pages.len() as u64;
            current.completed_pages += pages.len() as u64;
            if let Some((lba, _)) = pages.last() {
                current.cursor = lba + 1;
            }
            if rows.len() < limit {
                if current.completed_pages != current.expected_pages {
                    return Err(integrity("local snapshot page count mismatch"));
                }
                current.phase = "ready".into();
            }
            c.restore = Some(current.clone());
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        Ok(())
    }
    fn step_base(&self, limit: usize, max_objects: usize) -> Result<()> {
        let g = self.geometry();
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        s.check()?;
        let mut r = restore_state(&s)?;
        if matches!(r.phase.as_str(), "ready" | "complete") {
            return Ok(());
        }
        if r.phase == "data" {
            let root = s.root.objects;
            let rows = tree::scan_after(&mut *s, &OBJECTS, root, r.data_cursor, 128)?;
            let mut blocked = false;
            for (id, v) in &rows {
                let o = store::Object::decode(*id, v)?;
                if o.kind == 1 && o.missing && o.refs > 0 {
                    blocked = true;
                    break;
                }
                r.data_cursor = id + 1;
            }
            if !blocked && rows.len() < 128 {
                r.phase = "ready".into();
            }
            return save(&mut s, r);
        }
        if r.queue_tail != 0 {
            let row = tree::get(&mut *s, &QUEUE, r.queue, r.queue_tail - 1)?
                .ok_or_else(|| integrity("base metadata stack missing"))?;
            if !imported(&mut s, &Work::decode(&row)?)? {
                return Ok(());
            }
        }
        let mut c = s.cloud.clone();
        s.transaction(|tx| {
            let mut queue_edits: BTreeMap<u64, Option<Vec<u8>>> = BTreeMap::new();
            let mut reference_edits: BTreeMap<u64, Option<Vec<u8>>> = BTreeMap::new();
            let mut object_reads = 0;
            for _ in 0..limit {
                if r.queue_tail == 0 {
                    break;
                }
                let row = match queue_edits.get(&(r.queue_tail - 1)) {
                    Some(v) => v.clone(),
                    None => tree::get(tx, &QUEUE, r.queue, r.queue_tail - 1)?,
                }
                .ok_or_else(|| integrity("base metadata stack missing"))?;
                let work = Work::decode(&row)?;
                if tree::get(tx, &OBJECTS, tx.root.objects, oid(work.id)?)?.is_none() {
                    break;
                }
                let object = tx.object(oid(work.id)?)?;
                if object.id != work.id
                    || object.sha != work.sha
                    || !object.sealed
                    || object.kind != 2
                {
                    return Err(integrity("base metadata identity"));
                }
                if !tx
                    .store
                    .restore_cache
                    .iter()
                    .any(|c| c.id == object.id && c.sha == object.sha)
                {
                    if object_reads >= max_objects {
                        break;
                    }
                    object_reads += 1;
                }
                let cached = cached_object(tx.store, &object)?;
                let start = ((work.node.offset & !PORTABLE) % g.object_size) as usize;
                if start / PAGE < g.payload_pages as usize + 1
                    || start / PAGE >= g.payload_pages as usize + object.used as usize
                {
                    return Err(integrity("base node slot"));
                }
                let frame: &[u8; PAGE] = cached.raw[start..start + PAGE].try_into().unwrap();
                if hash(frame) != work.node.hash {
                    return Err(integrity("base node checksum"));
                }
                let node = tree::decode(
                    &PORTMAP,
                    &tx.store
                        .crypto
                        .unframe(codec::NODE, work.node.offset, frame)?,
                )?;
                let count = match &node {
                    Node::Branch { counts, .. } => counts.iter().try_fold(0u64, |a, n| {
                        a.checked_add(*n)
                            .ok_or_else(|| integrity("base cardinality overflow"))
                    })?,
                    Node::Leaf { values, .. } => values.len() as u64,
                };
                if work.count != u64::MAX && count != work.count {
                    return Err(integrity("base subtree cardinality"));
                }
                if work.count == u64::MAX {
                    r.expected_pages = count;
                    if count > tx.store.config.capacity_bytes / PAGE as u64 {
                        return Err(integrity("base cardinality exceeds disk"));
                    }
                }
                let mut children = Vec::new();
                match &node {
                    Node::Branch {
                        level,
                        base,
                        children: refs,
                        counts,
                    } => {
                        if *level != work.level || *base != work.base || *level == 0 {
                            return Err(integrity("base tree hierarchy"));
                        }
                        let width = 1u64 << (PORTMAP.leaf_bits as u32 + 6 * (*level - 1) as u32);
                        for ((slot, reference), count) in refs.iter().zip(counts) {
                            children.push(reference_work(
                                *reference,
                                *level - 1,
                                base + *slot as u64 * width,
                                *count,
                                &object,
                                &cached.table,
                                g,
                            )?);
                        }
                    }
                    Node::Leaf { base, values } => {
                        if work.level != 0 || *base != work.base {
                            return Err(integrity("base leaf range"));
                        }
                        for (slot, value) in values {
                            let p = StoredPage::decode(value)?;
                            let lba = base + *slot as u64;
                            if lba >= tx.store.config.capacity_bytes / PAGE as u64
                                || p.reference.slot >= g.slots
                                || p.reference.object_generation != 0
                            {
                                return Err(integrity("base page reference"));
                            }
                            let (id, sha) = *cached
                                .table
                                .get(&p.reference.object)
                                .ok_or_else(|| integrity("base data dependency missing"))?;
                            if oid(id)? != p.reference.object {
                                return Err(integrity("base data identity"));
                            }
                            let mut data =
                                tx.remote_object(id, sha, (p.reference.slot + 1) as u16)?;
                            if r.mode == "original" {
                                data.sync_state = 3;
                                tx.objects.insert(data.oid, data);
                            }
                        }
                        r.completed_pages += values.len() as u64;
                    }
                }
                tx.adopt_portable_node(work.node, &node, &mut reference_edits)?;
                if r.mode == "original" {
                    let mut object = tx.object(object.oid)?;
                    object.sync_state = 3;
                    tx.objects.insert(object.oid, object);
                }
                r.verified_nodes += 1;
                r.queue_tail -= 1;
                queue_edits.insert(r.queue_tail, None);
                for child in children.iter().rev() {
                    if r.queue_tail >= 4096 {
                        return Err(integrity("base metadata traversal bound"));
                    }
                    queue_edits.insert(r.queue_tail, Some(child.encode()));
                    r.queue_tail += 1;
                }
            }
            r.queue = tx.set(
                &QUEUE,
                r.queue,
                &queue_edits.into_iter().collect::<Vec<_>>(),
            )?;
            tx.root.portable_refs = tx.set(
                &store::PORTREFS,
                tx.root.portable_refs,
                &reference_edits.into_iter().collect::<Vec<_>>(),
            )?;
            r.baseline_counts = tx.apply_cloud_deltas(r.baseline_counts)?;
            if r.queue_tail == 0 {
                if r.completed_pages != r.expected_pages {
                    return Err(integrity("base page cardinality differs from root"));
                }
                tx.root.index = r.index;
                tx.root.allocated_pages = r.completed_pages;
                r.phase = if r.lazy { "ready" } else { "data" }.into();
            }
            c.restore = Some(r.clone());
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        Ok(())
    }
    fn finish_restore(&self) -> Result<()> {
        if self.quick_restore_finish()? { return Ok(()); }
        {
            let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            let mut r = restore_state(&s)?;
            if r.phase != "ready" && r.phase != "complete" {
                return Err(invalid("restore has not verified every referenced page"));
            }
            if r.phase != "complete" {
                if r.queue_tail != 0 || r.completed_pages != r.expected_pages {
                    return Err(integrity("restore completion invariants"));
                }
                r.phase = "complete".into();
                r.source_pin_cleanup_pending = r.kind == "local" && r.source_pin.is_some();
                let processed = r.processed;
                r.processed = MetaRef::default();
                let mut c = s.cloud.clone();
                c.restore = Some(r.clone());
                s.transaction(|tx| {
                    tx.replace(&SET, processed, MetaRef::default());
                    tx.root.restore_required = false;
                    tx.root.data_generation = if r.mode=="original"{r.generation}else{r.generation.max(1)};
                    if r.kind=="cloud" {
                        tx.root.index=r.index;
                        tx.root.allocated_pages=r.completed_pages;
                        tx.root.changed_pages=0;
                        if r.mode=="original" {
                            let p=r.publication.as_ref().ok_or_else(||invalid("original publication missing"))?;
                            c.binding=Some(p["binding"].clone());c.published_generation=r.generation;c.published_index=r.index;c.published_counts=r.baseline_counts;c.published_root=oid(Uuid::parse_str(&r.root_id).map_err(|_|integrity("restore root id"))?)?;
                            c.published_commit=Some(json!({"root_object_id":r.root_id,"root_sha256":r.root_sha256,"root_slot":0,"generation":r.generation,"receipt":"original-cloud-publication"}));
                        } else {c.base_index=r.index;c.base_counts=r.baseline_counts;c.base_root=oid(Uuid::parse_str(&r.root_id).map_err(|_|integrity("restore root id"))?)?;}
                    }
                    tx.save_cloud(&c)
                })?;
                s.cloud = c;
                s.restore_cache.clear();
            }
        }
        self.cleanup_source_pin()
    }
    fn cleanup_source_pin(&self) -> Result<()> {
        let (target_id, r) = {
            let s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            (s.config.id, restore_state(&s)?)
        };
        if !r.source_pin_cleanup_pending {
            return Ok(());
        }
        let source = self
            .shared
            .restore_source
            .lock()
            .map_err(|_| Error::Poisoned)?
            .clone()
            .ok_or_else(|| {
                invalid("restore is complete; unlock the source to release its durable pin")
            })?;
        {
            let mut s = source.store.lock().map_err(|_| Error::Poisoned)?;
            if s.config.id != r.source_volume_id {
                return Err(integrity("cleanup source identity"));
            }
            let mut snapshots = s.snapshots.clone();
            let removed: Vec<_> = snapshots
                .iter()
                .filter(|p| {
                    Some(&p.id) == r.source_pin.as_ref()
                        && p.target_volume_id == Some(target_id)
                        && p.pin == "restore"
                })
                .cloned()
                .collect();
            snapshots.retain(|p| !removed.iter().any(|q| q.id == p.id));
            if !removed.is_empty() {
                s.transaction(|tx| {
                    for p in &removed {
                        tx.replace(&PAGES, p.index, MetaRef::default());
                    }
                    tx.save_snapshots(&snapshots)
                })?;
                s.snapshots = snapshots;
            }
        }
        {
            let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            let mut current = restore_state(&s)?;
            current.source_pin_cleanup_pending = false;
            save(&mut s, current)?;
        }
        *self
            .shared
            .restore_source
            .lock()
            .map_err(|_| Error::Poisoned)? = None;
        Ok(())
    }
}
#[allow(clippy::too_many_arguments)] // Authenticated bootstrap inputs, shared by full and lazy restore.
fn seed_cloud(
    target: Volume,
    raw: &[u8],
    root_id: Uuid,
    index: MetaRef,
    generation: u64,
    table: &BTreeMap<u64, (Uuid, [u8; 32])>,
    source_id: Uuid,
    backing: Option<Value>,
    mode: &str,
    publication: Option<Value>,
    index_level: u8,
) -> Result<Volume> {
    let geometry = target.geometry();
    {
        let mut s = target.shared.store.lock().map_err(|_| Error::Poisoned)?;
        let mut r = initial("cloud", source_id, index, generation);
        r.lazy = backing.is_some();
        r.mode = mode.into();
        r.publication = publication;
        r.root_id = root_id.to_string();
        r.root_sha256 = hex(&hash(raw));
        r.received = 1;
        r.total = 1;
        let mut c = s.cloud.clone();
        c.lazy_backing = backing;
        if let Some(p) = &r.publication {
            c.binding = Some(p["binding"].clone());
        }
        s.transaction(|tx| {
            let root = tx.import_object(root_id, hash(raw), raw)?;
            tx.cloud_deltas.insert(root.oid, 1);
            r.baseline_counts = tx.apply_cloud_deltas(r.baseline_counts)?;
            r.processed = tx.set(&SET, r.processed, &[(oid(root_id)?, Some(vec![1]))])?;
            if index.empty() {
                r.phase = "ready".into();
            } else {
                push(
                    tx,
                    &mut r,
                    &[reference_work(
                        index,
                        index_level,
                        0,
                        u64::MAX,
                        &root,
                        table,
                        geometry,
                    )?],
                )?;
            }
            c.restore = Some(r);
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        let imported = s.object(oid(root_id)?)?;
        cache_verified(&mut s, &imported, raw)?;
    }
    Ok(target)
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;
    fn source(directory: &TempDir, password: Option<&str>) -> (Volume, Vec<(u64, Vec<u8>)>) {
        let path = directory.path().join("source.odv4");
        Volume::create(&path, 64 << 20, password).unwrap();
        let volume = Volume::open(path, password).unwrap();
        let pages = vec![
            (0, vec![3; PAGE]),
            (33 * PAGE as u64, vec![17; PAGE]),
            (1200 * PAGE as u64, vec![29; PAGE]),
        ];
        for (offset, bytes) in &pages {
            volume.write(*offset, bytes).unwrap();
        }
        volume.flush().unwrap();
        (volume, pages)
    }
    fn cloud_job(source: &Volume) -> (String, Vec<u8>) {
        let mut value = source.control(&json!({"cmd":"cloud.prepare"})).unwrap();
        let id = value["job"]["id"].as_str().unwrap().to_string();
        for _ in 0..256 {
            if value["job"]["phase"] == "ready" {
                break;
            }
            value = source
                .control(&json!({"cmd":"cloud.prepare","job_id":id,"max_pages":32,"max_objects":2}))
                .unwrap();
        }
        assert_eq!(value["job"]["phase"], "ready");
        let root_id = value["job"]["root_object_id"].as_str().unwrap();
        let mut root = vec![0; OBJECT as usize];
        source.read_export(&id, root_id, 0, &mut root).unwrap();
        (id, root)
    }
    fn readback(target: &Volume, pages: &[(u64, Vec<u8>)]) {
        for (offset, expected) in pages {
            let mut actual = vec![0; expected.len()];
            target.read(*offset, &mut actual).unwrap();
            assert_eq!(&actual, expected);
        }
    }
    #[test]
    fn cloud_restore_verifies_objects_reopens_each_step_and_preserves_crypto_identity() {
        let dir = tempfile::tempdir().unwrap();
        let (source, pages) = source(&dir, Some("cloud-password"));
        let (job, root) = cloud_job(&source);
        let path = dir.path().join("restored.odv4");
        assert!(Volume::restore_begin_v4(&path, &root, Some("wrong")).is_err());
        assert!(!path.exists());
        let mut broken_root = root.clone();
        broken_root[17 * PAGE - 1000] ^= 1;
        assert!(Volume::restore_begin_v4(&path, &broken_root, Some("cloud-password")).is_err());
        assert!(!path.exists());
        let mut target = Volume::restore_begin_v4(&path, &root, Some("cloud-password")).unwrap();
        let target_id = target.info().unwrap().id;
        assert_ne!(target_id, source.info().unwrap().id);
        let mut page = vec![0; PAGE];
        assert!(target.read(0, &mut page).is_err());
        let mut tested_corruption = false;
        let mut ready = false;
        for _ in 0..128 {
            let status = target
                .control(&json!({"cmd":"restore.status","limit":128}))
                .unwrap();
            if status["phase"] == "ready" {
                ready = true;
                break;
            }
            let needs = status["needed"].as_array().unwrap();
            if needs.is_empty() {
                target
                    .control(&json!({"cmd":"restore.step","max_pages":1}))
                    .unwrap();
            } else {
                for need in needs {
                    let id = need["id"].as_str().unwrap();
                    let mut raw = vec![0; OBJECT as usize];
                    source.read_export(&job, id, 0, &mut raw).unwrap();
                    if !tested_corruption {
                        let mut broken = raw.clone();
                        broken[18 * PAGE + 17] ^= 1;
                        assert!(target.restore_accept(id, &broken).is_err());
                        tested_corruption = true;
                    }
                    target.restore_accept(id, &raw).unwrap();
                    target.restore_accept(id, &raw).unwrap();
                }
            }
            drop(target);
            target = Volume::open(&path, Some("cloud-password")).unwrap();
            assert_eq!(target.info().unwrap().id, target_id);
            assert!(target.info().unwrap().restore_incomplete);
        }
        assert!(ready && tested_corruption);
        assert!(target.read(0, &mut page).is_err());
        target.control(&json!({"cmd":"restore.finish"})).unwrap();
        assert!(!target.info().unwrap().restore_incomplete);
        readback(&target, &pages);
        drop(target);
        let target = Volume::open(&path, Some("cloud-password")).unwrap();
        readback(&target, &pages);
        assert!(Volume::restore_begin_v4(&path, &root, Some("cloud-password")).is_err());
        readback(&target, &pages);
    }
    #[test]
    fn cloud_bootstrap_reseeds_same_target_without_overwriting_normal_disks() {
        let dir = tempfile::tempdir().unwrap();
        let (source, _) = source(&dir, None);
        let (_, root) = cloud_job(&source);
        let config = store::public_config(&root).unwrap();
        let crypto = config.unlock(None).unwrap();
        let fork = config.fork(&crypto, None).unwrap();
        let id = fork.id;
        let path = dir.path().join("bootstrap.odv4");
        drop(Store::create(Device::open(&path, true).unwrap(), fork, crypto).unwrap());
        let target = Volume::restore_begin_v4(&path, &root, None).unwrap();
        assert_eq!(target.info().unwrap().id, id);
        assert!(target.info().unwrap().restore_incomplete);
        drop(target);
        let normal = dir.path().join("normal.odv4");
        Volume::create(&normal, 64 << 20, None).unwrap();
        assert!(Volume::restore_begin_v4(&normal, &root, None).is_err());
        assert!(
            !Volume::open(&normal, None)
                .unwrap()
                .info()
                .unwrap()
                .restore_incomplete
        );
    }
    #[test]
    fn local_restore_reopens_with_an_independent_key_and_retries_source_pin_cleanup() {
        let dir = tempfile::tempdir().unwrap();
        let (source, pages) = source(&dir, Some("source-password"));
        let snapshot = source.snapshot_create().unwrap();
        let path = dir.path().join("local.odv4");
        let mut target =
            Volume::restore_snapshot_begin_v4(&source, &snapshot, &path, Some("target-password"))
                .unwrap();
        let id = target.info().unwrap().id;
        target
            .control(&json!({"cmd":"snapshot.restore_step","max_pages":1}))
            .unwrap();
        drop(target);
        source.snapshot_release(&snapshot).unwrap();
        target = Volume::open(&path, Some("target-password")).unwrap();
        assert_eq!(
            target.control(&json!({"cmd":"restore.status"})).unwrap()["phase"],
            "needs_source"
        );
        target.restore_snapshot_resume_v4(&source).unwrap();
        for _ in 0..16 {
            let status = target
                .control(&json!({"cmd":"snapshot.restore_step","max_pages":1}))
                .unwrap();
            if status["phase"] == "ready" {
                break;
            }
        }
        assert_eq!(
            target.control(&json!({"cmd":"restore.status"})).unwrap()["phase"],
            "ready"
        );
        // Simulate the durable target-complete / source-unpin crash boundary.
        {
            let mut s = target.shared.store.lock().unwrap();
            let mut r = restore_state(&s).unwrap();
            r.phase = "complete".into();
            r.source_pin_cleanup_pending = true;
            let mut c = s.cloud.clone();
            c.restore = Some(r);
            s.transaction(|tx| {
                tx.root.restore_required = false;
                tx.save_cloud(&c)
            })
            .unwrap();
            s.cloud = c;
        }
        drop(target);
        target = Volume::open(&path, Some("target-password")).unwrap();
        assert_eq!(target.info().unwrap().id, id);
        assert!(target.control(&json!({"cmd":"restore.status"})).unwrap()
            ["source_pin_cleanup_pending"]
            .as_bool()
            .unwrap());
        target.restore_snapshot_resume_v4(&source).unwrap();
        target.restore_snapshot_resume_v4(&source).unwrap();
        assert_eq!(
            target.control(&json!({"cmd":"restore.status"})).unwrap()["source_pin_cleanup_pending"],
            false
        );
        assert!(source
            .shared
            .store
            .lock()
            .unwrap()
            .snapshots
            .iter()
            .all(|s| s.pin != "restore"));
        readback(&target, &pages);
        drop(target);
        assert!(Volume::open(&path, Some("source-password")).is_err());
        readback(
            &Volume::open(&path, Some("target-password")).unwrap(),
            &pages,
        );
    }
    #[test]
    fn local_bootstrap_reuses_durable_pin_after_initialization_interruption() {
        let dir = tempfile::tempdir().unwrap();
        let (source, pages) = source(&dir, None);
        let snapshot = source.snapshot_create().unwrap();
        let path = dir.path().join("local-bootstrap.odv4");
        let (mut config, crypto) = Config::create(64 << 20, None).unwrap();
        config.restoring = true;
        let target_id = config.id;
        drop(Store::create(Device::open(&path, true).unwrap(), config, crypto).unwrap());
        // Source pin was committed, but target restore checkpoint was not.
        {
            let mut s = source.shared.store.lock().unwrap();
            let original = s
                .snapshots
                .iter()
                .find(|p| p.id == snapshot)
                .unwrap()
                .clone();
            let pin = Snapshot {
                id: Uuid::new_v4().to_string(),
                pin: "restore".into(),
                target_volume_id: Some(target_id),
                ..original
            };
            let mut list = s.snapshots.clone();
            list.push(pin.clone());
            s.transaction(|tx| {
                tx.replace(&PAGES, MetaRef::default(), pin.index);
                tx.save_snapshots(&list)
            })
            .unwrap();
            s.snapshots = list;
        }
        source.snapshot_release(&snapshot).unwrap();
        let target = Volume::restore_snapshot_begin_v4(&source, &snapshot, &path, None).unwrap();
        assert_eq!(target.info().unwrap().id, target_id);
        for _ in 0..8 {
            if target
                .control(&json!({"cmd":"snapshot.restore_step","max_pages":2}))
                .unwrap()["phase"]
                == "ready"
            {
                break;
            }
        }
        target.control(&json!({"cmd":"restore.finish"})).unwrap();
        readback(&target, &pages);
        assert!(source.shared.store.lock().unwrap().snapshots.is_empty());
    }
    fn finish_cloud(source: &Volume, job: &str, target: &Volume) {
        for _ in 0..128 {
            let status = target.control(&json!({"cmd":"restore.status"})).unwrap();
            if status["phase"] == "ready" {
                target.control(&json!({"cmd":"restore.finish"})).unwrap();
                return;
            }
            let needs = status["needed"].as_array().unwrap();
            if needs.is_empty() {
                target
                    .control(&json!({"cmd":"restore.step","max_pages":128}))
                    .unwrap();
            } else {
                for n in needs {
                    let id = n["id"].as_str().unwrap();
                    let mut raw = vec![0; OBJECT as usize];
                    source.read_export(job, id, 0, &mut raw).unwrap();
                    target.restore_accept(id, &raw).unwrap();
                }
            }
        }
        panic!("restore did not complete in its bounded fixture");
    }
    #[test]
    fn cloud_restore_failed_write_preserves_frontier_for_retry() {
        let dir = tempfile::tempdir().unwrap();
        let (source, pages) = source(&dir, None);
        let (job, root) = cloud_job(&source);
        let path = dir.path().join("write-failure.odv4");
        let target = Volume::restore_begin_v4(&path, &root, None).unwrap();
        target
            .control(&json!({"cmd":"restore.step","max_pages":128}))
            .unwrap();
        let status = target.control(&json!({"cmd":"restore.status"})).unwrap();
        let id = status["needed"][0]["id"].as_str().unwrap();
        let mut raw = vec![0; OBJECT as usize];
        source.read_export(&job, id, 0, &mut raw).unwrap();
        // Object bytes arrive, but the next checkpoint write fails before any receipt/root is committed.
        target.shared.device.fail_after.store(1, Ordering::Relaxed);
        assert!(target.restore_accept(id, &raw).is_err());
        drop(target);
        let target = Volume::open(&path, None).unwrap();
        let resumed = target.control(&json!({"cmd":"restore.status"})).unwrap();
        assert!(resumed["needed"]
            .as_array()
            .unwrap()
            .iter()
            .any(|n| n["id"] == id));
        assert!(target.info().unwrap().restore_incomplete);
        finish_cloud(&source, &job, &target);
        readback(&target, &pages);
    }
    #[test]
    #[ignore = "helper process; started only by the owned kill/reopen test"]
    fn restore_kill_child() {
        let Some(directory) = std::env::var_os("OVERLAYDISK_V4_OWNED_RESTORE_CHILD") else {
            return;
        };
        let directory = std::path::PathBuf::from(directory);
        let root = std::fs::read(directory.join("root.bin")).unwrap();
        let target = Volume::restore_begin_v4(directory.join("killed.odv4"), &root, None).unwrap();
        target
            .control(&json!({"cmd":"restore.step","max_pages":1}))
            .unwrap();
        std::fs::write(
            directory.join("durable-marker"),
            target.info().unwrap().id.to_string(),
        )
        .unwrap();
        loop {
            std::thread::sleep(Duration::from_millis(100));
        }
    }
    #[test]
    fn actual_process_kill_keeps_unmountable_checkpoint_and_resumes() {
        struct Child(std::process::Child);
        impl Drop for Child {
            fn drop(&mut self) {
                let _ = self.0.kill();
                let _ = self.0.wait();
            }
        }
        let dir = tempfile::tempdir().unwrap();
        let (source, pages) = source(&dir, None);
        let (job, root) = cloud_job(&source);
        std::fs::write(dir.path().join("root.bin"), root).unwrap();
        let mut child = Child(
            std::process::Command::new(std::env::current_exe().unwrap())
                .args([
                    "--exact",
                    "v4::restore::tests::restore_kill_child",
                    "--ignored",
                    "--nocapture",
                ])
                .env("OVERLAYDISK_V4_OWNED_RESTORE_CHILD", dir.path())
                .stdin(std::process::Stdio::null())
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .spawn()
                .unwrap(),
        );
        let until = std::time::Instant::now() + Duration::from_secs(20);
        let marker = dir.path().join("durable-marker");
        while !marker.exists() {
            assert!(
                std::time::Instant::now() < until,
                "owned child did not reach checkpoint"
            );
            assert!(
                child.0.try_wait().unwrap().is_none(),
                "owned child failed before checkpoint"
            );
            std::thread::sleep(Duration::from_millis(10));
        }
        child.0.kill().unwrap();
        child.0.wait().unwrap();
        let expected = std::fs::read_to_string(marker).unwrap();
        let target = Volume::open(dir.path().join("killed.odv4"), None).unwrap();
        assert_eq!(target.info().unwrap().id.to_string(), expected);
        assert!(target.info().unwrap().restore_incomplete);
        assert!(target.read(0, &mut [0; 512]).is_err());
        finish_cloud(&source, &job, &target);
        readback(&target, &pages);
    }
}
