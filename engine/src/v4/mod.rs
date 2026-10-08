//! Format 4: immutable transport objects and persistent incremental indexes.
mod cache;
mod cloud;
mod codec;
mod crypto_work;
mod geometry;
mod io;
mod lazy;
mod maintenance;
mod ntfs;
mod object_bytes;
#[cfg(test)]
mod logical_zero_tests;
mod portable_validation;
mod prepare_cache;
mod preparation;
mod receipts;
#[cfg(test)]
mod receipt_tests;
#[cfg(test)]
mod portable_validation_tests;
mod restore;
mod replica;
#[cfg(test)]
mod replica_tests;
mod store;
mod tree;
mod volume;
use codec::{Config, Crypto, MetaRef};
use geometry::Geometry;
use io::Device;
pub use lazy::{ObjectProvider, RemoteObject};
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    path::Path,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Condvar, Mutex,
    },
    thread::{self, JoinHandle},
    time::Duration,
};
use uuid::Uuid;
pub use volume::Volume;
pub const PAGE: usize = 4096;
pub const OBJECT: u64 = 4 * 1024 * 1024;
pub const MAX_CAPACITY: u64 = 8 * 1024 * 1024 * 1024 * 1024;
#[cfg(test)]
const SLOTS: u64 = 1007;
#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
    #[error("format error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("{0}")]
    Invalid(String),
    #[error("authentication or integrity failure: {0}")]
    Integrity(String),
    #[error("wrong password or damaged key envelope")]
    Password,
    #[error("volume already open")]
    Locked,
    #[error("persistence failed; reopen volume: {0}")]
    Background(String),
    #[error("remote object requires hydration: {0:?}")]
    Missing(RemoteObject),
    #[error("internal lock poisoned")]
    Poisoned,
}
pub type Result<T> = std::result::Result<T, Error>;
#[derive(Clone, Serialize, Debug)]
pub struct Info {
    pub id: Uuid,
    pub capacity_bytes: u64,
    pub encrypted: bool,
    pub page_size: usize,
    pub segment_size: u64,
    pub object_size: u64,
    pub format_version: u32,
    pub dirty_bytes: u64,
    pub allocated_pages: u64,
    pub segment_count: u64,
    pub authenticated: bool,
    pub snapshot_count: usize,
    pub background_error: Option<String>,
    pub data_generation: u64,
    pub published_generation: u64,
    pub restore_incomplete: bool,
}

#[cfg(test)]
mod base_tests;
#[cfg(test)]
mod fault_tests;
#[cfg(test)]
mod lazy_tests;

#[cfg(test)]
mod cache_tests;
#[cfg(test)]
mod reclaim_tests;

#[cfg(test)]
mod write_seal_tests;

#[cfg(test)]
mod geometry_tests;
#[cfg(test)]
mod storage_prepare_tests;
#[cfg(test)]
mod preparation_tests;
