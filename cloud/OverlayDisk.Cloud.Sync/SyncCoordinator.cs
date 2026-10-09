using System.Text.Json;
using System.Security.Cryptography;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Sync;

public sealed class SyncCoordinator(CloudRepository repository)
{
    private readonly SemaphoreSlim payloadReader = new(1, 1);
    private static void Log(IProgress<SyncLogEntry>? sink, string action, string message, string level = "info", ExportObject? item = null, ulong? generation = null, long? wireBytes = null)
        => sink?.Report(new(DateTimeOffset.UtcNow, action, message, level, item?.Id, item?.Kind, item?.Length, generation) { WireBytes = wireBytes });

    public async Task<SyncResult> RunAsync(ICloudVolume volume, CloudBinding binding, string name, ulong capacity, bool encrypted,
        int concurrency, IProgress<TransferProgress>? progress, CancellationToken ct, IProgress<SyncLogEntry>? log = null,
        int prepareCacheMiB = SyncPreparationLimits.DefaultCacheMiB)
    {
        SyncPreparationLimits.ValidateCacheMiB(prepareCacheMiB);
        if (encrypted != (repository.EncryptionSettings(binding.RemoteRoot) is not null)) throw new IOException("云端加密设置与解锁密钥不一致。");
        int objectSize = VolumeObjectSize(volume);
        var scope = repository.Scope(binding, volume.Id);
        await using var lease = await repository.Cache.AcquireAsync(scope, ct);
        var cache = await repository.LoadCacheAsync(scope, ct);
        Log(log, cache.OwnerConfirmed && cache.LatestKnown ? "cache.hit" : "cache.reconcile", cache.OwnerConfirmed && cache.LatestKnown ? "使用本机已确认的云端状态" : "缓存缺失或未完整确认，核对云端版本元数据");
        var initial = Unwrap(await volume.ControlAsync(new { cmd = "cloud.status" }, ct), "status");
        ValidateNativeGeometry(initial, objectSize);
        ValidateCachedGeometry(cache, objectSize);
        if (cache.Latest is { } cachedCommit) repository.AuthenticateCommit(binding.RemoteRoot, cachedCommit);
        if (cache.Publication is { } cachedIntent) repository.AuthenticateCommit(binding.RemoteRoot, cachedIntent.Commit);
        await repository.EnsureWriterAsync(binding, volume.Id, cache, ct);
        await volume.ControlAsync(new { cmd = "cloud.pause", paused = false }, ct);
        await ReconcileAsync(volume, binding, cache, initial, log, ct);
        ValidateCachedGeometry(cache, objectSize);
        if (cache.Latest is { } latestCommit) repository.AuthenticateCommit(binding.RemoteRoot, latestCommit);
        if (NoJob(initial) && !Dirty(initial) && GetLong(initial, "data_generation") == GetLong(initial, "published_generation") && cache.Latest is not null)
            return await UnchangedAsync(volume, binding, cache, progress, log, ct);

        var preparationLog = new PreparationLogTracker(log);
        JsonElement job;
        string jobId;
        try
        {
            if (NoJob(initial))
            {
                preparationLog.BeginFreeze();
                job = Unwrap(await volume.ControlAsync(new { cmd = "cloud.prepare", prepare_cache_mib = prepareCacheMiB,
                    max_pages = 16384, max_leaf_groups = 512, max_objects = 2 }, ct), "job");
            }
            else job = initial.GetProperty("job").Clone();
            if (job.ValueKind == JsonValueKind.Null)
            {
                preparationLog.NoChanges();
                var current = Unwrap(await volume.ControlAsync(new { cmd = "cloud.status" }, ct), "status");
                await ReconcileAsync(volume, binding, cache, current, log, ct);
                return await UnchangedAsync(volume, binding, cache, progress, log, ct);
            }
            ValidateNativeGeometry(job, objectSize);
            jobId = job.GetProperty("id").GetString()!;
            Log(log, "preparing.started", $"固定本次版本，处理变更对象和增量索引；当前磁盘整理缓存上限 {prepareCacheMiB} MiB，不扫描未变化的数据页");
            preparationLog.Observe(job);
            while (GetText(job, "phase") == "preparing")
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(PreparationProgress(job));
                job = Unwrap(await volume.ControlAsync(new { cmd = "cloud.prepare", job_id = jobId,
                    prepare_cache_mib = prepareCacheMiB, max_pages = 16384, max_leaf_groups = 512, max_objects = 2 }, ct), "job");
                ValidateNativeGeometry(job, objectSize);
                preparationLog.Observe(job);
                await Task.Yield();
            }
        }
        catch (Exception error) { preparationLog.Interrupt(error); throw; }
        string rootId = job.GetProperty("root_object_id").GetString() ?? throw new IOException("导出根对象尚未准备完成。");
        string rootHash = job.GetProperty("root_sha256").GetString()!;
        ulong generation = job.GetProperty("generation").GetUInt64();
        long total = checked(GetLong(job, "total_objects") * objectSize), done = 0, reused = 0, uploadedBytes = 0, reusedBytes = 0, logicalUploadedBytes = 0;
        TransferProgress Report(string phase, string message, long completed) => new(phase, message, completed, total, Math.Max(0, total - completed), Estimated: completed < total)
            { UploadedBytes = Interlocked.Read(ref uploadedBytes), ReusedBytes = Interlocked.Read(ref reusedBytes), LogicalUploadedBytes = Interlocked.Read(ref logicalUploadedBytes) };
        var alive = new HashSet<string>(StringComparer.Ordinal); // This generation additions only, never the full closure.
        using var slots = new SemaphoreSlim(Math.Clamp(concurrency, 1, 4));
        var receipts = new ReceiptBatcher(volume, jobId);
        bool rootCounted = false;
        long cursor = 0;
        while (true)
        {
            var batch = await volume.ControlAsync(new { cmd = "cloud.list", job_id = jobId, cursor, limit = 128 }, ct);
            var objects = batch.GetProperty("items").EnumerateArray().Select(ParseObject).ToArray();
            foreach (var obj in objects)
            {
                ValidateObject(obj, objectSize); alive.Add(obj.Id);
                if (obj.Uploaded) { done += obj.Length; reused++; reusedBytes += obj.Length; if (obj.Id == rootId) rootCounted = true; continue; }
                string path = CloudRepository.ObjectPath(binding.RemoteRoot, obj.Id);
                await repository.EnsureFolderAsync(cache, path[..path.LastIndexOf('/')], ct);
            }
            progress?.Report(Report("uploading", "正在上传增量对象", done));
            var uploads = objects.Where(o => !o.Uploaded && o.Id != rootId).Select(async obj =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    Log(log, "upload.started", "准备上传对象；若已有确认回执则复用", item: obj, generation: generation);
                    await volume.ControlAsync(new { cmd = "cloud.transfer", job_id = jobId, object_id = obj.Id, state = "uploading" }, ct);
                    var receipt = await UploadObjectAsync(volume, binding, jobId, obj, ct, log, generation);
                    await receipts.ConfirmAsync(new { object_id = obj.Id, sha256 = obj.Sha256, length = obj.Length, receipt = ShortReceipt(receipt) });
                    if (receipt.ReusedExisting) Interlocked.Add(ref reusedBytes, obj.Length);
                    else { Interlocked.Add(ref uploadedBytes, receipt.Length); Interlocked.Add(ref logicalUploadedBytes, obj.Length); }
                    Log(log, receipt.ReusedExisting ? "upload.reused" : "upload.confirmed", receipt.ReusedExisting ? "复用或找回已有远端对象，回执已记录" : "压缩对象上传已确认并记录回执", item: obj, generation: generation, wireBytes: receipt.ReusedExisting ? 0 : receipt.Length);
                    long count = Interlocked.Add(ref done, obj.Length);
                    progress?.Report(Report("uploading", "正在上传增量对象", count));
                }
                catch (Exception error)
                {
                    try { await volume.ControlAsync(new { cmd = "cloud.transfer", job_id = jobId, object_id = obj.Id, state = error is OperationCanceledException ? "pending" : "failed" }, CancellationToken.None); } catch { }
                    Log(log, "upload.failed", "对象未完成上传：" + error.Message, "error", obj, generation); throw;
                }
                finally { slots.Release(); }
            }).ToArray();
            await Task.WhenAll(uploads);
            if (!batch.TryGetProperty("next_cursor", out var next) || next.ValueKind == JsonValueKind.Null) break;
            cursor = next.GetInt64();
        }
        Log(log, "upload.reused", $"复用 {reused} 个已确认对象，无需创建目录或重新上传", generation: generation);
        ct.ThrowIfCancellationRequested();
        progress?.Report(Report("publishing", "正在提交云端版本", done));
        var rootObject = new ExportObject(rootId, "index", objectSize, rootHash, false);
        ValidateObject(rootObject, objectSize); alive.Add(rootId);
        bool rootAlreadyConfirmed = cache.Publication is { RemoteVerified: true } prior
            && prior.Commit.Generation == generation && prior.Commit.RootObjectId == rootId
            && prior.Commit.RootSha256.Equals(rootHash, StringComparison.OrdinalIgnoreCase);
        if (rootAlreadyConfirmed && !cache.Publication!.Closure.SetEquals(alive)) throw new IOException("恢复的发布清单与固定版本不一致。");
        try
        {
            if (!rootAlreadyConfirmed)
            {
            Log(log, "upload.started", "开始上传版本根对象", item: rootObject, generation: generation);
            await volume.ControlAsync(new { cmd = "cloud.transfer", job_id = jobId, object_id = rootId, state = "uploading" }, ct);
            string rootPath = CloudRepository.ObjectPath(binding.RemoteRoot, rootId);
            await repository.EnsureFolderAsync(cache, rootPath[..rootPath.LastIndexOf('/')], ct);
            var rootReceipt = await UploadObjectAsync(volume, binding, jobId, rootObject, ct, log, generation);
            await volume.ControlAsync(new { cmd = "cloud.receipt", job_id = jobId, object_id = rootId, sha256 = rootHash, length = objectSize, receipt = ShortReceipt(rootReceipt) }, ct);
            // A resumed root may already have contributed to the native receipt count.
            if (!rootCounted) { if (rootReceipt.ReusedExisting) reusedBytes += objectSize; else { uploadedBytes += rootReceipt.Length; logicalUploadedBytes += objectSize; } }
            Log(log, rootReceipt.ReusedExisting ? "upload.reused" : "upload.confirmed", rootReceipt.ReusedExisting ? "复用已确认的版本根对象" : "压缩版本根对象上传成功，回执已保存", item: rootObject, generation: generation, wireBytes: rootReceipt.ReusedExisting ? 0 : rootReceipt.Length);
            }
        }
        catch (Exception error)
        {
            try { await volume.ControlAsync(new { cmd = "cloud.transfer", job_id = jobId, object_id = rootId, state = error is OperationCanceledException ? "pending" : "failed" }, CancellationToken.None); } catch { }
            Log(log, "upload.failed", "版本根对象未确认：" + error.Message, "error", rootObject, generation); throw;
        }

        var latest = cache.Publication?.RemoteVerified == true ? cache.Publication.Commit : cache.Latest;
        if (latest is not null && (latest.WriterId != binding.DeviceId || latest.Generation > generation || latest.Generation == generation && (latest.RootObjectId != rootId || !latest.RootSha256.Equals(rootHash, StringComparison.OrdinalIgnoreCase))))
            throw new IOException("云端版本与本机状态冲突，已停止覆盖。");
        var intent = cache.Publication;
        if (intent is not null && (intent.Commit.Generation != generation || intent.Commit.RootObjectId != rootId || !intent.Commit.RootSha256.Equals(rootHash, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("尚有另一发布意图需要先恢复。");
        var commit = intent?.Commit ?? (latest?.RootObjectId == rootId ? latest : null) ??
            repository.ProtectCommit(binding.RemoteRoot, new RemoteCommit(4, volume.Id, binding.DeviceId, generation, name, capacity, encrypted, rootId, rootHash, DateTimeOffset.UtcNow) { ObjectSizeBytes = objectSize });
        string commitPath = CloudRepository.CommitPath(binding.RemoteRoot, commit);
        var removed = intent?.Removed ?? await DeltaAsync(volume, jobId, "remove", ct);
        if (removed.Overlaps(alive)) throw new IOException("本轮增量同时添加和删除同一对象。");
        cache.Publication = new() { Commit = commit, CommitPath = commitPath, Closure = alive, Removed = removed, DeltaId = jobId, RemoteVerified = intent?.RemoteVerified == true };
        cache.PendingDeleteObjects.ExceptWith(alive); cache.PendingDeleteCommits.Remove(commitPath);
        // A restart must retain both the old closure and the exact immutable
        // descriptor (including timestamp) before any remote publication.
        await repository.SaveCacheAsync(cache, ct);
        if (!cache.Publication.RemoteVerified)
        {
            await repository.PutJsonAsync(commitPath, commit, ct);
            cache.Publication.RemoteVerified = true;
            await repository.SaveCacheAsync(cache, ct);
        }
        var published = Unwrap(await volume.ControlAsync(new { cmd = "cloud.commit", job_id = jobId, root_object_id = rootId, root_sha256 = rootHash, receipt = commitPath }, ct), "status");
        await FinalizeAsync(cache, commit, commitPath, alive, removed, ct);
        Log(log, "commit.confirmed", "云端已返回版本提交成功，本地回执已持久保存", generation: generation);
        bool pendingChanges = Pending(published);
        bool pendingCleanup = cache.CleanupPending;
        if (pendingCleanup) Log(log, "cleanup.waiting_user", "旧对象已加入清单，等待手动清理");
        progress?.Report(Report(pendingChanges ? "pending" : "synced", pendingChanges ? "此快照已同步，仍有新变更待上传" : "云端已同步", total) with { Estimated = pendingChanges });
        return new(commit, commitPath, pendingCleanup, pendingChanges);
    }

    private async Task ReconcileAsync(ICloudVolume volume, CloudBinding binding, CloudSyncCacheState cache, JsonElement status, IProgress<SyncLogEntry>? log, CancellationToken ct)
    {
        ulong published = checked((ulong)GetLong(status, "published_generation"));
        if (cache.Publication is { } intent)
        {
            if (!intent.RemoteVerified)
            {
                try
                {
                    var saved = await repository.ReadCommitAsync(binding.RemoteRoot, intent.CommitPath, ct);
                    if (saved != intent.Commit) throw new IOException("恢复发布意图时发现提交记录不一致。");
                    intent.RemoteVerified = true; await repository.SaveCacheAsync(cache, ct);
                }
                catch (CloudObjectNotFoundException) { }
            }
            if (published == intent.Commit.Generation && NoJob(status))
            {
                if (!intent.RemoteVerified || !MatchesPublished(status, intent.Commit)) throw new IOException("本地发布记录与云端已确认版本不一致。");
                await FinalizeAsync(cache, intent.Commit, intent.CommitPath, intent.Closure, intent.Removed, ct);
                Log(log, "cache.recovered", "已恢复持久发布增量和清理计划", generation: published);
            }
            else if (published > intent.Commit.Generation) throw new IOException("同步缓存落后于本地发布进度，保留云端数据等待核对。");
        }
        if (cache.LatestKnown && cache.ClosureGeneration == published) return;
        if (!cache.LatestKnown || cache.Latest is not null && cache.Latest.Generation < published)
        {
            var latest = await repository.LatestAsync(binding.RemoteRoot, ct);
            if (latest is { } found && found.Commit.WriterId != binding.DeviceId) throw new IOException("云端版本属于另一写入设备。");
            cache.Latest = latest?.Commit; cache.LatestPath = latest?.Path; cache.LatestKnown = true;
        }
        if (published > 0)
        {
            if (cache.Latest is null || cache.Latest.Generation < published || cache.Latest.Generation == published && !MatchesPublished(status, cache.Latest))
                throw new IOException("本地已发布根与云端版本不一致。");
            // Recovery reads only the retained publication delta, never the volume's page map or full object closure.
            if (status.TryGetProperty("published_commit", out var native))
            {
                string delta = GetText(native, "delta_id");
                if (delta.Length == 0) delta = GetText(native, "id");
                if (delta.Length != 0 && cache.Latest.Generation == published)
                {
                    var added = await DeltaAsync(volume, delta, "add", ct);
                    var removed = await DeltaAsync(volume, delta, "remove", ct);
                    cache.PendingDeleteObjects.UnionWith(removed); cache.PendingDeleteObjects.ExceptWith(added);
                }
            }
        }
        cache.PublishedClosure.Clear(); // V4 stores durable additions/removals; no replicated full object catalog.
        cache.ClosureGeneration = published;
        cache.PublishedCommitPath = cache.Latest?.Generation == published ? cache.LatestPath : null;
        if (cache.Latest is not null) cache.PendingDeleteObjects.Remove(cache.Latest.RootObjectId);
        await repository.SaveCacheAsync(cache, ct);
    }
    private static bool MatchesPublished(JsonElement status, RemoteCommit commit)
    {
        if (!NativeGeometryMatches(status, commit.ObjectSizeBytes)) return false;
        if (!status.TryGetProperty("published_commit", out var value) || value.ValueKind != JsonValueKind.Object) return false;
        return GetText(value, "root_object_id") == commit.RootObjectId && GetText(value, "root_sha256").Equals(commit.RootSha256, StringComparison.OrdinalIgnoreCase)
            && (ulong)GetLong(value, "generation") == commit.Generation;
    }

    private async Task<SyncResult> UnchangedAsync(ICloudVolume volume, CloudBinding binding, CloudSyncCacheState cache, IProgress<TransferProgress>? progress, IProgress<SyncLogEntry>? log, CancellationToken ct)
    {
        var commit = cache.Latest ?? throw new IOException("本地标记已发布，但云端没有对应完整版本。");
        var before = Unwrap(await volume.ControlAsync(new { cmd = "cloud.status" }, ct), "status");
        if (commit.WriterId != binding.DeviceId || commit.Generation != (ulong)GetLong(before, "published_generation") || !MatchesPublished(before, commit)) throw new IOException("云端发布状态与本机不一致。");
        bool cleanup = cache.CleanupPending;
        var after = Unwrap(await volume.ControlAsync(new { cmd = "cloud.status" }, ct), "status");
        bool pending = Pending(after);
        Log(log, "sync.unchanged", cleanup ? "数据无变化，旧对象等待手动清理" : "数据无变化，无需上传云端对象", generation: commit.Generation);
        progress?.Report(new(pending ? "pending" : "synced", pending ? "有新变更，等待下一次同步" : "云端已同步，没有需要上传的新对象", 0, 0, 0, pending));
        return new(commit, cache.LatestPath!, cleanup, pending);
    }
    private async Task FinalizeAsync(CloudSyncCacheState cache, RemoteCommit commit, string commitPath, IReadOnlyCollection<string> closure, IReadOnlyCollection<string> removed, CancellationToken ct)
    {
        cache.PendingDeleteObjects.UnionWith(removed);
        cache.PendingDeleteObjects.ExceptWith(closure);
        if (cache.PublishedCommitPath is { } old && old != commitPath) cache.PendingDeleteCommits.Add(old);
        cache.PendingDeleteCommits.Remove(commitPath);
        cache.Latest = commit; cache.LatestPath = commitPath; cache.LatestKnown = true;
        cache.PublishedClosure.Clear(); cache.ClosureGeneration = commit.Generation; cache.PublishedCommitPath = commitPath; cache.Publication = null;
        // No deletion occurs unless this exact intent and current closure are durable.
        await repository.SaveCacheAsync(cache, ct);
    }
    /// <summary>Only an explicit user request calls this method. Normal sync never deletes remote objects.</summary>
    public async Task<CloudCleanupResult> CleanupAsync(ICloudVolume volume, CloudBinding binding,
        IProgress<SyncLogEntry>? log, CancellationToken ct)
    {
        int objectSize = VolumeObjectSize(volume);
        var scope=repository.Scope(binding,volume.Id);await using var lease=await repository.Cache.AcquireAsync(scope,ct);
        var cache=await repository.LoadCacheAsync(scope,ct); ValidateCachedGeometry(cache, objectSize); await repository.EnsureWriterAsync(binding,volume.Id,cache,ct);
        var status=Unwrap(await volume.ControlAsync(new {cmd="cloud.status"},ct),"status");
        ValidateNativeGeometry(status, objectSize);
        await ReconcileAsync(volume,binding,cache,status,log,ct);
        if (!NoJob(status)||cache.Publication is not null) throw new IOException("请先完成当前固定版本的同步，再清理云端旧块。");
        if (cache.Latest is null||!MatchesPublished(status,cache.Latest)) throw new IOException("尚未确认当前云端完整版本，不能清理。");
        repository.AuthenticateCommit(binding.RemoteRoot, cache.Latest);
        Log(log,"cleanup.started","开始手动清理已确认不再使用的云端对象");
        bool pending=await TryCleanupAsync(volume,binding,cache,log,ct);
        Log(log,pending?"cleanup.deferred":"cleanup.completed",pending?"部分对象尚不能清理，清单已保留，可稍后手动继续":"手动清理已完成");
        return new(pending,cache.PendingDeleteObjects.Count,cache.PendingDeleteCommits.Count,(long)cache.PendingDeleteObjects.Count*objectSize);
    }

    private async Task<bool> TryCleanupAsync(ICloudVolume volume, CloudBinding binding, CloudSyncCacheState cache, IProgress<SyncLogEntry>? log, CancellationToken ct)
    {
        if (!cache.CleanupPending) return false;
        try
        {
            // .NET 8 HashSet removal does not invalidate its enumerator. Advance each
            // candidate once while deleting confirmed entries, without cloning/sorting
            // the complete pending catalog or repeatedly selecting a protected prefix.
            using var objectCandidates = cache.PendingDeleteObjects.Where(id => id != cache.Latest?.RootObjectId && cache.Publication?.Closure.Contains(id) != true).GetEnumerator();
            using var commitCandidates = cache.PendingDeleteCommits.Where(path => path != cache.LatestPath).GetEnumerator();
            string? verifiedOwnPin = null; int retained = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var candidates = new List<string>(64); var commitBatch = new List<string>(64);
                while (candidates.Count < 64 && objectCandidates.MoveNext()) candidates.Add(objectCandidates.Current);
                while (candidates.Count + commitBatch.Count < 64 && commitCandidates.MoveNext()) commitBatch.Add(commitCandidates.Current);
                if (candidates.Count + commitBatch.Count == 0) break;
                var protection = await volume.ControlAsync(new { cmd = "cloud.gc_candidates", object_ids = candidates.ToArray() }, ct);
                var decision = ValidateCleanupProtection(protection, candidates, binding, volume.Id);
                retained += candidates.Count - decision.Allowed.Length;
                var objects = decision.Allowed; var commits = commitBatch.ToArray();
                var paths = objects.Select(id => CloudRepository.ObjectPath(cache.Scope.RemoteRoot, id)).Concat(commits).ToArray();
                if (paths.Length == 0) continue;
                if (decision.OwnPin is not null && decision.OwnPin != verifiedOwnPin)
                {
                    if (!await repository.VerifyCachePinAsync(binding, volume.Id, decision.OwnPin, ct))
                    { Log(log, "cleanup.deferred", "本地按需缓存的云端保护引用尚未确认，保留待清理对象"); return true; }
                    verifiedOwnPin = decision.OwnPin;
                }
                // Never cache reader presence. A reader which starts after this
                // fresh check must recheck latest after pinning; it either uses
                // our protected current closure, or aborts before reading an old one.
                if (await repository.HasReadersOtherThanAsync(cache.Scope.RemoteRoot, decision.OwnPin, ct)) { Log(log, "cleanup.deferred", "云端仍有其他恢复读取引用，暂不删除旧对象"); return true; }
                if (repository.Store is ICloudBatchDeleteStore batching) await batching.DeleteManyAsync(paths, ct);
                else foreach (string path in paths) await repository.Store.DeleteAsync(path, ct);
                foreach (string id in objects) { cache.PendingDeleteObjects.Remove(id); Log(log, "cleanup.deleted", "已确认删除旧对象", item: new(id, "obsolete", volume.ObjectSizeBytes, "", false), generation: cache.Latest?.Generation); }
                foreach (string path in commits) { cache.PendingDeleteCommits.Remove(path); Log(log, "cleanup.deleted", "已确认删除旧版本描述", generation: cache.Latest?.Generation); }
                if (repository.Cache is ICloudDeleteJournal journal)
                    await journal.ConfirmDeletionsAsync(cache.Scope, cache, objects, commits, ct);
                else await repository.SaveCacheAsync(cache, ct);
            }
            // Fold receipts once at completion, rather than rewrite the full
            // object catalog for each small confirmed deletion batch.
            if (repository.Cache is ICloudDeleteJournal) await repository.SaveCacheAsync(cache, ct);
            if (retained != 0) Log(log, "cleanup.deferred", $"保留 {retained} 个仍被本地数据、快照或按需来源引用的对象");
            return cache.CleanupPending;
        }
        catch (OperationCanceledException) { return true; }
        catch (IOException error) { Log(log, "cleanup.pending", "保留待清理计划：" + error.Message, "warning"); return true; }
    }

    private static (string[] Allowed, string? OwnPin) ValidateCleanupProtection(JsonElement result, IReadOnlyCollection<string> candidates, CloudBinding binding, string volumeId)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("allowed", out var allowed) || allowed.ValueKind != JsonValueKind.Array ||
            !result.TryGetProperty("protected", out var protectedIds) || protectedIds.ValueKind != JsonValueKind.Array ||
            !result.TryGetProperty("cache_pin", out var pin)) throw new IOException("磁盘未返回完整的云端清理保护信息。");
        var expected = candidates.ToHashSet(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        var removable = new List<string>();
        foreach (var (items, isAllowed) in new[] { (allowed, true), (protectedIds, false) })
            foreach (var value in items.EnumerateArray())
            {
                string id = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
                if (!expected.Contains(id) || !seen.Add(id)) throw new IOException("磁盘返回了重复或范围外的云端清理对象。");
                if (isAllowed) removable.Add(id);
            }
        if (seen.Count != expected.Count) throw new IOException("磁盘遗漏了待清理对象的保护判定。");
        string? own = null;
        if (pin.ValueKind != JsonValueKind.Null)
        {
            if (pin.ValueKind != JsonValueKind.Object || GetText(pin, "backend_id") != binding.ProviderId || GetText(pin, "account_id") != binding.AccountId ||
                GetText(pin, "remote_root") != binding.RemoteRoot || GetText(pin, "device_id") != binding.DeviceId || GetText(pin, "volume_id") != volumeId ||
                GetText(pin, "reader_pin") != CloudRepository.CachePinPath(binding.RemoteRoot, volumeId))
                throw new IOException("磁盘的本地缓存保护引用与当前清理账户或磁盘不一致。");
            own = GetText(pin, "reader_pin");
        }
        return (removable.ToArray(), own);
    }
    private static async Task<HashSet<string>> DeltaAsync(ICloudVolume volume, string jobId, string side, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal); long cursor = 0;
        while (true)
        {
            var page = await volume.ControlAsync(new { cmd = "cloud.delta", job_id = jobId, side, cursor, limit = 128 }, ct);
            foreach (var item in page.GetProperty("items").EnumerateArray()) ids.Add(CloudRepository.Component(item.GetProperty("id").GetString()!));
            if (!page.TryGetProperty("next_cursor", out var next) || next.ValueKind == JsonValueKind.Null) break;
            long following = next.GetInt64(); if (following <= cursor) throw new IOException("云增量游标未前进。"); cursor = following;
        }
        return ids;
    }
    private async Task<CloudObjectInfo> UploadObjectAsync(ICloudVolume volume, CloudBinding binding, string jobId, ExportObject obj, CancellationToken ct,
        IProgress<SyncLogEntry>? log, ulong generation)
    {
        var descriptor = repository.ObjectDescriptor(binding.RemoteRoot, obj.Id, checked((int)obj.Length), obj.Sha256);
        if (repository.Store is ICloudEncodedObjectStore prepared)
        {
            var known = await prepared.TryGetEncodedReceiptAsync(descriptor, ct);
            if (known is not null)
            {
                Log(log, "upload.receipt_hit", "命中已确认回执，无需读取本地对象", item: obj, generation: generation);
                return known with { ReusedExisting = true };
            }
            Log(log, "upload.read_started", "等待单路后台读取并校验待上传对象", item: obj, generation: generation);
            using var upload = await PrepareUploadAsync(volume, jobId, obj, descriptor, ct);
            Log(log, "upload.read_completed", "对象读取和校验完成，等待网盘上传响应", item: obj, generation: generation);
            return await prepared.PutEncodedAsync(upload, ct);
        }
        // Other providers retain the independently verified generic interface.
        Log(log, "upload.read_started", "等待单路后台读取并校验待上传对象", item: obj, generation: generation);
        using var verified = await PrepareUploadAsync(volume, jobId, obj, descriptor, ct);
        Log(log, "upload.read_completed", "对象读取和校验完成，等待存储后端上传响应", item: obj, generation: generation);
        if (repository.Store is ICloudPreparedUploadStore wireStore) return await wireStore.PutPreparedAsync(verified.Wire, ct);
        using var stream = verified.Wire.OpenRead();
        return await repository.Store.PutImmutableAsync(descriptor.Path, stream, verified.Wire.Length, verified.Wire.Sha256, ct);
    }
    private async Task<PreparedObjectUpload> PrepareUploadAsync(ICloudVolume volume, string jobId, ExportObject obj, CanonicalObjectDescriptor descriptor, CancellationToken ct)
    {
        // Network concurrency does not multiply background disk reads or hashing.
        await payloadReader.WaitAsync(ct);
        try
        {
            byte[] bytes = await volume.ReadObjectAsync(jobId, obj.Id, ct);
            try
            {
                using var content = new MemoryStream(bytes, false);
                return await PreparedObjectUpload.CreateAsync(descriptor, content, repository.EncryptionContext(descriptor.Path[..descriptor.Path.IndexOf("/objects/", StringComparison.Ordinal)]), ct);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { payloadReader.Release(); }
    }
    private sealed class ReceiptBatcher(ICloudVolume volume, string jobId)
    {
        private readonly object gate = new();
        private readonly List<(object Record, TaskCompletionSource Ready)> pending = [];
        private bool running;
        private Exception? failure;
        internal Task ConfirmAsync(object record)
        {
            lock (gate)
            {
                if (failure is not null) return Task.FromException(failure);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add((record, ready));
                if (!running) { running = true; _ = PumpAsync(); }
                return ready.Task;
            }
        }
        private async Task PumpAsync()
        {
            // Coalesce simultaneous network completions without waiting for the slowest upload.
            while (true)
            {
                await Task.Delay(10);
                (object Record, TaskCompletionSource Ready)[] batch;
                lock (gate) { batch = pending.Take(128).ToArray(); pending.RemoveRange(0, batch.Length); }
                try
                {
                    await volume.ControlAsync(new { cmd = "cloud.receipt", job_id = jobId, records = batch.Select(item => item.Record).ToArray() }, CancellationToken.None);
                    foreach (var item in batch) item.Ready.TrySetResult();
                }
                catch (Exception error)
                {
                    lock (gate)
                    {
                        failure = error;
                        foreach (var item in batch.Concat(pending)) item.Ready.TrySetException(error);
                        pending.Clear(); running = false;
                    }
                    return;
                }
                lock (gate) { if (pending.Count == 0) { running = false; return; } }
            }
        }
    }
    private static bool NoJob(JsonElement status) => !status.TryGetProperty("job", out var job) || job.ValueKind == JsonValueKind.Null;
    internal static TransferProgress PreparationProgress(JsonElement job)
    {
        string stage = GetText(job, "prepare_stage");
        long pages = Math.Max(0, GetLong(job, "changed_pages")), processed = Math.Clamp(GetLong(job, "processed_pages"), 0, pages);
        // Preparation is local work. Do not present logical page bytes as upload
        // bytes: the final number of immutable objects is known only when ready.
        string message = stage switch
        {
            "sealing" => $"正在封口活动对象（{Math.Max(0, GetLong(job, "sealed_objects"))}/{Math.Max(0, GetLong(job, "total_tail_objects"))}）",
            "indexing" when pages > 0 => $"正在整理增量索引（已处理 {processed:N0}/{pages:N0} 个变化页）",
            "indexing" => "正在整理版本索引",
            "root" => "正在生成本轮版本描述",
            _ => $"正在准备本轮版本（已处理 {processed:N0}/{pages:N0} 个变化页）"
        };
        return new("preparing", message, 0, GetLong(job, "estimated_bytes"), GetLong(job, "estimated_bytes"), true);
    }
    private static bool Dirty(JsonElement status) => status.TryGetProperty("local_dirty", out var dirty) && dirty.ValueKind == JsonValueKind.True;
    private static bool Pending(JsonElement status) => Dirty(status) || GetLong(status, "data_generation") > GetLong(status, "published_generation");
    private static int VolumeObjectSize(ICloudVolume volume) => CloudObjectGeometry.IsSupported(volume.ObjectSizeBytes) ? volume.ObjectSizeBytes : throw new IOException("磁盘对象大小必须是 4、8 或 16 MiB。");
    private static bool NativeGeometryMatches(JsonElement value, int expected) => value.TryGetProperty("object_size", out var size)
        ? size.ValueKind == JsonValueKind.Number && size.TryGetInt32(out int actual) && actual == expected : false;
    private static void ValidateNativeGeometry(JsonElement value, int expected)
    { if (!NativeGeometryMatches(value, expected)) throw new IOException("磁盘返回的对象大小与已认证的卷配置不一致。"); }
    private static void ValidateCachedGeometry(CloudSyncCacheState cache, int expected)
    { if (cache.Latest is { } latest && latest.ObjectSizeBytes != expected || cache.Publication is { } intent && intent.Commit.ObjectSizeBytes != expected) throw new IOException("已确认云端版本的对象大小与本地卷不一致。"); }
    private static void ValidateObject(ExportObject obj, int expected) { CloudRepository.Component(obj.Id); if (obj.Length != expected || obj.Sha256.Length != 64 || !obj.Sha256.All(Uri.IsHexDigit)) throw new IOException("逻辑对象长度或校验值与卷配置不符。"); }
    private static void VerifyObject(byte[] bytes, ExportObject obj) { if (bytes.Length != obj.Length || !CloudRepository.Hash(bytes).Equals(obj.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("本地导出对象校验失败。"); }
    private static JsonElement Unwrap(JsonElement value, string key) => value.TryGetProperty(key, out var nested) ? nested : value;
    public static ExportObject ParseObject(JsonElement o) => new(o.GetProperty("id").GetString()!, GetText(o, "kind"), GetLong(o, "length"), o.GetProperty("sha256").GetString()!, o.TryGetProperty("uploaded", out var uploaded) && uploaded.GetBoolean());
    public static string GetText(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : "";
    public static long GetLong(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.TryGetInt64(out var n) ? n : 0;
    private static string ShortReceipt(CloudObjectInfo value) => value.RemoteId is { Length: <= 256 } ? value.RemoteId : value.Path.Length <= 500 ? value.Path : "verified";
}
