use super::*;
use std::{
    io::{BufRead, BufReader},
    process::{Command, Stdio},
    sync::Arc,
};

fn new_volume() -> (tempfile::TempDir, Volume) {
    let dir = tempfile::tempdir().unwrap();
    let volume = Volume::create(dir.path().join("disk"), MIN_CAPACITY, None).unwrap();
    (dir, volume)
}

#[test]
fn zero_partial_cross_page_bounds_and_reopen() {
    let (dir, volume) = new_volume();
    let mut read = vec![9; 8192];
    volume.read(0, &mut read).unwrap();
    assert_eq!(read, vec![0; 8192]);
    let input: Vec<u8> = (0..5120).map(|i| (i % 251) as u8).collect();
    volume.write(3584, &input).unwrap();
    assert!(volume.write(1, &[0; 512]).is_err());
    assert!(volume.write(MIN_CAPACITY, &[1; 512]).is_err());
    assert!(volume.read(u64::MAX - 511, &mut [0; 512]).is_err());
    volume.write(MIN_CAPACITY, &[]).unwrap();
    drop(volume);
    let volume = Volume::open(dir.path().join("disk"), None).unwrap();
    let mut all = vec![0; 12288];
    volume.read(0, &mut all).unwrap();
    assert_eq!(&all[..3584], &[0; 3584]);
    assert_eq!(&all[3584..8704], input);
    assert!(all[8704..].iter().all(|x| *x == 0));
}

#[test]
fn concurrent_nonoverlapping_and_same_page_writes_do_not_lose_updates() {
    let (_dir, volume) = new_volume();
    let volume = Arc::new(volume);
    let threads: Vec<_> = (0..8)
        .map(|thread| {
            let volume = volume.clone();
            std::thread::spawn(move || {
                for revision in 1..=12u8 {
                    volume.write(thread * 512, &[revision; 512]).unwrap();
                    volume
                        .write(8192 + thread * 8192, &[thread as u8 + 1; 8192])
                        .unwrap();
                }
            })
        })
        .collect();
    for thread in threads {
        thread.join().unwrap();
    }
    let mut page = [0; 4096];
    volume.read(0, &mut page).unwrap();
    assert_eq!(page, [12; 4096]);
    for thread in 0..8 {
        let mut data = [0; 8192];
        volume.read(8192 + thread * 8192, &mut data).unwrap();
        assert_eq!(data, [thread as u8 + 1; 8192]);
    }
}

#[test]
fn trim_partial_full_and_final_short_page() {
    let dir = tempfile::tempdir().unwrap();
    let volume = Volume::create(dir.path().join("disk"), MIN_CAPACITY + 512, None).unwrap();
    volume.write(0, &[7; 12288]).unwrap();
    volume.trim(512, 10240).unwrap();
    let mut read = [0; 12288];
    volume.read(0, &mut read).unwrap();
    assert_eq!(&read[..512], &[7; 512]);
    assert_eq!(&read[512..10752], &[0; 10240]);
    assert_eq!(&read[10752..], &[7; 1536]);
    volume.write(MIN_CAPACITY, &[9; 512]).unwrap();
    volume.trim(MIN_CAPACITY, 512).unwrap();
    volume.read(MIN_CAPACITY, &mut read[..512]).unwrap();
    assert_eq!(&read[..512], &[0; 512]);
    volume.trim(0, volume.capacity()).unwrap();
    let segments_before_flush = volume.info().unwrap().segment_count;
    volume.flush().unwrap();
    assert_eq!(volume.info().unwrap().allocated_pages, 0);
    assert_eq!(volume.info().unwrap().segment_count, segments_before_flush);
    assert!(
        segments_before_flush > 0,
        "flush must not run whole-volume GC"
    );
    volume.compact().unwrap();
    assert_eq!(volume.info().unwrap().segment_count, 0);
}

#[test]
fn sqlite_full_rolls_back_overwrites_and_new_mappings() {
    let (dir, volume) = new_volume();
    volume.write(0, &[51; PAGE_SIZE]).unwrap();
    {
        let inner = volume.inner().unwrap();
        inner
            .conn
            .execute_batch("PRAGMA wal_checkpoint(TRUNCATE);")
            .unwrap();
        let page_count: u64 = inner
            .conn
            .query_row("PRAGMA page_count", [], |r| r.get(0))
            .unwrap();
        inner
            .conn
            .pragma_update(None, "max_page_count", page_count)
            .unwrap();
    }
    // Force SQLITE_FULL after some mappings (including page 0) changed inside
    // the transaction, without filling the host filesystem.
    let error = volume.write(0, &vec![97; 2 * 1024 * 1024]).unwrap_err();
    assert!(
        matches!(&error, Error::Sql(rusqlite::Error::SqliteFailure(e, _)) if e.code == rusqlite::ErrorCode::DiskFull),
        "{error}"
    );
    assert!(volume.inner().unwrap().conn.is_autocommit());
    let mut bytes = [0u8; PAGE_SIZE * 2];
    volume.read(0, &mut bytes).unwrap();
    assert_eq!(&bytes[..PAGE_SIZE], &[51; PAGE_SIZE]);
    assert_eq!(&bytes[PAGE_SIZE..], &[0; PAGE_SIZE]);
    assert_eq!(volume.info().unwrap().allocated_pages, 1);
    drop(volume);
    let volume = Volume::open(dir.path().join("disk"), None).unwrap();
    volume.read(0, &mut bytes).unwrap();
    assert_eq!(&bytes[..PAGE_SIZE], &[51; PAGE_SIZE]);
    assert_eq!(&bytes[PAGE_SIZE..], &[0; PAGE_SIZE]);
    volume.write(0, &[18; PAGE_SIZE]).unwrap();
    volume.read(0, &mut bytes[..PAGE_SIZE]).unwrap();
    assert_eq!(&bytes[..PAGE_SIZE], &[18; PAGE_SIZE]);
}

#[test]
fn missing_referenced_segment_prevents_open() {
    let (dir, volume) = new_volume();
    volume.write(0, &[7; PAGE_SIZE]).unwrap();
    let path = dir.path().join("disk");
    drop(volume);
    fs::remove_file(segment_path(&path, 0)).unwrap();
    assert!(Volume::open(&path, None).is_err());
}

#[test]
fn fixed_size_rollover_compaction_and_recovery() {
    let (dir, volume) = new_volume();
    let data = vec![81; 5 * 1024 * 1024];
    volume.write(0, &data).unwrap();
    volume.write(0, &vec![23; 4 * 1024 * 1024]).unwrap();
    let before = volume.info().unwrap();
    assert!(before.segment_count >= 3);
    assert!(before.obsolete_records >= 1024);
    volume.compact().unwrap();
    let after = volume.info().unwrap();
    assert_eq!(after.allocated_pages, 1280);
    assert_eq!(after.segment_count, 2);
    assert_eq!(after.obsolete_records, 0);
    for (_, path) in segments(&volume.dir).unwrap() {
        assert_eq!(fs::metadata(path).unwrap().len(), SEGMENT_SIZE);
    }
    drop(volume);
    let volume = Volume::open(dir.path().join("disk"), None).unwrap();
    let mut read = vec![0; data.len()];
    volume.read(0, &mut read).unwrap();
    assert!(read[..4 * 1024 * 1024].iter().all(|x| *x == 23));
    assert!(read[4 * 1024 * 1024..].iter().all(|x| *x == 81));
}

#[test]
fn exclusive_lock_and_create_never_overwrites() {
    let (dir, volume) = new_volume();
    assert!(matches!(
        Volume::open(dir.path().join("disk"), None),
        Err(Error::Locked)
    ));
    assert!(Volume::create(dir.path().join("disk"), MIN_CAPACITY, None).is_err());
    assert!(Volume::create(dir.path().join("bad"), 512, None).is_err());
    drop(volume);
    Volume::open(dir.path().join("disk"), None).unwrap();
}

#[test]
fn encryption_wrong_password_tamper_and_no_plaintext_on_disk() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("disk");
    let volume = Volume::create(&path, MIN_CAPACITY, Some("correct horse 测试")).unwrap();
    let secret = b"SENSITIVE-DATA-DO-NOT-STORE-PLAINTEXT";
    let mut data = [0u8; PAGE_SIZE];
    for (i, b) in data.iter_mut().enumerate() {
        *b = secret[i % secret.len()];
    }
    volume.write(0, &data).unwrap();
    volume.flush().unwrap();
    for entry in fs::read_dir(&path).unwrap() {
        let entry = entry.unwrap();
        // Windows enforces fs2's whole-file byte-range lock, unlike advisory
        // Unix flock. The empty lock file contains no stored user content.
        if entry.file_name() == "volume.lock" {
            continue;
        }
        let bytes = fs::read(entry.path()).unwrap();
        assert!(!bytes.windows(secret.len()).any(|w| w == secret));
    }
    drop(volume);
    assert!(matches!(
        Volume::open(&path, Some("wrong")),
        Err(Error::Password)
    ));
    let volume = Volume::open(&path, Some("correct horse 测试")).unwrap();
    let mut output = [0; PAGE_SIZE];
    volume.read(0, &mut output).unwrap();
    assert_eq!(output, data);
    let loc = location(&volume.inner().unwrap().conn, 0).unwrap().unwrap();
    let mut file = OpenOptions::new()
        .write(true)
        .open(segment_path(&path, loc.segment))
        .unwrap();
    file.seek(SeekFrom::Start(HEADER_SIZE + loc.slot * RECORD_SIZE + 99))
        .unwrap();
    file.write_all(&[0xDE, 0xAD, 0xBE, 0xEF]).unwrap();
    file.sync_all().unwrap();
    assert!(matches!(
        volume.read(0, &mut output),
        Err(Error::Integrity(0))
    ));
}

#[test]
fn truncated_orphan_is_recovered_but_referenced_segment_is_not() {
    let (dir, volume) = new_volume();
    volume.write(0, &[7; PAGE_SIZE]).unwrap();
    let path = dir.path().join("disk");
    drop(volume);
    fs::write(segment_path(&path, 7), b"torn").unwrap();
    let volume = Volume::open(&path, None).unwrap();
    assert!(!segment_path(&path, 7).exists());
    drop(volume);
    OpenOptions::new()
        .write(true)
        .open(segment_path(&path, 0))
        .unwrap()
        .set_len(15)
        .unwrap();
    assert!(Volume::open(&path, None).is_err());
}

// Spawned directly by the parent test, then forcibly terminated without Drop.
#[test]
#[ignore]
fn crash_child() {
    let Some(path) = std::env::var_os("OVERLAYDISK_CRASH_PATH") else {
        return;
    };
    let mode = std::env::var("OVERLAYDISK_CRASH_MODE").unwrap();
    let volume = Volume::create(PathBuf::from(path), MIN_CAPACITY, None).unwrap();
    volume.write(0, &[31; PAGE_SIZE * 2]).unwrap();
    if mode == "rotate-partial" {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(segment_path(&volume.dir, 1))
            .unwrap();
        file.set_len(SEGMENT_SIZE).unwrap();
        file.write_all(b"ODSE").unwrap();
        file.sync_all().unwrap();
    } else if mode == "rotate-complete" {
        volume.inner().unwrap().writer.rotate().unwrap();
    } else if mode == "committed" {
        volume.write(0, &[67; PAGE_SIZE * 2]).unwrap();
    } else {
        let mut inner = volume.inner().unwrap();
        let Inner { conn, writer } = &mut *inner;
        let tx = conn.transaction().unwrap();
        let version = next_generation(&tx).unwrap();
        volume
            .store_page(&tx, writer, 0, version, &[67; PAGE_SIZE])
            .unwrap();
        writer.sync().unwrap();
        // SQLite may have written uncommitted WAL frames; segment is durable.
        println!("OVERLAYDISK-READY");
        std::io::stdout().flush().unwrap();
        loop {
            std::thread::park_timeout(std::time::Duration::from_secs(1));
        }
    }
    println!("OVERLAYDISK-READY");
    std::io::stdout().flush().unwrap();
    loop {
        std::thread::park_timeout(std::time::Duration::from_secs(1));
    }
}

#[test]
fn killed_process_recovers_committed_state_and_discards_uncommitted_maps() {
    for mode in [
        "committed",
        "uncommitted",
        "rotate-partial",
        "rotate-complete",
    ] {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("disk");
        let mut child = Command::new(std::env::current_exe().unwrap())
            .args([
                "--exact",
                "storage::tests::crash_child",
                "--ignored",
                "--nocapture",
            ])
            .env("OVERLAYDISK_CRASH_PATH", &path)
            .env("OVERLAYDISK_CRASH_MODE", mode)
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()
            .unwrap();
        let output = child.stdout.take().unwrap();
        let mut ready = false;
        for line in BufReader::new(output).lines() {
            if line.unwrap().contains("OVERLAYDISK-READY") {
                ready = true;
                break;
            }
        }
        assert!(ready, "child exited before reaching crash point");
        child.kill().unwrap();
        child.wait().unwrap();
        let volume = Volume::open(&path, None).unwrap();
        let mut read = [0; PAGE_SIZE * 2];
        volume.read(0, &mut read).unwrap();
        assert_eq!(
            read,
            [if mode == "committed" { 67 } else { 31 }; PAGE_SIZE * 2]
        );
        // The abandoned record must not prevent future writes or reopen.
        volume.write(512, &[91; 512]).unwrap();
        drop(volume);
        let volume = Volume::open(&path, None).unwrap();
        volume.read(512, &mut read[..512]).unwrap();
        assert_eq!(&read[..512], &[91; 512]);
    }
}
