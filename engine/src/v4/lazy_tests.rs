use super::*;
use serde_json::{json, Value};
use std::{collections::HashMap, sync::atomic::AtomicUsize};
use tempfile::TempDir;

pub(super) struct Fixture {
    pub _dir: TempDir,
    pub source: Volume,
    pub target: Volume,
    pub path: std::path::PathBuf,
    pub objects: Arc<HashMap<String, Vec<u8>>>,
    pub root: Vec<u8>,
    pub backing: Value,
}
fn prepare(v: &Volume) -> (String, Vec<Value>) {
    let mut j = v.control(&json!({"cmd":"cloud.prepare"})).unwrap();
    let id = j["job"]["id"].as_str().unwrap().to_owned();
    for _ in 0..128 {
        if j["job"]["phase"] == "ready" {
            break;
        }
        j = v
            .control(&json!({"cmd":"cloud.prepare","job_id":id,"max_pages":128,"max_objects":16}))
            .unwrap();
    }
    assert_eq!(j["job"]["phase"], "ready");
    let items = v
        .control(&json!({"cmd":"cloud.list","job_id":id,"limit":128}))
        .unwrap();
    (id, items["items"].as_array().unwrap().clone())
}
pub(super) fn fixture(password: Option<&str>) -> Fixture {
    let dir = TempDir::new().unwrap();
    let source_path = dir.path().join("source.odv4");
    Volume::create(&source_path, 64 << 20, password).unwrap();
    let source = Volume::open(source_path, password).unwrap();
    source.write(0, &[11; PAGE]).unwrap();
    source.flush().unwrap();
    let snap = source.snapshot_create().unwrap();
    source.snapshot_manifest(&snap).unwrap();
    source.snapshot_release(&snap).unwrap();
    source.write(PAGE as u64, &[22; PAGE]).unwrap();
    source.write(33 * PAGE as u64, &[33; PAGE]).unwrap();
    source.flush().unwrap();
    let (job, items) = prepare(&source);
    let mut objects = HashMap::new();
    for item in items {
        let id = item["id"].as_str().unwrap().to_owned();
        let mut raw = vec![0; OBJECT as usize];
        source.read_export(&job, &id, 0, &mut raw).unwrap();
        objects.insert(id, raw);
    }
    let status = source.control(&json!({"cmd":"cloud.status"})).unwrap();
    let root_id = status["job"]["root_object_id"].as_str().unwrap();
    let root = objects[root_id].clone();
    let backing = json!({"provider_id":"mock","account_id":"account","remote_root":"/OverlayDisk/source","reader_pin":"lease", "source_volume_id":source.info().unwrap().id,"root_object_id":root_id,"root_sha256":codec::hex(&codec::hash(&root))});
    let path = dir.path().join("lazy.odv4");
    let target = Volume::lazy_begin(&path, &root, password, backing.clone()).unwrap();
    for _ in 0..128 {
        let status = target.control(&json!({"cmd":"restore.status"})).unwrap();
        if status["phase"] == "ready" {
            break;
        }
        for need in status["needed"].as_array().unwrap() {
            assert_eq!(need["kind"], "metadata");
            let id = need["id"].as_str().unwrap();
            target.restore_accept(id, &objects[id]).unwrap();
        }
        target
            .control(&json!({"cmd":"restore.step","max_pages":128}))
            .unwrap();
    }
    assert_eq!(
        target.control(&json!({"cmd":"restore.status"})).unwrap()["phase"],
        "ready"
    );
    target.control(&json!({"cmd":"restore.finish"})).unwrap();
    let st = target.control(&json!({"cmd":"lazy.status"})).unwrap();
    assert_eq!(st["missing_objects"], 2);
    assert_eq!(st["cached_objects"], 0);
    Fixture {
        _dir: dir,
        source,
        target,
        path,
        objects: Arc::new(objects),
        root,
        backing,
    }
}
pub(super) fn provider(
    objects: Arc<HashMap<String, Vec<u8>>>,
    calls: Arc<AtomicUsize>,
) -> Arc<ObjectProvider> {
    Arc::new(move |r, out| {
        calls.fetch_add(1, Ordering::SeqCst);
        out.copy_from_slice(&objects[&r.id]);
        Ok(())
    })
}
#[test]
fn metadata_only_reopen_hydrate_and_retry_preserve_generation() {
    let f = fixture(None);
    let target = f.target;
    let before = target.info().unwrap();
    let mut out = [0; PAGE];
    assert!(matches!(target.read(0, &mut out), Err(Error::Missing(_))));
    let needs = target
        .control(&json!({"cmd":"lazy.needs","offset":0,"length":4096}))
        .unwrap();
    let id = needs["items"][0]["id"].as_str().unwrap();
    let mut bad = f.objects[id].clone();
    bad[100000] ^= 1;
    assert!(target.lazy_import(id, &bad).is_err());
    target
        .set_object_provider(Some(Arc::new(|_, _| {
            Err(Error::Invalid("temporary offline".into()))
        })))
        .unwrap();
    assert!(target.read(0, &mut out).is_err());
    assert!(target.info().unwrap().background_error.is_none());
    let calls = Arc::new(AtomicUsize::new(0));
    target
        .set_object_provider(Some(provider(f.objects.clone(), calls.clone())))
        .unwrap();
    target.read(0, &mut out).unwrap();
    assert_eq!(out, [11; PAGE]);
    assert_eq!(calls.load(Ordering::SeqCst), 1);
    assert_eq!(
        target.info().unwrap().data_generation,
        before.data_generation
    );
    assert_eq!(target.info().unwrap().dirty_bytes, before.dirty_bytes);
    assert_eq!(
        target.control(&json!({"cmd":"lazy.status"})).unwrap()["missing_objects"],
        1
    );
    drop(target);
    let target = Volume::open(&f.path, None).unwrap();
    target.read(0, &mut out).unwrap();
    assert_eq!(out, [11; PAGE]);
    assert!(target.read(PAGE as u64, &mut out).is_err());
    assert_eq!(
        target.control(&json!({"cmd":"lazy.status"})).unwrap()["cached_objects"],
        1
    );
    assert_ne!(target.info().unwrap().id, f.source.info().unwrap().id);
    // Windows mandatory byte-range locks require the owning device handle here.
    let mut config_page = [0; PAGE];
    target.shared.device.read(0, &mut config_page).unwrap();
    assert_eq!(&config_page[..8], b"ODV4PLN1");
    let mut bad_backing = f.backing;
    bad_backing["root_sha256"] = json!("00".repeat(32));
    assert!(Volume::lazy_begin(
        f._dir.path().join("bad.odv4"),
        &f.root,
        None,
        bad_backing
    )
    .is_err());
}
#[test]
fn full_overwrite_snapshot_partial_write_and_lazy_export() {
    let f = fixture(None);
    let v = &f.target;
    let calls = Arc::new(AtomicUsize::new(0));
    v.set_object_provider(Some(provider(f.objects.clone(), calls.clone())))
        .unwrap();
    let snap = v.snapshot_create().unwrap();
    v.write(0, &[77; PAGE]).unwrap();
    v.flush().unwrap();
    assert_eq!(calls.load(Ordering::SeqCst), 0);
    let mut out = [0; PAGE];
    v.read(0, &mut out).unwrap();
    assert_eq!(out, [77; PAGE]);
    let old = v.snapshot_manifest(&snap).unwrap();
    let id = old["objects"]
        .as_array()
        .unwrap()
        .iter()
        .find(|o| f.objects.contains_key(o["id"].as_str().unwrap()))
        .unwrap()["id"]
        .as_str()
        .unwrap();
    let mut raw = vec![0; OBJECT as usize];
    v.object_read(&snap, id, 0, &mut raw).unwrap();
    assert_eq!(raw, f.objects[id]);
    assert_eq!(calls.load(Ordering::SeqCst), 1);
    v.write(PAGE as u64 + 512, &[99; 512]).unwrap();
    v.flush().unwrap();
    v.read(PAGE as u64, &mut out).unwrap();
    assert_eq!(&out[..512], &[22; 512]);
    assert_eq!(&out[512..1024], &[99; 512]);
    assert_eq!(&out[1024..], &[22; 3072]);
    assert_eq!(calls.load(Ordering::SeqCst), 2);
    let (job, items) = prepare(v);
    for item in items {
        v.read_export(&job, item["id"].as_str().unwrap(), 0, &mut raw)
            .unwrap();
        assert_eq!(codec::hex(&codec::hash(&raw)), item["sha256"]);
    }
    assert_eq!(calls.load(Ordering::SeqCst), 2);
}
#[test]
fn provider_wait_does_not_lock_frontend_and_unregister_drains() {
    let f = fixture(None);
    let v = Arc::new(f.target);
    let (entered_rx, wait_rx) = {
        let (entered_tx, entered_rx) = std::sync::mpsc::channel();
        let (wait_tx, wait_rx) = std::sync::mpsc::channel();
        let wait = Arc::new(Mutex::new(wait_rx));
        let objects = f.objects.clone();
        v.set_object_provider(Some(Arc::new(move |r, b| {
            entered_tx.send(()).unwrap();
            wait.lock().unwrap().recv().unwrap();
            b.copy_from_slice(&objects[&r.id]);
            Ok(())
        })))
        .unwrap();
        (entered_rx, wait_tx)
    };
    let reader = v.clone();
    let read = thread::spawn(move || {
        let mut out = [0; PAGE];
        reader.read(0, &mut out).unwrap();
        assert_eq!(out, [11; PAGE]);
    });
    entered_rx.recv_timeout(Duration::from_secs(5)).unwrap();
    v.write(8 * PAGE as u64, &[44; PAGE]).unwrap();
    let mut local = [0; PAGE];
    v.read(8 * PAGE as u64, &mut local).unwrap();
    assert_eq!(local, [44; PAGE]);
    v.info().unwrap();
    v.control(&json!({"cmd":"lazy.status"})).unwrap();
    let other = v.clone();
    let (done_tx, done_rx) = std::sync::mpsc::channel();
    let unregister = thread::spawn(move || {
        other.set_object_provider(None).unwrap();
        done_tx.send(()).unwrap();
    });
    assert!(done_rx.recv_timeout(Duration::from_millis(30)).is_err());
    wait_rx.send(()).unwrap();
    read.join().unwrap();
    unregister.join().unwrap();
    done_rx.recv_timeout(Duration::from_secs(2)).unwrap();
}
#[test]
fn lazy_snapshot_publish_full_restore_and_compaction_are_complete() {
    let f = fixture(None);
    let v = &f.target;
    let calls = Arc::new(AtomicUsize::new(0));
    v.set_object_provider(Some(provider(f.objects.clone(), calls.clone())))
        .unwrap();
    let snapshot = v.snapshot_create().unwrap();
    let (job, items) = prepare(v);
    assert_eq!(
        calls.load(Ordering::SeqCst),
        0,
        "prepare must never fetch missing payload"
    );
    let mut uploaded = HashMap::new();
    for item in items {
        let id = item["id"].as_str().unwrap();
        let mut raw = vec![0; OBJECT as usize];
        v.read_export(&job, id, 0, &mut raw).unwrap();
        assert_eq!(codec::hex(&codec::hash(&raw)), item["sha256"]);
        v.control(&json!({"cmd":"cloud.receipt","job_id":job,"object_id":id,"sha256":item["sha256"],"length":OBJECT,"receipt":"mock"})).unwrap();
        uploaded.insert(id.to_owned(), raw);
    }
    assert_eq!(calls.load(Ordering::SeqCst), 2);
    let cloud = v.control(&json!({"cmd":"cloud.status"})).unwrap();
    let j = &cloud["job"];
    v.control(&json!({"cmd":"cloud.commit","job_id":job,"root_object_id":j["root_object_id"],"root_sha256":j["root_sha256"],"receipt":"mock"})).unwrap();
    let copy = Volume::restore_begin_v4(
        f._dir.path().join("full-copy.odv4"),
        &uploaded[j["root_object_id"].as_str().unwrap()],
        None,
    )
    .unwrap();
    for _ in 0..128 {
        let st = copy.control(&json!({"cmd":"restore.status"})).unwrap();
        if st["phase"] == "ready" {
            break;
        }
        for n in st["needed"].as_array().unwrap() {
            let id = n["id"].as_str().unwrap();
            copy.restore_accept(id, &uploaded[id]).unwrap();
        }
        copy.control(&json!({"cmd":"restore.step"})).unwrap();
    }
    copy.control(&json!({"cmd":"restore.finish"})).unwrap();
    assert_eq!(
        copy.control(&json!({"cmd":"lazy.status"})).unwrap()["enabled"],
        false
    );
    for (at, value) in [(0, 11), (PAGE as u64, 22), (33 * PAGE as u64, 33)] {
        let mut bytes = [0; PAGE];
        copy.read(at, &mut bytes).unwrap();
        assert_eq!(bytes, [value; PAGE]);
    }
    v.control(&json!({"cmd":"compact.start","mode":"normal"}))
        .unwrap();
    for _ in 0..128 {
        if v.control(&json!({"cmd":"compact.step","max_objects":8}))
            .unwrap()["state"]
            == "done"
        {
            break;
        }
    }
    v.snapshot_manifest(&snapshot).unwrap();
    assert_eq!(calls.load(Ordering::SeqCst), 2);
}
#[test]
fn lazy_local_snapshot_restore_materializes_but_physical_compaction_does_not_fetch() {
    let f = fixture(None);
    let v = &f.target;
    let calls = Arc::new(AtomicUsize::new(0));
    v.set_object_provider(Some(provider(f.objects.clone(), calls.clone())))
        .unwrap();
    let snapshot = v.snapshot_create().unwrap();
    let target = Volume::restore_snapshot_begin_v4(
        v,
        &snapshot,
        f._dir.path().join("local-copy.odv4"),
        None,
    )
    .unwrap();
    for _ in 0..128 {
        if target
            .control(&json!({"cmd":"snapshot.restore_step","max_pages":2}))
            .unwrap()["phase"]
            == "ready"
        {
            break;
        }
    }
    target.control(&json!({"cmd":"restore.finish"})).unwrap();
    assert_eq!(calls.load(Ordering::SeqCst), 2);
    assert_eq!(
        target.control(&json!({"cmd":"lazy.status"})).unwrap()["enabled"],
        false
    );
    for (at, value) in [(0, 11), (PAGE as u64, 22), (33 * PAGE as u64, 33)] {
        let mut bytes = [0; PAGE];
        target.read(at, &mut bytes).unwrap();
        assert_eq!(bytes, [value; PAGE]);
    }
    // A second untouched lazy target exercises normal/deep compaction with absent payloads.
    let other =
        Volume::lazy_begin(f._dir.path().join("deep.odv4"), &f.root, None, f.backing).unwrap();
    for _ in 0..128 {
        let st = other.control(&json!({"cmd":"restore.status"})).unwrap();
        if st["phase"] == "ready" {
            break;
        }
        for n in st["needed"].as_array().unwrap() {
            let id = n["id"].as_str().unwrap();
            other.restore_accept(id, &f.objects[id]).unwrap();
        }
        other.control(&json!({"cmd":"restore.step"})).unwrap();
    }
    other.control(&json!({"cmd":"restore.finish"})).unwrap();
    let compact_calls = Arc::new(AtomicUsize::new(0));
    other
        .set_object_provider(Some(provider(f.objects.clone(), compact_calls.clone())))
        .unwrap();
    other
        .control(&json!({"cmd":"compact.start","mode":"normal"}))
        .unwrap();
    for _ in 0..128 {
        if other
            .control(&json!({"cmd":"compact.step","max_objects":8}))
            .unwrap()["state"]
            == "done"
        {
            break;
        }
    }
    assert_eq!(compact_calls.load(Ordering::SeqCst), 0);
    let before_info = other.info().unwrap();
    let before_pending =
        other.control(&json!({"cmd":"debug.summary"})).unwrap()["pending_objects"].clone();
    other
        .control(&json!({"cmd":"compact.start","mode":"deep"}))
        .unwrap();
    let mut done = false;
    for _ in 0..128 {
        if other
            .control(&json!({"cmd":"compact.step","max_objects":8}))
            .unwrap()["state"]
            == "done"
        {
            done = true;
            break;
        }
    }
    assert!(done);
    assert_eq!(compact_calls.load(Ordering::SeqCst), 0);
    let after_info = other.info().unwrap();
    assert_eq!(after_info.data_generation, before_info.data_generation);
    assert_eq!(after_info.dirty_bytes, before_info.dirty_bytes);
    assert_eq!(
        other.control(&json!({"cmd":"debug.summary"})).unwrap()["pending_objects"],
        before_pending
    );
    let mut bytes = [0; PAGE];
    other.read(0, &mut bytes).unwrap();
    assert_eq!(bytes, [11; PAGE]);
}
#[test]
fn interrupted_hydration_reopens_as_missing_or_verified_resident() {
    let f = fixture(None);
    let needs = f
        .target
        .control(&json!({"cmd":"lazy.needs","offset":0,"length":4096}))
        .unwrap();
    let id = needs["items"][0]["id"].as_str().unwrap().to_owned();
    drop(f.target);
    for cutoff in [0, 1, 2, 5, 20, 1000] {
        let path = f._dir.path().join(format!("interrupted-{cutoff}.odv4"));
        std::fs::copy(&f.path, &path).unwrap();
        let v = Volume::open(&path, None).unwrap();
        let before = v.info().unwrap().data_generation;
        v.shared.device.fail_after.store(cutoff, Ordering::Relaxed);
        let _ = v.lazy_import(&id, &f.objects[&id]);
        drop(v);
        let reopened = Volume::open(&path, None).unwrap();
        assert_eq!(reopened.info().unwrap().data_generation, before);
        let calls = Arc::new(AtomicUsize::new(0));
        reopened
            .set_object_provider(Some(provider(f.objects.clone(), calls)))
            .unwrap();
        let mut b = [0; PAGE];
        reopened.read(0, &mut b).unwrap();
        assert_eq!(b, [11; PAGE]);
    }
}
#[test]
fn blocked_hydration_keeps_overlap_order_and_flush_prefix() {
    use crate::async_io::{Operation, Queue};
    let f = fixture(None);
    let v = Arc::new(f.target);
    let (entered_tx, entered_rx) = std::sync::mpsc::channel();
    let (go_tx, go_rx) = std::sync::mpsc::channel();
    let go = Mutex::new(go_rx);
    let objects = f.objects.clone();
    v.set_object_provider(Some(Arc::new(move |r, b| {
        entered_tx.send(()).unwrap();
        go.lock().unwrap().recv().unwrap();
        b.copy_from_slice(&objects[&r.id]);
        Ok(())
    })))
    .unwrap();
    let queue = Queue::new(Arc::new(crate::async_v4::CoreBackend { volume: v.clone() })).unwrap();
    queue
        .submit(
            1,
            Operation::Read {
                offset: 0,
                length: PAGE,
                fua: false,
            },
        )
        .unwrap();
    entered_rx.recv_timeout(Duration::from_secs(5)).unwrap();
    queue
        .submit(
            2,
            Operation::Read {
                offset: 8 * PAGE as u64,
                length: PAGE,
                fua: false,
            },
        )
        .unwrap();
    queue.submit_write(3, 512, &[91; 512], false).unwrap();
    queue.submit(4, Operation::Flush).unwrap();
    queue
        .submit(
            5,
            Operation::Read {
                offset: 0,
                length: PAGE,
                fua: true,
            },
        )
        .unwrap();
    let c = queue
        .next_completion(Duration::from_secs(5))
        .unwrap()
        .unwrap();
    assert_eq!(c.token, 2);
    assert_eq!(c.status, 0);
    queue.release_completion(2).unwrap();
    assert!(queue
        .next_completion(Duration::from_millis(30))
        .unwrap()
        .is_none());
    go_tx.send(()).unwrap();
    let mut order = Vec::new();
    for _ in 0..4 {
        let c = queue
            .next_completion(Duration::from_secs(10))
            .unwrap()
            .unwrap();
        assert_eq!(c.status, 0);
        if c.token == 1 {
            assert_eq!(&c.data.as_ref().unwrap()[..PAGE], &[11; PAGE]);
        }
        if c.token == 5 {
            assert_eq!(&c.data.as_ref().unwrap()[512..1024], &[91; 512]);
        }
        order.push(c.token);
        queue.release_completion(c.token).unwrap();
    }
    assert_eq!(order, vec![1, 3, 4, 5]);
    queue.shutdown();
    let mut bytes = [0; PAGE];
    v.read_persistent(0, &mut bytes).unwrap();
    assert_eq!(&bytes[512..1024], &[91; 512]);
}
#[test]
fn lazy_bootstrap_and_each_metadata_step_resume_without_payloads() {
    let f = fixture(None);
    let path = f._dir.path().join("resume-lazy.odv4");
    let mut cfg = store::public_config(&f.root).unwrap();
    let crypto = cfg.unlock(None).unwrap();
    cfg.lazy = true;
    let cfg = cfg.fork(&crypto, None).unwrap();
    let id = cfg.id;
    // The root/bootstrap file can survive an interrupted begin before its initial restore queue.
    drop(store::Store::create(Device::open(&path, true).unwrap(), cfg, crypto).unwrap());
    let mut target = Volume::lazy_begin(&path, &f.root, None, f.backing).unwrap();
    assert_eq!(target.info().unwrap().id, id);
    let mut done = false;
    for _ in 0..64 {
        let st = target.control(&json!({"cmd":"restore.status"})).unwrap();
        if st["phase"] == "ready" {
            done = true;
            break;
        }
        for n in st["needed"].as_array().unwrap() {
            assert_eq!(n["kind"], "metadata");
            let id = n["id"].as_str().unwrap();
            target.restore_accept(id, &f.objects[id]).unwrap();
        }
        target
            .control(&json!({"cmd":"restore.step","max_pages":1}))
            .unwrap();
        assert!(target.info().unwrap().restore_incomplete);
        drop(target);
        target = Volume::open(&path, None).unwrap();
    }
    assert!(done);
    assert!(target.read(0, &mut [0; 512]).is_err());
    target.control(&json!({"cmd":"restore.finish"})).unwrap();
    let st = target.control(&json!({"cmd":"lazy.status"})).unwrap();
    assert_eq!(st["missing_objects"], 2);
    assert_eq!(st["cached_objects"], 0);
    target
        .set_object_provider(Some(provider(f.objects, Arc::new(AtomicUsize::new(0)))))
        .unwrap();
    let mut out = [0; PAGE];
    target.read(PAGE as u64, &mut out).unwrap();
    assert_eq!(out, [22; PAGE]);
}
