//! The native store owns integrity and persistence, never cloud secrets.
use super::*;
use codec::{hash, PageRef};

#[test]
fn local_create_rejects_passwords_and_persists_only_plaintext_configuration() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("plaintext.odv4");
    let error = Volume::create(&path, 64 << 20, Some("cloud-only-password")).unwrap_err();
    assert!(error
        .to_string()
        .contains("local disk encryption is unsupported"));
    assert!(
        !path.exists(),
        "rejected encryption must not create a container"
    );
    Volume::create(&path, 64 << 20, None).unwrap();
    let disk = Volume::open(&path, None).unwrap();
    assert!(!disk.info().unwrap().encrypted);
    let config = disk.shared.store.lock().unwrap().config.clone();
    let json = serde_json::to_value(&config).unwrap();
    assert_eq!(json["local_storage"], "plaintext-v1");
    assert_eq!(json["encrypted"], false);
    for field in ["salt", "nonce", "wrapped_key", "crypto_id"] {
        assert!(
            json.get(field).is_none(),
            "local configuration retained cloud secret material: {field}"
        );
    }
    assert_eq!(&config.encode().unwrap()[..8], b"ODV4PLN1");
    assert!(config.unlock(Some("cloud-only-password")).is_err());
    config.unlock(Some("")).unwrap();
    disk.write(0, &[0x52; PAGE]).unwrap();
    disk.flush().unwrap();
    drop(disk);
    assert!(Volume::open(&path, Some("cloud-only-password")).is_err());
    let reopened = Volume::open(&path, None).unwrap();
    let mut data = [0; PAGE];
    reopened.read(0, &mut data).unwrap();
    assert_eq!(data, [0x52; PAGE]);
}

#[test]
fn plaintext_format_rejects_legacy_headers_and_encrypted_configurations() {
    let (config, _) = Config::create(64 << 20, None).unwrap();
    let mut legacy = config.encode().unwrap();
    legacy[..8].copy_from_slice(b"ODV4CFGO");
    let digest = hash(&legacy[..PAGE - 32]);
    legacy[PAGE - 32..].copy_from_slice(&digest);
    assert!(Config::decode(&legacy)
        .err()
        .unwrap()
        .to_string()
        .contains("old containers"));
    let mut encrypted = config.clone();
    encrypted.encrypted = true;
    assert!(encrypted.encode().is_err());
    assert!(encrypted.unlock(None).is_err());
    let mut unsupported = config.clone();
    unsupported.local_storage = "legacy-v4".into();
    assert!(unsupported.unlock(None).is_err());
    let mut missing = serde_json::to_value(&config).unwrap();
    missing.as_object_mut().unwrap().remove("local_storage");
    assert!(serde_json::from_value::<Config>(missing).is_err());
    let mut old_secret = serde_json::to_value(&config).unwrap();
    old_secret["wrapped_key"] = serde_json::json!([1, 2, 3]);
    assert!(serde_json::from_value::<Config>(old_secret).is_err());
}

#[test]
fn plaintext_pages_and_metadata_detect_corruption_and_wrong_identity() {
    let (_, codec) = Config::create(64 << 20, None).unwrap();
    let original = [0x35; PAGE];
    let (mut bytes, tag) = codec.encode_page(7, 9, [0; 24], &original).unwrap();
    assert_eq!(bytes, original, "cloud compression must receive plaintext");
    let reference = PageRef {
        object: 1,
        object_generation: 1,
        slot: 1,
        version: 9,
        nonce: [0; 24],
        tag,
    };
    codec.decode_page(7, reference, &mut bytes).unwrap();
    assert!(codec.decode_page(8, reference, &mut bytes).is_err());
    let mut changed_version = reference;
    changed_version.version += 1;
    assert!(codec.decode_page(7, changed_version, &mut bytes).is_err());
    bytes[38] ^= 1;
    assert!(codec.decode_page(7, reference, &mut bytes).is_err());
    let mut frame = codec
        .frame(codec::NODE, 10, PAGE as u64, b"plain metadata")
        .unwrap();
    assert_eq!(&frame[64..78], b"plain metadata");
    assert_eq!(
        codec.unframe(codec::NODE, PAGE as u64, &frame).unwrap(),
        b"plain metadata"
    );
    assert!(codec.unframe(codec::NODE, 2 * PAGE as u64, &frame).is_err());
    frame[65] ^= 1;
    assert!(codec.unframe(codec::NODE, PAGE as u64, &frame).is_err());
    let mut table = b"plain descriptors".to_vec();
    let (reserved, tag) = codec.seal_blob(b"object identity", &mut table).unwrap();
    assert_eq!(reserved, [0; 24]);
    assert_eq!(table, b"plain descriptors");
    codec
        .open_blob(b"object identity", &reserved, &tag, &mut table)
        .unwrap();
    table[0] ^= 1;
    assert!(codec
        .open_blob(b"object identity", &reserved, &tag, &mut table)
        .is_err());
}
