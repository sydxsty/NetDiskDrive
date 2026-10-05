//! End-to-end format-3 contracts, independent of local physical offsets.
use overlaydisk_core::v2::{Volume, OBJECT, PAGE};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{collections::BTreeMap, sync::Arc};
const CAPACITY: u64 = 64 * 1024 * 1024;
fn command(v: &Volume, r: Value) -> Value {
    v.control_v3(&r).unwrap_or_else(|e| panic!("{r}: {e}"))
}
fn prepare(v: &Volume) -> Value {
    let mut j = command(v, json!({"cmd":"cloud.prepare"}))["job"].clone();
    for _ in 0..100 {
        if j["phase"] != "preparing" {
            return j;
        }
        j = command(
            v,
            json!({"cmd":"cloud.prepare","job_id":j["id"],"max_pages":4096,"max_objects":4}),
        )["job"]
            .clone();
    }
    panic!("preparation did not finish: {j}");
}
fn objects(v: &Volume, j: &Value) -> Vec<Value> {
    let mut all = vec![];
    let mut cursor = 0;
    loop {
        let r = command(
            v,
            json!({"cmd":"cloud.list","job_id":j["id"],"cursor":cursor,"limit":128}),
        );
        all.extend(r["items"].as_array().unwrap().clone());
        if let Some(n) = r["next_cursor"].as_u64() {
            cursor = n
        } else {
            return all;
        }
    }
}
fn export(v: &Volume, j: &Value, o: &Value) -> Vec<u8> {
    let mut bytes = vec![0; OBJECT as usize];
    for (i, b) in bytes.chunks_mut(1024 * 1024).enumerate() {
        v.read_export_v3(
            j["id"].as_str().unwrap(),
            o["id"].as_str().unwrap(),
            i as u64 * 1024 * 1024,
            b,
        )
        .unwrap();
    }
    assert_eq!(
        format!("{:x}", Sha256::digest(&bytes)),
        o["sha256"].as_str().unwrap()
    );
    bytes
}
fn receipt(v: &Volume, j: &Value, o: &Value) {
    command(
        v,
        json!({"cmd":"cloud.receipt","job_id":j["id"],"object_id":o["id"],"length":OBJECT,"sha256":o["sha256"],"receipt":"test-verified"}),
    );
}
fn publish(v: &Volume, j: &Value) {
    command(
        v,
        json!({"cmd":"cloud.commit","job_id":j["id"],"root_object_id":j["root_object_id"],"root_sha256":j["root_sha256"],"receipt":"latest-readback"}),
    );
}
#[test]
fn portable_cloud_publish_restore_resume_and_incremental_reuse() {
    for password in [None, Some("云端恢复密码")] {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("source.odv3");
        let target = dir.path().join("restored.odv3");
        let mut v = Volume::create_v3(&path, CAPACITY, password).unwrap();
        command(
            &v,
            json!({"cmd":"cloud.bind","backend_id":"test","account_id":"a","remote_root":"/test","device_id":"one","enabled":true}),
        );
        let mut expected = vec![0u8; 1100 * PAGE];
        for (i, page) in expected.chunks_mut(PAGE).enumerate() {
            page.fill((i % 251 + 1) as u8);
        }
        v.write(0, &expected).unwrap();
        v.flush().unwrap();
        let sourceid = v.info().unwrap().id;
        let first = prepare(&v);
        let list = objects(&v, &first);
        assert!(list.iter().filter(|o| o["kind"] == "data").count() >= 2);
        let mut remote = BTreeMap::new();
        for o in &list {
            assert_eq!(o["length"], OBJECT);
            remote.insert(o["id"].as_str().unwrap().to_owned(), export(&v, &first, o));
            if o["kind"] != "commit" {
                receipt(&v, &first, o);
            }
        }
        // Frozen upload snapshot survives newer local bytes and reopening.
        v.write(0, &[77; PAGE]).unwrap();
        v.flush().unwrap();
        drop(v);
        v = Volume::open(&path, password).unwrap();
        assert_eq!(
            command(&v, json!({"cmd":"cloud.status"}))["job"]["id"],
            first["id"]
        );
        for o in &list {
            assert_eq!(export(&v, &first, o), remote[o["id"].as_str().unwrap()]);
        }
        publish(&v, &first);
        assert!(v
            .read_export_v3(
                first["id"].as_str().unwrap(),
                list[0]["id"].as_str().unwrap(),
                0,
                &mut [0]
            )
            .is_err());
        let second = prepare(&v);
        let secondlist = objects(&v, &second);
        assert!(secondlist
            .iter()
            .any(|o| o["kind"] == "data" && o["uploaded"] == true));
        for o in &secondlist {
            if o["uploaded"] != true {
                remote.insert(o["id"].as_str().unwrap().to_owned(), export(&v, &second, o));
                if o["kind"] != "commit" {
                    receipt(&v, &second, o);
                }
            }
        }
        publish(&v, &second);
        let unchanged = command(&v, json!({"cmd":"cloud.prepare"}));
        assert_eq!(unchanged["up_to_date"], true);
        assert!(unchanged["job"].is_null());
        let root = &remote[first["root_object_id"].as_str().unwrap()];
        let mut restored = Volume::restore_begin_v3(&target, root, password).unwrap();
        assert_ne!(restored.info().unwrap().id, sourceid);
        assert!(restored.info().unwrap().restore_incomplete);
        assert!(restored.read(0, &mut [0; 512]).is_err());
        let mut accepted = 0;
        loop {
            let state = command(&restored, json!({"cmd":"restore.status","limit":1}));
            if state["phase"] == "ready" {
                break;
            }
            let needed = state["needed"].as_array().unwrap();
            assert_eq!(needed.len(), 1);
            let id = needed[0]["id"].as_str().unwrap();
            restored.restore_accept_v3(id, &remote[id]).unwrap();
            restored.restore_accept_v3(id, &remote[id]).unwrap();
            accepted += 1;
            if accepted == 2 {
                drop(restored);
                restored = Volume::open(&target, password).unwrap();
            }
        }
        command(&restored, json!({"cmd":"restore.finish"}));
        assert!(!restored.info().unwrap().restore_incomplete);
        let mut actual = vec![0; expected.len()];
        restored.read(0, &mut actual).unwrap();
        assert_eq!(actual, expected);
        assert_eq!(
            command(&restored, json!({"cmd":"cloud.status"}))["bound"],
            false
        );
        drop(restored);
        assert!(!Volume::inspect(&target).unwrap().restore_incomplete);
        let copy = dir.path().join("copy.odv3");
        std::fs::copy(&target, &copy).unwrap();
        let restored = Volume::open(copy, password).unwrap();
        restored.read(0, &mut actual).unwrap();
        assert_eq!(actual, expected);
    }
}
#[test]
fn compact_pause_reopen_concurrent_write_and_local_snapshot_restore() {
    let dir = tempfile::tempdir().unwrap();
    let p = dir.path().join("source.odv3");
    let t = dir.path().join("snapshot.odv3");
    let mut source = Arc::new(Volume::create_v3(&p, CAPACITY, Some("source-password")).unwrap());
    let old = vec![31u8; 70 * PAGE];
    source.write(0, &old).unwrap();
    let snapshot = command(&source, json!({"cmd":"snapshot.create","name":"before"}))["snapshot"]
        ["id"]
        .as_str()
        .unwrap()
        .to_owned();
    let generation = source.info().unwrap().data_generation;
    command(&source, json!({"cmd":"compact.start"}));
    command(&source, json!({"cmd":"compact.step","max_pages":7}));
    command(&source, json!({"cmd":"compact.pause","paused":true}));
    let paused = command(&source, json!({"cmd":"compact.status"}));
    assert_eq!(
        command(&source, json!({"cmd":"compact.step","max_pages":7})),
        paused
    );
    drop(source);
    source = Arc::new(Volume::open(&p, Some("source-password")).unwrap());
    assert_eq!(
        command(&source, json!({"cmd":"compact.status"}))["state"],
        "paused"
    );
    source.write(9 * PAGE as u64, &[99; PAGE]).unwrap();
    source.flush().unwrap();
    command(&source, json!({"cmd":"compact.pause","paused":false}));
    let mut done = false;
    for _ in 0..100 {
        let state = command(&source, json!({"cmd":"compact.step","max_pages":7}));
        if state["state"] == "done" {
            done = true;
            break;
        }
    }
    assert!(done);
    assert_eq!(source.info().unwrap().data_generation, generation + 1);
    let mut actual = vec![0; old.len()];
    source.read(0, &mut actual).unwrap();
    let mut expected = old.clone();
    expected[9 * PAGE..10 * PAGE].fill(99);
    assert_eq!(actual, expected);
    let mut target =
        Volume::restore_snapshot_begin_v3(source.clone(), &snapshot, &t, Some("target-password"))
            .unwrap();
    command(
        &target,
        json!({"cmd":"snapshot.restore_step","max_pages":11}),
    );
    drop(target);
    target = Volume::open(&t, Some("target-password")).unwrap();
    assert_eq!(
        command(&target, json!({"cmd":"restore.status"}))["phase"],
        "needs_source"
    );
    target.restore_snapshot_resume_v3(source.clone()).unwrap();
    for _ in 0..20 {
        if command(
            &target,
            json!({"cmd":"snapshot.restore_step","max_pages":11}),
        )["phase"]
            == "complete"
        {
            break;
        }
    }
    assert!(!target.info().unwrap().restore_incomplete);
    target.read(0, &mut actual).unwrap();
    assert_eq!(actual, old);
    command(
        &target,
        json!({"cmd":"snapshot.restore_step","max_pages":11}),
    );
    assert_eq!(
        command(&source, json!({"cmd":"snapshot.list"}))["items"]
            .as_array()
            .unwrap()
            .len(),
        1
    );
    drop(target);
    assert!(!Volume::inspect(&t).unwrap().restore_incomplete);
}
#[test]
fn incorrect_cloud_bytes_and_incomplete_target_never_mount() {
    let dir = tempfile::tempdir().unwrap();
    let v = Volume::create_v3(dir.path().join("s.odv3"), CAPACITY, Some("password")).unwrap();
    v.write(0, &[7; PAGE]).unwrap();
    let j = prepare(&v);
    let list = objects(&v, &j);
    let root = list.iter().find(|o| o["kind"] == "commit").unwrap();
    let mut bytes = export(&v, &j, root);
    assert!(
        Volume::restore_begin_v3(dir.path().join("wrong.odv3"), &bytes, Some("wrong")).is_err()
    );
    bytes[10000] ^= 1;
    assert!(
        Volume::restore_begin_v3(dir.path().join("corrupt.odv3"), &bytes, Some("password"))
            .is_err()
    );
    let bytes = export(&v, &j, root);
    let targetpath = dir.path().join("target.odv3");
    let target = Volume::restore_begin_v3(&targetpath, &bytes, Some("password")).unwrap();
    assert!(target.control_v3(&json!({"cmd":"restore.finish"})).is_err());
    assert!(target.write(0, &[1; 512]).is_err());
    drop(target);
    assert!(Volume::inspect(&targetpath).unwrap().restore_incomplete);
    let target = Volume::open(&targetpath, Some("password")).unwrap();
    assert!(target.info().unwrap().restore_incomplete);
    assert!(target.read(0, &mut [0; 512]).is_err());
}

#[test]
fn changed_group_reuses_other_portable_indexes_and_latest_receipts() {
    let dir = tempfile::tempdir().unwrap();
    let v = Volume::create_v3(dir.path().join("groups.odv3"), CAPACITY, None).unwrap();
    // Rotate forty-two small immutable source objects without allocating a
    // forty-two-object logical model. This exercises two portable index parts.
    for i in 0..42u64 {
        v.write(i * PAGE as u64, &[(i + 1) as u8; PAGE]).unwrap();
        command(
            &v,
            json!({"cmd":"snapshot.create","name":format!("capture-{i}")}),
        );
    }
    let first = prepare(&v);
    let first_list = objects(&v, &first);
    assert_eq!(
        first_list.iter().filter(|o| o["kind"] == "index").count(),
        2
    );
    for o in &first_list {
        if o["kind"] != "commit" {
            receipt(&v, &first, o);
        }
    }
    publish(&v, &first);
    v.write(41 * PAGE as u64, &[215; PAGE]).unwrap();
    let second = prepare(&v);
    let second_list = objects(&v, &second);
    let indexes: Vec<_> = second_list
        .iter()
        .filter(|o| o["kind"] == "index")
        .collect();
    assert_eq!(indexes.len(), 2);
    assert_eq!(indexes.iter().filter(|o| o["uploaded"] == true).count(), 1);
    assert_eq!(indexes.iter().filter(|o| o["uploaded"] == false).count(), 1);
    for o in &second_list {
        if o["kind"] != "commit" && o["uploaded"] != true {
            receipt(&v, &second, o);
        }
    }
    publish(&v, &second);
    let published = command(&v, json!({"cmd":"cloud.published_objects","limit":128}));
    assert_eq!(
        published["total_count"].as_u64().unwrap() as usize,
        second_list.len()
    );
    assert!(published["items"]
        .as_array()
        .unwrap()
        .iter()
        .all(|o| second_list.iter().any(|n| n["id"] == o["id"])));
}
