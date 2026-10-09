# Synchronization, durable cache, and manual cloud cleanup

This layer assumes that this computer is the only cloud writer. Restore readers
use persistent reader pins. It stores metadata and object identities, never
passwords, cookies, or signed download links.

Construct `CloudRepository(store, cache, validatedAccountId)` with a
`FileCloudSyncCache`. The validated identity must belong to that same provider
session; otherwise the repository validates it before trusting owner metadata.
The cache scope includes provider, account, writer and the exact volume root.
Normal synchronization never deletes remote objects. Only an explicit user action
calls `CleanupAsync`; startup, periodic synchronization, resume and exit do not
implicitly run garbage collection.

## Upload and publication

The native job supplies authenticated canonical descriptors and incremental
add/remove sets. Existing native receipts skip source reads. An optional
`ICloudEncodedObjectStore` is asked for a confirmed canonical receipt before
opening source bytes. On a miss, `PreparedObjectUpload.CreateAsync` reads and
verifies the canonical object once, encodes its sealed bytes as `zstd-v2`, and
owns an immutable wire buffer. The provider uploads/retries that same buffer,
using its independent wire SHA-256, length, and per-4-MiB MD5 list. Canonical
SHA/size always remain native identity; wire identity never substitutes for them.
A backend without the optional capability receives the same encoded bytes via
its self-verifying ordinary immutable API.

Only `.obj` payloads are compressed. JSON owner records, publication descriptors,
and reader/cache pins remain ordinary JSON. Successful provider acknowledgments
confirm fresh uploads; normal publication never downloads them for readback.
Unknown existing paths and lost-response recovery may need wire SHA verification
to retain immutable conflict protection. A complete acknowledged encoded receipt
is reusable without reading, hashing, compressing, or transferring the object.

Publication proceeds through acknowledged data objects and root, durable publication
intent, acknowledged immutable descriptor, native job commit, and durable final local
state. Receipts may be batched, but a completed native receipt must be durable
before it is reported as confirmed. The original descriptor timestamp is retained
in the intent so that an interrupted retry never changes an immutable path's
content. Garbage candidates come only from authenticated native deltas and known
obsolete commit paths, with current additions excluded again before publication.
No full published-object inventory or remote object-directory scan is needed for
an ordinary incremental run.

With no local changes, an unfinished publication is reconciled if necessary;
otherwise a trusted owner/latest cache requires zero store API calls. Pending
manual cleanup does not cause uploads or automatic deletion. New writes arriving
during a fixed job are reported separately from that job's completed generation.

## Restore the original volume

`CloudRepository.PrepareOriginalRestoreAsync(volumeId, expectedCommit, ct)` supports
the explicit restore-original option. The application first obtains the user's
confirmation that no local duplicate or other writer remains; this API cannot
prove remote exclusivity. It validates the current account, reads the existing
owner record and fresh latest descriptor, and requires the selected commit to
match exactly. It returns the original recorded writer identity in a per-volume
`CloudBinding`; it neither changes the installation's global device identity nor
replaces `owner.json`. Only `/OverlayDisk/<volume-id>` is accepted.

Call it at restore admission and again immediately before enabling the restored
writer. A changed latest commit rejects the operation. After the application has
also excluded active local jobs/duplicate containers, a deleted container's
unconfirmed publication intent may be abandoned locally. An intent matching the
freshly verified latest is adopted as that same baseline. Neither operation
deletes its uploaded objects or imports its unconfirmed deletion delta; uncertain
object-deletion proof is cleared. A previously acknowledged later or divergent
publication still rejects rollback. Native restore completion must authenticate and retain the original
UUID, published root/hash and generation before synchronization or mounting is
enabled. The method does not download existing payload objects, enumerate object
directories, create remote directories, upload, or delete. It durably seeds the
owner/latest baseline so an unchanged restored native state needs zero cloud API
calls; no-op checks also compare the actual native published root and hash.

Existing deletion candidates are preserved only when the cache proves the same
published baseline. If intermediate object deltas are unavailable, object-deletion
proof is forgotten locally while all remote data remains untouched. Proven older
commit paths may remain candidates. A newer/conflicting cached publication is
never rolled back, and no automatic cleanup runs during attach or resume.

## Incremental local persistence

`ICloudSyncCache` retains `AcquireAsync`, `LoadAsync`, and `SaveAsync`. Callers hold
the scope lease throughout load/mutate/save and do not retain its working state
for later mutation. A process semaphore and an OS file lease serialize writers.
`CacheStringSet` implements `ISet<string>` / `IReadOnlyCollection<string>` with
ordinal identity, supports assignment from a `HashSet<string>`, and tracks actual
additions/removals. It serializes as a normal JSON array through the built-in
streaming collection serializer; tracker details never enter the file. There is
no reverse implicit conversion which could hide a large clone.

Each scope has a `.cache` checkpoint, `.cache.wal` log and `.cache.lease` lock.
Every complete journal frame binds its epoch, consecutive sequence, previous
snapshot identity, new scalar fields and changed collection entries with a
SHA-256 checksum. Appends are flushed before success. Confirmed manual deletions
use that same append path, so accumulated old candidates are not rewritten for
every deletion batch. Publication intent, scope and path checks still apply to
all newly introduced entries.

Full checkpoints are streamed and amortized: after at least `max(256, live set
entries)` journal records or `max(16 MiB, checkpoint size)` journal bytes. A single
record is bounded to 4 MiB; a larger actual replacement streams a checkpoint.
Before switching checkpoint/log generations, the older proof is durably
invalidated. A failed switch may require metadata reconciliation, but cannot
resurrect an earlier proof. Missing logs, checksum errors, truncation, epoch or
sequence mismatch invalidate the entire proof; the reader does not silently
trust the last older valid prefix. Old cache formats are not migrated.

Up to eight inactive scopes remain warm, while leased scopes are retained. Warm
loads compare checkpoint/log length, modification time and creation time; they
read no payload and clone no full object collection when those stamps agree.
Only dirty collection entries and small scalar fields are examined on an
ordinary save. Unsaved mutations are rolled back to the last durable baseline
both at lease disposal and before the next warm load, including nested
publication fields. A write failure evicts the warm proof. `GetDiagnostics()`
reports aggregate read/write/append/checkpoint counts and bytes without paths or
credentials; these are functional counters, not a performance estimate.

Missing or invalid cache data triggers conservative metadata reconciliation:
list commit names, verify the latest descriptor, and reconcile native published
state/deltas. Unknown remote objects are retained. Losing a cache can leave
unidentified orphans; it never creates permission to delete them.

## Explicit cleanup and readers

Volumes support capacities from 64 MiB through 8 TiB, aligned to 512 bytes.

After an acknowledged publication, `EnsureCachePinAsync(binding, volumeId)`
establishes one immutable `readers/<volume-id>.json` local-cache pin. Only after
that call succeeds may the application persist the matching native `cache.bind`
backing and enable automatic local eviction. The acknowledged pin survives restart
and publication generations; neither each eviction nor a no-op sync refreshes it.
Uploaded receipts or an unfinished publication alone cannot enable eviction.
Turning the cache limit off keeps the pin: current data and local snapshots may
still need missing objects. `ReleaseDeletedVolumeCachePinAsync` is only for explicit
cleanup after the application has actually deleted the local container and excluded
a replacement using that same identity. It verifies and deletes only that pin.

Every cleanup batch also calls native `cloud.gc_candidates` with at most 64 object
identities (an empty batch is valid for commit-only cleanup). Native protects all
current, snapshot, job, base and published references, even resident objects.
The complete allowed/protected partition is validated before any remote deletion.
A matching native cache backing plus verification of the small remote pin permits
ignoring only this volume's own cache pin. Other reader pins still block cleanup.
Protected candidates remain durable while the iterator advances to later candidates;
no full object-directory inventory or clone of the pending catalog is required.
Objects imported from a different source root retain their original source/pin:
the native per-object source is authoritative for routing reads, not a blanket
replacement with the newest local sync directory.

`CleanupEstimateAsync` returns local known candidates. `CleanupAsync` is invoked
only after user confirmation. It checks current publication state and excludes
current root/intent objects, then deletes at most 64 known paths per batch using
`ICloudBatchDeleteStore` when available. Every batch checks reader pins freshly.
A new reader must create its pin and recheck latest before consuming objects; it
uses the protected current version or aborts the older view. Reader presence and
cloud browsing are never cached. Failure keeps durable candidates for a later
manual retry; confirmed deletions are journaled idempotently.

`HasPendingWorkAsync` concerns unfinished publication, not pending garbage, so
manual cleanup does not block normal successful exit. The sole-writer cache does
not detect external edits made behind the application.

## Offline verification

Run `dotnet run --project cloud/tests/OverlayDisk.Cloud.Sync.Tests -c Release`.
The model suite covers acknowledgement/publication ordering, prepared receipt-before-read,
zero-request no-op, incremental deltas, scope boundaries, recovery, reader pins,
manual-only deletion and interrupted cleanup. The journal cases additionally
cover a 30,000-entry pending set with one small append, zero warm payload reads,
unsaved scalar/set/publication rollback, checksum/sequence/epoch/truncation failures,
append denial, amortized checkpoints, scope substitution, and actual child-process
kills before complete and after durable journal writes. These tests use fake
providers, model volumes and disposable directories; no real cloud API or
performance benchmark is run.

新云端根统一为 `/OverlayDisk/<volume-id>`，不含格式版本目录。发现仅列出此目录下直接的磁盘 UUID 子目录，不探测历史版本目录；格式仍由已校验的提交描述和对象内部标记判断。按需导入长期保留 reader pin，元数据导入完成不表示文件载荷已独立保存，不能在此时释放源引用。

Run `dotnet run --project cloud/tests/OverlayDisk.Cloud.Sync.Tests -c Release -- --filter upload-ack` for the focused upload-acknowledgement checks. The persisted `RemoteVerified` cache flag now means a successful provider acknowledgement; its field name is retained so existing publication journals remain readable. Existing-owner discovery and interrupted/uncertain publication reconciliation still read necessary metadata; normal successful uploads do not trigger verification downloads.

Use `--filter original-restore` to run only the six original-volume attach cases:
metadata-only validation, original writer identity, zero-API initial no-op,
account/owner/path conflicts, latest changes/divergence, absent-container intent recovery,
safe deletion-proof retention/invalidation, native root mismatch and failed local
baseline persistence. No real account is contacted.

## Object geometry and compressed transport

`CloudObjectGeometry` accepts exactly 4, 8 or 16 MiB. `ICloudVolume.ObjectSizeBytes`
is mandatory and comes from native volume information. Native status/job
`object_size`, each export, canonical root receipts, and logical progress must
match that immutable geometry. Missing or conflicting size is an error before
publication. `RemoteCommit` requires explicit JSON `objectSizeBytes` and
`transportCodec: "zstd-v2"`; no missing-field or raw-object fallback exists.
`RemoteDisk.ObjectSizeBytes` exposes actual geometry for browsing. Listings skip
only explicitly unsupported old-format volumes, preserving I/O and corruption
errors. The old directories and objects are not moved or deleted.

`ReadObjectAsync(root, id, canonicalHash, objectSizeBytes, ct)` checks size before
requesting data, bounds wire input by zstd's compression bound plus its 128-byte
envelope, and decodes exactly the expected canonical size. The envelope and a
single standard zstd frame are mandatory. Dictionary references, window sizes
above 16 MiB, missing/wrong content size, extra frames, trailing bytes, truncated
streams, and canonical SHA mismatch fail. The authenticated expected SHA comes
from the caller's native descriptor or selected commit, never from the envelope
alone. Native root validation must additionally verify the imported plaintext root.
Copy and original restores preserve source object geometry.

`ICloudBoundedObjectReader` avoids Head/LIST for variable wire lengths: the Baidu
implementation uses locate and transfer only, checks HTTP completion and bounds,
and the repository performs full decompression and canonical verification.
Absent this optional interface, the ordinary read API is still bounded and
verified by the repository. JSON never passes through the compressed decoder.

Progress `UploadedBytes` counts compressed object bytes acknowledged as newly
uploaded, while `LogicalUploadedBytes` counts their canonical object sizes.
`ReusedBytes`, `TotalBytes`, `CompletedBytes`, and `PendingBytes` describe logical
object bytes. Remaining transfer size is explicitly estimated until all objects
are encoded; no canonical byte estimate is presented as measured network traffic.
Per-object logs include `WireBytes` separately. HTTP multipart overhead, retries,
and small JSON records are not included in the compressed payload counter.
Manual-cleanup byte estimates remain canonical object equivalents, not measured
compressed cloud space.

The pinned codec version, license and deterministic encoder profile are documented
in `docs/ZSTD-PROVENANCE.md` and `docs/ZSTD-LICENSE.txt`. A profile change that may
change wire bytes requires a new codec identity.

Current focused offline verification: `--filter object-transport` selects the
4/8/16 MiB compressed publication/read cases, strict current-format boundaries,
preparation progress, receipt-before-read, acknowledgment ordering, retries,
zero-request no-op and explicit cleanup. All fixtures use local model stores;
these are correctness checks, not throughput measurements or real-account tests.


## Cloud-only encryption

Local native objects contain plaintext. Every new cloud `.obj`, including data,
index and root, is first compressed with zstd and may then be encrypted with
AES-256-GCM. The plaintext transport is `zstd-v2`; encrypted transport is
`zstd-aes256gcm-v2`. Old formats are unsupported and are never migrated implicitly.

`CloudEncryptionContext.Create(volumeId, password)` creates a random 32-byte disk
salt once and derives a 32-byte master key with PBKDF2-HMAC-SHA256, exactly 600,000
iterations. The public `CloudEncryptionSettings` is retained in each commit;
`Unlock` validates its bounded, fixed parameters and its key check before any
object download. A salt prevents shared password precomputation; it does not
make a weak password resistant to targeted offline guessing. The application
persists the exported master key only through Windows DPAPI. No plaintext
password or unprotected key belongs in the cloud or ordinary settings JSON.

`RegisterEncryption(root, context)` configures a repository; callers own context
lifetime. `AuthenticateCommit` is required before adopting a listed version.
A HMAC authenticates every typed commit field, including volume, writer,
generation, root/hash, capacity, geometry and encryption profile. Registered
encrypted roots reject a plaintext downgrade. Public commit names, sizes and
key-derivation settings remain visible; object content and indexes are encrypted.

The AES key is separately derived from the master key. Each object's 96-bit
pseudorandom nonce is derived using a domain-separated HMAC over its immutable
path, canonical size/hash, encryption identity and compressed payload hash.
Identical retries retain identical ciphertext without a per-object nonce journal;
a different compressed representation cannot reuse the same nonce/plaintext
combination. Codec/compressor profile changes still require a new transport
identity. GCM authenticates the header and descriptor; failed authentication
returns before zstd inspects the payload. Receipt identity includes the full
public encryption settings identity, preventing plaintext/wrong-key reuse.

`--filter cloud-encryption` covers all object sizes, compression order, password,
KDF bounds, tampering and substitution, authenticated commits, reader lazy reads,
stable salt, zero-request unchanged sync, lost acknowledgements and missing
`/OverlayDisk` creation. These are offline correctness tests.
