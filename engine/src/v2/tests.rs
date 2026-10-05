use super::*;
use std::{
    io::{BufRead, BufReader},
    process::{Command, Stdio},
};

fn quiet(volume: &mut Volume) {
    volume.shared.stop.store(true, Ordering::Release);
    volume.shared.wake.notify_all();
    if let Some(thread) = volume.worker.take() {
        thread.join().unwrap();
    }
}
fn fixture() -> (tempfile::TempDir, Volume) {
    let dir = tempfile::tempdir().unwrap();
    let mut volume = Volume::create(dir.path().join("disk.odv2"), 64 << 20, None).unwrap();
    quiet(&mut volume);
    (dir, volume)
}

#[test]
fn random_model_reopen_trim_and_single_file() {
    let (dir, volume) = fixture();
    let mut model = vec![0u8; 128 * 1024];
    let mut seed = 7u64;
    for step in 0..150 {
        seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1);
        let offset = ((seed as usize % 240) * 512).min(model.len() - 8192);
        let length = ((seed >> 24) as usize % 16 + 1) * 512;
        if step % 5 == 0 {
            volume.trim(offset as u64, length as u64).unwrap();
            model[offset..offset + length].fill(0);
        } else {
            let data = vec![(step % 251 + 1) as u8; length];
            volume.write(offset as u64, &data).unwrap();
            model[offset..offset + length].copy_from_slice(&data);
        }
        if step % 13 == 0 {
            volume.flush().unwrap();
        }
        let mut read = vec![0; model.len()];
        volume.read(0, &mut read).unwrap();
        assert_eq!(read, model, "operation {step}");
    }
    volume.flush().unwrap();
    drop(volume);
    let volume = Volume::open(dir.path().join("disk.odv2"), None).unwrap();
    let mut read = vec![0; model.len()];
    volume.read(0, &mut read).unwrap();
    assert_eq!(read, model);
    assert_eq!(std::fs::read_dir(dir.path()).unwrap().count(), 1);
}

#[test]
fn ordinary_cache_persistent_view_and_contiguous_io() {
    let (_dir, volume) = fixture();
    let data = vec![77; 64 * 1024];
    volume.write(0, &data).unwrap();
    let mut persistent = vec![9; data.len()];
    volume.read_persistent(0, &mut persistent).unwrap();
    assert!(persistent.iter().all(|b| *b == 0));
    assert_eq!(volume.info().unwrap().dirty_bytes, 65536);
    volume.shared.file.events.lock().unwrap().clear();
    volume.flush().unwrap();
    let events = volume.shared.file.events.lock().unwrap().clone();
    assert_eq!(
        events
            .iter()
            .filter(|(write, offset, length)| *write
                && *length == 65536
                && *offset % OBJECT >= 17 * PAGE as u64)
            .count(),
        1,
        "data payload must be one 64-KiB physical write"
    );
    volume.read(0, &mut persistent).unwrap();
    volume.shared.file.events.lock().unwrap().clear();
    volume.read(0, &mut persistent).unwrap();
    let events = volume.shared.file.events.lock().unwrap().clone();
    assert_eq!(
        events
            .iter()
            .filter(|(write, _, length)| !*write && *length == 65536)
            .count(),
        1
    );
    assert_eq!(events.iter().filter(|(write, _, _)| !*write).count(), 1);
    assert_eq!(persistent, data);
    let state = volume.shared.state.lock().unwrap();
    assert_eq!(
        state.current.data_tail.unwrap().slot,
        16,
        "flush must not seal/fill active object"
    );
    assert_eq!(
        state.roots[0].as_ref().unwrap().seq,
        state.roots[1].as_ref().unwrap().seq
    );
}

#[test]
fn full_sparse_trim_is_a_range_not_capacity_work() {
    let dir = tempfile::tempdir().unwrap();
    let mut volume =
        Volume::create(dir.path().join("large.odv2"), 64 * 1024 * 1024 * 1024, None).unwrap();
    quiet(&mut volume);
    volume.write(0, &[8; PAGE]).unwrap();
    volume.write(volume.capacity() - 512, &[9; 512]).unwrap();
    volume.flush().unwrap();
    volume.shared.file.events.lock().unwrap().clear();
    volume.trim(0, volume.capacity()).unwrap();
    {
        let state = volume.shared.state.lock().unwrap();
        assert_eq!(state.trims.len(), 1);
        assert!(state.dirty.is_empty());
    }
    volume.flush().unwrap();
    assert_eq!(volume.info().unwrap().allocated_pages, 0);
    assert!(
        volume.shared.file.events.lock().unwrap().len() < 16,
        "whole-disk trim must not iterate logical capacity"
    );
    assert!(volume.shared.file.len().unwrap() <= 64 * 1024 * 1024);
    let mut bytes = [1; 512];
    volume.read(volume.capacity() - 512, &mut bytes).unwrap();
    assert_eq!(bytes, [0; 512]);
}

#[test]
fn encryption_wrong_password_tamper_and_inspect() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("secure.odv2");
    let mut volume = Volume::create(&path, 64 << 20, Some("密码 example")).unwrap();
    quiet(&mut volume);
    let marker = b"PRIVATE-CONTENT-NEVER-IN-PLAINTEXT";
    let mut page = [0; PAGE];
    for (i, b) in page.iter_mut().enumerate() {
        *b = marker[i % marker.len()];
    }
    volume.write(0, &page).unwrap();
    volume.flush().unwrap();
    let reference = {
        let state = volume.shared.state.lock().unwrap();
        volume.shared.lookup(&state.current, 0).unwrap().unwrap()
    };
    drop(volume);
    let bytes = std::fs::read(&path).unwrap();
    assert!(!bytes.windows(marker.len()).any(|w| w == marker));
    assert!(!Volume::inspect(&path).unwrap().authenticated);
    assert!(matches!(
        Volume::open(&path, Some("wrong")),
        Err(Error::Password)
    ));
    let mut volume = Volume::open(&path, Some("密码 example")).unwrap();
    quiet(&mut volume);
    let mut cipher = [0; PAGE];
    volume
        .shared
        .file
        .read(reference.offset(), &mut cipher)
        .unwrap();
    cipher[7] ^= 0x80;
    volume
        .shared
        .file
        .write(reference.offset(), &cipher)
        .unwrap();
    volume.shared.file.sync().unwrap();
    assert!(volume.read(0, &mut page).is_err());
}

#[test]
fn root_mirror_preserves_latest_and_child_loss_is_not_rollback() {
    let (dir, volume) = fixture();
    volume.write(0, &[11; PAGE]).unwrap();
    volume.flush().unwrap();
    volume.write(0, &[22; PAGE]).unwrap();
    volume.flush().unwrap();
    volume.shared.file.write(PAGE as u64, &[0; PAGE]).unwrap();
    volume.shared.file.sync().unwrap();
    drop(volume);
    let mut volume = Volume::open(dir.path().join("disk.odv2"), None).unwrap();
    quiet(&mut volume);
    let mut page = [0; PAGE];
    volume.read(0, &mut page).unwrap();
    assert_eq!(page, [22; PAGE]);
    let index = volume.shared.state.lock().unwrap().current.index;
    volume.shared.file.write(index.offset, &[0; PAGE]).unwrap();
    volume.shared.file.sync().unwrap();
    drop(volume);
    assert!(Volume::open(dir.path().join("disk.odv2"), None).is_err());
}

#[test]
fn snapshots_exact_objects_stay_immutable_through_compaction() {
    let (dir, volume) = fixture();
    volume.write(0, &vec![19; 128 * 1024]).unwrap();
    volume.flush().unwrap();
    let id = volume.snapshot_create().unwrap();
    let manifest = volume.snapshot_manifest(&id).unwrap();
    let ids: Vec<String> = manifest["objects"]
        .as_array()
        .unwrap()
        .iter()
        .map(|o| {
            assert_eq!(o["length"], OBJECT);
            o["id"].as_str().unwrap().to_owned()
        })
        .collect();
    assert!(!ids.is_empty());
    let mut hashes = Vec::new();
    for object in &ids {
        let mut bytes = vec![0; OBJECT as usize];
        volume.object_read(&id, object, 0, &mut bytes).unwrap();
        hashes.push(codec::hash(&bytes));
    }
    volume.write(0, &vec![83; 128 * 1024]).unwrap();
    volume.flush().unwrap();
    volume.compact().unwrap();
    for (object, hash) in ids.iter().zip(&hashes) {
        let mut bytes = vec![0; OBJECT as usize];
        volume.object_read(&id, object, 0, &mut bytes).unwrap();
        assert_eq!(&codec::hash(&bytes), hash);
    }
    drop(volume);
    let volume = Volume::open(dir.path().join("disk.odv2"), None).unwrap();
    for (object, hash) in ids.iter().zip(&hashes) {
        let mut bytes = vec![0; OBJECT as usize];
        volume.object_read(&id, object, 0, &mut bytes).unwrap();
        assert_eq!(&codec::hash(&bytes), hash);
    }
    volume.snapshot_release(&id).unwrap();
    assert!(volume.snapshot_manifest(&id).is_err());
    assert_eq!(std::fs::read_dir(dir.path()).unwrap().count(), 1);
}

#[test]
fn injected_space_full_never_exposes_torn_commit() {
    for fail in 0..4 {
        let (dir, volume) = fixture();
        volume.write(0, &[17; PAGE * 2]).unwrap();
        volume.flush().unwrap();
        volume.write(0, &[29; PAGE * 2]).unwrap();
        volume.shared.file.fail_after.store(fail, Ordering::Relaxed);
        assert!(volume.flush().is_err());
        drop(volume);
        let volume = Volume::open(dir.path().join("disk.odv2"), None).unwrap();
        let mut bytes = [0; PAGE * 2];
        volume.read(0, &mut bytes).unwrap();
        assert!(bytes == [17; PAGE * 2] || bytes == [29; PAGE * 2]);
    }
}

#[test]
#[ignore]
fn crash_child() {
    let Some(path) = std::env::var_os("ODV2_CRASH_PATH") else {
        return;
    };
    let stage = std::env::var("ODV2_CHILD_STAGE").unwrap();
    let mut volume = Volume::create(std::path::PathBuf::from(path), 64 << 20, None).unwrap();
    quiet(&mut volume);
    volume.write(0, &[31; PAGE * 2]).unwrap();
    volume.flush().unwrap();
    std::env::set_var("ODV2_CRASH_STAGE", &stage);
    if stage == "after-descriptors" {
        volume.snapshot_create().unwrap();
    } else {
        volume.write(0, &[47; PAGE * 2]).unwrap();
        volume.flush().unwrap();
    }
}

#[test]
fn killed_process_at_each_persistence_barrier() {
    for stage in [
        "after-data",
        "after-metadata",
        "after-root-write",
        "after-root-sync",
        "after-mirror-sync",
        "after-descriptors",
    ] {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("disk.odv2");
        let mut child = Command::new(std::env::current_exe().unwrap())
            .args([
                "--exact",
                "v2::tests::crash_child",
                "--ignored",
                "--nocapture",
            ])
            .env("ODV2_CRASH_PATH", &path)
            .env("ODV2_CHILD_STAGE", stage)
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
        assert!(ready, "child did not reach {stage}");
        child.kill().unwrap();
        child.wait().unwrap();
        let volume = Volume::open(&path, None).unwrap();
        let mut bytes = [0; PAGE * 2];
        volume.read(0, &mut bytes).unwrap();
        if stage == "after-data" || stage == "after-metadata" || stage == "after-descriptors" {
            assert_eq!(bytes, [31; PAGE * 2]);
        } else {
            assert!(bytes == [31; PAGE * 2] || bytes == [47; PAGE * 2]);
            if stage == "after-root-sync" || stage == "after-mirror-sync" {
                assert_eq!(bytes, [47; PAGE * 2]);
            }
        }
    }
}

#[test]
fn inflight_flush_keeps_new_reads_and_partial_writes_coherent() {
    let (_dir, volume) = fixture();
    let volume = Arc::new(volume);
    volume.write(0, &[41; PAGE * 2]).unwrap();
    let barrier = Arc::new(std::sync::Barrier::new(2));
    *volume.shared.pause_after_data.lock().unwrap() = Some(barrier.clone());
    let worker = {
        let volume = volume.clone();
        thread::spawn(move || volume.flush().unwrap())
    };
    barrier.wait();
    // Flush is deliberately paused in disk persistence while the frontend stays
    // live. These calls would deadlock if the state mutex crossed the sync.
    volume.write(512, &[77; 512]).unwrap();
    let mut bytes = [0; PAGE * 2];
    volume.read(0, &mut bytes).unwrap();
    assert_eq!(&bytes[..512], &[41; 512]);
    assert_eq!(&bytes[512..1024], &[77; 512]);
    assert!(bytes[1024..].iter().all(|b| *b == 41));
    volume.read_persistent(0, &mut bytes).unwrap();
    assert_eq!(bytes, [0; PAGE * 2]);
    assert!(volume.info().unwrap().dirty_bytes <= DIRTY_LIMIT as u64);
    barrier.wait();
    worker.join().unwrap();
    volume.read(0, &mut bytes).unwrap();
    assert_eq!(&bytes[512..1024], &[77; 512]);
    volume.read_persistent(0, &mut bytes).unwrap();
    assert_eq!(bytes, [41; PAGE * 2]);
    volume.flush().unwrap();
    volume.read_persistent(0, &mut bytes).unwrap();
    assert_eq!(&bytes[512..1024], &[77; 512]);
}

#[test]
fn background_flush_and_concurrent_partial_writes() {
    let dir = tempfile::tempdir().unwrap();
    let volume = Arc::new(Volume::create(dir.path().join("disk.odv2"), 64 << 20, None).unwrap());
    let workers: Vec<_> = (0..8)
        .map(|i| {
            let v = volume.clone();
            thread::spawn(move || {
                for _ in 0..10 {
                    v.write(i * 512, &[i as u8 + 1; 512]).unwrap();
                }
            })
        })
        .collect();
    for t in workers {
        t.join().unwrap();
    }
    let deadline = std::time::Instant::now() + Duration::from_secs(5);
    while volume.info().unwrap().dirty_bytes != 0 {
        assert!(std::time::Instant::now() < deadline);
        thread::sleep(Duration::from_millis(20));
    }
    let mut page = [0; PAGE];
    volume.read_persistent(0, &mut page).unwrap();
    for i in 0..8 {
        assert_eq!(&page[i * 512..(i + 1) * 512], &[i as u8 + 1; 512]);
    }
}

#[test]
fn released_snapshot_then_full_trim_compacts_to_control_object() {
    let (_dir, volume) = fixture();
    volume.write(0, &[55; PAGE]).unwrap();
    let snapshot = volume.snapshot_create().unwrap();
    volume.snapshot_release(&snapshot).unwrap();
    volume.trim(0, volume.capacity()).unwrap();
    volume.compact().unwrap();
    assert_eq!(volume.shared.file.len().unwrap(), OBJECT);
    volume.write(0, &[91; PAGE]).unwrap();
    volume.flush().unwrap();
    assert!(volume.shared.file.len().unwrap().is_multiple_of(OBJECT));
    let mut page = [0; PAGE];
    volume.read(0, &mut page).unwrap();
    assert_eq!(page, [91; PAGE]);
}
