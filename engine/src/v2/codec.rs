use super::{Error, Result, OBJECT, PAGE};
use argon2::{Algorithm, Argon2, Params, Version};
use chacha20poly1305::{
    aead::{Aead, AeadInPlace, KeyInit, Payload},
    Tag, XChaCha20Poly1305, XNonce,
};
use rand::{rngs::OsRng, RngCore};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use uuid::Uuid;
use zeroize::Zeroizing;

pub const PAYLOAD: usize = 4016;
pub const ROOT: u8 = 1;
pub const NODE: u8 = 2;
pub const JOURNAL: u8 = 3;
pub const CATALOG: u8 = 4;
pub const OBJECT_HEADER: u8 = 5;
pub const SEALS: u8 = 6;
pub const CLOUD_BLOB: u8 = 7;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct MetaRef {
    pub offset: u64,
    pub hash: [u8; 32],
}
impl MetaRef {
    pub fn empty(self) -> bool {
        self.offset == 0
    }
    pub fn put(self, out: &mut [u8]) {
        out[..8].copy_from_slice(&self.offset.to_le_bytes());
        out[8..40].copy_from_slice(&self.hash);
    }
    pub fn get(input: &[u8]) -> Self {
        let mut hash = [0; 32];
        hash.copy_from_slice(&input[8..40]);
        Self {
            offset: u64::from_le_bytes(input[..8].try_into().unwrap()),
            hash,
        }
    }
}
#[derive(Clone, Copy, Debug)]
pub struct PageRef {
    pub object: u64,
    pub object_generation: u64,
    pub slot: u64,
    pub version: u64,
    pub nonce: [u8; 24],
    pub tag: [u8; 16],
}
impl PageRef {
    pub fn offset(self) -> u64 {
        self.object * OBJECT + 17 * PAGE as u64 + self.slot * PAGE as u64
    }
    pub fn put(self, out: &mut [u8]) {
        for (i, v) in [self.object, self.object_generation, self.slot, self.version]
            .iter()
            .enumerate()
        {
            out[i * 8..i * 8 + 8].copy_from_slice(&v.to_le_bytes());
        }
        out[32..56].copy_from_slice(&self.nonce);
        out[56..72].copy_from_slice(&self.tag);
    }
    pub fn get(input: &[u8]) -> Option<Self> {
        let number = |i: usize| u64::from_le_bytes(input[i * 8..i * 8 + 8].try_into().unwrap());
        if number(0) == 0 {
            return None;
        }
        let mut nonce = [0; 24];
        nonce.copy_from_slice(&input[32..56]);
        let mut tag = [0; 16];
        tag.copy_from_slice(&input[56..72]);
        Some(Self {
            object: number(0),
            object_generation: number(1),
            slot: number(2),
            version: number(3),
            nonce,
            tag,
        })
    }
}

#[derive(Clone, Serialize, Deserialize)]
pub struct Config {
    pub format_version: u32,
    pub id: Uuid,
    pub capacity_bytes: u64,
    pub encrypted: bool,
    pub salt: [u8; 16],
    pub nonce: [u8; 24],
    pub wrapped_key: Vec<u8>,
    #[serde(default)]
    pub restoring: bool,
}
impl Config {
    fn ad(&self) -> Vec<u8> {
        let mut a = b"OverlayDisk v2 volume key".to_vec();
        a.extend_from_slice(self.id.as_bytes());
        a.extend_from_slice(&self.capacity_bytes.to_le_bytes());
        a
    }
    pub fn create(capacity: u64, password: Option<&str>) -> Result<(Self, Crypto)> {
        let mut config = Self {
            format_version: 2,
            id: Uuid::new_v4(),
            capacity_bytes: capacity,
            encrypted: password.is_some(),
            salt: [0; 16],
            nonce: [0; 24],
            wrapped_key: Vec::new(),
            restoring: false,
        };
        let key = if let Some(password) = password {
            if password.is_empty() {
                return Err(Error::Invalid("password cannot be empty".into()));
            }
            OsRng.fill_bytes(&mut config.salt);
            OsRng.fill_bytes(&mut config.nonce);
            let mut key = Zeroizing::new([0; 32]);
            OsRng.fill_bytes(key.as_mut());
            let wrapping = derive(password, &config.salt)?;
            config.wrapped_key = XChaCha20Poly1305::new_from_slice(wrapping.as_ref())
                .unwrap()
                .encrypt(
                    XNonce::from_slice(&config.nonce),
                    Payload {
                        msg: key.as_ref(),
                        aad: &config.ad(),
                    },
                )
                .map_err(|_| Error::Integrity("key wrapping failed".into()))?;
            Some(key)
        } else {
            None
        };
        let crypto = Crypto { id: config.id, key };
        Ok((config, crypto))
    }
    pub fn unlock(&self, password: Option<&str>) -> Result<Crypto> {
        if !matches!(self.format_version, 2 | 3)
            || self.capacity_bytes < 64 * 1024 * 1024
            || !self.capacity_bytes.is_multiple_of(512)
        {
            return Err(Error::Invalid("invalid V2 configuration".into()));
        }
        let key = if self.encrypted {
            let password = password.ok_or(Error::Password)?;
            let wrapping = derive(password, &self.salt)?;
            let bytes = Zeroizing::new(
                XChaCha20Poly1305::new_from_slice(wrapping.as_ref())
                    .unwrap()
                    .decrypt(
                        XNonce::from_slice(&self.nonce),
                        Payload {
                            msg: &self.wrapped_key,
                            aad: &self.ad(),
                        },
                    )
                    .map_err(|_| Error::Password)?,
            );
            if bytes.len() != 32 {
                return Err(Error::Password);
            }
            let mut key = Zeroizing::new([0; 32]);
            key.copy_from_slice(&bytes);
            Some(key)
        } else {
            if password.is_some() {
                return Err(Error::Invalid("unencrypted volume: omit password".into()));
            }
            None
        };
        Ok(Crypto { id: self.id, key })
    }
    pub fn encode(&self) -> Result<[u8; PAGE]> {
        let data = serde_json::to_vec(self)?;
        if data.len() > PAGE - 48 {
            return Err(Error::Invalid("configuration too large".into()));
        }
        let mut out = [0; PAGE];
        out[..8].copy_from_slice(b"ODV2CFG1");
        out[8..12].copy_from_slice(&(data.len() as u32).to_le_bytes());
        out[16..16 + data.len()].copy_from_slice(&data);
        let hash = Sha256::digest(&out[..PAGE - 32]);
        out[PAGE - 32..].copy_from_slice(&hash);
        Ok(out)
    }
    pub fn decode(input: &[u8; PAGE]) -> Result<Self> {
        if &input[..8] != b"ODV2CFG1"
            || Sha256::digest(&input[..PAGE - 32])[..] != input[PAGE - 32..]
        {
            return Err(Error::Integrity("configuration checksum".into()));
        }
        let size = u32::from_le_bytes(input[8..12].try_into().unwrap()) as usize;
        if size > PAGE - 48 {
            return Err(Error::Integrity("configuration length".into()));
        }
        Ok(serde_json::from_slice(&input[16..16 + size])?)
    }
}

fn derive(password: &str, salt: &[u8; 16]) -> Result<Zeroizing<[u8; 32]>> {
    let mut key = Zeroizing::new([0; 32]);
    Argon2::new(
        Algorithm::Argon2id,
        Version::V0x13,
        Params::new(65536, 3, 1, Some(32)).unwrap(),
    )
    .hash_password_into(password.as_bytes(), salt, key.as_mut())
    .map_err(|_| Error::Password)?;
    Ok(key)
}
pub struct Crypto {
    pub id: Uuid,
    pub key: Option<Zeroizing<[u8; 32]>>,
}
impl Crypto {
    pub fn seal_blob(&self, aad: &[u8], data: &mut [u8]) -> Result<([u8; 24], [u8; 16])> {
        let mut nonce = [0; 24];
        OsRng.fill_bytes(&mut nonce);
        let tag = if let Some(key) = &self.key {
            XChaCha20Poly1305::new_from_slice(key.as_ref())
                .unwrap()
                .encrypt_in_place_detached(XNonce::from_slice(&nonce), aad, data)
                .map_err(|_| Error::Integrity("descriptor encryption".into()))?
                .into()
        } else {
            let mut h = Sha256::new();
            h.update(aad);
            h.update(&data[..]);
            h.finalize()[..16].try_into().unwrap()
        };
        Ok((nonce, tag))
    }
    pub fn frame(&self, kind: u8, seq: u64, offset: u64, data: &[u8]) -> Result<[u8; PAGE]> {
        let mut nonce = [0; 24];
        OsRng.fill_bytes(&mut nonce);
        self.frame_nonce(kind, seq, offset, data, nonce)
    }
    pub fn frame_nonce(
        &self,
        kind: u8,
        seq: u64,
        offset: u64,
        data: &[u8],
        nonce: [u8; 24],
    ) -> Result<[u8; PAGE]> {
        if data.len() > PAYLOAD {
            return Err(Error::Invalid("metadata page too large".into()));
        }
        let mut out = [0u8; PAGE];
        out[..8].copy_from_slice(b"ODV2MET1");
        out[8] = kind;
        out[9] = u8::from(self.key.is_some());
        out[16..24].copy_from_slice(&seq.to_le_bytes());
        out[24..32].copy_from_slice(&offset.to_le_bytes());
        out[32..56].copy_from_slice(&nonce);
        out[56..60].copy_from_slice(&(data.len() as u32).to_le_bytes());
        out[64..64 + data.len()].copy_from_slice(data);
        let mut ad = out[..64].to_vec();
        ad.extend_from_slice(self.id.as_bytes());
        let tag = if let Some(key) = &self.key {
            let nonce: [u8; 24] = out[32..56].try_into().unwrap();
            XChaCha20Poly1305::new_from_slice(key.as_ref())
                .unwrap()
                .encrypt_in_place_detached(XNonce::from_slice(&nonce), &ad, &mut out[64..PAGE - 16])
                .map_err(|_| Error::Integrity("metadata encryption".into()))?
                .to_vec()
        } else {
            let mut h = Sha256::new();
            h.update(&ad);
            h.update(&out[64..PAGE - 16]);
            h.finalize()[..16].to_vec()
        };
        out[PAGE - 16..].copy_from_slice(&tag);
        Ok(out)
    }
    pub fn unframe(&self, kind: u8, offset: u64, input: &[u8; PAGE]) -> Result<Vec<u8>> {
        if &input[..8] != b"ODV2MET1"
            || input[8] != kind
            || input[9] != u8::from(self.key.is_some())
            || input[24..32] != offset.to_le_bytes()
        {
            return Err(Error::Integrity("metadata identity".into()));
        }
        let len = u32::from_le_bytes(input[56..60].try_into().unwrap()) as usize;
        if len > PAYLOAD {
            return Err(Error::Integrity("metadata length".into()));
        }
        let mut ad = input[..64].to_vec();
        ad.extend_from_slice(self.id.as_bytes());
        let mut body = Zeroizing::new(input[64..PAGE - 16].to_vec());
        if let Some(key) = &self.key {
            XChaCha20Poly1305::new_from_slice(key.as_ref())
                .unwrap()
                .decrypt_in_place_detached(
                    XNonce::from_slice(&input[32..56]),
                    &ad,
                    &mut body,
                    Tag::from_slice(&input[PAGE - 16..]),
                )
                .map_err(|_| Error::Integrity("metadata authentication".into()))?;
        } else {
            let mut h = Sha256::new();
            h.update(&ad);
            h.update(&body);
            if h.finalize()[..16] != input[PAGE - 16..] {
                return Err(Error::Integrity("metadata checksum".into()));
            }
        }
        Ok(body[..len].to_vec())
    }
    fn page_ad(&self, page: u64, version: u64) -> [u8; 32] {
        let mut ad = [0; 32];
        ad[..16].copy_from_slice(self.id.as_bytes());
        ad[16..24].copy_from_slice(&page.to_le_bytes());
        ad[24..].copy_from_slice(&version.to_le_bytes());
        ad
    }
    pub fn encode_page(
        &self,
        page: u64,
        version: u64,
        nonce: [u8; 24],
        data: &[u8; PAGE],
    ) -> Result<([u8; PAGE], [u8; 16])> {
        let mut output = *data;
        let ad = self.page_ad(page, version);
        let tag = if let Some(key) = &self.key {
            XChaCha20Poly1305::new_from_slice(key.as_ref())
                .unwrap()
                .encrypt_in_place_detached(XNonce::from_slice(&nonce), &ad, &mut output)
                .map_err(|_| Error::Integrity("data encryption".into()))?
                .into()
        } else {
            let mut hash = Sha256::new();
            hash.update(ad);
            hash.update(nonce);
            hash.update(output);
            hash.finalize()[..16].try_into().unwrap()
        };
        Ok((output, tag))
    }
    pub fn decode_page(&self, page: u64, reference: PageRef, data: &mut [u8; PAGE]) -> Result<()> {
        let ad = self.page_ad(page, reference.version);
        if let Some(key) = &self.key {
            XChaCha20Poly1305::new_from_slice(key.as_ref())
                .unwrap()
                .decrypt_in_place_detached(
                    XNonce::from_slice(&reference.nonce),
                    &ad,
                    data,
                    Tag::from_slice(&reference.tag),
                )
                .map_err(|_| Error::Integrity(format!("data page {page}")))?;
        } else {
            let mut hash = Sha256::new();
            hash.update(ad);
            hash.update(reference.nonce);
            hash.update(&data[..]);
            if hash.finalize()[..16] != reference.tag {
                return Err(Error::Integrity(format!("data page {page}")));
            }
        }
        Ok(())
    }
    pub fn authenticate_manifest(&self, id: &str, bytes: &[u8]) -> Result<serde_json::Value> {
        let aad = format!("OverlayDisk snapshot {} {id}", self.id);
        let mut digest = Sha256::new();
        digest.update(aad.as_bytes());
        digest.update(bytes);
        let nonce: [u8; 24] = digest.finalize()[..24].try_into().unwrap();
        let payload = if let Some(key) = &self.key {
            XChaCha20Poly1305::new_from_slice(key.as_ref())
                .unwrap()
                .encrypt(
                    XNonce::from_slice(&nonce),
                    Payload {
                        msg: bytes,
                        aad: aad.as_bytes(),
                    },
                )
                .map_err(|_| Error::Integrity("snapshot manifest encryption".into()))?
        } else {
            let mut out = bytes.to_vec();
            out.extend_from_slice(&Sha256::digest(bytes));
            out
        };
        Ok(
            serde_json::json!({"encrypted":self.key.is_some(),"nonce":hex(&nonce),"payload":hex(&payload)}),
        )
    }
}
pub fn hash(data: &[u8]) -> [u8; 32] {
    Sha256::digest(data).into()
}
pub fn hex(data: &[u8]) -> String {
    const H: &[u8] = b"0123456789abcdef";
    let mut out = String::with_capacity(data.len() * 2);
    for b in data {
        out.push(H[(b >> 4) as usize] as char);
        out.push(H[(b & 15) as usize] as char);
    }
    out
}
