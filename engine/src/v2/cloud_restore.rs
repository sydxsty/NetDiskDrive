use super::*;

fn source_crypto(restore: &Value) -> Result<Crypto> {
    let id = Uuid::parse_str(
        restore["source_volume_id"]
            .as_str()
            .ok_or_else(|| invalid("restore source identity"))?,
    )
    .map_err(|_| invalid("restore source UUID"))?;
    let key = if restore["source_key"].is_null() {
        None
    } else {
        Some(Zeroizing::new(
            from_hex(restore["source_key"].as_str().unwrap())?
                .try_into()
                .map_err(|_| integrity("restore key length"))?,
        ))
    };
    Ok(Crypto { id, key })
}
fn need_key(id: &str) -> String {
    format!("restore/need/{id}")
}
fn validate_descriptor(v: &Value) -> Result<()> {
    uuid(v, "id")?;
    if number(v, "length", 0) != OBJECT
        || v["sha256"]
            .as_str()
            .is_none_or(|s| s.len() != 64 || from_hex(s).is_err())
    {
        return Err(integrity("cloud object descriptor"));
    }
    if !matches!(v["kind"].as_str(), Some("index" | "directory" | "data")) {
        return Err(invalid("unexpected restore object kind"));
    }
    Ok(())
}
fn add_need(db: &mut CloudDb, value: Value) -> Result<()> {
    validate_descriptor(&value)?;
    let id = value["id"].as_str().unwrap();
    let key = need_key(id);
    let old = db.get(&key);
    if !old.is_null() {
        if old["sha256"] != value["sha256"] || old["kind"] != value["kind"] {
            return Err(integrity("conflicting object references"));
        }
        return Ok(());
    }
    let mut value = value;
    value["accepted"] = json!(false);
    let mut counts = db.get("restore_counts");
    if counts.is_null() {
        counts = json!({"total":0,"accepted":0,"metadata_pending":0});
    }
    counts["total"] = json!(number(&counts, "total", 0) + 1);
    counts["metadata_pending"] =
        json!(number(&counts, "metadata_pending", 0) + u64::from(value["kind"] != "data"));
    db.set("restore_counts", counts);
    db.set(key, value);
    Ok(())
}
fn validate_needs(db: &CloudDb, values: &[Value]) -> Result<()> {
    let mut seen = BTreeMap::new();
    for value in values {
        validate_descriptor(value)?;
        let id = value["id"].as_str().unwrap();
        let old = db.get(&need_key(id));
        if (!old.is_null() && (old["sha256"] != value["sha256"] || old["kind"] != value["kind"]))
            || seen
                .insert(id, value)
                .is_some_and(|old| old["sha256"] != value["sha256"] || old["kind"] != value["kind"])
        {
            return Err(integrity("conflicting object references"));
        }
    }
    Ok(())
}

fn bootstrap_empty(state: &State) -> bool {
    state.current.restore_required
        && state.current.allocated_pages == 0
        && state.current.index.empty()
        && state.current.data_generation == 0
        && state.snapshots.is_empty()
        && state.cloud.values.is_empty()
        && state.dirty.is_empty()
        && state.trims.is_empty()
        && state.inflight.is_none()
}
fn local_pin(state: &State, target: Uuid) -> Result<Option<Snapshot>> {
    let ids: Vec<_> = state
        .cloud
        .prefix_iter("snapshot/")
        .filter(|(_, label)| {
            label["pin"] == "restore" && label["target_volume_id"] == target.to_string()
        })
        .map(|(_, label)| string(label, "id"))
        .collect::<Result<_>>()?;
    if ids.len() > 1 {
        return Err(integrity("ambiguous local restore pins"));
    }
    ids.first()
        .map(|id| {
            state
                .snapshots
                .iter()
                .find(|s| &s.id == id)
                .cloned()
                .ok_or_else(|| integrity("local restore pin catalog is missing"))
        })
        .transpose()
}
#[cfg(test)]
fn bootstrap_crash_point(stage: &str) {
    if std::env::var("ODV3_RESTORE_CRASH_STAGE").ok().as_deref() == Some(stage) {
        println!("ODV3-RESTORE-CRASH-READY");
        use std::io::Write;
        std::io::stdout().flush().unwrap();
        loop {
            thread::park_timeout(Duration::from_secs(1));
        }
    }
}
#[cfg(not(test))]
fn bootstrap_crash_point(_stage: &str) {}

impl Volume {
    fn open_restore_bootstrap(path: &Path, capacity: u64, password: Option<&str>) -> Result<Self> {
        let target = if path.try_exists()? {
            Self::open(path, password)?
        } else {
            Self::create_format(path, capacity, password, 3, true)?
        };
        target.require_v3()?;
        {
            let state = target.shared.state.lock().map_err(|_| Error::Poisoned)?;
            check_failure(&state)?;
            if target.capacity() != capacity || !bootstrap_empty(&state) {
                return Err(invalid(
                    "target is not an authenticated empty restore bootstrap",
                ));
            }
        }
        Ok(target)
    }
    fn seed_local_restore(&self, source: &Volume, pin: &Snapshot) -> Result<()> {
        let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
        let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        check_failure(&state)?;
        if self.capacity() != source.capacity() || !bootstrap_empty(&state) {
            return Err(invalid(
                "target is not an authenticated empty restore bootstrap",
            ));
        }
        state.cloud.set("restore",json!({"kind":"local","phase":"data","source_volume_id":source.shared.config.id,"source_snapshot_id":pin.id,"expected_pages":pin.allocated_pages,"completed_pages":0,"cursor":0}));
        self.shared.persist(&mut state, false)
    }
    fn mark_restore_header_complete(&self) -> Result<()> {
        let mut config = self.shared.config.clone();
        config.restoring = false;
        let bytes = config.encode()?;
        self.shared.file.write(3 * PAGE as u64, &bytes)?;
        self.shared.file.sync()?;
        self.shared.file.write(0, &bytes)?;
        self.shared.file.sync()
    }
    pub fn restore_begin_v3(
        path: impl AsRef<Path>,
        image: &[u8],
        password: Option<&str>,
    ) -> Result<Self> {
        if image.len() != OBJECT as usize || image[8] != 3 {
            return Err(invalid("restore requires an exact 4 MiB commit object"));
        }
        let config = Config::decode(image[PAGE..2 * PAGE].try_into().unwrap())?;
        let crypto = config.unlock(password)?;
        let (_, id, body) = decode_image(&crypto, image, None)?;
        let root: Value = serde_json::from_slice(&body)?;
        if root["cloud_format"] != 1
            || root["volume_id"] != config.id.to_string()
            || number(&root, "capacity_bytes", 0) != config.capacity_bytes
            || config.capacity_bytes > 1024 * 1024 * 1024 * 1024
        {
            return Err(integrity("authenticated restore root/config mismatch"));
        }
        let dirs = root["directories"]
            .as_array()
            .ok_or_else(|| invalid("root directory list"))?;
        if dirs.len() > 32768 || dirs.iter().any(|d| d["kind"] != "directory") {
            return Err(invalid("invalid root directory list"));
        }
        validate_needs(&CloudDb::default(), dirs)?;
        if number(&root, "allocated_pages", u64::MAX) > config.capacity_bytes / PAGE as u64 {
            return Err(integrity("restore root page count"));
        }
        let target = Self::open_restore_bootstrap(
            path.as_ref(),
            config.capacity_bytes,
            if config.encrypted { password } else { None },
        )?;
        if target.shared.config.encrypted != config.encrypted {
            return Err(invalid("restore target encryption mode mismatch"));
        }
        bootstrap_crash_point("cloud-target-created");
        {
            let mut state = target.shared.state.lock().map_err(|_| Error::Poisoned)?;
            for d in dirs {
                add_need(&mut state.cloud, d.clone())?;
            }
            state.cloud.set("restore",json!({"phase":"metadata","kind":"cloud","root_object_id":id,"root_sha256":digest(image),"source_volume_id":config.id,"source_key":crypto.key.as_ref().map(|k|codec::hex(k.as_ref())),"generation":root["generation"],"expected_pages":root["allocated_pages"],"completed_pages":0}));
            target.shared.persist(&mut state, false)?;
        }
        Ok(target)
    }
    pub fn restore_accept_v3(&self, id: &str, image: &[u8]) -> Result<()> {
        self.require_v3()?;
        let expected = Uuid::parse_str(id).map_err(|_| invalid("restore object ID"))?;
        if image.len() != OBJECT as usize {
            return Err(invalid("restore object length"));
        }
        let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
        let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        check_failure(&state)?;
        let mut restore = state.cloud.get("restore");
        if restore["kind"] != "cloud" || restore["phase"] == "complete" {
            return Err(invalid("no active cloud restore"));
        }
        let key = need_key(id);
        let mut need = state.cloud.get(&key);
        if need.is_null() || need["sha256"] != digest(image) {
            return Err(integrity("restore object hash/identity"));
        }
        if need["accepted"] == true {
            return Ok(());
        }
        let crypto = source_crypto(&restore)?;
        let (kind, _, body) = decode_image(&crypto, image, Some(expected))?;
        match kind {
            2 => {
                if need["kind"] != "directory" {
                    return Err(integrity("directory object kind"));
                }
                let list: Vec<Value> = serde_json::from_slice(&body)?;
                if list.len() > 1024 {
                    return Err(invalid("manifest directory too large"));
                }
                if list.iter().any(|entry| entry["kind"] != "index") {
                    return Err(invalid("directory child kind"));
                }
                validate_needs(&state.cloud, &list)?;
                for entry in list {
                    add_need(&mut state.cloud, entry)?;
                }
            }
            1 => {
                if need["kind"] != "index" {
                    return Err(integrity("index object kind"));
                }
                let groups = parse_map(&body)?;
                let mut needs = Vec::with_capacity(groups.len());
                for (oid, hash, rows) in groups {
                    let existing = state.cloud.get(&need_key(&oid.to_string()));
                    if !existing.is_null() && existing["map_id"] != id {
                        return Err(integrity("one data object appears in multiple map groups"));
                    }
                    for (p, _, _) in &rows {
                        if *p >= self.capacity().div_ceil(PAGE as u64) {
                            return Err(integrity("restored page outside volume"));
                        }
                    }
                    needs.push(
                        json!({"id":oid,"kind":"data","length":OBJECT,"sha256":hash,"map_id":id}),
                    );
                }
                validate_needs(&state.cloud, &needs)?;
                for value in needs {
                    add_need(&mut state.cloud, value)?;
                }
                self.shared.allocate_spool(&mut state, id, image)?;
            }
            0 => {
                if need["kind"] != "data" {
                    return Err(integrity("data object kind"));
                }
                if number(&state.cloud.get("restore_counts"), "metadata_pending", 0) != 0 {
                    return Err(invalid("restore metadata must finish before data"));
                }
                let mapid = string(&need, "map_id")?;
                let map = self.shared.read_spool(&state, &mapid)?;
                let (_, _, body) = decode_image(&crypto, &map, None)?;
                let group = parse_map(&body)?
                    .into_iter()
                    .find(|(oid, _, _)| *oid == expected)
                    .ok_or_else(|| integrity("data object missing from its index"))?;
                let mut decoded = Vec::with_capacity(group.2.len());
                let mut seen = BTreeSet::new();
                for (page, _, reference) in group.2 {
                    if !seen.insert(page)
                        || state.dirty.contains_key(&page)
                        || self.shared.lookup(&state.current, page)?.is_some()
                    {
                        return Err(integrity("duplicate restored logical page"));
                    }
                    let offset = 17 * PAGE + reference.slot as usize * PAGE;
                    let mut bytes = Zeroizing::new([0; PAGE]);
                    bytes.copy_from_slice(&image[offset..offset + PAGE]);
                    crypto.decode_page(page, reference, &mut bytes)?;
                    if bytes.iter().all(|b| *b == 0) {
                        return Err(integrity("allocated cloud page is unexpectedly zero"));
                    }
                    decoded.push((page, bytes));
                }
                let count = decoded.len();
                for (p, b) in decoded {
                    Arc::make_mut(&mut state.dirty).insert(p, b);
                }
                restore["completed_pages"] =
                    json!(number(&restore, "completed_pages", 0) + count as u64);
                state.cloud.set("restore", restore);
            }
            _ => return Err(invalid("unexpected nested commit object")),
        }
        let mut counts = state.cloud.get("restore_counts");
        counts["accepted"] = json!(number(&counts, "accepted", 0) + 1);
        counts["metadata_pending"] =
            json!(number(&counts, "metadata_pending", 0) - u64::from(need["kind"] != "data"));
        state.cloud.set("restore_counts", counts);
        need["accepted"] = json!(true);
        state.cloud.set(key, need);
        self.shared.persist(&mut state, false)?;
        Ok(())
    }
    pub(super) fn restore_control(&self, r: &Value) -> Result<Value> {
        let cmd = string(r, "cmd")?;
        if cmd == "snapshot.restore_step" {
            return self.local_restore_step(number(r, "max_pages", 256).clamp(1, 1024) as usize);
        }
        let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
        let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        let mut restore = state.cloud.get("restore");
        if restore.is_null() {
            if cmd == "restore.status" && bootstrap_empty(&state) {
                return Ok(
                    json!({"phase":"initializing","kind":"uninitialized","needed":[],"items":[],"next_cursor":null,"completed_objects":0,"total_objects":0,"completed_pages":0,"total_pages":0,"target_volume_id":self.shared.config.id,"capacity_bytes":self.capacity(),"encrypted":self.shared.config.encrypted}),
                );
            }
            return Err(invalid("no restore state"));
        }
        if cmd == "restore.finish" {
            check_failure(&state)?;
            if restore["phase"] != "complete" {
                let counts = state.cloud.get("restore_counts");
                if restore["kind"] != "cloud"
                    || number(&counts, "accepted", 0) != number(&counts, "total", 0)
                    || state.current.allocated_pages != number(&restore, "expected_pages", u64::MAX)
                {
                    return Err(invalid("restore is not complete"));
                }
                restore["phase"] = json!("complete");
                restore["source_key"] = Value::Null;
                state.cloud.set("restore", restore.clone());
                state.cloud.set("restore_finished", json!(true));
                state.current.restore_required = false;
                Arc::make_mut(&mut state.cloud.values).retain(|key, _| {
                    !key.starts_with("restore/need/") && !key.starts_with("spool/")
                });
                state.cloud.force_checkpoint = true;
                self.shared.persist(&mut state, false)?;
            }
        }
        if cmd == "restore.finish" {
            self.mark_restore_header_complete()?;
        }
        let counts = state.cloud.get("restore_counts");
        let metadata = number(&counts, "metadata_pending", 0) > 0;
        let needed_count = if metadata {
            number(&counts, "metadata_pending", 0)
        } else {
            number(&counts, "total", 0) - number(&counts, "accepted", 0)
        } as usize;
        let cursor = number(r, "cursor", 0) as usize;
        let limit = number(r, "limit", 128).clamp(1, 128) as usize;
        let items: Vec<_> = state
            .cloud
            .prefix_iter("restore/need/")
            .filter(|(_, v)| v["accepted"] != true && (!metadata || v["kind"] != "data"))
            .skip(cursor)
            .take(limit)
            .map(|(_, v)| v.clone())
            .collect();
        let next = cursor + items.len();
        let phase = if restore["phase"] == "complete" {
            "complete"
        } else if restore["kind"] == "local" {
            if self
                .shared
                .restore_source
                .lock()
                .map_err(|_| Error::Poisoned)?
                .is_none()
            {
                "needs_source"
            } else {
                "data"
            }
        } else if metadata {
            "metadata"
        } else if needed_count == 0 {
            "ready"
        } else {
            "data"
        };
        Ok(
            json!({"phase":phase,"kind":restore["kind"],"needed":items,"items":items,"next_cursor":if next<needed_count{Some(next)}else{None},"completed_objects":number(&counts,"accepted",0),"total_objects":number(&counts,"total",0),"completed_pages":restore["completed_pages"],"total_pages":restore["expected_pages"],"root_object_id":restore["root_object_id"],"root_sha256":restore["root_sha256"],"source_volume_id":restore["source_volume_id"],"generation":restore["generation"],"capacity_bytes":self.capacity(),"encrypted":self.shared.config.encrypted,"source_snapshot_id":restore["source_snapshot_id"],"source_pin_cleanup_pending":restore["source_pin_cleanup_pending"]}),
        )
    }
    pub fn restore_snapshot_begin_v3(
        source: Arc<Volume>,
        snapshot_id: &str,
        path: impl AsRef<Path>,
        password: Option<&str>,
    ) -> Result<Self> {
        source.require_v3()?;
        source.require_restored()?;
        let target = Self::open_restore_bootstrap(path.as_ref(), source.capacity(), password)?;
        bootstrap_crash_point("local-target-created");
        let clone = {
            let _epoch = source.shared.epoch.write().map_err(|_| Error::Poisoned)?;
            let mut state = source.shared.state.lock().map_err(|_| Error::Poisoned)?;
            check_failure(&state)?;
            if let Some(pin) = local_pin(&state, target.shared.config.id)? {
                pin
            } else {
                if state.cloud.get(&format!("snapshot/{snapshot_id}"))["pin"] != "user" {
                    return Err(invalid("only a user snapshot can be restored"));
                }
                if state.snapshots.len() >= 1024 {
                    return Err(invalid("snapshot limit reached"));
                }
                let original = state
                    .snapshots
                    .iter()
                    .find(|s| s.id == snapshot_id)
                    .cloned()
                    .ok_or_else(|| invalid("source snapshot missing"))?;
                let mut clone = original;
                clone.id = Uuid::new_v4().to_string();
                state.snapshots.push(clone.clone());
                state.cloud.set(format!("snapshot/{}",clone.id),json!({"id":clone.id,"name":"Local restore pin","generation":clone.seq,"allocated_pages":clone.allocated_pages,"created_utc":now(),"upload_pin":false,"pin":"restore","target_volume_id":target.shared.config.id}));
                source.shared.persist(&mut state, true)?;
                clone
            }
        };
        bootstrap_crash_point("local-pin-created");
        target.seed_local_restore(&source, &clone)?;
        *target
            .shared
            .restore_source
            .lock()
            .map_err(|_| Error::Poisoned)? = Some(LocalRestoreSource {
            volume: source,
            snapshot: clone.id,
        });
        Ok(target)
    }
    pub fn restore_snapshot_resume_v3(&self, source: Arc<Volume>) -> Result<()> {
        self.require_v3()?;
        source.require_v3()?;
        source.require_restored()?;
        let initialize = {
            let state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
            bootstrap_empty(&state)
        };
        if initialize {
            let pin = {
                let state = source.shared.state.lock().map_err(|_| Error::Poisoned)?;
                check_failure(&state)?;
                local_pin(&state,self.shared.config.id)?.ok_or_else(||invalid("restore bootstrap has no durable source pin; retry Begin with the original user snapshot"))?
            };
            self.seed_local_restore(&source, &pin)?;
        }
        let state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        let restore = state.cloud.get("restore");
        if restore["kind"] != "local"
            || restore["source_volume_id"] != source.shared.config.id.to_string()
        {
            return Err(invalid("local restore source mismatch"));
        }
        let id = string(&restore, "source_snapshot_id")?;
        if restore["phase"] == "complete" {
            drop(state);
            self.mark_restore_header_complete()?;
            return self.finish_local_source_pin(&source, &id);
        }
        source.snapshot(&id)?;
        *self
            .shared
            .restore_source
            .lock()
            .map_err(|_| Error::Poisoned)? = Some(LocalRestoreSource {
            volume: source,
            snapshot: id,
        });
        Ok(())
    }
    fn finish_local_source_pin(&self, source: &Volume, id: &str) -> Result<()> {
        {
            let _epoch = source.shared.epoch.write().map_err(|_| Error::Poisoned)?;
            let mut state = source.shared.state.lock().map_err(|_| Error::Poisoned)?;
            check_failure(&state)?;
            state.snapshots.retain(|s| s.id != id);
            state.cloud.remove(&format!("snapshot/{id}"));
            source.shared.persist(&mut state, true)?;
        }
        {
            let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
            let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
            let mut restore = state.cloud.get("restore");
            restore["source_pin_cleanup_pending"] = json!(false);
            state.cloud.set("restore", restore);
            self.shared.persist(&mut state, false)?;
        }
        *self
            .shared
            .restore_source
            .lock()
            .map_err(|_| Error::Poisoned)? = None;
        Ok(())
    }
    fn local_restore_step(&self, limit: usize) -> Result<Value> {
        let mut restore = {
            let state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
            state.cloud.get("restore")
        };
        if restore["phase"] == "complete" {
            self.mark_restore_header_complete()?;
            return Ok(restore);
        }
        let (source, id) = {
            let hold = self
                .shared
                .restore_source
                .lock()
                .map_err(|_| Error::Poisoned)?;
            let s = hold
                .as_ref()
                .ok_or_else(|| invalid("resume requires the unlocked source volume"))?;
            (s.volume.clone(), s.snapshot.clone())
        };
        let cursor = number(&restore, "cursor", 0);
        let snapshot = source.snapshot(&id)?;
        let data = {
            let _epoch = source.shared.epoch.read().map_err(|_| Error::Poisoned)?;
            let pages = source.shared.snapshot_pages(&snapshot, cursor, limit)?;
            let root = Root {
                seq: snapshot.seq,
                index: snapshot.index,
                depth: snapshot.depth,
                allocated_pages: snapshot.allocated_pages,
                next_generation: 0,
                data_tail: None,
                meta_tail: None,
                catalog: MetaRef::default(),
                journal: MetaRef::default(),
                seals: snapshot.seals,
                data_generation: 0,
                cloud_checkpoint: MetaRef::default(),
                cloud_log: MetaRef::default(),
                restore_required: false,
            };
            let mut data = Vec::with_capacity(pages.len());
            for (page, _) in pages {
                data.push((page, source.shared.read_page(&root, page)?));
            }
            data
        };
        let done = data.len() < limit;
        {
            let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
            let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
            check_failure(&state)?;
            if let Some((page, _)) = data.last() {
                restore["cursor"] = json!(page + 1);
            }
            restore["completed_pages"] =
                json!(number(&restore, "completed_pages", 0) + data.len() as u64);
            for (page, bytes) in data {
                Arc::make_mut(&mut state.dirty).insert(page, bytes);
            }
            if done {
                restore["phase"] = json!("complete");
                state.cloud.set("restore_finished", json!(true));
                state.current.restore_required = false;
                restore["source_pin_cleanup_pending"] = json!(true);
            }
            state.cloud.set("restore", restore.clone());
            self.shared.persist(&mut state, false)?;
        }
        if done {
            self.mark_restore_header_complete()?;
            self.finish_local_source_pin(&source, &id)?;
        }
        Ok(restore)
    }
}
type PortablePage = (u64, Uuid, PageRef);
type PortableGroup = (Uuid, String, Vec<PortablePage>);
fn parse_map(bytes: &[u8]) -> Result<Vec<PortableGroup>> {
    if bytes.len() < 4 {
        return Err(integrity("map header"));
    }
    let count = u32::from_le_bytes(bytes[..4].try_into().unwrap()) as usize;
    if count > GROUP_OBJECTS as usize {
        return Err(integrity("map object count"));
    }
    let mut at = 4;
    let mut out = Vec::with_capacity(count);
    let mut seen = BTreeSet::new();
    for _ in 0..count {
        if at + 52 > bytes.len() {
            return Err(integrity("map object header"));
        }
        let id = Uuid::from_slice(&bytes[at..at + 16]).map_err(|_| integrity("map UUID"))?;
        if !seen.insert(id) {
            return Err(integrity("duplicate map object"));
        }
        let hash = codec::hex(&bytes[at + 16..at + 48]);
        let n = u32::from_le_bytes(bytes[at + 48..at + 52].try_into().unwrap()) as usize;
        at += 52;
        if n > SLOTS as usize || at + n * 88 > bytes.len() {
            return Err(integrity("map page bounds"));
        }
        let mut rows = Vec::with_capacity(n);
        for record in bytes[at..at + n * 88].chunks_exact(88) {
            let row = decode_record(record)?;
            if row.1 != id {
                return Err(integrity("page object identity"));
            }
            rows.push(row);
        }
        at += n * 88;
        out.push((id, hash, rows));
    }
    if at != bytes.len() {
        return Err(integrity("map trailing bytes"));
    }
    Ok(out)
}
