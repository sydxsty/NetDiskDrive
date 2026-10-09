use super::*;
use serde_json::{json, Value};
use std::{collections::HashMap, sync::atomic::AtomicUsize};

fn identity_frame(regions: &[(u64, &[u8])]) -> Vec<u8> {
    let mut frame = (regions.len() as u32).to_le_bytes().to_vec();
    for (offset, bytes) in regions {
        frame.extend_from_slice(&offset.to_le_bytes());
        frame.extend_from_slice(&(bytes.len() as u32).to_le_bytes());
        frame.extend_from_slice(bytes);
    }
    frame
}

fn begin(f: &lazy_tests::Fixture, name: &str, password: Option<&str>) -> Volume {
    let disk = Volume::restore_begin_options(
        f._dir.path().join(name),
        &f.root,
        password,
        json!({"mode":"copy","lazy":true,"backing":f.backing}),
    )
    .unwrap();
    disk.control(&json!({"cmd":"replica.bootstrap","commit":commit(f)}))
        .unwrap();
    for _ in 0..8 {
        let s = disk.control(&json!({"cmd":"restore.status"})).unwrap();
        if s["phase"] == "ready" {
            break;
        }
        for n in s["needed"].as_array().unwrap() {
            let id = n["id"].as_str().unwrap();
            disk.restore_accept(id, &f.objects[id]).unwrap();
        }
        disk.control(&json!({"cmd":"restore.step"})).unwrap();
    }
    disk.control(&json!({"cmd":"restore.finish"})).unwrap();
    disk
}
fn commit(f: &lazy_tests::Fixture) -> Value {
    let status = f.source.control(&json!({"cmd":"cloud.status"})).unwrap();
    let job = &status["job"];
    json!({"formatVersion":4,"volumeId":f.source.info().unwrap().id,"writerId":"12345678-1111-1111-1111-111111111111",
        "generation":job["generation"],"name":"fixture","capacityBytes":64u64<<20,"encrypted":f.source.info().unwrap().encrypted,
        "objectSizeBytes":OBJECT,"rootObjectId":f.backing["root_object_id"],"rootSha256":f.backing["root_sha256"],"transportCodec":"zstd-v1"})
}
fn options(f: &lazy_tests::Fixture) -> Value {
    json!({"backing":f.backing,"commit":commit(f)})
}
fn apply(disk: &Volume, discard: bool) -> Result<Value> {
    let s = disk.control(&json!({"cmd":"replica.status"}))?;
    disk.control(&json!({"cmd":"replica.apply","token":s["candidate"]["token"],"expected_revision":s["candidate"]["expected_revision"],"discard_local":discard}))
}

#[test]
fn quick_import_reads_only_top_index_and_persists_read_only_data_hydration() {
    let f = lazy_tests::fixture(None);
    let disk = begin(&f, "quick.odv4", None);
    let s = disk.control(&json!({"cmd":"restore.status"})).unwrap();
    assert_eq!(s["completed_nodes"], 1);
    assert_eq!(s["completed_pages"], 0);
    let state = disk.control(&json!({"cmd":"replica.status"})).unwrap();
    assert_eq!(state["local_changes"], false);
    assert_eq!(state["index_complete"], false);
    let calls = Arc::new(AtomicUsize::new(0));
    disk.set_object_provider(Some(lazy_tests::provider(f.objects.clone(), calls.clone())))
        .unwrap();
    disk.control(&json!({"cmd":"volume.read_only","enabled":true}))
        .unwrap();
    let mut bytes = [0; PAGE];
    disk.read(0, &mut bytes).unwrap();
    assert_eq!(bytes, [11; PAGE]);
    assert_eq!(calls.load(Ordering::SeqCst), 1);
    disk.read(0, &mut bytes).unwrap();
    assert_eq!(calls.load(Ordering::SeqCst), 1);
    assert!(disk.write(0, &[99; PAGE]).is_err());
    assert_eq!(
        disk.control(&json!({"cmd":"replica.status"})).unwrap()["local_changes"],
        false
    );
    drop(disk);
    let disk = Volume::open(f._dir.path().join("quick.odv4"), None).unwrap();
    disk.read(0, &mut bytes).unwrap();
    assert_eq!(bytes, [11; PAGE]);
}
#[test]
fn full_page_write_avoids_old_data_partial_write_hydrates_and_snapshots_survive() {
    let f = lazy_tests::fixture(None);
    let disk = begin(&f, "write.odv4", None);
    let calls = Arc::new(AtomicUsize::new(0));
    disk.set_object_provider(Some(lazy_tests::provider(f.objects.clone(), calls.clone())))
        .unwrap();
    let snap = disk.snapshot_create().unwrap();
    disk.write(0, &[55; PAGE]).unwrap();
    disk.flush().unwrap();
    assert_eq!(calls.load(Ordering::SeqCst), 0);
    disk.write(PAGE as u64, &[66; 512]).unwrap();
    disk.flush().unwrap();
    assert_eq!(calls.load(Ordering::SeqCst), 1);
    let mut bytes = [0; PAGE];
    disk.read(PAGE as u64, &mut bytes).unwrap();
    assert_eq!(&bytes[..512], &[66; 512]);
    assert_eq!(&bytes[512..], &[22; PAGE - 512]);
    let (_lease, mut reader) = disk
        .shared
        .read_snapshot(
            disk.shared
                .store
                .lock()
                .unwrap()
                .snapshots
                .iter()
                .find(|s| s.id == snap)
                .unwrap()
                .index,
        )
        .unwrap();
    // Snapshot's first page remains remote until actually requested.
    assert!(matches!(reader.read_page(0), Err(Error::Missing(_))));
    drop(reader);
    disk.snapshot_release(&snap).unwrap();
}
#[test]
fn replacement_requires_exact_confirmation_and_keeps_cancelled_local_changes() {
    let f = lazy_tests::fixture(None);
    let disk = begin(&f, "replace.odv4", None);
    disk.set_object_provider(Some(lazy_tests::provider(
        f.objects.clone(),
        Arc::new(AtomicUsize::new(0)),
    )))
    .unwrap();
    disk.write(0, &[77; PAGE]).unwrap();
    disk.flush().unwrap();
    disk.replica_stage(&f.root, &options(&f)).unwrap();
    assert!(apply(&disk, false).is_err());
    disk.control(&json!({"cmd":"replica.cancel"})).unwrap();
    let mut b = [0; PAGE];
    disk.read(0, &mut b).unwrap();
    assert_eq!(b, [77; PAGE]);
    disk.replica_stage(&f.root, &options(&f)).unwrap();
    disk.write(PAGE as u64, &[88; PAGE]).unwrap();
    disk.flush().unwrap();
    assert!(apply(&disk, true).is_err());
    disk.replica_stage(&f.root, &options(&f)).unwrap();
    apply(&disk, true).unwrap();
    disk.read(0, &mut b).unwrap();
    assert_eq!(b, [11; PAGE]);
    assert_eq!(
        disk.control(&json!({"cmd":"replica.status"})).unwrap()["local_changes"],
        false
    );
    drop(disk);
    let disk = Volume::open(f._dir.path().join("replace.odv4"), None).unwrap();
    assert_eq!(
        disk.control(&json!({"cmd":"replica.status"})).unwrap()["local_changes"],
        false
    );
    disk.read(0, &mut b).unwrap();
    assert_eq!(b, [11; PAGE]);
}
#[test]
fn invalid_root_and_truncated_object_never_replace_or_poison_current_view() {
    let f = lazy_tests::fixture(None);
    let disk = begin(&f, "bad.odv4", None);
    let before = disk.control(&json!({"cmd":"replica.status"})).unwrap();
    let mut bad = f.root.clone();
    bad[12345] ^= 1;
    assert!(disk.replica_stage(&bad, &options(&f)).is_err());
    assert!(disk.replica_stage(&f.root[..1000], &options(&f)).is_err());
    disk.set_object_provider(Some(lazy_tests::provider(
        f.objects.clone(),
        Arc::new(AtomicUsize::new(0)),
    )))
    .unwrap();
    let mut b = [0; PAGE];
    disk.read(0, &mut b).unwrap();
    assert_eq!(b, [11; PAGE]);
    let after = disk.control(&json!({"cmd":"replica.status"})).unwrap();
    assert_eq!(before["root_object_id"], after["root_object_id"]);
    assert!(disk.info().unwrap().background_error.is_none());
}
#[test]
fn identity_is_local_presentation_not_a_dirty_page_and_empty_plan_is_durable() {
    let f = lazy_tests::fixture(None);
    let disk = begin(&f, "identity.odv4", None);
    disk.set_object_provider(Some(lazy_tests::provider(
        f.objects.clone(),
        Arc::new(AtomicUsize::new(0)),
    )))
    .unwrap();
    disk.replica_identity(&identity_frame(&[(440, &[1, 2, 3, 4])]))
        .unwrap();
    let mut b = [0; PAGE];
    disk.read(0, &mut b).unwrap();
    assert_eq!(&b[440..444], &[1, 2, 3, 4]);
    let (_lease, mut r) = disk.read_context().unwrap();
    assert_eq!(r.read_page(0).unwrap().as_ref(), &[11; PAGE]);
    assert_eq!(
        disk.control(&json!({"cmd":"replica.status"})).unwrap()["local_changes"],
        false
    );
    disk.replica_identity(&identity_frame(&[])).unwrap();
    assert_eq!(
        disk.control(&json!({"cmd":"replica.status"})).unwrap()["identity_ready"],
        true
    );
}

#[test]
fn binary_gpt_identity_is_atomic_clean_and_durable_beyond_json_request_limit() {
    let f = lazy_tests::fixture(None);
    let path = f._dir.path().join("binary-gpt.odv4");
    let disk = begin(&f, "binary-gpt.odv4", None);
    let capacity = disk.capacity();
    let primary = vec![255; 17408];
    let backup = vec![254; 16896];
    let offset = capacity - backup.len() as u64;
    let legacy_request = json!({"cmd":"replica.identity","regions":[
        {"offset":offset,"bytes":backup},{"offset":0,"bytes":primary}]})
    .to_string();
    assert!(legacy_request.len() > 65536);
    let frame = identity_frame(&[(offset, &backup), (0, &primary)]);
    assert_eq!(frame.len(), 34332);
    disk.replica_identity(&frame).unwrap();
    let status = disk.control(&json!({"cmd":"replica.status"})).unwrap();
    assert_eq!(status["identity_ready"], true);
    assert_eq!(status["local_changes"], false);
    // Durable metadata carries both regions in one transaction. The runtime
    // view is rebuilt identically after close, without uploading guest pages.
    let identity = disk.shared.identity.lock().unwrap().clone();
    assert_eq!(identity.len(), 2);
    assert_eq!(identity[0].offset, offset);
    assert_eq!(identity[0].bytes, backup);
    assert_eq!(identity[1].bytes, primary);
    drop(disk);
    let reopened = Volume::open(path, None).unwrap();
    let identity = reopened.shared.identity.lock().unwrap();
    assert_eq!(identity.len(), 2);
    assert_eq!(identity[0].bytes, backup);
    assert_eq!(identity[1].bytes, primary);
    drop(identity);
    assert_eq!(
        reopened.control(&json!({"cmd":"replica.status"})).unwrap()["local_changes"],
        false
    );
}

#[test]
fn malformed_binary_identity_never_publishes_partial_regions() {
    let f = lazy_tests::fixture(None);
    let disk = begin(&f, "binary-invalid.odv4", None);
    let before = disk.control(&json!({"cmd":"replica.status"})).unwrap();
    let valid = identity_frame(&[(440, &[1, 2, 3, 4])]);
    let mut trailing = valid.clone();
    trailing.push(0);
    let first = vec![1; 32768];
    let second = vec![2; 32769];
    let bad = vec![
        vec![],
        5u32.to_le_bytes().to_vec(),
        1u32.to_le_bytes().to_vec(),
        identity_frame(&[(0, &[])]),
        valid[..valid.len() - 1].to_vec(),
        trailing,
        identity_frame(&[(440, &[1]), (65536, &[2])]),
        identity_frame(&[(u64::MAX - 1, &[1, 2, 3, 4])]),
        identity_frame(&[(disk.capacity() - 1, &[1, 2])]),
        identity_frame(&[
            (0, &first),
            (disk.capacity() - second.len() as u64, &second),
        ]),
        vec![0; Volume::MAX_REPLICA_IDENTITY_FRAME + 1],
    ];
    for frame in bad {
        assert!(disk.replica_identity(&frame).is_err());
        assert_eq!(
            disk.control(&json!({"cmd":"replica.status"})).unwrap(),
            before
        );
        assert!(disk.shared.identity.lock().unwrap().is_empty());
    }
    // The exact allowed payload boundary remains usable after all rejections.
    let part = vec![3; 16384];
    disk.replica_identity(&identity_frame(&[
        (0, &part),
        (16384, &part),
        (disk.capacity() - 32768, &part),
        (disk.capacity() - 16384, &part),
    ]))
    .unwrap();
    assert_eq!(disk.shared.identity.lock().unwrap().len(), 4);
    assert_eq!(
        disk.control(&json!({"cmd":"replica.status"})).unwrap()["local_changes"],
        false
    );
    assert!(disk.info().unwrap().background_error.is_none());
}

fn publish(v: &Volume, objects: &mut HashMap<String, Vec<u8>>) -> Value {
    let mut status = v.control(&json!({"cmd":"cloud.prepare"})).unwrap();
    for _ in 0..128 {
        if status["job"]["phase"] == "ready" {
            break;
        }
        status=v.control(&json!({"cmd":"cloud.prepare","job_id":status["job"]["id"],"max_pages":1024,"max_objects":64})).unwrap();
    }
    let job = status["job"].clone();
    assert_eq!(job["phase"], "ready");
    let mut cursor = 0;
    loop {
        let list = v
            .control(&json!({"cmd":"cloud.list","job_id":job["id"],"cursor":cursor,"limit":128}))
            .unwrap();
        for item in list["items"].as_array().unwrap() {
            let id = item["id"].as_str().unwrap();
            let mut bytes = vec![0; v.object_size() as usize];
            v.read_export(job["id"].as_str().unwrap(), id, 0, &mut bytes)
                .unwrap();
            objects.insert(id.to_owned(), bytes);
            v.control(&json!({"cmd":"cloud.receipt","job_id":job["id"],"object_id":id,"sha256":item["sha256"],"length":v.object_size(),"receipt":"mock"})).unwrap();
        }
        if let Some(n) = list["next_cursor"].as_u64() {
            cursor = n;
        } else {
            break;
        }
    }
    v.control(&json!({"cmd":"cloud.commit","job_id":job["id"],"root_object_id":job["root_object_id"],"root_sha256":job["root_sha256"],"receipt":"mock"})).unwrap();
    job
}
#[test]
fn newer_source_and_local_writes_use_disjoint_ordinals_and_keep_manual_snapshot() {
    let f = lazy_tests::fixture(None);
    let disk = begin(&f, "new-generation.odv4", None);
    let mut objects = (*f.objects).clone();
    publish(&f.source, &mut objects);
    disk.set_object_provider(Some(lazy_tests::provider(
        Arc::new(objects.clone()),
        Arc::new(AtomicUsize::new(0)),
    )))
    .unwrap();
    let mut bytes = [0; PAGE];
    disk.read(PAGE as u64, &mut bytes).unwrap();
    assert_eq!(bytes, [22; PAGE]);
    disk.write(0, &[77; PAGE]).unwrap();
    disk.flush().unwrap();
    let snapshot = disk.snapshot_create().unwrap();
    f.source.write(0, &[99; PAGE]).unwrap();
    f.source.flush().unwrap();
    let job = publish(&f.source, &mut objects);
    let root = objects[job["root_object_id"].as_str().unwrap()].clone();
    let mut opt = options_from_job(&f, &job);
    opt["backing"]["reader_pin"] = json!("new-reader-pin");
    let calls = Arc::new(AtomicUsize::new(0));
    disk.set_object_provider(Some(lazy_tests::provider(Arc::new(objects), calls.clone())))
        .unwrap();
    disk.replica_stage(&root, &opt).unwrap();
    assert!(apply(&disk, false).is_err());
    let before = calls.load(Ordering::SeqCst);
    apply(&disk, true).unwrap();
    disk.read(PAGE as u64, &mut bytes).unwrap();
    assert_eq!(bytes, [22; PAGE]);
    assert_eq!(
        calls.load(Ordering::SeqCst),
        before,
        "unchanged cached data must be reused"
    );
    disk.read(0, &mut bytes).unwrap();
    assert_eq!(bytes, [99; PAGE]);
    let index = disk
        .shared
        .store
        .lock()
        .unwrap()
        .snapshots
        .iter()
        .find(|s| s.id == snapshot)
        .unwrap()
        .index;
    {
        let (_lease, mut reader) = disk.shared.read_snapshot(index).unwrap();
        assert_eq!(reader.read_page(0).unwrap().as_ref(), &[77; PAGE]);
    }
    assert_eq!(
        disk.control(&json!({"cmd":"replica.status"})).unwrap()["local_changes"],
        false
    );
    drop(disk);
    let disk = Volume::open(f._dir.path().join("new-generation.odv4"), None).unwrap();
    disk.read(0, &mut bytes).unwrap();
    assert_eq!(bytes, [99; PAGE]);
    let index = disk
        .shared
        .store
        .lock()
        .unwrap()
        .snapshots
        .iter()
        .find(|s| s.id == snapshot)
        .unwrap()
        .index;
    {
        let (_lease, mut reader) = disk.shared.read_snapshot(index).unwrap();
        assert_eq!(reader.read_page(0).unwrap().as_ref(), &[77; PAGE]);
    }
}
fn options_from_job(f: &lazy_tests::Fixture, job: &Value) -> Value {
    let mut commit = json!({"formatVersion":4,"volumeId":f.source.info().unwrap().id,"writerId":"12345678-1111-1111-1111-111111111111","name":"fixture","capacityBytes":f.source.capacity(),"encrypted":f.source.info().unwrap().encrypted,"objectSizeBytes":f.source.object_size(),"transportCodec":"zstd-v1"});
    let mut backing = f.backing.clone();
    commit["generation"] = job["generation"].clone();
    commit["rootObjectId"] = job["root_object_id"].clone();
    commit["rootSha256"] = job["root_sha256"].clone();
    backing["root_object_id"] = job["root_object_id"].clone();
    backing["root_sha256"] = job["root_sha256"].clone();
    json!({"backing":backing,"commit":commit})
}
#[test]
fn identity_page_writes_materialize_overlay_and_survive_flush_reopen() {
    let f = lazy_tests::fixture(None);
    let disk = begin(&f, "identity-write.odv4", None);
    disk.set_object_provider(Some(lazy_tests::provider(
        f.objects.clone(),
        Arc::new(AtomicUsize::new(0)),
    )))
    .unwrap();
    disk.replica_identity(&identity_frame(&[(440, &[1, 2, 3, 4])]))
        .unwrap();
    disk.write(512, &[77; 512]).unwrap();
    let mut b = [0; PAGE];
    disk.read(0, &mut b).unwrap();
    assert_eq!(&b[440..444], &[1, 2, 3, 4]);
    assert_eq!(&b[512..1024], &[77; 512]);
    let mut mbr = [88; 512];
    mbr[440..444].copy_from_slice(&[5, 6, 7, 8]);
    disk.write(0, &mbr).unwrap();
    disk.flush().unwrap();
    disk.read_persistent(0, &mut b).unwrap();
    assert_eq!(&b[..512], &mbr);
    assert_eq!(&b[512..1024], &[77; 512]);
    drop(disk);
    let disk = Volume::open(f._dir.path().join("identity-write.odv4"), None).unwrap();
    disk.read(0, &mut b).unwrap();
    assert_eq!(&b[..512], &mbr);
}
#[test]
fn writing_raw_source_over_identity_is_not_wrongly_deduplicated() {
    let f = lazy_tests::fixture(None);
    let disk = begin(&f, "identity-raw.odv4", None);
    disk.set_object_provider(Some(lazy_tests::provider(
        f.objects.clone(),
        Arc::new(AtomicUsize::new(0)),
    )))
    .unwrap();
    disk.replica_identity(&identity_frame(&[(440, &[1, 2, 3, 4])]))
        .unwrap();
    disk.write(0, &[11; PAGE]).unwrap();
    disk.flush().unwrap();
    let mut b = [0; PAGE];
    disk.read(0, &mut b).unwrap();
    assert_eq!(b, [11; PAGE]);
}
#[test]
fn cached_candidate_retries_without_root_redownload() {
    let f = lazy_tests::fixture(None);
    let disk = begin(&f, "cached.odv4", None);
    let s = disk
        .control(&json!({"cmd":"replica.stage_cached","options":options(&f)}))
        .unwrap();
    assert_eq!(s["cached"], true);
    assert_eq!(s["candidate"]["ready"], true);
    disk.control(&json!({"cmd":"replica.cancel"})).unwrap();
    let mut opt = options(&f);
    opt["commit"]["rootObjectId"] = json!(Uuid::new_v4().to_string());
    assert_eq!(
        disk.control(&json!({"cmd":"replica.stage_cached","options":opt}))
            .unwrap()["cached"],
        false
    );
    assert!(disk.info().unwrap().background_error.is_none());
}
#[test]
fn blocked_download_does_not_hold_foreground_status_or_snapshot_lock() {
    use std::sync::mpsc;
    let f = lazy_tests::fixture(None);
    let disk = Arc::new(begin(&f, "concurrent.odv4", None));
    disk.set_object_provider(Some(lazy_tests::provider(
        f.objects.clone(),
        Arc::new(AtomicUsize::new(0)),
    )))
    .unwrap();
    let mut cached = [0; PAGE];
    disk.read(0, &mut cached).unwrap();
    let (entered_tx, entered_rx) = mpsc::channel();
    let (release_tx, release_rx) = mpsc::channel();
    let release = Mutex::new(release_rx);
    let objects = f.objects.clone();
    disk.set_object_provider(Some(Arc::new(move |r, out| {
        entered_tx.send(()).unwrap();
        release.lock().unwrap().recv().unwrap();
        out.copy_from_slice(&objects[&r.id]);
        Ok(())
    })))
    .unwrap();
    let reader = disk.clone();
    let reading = std::thread::spawn(move || {
        let mut data = [0; PAGE];
        reader.read(PAGE as u64, &mut data).unwrap();
        data
    });
    entered_rx.recv_timeout(Duration::from_secs(10)).unwrap();
    let check = disk.clone();
    let (done_tx, done_rx) = mpsc::channel();
    let checking = std::thread::spawn(move || {
        check.info().unwrap();
        check.control(&json!({"cmd":"blocks.summary"})).unwrap();
        let snap = check.snapshot_create().unwrap();
        let mut data = [0; PAGE];
        check.read(0, &mut data).unwrap();
        assert_eq!(data, [11; PAGE]);
        check.write(0, &[88; PAGE]).unwrap();
        check.flush().unwrap();
        check.snapshot_release(&snap).unwrap();
        done_tx.send(()).unwrap();
    });
    let completed = done_rx.recv_timeout(Duration::from_secs(10));
    release_tx.send(()).unwrap();
    assert_eq!(reading.join().unwrap(), [22; PAGE]);
    checking.join().unwrap();
    completed.expect("foreground operations must complete while network is stopped");
}
#[test]
fn replacement_write_faults_keep_whole_old_or_new_root_and_manual_snapshot() {
    let f = lazy_tests::fixture(None);
    let base = f._dir.path().join("fault-base.odv4");
    let disk = begin(&f, "fault-base.odv4", None);
    disk.set_object_provider(Some(lazy_tests::provider(
        f.objects.clone(),
        Arc::new(AtomicUsize::new(0)),
    )))
    .unwrap();
    let mut data = [0; PAGE];
    disk.read(0, &mut data).unwrap();
    disk.write(0, &[77; PAGE]).unwrap();
    disk.flush().unwrap();
    let snapshot = disk.snapshot_create().unwrap();
    disk.replica_stage(&f.root, &options(&f)).unwrap();
    drop(disk);
    let probe = f._dir.path().join("fault-probe.odv4");
    std::fs::copy(&base, &probe).unwrap();
    let disk = Volume::open(&probe, None).unwrap();
    disk.shared.device.events.lock().unwrap().clear();
    apply(&disk, true).unwrap();
    let cuts = disk
        .shared
        .device
        .events
        .lock()
        .unwrap()
        .iter()
        .filter(|(write, _, _)| *write)
        .count() as i64
        + 3;
    drop(disk);
    let mut successes = 0;
    for cut in 0..cuts {
        let path = f._dir.path().join(format!("replace-fault-{cut}.odv4"));
        std::fs::copy(&base, &path).unwrap();
        let disk = Volume::open(&path, None).unwrap();
        disk.shared.device.fail_after.store(cut, Ordering::Relaxed);
        if apply(&disk, true).is_ok() {
            successes += 1;
        }
        drop(disk);
        let disk = Volume::open(&path, None).unwrap();
        disk.read(0, &mut data).unwrap();
        assert!(data == [77; PAGE] || data == [11; PAGE]);
        let status = disk.control(&json!({"cmd":"replica.status"})).unwrap();
        assert_eq!(status["local_changes"], data == [77; PAGE]);
        let index = disk
            .shared
            .store
            .lock()
            .unwrap()
            .snapshots
            .iter()
            .find(|s| s.id == snapshot)
            .unwrap()
            .index;
        {
            let (_lease, mut reader) = disk.shared.read_snapshot(index).unwrap();
            assert_eq!(reader.read_page(0).unwrap().as_ref(), &[77; PAGE]);
        }
        drop(disk);
        std::fs::remove_file(path).unwrap();
    }
    assert!(successes > 0);
}
#[test]
fn optional_upload_after_pulling_an_updated_source_exports_the_new_base() {
    let f = lazy_tests::fixture(None);
    let disk = begin(&f, "upload-copy.odv4", None);
    let mut objects = (*f.objects).clone();
    publish(&f.source, &mut objects);
    disk.set_object_provider(Some(lazy_tests::provider(
        Arc::new(objects.clone()),
        Arc::new(AtomicUsize::new(0)),
    )))
    .unwrap();
    let mut copy_objects = HashMap::new();
    publish(&disk, &mut copy_objects);
    f.source.write(0, &[91; PAGE]).unwrap();
    f.source.flush().unwrap();
    let next = publish(&f.source, &mut objects);
    let raw = objects[next["root_object_id"].as_str().unwrap()].clone();
    disk.set_object_provider(Some(lazy_tests::provider(
        Arc::new(objects),
        Arc::new(AtomicUsize::new(0)),
    )))
    .unwrap();
    disk.replica_stage(&raw, &options_from_job(&f, &next))
        .unwrap();
    apply(&disk, true).unwrap();
    let new_copy = publish(&disk, &mut copy_objects);
    let child = Volume::restore_begin_v4(
        f._dir.path().join("copy-of-copy.odv4"),
        &copy_objects[new_copy["root_object_id"].as_str().unwrap()],
        None,
    )
    .unwrap();
    for _ in 0..64 {
        let status = child.control(&json!({"cmd":"restore.status"})).unwrap();
        if status["phase"] == "ready" {
            break;
        }
        for n in status["needed"].as_array().unwrap() {
            let id = n["id"].as_str().unwrap();
            child.restore_accept(id, &copy_objects[id]).unwrap();
        }
        child.control(&json!({"cmd":"restore.step"})).unwrap();
    }
    child.control(&json!({"cmd":"restore.finish"})).unwrap();
    let mut data = [0; PAGE];
    child.read(0, &mut data).unwrap();
    assert_eq!(data, [91; PAGE]);
    child.read(PAGE as u64, &mut data).unwrap();
    assert_eq!(data, [22; PAGE]);
    child.write(2 * PAGE as u64, &[42; PAGE]).unwrap();
    child.flush().unwrap();
    child.read(2 * PAGE as u64, &mut data).unwrap();
    assert_eq!(data, [42; PAGE]);
}
#[test]
fn a_wide_index_stays_lazy_and_downloads_only_the_accessed_path() {
    let dir = tempfile::tempdir().unwrap();
    let source_path = dir.path().join("wide-source.odv4");
    Volume::create(&source_path, 256 << 20, None).unwrap();
    let source = Volume::open(&source_path, None).unwrap();
    for i in 0..1050u64 {
        source
            .write(i * 32 * PAGE as u64, &[(i % 251 + 1) as u8; PAGE])
            .unwrap();
    }
    source.flush().unwrap();
    let mut objects = HashMap::new();
    let job = publish(&source, &mut objects);
    assert!(objects.len() > 3);
    let backing = json!({"provider_id":"mock","account_id":"account","remote_root":"/OverlayDisk/source","reader_pin":"pin","source_volume_id":source.info().unwrap().id,"root_object_id":job["root_object_id"],"root_sha256":job["root_sha256"]});
    let target = Volume::lazy_begin(
        dir.path().join("wide-target.odv4"),
        &objects[job["root_object_id"].as_str().unwrap()],
        None,
        backing,
    )
    .unwrap();
    target.control(&json!({"cmd":"replica.bootstrap"})).unwrap();
    let mut initial = 1;
    for _ in 0..8 {
        let status = target.control(&json!({"cmd":"restore.status"})).unwrap();
        if status["phase"] == "ready" {
            break;
        }
        for need in status["needed"].as_array().unwrap() {
            let id = need["id"].as_str().unwrap();
            target.restore_accept(id, &objects[id]).unwrap();
            initial += 1;
        }
        target.control(&json!({"cmd":"restore.step"})).unwrap();
    }
    let status = target.control(&json!({"cmd":"restore.finish"})).unwrap();
    assert_eq!(status["completed_pages"], 0);
    assert!(initial <= 2);
    assert_eq!(target.info().unwrap().allocated_pages, 1050);
    let total = objects.len();
    let calls = Arc::new(AtomicUsize::new(0));
    target
        .set_object_provider(Some(lazy_tests::provider(Arc::new(objects), calls.clone())))
        .unwrap();
    let mut data = [0; PAGE];
    target.read(0, &mut data).unwrap();
    assert_eq!(data, [1; PAGE]);
    assert!(calls.load(Ordering::SeqCst) > 0);
    assert!(
        calls.load(Ordering::SeqCst) + initial < total,
        "reading one page must not download the entire index or data"
    );
    let n = calls.load(Ordering::SeqCst);
    target.read(0, &mut data).unwrap();
    assert_eq!(calls.load(Ordering::SeqCst), n);
    assert_eq!(
        target.control(&json!({"cmd":"replica.status"})).unwrap()["local_changes"],
        false
    );
}
#[test]
fn divergent_original_writer_rejects_collision_without_poisoning_local_data() {
    let f = lazy_tests::fixture(None);
    let opt = base_tests::options(&f, "original", true);
    let disk = Volume::restore_begin_options(
        f._dir.path().join("original-diverged.odv4"),
        &f.root,
        None,
        opt.clone(),
    )
    .unwrap();
    disk.control(&json!({"cmd":"replica.bootstrap","commit":opt["publication"]["commit"]}))
        .unwrap();
    for _ in 0..8 {
        let status = disk.control(&json!({"cmd":"restore.status"})).unwrap();
        if status["phase"] == "ready" {
            break;
        }
        for need in status["needed"].as_array().unwrap() {
            let id = need["id"].as_str().unwrap();
            disk.restore_accept(id, &f.objects[id]).unwrap();
        }
        disk.control(&json!({"cmd":"restore.step"})).unwrap();
    }
    disk.control(&json!({"cmd":"restore.finish"})).unwrap();
    disk.write(0, &[77; PAGE]).unwrap();
    disk.flush().unwrap();
    let mut objects = (*f.objects).clone();
    publish(&f.source, &mut objects);
    f.source.write(0, &[99; PAGE]).unwrap();
    f.source.flush().unwrap();
    let job = publish(&f.source, &mut objects);
    let mut stage = options_from_job(&f, &job);
    stage["backing"]["remote_root"] = opt["backing"]["remote_root"].clone();
    stage["commit"]["writerId"] = opt["publication"]["commit"]["writerId"].clone();
    assert!(disk
        .replica_stage(&objects[job["root_object_id"].as_str().unwrap()], &stage)
        .is_err());
    assert!(disk.info().unwrap().background_error.is_none());
    let mut data = [0; PAGE];
    disk.read(0, &mut data).unwrap();
    assert_eq!(data, [77; PAGE]);
}
#[test]
fn deferred_sources_keep_reader_pins_and_gc_protects_unseen_objects() {
    let f = lazy_tests::fixture(None);
    let options = base_tests::options(&f, "original", true);
    let disk = Volume::restore_begin_options(
        f._dir.path().join("protected-source.odv4"),
        &f.root,
        None,
        options.clone(),
    )
    .unwrap();
    disk.control(&json!({"cmd":"replica.bootstrap","commit":options["publication"]["commit"]}))
        .unwrap();
    for _ in 0..8 {
        let status = disk.control(&json!({"cmd":"restore.status"})).unwrap();
        if status["phase"] == "ready" {
            break;
        }
        for need in status["needed"].as_array().unwrap() {
            let id = need["id"].as_str().unwrap();
            disk.restore_accept(id, &f.objects[id]).unwrap();
        }
        disk.control(&json!({"cmd":"restore.step"})).unwrap();
    }
    disk.control(&json!({"cmd":"restore.finish"})).unwrap();
    let mut backing = options["publication"]["binding"].clone();
    backing["volume_id"] = json!(disk.info().unwrap().id);
    backing["reader_pin"] = json!(format!(
        "{}/readers/{}.json",
        backing["remote_root"].as_str().unwrap(),
        disk.info().unwrap().id
    ));
    let bound = disk
        .control(&json!({"cmd":"cache.bind","backing":backing}))
        .unwrap();
    assert!(bound["replaces_origin_pin"].is_null());
    assert_eq!(bound["origin_pin_required"], true);
    let mut id = [9u8; 16];
    id[8..].copy_from_slice(&12345u64.to_le_bytes());
    let unknown = Uuid::from_bytes(id).to_string();
    let result = disk
        .control(&json!({"cmd":"cloud.gc_candidates","object_ids":[unknown]}))
        .unwrap();
    assert!(result["allowed"].as_array().unwrap().is_empty());
    assert_eq!(result["protected"][0], unknown);
    assert_eq!(
        disk.control(&json!({"cmd":"replica.status"})).unwrap()["local_changes"],
        false
    );
}
