using System.Collections.Concurrent;
using System.Text.Json;
using OverlayDisk.Cloud.Sync;

namespace OverlayDisk.Services;

public sealed partial class ApplicationService
{
    private readonly ConcurrentDictionary<string, string> cacheErrors = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> cacheSettingsGates = new();

    internal static bool RetainReplicaSourcePins(AppSettings settings, string id)
    {
        bool changed = false;
        foreach (var record in settings.Restores.Values.Where(r => r.LocalDiskId == id && !r.ContainerDeleted && r.SourcePinReplaced))
        { record.SourcePinReplaced = false; changed = true; }
        return changed;
    }

    internal static LocalCacheSettings ParseCacheSettings(JsonElement args)
    {
        if (!args.TryGetProperty("limitBytes", out var limit) || !limit.TryGetUInt64(out ulong bytes) ||
            bytes != 0 && (bytes < 64UL * 1024 * 1024 || bytes > CloudRepository.MaximumCapacityBytes || bytes % (4 * 1024 * 1024) != 0))
            throw new IOException("缓存上限应为 0（不限制）或 64 MiB 至 8 TiB，并对齐到 4 MiB。");
        string policy = Text(args, "policy", "lru");
        if (policy is not ("lru" or "lfu" or "sequential")) throw new IOException("请选择有效的缓存保留策略。");
        return new(bytes, policy);
    }

    private async Task<object> SaveCacheSettingsAsync(string id, JsonElement args, CancellationToken ct)
    {
        var preference = ParseCacheSettings(args);
        _ = Disk(id);
        lock (gate) settings.LocalCaches[id] = preference;
        Save();
        try { await ApplyCacheSettingsAsync(id, ct); cacheErrors.TryRemove(id, out _); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            cacheErrors[id] = "设置已保存，但尚未应用：" + Friendly(error);
            Changed(); throw new IOException(cacheErrors[id], error);
        }
        Log(id, "", "cache", "cache.configured", preference.LimitBytes == 0 ? "已关闭本地缓存上限；已有缺块继续按需读取" : $"本地缓存上限已设为 {preference.LimitBytes / (1024 * 1024)} MiB，策略 {preference.Policy}");
        await RefreshWorkerAsync(ct); Changed(); return new { ok = true };
    }

    private async Task ApplyCacheSettingsSafeAsync(string id, CancellationToken ct)
    {
        try { await ApplyCacheSettingsAsync(id, ct); cacheErrors.TryRemove(id, out _); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            string message = "缓存管理等待处理：" + Friendly(error);
            if (cacheErrors.GetValueOrDefault(id) != message) Log(id, "", "cache", "cache.waiting", message, "warning");
            cacheErrors[id] = message;
        }
    }

    private void RecoverCacheSource(JsonElement disk)
    {
        if (!Flag(disk, "unlocked") || !disk.TryGetProperty("cache", out var cache) || !Flag(cache, "source_ready") ||
            !cache.TryGetProperty("backing", out var backing) || backing.ValueKind != JsonValueKind.Object) return;
        string id = Text(disk, "id"), root = Text(backing, "remote_root"), provider = Text(backing, "backend_id"),
            accountId = Text(backing, "account_id"), writer = Text(backing, "device_id"), pin = Text(backing, "reader_pin");
        if (Text(backing, "volume_id") != id || provider != "baidu-private-web" || string.IsNullOrWhiteSpace(accountId) ||
            !Guid.TryParse(writer, out _) || !CloudRepository.IsRootForVolume(root, id) || pin != CloudRepository.CachePinPath(root, id))
            throw new IOException("磁盘内保存的缓存来源身份无效。");
        var binding = new CloudBinding(provider, accountId, root, writer);
        string path = Text(disk, "containerPath"); bool changed = false;
        lock (gate)
        {
            if (!settings.CacheSources.TryGetValue(id, out var old) || old.Binding != binding || old.ReaderPin != pin || old.ContainerPath != path || old.ContainerDeleted)
            { settings.CacheSources[id] = new() { Binding = binding, ReaderPin = pin, ContainerPath = path }; changed = true; }
            if (!settings.LocalCaches.ContainsKey(id))
            { settings.LocalCaches[id] = new(UInt(cache, "max_bytes"), Text(cache, "policy", "lru")); changed = true; }
            if (!settings.Bindings.ContainsKey(id)) { settings.Bindings[id] = binding; changed = true; }
            // Repair the tiny GUI checkpoint window after a durable native pin
            // handoff. This marks eligibility only; deletion still needs manual GC.
            if (Flag(cache, "origin_pin_required")) changed |= RetainReplicaSourcePins(settings, id);
            else if (cache.TryGetProperty("origin_backing", out var origin) && Text(origin, "backend_id") == provider &&
                Text(origin, "account_id") == accountId && Text(origin, "remote_root") == root)
            {
                string oldPin = Text(origin, "reader_pin");
                if (oldPin.Length != 0 && oldPin != pin)
                    foreach (var record in settings.Restores.Values.Where(r => r.LocalDiskId == id && r.ReaderPin == oldPin && !r.SourcePinReplaced))
                    { record.SourcePinReplaced = true; changed = true; }
            }
        }
        if (changed) Save();
    }

    private async Task ApplyCacheSettingsAsync(string id, CancellationToken ct, bool handOffOriginalPin = false)
    {
        if (!worker.IsConnected) return;
        var mutex = cacheSettingsGates.GetOrAdd(id, _ => new(1, 1)); await mutex.WaitAsync(ct);
        try
        {
            await RefreshWorkerAsync(ct); var disk = Disk(id);
            if (!Flag(disk, "unlocked")) return;
            RecoverCacheSource(disk);
            LocalCacheSettings preference; CloudBinding? binding;
            lock (gate) { preference = settings.LocalCaches.GetValueOrDefault(id) ?? new(); binding = settings.Bindings.GetValueOrDefault(id); }
            var cache = Element(await worker.InvokeAsync("cache.status", Element(new { id }), ct));
            if (UInt(cache, "max_bytes") != preference.LimitBytes || Text(cache, "policy", "lru") != preference.Policy)
                cache = Element(await worker.InvokeAsync("cache.configure", Element(new { id, max_bytes = preference.LimitBytes, policy = preference.Policy }), ct));
            bool sameOrigin = handOffOriginalPin && !Flag(cache, "origin_pin_required") && binding is not null && cache.TryGetProperty("origin_backing", out var origin) &&
                Text(origin, "remote_root") == binding.RemoteRoot && Text(origin, "account_id") == binding.AccountId;
            if ((preference.LimitBytes > 0 || sameOrigin) && !Flag(cache, "source_ready") && binding is not null && binding.AccountId == account?.AccountId && client is not null)
            {
                var cloud = await new WorkerVolume(worker, id, DiskObjectSize(disk)).ControlAsync(new { cmd = "cloud.status" }, ct);
                bool uploadActive = cloud.TryGetProperty("job", out var upload) && upload.ValueKind == JsonValueKind.Object && Text(upload, "phase") != "published";
                if (!uploadActive && cloud.TryGetProperty("published_commit", out var commit) && commit.ValueKind == JsonValueKind.Object)
                {
                    string pin = await Repository().EnsureCachePinAsync(binding, id, ct);
                    // The pin must reach the provider before native can discard any payload.
                    lock (gate) settings.CacheSources[id] = new() { Binding = binding, ReaderPin = pin, ContainerPath = Text(disk, "containerPath") };
                    Save();
                    cache = Element(await worker.InvokeAsync("cache.bind", Element(new { id, backing = new {
                        reader_pin = pin, backend_id = binding.ProviderId, account_id = binding.AccountId,
                        remote_root = binding.RemoteRoot, device_id = binding.DeviceId, volume_id = id } }), ct));
                    string replaced = Text(cache, "replaces_origin_pin");
                    if (replaced.Length != 0 && replaced != pin)
                    {
                        lock (gate) foreach (var record in settings.Restores.Values.Where(r => r.LocalDiskId == id && r.ReaderPin == replaced)) record.SourcePinReplaced = true;
                        Save();
                    }
                    Log(id, "", "cache", "cache.ready", "云端读取引用已确认，本地已同步块可按设置淘汰");
                }
            }
            // Connectivity gates eviction; it does not discard the backing of existing
            // placeholders when the user disables the quota or signs out.
            string sourceField = Flag(cache, "source_ready") ? "backing" : "origin_backing";
            string sourceAccount = cache.TryGetProperty(sourceField, out var source) ? Text(source, "account_id") : "";
            bool available = (Flag(cache, "source_ready") || Flag(cache, "origin_ready")) && sourceAccount == account?.AccountId && client is not null && cloudReadsOpen && !exiting;
            if (Flag(cache, "online") != available)
                await worker.InvokeAsync("cache.online", Element(new { id, available }), ct);
        }
        finally { mutex.Release(); }
    }

    private async Task ReleaseDeletedCachePinsAsync(CloudBinding binding, CancellationToken ct)
    {
        KeyValuePair<string, CacheSourceRecord>[] candidates;
        lock (gate) candidates = settings.CacheSources.Where(p => p.Value.ContainerDeleted && p.Value.Binding == binding).ToArray();
        foreach (var (id, source) in candidates)
        {
            if (File.Exists(source.ContainerPath) || Disks().Any(d => Text(d, "id") == id)) continue;
            string expected = CloudRepository.CachePinPath(binding.RemoteRoot, id);
            if (source.ReaderPin != expected) throw new IOException("待清理的缓存引用身份不匹配。");
            await Repository().ReleaseDeletedVolumeCachePinAsync(binding, id, ct);
            lock (gate) settings.CacheSources.Remove(id);
            Save(); Log(id, "", "cleanup", "cleanup.deleted", "手动移除了已删除容器的云端缓存引用");
        }
    }
}
