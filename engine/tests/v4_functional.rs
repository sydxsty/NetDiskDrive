use overlaydisk_core::v4::{Volume, OBJECT, PAGE};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::collections::BTreeMap;
fn create(path: &std::path::Path, password: Option<&str>) -> Volume {
    Volume::create(path, 64 << 20, password).unwrap();
    Volume::open(path, password).unwrap()
}
fn prepare(v: &Volume) -> Value {
    let mut j = v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].clone();
    for _ in 0..10000 {
        if j.is_null() || j["phase"] != "preparing" {
            return j;
        }
        j = v
            .control(
                &json!({"cmd":"cloud.prepare","job_id":j["id"],"max_pages":128,"max_objects":1}),
            )
            .unwrap()["job"]
            .clone();
    }
    panic!("prepare made no finite progress")
}
fn list(v: &Volume, job: &Value, side: Option<&str>) -> Vec<Value> {
    let mut out = Vec::new();
    let mut cursor = 0;
    loop {
        let page=v.control(&json!({"cmd":if side.is_some(){"cloud.delta"}else{"cloud.list"},"job_id":job["id"],"side":side,"cursor":cursor,"limit":17})).unwrap();
        out.extend(page["items"].as_array().unwrap().clone());
        if let Some(next) = page["next_cursor"].as_u64() {
            assert!(next > cursor);
            cursor = next;
        } else {
            break;
        }
    }
    out
}
fn capture(v: &Volume, j: &Value) -> BTreeMap<String, Vec<u8>> {
    let mut objects = BTreeMap::new();
    for item in list(v, j, None) {
        let id = item["id"].as_str().unwrap();
        let mut bytes = vec![0; OBJECT as usize];
        v.read_export(j["id"].as_str().unwrap(), id, 0, &mut bytes)
            .unwrap();
        let actual = format!("{:x}", Sha256::digest(&bytes));
        assert_eq!(actual, item["sha256"].as_str().unwrap());
        v.control(&json!({"cmd":"cloud.receipt","job_id":j["id"],"object_id":id,"sha256":actual,"length":OBJECT,"receipt":"memory-confirmed"})).unwrap();
        objects.insert(id.to_string(), bytes);
    }
    objects
}
fn commit(v: &Volume, j: &Value) {
    v.control(&json!({"cmd":"cloud.commit","job_id":j["id"],"root_object_id":j["root_object_id"],"root_sha256":j["root_sha256"],"receipt":"memory-root-readback"})).unwrap();
}
#[test]
fn plaintext_random_sector_model_reopens_single_file() {
    {
            let password: Option<&str> = None;
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("source.odv4");
        let v = create(&path, password);
        let mut model = vec![0; 8 << 20];
        let mut seed = 0x3141592653589793u64;
        for step in 0..240 {
            seed ^= seed << 13;
            seed ^= seed >> 7;
            seed ^= seed << 17;
            let len = [512, 4096, 8192, 16896][step % 4];
            let at = ((seed as usize % (model.len() - len)) / 512) * 512;
            if step % 7 == 0 {
                v.trim(at as u64, len as u64).unwrap();
                model[at..at + len].fill(0);
            } else {
                let data = (0..len)
                    .map(|i| ((seed >> (i % 8 * 8)) as u8).wrapping_add(i as u8))
                    .collect::<Vec<_>>();
                v.write(at as u64, &data).unwrap();
                model[at..at + len].copy_from_slice(&data);
            }
            let mut actual = vec![0; len];
            v.read(at as u64, &mut actual).unwrap();
            assert_eq!(actual, model[at..at + len]);
            if step % 31 == 0 {
                v.flush().unwrap();
            }
        }
        v.flush().unwrap();
        drop(v);
        let copy = dir.path().join("copy.odv4");
        std::fs::copy(&path, &copy).unwrap();
        let v = Volume::open(copy, password).unwrap();
        let mut actual = vec![0; model.len()];
        v.read(0, &mut actual).unwrap();
        assert_eq!(actual, model);
    }
}
#[test]
fn incremental_snapshot_objects_survive_new_writes_reopen_and_compaction() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("disk.odv4");
    let v = create(&path, None);
    v.control(&json!({"cmd":"cloud.bind","binding":{"provider":"memory","account_id":"test","writer_id":"test","remote_root":"/test","enabled":true}})).unwrap();
    let data = (0..8 * 1024 * 1024)
        .map(|i| (i / 4096 + i % 251) as u8)
        .collect::<Vec<_>>();
    v.write(0, &data).unwrap();
    v.flush().unwrap();
    let first = prepare(&v);
    let mut objects = capture(&v, &first);
    commit(&v, &first);
    let first_generation = v.info().unwrap().data_generation;
    let mut read = vec![0; data.len()];
    v.read(0, &mut read).unwrap();
    v.write(0, &data).unwrap();
    v.flush().unwrap();
    assert_eq!(v.info().unwrap().data_generation, first_generation);
    assert!(prepare(&v).is_null());
    v.write(3 * PAGE as u64, &[0xEF; PAGE]).unwrap();
    v.flush().unwrap();
    let second = prepare(&v);
    let additions = list(&v, &second, None);
    assert!(
        additions.len() < objects.len() + 2,
        "one page must not recreate all data objects"
    );
    let second_objects = capture(&v, &second);
    let delta = list(&v, &second, Some("remove"));
    for old in delta {
        objects.remove(old["id"].as_str().unwrap());
    }
    objects.extend(second_objects);
    commit(&v, &second);
    v.compact().unwrap();
    assert!(
        prepare(&v).is_null(),
        "ordinary compaction cannot dirty cloud data"
    );
    drop(v);
    let v = Volume::open(&path, None).unwrap();
    assert!(prepare(&v).is_null());
    let status = v.control(&json!({"cmd":"cloud.status"})).unwrap();
    assert_eq!(
        status["published_commit"]["root_object_id"],
        second["root_object_id"]
    );
    let mut page = [0; PAGE];
    v.read(3 * PAGE as u64, &mut page).unwrap();
    assert_eq!(page, [0xEF; PAGE]);
    assert!(objects.contains_key(second["root_object_id"].as_str().unwrap()));
    let target = dir.path().join("restored.odv4");
    let copy = Volume::restore_begin_v4(
        &target,
        &objects[second["root_object_id"].as_str().unwrap()],
        None,
    )
    .unwrap();
    assert_ne!(copy.info().unwrap().id, v.info().unwrap().id);
    for _ in 0..10000 {
        let status = copy.control(&json!({"cmd":"restore.status"})).unwrap();
        if status["phase"] == "ready" {
            break;
        }
        let needs = status["needed"].as_array().unwrap();
        if needs.is_empty() {
            copy.control(&json!({"cmd":"restore.step","max_pages":128}))
                .unwrap();
        } else {
            for need in needs {
                let id = need["id"].as_str().unwrap();
                copy.restore_accept(id, &objects[id]).unwrap();
            }
        }
    }
    assert_eq!(
        copy.control(&json!({"cmd":"restore.status"})).unwrap()["phase"],
        "ready"
    );
    copy.control(&json!({"cmd":"restore.finish"})).unwrap();
    let mut expected = data;
    expected[3 * PAGE..4 * PAGE].fill(0xEF);
    let mut actual = vec![0; expected.len()];
    copy.read(0, &mut actual).unwrap();
    assert_eq!(actual, expected);
}
#[test]
fn snapshot_manifest_pin_preserves_raw_object_bytes_after_overwrite() {
    let dir = tempfile::tempdir().unwrap();
    let v = create(&dir.path().join("disk.odv4"), None);
    v.write(0, &vec![42; 2 << 20]).unwrap();
    v.flush().unwrap();
    let snapshot = v.snapshot_create().unwrap();
    let manifest = v.snapshot_manifest(&snapshot).unwrap();
    let descriptors = manifest["objects"].as_array().unwrap();
    let mut hashes = BTreeMap::new();
    for object in descriptors {
        let id = object["id"].as_str().unwrap();
        let mut bytes = vec![0; OBJECT as usize];
        v.object_read(&snapshot, id, 0, &mut bytes).unwrap();
        hashes.insert(id.to_string(), Sha256::digest(&bytes));
    }
    v.write(0, &vec![77; 2 << 20]).unwrap();
    v.flush().unwrap();
    v.compact().unwrap();
    for (id, expected) in hashes {
        let mut bytes = vec![0; OBJECT as usize];
        v.object_read(&snapshot, &id, 0, &mut bytes).unwrap();
        assert_eq!(Sha256::digest(&bytes), expected);
    }
    v.snapshot_release(&snapshot).unwrap();
    assert!(v.snapshot_manifest(&snapshot).is_err());
}
