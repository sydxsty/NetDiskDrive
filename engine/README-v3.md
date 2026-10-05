# Format 3 local disk and portable backups

Version 0.3 creates new `.odv3` files. There is no in-place migration. The release
for format 2 remains the way to open and manage old disks. The persisted format
number is 3, so the old format-2 writer rejects the file.

All disk bytes, COW index pages, wrapped encryption keys, snapshot catalogs,
cloud binding identifiers, upload jobs/pins/receipts, restore progress and
compaction progress are inside one file. Account cookies do not enter this
format. Encryption is fixed on creation. Ordinary writes acknowledge the cache;
Flush/FUA persist the accepted prefix. Closing the handle does not imply Flush.

## Native API

`include/overlaydisk_v3.h` is authoritative. A V3 handle uses the existing V2
read/write/trim/Flush/queue/completion/drain/close ABI. V2 snapshot and compaction
entrypoints reject V3 handles. Use the V3 controls instead. All symbols are Cdecl.

`od_v3_control` requires a 131072-byte output buffer **before** running the
command. It returns 0 or -1; `od_v3_last_error` is the calling thread's error.
Mutations run in the same ordered disk scheduler. Immutable export reads bypass
the scheduler and hold the relevant pin; a network upload never holds a disk
queue slot. A file remains single-writer, including during restore.

Controls include:

- `snapshot.list` (cursor/limit <= 128), `snapshot.create` (name),
  `snapshot.rename` (id/name), `snapshot.delete` (id). Task pins are visible but
  cannot be deleted as user snapshots.
- `cloud.bind` (backend_id/account_id/remote_root/device_id/enabled),
  `cloud.status`, `cloud.pause` (paused).
- `cloud.prepare`: first creates a durable upload snapshot; subsequent bounded
  steps accept max_pages (<= 4096) and max_objects (<= 4). Once a generation is
  already published it returns `{job:null,up_to_date:true,status:...}`.
- `cloud.list` / `cloud.objects` (job_id/cursor/limit <= 128),
  `cloud.published_objects` (cursor/limit <= 128).
- `cloud.receipt` (job_id/object_id/length/sha256/receipt) records verified remote
  storage. `cloud.commit` (job_id/root_object_id/root_sha256/receipt) is allowed
  only when all other objects have receipts. The caller must upload the commit,
  safely publish the latest pointer and read it back before committing locally.
- `restore.status` (cursor/limit <= 128), `restore.finish`.
- `compact.start`, `compact.step` (max_pages <= 1024), `compact.pause` (paused),
  `compact.status`. `estimated_reclaim_bytes` denotes reusable local extents;
  `reclaimed_bytes` counts the bytes actually truncated from the file's tail.
- `debug.blocks` (page/page_size <= 128/view="physical") returns bounded physical
  4-MiB object rows with allocation, snapshot/upload pins and receipt state.

JSON list cursors address the current list. Restart `restore.status` at cursor 0
after accepting a batch, since accepted objects disappear from `needed`.

## Portable object format

Every remote object is exactly 4 MiB and has a random immutable UUID. There are
four kinds: data, binary index, directory, and commit. The first eight bytes are
`ODV3OBJ1`. Headers identify the volume and object, format version, kind, nonce,
length and authentication tag. Neither a local extent number nor a local
physical byte offset is exported. SHA-256 in the authenticated parent binds the
whole child image.

Data objects preserve encrypted 4-KiB page payloads, zero the local descriptor
area, and zero the unused tail. Binary index records contain the logical page,
source object UUID, slot, page version, nonce and tag. A part covers at most 40
source objects and has a bounded size. Unchanged index/directory images reuse
their UUID, nonce and hash across backups. Directory payloads contain immutable
descriptors only; transient upload receipt flags never affect their content.
The small authenticated commit object carries the directory closure, source
configuration, capacity, snapshot identity and data generation. Recovering the
cloud volume therefore needs only the exact commit object plus its referenced
objects, not a separate complete page-map JSON file.

Only one upload snapshot is pinned at a time. New local writes advance the data
generation and coalesce behind it. Receipts are scoped to the active/latest
remote closure; obsolete receipts are discarded before remote garbage
collection can make them stale. The network coordinator owns account sessions,
retry policy, provider publication, latest-pointer verification and remote GC.

## Recovery and cleanup

Cloud RestoreBegin authenticates the commit with the source password and creates
a new local UUID/key. It uses the source encryption mode/password. The target
header starts with `restoring=true` and its authenticated root also requires
restore completion, so even an interrupted initialization cannot be mounted.
Changing the public header flag cannot bypass the authenticated gate. Each accepted object commits its pages and receipt atomically.
`restore.status` returns only a bounded list of needed objects, metadata first.
Close/reopen plus the target password resumes from persisted progress. If a
crash occurs after the authenticated empty target is created but before the
first restore state is committed, status returns `initializing/uninitialized`.
The caller closes that handle and retries RestoreBegin with the original
verified commit. Only an authenticated, empty, restore-required target can be
reseeded, and its local identity stays unchanged; ordinary disks and existing
restore progress are rejected. Finish
checks all receipts and the exact logical-page count, commits completion, then
clears both public restoring-header copies. The target inherits no cloud binding.

Local snapshot restore creates an independent target with the supplied **target**
password. A durable source pin plus an in-memory source Arc protect it even if
the source UI handle closes. Reopen the target and call
`od_v3_restore_snapshot_resume(target,unlocked_source)` after unlocking the source
to resume. If initialization was interrupted, retry the begin call with the
original source snapshot and target password. The engine discovers and reuses
an already durable source pin by target identity, even if the user later deleted
the original snapshot. If the source pin was not committed yet, it creates one
from the original snapshot. `snapshot.restore_step` copies bounded page batches. If completion
was durable but a crash interrupted source-unpin cleanup, status reports
`source_pin_cleanup_pending`; the same resume call on the completed target
finishes cleanup idempotently.

Compaction copies only captured pages whose current versions still match,
then marks other snapshots before recycling candidates. It never overwrites a
newer frontend write and does not advance logical data_generation. Pause and
progress survive reopening. Reused candidate identities are skipped safely.
At the end a full reference verification checks the current mirrored roots,
snapshots and cloud pins before truncating consecutive free tail extents. This
final verification can take longer than a bounded moving step. Interior free
extents are reused; this release does not promise sparse hole punching for them.
Resize/sync failure latches the handle for reopening, and no out-of-file extent
remains in its allocator.

Cloud catalogs use object-level records, append logs, bounded linked checkpoint
chunks, and obsolete identity/cache pruning. They do not materialize the block
page-map as JSON. The live object catalog currently remains resident in memory;
this is not a disk-paged key/value index. Checkpoint frequency scales with the
live catalog. Page-list/export APIs are bounded even when the catalog is large.

## Functional verification

`cargo test` covers prior V1/V2 behavior, the ordered queue and C ABI, encrypted
and plain full cloud export/restore/resume, immutable upload pins, incremental
index reuse, independent local snapshot copies, pause/reopen compaction,
concurrent writes, a sparse 1-TiB address boundary, checkpoint chains/pruning,
injected full-disk/resize/sync errors, and process kill at commit boundaries.
These are correctness tests, not performance measurements. Windows execution and
provider end-to-end tests are additional integration checks in the application.
