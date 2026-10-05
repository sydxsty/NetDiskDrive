//! Lifecycle regressions for physical-only reclamation and wide sparse page roots.
use super::*;
use serde_json::{json, Value};
use std::collections::HashMap;
fn fresh(path: impl AsRef<Path>, capacity: u64, password: Option<&str>) -> Volume {
    Volume::create(&path, capacity, password).unwrap();
    Volume::open(path, password).unwrap()
}
fn bind(v: &Volume) {
    let id = v.info().unwrap().id;
    v.control(&json!({"cmd":"cloud.bind","binding":{"backend_id":"mock","account_id":"account","remote_root":format!("/OverlayDisk/{id}"),"device_id":Uuid::new_v4(),"enabled":true}})).unwrap();
}
fn publish(v: &Volume) -> (Value, HashMap<String, Vec<u8>>) {
    let mut job = v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].clone();
    for _ in 0..256 {
        if job["phase"] == "ready" {
            break;
        }
        job = v
            .control(
                &json!({"cmd":"cloud.prepare","job_id":job["id"],"max_pages":512,"max_objects":8}),
            )
            .unwrap()["job"]
            .clone();
    }
    assert_eq!(job["phase"], "ready");
    let items = v
        .control(&json!({"cmd":"cloud.list","job_id":job["id"],"limit":128}))
        .unwrap();
    assert!(items["next_cursor"].is_null());
    let mut remote = HashMap::new();
    for o in items["items"].as_array().unwrap() {
        let id = o["id"].as_str().unwrap();
        let mut raw = vec![0; OBJECT as usize];
        v.read_export(job["id"].as_str().unwrap(), id, 0, &mut raw)
            .unwrap();
        remote.insert(id.into(), raw);
        v.control(&json!({"cmd":"cloud.receipt","job_id":job["id"],"object_id":id,"sha256":o["sha256"],"length":OBJECT,"receipt":"owned-test-ack"})).unwrap();
    }
    v.control(&json!({"cmd":"cloud.commit","job_id":job["id"],"root_object_id":job["root_object_id"],"root_sha256":job["root_sha256"],"receipt":"owned-test-ack"})).unwrap();
    (job, remote)
}
fn signature(v: &Volume) -> Value {
    let c = v.control(&json!({"cmd":"cloud.status"})).unwrap();
    let s = v.control(&json!({"cmd":"debug.summary"})).unwrap();
    json!({"generation":c["data_generation"],"dirty":c["changed_pages"],"local_dirty":c["local_dirty"],"pending":s["pending_objects"],"bytes":s["pending_bytes"],"new":s["new_changes"]})
}
fn normal(v: &Volume) {
    let before = signature(v);
    let live = {
        let mut s = v.shared.store.lock().unwrap();
        let root = s.root.objects;
        tree::scan_after(&mut *s, &store::OBJECTS, root, 0, 1024)
            .unwrap()
            .into_iter()
            .map(|(oid, b)| store::Object::decode(oid, &b).unwrap())
            .filter(|o| o.kind == 1 && !o.missing)
            .map(|o| (o.id, o.sha, o.extent))
            .collect::<Vec<_>>()
    };
    v.shared.device.events.lock().unwrap().clear();
    v.control(&json!({"cmd":"compact.start","mode":"normal"}))
        .unwrap();
    let mut done = false;
    for _ in 0..256 {
        let status = v
            .control(&json!({"cmd":"compact.step","max_objects":3}))
            .unwrap();
        if status["state"] == "done" {
            assert_eq!(status["moved_bytes"], 0);
            done = true;
            break;
        }
    }
    assert!(done, "normal collection must stop at its fixed frontier");
    assert_eq!(signature(v), before);
    let events = v.shared.device.events.lock().unwrap().clone();
    for (_, _, extent) in &live {
        assert!(
            !events.iter().any(|(write, offset, len)| !*write
                && *offset < (*extent + 1) * OBJECT
                && offset + *len as u64 > *extent * OBJECT),
            "normal collection read live data payload"
        );
    }
    {
        let mut s = v.shared.store.lock().unwrap();
        for (id, sha, extent) in live {
            if let Ok(o) = s.object_id(&id.to_string()) {
                assert_eq!(o.sha, sha);
                assert_eq!(o.extent, extent);
            }
        }
    }
    assert!(v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
}
#[test]
fn normal_published_local_and_direct_base_keep_upload_set_and_payload_untouched() {
    let d = tempfile::tempdir().unwrap();
    let p = d.path().join("local.odv4");
    let v = fresh(&p, 64 << 20, Some("secret"));
    bind(&v);
    v.write(0, &[17; PAGE]).unwrap();
    publish(&v);
    let snapshot = v.snapshot_create().unwrap();
    v.write(0, &[23; PAGE]).unwrap();
    publish(&v);
    normal(&v);
    normal(&v);
    assert_eq!(
        v.snapshot_manifest(&snapshot).unwrap()["objects"]
            .as_array()
            .unwrap()
            .len(),
        1
    );
    let f = lazy_tests::fixture(Some("password"));
    for mode in ["original", "copy"] {
        let target = f._dir.path().join(format!("reclaim-{mode}.odv4"));
        let opt = base_tests::options(&f, mode, true);
        let t = Volume::restore_begin_options(&target, &f.root, Some("password"), opt).unwrap();
        base_tests::finish(&t, &f, 8);
        t.set_object_provider(Some(lazy_tests::provider(
            f.objects.clone(),
            Arc::new(std::sync::atomic::AtomicUsize::new(0)),
        )))
        .unwrap();
        let snap = t.snapshot_create().unwrap();
        if mode == "copy" {
            bind(&t);
            publish(&t);
        }
        normal(&t);
        t.write(0, &[49; PAGE]).unwrap();
        publish(&t);
        normal(&t);
        assert!(!t.snapshot_manifest(&snap).unwrap()["objects"]
            .as_array()
            .unwrap()
            .is_empty());
    }
}
#[test]
fn cancel_deep_plan_preserves_completed_changes_and_allows_normal_in_read_only() {
    let d = tempfile::tempdir().unwrap();
    let v = fresh(d.path().join("cancel.odv4"), 64 << 20, None);
    bind(&v);
    v.write(0, &[2; PAGE]).unwrap();
    publish(&v);
    let before = signature(&v);
    v.control(&json!({"cmd":"compact.start","mode":"deep"}))
        .unwrap();
    v.control(&json!({"cmd":"volume.read_only","enabled":true}))
        .unwrap();
    let status = v.control(&json!({"cmd":"compact.cancel"})).unwrap();
    assert_eq!(status["state"], "cancelled");
    assert_eq!(signature(&v), before);
    normal(&v);
}
#[test]
fn eight_tib_sparse_last_page_survives_cloud_base_snapshot_trim_and_reopen() {
    let d = tempfile::tempdir().unwrap();
    let p = d.path().join("wide.odv4");
    let v = fresh(&p, MAX_CAPACITY, Some("wide-pass"));
    bind(&v);
    let last = MAX_CAPACITY - PAGE as u64;
    v.write(0, &[7; PAGE]).unwrap();
    v.flush().unwrap();
    {
        let mut s = v.shared.store.lock().unwrap();
        let root = s.root.index;
        assert_eq!(tree::root_level(&mut *s, &store::PAGES, root).unwrap(), 4);
    }
    v.write(last, &[91; PAGE]).unwrap();
    v.flush().unwrap();
    let snap = v.snapshot_create().unwrap();
    let (job, remote) = publish(&v);
    let root = remote[job["root_object_id"].as_str().unwrap()].clone();
    assert!(v.shared.device.len().unwrap() < 128 << 20);
    v.trim(last, 512).unwrap();
    v.flush().unwrap();
    let mut out = [0; PAGE];
    v.read(last, &mut out).unwrap();
    assert_eq!(&out[..512], &[0; 512]);
    assert!(out[512..].iter().all(|b| *b == 91));
    assert!(!v.snapshot_manifest(&snap).unwrap()["objects"]
        .as_array()
        .unwrap()
        .is_empty());
    drop(v);
    let v = Volume::open(&p, Some("wide-pass")).unwrap();
    v.read(last, &mut out).unwrap();
    assert!(out[512..].iter().all(|b| *b == 91));
    let t = Volume::restore_begin_v4(d.path().join("wide-copy.odv4"), &root, Some("wide-pass"))
        .unwrap();
    let mut done = false;
    for _ in 0..256 {
        let st = t.control(&json!({"cmd":"restore.status"})).unwrap();
        if st["phase"] == "ready" {
            t.control(&json!({"cmd":"restore.finish"})).unwrap();
            done = true;
            break;
        }
        for n in st["needed"].as_array().unwrap() {
            let id = n["id"].as_str().unwrap();
            t.restore_accept(id, &remote[id]).unwrap();
        }
        t.control(&json!({"cmd":"restore.step","max_nodes":8,"max_objects":2}))
            .unwrap();
    }
    assert!(done);
    t.read(last, &mut out).unwrap();
    assert_eq!(out, [91; PAGE]);
    assert_eq!(t.capacity(), MAX_CAPACITY);
    assert!(Volume::create(
        d.path().join("too-wide.odv4"),
        MAX_CAPACITY + PAGE as u64,
        None
    )
    .is_err());
}
#[test]
fn deep_whole_object_relocation_keeps_identity_receipts_and_guest_generation() {
    let d = tempfile::tempdir().unwrap();
    let v = fresh(d.path().join("physical-deep.odv4"), 64 << 20, None);
    bind(&v);
    // Occupy an early extent until live objects have been placed beyond it.
    {
        let mut s = v.shared.store.lock().unwrap();
        s.transaction(|tx| tx.allocate_object(1, 0).map(|_| ()))
            .unwrap();
    }
    v.write(0, &[111; PAGE]).unwrap();
    publish(&v);
    let snapshot = v.snapshot_create().unwrap();
    let before = signature(&v);
    let objects = {
        let mut s = v.shared.store.lock().unwrap();
        let r = s.root.objects;
        tree::scan_after(&mut *s, &store::OBJECTS, r, 0, 128)
            .unwrap()
            .into_iter()
            .map(|(i, b)| store::Object::decode(i, &b).unwrap())
            .filter(|o| o.sealed)
            .collect::<Vec<_>>()
    };
    v.control(&json!({"cmd":"volume.read_only","enabled":true}))
        .unwrap();
    v.control(&json!({"cmd":"compact.start","mode":"deep"}))
        .unwrap();
    let mut result = Value::Null;
    for _ in 0..256 {
        result = v
            .control(&json!({"cmd":"compact.step","max_objects":1}))
            .unwrap();
        if result["state"] == "done" {
            break;
        }
    }
    assert_eq!(result["state"], "done");
    assert!(result["moved_bytes"].as_u64().unwrap() > 0);
    assert_eq!(signature(&v), before);
    {
        let mut s = v.shared.store.lock().unwrap();
        for old in objects {
            let new = s.object(old.oid).unwrap();
            assert_eq!(old.id, new.id);
            assert_eq!(old.sha, new.sha);
            assert_eq!(old.sync_state, new.sync_state);
        }
    }
    assert!(v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
    let mut out = [0; PAGE];
    v.read(0, &mut out).unwrap();
    assert_eq!(out, [111; PAGE]);
    assert_eq!(
        v.snapshot_manifest(&snapshot).unwrap()["objects"]
            .as_array()
            .unwrap()
            .len(),
        1
    );
}
#[test]
fn cache_capability_and_eviction_process_kills_reopen_with_remote_proof() {
    use std::process::{Command, Stdio};
    use std::time::Instant;
    let d = tempfile::tempdir().unwrap();
    for stage in [
        "cache_first_config",
        "cache_both_configs",
        "cache_after_eviction",
    ] {
        let path = d.path().join(format!("{stage}.odv4"));
        let v = fresh(&path, 64 << 20, Some("crash-password"));
        v.write(0, &[123; PAGE]).unwrap();
        v.flush().unwrap();
        let mut remote = HashMap::new();
        if stage == "cache_after_eviction" {
            bind(&v);
            remote = publish(&v).1;
            let c = v.control(&json!({"cmd":"cloud.status"})).unwrap();
            let mut backing = c["binding"].clone();
            backing["volume_id"] = json!(v.info().unwrap().id);
            backing["reader_pin"] = json!(format!(
                "{}/readers/{}.json",
                backing["remote_root"].as_str().unwrap(),
                v.info().unwrap().id
            ));
            v.control(&json!({"cmd":"cache.bind","backing":backing}))
                .unwrap();
            v.control(&json!({"cmd":"cache.configure","max_bytes":1,"policy":"lru"}))
                .unwrap();
        }
        let before = v.info().unwrap().data_generation;
        drop(v);
        let ready = d.path().join(format!("{stage}.ready"));
        let mut child = Command::new(std::env::current_exe().unwrap())
            .args([
                "--ignored",
                "--exact",
                "v4::reclaim_tests::cache_crash_child",
            ])
            .env("OVERLAYDISK_V4_CRASH_PATH", &path)
            .env("OVERLAYDISK_V4_CRASH_STAGE", stage)
            .env("OVERLAYDISK_V4_CRASH_READY", &ready)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(30);
        while !ready.exists() {
            assert!(
                child.try_wait().unwrap().is_none(),
                "cache helper ended before {stage}"
            );
            if Instant::now() > deadline {
                let _ = child.kill();
                let _ = child.wait();
                panic!("cache fault point timeout {stage}");
            }
            thread::sleep(Duration::from_millis(10));
        }
        child.kill().unwrap();
        child.wait().unwrap();
        let v = Volume::open(&path, Some("crash-password")).unwrap();
        assert_eq!(v.info().unwrap().data_generation, before);
        for offset in [0, 3 * PAGE as u64] {
            let mut header = [0; PAGE];
            v.shared.device.read(offset, &mut header).unwrap();
            assert_eq!(&header[..8], b"ODV4CFGO");
            assert!(Config::decode(&header).unwrap().cache_capable);
        }
        if stage == "cache_after_eviction" {
            assert_eq!(
                v.control(&json!({"cmd":"cache.status"})).unwrap()["source_ready"],
                true
            );
            v.set_object_provider(Some(Arc::new(move |r, out| {
                out.copy_from_slice(&remote[&r.id]);
                Ok(())
            })))
            .unwrap();
            v.control(&json!({"cmd":"cache.online","available":true}))
                .unwrap();
            v.control(&json!({"cmd":"cache.step"})).unwrap();
        }
        let mut out = [0; PAGE];
        v.read(0, &mut out).unwrap();
        assert_eq!(out, [123; PAGE]);
        assert_eq!(v.info().unwrap().data_generation, before);
    }
}
#[test]
#[ignore]
fn cache_crash_child() {
    let p = std::env::var("OVERLAYDISK_V4_CRASH_PATH").unwrap();
    let v = Volume::open(p, Some("crash-password")).unwrap();
    if std::env::var("OVERLAYDISK_V4_CRASH_STAGE").unwrap() == "cache_after_eviction" {
        v.set_object_provider(Some(Arc::new(|_, _| {
            Err(Error::Invalid("unexpected test download".into()))
        })))
        .unwrap();
        v.control(&json!({"cmd":"cache.online","available":true}))
            .unwrap();
        v.control(&json!({"cmd":"cache.step"})).unwrap();
    } else {
        v.control(&json!({"cmd":"cache.configure","max_bytes":1,"policy":"lru"}))
            .unwrap();
    }
    panic!("fault point missing");
}
#[test]
fn origin_copy_active_upload_eviction_preserves_logical_pending_and_rejects_missing_statistics() {
    let f = lazy_tests::fixture(None);
    let v = f.target;
    v.set_object_provider(Some(lazy_tests::provider(
        f.objects.clone(),
        Arc::new(std::sync::atomic::AtomicUsize::new(0)),
    )))
    .unwrap();
    bind(&v);
    let mut job = v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].clone();
    for _ in 0..128 {
        if job["phase"] == "ready" {
            break;
        }
        job = v
            .control(&json!({"cmd":"cloud.prepare","job_id":job["id"],"max_pages":128}))
            .unwrap()["job"]
            .clone();
    }
    assert_eq!(job["phase"], "ready");
    let before = v.control(&json!({"cmd":"debug.summary"})).unwrap();
    assert!(before["pending_missing_objects"].as_u64().unwrap() >= 2);
    let pending = before["pending_objects"].clone();
    let generation = v.info().unwrap().data_generation;
    let mut out = [0; PAGE];
    v.read(0, &mut out).unwrap();
    assert_eq!(out, [11; PAGE]);
    let cached = v.control(&json!({"cmd":"debug.summary"})).unwrap();
    assert_eq!(cached["pending_objects"], pending);
    assert!(
        cached["pending_missing_objects"].as_u64().unwrap()
            < before["pending_missing_objects"].as_u64().unwrap()
    );
    v.control(&json!({"cmd":"cache.configure","max_bytes":1,"policy":"sequential"}))
        .unwrap();
    v.control(&json!({"cmd":"cache.online","available":true}))
        .unwrap();
    let status = v
        .control(&json!({"cmd":"cache.step","max_objects":4}))
        .unwrap();
    assert!(status["evicted_objects"].as_u64().unwrap() > 0);
    assert_eq!(
        v.control(&json!({"cmd":"debug.summary"})).unwrap()["pending_objects"],
        pending
    );
    assert_eq!(v.info().unwrap().data_generation, generation);
    assert_eq!(
        v.control(&json!({"cmd":"cloud.status"})).unwrap()["job"]["id"],
        job["id"]
    );
    drop(v);
    let v = Volume::open(&f.path, None).unwrap();
    assert_eq!(
        v.control(&json!({"cmd":"debug.summary"})).unwrap()["pending_objects"],
        pending
    );
    // Current containers require complete persisted statistics; do not silently upgrade an older schema.
    {
        let mut s = v.shared.store.lock().unwrap();
        let r = s.root.stats;
        let mut stats: Value = serde_json::from_slice(&s.blob(r).unwrap()).unwrap();
        stats.as_object_mut().unwrap().remove("missing_counts");
        s.transaction(|tx| {
            tx.root.stats = tx.replace_blob(tx.root.stats, &serde_json::to_vec(&stats)?)?;
            Ok(())
        })
        .unwrap();
    }
    drop(v);
    assert!(Volume::open(&f.path, None).is_err());
}
#[test]
fn local_snapshot_restore_holds_hydration_pin_until_batch_has_copied_source() {
    let f = lazy_tests::fixture(None);
    let source = Arc::new(f.target);
    let weak = Arc::downgrade(&source);
    let remote = f.objects.clone();
    let count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let calls = count.clone();
    source
        .set_object_provider(Some(Arc::new(move |r, out| {
            assert!(
                calls.fetch_add(1, Ordering::SeqCst) < 4,
                "restore repeatedly refetched an evicted source"
            );
            let v = weak.upgrade().unwrap();
            let id = Uuid::parse_str(&r.id).unwrap();
            let oid = u64::from_le_bytes(id.as_bytes()[8..].try_into().unwrap());
            assert!(
                v.shared.cache_runtime.is_pinned(oid),
                "local restore must pin the source across hydration"
            );
            out.copy_from_slice(&remote[&r.id]);
            v.lazy_import(&r.id, out)?;
            v.control(&json!({"cmd":"cache.step","max_objects":4}))?;
            assert!(!v.shared.store.lock().unwrap().object(oid).unwrap().missing);
            Ok(())
        })))
        .unwrap();
    source
        .control(&json!({"cmd":"cache.configure","max_bytes":1,"policy":"lru"}))
        .unwrap();
    source
        .control(&json!({"cmd":"cache.online","available":true}))
        .unwrap();
    let snap = source.snapshot_create().unwrap();
    let target = Volume::restore_snapshot_begin_v4(
        &source,
        &snap,
        f._dir.path().join("cache-local-copy.odv4"),
        None,
    )
    .unwrap();
    let mut ready = false;
    for _ in 0..128 {
        let status = target
            .control(&json!({"cmd":"snapshot.restore_step","max_pages":128}))
            .unwrap();
        if status["phase"] == "ready" {
            ready = true;
            break;
        }
    }
    assert!(ready);
    target.control(&json!({"cmd":"restore.finish"})).unwrap();
    let mut out = [0; PAGE];
    for (offset, byte) in [(0, 11), (PAGE as u64, 22), (33 * PAGE as u64, 33)] {
        target.read(offset, &mut out).unwrap();
        assert_eq!(out, [byte; PAGE]);
    }
    assert_eq!(count.load(Ordering::SeqCst), 2);
}
