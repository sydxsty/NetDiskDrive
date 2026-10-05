# Current OverlayDisk container format

OverlayDisk 0.7 uses the `.odv4` extension and the `v4::Volume` / `od_v4_*`
interfaces, with the new **ODV4CFGO** configuration magic. Every configuration
contains an explicit 4/8/16 MiB `object_size`, included in the password envelope's
associated data. Other configuration fields are explicit as well. Earlier magic
values, omitted geometry and old schema recovery paths are rejected; there is no
conversion or migration. Historical source and release artifacts are not inputs
to the current application.

## Geometry

Each volume selects one immutable object size at creation. Internal pages and
NTFS clusters remain 4 KiB. Objects can pack pages from unrelated logical disk
addresses; an object is not a fixed logical-address stripe.

| Object size | Header + table pages | Data slots | Maximum external dependencies |
| --- | --- | --- | --- |
| 4 MiB | 17 | 1007 | 1024 |
| 8 MiB | 33 | 2015 | 2048 |
| 16 MiB | 65 | 4031 | 4096 |

Each object has one header page followed by the descriptor table. The last 1 KiB
of the table contains the public KDF/configuration record, authenticated by the
object's body hash. Metadata objects reserve their first payload slot for a root
descriptor when needed; other slots contain authenticated portable index nodes.
All physical extents, slot keys, dependency keys, portable offsets, bounds and
statistics use the volume geometry. The entire first control extent is protected
from sparse deallocation.

A UUID/ordinal-to-extent directory separates object identity from physical
location. Object IDs use a persisted allocation nonce; imports continuing an
original identity use a fresh allocation namespace for future objects. Relocation
preserves bytes, UUID, SHA, references and cloud receipts. Ordinal references are
never treated as reconstructed UUIDs.

## Writes, authentication and durability

Normal writes acknowledge a bounded in-memory version visible to following
reads. Background persistence limits dirty memory. Flush and FUA wait for the
ordered prefix to reach stable storage; Close alone does not imply Flush.

Pages have independent authentication and authenticated plaintext digests.
Identical writes do not create new versions. Dirty pages retain their digest so
commit and encoding do not repeatedly hash the same plaintext. Encrypted batches
of at least 128 pages can use at most four persistent CPU workers, 64 pages per
task and eight queued tasks. Small batches run directly; publication remains an
ordered transaction.

COW page maps, dirty keys, object/reference directories, snapshots, jobs, receipts,
statistics and free-space deltas live in the same container. Sorted multi-key
lookups share subtree traversal; private metadata frames and contiguous payload
writes are coalesced into I/O of at most 1 MiB. New frames remain readable from
the owning transaction's buffers before writeout.

Data and metadata are synced before publishing either root copy. Both root slots
are written and synced before retired ranges can be reused. Retirement sequence
and older readers prevent reuse during the same transaction or underneath a
snapshot reader. Persistence errors latch the handle until reopen.

Normal Flush does not seal every partial object. Full objects seal once; a sync
cut rotates the active pools and later prepares only the frozen version. Five
pools isolate ordinary/unclassified data, MFT, NTFS log, USN and other recognized
metadata. Classification is advisory: unknown content is always retained.

Seal consumes immutable ciphertext still owned by its transaction directly,
without first writing and rereading it. Existing tail pages are read and their
authentication and original digest verified. Framing and required padding are
written separately; payload reaches its final position once. Only a bounded,
current-session proof that a newly assigned extent was entirely sparse may omit
padding. The proof is removed on seal/retirement and discarded on reopen. File
length and saved allocator records cannot prove an unused tail is zero after an
interrupted append. Unsupported sparse queries keep the padding write.

## Incremental cloud versions

`cloud.prepare` flushes once to freeze an immutable page root and dirty set.
New writes enter a later generation. Further preparation consumes only frozen
changed keys and affected index paths, with at most 4096 keys and 128 leaf groups
per step. Portable index nodes share metadata objects of the same volume size.
The root explicitly records geometry and index depth.

`cloud.list` returns this generation's added objects; `cloud.delta` exposes the
persistent add/remove sets. Every object, including the root, requires a durable
canonical receipt before `cloud.commit`. A prior task cannot clear later changes
to the same page. The last published delta remains available to recover a lost
external cache checkpoint. Progress overlays do not themselves modify guest data.

`read_export` returns leased, immutable **canonical raw bytes**. The saved SHA and
length describe those bytes. The cloud transport verifies that SHA, then wraps
zstd-compressed bytes in the fixed `zstd-v1` envelope. Wire SHA/length/part MD5 are
separate from canonical SHA/length. Confirmed compressed upload receipts still
become canonical native receipts. The native library contains no cloud protocol,
credentials or zstd transform. See `docs/ZSTD-PROVENANCE.md` for the wire profile.

## Import, hydration and snapshots

Imports authenticate the root and portable index, keep it as an immutable base,
and maintain local COW deltas. The old per-page cloud reconstruction queue is
removed. Metadata cache is bounded to 32 MiB (8/4/2 objects depending on geometry).
Full import additionally materializes and validates referenced data; lazy import
retains authenticated missing-object descriptors. Completion is required before
mounting.

`copy` receives a new disk identity. Its initial publication includes its source
baseline; it can use protected origin objects without permanently materializing
the entire virtual disk. `original` retains identity and published baseline only
with an authenticated matching source root and confirmed single-writer ownership.
It does not permit concurrent writers. An unchanged original import needs no
upload.

The object provider supplies exactly one canonical object of the volume's size.
The application decompresses and verifies the downloaded object first; native
then checks its saved SHA and authenticated header before atomically installing
it. Missing data never reads as zero. Callbacks run outside storage and reader
locks while the original guest operation keeps its ordering and physical lease.
Hydration changes neither guest generation nor dirty keys. Local persistence
errors remain fatal to the handle; network failures can retry.

`lazy.needs` traverses a bounded logical range, at most 4096 mapped pages and 16
missing objects per call, and returns a continuation offset. Full-page overwrites
need no old payload; partial writes fetch only required source objects. Managed
prefetch limits both object count and planning calls, prioritizes demanded reads,
and cancels obsolete speculation without cancelling replacement foreground work.

Snapshots pin immutable roots and missing-object references. Explicit snapshot
inspection can enumerate its objects; routine sync does not use that path.
Restoring a local snapshot produces an independent, complete container and holds
hydrated source objects through each copied batch. Raw object snapshot helpers
remain useful for inspection and integrity tests.

## Manual reclamation and local cache limits

Normal compaction releases unreferenced objects/private COW metadata, punches safe
holes and truncates an empty tail; it does not move or read live payloads. Deep
compaction moves complete objects into earlier holes without generating new
identities or uploads. Both are manual, bounded and pausable. TRIM and remote old
object deletion also remain manual.

An explicitly configured soft local quota may evict only complete, published
objects with authenticated remote source evidence and durable reader protection.
Dirty objects, open tails, readers and unconfirmed data stay local. LRU/LFU and
sequential policies use bounded candidate sets and in-memory access hints. An
object may reference its own publication or a protected import origin. A reopen
starts offline until the application confirms the account/read channel. Disabling
the quota retains existing missing-object sources.

Logical pending statistics include resident and missing objects; physical block
views show local extents and report the volume's object size. Snapshot references
are separate from actual upload state. API counters and local I/O counters allow
functional validation without throughput benchmarks.

## Mounting and bounds

Virtual capacity is bounded to 8 TiB. Page trees grow from height four to five only
when a high LBA is first written, preserving shared COW subtrees. Local and portable
nodes coexist by design within current-format trees; this is not an old-format
reader. Small Windows disks use MBR, 2 TiB and above use GPT with one data volume
and an optional MSR. NTFS allocation and identity checks validate supported layouts.

Read-only mode is enforced by the native ordered barrier, managed access guard
and WinSpd write protection. It rejects guest writes and TRIM, while verified
hydration, snapshots and physical-only maintenance remain available. Tests use
isolated new containers and fixed content/operation-count assertions; see
`docs/VALIDATION.md` for the current release evidence.
