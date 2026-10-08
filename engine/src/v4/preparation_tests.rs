//! Functional and I/O-count regressions; no throughput or elapsed-time benchmark.
use super::*;
use serde_json::{json, Value};

fn step(volume: &Volume, job: &Value) -> Value {
    volume.control(&json!({"cmd":"cloud.prepare","job_id":job["id"],"prepare_cache_mib":16,
        "max_pages":16384,"max_leaf_groups":512,"max_objects":2})).unwrap()["job"].clone()
}

#[test]
fn scoped_io_excludes_other_devices_and_other_threads() {
    let dir = tempfile::tempdir().unwrap();
    let first = Arc::new(Device::open(&dir.path().join("one"), true).unwrap());
    let second = Device::open(&dir.path().join("two"), true).unwrap();
    for device in [&*first, &second] {
        device.grow(PAGE as u64).unwrap();
        device.write(0, &[7; PAGE]).unwrap();
    }
    let scope = first.io_scope();
    first.read(0, &mut [0; PAGE]).unwrap();
    second.read(0, &mut [0; PAGE]).unwrap();
    let foreground = first.clone();
    std::thread::spawn(move || {
        foreground.read(0, &mut [0; PAGE]).unwrap();
        foreground.write(0, &[9; PAGE]).unwrap();
        foreground.sync().unwrap();
    }).join().unwrap();
    first.sync().unwrap();
    let counted = scope.snapshot();
    assert_eq!(counted.local_read_bytes, PAGE as u64);
    assert_eq!(counted.read_calls, 1);
    assert_eq!(counted.local_write_bytes, 0);
    assert_eq!(counted.flush_count, 1);
}

#[test]
fn cache_setting_rejects_bad_input_without_flush_or_disk_mutation() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("invalid.odv4");
    Volume::create(&path, 64 << 20, None).unwrap();
    let volume = Volume::open(&path, None).unwrap();
    let before = volume.shared.device.diagnostics();
    for value in [json!(0),json!(15),json!(1025),json!(64.5),json!(-1),json!("64"),Value::Null] {
        assert!(volume.control(&json!({"cmd":"cloud.prepare","prepare_cache_mib":value})).is_err());
    }
    assert_eq!(volume.shared.device.diagnostics(), before);
    assert!(volume.control(&json!({"cmd":"cloud.status"})).unwrap()["job"].is_null());
}

#[test]
fn preparation_resume_keeps_snapshot_new_changes_and_scoped_progress() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("resume.odv4");
    Volume::create(&path, 128 << 20, Some("prepare-fixture")).unwrap();
    let volume = Volume::open(&path, Some("prepare-fixture")).unwrap();
    // More than one leaf-budget batch without allocating a large payload.
    for n in 0..260u64 {
        volume.write((4096 + n * 32) * PAGE as u64, &[23; PAGE]).unwrap();
    }
    volume.flush().unwrap();
    let mut job = volume.control(&json!({"cmd":"cloud.prepare","prepare_cache_mib":16})).unwrap()["job"].clone();
    assert_eq!(job["changed_pages"], 260);
    assert_eq!(job["preparation_diagnostics"]["totals"]["flush_count"], 3);
    while job["prepare_stage"] == "sealing" { job = step(&volume, &job); }
    job = volume.control(&json!({"cmd":"cloud.prepare","job_id":job["id"],"prepare_cache_mib":16,
        "max_leaf_groups":128,"max_pages":4096})).unwrap()["job"].clone();
    assert_eq!(job["processed_pages"], 128);
    let generation = job["generation"].clone();
    volume.write(4096 * PAGE as u64, &[77; PAGE]).unwrap();
    volume.flush().unwrap();
    let before_query = volume.shared.device.diagnostics();
    let diagnostics = volume.control(&json!({"cmd":"sync.diagnostics"})).unwrap();
    assert_eq!(volume.shared.device.diagnostics(), before_query, "diagnostics must not read a cloud blob");
    assert_eq!(diagnostics["preparation_diagnostics"]["job_id"], job["id"]);
    drop(volume);
    let volume = Volume::open(&path, Some("prepare-fixture")).unwrap();
    assert!(volume.control(&json!({"cmd":"sync.diagnostics"})).unwrap()["preparation_diagnostics"].is_null());
    for _ in 0..16 {
        if job["phase"] == "ready" { break; }
        job = step(&volume, &job);
    }
    assert_eq!(job["phase"], "ready");
    assert_eq!(job["processed_pages"], 260);
    assert_eq!(job["generation"], generation);
    let status = volume.control(&json!({"cmd":"cloud.status"})).unwrap();
    assert_eq!(status["changed_pages"], 1);
    assert!(status["local_dirty"].as_bool().unwrap());
    assert_eq!(job["preparation_diagnostics"]["scope"], "process_session");
    let mut bytes = [0; PAGE];
    volume.read(4096 * PAGE as u64, &mut bytes).unwrap();
    assert_eq!(bytes, [77; PAGE]);
    let snapshot = volume.shared.store.lock().unwrap().cloud.job.as_ref().unwrap().snapshot;
    let (_lease, mut reader) = volume.shared.read_snapshot(snapshot).unwrap();
    assert_eq!(*reader.read_page(4096).unwrap(), [23; PAGE]);
    let unchanged = volume.shared.device.diagnostics();
    let ready = step(&volume, &job);
    assert_eq!(ready["id"], job["id"]);
    assert_eq!(volume.shared.device.diagnostics()["physical_write_bytes"], unchanged["physical_write_bytes"]);
}

#[test]
fn original_lazy_preparation_flushes_buffered_changes_before_baseline_counts() {
    let fixture = lazy_tests::fixture(None);
    let options = base_tests::options(&fixture, "original", true);
    let volume = Volume::restore_begin_options(fixture._dir.path().join("buffered-original.odv4"),
        &fixture.root, None, options.clone()).unwrap();
    volume.control(&json!({"cmd":"replica.bootstrap","commit":options["publication"]["commit"]})).unwrap();
    base_tests::finish(&volume, &fixture, 128);
    volume.set_object_provider(Some(lazy_tests::provider(fixture.objects.clone(),
        Arc::new(std::sync::atomic::AtomicUsize::new(0))))).unwrap();
    // Avoid the background commit worker so this exercises the exact boundary:
    // the first write is visible only in the frontend cache at prepare entry.
    volume.stop_background_commit_for_test();
    volume.write(0, &[87; PAGE]).unwrap();
    {
        let store = volume.shared.store.lock().unwrap();
        assert_eq!(store.root.changed_pages, 0);
        assert!(!store.cloud.replica.as_ref().unwrap().counts_complete.is_complete());
    }
    let mut job = volume.control(&json!({"cmd":"cloud.prepare","prepare_cache_mib":16})).unwrap()["job"].clone();
    for _ in 0..16 {
        if job["phase"] == "ready" { break; }
        job = step(&volume, &job);
    }
    assert_eq!(job["phase"], "ready");
    assert_eq!(job["changed_pages"], 1);
    assert_eq!(job["processed_pages"], 1);
    assert!(volume.info().unwrap().background_error.is_none());
    let mut bytes = [0; PAGE];
    volume.read(0, &mut bytes).unwrap();
    assert_eq!(bytes, [87; PAGE]);
    volume.read(PAGE as u64, &mut bytes).unwrap();
    assert_eq!(bytes, [22; PAGE]);
}

#[test]
fn pause_releases_preparation_cache_and_published_noop_does_no_io() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("paused.odv4");
    Volume::create(&path, 64 << 20, None).unwrap();
    let volume = Volume::open(&path, None).unwrap();
    volume.write(0, &[17; PAGE]).unwrap();
    let mut job = volume.control(&json!({"cmd":"cloud.prepare","prepare_cache_mib":16})).unwrap()["job"].clone();
    let id = job["id"].clone();
    assert!(job["preparation_diagnostics"]["cache"]["prepare_cache_used_bytes"].as_u64().unwrap() > 0);
    volume.control(&json!({"cmd":"cloud.pause","paused":true})).unwrap();
    let paused = volume.control(&json!({"cmd":"sync.diagnostics"})).unwrap();
    assert_eq!(paused["preparation_diagnostics"]["job_id"], id);
    assert_eq!(paused["preparation_diagnostics"]["cache"]["prepare_cache_used_bytes"], 0);
    assert_eq!(volume.shared.store.lock().unwrap().prepare_cache_metrics()["prepare_cache_used_bytes"], 0);
    volume.control(&json!({"cmd":"cloud.pause","paused":false})).unwrap();
    for _ in 0..16 {
        if job["phase"] == "ready" { break; }
        job = step(&volume, &job);
    }
    assert_eq!(job["phase"], "ready");
    assert_eq!(job["id"], id);
    assert_eq!(job["preparation_diagnostics"]["cache"]["prepare_cache_used_bytes"], 0);
    let objects = volume.control(&json!({"cmd":"cloud.list","job_id":id,"limit":128})).unwrap();
    let receipts = objects["items"].as_array().unwrap().iter().map(|o|
        json!({"object_id":o["id"],"sha256":o["sha256"],"length":volume.object_size()})).collect::<Vec<_>>();
    volume.control(&json!({"cmd":"cloud.receipt","job_id":id,"records":receipts})).unwrap();
    volume.control(&json!({"cmd":"cloud.commit","job_id":id,"root_object_id":job["root_object_id"],
        "root_sha256":job["root_sha256"],"receipt":"isolated-test"})).unwrap();
    let before = volume.shared.device.diagnostics();
    let previous_report = volume.preparation_diagnostics(None);
    let no_op = volume.control(&json!({"cmd":"cloud.prepare","prepare_cache_mib":32})).unwrap();
    assert!(no_op["job"].is_null());
    assert_eq!(no_op["up_to_date"], true);
    assert_eq!(volume.shared.device.diagnostics(), before, "a published unchanged disk must not read, write, or flush");
    assert_eq!(volume.preparation_diagnostics(None), previous_report, "a no-op must not reset or append to the previous job");
}
