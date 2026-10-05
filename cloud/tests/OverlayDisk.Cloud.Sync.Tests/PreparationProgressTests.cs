using System.Text.Json;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

internal static class PreparationProgressTests
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("preparation reports sealing index pages and root separately without extra requests", Stages),
        ("unspecified preparation stage uses generic wording without inventing upload bytes", Fallback)
    ];
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task<Progress> Run(bool stages)
    {
        var volume = new Volume(stages); var store = new Store(); var cache = new MemoryCloudSyncCache();
        var binding = new CloudBinding(store.ProviderId, "fixture-account", CloudRepository.RootPath(volume.Id), Guid.NewGuid().ToString());
        var scope = CloudCacheScope.From(binding);
        await using (var lease = await cache.AcquireAsync(scope))
        {
            var state = new CloudSyncCacheState { Scope = scope, OwnerConfirmed = true, LatestKnown = true };
            state.ConfirmedFolders.UnionWith([CloudRepository.BasePath, binding.RemoteRoot, binding.RemoteRoot + "/commits",
                binding.RemoteRoot + "/objects/" + volume.RootId[..2]]);
            await cache.SaveAsync(scope, state);
        }
        var progress = new Progress();
        await new SyncCoordinator(new(store, cache, binding.AccountId)).RunAsync(volume, binding, "fixture", 64UL << 20, false, 2, progress, default);
        Check(volume.PrepareRequests.Count == 5 && volume.PrepareRequests.All(p => p.GetProperty("max_pages").GetInt32() == 4096 && p.GetProperty("max_objects").GetInt32() == 2),
            "Progress reporting added preparation calls or changed unrelated object concurrency");
        Check(store.Calls.Count == 2 && store.Calls.All(c => c.StartsWith("put:", StringComparison.Ordinal)) && volume.Reads == 1,
            "Progress reporting caused extra cloud requests or local payload reads");
        var preparing = progress.Items.Where(p => p.Phase == "preparing").ToArray();
        Check(preparing.Length == 5 && preparing.All(p => p.CompletedBytes == 0 && p.TotalBytes == CloudRepository.ObjectLength
            && p.PendingBytes == CloudRepository.ObjectLength && p.Estimated && p.UploadedBytes == 0 && p.ReusedBytes == 0),
            "Local processed page counts were incorrectly reported as bytes uploaded");
        Check(progress.Items.Last().LogicalUploadedBytes == CloudRepository.ObjectLength && progress.Items.Last().UploadedBytes is > 0 and < CloudRepository.ObjectLength, "Canonical and compressed upload byte accounting were confused");
        return progress;
    }
    private static async Task Stages()
    {
        var progress = await Run(true); var p = progress.Items.Where(x => x.Phase == "preparing").ToArray();
        Check(p[0].Message.Contains("封口活动对象（0/2）", StringComparison.Ordinal) && p[1].Message.Contains("封口活动对象（1/2）", StringComparison.Ordinal), "Tail sealing progress was hidden");
        Check(p[2].Message.Contains("增量索引", StringComparison.Ordinal) && p[2].Message.Contains(128L.ToString("N0") + "/" + 8192L.ToString("N0"), StringComparison.Ordinal), "Index page progress was hidden");
        Check(p[3].Message.Contains(4096L.ToString("N0") + "/" + 8192L.ToString("N0"), StringComparison.Ordinal) && !p[3].Message.Contains("封口", StringComparison.Ordinal), "Index packing was mislabeled as object sealing");
        Check(p[4].Message.Contains("版本描述", StringComparison.Ordinal), "Root publication preparation was mislabeled");
    }
    private static async Task Fallback()
    {
        var progress = await Run(false);
        Check(progress.Items.Where(p => p.Phase == "preparing").All(p => p.Message.Contains("准备本轮版本", StringComparison.Ordinal) && !p.Message.Contains("封口", StringComparison.Ordinal)), "Unknown native stage was guessed as sealing");
    }
    private sealed class Progress : IProgress<TransferProgress>
    { internal List<TransferProgress> Items { get; } = []; public void Report(TransferProgress value) => Items.Add(value); }
    private sealed class Volume(bool explicitStages) : ICloudVolume
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public int ObjectSizeBytes => CloudObjectGeometry.DefaultSize;
        internal string RootId { get; } = Guid.NewGuid().ToString();
        private readonly string jobId = Guid.NewGuid().ToString();
        private readonly byte[] bytes = new byte[CloudRepository.ObjectLength];
        internal List<JsonElement> PrepareRequests { get; } = [];
        internal int Reads;
        private int stage;
        private string Hash => CloudRepository.Hash(bytes);
        private object Job() => new { object_size = ObjectSizeBytes, id = jobId, phase = stage < 5 ? "preparing" : "ready", generation = 1UL,
            prepare_stage = explicitStages ? stage switch { 0 or 1 => "sealing", 2 or 3 => "indexing", 4 => "root", _ => "ready" } : "",
            sealed_objects = Math.Min(stage, 2), total_tail_objects = 2, processed_pages = stage switch { 2 => 128, 3 => 4096, >= 4 => 8192, _ => 0 }, changed_pages = 8192,
            estimated_bytes = CloudRepository.ObjectLength, total_objects = 1, uploaded_objects = 0, root_object_id = RootId, root_sha256 = Hash };
        public Task<JsonElement> ControlAsync(object request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var r = JsonSerializer.SerializeToElement(request); object result;
            switch (r.GetProperty("cmd").GetString())
            {
                case "cloud.pause": case "cloud.transfer": result = new { ok = true }; break;
                case "cloud.status": result = new { object_size = ObjectSizeBytes, data_generation = 1UL, published_generation = 0UL, local_dirty = true, job = Job() }; break;
                case "cloud.prepare": PrepareRequests.Add(r.Clone()); stage++; result = new { job = Job() }; break;
                case "cloud.list": result = new { items = new[] { new { id = RootId, kind = "index", length = CloudRepository.ObjectLength, sha256 = Hash, uploaded = false } }, next_cursor = (long?)null }; break;
                case "cloud.delta": result = new { items = Array.Empty<object>(), next_cursor = (long?)null }; break;
                case "cloud.receipt": result = new { job = Job() }; break;
                case "cloud.commit": result = new { status = new { object_size = ObjectSizeBytes, data_generation = 1UL, published_generation = 1UL, local_dirty = false, job = (object?)null } }; break;
                default: throw new Exception("Unexpected native command " + r.GetProperty("cmd").GetString());
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
        public Task<byte[]> ReadObjectAsync(string job, string id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Check(job == jobId && id == RootId, "Wrong export identity"); Reads++; return Task.FromResult(bytes.ToArray()); }
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
