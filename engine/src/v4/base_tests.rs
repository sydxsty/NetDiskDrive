use super::*;
use serde_json::{json, Value};
use std::sync::atomic::AtomicUsize;
pub(super) fn options(f: &lazy_tests::Fixture, mode: &str, lazy: bool) -> Value {
    let mut backing = f.backing.clone();
    let id = f.source.info().unwrap().id;
    backing["remote_root"] = json!(format!("/OverlayDisk/{id}"));
    let writer = Uuid::new_v4();
    let generation =
        f.source.control(&json!({"cmd":"cloud.status"})).unwrap()["job"]["generation"].clone();
    let publication = json!({"commit":{"formatVersion":4,"objectSizeBytes":f.source.object_size(),"volumeId":id,"writerId":writer,"generation":generation,"name":"source","capacityBytes":f.source.capacity(),"encrypted":f.source.info().unwrap().encrypted,"rootObjectId":backing["root_object_id"],"rootSha256":backing["root_sha256"],"rootSlot":0,"updatedUtc":"2026-10-05T00:00:00Z"},"binding":{"backend_id":"mock","account_id":"account","remote_root":backing["remote_root"],"device_id":writer,"enabled":true}});
    json!({"mode":mode,"lazy":lazy,"source_volume_id":id,"backing":backing,"publication":if mode=="original"{publication}else{Value::Null}})
}
pub(super) fn finish(v: &Volume, f: &lazy_tests::Fixture, step: u64) -> Value {
    for _ in 0..128 {
        let st = v.control(&json!({"cmd":"restore.status"})).unwrap();
        assert_eq!(st["queued_page_records"], 0);
        if st["phase"] == "ready" {
            return v.control(&json!({"cmd":"restore.finish"})).unwrap();
        }
        for n in st["needed"].as_array().unwrap() {
            let id = n["id"].as_str().unwrap();
            v.restore_accept(id, &f.objects[id]).unwrap();
        }
        v.control(&json!({"cmd":"restore.step","max_nodes":step,"max_objects":2}))
            .unwrap();
    }
    panic!("base restore did not finish")
}
fn prepare(v: &Volume) -> Value {
    let mut j = v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].clone();
    for _ in 0..128 {
        if j["phase"] == "ready" {
            return j;
        }
        j = v
            .control(&json!({"cmd":"cloud.prepare","job_id":j["id"],"max_pages":256}))
            .unwrap()["job"]
            .clone();
    }
    panic!("cloud prepare did not finish")
}
#[test]
fn original_base_noop_then_only_changed_path_survives_snapshot_and_reopen() {
    let f = lazy_tests::fixture(Some("password"));
    let path = f._dir.path().join("original.odv4");
    let opt = options(&f, "original", true);
    let v = Volume::restore_begin_options(&path, &f.root, Some("password"), opt.clone()).unwrap();
    assert_eq!(
        v.control(&json!({"cmd":"cloud.status"})).unwrap()["binding"],
        opt["publication"]["binding"]
    );
    let st = finish(&v, &f, 1);
    assert_eq!(st["mode"], "original");
    assert_eq!(st["index_mode"], "portable_base");
    assert_eq!(st["metadata_object_reads"], 0);
    assert!(st["metadata_cache_hits"].as_u64().unwrap() > 0);
    assert_eq!(v.info().unwrap().id, f.source.info().unwrap().id);
    let cloud = v.control(&json!({"cmd":"cloud.status"})).unwrap();
    assert_eq!(cloud["data_generation"], cloud["published_generation"]);
    assert_eq!(cloud["changed_pages"], 0);
    assert_eq!(cloud["local_dirty"], false);
    assert_eq!(
        cloud["published_commit"]["root_object_id"],
        f.backing["root_object_id"]
    );
    assert!(v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
    {
        let store = v.shared.store.lock().unwrap();
        assert_eq!(store.root.index, store.cloud.published_index);
        assert_ne!(store.root.index.offset & store::PORTABLE, 0);
        assert!(store.root.dirty.empty());
    }
    let calls = Arc::new(AtomicUsize::new(0));
    v.set_object_provider(Some(lazy_tests::provider(f.objects.clone(), calls.clone())))
        .unwrap();
    let snapshot = v.snapshot_create().unwrap();
    v.write(0, &[87; PAGE]).unwrap();
    v.flush().unwrap();
    assert_eq!(calls.load(Ordering::SeqCst), 0);
    let job = prepare(&v);
    assert_eq!(job["processed_pages"], 1);
    let list = v
        .control(&json!({"cmd":"cloud.list","job_id":job["id"],"limit":128}))
        .unwrap();
    assert_eq!(list["items"].as_array().unwrap().len(), 2);
    assert_eq!(calls.load(Ordering::SeqCst), 0);
    for n in list["items"].as_array().unwrap() {
        let id = n["id"].as_str().unwrap();
        assert!(!f.objects.contains_key(id));
        let mut raw = vec![0; OBJECT as usize];
        v.read_export(job["id"].as_str().unwrap(), id, 0, &mut raw)
            .unwrap();
        v.control(&json!({"cmd":"cloud.receipt","job_id":job["id"],"object_id":id,"sha256":n["sha256"],"length":OBJECT,"receipt":"ack"})).unwrap();
    }
    v.control(&json!({"cmd":"cloud.commit","job_id":job["id"],"root_object_id":job["root_object_id"],"root_sha256":job["root_sha256"],"receipt":"ack"})).unwrap();
    let manifest = v.snapshot_manifest(&snapshot).unwrap();
    for n in manifest["objects"].as_array().unwrap() {
        let id = n["id"].as_str().unwrap();
        let mut raw = vec![0; OBJECT as usize];
        v.object_read(&snapshot, id, 0, &mut raw).unwrap();
        assert_eq!(raw, f.objects[id]);
    }
    v.snapshot_release(&snapshot).unwrap();
    drop(v);
    let v = Volume::open(&path, Some("password")).unwrap();
    let mut out = [0; PAGE];
    v.read(0, &mut out).unwrap();
    assert_eq!(out, [87; PAGE]);
    v.read(PAGE as u64, &mut out).unwrap();
    assert_eq!(out, [22; PAGE]);
    v.control(&json!({"cmd":"compact.start","mode":"normal"}))
        .unwrap();
    for _ in 0..128 {
        if v.control(&json!({"cmd":"compact.step","max_objects":32}))
            .unwrap()["state"]
            == "done"
        {
            break;
        }
    }
    v.read(33 * PAGE as u64, &mut out).unwrap();
    assert_eq!(out, [33; PAGE]);
}
#[test]
fn original_sessions_have_separate_new_object_names_and_reject_mismatched_source() {
    let f = lazy_tests::fixture(None);
    let opt = options(&f, "original", true);
    let mut ids = Vec::new();
    for name in ["one", "two"] {
        let path = f._dir.path().join(format!("{name}.odv4"));
        let v = Volume::restore_begin_options(&path, &f.root, None, opt.clone()).unwrap();
        finish(&v, &f, 256);
        v.write(0, &[42; PAGE]).unwrap();
        v.flush().unwrap();
        let snapshot = v.snapshot_create().unwrap();
        let m = v.snapshot_manifest(&snapshot).unwrap();
        let id = m["objects"]
            .as_array()
            .unwrap()
            .iter()
            .find(|o| !f.objects.contains_key(o["id"].as_str().unwrap()))
            .unwrap()["id"]
            .as_str()
            .unwrap()
            .to_owned();
        drop(v);
        let v = Volume::open(&path, None).unwrap();
        assert!(v.snapshot_manifest(&snapshot).unwrap()["objects"]
            .as_array()
            .unwrap()
            .iter()
            .any(|o| o["id"] == id));
        ids.push(id);
    }
    assert_ne!(ids[0], ids[1]);
    assert_eq!(
        &Uuid::parse_str(&ids[0]).unwrap().as_bytes()[8..],
        &Uuid::parse_str(&ids[1]).unwrap().as_bytes()[8..]
    );
    let path = f._dir.path().join("wrong.odv4");
    let mut invalid = opt;
    invalid["source_volume_id"] = json!(Uuid::new_v4());
    assert!(Volume::restore_begin_options(&path, &f.root, None, invalid).is_err());
    assert!(!path.exists());
}
#[test]
fn portable_metadata_import_has_node_checkpoints_not_page_jobs_or_repeated_object_reads() {
    let dir = tempfile::tempdir().unwrap();
    let source_path = dir.path().join("dense.odv4");
    Volume::create(&source_path, 64 << 20, None).unwrap();
    let source = Volume::open(source_path, None).unwrap();
    source.write(0, &vec![19; 512 * PAGE]).unwrap();
    source.flush().unwrap();
    let job = prepare(&source);
    let list = source
        .control(&json!({"cmd":"cloud.list","job_id":job["id"],"limit":128}))
        .unwrap();
    let mut objects = std::collections::HashMap::new();
    for o in list["items"].as_array().unwrap() {
        let id = o["id"].as_str().unwrap();
        let mut raw = vec![0; OBJECT as usize];
        source
            .read_export(job["id"].as_str().unwrap(), id, 0, &mut raw)
            .unwrap();
        objects.insert(id.to_owned(), raw);
    }
    let root_id = job["root_object_id"].as_str().unwrap();
    let backing = json!({"provider_id":"mock","account_id":"account","remote_root":format!("/OverlayDisk/{}",source.info().unwrap().id),"reader_pin":"pin","source_volume_id":source.info().unwrap().id,"root_object_id":root_id,"root_sha256":job["root_sha256"]});
    let target = Volume::lazy_begin(
        dir.path().join("base.odv4"),
        &objects[root_id],
        None,
        backing,
    )
    .unwrap();
    let mut steps = 0;
    loop {
        let st = target.control(&json!({"cmd":"restore.status"})).unwrap();
        if st["phase"] == "ready" {
            assert_eq!(st["completed_pages"], 512);
            assert_eq!(st["queued_page_records"], 0);
            assert_eq!(st["metadata_object_reads"], 0);
            assert!(st["completed_nodes"].as_u64().unwrap() < 32);
            break;
        }
        for n in st["needed"].as_array().unwrap() {
            assert_eq!(n["kind"], "metadata");
            let id = n["id"].as_str().unwrap();
            target.restore_accept(id, &objects[id]).unwrap();
        }
        target
            .control(&json!({"cmd":"restore.step","max_nodes":1,"max_objects":1}))
            .unwrap();
        steps += 1;
        assert!(steps < 32);
    }
    assert_eq!(
        target
            .shared
            .device
            .events
            .lock()
            .unwrap()
            .iter()
            .filter(|(write, _, len)| !*write && *len == OBJECT as usize)
            .count(),
        0
    );
    {
        let mut store = target.shared.store.lock().unwrap();
        assert!(store.root.dirty.empty());
        let root = store.root.portable_refs;
        assert!(tree::len(&mut *store, &store::PORTREFS, root).unwrap() < 32);
        assert!(store.restore_cache.len() <= 8);
    }
    target.control(&json!({"cmd":"restore.finish"})).unwrap();
    let job = prepare(&target);
    assert_eq!(
        job["processed_pages"], 0,
        "copy baseline must not synthesize dirty pages"
    );
}
#[test]
fn original_import_reopens_each_node_and_full_import_materializes_without_page_jobs() {
    let f = lazy_tests::fixture(None);
    let path = f._dir.path().join("original-resume.odv4");
    let opt = options(&f, "original", true);
    let mut v = Volume::restore_begin_options(&path, &f.root, None, opt.clone()).unwrap();
    let mut done = false;
    for _ in 0..64 {
        let st = v.control(&json!({"cmd":"restore.status"})).unwrap();
        assert_eq!(st["mode"], "original");
        assert_eq!(st["queued_page_records"], 0);
        if st["phase"] == "ready" {
            done = true;
            break;
        }
        for n in st["needed"].as_array().unwrap() {
            let id = n["id"].as_str().unwrap();
            v.restore_accept(id, &f.objects[id]).unwrap();
        }
        v.control(&json!({"cmd":"restore.step","max_nodes":1}))
            .unwrap();
        drop(v);
        v = Volume::open(&path, None).unwrap();
    }
    assert!(done);
    v.control(&json!({"cmd":"restore.finish"})).unwrap();
    assert!(v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
    let path = f._dir.path().join("original-full.odv4");
    let full = Volume::restore_begin_options(&path, &f.root, None, options(&f, "original", false))
        .unwrap();
    finish(&full, &f, 64);
    assert_eq!(
        full.control(&json!({"cmd":"lazy.status"})).unwrap()["enabled"],
        false
    );
    assert!(full.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
    let mut bytes = [0; PAGE];
    for (at, val) in [(0, 11), (PAGE as u64, 22), (33 * PAGE as u64, 33)] {
        full.read(at, &mut bytes).unwrap();
        assert_eq!(bytes, [val; PAGE]);
    }
    let mut mismatch = opt;
    mismatch["publication"]["binding"]["account_id"] = json!("different");
    let wrong = f._dir.path().join("bad-binding.odv4");
    assert!(Volume::restore_begin_options(&wrong, &f.root, None, mismatch).is_err());
    assert!(!wrong.exists());
}
#[test]
fn published_empty_original_generation_zero_stays_clean() {
    let dir = tempfile::tempdir().unwrap();
    let source_path = dir.path().join("empty.odv4");
    Volume::create(&source_path, 64 << 20, None).unwrap();
    let source = Volume::open(source_path, None).unwrap();
    let job = prepare(&source);
    assert_eq!(job["generation"], 0);
    let mut root = vec![0; OBJECT as usize];
    source
        .read_export(
            job["id"].as_str().unwrap(),
            job["root_object_id"].as_str().unwrap(),
            0,
            &mut root,
        )
        .unwrap();
    let id = source.info().unwrap().id;
    let writer = Uuid::new_v4();
    let remote_root = format!("/OverlayDisk/{id}");
    let options = json!({"mode":"original","lazy":true,"source_volume_id":id,"backing":{"provider_id":"mock","account_id":"account","remote_root":remote_root,"reader_pin":"pin","source_volume_id":id,"root_object_id":job["root_object_id"],"root_sha256":job["root_sha256"]},"publication":{"commit":{"formatVersion":4,"objectSizeBytes":source.object_size(),"volumeId":id,"writerId":writer,"generation":0,"rootObjectId":job["root_object_id"],"rootSha256":job["root_sha256"],"capacityBytes":64<<20,"encrypted":false,"rootSlot":0},"binding":{"backend_id":"mock","account_id":"account","remote_root":remote_root,"device_id":writer,"enabled":true}}});
    let path = dir.path().join("original-empty.odv4");
    let target = Volume::restore_begin_options(&path, &root, None, options).unwrap();
    assert_eq!(
        target.control(&json!({"cmd":"restore.status"})).unwrap()["phase"],
        "ready"
    );
    target.control(&json!({"cmd":"restore.finish"})).unwrap();
    drop(target);
    let target = Volume::open(&path, None).unwrap();
    let status = target.control(&json!({"cmd":"cloud.status"})).unwrap();
    assert_eq!(status["data_generation"], 0);
    assert_eq!(status["published_generation"], 0);
    assert_eq!(status["local_dirty"], false);
    let next = target.control(&json!({"cmd":"cloud.prepare"})).unwrap();
    assert!(next["job"].is_null());
    assert_eq!(next["up_to_date"], true);
    let mut bytes = [99; PAGE];
    target.read(0, &mut bytes).unwrap();
    assert_eq!(bytes, [0; PAGE]);
}

#[test]
fn full_restore_frontier_reports_local_work_after_downloads_and_reopen() {
    let f = lazy_tests::fixture(None);
    let path = f._dir.path().join("public-full-restore.odv4");
    let mut target = Volume::restore_begin_v4(&path, &f.root, None).unwrap();
    let mut downloaded_data = false;
    let mut finished = false;
    let mut saw_local_data_step = false;
    for _ in 0..32 {
        let status = target.control(&json!({"cmd":"restore.status"})).unwrap();
        let needed = status["needed"].as_array().unwrap();
        match status["phase"].as_str().unwrap() {
            "ready" => {
                target.control(&json!({"cmd":"restore.finish"})).unwrap();
                finished = true;
                break;
            }
            "building" => {
                assert!(needed.is_empty());
                if status["work_phase"] == "data" {
                    assert!(downloaded_data);
                    saw_local_data_step = true;
                }
                assert!(target.info().unwrap().restore_incomplete);
                assert!(target.read(0, &mut [0; 512]).is_err());
                target
                    .control(&json!({"cmd":"restore.step","max_nodes":128}))
                    .unwrap();
            }
            "metadata" | "data" => {
                assert!(
                    !needed.is_empty(),
                    "download phase must expose an actionable frontier: {status}"
                );
                let data = status["phase"] == "data";
                for object in needed {
                    let id = object["id"].as_str().unwrap();
                    target.restore_accept(id, &f.objects[id]).unwrap();
                }
                if data {
                    downloaded_data = true;
                    drop(target);
                    target = Volume::open(&path, None).unwrap();
                }
            }
            other => panic!("unexpected restore phase {other}"),
        }
    }
    assert!(finished && downloaded_data && saw_local_data_step);
    for (offset, value) in [(0, 11), (PAGE as u64, 22), (33 * PAGE as u64, 33)] {
        let mut bytes = [0; PAGE];
        target.read(offset, &mut bytes).unwrap();
        assert_eq!(bytes, [value; PAGE]);
    }
}
