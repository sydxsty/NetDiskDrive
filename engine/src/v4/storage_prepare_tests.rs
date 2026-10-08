//! Preparation regressions count I/O and validate durable bytes; no timing or
//! throughput measurements and no real/user containers are involved.
use super::store::{Page, Store, DIRTY, PORTABLE, PORTMAP};
use super::tree::{self, Spec, Storage};
use super::*;
use tempfile::TempDir;

fn fixture(password: Option<&str>) -> (TempDir, Config, Store) {
    let dir = TempDir::new().unwrap();
    let (config, crypto) = Config::create(64 << 20, password).unwrap();
    let store = Store::create(
        Device::open(&dir.path().join("prepare.odv4"), true).unwrap(),
        config.clone(),
        crypto,
    )
    .unwrap();
    (dir, config, store)
}
fn sealed_page(store: &mut Store) -> Page {
    store
        .transaction(|tx| {
            let page = tx.append_page(0, &[37; PAGE], 0)?;
            tx.seal_tails()?;
            Ok(page)
        })
        .unwrap()
}
fn append_metadata(store: &mut Store, oid: u64, page: &Page, key: u64) -> (u64, MetaRef) {
    store
        .transaction(|tx| {
            tx.portable = Some(oid);
            let root = tree::set_many(
                tx,
                &PORTMAP,
                MetaRef::default(),
                &[(key, Some(page.encode()))],
            )?;
            tx.portable = None;
            Ok(((root.offset & !PORTABLE) / tx.geometry().object_size, root))
        })
        .unwrap()
}
fn count(store: &Store, name: &str) -> u64 {
    store
        .prepare_cache_metrics()
        .get(name)
        .copied()
        .unwrap_or(0)
}
fn overlap(at: u64, len: usize, start: u64, end: u64) -> u64 {
    (at + len as u64).min(end).saturating_sub(at.max(start))
}

#[test]
fn dependencies_and_generated_ciphertext_survive_preparation_batches() {
    let (_dir, _config, mut store) = fixture(Some("prepare-cache-test"));
    let page = sealed_page(&mut store);
    store.configure_prepare_cache(64).unwrap();
    let mut oid = 0;
    for key in [0, 64, 128, 192] {
        oid = append_metadata(&mut store, oid, &page, key).0;
    }
    let object = store.object(oid).unwrap();
    assert!(object.used > 4 && !object.sealed);
    assert_eq!(object.external_count, 1);
    assert_eq!(
        count(&store, "prepare_dependency_loads"),
        0,
        "a locally built dependency table was reloaded between commits"
    );
    store.device.events.lock().unwrap().clear();
    let sealed = store.transaction(|tx| tx.seal(oid, None)).unwrap();
    assert_eq!(
        count(&store, "prepare_seal_cached_pages"),
        object.used as u64 - 1
    );
    let g = store.geometry();
    let start = object.extent * g.object_size + (g.payload_pages + 1) * PAGE as u64;
    let end = object.extent * g.object_size + (g.payload_pages + object.used as u64) * PAGE as u64;
    let events = store.device.events.lock().unwrap().clone();
    assert_eq!(
        events
            .iter()
            .filter(|(write, _, _)| !write)
            .map(|(_, at, len)| overlap(*at, *len, start, end))
            .sum::<u64>(),
        0
    );
    assert_eq!(
        events
            .iter()
            .filter(|(write, _, _)| *write)
            .map(|(_, at, len)| overlap(*at, *len, start, end))
            .sum::<u64>(),
        0,
        "sealing rewrote immutable metadata payload"
    );
    let raw = store.verify_object(&sealed).unwrap();
    assert_eq!(codec::hash(&raw), sealed.sha);
    assert!(count(&store, "prepare_cache_used_bytes") <= 64 << 20);
    let hits = count(&store, "prepare_cache_hits");
    store.end_prepare_cache();
    assert_eq!(count(&store, "prepare_cache_used_bytes"), 0);
    assert_eq!(
        count(&store, "prepare_cache_hits"),
        hits,
        "end erased the task diagnostics"
    );
}

#[test]
fn reopened_metadata_tail_reads_and_authenticates_instead_of_trusting_cache() {
    let (dir, config, mut store) = fixture(Some("reopen-cache-test"));
    let page = sealed_page(&mut store);
    store.configure_prepare_cache(64).unwrap();
    let (oid, _) = append_metadata(&mut store, 0, &page, 0);
    let object = store.object(oid).unwrap();
    drop(store);
    let path = dir.path().join("prepare.odv4");
    let broken = dir.path().join("broken.odv4");
    std::fs::copy(&path, &broken).unwrap();
    let mut store = Store::open(
        Device::open(&path, false).unwrap(),
        config.clone(),
        config.unlock(Some("reopen-cache-test")).unwrap(),
    )
    .unwrap();
    store.configure_prepare_cache(16).unwrap();
    let sealed = store.transaction(|tx| tx.seal(oid, None)).unwrap();
    assert_eq!(count(&store, "prepare_seal_cached_pages"), 0);
    assert!(store.diagnostics["seal_validated_pages"] >= object.used as u64 - 1);
    store.verify_object(&sealed).unwrap();
    drop(store);
    let mut damaged = Store::open(
        Device::open(&broken, false).unwrap(),
        config.clone(),
        config.unlock(Some("reopen-cache-test")).unwrap(),
    )
    .unwrap();
    damaged.configure_prepare_cache(16).unwrap();
    let offset = object.extent * damaged.object_size()
        + (damaged.geometry().payload_pages + 1) * PAGE as u64;
    let mut frame = [0; PAGE];
    damaged.device.read(offset, &mut frame).unwrap();
    frame[91] ^= 0x80;
    damaged.device.write(offset, &frame).unwrap();
    assert!(damaged.transaction(|tx| tx.seal(oid, None)).is_err());
    assert_eq!(
        count(&damaged, "prepare_cache_used_bytes"),
        0,
        "failed authentication left cached transaction state reusable"
    );
}

#[test]
fn failed_flush_discards_staged_dependencies_and_ciphertext() {
    let (dir, config, mut store) = fixture(None);
    let page = sealed_page(&mut store);
    store.configure_prepare_cache(16).unwrap();
    let (oid, _) = append_metadata(&mut store, 0, &page, 0);
    let before = store.object(oid).unwrap();
    let sequence = store.root.seq;
    store.device.fail_sync.store(true, Ordering::Release);
    let failed = store.transaction(|tx| {
        tx.portable = Some(oid);
        tree::set_many(
            tx,
            &PORTMAP,
            MetaRef::default(),
            &[(64, Some(page.encode()))],
        )?;
        tx.portable = None;
        Ok(())
    });
    assert!(failed.is_err());
    assert_eq!(count(&store, "prepare_cache_used_bytes"), 0);
    drop(store);
    let mut reopened = Store::open(
        Device::open(&dir.path().join("prepare.odv4"), false).unwrap(),
        config.clone(),
        config.unlock(None).unwrap(),
    )
    .unwrap();
    assert_eq!(reopened.root.seq, sequence);
    assert_eq!(reopened.object(oid).unwrap().used, before.used);
    reopened.configure_prepare_cache(16).unwrap();
    let sealed = reopened.transaction(|tx| tx.seal(oid, None)).unwrap();
    reopened.verify_object(&sealed).unwrap();
    assert_eq!(count(&reopened, "prepare_seal_cached_pages"), 0);
}

fn leaf(spec: &Spec) -> Vec<u8> {
    let mut bytes = vec![0; 32 + spec.value_size];
    bytes[..4].copy_from_slice(b"ODTR");
    bytes[4] = 2;
    bytes[5] = spec.tag;
    bytes[6] = spec.leaf_bits;
    bytes[8..10].copy_from_slice(&(spec.value_size as u16).to_le_bytes());
    bytes[24..32].copy_from_slice(&1u64.to_le_bytes());
    bytes[32] = 1;
    bytes
}

#[test]
fn index_prefetch_reads_only_requested_pages_in_bounded_adjacent_runs() {
    let (dir, config, mut store) = fixture(None);
    let payload = leaf(&DIRTY);
    let references = store
        .transaction(|tx| {
            (0..140)
                .map(|_| tx.write_node(&payload))
                .collect::<Result<Vec<_>>>()
        })
        .unwrap();
    drop(store);
    let mut store = Store::open(
        Device::open(&dir.path().join("prepare.odv4"), false).unwrap(),
        config.clone(),
        config.unlock(None).unwrap(),
    )
    .unwrap();
    store.configure_prepare_cache(16).unwrap();
    let requested = references
        .iter()
        .enumerate()
        .filter(|(i, _)| *i != 67)
        .map(|(_, r)| *r)
        .collect::<Vec<_>>();
    store.device.events.lock().unwrap().clear();
    store.prefetch_nodes(&requested).unwrap();
    let reads = store.device.events.lock().unwrap().clone();
    assert_eq!(
        reads.iter().map(|(_, _, length)| *length).sum::<usize>(),
        requested.len() * PAGE
    );
    assert!(reads
        .iter()
        .all(|(write, _, length)| !write && *length <= 256 * 1024));
    assert!(
        reads.len() < requested.len() / 2,
        "prefetch did not combine adjacent pages"
    );
    assert!(reads.iter().all(|(_, at, len)| overlap(
        *at,
        *len,
        references[67].offset,
        references[67].offset + PAGE as u64
    ) == 0));
    for reference in &requested {
        assert_eq!(
            store.read_decoded(&DIRTY, *reference).unwrap(),
            tree::decode(&DIRTY, &payload).unwrap()
        );
        assert!(
            store.read_decoded(&PORTMAP, *reference).is_err(),
            "parsed cache ignored the tree specification"
        );
        store.read_decoded(&DIRTY, *reference).unwrap();
    }
    assert_eq!(store.device.events.lock().unwrap().len(), reads.len());
    // After reopening, the same prefetched path must reject a damaged frame.
    let r = requested[0];
    let mut frame = [0; PAGE];
    store.device.read(r.offset, &mut frame).unwrap();
    frame[110] ^= 1;
    store.device.write(r.offset, &frame).unwrap();
    drop(store);
    let mut store = Store::open(
        Device::open(&dir.path().join("prepare.odv4"), false).unwrap(),
        config.clone(),
        config.unlock(None).unwrap(),
    )
    .unwrap();
    store.configure_prepare_cache(16).unwrap();
    assert!(store.prefetch_nodes(&[r]).is_err());
}

#[test]
fn replaced_unwritten_control_blobs_are_not_written_and_commit_still_flushes_three_times() {
    let (_dir, _config, mut store) = fixture(None);
    store.configure_prepare_cache(16).unwrap();
    let before = store.device.diagnostics()["physical_sync_calls"];
    store.device.events.lock().unwrap().clear();
    let (obsolete, live) = store
        .transaction(|tx| {
            let old = tx.blob(&vec![12; 12_000])?;
            assert_eq!(tx.read_blob(old)?, vec![12; 12_000]);
            let next = tx.replace_blob(old, &vec![91; 12_000])?;
            assert_eq!(tx.read_blob(next)?, vec![91; 12_000]);
            Ok((old, next))
        })
        .unwrap();
    assert_eq!(store.blob(live).unwrap(), vec![91; 12_000]);
    assert!(count(&store, "prepare_skipped_metadata_write_pages") >= 4);
    assert_eq!(
        store.device.diagnostics()["physical_sync_calls"] - before,
        3
    );
    assert!(store
        .device
        .events
        .lock()
        .unwrap()
        .iter()
        .filter(|(write, _, _)| *write)
        .all(
            |(_, at, len)| overlap(*at, *len, obsolete.offset, obsolete.offset + PAGE as u64) == 0
        ));
}

#[test]
fn unified_lru_budget_keeps_hot_entries_and_never_exposes_future_epochs() {
    let mut cache = super::prepare_cache::Cache::default();
    assert!(cache.configure(15).is_err());
    assert!(cache.configure(1025).is_err());
    cache.configure(16).unwrap();
    let hot = MetaRef {
        offset: 4096,
        hash: [1; 32],
    };
    cache.put_node(hot, 7, vec![2; 4000]);
    assert!(cache.node(hot, 6).is_none());
    for n in 2..6000 {
        let reference = MetaRef {
            offset: n * PAGE as u64,
            hash: codec::hash(&n.to_le_bytes()),
        };
        cache.put_node(reference, 7, vec![3; 4000]);
        assert_eq!(cache.node(hot, 7).unwrap()[0], 2);
        assert!(cache.metrics()["prepare_cache_used_bytes"] <= 16 << 20);
    }
    assert!(cache.metrics()["prepare_cache_evictions"] > 1000);
    assert!(cache.contains_node(hot, 7));
    let hits = cache.metrics()["prepare_cache_hits"];
    cache.configure(16).unwrap();
    assert_eq!(cache.metrics()["prepare_cache_hits"], hits);
    cache.end();
    assert_eq!(cache.metrics()["prepare_cache_used_bytes"], 0);
    assert!(
        cache.contains_node(hot, 7),
        "ending preparation discarded the foreground node cache"
    );
}
