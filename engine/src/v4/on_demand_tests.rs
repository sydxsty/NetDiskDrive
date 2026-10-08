//! Multi-object source graphs: unused branches must remain genuinely remote,
//! including across an original writer's publication and interrupted retries.
use super::*;
use serde_json::{json, Value};
use std::collections::{HashMap, HashSet};

const CAPACITY: u64 = 32 << 30;
const STRIDE: u64 = 4 << 30;

fn prepare(v: &Volume) -> Value {
    let mut job = v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].clone();
    for _ in 0..64 {
        if job["phase"] == "ready" {
            return job;
        }
        job = v
            .control(&json!({"cmd":"cloud.prepare","job_id":job["id"]}))
            .unwrap()["job"]
            .clone();
    }
    panic!("preparation did not finish");
}
fn publish(v: &Volume, objects: &mut HashMap<String, Vec<u8>>) -> Value {
    let job = prepare(v);
    let listed = v
        .control(&json!({"cmd":"cloud.list","job_id":job["id"]}))
        .unwrap();
    assert!(listed["next_cursor"].is_null());
    for item in listed["items"].as_array().unwrap() {
        let id = item["id"].as_str().unwrap();
        let mut raw = vec![0; v.object_size() as usize];
        v.read_export(job["id"].as_str().unwrap(), id, 0, &mut raw)
            .unwrap();
        objects.insert(id.to_owned(), raw);
        v.control(
            &json!({"cmd":"cloud.receipt","job_id":job["id"],"object_id":id,
            "sha256":item["sha256"],"length":v.object_size()}),
        )
        .unwrap();
    }
    v.control(
        &json!({"cmd":"cloud.commit","job_id":job["id"],"root_object_id":job["root_object_id"],
        "root_sha256":job["root_sha256"],"receipt":"offline-ack"}),
    )
    .unwrap();
    job
}
struct Fixture {
    _dir: tempfile::TempDir,
    target: std::path::PathBuf,
    objects: HashMap<String, Vec<u8>>,
    metadata: HashSet<String>,
    initial: HashSet<String>,
    options: Value,
    password: Option<&'static str>,
}
impl Fixture {
    fn new(size: u64, password: Option<&'static str>) -> Self {
        let dir = tempfile::tempdir().unwrap();
        let source_path = dir.path().join("source.odv4");
        Volume::create_sized(&source_path, CAPACITY, password, size).unwrap();
        let source = Volume::open(&source_path, password).unwrap();
        let mut objects = HashMap::new();
        let mut metadata = HashSet::new();
        let mut job = Value::Null;
        // Each published update seals a different immutable index object. The
        // final root shares the untouched older subtrees at distant addresses.
        for i in 0..4u64 {
            source.write(i * STRIDE, &[i as u8 + 11; PAGE]).unwrap();
            source.flush().unwrap();
            job = publish(&source, &mut objects);
            metadata.insert(job["root_object_id"].as_str().unwrap().to_owned());
        }
        assert_eq!(metadata.len(), 4);
        let id = source.info().unwrap().id;
        let root_id = job["root_object_id"].as_str().unwrap();
        let remote = format!("/OverlayDisk/{id}");
        let writer = Uuid::new_v4();
        let binding = json!({"backend_id":"mock","account_id":"test","remote_root":remote,"device_id":writer,"enabled":true});
        let commit = json!({"formatVersion":4,"volumeId":id,"writerId":writer,"generation":job["generation"],
            "name":"fixture","capacityBytes":CAPACITY,"encrypted":password.is_some(),"objectSizeBytes":size,
            "rootObjectId":root_id,"rootSha256":job["root_sha256"],"rootSlot":0});
        let backing = json!({"provider_id":"mock","account_id":"test","source_volume_id":id,"remote_root":remote,
            "reader_pin":format!("{remote}/readers/test.json"),"root_object_id":root_id,"root_sha256":job["root_sha256"]});
        let options = json!({"mode":"original","lazy":true,"source_volume_id":id,"backing":backing,
            "publication":{"binding":binding,"commit":commit}});
        let target = dir.path().join("target.odv4");
        let v =
            Volume::restore_begin_options(&target, &objects[root_id], password, options.clone())
                .unwrap();
        v.control(&json!({"cmd":"replica.bootstrap","commit":options["publication"]["commit"]}))
            .unwrap();
        let mut initial = HashSet::from([root_id.to_owned()]);
        for _ in 0..8 {
            let status = v.control(&json!({"cmd":"restore.status"})).unwrap();
            if status["phase"] == "ready" {
                break;
            }
            for need in status["needed"].as_array().unwrap() {
                let id = need["id"].as_str().unwrap();
                v.restore_accept(id, &objects[id]).unwrap();
                initial.insert(id.to_owned());
            }
            v.control(&json!({"cmd":"restore.step"})).unwrap();
        }
        v.control(&json!({"cmd":"restore.finish"})).unwrap();
        assert!(initial.len() <= 2);
        assert_eq!(v.info().unwrap().allocated_pages, 4);
        Self {
            _dir: dir,
            target,
            objects,
            metadata,
            initial,
            options,
            password,
        }
    }
    fn open(&self, calls: &Arc<Mutex<Vec<String>>>) -> Volume {
        let v = Volume::open(&self.target, self.password).unwrap();
        let objects = self.objects.clone();
        let calls = calls.clone();
        v.set_object_provider(Some(Arc::new(move |object, out| {
            calls.lock().unwrap().push(object.id.clone());
            out.copy_from_slice(objects.get(&object.id).expect("only known source objects"));
            Ok(())
        })))
        .unwrap();
        v
    }
}

#[test]
fn original_incremental_sync_keeps_unvisited_indexes_remote_across_reopen() {
    for (size, password) in [
        (4 << 20, None),
        (8 << 20, Some("encrypted-source")),
        (16 << 20, None),
    ] {
        let mut f = Fixture::new(size, password);
        let calls = Arc::new(Mutex::new(Vec::new()));
        let v = f.open(&calls);
        assert!(v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
        assert!(calls.lock().unwrap().is_empty());
        let snap = v.snapshot_create().unwrap();
        v.write(0, &[87; PAGE]).unwrap();
        v.flush().unwrap();
        let after_write = calls.lock().unwrap().clone();
        assert!(!after_write.is_empty());
        assert!(
            after_write.iter().all(|id| f.metadata.contains(id)),
            "full overwrite downloaded old data"
        );
        let before_objects: HashSet<_> = f.objects.keys().cloned().collect();
        let job = publish(&v, &mut f.objects);
        assert_eq!(job["processed_pages"], 1);
        assert_eq!(
            *calls.lock().unwrap(),
            after_write,
            "sync downloaded unrelated source indexes"
        );
        assert_eq!(
            f.objects.len() - before_objects.len(),
            2,
            "only changed data and its new index/root are uploaded"
        );
        let fetched: HashSet<_> = after_write
            .iter()
            .cloned()
            .chain(f.initial.iter().cloned())
            .collect();
        assert!(f.metadata.difference(&fetched).count() >= 2);
        assert_eq!(
            v.control(&json!({"cmd":"replica.status"})).unwrap()["reference_accounting"],
            "source_anchor"
        );
        let untouched = f.metadata.difference(&fetched).next().unwrap();
        let gc = v
            .control(&json!({"cmd":"cloud.gc_candidates","object_ids":[untouched]}))
            .unwrap();
        assert!(gc["allowed"].as_array().unwrap().is_empty());
        assert_eq!(gc["protected"][0], *untouched);
        v.snapshot_release(&snap).unwrap();
        drop(v);
        let v = f.open(&calls);
        let count = calls.lock().unwrap().len();
        let mut out = [0; PAGE];
        v.read(0, &mut out).unwrap();
        assert_eq!(out, [87; PAGE]);
        v.write(0, &[91; PAGE]).unwrap();
        v.flush().unwrap();
        let second = publish(&v, &mut f.objects);
        assert_eq!(second["processed_pages"], 1);
        assert_eq!(calls.lock().unwrap().len(), count);
        v.control(&json!({"cmd":"volume.read_only","enabled":true}))
            .unwrap();
        v.read(STRIDE, &mut out).unwrap();
        assert_eq!(out, [12; PAGE]);
        assert!(v.write(STRIDE, &[99; PAGE]).is_err());
        assert!(v.info().unwrap().background_error.is_none());
        drop(v);
        // Authenticate the resulting cloud root independently and verify both
        // changed and shared bytes using the same ordinary restore/read path.
        let mut options = f.options.clone();
        options["publication"]["commit"]["generation"] = second["generation"].clone();
        options["publication"]["commit"]["rootObjectId"] = second["root_object_id"].clone();
        options["publication"]["commit"]["rootSha256"] = second["root_sha256"].clone();
        options["backing"]["root_object_id"] = second["root_object_id"].clone();
        options["backing"]["root_sha256"] = second["root_sha256"].clone();
        let check = Volume::restore_begin_options(
            f._dir.path().join("verify.odv4"),
            &f.objects[second["root_object_id"].as_str().unwrap()],
            password,
            options,
        )
        .unwrap();
        check.control(&json!({"cmd":"replica.bootstrap"})).unwrap();
        check.control(&json!({"cmd":"restore.step"})).unwrap();
        check.control(&json!({"cmd":"restore.finish"})).unwrap();
        let objects = Arc::new(f.objects);
        check
            .set_object_provider(Some(Arc::new(move |object, out| {
                out.copy_from_slice(&objects[&object.id]);
                Ok(())
            })))
            .unwrap();
        for i in 0..4u64 {
            check.read(i * STRIDE, &mut out).unwrap();
            assert_eq!(out, [if i == 0 { 91 } else { i as u8 + 11 }; PAGE]);
        }
    }
}

#[test]
fn source_anchor_marker_rejects_old_boolean_only_writers() {
    use super::replica::CountCoverage;
    for value in [
        CountCoverage::Deferred,
        CountCoverage::Complete,
        CountCoverage::SourceAnchor,
    ] {
        let encoded = serde_json::to_string(&value).unwrap();
        assert_eq!(
            serde_json::from_str::<CountCoverage>(&encoded).unwrap(),
            value
        );
        assert_eq!(
            serde_json::from_str::<bool>(&encoded).is_err(),
            value == CountCoverage::SourceAnchor
        );
    }
    assert!(serde_json::from_str::<CountCoverage>("\"other\"").is_err());
}

#[test]
fn prefetch_discovers_only_the_requested_index_path_and_trim_does_not_walk_past_it() {
    let mut f = Fixture::new(8 << 20, Some("encrypted-source"));
    let calls = Arc::new(Mutex::new(Vec::new()));
    let v = f.open(&calls);
    let mut metadata = HashSet::new();
    let mut data = HashSet::new();
    for _ in 0..8 {
        let needs = v
            .control(&json!({"cmd":"lazy.needs","offset":0,"length":PAGE,"limit":1}))
            .unwrap();
        let items = needs["items"].as_array().unwrap();
        assert!(items.len() <= 1);
        if items.is_empty() {
            break;
        }
        let item = &items[0];
        let id = item["id"].as_str().unwrap();
        if item["kind"] == "metadata" {
            assert_eq!(needs["waiting_for_index"], true);
            assert_eq!(needs["next_offset"], 0);
            assert!(metadata.insert(id.to_owned()));
        } else {
            assert_eq!(item["kind"], "data");
            assert!(data.insert(id.to_owned()));
        }
        v.lazy_import(id, &f.objects[id]).unwrap();
    }
    assert!(!metadata.is_empty());
    assert_eq!(data.len(), 1);
    assert!(
        calls.lock().unwrap().is_empty(),
        "a mapping query performed hidden downloads"
    );
    assert!(
        f.metadata
            .difference(&metadata)
            .filter(|id| !f.initial.contains(*id))
            .count()
            >= 2
    );
    // The next allocated page is exactly outside this empty range and its
    // index belongs to another cold object. Looking past the bound is costly.
    let hole = v
        .control(&json!({"cmd":"lazy.needs","offset":STRIDE-PAGE as u64,"length":PAGE,"limit":16}))
        .unwrap();
    assert!(hole["items"].as_array().unwrap().is_empty());
    assert!(hole["next_offset"].is_null());
    let mut out = [0; PAGE];
    v.read(0, &mut out).unwrap();
    assert_eq!(out, [11; PAGE]);
    v.trim(0, (PAGE * 2) as u64).unwrap();
    v.flush().unwrap();
    assert_eq!(publish(&v, &mut f.objects)["processed_pages"], 1);
    assert!(
        calls.lock().unwrap().is_empty(),
        "range-limited trim/sync fetched a different region"
    );
    v.read(0, &mut out).unwrap();
    assert_eq!(out, [0; PAGE]);
}

#[test]
fn preparation_download_failure_and_corruption_retry_without_poison_or_full_traversal() {
    let mut f = Fixture::new(4 << 20, None);
    let calls = Arc::new(Mutex::new(Vec::new()));
    let v = f.open(&calls);
    v.write(0, &[44; PAGE]).unwrap();
    v.flush().unwrap();
    let cold = calls.lock().unwrap()[0].clone();
    // Simulate loss of a cached source index, while the new local COW path is
    // durable. Preparation must resolve the old path before its transaction.
    {
        let mut s = v.shared.store.lock().unwrap();
        let mut object = s.object_id(&cold).unwrap();
        assert!(object.origin_backed && object.kind == 2);
        object.missing = true;
        object.extent = 0;
        s.transaction(|tx| {
            tx.objects.insert(object.oid, object.clone());
            Ok(())
        })
        .unwrap();
    }
    drop(v);
    let requests = Arc::new(Mutex::new(Vec::new()));
    let mut job_id = None;
    for corrupt in [false, true] {
        let v = Volume::open(&f.target, None).unwrap();
        let source = f.objects.clone();
        let requests = requests.clone();
        v.set_object_provider(Some(Arc::new(move |object, out| {
            requests
                .lock()
                .unwrap()
                .push((object.id.clone(), object.kind, object.reason));
            if !corrupt {
                return Err(Error::Invalid("injected interrupted download".into()));
            }
            out.copy_from_slice(&source[&object.id]);
            out[PAGE] ^= 1;
            Ok(())
        })))
        .unwrap();
        let mut failed = false;
        for _ in 0..8 {
            match v.control(&json!({"cmd":"cloud.prepare"})) {
                Ok(result) => assert_ne!(result["job"]["phase"], "ready"),
                Err(error) => {
                    assert!(
                        matches!(error, Error::Integrity(_) | Error::Invalid(_)),
                        "{error}"
                    );
                    failed = true;
                    break;
                }
            }
        }
        assert!(failed);
        let status = v.control(&json!({"cmd":"cloud.status"})).unwrap();
        let id = status["job"]["id"].as_str().unwrap().to_owned();
        if let Some(before) = &job_id {
            assert_eq!(&id, before);
        }
        job_id = Some(id);
        assert_eq!(status["job"]["processed_pages"], 0);
        assert_eq!(status["job"]["changed_pages"], 1);
        assert!(v.info().unwrap().background_error.is_none());
        let mut out = [0; PAGE];
        v.read(0, &mut out).unwrap();
        assert_eq!(out, [44; PAGE]);
    }
    assert_eq!(
        *requests.lock().unwrap(),
        vec![(cold.clone(), "metadata", "sync"); 2]
    );
    let v = Arc::new(Volume::open(&f.target, None).unwrap());
    let (entered_tx, entered_rx) = std::sync::mpsc::channel();
    let (release_tx, release_rx) = std::sync::mpsc::channel();
    let release_rx = Mutex::new(release_rx);
    let source = f.objects.clone();
    let downloaded = requests.clone();
    v.set_object_provider(Some(Arc::new(move |object, out| {
        downloaded
            .lock()
            .unwrap()
            .push((object.id.clone(), object.kind, object.reason));
        entered_tx.send(()).unwrap();
        release_rx
            .lock()
            .unwrap()
            .recv_timeout(Duration::from_secs(10))
            .unwrap();
        out.copy_from_slice(&source[&object.id]);
        Ok(())
    })))
    .unwrap();
    let syncing = v.clone();
    let sync = thread::spawn(move || prepare(&syncing));
    entered_rx.recv_timeout(Duration::from_secs(5)).unwrap();
    let front = v.clone();
    let (done_tx, done_rx) = std::sync::mpsc::channel();
    let foreground = thread::spawn(move || {
        let result = (|| -> Result<String> {
            front.control(&json!({"cmd":"debug.summary"}))?;
            front.control(&json!({"cmd":"snapshot.list"}))?;
            let snapshot = front.snapshot_create()?;
            front.write(0, &[99; PAGE])?;
            front.flush()?;
            let mut bytes = [0; PAGE];
            front.read(0, &mut bytes)?;
            assert_eq!(bytes, [99; PAGE]);
            Ok(snapshot)
        })();
        done_tx.send(result).unwrap();
    });
    let result = done_rx.recv_timeout(Duration::from_secs(5));
    release_tx.send(()).unwrap();
    let snap = result
        .expect("network wait blocked resident operations")
        .unwrap();
    foreground.join().unwrap();
    let ready = sync.join().unwrap();
    assert_eq!(ready["id"], job_id.unwrap());
    publish(&v, &mut f.objects);
    assert_eq!(
        v.control(&json!({"cmd":"cloud.status"})).unwrap()["changed_pages"],
        1
    );
    v.snapshot_release(&snap).unwrap();
    assert_eq!(publish(&v, &mut f.objects)["processed_pages"], 1);
    assert_eq!(
        *requests.lock().unwrap(),
        vec![(cold, "metadata", "sync"); 3]
    );
    assert!(v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
}

#[test]
fn cancelling_a_pulled_candidate_preserves_exact_local_publication_counts() {
    let mut f = Fixture::new(4 << 20, None);
    let calls = Arc::new(Mutex::new(Vec::new()));
    let v = f.open(&calls);
    v.write(0, &[44; PAGE]).unwrap();
    v.flush().unwrap();
    let job = publish(&v, &mut f.objects);
    let mut commit = f.options["publication"]["commit"].clone();
    commit["rootObjectId"] = job["root_object_id"].clone();
    commit["rootSha256"] = job["root_sha256"].clone();
    commit["generation"] = job["generation"].clone();
    let mut backing = f.options["backing"].clone();
    backing["root_object_id"] = job["root_object_id"].clone();
    backing["root_sha256"] = job["root_sha256"].clone();
    backing["reader_pin"] = json!("/OverlayDisk/test/readers/new.json");
    let stage = json!({"backing":backing,"commit":commit});
    v.replica_stage(&f.objects[job["root_object_id"].as_str().unwrap()], &stage)
        .unwrap();
    v.control(&json!({"cmd":"replica.cancel"})).unwrap();
    let count = calls.lock().unwrap().len();
    v.write(0, &[66; PAGE]).unwrap();
    v.flush().unwrap();
    let next = prepare(&v);
    let removed = v
        .control(&json!({"cmd":"cloud.delta","job_id":next["id"],"side":"remove"}))
        .unwrap();
    assert_eq!(
        removed["items"].as_array().unwrap().len(),
        2,
        "known locally published references must be decremented even after source registration"
    );
    publish(&v, &mut f.objects);
    assert_eq!(calls.lock().unwrap().len(), count);
    assert!(v.info().unwrap().background_error.is_none());
}
