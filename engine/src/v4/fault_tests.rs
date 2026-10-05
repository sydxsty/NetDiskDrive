use super::store::{Store, DIRTY, PAGES};
use super::*;
use std::sync::atomic::Ordering;
fn open(path: &Path) -> Store {
    let d = Device::open(path, false).unwrap();
    let mut bytes = [0; PAGE];
    d.read(0, &mut bytes).unwrap();
    let c = Config::decode(&bytes).unwrap();
    let k = c.unlock(None).unwrap();
    Store::open(d, c, k).unwrap()
}
fn replace(s: &mut Store, value: u8) -> Result<()> {
    s.transaction(|tx| {
        let mut pages = Vec::new();
        for index in [0, 1] {
            pages.push((
                index,
                Some(tx.append_page(index, &[value; PAGE], 0)?.encode()),
            ));
        }
        tx.root.index = tx.set(&PAGES, tx.root.index, &pages)?;
        tx.root.dirty = tx.set(
            &DIRTY,
            tx.root.dirty,
            &[(0, Some(vec![1])), (1, Some(vec![1]))],
        )?;
        tx.root.allocated_pages = 2;
        tx.root.changed_pages = 2;
        tx.root.data_generation += 1;
        Ok(())
    })
}
fn baseline(path: &Path) {
    Volume::create(path, 64 << 20, None).unwrap();
    let mut s = open(path);
    replace(&mut s, 11).unwrap();
    s.device.resize(s.root.next_extent * OBJECT).unwrap();
    s.device.sync().unwrap();
}
#[test]
fn every_injected_physical_write_failure_recovers_an_atomic_prefix() {
    let dir = tempfile::tempdir().unwrap();
    let base = dir.path().join("base.odv4");
    baseline(&base);
    let probe = dir.path().join("probe.odv4");
    std::fs::copy(&base, &probe).unwrap();
    let mut sample = open(&probe);
    sample.device.events.lock().unwrap().clear();
    replace(&mut sample, 22).unwrap();
    let cuts = sample
        .device
        .events
        .lock()
        .unwrap()
        .iter()
        .filter(|(write, _, _)| *write)
        .count() as i64
        + 3;
    drop(sample);
    let mut successes = 0;
    for cutoff in 0..cuts {
        let path = dir.path().join(format!("failure-{cutoff}.odv4"));
        std::fs::copy(&base, &path).unwrap();
        let mut s = open(&path);
        s.device.fail_after.store(cutoff, Ordering::Relaxed);
        let result = replace(&mut s, 22);
        if result.is_ok() {
            successes += 1;
        }
        drop(s);
        let mut recovered = open(&path);
        let root = recovered.root.index;
        let a = recovered.read_page(root, 0).unwrap();
        let b = recovered.read_page(root, 1).unwrap();
        assert_eq!(a, b, "torn logical transaction at physical write {cutoff}");
        assert!(a == [11; PAGE] || a == [22; PAGE]);
        replace(&mut recovered, 33).unwrap();
        let root = recovered.root.index;
        assert_eq!(recovered.read_page(root, 0).unwrap(), [33; PAGE]);
        drop(recovered);
        std::fs::remove_file(path).unwrap();
    }
    assert!(successes > 0, "fault sweep must include a completed commit");
}
#[test]
fn corrupted_unsealed_page_cannot_be_rehashed_into_a_valid_export() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("corrupt.odv4");
    baseline(&path);
    {
        let mut s = open(&path);
        let root = s.root.index;
        let page = s.page(root, 0).unwrap().unwrap();
        let object = s.object(page.reference.object).unwrap();
        let at = object.extent * OBJECT + (17 + page.reference.slot) * PAGE as u64;
        let mut bytes = [0; PAGE];
        s.device.read(at, &mut bytes).unwrap();
        bytes[81] ^= 1;
        s.device.write(at, &bytes).unwrap();
        s.device.sync().unwrap();
    }
    let v = Volume::open(&path, None).unwrap();
    let mut output = [0; PAGE];
    assert!(v.read(0, &mut output).is_err());
    let j = v
        .control(&serde_json::json!({"cmd":"cloud.prepare"}))
        .unwrap();
    let error = v
        .control(&serde_json::json!({"cmd":"cloud.prepare","job_id":j["job"]["id"]}))
        .unwrap_err();
    assert!(matches!(error, Error::Integrity(_)));
    assert!(
        v.write(0, &[9; PAGE]).is_err(),
        "failed sealing latches a durability error"
    );
}
#[test]
fn process_kill_before_and_during_root_publication_preserves_atomic_versions() {
    use std::{
        process::{Command, Stdio},
        time::{Duration, Instant},
    };
    let dir = tempfile::tempdir().unwrap();
    for stage in ["before_root", "after_first_root"] {
        let path = dir.path().join(format!("{stage}.odv4"));
        baseline(&path);
        let ready = dir.path().join(format!("{stage}.ready"));
        let mut child = Command::new(std::env::current_exe().unwrap())
            .arg("--ignored")
            .arg("--exact")
            .arg("v4::fault_tests::crash_child")
            .env("OVERLAYDISK_V4_CRASH_PATH", &path)
            .env("OVERLAYDISK_V4_CRASH_STAGE", stage)
            .env("OVERLAYDISK_V4_CRASH_READY", &ready)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(15);
        while !ready.exists() {
            if let Some(status) = child.try_wait().unwrap() {
                panic!("crash helper ended before fault point: {status}")
            }
            if Instant::now() > deadline {
                let _ = child.kill();
                let _ = child.wait();
                panic!("crash helper did not reach fault point")
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        child.kill().unwrap();
        child.wait().unwrap();
        let mut recovered = open(&path);
        let root = recovered.root.index;
        let expected = if stage == "before_root" { 11 } else { 22 };
        assert_eq!(recovered.read_page(root, 0).unwrap(), [expected; PAGE]);
        assert_eq!(recovered.read_page(root, 1).unwrap(), [expected; PAGE]);
        replace(&mut recovered, 33).unwrap();
    }
}
#[test]
#[ignore]
fn crash_child() {
    let path = std::env::var("OVERLAYDISK_V4_CRASH_PATH").unwrap();
    let mut s = open(Path::new(&path));
    replace(&mut s, 22).unwrap();
    panic!("crash point missing")
}
#[test]
fn free_space_checkpoint_reuses_only_unreferenced_metadata_and_keeps_snapshot_roots() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("checkpoint.odv4");
    baseline(&path);
    let mut store = open(&path);
    let snapshot = super::cloud::Snapshot {
        id: Uuid::new_v4().to_string(),
        name: "before-reuse".into(),
        index: store.root.index,
        generation: store.root.data_generation,
        created_utc: "0".into(),
        pin: "user".into(),
        target_volume_id: None,
    };
    let snapshots = vec![snapshot.clone()];
    store
        .transaction(|tx| {
            tx.replace(&PAGES, MetaRef::default(), snapshot.index);
            tx.save_snapshots(&snapshots)
        })
        .unwrap();
    store.snapshots = snapshots;
    for value in 12..28 {
        replace(&mut store, value).unwrap();
    }
    store.root.free_events = u64::MAX;
    replace(&mut store, 91).unwrap();
    assert_eq!(
        store.root.free_events, 0,
        "force the amortized free-space checkpoint path"
    );
    drop(store);
    let mut store = open(&path);
    let current = store.root.index;
    assert_eq!(store.read_page(current, 0).unwrap(), [91; PAGE]);
    let old = store.snapshots[0].index;
    assert_eq!(store.read_page(old, 0).unwrap(), [11; PAGE]);
    store
        .transaction(|tx| {
            tx.replace(&PAGES, old, MetaRef::default());
            tx.save_snapshots(&[])
        })
        .unwrap();
    store.snapshots.clear();
    for value in 92..102 {
        replace(&mut store, value).unwrap();
    }
    drop(store);
    let mut store = open(&path);
    let current = store.root.index;
    assert_eq!(store.read_page(current, 0).unwrap(), [101; PAGE]);
    assert!(store.snapshots.is_empty());
}
