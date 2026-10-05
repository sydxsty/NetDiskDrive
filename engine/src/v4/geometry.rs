//! Per-volume immutable transport/storage geometry. The explicit geometry is
//! mandatory in every current container and authenticated transport root.
use super::{Error, Result, OBJECT, PAGE};
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Geometry {
    pub object_size: u64,
    pub pages: u64,
    pub payload_pages: u64,
    pub slots: u64,
    pub external_limit: u64,
    pub external_stride: u64,
}
impl Geometry {
    pub fn new(object_size: u64) -> Result<Self> {
        if !matches!(object_size, 4_194_304 | 8_388_608 | 16_777_216) {
            return Err(Error::Invalid("object size must be 4, 8 or 16 MiB".into()));
        }
        let pages = object_size / PAGE as u64;
        let payload_pages = 1 + 16 * (object_size / OBJECT);
        Ok(Self {
            object_size,
            pages,
            payload_pages,
            slots: pages - payload_pages,
            external_limit: pages,
            external_stride: pages * 2,
        })
    }
    pub fn header_bytes(self) -> usize {
        self.payload_pages as usize * PAGE
    }
}
