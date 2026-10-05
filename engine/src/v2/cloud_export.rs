use super::*;

fn job_key(id: &str, object: &str) -> String {
    format!("job/{id}/object/{object}")
}
fn page(v: &Value, key: &str) -> u64 {
    v[key].as_u64().unwrap_or(0)
}
impl Volume {
    fn status_locked(&self, state: &State) -> Value {
        let binding = state.cloud.get("binding");
        let job = state.cloud.get("job");
        let published = state.cloud.number("published_generation");
        json!({"bound":!binding.is_null(),"enabled":binding["enabled"].as_bool().unwrap_or(false),"paused":state.cloud.get("paused").as_bool().unwrap_or(false),"backend_id":binding["backend_id"],"account_id":binding["account_id"],"remote_root":binding["remote_root"],"data_generation":state.current.data_generation,"published_generation":published,"pending_generation":state.current.data_generation,"local_dirty":!state.dirty.is_empty()||!state.trims.is_empty()||state.inflight.is_some(),"estimated_bytes":job["estimated_bytes"].as_u64().unwrap_or(0),"job":job,"last_error":state.failure})
    }
    pub(super) fn cloud_status(&self) -> Result<Value> {
        let state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        Ok(self.status_locked(&state))
    }
    pub(super) fn cloud_objects(&self, request: &Value) -> Result<Value> {
        let state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        let id = uuid(request, "job_id")?;
        let current = state.cloud.get("job");
        if current["id"] != id {
            return Err(invalid("upload job not found"));
        }
        let prefix = format!("job/{id}/object/");
        let count = state.cloud.prefix_count(&prefix);
        let cursor = number(request, "cursor", 0) as usize;
        let limit = number(request, "limit", 128).clamp(1, 128) as usize;
        if cursor > count {
            return Err(invalid("cursor outside list"));
        }
        let items: Vec<_> = state
            .cloud
            .prefix_page(&prefix, cursor, limit)
            .iter()
            .map(|(_, v)| public_object(v))
            .collect();
        let next = cursor + items.len();
        Ok(
            json!({"items":items,"next_cursor":if next<count{Some(next)}else{None},"total_count":count}),
        )
    }
    pub(super) fn published_objects(&self, request: &Value) -> Result<Value> {
        let state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        let count = state.cloud.prefix_count("published/");
        let cursor = number(request, "cursor", 0) as usize;
        let limit = number(request, "limit", 128).clamp(1, 128) as usize;
        let items: Vec<_> = state
            .cloud
            .prefix_page("published/", cursor, limit)
            .into_iter()
            .map(|(_, v)| v)
            .collect();
        let next = cursor + items.len();
        Ok(
            json!({"items":items,"next_cursor":if next<count{Some(next)}else{None},"total_count":count}),
        )
    }
    pub(super) fn cloud_update(&self, r: &Value) -> Result<Value> {
        let cmd = string(r, "cmd")?;
        let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
        let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        check_failure(&state)?;
        let mut catalog = false;
        match cmd.as_str() {
            "cloud.bind" => {
                if !state.cloud.get("job").is_null() {
                    return Err(invalid(
                        "finish the active upload before changing its binding",
                    ));
                }
                for key in ["backend_id", "account_id", "remote_root", "device_id"] {
                    string(r, key)?;
                }
                let old = state.cloud.get("binding");
                if !old.is_null()
                    && (old["account_id"] != r["account_id"]
                        || old["remote_root"] != r["remote_root"]
                        || old["backend_id"] != r["backend_id"])
                {
                    for (key, _) in state.cloud.prefix("receipt/") {
                        state.cloud.remove(&key);
                    }
                    for (key, _) in state.cloud.prefix("published/") {
                        state.cloud.remove(&key);
                    }
                    state.cloud.set("published_generation", json!(0));
                    state.cloud.remove("published_commit");
                }
                state.cloud.set("binding",json!({"backend_id":r["backend_id"],"account_id":r["account_id"],"remote_root":r["remote_root"],"device_id":r["device_id"],"enabled":r["enabled"].as_bool().unwrap_or(true)}));
            }
            "cloud.pause" => state
                .cloud
                .set("paused", json!(r["paused"].as_bool().unwrap_or(true))),
            "cloud.receipt" => {
                let job_id = uuid(r, "job_id")?;
                let job = state.cloud.get("job");
                if job["id"] != job_id {
                    return Err(invalid("job identity changed"));
                }
                let id = uuid(r, "object_id")?;
                let key = job_key(&job_id, &id);
                let mut object = state.cloud.get(&key);
                if object.is_null()
                    || object["sha256"] != r["sha256"]
                    || number(r, "length", 0) != OBJECT
                {
                    return Err(integrity("receipt does not match the prepared object"));
                }
                let receipt = string(r, "receipt")?;
                if receipt.len() > 512 {
                    return Err(invalid("receipt exceeds 512 bytes"));
                }
                object["uploaded"] = json!(true);
                put_job_object(&mut state.cloud, &job_id, object.clone());
                state.cloud.set(
                    format!("receipt/{id}"),
                    json!({"sha256":object["sha256"],"receipt":receipt}),
                );
                update_job_counts(&mut state.cloud);
            }
            "cloud.commit" => {
                let id = uuid(r, "job_id")?;
                let job = state.cloud.get("job");
                if job["id"] != id
                    || job["phase"] != "ready"
                    || job["root_object_id"] != r["root_object_id"]
                    || job["root_sha256"] != r["root_sha256"]
                {
                    return Err(invalid("upload is not ready for verified publication"));
                }
                let receipt = string(r, "receipt")?;
                if receipt.len() > 512 {
                    return Err(invalid("receipt exceeds 512 bytes"));
                }
                for (key, _) in state.cloud.prefix("published/") {
                    state.cloud.remove(&key);
                }
                // Only the latest remote closure survives remote GC. Older
                // receipts cannot prove an object is still remotely available.
                for (key, _) in state.cloud.prefix("receipt/") {
                    state.cloud.remove(&key);
                }
                for (_, object) in state.cloud.prefix(&format!("job/{id}/object/")) {
                    let oid = object["id"].as_str().unwrap();
                    state
                        .cloud
                        .set(format!("published/{oid}"), public_object(&object));
                    state.cloud.set(
                        format!("receipt/{oid}"),
                        json!({"sha256":object["sha256"],"receipt":receipt}),
                    );
                    state.cloud.remove(&format!("spool/{oid}"));
                }
                let snapshot = job["snapshot_id"]
                    .as_str()
                    .ok_or_else(|| invalid("job pin absent"))?;
                state.snapshots.retain(|s| s.id != snapshot);
                state.cloud.remove(&format!("snapshot/{snapshot}"));
                state
                    .cloud
                    .set("published_generation", job["generation"].clone());
                state.cloud.set("published_commit",json!({"root_object_id":r["root_object_id"],"root_sha256":r["root_sha256"],"generation":job["generation"]}));
                for (key, _) in state.cloud.prefix(&format!("job/{id}/")) {
                    state.cloud.remove(&key);
                }
                for (key, _) in state.cloud.prefix(&format!("draft/{id}/")) {
                    state.cloud.remove(&key);
                }
                state.cloud.remove("job");
                catalog = true;
            }
            _ => return Err(invalid("unknown cloud update")),
        }
        if cmd == "cloud.commit" {
            self.shared.prune_cloud(&mut state)?;
        }
        self.shared.persist(&mut state, catalog)?;
        let status = self.status_locked(&state);
        if cmd == "cloud.receipt" {
            Ok(json!({"job":state.cloud.get("job")}))
        } else {
            Ok(json!({"status":status}))
        }
    }
    pub(super) fn cloud_prepare(&self, r: &Value) -> Result<Value> {
        self.flush()?;
        let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
        let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        check_failure(&state)?;
        if state.current.restore_required || state.cloud.restore_incomplete() {
            return Err(invalid("cannot upload an incomplete restore"));
        }
        if state.cloud.get("paused").as_bool().unwrap_or(false) {
            return Err(invalid("cloud sync is paused"));
        }
        if state.cloud.get("job").is_null() {
            if !state.cloud.get("published_commit").is_null()
                && state.cloud.number("published_generation") >= state.current.data_generation
                && state.dirty.is_empty()
                && state.trims.is_empty()
            {
                return Ok(
                    json!({"job":null,"up_to_date":true,"status":self.status_locked(&state)}),
                );
            }
            if !state.dirty.is_empty() || !state.trims.is_empty() {
                self.shared.persist(&mut state, false)?;
            }
            let id = Uuid::new_v4().to_string();
            let snapshot = self
                .shared
                .capture(&mut state, "Automatic upload", "upload")?;
            let job = json!({"id":id,"snapshot_id":snapshot.id,"generation":state.current.data_generation,"phase":"preparing","stage":"scan","cursor":0,"prepared_pages":0,"total_pages":snapshot.allocated_pages,"total_objects":0,"uploaded_objects":0,"estimated_bytes":0,"root_object_id":null,"root_sha256":null});
            state.cloud.set("job", job.clone());
            self.shared.persist(&mut state, true)?;
            return Ok(json!({"job":job}));
        }
        let mut job = state.cloud.get("job");
        let id = job["id"].as_str().unwrap().to_owned();
        if r["job_id"].as_str().is_some_and(|x| x != id) {
            return Err(invalid("upload job identity changed"));
        }
        if job["phase"] != "preparing" {
            return Ok(json!({"job":job}));
        }
        let snapshot = state
            .snapshots
            .iter()
            .find(|s| Some(s.id.as_str()) == job["snapshot_id"].as_str())
            .cloned()
            .ok_or_else(|| invalid("upload pin missing"))?;
        match job["stage"].as_str().unwrap_or("scan") {
            "scan" => {
                let limit = number(r, "max_pages", 1024).clamp(1, 4096) as usize;
                let pages = self
                    .shared
                    .snapshot_pages(&snapshot, page(&job, "cursor"), limit)?;
                let mut groups: BTreeMap<u64, Vec<u8>> = BTreeMap::new();
                for (logical, reference) in &pages {
                    let header = self.shared.object_header(reference.object)?;
                    let key = format!("source/{}", header.id);
                    let mut source = state.cloud.get(&key);
                    if source.is_null() {
                        let ordinal = state.cloud.number("next_ordinal");
                        state.cloud.set("next_ordinal", json!(ordinal + 1));
                        let mut nonce = [0; 24];
                        OsRng.fill_bytes(&mut nonce);
                        source = json!({"id":header.id,"kind":"data","length":OBJECT,"ordinal":ordinal,"nonce":codec::hex(&nonce),"used_slots":state.cloud.values.get(&format!("highwater/{}",header.id)).and_then(Value::as_u64).unwrap_or(SLOTS)});
                        state.cloud.set(&key, source.clone());
                    }
                    let group = page(&source, "ordinal") / GROUP_OBJECTS;
                    groups
                        .entry(group)
                        .or_default()
                        .extend_from_slice(&page_record(*logical, *reference, header.id));
                    let obj_key = job_key(&id, &header.id.to_string());
                    if state.cloud.get(&obj_key).is_null() {
                        let mut obj = source.clone();
                        obj["source_number"] = json!(reference.object);
                        obj["group"] = json!(group);
                        obj["uploaded"] = json!(false);
                        put_job_object(&mut state.cloud, &id, obj);
                    }
                }
                for (group, records) in groups {
                    let key = format!("draft/{id}/{group:016x}");
                    let previous: MetaRef =
                        serde_json::from_value(state.cloud.get(&key)["head"].clone())
                            .unwrap_or_default();
                    let mut payload = vec![0; 40];
                    previous.put(&mut payload);
                    payload.extend(records);
                    let head = self.shared.append_cloud_blob(&mut state, &payload)?;
                    state.cloud.set(key, json!({"head":head,"group":group}));
                }
                if let Some((last, _)) = pages.last() {
                    job["cursor"] = json!(last + 1);
                }
                job["prepared_pages"] = json!(page(&job, "prepared_pages") + pages.len() as u64);
                if pages.len() < limit {
                    job["stage"] = json!("data");
                    job["object_cursor"] = json!(0);
                }
            }
            "data" => {
                let prefix = format!("job/{id}/object/");
                let total =
                    number(&state.cloud.get(&format!("job/{id}/counts")), "total", 0) as usize;
                let cursor = page(&job, "object_cursor") as usize;
                let count = number(r, "max_objects", 2).clamp(1, 4) as usize;
                let after = job["object_after"].as_str().unwrap_or(&prefix).to_owned();
                let batch: Vec<_> = state
                    .cloud
                    .values
                    .range((std::ops::Bound::Excluded(after), std::ops::Bound::Unbounded))
                    .take_while(|(k, _)| k.starts_with(&prefix))
                    .take(count)
                    .map(|(k, v)| (k.clone(), v.clone()))
                    .collect();
                for (key, mut object) in batch {
                    if object["sha256"].as_str().is_none() {
                        let bytes = self.data_image(&object)?;
                        // A freshly hashed object must also contain authentic
                        // live pages; a checksum of already-corrupt ciphertext
                        // must never be published as a successful backup.
                        let draft = state
                            .cloud
                            .get(&format!("draft/{id}/{:016x}", page(&object, "group")));
                        let head: MetaRef = serde_json::from_value(draft["head"].clone())?;
                        for row in self.draft_records(head)?.chunks_exact(88) {
                            let (logical, source, reference) = decode_record(row)?;
                            if source.to_string() != object["id"].as_str().unwrap() {
                                continue;
                            }
                            let start = 17 * PAGE + reference.slot as usize * PAGE;
                            let mut plain = Zeroizing::new([0; PAGE]);
                            plain.copy_from_slice(&bytes[start..start + PAGE]);
                            self.shared
                                .crypto
                                .decode_page(logical, reference, &mut plain)?;
                        }
                        object["sha256"] = json!(digest(&bytes));
                        let source_key = format!("source/{}", object["id"].as_str().unwrap());
                        let mut source = state.cloud.get(&source_key);
                        source["sha256"] = object["sha256"].clone();
                        state.cloud.set(source_key, source);
                    }
                    object["uploaded"] = json!(has_receipt(&state.cloud, &object));
                    put_job_object(&mut state.cloud, &id, object);
                    job["object_after"] = json!(key);
                }
                let next = (cursor + count).min(total);
                job["object_cursor"] = json!(next);
                if next == total {
                    job["stage"] = json!("index");
                    job["group_cursor"] = json!(0);
                }
            }
            "index" => {
                let prefix = format!("draft/{id}/");
                let total = state.cloud.prefix_count(&prefix);
                let cursor = page(&job, "group_cursor") as usize;
                let group_page = state.cloud.prefix_page(&prefix, cursor, 1);
                if let Some((_, group)) = group_page.first() {
                    let head: MetaRef = serde_json::from_value(group["head"].clone())?;
                    let records = self.draft_records(head)?;
                    let mut by_object: BTreeMap<Uuid, Vec<(u64, PageRef)>> = BTreeMap::new();
                    for chunk in records.chunks_exact(88) {
                        let (p, oid, r) = decode_record(chunk)?;
                        by_object.entry(oid).or_default().push((p, r));
                    }
                    let mut body = Vec::new();
                    body.extend_from_slice(&(by_object.len() as u32).to_le_bytes());
                    for (oid, mut rows) in by_object {
                        rows.sort_by_key(|x| x.0);
                        let object = state.cloud.get(&job_key(&id, &oid.to_string()));
                        body.extend_from_slice(oid.as_bytes());
                        body.extend_from_slice(&from_hex(
                            object["sha256"]
                                .as_str()
                                .ok_or_else(|| invalid("data hash absent"))?,
                        )?);
                        body.extend_from_slice(&(rows.len() as u32).to_le_bytes());
                        for (p, r) in rows {
                            body.extend_from_slice(&page_record(p, r, oid));
                        }
                    }
                    let object = self.prepare_image(&mut state, 1, &body, None)?;
                    put_job_object(&mut state.cloud, &id, object.clone());
                    state.cloud.set(
                        format!("job/{id}/index/{:016x}", page(group, "group")),
                        portable_object(&object),
                    );
                    job["group_cursor"] = json!(cursor + 1);
                }
                if cursor + 1 >= total {
                    job["stage"] = json!("directory");
                    job["directory_cursor"] = json!(0);
                }
            }
            "directory" => {
                let prefix = format!("job/{id}/index/");
                let total = state.cloud.prefix_count(&prefix);
                let cursor = page(&job, "directory_cursor") as usize;
                let part: Vec<_> = state
                    .cloud
                    .prefix_page(&prefix, cursor, 1024)
                    .into_iter()
                    .map(|(_, v)| v)
                    .collect();
                if !part.is_empty() {
                    let object =
                        self.prepare_image(&mut state, 2, &serde_json::to_vec(&part)?, None)?;
                    put_job_object(&mut state.cloud, &id, object.clone());
                    state.cloud.set(
                        format!("job/{id}/directory/{cursor:016x}"),
                        portable_object(&object),
                    );
                }
                let next = (cursor + 1024).min(total);
                job["directory_cursor"] = json!(next);
                if next == total {
                    job["stage"] = json!("commit");
                }
            }
            "commit" => {
                let directories: Vec<_> = state
                    .cloud
                    .prefix(&format!("job/{id}/directory/"))
                    .into_iter()
                    .map(|(_, v)| v)
                    .collect();
                let payload = json!({"cloud_format":1,"volume_id":self.shared.config.id,"snapshot_id":snapshot.id,"generation":job["generation"],"capacity_bytes":self.capacity(),"allocated_pages":snapshot.allocated_pages,"directories":directories});
                let object = self.prepare_image(
                    &mut state,
                    3,
                    &serde_json::to_vec(&payload)?,
                    Some(&self.shared.config),
                )?;
                let oid = object["id"].as_str().unwrap().to_owned();
                job["root_object_id"] = json!(oid);
                job["root_sha256"] = object["sha256"].clone();
                job["phase"] = json!("uploading");
                put_job_object(&mut state.cloud, &id, object);
            }
            _ => return Err(invalid("invalid upload preparation stage")),
        }
        state.cloud.set("job", job);
        update_job_counts(&mut state.cloud);
        self.shared.persist(&mut state, false)?;
        Ok(json!({"job":state.cloud.get("job")}))
    }
    fn draft_records(&self, mut at: MetaRef) -> Result<Vec<u8>> {
        let mut out = Vec::new();
        let mut seen = BTreeSet::new();
        while !at.empty() {
            if !seen.insert(at.offset) {
                return Err(integrity("draft cycle"));
            }
            let bytes = self.shared.read_cloud_blob(at)?;
            if bytes.len() < 40 || !(bytes.len() - 40).is_multiple_of(88) {
                return Err(integrity("draft records"));
            }
            out.extend_from_slice(&bytes[40..]);
            if out.len() > GROUP_OBJECTS as usize * SLOTS as usize * 88 {
                return Err(invalid("index group exceeds its record bound"));
            }
            at = MetaRef::get(&bytes[..40]);
        }
        Ok(out)
    }
    fn prepare_image(
        &self,
        state: &mut State,
        kind: u8,
        payload: &[u8],
        configuration: Option<&Config>,
    ) -> Result<Value> {
        let key = format!("image/{kind}/{}", digest(payload));
        let mut record = state.cloud.get(&key);
        if record.is_null() {
            let id = Uuid::new_v4();
            let mut nonce = [0; 24];
            OsRng.fill_bytes(&mut nonce);
            record = json!({"id":id,"kind":if kind==1{"index"}else if kind==2{"directory"}else{"commit"},"length":OBJECT,"nonce":codec::hex(&nonce)});
        }
        let id = Uuid::parse_str(record["id"].as_str().unwrap()).unwrap();
        let nonce: [u8; 24] = from_hex(record["nonce"].as_str().unwrap())?
            .try_into()
            .map_err(|_| invalid("object nonce"))?;
        let image = encode_image(&self.shared.crypto, kind, id, payload, configuration, nonce)?;
        record["sha256"] = json!(digest(&image));
        record["uploaded"] = json!(has_receipt(&state.cloud, &record));
        if !record["uploaded"].as_bool().unwrap_or(false) || kind == 3 {
            self.shared.allocate_spool(state, &id.to_string(), &image)?;
        }
        state.cloud.set(key, record.clone());
        Ok(record)
    }
    fn data_image(&self, record: &Value) -> Result<Vec<u8>> {
        let id = Uuid::parse_str(
            record["id"]
                .as_str()
                .ok_or_else(|| invalid("data identity"))?,
        )
        .map_err(|_| invalid("data UUID"))?;
        let source = page(record, "source_number");
        if self.shared.object_header(source)?.id != id {
            return Err(integrity("source object was reused"));
        }
        let nonce = from_hex(record["nonce"].as_str().unwrap())?
            .try_into()
            .map_err(|_| invalid("data nonce"))?;
        let mut image = encode_image(&self.shared.crypto, 0, id, &[], None, nonce)?;
        let used = page(record, "used_slots").min(SLOTS) as usize;
        let start = 17 * PAGE;
        self.shared.file.read(
            source * OBJECT + start as u64,
            &mut image[start..start + used * PAGE],
        )?;
        Ok(image)
    }
    pub fn read_export_v3(&self, job: &str, id: &str, offset: u64, out: &mut [u8]) -> Result<()> {
        self.require_v3()?;
        if offset
            .checked_add(out.len() as u64)
            .filter(|n| *n <= OBJECT)
            .is_none()
        {
            return Err(invalid("export range exceeds 4 MiB"));
        }
        let _epoch = self.shared.epoch.read().map_err(|_| Error::Poisoned)?;
        let state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        if state.cloud.get("job")["id"] != job {
            return Err(invalid("upload pin expired"));
        }
        let object = state.cloud.get(&job_key(job, id));
        if object.is_null() || object["sha256"].as_str().is_none() {
            return Err(invalid("object is not prepared"));
        }
        let spool = state.cloud.get(&format!("spool/{id}"));
        drop(state);
        // The epoch pins extents; ordinary cache reads/writes can acquire the
        // state mutex while a network exporter reads and hashes this object.
        let image = if object["kind"] == "data" {
            self.data_image(&object)?
        } else {
            let extent = spool["extent"]
                .as_u64()
                .ok_or_else(|| invalid("prepared object is unavailable"))?;
            let mut bytes = vec![0; OBJECT as usize];
            self.shared.file.read(extent * OBJECT, &mut bytes)?;
            bytes
        };
        if digest(&image) != object["sha256"].as_str().unwrap() {
            return Err(integrity("export object changed"));
        }
        out.copy_from_slice(&image[offset as usize..offset as usize + out.len()]);
        Ok(())
    }
}
fn public_object(record: &Value) -> Value {
    json!({"id":record["id"],"kind":record["kind"],"length":OBJECT,"sha256":record["sha256"],"uploaded":record["uploaded"].as_bool().unwrap_or(false)})
}
fn has_receipt(db: &CloudDb, object: &Value) -> bool {
    let receipt = db.get(&format!("receipt/{}", object["id"].as_str().unwrap_or("")));
    !receipt.is_null() && receipt["sha256"] == object["sha256"]
}
fn update_job_counts(db: &mut CloudDb) {
    let mut job = db.get("job");
    if job.is_null() {
        return;
    }
    let id = job["id"].as_str().unwrap().to_owned();
    let counts = db.get(&format!("job/{id}/counts"));
    let count = number(&counts, "total", 0);
    let uploaded = number(&counts, "uploaded", 0);
    let ready = !job["root_object_id"].is_null() && number(&counts, "pending", 0) == 0;
    job["total_objects"] = json!(count);
    job["uploaded_objects"] = json!(uploaded);
    job["estimated_bytes"] = json!(count * OBJECT);
    if job["phase"] != "preparing" {
        job["phase"] = json!(if ready { "ready" } else { "uploading" });
    }
    db.set("job", job);
}

fn portable_object(record: &Value) -> Value {
    json!({"id":record["id"],"kind":record["kind"],"length":OBJECT,"sha256":record["sha256"]})
}

fn put_job_object(db: &mut CloudDb, job: &str, object: Value) {
    let key = job_key(job, object["id"].as_str().unwrap());
    let old = db.get(&key);
    let counts_key = format!("job/{job}/counts");
    let mut counts = db.get(&counts_key);
    if counts.is_null() {
        counts = json!({"total":0,"uploaded":0,"pending":0});
    }
    let old_uploaded = u64::from(old["uploaded"] == true);
    let new_uploaded = u64::from(object["uploaded"] == true);
    let old_pending =
        u64::from(!old.is_null() && old["kind"] != "commit" && old["uploaded"] != true);
    let new_pending = u64::from(object["kind"] != "commit" && object["uploaded"] != true);
    counts["total"] = json!(number(&counts, "total", 0) + u64::from(old.is_null()));
    counts["uploaded"] = json!(number(&counts, "uploaded", 0) - old_uploaded + new_uploaded);
    counts["pending"] = json!(number(&counts, "pending", 0) - old_pending + new_pending);
    db.set(key, object);
    db.set(counts_key, counts);
}
