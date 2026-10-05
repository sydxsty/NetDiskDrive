using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Baidu;

internal sealed record CachedCloudObject(CloudObjectInfo Info, string? Sha256 = null, CanonicalObjectDescriptor? Canonical = null);
internal readonly record struct CloudCacheLookup(bool Known, CachedCloudObject? Item);

/// <summary>Complete directory proofs for one exclusively written account. No credentials are persisted.</summary>
internal sealed partial class BaiduMetadataCache : IDisposable
{
    internal const int MaximumDirectoryEntries = 16384;
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ODCACHE2\n");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, DirectoryState> states = new(StringComparer.Ordinal);
    private readonly int maximumMemoryEntries;
    private readonly string accountId;
    private readonly string providerId;
    private readonly string? directory;
    private readonly FileStream? lease;
    private long revision, clock;
    private bool disposed;

    private sealed class DirectoryState
    {
        public Dictionary<string, CachedCloudObject>? Entries;
        public bool Loaded;
        public int Mutations;
        public long Used;
        public Guid CheckpointId;
        public long JournalSequence, JournalBytes;
        public int JournalRecords;
    }
    private sealed record Payload(int Version, string ProviderId, string AccountId, string Directory, Guid CheckpointId, CachedCloudObject[] Entries);

    public BaiduMetadataCache(string? root, string providerId, string accountId, int maximumMemoryEntries)
    {
        this.providerId = providerId; this.accountId = accountId; this.maximumMemoryEntries = maximumMemoryEntries;
        if (root is null) return;
        try
        {
            directory = Path.Combine(Path.GetFullPath(root), Hash(Encoding.UTF8.GetBytes(providerId + "\n" + accountId)));
            Directory.CreateDirectory(directory);
            lease = new FileStream(Path.Combine(directory, "lease.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { throw Failure("MetadataCacheUnavailable"); }
        catch (UnauthorizedAccessException) { throw Failure("MetadataCacheUnavailable"); }
    }

    public async Task<CloudCacheLookup> LookupAsync(string path, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await StateAsync(Parent(path), ct).ConfigureAwait(false);
            var result = state.Entries is { } items ? new CloudCacheLookup(true, items.GetValueOrDefault(path)) : default;
            Evict(); return result;
        }
        finally { gate.Release(); }
    }

    public async Task<long> ListingRevisionAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return revision; }
        finally { gate.Release(); }
    }

    public async Task RecordListingAsync(string path, IReadOnlyList<CloudObjectInfo> items, long observedRevision, CancellationToken ct)
    {
        if (items.Count > MaximumDirectoryEntries) return;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await StateAsync(path, ct).ConfigureAwait(false);
            // A listing concurrent with our own mutation is not a complete proof.
            if (revision != observedRevision || state.Mutations != 0) return;
            var values = new Dictionary<string, CachedCloudObject>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (Parent(item.Path) != path || !values.TryAdd(item.Path, new(item))) throw Failure("InvalidMetadataListing");
                if (state.Entries?.GetValueOrDefault(item.Path) is { } prior && SameIdentity(prior.Info, item)) values[item.Path] = new(item, prior.Sha256, prior.Canonical);
            }
            state.Entries = values; state.Loaded = true; state.Used = ++clock;
            await SaveAsync(path, state, ct).ConfigureAwait(false); Evict();
        }
        finally { gate.Release(); }
    }

    public async Task RememberVerifiedAsync(CachedCloudObject item, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var parent = Parent(item.Info.Path); var state = await StateAsync(parent, ct).ConfigureAwait(false);
            if (state.Entries is null) return; // A single receipt cannot prove all siblings absent.
            state.Entries[item.Info.Path] = item;
            await AppendAsync(parent, state, "patch", Guid.Empty, [item], null, ct).ConfigureAwait(false);
            await MaybeCheckpointAsync(parent, state, ct).ConfigureAwait(false);
            Evict();
        }
        finally { gate.Release(); }
    }

    public async Task RememberEmptyDirectoryAsync(string path, long expectedRevision, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await StateAsync(path, ct).ConfigureAwait(false);
            if (state.Mutations != 0 || revision != expectedRevision || state.Entries is { Count: > 0 }) return;
            state.Entries = new(StringComparer.Ordinal); state.Loaded = true; state.Used = ++clock;
            await SaveAsync(path, state, ct).ConfigureAwait(false); Evict();
        }
        finally { gate.Release(); }
    }

    public async Task<Mutation> BeginMutationAsync(IReadOnlyList<string> paths, bool deletingDirectory, CancellationToken ct)
    {
        var parents = paths.Select(Parent).Distinct(StringComparer.Ordinal).ToArray();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (deletingDirectory) InvalidateAllLocked(); // Descendant cache files must not survive a directory deletion.
            var mutationId = Guid.NewGuid();
            try
            {
                foreach (var parent in parents)
                {
                    var state = await StateAsync(parent, ct).ConfigureAwait(false);
                    if (state.Entries is null) InvalidateFile(parent);
                    else await AppendAsync(parent, state, "begin", mutationId, null, null, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                foreach (var parent in parents)
                {
                    if (states.TryGetValue(parent, out var state)) state.Entries = null;
                    try { InvalidateFile(parent); } catch (CloudProviderException) { }
                }
                throw;
            }
            revision++;
            foreach (var parent in parents) states[parent].Mutations++;
            return new Mutation(this, parents, revision, mutationId);
        }
        finally { gate.Release(); }
    }

    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { InvalidateAllLocked(); }
        finally { gate.Release(); }
    }
    private void InvalidateAllLocked()
    {
        revision++;
        foreach (var state in states.Values) { state.Entries = null; state.Loaded = true; }
        if (directory is null) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.cache")) InvalidateFilePath(file);
    }

    private async Task<long?> FinishAsync(string[] parents, long startedRevision, Guid mutationId, IReadOnlyList<CachedCloudObject>? changed, IReadOnlyList<string>? removed, bool success)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var uninterrupted = revision == startedRevision;
            revision++;
            foreach (var parent in parents) states[parent].Mutations--;
            try
            {
            foreach (var parent in parents)
            {
                var state = states[parent];
                if (!success) { state.Entries = null; state.Loaded = true; InvalidateFile(parent); continue; }
                if (state.Entries is { } values)
                {
                    if (changed is not null) foreach (var item in changed.Where(i => Parent(i.Info.Path) == parent)) values[item.Info.Path] = item;
                    if (removed is not null) foreach (var path in removed.Where(p => Parent(p) == parent)) values.Remove(path);
                    await AppendAsync(parent, state, "commit", mutationId,
                        changed?.Where(i => Parent(i.Info.Path) == parent).ToArray(),
                        removed?.Where(p => Parent(p) == parent).ToArray(), CancellationToken.None).ConfigureAwait(false);
                    await MaybeCheckpointAsync(parent, state, CancellationToken.None).ConfigureAwait(false);
                }
            }
            Evict();
            return success && uninterrupted ? revision : null;
            }
            catch
            {
                foreach (var parent in parents)
                {
                    states[parent].Entries = null;
                    try { InvalidateFile(parent); } catch (CloudProviderException) { }
                }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    internal sealed class Mutation(BaiduMetadataCache owner, string[] parents, long startedRevision, Guid mutationId) : IAsyncDisposable
    {
        private bool finished;
        public long? UninterruptedCompletion { get; private set; }
        public async Task CompleteAsync(IReadOnlyList<CachedCloudObject>? changed = null, IReadOnlyList<string>? removed = null)
        {
            if (finished) throw new InvalidOperationException("Cache mutation already finished.");
            // Mark finished before persistence: a save error must not decrement twice.
            finished = true; UninterruptedCompletion = await owner.FinishAsync(parents, startedRevision, mutationId, changed, removed, true).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            if (finished) return;
            finished = true; _ = await owner.FinishAsync(parents, startedRevision, mutationId, null, null, false).ConfigureAwait(false);
        }
    }

    private async Task<DirectoryState> StateAsync(string path, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!states.TryGetValue(path, out var state)) states[path] = state = new();
        state.Used = ++clock;
        if (!state.Loaded)
        {
            state.Loaded = true;
            if (directory is not null) state.Entries = await LoadAsync(path, state, ct).ConfigureAwait(false);
        }
        return state;
    }
    private void Evict()
    {
        foreach (var key in states.Where(p => p.Value.Mutations == 0 && p.Value.Entries is null).Select(p => p.Key).ToArray()) states.Remove(key);
        var used = states.Values.Sum(s => s.Entries is null ? 0 : Math.Max(1, s.Entries.Count));
        foreach (var pair in states.Where(p => p.Value.Mutations == 0).OrderBy(p => p.Value.Used).ToArray())
        {
            if (used <= maximumMemoryEntries) break;
            used -= pair.Value.Entries is null ? 0 : Math.Max(1, pair.Value.Entries.Count);
            states.Remove(pair.Key); // Disk checkpoints remain intact.
        }
    }
    private string FilePath(string path) => Path.Combine(directory!, Hash(Encoding.UTF8.GetBytes(path)) + ".cache");
    private void InvalidateFile(string parent) { if (directory is not null) InvalidateFilePath(FilePath(parent)); }
    private static void InvalidateFilePath(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            file.Write("INVALID\n"u8); file.Flush(true);
        }
        catch (IOException) { throw Failure("MetadataCacheInvalidationFailed"); }
        catch (UnauthorizedAccessException) { throw Failure("MetadataCacheInvalidationFailed"); }
    }

    private static string Parent(string path) { var i = path.LastIndexOf('/'); return i <= 0 ? "/" : path[..i]; }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool SameIdentity(CloudObjectInfo a, CloudObjectInfo b) => a.Path == b.Path && a.Length == b.Length && a.IsDirectory == b.IsDirectory &&
        a.RemoteId == b.RemoteId && a.ProviderChecksum == b.ProviderChecksum && a.LastModified == b.LastModified;
    private static CloudProviderException Failure(string code) => new(code, "The local Baidu metadata cache could not be safely updated.");
    public void Dispose() { disposed = true; lease?.Dispose(); gate.Dispose(); }
}
