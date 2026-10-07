//! Real-volume cache regressions. The simulated remote store contains authenticated
//! immutable exports; no test changes a user volume or makes a network request.
use super::*;
use crate::async_io::{Operation, Queue};
use crate::async_v4::CoreBackend;
use serde_json::{json, Value};
use std::collections::{BTreeSet, HashMap};
use std::sync::atomic::AtomicUsize;
use tempfile::TempDir;

#[derive(Clone, Default)]
struct Remote {
    objects: Arc<Mutex<HashMap<String, Vec<u8>>>>,
    calls: Arc<AtomicUsize>,
}
impl Remote {
    fn provider(&self) -> Arc<ObjectProvider> {
        let remote = self.clone();
        Arc::new(move |request, output| {
            remote.calls.fetch_add(1, Ordering::SeqCst);
            let objects = remote.objects.lock().unwrap();
            let bytes = objects.get(&request.id).ok_or_else(|| {
                Error::Invalid("fixture refused an object without an upload receipt".into())
            })?;
            assert_eq!(request.length, OBJECT);
            assert_eq!(request.sha256, codec::hex(&codec::hash(bytes)));
            assert_eq!(output.len(), OBJECT as usize);
            output.copy_from_slice(bytes);
            Ok(())
        })
    }
    fn bytes(&self, id: &str) -> Vec<u8> {
        self.objects.lock().unwrap()[id].clone()
    }
}

fn binding(v: &Volume) -> Value {
    let id = v.info().unwrap().id;
    json!({"backend_id":"cache-fixture","account_id":"owned-fixture",
        "remote_root":format!("/OverlayDisk/{id}"),"device_id":Uuid::new_v4(),"enabled":true})
}
fn bind_cloud(v: &Volume) {
    v.control(&json!({"cmd":"cloud.bind","binding":binding(v)}))
        .unwrap();
}
fn publish(v: &Volume, remote: &Remote) -> Vec<Value> {
    let mut job = v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].clone();
    assert!(job.is_object(), "fixture must contain a changed version");
    for _ in 0..128 {
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
    let listed = v
        .control(&json!({"cmd":"cloud.list","job_id":job["id"],"limit":128}))
        .unwrap();
    assert!(
        listed["next_cursor"].is_null(),
        "bounded fixture unexpectedly requires more objects"
    );
    let objects = listed["items"].as_array().unwrap().clone();
    for object in &objects {
        let id = object["id"].as_str().unwrap();
        let mut bytes = vec![0; OBJECT as usize];
        v.read_export(job["id"].as_str().unwrap(), id, 0, &mut bytes)
            .unwrap();
        assert_eq!(codec::hex(&codec::hash(&bytes)), object["sha256"]);
        remote.objects.lock().unwrap().insert(id.to_owned(), bytes);
        v.control(
            &json!({"cmd":"cloud.receipt","job_id":job["id"],"object_id":id,
            "sha256":object["sha256"],"length":OBJECT,"receipt":"fixture-upload-ack"}),
        )
        .unwrap();
    }
    v.control(
        &json!({"cmd":"cloud.commit","job_id":job["id"],"root_object_id":job["root_object_id"],
        "root_sha256":job["root_sha256"],"receipt":"fixture-publication-ack"}),
    )
    .unwrap();
    objects
}
fn cache_pin(v: &Volume) -> Value {
    let cloud = v.control(&json!({"cmd":"cloud.status"})).unwrap();
    let mut backing = cloud["binding"].clone();
    let id = v.info().unwrap().id;
    backing["volume_id"] = json!(id);
    backing["reader_pin"] = json!(format!(
        "{}/readers/{id}.json",
        backing["remote_root"].as_str().unwrap()
    ));
    backing
}
fn enable_cache(v: &Volume, remote: &Remote) {
    v.set_object_provider(Some(remote.provider())).unwrap();
    v.control(&json!({"cmd":"cache.bind","backing":cache_pin(v)}))
        .unwrap();
    v.control(&json!({"cmd":"cache.configure","max_bytes":1,"policy":"lru"}))
        .unwrap();
    v.control(&json!({"cmd":"cache.online","available":true}))
        .unwrap();
}
fn object(v: &Volume, id: &str) -> store::Object {
    v.shared.store.lock().unwrap().object_id(id).unwrap()
}
fn data_ids(items: &[Value]) -> Vec<String> {
    items
        .iter()
        .filter(|o| o["kind"] == "data")
        .map(|o| o["id"].as_str().unwrap().to_owned())
        .collect()
}
fn evict(v: &Volume, ids: &[String]) -> Value {
    for _ in 0..128 {
        let status = v
            .control(&json!({"cmd":"cache.step","max_objects":4}))
            .unwrap();
        if ids.iter().all(|id| object(v, id).missing) && status["pending_reclaims"] == 0 {
            return status;
        }
    }
    panic!(
        "bounded cache fixture did not evict expected uploaded objects: {}",
        v.control(&json!({"cmd":"cache.status"})).unwrap()
    );
}
fn logical_state(v: &Volume) -> (u64, u64, Value, Value) {
    let info = v.info().unwrap();
    (
        info.data_generation,
        info.dirty_bytes,
        v.control(&json!({"cmd":"cloud.status"})).unwrap()["changed_pages"].clone(),
        v.control(&json!({"cmd":"blocks.summary"})).unwrap()["pending_objects"].clone(),
    )
}

#[test]
fn encrypted_publication_cache_frees_physical_bytes_and_reopens_without_guest_changes() {
    let directory = TempDir::new().unwrap();
    let path = directory.path().join("published.odv4");
    Volume::create(&path, 64 << 20, Some("cache-fixture-password")).unwrap();
    let v = Volume::open(&path, Some("cache-fixture-password")).unwrap();
    let expected: Vec<u8> = (0..1200 * PAGE)
        .map(|i| ((i * 37 + i / PAGE) % 251) as u8)
        .collect();
    v.write(0, &expected).unwrap();
    v.flush().unwrap();
    bind_cloud(&v);
    let remote = Remote::default();
    let ids = data_ids(&publish(&v, &remote));
    assert!(ids.len() >= 2);
    v.set_object_provider(Some(remote.provider())).unwrap();
    v.control(&json!({"cmd":"cache.bind","backing":cache_pin(&v)}))
        .unwrap();
    v.control(&json!({"cmd":"cache.configure","max_bytes":1,"policy":"lru"}))
        .unwrap();
    let offline = v
        .control(&json!({"cmd":"cache.step","max_objects":4}))
        .unwrap();
    assert_eq!(offline["blocked_reason"], "offline");
    assert!(ids.iter().all(|id| !object(&v, id).missing));
    v.control(&json!({"cmd":"cache.online","available":true}))
        .unwrap();
    let before = logical_state(&v);
    let allocated = v.shared.device.allocated_bytes().unwrap();
    let evicted = evict(&v, &ids);
    assert!(evicted["evicted_bytes"].as_u64().unwrap() > 0);
    assert!(
        v.shared.device.allocated_bytes().unwrap() < allocated,
        "eviction must free disk allocation, not only report missing objects"
    );
    assert_eq!(logical_state(&v), before);
    assert_eq!(remote.calls.load(Ordering::SeqCst), 0);
    for id in &ids {
        let source = v
            .control(&json!({"cmd":"cache.source","object_id":id}))
            .unwrap();
        assert_eq!(source["source"], "own");
        assert_eq!(source["backing"]["volume_id"], json!(v.info().unwrap().id));
    }
    let mut actual = vec![0; expected.len()];
    v.read(0, &mut actual).unwrap();
    assert_eq!(codec::hash(&actual), codec::hash(&expected));
    assert_eq!(remote.calls.load(Ordering::SeqCst), ids.len());
    assert_eq!(logical_state(&v), before);
    assert!(v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].is_null());
    drop(v);
    let reopened = Volume::open(&path, Some("cache-fixture-password")).unwrap();
    assert_eq!(
        reopened.control(&json!({"cmd":"cache.status"})).unwrap()["online"],
        false
    );
    reopened.read(0, &mut actual).unwrap();
    assert_eq!(actual, expected);
    assert_eq!(logical_state(&reopened), before);
}

#[test]
fn unconfirmed_source_and_unpublished_dirty_objects_are_never_discarded() {
    let directory = TempDir::new().unwrap();
    let path = directory.path().join("dirty.odv4");
    Volume::create(&path, 64 << 20, None).unwrap();
    let v = Volume::open(&path, None).unwrap();
    v.write(0, &[17; PAGE]).unwrap();
    v.flush().unwrap();
    bind_cloud(&v);
    let remote = Remote::default();
    v.set_object_provider(Some(remote.provider())).unwrap();
    v.control(&json!({"cmd":"cache.configure","max_bytes":1,"policy":"lfu"}))
        .unwrap();
    v.control(&json!({"cmd":"cache.online","available":true}))
        .unwrap();
    assert!(v
        .control(&json!({"cmd":"cache.bind","backing":cache_pin(&v)}))
        .is_err());
    assert_eq!(
        v.control(&json!({"cmd":"cache.step"})).unwrap()["blocked_reason"],
        "source_not_ready"
    );
    let published = data_ids(&publish(&v, &remote));
    assert_eq!(
        v.control(&json!({"cmd":"cache.step"})).unwrap()["blocked_reason"],
        "source_not_ready"
    );
    assert!(published.iter().all(|id| !object(&v, id).missing));
    enable_cache(&v, &remote);
    // Keep this interval genuinely RAM-dirty; the normal 250 ms background
    // commit timer must not turn it into a timing-dependent clean-page test.
    let pending_commit = v.shared.flush_lock.lock().unwrap();
    v.write(0, &[29; PAGE]).unwrap();
    let dirty_bytes = v.info().unwrap().dirty_bytes;
    assert!(dirty_bytes > 0);
    v.control(&json!({"cmd":"cache.step","max_objects":4}))
        .unwrap();
    assert_eq!(v.info().unwrap().dirty_bytes, dirty_bytes);
    let mut page = [0; PAGE];
    v.read(0, &mut page).unwrap();
    assert_eq!(page, [29; PAGE]);
    drop(pending_commit);
    v.flush().unwrap();
    let snapshot = v.snapshot_create().unwrap();
    let manifest = v.snapshot_manifest(&snapshot).unwrap();
    let dirty_id = manifest["objects"][0]["id"].as_str().unwrap().to_owned();
    assert!(
        object(&v, &dirty_id).sealed,
        "the test must exercise sealed but unpublished data, not just an active tail"
    );
    assert!(v
        .control(&json!({"cmd":"cache.source","object_id":dirty_id}))
        .is_err());
    for _ in 0..4 {
        v.control(&json!({"cmd":"cache.step","max_objects":4}))
            .unwrap();
    }
    assert!(!object(&v, &dirty_id).missing);
    v.read(0, &mut page).unwrap();
    assert_eq!(page, [29; PAGE]);
    assert_eq!(remote.calls.load(Ordering::SeqCst), 0);
    assert_eq!(
        v.control(&json!({"cmd":"cloud.status"})).unwrap()["local_dirty"],
        true
    );
}

#[test]
fn snapshot_only_published_history_can_be_evicted_and_gc_keeps_its_remote_source() {
    let directory = TempDir::new().unwrap();
    let path = directory.path().join("history.odv4");
    Volume::create(&path, 64 << 20, Some("history-password")).unwrap();
    let v = Volume::open(&path, Some("history-password")).unwrap();
    let remote = Remote::default();
    v.write(0, &[41; PAGE]).unwrap();
    v.flush().unwrap();
    bind_cloud(&v);
    let first = publish(&v, &remote);
    let old = data_ids(&first).remove(0);
    enable_cache(&v, &remote);
    let snapshot = v.snapshot_create().unwrap();
    v.write(0, &[53; PAGE]).unwrap();
    v.flush().unwrap();
    publish(&v, &remote);
    let historical = object(&v, &old);
    assert_eq!(historical.current_refs, 0);
    assert!(historical.refs > 0);
    let gc = v
        .control(&json!({"cmd":"cloud.gc_candidates","object_ids":[old]}))
        .unwrap();
    assert_eq!(gc["protected"], json!([old]));
    assert_eq!(gc["allowed"], json!([]));
    let before = logical_state(&v);
    evict(&v, std::slice::from_ref(&old));
    assert_eq!(logical_state(&v), before);
    assert_eq!(
        v.control(&json!({"cmd":"cache.source","object_id":old}))
            .unwrap()["source"],
        "own"
    );
    let mut bytes = vec![0; OBJECT as usize];
    v.object_read(&snapshot, &old, 0, &mut bytes).unwrap();
    assert_eq!(codec::hash(&bytes), codec::hash(&remote.bytes(&old)));
    assert_eq!(remote.calls.load(Ordering::SeqCst), 1);
    let index = v
        .shared
        .store
        .lock()
        .unwrap()
        .snapshots
        .iter()
        .find(|entry| entry.id == snapshot)
        .unwrap()
        .index;
    let (lease, mut reader) = v.shared.read_snapshot(index).unwrap();
    v.snapshot_release(&snapshot).unwrap();
    let held = v
        .control(&json!({"cmd":"cache.step","max_objects":4}))
        .unwrap();
    assert!(
        object(&v, &old).missing,
        "the old reader fixture must actually retire the cached object"
    );
    assert!(
        held["pending_reclaims"].as_u64().unwrap() > 0,
        "active reader must defer physical hole punching"
    );
    assert_eq!(
        *reader.read_page(0).unwrap(),
        [41; PAGE],
        "an older active reader must retain its original physical bytes"
    );
    let gc = v
        .control(&json!({"cmd":"cloud.gc_candidates","object_ids":[old]}))
        .unwrap();
    assert_eq!(
        gc["protected"],
        json!([old]),
        "reader lifetime remains a GC boundary after snapshot release"
    );
    drop(reader);
    drop(lease);
    let released = v
        .control(&json!({"cmd":"cache.step","max_objects":4}))
        .unwrap();
    assert_eq!(released["pending_reclaims"], 0);
    assert!(released["evicted_bytes"].as_u64().unwrap() > held["evicted_bytes"].as_u64().unwrap());
    let mut current = [0; PAGE];
    v.read(0, &mut current).unwrap();
    assert_eq!(current, [53; PAGE]);
}

#[test]
fn read_only_queue_barrier_rejects_guest_mutations_but_cache_and_hydration_still_work() {
    let directory = TempDir::new().unwrap();
    let path = directory.path().join("readonly.odv4");
    Volume::create(&path, 64 << 20, None).unwrap();
    let v = Arc::new(Volume::open(&path, None).unwrap());
    let remote = Remote::default();
    v.write(0, &[67; PAGE]).unwrap();
    v.flush().unwrap();
    bind_cloud(&v);
    let ids = data_ids(&publish(&v, &remote));
    enable_cache(&v, &remote);
    v.control(&json!({"cmd":"compact.start","mode":"deep"}))
        .unwrap();
    let queue = Queue::new(Arc::new(CoreBackend { volume: v.clone() })).unwrap();
    queue
        .submit_write(1, PAGE as u64, &[79; PAGE], false)
        .unwrap();
    queue
        .call(Operation::Control(
            json!({"cmd":"volume.read_only","enabled":true}),
        ))
        .unwrap();
    let accepted = queue
        .next_completion(Duration::from_secs(5))
        .unwrap()
        .unwrap();
    assert_eq!(accepted.token, 1);
    assert_eq!(accepted.status, 0);
    queue.release_completion(1).unwrap();
    assert_eq!(
        v.info().unwrap().dirty_bytes,
        0,
        "mode barrier persists previously accepted guest writes"
    );
    let before = logical_state(&v);
    assert!(v.write(0, &[91; PAGE]).is_err());
    assert!(v.trim(0, PAGE as u64).is_err());
    assert_eq!(
        v.control(&json!({"cmd":"compact.start","mode":"deep"}))
            .unwrap()["mode"],
        "deep"
    );
    v.control(&json!({"cmd":"compact.step","max_objects":1}))
        .unwrap();
    assert_eq!(
        logical_state(&v),
        before,
        "read-only deep maintenance is physical and must not create upload work"
    );
    queue.submit_write(2, 0, &[91; PAGE], true).unwrap();
    queue
        .submit(
            3,
            Operation::Trim {
                offset: 0,
                length: PAGE as u64,
                fua: true,
            },
        )
        .unwrap();
    let mut rejected = BTreeSet::new();
    for _ in 0..2 {
        let failed = queue
            .next_completion(Duration::from_secs(5))
            .unwrap()
            .unwrap();
        assert_ne!(failed.status, 0);
        assert!(failed
            .error
            .as_ref()
            .unwrap()
            .to_str()
            .unwrap()
            .contains("read-only"));
        rejected.insert(failed.token);
        queue.release_completion(failed.token).unwrap();
    }
    assert_eq!(rejected, BTreeSet::from([2, 3]));
    assert_eq!(logical_state(&v), before);
    v.control(&json!({"cmd":"compact.cancel"})).unwrap();
    evict(&v, &ids);
    let mut page = [0; PAGE];
    v.read(0, &mut page).unwrap();
    assert_eq!(page, [67; PAGE]);
    v.read(PAGE as u64, &mut page).unwrap();
    assert_eq!(page, [79; PAGE]);
    assert_eq!(remote.calls.load(Ordering::SeqCst), 1);
    assert_eq!(logical_state(&v), before);
    let snapshot = v.snapshot_create().unwrap();
    v.snapshot_release(&snapshot).unwrap();
    v.control(&json!({"cmd":"compact.start","mode":"normal"}))
        .unwrap();
    v.control(&json!({"cmd":"compact.step","max_objects":1}))
        .unwrap();
    assert_eq!(
        logical_state(&v),
        before,
        "normal maintenance must not create upload work either"
    );
    v.control(&json!({"cmd":"compact.cancel"})).unwrap();
    queue
        .call(Operation::Control(
            json!({"cmd":"volume.read_only","enabled":false}),
        ))
        .unwrap();
    queue.write(0, &[103; PAGE], true).unwrap();
    v.read(0, &mut page).unwrap();
    assert_eq!(page, [103; PAGE]);
    queue.shutdown();
}
