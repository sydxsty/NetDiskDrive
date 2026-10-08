//! Lazy immutable source trees. Local COW nodes keep exact references; imported
//! trees keep a durable source anchor until explicitly detached by the user.
use super::cloud::Cloud;
use super::codec::{hash, hex};
use super::store::{Object, Txn, COUNTS, DIRTY, OBJECTS, PAGES, PORTABLE, PORTMAP, QUEUE};
use super::tree::{self, Node};
use super::*;
use serde_json::{json, Value};

/// Boolean values are the existing local catalog states. The explicit anchor
/// marker deliberately cannot be opened by older writers: they would otherwise
/// replace our partial publication counts with the original source's counts.
/// This only changes local control metadata, never a cloud object or index.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum CountCoverage {
    Deferred,
    Complete,
    SourceAnchor,
}
impl CountCoverage {
    pub fn is_complete(self) -> bool { self == Self::Complete }
}
impl Serialize for CountCoverage {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> std::result::Result<S::Ok, S::Error> {
        match self {
            Self::Deferred => serializer.serialize_bool(false),
            Self::Complete => serializer.serialize_bool(true),
            Self::SourceAnchor => serializer.serialize_str("source_anchor_v1"),
        }
    }
}
impl<'de> Deserialize<'de> for CountCoverage {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> std::result::Result<Self, D::Error> {
        #[derive(Deserialize)]
        #[serde(untagged)]
        enum State { Complete(bool), Mode(String) }
        match State::deserialize(deserializer)? {
            State::Complete(false) => Ok(Self::Deferred),
            State::Complete(true) => Ok(Self::Complete),
            State::Mode(mode) if mode == "source_anchor_v1" => Ok(Self::SourceAnchor),
            _ => Err(serde::de::Error::custom("unsupported source reference accounting")),
        }
    }
}

#[derive(Clone, Serialize, Deserialize)]
pub(super) struct IdentityRegion {
    pub offset: u64,
    pub bytes: Vec<u8>,
}
#[derive(Clone, Serialize, Deserialize)]
pub(super) struct Candidate {
    pub token: String,
    pub expected_revision: u64,
    pub index: MetaRef,
    pub root_object_id: String,
    pub root_sha256: String,
    pub generation: u64,
    pub pages: u64,
    pub backing: Value,
    pub publication: Option<Value>,
    pub commit: Value,
    pub ready: bool,
}
#[derive(Clone, Serialize, Deserialize)]
pub(super) struct Replica {
    pub source_volume_id: Uuid,
    pub mode: String,
    pub source_index: MetaRef,
    pub root_object_id: String,
    pub root_sha256: String,
    pub generation: u64,
    pub local_baseline_generation: u64,
    pub counts_complete: CountCoverage,
    pub pins: Vec<Value>,
    pub identity: Vec<IdentityRegion>,
    pub identity_ready: bool,
    pub candidate: Option<Candidate>,
    pub commit: Option<Value>,
}
fn invalid(s: &str) -> Error {
    Error::Invalid(s.into())
}
fn integrity(s: &str) -> Error {
    Error::Integrity(s.into())
}
fn ordinal(id: Uuid) -> u64 {
    u64::from_le_bytes(id.as_bytes()[8..].try_into().unwrap())
}

impl store::Store {
    pub(super) fn validate_remote_identity(&mut self, id: Uuid, sha: [u8; 32]) -> Result<()> {
        let oid = ordinal(id);
        let range = store::allocation_range(&self.config)?;
        let source_limit = if self
            .cloud
            .restore
            .as_ref()
            .is_some_and(|r| r.kind == "cloud" && r.mode == "copy")
        {
            range.start
        } else {
            range.end
        };
        if oid == 0 || oid >= source_limit {
            return Err(integrity("source object namespace"));
        }
        let root = self.root.objects;
        if let Some(row) = tree::get(self, &OBJECTS, root, oid)? {
            let object = Object::decode(oid, &row)?;
            if object.id != id || object.sha != sha || !object.sealed {
                return Err(integrity("remote object identity collision"));
            }
        }
        Ok(())
    }
    pub(super) fn validate_remote_dependencies(&mut self, raw: &[u8], oid: u64) -> Result<()> {
        for (_, (id, sha)) in store::external_table(&self.crypto, oid, raw)? {
            self.validate_remote_identity(id, sha)?;
        }
        Ok(())
    }
}

impl Txn<'_> {
    /// Register only authenticated object identities. Kind and slot count remain
    /// unknown until the immutable object itself has passed authentication.
    pub(super) fn register_remote(&mut self, id: Uuid, sha: [u8; 32]) -> Result<()> {
        let oid = ordinal(id);
        if oid == 0 || oid >= OBJECTS.capacity()? || oid >= PORTABLE / self.geometry().object_size {
            return Err(integrity("remote object ordinal"));
        }
        let row = match self.objects.get(&oid) {
            Some(o) => Some(o.encode()),
            None => tree::get(self, &OBJECTS, self.root.objects, oid)?,
        };
        let mut object = if let Some(row) = row {
            let o = Object::decode(oid, &row)?;
            if o.id != id || o.sha != sha || !o.sealed {
                return Err(integrity("remote object identity collision"));
            }
            o
        } else {
            Object {
                oid,
                id,
                sha,
                extent: 0,
                kind: 0,
                pool: 0,
                used: 0,
                sealed: true,
                missing: true,
                remote: true,
                remote_source: 1,
                cache_backed: false,
                origin_backed: false,
                sync_state: 3,
                refs: 0,
                current_refs: 0,
                cloud_refs: 0,
                external_count: 0,
            }
        };
        if !object.origin_backed {
            object.refs = object
                .refs
                .checked_add(1)
                .ok_or_else(|| integrity("source anchor overflow"))?;
        }
        object.remote = true;
        object.origin_backed = true;
        object.remote_source = 1;
        self.root.next_oid = self.root.next_oid.max(oid + 1);
        self.objects.insert(oid, object);
        Ok(())
    }
    pub(super) fn register_dependencies(&mut self, raw: &[u8], oid: u64) -> Result<()> {
        let header = store::decode_header(&self.store.crypto, oid, raw)?;
        if header["kind"] == 2 {
            for (_, (id, sha)) in store::external_table(&self.store.crypto, oid, raw)? {
                self.register_remote(id, sha)?;
            }
        }
        Ok(())
    }
}

impl Volume {
    pub(crate) const MAX_REPLICA_IDENTITY_FRAME: usize = 4 + 4 * 12 + 65536;

    /// Atomically install the local partition identity. Bytes use a bounded
    /// binary frame so a normal GPT pair cannot exceed the JSON control limit.
    /// All integers are little endian: count, then (offset, length, bytes).
    pub fn replica_identity(&self, frame: &[u8]) -> Result<()> {
        if frame.len() < 4 || frame.len() > Self::MAX_REPLICA_IDENTITY_FRAME {
            return Err(invalid("invalid identity frame length"));
        }
        let count = u32::from_le_bytes(frame[..4].try_into().unwrap()) as usize;
        if count > 4 {
            return Err(invalid("identity overlay has too many regions"));
        }
        let mut remaining = &frame[4..];
        let mut regions = Vec::with_capacity(count);
        let mut total = 0usize;
        for _ in 0..count {
            if remaining.len() < 12 {
                return Err(invalid("truncated identity region header"));
            }
            let offset = u64::from_le_bytes(remaining[..8].try_into().unwrap());
            let length = u32::from_le_bytes(remaining[8..12].try_into().unwrap()) as usize;
            remaining = &remaining[12..];
            if length == 0 || length > remaining.len() || length > 65536 - total {
                return Err(invalid("invalid identity region length"));
            }
            let capacity = self.capacity();
            let end = offset
                .checked_add(length as u64)
                .ok_or_else(|| invalid("identity region overflow"))?;
            if end > capacity || !(end <= 65536 || offset >= capacity.saturating_sub(65536)) {
                return Err(invalid(
                    "identity overlay must be confined to partition tables",
                ));
            }
            regions.push(IdentityRegion {
                offset,
                bytes: remaining[..length].to_vec(),
            });
            total += length;
            remaining = &remaining[length..];
        }
        if !remaining.is_empty() {
            return Err(invalid("trailing bytes in identity frame"));
        }
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        let mut c = s.cloud.clone();
        let r = c
            .replica
            .as_mut()
            .ok_or_else(|| invalid("not a cloud replica"))?;
        if r.mode != "copy" {
            return Err(invalid("original disk cannot override its identity"));
        }
        r.identity = regions;
        r.identity_ready = true;
        let identity = r.identity.clone();
        s.transaction(|tx| tx.save_cloud(&c))?;
        s.cloud = c;
        *self.shared.identity.lock().map_err(|_| Error::Poisoned)? = identity;
        Ok(())
    }

    pub(super) fn replica_control(&self, request: &Value) -> Result<Value> {
        match request["cmd"].as_str().unwrap_or("") {
            "replica.bootstrap" => self.bootstrap_replica(request.get("commit").cloned())?,
            "replica.status" => {}
            "replica.stage_cached" => {
                let options = &request["options"];
                let raw = {
                    let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
                    let id =
                        Uuid::parse_str(options["commit"]["rootObjectId"].as_str().unwrap_or(""))
                            .map_err(|_| invalid("root ID"))?;
                    if ordinal(id) == 0 || ordinal(id) >= OBJECTS.capacity()? {
                        return Ok(json!({"cached":false}));
                    }
                    let root = s.root.objects;
                    let object = tree::get(&mut *s, &OBJECTS, root, ordinal(id))?
                        .map(|row| Object::decode(ordinal(id), &row))
                        .transpose()?;
                    match object {
                        Some(o) if o.id == id && !o.missing && o.sealed => {
                            Some(s.verify_object(&o)?)
                        }
                        _ => None,
                    }
                };
                if let Some(raw) = raw {
                    self.replica_stage(&raw, options)?;
                    let mut status = self.replica_status()?;
                    status["cached"] = json!(true);
                    return Ok(status);
                }
                return Ok(json!({"cached":false}));
            }
            "replica.stage_current" => {
                let (object, backing, commit) = {
                    let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
                    let r = s
                        .cloud
                        .replica
                        .clone()
                        .ok_or_else(|| invalid("not a cloud replica"))?;
                    (
                        s.object(ordinal(
                            Uuid::parse_str(&r.root_object_id).map_err(|_| integrity("root ID"))?,
                        ))?,
                        s.cloud.lazy_backing.clone(),
                        request.get("commit").cloned().or(r.commit.clone()),
                    )
                };
                if object.missing {
                    self.shared
                        .hydrate_missing(&RemoteObject::from_object(&object, self.object_size()))?;
                }
                let raw = {
                    let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
                    let object = s.object(object.oid)?;
                    s.verify_object(&object)?
                };
                self.replica_stage(&raw, &json!({"backing":backing,"commit":commit}))?;
            }
            "replica.cancel" => {
                let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
                let mut c = s.cloud.clone();
                if let Some(r) = c.replica.as_mut() {
                    r.candidate = None;
                }
                s.transaction(|tx| tx.save_cloud(&c))?;
                s.cloud = c;
            }
            "replica.apply" => self.apply_replica(request)?,
            _ => return Err(invalid("unknown replica command")),
        }
        self.replica_status()
    }
    fn replica_status(&self) -> Result<Value> {
        let (_lease, mut reader) = self.read_context()?;
        let c: Cloud = if reader.root.cloud.empty() {
            Cloud::default()
        } else {
            serde_json::from_slice(&reader.blob(reader.root.cloud)?)?
        };
        let Some(r) = c.replica else {
            return Ok(json!({"enabled":false}));
        };
        Ok(
            json!({"enabled":true,"mode":r.mode,"source_volume_id":r.source_volume_id,"root_object_id":r.root_object_id,
            "root_sha256":r.root_sha256,"generation":r.generation,"local_revision":reader.root.data_generation,
            "local_changes":reader.root.data_generation != r.local_baseline_generation || self.has_local_dirty(),
            "index_complete":r.counts_complete.is_complete(),"reference_accounting":if r.counts_complete == CountCoverage::SourceAnchor {"source_anchor"} else if r.counts_complete.is_complete() {"complete"} else {"deferred"},"verification":"on_access","mount_ready":!reader.root.restore_required,
            "identity_ready":r.mode!="copy" || r.identity_ready,
            "backing":c.lazy_backing,"candidate":r.candidate,"commit":r.commit,"retained_sources":r.pins.len()}),
        )
    }
    fn bootstrap_replica(&self, commit: Option<Value>) -> Result<()> {
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        if s.cloud.replica.is_some() {
            return Ok(());
        }
        let r = s
            .cloud
            .restore
            .clone()
            .ok_or_else(|| invalid("not a cloud restore"))?;
        if r.kind != "cloud" || !r.lazy {
            return Err(invalid("lazy source required"));
        }
        let id = Uuid::parse_str(&r.root_id).map_err(|_| integrity("source root ID"))?;
        let root = s.object(ordinal(id))?;
        let raw = s.verify_object(&root)?;
        let mut c = s.cloud.clone();
        c.replica = Some(Replica {
            source_volume_id: r.source_volume_id,
            mode: r.mode.clone(),
            source_index: r.index,
            root_object_id: r.root_id.clone(),
            root_sha256: r.root_sha256.clone(),
            generation: r.generation,
            local_baseline_generation: r.generation.max(u64::from(r.mode == "copy")),
            counts_complete: if r.phase == "complete" { CountCoverage::Complete } else { CountCoverage::Deferred },
            pins: c.lazy_backing.iter().cloned().collect(),
            identity: vec![],
            identity_ready: false,
            candidate: None,
            commit,
        });
        s.validate_remote_dependencies(&raw, root.oid)?;
        s.transaction(|tx| {
            tx.root.deferred_index = true;
            tx.register_remote(root.id, root.sha)?;
            tx.register_dependencies(&raw, root.oid)?;
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        Ok(())
    }
    pub(super) fn quick_restore_step(&self) -> Result<bool> {
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        if s.cloud.replica.is_none() {
            return Ok(false);
        }
        let mut c = s.cloud.clone();
        let r = c.restore.as_mut().ok_or_else(|| invalid("restore state"))?;
        if matches!(r.phase.as_str(), "ready" | "complete") {
            return Ok(true);
        }
        // Only the top node is required for authenticated cardinality; descendants
        // stay as immutable references and are resolved by normal block reads.
        let count = tree::len(&mut *s, &PORTMAP, r.index)?;
        if count > s.config.capacity_bytes / PAGE as u64 {
            return Err(integrity("root cardinality"));
        }
        r.expected_pages = count;
        r.verified_nodes = u64::from(!r.index.empty());
        r.phase = "ready".into();
        let queue = r.queue;
        let index = r.index;
        r.queue = MetaRef::default();
        r.queue_tail = 0;
        s.transaction(|tx| {
            tx.replace(&QUEUE, queue, MetaRef::default());
            tx.root.index = index;
            tx.root.allocated_pages = count;
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        Ok(true)
    }
    pub(super) fn quick_restore_finish(&self) -> Result<bool> {
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        if s.cloud.replica.is_none() {
            return Ok(false);
        }
        let mut c = s.cloud.clone();
        let mut r = c.restore.clone().ok_or_else(|| invalid("restore state"))?;
        if r.phase == "complete" {
            return Ok(true);
        }
        if r.phase != "ready" {
            return Err(invalid("root index is not verified"));
        }
        r.phase = "complete".into();
        let root = ordinal(Uuid::parse_str(&r.root_id).map_err(|_| integrity("root ID"))?);
        if r.mode == "original" {
            c.published_index = r.index;
            c.published_root = root;
            c.published_generation = r.generation;
            c.published_counts = MetaRef::default();
            c.published_commit = Some(
                json!({"root_object_id":r.root_id,"root_sha256":r.root_sha256,"root_slot":0,"generation":r.generation,"receipt":"original-cloud-publication"}),
            );
        } else {
            c.base_index = r.index;
            c.base_root = root;
            c.base_counts = MetaRef::default();
        }
        c.restore = Some(r.clone());
        s.transaction(|tx| {
            tx.root.restore_required = false;
            tx.root.index = r.index;
            tx.root.allocated_pages = r.expected_pages;
            tx.root.data_generation = r.generation.max(u64::from(r.mode == "copy"));
            tx.root.changed_pages = 0;
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        Ok(true)
    }
    pub(super) fn apply_identity(&self, offset: u64, output: &mut [u8]) -> Result<()> {
        if offset >= 65536 && offset + output.len() as u64 <= self.capacity().saturating_sub(65536)
        {
            return Ok(());
        }
        let regions = self.shared.identity.lock().map_err(|_| Error::Poisoned)?;
        for region in regions.iter() {
            let first = offset.max(region.offset);
            let end = (offset + output.len() as u64).min(region.offset + region.bytes.len() as u64);
            if first < end {
                output[(first - offset) as usize..(end - offset) as usize].copy_from_slice(
                    &region.bytes[(first - region.offset) as usize..(end - region.offset) as usize],
                );
            }
        }
        Ok(())
    }

    pub fn replica_stage(&self, raw: &[u8], options: &Value) -> Result<()> {
        if raw.len() as u64 != self.object_size() {
            return Err(invalid("replica root geometry"));
        }
        self.flush()?;
        let (index, depth, token) = {
            let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            s.check()?;
            let mut c = s.cloud.clone();
            let replica = c
                .replica
                .as_mut()
                .ok_or_else(|| invalid("not a cloud replica"))?;
            if c.job.is_some() {
                return Err(invalid(
                    "finish the pending upload before replacing the replica",
                ));
            }
            let source = store::public_config(raw)?;
            if source.id != replica.source_volume_id
                || source.crypto_id.unwrap_or(source.id) != s.crypto.id
                || source.capacity_bytes != s.config.capacity_bytes
                || source.object_size != s.object_size()
                || source
                    .allocation_depth
                    .checked_add(u8::from(replica.mode == "copy"))
                    != Some(s.config.allocation_depth)
                || source.encrypted != s.config.encrypted
            {
                return Err(integrity("replica source identity or geometry changed"));
            }
            let address = u64::from_le_bytes(raw[24..32].try_into().unwrap());
            if address & PORTABLE == 0 || address % self.object_size() != 0 {
                return Err(integrity("replica root address"));
            }
            let oid = (address & !PORTABLE) / self.object_size();
            let header = store::decode_header(&s.crypto, oid, raw)?;
            let id = Uuid::parse_str(header["id"].as_str().unwrap_or(""))
                .map_err(|_| integrity("replica root identity"))?;
            if ordinal(id) != oid
                || header["kind"] != 2
                || header["config"] != serde_json::to_value(&source)?
            {
                return Err(integrity("replica root descriptor"));
            }
            let g = s.geometry();
            let descriptor: Value = serde_json::from_slice(
                &s.crypto.unframe(
                    codec::ROOT,
                    PORTABLE | (oid * g.object_size + g.payload_pages * PAGE as u64),
                    raw[g.header_bytes()..g.header_bytes() + PAGE]
                        .try_into()
                        .unwrap(),
                )?,
            )?;
            let index: MetaRef = serde_json::from_value(descriptor["index"].clone())?;
            let generation = descriptor["generation"]
                .as_u64()
                .ok_or_else(|| integrity("replica generation"))?;
            let depth = descriptor["index_depth"]
                .as_u64()
                .ok_or_else(|| integrity("replica index depth"))?;
            if descriptor["format_version"] != 4
                || descriptor["container_id"] != source.id.to_string()
                || descriptor["crypto_id"] != s.crypto.id.to_string()
                || descriptor["capacity_bytes"] != s.config.capacity_bytes
                || descriptor["object_size"] != g.object_size
                || descriptor["page_size"] != PAGE
                || !(4..=5).contains(&depth)
                || generation < replica.generation
            {
                return Err(integrity("replica root fields or generation rollback"));
            }
            let digest = hash(raw);
            let sha = hex(&digest);
            let backing = options["backing"].clone();
            let commit = options["commit"].clone();
            let old = c
                .lazy_backing
                .as_ref()
                .ok_or_else(|| invalid("source backing missing"))?;
            for key in [
                "provider_id",
                "account_id",
                "remote_root",
                "source_volume_id",
            ] {
                if backing[key] != old[key] {
                    return Err(integrity("replica backing changed"));
                }
            }
            if backing["reader_pin"].as_str().is_none_or(str::is_empty)
                || backing["root_object_id"] != id.to_string()
                || backing["root_sha256"] != sha
                || commit["volumeId"] != source.id.to_string()
                || commit["rootObjectId"] != id.to_string()
                || commit["rootSha256"]
                    .as_str()
                    .is_none_or(|h| !h.eq_ignore_ascii_case(&sha))
                || commit["generation"] != generation
                || commit["capacityBytes"] != source.capacity_bytes
                || commit["objectSizeBytes"] != source.object_size
                || commit["encrypted"] != source.encrypted
                || commit["formatVersion"] != 4
            {
                return Err(integrity("replica commit differs from authenticated root"));
            }
            if replica.mode == "original"
                && c.binding
                    .as_ref()
                    .is_none_or(|b| b["device_id"] != commit["writerId"])
            {
                return Err(integrity("original writer changed"));
            }
            if generation == replica.generation
                && (id.to_string() != replica.root_object_id || sha != replica.root_sha256)
            {
                return Err(integrity("conflicting roots at same generation"));
            }
            let token = Uuid::new_v4().to_string();
            replica.candidate = Some(Candidate {
                token: token.clone(),
                expected_revision: s.root.data_generation,
                index,
                root_object_id: id.to_string(),
                root_sha256: sha,
                generation,
                pages: 0,
                backing: backing.clone(),
                publication: options.get("publication").cloned(),
                commit,
                ready: false,
            });
            if !replica
                .pins
                .iter()
                .any(|p| p["reader_pin"] == backing["reader_pin"])
            {
                replica.pins.push(backing);
            }
            // Dependency table is authenticated before persisting a candidate.
            s.validate_remote_identity(id, digest)?;
            s.validate_remote_dependencies(raw, oid)?;
            s.transaction(|tx| {
                tx.import_object(id, digest, raw)?;
                tx.register_remote(id, digest)?;
                tx.register_dependencies(raw, oid)?;
                tx.save_cloud(&c)
            })?;
            s.cloud = c;
            (index, depth, token)
        };
        let count = self.with_hydration_reason("replica", || {
            let (_lease, mut reader) = self.read_context()?;
            if !index.empty() && tree::root_level(&mut reader, &PORTMAP, index)? as u64 != depth {
                return Err(integrity("replica root index depth"));
            }
            tree::len(&mut reader, &PORTMAP, index)
        })?;
        if count > self.capacity() / PAGE as u64 {
            return Err(integrity("replica page cardinality"));
        }
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        let mut c = s.cloud.clone();
        let candidate = c
            .replica
            .as_mut()
            .and_then(|r| r.candidate.as_mut())
            .filter(|c| c.token == token)
            .ok_or_else(|| invalid("replica preparation was cancelled"))?;
        candidate.pages = count;
        candidate.ready = true;
        s.transaction(|tx| tx.save_cloud(&c))?;
        s.cloud = c;
        Ok(())
    }
    fn apply_replica(&self, request: &Value) -> Result<()> {
        self.flush()?;
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        s.check()?;
        let mut c = s.cloud.clone();
        let r = c
            .replica
            .as_mut()
            .ok_or_else(|| invalid("not a cloud replica"))?;
        let candidate = r
            .candidate
            .clone()
            .ok_or_else(|| invalid("no prepared replica"))?;
        if !candidate.ready
            || request["token"] != candidate.token
            || request["expected_revision"].as_u64() != Some(candidate.expected_revision)
            || s.root.data_generation != candidate.expected_revision
        {
            return Err(invalid("replica confirmation is stale"));
        }
        if s.root.data_generation != r.local_baseline_generation && request["discard_local"] != true
        {
            return Err(invalid(
                "replacing the replica requires confirmation to discard local changes",
            ));
        }
        if c.job.is_some() {
            return Err(invalid("pending upload prevents replica replacement"));
        }
        // Count only local COW nodes; inherited subtrees remain opaque. This is
        // proportional to local edits, never to the original disk's page count.
        let mut local_counts = BTreeMap::<u64, u64>::new();
        let mut stack = vec![s.root.index];
        while let Some(reference) = stack.pop() {
            if reference.empty() || reference.offset & PORTABLE != 0 {
                continue;
            }
            match tree::visit_node(&mut *s, &PAGES, reference)? {
                Node::Branch { children, .. } => stack.extend(children.into_iter().map(|(_, r)| r)),
                Node::Leaf { values, .. } => {
                    for (_, row) in values {
                        let p = store::Page::decode(&row)?;
                        let o = s.object(p.reference.object)?;
                        if !o.origin_backed {
                            *local_counts.entry(o.oid).or_default() += 1;
                        }
                    }
                }
            }
        }
        let new_generation = s
            .root
            .data_generation
            .checked_add(1)
            .ok_or_else(|| invalid("generation exhausted"))?
            .max(candidate.generation);
        r.source_index = candidate.index;
        r.root_object_id = candidate.root_object_id.clone();
        r.root_sha256 = candidate.root_sha256.clone();
        r.generation = candidate.generation;
        r.local_baseline_generation = if r.mode == "original" {
            candidate.generation
        } else {
            new_generation
        };
        r.counts_complete = CountCoverage::Deferred;
        r.candidate = None;
        r.commit = Some(candidate.commit.clone());
        r.identity.clear();
        r.identity_ready = false;
        c.lazy_backing = Some(candidate.backing.clone());
        let root_oid =
            ordinal(Uuid::parse_str(&candidate.root_object_id).map_err(|_| integrity("root ID"))?);
        if r.mode == "original" {
            c.published_root = root_oid;
            c.published_index = candidate.index;
            c.published_counts = MetaRef::default();
            c.published_generation = candidate.generation;
            c.published_commit = Some(
                json!({"root_object_id":candidate.root_object_id,"root_sha256":candidate.root_sha256,"root_slot":0,"generation":candidate.generation,"receipt":"original-cloud-publication"}),
            );
        } else {
            c.base_root = root_oid;
            c.base_index = candidate.index;
            c.base_counts = MetaRef::default();
        }
        let local_generation = r.local_baseline_generation;
        if let Some(restore) = c.restore.as_mut() {
            restore.index = candidate.index;
            restore.root_id = candidate.root_object_id.clone();
            restore.root_sha256 = candidate.root_sha256.clone();
            restore.generation = candidate.generation;
            restore.expected_pages = candidate.pages;
            restore.phase = "complete".into();
        }
        s.transaction(|tx| {
            for (oid, n) in local_counts {
                let mut object = tx.object(oid)?;
                object.current_refs = object
                    .current_refs
                    .checked_sub(n)
                    .ok_or_else(|| integrity("local reference count during replacement"))?;
                tx.objects.insert(oid, object);
            }
            tx.replace(&PAGES, tx.root.index, candidate.index);
            tx.root.index = candidate.index;
            tx.replace(&DIRTY, tx.root.dirty, MetaRef::default());
            tx.root.dirty = MetaRef::default();
            tx.root.changed_pages = 0;
            tx.root.tails = [0; 5];
            tx.root.allocated_pages = candidate.pages;
            tx.root.data_generation = local_generation;
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        self.shared
            .identity
            .lock()
            .map_err(|_| Error::Poisoned)?
            .clear();
        Ok(())
    }

    /// An original writer reuses its pinned cloud objects without enumerating
    /// them. Only explicitly publishing a copy into a different repository
    /// requires discovering the full set of objects that must be copied there.
    pub(super) fn prepare_replica_counts(&self) -> Result<()> {
        let replica = {
            let s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            match &s.cloud.replica {
                Some(r)
                    if !r.counts_complete.is_complete()
                        && (s.root.changed_pages != 0 || s.cloud.base_root != 0) =>
                {
                    r.clone()
                }
                _ => return Ok(()),
            }
        };
        if replica.mode == "original" {
            let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
            let mut c = s.cloud.clone();
            let backing = c.lazy_backing.as_ref().ok_or_else(|| invalid("source anchor missing"))?;
            let binding = c.binding.as_ref().ok_or_else(|| invalid("source writer missing"))?;
            if backing["provider_id"] != binding["backend_id"]
                || backing["account_id"] != binding["account_id"]
                || backing["remote_root"] != binding["remote_root"]
                || backing["source_volume_id"] != s.config.id.to_string()
                || backing["reader_pin"].as_str().is_none_or(str::is_empty)
                || !replica.pins.iter().any(|pin| pin == backing)
            {
                return Err(invalid("inherited objects require the original pinned cloud repository"));
            }
            let r = c.replica.as_mut().ok_or_else(|| invalid("replica changed"))?;
            if r.counts_complete != CountCoverage::SourceAnchor {
                r.counts_complete = CountCoverage::SourceAnchor;
                s.transaction(|tx| tx.save_cloud(&c))?;
                s.cloud = c;
            }
            return Ok(());
        }
        let depth = self.with_hydration_reason("copy_publish", || {
            let (_lease, mut reader) = self.read_context()?;
            tree::root_level(&mut reader, &PORTMAP, replica.source_index)
        })?;
        let mut stack = vec![(replica.source_index, depth, 0, None)];
        let mut counts = BTreeMap::<u64, u64>::new();
        let rootid =
            ordinal(Uuid::parse_str(&replica.root_object_id).map_err(|_| integrity("root ID"))?);
        counts.insert(rootid, 1);
        while let Some((reference, level, expected_base, expected_count)) = stack.pop() {
            if reference.empty() {
                continue;
            }
            let node = self.with_hydration_reason("copy_publish", || {
                let (_lease, mut reader) = self.read_context()?;
                tree::visit_node(&mut reader, &PORTMAP, reference)
            })?;
            *counts
                .entry((reference.offset & !PORTABLE) / self.object_size())
                .or_default() += 1;
            match node {
                Node::Branch {
                    level: actual,
                    base,
                    children,
                    counts: child_counts,
                } => {
                    if actual != level
                        || base != expected_base
                        || expected_count.is_some_and(|n| n != child_counts.iter().sum::<u64>())
                    {
                        return Err(integrity("source child index shape or count"));
                    }
                    let width = 1u64 << (PORTMAP.leaf_bits as u32 + 6 * (level as u32 - 1));
                    stack.extend(
                        children
                            .into_iter()
                            .zip(child_counts)
                            .map(|((slot, r), n)| {
                                (r, level - 1, base + slot as u64 * width, Some(n))
                            }),
                    );
                }
                Node::Leaf { base, values } => {
                    if level != 0
                        || base != expected_base
                        || expected_count.is_some_and(|n| n != values.len() as u64)
                    {
                        return Err(integrity("source leaf index shape or count"));
                    }
                    for (slot, row) in values {
                        if base + slot as u64 >= self.capacity() / PAGE as u64 {
                            return Err(integrity("source page outside volume"));
                        }
                        *counts
                            .entry(store::Page::decode(&row)?.reference.object)
                            .or_default() += 1;
                    }
                }
            }
        }
        let mut s = self.shared.store.lock().map_err(|_| Error::Poisoned)?;
        let mut c = s.cloud.clone();
        let r = c
            .replica
            .as_mut()
            .ok_or_else(|| invalid("replica changed"))?;
        if r.root_object_id != replica.root_object_id {
            return Err(invalid("replica changed during catalog validation"));
        }
        r.counts_complete = CountCoverage::Complete;
        s.transaction(|tx| {
            let mut root = MetaRef::default();
            let rows = counts
                .into_iter()
                .map(|(id, n)| (id, Some(n.to_le_bytes().to_vec())))
                .collect::<Vec<_>>();
            for batch in rows.chunks(256) {
                root = tx.set(&COUNTS, root, batch)?;
            }
            tx.replace(&COUNTS, c.base_counts, MetaRef::default());
            c.base_counts = root;
            tx.save_cloud(&c)
        })?;
        s.cloud = c;
        Ok(())
    }
}
