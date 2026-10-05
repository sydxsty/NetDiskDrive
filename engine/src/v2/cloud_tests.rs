use super::*;
use serde_json::{json, Value};
use std::path::PathBuf;
use std::{
    io::{BufRead, BufReader},
    process::{Command, Stdio},
};
fn quiet(v: &mut Volume) {
    v.shared.stop.store(true, Ordering::Release);
    v.shared.wake.notify_all();
    if let Some(t) = v.worker.take() {
        t.join().unwrap();
    }
}
fn create(path: &Path) -> Volume {
    let mut v = Volume::create_v3(path, 64 << 20, None).unwrap();
    quiet(&mut v);
    v.write(0, &[71; PAGE * 5]).unwrap();
    v.flush().unwrap();
    v
}
fn cmd(v: &Volume, r: Value) -> Value {
    v.control_v3(&r).unwrap()
}
fn prepare(v: &Volume) -> Value {
    for _ in 0..40 {
        let job = cmd(
            v,
            json!({"cmd":"cloud.prepare","max_pages":2,"max_objects":1}),
        )["job"]
            .clone();
        if job["phase"] != "preparing" {
            return job;
        }
    }
    panic!("preparation did not finish")
}
fn export_restore(v: &Volume, path: &Path) {
    let job = prepare(v);
    let list = cmd(v, json!({"cmd":"cloud.list","job_id":job["id"]}));
    let mut remote = std::collections::BTreeMap::new();
    for o in list["items"].as_array().unwrap() {
        let id = o["id"].as_str().unwrap();
        let mut bytes = vec![0; OBJECT as usize];
        v.read_export_v3(job["id"].as_str().unwrap(), id, 0, &mut bytes)
            .unwrap();
        remote.insert(id.to_owned(), bytes);
    }
    let target =
        Volume::restore_begin_v3(path, &remote[job["root_object_id"].as_str().unwrap()], None)
            .unwrap();
    for _ in 0..20 {
        let status = cmd(&target, json!({"cmd":"restore.status"}));
        if status["phase"] == "ready" {
            break;
        }
        for object in status["needed"].as_array().unwrap() {
            let id = object["id"].as_str().unwrap();
            target.restore_accept_v3(id, &remote[id]).unwrap();
        }
    }
    cmd(&target, json!({"cmd":"restore.finish"}));
    let mut bytes = [0; PAGE * 5];
    target.read(0, &mut bytes).unwrap();
    assert_eq!(bytes, [71; PAGE * 5]);
}
#[test]
fn preparation_space_errors_latch_and_reopen_retries_without_duplicate_pages() {
    for stage in ["scan", "index"] {
        for fail in 0..4 {
            let dir = tempfile::tempdir().unwrap();
            let path = dir.path().join("source.odv3");
            let v = create(&path);
            cmd(&v, json!({"cmd":"cloud.prepare"}));
            if stage == "index" {
                for _ in 0..12 {
                    if cmd(&v, json!({"cmd":"cloud.status"}))["job"]["stage"] == "index" {
                        break;
                    }
                    cmd(&v, json!({"cmd":"cloud.prepare","max_pages":2}));
                }
            }
            v.shared.file.fail_after.store(fail, Ordering::Relaxed);
            let result = v.control_v3(&json!({"cmd":"cloud.prepare","max_pages":2}));
            if result.is_err() {
                assert!(v.shared.state.lock().unwrap().failure.is_some());
                assert!(v.write(0, &[8; 512]).is_err());
            }
            drop(v);
            let v = Volume::open(&path, None).unwrap();
            export_restore(&v, &dir.path().join("restored.odv3"));
        }
    }
}
#[test]
fn compact_reopen_reuses_candidates_without_freeing_new_identity() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("source.odv3");
    let v = create(&path);
    cmd(&v, json!({"cmd":"compact.start"}));
    for _ in 0..30 {
        let status = cmd(&v, json!({"cmd":"compact.step","max_pages":2}));
        if status["phase"] == "reclaiming" {
            break;
        }
    }
    drop(v);
    let v = Volume::open(&path, None).unwrap();
    v.write(0, &[91; PAGE * 5]).unwrap();
    v.flush().unwrap();
    for _ in 0..30 {
        if cmd(&v, json!({"cmd":"compact.step","max_pages":2}))["state"] == "done" {
            break;
        }
    }
    v.flush().unwrap();
    drop(v);
    let v = Volume::open(path, None).unwrap();
    let mut bytes = [0; PAGE * 5];
    v.read(0, &mut bytes).unwrap();
    assert_eq!(bytes, [91; PAGE * 5]);
}
#[test]
#[ignore]
fn cloud_crash_child() {
    let Some(path) = std::env::var_os("ODV3_CRASH_PATH") else {
        return;
    };
    let v = create(Path::new(&path));
    cmd(&v, json!({"cmd":"cloud.prepare"}));
    std::env::set_var("ODV2_CRASH_STAGE", std::env::var("ODV3_STAGE").unwrap());
    cmd(&v, json!({"cmd":"cloud.prepare","max_pages":2}));
}
#[test]
fn killed_cloud_prepare_resumes_at_each_commit_boundary() {
    for stage in [
        "after-data",
        "after-metadata",
        "after-root-write",
        "after-root-sync",
        "after-mirror-sync",
    ] {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("source.odv3");
        let mut child = Command::new(std::env::current_exe().unwrap())
            .args([
                "--exact",
                "v2::cloud_tests::cloud_crash_child",
                "--ignored",
                "--nocapture",
            ])
            .env("ODV3_CRASH_PATH", &path)
            .env("ODV3_STAGE", stage)
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()
            .unwrap();
        let mut ready = false;
        for line in BufReader::new(child.stdout.take().unwrap()).lines() {
            if line.unwrap().contains("ODV2-CRASH-READY") {
                ready = true;
                break;
            }
        }
        assert!(ready, "{stage}");
        child.kill().unwrap();
        child.wait().unwrap();
        let v = Volume::open(path, None).unwrap();
        export_restore(&v, &dir.path().join("restored.odv3"));
    }
}
#[test]
fn sparse_one_tib_control_pages_and_names_are_bounded() {
    let dir = tempfile::tempdir().unwrap();
    let v = Volume::create_v3(dir.path().join("large.odv3"), 1 << 40, None).unwrap();
    v.write((1 << 40) - PAGE as u64, &[11; PAGE]).unwrap();
    cmd(&v, json!({"cmd":"snapshot.create","name":"last-page"}));
    let state = cmd(&v, json!({"cmd":"debug.blocks","page":0,"page_size":4}));
    assert!(state["items"].as_array().unwrap().len() <= 4);
    assert!(v
        .control_v3(&json!({"cmd":"snapshot.create","name":""}))
        .is_err());
    v.write(0, &[12; PAGE]).unwrap();
    v.flush().unwrap();
    let mut bytes = [0; PAGE];
    v.read((1 << 40) - PAGE as u64, &mut bytes).unwrap();
    assert_eq!(bytes, [11; PAGE]);
}

#[test]
fn tail_reclamation_returns_space_and_resize_sync_failures_never_reuse_truncated_offsets() {
    for failure in ["none", "resize", "sync"] {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("tail.odv3");
        let v = create(&path);
        let before = std::fs::metadata(&path).unwrap().len();
        if failure == "resize" {
            v.shared.file.fail_resize.store(true, Ordering::Relaxed);
        }
        if failure == "sync" {
            v.shared.file.fail_sync.store(true, Ordering::Relaxed);
        }
        let result = {
            let _epoch = v.shared.epoch.write().unwrap();
            let mut state = v.shared.state.lock().unwrap();
            let result = v.truncate_free_tail(&mut state);
            let end = v.shared.file.len().unwrap() / OBJECT;
            assert!(state.free.iter().all(|n| *n < end));
            result
        };
        if failure == "none" {
            assert!(result.unwrap() > 0);
            assert!(std::fs::metadata(&path).unwrap().len() < before);
        } else {
            assert!(result.is_err());
            assert!(v.write(0, &[5; 512]).is_err());
        }
        drop(v);
        let v = Volume::open(path, None).unwrap();
        let mut bytes = [0; PAGE * 5];
        v.read(0, &mut bytes).unwrap();
        assert_eq!(bytes, [71; PAGE * 5]);
        v.write(PAGE as u64, &[25; PAGE]).unwrap();
        v.flush().unwrap();
    }
}

#[cfg(unix)]
#[test]
fn closing_volume_releases_lock_even_when_a_process_inherited_the_descriptor() {
    unsafe extern "C" {
        fn fork() -> i32;
        fn pause() -> i32;
        fn kill(pid: i32, signal: i32) -> i32;
        fn waitpid(pid: i32, status: *mut i32, options: i32) -> i32;
        fn _exit(status: i32) -> !;
    }
    struct Child(i32);
    impl Drop for Child {
        fn drop(&mut self) {
            unsafe {
                kill(self.0, 9);
                waitpid(self.0, std::ptr::null_mut(), 0);
            }
        }
    }
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("inherited.odv3");
    let v = create(&path);
    let pid = unsafe { fork() };
    assert!(pid >= 0);
    if pid == 0 {
        unsafe {
            pause();
            _exit(0)
        }
    }
    let _child = Child(pid);
    drop(v);
    let reopened = Volume::open(path, None)
        .expect("dropping the owning handle must release its exclusive lock immediately");
    let mut bytes = [0; PAGE * 5];
    reopened.read(0, &mut bytes).unwrap();
    assert_eq!(bytes, [71; PAGE * 5]);
}

#[test]
fn newly_prepared_cloud_data_authenticates_pages_before_receiptable_hash() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("corrupt.odv3");
    let v = create(&path);
    let reference = {
        let state = v.shared.state.lock().unwrap();
        v.shared.lookup(&state.current, 0).unwrap().unwrap()
    };
    let mut ciphertext = [0; PAGE];
    v.shared
        .file
        .read(reference.offset(), &mut ciphertext)
        .unwrap();
    ciphertext[0] ^= 1;
    v.shared
        .file
        .write(reference.offset(), &ciphertext)
        .unwrap();
    v.shared.file.sync().unwrap();
    let mut rejected = false;
    for _ in 0..12 {
        match v.control_v3(&json!({"cmd":"cloud.prepare","max_pages":16})) {
            Ok(j) => assert_eq!(j["job"]["phase"], "preparing"),
            Err(Error::Integrity(_)) => {
                rejected = true;
                break;
            }
            Err(e) => panic!("unexpected {e}"),
        }
    }
    assert!(rejected);
}

#[test]
fn authenticated_restore_gate_survives_public_header_flag_tampering() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("unfinished.odv3");
    let mut v = Volume::create_format(&path, 64 << 20, Some("password"), 3, true).unwrap();
    quiet(&mut v);
    let mut header = v.shared.config.clone();
    header.restoring = false;
    let bytes = header.encode().unwrap();
    v.shared.file.write(0, &bytes).unwrap();
    v.shared.file.write(3 * PAGE as u64, &bytes).unwrap();
    v.shared.file.sync().unwrap();
    drop(v);
    let v = Volume::open(path, Some("password")).unwrap();
    assert!(v.info().unwrap().restore_incomplete);
    assert!(v.read(0, &mut [0; 512]).is_err());
    assert!(v.write(0, &[1; 512]).is_err());
}

fn cloud_package(v: &Volume) -> (Value, std::collections::BTreeMap<String, Vec<u8>>) {
    let job = prepare(v);
    let list = cmd(v, json!({"cmd":"cloud.list","job_id":job["id"]}));
    let mut objects = std::collections::BTreeMap::new();
    for object in list["items"].as_array().unwrap() {
        let id = object["id"].as_str().unwrap();
        let mut bytes = vec![0; OBJECT as usize];
        v.read_export_v3(job["id"].as_str().unwrap(), id, 0, &mut bytes)
            .unwrap();
        objects.insert(id.to_owned(), bytes);
    }
    (job, objects)
}
fn finish_bootstrap_cloud(target: &Volume, objects: &std::collections::BTreeMap<String, Vec<u8>>) {
    for _ in 0..20 {
        let state = cmd(target, json!({"cmd":"restore.status"}));
        if state["phase"] == "ready" {
            break;
        }
        for object in state["needed"].as_array().unwrap() {
            let id = object["id"].as_str().unwrap();
            target.restore_accept_v3(id, &objects[id]).unwrap();
        }
    }
    cmd(target, json!({"cmd":"restore.finish"}));
    let mut data = [0; PAGE * 5];
    target.read(0, &mut data).unwrap();
    assert_eq!(data, [71; PAGE * 5]);
}
#[test]
#[ignore]
fn restore_initialization_crash_child() {
    let Some(directory) = std::env::var_os("ODV3_RESTORE_DIRECTORY") else {
        return;
    };
    let directory = PathBuf::from(directory);
    let stage = std::env::var("ODV3_RESTORE_STAGE").unwrap();
    let source = create(&directory.join("source.odv3"));
    if stage == "cloud-target-created" {
        let (job, objects) = cloud_package(&source);
        for (id, bytes) in &objects {
            std::fs::write(directory.join(id), bytes).unwrap();
        }
        std::fs::write(
            directory.join("root-id"),
            job["root_object_id"].as_str().unwrap(),
        )
        .unwrap();
        std::env::set_var("ODV3_RESTORE_CRASH_STAGE", &stage);
        let _target = Volume::restore_begin_v3(
            directory.join("target.odv3"),
            &objects[job["root_object_id"].as_str().unwrap()],
            None,
        )
        .unwrap();
    } else {
        let id = cmd(
            &source,
            json!({"cmd":"snapshot.create","name":"protected source"}),
        )["snapshot"]["id"]
            .as_str()
            .unwrap()
            .to_owned();
        std::fs::write(directory.join("snapshot-id"), &id).unwrap();
        std::env::set_var("ODV3_RESTORE_CRASH_STAGE", &stage);
        let _target = Volume::restore_snapshot_begin_v3(
            Arc::new(source),
            &id,
            directory.join("target.odv3"),
            Some("bootstrap-target"),
        )
        .unwrap();
    }
}
fn killed_bootstrap(stage: &str) -> tempfile::TempDir {
    let dir = tempfile::tempdir().unwrap();
    let mut child = Command::new(std::env::current_exe().unwrap())
        .args([
            "--exact",
            "v2::cloud_tests::restore_initialization_crash_child",
            "--ignored",
            "--nocapture",
        ])
        .env("ODV3_RESTORE_DIRECTORY", dir.path())
        .env("ODV3_RESTORE_STAGE", stage)
        .stdout(Stdio::piped())
        .stderr(Stdio::inherit())
        .spawn()
        .unwrap();
    let mut ready = false;
    for line in BufReader::new(child.stdout.take().unwrap()).lines() {
        if line.unwrap().contains("ODV3-RESTORE-CRASH-READY") {
            ready = true;
            break;
        }
    }
    assert!(ready, "{stage}");
    child.kill().unwrap();
    child.wait().unwrap();
    dir
}
#[test]
fn bootstrap_cloud_reseeds_after_kill_without_changing_identity_or_mount_gate() {
    let dir = killed_bootstrap("cloud-target-created");
    let path = dir.path().join("target.odv3");
    let v = Volume::open(&path, None).unwrap();
    let id = v.info().unwrap().id;
    assert!(v.info().unwrap().restore_incomplete);
    assert!(v.read(0, &mut [0; 512]).is_err());
    assert!(v.write(0, &[1; 512]).is_err());
    assert_eq!(
        cmd(&v, json!({"cmd":"restore.status"}))["phase"],
        "initializing"
    );
    assert!(v.control_v3(&json!({"cmd":"restore.finish"})).is_err());
    drop(v);
    let source = Volume::open(dir.path().join("source.odv3"), None).unwrap();
    let (job, objects) = cloud_package(&source);
    let target = Volume::restore_begin_v3(
        &path,
        &objects[job["root_object_id"].as_str().unwrap()],
        None,
    )
    .unwrap();
    assert_eq!(target.info().unwrap().id, id);
    finish_bootstrap_cloud(&target, &objects);
}
#[test]
fn bootstrap_local_recovers_before_and_after_pin_commit_without_leaking_or_duplicate_pins() {
    for stage in ["local-target-created", "local-pin-created"] {
        let dir = killed_bootstrap(stage);
        let path = dir.path().join("target.odv3");
        let source = Arc::new(Volume::open(dir.path().join("source.odv3"), None).unwrap());
        let snapshot = std::fs::read_to_string(dir.path().join("snapshot-id")).unwrap();
        let mut target = Volume::open(&path, Some("bootstrap-target")).unwrap();
        let id = target.info().unwrap().id;
        assert_eq!(
            cmd(&target, json!({"cmd":"restore.status"}))["kind"],
            "uninitialized"
        );
        assert!(target.write(0, &[1; 512]).is_err());
        if stage == "local-pin-created" {
            // The durable task pin survives deletion of the user's original
            // snapshot and an initial target-metadata ENOSPC failure.
            cmd(&source, json!({"cmd":"snapshot.delete","id":snapshot}));
            target.shared.file.fail_after.store(0, Ordering::Relaxed);
            assert!(target.restore_snapshot_resume_v3(source.clone()).is_err());
            drop(target);
            target = Volume::open(&path, Some("bootstrap-target")).unwrap();
            assert_eq!(
                cmd(&target, json!({"cmd":"restore.status"}))["phase"],
                "initializing"
            );
        } else {
            assert!(target.restore_snapshot_resume_v3(source.clone()).is_err());
        }
        drop(target);
        let target = Volume::restore_snapshot_begin_v3(
            source.clone(),
            &snapshot,
            &path,
            Some("bootstrap-target"),
        )
        .unwrap();
        assert_eq!(target.info().unwrap().id, id);
        let pins = cmd(&source, json!({"cmd":"snapshot.list"}));
        assert_eq!(
            pins["items"]
                .as_array()
                .unwrap()
                .iter()
                .filter(|s| s["pin"] == "restore")
                .count(),
            1
        );
        source.write(0, &[89; PAGE * 5]).unwrap();
        source.flush().unwrap();
        for _ in 0..8 {
            if cmd(
                &target,
                json!({"cmd":"snapshot.restore_step","max_pages":2}),
            )["phase"]
                == "complete"
            {
                break;
            }
        }
        let mut bytes = [0; PAGE * 5];
        target.read(0, &mut bytes).unwrap();
        assert_eq!(bytes, [71; PAGE * 5]);
        let pins = cmd(&source, json!({"cmd":"snapshot.list"}));
        assert_eq!(
            pins["items"]
                .as_array()
                .unwrap()
                .iter()
                .filter(|s| s["pin"] == "restore")
                .count(),
            0
        );
    }
}
#[test]
fn bootstrap_reseed_rejects_normal_disks_and_existing_restore_progress() {
    let dir = tempfile::tempdir().unwrap();
    let source = Arc::new(create(&dir.path().join("source.odv3")));
    let snapshot = cmd(
        &source,
        json!({"cmd":"snapshot.create","name":"normal source"}),
    )["snapshot"]["id"]
        .as_str()
        .unwrap()
        .to_owned();
    let (job, objects) = cloud_package(&source);
    let root = &objects[job["root_object_id"].as_str().unwrap()];
    for allocated in [false, true] {
        let path = dir.path().join(format!("normal-{allocated}.odv3"));
        let v = Volume::create_v3(&path, 64 << 20, None).unwrap();
        if allocated {
            v.write(0, &[36; PAGE]).unwrap();
            v.flush().unwrap();
        }
        let id = v.info().unwrap().id;
        drop(v);
        assert!(Volume::restore_begin_v3(&path, root, None).is_err());
        assert!(Volume::restore_snapshot_begin_v3(source.clone(), &snapshot, &path, None).is_err());
        let v = Volume::open(&path, None).unwrap();
        assert_eq!(v.info().unwrap().id, id);
        assert!(!v.info().unwrap().restore_incomplete);
        let mut bytes = [0; PAGE];
        v.read(0, &mut bytes).unwrap();
        assert_eq!(bytes, [if allocated { 36 } else { 0 }; PAGE]);
    }
    let path = dir.path().join("in-progress.odv3");
    let v = Volume::restore_begin_v3(&path, root, None).unwrap();
    let status = cmd(&v, json!({"cmd":"restore.status","limit":1}));
    let first = status["needed"][0]["id"].as_str().unwrap();
    v.restore_accept_v3(first, &objects[first]).unwrap();
    drop(v);
    assert!(Volume::restore_begin_v3(&path, root, None).is_err());
    assert!(Volume::restore_snapshot_begin_v3(source.clone(), &snapshot, &path, None).is_err());
    let v = Volume::open(&path, None).unwrap();
    assert_eq!(
        cmd(&v, json!({"cmd":"restore.status"}))["completed_objects"],
        1
    );
    finish_bootstrap_cloud(&v, &objects);
}
