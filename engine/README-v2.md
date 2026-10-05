# OverlayDisk V2 local container

V2 is a new format and API. V1 files and its ABI remain supported separately;
V2 never upgrades a V1 volume in place. Copy files through mounted disks to move
between formats. There is no Baidu or other network backend in this version.

## One persistent file

Everything required to reopen a volume is in its `.odv2` file: two configuration
copies, wrapped volume key, two authenticated roots, incremental COW index,
metadata journal, allocation state and snapshot catalog. There are no SQLite,
WAL, manifest or lock side files. The resident file handle holds an `fs2`
exclusive whole-file lock until close. Windows uses byte-range locking, not
`share_mode(0)`. Copy the complete file only after unmounting it cleanly.

The file is thin provisioned. Windows explicitly requests `FSCTL_SET_SPARSE`;
Unix file extension leaves holes. Growth reserves 64 MiB of address space at a
time and provides free 4 MiB object slots. The initial logical file length is
64 MiB, independent of the virtual capacity. This is not a 64 MiB eager payload
write. `inspect` reads the two independent 4 KiB configuration copies and file
length only, never the whole container. Its `authenticated=false` result is a
hint for displaying a password prompt, not trusted unlocked metadata.

The first 4 MiB is control space. Each following data or metadata object has:

| Region | Size |
| --- | ---: |
| Object identity header | 4 KiB |
| Descriptor table | 64 KiB |
| 1007 payload slots | 1007 × 4 KiB |
| Total | exactly 4 MiB |

Objects receive independent random UUIDs. Local physical slot numbers and
allocation generations are separate from these IDs. Data pages are encrypted
directly into final 4 KiB COW positions; an already committed payload position
is never overwritten while reachable. Unpublished tail slots from an interrupted
transaction may be reused with fresh nonces. Active descriptors are reconstructed
from committed authenticated metadata. A normal flush does not seal or fill the
remainder of an active object.

## Writes, flush and recovery

An ordinary write acknowledges the bounded memory cache. It is allowed to be
lost on process termination or power loss before persistence. The worker
persists dirty state approximately every 100 ms and wakes at 16 MiB. `flush`
is the durability barrier; the asynchronous adapter implements FUA using this
barrier. `read_persistent` reads the committed view, while ordinary reads include
pending writes and trims. Closing or dropping a Rust `Volume` stops the worker
and does not promise an implicit successful flush.

A commit freezes one dirty/trim prefix under a short state lock. It then releases
that lock during encoding, disk writes and synchronization. Readers see the
frozen changes over the old persistent root, followed by newer foreground cache
changes. Partial foreground writes use the same view. A commit mutex serializes
publication; an epoch lock prevents offline maintenance from recycling an extent
while reads or a commit use it. Multiple reads can perform positioned disk I/O
concurrently. Offline compaction and snapshot maintenance take exclusive epoch
ownership. Cold partial writes may still hold the state lock for a page read.

Commit order is:

1. Write new payload pages and synchronize the container.
2. Write authenticated page-map journal records and affected COW index pages;
   synchronize them before publishing a root.
3. Publish and synchronize one new root.
4. Mirror the same generation into the other root and synchronize it before
   reporting a successful flush.

Thus either root can recover the latest successfully acknowledged flush if the
other root is corrupt. Interrupted publication can recover either the previous
or the complete new commit, never a mixture. After selecting the latest valid
root, missing or invalid referenced metadata/data produces an integrity error;
the implementation does not silently substitute older file contents. Data
authentication is checked when pages are read. Metadata references carry hashes
and metadata frames have independent authenticated encryption.

The index uses 32-page leaves and 64-way internal nodes. Each batch rewrites only
affected leaves and ancestors. It does not serialize the complete page map on
each flush. Full-page trim is represented as merged page ranges; trimming a
whole sparse virtual disk clears the index without writing zeros across its
virtual capacity. Partial-page trim preserves the rest of that page.

Persistence errors become sticky and are returned by later writes/flushes.
The frozen and foreground cache remains available for reads until close, but
the application must not describe it as saved. Reopen recovers a complete
committed generation. An operation reporting an I/O error may have reached the
new complete commit if the error occurred during final publication.

## Encryption and integrity

Creation accepts either no password or a nonempty UTF-8 password. Encrypted
volumes use a random 256-bit volume key, wrapped with XChaCha20-Poly1305 under an
Argon2id v19 key (64 MiB, 3 iterations, parallelism 1). Passwords are not stored.
Each data page has an independent nonce and detached tag; associated data binds
volume UUID, logical page and version. Nonces are generated in batches from the
OS CSPRNG. Metadata pages, descriptors and exported manifests are also encrypted
and authenticated. Existing RustCrypto implementations provide the primitives.

Unencrypted volumes use integrity checksums, which detect accidental corruption;
they do not authenticate against someone able to edit and recompute them.
Encrypted file contents never need a plaintext temporary file. RAM, paging,
crash dumps and access by software while the virtual disk is unlocked are
outside this at-rest guarantee. The format does not prevent replacement of the
entire file by an older valid backup. There is no password recovery or in-place
change of encryption mode.

## Snapshots and object export

`snapshot_create` flushes and records a persistent point-in-time index. Only this
explicit operation seals the relevant objects. Physical identity headers remain
immutable: seal state, descriptor authentication information and deterministic
export-header material live in authenticated metadata. Descriptor writes are
synchronized before seal publication. An object already sealed for a live
snapshot is not changed by subsequent writes or compaction.

`snapshot_manifest` returns an object list with `{id, length: 4194304, kind}`, the
public wrapped-key configuration, and an authenticated manifest payload. The
payload contains logical-page-to-object-UUID/offset mappings and crypto tags,
plus a logical checkpoint location. Object IDs never encode physical offsets.
For a fixed snapshot the manifest and exported object bytes are stable.

`object_read(snapshot, id, offset, buffer)` streams exactly the corresponding
4 MiB logical object, including a synthesized stable 4 KiB sealed header,
descriptor bytes, payload and zero tail. It is not simply a raw slice of the
local file when a reused slot has an unused tail. The exported data remains
ciphertext for encrypted volumes. Exported local checkpoint frames still contain
local addressing internally; the logical page map is provided to support a
future backend/restore implementation. This version exports snapshots but does
not implement cloud transfer or snapshot import.

`snapshot_release` persists removal of the snapshot. Up to 1024 snapshots are
supported. Snapshot creation/export is explicit maintenance and can traverse
the allocated index; it is not a constant-time operation.

## Reclamation and memory bounds

There is no automatic online GC. Open and offline `compact` rebuild free extents
from objects reachable through both roots and snapshots. Normal long-running
overwrite workloads can therefore grow the file until offline maintenance.
Compaction repacks live pages into new UUID objects inside the same container,
publishes new roots, then reuses/truncates unreachable storage. It needs temporary
space in that same file. It never creates a second persistent container and
never moves/reuses snapshot-pinned objects. With live snapshots, referenced
metadata histories may also remain pinned. Releasing all snapshots lets a later
compaction discard obsolete seal history.

- Dirty page payload, including the frozen in-flight batch and newer foreground
  cache, is bounded by 64 MiB. A write that would exceed it applies backpressure.
  One call may not span more than 64 MiB of 4 KiB pages.
- At most 4096 foreground trim intervals are buffered before a barrier.
- The metadata node cache holds at most 1024 nodes (about 3 MiB of node payload,
  plus collection overhead). There is no in-memory complete page map.
- Coalesced data I/O windows are at most 1 MiB. Dirty-page snapshots captured by
  a read, request buffers, allocator reachability sets and exported manifests
  are additional temporary memory. A large manifest necessarily scales with
  the snapshot's allocated page count.
- The asynchronous request queue has its own bounded budget, defined by its
  adapter; that budget is separate from the 64 MiB storage cache.

Windows uses `FILE_FLAG_NO_BUFFERING | FILE_FLAG_OVERLAPPED`, 4096-byte aligned
buffers/offsets/lengths, explicit-offset I/O and per-operation completion events.
Consecutive page payloads are combined into larger reads/writes. API offsets and
lengths remain 512-byte aligned. The configured local filesystem must support
the sparse/direct-I/O features; the implementation returns an error otherwise.

## Functional validation

Run `cargo test --locked` (or the release-profile equivalent). V2 tests cover a
random byte-array model, partial writes/trim, whole sparse disk trim, exact and
coalesced I/O, bounded in-flight cache behavior, concurrent writers and reader
views during a paused commit, wrong password/tampering, injected disk-full
errors, actual child-process termination at each durability barrier, descriptor
publication interruption, mirrored-root recovery, hard failure for lost child
metadata, stable 4 MiB snapshot objects, compaction with pins, release, reopen
and single-file copy. These are correctness tests, not performance measurements.
Process-kill tests do not replace physical power-loss validation on target
storage hardware.
