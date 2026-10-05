//! Local transactional block storage. See README.md for persistence guarantees.
mod async_io;
#[cfg(test)]
mod async_io_tests;
mod async_v4;
mod ffi;
mod ffi_v2;
mod ffi_v3;
mod ffi_v4;
mod storage;
pub mod v2;
pub mod v4;
pub use storage::{Error, Result, Volume, VolumeInfo, PAGE_SIZE, SEGMENT_SIZE};
