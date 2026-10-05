//! Format-3 control metadata. Values are object-level records, never a RAM page map.
use super::*;
use chacha20poly1305::{
    aead::{AeadInPlace, KeyInit},
    Tag, XChaCha20Poly1305, XNonce,
};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::collections::BTreeSet;
#[path = "cloud_admin.rs"]
mod admin;
#[path = "cloud_export.rs"]
mod export;
#[path = "cloud_restore.rs"]
mod restore;

const BLOB_LIMIT: usize = 256 * 1024 * 1024;
pub(super) const CHECKPOINT_CHUNK_BYTES: usize = 1024 * 1024;
const CLOUD_MAGIC: &[u8; 8] = b"ODV3OBJ1";
const GROUP_OBJECTS: u64 = 40;
#[derive(Clone, Default)]
pub(super) struct CloudDb {
    pub values: Arc<BTreeMap<String, Value>>,
    pub pending: Vec<CloudEvent>,
    pub log_events: usize,
    /// A lifecycle sweep can remove many keys without a matching RAM tombstone vector.
    pub force_checkpoint: bool,
}
#[derive(Clone, Serialize, Deserialize)]
pub(super) struct CloudEvent {
    pub key: String,
    pub value: Option<Value>,
}
#[derive(Serialize, Deserialize)]
pub(super) struct CloudLog {
    pub previous: MetaRef,
    pub events: Vec<CloudEvent>,
}
#[derive(Serialize, Deserialize)]
pub(super) struct CloudCheckpoint {
    pub checkpoint_version: u32,
    pub previous: MetaRef,
    pub entries: Vec<(String, Value)>,
}
pub(super) struct LocalRestoreSource {
    pub volume: Arc<Volume>,
    pub snapshot: String,
}
impl CloudDb {
    pub(super) fn get(&self, key: &str) -> Value {
        self.values.get(key).cloned().unwrap_or(Value::Null)
    }
    fn set(&mut self, key: impl Into<String>, value: Value) {
        let key = key.into();
        Arc::make_mut(&mut self.values).insert(key.clone(), value.clone());
        self.pending.push(CloudEvent {
            key,
            value: Some(value),
        });
    }
    fn remove(&mut self, key: &str) {
        if Arc::make_mut(&mut self.values).remove(key).is_some() {
            self.pending.push(CloudEvent {
                key: key.into(),
                value: None,
            });
        }
    }
    fn prefix(&self, prefix: &str) -> Vec<(String, Value)> {
        self.prefix_iter(prefix)
            .map(|(k, v)| (k.clone(), v.clone()))
            .collect()
    }
    fn prefix_iter<'a>(
        &'a self,
        prefix: &'a str,
    ) -> impl Iterator<Item = (&'a String, &'a Value)> + 'a {
        self.values
            .range(prefix.to_owned()..)
            .take_while(move |(k, _)| k.starts_with(prefix))
    }
    fn prefix_count(&self, prefix: &str) -> usize {
        self.prefix_iter(prefix).count()
    }
    fn prefix_page(&self, prefix: &str, offset: usize, limit: usize) -> Vec<(String, Value)> {
        self.prefix_iter(prefix)
            .skip(offset)
            .take(limit)
            .map(|(k, v)| (k.clone(), v.clone()))
            .collect()
    }
    pub(super) fn number(&self, key: &str) -> u64 {
        self.values.get(key).and_then(Value::as_u64).unwrap_or(0)
    }
    pub(super) fn restore_incomplete(&self) -> bool {
        let r = self.get("restore");
        !r.is_null() && r["phase"] != "complete"
    }
}
fn string(v: &Value, key: &str) -> Result<String> {
    let s = v[key]
        .as_str()
        .ok_or_else(|| invalid(format!("missing {key}")))?;
    if s.len() > 4096 {
        return Err(invalid("text field too long"));
    }
    Ok(s.into())
}
fn number(v: &Value, key: &str, default: u64) -> u64 {
    v[key].as_u64().unwrap_or(default)
}
fn uuid(v: &Value, key: &str) -> Result<String> {
    let s = string(v, key)?;
    Uuid::parse_str(&s).map_err(|_| invalid("invalid UUID"))?;
    Ok(s)
}
fn digest(bytes: &[u8]) -> String {
    codec::hex(&Sha256::digest(bytes))
}
fn from_hex(s: &str) -> Result<Vec<u8>> {
    if !s.len().is_multiple_of(2)
        || s.len() > 32 * 1024
        || !s.bytes().all(|b| b.is_ascii_hexdigit())
    {
        return Err(invalid("invalid hex length"));
    }
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).map_err(|_| invalid("invalid hex")))
        .collect()
}
fn now() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}
fn image_aad(image: &[u8], start: usize) -> Vec<u8> {
    let mut aad = b"OverlayDisk portable cloud object v1".to_vec();
    aad.extend_from_slice(&image[..80]);
    aad.extend_from_slice(&image[96..start]);
    aad
}
fn encode_image(
    crypto: &Crypto,
    kind: u8,
    id: Uuid,
    payload: &[u8],
    configuration: Option<&Config>,
    nonce: [u8; 24],
) -> Result<Vec<u8>> {
    let start = if kind == 3 { 2 * PAGE } else { PAGE };
    if payload.len() > OBJECT as usize - start {
        return Err(invalid("portable manifest part exceeds 4 MiB"));
    }
    let mut image = vec![0; OBJECT as usize];
    image[..8].copy_from_slice(CLOUD_MAGIC);
    image[8] = kind;
    image[9] = u8::from(crypto.key.is_some());
    image[10..12].copy_from_slice(&1u16.to_le_bytes());
    image[16..32].copy_from_slice(crypto.id.as_bytes());
    image[32..48].copy_from_slice(id.as_bytes());
    image[48..52].copy_from_slice(&(payload.len() as u32).to_le_bytes());
    image[56..80].copy_from_slice(&nonce);
    if kind == 3 {
        image[PAGE..2 * PAGE].copy_from_slice(
            &configuration
                .ok_or_else(|| invalid("commit config absent"))?
                .encode()?,
        );
    }
    image[start..start + payload.len()].copy_from_slice(payload);
    let aad = image_aad(&image, start);
    let body = if kind == 0 {
        &mut [][..]
    } else {
        &mut image[start..]
    };
    let tag: [u8; 16] = if let Some(key) = &crypto.key {
        XChaCha20Poly1305::new_from_slice(key.as_ref())
            .unwrap()
            .encrypt_in_place_detached(XNonce::from_slice(&nonce), &aad, body)
            .map_err(|_| integrity("cloud object encryption"))?
            .into()
    } else {
        let mut h = Sha256::new();
        h.update(&aad);
        h.update(&*body);
        h.finalize()[..16].try_into().unwrap()
    };
    image[80..96].copy_from_slice(&tag);
    Ok(image)
}
fn decode_image(
    crypto: &Crypto,
    image: &[u8],
    expected: Option<Uuid>,
) -> Result<(u8, Uuid, Zeroizing<Vec<u8>>)> {
    if image.len() != OBJECT as usize
        || &image[..8] != CLOUD_MAGIC
        || image[8] > 3
        || image[9] != u8::from(crypto.key.is_some())
        || image[10..12] != 1u16.to_le_bytes()
        || image[16..32] != *crypto.id.as_bytes()
    {
        return Err(integrity("portable object identity"));
    }
    let id = Uuid::from_slice(&image[32..48]).map_err(|_| integrity("object UUID"))?;
    if expected.is_some_and(|x| x != id) {
        return Err(integrity("portable object ID mismatch"));
    }
    let kind = image[8];
    let start = if kind == 3 { 2 * PAGE } else { PAGE };
    let length = u32::from_le_bytes(image[48..52].try_into().unwrap()) as usize;
    if length > image.len() - start {
        return Err(integrity("portable object length"));
    }
    let aad = image_aad(image, start);
    let mut body = Zeroizing::new(if kind == 0 {
        Vec::new()
    } else {
        image[start..].to_vec()
    });
    if let Some(key) = &crypto.key {
        XChaCha20Poly1305::new_from_slice(key.as_ref())
            .unwrap()
            .decrypt_in_place_detached(
                XNonce::from_slice(&image[56..80]),
                &aad,
                &mut body,
                Tag::from_slice(&image[80..96]),
            )
            .map_err(|_| integrity("portable object authentication"))?;
    } else {
        let mut h = Sha256::new();
        h.update(&aad);
        h.update(&*body);
        if h.finalize()[..16] != image[80..96] {
            return Err(integrity("portable object checksum"));
        }
    }
    if kind != 0 {
        body.truncate(length);
    }
    Ok((kind, id, body))
}
fn page_record(page: u64, r: PageRef, source: Uuid) -> [u8; 88] {
    let mut out = [0; 88];
    out[..8].copy_from_slice(&page.to_le_bytes());
    out[8..24].copy_from_slice(source.as_bytes());
    out[24..32].copy_from_slice(&r.slot.to_le_bytes());
    out[32..40].copy_from_slice(&r.version.to_le_bytes());
    out[40..64].copy_from_slice(&r.nonce);
    out[64..80].copy_from_slice(&r.tag);
    out
}
fn decode_record(bytes: &[u8]) -> Result<(u64, Uuid, PageRef)> {
    if bytes.len() != 88 {
        return Err(invalid("page descriptor length"));
    }
    let u = |i| u64::from_le_bytes(bytes[i..i + 8].try_into().unwrap());
    let id = Uuid::from_slice(&bytes[8..24]).map_err(|_| invalid("page object ID"))?;
    let slot = u(24);
    if slot >= SLOTS {
        return Err(invalid("page slot"));
    }
    Ok((
        u(0),
        id,
        PageRef {
            object: 0,
            object_generation: 0,
            slot,
            version: u(32),
            nonce: bytes[40..64].try_into().unwrap(),
            tag: bytes[64..80].try_into().unwrap(),
        },
    ))
}

impl Shared {
    pub(super) fn read_cloud_blob(&self, mut at: MetaRef) -> Result<Vec<u8>> {
        let mut out = Vec::new();
        let mut seen = BTreeSet::new();
        while !at.empty() {
            if !seen.insert(at.offset) {
                return Err(integrity("cloud blob cycle"));
            }
            let part = self.load_meta(at, codec::CLOUD_BLOB)?;
            if part.len() < 42 {
                return Err(integrity("cloud blob frame"));
            }
            let count = u16::from_le_bytes(part[40..42].try_into().unwrap()) as usize;
            if count != part.len() - 42 || out.len() + count > BLOB_LIMIT {
                return Err(integrity("cloud blob bounds"));
            }
            out.extend_from_slice(&part[42..]);
            at = MetaRef::get(&part[..40]);
        }
        Ok(out)
    }
    fn trace_cloud_blob(&self, mut at: MetaRef, objects: &mut BTreeSet<u64>) -> Result<()> {
        let mut seen = BTreeSet::new();
        while !at.empty() {
            if !seen.insert(at.offset) {
                return Err(integrity("cloud blob cycle"));
            }
            objects.insert(at.offset / OBJECT);
            let bytes = self.load_meta(at, codec::CLOUD_BLOB)?;
            if bytes.len() < 42 {
                return Err(integrity("cloud blob header"));
            }
            at = MetaRef::get(&bytes[..40]);
        }
        Ok(())
    }
    pub(super) fn load_cloud(&self, root: &Root) -> Result<CloudDb> {
        let mut db = CloudDb::default();
        let mut checkpoint = root.cloud_checkpoint;
        let mut checkpoint_seen = BTreeSet::new();
        while !checkpoint.empty() {
            if !checkpoint_seen.insert(checkpoint.offset) {
                return Err(integrity("cloud checkpoint cycle"));
            }
            let bytes = self.read_cloud_blob(checkpoint)?;
            let value: Value = serde_json::from_slice(&bytes)?;
            if value.get("checkpoint_version").is_none() && checkpoint_seen.len() == 1 {
                // Read the initial development format as well. New checkpoints
                // are always split; a whole database is never serialized again.
                db.values = Arc::new(serde_json::from_value(value)?);
                break;
            }
            let chunk: CloudCheckpoint = serde_json::from_value(value)?;
            if chunk.checkpoint_version != 1 || bytes.len() > CHECKPOINT_CHUNK_BYTES + 4096 {
                return Err(integrity("cloud checkpoint format/bounds"));
            }
            let values = Arc::make_mut(&mut db.values);
            for (key, value) in chunk.entries {
                if values.insert(key, value).is_some() {
                    return Err(integrity("duplicate cloud checkpoint key"));
                }
            }
            checkpoint = chunk.previous;
        }
        let mut at = root.cloud_log;
        let mut seen = BTreeSet::new();
        let mut changed = BTreeSet::new();
        while !at.empty() {
            if !seen.insert(at.offset) {
                return Err(integrity("cloud log cycle"));
            }
            let log: CloudLog = serde_json::from_slice(&self.read_cloud_blob(at)?)?;
            at = log.previous;
            // The chain is newest first. Only the newest event for each key is
            // needed; do not retain all historical Value payloads in memory.
            for event in log.events.into_iter().rev() {
                db.log_events += 1;
                if !changed.insert(event.key.clone()) {
                    continue;
                }
                if let Some(value) = event.value {
                    Arc::make_mut(&mut db.values).insert(event.key, value);
                } else {
                    Arc::make_mut(&mut db.values).remove(&event.key);
                }
            }
        }
        Ok(db)
    }
    pub(super) fn cloud_reachable(&self, root: &Root, objects: &mut BTreeSet<u64>) -> Result<()> {
        let mut checkpoint = root.cloud_checkpoint;
        let mut checkpoints = BTreeSet::new();
        while !checkpoint.empty() {
            if !checkpoints.insert(checkpoint.offset) {
                return Err(integrity("cloud checkpoint cycle"));
            }
            self.trace_cloud_blob(checkpoint, objects)?;
            let value: Value = serde_json::from_slice(&self.read_cloud_blob(checkpoint)?)?;
            if value.get("checkpoint_version").is_none() && checkpoints.len() == 1 {
                break;
            }
            let chunk: CloudCheckpoint = serde_json::from_value(value)?;
            if chunk.checkpoint_version != 1 {
                return Err(integrity("cloud checkpoint version"));
            }
            checkpoint = chunk.previous;
        }
        let mut at = root.cloud_log;
        let mut seen = BTreeSet::new();
        while !at.empty() {
            if !seen.insert(at.offset) {
                return Err(integrity("cloud log cycle"));
            }
            self.trace_cloud_blob(at, objects)?;
            let log: CloudLog = serde_json::from_slice(&self.read_cloud_blob(at)?)?;
            at = log.previous;
        }
        let db = self.load_cloud(root)?;
        for (key, value) in db.values.iter() {
            if key.starts_with("spool/") {
                if let Some(n) = value["extent"].as_u64() {
                    if n == 0 || n * OBJECT + OBJECT > self.file.len()? {
                        return Err(integrity("cloud spool reference"));
                    }
                    objects.insert(n);
                }
            }
            if key.starts_with("draft/") {
                let mut head: MetaRef = serde_json::from_value(value["head"].clone())?;
                let mut seen = BTreeSet::new();
                while !head.empty() {
                    if !seen.insert(head.offset) {
                        return Err(integrity("draft cycle"));
                    }
                    self.trace_cloud_blob(head, objects)?;
                    let bytes = self.read_cloud_blob(head)?;
                    if bytes.len() < 40 {
                        return Err(integrity("draft length"));
                    }
                    head = MetaRef::get(&bytes[..40]);
                }
            }
        }
        Ok(())
    }
    /// Keep identities that can still be exported and receipts for the current
    /// cloud closure. This changes only metadata: old roots retain their own
    /// records/extents until the ordinary COW publication protocol completes.
    pub(super) fn prune_cloud(&self, state: &mut State) -> Result<usize> {
        let mut physical = BTreeSet::new();
        self.index_objects(state.current.index, state.current.depth, &mut physical)?;
        for snapshot in &state.snapshots {
            self.index_objects(snapshot.index, snapshot.depth, &mut physical)?;
        }
        let mut identities = BTreeSet::new();
        for number in physical {
            identities.insert(self.object_header(number)?.id.to_string());
        }
        let active_job = state.cloud.get("job")["id"]
            .as_str()
            .map(|id| format!("job/{id}/"));
        let active_draft = state.cloud.get("job")["id"]
            .as_str()
            .map(|id| format!("draft/{id}/"));
        let compact = state.cloud.get("compact");
        let active_compact = if compact["state"] == "done" || compact["state"] == "idle" {
            None
        } else {
            compact["id"].as_str().map(|id| format!("compact/{id}/"))
        };
        let mut remote = BTreeSet::new();
        for (_, value) in state.cloud.prefix_iter("published/") {
            if let Some(id) = value["id"].as_str() {
                remote.insert(id.to_owned());
            }
        }
        if let Some(prefix) = &active_job {
            for (_, value) in state.cloud.prefix_iter(prefix) {
                if let Some(id) = value["id"].as_str() {
                    remote.insert(id.to_owned());
                }
            }
        }
        identities.extend(remote.iter().cloned());
        let restoring = state.cloud.restore_incomplete();
        let mut staged = BTreeSet::new();
        if restoring {
            for (_, value) in state.cloud.prefix_iter("restore/need/") {
                if let Some(id) = value["id"].as_str() {
                    staged.insert(id.to_owned());
                }
            }
        }
        if let Some(prefix) = &active_job {
            for (_, value) in state.cloud.prefix_iter(prefix) {
                if let Some(id) = value["id"].as_str() {
                    staged.insert(id.to_owned());
                }
            }
        }
        identities.extend(staged.iter().cloned());
        let before = state.cloud.values.len();
        Arc::make_mut(&mut state.cloud.values).retain(|key, value| {
            if let Some(id) = key
                .strip_prefix("source/")
                .or_else(|| key.strip_prefix("highwater/"))
            {
                return identities.contains(id);
            }
            if let Some(id) = key.strip_prefix("receipt/") {
                return remote.contains(id);
            }
            if key.starts_with("image/") {
                return value["id"]
                    .as_str()
                    .is_some_and(|id| identities.contains(id));
            }
            if let Some(id) = key.strip_prefix("spool/") {
                return staged.contains(id);
            }
            if key.starts_with("job/") {
                return active_job
                    .as_ref()
                    .is_some_and(|prefix| key.starts_with(prefix));
            }
            if key.starts_with("draft/") {
                return active_draft
                    .as_ref()
                    .is_some_and(|prefix| key.starts_with(prefix));
            }
            if key.starts_with("compact/") {
                return active_compact
                    .as_ref()
                    .is_some_and(|prefix| key.starts_with(prefix));
            }
            if key.starts_with("restore/need/") {
                return restoring;
            }
            true
        });
        let removed = before - state.cloud.values.len();
        if removed != 0 {
            state.cloud.force_checkpoint = true;
        }
        Ok(removed)
    }
    fn allocate_spool(&self, state: &mut State, id: &str, image: &[u8]) -> Result<()> {
        if image.len() != OBJECT as usize {
            return Err(invalid("cloud object must be exactly 4 MiB"));
        }
        if !state.cloud.get(&format!("spool/{id}")).is_null() {
            return Ok(());
        }
        let result: Result<()> = (|| {
            let extent = if let Some(n) = state.free.pop() {
                n
            } else {
                let n = self.file.len()? / OBJECT;
                self.file.grow((n + 1) * OBJECT)?;
                state.free.extend((n + 1..self.file.len()? / OBJECT).rev());
                n
            };
            self.file.write(extent * OBJECT, image)?;
            state.cloud.set(
                format!("spool/{id}"),
                json!({"extent":extent,"sha256":digest(image)}),
            );
            Ok(())
        })();
        if let Err(ref e) = result {
            state.failure = Some(e.to_string());
        }
        result
    }
    fn read_spool(&self, state: &State, id: &str) -> Result<Vec<u8>> {
        let v = state.cloud.get(&format!("spool/{id}"));
        let extent = v["extent"]
            .as_u64()
            .ok_or_else(|| invalid("prepared object is unavailable"))?;
        let mut bytes = vec![0; OBJECT as usize];
        self.file.read(extent * OBJECT, &mut bytes)?;
        if digest(&bytes) != v["sha256"].as_str().unwrap_or("") {
            return Err(integrity("cloud spool hash"));
        }
        Ok(bytes)
    }
    fn snapshot_pages(
        &self,
        snapshot: &Snapshot,
        cursor: u64,
        limit: usize,
    ) -> Result<Vec<(u64, PageRef)>> {
        let mut out = Vec::new();
        self.walk_after(snapshot.index, snapshot.depth, 0, cursor, limit, &mut out)?;
        Ok(out)
    }
    fn walk_after(
        &self,
        reference: MetaRef,
        level: u8,
        base: u64,
        cursor: u64,
        limit: usize,
        out: &mut Vec<(u64, PageRef)>,
    ) -> Result<()> {
        if reference.empty() || out.len() >= limit || base + (1u64 << (5 + 6 * level)) <= cursor {
            return Ok(());
        }
        match &*self.node(reference, level)? {
            Node::Leaf(pages) => {
                for (i, p) in pages.iter().enumerate() {
                    if let Some(p) = p {
                        let page = base + i as u64;
                        if page >= cursor && out.len() < limit {
                            out.push((page, *p));
                        }
                    }
                }
            }
            Node::Branch(children) => {
                for (i, child) in children.iter().enumerate() {
                    self.walk_after(
                        *child,
                        level - 1,
                        base + ((i as u64) << (5 + 6 * (level - 1))),
                        cursor,
                        limit,
                        out,
                    )?;
                    if out.len() >= limit {
                        break;
                    }
                }
            }
        }
        Ok(())
    }
    fn capture(&self, state: &mut State, name: &str, pin: &str) -> Result<Snapshot> {
        if state.snapshots.len() >= 1024 {
            return Err(invalid("snapshot limit reached"));
        }
        for tail in [state.current.data_tail, state.current.meta_tail]
            .iter()
            .flatten()
        {
            let header = self.object_header(tail.number)?;
            state
                .cloud
                .set(format!("highwater/{}", header.id), json!(tail.slot));
        }
        let snapshot = Snapshot {
            id: Uuid::new_v4().to_string(),
            index: state.current.index,
            depth: state.current.depth,
            seq: state.current.seq,
            allocated_pages: state.current.allocated_pages,
            seals: state.current.seals,
        };
        state.current.data_tail = None;
        state.current.meta_tail = None;
        state.snapshots.push(snapshot.clone());
        state.cloud.set(format!("snapshot/{}",snapshot.id),json!({"id":snapshot.id,"name":name,"generation":state.current.data_generation,"allocated_pages":snapshot.allocated_pages,"created_utc":now(),"upload_pin":pin=="upload","pin":pin}));
        Ok(snapshot)
    }
}

impl Volume {
    pub(super) fn require_restored(&self) -> Result<()> {
        let state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        if state.current.restore_required
            || state.cloud.restore_incomplete()
            || self.shared.config.restoring && state.cloud.get("restore_finished") != true
        {
            return Err(invalid(
                "restore is incomplete; block device access is disabled",
            ));
        }
        Ok(())
    }
    fn require_v3(&self) -> Result<()> {
        if self.shared.config.format_version != 3 {
            return Err(invalid("this operation requires a format-3 container"));
        }
        Ok(())
    }
    pub fn control_v3(&self, request: &Value) -> Result<Value> {
        self.require_v3()?;
        let cmd = string(request, "cmd")?;
        match cmd.as_str() {
            "cloud.status" => self.cloud_status(),
            "cloud.list" | "cloud.objects" => self.cloud_objects(request),
            "cloud.published_objects" => self.published_objects(request),
            "cloud.prepare" => self.cloud_prepare(request),
            "cloud.bind" | "cloud.pause" | "cloud.receipt" | "cloud.commit" => {
                self.cloud_update(request)
            }
            "snapshot.list" | "snapshot.create" | "snapshot.rename" | "snapshot.delete" => {
                self.snapshot_control(request)
            }
            "compact.start" | "compact.step" | "compact.pause" | "compact.status" => {
                self.compact_control(request)
            }
            "debug.blocks" => self.debug_blocks(request),
            "restore.status" | "restore.finish" | "snapshot.restore_step" => {
                self.restore_control(request)
            }
            _ => Err(invalid("unknown V3 command")),
        }
    }
}

#[cfg(test)]
mod catalog_tests {
    use super::*;

    fn volume() -> (tempfile::TempDir, Volume) {
        let directory = tempfile::tempdir().unwrap();
        let mut volume =
            Volume::create_v3(directory.path().join("catalog.odv3"), 64 << 20, None).unwrap();
        volume.shared.stop.store(true, Ordering::Release);
        volume.shared.wake.notify_all();
        volume.worker.take().unwrap().join().unwrap();
        (directory, volume)
    }

    #[test]
    fn prefix_pagination_and_non_ascii_hex_are_bounded() {
        let mut db = CloudDb::default();
        for i in 0..109 {
            db.set(format!("item/{i:04}"), json!({"id":i}));
        }
        db.set("item0/extra", json!(1));
        assert_eq!(db.prefix_count("item/"), 109);
        let page = db.prefix_page("item/", 101, 5);
        assert_eq!(page.len(), 5);
        assert_eq!(page[0].0, "item/0101");
        assert_eq!(page[4].0, "item/0105");
        assert!(db.prefix_page("item/", 109, 3).is_empty());
        assert!(db.prefix_page("item/", 0, 0).is_empty());
        assert!(from_hex("0aFF").unwrap() == [10, 255]);
        assert!(std::panic::catch_unwind(|| from_hex(&"汉".repeat(22)))
            .unwrap()
            .is_err());
    }

    #[test]
    fn streaming_checkpoint_reopen_log_and_missing_chunk_fail_closed() {
        use std::io::{Read, Seek, SeekFrom, Write};
        let (directory, volume) = volume();
        let path = directory.path().join("catalog.odv3");
        let lost_chunk;
        {
            let mut state = volume.shared.state.lock().unwrap();
            let text = "x".repeat(8192);
            for i in 0..400 {
                state
                    .cloud
                    .set(format!("test/{i:04}"), json!({"text":text,"number":i}));
            }
            state.cloud.force_checkpoint = true;
            volume.shared.persist(&mut state, false).unwrap();
            assert!(!state.cloud.force_checkpoint);
            let mut reference = state.current.cloud_checkpoint;
            let mut count = 0;
            let mut second = MetaRef::default();
            while !reference.empty() {
                let bytes = volume.shared.read_cloud_blob(reference).unwrap();
                assert!(bytes.len() <= CHECKPOINT_CHUNK_BYTES);
                let chunk: CloudCheckpoint = serde_json::from_slice(&bytes).unwrap();
                if count == 1 {
                    second = reference;
                }
                reference = chunk.previous;
                count += 1;
            }
            assert!(count >= 4);
            lost_chunk = second;
            state.cloud.set("test/0001", json!({"updated":true}));
            state.cloud.remove("test/0002");
            volume.shared.persist(&mut state, false).unwrap();
            state.cloud.set("test/0001", json!({"updated":false}));
            state.cloud.set("test/0001", json!({"updated":true}));
            state.cloud.set("test/0002", json!({"temporary":true}));
            state.cloud.remove("test/0002");
            volume.shared.persist(&mut state, false).unwrap();
            assert!(!state.current.cloud_log.empty());
        }
        drop(volume);
        let reopened = Volume::open(&path, None).unwrap();
        {
            let state = reopened.shared.state.lock().unwrap();
            assert_eq!(state.cloud.prefix_count("test/"), 399);
            assert_eq!(state.cloud.get("test/0001"), json!({"updated":true}));
            assert_eq!(state.cloud.get("test/0399")["number"], 399);
            assert!(state.cloud.get("test/0002").is_null());
        }
        drop(reopened);
        let mut file = std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .open(&path)
            .unwrap();
        file.seek(SeekFrom::Start(lost_chunk.offset + 120)).unwrap();
        let mut byte = [0];
        file.read_exact(&mut byte).unwrap();
        byte[0] ^= 1;
        file.seek(SeekFrom::Start(lost_chunk.offset + 120)).unwrap();
        file.write_all(&byte).unwrap();
        file.sync_all().unwrap();
        drop(file);
        assert!(Volume::open(&path, None).is_err());
    }

    #[test]
    fn lifecycle_sweep_preserves_snapshot_identity_and_active_staging() {
        let (directory, volume) = volume();
        volume.write(0, &[7; PAGE]).unwrap();
        volume.flush().unwrap();
        let original = {
            let state = volume.shared.state.lock().unwrap();
            let reference = volume.shared.lookup(&state.current, 0).unwrap().unwrap();
            volume
                .shared
                .object_header(reference.object)
                .unwrap()
                .id
                .to_string()
        };
        volume
            .control_v3(&json!({"cmd":"snapshot.create","name":"keep identity"}))
            .unwrap();
        volume.write(0, &[9; PAGE]).unwrap();
        volume.flush().unwrap();
        let stale = Uuid::new_v4().to_string();
        let remote = Uuid::new_v4().to_string();
        let staged = Uuid::new_v4().to_string();
        let orphan = Uuid::new_v4().to_string();
        let job = Uuid::new_v4().to_string();
        {
            let mut state = volume.shared.state.lock().unwrap();
            state.cloud.set(
                format!("source/{original}"),
                json!({"id":original,"nonce":"unchanged"}),
            );
            state.cloud.set(format!("highwater/{original}"), json!(1));
            state.cloud.set(
                format!("receipt/{original}"),
                json!({"sha256":"old remote version"}),
            );
            state
                .cloud
                .set(format!("source/{stale}"), json!({"id":stale}));
            state.cloud.set(format!("highwater/{stale}"), json!(1));
            state
                .cloud
                .set(format!("receipt/{stale}"), json!({"sha256":"stale"}));
            state.cloud.set("image/1/stale", json!({"id":stale}));
            state
                .cloud
                .set(format!("published/{remote}"), json!({"id":remote}));
            state
                .cloud
                .set(format!("receipt/{remote}"), json!({"sha256":"remote"}));
            state.cloud.set("image/2/current", json!({"id":remote}));
            state.cloud.set("job", json!({"id":job}));
            state
                .cloud
                .set(format!("job/{job}/object/{staged}"), json!({"id":staged}));
            state
                .cloud
                .set(format!("receipt/{staged}"), json!({"sha256":"staged"}));
            volume
                .shared
                .allocate_spool(&mut state, &staged, &vec![0; OBJECT as usize])
                .unwrap();
            volume
                .shared
                .allocate_spool(&mut state, &orphan, &vec![0; OBJECT as usize])
                .unwrap();
            let events = state.cloud.pending.len();
            assert!(volume.shared.prune_cloud(&mut state).unwrap() >= 6);
            assert_eq!(state.cloud.pending.len(), events); // No unbounded tombstone event vector.
            assert!(state.cloud.force_checkpoint);
            assert_eq!(
                state.cloud.get(&format!("source/{original}"))["nonce"],
                "unchanged"
            );
            assert!(!state.cloud.get(&format!("highwater/{original}")).is_null());
            assert!(state.cloud.get(&format!("receipt/{original}")).is_null());
            assert!(!state.cloud.get(&format!("receipt/{remote}")).is_null());
            assert!(!state.cloud.get(&format!("receipt/{staged}")).is_null());
            assert!(!state.cloud.get(&format!("spool/{staged}")).is_null());
            assert!(state.cloud.get(&format!("spool/{orphan}")).is_null());
            volume.shared.persist(&mut state, false).unwrap();
        }
        drop(volume);
        let reopened = Volume::open(directory.path().join("catalog.odv3"), None).unwrap();
        let state = reopened.shared.state.lock().unwrap();
        assert_eq!(
            state.cloud.get(&format!("source/{original}"))["nonce"],
            "unchanged"
        );
        assert!(state.cloud.get(&format!("source/{stale}")).is_null());
        assert_eq!(
            reopened.shared.read_spool(&state, &staged).unwrap().len(),
            OBJECT as usize
        );
    }
}
