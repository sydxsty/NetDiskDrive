use argon2::{Algorithm, Argon2, Params, Version};
use chacha20poly1305::{
    aead::{Aead, KeyInit, Payload},
    XChaCha20Poly1305, XNonce,
};
use fs2::FileExt;
use rand::{rngs::OsRng, RngCore};
use rusqlite::{params, Connection, OpenFlags, OptionalExtension, Transaction};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, HashSet},
    fs::{self, File, OpenOptions},
    io::{Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    sync::{Mutex, MutexGuard},
};
use uuid::Uuid;
use zeroize::Zeroizing;

pub const PAGE_SIZE: usize = 4096;
pub const SEGMENT_SIZE: u64 = 4 * 1024 * 1024;
const HEADER_SIZE: u64 = 4096;
const RECORD_SIZE: u64 = 4160;
const RECORDS_PER_SEGMENT: u64 = (SEGMENT_SIZE - HEADER_SIZE) / RECORD_SIZE;
const MIN_CAPACITY: u64 = 64 * 1024 * 1024;
const FORMAT_VERSION: u32 = 1;
const SEGMENT_MAGIC: &[u8; 8] = b"ODSEG001";
const RECORD_MAGIC: &[u8; 8] = b"ODPAGE01";

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
    #[error("index error: {0}")]
    Sql(#[from] rusqlite::Error),
    #[error("format error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("{0}")]
    Invalid(String),
    #[error("wrong password or damaged key envelope")]
    Password,
    #[error("page authentication failed: {0}")]
    Integrity(u64),
    #[error("volume is already open in another process")]
    Locked,
    #[error("internal lock poisoned; close and reopen the volume")]
    Poisoned,
}
pub type Result<T> = std::result::Result<T, Error>;

#[derive(Serialize, Deserialize)]
struct Manifest {
    format_version: u32,
    id: Uuid,
    capacity_bytes: u64,
    page_size: usize,
    segment_size: u64,
    encryption: Option<Envelope>,
}

#[derive(Serialize, Deserialize)]
struct Envelope {
    algorithm: String,
    kdf: String,
    salt: [u8; 16],
    nonce: [u8; 24],
    wrapped_key: Vec<u8>,
}

#[derive(Serialize, Debug)]
pub struct VolumeInfo {
    pub id: Uuid,
    pub capacity_bytes: u64,
    pub segment_size: u64,
    pub page_size: usize,
    pub encrypted: bool,
    pub allocated_pages: u64,
    pub segment_count: u64,
    pub stored_records: u64,
    pub obsolete_records: u64,
    pub format_version: u32,
}

pub struct Volume {
    dir: PathBuf,
    manifest: Manifest,
    key: Option<Zeroizing<[u8; 32]>>,
    inner: Mutex<Inner>,
    // Keep the advisory lock alive until the connection and writer have closed.
    _lock: VolumeLock,
}

struct VolumeLock(File);
impl Drop for VolumeLock {
    fn drop(&mut self) {
        // This field drops after the database/writer. Explicit unlock also
        // releases a lock inherited temporarily by a concurrently forked child.
        let _ = FileExt::unlock(&self.0);
    }
}

struct Inner {
    conn: Connection,
    writer: SegmentWriter,
}

#[derive(Clone, Copy)]
struct Location {
    segment: u64,
    slot: u64,
    version: u64,
}

struct SegmentWriter {
    dir: PathBuf,
    id: Uuid,
    active: Option<(u64, File, u64)>,
    next_segment: u64,
    stored_records: u64,
}

fn invalid(message: impl Into<String>) -> Error {
    Error::Invalid(message.into())
}

fn sync_dir(path: &Path) -> Result<()> {
    #[cfg(unix)]
    File::open(path)?.sync_all()?;
    #[cfg(not(unix))]
    let _ = path;
    Ok(())
}

fn segment_path(dir: &Path, id: u64) -> PathBuf {
    dir.join(format!("segment-{id:016x}.ods"))
}

fn segments(dir: &Path) -> Result<Vec<(u64, PathBuf)>> {
    let mut result = Vec::new();
    for entry in fs::read_dir(dir)? {
        let entry = entry?;
        let name = entry.file_name();
        let name = name.to_string_lossy();
        if let Some(hex) = name
            .strip_prefix("segment-")
            .and_then(|x| x.strip_suffix(".ods"))
        {
            if hex.len() != 16 {
                return Err(invalid("malformed segment filename"));
            }
            let id =
                u64::from_str_radix(hex, 16).map_err(|_| invalid("malformed segment filename"))?;
            if !entry.file_type()?.is_file() {
                return Err(invalid("segment is not a regular file"));
            }
            result.push((id, entry.path()));
        }
    }
    result.sort_unstable_by_key(|x| x.0);
    Ok(result)
}

fn manifest_ad(m: &Manifest) -> Vec<u8> {
    let mut ad = b"OverlayDisk key envelope v1".to_vec();
    ad.extend_from_slice(m.id.as_bytes());
    ad.extend_from_slice(&m.capacity_bytes.to_le_bytes());
    ad.extend_from_slice(&(m.page_size as u64).to_le_bytes());
    ad.extend_from_slice(&m.segment_size.to_le_bytes());
    ad
}

fn derive_key(password: &str, salt: &[u8; 16]) -> Result<Zeroizing<[u8; 32]>> {
    let params = Params::new(65536, 3, 1, Some(32)).map_err(|e| invalid(e.to_string()))?;
    let argon = Argon2::new(Algorithm::Argon2id, Version::V0x13, params);
    let mut key = Zeroizing::new([0u8; 32]);
    argon
        .hash_password_into(password.as_bytes(), salt, key.as_mut())
        .map_err(|e| invalid(e.to_string()))?;
    Ok(key)
}

fn open_key(m: &Manifest, password: Option<&str>) -> Result<Option<Zeroizing<[u8; 32]>>> {
    match &m.encryption {
        None => {
            if password.is_some() {
                return Err(invalid("this volume is not encrypted; omit the password"));
            }
            Ok(None)
        }
        Some(e) => {
            if e.algorithm != "XChaCha20-Poly1305"
                || e.kdf != "Argon2id-v19-m65536-t3-p1"
                || e.wrapped_key.len() != 48
            {
                return Err(invalid("unsupported key envelope"));
            }
            let password = password.ok_or(Error::Password)?;
            let wrapping = derive_key(password, &e.salt)?;
            let cipher = XChaCha20Poly1305::new_from_slice(wrapping.as_ref())
                .map_err(|_| Error::Password)?;
            let bytes = Zeroizing::new(
                cipher
                    .decrypt(
                        XNonce::from_slice(&e.nonce),
                        Payload {
                            msg: &e.wrapped_key,
                            aad: &manifest_ad(m),
                        },
                    )
                    .map_err(|_| Error::Password)?,
            );
            let mut key = Zeroizing::new([0; 32]);
            if bytes.len() != 32 {
                return Err(Error::Password);
            }
            key.copy_from_slice(&bytes);
            Ok(Some(key))
        }
    }
}

impl SegmentWriter {
    fn open(dir: &Path, id: Uuid, referenced: &HashMap<u64, u64>) -> Result<Self> {
        let files = segments(dir)?;
        let mut writer = Self {
            dir: dir.to_path_buf(),
            id,
            active: None,
            next_segment: 0,
            stored_records: 0,
        };
        let mut orphans = Vec::new();
        for (segment, path) in files {
            writer.next_segment = segment
                .checked_add(1)
                .ok_or_else(|| invalid("segment id exhausted"))?;
            let Some(committed_end) = referenced.get(&segment) else {
                // No committed mapping can observe an orphan, including a
                // partially initialized file left by a killed process.
                orphans.push(path);
                continue;
            };
            if *committed_end > RECORDS_PER_SEGMENT {
                return Err(invalid("index slot exceeds segment capacity"));
            }
            let mut file = OpenOptions::new().read(true).write(true).open(path)?;
            let valid_size = file.metadata()?.len() == SEGMENT_SIZE;
            let mut header = [0u8; 32];
            let valid_header = file.read_exact(&mut header).is_ok()
                && &header[..8] == SEGMENT_MAGIC
                && &header[8..24] == id.as_bytes()
                && header[24..32] == segment.to_le_bytes();
            if !valid_size || !valid_header {
                return Err(invalid(
                    "referenced segment has invalid size, header or identity",
                ));
            }
            // A nonzero record header, including a torn record, consumes its slot.
            // Scan the complete segment so malformed gaps cannot cause overwrites.
            let mut used = *committed_end;
            file.seek(SeekFrom::Start(HEADER_SIZE + committed_end * RECORD_SIZE))?;
            let mut tail = Zeroizing::new(vec![
                0u8;
                ((RECORDS_PER_SEGMENT - committed_end) * RECORD_SIZE)
                    as usize
            ]);
            file.read_exact(&mut tail)?;
            for (index, record) in tail.chunks_exact(RECORD_SIZE as usize).enumerate() {
                if record[..8] != [0; 8] {
                    used = committed_end + index as u64 + 1;
                }
            }
            writer.stored_records += used;
            writer.active = Some((segment, file, used));
        }
        for segment in referenced.keys() {
            if !segment_path(dir, *segment).is_file() {
                return Err(invalid("index references a missing segment"));
            }
        }
        // Reclaim only after every referenced segment passed validation.
        for path in orphans {
            fs::remove_file(path)?;
        }
        Ok(writer)
    }

    fn rotate(&mut self) -> Result<()> {
        self.sync()?;
        let segment = self.next_segment;
        self.next_segment = segment
            .checked_add(1)
            .ok_or_else(|| invalid("segment id exhausted"))?;
        let mut file = OpenOptions::new()
            .read(true)
            .write(true)
            .create_new(true)
            .open(segment_path(&self.dir, segment))?;
        file.set_len(SEGMENT_SIZE)?;
        let mut header = [0u8; HEADER_SIZE as usize];
        header[..8].copy_from_slice(SEGMENT_MAGIC);
        header[8..24].copy_from_slice(self.id.as_bytes());
        header[24..32].copy_from_slice(&segment.to_le_bytes());
        file.write_all(&header)?;
        file.sync_all()?;
        sync_dir(&self.dir)?;
        self.active = Some((segment, file, 0));
        Ok(())
    }

    fn append(&mut self, record: &[u8; RECORD_SIZE as usize], version: u64) -> Result<Location> {
        if self
            .active
            .as_ref()
            .map(|x| x.2 == RECORDS_PER_SEGMENT)
            .unwrap_or(true)
        {
            self.rotate()?;
        }
        let (segment, file, next_slot) = self.active.as_mut().expect("active segment");
        let slot = *next_slot;
        *next_slot += 1;
        self.stored_records += 1;
        file.seek(SeekFrom::Start(HEADER_SIZE + slot * RECORD_SIZE))?;
        file.write_all(record)?;
        Ok(Location {
            segment: *segment,
            slot,
            version,
        })
    }

    fn sync(&mut self) -> Result<()> {
        if let Some((_, file, _)) = &mut self.active {
            file.sync_all()?;
        }
        sync_dir(&self.dir)
    }
}

impl Volume {
    pub fn create(path: impl AsRef<Path>, capacity: u64, password: Option<&str>) -> Result<Self> {
        if capacity < MIN_CAPACITY
            || !capacity.is_multiple_of(512)
            || capacity > i64::MAX as u64 - PAGE_SIZE as u64
        {
            return Err(invalid("capacity must be at least 64 MiB, 512-byte aligned, and fit signed 64-bit addressing"));
        }
        if password == Some("") {
            return Err(invalid("encrypted volumes require a nonempty password"));
        }
        let path = path.as_ref();
        match fs::create_dir(path) {
            Ok(()) => (),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists && path.is_dir() => (),
            Err(e) => return Err(e.into()),
        }
        if fs::read_dir(path)?.next().is_some() {
            return Err(invalid("create requires a new or empty directory"));
        }
        let lock = OpenOptions::new()
            .read(true)
            .write(true)
            .create_new(true)
            .open(path.join("volume.lock"))?;
        FileExt::try_lock_exclusive(&lock).map_err(|_| Error::Locked)?;
        let mut manifest = Manifest {
            format_version: FORMAT_VERSION,
            id: Uuid::new_v4(),
            capacity_bytes: capacity,
            page_size: PAGE_SIZE,
            segment_size: SEGMENT_SIZE,
            encryption: None,
        };
        let key = if let Some(password) = password {
            let mut key = Zeroizing::new([0u8; 32]);
            OsRng.fill_bytes(key.as_mut());
            let mut salt = [0u8; 16];
            let mut nonce = [0u8; 24];
            OsRng.fill_bytes(&mut salt);
            OsRng.fill_bytes(&mut nonce);
            let wrapping = derive_key(password, &salt)?;
            let cipher = XChaCha20Poly1305::new_from_slice(wrapping.as_ref())
                .map_err(|_| Error::Password)?;
            let wrapped_key = cipher
                .encrypt(
                    XNonce::from_slice(&nonce),
                    Payload {
                        msg: key.as_ref(),
                        aad: &manifest_ad(&manifest),
                    },
                )
                .map_err(|_| Error::Password)?;
            manifest.encryption = Some(Envelope {
                algorithm: "XChaCha20-Poly1305".into(),
                kdf: "Argon2id-v19-m65536-t3-p1".into(),
                salt,
                nonce,
                wrapped_key,
            });
            Some(key)
        } else {
            None
        };
        let mut meta = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(path.join("volume.json"))?;
        meta.write_all(&serde_json::to_vec_pretty(&manifest)?)?;
        meta.sync_all()?;
        let conn = Connection::open(path.join("index.sqlite3"))?;
        configure(&conn)?;
        conn.execute_batch("BEGIN IMMEDIATE; CREATE TABLE state (key TEXT PRIMARY KEY, value TEXT NOT NULL); CREATE TABLE pages (page INTEGER PRIMARY KEY CHECK(page>=0), segment INTEGER NOT NULL, slot INTEGER NOT NULL, version INTEGER NOT NULL); CREATE INDEX pages_segment ON pages(segment); COMMIT;")?;
        conn.execute(
            "INSERT INTO state VALUES('id',?1),('capacity',?2),('generation','0')",
            params![manifest.id.to_string(), capacity.to_string()],
        )?;
        sync_dir(path)?;
        if let Some(parent) = path.parent() {
            sync_dir(parent)?;
        }
        let writer = SegmentWriter::open(path, manifest.id, &HashMap::new())?;
        Ok(Self {
            dir: path.to_path_buf(),
            manifest,
            key,
            inner: Mutex::new(Inner { conn, writer }),
            _lock: VolumeLock(lock),
        })
    }

    pub fn open(path: impl AsRef<Path>, password: Option<&str>) -> Result<Self> {
        let path = path.as_ref();
        let lock = OpenOptions::new()
            .read(true)
            .write(true)
            .open(path.join("volume.lock"))?;
        FileExt::try_lock_exclusive(&lock).map_err(|_| Error::Locked)?;
        let manifest: Manifest = serde_json::from_reader(File::open(path.join("volume.json"))?)?;
        if manifest.format_version != FORMAT_VERSION
            || manifest.page_size != PAGE_SIZE
            || manifest.segment_size != SEGMENT_SIZE
            || manifest.capacity_bytes < MIN_CAPACITY
            || !manifest.capacity_bytes.is_multiple_of(512)
            || manifest.capacity_bytes > i64::MAX as u64 - PAGE_SIZE as u64
        {
            return Err(invalid("unsupported or invalid volume format"));
        }
        let key = open_key(&manifest, password)?;
        let conn = Connection::open_with_flags(
            path.join("index.sqlite3"),
            OpenFlags::SQLITE_OPEN_READ_WRITE,
        )?;
        configure(&conn)?;
        let id: String =
            conn.query_row("SELECT value FROM state WHERE key='id'", [], |r| r.get(0))?;
        let capacity: String =
            conn.query_row("SELECT value FROM state WHERE key='capacity'", [], |r| {
                r.get(0)
            })?;
        if id != manifest.id.to_string() || capacity != manifest.capacity_bytes.to_string() {
            return Err(invalid("index belongs to a different volume"));
        }
        let writer = SegmentWriter::open(path, manifest.id, &referenced_segments(&conn)?)?;
        let volume = Self {
            dir: path.to_path_buf(),
            manifest,
            key,
            inner: Mutex::new(Inner { conn, writer }),
            _lock: VolumeLock(lock),
        };
        // Orphan segments have been reclaimed; uncommitted tails in live
        // segments were skipped and cannot replace committed page mappings.
        Ok(volume)
    }

    fn inner(&self) -> Result<MutexGuard<'_, Inner>> {
        self.inner.lock().map_err(|_| Error::Poisoned)
    }
    pub fn capacity(&self) -> u64 {
        self.manifest.capacity_bytes
    }

    fn bounds(&self, offset: u64, length: u64) -> Result<()> {
        if !offset.is_multiple_of(512) || !length.is_multiple_of(512) {
            return Err(invalid("I/O offset and length must be 512-byte aligned"));
        }
        if offset
            .checked_add(length)
            .filter(|x| *x <= self.capacity())
            .is_none()
        {
            return Err(invalid("I/O range exceeds volume capacity"));
        }
        Ok(())
    }

    fn page_ad(&self, page: u64, version: u64) -> Vec<u8> {
        let mut ad = b"OverlayDisk page v1".to_vec();
        ad.extend_from_slice(self.manifest.id.as_bytes());
        ad.extend_from_slice(&page.to_le_bytes());
        ad.extend_from_slice(&version.to_le_bytes());
        ad
    }

    fn encode(
        &self,
        page: u64,
        version: u64,
        data: &[u8; PAGE_SIZE],
    ) -> Result<[u8; RECORD_SIZE as usize]> {
        let mut record = [0u8; RECORD_SIZE as usize];
        record[..8].copy_from_slice(RECORD_MAGIC);
        record[8..16].copy_from_slice(&page.to_le_bytes());
        record[16..24].copy_from_slice(&version.to_le_bytes());
        OsRng.fill_bytes(&mut record[24..48]);
        let ad = self.page_ad(page, version);
        if let Some(key) = &self.key {
            let cipher = XChaCha20Poly1305::new_from_slice(key.as_ref())
                .map_err(|_| invalid("invalid encryption key"))?;
            let encrypted = cipher
                .encrypt(
                    XNonce::from_slice(&record[24..48]),
                    Payload {
                        msg: data,
                        aad: &ad,
                    },
                )
                .map_err(|_| invalid("page encryption failed"))?;
            record[48..].copy_from_slice(&encrypted);
        } else {
            record[48..48 + PAGE_SIZE].copy_from_slice(data);
            let mut hash = Sha256::new();
            hash.update(&ad);
            hash.update(&record[24..48]);
            hash.update(data);
            record[48 + PAGE_SIZE..].copy_from_slice(&hash.finalize()[..16]);
        }
        Ok(record)
    }

    fn read_page(&self, conn: &Connection, page: u64) -> Result<Zeroizing<[u8; PAGE_SIZE]>> {
        let mut output = Zeroizing::new([0u8; PAGE_SIZE]);
        let location = location(conn, page)?;
        let Some(location) = location else {
            return Ok(output);
        };
        if location.slot >= RECORDS_PER_SEGMENT {
            return Err(Error::Integrity(page));
        }
        let mut file = File::open(segment_path(&self.dir, location.segment))?;
        file.seek(SeekFrom::Start(HEADER_SIZE + location.slot * RECORD_SIZE))?;
        let mut record = Zeroizing::new([0u8; RECORD_SIZE as usize]);
        file.read_exact(record.as_mut())?;
        if &record[..8] != RECORD_MAGIC
            || record[8..16] != page.to_le_bytes()
            || record[16..24] != location.version.to_le_bytes()
        {
            return Err(Error::Integrity(page));
        }
        let ad = self.page_ad(page, location.version);
        if let Some(key) = &self.key {
            let cipher = XChaCha20Poly1305::new_from_slice(key.as_ref())
                .map_err(|_| Error::Integrity(page))?;
            let plain = Zeroizing::new(
                cipher
                    .decrypt(
                        XNonce::from_slice(&record[24..48]),
                        Payload {
                            msg: &record[48..],
                            aad: &ad,
                        },
                    )
                    .map_err(|_| Error::Integrity(page))?,
            );
            if plain.len() != PAGE_SIZE {
                return Err(Error::Integrity(page));
            }
            output.copy_from_slice(&plain);
        } else {
            let mut hash = Sha256::new();
            hash.update(&ad);
            hash.update(&record[24..48 + PAGE_SIZE]);
            if hash.finalize()[..16] != record[48 + PAGE_SIZE..] {
                return Err(Error::Integrity(page));
            }
            output.copy_from_slice(&record[48..48 + PAGE_SIZE]);
        }
        Ok(output)
    }

    pub fn read(&self, offset: u64, output: &mut [u8]) -> Result<()> {
        self.bounds(offset, output.len() as u64)?;
        let inner = self.inner()?;
        let mut done = 0;
        while done < output.len() {
            let absolute = offset + done as u64;
            let page = absolute / PAGE_SIZE as u64;
            let within = (absolute % PAGE_SIZE as u64) as usize;
            let amount = (PAGE_SIZE - within).min(output.len() - done);
            let bytes = self.read_page(&inner.conn, page)?;
            output[done..done + amount].copy_from_slice(&bytes[within..within + amount]);
            done += amount;
        }
        Ok(())
    }

    /// A successful write is durable on local storage. Cloud persistence is not implemented.
    pub fn write(&self, offset: u64, input: &[u8]) -> Result<()> {
        self.bounds(offset, input.len() as u64)?;
        if input.is_empty() {
            return Ok(());
        }
        let mut inner = self.inner()?;
        let Inner { conn, writer } = &mut *inner;
        let tx = conn.transaction()?;
        let version = next_generation(&tx)?;
        let mut done = 0;
        while done < input.len() {
            let absolute = offset + done as u64;
            let page = absolute / PAGE_SIZE as u64;
            let within = (absolute % PAGE_SIZE as u64) as usize;
            let amount = (PAGE_SIZE - within).min(input.len() - done);
            let mut data = if within == 0 && amount == PAGE_SIZE {
                Zeroizing::new([0u8; PAGE_SIZE])
            } else {
                self.read_page(&tx, page)?
            };
            data[within..within + amount].copy_from_slice(&input[done..done + amount]);
            self.store_page(&tx, writer, page, version, &data)?;
            done += amount;
        }
        writer.sync()?;
        tx.commit()?;
        Ok(())
    }

    fn store_page(
        &self,
        tx: &Transaction<'_>,
        writer: &mut SegmentWriter,
        page: u64,
        version: u64,
        data: &[u8; PAGE_SIZE],
    ) -> Result<()> {
        if data.iter().all(|b| *b == 0) {
            tx.execute("DELETE FROM pages WHERE page=?1", [page])?;
        } else {
            let record = Zeroizing::new(self.encode(page, version, data)?);
            let loc = writer.append(&record, version)?;
            tx.execute("INSERT INTO pages(page,segment,slot,version) VALUES(?1,?2,?3,?4) ON CONFLICT(page) DO UPDATE SET segment=excluded.segment,slot=excluded.slot,version=excluded.version", params![page, loc.segment, loc.slot, loc.version])?;
        }
        Ok(())
    }

    pub fn trim(&self, offset: u64, length: u64) -> Result<()> {
        self.bounds(offset, length)?;
        if length == 0 {
            return Ok(());
        }
        let end = offset + length;
        let first = offset / PAGE_SIZE as u64;
        let last = (end - 1) / PAGE_SIZE as u64;
        let mut inner = self.inner()?;
        let Inner { conn, writer } = &mut *inner;
        let tx = conn.transaction()?;
        let version = next_generation(&tx)?;
        let full_start = offset.div_ceil(PAGE_SIZE as u64);
        let full_end = end / PAGE_SIZE as u64;
        tx.execute(
            "DELETE FROM pages WHERE page>=?1 AND page<?2",
            params![full_start, full_end],
        )?;
        for page in [first, last].into_iter().collect::<HashSet<_>>() {
            let page_start = page * PAGE_SIZE as u64;
            let start = offset.saturating_sub(page_start).min(PAGE_SIZE as u64) as usize;
            let stop = (end - page_start).min(PAGE_SIZE as u64) as usize;
            if start == 0 && stop == PAGE_SIZE {
                continue;
            }
            let mut data = self.read_page(&tx, page)?;
            data[start..stop].fill(0);
            self.store_page(&tx, writer, page, version, &data)?;
        }
        writer.sync()?;
        tx.commit()?;
        // Garbage collection is maintenance, not part of the acknowledged trim.
        // Leave it to reopen/compact so a removal failure cannot report that a
        // successfully committed trim was unsuccessful.
        Ok(())
    }

    pub fn flush(&self) -> Result<()> {
        let mut inner = self.inner()?;
        inner.writer.sync()?;
        // Writes already sync their WAL commits. A passive checkpoint advances
        // the database without waiting for unrelated SQLite readers. Never run
        // whole-volume GC on a filesystem's frequent FLUSH path.
        inner
            .conn
            .execute_batch("PRAGMA wal_checkpoint(PASSIVE);")?;
        sync_dir(&self.dir)
    }

    pub fn info(&self) -> Result<VolumeInfo> {
        let inner = self.inner()?;
        let allocated_pages: u64 = inner
            .conn
            .query_row("SELECT count(*) FROM pages", [], |r| r.get(0))?;
        Ok(VolumeInfo {
            id: self.manifest.id,
            capacity_bytes: self.capacity(),
            segment_size: SEGMENT_SIZE,
            page_size: PAGE_SIZE,
            encrypted: self.key.is_some(),
            allocated_pages,
            segment_count: segments(&self.dir)?.len() as u64,
            stored_records: inner.writer.stored_records,
            obsolete_records: inner.writer.stored_records.saturating_sub(allocated_pages),
            format_version: FORMAT_VERSION,
        })
    }

    /// Compact while the virtual device is unmounted. Uses temporary disk space
    /// roughly equal to live payload and commits all replacement mappings at once.
    pub fn compact(&self) -> Result<()> {
        let mut inner = self.inner()?;
        let pages: Vec<u64> = {
            let mut statement = inner.conn.prepare("SELECT page FROM pages ORDER BY page")?;
            let values = statement
                .query_map([], |r| r.get(0))?
                .collect::<std::result::Result<Vec<_>, _>>()?;
            values
        };
        if !pages.is_empty() {
            inner.writer.rotate()?;
        }
        let Inner { conn, writer } = &mut *inner;
        let tx = conn.transaction()?;
        let version = next_generation(&tx)?;
        for page in pages {
            let data = self.read_page(&tx, page)?;
            self.store_page(&tx, writer, page, version, &data)?;
        }
        writer.sync()?;
        tx.commit()?;
        self.collect_garbage(&mut inner)?;
        sync_dir(&self.dir)
    }

    fn collect_garbage(&self, inner: &mut Inner) -> Result<()> {
        let referenced = referenced_segments(&inner.conn)?;
        // All handles must be closed before deletion on Windows.
        inner.writer.sync()?;
        inner.writer.active = None;
        for (id, path) in segments(&self.dir)? {
            if !referenced.contains_key(&id) {
                fs::remove_file(path)?;
            }
        }
        let previous_next = inner.writer.next_segment;
        inner.writer = SegmentWriter::open(&self.dir, self.manifest.id, &referenced)?;
        inner.writer.next_segment = inner.writer.next_segment.max(previous_next);
        Ok(())
    }
}

fn configure(conn: &Connection) -> Result<()> {
    conn.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA trusted_schema=OFF;")?;
    Ok(())
}

fn referenced_segments(conn: &Connection) -> Result<HashMap<u64, u64>> {
    let mut statement = conn.prepare("SELECT segment,max(slot)+1 FROM pages GROUP BY segment")?;
    let values = statement
        .query_map([], |r| Ok((r.get(0)?, r.get(1)?)))?
        .collect::<std::result::Result<HashMap<_, _>, _>>()?;
    Ok(values)
}

fn location(conn: &Connection, page: u64) -> Result<Option<Location>> {
    Ok(conn
        .query_row(
            "SELECT segment,slot,version FROM pages WHERE page=?1",
            [page],
            |r| {
                Ok(Location {
                    segment: r.get(0)?,
                    slot: r.get(1)?,
                    version: r.get(2)?,
                })
            },
        )
        .optional()?)
}

fn next_generation(tx: &Transaction<'_>) -> Result<u64> {
    let value: String =
        tx.query_row("SELECT value FROM state WHERE key='generation'", [], |r| {
            r.get(0)
        })?;
    let generation = value
        .parse::<u64>()
        .map_err(|_| invalid("invalid generation counter"))?
        .checked_add(1)
        .filter(|x| *x <= i64::MAX as u64)
        .ok_or_else(|| invalid("generation counter exhausted"))?;
    tx.execute(
        "UPDATE state SET value=?1 WHERE key='generation'",
        [generation.to_string()],
    )?;
    Ok(generation)
}

#[cfg(test)]
mod tests;
