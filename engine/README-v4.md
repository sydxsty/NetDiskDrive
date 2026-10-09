# Current OverlayDisk container format

The current engine uses the `.odv4` extension and the `v4::Volume` / `od_v4_*`
interfaces, with **ODV4PLN1** configuration magic and mandatory
`local_storage: "plaintext-v1"`. Metadata frames use **ODV4METP**. Local containers
and canonical objects are always plaintext; `encrypted` is always false. Local
configuration contains no password salt, key envelope or encryption key. Its
`integrity_id` binds checksums across original disks and independent copies.
Cloud compression and encryption are handled by the managed transport layer,
after native export and before upload; downloads are decrypted/decompressed
before native import. A cloud commit's `encrypted` flag describes that transport,
not the local container.

Every configuration contains an explicit 4/8/16 MiB `object_size`. Earlier
configuration magic, omitted plaintext marker, encryption fields and old schema
recovery paths are rejected. There is no conversion or migration. Existing ABI
password parameters accept only null/empty values; nonempty passwords fail before
creating a local file. Historical source and release artifacts are not inputs
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
of the table contains the public plaintext configuration record, covered by the
object's body hash. Metadata objects reserve their first payload slot for a root
descriptor when needed; other slots contain authenticated portable index nodes.
All physical extents, slot keys, dependency keys, portable offsets, bounds and
statistics use the volume geometry. The entire first control extent is protected
from sparse deallocation. Its configuration mirrors occupy pages 0/3 and its
commit roots pages 1/2. Upload receipt journal pages start at page 4.

A UUID/ordinal-to-extent directory separates object identity from physical
location. Object IDs use a persisted allocation nonce. A source and its descendant
copies allocate in disjoint bounded ordinal ranges, selected by the authenticated
`allocation_depth`: importing a later source generation cannot collide with a
copy's local COW objects. Imports preserve existing object identities. Relocation
preserves bytes, UUID, SHA, references and cloud receipts. Ordinal references are
never treated as reconstructed UUIDs.

## Writes, authentication and durability

Normal writes acknowledge a bounded in-memory version visible to following
reads. Background persistence limits dirty memory. Flush and FUA wait for the
ordered prefix to reach stable storage; Close alone does not imply Flush.

Pages have independent position/version-bound checksums and SHA-256 content digests.
These detect corruption; local plaintext storage does not protect against malicious
local editing. Cloud AEAD is responsible for remote adversarial authentication.
Identical writes do not create new versions. Dirty pages retain their digest so
commit and encoding do not repeatedly hash the same plaintext. Page encoding batches
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

Seal consumes immutable plaintext still owned by its transaction directly,
without first writing and rereading it. Existing tail pages are read and their
authentication and original digest verified. Framing is written separately;
payload reaches its final position once. Unused tail bytes are logical zeros:
their physical locations may retain previous contents after extent reuse or an
interrupted append. Whole-object reads authenticate the header against the object
catalogue, read only the used prefix, then synthesize zero padding. Export,
verification and relocation use the same canonical representation. Incoming
network objects are verified without normalization and nonzero canonical padding
is rejected. Imports, hydration and relocation persist only the authenticated
prefix. The reserved metadata root slot is always explicitly initialized; it is
inside the used prefix and is not an unused tail. No automatic sparse deallocation
or TRIM is performed by this optimization.

## Incremental cloud versions

`cloud.prepare` flushes once to freeze an immutable page root and dirty set.
New writes enter a later generation. Further preparation consumes only frozen
changed keys and affected index paths, with at most 16,384 keys and 512 leaf groups
per step. Batches start at 128 leaf groups and adapt within those bounds. The
preparation cache defaults to 64 MiB per preparing volume and is configurable
from 16 to 1024 MiB. Portable index nodes share metadata objects of the same volume size.
The root explicitly records geometry and index depth.

`cloud.list` returns this generation's added objects; `cloud.delta` exposes the
persistent add/remove sets. Every object, including the root, requires a durable
canonical receipt before `cloud.commit`. A prior task cannot clear later changes
to the same page. The last published delta remains available to recover a lost
external cache checkpoint. Progress overlays do not themselves modify guest data.

An ordinary receipt batch appends one authenticated 4 KiB control page and performs
one durability flush, without rewriting COW roots or block statistics. Duplicate
receipts add no writes. Records are bound to the immutable job, checkpoint epoch,
sequence and previous-frame hash. Their OIDs must belong to that prepared job.
At most 256 distinct confirmations remain outside the tree checkpoint; before
exceeding this bound, one normal COW transaction folds them into the receipt and
status indexes, publishes a new epoch through both roots, and only then reuses
the journal area. Version commit reuses the complete addition set as the final
receipt set after validating complete confirmation coverage.

Read leases capture bounded confirmed-receipt overlays along with their root.
Job/epoch matching prevents double counting across a concurrent checkpoint.
Reopen replays only the authenticated consecutive journal prefix. An uncertain
tail cannot confirm objects or permit premature publication; resumption
checkpoints the valid prefix before reusing that tail. A failed append or flush
latches the handle. Receipt logging preserves the normal data Flush/FUA protocol.
These are local control records and are never added to cloud upload objects.

`read_export` returns leased, immutable **canonical raw bytes**. The saved SHA and
length describe those bytes. The cloud transport verifies that SHA, then wraps
zstd-compressed bytes in the `zstd-v2` envelope, or encrypts the compressed
bytes with AES-256-GCM in `zstd-aes256gcm-v2`. Per-disk salt and key derivation
are managed transport concerns; the native store receives no cloud password.
Wire SHA/length/part MD5 are
separate from canonical SHA/length. Confirmed compressed upload receipts still
become canonical native receipts. The native library contains no cloud protocol,
credentials or zstd transform. See `docs/ZSTD-PROVENANCE.md` for the wire profile.

## Import, hydration and snapshots

Cloud imports authenticate the version, root object and initial index root, keep
the portable index as an immutable base, and maintain local COW deltas. Startup
does not enumerate all leaf mappings or fetch all metadata objects. Both copy and
original imports use the same deferred index/data path, and read-only and writable
mounts share it. Mounting may still demand partition-table and NTFS objects.
Completion means the initial view is usable; unvisited objects are not claimed
to be verified. The bounded metadata cache does not grow with virtual capacity.

`copy` receives a new disk identity and does not enable cloud upload. Its initial
publication, only if upload is explicitly enabled later, includes its source
baseline; it can use protected origin objects without permanently materializing
the entire virtual disk. `original` retains identity and published baseline only
with an authenticated matching source root and confirmed single-writer ownership.
It does not permit concurrent writers. An unchanged original import needs no
upload.

The copy's MBR/GPT identity is a local presentation overlay. The application
sends both partition tables together through `od_v4_replica_identity` using a
bounded binary frame; it does not expand GPT bytes into the 64 KiB JSON control
channel. Both regions become durable in one transaction without dirtying guest
pages. Invalid, truncated or oversized frames are rejected before mutation.

The object provider supplies exactly one canonical object of the volume's size.
The application decompresses and verifies the downloaded object first; native
then checks its saved SHA and authenticated header before atomically installing
it. Metadata nodes are validated within the already downloaded object: roots and
children must use valid portable slots, data references must belong to the
authenticated dependency table, and contained parent/child ranges and counts
must agree. Cross-object children are checked on access; these checks never fetch
an unvisited subtree merely to validate the current object. Missing data never
reads as zero. Callbacks run outside storage and reader
locks while the original guest operation keeps its ordering and physical lease.
Hydration changes neither guest generation nor dirty keys. Local persistence
errors remain fatal to the handle; network failures can retry.

Manual latest-version loading requires an unmounted volume and a staged token
bound to the current local data revision. Applying a candidate atomically replaces
the current root, retains explicit local snapshots and protected source roots, and
requires explicit discard confirmation when local writes exist. Normal status
queries never check for a newer cloud version. A clean unchanged source does not
download object payloads. Immutable cache entries are reused by UUID and digest;
only subsequently accessed missing objects are downloaded. Copy partition identity
is a local presentation overlay until a guest write to that page materializes the
visible bytes as COW data. Failed identity preparation after a committed switch
is reported separately and retried before mounting.

`index_complete` describes whether the source reference catalog has been expanded,
not whether every data object has been read. Until then, lazy-object totals and
block statistics are explicitly known-object counts. An original writer retains
its source reader pin and counts newly created objects exactly; inherited objects
with unknown counts remain protected, rather than being treated as unreferenced.
Preparation and retries update only changed index paths. The durable local
`source_anchor_v1` accounting marker rejects older writers that would replace the
incremental counts with a full source baseline; cloud object bytes are unchanged.
Explicitly publishing an independent copy to another repository still discovers
and transfers its complete required object graph. Ordinary import, read/write,
same-repository sync and manual latest-version loading do not require this walk.

`lazy.needs` traverses a bounded logical range, at most 4096 mapped pages and 16
missing objects per call, and returns a continuation offset. A missing index is
returned as a metadata descriptor with `waiting_for_index`; after importing it,
the caller retries the same offset. Traversal never opens a subtree beyond the
exclusive range end. Metadata and data share the configured prefetch object budget.
Full-page overwrites
need no old payload; partial writes fetch only required source objects. Managed
prefetch limits both object count and planning calls, prioritizes demanded reads,
and cancels obsolete speculation without cancelling replacement foreground work.
Provider callbacks carry object kind and request reason through the binary channel
for activity logs; registration uses `od_v4_set_object_provider_with_context` so a
mismatched older native library fails registration rather than invoking the wrong ABI.

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
