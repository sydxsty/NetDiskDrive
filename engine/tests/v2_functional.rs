//! Black-box V2 behavior tests: only the public Volume API and an independent
//! byte-vector model are used; no container offsets or index internals are read.
use overlaydisk_core::v2::Volume;
use rand::{rngs::StdRng, Rng, RngCore, SeedableRng};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    fs,
    sync::{
        atomic::{AtomicUsize, Ordering},
        Arc, Barrier,
    },
    thread,
    time::Duration,
};

const MIB: usize = 1024 * 1024;
const CAPACITY: u64 = 64 * MIB as u64;
const MODEL_BYTES: usize = 8 * MIB;
const OBJECT_BYTES: usize = 4 * MIB;
const PASSWORD: &str = "V2-independent-functional-test-密码";

fn secret(encrypted: bool) -> Option<&'static str> {
    encrypted.then_some(PASSWORD)
}

fn assert_region(volume: &Volume, expected: &[u8], offset: usize, length: usize, context: &str) {
    let mut actual = vec![0u8; length];
    volume.read(offset as u64, &mut actual).unwrap();
    if let Some(index) = actual
        .iter()
        .zip(&expected[offset..offset + length])
        .position(|(actual, expected)| actual != expected)
    {
        panic!(
            "{context}: byte {} differs: got {}, expected {}",
            offset + index,
            actual[index],
            expected[offset + index]
        );
    }
}

fn write_model(volume: &Volume, model: &[u8]) {
    for (chunk, bytes) in model.chunks(MIB).enumerate() {
        volume.write((chunk * MIB) as u64, bytes).unwrap();
    }
}

#[test]
fn randomized_sector_page_and_object_boundaries_match_reference_after_reopen() {
    for encrypted in [false, true] {
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("random.odv2");
        let mut volume = Volume::create(&path, CAPACITY, secret(encrypted)).unwrap();
        let mut rng = StdRng::seed_from_u64(0x7e47_414c_32c9_08b1);
        let mut model = vec![0; MODEL_BYTES];
        assert_region(&volume, &model, 0, MODEL_BYTES, "new sparse volume");
        // Initial live data exceeds one 4 MiB export object in both modes.
        rng.fill_bytes(&mut model);
        write_model(&volume, &model);
        volume.flush().unwrap();
        assert_region(&volume, &model, 0, MODEL_BYTES, "initial full model");

        let lengths = [512, 512, 512, 4096, 4096, 8192, 65536, MIB];
        for step in 0..320 {
            let length = lengths[rng.gen_range(0..lengths.len())];
            let offset = if step % 11 == 0 {
                OBJECT_BYTES - 512
            } else if step % 17 == 0 {
                4096 - 512
            } else {
                rng.gen_range(0..=(MODEL_BYTES - length) / 512) * 512
            };
            match rng.gen_range(0..10) {
                0..=5 => {
                    let mut data = vec![0; length];
                    rng.fill_bytes(&mut data);
                    volume.write(offset as u64, &data).unwrap();
                    model[offset..offset + length].copy_from_slice(&data);
                }
                6..=7 => {
                    volume.trim(offset as u64, length as u64).unwrap();
                    model[offset..offset + length].fill(0);
                }
                _ => {}
            }
            let context = format!("encrypted={encrypted}, step={step}");
            assert_region(&volume, &model, offset, length, &context);
            // Also inspect neighboring sectors, catching an over-wide partial update.
            let start = offset.saturating_sub(512);
            let end = (offset + length + 512).min(MODEL_BYTES);
            assert_region(&volume, &model, start, end - start, &context);
            if (step + 1) % 48 == 0 {
                volume.flush().unwrap();
                assert_region(&volume, &model, 0, MODEL_BYTES, &context);
            }
            if (step + 1) % 96 == 0 {
                volume.flush().unwrap();
                drop(volume);
                volume = Volume::open(&path, secret(encrypted)).unwrap();
                assert_region(&volume, &model, 0, MODEL_BYTES, &context);
            }
        }
        volume.flush().unwrap();
        drop(volume);
        let reopened = Volume::open(&path, secret(encrypted)).unwrap();
        assert_region(&reopened, &model, 0, MODEL_BYTES, "final durable model");
    }
}

fn sector_pattern(sector: usize, iteration: usize) -> [u8; 512] {
    let mut data = [0; 512];
    for (index, byte) in data.iter_mut().enumerate() {
        *byte = ((sector * 31 + iteration * 17 + index * 7) % 251 + 1) as u8;
    }
    data
}

#[test]
fn disjoint_sectors_in_one_page_survive_concurrent_writers_and_flushes() {
    for encrypted in [false, true] {
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("concurrent.odv2");
        let volume = Arc::new(Volume::create(&path, CAPACITY, secret(encrypted)).unwrap());
        let start = Arc::new(Barrier::new(10));
        let finished = Arc::new(AtomicUsize::new(0));
        let mut writers = Vec::new();
        for sector in 0..8 {
            let volume = volume.clone();
            let start = start.clone();
            let finished = finished.clone();
            writers.push(thread::spawn(move || {
                struct Finished(Arc<AtomicUsize>);
                impl Drop for Finished {
                    fn drop(&mut self) {
                        self.0.fetch_add(1, Ordering::Release);
                    }
                }
                let _finished = Finished(finished);
                start.wait();
                for iteration in 0..64 {
                    let pattern = sector_pattern(sector, iteration);
                    volume.write((sector * 512) as u64, &pattern).unwrap();
                    let mut actual = [0; 512];
                    volume.read((sector * 512) as u64, &mut actual).unwrap();
                    assert!(
                        actual == pattern,
                        "own sector changed: sector={sector}, iteration={iteration}"
                    );
                    thread::yield_now();
                }
            }));
        }
        let flusher = {
            let volume = volume.clone();
            let start = start.clone();
            let finished = finished.clone();
            thread::spawn(move || {
                start.wait();
                loop {
                    volume.flush().unwrap();
                    if finished.load(Ordering::Acquire) == 8 {
                        break;
                    }
                    thread::sleep(Duration::from_millis(1));
                }
            })
        };
        start.wait();
        // Collect every join before propagating a writer panic, so the flusher
        // is always allowed to stop and the test does not strand live handles.
        let results: Vec<_> = writers.into_iter().map(|writer| writer.join()).collect();
        flusher.join().unwrap();
        for result in results {
            result.unwrap();
        }
        let mut model = vec![0; 4096];
        for sector in 0..8 {
            model[sector * 512..(sector + 1) * 512].copy_from_slice(&sector_pattern(sector, 63));
        }
        assert_region(&volume, &model, 0, 4096, "concurrent cache view");
        volume.flush().unwrap();
        drop(volume);
        let reopened = Volume::open(&path, secret(encrypted)).unwrap();
        assert_region(&reopened, &model, 0, 4096, "concurrent durable view");
    }
}

#[test]
fn copied_container_opens_in_an_empty_directory_without_sidecars() {
    for encrypted in [false, true] {
        let source = tempfile::tempdir().unwrap();
        let destination = tempfile::tempdir().unwrap();
        let path = source.path().join("original.odv2");
        let copied = destination.path().join("copied.odv2");
        let volume = Volume::create(&path, CAPACITY, secret(encrypted)).unwrap();
        let mut model = vec![0; MODEL_BYTES];
        StdRng::seed_from_u64(0x9914_22e9).fill_bytes(&mut model);
        write_model(&volume, &model);
        volume.trim((OBJECT_BYTES - 512) as u64, 8192).unwrap();
        model[OBJECT_BYTES - 512..OBJECT_BYTES - 512 + 8192].fill(0);
        volume.flush().unwrap();
        drop(volume);
        let source_files: Vec<_> = fs::read_dir(source.path()).unwrap().collect();
        assert_eq!(
            source_files.len(),
            1,
            "the source volume must be one self-contained file"
        );
        fs::copy(&path, &copied).unwrap();
        let volume = Volume::open(&copied, secret(encrypted)).unwrap();
        assert_eq!(volume.capacity(), CAPACITY);
        assert_region(&volume, &model, 0, MODEL_BYTES, "copied single-file volume");
        drop(volume);
        assert_eq!(
            fs::read_dir(destination.path()).unwrap().count(),
            1,
            "opening must not create required sidecars"
        );
    }
}

fn snapshot_hashes(volume: &Volume, snapshot: &str) -> BTreeMap<String, Vec<u8>> {
    let manifest = volume.snapshot_manifest(snapshot).unwrap();
    let objects = manifest["objects"]
        .as_array()
        .expect("snapshot object list");
    assert!(!objects.is_empty(), "nonempty disk must export objects");
    let mut hashes = BTreeMap::new();
    let mut buffer = vec![0; MIB];
    for object in objects {
        assert_eq!(object["length"].as_u64(), Some(OBJECT_BYTES as u64));
        let id = object["id"].as_str().expect("object ID");
        let mut hash = Sha256::new();
        let mut read = 0;
        while read < OBJECT_BYTES {
            volume
                .object_read(snapshot, id, read as u64, &mut buffer)
                .unwrap();
            hash.update(&buffer);
            read += buffer.len();
        }
        assert_eq!(read, OBJECT_BYTES);
        assert!(volume
            .object_read(snapshot, id, OBJECT_BYTES as u64, &mut buffer[..512])
            .is_err());
        assert!(
            hashes
                .insert(id.to_owned(), hash.finalize().to_vec())
                .is_none(),
            "duplicate object ID in manifest"
        );
    }
    hashes
}

#[test]
fn snapshot_objects_remain_immutable_after_overwrite_compact_and_reopen() {
    for encrypted in [false, true] {
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("snapshots.odv2");
        let volume = Volume::create(&path, CAPACITY, secret(encrypted)).unwrap();
        let mut rng = StdRng::seed_from_u64(0x8571_abe2);
        let mut model = vec![0; MODEL_BYTES];
        rng.fill_bytes(&mut model);
        write_model(&volume, &model);
        let snapshot = volume.snapshot_create().unwrap();
        let before = snapshot_hashes(&volume, &snapshot);

        rng.fill_bytes(&mut model);
        write_model(&volume, &model);
        volume.trim(512, 8192).unwrap();
        model[512..512 + 8192].fill(0);
        volume.flush().unwrap();
        volume.compact().unwrap();
        assert_region(
            &volume,
            &model,
            0,
            MODEL_BYTES,
            "current data after compaction",
        );
        assert_eq!(
            snapshot_hashes(&volume, &snapshot),
            before,
            "pinned snapshot objects changed"
        );
        volume.flush().unwrap();
        drop(volume);

        let reopened = Volume::open(&path, secret(encrypted)).unwrap();
        assert_region(
            &reopened,
            &model,
            0,
            MODEL_BYTES,
            "current data after reopening snapshot volume",
        );
        assert_eq!(
            snapshot_hashes(&reopened, &snapshot),
            before,
            "snapshot changed across reopen"
        );
        reopened.snapshot_release(&snapshot).unwrap();
        assert!(reopened.snapshot_manifest(&snapshot).is_err());
        let mut data = [0; 512];
        for object in before.keys() {
            assert!(
                reopened
                    .object_read(&snapshot, object, 0, &mut data)
                    .is_err(),
                "released snapshot remained readable"
            );
        }
    }
}
