using System.Collections.Concurrent;
using System.Text.Json;
using OverlayDisk.Cloud.Sync;

namespace OverlayDisk.Services;

public sealed partial class ApplicationService
{
    private sealed class ReplicaRun
    {
        public required JobRun Job;
        public Task<object>? Preparation;
        public ReplicaCandidateRecord? Candidate;
        public volatile bool Active = true;
    }
    private readonly ConcurrentDictionary<string, ReplicaRun> replicaRuns = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> replicaActionGates = new();
    private const string ReplicaVerification = "已加载对象已校验；未访问的索引和内容按需验证";

    private RestoreRecord ReplicaSource(JsonElement disk)
    {
        string id = Text(disk, "id");
        string rootId = disk.TryGetProperty("replica", out var native) && native.ValueKind == JsonValueKind.Object ? Text(native, "root_object_id") : "";
        lock (gate)
            return settings.Restores.Values.Where(r => r.Lazy && r.Complete && !r.PreparedReplicaOnly && !r.ContainerDeleted && r.LocalDiskId == id
                    && (rootId.Length == 0 || r.Commit.RootObjectId == rootId))
                .OrderByDescending(r => r.Commit.Generation).FirstOrDefault()
                ?? throw new IOException("这块磁盘没有保存可更新的云端来源，请先解锁磁盘并刷新状态。");
    }
    private object ReplicaView(JsonElement disk)
    {
        string id = Text(disk, "id");
        var native = disk.TryGetProperty("replica", out var value) && value.ValueKind == JsonValueKind.Object ? value : Element(new { });
        RestoreRecord? source;
        lock (gate) source = settings.Restores.Values.Where(r => r.Lazy && r.Complete && !r.PreparedReplicaOnly && !r.ContainerDeleted && r.LocalDiskId == id).OrderByDescending(r => r.Commit.Generation).FirstOrDefault();
        replicaRuns.TryGetValue(id, out var run);
        bool candidate = native.TryGetProperty("candidate", out var pending) && pending.ValueKind == JsonValueKind.Object;
        return new
        {
            available = Flag(native, "enabled") || source != null,
            sourceGeneration = native.TryGetProperty("generation", out var generation) ? generation.GetUInt64() : source?.Commit.Generation ?? 0,
            hasLocalChanges = Flag(native, "local_changes"), indexComplete = Flag(native, "index_complete"),
            identityReady = !Flag(native, "enabled") || Flag(native, "identity_ready"),
            state = run?.Job.Progress.Phase ?? (candidate ? "ready" : "idle"),
            message = run?.Job.Progress.Message ?? (candidate ? "上次准备的云端快照尚未应用，可重新检查或取消" : "手动检查云端最新快照"),
            verifiedScope = ReplicaVerification,
            candidate = candidate ? (object)pending.Clone() : null
        };
    }
    private async Task<object> ReplicaStatusAsync(string id, CancellationToken ct)
    {
        await RefreshWorkerAsync(ct);
        var disk = Disk(id);
        var result = Element(ReplicaView(disk)).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        if (disk.TryGetProperty("replica", out var native) && native.ValueKind == JsonValueKind.Object)
        {
            bool ready = native.TryGetProperty("candidate", out var candidate) && candidate.ValueKind == JsonValueKind.Object;
            result["token"] = ready ? Text(candidate, "token") : "";
            result["expectedRevision"] = (ready ? UInt(candidate, "expected_revision") : UInt(native, "local_revision")).ToString(System.Globalization.CultureInfo.InvariantCulture);
            result["generation"] = ready ? UInt(candidate, "generation") : UInt(native, "generation");
            result["localChanges"] = Flag(native, "local_changes");
        }
        return result;
    }
    private async Task<object> PrepareReplicaAsync(string id, CancellationToken ct)
    {
        var serial = replicaActionGates.GetOrAdd(id, _ => new(1, 1)); await serial.WaitAsync(ct);
        Task<object> preparation;
        try { preparation = StartReplicaPreparation(id, ct); }
        finally { serial.Release(); }
        return await preparation;
    }
    private Task<object> StartReplicaPreparation(string id, CancellationToken ct)
    {
        lock (jobGate)
        {
            RequireRunning();
            if (!networkAdmissionsOpen || deletingDisks.Contains(id)) throw new IOException("网络连接或磁盘正在关闭，请稍后再试。");
            if (replicaRuns.TryGetValue(id, out var existing) && existing.Active && existing.Preparation != null) return existing.Preparation;
            if (runs.TryGetValue(id, out var upload) && upload.Task is { IsCompleted: false }) throw new IOException("这块磁盘正在上传，请先暂停并处理已有上传任务。");
            var job = new JobRun { DiskId = id, Kind = "replica", Title = "加载云端最新快照", Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token),
                VerifiedScope = ReplicaVerification, Progress = new("checking", "正在检查云端最新版本", 0, 0, 0) };
            var run = new ReplicaRun { Job = job };
            replicaRuns[id] = run; runs["replica:" + id] = job;
            run.Preparation = Task.Run(() => PrepareReplicaCoreAsync(id, run)); job.Task = run.Preparation;
            Changed(); return run.Preparation;
        }
    }
    private async Task<object> PrepareReplicaCoreAsync(string id, ReplicaRun run)
    {
        var ct = run.Job.Cancellation.Token;
        bool reserved = false;
        try
        {
            await initialization.WaitAsync(ct);
            await RefreshWorkerAsync(ct); var disk = Disk(id);
            WorkerApplicationService.RequireReplicaUnmounted(Flag(disk, "mounted"));
            if (!Flag(disk, "unlocked")) throw new IOException("请先解锁磁盘，再加载云端最新快照。");
            RecoverLazySource(disk);
            var source = ReplicaSource(disk);
            if (source.AccountId != account?.AccountId) throw new IOException("请登录这块磁盘来源对应的百度网盘账户。");
            var repository = Repository();
            // Do not cancel the RPC wait after sending a reservation or native stage:
            // otherwise a late worker reply could leave an unobserved candidate.
            ct.ThrowIfCancellationRequested();
            var status = Element(await worker.InvokeAsync("replica.prepare", Element(new { id }), CancellationToken.None)); reserved = true;
            ct.ThrowIfCancellationRequested();
            if (status.TryGetProperty("candidate", out var oldCandidate) && oldCandidate.ValueKind == JsonValueKind.Object)
            {
                // A process restart can preserve a candidate. Explicitly checking again
                // cancels that candidate before selecting the current remote version.
                await worker.InvokeAsync("replica.cancel", Element(new { id }), CancellationToken.None);
                status = Element(await worker.InvokeAsync("replica.prepare", Element(new { id }), CancellationToken.None));
            }
            Log(id, run.Job.Id, "replica", "version.check", "检查原来源的最新提交；不扫描对象目录");
            var latest = await repository.LatestForReplicaAsync(source.RemoteRoot, source.VerifiedCommit ? source.Commit : null, ct)
                ?? throw new IOException("云端没有完整可加载版本。");
            UnlockCloudCommit(repository, source.AccountId, source.RemoteRoot, latest.Commit, null);
            bool sameRoot = Text(status, "root_object_id") == latest.Commit.RootObjectId
                && Text(status, "root_sha256").Equals(latest.Commit.RootSha256, StringComparison.OrdinalIgnoreCase);
            if (sameRoot && !source.VerifiedCommit)
            {
                lock (gate) { source.Commit = latest.Commit; source.VerifiedCommit = true; }
                Save();
            }
            if (sameRoot && !Flag(status, "local_changes"))
            {
                await worker.InvokeAsync("replica.cancel", Element(new { id }), CancellationToken.None); reserved = false;
                run.Active = false; run.Job.ReadyToMount = true; run.Job.Progress = new("complete", "已是云端最新快照，未下载任何对象", 0, 0, 0);
                Log(id, run.Job.Id, "replica", "unchanged", run.Job.Progress.Message, generation: latest.Commit.Generation);
                return new { unchanged = true, localChanges = false, generation = latest.Commit.Generation,
                    expectedRevision = UInt(status, "local_revision").ToString(System.Globalization.CultureInfo.InvariantCulture), token = "" };
            }
            string pin; JsonElement staged; long processed = 0;
            if (sameRoot)
            {
                pin = source.ReaderPin ?? throw new IOException("云端来源缺少持久读取保护。");
                staged = Element(await worker.InvokeAsync("replica.stageCurrent", Element(new { id, commit = latest.Commit }), CancellationToken.None));
                run.Job.Progress = run.Job.Progress with { ReusedBytes = latest.Commit.ObjectSizeBytes };
            }
            else
            {
                ReplicaCandidateRecord? resumable;
                lock (gate) resumable = settings.ReplicaCandidates.GetValueOrDefault(id);
                string prefix = source.RemoteRoot + "/readers/";
                bool resumePin = resumable != null && resumable.Commit == latest.Commit
                    && resumable.ReaderPin.StartsWith(prefix, StringComparison.Ordinal) && resumable.ReaderPin.EndsWith(".json", StringComparison.Ordinal)
                    && Guid.TryParse(resumable.ReaderPin[prefix.Length..^5], out _);
                string readerId = resumePin ? resumable!.ReaderPin[prefix.Length..^5] : Guid.NewGuid().ToString();
                string plannedPin = prefix + readerId + ".json";
                bool previouslyConfirmed = resumePin && resumable!.PinConfirmed;
                lock (gate)
                {
                    // Record intent before creating a remote reference, so interruption
                    // at any point cannot strand a pin outside the manual cleanup ledger.
                    RememberReplicaPin(settings, source, id, latest.Commit, plannedPin);
                    settings.ReplicaCandidates[id] = new() { Commit = latest.Commit, ReaderPin = plannedPin, PinConfirmed = previouslyConfirmed };
                }
                Save();
                pin = await repository.EnsureReplicaReaderAsync(source.RemoteRoot, latest.Commit, readerId, previouslyConfirmed, ct);
                if (pin != plannedPin) throw new IOException("云端读取引用与已保存的准备记录不一致。");
                lock (gate) settings.ReplicaCandidates[id].PinConfirmed = true;
                Save();
                object? publication = null;
                if (source.Mode == "original")
                {
                    var binding = source.OriginalBinding ?? throw new IOException("原硬盘缺少已保存的写入身份。");
                    publication = new { commit = latest.Commit, binding = new { backend_id = binding.ProviderId, account_id = binding.AccountId,
                        remote_root = binding.RemoteRoot, device_id = binding.DeviceId, enabled = true } };
                }
                var stageArgs = Element(new { id, options = new { commit = latest.Commit, publication,
                    backing = new { provider_id = repository.Store.ProviderId, account_id = source.AccountId, source_volume_id = latest.Commit.VolumeId,
                        remote_root = source.RemoteRoot, root_object_id = latest.Commit.RootObjectId, root_sha256 = latest.Commit.RootSha256, reader_pin = pin } } });
                ct.ThrowIfCancellationRequested();
                staged = Element(await worker.InvokeAsync("replica.stageCached", stageArgs, CancellationToken.None));
                if (!Flag(staged, "cached"))
                {
                    run.Job.Progress = new("downloading", "正在下载并校验新快照的必要对象", 0, latest.Commit.ObjectSizeBytes, 0); Changed();
                    Log(id, run.Job.Id, "replica", "root.download", run.Job.Progress.Message, objectId: latest.Commit.RootObjectId);
                    var downloaded = await repository.ReadObjectForReplicaAsync(source.RemoteRoot, latest.Commit.RootObjectId, latest.Commit.RootSha256, latest.Commit.ObjectSizeBytes, ct);
                    byte[] root = downloaded.Canonical; Interlocked.Add(ref run.Job.DownloadedBytes, downloaded.WireBytes); processed = root.Length;
                    ct.ThrowIfCancellationRequested();
                    staged = Element(await worker.StageReplicaAsync(stageArgs, root, CancellationToken.None));
                }
                else run.Job.Progress = run.Job.Progress with { ReusedBytes = latest.Commit.ObjectSizeBytes };
            }
            ct.ThrowIfCancellationRequested();
            var candidate = staged.GetProperty("candidate");
            run.Candidate = new() { Token = Text(candidate, "token"), ExpectedRevision = UInt(candidate, "expected_revision"), Commit = latest.Commit, ReaderPin = pin, PinConfirmed = true };
            lock (gate) settings.ReplicaCandidates[id] = run.Candidate;
            Save();
            run.Job.Progress = new("ready", Flag(staged, "local_changes") ? "快照已准备好，等待确认丢弃本地修改" : "快照已准备好，等待切换", processed, processed, 0) { ReusedBytes = run.Job.Progress.ReusedBytes };
            Log(id, run.Job.Id, "replica", "prepared", run.Job.Progress.Message, generation: latest.Commit.Generation);
            return new { token = run.Candidate.Token, expectedRevision = run.Candidate.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), generation = latest.Commit.Generation,
                localChanges = Flag(staged, "local_changes"), unchanged = false };
        }
        catch (Exception error)
        {
            bool cancelled = error is OperationCanceledException || ct.IsCancellationRequested;
            if (reserved && worker.IsConnected)
                try { await worker.InvokeAsync("replica.cancel", Element(new { id }), CancellationToken.None); } catch (IOException) { }
            run.Active = false;
            run.Job.Progress = new(cancelled ? "cancelled" : "error", cancelled ? "已取消加载，当前快照保持不变" : Friendly(error), 0, 0, 0,
                Error: cancelled ? null : Friendly(error));
            Log(id, run.Job.Id, "replica", cancelled ? "prepare.cancelled" : "prepare.failed", run.Job.Progress.Message, cancelled ? "info" : "error");
            if (cancelled && error is not OperationCanceledException) throw new OperationCanceledException(run.Job.Progress.Message, error, ct);
            throw;
        }
        finally
        {
            try { await RefreshWorkerAsync(CancellationToken.None); } catch (IOException error) { notice = Friendly(error); }
            Changed();
        }
    }
    internal static void RememberReplicaPin(AppSettings settings, RestoreRecord source, string localDiskId, RemoteCommit commit, string pin)
    {
        string prefix = source.RemoteRoot + "/readers/";
        if (!pin.StartsWith(prefix, StringComparison.Ordinal) || !pin.EndsWith(".json", StringComparison.Ordinal)
            || !Guid.TryParse(pin[prefix.Length..^5], out _)) throw new IOException("云端读取引用与来源不一致。");
        string recordId = pin[prefix.Length..^5];
        if (settings.Restores.TryGetValue(recordId, out var existing))
        {
            if (existing.LocalDiskId != localDiskId || existing.AccountId != source.AccountId || existing.RemoteRoot != source.RemoteRoot
                || existing.Commit != commit || existing.ReaderPin != pin || existing.ContainerDeleted)
                throw new IOException("云端读取引用与本地记录冲突。");
            return;
        }
        settings.Restores[recordId] = new RestoreRecord { Id = recordId, LocalDiskId = localDiskId, AccountId = source.AccountId, RemoteRoot = source.RemoteRoot,
            TargetPath = source.TargetPath, Name = source.Name, ReaderPin = pin, Commit = commit, VerifiedCommit = true,
            Lazy = true, Begun = true, Complete = true, PreparedReplicaOnly = true, Mode = source.Mode,
            OriginalConfirmed = source.OriginalConfirmed, OriginalBinding = source.OriginalBinding };
    }
    private async Task<object> ApplyReplicaAsync(string id, JsonElement args, CancellationToken ct)
    {
        var serial = replicaActionGates.GetOrAdd(id, _ => new(1, 1)); await serial.WaitAsync(ct);
        try
        {
            RequireRunning();
            if (!replicaRuns.TryGetValue(id, out var run) || !run.Active || run.Candidate is not { } candidate)
                throw new IOException("请先检查最新快照，并确认本次加载。");
            if (candidate.Token != Text(args, "token") || candidate.ExpectedRevision != WorkerApplicationService.ReplicaExpectedRevision(args)) throw new IOException("确认已过期，请重新检查云端快照。");
            await RefreshWorkerAsync(ct); var disk = Disk(id); WorkerApplicationService.RequireReplicaUnmounted(Flag(disk, "mounted"));
            var source = ReplicaSource(disk);
            run.Job.Progress = run.Job.Progress with { Phase = "switching", Message = "正在原子切换快照；完成后请手动挂载" }; Changed();
            ct.ThrowIfCancellationRequested();
            var applied = Element(await worker.InvokeAsync("replica.apply", args, CancellationToken.None));
            string identityError = Text(applied, "identityError");
            run.Active = false;
            // Native publication is already durable. GUI checkpoint failure must never
            // claim the old root is still current; native backing repairs it on next unlock.
            try
            {
                string prefix = source.RemoteRoot + "/readers/", recordId = candidate.ReaderPin[prefix.Length..^5];
                lock (gate)
                {
                    settings.Restores[recordId] = new RestoreRecord { Id = recordId, LocalDiskId = id, AccountId = source.AccountId, RemoteRoot = source.RemoteRoot,
                        TargetPath = source.TargetPath, Name = source.Name, ReaderPin = candidate.ReaderPin, Commit = candidate.Commit, VerifiedCommit = true,
                        Lazy = true, Begun = true, Complete = true, Mode = source.Mode, OriginalConfirmed = source.OriginalConfirmed, OriginalBinding = source.OriginalBinding };
                    settings.ReplicaCandidates.Remove(id);
                    if (source.Mode == "original") { settings.LastSuccess[id] = candidate.Commit.UpdatedUtc; settings.PendingDisks.Remove(id); }
                }
                Save();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { notice = "快照已加载，界面记录尚未保存；下次解锁时将从容器恢复来源。"; Log(id, run.Job.Id, "replica", "checkpoint.warning", error.Message, "warning"); }
            run.Job.Progress = run.Job.Progress with { Phase = "complete", Message = identityError.Length == 0
                ? "云端快照已加载，请手动挂载；未访问内容按需下载" : "快照已加载，磁盘身份准备尚未完成；挂载时将重试：" + identityError, Error = null };
            run.Job.ReadyToMount = identityError.Length == 0;
            if (identityError.Length != 0) notice = run.Job.Progress.Message;
            Log(id, run.Job.Id, "replica", "applied", run.Job.Progress.Message, generation: candidate.Commit.Generation);
            try { await RefreshWorkerAsync(CancellationToken.None); }
            catch (IOException error) { notice = "快照已加载，状态暂时无法刷新：" + Friendly(error); }
            Changed(); return new { ok = true, generation = candidate.Commit.Generation,
                identityReady = identityError.Length == 0, identityError = identityError.Length == 0 ? null : identityError };
        }
        catch (Exception error)
        {
            if (replicaRuns.TryGetValue(id, out var run) && run.Active)
            {
                bool switched = false;
                if (worker.IsConnected && run.Candidate is { } attempted)
                {
                    try
                    {
                        var actual = Element(await worker.InvokeAsync("replica.status", Element(new { id }), CancellationToken.None));
                        switched = Text(actual, "root_object_id") == attempted.Commit.RootObjectId
                            && Text(actual, "root_sha256").Equals(attempted.Commit.RootSha256, StringComparison.OrdinalIgnoreCase)
                            && (!actual.TryGetProperty("candidate", out var pending) || pending.ValueKind != JsonValueKind.Object)
                            && !Flag(actual, "local_changes");
                        if (switched)
                        {
                            // Partition identity hydration can fail after the durable root
                            // switch. Unlock/mount will repair it before exposing a device.
                            await worker.InvokeAsync("replica.cancel", Element(new { id }), CancellationToken.None);
                            await RefreshWorkerAsync(CancellationToken.None); RecoverLazySource(Disk(id)); run.Active = false;
                        }
                    }
                    catch (IOException) { /* Keep the original failure; the next explicit check reconciles durable state. */ }
                }
                run.Job.Progress = run.Job.Progress with { Phase = switched ? "error" : "ready",
                    Message = (switched ? "快照已切换，挂载准备尚未完成；重试挂载前将继续校验磁盘身份：" : "加载尚未完成：") + Friendly(error), Error = Friendly(error) };
                Log(id, run.Job.Id, "replica", "apply.failed", run.Job.Progress.Message, "error"); Changed();
            }
            throw;
        }
        finally { serial.Release(); }
    }
    private async Task<object> CancelReplicaAsync(string id, JsonElement args, CancellationToken ct)
    {
        string suppliedToken = Text(args, "token");
        replicaRuns.TryGetValue(id, out var preparing);
        if (suppliedToken.Length != 0 && preparing is { Active: true } && preparing.Candidate?.Token != suppliedToken)
            return new { ok = true, stale = true };
        if (preparing != null)
        {
            preparing.Job.Cancellation.Cancel();
            if (preparing.Preparation != null) try { await preparing.Preparation; } catch (Exception) { }
        }
        var serial = replicaActionGates.GetOrAdd(id, _ => new(1, 1)); await serial.WaitAsync(ct);
        try
        {
            if (replicaRuns.TryGetValue(id, out var current) && !ReferenceEquals(current, preparing))
                return new { ok = true, stale = true };
            if (worker.IsConnected) await worker.InvokeAsync("replica.cancel", args, CancellationToken.None);
            lock (gate)
                if (settings.ReplicaCandidates.TryGetValue(id, out var cached)) { cached.Token = ""; cached.ExpectedRevision = 0; }
            Save();
            if (replicaRuns.TryGetValue(id, out var run))
            {
                run.Active = false;
                if (run.Job.Progress.Phase != "complete") run.Job.Progress = run.Job.Progress with { Phase = "cancelled", Message = "已取消加载，当前快照保持不变", Error = null };
            }
            await RefreshWorkerAsync(CancellationToken.None); Changed(); return new { ok = true };
        }
        finally { serial.Release(); }
    }
}
