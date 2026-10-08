//! Functional I/O-count and fault regressions for virtual object padding.
use super::store::{Object, Page as StoredPage, Store};
use super::*;
use tempfile::TempDir;

fn directory() -> TempDir {
    let base = std::env::var_os("NETDISKDRIVE_TEST_TMPDIR")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(std::env::temp_dir);
    tempfile::Builder::new()
        .prefix("logical-zero-")
        .tempdir_in(base)
        .unwrap()
}
fn create(path: &Path, size: u64, password: Option<&str>) -> (Config, Store) {
    let (config, crypto) = Config::create_sized(64 << 20, password, size).unwrap();
    let store = Store::create(Device::open(path, true).unwrap(), config.clone(), crypto).unwrap();
    (config, store)
}
fn reopen(path: &Path, config: &Config, password: Option<&str>) -> Store {
    Store::open(
        Device::open(path, false).unwrap(),
        config.clone(),
        config.unlock(password).unwrap(),
    )
    .unwrap()
}
fn append(store: &mut Store, seed: u8) -> (StoredPage, Object) {
    let page = store
        .transaction(|tx| tx.append_page(0, &[seed; PAGE], 0))
        .unwrap();
    let object = store.object(page.reference.object).unwrap();
    (page, object)
}
fn seal(store: &mut Store, oid: u64) -> Result<Object> {
    store.transaction(|tx| {
        let object = tx.seal(oid, None)?;
        for tail in &mut tx.root.tails {
            if *tail == oid {
                *tail = 0;
            }
        }
        Ok(object)
    })
}
fn end(store: &Store, object: &Object) -> usize {
    object_bytes::payload_end(store.geometry(), object.kind as u64, object.used as u64).unwrap()
}
fn poison_tail(store: &Store, object: &Object, seed: u8) {
    let end = end(store, object) as u64;
    assert!(end < store.object_size());
    store
        .device
        .write(object.extent * store.object_size() + end, &[seed; PAGE])
        .unwrap();
    store
        .device
        .write(
            (object.extent + 1) * store.object_size() - PAGE as u64,
            &[seed; PAGE],
        )
        .unwrap();
    store.device.sync().unwrap();
}
fn overlap(at: u64, length: usize, start: u64, end: u64) -> u64 {
    (at + length as u64).min(end).saturating_sub(at.max(start))
}
fn assert_tail_untouched(store: &Store, object: &Object, writes: bool) {
    let first = object.extent * store.object_size() + end(store, object) as u64;
    let last = (object.extent + 1) * store.object_size();
    assert_eq!(
        store
            .device
            .events
            .lock()
            .unwrap()
            .iter()
            .filter(|(write, _, _)| *write == writes)
            .map(|(_, at, length)| overlap(*at, *length, first, last))
            .sum::<u64>(),
        0,
        "an operation touched physical unused padding"
    );
}

#[test]
fn logical_zero_tail_skips_physical_io_and_survives_reopen_for_every_geometry() {
    let directory = directory();
    for size in [4 << 20, 8 << 20, 16 << 20] {
        let password = (size == 8 << 20).then_some("logical-zero-encrypted");
        let path = directory.path().join(format!("{size}.odv4"));
        let (config, mut store) = create(&path, size, password);
        let (page, object) = append(&mut store, 79);
        poison_tail(&store, &object, 173);
        store.device.events.lock().unwrap().clear();
        let sealed = seal(&mut store, object.oid).unwrap();
        assert_tail_untouched(&store, &sealed, true);
        assert_eq!(store.device.diagnostics()["deallocated_bytes"], 0);
        store.device.events.lock().unwrap().clear();
        let canonical = store.verify_object(&sealed).unwrap();
        assert_tail_untouched(&store, &sealed, false);
        assert!(canonical[end(&store, &sealed)..]
            .iter()
            .all(|byte| *byte == 0));
        let mut plain: [u8; PAGE] = canonical
            [store.geometry().header_bytes()..store.geometry().header_bytes() + PAGE]
            .try_into()
            .unwrap();
        store
            .crypto
            .decode_page(0, page.reference, &mut plain)
            .unwrap();
        assert_eq!(plain, [79; PAGE]);
        let boundary = end(&store, &sealed) as u64;
        for (offset, length) in [
            (0, 37),
            (boundary - 17, 50),
            (boundary + PAGE as u64, 71),
            (size - 31, 31),
        ] {
            let mut bytes = vec![231; length];
            object_bytes::read_range(&store.device, &store.crypto, &sealed, offset, &mut bytes)
                .unwrap();
            assert_eq!(bytes, canonical[offset as usize..offset as usize + length]);
        }
        drop(store);
        let mut store = reopen(&path, &config, password);
        let persisted = store.object(sealed.oid).unwrap();
        assert_eq!(store.verify_object(&persisted).unwrap(), canonical);
        let mut physical = [0; PAGE];
        store
            .device
            .read(persisted.extent * size + boundary, &mut physical)
            .unwrap();
        assert_eq!(physical, [173; PAGE], "sealing physically erased the tail");
    }
}

#[test]
fn logical_zero_boundaries_do_not_hide_header_payload_or_root_descriptor_damage() {
    let directory = directory();
    let (_config, mut store) = create(
        &directory.path().join("bounds.odv4"),
        OBJECT,
        Some("bounds"),
    );
    let (_, data) = append(&mut store, 41);
    let data = seal(&mut store, data.oid).unwrap();
    let canonical = store.verify_object(&data).unwrap();
    let mut false_catalogue = data.clone();
    false_catalogue.used = 0;
    let mut out = [0; 16];
    assert!(object_bytes::read_range(
        &store.device,
        &store.crypto,
        &false_catalogue,
        OBJECT - 16,
        &mut out
    )
    .is_err());
    false_catalogue.used = u16::MAX;
    assert!(store.verify_object(&false_catalogue).is_err());
    let header_at = data.extent * OBJECT;
    let mut header: [u8; PAGE] = canonical[..PAGE].try_into().unwrap();
    header[97] ^= 128;
    store.device.write(header_at, &header).unwrap();
    assert!(
        object_bytes::read_range(&store.device, &store.crypto, &data, OBJECT - 16, &mut out)
            .is_err()
    );
    store.device.write(header_at, &canonical[..PAGE]).unwrap();
    let payload_at = header_at + store.geometry().header_bytes() as u64;
    let mut damaged: [u8; PAGE] = canonical
        [store.geometry().header_bytes()..store.geometry().header_bytes() + PAGE]
        .try_into()
        .unwrap();
    damaged[117] ^= 1;
    store.device.write(payload_at, &damaged).unwrap();
    assert!(store.verify_object(&data).is_err());
    store
        .device
        .write(
            payload_at,
            &canonical[store.geometry().header_bytes()..store.geometry().header_bytes() + PAGE],
        )
        .unwrap();

    let metadata = store
        .transaction(|tx| {
            let mut object = tx.allocate_object(2, 0)?;
            object.used = 1;
            tx.objects.insert(object.oid, object.clone());
            Ok(object)
        })
        .unwrap();
    // The reserved descriptor slot is live framing, unlike the unused suffix.
    store
        .device
        .write(
            metadata.extent * OBJECT + store.geometry().header_bytes() as u64,
            &[199; PAGE],
        )
        .unwrap();
    poison_tail(&store, &metadata, 201);
    store.device.events.lock().unwrap().clear();
    let metadata = seal(&mut store, metadata.oid).unwrap();
    assert_tail_untouched(&store, &metadata, true);
    let raw = store.verify_object(&metadata).unwrap();
    assert!(raw[store.geometry().header_bytes()..end(&store, &metadata)]
        .iter()
        .all(|byte| *byte == 0));
    store::external_table(&store.crypto, metadata.oid, &raw).unwrap();
    store
        .device
        .write(
            metadata.extent * OBJECT + store.geometry().header_bytes() as u64,
            &[1; PAGE],
        )
        .unwrap();
    assert!(
        store.verify_object(&metadata).is_err(),
        "reserved root slot damage was treated as padding"
    );

    let mut invalid_transport = canonical.clone();
    invalid_transport[OBJECT as usize - 1] = 1;
    assert!(store::decode_header(&store.crypto, data.oid, &invalid_transport).is_err());
    assert!(
        store::decode_header(&store.crypto, data.oid, &canonical[..canonical.len() - 1]).is_err()
    );
}

#[test]
fn import_and_lazy_hydration_write_only_canonical_prefix() {
    let directory = directory();
    let (config, mut source) = create(&directory.path().join("source.odv4"), 8 << 20, None);
    let (_, object) = append(&mut source, 29);
    let object = seal(&mut source, object.oid).unwrap();
    let raw = source.verify_object(&object).unwrap();
    let header = store::decode_header(&source.crypto, object.oid, &raw).unwrap();
    for lazy in [false, true] {
        let path = directory.path().join(format!("target-{lazy}.odv4"));
        let mut target = Store::create(
            Device::open(&path, true).unwrap(),
            config.clone(),
            config.unlock(None).unwrap(),
        )
        .unwrap();
        if lazy {
            target
                .transaction(|tx| tx.remote_object(object.id, object.sha, 1))
                .unwrap();
        }
        let mut location = object.clone();
        location.extent = target.root.next_extent;
        poison_tail(&target, &location, 166);
        target.device.events.lock().unwrap().clear();
        let installed = if lazy {
            target
                .transaction(|tx| tx.hydrate_object(object.oid, &raw, &header))
                .unwrap();
            target.object(object.oid).unwrap()
        } else {
            target
                .transaction(|tx| tx.import_object(object.id, object.sha, &raw))
                .unwrap()
        };
        assert_eq!(installed.extent, location.extent);
        assert_tail_untouched(&target, &installed, true);
        assert_eq!(target.verify_object(&installed).unwrap(), raw);
        drop(target);
        let mut target = reopen(&path, &config, None);
        let installed = target.object(object.oid).unwrap();
        assert_eq!(target.verify_object(&installed).unwrap(), raw);
        let mut poison = [0; PAGE];
        target
            .device
            .read(
                installed.extent * target.object_size() + end(&target, &installed) as u64,
                &mut poison,
            )
            .unwrap();
        assert_eq!(poison, [166; PAGE]);
    }
}

fn change(store: &mut Store, oid: u64, relocate: bool) -> Result<()> {
    if relocate {
        store.transaction(|tx| {
            if !tx.move_object(oid)? {
                return Err(Error::Invalid("fixture has no relocation target".into()));
            }
            Ok(())
        })
    } else {
        seal(store, oid).map(|_| ())
    }
}

#[test]
fn interrupted_sealing_and_relocation_recover_without_physical_padding_writes() {
    let directory = directory();
    for relocate in [false, true] {
        let base = directory.path().join(format!("base-{relocate}.odv4"));
        let (config, mut store) = create(&base, OBJECT, None);
        let unused = if relocate {
            let (_, filler) = append(&mut store, 17);
            Some(seal(&mut store, filler.oid).unwrap())
        } else {
            None
        };
        let (page, mut object) = append(&mut store, 83);
        if relocate {
            object = seal(&mut store, object.oid).unwrap();
        }
        poison_tail(&store, &object, 184);
        if let Some(filler) = unused {
            store.transaction(|tx| tx.free_object(filler.oid)).unwrap();
            poison_tail(&store, &filler, 185);
        }
        drop(store);
        let sample = directory.path().join(format!("sample-{relocate}.odv4"));
        std::fs::copy(&base, &sample).unwrap();
        let mut sample_store = reopen(&sample, &config, None);
        sample_store.device.events.lock().unwrap().clear();
        change(&mut sample_store, object.oid, relocate).unwrap();
        let moved = sample_store.object(object.oid).unwrap();
        assert_tail_untouched(&sample_store, &moved, true);
        let writes = sample_store
            .device
            .events
            .lock()
            .unwrap()
            .iter()
            .filter(|(write, _, _)| *write)
            .count() as i64;
        drop(sample_store);
        let mut committed = 0;
        for cutoff in -1..=writes {
            let path = directory
                .path()
                .join(format!("cut-{relocate}-{cutoff}.odv4"));
            std::fs::copy(&base, &path).unwrap();
            let mut store = reopen(&path, &config, None);
            if cutoff < 0 {
                store.device.fail_sync.store(true, Ordering::Relaxed);
            } else {
                store.device.fail_after.store(cutoff, Ordering::Relaxed);
            }
            if change(&mut store, object.oid, relocate).is_ok() {
                committed += 1;
            }
            drop(store);
            let mut recovered = reopen(&path, &config, None);
            let mut current = recovered.object(object.oid).unwrap();
            if !current.sealed {
                current = seal(&mut recovered, current.oid).unwrap();
            }
            if relocate {
                assert_eq!(current.sha, object.sha);
                assert_eq!(current.id, object.id);
            }
            let raw = recovered.verify_object(&current).unwrap();
            let at = recovered.geometry().header_bytes();
            let mut payload: [u8; PAGE] = raw[at..at + PAGE].try_into().unwrap();
            recovered
                .crypto
                .decode_page(0, page.reference, &mut payload)
                .unwrap();
            assert_eq!(payload, [83; PAGE], "write cutoff {cutoff} lost data");
            assert!(raw[end(&recovered, &current)..]
                .iter()
                .all(|byte| *byte == 0));
            assert_eq!(recovered.device.diagnostics()["deallocated_bytes"], 0);
            drop(recovered);
            std::fs::remove_file(path).unwrap();
        }
        assert!(committed > 0);
    }
}
