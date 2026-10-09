//! Geometry and recovery checks. New isolated containers only, no network or timing benchmarks.
use super::*;
use serde_json::{json, Value};
use std::collections::HashMap;
use std::sync::atomic::AtomicUsize;

const CAPACITY: u64 = 96 << 20;
const START: u64 = 8 << 20;
fn data(length: usize) -> Vec<u8> {
    (0..length)
        .map(|n| ((n * 31 + n / PAGE * 13 + 19) % 251) as u8)
        .collect()
}
fn prepare(v: &Volume) -> Value {
    let mut job = v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].clone();
    for _ in 0..128 {
        if job["phase"] == "ready" {
            return job;
        }
        job = v
            .control(
                &json!({"cmd":"cloud.prepare","job_id":job["id"],"max_pages":4096,"max_objects":4}),
            )
            .unwrap()["job"]
            .clone();
    }
    panic!("geometry prepare did not finish: {job}")
}
fn publish(v: &Volume) -> (Value, HashMap<String, Vec<u8>>) {
    let size = v.object_size();
    let job = prepare(v);
    assert_eq!(job["object_size"], size);
    let items = v
        .control(&json!({"cmd":"cloud.list","job_id":job["id"],"limit":128}))
        .unwrap();
    assert!(items["next_cursor"].is_null());
    let mut objects = HashMap::new();
    for item in items["items"].as_array().unwrap() {
        assert_eq!(item["length"], size);
        let id = item["id"].as_str().unwrap();
        let mut raw = vec![0; size as usize];
        v.read_export(job["id"].as_str().unwrap(), id, 0, &mut raw)
            .unwrap();
        assert_eq!(codec::hex(&codec::hash(&raw)), item["sha256"]);
        assert_eq!(store::public_config(&raw).unwrap().object_size, size);
        assert!(v.control(&json!({"cmd":"cloud.receipt","job_id":job["id"],"object_id":id,"sha256":item["sha256"],"length":size/2,"receipt":"wrong-size"})).is_err());
        v.control(&json!({"cmd":"cloud.receipt","job_id":job["id"],"object_id":id,"sha256":item["sha256"],"length":size,"receipt":"fixture-ack"})).unwrap();
        objects.insert(id.to_owned(), raw);
    }
    v.control(&json!({"cmd":"cloud.commit","job_id":job["id"],"root_object_id":job["root_object_id"],"root_sha256":job["root_sha256"],"receipt":"fixture-published"})).unwrap();
    (job, objects)
}
fn bind(v: &Volume) -> Value {
    let id = v.info().unwrap().id;
    let binding = json!({"backend_id":"geometry-fixture","account_id":"fixture","remote_root":format!("/OverlayDisk/{id}"),"device_id":Uuid::new_v4(),"enabled":true});
    v.control(&json!({"cmd":"cloud.bind","binding":binding}))
        .unwrap();
    binding
}
fn restore(v: &Volume, objects: &HashMap<String, Vec<u8>>) {
    for _ in 0..128 {
        let status = v.control(&json!({"cmd":"restore.status"})).unwrap();
        assert_eq!(status["object_size"], v.object_size());
        if status["phase"] == "ready" {
            v.control(&json!({"cmd":"restore.finish"})).unwrap();
            return;
        }
        for need in status["needed"].as_array().unwrap() {
            assert_eq!(need["length"], v.object_size());
            let id = need["id"].as_str().unwrap();
            v.restore_accept(id, &objects[id]).unwrap();
        }
        v.control(&json!({"cmd":"restore.step","max_nodes":256,"max_pages":4096}))
            .unwrap();
    }
    panic!("geometry restore did not finish")
}
fn provider(
    objects: Arc<HashMap<String, Vec<u8>>>,
    size: u64,
    calls: Arc<AtomicUsize>,
) -> Arc<ObjectProvider> {
    Arc::new(move |request, out| {
        assert_eq!(request.length, size);
        assert_eq!(out.len(), size as usize);
        calls.fetch_add(1, Ordering::SeqCst);
        out.copy_from_slice(&objects[&request.id]);
        Ok(())
    })
}

#[test]
fn all_object_sizes_preserve_cross_object_writes_snapshots_and_reopen() {
    for size in [4 << 20, 8 << 20, 16 << 20] {
        {
            let password: Option<&str> = None;
            let directory = tempfile::tempdir().unwrap();
            let path = directory.path().join("source.odv4");
            Volume::create_sized(&path, CAPACITY, password, size).unwrap();
            let v = Volume::open(&path, password).unwrap();
            assert_eq!(v.info().unwrap().object_size, size);
            let geometry = Geometry::new(size).unwrap();
            let mut expected = data((geometry.slots as usize + 7) * PAGE);
            v.write(START, &expected).unwrap();
            v.flush().unwrap();
            let snapshot = v.snapshot_create().unwrap();
            let manifest = v.snapshot_manifest(&snapshot).unwrap();
            let first = &manifest["objects"][0];
            assert_eq!(first["length"], size);
            let mut raw = vec![0; size as usize];
            v.object_read(&snapshot, first["id"].as_str().unwrap(), 0, &mut raw)
                .unwrap();
            let snapshot_hash = codec::hash(&raw);
            let changed = geometry.slots as usize * PAGE - 512;
            expected[changed..changed + 1024].fill(87);
            v.write(START + changed as u64, &[87; 1024]).unwrap();
            v.write(CAPACITY - 512, &[29; 512]).unwrap();
            v.flush().unwrap();
            let mut actual = vec![0; expected.len()];
            v.read(START, &mut actual).unwrap();
            assert_eq!(actual, expected);
            v.object_read(&snapshot, first["id"].as_str().unwrap(), 0, &mut raw)
                .unwrap();
            assert_eq!(codec::hash(&raw), snapshot_hash);
            drop(v);
            let v = Volume::open(&path, password).unwrap();
            assert_eq!(v.object_size(), size);
            v.read(START, &mut actual).unwrap();
            assert_eq!(actual, expected);
            let mut last = [0; 512];
            v.read(CAPACITY - 512, &mut last).unwrap();
            assert_eq!(last, [29; 512]);
            v.object_read(&snapshot, first["id"].as_str().unwrap(), 0, &mut raw)
                .unwrap();
            assert_eq!(codec::hash(&raw), snapshot_hash);
        }
    }
}

#[test]
fn large_cloud_objects_roundtrip_full_and_lazy_original_restore() {
    for size in [4 << 20, 8 << 20, 16 << 20] {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("source.odv4");
        Volume::create_sized(&path, CAPACITY, None, size).unwrap();
        let v = Volume::open(&path, None).unwrap();
        let binding = bind(&v);
        let expected = data((Geometry::new(size).unwrap().slots as usize + 3) * PAGE);
        v.write(START, &expected).unwrap();
        v.flush().unwrap();
        let (job, objects) = publish(&v);
        let root_id = job["root_object_id"].as_str().unwrap();
        let raw = &objects[root_id];
        let full =
            Volume::restore_begin_v4(directory.path().join("full.odv4"), raw, None)
                .unwrap();
        restore(&full, &objects);
        assert_eq!(full.object_size(), size);
        let mut actual = vec![0; expected.len()];
        full.read(START, &mut actual).unwrap();
        assert_eq!(actual, expected);
        drop(full);
        let source = v.info().unwrap().id;
        let root = binding["remote_root"].as_str().unwrap();
        let task = Uuid::new_v4();
        let backing = json!({"provider_id":"geometry-fixture","account_id":"fixture","remote_root":root,"reader_pin":format!("{root}/readers/{task}.json"),"source_volume_id":source,"root_object_id":root_id,"root_sha256":job["root_sha256"]});
        let commit = json!({"formatVersion":4,"volumeId":source,"writerId":binding["device_id"],"generation":job["generation"],"name":"source","capacityBytes":CAPACITY,"objectSizeBytes":size,"encrypted":true,"rootObjectId":root_id,"rootSha256":job["root_sha256"],"rootSlot":0,"updatedUtc":"2026-10-05T00:00:00Z"});
        let options = json!({"mode":"original","lazy":true,"source_volume_id":source,"backing":backing,"publication":{"binding":binding,"commit":commit}});
        let target_path = directory.path().join("original.odv4");
        let lazy =
            Volume::restore_begin_options(&target_path, raw, None, options).unwrap();
        restore(&lazy, &objects);
        assert_eq!(lazy.info().unwrap().id, source);
        assert!(lazy.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
        assert!(
            lazy.control(&json!({"cmd":"lazy.status"})).unwrap()["missing_objects"]
                .as_u64()
                .unwrap()
                > 0
        );
        let calls = Arc::new(AtomicUsize::new(0));
        lazy.set_object_provider(Some(provider(Arc::new(objects), size, calls.clone())))
            .unwrap();
        lazy.read(START, &mut actual).unwrap();
        assert_eq!(actual, expected);
        assert!(calls.load(Ordering::SeqCst) > 0);
        assert!(lazy.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
        drop(lazy);
        let lazy = Volume::open(&target_path, None).unwrap();
        lazy.read(START, &mut actual).unwrap();
        assert_eq!(actual, expected);
    }
}

#[test]
fn large_object_compaction_and_cache_preserve_identity_and_sync_generation() {
    for size in [8 << 20, 16 << 20] {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("source.odv4");
        Volume::create_sized(&path, CAPACITY, None, size).unwrap();
        let v = Volume::open(&path, None).unwrap();
        v.write(START, &[12; PAGE]).unwrap();
        let temporary = v.snapshot_create().unwrap();
        v.snapshot_manifest(&temporary).unwrap();
        v.snapshot_release(&temporary).unwrap();
        let expected = data((Geometry::new(size).unwrap().slots as usize + 3) * PAGE);
        v.write(START, &expected).unwrap();
        v.flush().unwrap();
        let mut backing = bind(&v);
        let (_job, objects) = publish(&v);
        let generation = v.info().unwrap().data_generation;
        for mode in ["normal", "deep"] {
            v.control(&json!({"cmd":"compact.start","mode":mode}))
                .unwrap();
            let mut done = false;
            for _ in 0..256 {
                let st = v
                    .control(&json!({"cmd":"compact.step","max_objects":4}))
                    .unwrap();
                if st["state"] == "done" {
                    done = true;
                    break;
                }
            }
            assert!(done);
            assert_eq!(v.info().unwrap().data_generation, generation);
            assert!(v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
            for (id, raw) in &objects {
                let mut store = v.shared.store.lock().unwrap();
                let o = store.object_id(id).unwrap();
                // Physical padding may retain a retired object's bytes. Object
                // identity is defined over its authenticated canonical export.
                let actual = store.verify_object(&o).unwrap();
                assert_eq!(codec::hash(&actual), codec::hash(raw));
            }
        }
        let id = v.info().unwrap().id;
        backing["volume_id"] = json!(id);
        backing["reader_pin"] = json!(format!(
            "{}/readers/{id}.json",
            backing["remote_root"].as_str().unwrap()
        ));
        let calls = Arc::new(AtomicUsize::new(0));
        v.set_object_provider(Some(provider(Arc::new(objects), size, calls.clone())))
            .unwrap();
        v.control(&json!({"cmd":"cache.bind","backing":backing}))
            .unwrap();
        v.control(&json!({"cmd":"cache.configure","max_bytes":1,"policy":"lru"}))
            .unwrap();
        v.control(&json!({"cmd":"cache.online","available":true}))
            .unwrap();
        let mut missing = false;
        for _ in 0..64 {
            let status = v
                .control(&json!({"cmd":"cache.step","max_objects":4}))
                .unwrap();
            if status["missing_objects"].as_u64().unwrap() > 0 {
                missing = true;
                break;
            }
        }
        assert!(missing);
        let mut actual = vec![0; expected.len()];
        v.read(START, &mut actual).unwrap();
        assert_eq!(actual, expected);
        assert!(calls.load(Ordering::SeqCst) > 0);
        assert_eq!(v.info().unwrap().data_generation, generation);
        assert!(v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
    }
}

#[test]
fn geometry_configuration_requires_size_and_checksum_protects_it() {
    let (current, _) = Config::create(CAPACITY, None).unwrap();
    let json = serde_json::to_value(&current).unwrap();
    assert_eq!(json["object_size"], OBJECT);
    let mut missing = json.clone();
    missing.as_object_mut().unwrap().remove("object_size");
    assert!(serde_json::from_value::<Config>(missing).is_err());
    let decoded: Config = serde_json::from_value(json).unwrap();
    assert_eq!(decoded.object_size, OBJECT);
    decoded.unlock(None).unwrap();
    assert_eq!(&current.encode().unwrap()[..8], b"ODV4PLN1");
    for size in [8 << 20, 16 << 20] {
        let (c, _) = Config::create_sized(CAPACITY, None, size).unwrap();
        assert_eq!(&c.encode().unwrap()[..8], b"ODV4PLN1");
        let mut corrupt = c.encode().unwrap();
        corrupt[20] ^= 1;
        assert!(Config::decode(&corrupt).is_err());
    }
    for invalid in [0, 2 << 20, 12 << 20, 32 << 20] {
        assert!(Geometry::new(invalid).is_err());
    }
}

#[test]
fn control_extent_protection_covers_each_complete_geometry() {
    let directory = tempfile::tempdir().unwrap();
    for size in [4 << 20, 8 << 20, 16 << 20] {
        let path = directory.path().join(format!("control-{size}.odv4"));
        Volume::create_sized(&path, CAPACITY, None, size).unwrap();
        let v = Volume::open(&path, None).unwrap();
        assert!(v
            .shared
            .device
            .deallocate(size - PAGE as u64, PAGE as u64)
            .is_err());
        assert_eq!(v.info().unwrap().object_size, size);
    }
}
