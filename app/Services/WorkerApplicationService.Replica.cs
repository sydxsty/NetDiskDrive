using System.Collections.Concurrent;
using System.Text.Json;
using OverlayDisk.Worker;

namespace OverlayDisk.Services;

public sealed partial class WorkerApplicationService
{
    // A reservation spans the network preparation and user confirmation. The per-disk
    // command gate covers each transition; status and hydration queries stay independent.
    private readonly ConcurrentDictionary<string, byte> replicaReservations = new();

    private static void EnsureReplicaIdentity(CoreDisk core)
    {
        var status = core.Control(new { cmd = "replica.status" });
        if (Text(status, "mode") != "copy" || !status.TryGetProperty("enabled", out var enabled) || !enabled.GetBoolean()
            || status.TryGetProperty("identity_ready", out var ready) && ready.GetBoolean()) return;
        var regions = DiskIdentityRewriter.Plan(core.Id, core.Capacity, (offset, length) =>
        { var bytes = new byte[length]; core.ReadForManagement(offset, bytes, length); return bytes; });
        core.SetReplicaIdentity(regions);
    }

    internal static void RequireReplicaUnmounted(bool mounted)
    {
        if (mounted) throw new IOException("请先手动安全卸载磁盘，再加载云端最新快照。加载完成后需要手动重新挂载。");
    }
    private static bool HasReplicaCandidate(JsonElement status) => status.ValueKind == JsonValueKind.Object
        && status.TryGetProperty("candidate", out var candidate) && candidate.ValueKind == JsonValueKind.Object;
    private void RequireNoReplicaChange(DiskEntry entry, bool closing = false)
    {
        if (replicaReservations.ContainsKey(entry.Id)
            || !closing && controller.TryGetCore(entry.Id) is { } core && HasReplicaCandidate(core.Control(new { cmd = "replica.status" })))
            throw new IOException("正在准备或确认云端快照，请完成加载或取消后再操作这块磁盘。");
    }
    private async Task RequireReplicaUnmountedAsync(DiskEntry entry, CancellationToken ct)
    {
        RequireReplicaUnmounted(entry.Mounted);
        await ActiveDisks.Gate.WaitAsync(ct);
        try { RequireReplicaUnmounted(ActiveDisks.Items.ContainsKey(Guid.Parse(entry.Id))); }
        finally { ActiveDisks.Gate.Release(); }
    }
    private async Task<object> ReplicaActionAsync(string method, DiskEntry entry, JsonElement args, CancellationToken ct)
    {
        var core = Core(entry);
        if (method == "replica.status") return core.Control(new { cmd = "replica.status" });
        await RequireReplicaUnmountedAsync(entry, ct);
        switch (method)
        {
            case "replica.prepare":
            {
                var uploads = cloudGates.GetOrAdd(entry.Id, _ => new(1, 1));
                await uploads.WaitAsync(ct);
                try
                {
                    if (replicaReservations.ContainsKey(entry.Id)) return core.Control(new { cmd = "replica.status" });
                    if (maintenance.Values.Any(m => m.DiskId == entry.Id && m.Task is { IsCompleted: false })
                        || localRestores.Values.Any(r => !r.Complete && (r.SourceId == entry.Id || r.TargetId == entry.Id)))
                        throw new IOException("这块磁盘还有整理或快照恢复任务，请先完成或暂停相关任务。");
                    var cloud = core.Control(new { cmd = "cloud.status" });
                    if (cloud.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.Object && Text(job, "phase") != "published")
                        throw new IOException("这块磁盘还有未完成的上传版本，请先完成或放弃该版本，再加载云端快照。");
                    var status = core.Control(new { cmd = "replica.status" });
                    if (!status.TryGetProperty("enabled", out var enabled) || !enabled.GetBoolean()) throw new IOException("这块磁盘没有可以更新的云端来源。");
                    replicaReservations[entry.Id] = 0;
                    await StopCacheLoopAsync(entry.Id);
                    core.CancelPrefetch();
                    core.Flush();
                    return core.Control(new { cmd = "replica.status" });
                }
                catch { replicaReservations.TryRemove(entry.Id, out _); throw; }
                finally { uploads.Release(); }
            }
            case "replica.apply":
            {
                var status = core.Control(new { cmd = "replica.status" });
                ValidateReplicaConfirmation(status, args);
                core.Control(new { cmd = "replica.apply", token = Text(args, "token"),
                    expected_revision = ReplicaExpectedRevision(args), discard_local = OptionalBool(args, "discardLocalChanges") == true });
                string? identityError = null;
                try { EnsureReplicaIdentity(core); }
                catch (IOException error) { identityError = error.Message; }
                replicaReservations.TryRemove(entry.Id, out _);
                StartCacheLoop(core);
                return new { applied = true, identityReady = identityError == null, identityError, status = core.Control(new { cmd = "replica.status" }) };
            }
            case "replica.cancel":
            {
                var status = core.Control(new { cmd = "replica.status" });
                string token = Text(args, "token");
                if (token.Length != 0 && HasReplicaCandidate(status) && Text(status.GetProperty("candidate"), "token") != token)
                    throw new IOException("确认窗口已过期，请刷新当前加载任务。");
                var result = core.Control(new { cmd = "replica.cancel" });
                replicaReservations.TryRemove(entry.Id, out _);
                StartCacheLoop(core); return result;
            }
            case "replica.stageCurrent":
                if (!replicaReservations.ContainsKey(entry.Id)) throw new IOException("请先准备云端快照加载任务。");
                return core.Control(new { cmd = "replica.stage_current", commit = args.GetProperty("commit") });
            case "replica.stageCached":
                if (!replicaReservations.ContainsKey(entry.Id)) throw new IOException("请先准备云端快照加载任务。");
                return core.Control(new { cmd = "replica.stage_cached", options = args.GetProperty("options") });
            default: throw new IOException("不支持的副本操作。");
        }
    }
    internal static void ValidateReplicaConfirmation(JsonElement status, JsonElement args)
    {
        if (!HasReplicaCandidate(status)) throw new IOException("没有准备完成的云端快照，请重新检查最新版本。");
        var candidate = status.GetProperty("candidate");
        if (Text(args, "token").Length == 0 || Text(candidate, "token") != Text(args, "token")
            || candidate.GetProperty("expected_revision").GetUInt64() != ReplicaExpectedRevision(args)
            || status.GetProperty("local_revision").GetUInt64() != ReplicaExpectedRevision(args))
            throw new IOException("本地磁盘或待加载版本已变化，请重新检查并确认。");
        if (status.TryGetProperty("local_changes", out var dirty) && dirty.GetBoolean() && OptionalBool(args, "discardLocalChanges") != true)
            throw new IOException("加载云端快照会丢弃本地修改。请先明确确认，或取消并保留当前内容。");
    }
    internal static ulong ReplicaExpectedRevision(JsonElement args)
    {
        if (args.TryGetProperty("expectedRevision", out var revision))
        {
            if (revision.ValueKind == JsonValueKind.Number && revision.TryGetUInt64(out var number)) return number;
            if (revision.ValueKind == JsonValueKind.String && ulong.TryParse(revision.GetString(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out number)) return number;
        }
        throw new IOException("缺少有效的本地修订号，请重新检查云端快照。");
    }
    private object StageReplica(JsonElement args, ReadOnlyMemory<byte> root)
    {
        var entry = Entry(args); RequireReplicaUnmounted(entry.Mounted);
        if (!replicaReservations.ContainsKey(entry.Id)) throw new IOException("请先准备云端快照加载任务。");
        var core = Core(entry);
        return core.StageReplica(root.ToArray(), args.GetProperty("options"));
    }
}
