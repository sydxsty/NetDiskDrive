use super::*;

impl Volume {
    pub(super) fn snapshot_control(&self, r: &Value) -> Result<Value> {
        let cmd = string(r, "cmd")?;
        if cmd == "snapshot.create" {
            self.require_restored()?;
            self.flush()?;
        }
        let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
        let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        check_failure(&state)?;
        if cmd == "snapshot.list" {
            let items: Vec<_> = state
                .cloud
                .prefix("snapshot/")
                .into_iter()
                .map(|(_, v)| v)
                .collect();
            let cursor = number(r, "cursor", 0) as usize;
            let limit = number(r, "limit", 128).clamp(1, 128) as usize;
            let page: Vec<_> = items.iter().skip(cursor).take(limit).cloned().collect();
            let next = cursor + page.len();
            return Ok(
                json!({"items":page,"next_cursor":if next<items.len(){Some(next)}else{None},"total_count":items.len()}),
            );
        }
        if cmd == "snapshot.create" {
            let name = string(r, "name")?;
            if name.chars().count() > 64 || name.trim().is_empty() {
                return Err(invalid("snapshot name must contain 1–64 characters"));
            }
            if !state.dirty.is_empty() || !state.trims.is_empty() {
                self.shared.persist(&mut state, false)?;
            }
            let snapshot = self.shared.capture(&mut state, &name, "user")?;
            self.shared.persist(&mut state, true)?;
            return Ok(json!({"snapshot":state.cloud.get(&format!("snapshot/{}",snapshot.id))}));
        }
        let id = uuid(r, "id")?;
        let key = format!("snapshot/{id}");
        let mut label = state.cloud.get(&key);
        if label.is_null() {
            return Err(invalid("snapshot not found"));
        }
        if label["pin"] != "user" {
            return Err(invalid("this snapshot is owned by an active task"));
        }
        match cmd.as_str() {
            "snapshot.rename" => {
                let name = string(r, "name")?;
                if name.trim().is_empty() || name.chars().count() > 64 {
                    return Err(invalid("invalid snapshot name"));
                }
                label["name"] = json!(name);
                state.cloud.set(key, label);
                self.shared.persist(&mut state, false)?;
            }
            "snapshot.delete" => {
                state.snapshots.retain(|s| s.id != id);
                state.cloud.remove(&key);
                self.shared.persist(&mut state, true)?;
            }
            _ => return Err(invalid("snapshot operation")),
        }
        Ok(json!({"ok":true}))
    }
    pub(super) fn compact_control(&self, r: &Value) -> Result<Value> {
        let cmd = string(r, "cmd")?;
        if cmd == "compact.start" {
            self.flush()?;
        }
        let _epoch = self.shared.epoch.write().map_err(|_| Error::Poisoned)?;
        let mut state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
        check_failure(&state)?;
        if state.current.restore_required || state.cloud.restore_incomplete() {
            return Err(invalid("cannot compact an incomplete restore"));
        }
        let mut job = state.cloud.get("compact");
        if cmd == "compact.status" {
            return Ok(compact_public(&job));
        }
        if cmd == "compact.start" {
            if !job.is_null() && job["state"] != "done" && job["state"] != "idle" {
                return Ok(compact_public(&job));
            }
            if !state.dirty.is_empty() || !state.trims.is_empty() {
                self.shared.persist(&mut state, false)?;
            }
            let snapshot = self
                .shared
                .capture(&mut state, "Online compaction", "compact")?;
            let id = Uuid::new_v4().to_string();
            job = json!({"id":id,"state":"running","phase":"moving","snapshot_id":snapshot.id,"cursor":0,"scanned_pages":0,"moved_pages":0,"total_pages":snapshot.allocated_pages,"estimated_reclaim_bytes":0,"marked_snapshots":[],"mark_cursor":0,"mark_snapshot":null,"reclaim_cursor":0});
            state.cloud.set("compact", job.clone());
            self.shared.persist(&mut state, true)?;
            return Ok(compact_public(&job));
        }
        if job.is_null() {
            return Ok(compact_public(&job));
        }
        if cmd == "compact.pause" {
            if job["state"] != "done" {
                job["state"] = json!(if r["paused"].as_bool().unwrap_or(true) {
                    "paused"
                } else {
                    "running"
                });
                state.cloud.set("compact", job.clone());
                self.shared.persist(&mut state, false)?;
            }
            return Ok(compact_public(&job));
        }
        if job["state"] != "running" {
            return Ok(compact_public(&job));
        }
        let limit = number(r, "max_pages", 256).clamp(1, 1024) as usize;
        let id = job["id"].as_str().unwrap().to_owned();
        let pin = job["snapshot_id"].as_str().unwrap().to_owned();
        let mut catalog = false;
        // Finish newer frontend writes before comparing the persistent map. The
        // epoch excludes other commits; the state lock excludes cache mutation.
        if !state.dirty.is_empty() || !state.trims.is_empty() {
            self.shared.persist(&mut state, false)?;
        }
        match job["phase"].as_str().unwrap_or("moving") {
            "moving" => {
                let snapshot = state
                    .snapshots
                    .iter()
                    .find(|s| s.id == pin)
                    .cloned()
                    .ok_or_else(|| invalid("compaction pin missing"))?;
                let pages =
                    self.shared
                        .snapshot_pages(&snapshot, number(&job, "cursor", 0), limit)?;
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
                let mut moved = 0;
                for (page, original) in &pages {
                    let key = format!("compact/{id}/candidate/{:016x}", original.object);
                    if state.cloud.get(&key).is_null() {
                        let header = self.shared.object_header(original.object)?;
                        state
                            .cloud
                            .set(key, json!({"number":original.object,"id":header.id}));
                    }
                    if let Some(current) = self.shared.lookup(&state.current, *page)? {
                        if current.object == original.object
                            && current.object_generation == original.object_generation
                            && current.slot == original.slot
                            && current.version == original.version
                        {
                            let bytes = self.shared.read_page(&root, *page)?;
                            Arc::make_mut(&mut state.dirty).insert(*page, bytes);
                            moved += 1;
                        }
                    }
                }
                job["scanned_pages"] = json!(number(&job, "scanned_pages", 0) + pages.len() as u64);
                job["moved_pages"] = json!(number(&job, "moved_pages", 0) + moved);
                if let Some((p, _)) = pages.last() {
                    job["cursor"] = json!(p + 1);
                }
                if pages.len() < limit {
                    job["phase"] = json!("marking");
                }
            }
            "marking" => {
                let marked: Vec<String> = serde_json::from_value(job["marked_snapshots"].clone())?;
                let selected = job["mark_snapshot"]
                    .as_str()
                    .and_then(|id| state.snapshots.iter().find(|s| s.id == id))
                    .cloned()
                    .or_else(|| {
                        state
                            .snapshots
                            .iter()
                            .find(|s| s.id != pin && !marked.contains(&s.id))
                            .cloned()
                    });
                if let Some(snapshot) = selected {
                    if job["mark_snapshot"] != snapshot.id {
                        job["mark_snapshot"] = json!(snapshot.id);
                        job["mark_cursor"] = json!(0);
                    }
                    let pages = self.shared.snapshot_pages(
                        &snapshot,
                        number(&job, "mark_cursor", 0),
                        limit,
                    )?;
                    for (_, reference) in &pages {
                        if !state
                            .cloud
                            .get(&format!("compact/{id}/candidate/{:016x}", reference.object))
                            .is_null()
                        {
                            state.cloud.set(
                                format!("compact/{id}/protected/{:016x}", reference.object),
                                json!(true),
                            );
                        }
                    }
                    if let Some((page, _)) = pages.last() {
                        job["mark_cursor"] = json!(page + 1);
                    }
                    if pages.len() < limit {
                        let mut marked = marked;
                        marked.push(snapshot.id);
                        job["marked_snapshots"] = json!(marked);
                        job["mark_snapshot"] = Value::Null;
                    }
                } else {
                    state.snapshots.retain(|s| s.id != pin);
                    state.cloud.remove(&format!("snapshot/{pin}"));
                    job["phase"] = json!("reclaiming");
                    catalog = true;
                }
            }
            "reclaiming" => {
                let candidates = state.cloud.prefix(&format!("compact/{id}/candidate/"));
                let cursor = number(&job, "reclaim_cursor", 0) as usize;
                let mut reclaimed = 0;
                for (_, candidate) in candidates.iter().skip(cursor).take(limit) {
                    let n = number(candidate, "number", 0);
                    if state.cloud.get(&format!("compact/{id}/protected/{n:016x}")) == true {
                        continue;
                    }
                    // Reopen reconstructs free extents from committed reachability.
                    // This old identity may already have been recycled before resume.
                    if state.free.contains(&n) {
                        continue;
                    }
                    let header = match self.shared.object_header(n) {
                        Ok(h) => h,
                        Err(Error::Io(e)) => return Err(Error::Io(e)),
                        Err(_) => continue,
                    };
                    if header.id.to_string() != candidate["id"].as_str().unwrap_or("") {
                        continue;
                    }
                    state.free.push(n);
                    reclaimed += OBJECT;
                }
                let next = (cursor + limit).min(candidates.len());
                job["reclaim_cursor"] = json!(next);
                job["estimated_reclaim_bytes"] =
                    json!(number(&job, "estimated_reclaim_bytes", 0) + reclaimed);
                if next == candidates.len() {
                    job["phase"] = json!("truncating");
                    for (key, _) in state.cloud.prefix(&format!("compact/{id}/")) {
                        state.cloud.remove(&key);
                    }
                }
            }
            "truncating" => {
                // Publish control-metadata retirement before checking the two
                // durable roots. No still-pinned extent can become tail slack.
                self.shared.prune_cloud(&mut state)?;
                self.shared.persist(&mut state, false)?;
                let released = self.truncate_free_tail(&mut state)?;
                job["reclaimed_bytes"] = json!(number(&job, "reclaimed_bytes", 0) + released);
                job["state"] = json!("done");
                job["phase"] = json!("done");
            }
            _ => return Err(invalid("invalid compaction phase")),
        }
        state.cloud.set("compact", job.clone());
        if job["state"] == "done" {
            self.shared.prune_cloud(&mut state)?;
        }
        state.maintenance = true;
        let result = self.shared.persist(&mut state, catalog);
        state.maintenance = false;
        result?;
        Ok(compact_public(&job))
    }
    pub(in crate::v2) fn truncate_free_tail(&self, state: &mut State) -> Result<u64> {
        let result = (|| {
            let live = self.shared.reachable_roots(&state.roots)?;
            let free: BTreeSet<_> = state.free.iter().copied().collect();
            if live.iter().any(|n| free.contains(n)) {
                return Err(integrity("free extent is still reachable"));
            }
            let before = self.shared.file.len()?;
            let mut end = before / OBJECT;
            while end > 1 && free.contains(&(end - 1)) {
                end -= 1;
            }
            let after = end * OBJECT;
            if after < before {
                // Do this before resize: sync can fail after length changed.
                state.free.retain(|n| *n < end);
                self.shared.file.resize(after)?;
                self.shared.file.sync()?;
            }
            Ok(before - after)
        })();
        if let Err(ref e) = result {
            state.failure = Some(e.to_string());
        }
        result
    }
    pub(super) fn debug_blocks(&self, r: &Value) -> Result<Value> {
        let page = number(r, "page", 0);
        let size = number(r, "page_size", 128).clamp(1, 128);
        let _epoch = self.shared.epoch.read().map_err(|_| Error::Poisoned)?;
        let (total, free, tails, snapshots, labels, receipts, spools, highwaters) = {
            let state = self.shared.state.lock().map_err(|_| Error::Poisoned)?;
            (
                self.shared.file.len()? / OBJECT,
                state.free.iter().copied().collect::<BTreeSet<_>>(),
                [state.current.data_tail, state.current.meta_tail],
                state.snapshots.clone(),
                state.cloud.prefix("snapshot/"),
                state.cloud.prefix("receipt/"),
                state.cloud.prefix("spool/"),
                state.cloud.prefix("highwater/"),
            )
        };
        let mut pins = BTreeSet::new();
        let mut uploads = BTreeSet::new();
        for snapshot in &snapshots {
            let objects = self.shared.snapshot_objects(snapshot)?;
            if labels
                .iter()
                .any(|(_, v)| v["id"] == snapshot.id && v["upload_pin"] == true)
            {
                uploads.extend(objects.iter().copied());
            }
            pins.extend(objects);
        }
        let spools: BTreeSet<_> = spools
            .iter()
            .filter_map(|(_, v)| v["extent"].as_u64())
            .collect();
        let mut items = Vec::new();
        for n in page.saturating_mul(size)..(page.saturating_mul(size) + size).min(total) {
            let mut used = 0;
            let mut uploaded = false;
            let mut object_id: Option<String> = None;
            let mut kind = "reserved";
            let status = if n == 0 {
                "control"
            } else if free.contains(&n) {
                "free"
            } else if spools.contains(&n) {
                "upload_staging"
            } else {
                match self.shared.object_header(n) {
                    Ok(header) => {
                        object_id = Some(header.id.to_string());
                        kind = if header.kind == 0 { "data" } else { "metadata" };
                        used = tails
                            .iter()
                            .flatten()
                            .find(|t| t.number == n)
                            .map(|t| t.slot)
                            .or_else(|| {
                                highwaters
                                    .iter()
                                    .find(|(k, _)| k == &format!("highwater/{}", header.id))
                                    .and_then(|(_, v)| v.as_u64())
                            })
                            .unwrap_or(SLOTS);
                        uploaded = receipts
                            .iter()
                            .any(|(k, _)| k == &format!("receipt/{}", header.id));
                        if tails.iter().flatten().any(|t| t.number == n) {
                            "active"
                        } else {
                            "allocated"
                        }
                    }
                    Err(_) => "metadata_or_reserved",
                }
            };
            items.push(json!({"index":n,"object_id":object_id,"kind":kind,"offset":n*OBJECT,"length":OBJECT,"state":status,"used_pages":used,"snapshot_pinned":pins.contains(&n),"upload_pinned":uploads.contains(&n)||spools.contains(&n),"uploaded":uploaded}));
        }
        Ok(
            json!({"items":items,"page":page,"page_size":size,"total_count":total,"view":"physical"}),
        )
    }
}
fn compact_public(v: &Value) -> Value {
    json!({"state":v["state"].as_str().unwrap_or("idle"),"phase":v["phase"],"scanned_pages":number(v,"scanned_pages",0),"moved_pages":number(v,"moved_pages",0),"total_pages":number(v,"total_pages",0),"estimated_reclaim_bytes":number(v,"estimated_reclaim_bytes",0),"reclaimed_bytes":number(v,"reclaimed_bytes",0)})
}
