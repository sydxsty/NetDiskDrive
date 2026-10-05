> V2 应用使用单文件核心与独立异步 ABI，见 [README-v2.md](README-v2.md)。以下内容保留说明 V1 核心。

# OverlayDisk local core v1

`overlaydisk_core` is a Rust library and C-compatible shared library for a local,
512-byte-addressed virtual disk. It does not implement GPT, NTFS, a Windows
device, or a cloud backend. The Windows host supplies the block-device adapter.

## Storage format and durability

- Capacity is fixed at creation, at least 64 MiB, and a multiple of 512 bytes.
- Logical pages are 4096 bytes. Missing pages read as zero; partial writes use
  read-modify-write under the same volume mutex. Every operation is thread safe;
  commits are serialized, including overlapping writes and trim.
- Physical data files are exactly 4 MiB, named `segment-<16 hex digits>.ods`.
  Each has a 4096-byte identity header and 1007 fixed 4160-byte records. Remaining
  space is zero padding. Creating a segment extends the file immediately;
  allocation may be sparse on the host filesystem. Metadata sidecars
  (`volume.json`, SQLite files, lock) are deliberately not 4 MiB data segments.
- Records contain format marker, logical page number, generation, 24-byte nonce,
  and a 4096-byte payload plus 16-byte authenticator. Records are never edited.
  Abandoned/torn slots are skipped on recovery.
- The SQLite index uses WAL mode and `synchronous=FULL`. A write appends all
  replacement records, syncs every affected segment, then atomically commits
  the new page mappings. Successful write/trim already means locally durable;
  `flush` additionally attempts a nonblocking SQLite checkpoint. It never scans
  the whole volume or reclaims segments on the filesystem's flush path.
- A killed process before index commit leaves the previous complete mapping.
  A killed process after commit sees the new mapping. Orphan segment files,
  including interrupted initialization, are reclaimed only after live segment
  headers have been validated. Lost or damaged referenced segments fail open.
- A process-level exclusive file lock prevents a second writable open. Callers
  must not copy or independently edit an open volume directory. Close/unmount
  before copying a volume; copying only the database without its WAL is unsafe.
- Filesystem/device flush guarantees ultimately depend on the host storage.
  POSIX builds also sync directory metadata. Windows builds flush data through
  `File::sync_all` and SQLite's Windows VFS; no generic directory fsync is used.
  Process-kill recovery is tested separately from power-loss resilience.

## Encryption and trust boundary

A NULL password selects unencrypted storage. A nonempty password at creation
selects XChaCha20-Poly1305 with a random 256-bit volume key. Argon2id v19
(64 MiB, 3 iterations, parallelism 1) derives a wrapping key; the volume key is
authenticated and wrapped in `volume.json`. Each written page has an independent
random 192-bit nonce. Associated data includes the volume UUID, page number,
and generation. The key envelope also binds UUID, capacity, and format sizes.
Passwords and keys use zeroizing buffers in Rust. File content reaches disk only
as ciphertext when encryption is enabled, including abandoned record versions.

Clear metadata reveals volume capacity, page allocation and update patterns.
The SQLite mapping is not an authenticated Merkle tree: this v1 does not claim
whole-volume rollback resistance or detection of malicious mapping deletion.
Authenticated pages detect corruption/substitution on reads; the unencrypted
mode uses a truncated SHA-256 checksum for accidental corruption, not protection
against an attacker who can recompute checksums. Encryption does not protect a
mounted disk from malware or other software with permission to read it. RAM,
OS paging and crash dumps are outside this storage-format guarantee. There is
no password recovery or in-place change of encryption mode in v1.

## Space reclamation

`trim` removes full-page mappings and zeroes partial-page ranges atomically.
All-zero writes also become holes. Reopen discards completely dead segments;
`flush` only persists data and checkpoints, so frequent NTFS flushes do not
trigger whole-volume maintenance. Partially live segments require `compact`: while the device is
unmounted, it writes live pages into fresh segments, commits all new mappings,
then deletes obsolete segments. It needs temporary space approximately equal to
the live payload and holds the volume mutex throughout. Failure before commit
preserves the original mappings. There is no automatic live compaction in v1.
Trim is logical deletion, not secure erasure; old versions can remain until
compaction and host storage can retain deleted blocks.

## C ABI

See `include/overlaydisk_core.h`. Strings are UTF-8 and NUL terminated. `od_open`
returns an owned handle or NULL; operations return 0 or -1, and `od_last_error`
returns a thread-local diagnostic that remains valid until the next ABI call on
that thread. `od_capacity` returns 0 on error. `od_info` writes NUL-terminated
JSON; a 1024-byte buffer is sufficient for v1. An undersized buffer returns -1
and reports the required size. Reads are zeroed on error at the ABI boundary.

The caller owns all buffers and must provide valid pointers and lengths. Multiple
threads may use one handle, but closing it while another call is active, reusing
a closed handle, or supplying invalid pointers is undefined behavior. Rust
panics are caught at the boundary; pointer misuse cannot be caught. `od_close`
does not perform optional maintenance; previously acknowledged writes are
already durable. Call `od_flush` before clean unmount to report data-sync and
checkpoint failures. `od_compact` is an additional offline maintenance entrypoint.

## Build and validation

```text
cargo build --release --locked
cargo test --locked
cargo test --locked killed_process_recovers -- --nocapture
```

The tests cover zero holes, cross-page partial I/O, final partial capacity page,
bounds/overflow checks, concurrent overlapping and disjoint writes, trim,
fixed-size segment rollover, compaction, exclusive lock, wrong password,
encrypted-content absence from physical files, tampering, truncated orphans,
missing referenced segments, injected SQLite-full rollback, and actual
child-process termination before/after a commit. The process-kill
tests use this crate's test executable and work on Windows as well as Linux.

For an external Windows test harness, build the `crash_probe` example and run
`crash_probe create <directory>`, then `crash_probe write-loop <directory>`.
After reading `COMMITTED <generation>` on stdout, terminate that process forcibly
and run `crash_probe verify <directory>`; it checks a complete generation across
all 256 KiB, allowing the last observed generation or a later complete one.
