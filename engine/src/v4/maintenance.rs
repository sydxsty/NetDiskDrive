//! Incremental catalog statistics and resumable compaction. Queries read only
//! statistics roots and the requested index page, never all file/page mappings.
use super::cloud::Compact;
use super::store::{Object, Root, Store, Txn, BLOCKS, OBJECTS};
use super::tree::{self, Spec, Storage};
use super::{Error, Geometry, MetaRef, Result};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::{BTreeMap, BTreeSet};

const EXTENT_BITS: u32 = 42;
const EXTENT_MASK: u64 = (1u64 << EXTENT_BITS) - 1;
const CATEGORIES: usize = 35;
const TYPE_COUNT: usize = 6;
const PAIR_START: usize = 5 + TYPE_COUNT;
const STATUS_NAMES: [&str; 4] = ["local", "pending", "uploaded", "synced"];
const TYPE_NAMES: [&str; TYPE_COUNT] = [
    "data",
    "ntfs_mft",
    "ntfs_log",
    "ntfs_usn",
    "ntfs_metadata",
    "container_index",
];
const INDEX: Spec = Spec {
    tag: 15,
    leaf_bits: 5,
    depth: 8,
    value_size: 1,
    tracked: false,
};

#[derive(Clone, Serialize, Deserialize)]
struct Statistics {
    version: u32,
    index: MetaRef,
    counts: Vec<u64>,
    missing_counts: Vec<u64>,
}

#[cfg(test)]
mod tests {
    use super::super::{codec::Config, io::Device, Volume};
    use super::*;
    use std::sync::{mpsc, Arc};
    use std::time::Duration;

    fn fresh(path: &std::path::Path) -> Store {
        let (config, crypto) = Config::create(64 * 1024 * 1024, None).unwrap();
        Store::create(Device::open(path, true).unwrap(), config, crypto).unwrap()
    }
    fn fixture_objects(store: &mut Store, count: u64) -> Vec<Object> {
        store
            .transaction(|tx| {
                let mut objects = Vec::new();
                for i in 0..count {
                    let kind = i as usize % TYPE_COUNT;
                    let mut object = tx.allocate_object(
                        if kind == 5 { 2 } else { 1 },
                        if kind == 5 { 0 } else { kind as u8 },
                    )?;
                    object.sync_state = ((i / TYPE_COUNT as u64) % 4) as u8;
                    object.refs = 1;
                    object.current_refs = u64::from(object.kind == 1);
                    object.cloud_refs = u64::from(object.kind == 2);
                    tx.objects.insert(object.oid, object.clone());
                    objects.push(object);
                }
                Ok(objects)
            })
            .unwrap()
    }

    #[test]
    fn catalog_statistics_rank_filters_runtime_overlay_and_reopen() {
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("statistics.odv4");
        let mut store = fresh(&path);
        let mut objects = fixture_objects(&mut store, 96);
        assert_eq!(store.debug_summary().unwrap()["pending_objects"], 24);
        // Snapshot-only historical data is retained locally, but is not a pending upload.
        let historical = objects
            .iter_mut()
            .find(|o| o.kind == 1 && o.sync_state == 1)
            .unwrap();
        historical.current_refs = 0;
        let historic_oid = historical.oid;
        store
            .transaction(|tx| {
                tx.objects.insert(historic_oid, historical.clone());
                Ok(())
            })
            .unwrap();
        assert_eq!(store.debug_summary().unwrap()["pending_objects"], 23);
        let transient: Vec<_> = objects
            .iter()
            .filter(|o| effective_state(o).unwrap() == 1)
            .take(3)
            .cloned()
            .collect();
        for (index, object) in transient.iter().enumerate() {
            store.cloud.transfer.insert(
                object.oid,
                if index == 2 {
                    "failed".into()
                } else {
                    "uploading".into()
                },
            );
        }
        let summary = store.debug_summary().unwrap();
        assert_eq!(summary["counts"]["uploading"], 2);
        assert_eq!(summary["counts"]["failed"], 1);
        assert_eq!(summary["counts"]["pending"], 20);
        assert_eq!(summary["pending_objects"], 23);
        for state in STATUS_NAMES.into_iter().map(Some).chain([None]) {
            for kind in TYPE_NAMES.into_iter().map(Some).chain([None]) {
                if state.is_none() && kind.is_none() {
                    continue;
                }
                let expected: Vec<_> = objects
                    .iter()
                    .filter(|object| {
                        let status = store
                            .cloud
                            .transfer
                            .get(&object.oid)
                            .map(String::as_str)
                            .unwrap_or(STATUS_NAMES[effective_state(object).unwrap()]);
                        state.is_none_or(|wanted| wanted == status)
                            && kind.is_none_or(|wanted| wanted == TYPE_NAMES[content_type(object)])
                    })
                    .map(|o| o.extent)
                    .collect();
                for start in [0, 3, expected.len() as u64, 200] {
                    let result = store.debug_query(&json!({"start":start,"limit":7,"sync_state":state,"content_type":kind})).unwrap();
                    assert_eq!(
                        result["total_count"].as_u64().unwrap(),
                        expected.len() as u64
                    );
                    let actual: Vec<_> = result["items"]
                        .as_array()
                        .unwrap()
                        .iter()
                        .map(|v| v["index"].as_u64().unwrap())
                        .collect();
                    assert_eq!(
                        actual,
                        expected
                            .iter()
                            .skip(start as usize)
                            .take(7)
                            .copied()
                            .collect::<Vec<_>>()
                    );
                }
            }
        }
        let config = store.config.clone();
        drop(store);
        let mut reopened = Store::open(
            Device::open(&path, false).unwrap(),
            config.clone(),
            config.unlock(None).unwrap(),
        )
        .unwrap();
        let summary = reopened.debug_summary().unwrap();
        assert_eq!(summary["objects"], 96);
        assert_eq!(summary["pending_objects"], 23);
        assert_eq!(summary["counts"]["uploading"], 0);
        assert_eq!(
            reopened
                .debug_query(&json!({"start":0,"limit":512,"content_type":"ntfs_mft"}))
                .unwrap()["total_count"],
            16
        );
    }

    #[test]
    fn compact_pause_reopen_protects_published_objects_and_updates_statistics() {
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("compact.odv4");
        let mut store = fresh(&path);
        let objects = store
            .transaction(|tx| {
                let mut objects = Vec::new();
                for _ in 0..12 {
                    objects.push(tx.allocate_object(2, 0)?);
                }
                Ok(objects)
            })
            .unwrap();
        let mut cloud = store.cloud.clone();
        store
            .transaction(|tx| {
                cloud.published_counts = tx.set(
                    &super::super::store::COUNTS,
                    MetaRef::default(),
                    &[(objects[4].oid, Some(1u64.to_le_bytes().to_vec()))],
                )?;
                tx.save_cloud(&cloud)
            })
            .unwrap();
        store.cloud = cloud;
        store.compact_start("normal").unwrap();
        store.compact_step(3).unwrap();
        store.compact_pause(true).unwrap();
        let before = store.compact_status().unwrap();
        assert_eq!(store.compact_step(32).unwrap(), before);
        store.compact_pause(false).unwrap(); // A running task is paused automatically on reopen.
        let cursor = store.compact_status().unwrap()["cursor"].clone();
        let config = store.config.clone();
        drop(store);
        let mut store = Store::open(
            Device::open(&path, false).unwrap(),
            config.clone(),
            config.unlock(None).unwrap(),
        )
        .unwrap();
        assert_eq!(store.compact_status().unwrap()["state"], "paused");
        assert_eq!(store.compact_status().unwrap()["cursor"], cursor);
        store.compact_pause(false).unwrap();
        for _ in 0..40 {
            if store.compact_step(3).unwrap()["state"] == "done" {
                break;
            }
        }
        assert_eq!(store.compact_status().unwrap()["state"], "done");
        assert!(store.object(objects[4].oid).is_ok());
        assert_eq!(store.debug_summary().unwrap()["objects"], 1);
        for object in objects.iter().filter(|o| o.oid != objects[4].oid) {
            assert!(store.object(object.oid).is_err());
        }
    }

    #[test]
    fn readonly_debug_queries_do_not_wait_for_the_store_writer_mutex() {
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("query.odv4");
        Volume::create(&path, 64 * 1024 * 1024, None).unwrap();
        let volume = Arc::new(Volume::open(&path, None).unwrap());
        volume.write(0, &[71; 4096]).unwrap();
        volume.flush().unwrap();
        let guard = volume.shared.store.lock().unwrap();
        volume.write(4096, &[72; 4096]).unwrap(); // Remains a RAM page version while Store is locked.
        let (sent, received) = mpsc::channel();
        let cloned = volume.clone();
        let query = std::thread::spawn(move || {
            let summary = cloned.debug_control(&json!({"cmd":"blocks.summary"}));
            let blocks = cloned.debug_control(&json!({"cmd":"blocks.query","start":0,"limit":16}));
            sent.send((summary, blocks)).unwrap();
        });
        let result = received.recv_timeout(Duration::from_secs(5));
        drop(guard);
        query.join().unwrap();
        let (summary, blocks) = result.expect("debug query waited for StoreMutex");
        let summary = summary.unwrap();
        assert!(summary["objects"].as_u64().unwrap() > 0);
        assert!(summary["ram_pending_pages"].as_u64().unwrap() > 0);
        assert_eq!(summary["pending_bytes_estimated"], true);
        assert!(!blocks.unwrap()["items"].as_array().unwrap().is_empty());
        volume.flush().unwrap();
        let settled = volume
            .debug_control(&json!({"cmd":"blocks.summary"}))
            .unwrap();
        let unchanged = volume
            .debug_control(&json!({"cmd":"blocks.changes","since_revision":settled["revision"]}))
            .unwrap();
        assert_eq!(unchanged["changed"], false);
    }

    #[test]
    fn deep_compaction_preserves_current_and_snapshot_content() {
        use super::super::store::PAGES;
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("deep.odv4");
        let mut store = fresh(&path);
        store
            .transaction(|tx| {
                let mut changes = Vec::new();
                for page in 0..32 {
                    let p = tx.append_page(page, &[page as u8; super::super::PAGE], 0)?;
                    changes.push((page, Some(p.encode())));
                }
                tx.root.index = tx.set(&PAGES, tx.root.index, &changes)?;
                tx.root.allocated_pages = 32;
                tx.root.data_generation += 1;
                tx.seal_tails()
            })
            .unwrap();
        let snapshot = super::super::cloud::Snapshot {
            id: uuid::Uuid::new_v4().to_string(),
            name: "Before".into(),
            index: store.root.index,
            generation: store.root.data_generation,
            created_utc: "1".into(),
            pin: "user".into(),
            target_volume_id: None,
        };
        store
            .transaction(|tx| {
                tx.replace(&PAGES, MetaRef::default(), snapshot.index);
                tx.save_snapshots(std::slice::from_ref(&snapshot))
            })
            .unwrap();
        store.snapshots.push(snapshot.clone());
        store
            .transaction(|tx| {
                let mut changes = Vec::new();
                for page in 0..16 {
                    let p = tx.append_page(page, &[201; super::super::PAGE], 0)?;
                    changes.push((page, Some(p.encode())));
                }
                tx.root.index = tx.set(&PAGES, tx.root.index, &changes)?;
                tx.root.data_generation += 1;
                tx.seal_tails()
            })
            .unwrap();
        store.compact_start("deep").unwrap();
        for _ in 0..60 {
            if store.compact_step(2).unwrap()["state"] == "done" {
                break;
            }
        }
        assert_eq!(store.compact_status().unwrap()["state"], "done");
        for page in 0..32 {
            assert_eq!(
                store.read_page(store.root.index, page).unwrap(),
                [if page < 16 { 201 } else { page as u8 }; super::super::PAGE]
            );
            assert_eq!(
                store.read_page(snapshot.index, page).unwrap(),
                [page as u8; super::super::PAGE]
            );
        }
        let config = store.config.clone();
        drop(store);
        let mut store = Store::open(
            Device::open(&path, false).unwrap(),
            config.clone(),
            config.unlock(None).unwrap(),
        )
        .unwrap();
        assert_eq!(
            store.read_page(store.root.index, 0).unwrap(),
            [201; super::super::PAGE]
        );
        assert_eq!(
            store.read_page(snapshot.index, 0).unwrap(),
            [0; super::super::PAGE]
        );
        assert_eq!(store.debug_summary().unwrap()["pending_objects"], 0); // Not bound to a cloud.
    }

    #[test]
    fn reclamation_waits_for_old_read_lease_and_zero_physical_bytes_do_not_end_the_scan() {
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("lease.odv4");
        let mut store = fresh(&path);
        store
            .transaction(|tx| {
                for _ in 0..65 {
                    tx.allocate_object(2, 0)?;
                }
                Ok(())
            })
            .unwrap();
        let lease = super::super::store::ReadLease::acquire(store.readers.clone()).unwrap();
        store.compact_start("normal").unwrap();
        for _ in 0..10 {
            store.compact_step(32).unwrap();
        }
        assert_eq!(store.compact_status().unwrap()["phase"], "scan");
        assert_eq!(store.compact_status().unwrap()["state"], "running");
        assert_eq!(store.debug_summary().unwrap()["objects"], 65);
        drop(lease);
        for _ in 0..15 {
            if store.compact_step(32).unwrap()["state"] == "done" {
                break;
            }
        }
        assert_eq!(store.compact_status().unwrap()["state"], "done");
        assert!(!store.has_decommit_pending().unwrap());
        assert_eq!(store.debug_summary().unwrap()["objects"], 0);
    }

    #[test]
    fn preparing_upload_pin_is_not_misreported_as_a_user_snapshot() {
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("pins.odv4");
        Volume::create(&path, 64 * 1024 * 1024, None).unwrap();
        let volume = Volume::open(&path, None).unwrap();
        volume.write(0, &[18; 4096]).unwrap();
        volume.flush().unwrap();
        let extent = {
            let mut store = volume.shared.store.lock().unwrap();
            let root = store.root.index;
            let page = store.page(root, 0).unwrap().unwrap();
            store.object(page.reference.object).unwrap().extent
        };
        volume
            .control(&json!({"cmd":"cloud.bind","binding":{"provider":"test"}}))
            .unwrap();
        volume.control(&json!({"cmd":"cloud.prepare"})).unwrap();
        volume.write(0, &[29; 4096]).unwrap();
        volume.flush().unwrap();
        let before = volume
            .debug_control(&json!({"cmd":"blocks.query","start":extent,"limit":1}))
            .unwrap();
        assert_eq!(before["items"][0]["snapshot_pinned"], false);
        assert!(before["items"][0]["upload_pinned"].is_null());
        volume.snapshot_create().unwrap(); // This user snapshot contains the newer page.
        let after = volume
            .debug_control(&json!({"cmd":"blocks.query","start":extent,"limit":1}))
            .unwrap();
        assert!(after["items"][0]["snapshot_pinned"].is_null());
    }
}
impl Default for Statistics {
    fn default() -> Self {
        Self {
            version: 1,
            index: MetaRef::default(),
            counts: vec![0; CATEGORIES],
            missing_counts: vec![0; CATEGORIES],
        }
    }
}
impl Statistics {
    fn validate(&self) -> Result<()> {
        if self.counts.len() != CATEGORIES || self.counts.iter().any(|count| *count > EXTENT_MASK) {
            return Err(integrity("statistics bounds"));
        }
        if self.version != 1
            || self.counts[1..5].iter().sum::<u64>() != self.counts[0]
            || self.counts[5..PAIR_START].iter().sum::<u64>() != self.counts[0]
            || self.counts[PAIR_START..].iter().sum::<u64>() != self.counts[0]
        {
            return Err(integrity("statistics cardinality mismatch"));
        }
        for state in 0..4 {
            if (0..TYPE_COUNT)
                .map(|kind| self.counts[PAIR_START + state * TYPE_COUNT + kind])
                .sum::<u64>()
                != self.counts[1 + state]
            {
                return Err(integrity("statistics state/type count mismatch"));
            }
        }
        if self.missing_counts.len() != CATEGORIES
            || self.missing_counts.iter().any(|n| *n > EXTENT_MASK)
            || self.missing_counts[1..5].iter().sum::<u64>() != self.missing_counts[0]
            || self.missing_counts[5..PAIR_START].iter().sum::<u64>() != self.missing_counts[0]
            || self.missing_counts[PAIR_START..].iter().sum::<u64>() != self.missing_counts[0]
        {
            return Err(integrity("nonresident statistics cardinality"));
        }
        Ok(())
    }
    fn before(&self, category: usize) -> u64 {
        self.counts[..category].iter().sum()
    }
}
fn integrity(message: &str) -> Error {
    Error::Integrity(message.into())
}
fn invalid(message: &str) -> Error {
    Error::Invalid(message.into())
}
fn content_type(object: &Object) -> usize {
    if object.kind == 1 {
        object.pool.min(4) as usize
    } else {
        5
    }
}

fn effective_state(object: &Object) -> Result<usize> {
    if object.sync_state > 3 {
        return Err(integrity("object sync state"));
    }
    // Historical/local-only objects do not contribute pending network bytes.
    if object.sync_state == 1 && object.current_refs == 0 && object.cloud_refs == 0 {
        Ok(0)
    } else {
        Ok(object.sync_state as usize)
    }
}
fn categories(object: &Object) -> Result<[usize; 4]> {
    if object.sync_state > 3 || object.extent > EXTENT_MASK {
        return Err(integrity("object statistics identity"));
    }
    let state = effective_state(object)?;
    let kind = content_type(object);
    Ok([
        0,
        1 + state,
        5 + kind,
        PAIR_START + state * TYPE_COUNT + kind,
    ])
}
fn index_key(category: usize, extent: u64) -> u64 {
    ((category as u64) << EXTENT_BITS) | extent
}
fn read_statistics<C: CatalogRead>(store: &mut C, reference: MetaRef) -> Result<Statistics> {
    let stats: Statistics = if reference.empty() {
        Statistics::default()
    } else {
        serde_json::from_slice(&store.catalog_blob(reference)?)?
    };
    stats.validate()?;
    Ok(stats)
}

pub(super) fn validate_statistics(store: &mut Store) -> Result<()> {
    if store.root.stats.empty() {
        return Ok(());
    }
    let stats: Statistics = serde_json::from_slice(&store.blob(store.root.stats)?)?;
    stats.validate()
}

/// Call once per final before/after object descriptor in the root transaction.
/// Includes creation, free, physical relocation, and persisted sync-state changes.
/// Reference-count-only edits do not rewrite any statistic or secondary index.
pub(super) fn apply_object_changes(
    tx: &mut Txn<'_>,
    changes: &[(Option<Object>, Option<Object>)],
) -> Result<()> {
    let mut stats = read_statistics(tx.store, tx.root.stats)?;
    let mut rows = BTreeMap::new();
    let mut missing_changed = false;
    for (before, after) in changes {
        let missing_before = before.as_ref().filter(|o| o.missing);
        let missing_after = after.as_ref().filter(|o| o.missing);
        let missing_identity =
            |o: &Object| -> Result<_> { Ok((effective_state(o)?, content_type(o))) };
        if missing_before.map(missing_identity).transpose()?
            != missing_after.map(missing_identity).transpose()?
        {
            missing_changed = true;
            for (o, adding) in missing_before
                .iter()
                .map(|o| (o, false))
                .chain(missing_after.iter().map(|o| (o, true)))
            {
                for category in categories(o)? {
                    stats.missing_counts[category] = if adding {
                        stats.missing_counts[category].checked_add(1)
                    } else {
                        stats.missing_counts[category].checked_sub(1)
                    }
                    .ok_or_else(|| integrity("nonresident statistics underflow"))?;
                }
            }
        }
        let before = before.as_ref().filter(|o| !o.missing);
        let after = after.as_ref().filter(|o| !o.missing);
        let identity = |object: &Object| -> Result<_> {
            Ok((
                object.extent,
                effective_state(object)?,
                content_type(object),
            ))
        };
        if before.map(identity).transpose()? == after.map(identity).transpose()? {
            continue;
        }
        for (object, adding) in before
            .iter()
            .map(|o| (o, false))
            .chain(after.iter().map(|o| (o, true)))
        {
            for category in categories(object)? {
                stats.counts[category] = if adding {
                    stats.counts[category].checked_add(1)
                } else {
                    stats.counts[category].checked_sub(1)
                }
                .ok_or_else(|| integrity("statistics counter overflow/underflow"))?;
                rows.insert(index_key(category, object.extent), adding.then(|| vec![1]));
            }
        }
    }
    if rows.is_empty() && !missing_changed {
        return Ok(());
    }
    stats.validate()?;
    if !rows.is_empty() {
        stats.index = tx.set(&INDEX, stats.index, &rows.into_iter().collect::<Vec<_>>())?;
    }
    tx.root.stats = tx.replace_blob(tx.root.stats, &serde_json::to_vec(&stats)?)?;
    Ok(())
}

#[derive(Clone)]
struct Overlay {
    object: Object,
    state: String,
}

trait CatalogRead: Storage {
    fn catalog_geometry(&self) -> Geometry;
    fn catalog_blob(&mut self, reference: MetaRef) -> Result<Vec<u8>>;
    fn catalog_object(&mut self, oid: u64) -> Result<Object>;
    fn catalog_len(&self) -> Result<u64>;
}
impl CatalogRead for Store {
    fn catalog_geometry(&self) -> Geometry {
        self.geometry()
    }
    fn catalog_blob(&mut self, r: MetaRef) -> Result<Vec<u8>> {
        self.blob(r)
    }
    fn catalog_object(&mut self, oid: u64) -> Result<Object> {
        self.object(oid)
    }
    fn catalog_len(&self) -> Result<u64> {
        self.device.len()
    }
}
impl CatalogRead for super::volume::Reader {
    fn catalog_geometry(&self) -> Geometry {
        self.geometry()
    }
    fn catalog_blob(&mut self, r: MetaRef) -> Result<Vec<u8>> {
        self.blob(r)
    }
    fn catalog_object(&mut self, oid: u64) -> Result<Object> {
        self.object(oid)
    }
    fn catalog_len(&self) -> Result<u64> {
        self.device_len()
    }
}
struct DebugView<'a, C: CatalogRead> {
    reader: &'a mut C,
    root: Root,
    cloud: super::cloud::Cloud,
    has_snapshots: bool,
}
impl<C: CatalogRead> Storage for DebugView<'_, C> {
    fn read_node(&mut self, r: MetaRef) -> Result<Vec<u8>> {
        self.reader.read_node(r)
    }
    fn write_node(&mut self, _: &[u8]) -> Result<MetaRef> {
        Err(invalid("immutable debug view"))
    }
}
impl<C: CatalogRead> CatalogRead for DebugView<'_, C> {
    fn catalog_geometry(&self) -> Geometry {
        self.reader.catalog_geometry()
    }
    fn catalog_blob(&mut self, r: MetaRef) -> Result<Vec<u8>> {
        self.reader.catalog_blob(r)
    }
    fn catalog_object(&mut self, oid: u64) -> Result<Object> {
        self.reader.catalog_object(oid)
    }
    fn catalog_len(&self) -> Result<u64> {
        self.reader.catalog_len()
    }
}
impl super::Volume {
    pub(super) fn debug_control(&self, request: &Value) -> Result<Value> {
        let (lease, mut reader) = self.read_context()?;
        let ram_pending_pages = self.local_dirty_pages();
        let revision = format!(
            "{}:{}:{}",
            lease.root.revision, lease.volatile_revision, ram_pending_pages
        );
        let cmd = request["cmd"].as_str().unwrap_or("");
        if matches!(cmd, "blocks.changes" | "debug.changes") {
            let changed = request["since_revision"] != json!(revision);
            return Ok(json!({"revision":revision,"changed":changed,"reset_required":changed}));
        }
        let mut cloud: super::cloud::Cloud = if lease.root.cloud.empty() {
            Default::default()
        } else {
            serde_json::from_slice(&reader.blob(lease.root.cloud)?)?
        };
        cloud.transfer = lease
            .transfer
            .iter()
            .map(|(oid, state)| (*oid, state.clone()))
            .collect();
        let mut view = DebugView {
            reader: &mut reader,
            root: lease.root.clone(),
            cloud,
            has_snapshots: lease.root.snapshot_count != 0,
        };
        let mut result = match cmd {
            "debug.summary" | "blocks.summary" => view.debug_summary()?,
            "debug.query" | "blocks.query" | "debug.blocks" => view.debug_query(request)?,
            _ => return Err(invalid("unsupported debug query")),
        };
        result["revision"] = json!(revision);
        if matches!(cmd, "debug.summary" | "blocks.summary") {
            result["ram_pending_pages"] = json!(ram_pending_pages);
            result["pending_bytes_estimated"] = json!(ram_pending_pages != 0);
            result["new_changes"]["ram_pending_pages"] = json!(ram_pending_pages);
        }
        // Lease stays alive until every queried frame has been read; physical
        // relocation/free never invalidates this immutable registry root.
        drop(lease);
        Ok(result)
    }
}
impl<C: CatalogRead> DebugView<'_, C> {
    fn geometry(&self) -> Geometry {
        self.reader.catalog_geometry()
    }
    fn overlays(&mut self) -> Result<Vec<Overlay>> {
        let entries: Vec<_> = self
            .cloud
            .transfer
            .iter()
            .filter(|(_, state)| matches!(state.as_str(), "uploading" | "failed"))
            .map(|(oid, state)| (*oid, state.clone()))
            .collect();
        let mut out = Vec::with_capacity(entries.len());
        for (oid, state) in entries {
            let root = self.root.objects;
            if let Some(value) = tree::get(self, &OBJECTS, root, oid)? {
                out.push(Overlay {
                    object: Object::decode(oid, &value)?,
                    state,
                });
            }
        }
        out.sort_by_key(|entry| entry.object.extent);
        Ok(out)
    }

    pub fn debug_summary(&mut self) -> Result<Value> {
        let g = self.geometry();
        let stats = read_statistics(self, self.root.stats)?;
        let overlays = self.overlays()?;
        let mut counts = BTreeMap::new();
        for (state, name) in STATUS_NAMES.iter().enumerate() {
            counts.insert(
                *name,
                stats.counts[1 + state] + stats.missing_counts[1 + state],
            );
        }
        counts.insert("uploading", 0);
        counts.insert("failed", 0);
        let pending_types = &stats.counts[PAIR_START + TYPE_COUNT..PAIR_START + 2 * TYPE_COUNT];
        for entry in &overlays {
            let source = STATUS_NAMES
                .get(effective_state(&entry.object)?)
                .ok_or_else(|| integrity("runtime object state"))?;
            let value = counts.get_mut(source).unwrap();
            *value = value
                .checked_sub(1)
                .ok_or_else(|| integrity("runtime statistics underflow"))?;
            *counts.get_mut(entry.state.as_str()).unwrap() += 1;
        }
        let pending_objects = stats.counts[2] + stats.missing_counts[2];
        let current_job = if let Some(job) = &self.cloud.job {
            let add = job.add;
            let receipts = job.receipts;
            let phase = job.phase.clone();
            let generation = job.generation;
            let additions = tree::len(self, &super::store::SET, add)?;
            let confirmed = tree::len(self, &super::store::SET, receipts)?;
            let remaining = additions
                .checked_sub(confirmed)
                .ok_or_else(|| integrity("receipt cardinality exceeds additions"))?;
            json!({"phase":phase,"generation":generation,"pending_objects":remaining,"pending_bytes":remaining*g.object_size})
        } else {
            json!({"pending_objects":0,"pending_bytes":0})
        };
        let types: Vec<_> = TYPE_NAMES.iter().enumerate().map(|(kind, name)| json!({
            "id":name,"objects":stats.counts[5+kind]+stats.missing_counts[5+kind],"pending_objects":pending_types[kind]+stats.missing_counts[PAIR_START+TYPE_COUNT+kind],"pending_bytes":(pending_types[kind]+stats.missing_counts[PAIR_START+TYPE_COUNT+kind])*g.object_size
        })).collect();
        let new_dirty = tree::len(self, &super::store::DIRTY, self.root.dirty)?;
        Ok(
            json!({"object_size":g.object_size,"revision":self.root.revision,"total_blocks":self.reader.catalog_len()?/g.object_size,
            "objects":stats.counts[0]+stats.missing_counts[0],"resident_objects":stats.counts[0],"missing_objects":stats.missing_counts[0],"counts":counts,"types":types,
            "index_complete":self.cloud.replica.as_ref().is_none_or(|r|r.counts_complete),
            "pending_objects":pending_objects,"pending_bytes":pending_objects*g.object_size,"pending_resident_objects":stats.counts[2],"pending_missing_objects":stats.missing_counts[2],
            "changed_pages":self.root.changed_pages,"current_job":current_job,
            "new_changes":{"changed_pages":new_dirty,"logical_changed_bytes":new_dirty*super::PAGE as u64},
            "normal_reclaimable_bytes":Value::Null,"deep_reclaimable_bytes":Value::Null}),
        )
    }

    fn object_view(&mut self, object: &Object) -> Result<Value> {
        let g = self.geometry();
        let persisted = *STATUS_NAMES
            .get(effective_state(object)?)
            .ok_or_else(|| integrity("object sync state"))?;
        let state = self
            .cloud
            .transfer
            .get(&object.oid)
            .filter(|s| matches!(s.as_str(), "uploading" | "failed"))
            .map(String::as_str)
            .unwrap_or(persisted)
            .to_owned();
        let upload_pinned = if let Some((counts, ready)) = self
            .cloud
            .job
            .as_ref()
            .map(|job| (job.counts, job.phase == "ready"))
        {
            if tree::get(self, &super::store::COUNTS, counts, object.oid)?.is_some() {
                Some(true)
            } else if ready {
                Some(false)
            } else {
                None
            }
        } else {
            Some(false)
        };
        Ok(
            json!({"index":object.extent,"block":object.extent,"object_id":object.id,"oid":object.oid,
            "kind":if object.kind==1 {"data"} else {"metadata"},"content_type":TYPE_NAMES[content_type(object)],
            "sync_state":state,"uploaded":object.sync_state>=2,"sealed":object.sealed,
            "used_pages":object.used,"used_bytes":object.used as u64*super::PAGE as u64,
            "local_reference_count":object.refs,"current_reference_count":object.current_refs,"cloud_reference_count":object.cloud_refs,
            "snapshot_pinned":if object.refs==0||!self.has_snapshots{Some(false)}else if object.current_refs==0&&self.cloud.job.is_none(){Some(true)}else{None},
            "upload_pinned":upload_pinned,"length":g.object_size}),
        )
    }

    fn block_view(&mut self, extent: u64, block: Option<u64>) -> Result<Value> {
        let g = self.geometry();
        if extent == 0 {
            return Ok(
                json!({"index":0,"kind":"control","content_type":"container_index","sync_state":"local","length":g.object_size}),
            );
        }
        match block {
            Some(u64::MAX) => Ok(
                json!({"index":extent,"kind":"free","content_type":"free","sync_state":"free","length":g.object_size}),
            ),
            Some(0) => Ok(
                json!({"index":extent,"kind":"metadata","content_type":"container_index","sync_state":"local","length":g.object_size}),
            ),
            Some(oid) => {
                let object = self.reader.catalog_object(oid)?;
                if object.extent != extent {
                    return Err(integrity("physical/object index mismatch"));
                }
                self.object_view(&object)
            }
            None => Ok(
                json!({"index":extent,"kind":if extent>=self.root.next_extent{"free"}else{"reserved"},
                "content_type":if extent>=self.root.next_extent{"free"}else{"container_index"},
                "sync_state":if extent>=self.root.next_extent{"free"}else{"local"},"length":g.object_size}),
            ),
        }
    }

    fn category_page(
        &mut self,
        stats: &Statistics,
        category: usize,
        skip: u64,
        limit: usize,
    ) -> Result<Vec<u64>> {
        if skip >= stats.counts[category] {
            return Ok(Vec::new());
        }
        let length = (stats.counts[category] - skip).min(limit as u64) as usize;
        let rows = tree::scan_rank(
            self,
            &INDEX,
            stats.index,
            stats.before(category) + skip,
            length,
        )?;
        let mut out = Vec::with_capacity(rows.len());
        for (key, _) in rows {
            if key >> EXTENT_BITS != category as u64 {
                return Err(integrity("secondary index/statistics mismatch"));
            }
            out.push(key & EXTENT_MASK);
        }
        if out.len() != length {
            return Err(integrity("secondary index has missing entries"));
        }
        Ok(out)
    }

    pub fn debug_query(&mut self, request: &Value) -> Result<Value> {
        let g = self.geometry();
        let limit = request["limit"]
            .as_u64()
            .or_else(|| request["page_size"].as_u64())
            .unwrap_or(256)
            .clamp(1, 256) as usize;
        let start = request["start"].as_u64().unwrap_or_else(|| {
            request["page"]
                .as_u64()
                .unwrap_or(0)
                .saturating_mul(limit as u64)
        });
        let status = request["sync_state"].as_str().filter(|s| !s.is_empty());
        let kind = request["content_type"].as_str().filter(|s| !s.is_empty());
        let total = self.reader.catalog_len()? / g.object_size;
        if status.is_none() && kind.is_none() {
            let end = start.saturating_add(limit as u64).min(total);
            let root = self.root.blocks;
            let entries = tree::scan_after(self, &BLOCKS, root, start, limit)?
                .into_iter()
                .take_while(|(extent, _)| *extent < end)
                .map(|(extent, bytes)| (extent, u64::from_le_bytes(bytes.try_into().unwrap())))
                .collect::<BTreeMap<_, _>>();
            let mut items = Vec::new();
            for extent in start..end {
                items.push(self.block_view(extent, entries.get(&extent).copied())?);
            }
            return Ok(
                json!({"items":items,"total_count":total,"revision":self.root.revision,"start":start}),
            );
        }
        let kind_index = kind
            .map(|name| {
                TYPE_NAMES
                    .iter()
                    .position(|item| *item == name)
                    .ok_or_else(|| invalid("unknown content type filter"))
            })
            .transpose()?;
        let overlays = self
            .overlays()?
            .into_iter()
            .filter(|entry| !entry.object.missing)
            .collect::<Vec<_>>();
        if matches!(status, Some("uploading" | "failed")) {
            let matches: Vec<_> = overlays
                .iter()
                .filter(|entry| {
                    Some(entry.state.as_str()) == status
                        && kind_index.is_none_or(|kind| content_type(&entry.object) == kind)
                })
                .collect();
            let items = matches
                .iter()
                .skip(start.min(usize::MAX as u64) as usize)
                .take(limit)
                .map(|entry| self.object_view(&entry.object))
                .collect::<Result<Vec<_>>>()?;
            return Ok(
                json!({"items":items,"total_count":matches.len(),"revision":self.root.revision,"start":start}),
            );
        }
        let state_index = status
            .map(|name| {
                STATUS_NAMES
                    .iter()
                    .position(|item| *item == name)
                    .ok_or_else(|| invalid("unknown sync state filter"))
            })
            .transpose()?;
        let category = match (state_index, kind_index) {
            (None, Some(kind)) => 5 + kind,
            (Some(state), None) => 1 + state,
            (Some(state), Some(kind)) => PAIR_START + state * TYPE_COUNT + kind,
            _ => 0,
        };
        let stats = read_statistics(self, self.root.stats)?;
        let excluded: BTreeSet<_> = if let Some(state) = state_index {
            overlays
                .iter()
                .filter(|entry| {
                    effective_state(&entry.object).is_ok_and(|actual| actual == state)
                        && kind_index.is_none_or(|kind| content_type(&entry.object) == kind)
                })
                .map(|entry| entry.object.extent)
                .collect()
        } else {
            BTreeSet::new()
        };
        let count = stats.counts[category]
            .checked_sub(excluded.len() as u64)
            .ok_or_else(|| integrity("runtime filter cardinality"))?;
        let mut excluded_ranks = Vec::with_capacity(excluded.len());
        for extent in &excluded {
            let rank = tree::rank(self, &INDEX, stats.index, index_key(category, *extent))?;
            excluded_ranks.push(
                rank.checked_sub(stats.before(category))
                    .ok_or_else(|| integrity("runtime index rank"))?,
            );
        }
        excluded_ranks.sort_unstable();
        let mut raw_skip = start;
        for rank in excluded_ranks {
            if rank <= raw_skip {
                raw_skip += 1;
            }
        }
        let physical = self.category_page(
            &stats,
            category,
            raw_skip,
            limit.saturating_add(excluded.len()),
        )?;
        let mut items = Vec::new();
        for extent in physical
            .into_iter()
            .filter(|extent| !excluded.contains(extent))
            .take(limit)
        {
            let root = self.root.blocks;
            let block = tree::get(self, &BLOCKS, root, extent)?
                .map(|v| u64::from_le_bytes(v.try_into().unwrap()));
            items.push(self.block_view(extent, block)?);
        }
        Ok(json!({"items":items,"total_count":count,"revision":self.root.revision,"start":start}))
    }
}

impl Store {
    pub fn debug_summary(&mut self) -> Result<Value> {
        let root = self.root.clone();
        let cloud = self.cloud.clone();
        let has_snapshots = !self.snapshots.is_empty();
        DebugView {
            reader: self,
            root,
            cloud,
            has_snapshots,
        }
        .debug_summary()
    }
    pub fn debug_query(&mut self, r: &Value) -> Result<Value> {
        let root = self.root.clone();
        let cloud = self.cloud.clone();
        let has_snapshots = !self.snapshots.is_empty();
        DebugView {
            reader: self,
            root,
            cloud,
            has_snapshots,
        }
        .debug_query(r)
    }
    pub fn debug_changes(&self, since: u64) -> Value {
        json!({"revision":self.root.revision,"changed":since!=self.root.revision,"reset_required":since!=self.root.revision})
    }
    pub fn compact_status(&self) -> Result<Value> {
        Ok(self
            .cloud
            .compact
            .as_ref()
            .map(Compact::public)
            .unwrap_or_else(|| json!({"state":"idle","phase":"idle"})))
    }
    pub fn compact_cancel(&mut self) -> Result<Value> {
        let Some(task) = &self.cloud.compact else {
            return self.compact_status();
        };
        if matches!(task.phase.as_str(), "done" | "cancelled") {
            return self.compact_status();
        }
        let mut cloud = self.cloud.clone();
        let task = cloud.compact.as_mut().unwrap();
        task.phase = "cancelled".into();
        task.paused = false;
        self.transaction(|tx| tx.save_cloud(&cloud))?;
        self.cloud = cloud;
        self.compact_status()
    }

    pub fn compact_start(&mut self, mode: &str) -> Result<Value> {
        self.check()?;
        if !matches!(mode, "normal" | "deep") {
            return Err(invalid("compact mode must be normal or deep"));
        }
        if self.root.restore_required {
            return Err(invalid("restore must finish before compaction"));
        }
        if let Some(task) = &self.cloud.compact {
            if !matches!(task.phase.as_str(), "done" | "cancelled") {
                if task.mode != mode {
                    return Err(invalid(
                        "resume or finish the existing compaction mode first",
                    ));
                }
                return self.compact_pause(false);
            }
        }
        let mut cloud = self.cloud.clone();
        cloud.compact = Some(Compact {
            phase: "scan".into(),
            mode: mode.into(),
            cursor: 1,
            reclaimed_bytes: 0,
            moved_bytes: 0,
            processed_objects: 0,
            end_oid: self.root.next_oid,
            paused: false,
            truncated_bytes: 0,
            reclaim_cutoff: 0,
            metadata_cursor: 0,
            metadata_end: 0,
        });
        self.transaction(|tx| tx.save_cloud(&cloud))?;
        self.cloud = cloud;
        self.compact_status()
    }

    pub fn compact_pause(&mut self, paused: bool) -> Result<Value> {
        let Some(task) = &self.cloud.compact else {
            return self.compact_status();
        };
        if task.paused == paused || matches!(task.phase.as_str(), "done" | "cancelled") {
            return self.compact_status();
        }
        let mut cloud = self.cloud.clone();
        cloud.compact.as_mut().unwrap().paused = paused;
        self.transaction(|tx| tx.save_cloud(&cloud))?;
        self.cloud = cloud;
        self.compact_status()
    }

    pub fn compact_step(&mut self, max_objects: usize) -> Result<Value> {
        let g = self.geometry();
        self.check()?;
        let Some(mut task) = self.cloud.compact.clone() else {
            return self.compact_status();
        };
        if task.paused || matches!(task.phase.as_str(), "done" | "cancelled") {
            return self.compact_status();
        }
        if self.root.restore_required {
            return Err(invalid("restore must finish before compaction"));
        }
        if task.phase == "reclaim" {
            // Freeze the retired-space frontier. Our own progress COW writes and concurrent
            // guest/cache work belong to a later manual collection, never extend this task.
            if task.reclaim_cutoff == 0 {
                task.reclaim_cutoff = self.root.seq;
                task.metadata_cursor = g.object_size;
                task.metadata_end = self.root.next_extent * g.object_size;
                let mut cloud = self.cloud.clone();
                cloud.compact = Some(task.clone());
                self.transaction(|tx| tx.save_cloud(&cloud))?;
                self.cloud = cloud;
            }
            let reclaimed = self.decommit_free_before(32, task.reclaim_cutoff)?;
            let pending = self.has_decommit_pending_before(task.reclaim_cutoff)?;
            let (meta_freed, next, meta_done) = self.decommit_metadata_step(
                task.metadata_cursor,
                task.metadata_end,
                task.reclaim_cutoff,
            )?;
            let truncated = if !pending && meta_done {
                self.truncate_free_tail()?
            } else {
                0
            };
            if reclaimed == 0
                && meta_freed == 0
                && next == task.metadata_cursor
                && truncated == 0
                && (pending || !meta_done)
            {
                return self.compact_status();
            }
            task.metadata_cursor = next;
            task.reclaimed_bytes = task
                .reclaimed_bytes
                .checked_add(reclaimed + meta_freed)
                .ok_or_else(|| integrity("compaction counter overflow"))?;
            task.truncated_bytes = task
                .truncated_bytes
                .checked_add(truncated)
                .ok_or_else(|| integrity("compaction counter overflow"))?;
            if !pending && meta_done {
                task.phase = "done".into();
            }
            let mut cloud = self.cloud.clone();
            cloud.compact = Some(task);
            self.transaction(|tx| tx.save_cloud(&cloud))?;
            self.cloud = cloud;
            return self.compact_status();
        }
        if task.phase != "scan" {
            return Err(integrity("unknown compact phase"));
        }
        // An old view may still name a descriptor whose last current reference was removed.
        // Wait without persisting another progress root; hydration pins cover the handoff.
        if task.phase == "scan"
            && self
                .readers
                .lock()
                .map_err(|_| Error::Poisoned)?
                .active
                .keys()
                .any(|seq| *seq < self.root.seq)
        {
            return self.compact_status();
        }
        let limit = max_objects.clamp(1, 32);
        let root = self.root.objects;
        let rows = tree::scan_after(self, &OBJECTS, root, task.cursor, limit)?
            .into_iter()
            .take_while(|(oid, _)| *oid < task.end_oid)
            .collect::<Vec<_>>();
        let done = rows.len() < limit;
        let mut objects = Vec::with_capacity(rows.len());
        for (oid, bytes) in rows {
            objects.push(Object::decode(oid, &bytes)?);
            task.cursor = oid + 1;
        }
        task.processed_objects += objects.len() as u64;
        let mut protected = BTreeSet::new();
        for object in &objects {
            let job_pinned = self.cloud.job.as_ref().is_some_and(|job| {
                job.meta_tail == object.oid
                    || job
                        .seal_tails
                        .get(job.seal_cursor..)
                        .is_some_and(|pending| pending.contains(&object.oid))
            });
            if self
                .cache_runtime
                .as_ref()
                .is_some_and(|rt| rt.is_pinned(object.oid))
                || self.root.tails.contains(&object.oid)
                || job_pinned
                || self.cloud.transfer.contains_key(&object.oid)
                || self.count(self.cloud.published_counts, object.oid)? > 0
                || self.count(self.cloud.base_counts, object.oid)? > 0
                || if let Some(job) = &self.cloud.job {
                    self.count(job.counts, object.oid)? > 0
                } else {
                    false
                }
            {
                protected.insert(object.oid);
            }
        }
        let mut cloud = self.cloud.clone();
        self.transaction(|tx| {
            for object in &objects {
                if object.refs == 0 && !protected.contains(&object.oid) {
                    tx.free_object(object.oid)?;
                } else if task.mode == "deep" && object.sealed && tx.move_object(object.oid)? {
                    // Whole-object physical relocation leaves the immutable transport bytes,
                    // identity, page references, receipts, and guest generation unchanged.
                    task.moved_bytes = task
                        .moved_bytes
                        .checked_add(g.object_size)
                        .ok_or_else(|| integrity("compaction counter overflow"))?;
                }
            }
            if done {
                task.phase = "reclaim".into();
                task.cursor = task.end_oid;
                task.reclaim_cutoff = tx.root.seq + 1;
                task.metadata_cursor = g.object_size;
                task.metadata_end = tx.root.next_extent * g.object_size;
            }
            cloud.compact = Some(task.clone());
            tx.save_cloud(&cloud)
        })?;
        self.cloud = cloud;
        self.compact_status()
    }
}
