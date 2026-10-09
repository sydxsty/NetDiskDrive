use super::{Error, Result, PAGE};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use uuid::Uuid;

pub const PAYLOAD: usize = 4016;
pub const ROOT: u8 = 1;
pub const NODE: u8 = 2;
pub const OBJECT_HEADER: u8 = 5;
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

/// Local/canonical object format. Encryption belongs to the cloud transport.
#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Config {
    pub format_version: u32,
    pub local_storage: String,
    pub id: Uuid,
    pub integrity_id: Option<Uuid>,
    pub capacity_bytes: u64,
    pub object_size: u64,
    pub encrypted: bool,
    pub restoring: bool,
    pub lazy: bool,
    pub direct_base: bool,
    pub wide: bool,
    pub cache_capable: bool,
    #[serde(default, skip_serializing_if = "zero_allocation_depth")]
    pub allocation_depth: u8,
}
fn zero_allocation_depth(value: &u8) -> bool {
    *value == 0
}
fn reject_local_password(password: Option<&str>) -> Result<()> {
    if password.is_some_and(|p| !p.is_empty()) {
        return Err(Error::Invalid(
            "local disk encryption is unsupported; configure cloud encryption in the application"
                .into(),
        ));
    }
    Ok(())
}
impl Config {
    #[cfg(test)]
    pub fn create(capacity: u64, password: Option<&str>) -> Result<(Self, PageCodec)> {
        Self::create_sized(capacity, password, super::OBJECT)
    }
    pub fn create_sized(
        capacity: u64,
        password: Option<&str>,
        object_size: u64,
    ) -> Result<(Self, PageCodec)> {
        reject_local_password(password)?;
        let geometry = super::Geometry::new(object_size)?;
        let config = Self {
            format_version: 4,
            local_storage: "plaintext-v1".into(),
            id: Uuid::new_v4(),
            integrity_id: None,
            capacity_bytes: capacity,
            object_size,
            encrypted: false,
            restoring: false,
            lazy: false,
            direct_base: false,
            wide: capacity > 1024 * 1024 * 1024 * 1024,
            cache_capable: false,
            allocation_depth: 0,
        };
        let codec = PageCodec {
            id: config.id,
            geometry,
        };
        Ok((config, codec))
    }
    pub fn unlock(&self, password: Option<&str>) -> Result<PageCodec> {
        reject_local_password(password)?;
        self.validate_plaintext()?;
        let geometry = super::Geometry::new(self.object_size)?;
        super::store::allocation_range(self)?;
        if self.format_version != 4
            || self.capacity_bytes < 64 * 1024 * 1024
            || self.capacity_bytes > super::MAX_CAPACITY
            || !self.capacity_bytes.is_multiple_of(512)
        {
            return Err(Error::Invalid("invalid plaintext V4 configuration".into()));
        }
        Ok(PageCodec {
            id: self.integrity_id.unwrap_or(self.id),
            geometry,
        })
    }
    pub(super) fn validate_plaintext(&self) -> Result<()> {
        if self.local_storage != "plaintext-v1" || self.encrypted {
            return Err(Error::Invalid(
                "unsupported local storage format; this build requires plaintext-v1 containers"
                    .into(),
            ));
        }
        Ok(())
    }
    #[cfg(test)]
    pub fn fork(&self, codec: &PageCodec, password: Option<&str>) -> Result<Self> {
        self.fork_identity(codec, password, Uuid::new_v4())
    }
    pub fn fork_identity(
        &self,
        codec: &PageCodec,
        password: Option<&str>,
        id: Uuid,
    ) -> Result<Self> {
        reject_local_password(password)?;
        self.validate_plaintext()?;
        let mut c = self.clone();
        if id != self.id {
            c.allocation_depth = c
                .allocation_depth
                .checked_add(1)
                .ok_or_else(|| Error::Invalid("copy ancestry is exhausted".into()))?;
            super::store::allocation_range(&c)?;
        }
        c.id = id;
        c.integrity_id = Some(codec.id);
        c.restoring = true;
        Ok(c)
    }
    pub fn encode(&self) -> Result<[u8; PAGE]> {
        self.validate_plaintext()?;
        let data = serde_json::to_vec(self)?;
        if data.len() > PAGE - 48 {
            return Err(Error::Invalid("configuration too large".into()));
        }
        let mut out = [0; PAGE];
        out[..8].copy_from_slice(b"ODV4PLN1");
        out[8..12].copy_from_slice(&(data.len() as u32).to_le_bytes());
        out[16..16 + data.len()].copy_from_slice(&data);
        let hash = Sha256::digest(&out[..PAGE - 32]);
        out[PAGE - 32..].copy_from_slice(&hash);
        Ok(out)
    }
    pub fn decode(input: &[u8; PAGE]) -> Result<Self> {
        if &input[..8] != b"ODV4PLN1" {
            return Err(Error::Invalid("unsupported local storage format; old containers require their original application".into()));
        }
        if Sha256::digest(&input[..PAGE - 32])[..] != input[PAGE - 32..] {
            return Err(Error::Integrity("configuration checksum".into()));
        }
        let size = u32::from_le_bytes(input[8..12].try_into().unwrap()) as usize;
        if size > PAGE - 48 {
            return Err(Error::Integrity("configuration length".into()));
        }
        let config: Self = serde_json::from_slice(&input[16..16 + size])?;
        config.validate_plaintext()?;
        super::Geometry::new(config.object_size)?;
        Ok(config)
    }
}

/// Plaintext codec with position-bound checksums. These checksums detect
/// corruption; cloud AEAD provides secrecy and adversarial authentication.
pub struct PageCodec {
    pub(super) geometry: super::Geometry,
    pub id: Uuid,
}
impl PageCodec {
    pub fn seal_blob(&self, aad: &[u8], data: &mut [u8]) -> Result<([u8; 24], [u8; 16])> {
        let mut h = Sha256::new();
        h.update(aad);
        h.update(&data[..]);
        Ok(([0; 24], h.finalize()[..16].try_into().unwrap()))
    }
    pub fn open_blob(
        &self,
        aad: &[u8],
        reserved: &[u8; 24],
        tag: &[u8; 16],
        data: &mut [u8],
    ) -> Result<()> {
        let mut h = Sha256::new();
        h.update(aad);
        h.update(&data[..]);
        if *reserved != [0; 24] || h.finalize()[..16] != tag[..] {
            return Err(Error::Integrity("object descriptors checksum".into()));
        }
        Ok(())
    }
    pub fn frame(&self, kind: u8, seq: u64, offset: u64, data: &[u8]) -> Result<[u8; PAGE]> {
        if data.len() > PAYLOAD {
            return Err(Error::Invalid("metadata page too large".into()));
        }
        let mut out = [0u8; PAGE];
        out[..8].copy_from_slice(b"ODV4METP");
        out[8] = kind;
        out[16..24].copy_from_slice(&seq.to_le_bytes());
        out[24..32].copy_from_slice(&offset.to_le_bytes());
        out[56..60].copy_from_slice(&(data.len() as u32).to_le_bytes());
        out[64..64 + data.len()].copy_from_slice(data);
        let mut h = Sha256::new();
        h.update(&out[..64]);
        h.update(self.id.as_bytes());
        h.update(&out[64..PAGE - 16]);
        out[PAGE - 16..].copy_from_slice(&h.finalize()[..16]);
        Ok(out)
    }
    pub fn unframe(&self, kind: u8, offset: u64, input: &[u8; PAGE]) -> Result<Vec<u8>> {
        if &input[..8] != b"ODV4METP"
            || input[8] != kind
            || input[9] != 0
            || input[32..56] != [0; 24]
            || input[24..32] != offset.to_le_bytes()
        {
            return Err(Error::Integrity("metadata identity".into()));
        }
        let len = u32::from_le_bytes(input[56..60].try_into().unwrap()) as usize;
        if len > PAYLOAD {
            return Err(Error::Integrity("metadata length".into()));
        }
        let mut h = Sha256::new();
        h.update(&input[..64]);
        h.update(self.id.as_bytes());
        h.update(&input[64..PAGE - 16]);
        if h.finalize()[..16] != input[PAGE - 16..] {
            return Err(Error::Integrity("metadata checksum".into()));
        }
        Ok(input[64..64 + len].to_vec())
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
        reserved: [u8; 24],
        data: &[u8; PAGE],
    ) -> Result<([u8; PAGE], [u8; 16])> {
        if reserved != [0; 24] {
            return Err(Error::Invalid("plaintext page reserved bytes".into()));
        }
        let mut h = Sha256::new();
        h.update(self.page_ad(page, version));
        h.update(data);
        Ok((*data, h.finalize()[..16].try_into().unwrap()))
    }
    pub fn decode_page(&self, page: u64, reference: PageRef, data: &mut [u8; PAGE]) -> Result<()> {
        let mut h = Sha256::new();
        h.update(self.page_ad(page, reference.version));
        h.update(&data[..]);
        if reference.nonce != [0; 24] || h.finalize()[..16] != reference.tag {
            return Err(Error::Integrity(format!("data page {page}")));
        }
        Ok(())
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
