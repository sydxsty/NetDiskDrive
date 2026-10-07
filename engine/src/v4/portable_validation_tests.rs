use super::codec::{hash, hex, Crypto, MetaRef, PageRef};
use super::portable_validation::validate_node_reference;
use super::store::{self, Object, Page, PORTABLE};
use super::*;
use serde_json::json;

#[derive(Clone, Copy, Debug)]
enum Mutation {
    Valid,
    LocalRoot,
    LocalChild,
    MissingChildDependency,
    ChildInHeader,
    ChildBeyondUsed,
    WrongChildRange,
    WrongChildCount,
    MissingDataDependency,
    MetadataAsData,
    DataBeyondSlots,
    DataBeyondCapacity,
    EmptyRootWithHash,
}

fn object_id(ordinal: u64) -> Uuid {
    let mut id = [8; 16];
    id[8..].copy_from_slice(&ordinal.to_le_bytes());
    Uuid::from_bytes(id)
}

fn node_header(level: u8, base: u64, bitmap: u64) -> Vec<u8> {
    let mut bytes = vec![0; 32];
    bytes[..4].copy_from_slice(b"ODTR");
    bytes[4] = 2;
    bytes[5] = 4;
    bytes[6] = 5;
    bytes[7] = level;
    bytes[8..10].copy_from_slice(&104u16.to_le_bytes());
    bytes[16..24].copy_from_slice(&base.to_le_bytes());
    bytes[24..32].copy_from_slice(&bitmap.to_le_bytes());
    bytes
}

/// Produce correctly authenticated bytes, including all parent hashes. Failures
/// must therefore come from graph validation rather than a stale checksum.
fn metadata(config: &Config, crypto: &Crypto, mutation: Mutation) -> Vec<u8> {
    let g = crypto.geometry;
    let oid = 1;
    let id = object_id(oid);
    let mut raw = vec![0; g.object_size as usize];
    let mut leaf = node_header(
        0,
        if matches!(mutation, Mutation::DataBeyondCapacity) {
            config.capacity_bytes / PAGE as u64
        } else {
            0
        },
        1,
    );
    let page = Page {
        reference: PageRef {
            object: match mutation {
                Mutation::MissingDataDependency => 9,
                Mutation::MetadataAsData => oid,
                _ => 2,
            },
            object_generation: 1,
            slot: if matches!(mutation, Mutation::DataBeyondSlots) {
                g.slots
            } else {
                0
            },
            version: 1,
            nonce: [0; 24],
            tag: [0; 16],
        },
        digest: [5; 32],
    };
    leaf.extend_from_slice(&page.encode());
    let mut reference = MetaRef::default();
    for level in 0..=4u8 {
        let payload = if level == 0 {
            leaf.clone()
        } else {
            let mut child = reference;
            if level == 1 {
                match mutation {
                    Mutation::LocalChild => child.offset &= !PORTABLE,
                    Mutation::MissingChildDependency => child.offset += 8 * g.object_size,
                    Mutation::ChildInHeader => {
                        child.offset = PORTABLE | (oid * g.object_size + PAGE as u64)
                    }
                    Mutation::ChildBeyondUsed => {
                        child.offset =
                            PORTABLE | (oid * g.object_size + (g.payload_pages + 7) * PAGE as u64)
                    }
                    _ => {}
                }
            }
            let mut payload = node_header(
                level,
                0,
                if level == 1 && matches!(mutation, Mutation::WrongChildRange) {
                    2
                } else {
                    1
                },
            );
            let mut row = [0; 48];
            child.put(&mut row);
            row[40..].copy_from_slice(
                &(if level == 1 && matches!(mutation, Mutation::WrongChildCount) {
                    2u64
                } else {
                    1
                })
                .to_le_bytes(),
            );
            payload.extend_from_slice(&row);
            payload
        };
        let at = (g.payload_pages as usize + level as usize + 1) * PAGE;
        let offset = PORTABLE | (oid * g.object_size + at as u64);
        let frame = crypto.frame(codec::NODE, 1, offset, &payload).unwrap();
        raw[at..at + PAGE].copy_from_slice(&frame);
        reference = MetaRef {
            offset,
            hash: hash(&frame),
        };
    }
    match mutation {
        Mutation::LocalRoot => reference.offset &= !PORTABLE,
        Mutation::EmptyRootWithHash => reference.offset = 0,
        _ => {}
    }
    let descriptor = json!({"format_version":4,"container_id":config.id,"crypto_id":crypto.id,
        "capacity_bytes":config.capacity_bytes,"generation":1,"index":reference,"index_depth":4,
        "page_size":PAGE,"object_size":g.object_size});
    let at = g.header_bytes();
    raw[at..at + PAGE].copy_from_slice(
        &crypto
            .frame(
                codec::ROOT,
                1,
                PORTABLE | (oid * g.object_size + at as u64),
                &serde_json::to_vec(&descriptor).unwrap(),
            )
            .unwrap(),
    );
    let public = g.header_bytes() - 1024;
    let config_bytes = serde_json::to_vec(config).unwrap();
    raw[public..public + 8].copy_from_slice(b"ODV4PUB1");
    raw[public + 8..public + 12].copy_from_slice(&(config_bytes.len() as u32).to_le_bytes());
    raw[public + 12..public + 12 + config_bytes.len()].copy_from_slice(&config_bytes);
    let public_hash = hash(&raw[public..public + 992]);
    raw[public + 992..public + 1024].copy_from_slice(&public_hash);
    let mut table = raw[PAGE..public].to_vec();
    table[..16].copy_from_slice(object_id(2).as_bytes());
    table[16..48].fill(7);
    let (nonce, tag) = crypto
        .seal_blob(
            format!("OverlayDisk v4 object table {id}").as_bytes(),
            &mut table,
        )
        .unwrap();
    raw[PAGE..public].copy_from_slice(&table);
    let header = json!({"id":id,"oid":oid,"kind":2,"used":6,"external_count":1,
        "body_sha256":hex(&hash(&raw[PAGE..])),"table_nonce":hex(&nonce),"table_tag":hex(&tag),"config":config});
    raw[..PAGE].copy_from_slice(
        &crypto
            .frame(
                codec::OBJECT_HEADER,
                1,
                PORTABLE | (oid * g.object_size),
                &serde_json::to_vec(&header).unwrap(),
            )
            .unwrap(),
    );
    raw
}

#[test]
fn authenticated_invalid_portable_graphs_are_rejected_before_import() {
    let (config, crypto) = Config::create(64 << 20, Some("password")).unwrap();
    let dir = tempfile::tempdir().unwrap();
    let cases = [
        Mutation::LocalRoot,
        Mutation::LocalChild,
        Mutation::MissingChildDependency,
        Mutation::ChildInHeader,
        Mutation::ChildBeyondUsed,
        Mutation::WrongChildRange,
        Mutation::WrongChildCount,
        Mutation::MissingDataDependency,
        Mutation::MetadataAsData,
        Mutation::DataBeyondSlots,
        Mutation::DataBeyondCapacity,
        Mutation::EmptyRootWithHash,
    ];
    for (n, mutation) in cases.iter().enumerate() {
        let raw = metadata(&config, &crypto, *mutation);
        assert!(
            store::decode_header(&crypto, 1, &raw).is_ok(),
            "fixture must authenticate: {mutation:?}"
        );
        assert!(
            store::external_table(&crypto, 1, &raw).is_err(),
            "accepted {mutation:?}"
        );
        let path = dir.path().join(format!("invalid-{n}.odv4"));
        let backing = json!({"provider_id":"mock","account_id":"account","remote_root":"/objects",
            "reader_pin":"fixture","source_volume_id":config.id,"root_object_id":object_id(1),
            "root_sha256":hex(&hash(&raw))});
        assert!(
            Volume::restore_begin_options(
                &path,
                &raw,
                Some("password"),
                json!({"mode":"copy","lazy":true,"backing":backing})
            )
            .is_err(),
            "import accepted {mutation:?}"
        );
        assert!(
            !path.exists(),
            "malformed root created a container: {mutation:?}"
        );
    }
}

#[test]
fn valid_portable_metadata_is_accepted_for_all_object_sizes() {
    for size in [4, 8, 16] {
        let (config, crypto) = Config::create_sized(64 << 20, None, size << 20).unwrap();
        let raw = metadata(&config, &crypto, Mutation::Valid);
        assert_eq!(store::external_table(&crypto, 1, &raw).unwrap().len(), 1);
    }
}

#[test]
fn portable_readers_reject_wrong_kinds_and_unallocated_slots_but_allow_lazy_placeholders() {
    for size in [4, 8, 16] {
        let g = Geometry::new(size << 20).unwrap();
        let reference = MetaRef {
            offset: PORTABLE | (g.object_size + (g.payload_pages + 1) * PAGE as u64),
            hash: [1; 32],
        };
        let mut object = Object {
            oid: 1,
            id: object_id(1),
            extent: 2,
            kind: 2,
            pool: 0,
            used: 2,
            sealed: true,
            missing: false,
            remote: true,
            remote_source: 1,
            cache_backed: false,
            origin_backed: true,
            sync_state: 3,
            refs: 1,
            current_refs: 0,
            cloud_refs: 0,
            sha: [2; 32],
            external_count: 1,
        };
        validate_node_reference(reference, &object, g).unwrap();
        object.kind = 1;
        assert!(validate_node_reference(reference, &object, g).is_err());
        object.kind = 2;
        object.used = 1;
        assert!(validate_node_reference(reference, &object, g).is_err());
        object.missing = true;
        object.kind = 0;
        object.used = 0;
        validate_node_reference(reference, &object, g).unwrap();
        for offset in [
            reference.offset & !PORTABLE,
            reference.offset + 1,
            PORTABLE | (g.object_size + g.header_bytes() as u64),
        ] {
            assert!(validate_node_reference(
                MetaRef {
                    offset,
                    ..reference
                },
                &object,
                g
            )
            .is_err());
        }
    }
}

#[test]
fn encrypted_writer_objects_validate_without_fetching_any_dependency() {
    let f = lazy_tests::fixture(Some("password"));
    let store = f.source.shared.store.lock().unwrap();
    for raw in f.objects.values() {
        let address = u64::from_le_bytes(raw[24..32].try_into().unwrap());
        let oid = (address & !PORTABLE) / store.object_size();
        if store::decode_header(&store.crypto, oid, raw).unwrap()["kind"] == 2 {
            store::external_table(&store.crypto, oid, raw).unwrap();
        }
    }
}
