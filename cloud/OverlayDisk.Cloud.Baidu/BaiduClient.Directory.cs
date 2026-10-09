using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Baidu;

public sealed partial class BaiduClient
{
    public async IAsyncEnumerable<CloudObjectInfo> ListAsync(string directory,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        directory = PathValue(directory);
        RequireBduss();
        var cache = await MetadataAsync(cancellationToken).ConfigureAwait(false);
        var revision = cache is null ? 0 : await cache.ListingRevisionAsync(cancellationToken).ConfigureAwait(false);
        List<CloudObjectInfo>? complete = cache is null ? null : [];
        string? previousPage = null;
        for (var page = 1; page <= options.MaximumListPages; page++)
        {
            using var document = await JsonAsync(HttpMethod.Get, Url(Pan, "rest/2.0/xpan/file",
                ("method", "list"), ("dir", directory), ("page", page.ToString(CultureInfo.InvariantCulture)),
                ("num", options.PageSize.ToString(CultureInfo.InvariantCulture)), ("order", "name"), ("desc", "0"), ("web", "1")),
                null, "list", cancellationToken, native: true, missingDirectory: directory).ConfigureAwait(false);
            var root = Data(document.RootElement);
            if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new CloudProviderException("MissingListing", "Baidu did not return a directory listing.");
            if (list.GetArrayLength() > options.PageSize)
                throw new CloudProviderException("MalformedListing", "Baidu returned an oversized listing page.");
            var signature = HashHex(SHA256.HashData(Encoding.UTF8.GetBytes(list.GetRawText())));
            if (list.GetArrayLength() > 0 && signature == previousPage)
                throw new CloudProviderException("RepeatedListing", "Baidu repeated a directory page; enumeration is incomplete.", isTransient: true);
            previousPage = signature;
            foreach (var item in list.EnumerateArray())
            {
                var parsed = ParseObject(item);
                var parent = Parent(parsed.Path);
                if (parent != directory || parsed.Path == directory)
                    throw new CloudProviderException("MalformedListing", "Baidu returned an object outside the requested directory.");
                if (complete is not null)
                {
                    if (complete.Count == BaiduMetadataCache.MaximumDirectoryEntries) complete = null;
                    else complete.Add(parsed);
                }
                yield return parsed;
            }
            if (list.GetArrayLength() < options.PageSize)
            {
                if (cache is not null && complete is not null) await cache.RecordListingAsync(directory, complete, revision, cancellationToken).ConfigureAwait(false);
                yield break;
            }
        }
        throw new CloudProviderException("ListingLimit", "The directory listing limit was reached before completion.");
    }

    public async Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken = default)
        => await HeadCoreAsync(path, false, cancellationToken).ConfigureAwait(false);

    private async Task<CloudObjectInfo?> HeadCoreAsync(string path, bool fresh, CancellationToken cancellationToken)
    {
        path = PathValue(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (path == "/") { _ = await ValidateAsync(cancellationToken).ConfigureAwait(false); return new CloudObjectInfo("/", 0, true); }
        if (!fresh && await MetadataAsync(cancellationToken).ConfigureAwait(false) is { } cache)
        {
            var known = await cache.LookupAsync(path, cancellationToken).ConfigureAwait(false);
            if (known.Known) return known.Item?.Info;
        }
        try
        {
            CloudObjectInfo? found = null;
            await foreach (var item in ListAsync(Parent(path), cancellationToken).ConfigureAwait(false))
                if (item.Path == path) found = item;
            return found;
        }
        catch (CloudObjectNotFoundException)
        { return null; }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = PathValue(path);
        var cache = await MetadataAsync(cancellationToken).ConfigureAwait(false);
        var current = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            var pathGate = PathGate(current);
            await pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            var existing = await HeadAsync(current, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!existing.IsDirectory) throw new CloudObjectConflictException(current);
                continue;
            }
            await InitializeWebSessionAsync(cancellationToken).ConfigureAwait(false);
            await using var mutation = cache is null ? null : await cache.BeginMutationAsync([current], false, cancellationToken).ConfigureAwait(false);
            CloudObjectInfo? directoryInfo;
            var newlyCreated = false;
            try
            {
                using var created = await JsonAsync(HttpMethod.Post, WebUrl("api/create", ("a", "commit")),
                    Form(("path", current), ("isdir", "1"), ("block_list", "[]"), ("size", "0"), ("rtype", "0")),
                    "mkdir", cancellationToken, retry: false).ConfigureAwait(false);
                directoryInfo = CreatedMetadata(Data(created.RootElement), current, 0, true);
                directoryInfo ??= await HeadCoreAsync(current, true, cancellationToken).ConfigureAwait(false);
                newlyCreated = true;
            }
            catch (CloudProviderException)
            {
                // A lost response or a concurrent creator is only success after stat confirms the directory.
                directoryInfo = await HeadCoreAsync(current, true, cancellationToken).ConfigureAwait(false);
                if (directoryInfo?.IsDirectory != true) throw;
            }
            if (directoryInfo?.IsDirectory != true)
                throw new CloudProviderException("DirectoryNotConfirmed", "Baidu did not confirm the newly created directory.", isTransient: true);
            if (mutation is not null) await mutation.CompleteAsync([new(directoryInfo)]).ConfigureAwait(false);
            if (newlyCreated && cache is not null && mutation?.UninterruptedCompletion is { } completion)
                await cache.RememberEmptyDirectoryAsync(current, completion, cancellationToken).ConfigureAwait(false);
            }
            finally { pathGate.Release(); }
        }
    }

    private static CloudObjectInfo? CreatedMetadata(JsonElement value, string path, long length, bool directory)
    {
        var returnedPath = Text(value, "path"); var returnedLength = Num(value, "size"); var isDirectory = Num(value, "isdir");
        if (returnedPath is not null && returnedPath != path || returnedLength is not null && returnedLength != length ||
            isDirectory is not null && isDirectory != (directory ? 1 : 0))
            throw new CloudProviderException("CreatedMetadataMismatch", "Baidu's create response does not describe the requested object.");
        var id = Text(value, "fs_id");
        if (returnedPath is null || returnedLength is null || isDirectory is null || !ulong.TryParse(id, out var number) || number == 0) return null;
        return new CloudObjectInfo(path, length, directory, id, Text(value, "md5"));
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        => DeleteManyAsync([path], cancellationToken);

    public async Task DeleteManyAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalized = paths.Select(PathValue).Distinct(StringComparer.Ordinal).ToArray();
        if (normalized.Contains("/", StringComparer.Ordinal)) throw new ArgumentException("The account root cannot be deleted.", nameof(paths));
        foreach (var chunk in normalized.Chunk(64))
        {
            // Acquire stripes in a common order; different paths can share a stripe.
            var locks = chunk.Select(PathGate).Distinct().OrderBy(g => Array.IndexOf(this.paths, g)).ToArray();
            var acquired = 0;
            try
            {
                foreach (var mutex in locks) { await mutex.WaitAsync(cancellationToken).ConfigureAwait(false); acquired++; }
                await DeleteBatchAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
            finally { for (var i = acquired - 1; i >= 0; i--) locks[i].Release(); }
        }
    }

    private async Task DeleteBatchAsync(string[] paths, CancellationToken cancellationToken)
    {
        var cache = await MetadataAsync(cancellationToken).ConfigureAwait(false);
        var targets = new List<CloudObjectInfo>();
        foreach (var group in paths.GroupBy(Parent, StringComparer.Ordinal))
        {
            var cached = new List<CloudObjectInfo>(); var complete = cache is not null;
            if (cache is not null)
                foreach (var path in group)
                {
                    var item = await cache.LookupAsync(path, cancellationToken).ConfigureAwait(false);
                    if (!item.Known) { complete = false; break; }
                    if (item.Item is not null) cached.Add(item.Item.Info);
                }
            if (complete) targets.AddRange(cached);
            else
            {
                var wanted = group.ToHashSet(StringComparer.Ordinal);
                targets.AddRange(await FreshMatchesAsync(group.Key, wanted, cancellationToken).ConfigureAwait(false));
            }
        }
        if (targets.Count == 0) return;
        await InitializeWebSessionAsync(cancellationToken).ConfigureAwait(false);
        var selected = targets.Select(t => t.Path).ToArray();
        await using var mutation = cache is null ? null : await cache.BeginMutationAsync(selected, targets.Any(t => t.IsDirectory), cancellationToken).ConfigureAwait(false);
        CloudProviderException? rejection = null;
        try
        {
            using var result = await JsonAsync(HttpMethod.Post, WebUrl("api/filemanager", ("opera", "delete"),
                ("async", "2"), ("onnest", "fail"), ("newVerify", "1")),
                Form(("filelist", JsonSerializer.Serialize(selected))), "delete", cancellationToken, retry: false).ConfigureAwait(false);
            CheckItemErrors(result.RootElement);
            var task = Text(Data(result.RootElement), "taskid");
            if (task is not null and not "0") await WaitDeleteTaskAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException error) { rejection = error; }
        // Missing/ambiguous replies, including code 12, are never blanket success.
        // Only a fresh listing of each affected parent can establish absence.
        for (var attempt = 0; attempt < (rejection is null ? options.MaximumAttempts : 1); attempt++)
        {
            var remaining = new List<CloudObjectInfo>();
            foreach (var group in selected.GroupBy(Parent, StringComparer.Ordinal))
                remaining.AddRange(await FreshMatchesAsync(group.Key, group.ToHashSet(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false));
            if (remaining.Count == 0)
            {
                if (mutation is not null) await mutation.CompleteAsync(removed: selected).ConfigureAwait(false);
                return;
            }
            if (rejection is not null) throw rejection;
            if (attempt + 1 < options.MaximumAttempts) await Task.Delay(options.TaskPollDelay, cancellationToken).ConfigureAwait(false);
        }
        throw new CloudProviderException("DeleteNotConfirmed", "The cloud objects are still present after deletion.", isTransient: true);
    }

    private async Task<List<CloudObjectInfo>> FreshMatchesAsync(string parent, HashSet<string> paths, CancellationToken cancellationToken)
    {
        var matches = new List<CloudObjectInfo>();
        try
        {
            await foreach (var item in ListAsync(parent, cancellationToken).ConfigureAwait(false))
                if (paths.Contains(item.Path)) matches.Add(item);
        }
        catch (CloudObjectNotFoundException) { }
        return matches;
    }

    private async Task WaitDeleteTaskAsync(string task, CancellationToken cancellationToken)
    {
        for (var poll = 0; poll < options.MaximumTaskPolls; poll++)
        {
            if (poll > 0) await Task.Delay(options.TaskPollDelay, cancellationToken).ConfigureAwait(false);
            using var status = await JsonAsync(HttpMethod.Get, WebUrl("share/taskquery", ("taskid", task)),
                null, "delete-poll", cancellationToken).ConfigureAwait(false);
            CheckItemErrors(status.RootElement);
            var state = Text(Data(status.RootElement), "status");
            if (state == "success") return;
            if (state == "failed") throw new CloudProviderException("DeleteFailed", "Baidu's deletion task failed.");
            if (state is not ("pending" or "running" or "processing" or "waiting"))
                throw new CloudProviderException("UnknownTaskStatus", "Baidu returned an unknown deletion task state.");
        }
        throw new CloudProviderException("DeleteStillRunning", "Baidu has not finished the deletion task.", isTransient: true);
    }

    private static void CheckItemErrors(JsonElement root)
    {
        if (Num(root, "task_errno") is { } taskError && taskError != 0) throw ApiError(taskError);
        foreach (var name in new[] { "info", "list" })
            if (root.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray()) EnsureSuccess(item, false);
        if (root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object) CheckItemErrors(nested);
    }

    private static CloudObjectInfo ParseObject(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new CloudProviderException("MalformedListing", "Baidu returned an invalid file entry.");
        var path = PathValue(Required(item, "path"));
        var isDirectory = Num(item, "isdir") switch { 0 => false, 1 => true, _ => throw new CloudProviderException("MalformedListing", "Baidu omitted the file type.") };
        var length = Num(item, "size");
        if (length is null or < 0) throw new CloudProviderException("MalformedListing", "Baidu omitted the file length.");
        DateTimeOffset? modified = null;
        if (Num(item, "server_mtime") is { } timestamp)
        {
            try { modified = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
            catch (ArgumentOutOfRangeException) { throw new CloudProviderException("MalformedListing", "Baidu returned an invalid modification time."); }
        }
        return new CloudObjectInfo(path, length.Value, isDirectory, Text(item, "fs_id"), Text(item, "md5"), modified);
    }

    private static string Parent(string path) { var last = path.LastIndexOf('/'); return last <= 0 ? "/" : path[..last]; }
    private SemaphoreSlim PathGate(string path) => paths[(int)((uint)StringComparer.Ordinal.GetHashCode(path) % (uint)paths.Length)];
}
