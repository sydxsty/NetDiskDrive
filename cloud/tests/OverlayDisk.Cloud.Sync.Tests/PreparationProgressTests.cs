using System.Text.Json;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

internal static class PreparationProgressTests
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("preparation reports sealing index pages and root separately without extra requests", Stages),
        ("unspecified preparation stage uses generic wording without inventing upload bytes", Fallback),
        ("initial and resumed preparation forward each disk cache budget and bounded work limits", CacheBudgets),
        ("preparation logs phase IO from the current job without global counters or extra requests", StageLogs),
        ("one native step may complete several phases without invented start timestamps", SkippedStages),
        ("invalid preparation budgets fail before disk or cloud requests", InvalidBudget),
        ("interrupted preparation reports only its last returned checkpoint without polling", Interrupted),
        ("foreign preparation diagnostics cannot be attributed to the current job", ForeignDiagnostics)
    ];
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Fixture
    {
        internal readonly Volume Volume;
        internal readonly Store Store = new();
        internal readonly MemoryCloudSyncCache Cache = new();
        internal readonly Progress Progress = new();
        internal readonly Logs Logs = new();
        private readonly CloudBinding binding;
        internal Fixture(bool stages, bool fresh = false, bool jump = false, bool foreignDiagnostics = false, int failAt = 0)
        {
            Volume = new(stages, fresh, jump, foreignDiagnostics, failAt);
            binding = new(Store.ProviderId, "fixture-account", CloudRepository.RootPath(Volume.Id), Guid.NewGuid().ToString());
        }
        internal async Task Run(int cacheMiB = 64)
        {
            var scope = CloudCacheScope.From(binding);
            await using (var lease = await Cache.AcquireAsync(scope))
            {
                var state = new CloudSyncCacheState { Scope = scope, OwnerConfirmed = true, LatestKnown = true };
                state.ConfirmedFolders.UnionWith([CloudRepository.BasePath, binding.RemoteRoot, binding.RemoteRoot + "/commits",
                    binding.RemoteRoot + "/objects/" + Volume.RootId[..2]]);
                await Cache.SaveAsync(scope, state);
            }
            await new SyncCoordinator(new(Store, Cache, binding.AccountId)).RunAsync(Volume, binding, "fixture", 64UL << 20, false, 2,
                Progress, default, Logs, cacheMiB);
        }
        internal void Verify(int cacheMiB, int expectedRequests = 5, int expectedProgress = 5)
        {
            Check(Volume.PrepareRequests.Count == expectedRequests && Volume.PrepareRequests.All(p =>
                p.GetProperty("max_pages").GetInt32() == 16384 && p.GetProperty("max_leaf_groups").GetInt32() == 512
                && p.GetProperty("max_objects").GetInt32() == 2 && p.GetProperty("prepare_cache_mib").GetInt32() == cacheMiB),
                "Preparation budget or work bounds were lost on an initial/resumed/continuation request");
            Check(Store.Calls.Count == 2 && Store.Calls.All(c => c.StartsWith("put:", StringComparison.Ordinal)) && Volume.Reads == 1,
                "Preparation logging caused extra cloud requests or local payload reads");
            Check(Volume.Commands.Count(c => c == "cloud.status") == 1 && !Volume.Commands.Contains("sync.diagnostics"),
                "Diagnostics introduced an extra status request or polling path");
            var preparing = Progress.Items.Where(p => p.Phase == "preparing").ToArray();
            Check(preparing.Length == expectedProgress && preparing.All(p => p.CompletedBytes == 0 && p.TotalBytes == CloudRepository.ObjectLength
                && p.PendingBytes == CloudRepository.ObjectLength && p.Estimated && p.UploadedBytes == 0 && p.ReusedBytes == 0),
                "Local processed page counts were incorrectly reported as bytes uploaded");
            Check(Progress.Items.Last().LogicalUploadedBytes == CloudRepository.ObjectLength && Progress.Items.Last().UploadedBytes is > 0 and < CloudRepository.ObjectLength,
                "Canonical and compressed upload byte accounting were confused");
        }
    }
    private static async Task Stages()
    {
        var f = new Fixture(true); await f.Run(); f.Verify(64);
        var p = f.Progress.Items.Where(x => x.Phase == "preparing").ToArray();
        Check(p[0].Message.Contains("封口活动对象（0/2）", StringComparison.Ordinal) && p[1].Message.Contains("封口活动对象（1/2）", StringComparison.Ordinal), "Tail sealing progress was hidden");
        Check(p[2].Message.Contains("增量索引", StringComparison.Ordinal) && p[2].Message.Contains(128L.ToString("N0") + "/" + 8192L.ToString("N0"), StringComparison.Ordinal), "Index page progress was hidden");
        Check(p[3].Message.Contains(4096L.ToString("N0") + "/" + 8192L.ToString("N0"), StringComparison.Ordinal) && !p[3].Message.Contains("封口", StringComparison.Ordinal), "Index packing was mislabeled as object sealing");
        Check(p[4].Message.Contains("版本描述", StringComparison.Ordinal), "Root publication preparation was mislabeled");
    }
    private static async Task Fallback()
    {
        var f = new Fixture(false); await f.Run(); f.Verify(64);
        Check(f.Progress.Items.Where(p => p.Phase == "preparing").All(p => p.Message.Contains("准备本轮版本", StringComparison.Ordinal) && !p.Message.Contains("封口", StringComparison.Ordinal)),
            "Unknown native stage was guessed as sealing");
        Check(!f.Logs.Items.Any(l => l.Action.StartsWith("preparation.", StringComparison.Ordinal)), "Missing diagnostics were invented as known preparation phases");
    }
    private static async Task CacheBudgets()
    {
        var fresh = new Fixture(true, fresh: true); await fresh.Run(16); fresh.Verify(16, 6);
        Check(!fresh.Volume.PrepareRequests[0].TryGetProperty("job_id", out _) && fresh.Volume.PrepareRequests.Skip(1).All(p =>
            p.GetProperty("job_id").GetString() == fresh.Volume.JobId), "Initial and subsequent preparation were confused");
        var resumed = new Fixture(true); await resumed.Run(1024); resumed.Verify(1024);
        Check(resumed.Volume.PrepareRequests.All(p => p.GetProperty("job_id").GetString() == resumed.Volume.JobId), "Resume froze another generation");
        Check(!resumed.Logs.Items.Any(l => l.Action.StartsWith("preparation.freeze.", StringComparison.Ordinal)), "Resume re-reported an old freeze as new work");
    }
    private static async Task StageLogs()
    {
        var f = new Fixture(true, fresh: true); await f.Run(128); f.Verify(128, 6);
        foreach (string stage in new[] { "freeze", "sealing", "indexing", "root" })
        {
            Check(f.Logs.Items.Count(l => l.Action == "preparation." + stage + ".started") == 1
                && f.Logs.Items.Count(l => l.Action == "preparation." + stage + ".completed") == 1,
                "Each observed preparation phase must have one start and completion, without progress log spam");
            var log = f.Logs.Items.Single(l => l.Action == "preparation." + stage + ".completed");
            int units = stage switch { "freeze" => 1, "sealing" => 2, "indexing" => 3, _ => 4 };
            Check(log.Generation == 1 && log.Message.Contains(f.Volume.JobId, StringComparison.Ordinal)
                && log.Message.Contains($"本地读取 {(units * 4096L):N0} B / {units:N0} 次", StringComparison.Ordinal)
                && log.Message.Contains($"本地写入 {(units * 8192L):N0} B / {(units * 2):N0} 次", StringComparison.Ordinal)
                && log.Message.Contains($"刷盘 {units:N0} 次", StringComparison.Ordinal)
                && log.Message.Contains($"耗时 {(units * 10):N0} ms", StringComparison.Ordinal)
                && log.Message.Contains("仅本任务本次进程累计，重启后重新计数", StringComparison.Ordinal),
                "Task-local phase IO or process-session scope was omitted or replaced by whole-volume counters");
        }
    }
    private static async Task SkippedStages()
    {
        var f = new Fixture(true, jump: true); await f.Run(); f.Verify(64, 1, 1);
        foreach (string stage in new[] { "indexing", "root" })
            Check(!f.Logs.Items.Any(l => l.Action == "preparation." + stage + ".started")
                && f.Logs.Items.Single(l => l.Action == "preparation." + stage + ".completed").Message.Contains("本次原生调用内完成", StringComparison.Ordinal),
                "A native step that finished multiple phases received invented start timestamps or lost phase counts");
    }
    private static async Task InvalidBudget()
    {
        foreach (int value in new[] { -1, 15, 1025, int.MaxValue })
        {
            var f = new Fixture(true); bool rejected = false;
            try { await f.Run(value); } catch (IOException) { rejected = true; }
            Check(rejected && f.Volume.Commands.Count == 0 && f.Store.Calls.Count == 0, "Invalid preparation budget reached storage or the network");
        }
    }
    private static async Task Interrupted()
    {
        var f = new Fixture(true, failAt: 3); bool rejected = false;
        try { await f.Run(); } catch (IOException) { rejected = true; }
        Check(rejected && f.Volume.PrepareRequests.Count == 3 && f.Store.Calls.Count == 0 && f.Volume.Commands.Count(c => c == "cloud.status") == 1,
            "Preparation failure caused retry polling, cloud traffic, or lost its error");
        var log = f.Logs.Items.Single(l => l.Action == "preparation.indexing.interrupted");
        Check(log.Level == "error" && log.Message.Contains("计数截至上次已返回的检查点", StringComparison.Ordinal), "Interrupted phase claimed unreturned IO was measured");
        Check(!f.Logs.Items.Any(l => l.Action == "preparation.indexing.completed"), "Failed preparation was logged as complete");
    }
    private static async Task ForeignDiagnostics()
    {
        var f = new Fixture(true, foreignDiagnostics: true); await f.Run(); f.Verify(64);
        var completed = f.Logs.Items.Where(l => l.Action.StartsWith("preparation.", StringComparison.Ordinal) && l.Action.EndsWith(".completed", StringComparison.Ordinal)).ToArray();
        Check(completed.Length == 3 && completed.All(l => l.Message.Contains("尚未提供此阶段", StringComparison.Ordinal)), "Another job's diagnostics were attributed to this export");
    }
    private sealed class Logs : IProgress<SyncLogEntry>
    { internal List<SyncLogEntry> Items { get; } = []; public void Report(SyncLogEntry value) => Items.Add(value); }
    private sealed class Progress : IProgress<TransferProgress>
    { internal List<TransferProgress> Items { get; } = []; public void Report(TransferProgress value) => Items.Add(value); }
    private sealed class Volume(bool explicitStages, bool fresh, bool jump, bool foreignDiagnostics, int failAt) : ICloudVolume
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public int ObjectSizeBytes => CloudObjectGeometry.DefaultSize;
        internal string RootId { get; } = Guid.NewGuid().ToString();
        internal string JobId { get; } = Guid.NewGuid().ToString();
        private readonly byte[] bytes = new byte[CloudRepository.ObjectLength];
        internal List<JsonElement> PrepareRequests { get; } = [];
        internal List<string> Commands { get; } = [];
        private bool jobExists = !fresh;
        internal int Reads;
        private int stage;
        private string Hash => CloudRepository.Hash(bytes);
        private object Job() => new { object_size = ObjectSizeBytes, id = JobId, phase = stage < 5 ? "preparing" : "ready", generation = 1UL,
            prepare_stage = explicitStages ? stage switch { 0 or 1 => "sealing", 2 or 3 => "indexing", 4 => "root", _ => "ready" } : "",
            sealed_objects = Math.Min(stage, 2), total_tail_objects = 2, processed_pages = stage switch { 2 => 128, 3 => 4096, >= 4 => 8192, _ => 0 }, changed_pages = 8192,
            estimated_bytes = CloudRepository.ObjectLength, total_objects = 1, uploaded_objects = 0, root_object_id = RootId, root_sha256 = Hash,
            local_read_bytes = 987654321L, local_write_bytes = 987654321L,
            preparation_diagnostics = explicitStages ? new {
                job_id = foreignDiagnostics ? Guid.NewGuid().ToString() : JobId, scope = "process_session",
                stages = new { freeze = Metrics(1, 1), sealing = Metrics(2, Math.Min(stage, 2)),
                    indexing = Metrics(3, Math.Clamp(stage - 1, 0, 3)), root = Metrics(4, Math.Max(0, stage - 4)) }
            } : null };
        private static object Metrics(int units, int steps) => new { local_read_bytes = steps > 0 ? units * 4096L : 0,
            local_write_bytes = steps > 0 ? units * 8192L : 0, read_calls = steps > 0 ? units : 0,
            write_calls = steps > 0 ? units * 2 : 0, flush_count = steps > 0 ? units : 0,
            duration_ms = steps > 0 ? units * 10 : 0, steps };
        public Task<JsonElement> ControlAsync(object request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var r = JsonSerializer.SerializeToElement(request); object result;
            Commands.Add(r.GetProperty("cmd").GetString()!);
            switch (r.GetProperty("cmd").GetString())
            {
                case "cloud.pause": case "cloud.transfer": result = new { ok = true }; break;
                case "cloud.status": result = new { object_size = ObjectSizeBytes, data_generation = 1UL, published_generation = 0UL, local_dirty = true, job = jobExists ? Job() : null }; break;
                case "cloud.prepare":
                    PrepareRequests.Add(r.Clone());
                    if (PrepareRequests.Count == failAt) throw new IOException("Injected preparation failure");
                    if (!jobExists) jobExists = true;
                    else stage = jump ? 5 : stage + 1;
                    result = new { job = Job() }; break;
                case "cloud.list": result = new { items = new[] { new { id = RootId, kind = "index", length = CloudRepository.ObjectLength, sha256 = Hash, uploaded = false } }, next_cursor = (long?)null }; break;
                case "cloud.delta": result = new { items = Array.Empty<object>(), next_cursor = (long?)null }; break;
                case "cloud.receipt": result = new { job = Job() }; break;
                case "cloud.commit": result = new { status = new { object_size = ObjectSizeBytes, data_generation = 1UL, published_generation = 1UL, local_dirty = false, job = (object?)null } }; break;
                default: throw new Exception("Unexpected native command " + r.GetProperty("cmd").GetString());
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
        public Task<byte[]> ReadObjectAsync(string job, string id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Check(job == JobId && id == RootId, "Wrong export identity"); Reads++; return Task.FromResult(bytes.ToArray()); }
    }
    private sealed class Store : ICloudObjectStore
    {
        public string ProviderId => "preparing-fixture";
        internal List<string> Calls { get; } = [];
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken ct = default) => throw new Exception("Unnecessary account request");
        public Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken ct = default) => throw new Exception("Unnecessary object HEAD");
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => throw new Exception("Known directory was recreated");
        public IAsyncEnumerable<CloudObjectInfo> ListAsync(string path, CancellationToken ct = default) => throw new Exception("Unnecessary cloud listing");
        public async Task<CloudObjectInfo> PutImmutableAsync(string path, Stream content, long length, string sha256, CancellationToken ct = default)
        {
            using var memory = new MemoryStream(); await content.CopyToAsync(memory, ct); var bytes = memory.ToArray();
            Check(bytes.Length == length && CloudRepository.Hash(bytes) == sha256, "Prepared payload mismatch"); Calls.Add("put:" + path); return new(path, length, false);
        }
        public Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken ct = default) => throw new Exception("Progress caused a remote read");
        public Task DeleteAsync(string path, CancellationToken ct = default) => throw new Exception("Progress caused cleanup");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
