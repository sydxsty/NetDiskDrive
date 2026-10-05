//! Correctness and bounded-I/O regressions for encrypted batch writes and sealing.
//! No elapsed-time or throughput assertions; files are isolated temporary fixtures.
use super::*;
use crate::async_io::{Operation, Queue};
use crate::async_v4::CoreBackend;
use serde_json::{json, Value};
use std::collections::BTreeSet;
use std::io::{Read, Seek, SeekFrom, Write};
use tempfile::TempDir;

const PASSWORD: &str = "write-seal-fixture";
const BASE: u64 = 16 * 1024 * 1024;
fn create() -> (TempDir, Volume) {
    let dir = TempDir::new().unwrap();
    let path = dir.path().join("test.odv4");
    Volume::create(&path, 64 * 1024 * 1024, Some(PASSWORD)).unwrap();
    let volume = Volume::open(path, Some(PASSWORD)).unwrap();
    (dir, volume)
}
fn bytes(pages: usize, seed: usize) -> Vec<u8> {
    (0..pages * PAGE)
        .map(|n| ((n * 37 + n / PAGE * 17 + seed) % 251) as u8)
        .collect()
}
fn counter(v: &Volume, name: &str) -> u64 {
    let value = v.control(&json!({"cmd":"sync.diagnostics"})).unwrap();
    value[name].as_u64().unwrap_or(0)
}
fn page_object(v: &Volume, lba: u64) -> store::Object {
    let mut store = v.shared.store.lock().unwrap();
    let root = store.root.index;
    let page = store.page(root, lba).unwrap().unwrap();
    store.object(page.reference.object).unwrap()
}
fn prepare(v: &Volume) -> Value {
    let mut job = v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].clone();
    for _ in 0..64 {
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
    panic!("bounded preparation did not finish: {job}");
}
fn export_and_check(v: &Volume, job: &Value) -> Vec<Value> {
    let listed = v
        .control(&json!({"cmd":"cloud.list","job_id":job["id"],"limit":128}))
        .unwrap();
    assert!(listed["next_cursor"].is_null());
    let objects = listed["items"].as_array().unwrap().clone();
    assert!(!objects.is_empty());
    for object in &objects {
        let mut raw = vec![0; OBJECT as usize];
        v.read_export(
            job["id"].as_str().unwrap(),
            object["id"].as_str().unwrap(),
            0,
            &mut raw,
        )
        .unwrap();
        assert_eq!(codec::hex(&codec::hash(&raw)), object["sha256"]);
    }
    objects
}
fn overlap(at: u64, length: usize, start: u64, end: u64) -> u64 {
    (at + length as u64).min(end).saturating_sub(at.max(start))
}

#[test]
fn encrypted_cross_object_batch_seals_fresh_bytes_once_and_coalesces_io() {
    let (dir, disk) = create();
    let expected = bytes(1200, 29);
    disk.shared.device.events.lock().unwrap().clear();
    disk.write(BASE, &expected).unwrap();
    disk.flush().unwrap();
    let sealed = page_object(&disk, BASE / PAGE as u64);
    assert!(sealed.sealed && sealed.used as u64 == SLOTS);
    let payload = sealed.extent * OBJECT + 17 * PAGE as u64;
    let end = payload + SLOTS * PAGE as u64;
    let events = disk.shared.device.events.lock().unwrap().clone();
    assert_eq!(
        events
            .iter()
            .filter(|(write, _, _)| !write)
            .map(|(_, at, len)| overlap(*at, *len, payload, end))
            .sum::<u64>(),
        0,
        "sealing a full object created in this batch reread its freshly generated ciphertext"
    );
    assert_eq!(
        events
            .iter()
            .filter(|(write, _, _)| *write)
            .map(|(_, at, len)| overlap(*at, *len, payload, end))
            .sum::<u64>(),
        SLOTS * PAGE as u64,
        "payload was rewritten while finalizing its object"
    );
    assert!(
        events
            .iter()
            .filter(|(write, at, len)| *write && overlap(*at, *len, payload, end) != 0)
            .count()
            <= 5,
        "contiguous object payload writes fragmented into page-sized requests"
    );
    assert!(counter(&disk, "seal_fresh_pages") >= SLOTS);
    assert_eq!(counter(&disk, "seal_disk_pages"), 0);
    assert!(counter(&disk, "crypto_pages") >= 1200);
    let workers = counter(&disk, "crypto_worker_limit");
    assert!((1..=4).contains(&workers));
    if workers > 1 {
        assert!(counter(&disk, "crypto_parallel_pages") >= 1200);
    }
    assert!(
        (1..1200).contains(&counter(&disk, "crypto_nonce_batches")),
        "randomness was requested separately for every page"
    );
    assert!(
        counter(&disk, "metadata_write_pages") > counter(&disk, "metadata_write_batches"),
        "metadata writes were not coalesced"
    );
    {
        let mut store = disk.shared.store.lock().unwrap();
        let root = store.root.index;
        let mut nonces = BTreeSet::new();
        for index in BASE / PAGE as u64..BASE / PAGE as u64 + 1200 {
            assert!(
                nonces.insert(store.page(root, index).unwrap().unwrap().reference.nonce),
                "batch reused an encryption nonce"
            );
        }
    }
    disk.shared.device.events.lock().unwrap().clear();
    let mut actual = vec![0; expected.len()];
    disk.read(BASE, &mut actual).unwrap();
    assert_eq!(actual, expected);
    let payload_reads = disk
        .shared
        .device
        .events
        .lock()
        .unwrap()
        .iter()
        .filter(|(write, _, len)| !*write && *len > PAGE)
        .count();
    assert!(
        payload_reads <= 6,
        "contiguous logical reads were fragmented"
    );
    let job = prepare(&disk);
    export_and_check(&disk, &job);
    assert!(counter(&disk, "cloud_index_lookup_batches") > 0);
    drop(disk);
    let disk = Volume::open(dir.path().join("test.odv4"), Some(PASSWORD)).unwrap();
    disk.read(BASE, &mut actual).unwrap();
    assert_eq!(actual, expected);
}

#[test]
fn restarted_partial_tail_is_authenticated_before_sealing_and_corruption_fails() {
    let (dir, disk) = create();
    let expected = bytes(73, 47);
    disk.write(BASE, &expected).unwrap();
    disk.flush().unwrap();
    let object = page_object(&disk, BASE / PAGE as u64);
    assert!(!object.sealed);
    drop(disk);
    let path = dir.path().join("test.odv4");
    let corrupt = dir.path().join("damaged.odv4");
    std::fs::copy(&path, &corrupt).unwrap();
    {
        let disk = Volume::open(&path, Some(PASSWORD)).unwrap();
        let job = prepare(&disk);
        export_and_check(&disk, &job);
        assert!(counter(&disk, "seal_disk_pages") >= 73);
        assert!(counter(&disk, "seal_validated_pages") >= 73);
        let mut actual = vec![0; expected.len()];
        disk.read(BASE, &mut actual).unwrap();
        assert_eq!(actual, expected);
    }
    {
        let mut file = std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .open(&corrupt)
            .unwrap();
        file.seek(SeekFrom::Start(
            object.extent * OBJECT + 17 * PAGE as u64 + 97,
        ))
        .unwrap();
        let mut byte = [0];
        file.read_exact(&mut byte).unwrap();
        byte[0] ^= 0x80;
        file.seek(SeekFrom::Current(-1)).unwrap();
        file.write_all(&byte).unwrap();
        file.sync_all().unwrap();
    }
    let disk = Volume::open(&corrupt, Some(PASSWORD)).unwrap();
    let first = disk.control(&json!({"cmd":"cloud.prepare"})).unwrap();
    let result =
        disk.control(&json!({"cmd":"cloud.prepare","job_id":first["job"]["id"],"max_objects":4}));
    assert!(
        matches!(result, Err(Error::Integrity(_))),
        "damaged persisted ciphertext was blessed as a new sealed object: {result:?}"
    );
    assert!(
        disk.write(BASE + PAGE as u64, &[7; PAGE]).is_err(),
        "sealing persistence/integrity failure did not latch"
    );
}

#[test]
fn tiny_sealed_objects_use_proven_zero_tail_without_full_padding_writes() {
    let (_dir, disk) = create();
    let expected = bytes(1, 61);
    disk.write(BASE, &expected).unwrap();
    disk.flush().unwrap();
    let before = counter(&disk, "seal_padding_write_bytes");
    let job = prepare(&disk);
    assert_eq!(
        counter(&disk, "seal_padding_write_bytes"),
        before,
        "fresh sparse extent was filled with almost 4 MiB of avoidable zero writes"
    );
    let objects = export_and_check(&disk, &job);
    let data = objects.iter().find(|o| o["kind"] == "data").unwrap();
    let mut raw = vec![0; OBJECT as usize];
    disk.read_export(
        job["id"].as_str().unwrap(),
        data["id"].as_str().unwrap(),
        0,
        &mut raw,
    )
    .unwrap();
    assert!(
        raw[18 * PAGE..].iter().all(|byte| *byte == 0),
        "skipped padding did not actually export zero bytes"
    );
    assert_ne!(
        &raw[17 * PAGE..18 * PAGE],
        expected.as_slice(),
        "encrypted payload was stored as plaintext"
    );
}

#[test]
fn reopened_uncommitted_tail_bytes_are_zeroed_before_export_not_trusted_as_sparse() {
    let (dir, disk) = create();
    let expected = bytes(73, 67);
    disk.write(BASE, &expected).unwrap();
    disk.flush().unwrap();
    let generation = disk.info().unwrap().data_generation;
    let object = page_object(&disk, BASE / PAGE as u64);
    assert!(!object.sealed);
    drop(disk);
    let path = dir.path().join("test.odv4");
    // A killed append can leave ciphertext after the committed `used` boundary
    // while both committed roots still describe an earlier partial object.
    {
        let mut file = std::fs::OpenOptions::new().write(true).open(&path).unwrap();
        file.seek(SeekFrom::Start(
            object.extent * OBJECT + (17 + object.used as u64) * PAGE as u64,
        ))
        .unwrap();
        file.write_all(&bytes(3, 71)).unwrap();
        file.sync_all().unwrap();
    }
    let disk = Volume::open(&path, Some(PASSWORD)).unwrap();
    let job = prepare(&disk);
    let objects = export_and_check(&disk, &job);
    let data = objects
        .iter()
        .find(|o| o["id"] == object.id.to_string())
        .unwrap();
    let mut raw = vec![0; OBJECT as usize];
    disk.read_export(
        job["id"].as_str().unwrap(),
        data["id"].as_str().unwrap(),
        0,
        &mut raw,
    )
    .unwrap();
    assert!(
        raw[(17 + object.used as usize) * PAGE..]
            .iter()
            .all(|byte| *byte == 0),
        "uncommitted append garbage leaked into sealed object padding"
    );
    assert_eq!(disk.info().unwrap().data_generation, generation);
    let mut actual = vec![0; expected.len()];
    disk.read(BASE, &mut actual).unwrap();
    assert_eq!(actual, expected);
}

#[test]
fn identical_encrypted_batch_skips_crypto_writes_and_dirty_generation() {
    let (_dir, disk) = create();
    let expected = bytes(256, 79);
    disk.write(BASE, &expected).unwrap();
    disk.flush().unwrap();
    let generation = disk.info().unwrap().data_generation;
    let encrypted = counter(&disk, "crypto_pages");
    let writes = disk.shared.device.diagnostics()["physical_write_calls"];
    disk.shared.device.events.lock().unwrap().clear();
    disk.write(BASE, &expected).unwrap();
    disk.flush().unwrap();
    assert_eq!(disk.info().unwrap().data_generation, generation);
    assert_eq!(disk.info().unwrap().dirty_bytes, 0);
    assert_eq!(counter(&disk, "crypto_pages"), encrypted);
    assert_eq!(
        disk.shared.device.diagnostics()["physical_write_calls"],
        writes
    );
    assert!(!disk.has_local_dirty());
}

#[test]
fn abandoned_extent_beyond_committed_allocator_end_is_not_assumed_zero() {
    let (dir, disk) = create();
    let extent = disk.shared.store.lock().unwrap().root.next_extent;
    drop(disk);
    let path = dir.path().join("test.odv4");
    // An interrupted transaction may have grown/written a new object while the
    // committed allocator still points to its old next_extent. File length is
    // therefore not proof that this next allocation contains only zero bytes.
    {
        let mut file = std::fs::OpenOptions::new().write(true).open(&path).unwrap();
        file.seek(SeekFrom::Start(extent * OBJECT + 31 * PAGE as u64))
            .unwrap();
        file.write_all(&bytes(3, 83)).unwrap();
        file.sync_all().unwrap();
    }
    let disk = Volume::open(&path, Some(PASSWORD)).unwrap();
    let expected = bytes(1, 89);
    disk.write(BASE, &expected).unwrap();
    disk.flush().unwrap();
    let object = page_object(&disk, BASE / PAGE as u64);
    assert_eq!(
        object.extent, extent,
        "fixture did not reuse the abandoned allocation"
    );
    let job = prepare(&disk);
    export_and_check(&disk, &job);
    let mut raw = vec![0; OBJECT as usize];
    disk.read_export(
        job["id"].as_str().unwrap(),
        &object.id.to_string(),
        0,
        &mut raw,
    )
    .unwrap();
    assert!(
        raw[18 * PAGE..].iter().all(|byte| *byte == 0),
        "abandoned extent garbage entered supposedly zero object padding"
    );
    let mut actual = vec![0; PAGE];
    disk.read(BASE, &mut actual).unwrap();
    assert_eq!(actual, expected);
}

#[test]
fn batched_write_then_single_page_fua_preserves_order_snapshot_and_restart() {
    let (dir, disk) = create();
    let disk = Arc::new(disk);
    let queue = Queue::new(Arc::new(CoreBackend {
        volume: disk.clone(),
    }))
    .unwrap();
    let mut expected = bytes(256, 97);
    let replacement = bytes(1, 113);
    queue.submit_write(1, BASE, &expected, false).unwrap();
    queue.submit_write(2, BASE, &replacement, true).unwrap();
    for _ in 0..2 {
        let completion = queue
            .next_completion(Duration::from_secs(30))
            .unwrap()
            .expect("FUA write did not complete");
        assert!(completion.error.is_none(), "ordered encrypted write failed");
        queue.release_completion(completion.token).unwrap();
    }
    expected[..PAGE].copy_from_slice(&replacement);
    let mut actual = vec![0; expected.len()];
    disk.read_persistent(BASE, &mut actual).unwrap();
    assert_eq!(actual, expected);
    let snapshot = disk.snapshot_create().unwrap();
    let manifest = disk.snapshot_manifest(&snapshot).unwrap();
    let first = &manifest["objects"][0];
    let mut raw = vec![0; OBJECT as usize];
    disk.object_read(&snapshot, first["id"].as_str().unwrap(), 0, &mut raw)
        .unwrap();
    let digest = codec::hash(&raw);
    disk.write(BASE, &[29; PAGE]).unwrap();
    disk.flush().unwrap();
    disk.object_read(&snapshot, first["id"].as_str().unwrap(), 0, &mut raw)
        .unwrap();
    assert_eq!(codec::hash(&raw), digest);
    // Complete a second barrier and release the queue before reopening the file.
    queue.submit(3, Operation::Flush).unwrap();
    let completion = queue
        .next_completion(Duration::from_secs(30))
        .unwrap()
        .unwrap();
    assert!(completion.error.is_none());
    queue.release_completion(3).unwrap();
    drop(queue);
    drop(disk);
    let disk = Volume::open(dir.path().join("test.odv4"), Some(PASSWORD)).unwrap();
    expected[..PAGE].fill(29);
    disk.read(BASE, &mut actual).unwrap();
    assert_eq!(actual, expected);
    disk.object_read(&snapshot, first["id"].as_str().unwrap(), 0, &mut raw)
        .unwrap();
    assert_eq!(codec::hash(&raw), digest);
}
