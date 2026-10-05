using System.Collections.Concurrent;
using System.Collections;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OverlayDisk.Cloud.Sync;

/// <summary>Ordinal string identity set with reversible changes since its last
/// durable save. System.Text.Json treats ISet as an array and streams it without
/// buffering a custom converter's entire value. No internal writable set escapes.</summary>
public sealed class CacheStringSet : ISet<string>, IReadOnlyCollection<string>
{
    private readonly HashSet<string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> added = new(StringComparer.Ordinal), removed = new(StringComparer.Ordinal);
    public CacheStringSet() { }
    public CacheStringSet(IEqualityComparer<string>? comparer)
    {
        if (comparer is not null && comparer != StringComparer.Ordinal && comparer != EqualityComparer<string>.Default)
            throw new ArgumentException("Cloud identities require ordinal case-sensitive comparison.", nameof(comparer));
    }
    public CacheStringSet(IEnumerable<string> source) { foreach (string value in source) values.Add(value); }
    public CacheStringSet(IEnumerable<string> source, IEqualityComparer<string>? comparer) : this(comparer)
    { foreach (string value in source) values.Add(value); }
    public static implicit operator CacheStringSet(HashSet<string> source) => new(source);
    public int Count => values.Count;
    public bool IsReadOnly => false;
    public IEqualityComparer<string> Comparer => StringComparer.Ordinal;
    internal IEnumerable<string> Added => added;
    internal IEnumerable<string> Removed => removed;
    internal bool Changed => added.Count != 0 || removed.Count != 0;
    internal void AcceptChanges() { added.Clear(); removed.Clear(); }
    internal void Rollback() { values.ExceptWith(added); values.UnionWith(removed); AcceptChanges(); }
    public bool Add(string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!values.Add(item)) return false;
        if (!removed.Remove(item)) added.Add(item);
        return true;
    }
    void ICollection<string>.Add(string item) => Add(item);
    public bool Remove(string item)
    {
        if (!values.Remove(item)) return false;
        if (!added.Remove(item)) removed.Add(item);
        return true;
    }
    public void Clear() { foreach (string item in values.ToArray()) Remove(item); }
    public bool Contains(string item) => values.Contains(item);
    public void CopyTo(string[] array, int arrayIndex) => values.CopyTo(array, arrayIndex);
    public IEnumerator<string> GetEnumerator() => values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void ExceptWith(IEnumerable<string> other)
    { if (ReferenceEquals(other, this)) { Clear(); return; } foreach (string item in other) Remove(item); }
    public void UnionWith(IEnumerable<string> other) { foreach (string item in other) Add(item); }
    public void IntersectWith(IEnumerable<string> other)
    { var retained = new HashSet<string>(other, StringComparer.Ordinal); foreach (string item in values.Where(v => !retained.Contains(v)).ToArray()) Remove(item); }
    public void SymmetricExceptWith(IEnumerable<string> other)
    { foreach (string item in new HashSet<string>(other, StringComparer.Ordinal)) if (!Remove(item)) Add(item); }
    public bool IsSubsetOf(IEnumerable<string> other) => values.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<string> other) => values.IsSupersetOf(other);
    public bool IsProperSupersetOf(IEnumerable<string> other) => values.IsProperSupersetOf(other);
    public bool IsProperSubsetOf(IEnumerable<string> other) => values.IsProperSubsetOf(other);
    public bool Overlaps(IEnumerable<string> other) => values.Overlaps(other);
    public bool SetEquals(IEnumerable<string> other) => values.SetEquals(other);
}

public sealed record CloudCacheScope(string ProviderId, string AccountId, string WriterId, string RemoteRoot)
{
    public static CloudCacheScope From(CloudBinding binding) => new(binding.ProviderId, binding.AccountId, binding.DeviceId, binding.RemoteRoot);
}
public sealed class CloudPublicationIntent
{
    public RemoteCommit Commit { get; set; } = null!;
    public string CommitPath { get; set; } = "";
    public CacheStringSet Closure { get; set; } = new(StringComparer.Ordinal);
    public CacheStringSet Removed { get; set; } = new(StringComparer.Ordinal);
    public string DeltaId { get; set; } = "";
    // Successful immutable publication acknowledged by the provider. The persisted
    // field name is retained for existing journals; it does not require a download.
    public bool RemoteVerified { get; set; }
}
public sealed class CloudSyncCacheState
{
    public int Version { get; set; } = 2;
    public string SnapshotId { get; set; } = Guid.NewGuid().ToString("N");
    public CloudCacheScope Scope { get; set; } = null!;
    public bool OwnerConfirmed { get; set; }
    public bool LatestKnown { get; set; }
    public RemoteCommit? Latest { get; set; }
    public string? LatestPath { get; set; }
    public CacheStringSet ConfirmedFolders { get; set; } = new(StringComparer.Ordinal);
    public CacheStringSet PublishedClosure { get; set; } = new(StringComparer.Ordinal);
    public ulong? ClosureGeneration { get; set; }
    public string? PublishedCommitPath { get; set; }
    public CacheStringSet PendingDeleteObjects { get; set; } = new(StringComparer.Ordinal);
    public CacheStringSet PendingDeleteCommits { get; set; } = new(StringComparer.Ordinal);
    public CloudPublicationIntent? Publication { get; set; }
    internal bool CleanupPending => PendingDeleteObjects.Count != 0 || PendingDeleteCommits.Count != 0;
    internal static CloudSyncCacheState Empty(CloudCacheScope scope) => new() { Scope = scope };
    internal CacheStringSet?[] Sets() => [ConfirmedFolders, PublishedClosure, PendingDeleteObjects, PendingDeleteCommits, Publication?.Closure, Publication?.Removed];
    internal void ValidateHeader(CloudCacheScope scope)
    {
        if (Version != 2 || !Guid.TryParse(SnapshotId, out _) || Scope != scope || ConfirmedFolders is null || PublishedClosure is null || PendingDeleteObjects is null || PendingDeleteCommits is null)
            throw new IOException("同步缓存身份或格式无效。");
        CloudRepository.ValidateRoot(scope.RemoteRoot);
        var sets = Sets().Where(s => s is not null).Cast<CacheStringSet>().ToArray();
        if (sets.Distinct(ReferenceEqualityComparer.Instance).Count() != sets.Length) throw new IOException("同步缓存集合不能共享可变实例。");
        if (PublishedCommitPath is not null) CloudRepository.ValidateCommitPath(scope.RemoteRoot, PublishedCommitPath);
        if (Latest is not null)
        {
            CloudRepository.ValidateCommit(scope.RemoteRoot, LatestPath ?? "", Latest);
            if (Latest.WriterId != scope.WriterId) throw new IOException("缓存写入者与绑定不符。");
        }
        else if (LatestPath is not null) throw new IOException("缓存版本路径没有对应版本。");
        if (Publication is { } pending)
        {
            if (pending.Commit is null || pending.Closure is null || pending.Removed is null) throw new IOException("缓存发布意图不完整。");
            CloudRepository.ValidateCommit(scope.RemoteRoot, pending.CommitPath, pending.Commit);
            if (pending.Commit.WriterId != scope.WriterId || !pending.Closure.Contains(pending.Commit.RootObjectId) || string.IsNullOrEmpty(pending.DeltaId))
                throw new IOException("缓存发布意图无效。");
        }
    }
    internal static void ValidateItem(CloudCacheScope scope, int set, string item)
    {
        if (set == 0)
        {
            if (item is null || item != CloudRepository.BasePath && item != scope.RemoteRoot && item != scope.RemoteRoot + "/objects" && item != scope.RemoteRoot + "/commits" && item != scope.RemoteRoot + "/readers"
                && !(item.StartsWith(scope.RemoteRoot + "/objects/", StringComparison.Ordinal) && item.Length == scope.RemoteRoot.Length + 11 && item[^2..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                throw new IOException("同步缓存包含范围外目录。");
        }
        else if (set == 3) CloudRepository.ValidateCommitPath(scope.RemoteRoot, item);
        else if (set is 1 or 2 or 4 or 5) CloudRepository.Component(item);
        else throw new IOException("同步缓存集合标识无效。");
    }
    internal void Validate(CloudCacheScope scope)
    {
        ValidateHeader(scope); var sets = Sets();
        for (int i = 0; i < sets.Length; i++) if (sets[i] is { } values) foreach (string value in values) ValidateItem(scope, i, value);
        if (Publication is { } pending && pending.Removed.Overlaps(pending.Closure)) throw new IOException("发布增量集合交叠。");
    }

}
public interface ICloudSyncCache
{
    ValueTask<IAsyncDisposable> AcquireAsync(CloudCacheScope scope, CancellationToken cancellationToken = default);
    Task<CloudSyncCacheState?> LoadAsync(CloudCacheScope scope, CancellationToken cancellationToken = default);
    Task SaveAsync(CloudCacheScope scope, CloudSyncCacheState state, CancellationToken cancellationToken = default);
}
// Implementations with this marker validate all entries on cold load and every
// changed entry before a durable save. A warm load needs only scope/header checks.
internal interface IValidatedCloudSyncCache : ICloudSyncCache { }
internal sealed class CacheLease(SemaphoreSlim semaphore) : IAsyncDisposable
{
    public ValueTask DisposeAsync() { semaphore.Release(); return ValueTask.CompletedTask; }
}
public sealed class MemoryCloudSyncCache : ICloudSyncCache
{
    private readonly ConcurrentDictionary<CloudCacheScope, byte[]> values = new();
    private readonly ConcurrentDictionary<CloudCacheScope, SemaphoreSlim> locks = new();
    public async ValueTask<IAsyncDisposable> AcquireAsync(CloudCacheScope scope, CancellationToken cancellationToken = default)
    { var semaphore = locks.GetOrAdd(scope, _ => new(1, 1)); await semaphore.WaitAsync(cancellationToken); return new CacheLease(semaphore); }
    public Task<CloudSyncCacheState?> LoadAsync(CloudCacheScope scope, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(values.TryGetValue(scope, out var bytes) ? JsonSerializer.Deserialize<CloudSyncCacheState>(bytes, CloudRepository.Json) : null); }
    public Task SaveAsync(CloudCacheScope scope, CloudSyncCacheState state, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); state.Validate(scope); values[scope] = JsonSerializer.SerializeToUtf8Bytes(state, CloudRepository.Json); return Task.CompletedTask; }
}
/// <summary>Atomic per-account/provider/writer/root snapshots. No credentials are stored.</summary>
internal interface ICloudDeleteJournal
{
    Task ConfirmDeletionsAsync(CloudCacheScope scope, CloudSyncCacheState state, string[] objects, string[] commits, CancellationToken ct);
}

public sealed record CloudSyncCacheDiagnostics(long CheckpointReads, long CheckpointWrites, long JournalReads,
    long JournalWrites, long WarmHits, long BytesRead, long BytesWritten);

/// <summary>
/// Scope transactions use AcquireAsync. Each successful save is a checksummed
/// append; checkpoints are amortized by changed bytes/entries. Warm views are
/// rolled back to their last durable baseline on lease disposal and before reuse.
/// A damaged journal invalidates the complete proof instead of replaying an
/// earlier state which could authorize collection of a newer published object.
/// </summary>
public sealed class FileCloudSyncCache(string directory) : IValidatedCloudSyncCache, ICloudDeleteJournal
{
    private static readonly byte[] Magic = "ODSC03\r\n"u8.ToArray(), WalMagic = "ODSCW01\n"u8.ToArray();
    private const int WalHeaderLength = 56, MaximumRecordBytes = 4 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Operations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Entry> Warm = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> Active = new(StringComparer.OrdinalIgnoreCase);
    private static long clock;
    private readonly string directory = Path.GetFullPath(directory);
    private long checkpointReads, checkpointWrites, journalReads, journalWrites, warmHits, bytesRead, bytesWritten;

    private sealed record PublicationFields(RemoteCommit Commit, string CommitPath, string DeltaId, bool RemoteVerified);
    private sealed record Fields(int Version, string SnapshotId, CloudCacheScope Scope, bool OwnerConfirmed, bool LatestKnown,
        RemoteCommit? Latest, string? LatestPath, ulong? ClosureGeneration, string? PublishedCommitPath, PublicationFields? Publication);
    private sealed record Patch(int Set, bool Reset, string[] Added, string[] Removed);
    private sealed record Record(Guid Epoch, long Sequence, string Before, Fields Values, Patch[] Changes);
    private sealed record Checkpoint(Guid Epoch, CloudSyncCacheState State);
    private readonly record struct Stamp(bool Exists, long Length, long Modified, long Created)
    {
        public static Stamp Read(string path)
        {
            var info = new FileInfo(path); info.Refresh();
            return info.Exists ? new(true, info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks) : default;
        }
    }
    private sealed class Entry
    {
        public required CloudSyncCacheState State;
        public required Fields Baseline;
        public required CacheStringSet?[] Sets;
        public Guid Epoch;
        public long Sequence, Records, WalBytes, Used;
        public Stamp CheckpointStamp, JournalStamp;
        public void Commit()
        {
            foreach (var set in State.Sets()) set?.AcceptChanges();
            Baseline = Capture(State); Sets = State.Sets();
        }
        public void Rollback()
        {
            foreach (var set in Sets) set?.Rollback();
            ApplyFields(State, Baseline, Sets);
        }
    }
    private sealed class FileLease(FileStream file, SemaphoreSlim semaphore, string path) : IAsyncDisposable
    {
        private int disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            var operation = Operations.GetOrAdd(path, _ => new(1, 1));
            try
            {
                await operation.WaitAsync().ConfigureAwait(false);
                try { if (Warm.TryGetValue(path, out var entry)) entry.Rollback(); }
                finally { operation.Release(); }
            }
            finally
            {
                file.Dispose(); Active.TryRemove(path, out _); semaphore.Release();
                TrimWarm();
            }
        }
    }
    public CloudSyncCacheDiagnostics GetDiagnostics() => new(
        Interlocked.Read(ref checkpointReads), Interlocked.Read(ref checkpointWrites), Interlocked.Read(ref journalReads),
        Interlocked.Read(ref journalWrites), Interlocked.Read(ref warmHits), Interlocked.Read(ref bytesRead), Interlocked.Read(ref bytesWritten));
    private string FilePath(CloudCacheScope scope) => Path.Combine(directory, CloudRepository.Hash(JsonSerializer.SerializeToUtf8Bytes(scope, CloudRepository.Json)) + ".cache");

    public async ValueTask<IAsyncDisposable> AcquireAsync(CloudCacheScope scope, CancellationToken cancellationToken = default)
    {
        string path = FilePath(scope); var semaphore = Locks.GetOrAdd(path, _ => new(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            var file = new FileStream(path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Active[path] = 1; return new FileLease(file, semaphore, path);
        }
        catch { semaphore.Release(); throw; }
    }

    public async Task<CloudSyncCacheState?> LoadAsync(CloudCacheScope scope, CancellationToken cancellationToken = default)
    {
        string path = FilePath(scope); var operation = Operations.GetOrAdd(path, _ => new(1, 1));
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Warm.TryGetValue(path, out var cached) && Unchanged(path, cached))
            {
                cached.Rollback(); cached.Used = Interlocked.Increment(ref clock);
                Interlocked.Increment(ref warmHits); return cached.State;
            }
            Warm.TryRemove(path, out _);
            var entry = await ReadAsync(path, scope, cancellationToken).ConfigureAwait(false);
            if (entry is null) return null;
            Warm[path] = entry; TrimWarm(); return entry.State;
        }
        finally { operation.Release(); }
    }

    public async Task SaveAsync(CloudCacheScope scope, CloudSyncCacheState state, CancellationToken cancellationToken = default)
    {
        state.ValidateHeader(scope);
        string path = FilePath(scope); var operation = Operations.GetOrAdd(path, _ => new(1, 1));
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            Entry? entry = null;
            if (Warm.TryGetValue(path, out var current))
            {
                if (!Unchanged(path, current)) { Warm.TryRemove(path, out _); throw new IOException("同步缓存已在外部变化，必须重新核对。"); }
                entry = current;
            }
            else if (File.Exists(path)) entry = await ReadAsync(path, scope, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                state.Validate(scope);
                await CheckpointAsync(path, state, cancellationToken).ConfigureAwait(false); return;
            }
            var sets = state.Sets();
            var patches = new List<Patch>();
            for (int index = 0; index < sets.Length; index++)
            {
                var set = sets[index]; if (set is null) continue;
                var old = entry.Sets[index];
                if (ReferenceEquals(set, old))
                {
                    if (set.Changed) patches.Add(new(index, false, set.Added.ToArray(), set.Removed.ToArray()));
                }
                else
                {
                    old?.Rollback(); // Do not compare against another caller's unsaved edits.
                    patches.Add(new(index, true, set.ToArray(), []));
                }
            }
            ValidatePatches(scope, state, patches);
            var fields = Capture(state);
            if (patches.Count == 0 && fields with { SnapshotId = "" } == entry.Baseline with { SnapshotId = "" })
            {
                state.SnapshotId = entry.Baseline.SnapshotId;
                entry.State = state; entry.Commit(); entry.Used = Interlocked.Increment(ref clock); Warm[path] = entry;
                return;
            }
            var next = fields with { SnapshotId = Guid.NewGuid().ToString("N") };
            var record = new Record(entry.Epoch, checked(entry.Sequence + 1), entry.Baseline.SnapshotId, next, patches.ToArray());
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(record, CloudRepository.Json);
            if (payload.Length > MaximumRecordBytes)
            {
                state.SnapshotId = next.SnapshotId; state.Validate(scope);
                await CheckpointAsync(path, state, cancellationToken).ConfigureAwait(false); return;
            }
            byte[] frame = new byte[4 + payload.Length + 32];
            BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
            payload.CopyTo(frame, 4); SHA256.HashData(payload).CopyTo(frame, 4 + payload.Length);
            using (var file = new FileStream(path + ".wal", FileMode.Open, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (file.Length != entry.WalBytes) throw new IOException("同步缓存日志代数已变化。");
                file.Position = file.Length;
                await file.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false); file.Flush(true);
            }
            Interlocked.Increment(ref journalWrites); Interlocked.Add(ref bytesWritten, frame.Length);
            state.SnapshotId = next.SnapshotId;
            entry.State = state; entry.Sequence++; entry.Records++; entry.WalBytes += frame.Length; entry.Commit();
            entry.CheckpointStamp = Stamp.Read(path); entry.JournalStamp = Stamp.Read(path + ".wal"); entry.Used = Interlocked.Increment(ref clock);
            Warm[path] = entry;
            long entries = sets.Where(set => set is not null).Sum(set => (long)set!.Count);
            if (entry.Records >= Math.Max(256, entries) || entry.WalBytes >= Math.Max(16L * 1024 * 1024, entry.CheckpointStamp.Length))
                await CheckpointAsync(path, state, cancellationToken).ConfigureAwait(false);
            TrimWarm();
        }
        catch { Warm.TryRemove(path, out _); throw; }
        finally { operation.Release(); }
    }

    async Task ICloudDeleteJournal.ConfirmDeletionsAsync(CloudCacheScope scope, CloudSyncCacheState state, string[] objects, string[] commits, CancellationToken ct)
    {
        if (state.Scope != scope || objects.Length + commits.Length is < 1 or > 64) throw new IOException("删除回执范围无效。");
        foreach (string id in objects) CloudRepository.Component(id);
        foreach (string path in commits) CloudRepository.ValidateCommitPath(scope.RemoteRoot, path);
        // Idempotent even if the caller removed these after remote confirmation.
        state.PendingDeleteObjects.ExceptWith(objects); state.PendingDeleteCommits.ExceptWith(commits);
        await SaveAsync(scope, state, ct).ConfigureAwait(false);
    }

    private static Fields Capture(CloudSyncCacheState s) => new(s.Version, s.SnapshotId, s.Scope, s.OwnerConfirmed, s.LatestKnown,
        s.Latest, s.LatestPath, s.ClosureGeneration, s.PublishedCommitPath,
        s.Publication is { } p ? new(p.Commit, p.CommitPath, p.DeltaId, p.RemoteVerified) : null);
    private static void ApplyFields(CloudSyncCacheState state, Fields fields, CacheStringSet?[] sets)
    {
        state.Version = fields.Version; state.SnapshotId = fields.SnapshotId; state.Scope = fields.Scope;
        state.OwnerConfirmed = fields.OwnerConfirmed; state.LatestKnown = fields.LatestKnown;
        state.Latest = fields.Latest; state.LatestPath = fields.LatestPath; state.ClosureGeneration = fields.ClosureGeneration; state.PublishedCommitPath = fields.PublishedCommitPath;
        state.ConfirmedFolders = sets[0] ?? new(); state.PublishedClosure = sets[1] ?? new();
        state.PendingDeleteObjects = sets[2] ?? new(); state.PendingDeleteCommits = sets[3] ?? new();
        state.Publication = fields.Publication is { } p ? new() { Commit = p.Commit, CommitPath = p.CommitPath, DeltaId = p.DeltaId,
            RemoteVerified = p.RemoteVerified, Closure = sets[4] ?? new(), Removed = sets[5] ?? new() } : null;
    }
    private static void ValidatePatches(CloudCacheScope scope, CloudSyncCacheState state, IEnumerable<Patch> patches)
    {
        var seen = new HashSet<int>();
        foreach (var patch in patches)
        {
            if (patch.Set is < 0 or > 5 || !seen.Add(patch.Set) || patch.Added is null || patch.Removed is null || patch.Reset && patch.Removed.Length != 0)
                throw new IOException("同步缓存增量格式无效。");
            foreach (string item in patch.Added.Concat(patch.Removed)) CloudSyncCacheState.ValidateItem(scope, patch.Set, item);
            if (state.Publication is { } publication)
            {
                if (patch.Set == 4 && patch.Added.Any(publication.Removed.Contains) || patch.Set == 5 && patch.Added.Any(publication.Closure.Contains))
                    throw new IOException("发布增量集合交叠。");
            }
            else if (patch.Set >= 4) throw new IOException("增量没有对应发布意图。");
        }
    }
    private static bool Unchanged(string path, Entry entry) => Stamp.Read(path) == entry.CheckpointStamp && Stamp.Read(path + ".wal") == entry.JournalStamp;
    private static void TrimWarm()
    {
        if (Warm.Count <= 8) return;
        foreach (var pair in Warm.Where(pair => !Active.ContainsKey(pair.Key)).OrderBy(pair => pair.Value.Used).ToArray())
        {
            if (Warm.Count <= 8) break;
            // Avoid evicting a cache operation that is not wrapped in an outer lease.
            var operation = Operations.GetOrAdd(pair.Key, _ => new(1, 1));
            if (!operation.Wait(0)) continue;
            try { Warm.TryRemove(pair.Key, out _); } finally { operation.Release(); }
        }
    }

    private async Task<Entry?> ReadAsync(string path, CloudCacheScope scope, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var checkpointStamp = Stamp.Read(path); var walStamp = Stamp.Read(path + ".wal");
            Checkpoint? checkpoint;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var header = new byte[40]; await file.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                if (!header.AsSpan(0, 8).SequenceEqual(Magic)) return null;
                using var sha = SHA256.Create();
                byte[]? digest;
                using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Read, leaveOpen: true))
                {
                    checkpoint = await JsonSerializer.DeserializeAsync<Checkpoint>(hashing, CloudRepository.Json, ct).ConfigureAwait(false);
                    var tail = new byte[4096]; while (await hashing.ReadAsync(tail, ct).ConfigureAwait(false) != 0) { }
                    digest = sha.Hash;
                }
                if (digest is null || !CryptographicOperations.FixedTimeEquals(header.AsSpan(8), digest)) return null;
                Interlocked.Increment(ref checkpointReads); Interlocked.Add(ref bytesRead, file.Length);
            }
            if (checkpoint is null || checkpoint.Epoch == Guid.Empty || checkpoint.State is null) return null;
            var state = checkpoint.State; state.Validate(scope);
            foreach (var set in state.Sets()) set?.AcceptChanges();
            using var journal = new FileStream(path + ".wal", FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var walHeader = new byte[WalHeaderLength]; await journal.ReadExactlyAsync(walHeader, ct).ConfigureAwait(false);
            if (!walHeader.AsSpan(0, 8).SequenceEqual(WalMagic) || new Guid(walHeader.AsSpan(8, 16)) != checkpoint.Epoch ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(walHeader.AsSpan(0, 24)), walHeader.AsSpan(24))) return null;
            long sequence = 0, records = 0; var lengthBytes = new byte[4];
            while (journal.Position < journal.Length)
            {
                await journal.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                if (length <= 0 || length > MaximumRecordBytes || journal.Length - journal.Position < length + 32L) return null;
                var payload = new byte[length]; var checksum = new byte[32];
                await journal.ReadExactlyAsync(payload, ct).ConfigureAwait(false); await journal.ReadExactlyAsync(checksum, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), checksum)) return null;
                var record = JsonSerializer.Deserialize<Record>(payload, CloudRepository.Json);
                if (record is null || record.Epoch != checkpoint.Epoch || record.Sequence != ++sequence || record.Before != state.SnapshotId || record.Values is null || record.Changes is null || record.Changes.Length > 6) return null;
                var sets = state.Sets();
                if (record.Values.Publication is null) { sets[4] = null; sets[5] = null; }
                else if (state.Publication is null) { sets[4] = new(); sets[5] = new(); }
                foreach (var patch in record.Changes)
                {
                    if (patch.Set is < 0 or > 5 || patch.Added is null || patch.Removed is null || patch.Set >= 4 && record.Values.Publication is null) return null;
                    var set = patch.Reset ? new CacheStringSet() : sets[patch.Set] ?? new();
                    set.ExceptWith(patch.Removed); set.UnionWith(patch.Added); sets[patch.Set] = set;
                }
                ApplyFields(state, record.Values, sets); state.ValidateHeader(scope); ValidatePatches(scope, state, record.Changes);
                foreach (var set in state.Sets()) set?.AcceptChanges();
                records++; Interlocked.Increment(ref journalReads);
            }
            Interlocked.Add(ref bytesRead, journal.Length);
            if (Stamp.Read(path) != checkpointStamp || Stamp.Read(path + ".wal") != walStamp) return null;
            var entry = new Entry { State = state, Baseline = Capture(state), Sets = state.Sets(), Epoch = checkpoint.Epoch,
                Sequence = sequence, Records = records, WalBytes = journal.Length, Used = Interlocked.Increment(ref clock),
                CheckpointStamp = checkpointStamp, JournalStamp = walStamp };
            entry.Commit(); return entry;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    private async Task CheckpointAsync(string path, CloudSyncCacheState state, CancellationToken ct)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Guid epoch = Guid.NewGuid(); state.SnapshotId = Guid.NewGuid().ToString("N");
        try
        {
            // Invalidate the old proof before replacing either generation. A
            // crash here can lose cached knowledge, never resurrect an old proof.
            if (File.Exists(path))
            {
                using var old = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
                old.Write("INVALID\n"u8); old.Flush(true);
            }
            var walHeader = new byte[WalHeaderLength]; WalMagic.CopyTo(walHeader, 0); epoch.TryWriteBytes(walHeader.AsSpan(8, 16));
            SHA256.HashData(walHeader.AsSpan(0, 24)).CopyTo(walHeader, 24);
            using (var journal = new FileStream(path + ".wal", FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await journal.WriteAsync(walHeader, ct).ConfigureAwait(false); await journal.FlushAsync(ct).ConfigureAwait(false); journal.Flush(true);
            }
            long size;
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(Magic, ct).ConfigureAwait(false); await file.WriteAsync(new byte[32], ct).ConfigureAwait(false);
                byte[] digest;
                using (var sha = SHA256.Create())
                using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write, leaveOpen: true))
                {
                    await JsonSerializer.SerializeAsync(hashing, new Checkpoint(epoch, state), CloudRepository.Json, ct).ConfigureAwait(false);
                    await hashing.FlushFinalBlockAsync(ct).ConfigureAwait(false); digest = sha.Hash!;
                }
                file.Position = 8; await file.WriteAsync(digest, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false); file.Flush(true); size = file.Length;
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
            var entry = new Entry { State = state, Baseline = Capture(state), Sets = state.Sets(), Epoch = epoch,
                WalBytes = WalHeaderLength, CheckpointStamp = Stamp.Read(path), JournalStamp = Stamp.Read(path + ".wal"), Used = Interlocked.Increment(ref clock) };
            entry.Commit(); Warm[path] = entry;
            Interlocked.Increment(ref checkpointWrites); Interlocked.Add(ref bytesWritten, size + WalHeaderLength);
        }
        catch { Warm.TryRemove(path, out _); throw; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
