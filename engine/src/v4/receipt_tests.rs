use super::*;
use serde_json::{json, Value};
use std::process::Command;

fn prepare(v: &Volume) -> Value {
    let mut job = v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["job"].clone();
    for _ in 0..256 {
        if job["phase"] == "ready" { return job; }
        job = v.control(&json!({"cmd":"cloud.prepare","job_id":job["id"]})).unwrap()["job"].clone();
    }
    panic!("prepare did not finish")
}
fn objects(v: &Volume, job: &Value) -> Vec<Value> {
    let mut result = Vec::new(); let mut cursor = 0;
    loop {
        let page = v.control(&json!({"cmd":"cloud.list","job_id":job["id"],"cursor":cursor,"limit":128})).unwrap();
        result.extend(page["items"].as_array().unwrap().iter().cloned());
        let Some(next) = page["next_cursor"].as_u64() else { return result; };
        cursor = next;
    }
}
fn ack(v: &Volume, job: &Value, items: &[Value]) -> Result<Value> {
    v.control(&json!({"cmd":"cloud.receipt","job_id":job["id"],"records":items.iter().map(|o|
        json!({"object_id":o["id"],"sha256":o["sha256"],"length":o["length"]})).collect::<Vec<_>>()}))
}
fn publish(v: &Volume, job: &Value) -> Result<Value> {
    v.control(&json!({"cmd":"cloud.commit","job_id":job["id"],"root_object_id":job["root_object_id"],
        "root_sha256":job["root_sha256"],"receipt":"isolated-memory-cloud"}))
}
fn fixture(path: &Path, count: usize) -> (Volume, Value, Vec<Value>) {
    Volume::create(path, 64 << 20, Some("receipt-password")).unwrap();
    let v = Volume::open(path, Some("receipt-password")).unwrap();
    // Many small immutable objects exercise checkpoint boundaries without a
    // large payload or a throughput benchmark.
    {
        let mut store = v.shared.store.lock().unwrap();
        store.transaction(|tx| {
            let mut pages = Vec::new(); let mut dirty = Vec::new();
            for index in 0..count as u64 {
                let p = tx.append_page(index, &[index as u8; PAGE], 0)?;
                tx.seal(p.reference.object, None)?;
                tx.root.tails[0] = 0;
                pages.push((index, Some(p.encode())));
                dirty.push((index, Some(vec![1])));
            }
            tx.root.index = tx.set(&store::PAGES, tx.root.index, &pages)?;
            tx.root.dirty = tx.set(&store::DIRTY, tx.root.dirty, &dirty)?;
            tx.root.allocated_pages = count as u64; tx.root.changed_pages = count as u64;
            tx.root.data_generation += 1;
            Ok(())
        }).unwrap();
    }
    let job = prepare(&v); let items = objects(&v, &job);
    (v, job, items)
}
#[test]
fn receipt_is_one_durable_page_duplicate_is_zero_write_and_reopen_preserves_it() {
    let dir = tempfile::tempdir().unwrap(); let path = dir.path().join("receipt.odv4");
    let (v, job, items) = fixture(&path, 2);
    let seq = v.shared.store.lock().unwrap().root.seq;
    let before = v.shared.device.diagnostics();
    ack(&v, &job, &items[..1]).unwrap();
    let after = v.shared.device.diagnostics();
    assert_eq!(after["physical_write_bytes"] - before["physical_write_bytes"], PAGE as u64);
    assert_eq!(after["physical_sync_calls"] - before["physical_sync_calls"], 1);
    assert_eq!(v.shared.store.lock().unwrap().root.seq, seq, "receipt rewrote the COW root");
    ack(&v, &job, &items[..1]).unwrap();
    assert_eq!(v.shared.device.diagnostics()["physical_write_bytes"], after["physical_write_bytes"]);
    assert_eq!(v.shared.device.diagnostics()["physical_sync_calls"], after["physical_sync_calls"]);
    assert!(publish(&v, &job).is_err(), "incomplete receipts published a version");
    v.snapshot_create().unwrap();
    assert_eq!(v.control(&json!({"cmd":"cloud.status"})).unwrap()["job"]["uploaded_objects"], 1);
    drop(v);
    let v = Volume::open(&path, Some("receipt-password")).unwrap();
    assert_eq!(objects(&v, &job).iter().filter(|o| o["uploaded"] == true).count(), 1);
    ack(&v, &job, &items).unwrap();
    publish(&v, &job).unwrap();
    drop(v);
    let v = Volume::open(&path, Some("receipt-password")).unwrap();
    assert!(objects(&v, &job).iter().all(|o| o["uploaded"] == true));
    assert_eq!(v.control(&json!({"cmd":"cloud.prepare"})).unwrap()["up_to_date"], true);
}
#[test]
fn receipt_checkpoint_reuses_journal_only_after_durable_root_and_keeps_read_leases() {
    let dir = tempfile::tempdir().unwrap(); let path = dir.path().join("checkpoint.odv4");
    let (v, job, items) = fixture(&path, 260);
    ack(&v, &job, &items[..128]).unwrap(); ack(&v, &job, &items[128..256]).unwrap();
    let (lease, mut reader) = v.read_context().unwrap();
    let old: cloud::Cloud = serde_json::from_slice(&reader.blob(lease.root.cloud).unwrap()).unwrap();
    assert_eq!(lease.receipts.count(old.job.as_ref().unwrap()), 256);
    ack(&v, &job, &items[256..]).unwrap();
    let current = v.shared.store.lock().unwrap().cloud.job.clone().unwrap();
    assert_eq!(current.receipt_epoch, 1);
    assert_eq!(lease.receipts.count(&current), 0, "old overlay counted twice after checkpoint");
    assert_eq!(tree::len(&mut reader, &store::SET, old.job.as_ref().unwrap().receipts).unwrap(), 0);
    assert_eq!(lease.receipts.count(old.job.as_ref().unwrap()), 256);
    drop(reader); drop(lease); drop(v);
    let v = Volume::open(&path, Some("receipt-password")).unwrap();
    assert_eq!(objects(&v, &job).iter().filter(|o| o["uploaded"] == true).count(), items.len());
    publish(&v, &job).unwrap();
    let mut read = [0; PAGE]; v.read(259 * PAGE as u64, &mut read).unwrap();
    assert_eq!(read, [259u64 as u8; PAGE]);
}
#[test]
fn damaged_receipt_tail_never_confirms_missing_objects_and_can_resume() {
    let dir = tempfile::tempdir().unwrap(); let path = dir.path().join("torn.odv4");
    let (v, job, items) = fixture(&path, 3);
    ack(&v, &job, &items[..1]).unwrap(); ack(&v, &job, &items[1..2]).unwrap();
    let mut frame = [0; PAGE]; v.shared.device.read(5 * PAGE as u64, &mut frame).unwrap();
    frame[120] ^= 1; v.shared.device.write(5 * PAGE as u64, &frame).unwrap();
    v.shared.device.sync().unwrap(); drop(v);
    let v = Volume::open(&path, Some("receipt-password")).unwrap();
    assert_eq!(objects(&v, &job).iter().filter(|o| o["uploaded"] == true).count(), 1);
    assert!(publish(&v, &job).is_err());
    ack(&v, &job, &items).unwrap();
    assert_eq!(v.shared.store.lock().unwrap().cloud.job.as_ref().unwrap().receipt_epoch, 1);
    publish(&v, &job).unwrap();
}
#[test]
fn failed_receipt_sync_never_reports_success_or_permits_publication() {
    let dir = tempfile::tempdir().unwrap(); let path = dir.path().join("failed.odv4");
    let (v, job, items) = fixture(&path, 1);
    v.shared.device.fail_sync.store(true, Ordering::Relaxed);
    assert!(ack(&v, &job, &items).is_err());
    assert!(publish(&v, &job).is_err());
    assert!(v.control(&json!({"cmd":"cloud.status"})).is_err());
    v.shared.device.fail_sync.store(false, Ordering::Relaxed);
    drop(v);
    let v = Volume::open(&path, Some("receipt-password")).unwrap();
    // A failed OS flush can have written some or all bytes. Only authenticated
    // records are replayed; retry is idempotent in both outcomes.
    ack(&v, &job, &items).unwrap(); publish(&v, &job).unwrap();
}
#[test]
fn invalid_receipt_batch_is_atomic_and_old_journal_cannot_confirm_a_new_job() {
    let dir = tempfile::tempdir().unwrap(); let path = dir.path().join("identity.odv4");
    let (v, job, items) = fixture(&path, 2);
    let mut invalid = items.clone(); invalid.last_mut().unwrap()["sha256"] = json!("00".repeat(32));
    let before = v.shared.device.diagnostics();
    assert!(ack(&v, &job, &invalid).is_err());
    assert_eq!(v.shared.device.diagnostics()["physical_write_bytes"], before["physical_write_bytes"]);
    assert_eq!(v.control(&json!({"cmd":"cloud.status"})).unwrap()["job"]["uploaded_objects"], 0);
    ack(&v, &job, &items).unwrap(); publish(&v, &job).unwrap();
    v.write(0, &[89; PAGE]).unwrap();
    let next = prepare(&v);
    assert_ne!(next["id"], job["id"]);
    let next_items = objects(&v, &next);
    drop(v);
    let v = Volume::open(&path, Some("receipt-password")).unwrap();
    assert!(objects(&v, &next).iter().all(|o| o["uploaded"] == false));
    assert!(publish(&v, &next).is_err());
    ack(&v, &next, &next_items).unwrap(); publish(&v, &next).unwrap();
}
#[test]
fn late_transfer_notifications_cannot_override_durable_receipts_across_checkpoint() {
    let dir = tempfile::tempdir().unwrap(); let path = dir.path().join("late-status.odv4");
    let (v, job, items) = fixture(&path, 1);
    ack(&v, &job, &items[..1]).unwrap();
    for checkpointed in [false, true] {
        if checkpointed { v.shared.store.lock().unwrap().checkpoint_receipts().unwrap(); }
        for state in ["failed", "uploading", "pending"] {
            let before = v.shared.device.diagnostics()["physical_write_bytes"];
            v.control(&json!({"cmd":"cloud.transfer","job_id":job["id"],"object_id":items[0]["id"],"state":state})).unwrap();
            let summary = v.control(&json!({"cmd":"blocks.summary"})).unwrap();
            assert_eq!(summary["counts"]["uploaded"], 1);
            assert_eq!(summary["counts"]["uploading"], 0);
            assert_eq!(summary["counts"]["failed"], 0);
            assert_eq!(v.shared.device.diagnostics()["physical_write_bytes"], before);
        }
    }
}
#[test]
fn crash_after_receipt_sync_or_checkpoint_preserves_confirmed_prefix() {
    let dir = tempfile::tempdir().unwrap();
    for stage in ["receipt_after_sync", "receipt_after_checkpoint"] {
        let path = dir.path().join(format!("{stage}.odv4"));
        let (v, job, items) = fixture(&path, 2);
        if stage == "receipt_after_checkpoint" { ack(&v, &job, &items[..1]).unwrap(); }
        drop(v);
        let status = Command::new(std::env::current_exe().unwrap()).args(["--ignored", "--exact", "v4::receipt_tests::receipt_crash_child"])
            .env("OD_V4_RECEIPT_PATH", &path).env("OD_V4_RECEIPT_CRASH", stage).status().unwrap();
        assert_eq!(status.code(), Some(77));
        let v = Volume::open(&path, Some("receipt-password")).unwrap();
        assert_eq!(objects(&v, &job).iter().filter(|o| o["uploaded"] == true).count(), 1);
        ack(&v, &job, &items).unwrap(); publish(&v, &job).unwrap();
    }
}
#[test]
fn interrupted_checkpoint_keeps_old_log_until_new_root_is_durable() {
    use std::process::Stdio;
    use std::time::{Duration, Instant};
    let dir = tempfile::tempdir().unwrap();
    for stage in ["before_root", "after_first_root"] {
        let path = dir.path().join(format!("{stage}.odv4"));
        let ready = dir.path().join(format!("{stage}.ready"));
        let (v, job, items) = fixture(&path, 2);
        ack(&v, &job, &items[..1]).unwrap(); drop(v);
        let mut child = Command::new(std::env::current_exe().unwrap())
            .args(["--ignored", "--exact", "v4::receipt_tests::receipt_crash_child"])
            .env("OD_V4_RECEIPT_PATH", &path).env("OD_V4_RECEIPT_CRASH", "receipt_after_checkpoint")
            .env("OVERLAYDISK_V4_CRASH_STAGE", stage).env("OVERLAYDISK_V4_CRASH_READY", &ready)
            .stdout(Stdio::null()).stderr(Stdio::null()).spawn().unwrap();
        let deadline = Instant::now() + Duration::from_secs(20);
        while !ready.exists() {
            if let Some(status) = child.try_wait().unwrap() { panic!("checkpoint helper exited: {status}"); }
            if Instant::now() > deadline {
                let _ = child.kill(); let _ = child.wait(); panic!("checkpoint fault point not reached");
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        child.kill().unwrap(); child.wait().unwrap();
        let v = Volume::open(&path, Some("receipt-password")).unwrap();
        assert_eq!(objects(&v, &job).iter().filter(|o| o["uploaded"] == true).count(), 1);
        ack(&v, &job, &items).unwrap(); publish(&v, &job).unwrap();
    }
}
#[test]
#[ignore]
fn receipt_crash_child() {
    let path = std::env::var("OD_V4_RECEIPT_PATH").unwrap();
    let v = Volume::open(path, Some("receipt-password")).unwrap();
    let job = v.control(&json!({"cmd":"cloud.status"})).unwrap()["job"].clone();
    if std::env::var("OD_V4_RECEIPT_CRASH").unwrap() == "receipt_after_checkpoint" {
        v.shared.store.lock().unwrap().checkpoint_receipts().unwrap();
    } else { ack(&v, &job, &objects(&v, &job)[..1]).unwrap(); }
    panic!("crash point missed")
}
