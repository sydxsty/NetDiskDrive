using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OverlayDisk.Cloud.Baidu;
using OverlayDisk.Cloud.Sync;

namespace OverlayDisk.Services;

/// <summary>Live acceptance test. Every disk, file and remote directory is newly created and individually identified.</summary>
internal static class AppCloudSelfTests
{
    public static async Task<int> RunAsync(string output)
    {
        if (UnelevatedLauncher.IsAdministrator) throw new InvalidOperationException("请从普通权限进程启动应用闭环测试。");
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("应用闭环测试需要全新的输出目录。");
        Directory.CreateDirectory(output);
        string sourcePath = Path.Combine(output, "source.odv4"), targetPath = Path.Combine(output, "target.odv4");
        string catalogPath = Path.Combine(SettingsStorage.DirectoryPath, "disks.json"), settingsPath = Path.Combine(SettingsStorage.DirectoryPath, "settings.json");
        var originalPreferences = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject() : new JsonObject();
        bool hasPending = Property(originalPreferences, "pendingDisks") is JsonArray pendingDisks && pendingDisks.Count != 0;
        var successes = Property(originalPreferences, "lastSuccess") as JsonObject;
        bool hasUnconfirmedBinding = Property(originalPreferences, "bindings") is JsonObject bindings
            && bindings.Any(binding => successes is null || Key(successes, binding.Key) is null);
        bool hasUnfinishedRestore = Property(originalPreferences, "restores") is JsonObject restores
            && restores.Any(item => item.Value is JsonObject record && Property(record, "complete")?.GetValue<bool>() != true);
        string localRestoresPath = Path.Combine(SettingsStorage.DirectoryPath, "snapshot-restores.json");
        if (File.Exists(localRestoresPath))
            hasUnfinishedRestore |= JsonNode.Parse(File.ReadAllText(localRestoresPath))!.AsArray().OfType<JsonObject>().Any(record => Property(record, "complete")?.GetValue<bool>() != true);
        if (hasPending || hasUnconfirmedBinding || hasUnfinishedRestore)
            throw new IOException("测试前提不满足：用户已有待同步或未完成恢复任务；未启动磁盘服务、未改动这些任务。");
        JsonNode? previousCapacity = Property(originalPreferences, "defaultCapacityGiB")?.DeepClone();
        JsonNode? previousDirectory = Property(originalPreferences, "defaultDirectory")?.DeepClone();
        const string password = "App-cloud-acceptance-fixture-only";
        var checks = new List<string>(); var cleanupErrors = new List<string>();
        string stage = "account", lastProgress = "", fileName = "acceptance-" + Guid.NewGuid().ToString("N") + ".bin";
        string? sourceId = null, targetId = null, restoreId = null, remoteRoot = null;
        bool synced = false, sourceClosed = false, targetClosed = false, restored = false, remoteDeleted = false, serviceStopped = false, metadataCleaned = false;
        var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Exception? failure = null;
        var session = await WindowsSessionVault.LoadAsync(SettingsStorage.SessionPath)
            ?? throw new IOException("请先完成应用内登录。");
        await using var provider = new BaiduClient(session);
        var service = new ApplicationService();
        var sourceDiagnostics = new List<object>();
        static JsonElement Json(object? value) => value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, SettingsStorage.Json);
        async Task<JsonElement> Call(string method, object args) => Json(await service.InvokeAsync(method, Json(args), CancellationToken.None));
        async Task<JsonElement> State() => Json(await service.GetStateAsync(CancellationToken.None));
        void SetStage(string value) { stage = value; Console.WriteLine("APP_CLOUD_STAGE " + value); }
        async Task<JsonElement> WaitState(Func<JsonElement, bool> predicate, TimeSpan maximum)
        {
            var until = DateTimeOffset.UtcNow + maximum;
            while (true)
            {
                var state = await State();
                if (predicate(state)) return state;
                if (DateTimeOffset.UtcNow > until) throw new TimeoutException("应用闭环测试等待超时：" + stage);
                await Task.Delay(750);
            }
        }
        async Task<JsonElement> WaitTask(string kind, string identity)
        {
            return await WaitState(state =>
            {
                var tasks = state.GetProperty("tasks").EnumerateArray().Where(t => Text(t, "kind") == kind
                    && Text(t, kind == "sync" ? "diskId" : "id") == identity).ToArray();
                if (tasks.Length == 0) return false;
                var task = tasks[^1]; string phase = Text(task, "state");
                if (phase != lastProgress) { lastProgress = phase; Console.WriteLine("APP_CLOUD_TASK " + kind + " " + phase); }
                if (phase is "error" or "paused") throw new IOException(Text(task, "error", Text(task, "message", "任务未完成。")));
                return phase == (kind == "sync" ? "synced" : "complete") && !Flag(task, "canPause");
            }, TimeSpan.FromMinutes(20));
        }
        JsonElement FindDisk(JsonElement state, string path) => state.GetProperty("disks").EnumerateArray().Single(d => SamePath(Text(d, "containerPath"), path));
        void Remember(JsonElement disk, string path)
        {
            string id = Text(disk, "id"); if (!Guid.TryParse(id, out _) || !SamePath(Text(disk, "containerPath"), path)) throw new IOException("测试磁盘身份无效。");
            owned[id] = path;
            File.WriteAllText(Path.Combine(output, "owned-resources.json"), JsonSerializer.Serialize(new { sourcePath, targetPath, sourceId, targetId, restoreId, remoteRoot, disks = owned }, SettingsStorage.Json));
        }
        void SourceCheckpoint(string label, JsonElement state)
        {
            if (sourceId is null) return;
            var disk = FindDisk(state, sourcePath);
            JsonElement cloud = disk.TryGetProperty("cloud", out var c) ? c : default;
            ulong? Number(string key) => cloud.ValueKind == JsonValueKind.Object && cloud.TryGetProperty(key, out var p) && p.TryGetUInt64(out var value) ? value : null;
            bool? Dirty() => cloud.ValueKind == JsonValueKind.Object && cloud.TryGetProperty("local_dirty", out var p) ? p.GetBoolean() : null;
            var latest = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            sourceDiagnostics.Add(new
            {
                label, mounted = Flag(disk, "mounted"), unlocked = Flag(disk, "unlocked"), dataGeneration = Number("data_generation"),
                publishedGeneration = Number("published_generation"), localDirty = Dirty(),
                persistedPending = Property(latest, "pendingDisks") is JsonArray pending && pending.Any(id => id?.GetValue<string>() == sourceId),
                lastSuccessKnown = Property(latest, "lastSuccess") is JsonObject successes && Key(successes, sourceId) is not null
            });
            File.WriteAllText(Path.Combine(output, "source-status.json"), JsonSerializer.Serialize(sourceDiagnostics, SettingsStorage.Json));
        }
        try
        {
            await provider.ValidateAsync();
            var state = await WaitState(s => s.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object, TimeSpan.FromMinutes(2));
            var unavailable = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
            foreach (var entry in state.GetProperty("disks").EnumerateArray()) if (Text(entry, "driveLetter") is { Length: 1 } letter) unavailable.Add(char.ToUpperInvariant(letter[0]));
            char driveLetter = "ZYXWVUTSRQPONMLKJIHGFED".FirstOrDefault(c => !unavailable.Contains(c));
            if (driveLetter == default) throw new IOException("没有可用的测试盘符。");
            checks.Add("ordinary application loads only its own protected login session");
            SetStage("create-and-save-file");
            await Call("disks.create", new { name = "云端闭环测试", containerPath = sourcePath, capacityBytes = 256UL * 1024 * 1024, driveLetter = driveLetter.ToString(), encrypted = false });
            var source = FindDisk(await State(), sourcePath); sourceId = Text(source, "id"); Remember(source, sourcePath);
            if (!Flag(source, "mounted")) throw new IOException("测试源盘未挂载。");
            byte[] expected = new byte[64 * 1024]; new Random(9201).NextBytes(expected);
            string expectedHash = Convert.ToHexString(SHA256.HashData(expected));
            await File.WriteAllBytesAsync(driveLetter + @":\" + fileName, expected);
            await Call("disks.unmount", new { id = sourceId });
            source = FindDisk(await State(), sourcePath);
            if (Flag(source, "mounted") || Flag(source, "unlocked")) throw new IOException("源盘未完成安全卸载。");
            checks.Add("real administrator worker creates NTFS and saves a 64 KiB file before safe unmount");

            SetStage("sync");
            await Call("disks.unlock", new { id = sourceId });
            remoteRoot = CloudRepository.RootPath(sourceId);
            if (await provider.HeadAsync(remoteRoot) is not null) throw new IOException("测试云目录已存在，已拒绝写入。");
            Remember(source, sourcePath);
            await Call("sync.enable", new { id = sourceId, encrypted = true, password });
            state = await WaitTask("sync", sourceId); synced = true;
            SourceCheckpoint("after-sync", state);
            checks.Add("ApplicationService publishes and confirms the real cloud synchronization job");
            await Call("sync.pause", new { id = sourceId });
            await Call("disks.unmount", new { id = sourceId });
            state = await State(); source = FindDisk(state, sourcePath);
            SourceCheckpoint("after-source-close", state);
            sourceClosed = !Flag(source, "mounted") && !Flag(source, "unlocked");
            if (!sourceClosed) throw new IOException("同步后的源盘未关闭，不能继续清理云目录。");

            SetStage("cloud-import");
            var cloud = await Call("cloud.list", new { });
            if (!cloud.EnumerateArray().Any(d => Text(d, "id") == sourceId)) throw new IOException("云端列表找不到测试源盘。");
            var import = await Call("cloud.import", new { id = sourceId, path = targetPath, name = "云端恢复闭环测试", password, lazy = false });
            restoreId = Text(import, "taskId"); if (!Guid.TryParse(restoreId, out _)) throw new IOException("恢复任务标识无效。");
            state = await WaitTask("restore", restoreId);
            var target = FindDisk(state, targetPath); targetId = Text(target, "id"); Remember(target, targetPath);
            if (targetId == sourceId) throw new IOException("恢复盘未获得独立身份。");
            SetStage("mount-and-verify-restored-file");
            await Call("disks.mount", new { id = targetId });
            target = FindDisk(await State(), targetPath);
            string restoredFile = Text(target, "driveLetter") + @":\" + fileName;
            byte[] actual = await File.ReadAllBytesAsync(restoredFile);
            if (actual.Length != expected.Length || Convert.ToHexString(SHA256.HashData(actual)) != expectedHash) throw new IOException("恢复后 NTFS 文件内容不一致。");
            restored = true;
            await Call("disks.unmount", new { id = targetId });
            target = FindDisk(await State(), targetPath); targetClosed = !Flag(target, "mounted") && !Flag(target, "unlocked");
            if (!targetClosed) throw new IOException("恢复盘未安全关闭。");
            checks.Add("cloud import completes, restored NTFS mounts, and the complete file SHA-256 matches");
            SetStage("remove-owned-cloud-root");
            if (!synced || !sourceClosed || remoteRoot != CloudRepository.RootPath(sourceId)) throw new IOException("测试云目录清理条件不成立。");
            await provider.DeleteAsync(remoteRoot); remoteDeleted = true;
            checks.Add("only the unique test cloud directory is deleted after source closure");
            SourceCheckpoint("before-shutdown", await State());
            await service.ShutdownAsync(CancellationToken.None); serviceStopped = true;
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (!serviceStopped)
            {
                // Discover even a create operation whose final RPC failed, by this test's
                // exclusive fresh paths. Never choose a disk by its number or capacity.
                try
                {
                    var current = File.Exists(catalogPath) ? JsonNode.Parse(File.ReadAllText(catalogPath))!.AsArray() : new JsonArray();
                    foreach (var node in current.OfType<JsonObject>())
                    {
                        string path = NodeText(node, "containerPath"), id = NodeText(node, "id");
                        if (Guid.TryParse(id, out _) && (SamePath(path, sourcePath) || SamePath(path, targetPath))) owned[id] = path;
                    }
                    foreach (var id in owned.Keys)
                    {
                        try { await Call("sync.pause", new { id }); await Call("disks.unmount", new { id }); }
                        catch (Exception error) { cleanupErrors.Add("close test disk: " + error.Message); }
                    }
                    // This flag affects only this test service instance; user disks were never
                    // opened by it. Failed test data may remain local for diagnosis.
                    await Call("app.allowUnsyncedExit", new { });
                    await service.ShutdownAsync(CancellationToken.None); serviceStopped = true;
                }
                catch (Exception error) { cleanupErrors.Add("stop test worker: " + error.Message); }
            }
            if (serviceStopped)
            {
                sourceClosed |= owned.Values.Any(path => SamePath(path, sourcePath));
                targetClosed |= owned.Values.Any(path => SamePath(path, targetPath));
                try
                {
                    CleanupCatalog(catalogPath, owned);
                    CleanupSettings(settingsPath, owned, sourceId, targetPath, remoteRoot, output, previousCapacity, previousDirectory);
                    metadataCleaned = true;
                }
                catch (Exception error) { cleanupErrors.Add("remove test records: " + error.Message); }
            }
        }
        bool passed = failure is null && synced && restored && remoteDeleted && serviceStopped && metadataCleaned && cleanupErrors.Count == 0;
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            passed, stage, elevated = false, synced, restored, sourceClosed, targetClosed, remoteDeleted, serviceStopped, metadataCleaned,
            testRemoteRoot = remoteRoot, error = failure?.Message, cleanupErrors, checks
        }, SettingsStorage.Json));
        if (!passed) throw new IOException("应用云端闭环测试失败：" + (failure?.Message ?? string.Join("；", cleanupErrors)));
        Console.WriteLine("APP_CLOUD_SMOKE_OK"); return 0;
    }

    private static string Text(JsonElement value, string key, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : fallback;
    private static bool Flag(JsonElement value, string key) => value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
    private static bool SamePath(string first, string second) => !string.IsNullOrWhiteSpace(first) && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static string? Key(JsonObject value, string key) => value.Select(pair => pair.Key).FirstOrDefault(name => name.Equals(key, StringComparison.OrdinalIgnoreCase));
    private static JsonNode? Property(JsonObject value, string key) => Key(value, key) is { } name ? value[name] : null;
    private static string NodeText(JsonObject value, string key) => Property(value, key)?.GetValue<string>() ?? "";

    private static void CleanupCatalog(string path, IReadOnlyDictionary<string, string> owned) => TransformLatest(path, root =>
    {
        var entries = root.AsArray();
        foreach (var node in entries.OfType<JsonObject>().ToArray())
            if (owned.TryGetValue(NodeText(node, "id"), out var expectedPath) && SamePath(NodeText(node, "containerPath"), expectedPath)) entries.Remove(node);
    });

    private static void CleanupSettings(string path, IReadOnlyDictionary<string, string> owned, string? sourceId, string targetPath,
        string? remoteRoot, string output, JsonNode? previousCapacity, JsonNode? previousDirectory) => TransformLatest(path, root =>
    {
        var settings = root.AsObject();
        foreach (string name in new[] { "bindings", "lastSuccess" })
            if (Property(settings, name) is JsonObject map)
                foreach (string id in owned.Keys) if (Key(map, id) is { } key) map.Remove(key);
        foreach (string name in new[] { "pausedDisks", "pendingDisks" })
            if (Property(settings, name) is JsonArray set)
                foreach (var item in set.ToArray()) if (item is not null && owned.ContainsKey(item.GetValue<string>())) set.Remove(item);
        if (Property(settings, "restores") is JsonObject restores)
            foreach (var pair in restores.ToArray())
                if (pair.Value is JsonObject record && SamePath(NodeText(record, "targetPath"), targetPath)
                    && NodeText(record, "remoteRoot") == remoteRoot && Property(record, "commit") is JsonObject commit && NodeText(commit, "volumeId") == sourceId)
                    restores.Remove(pair.Key);
        // Creation adjusts two defaults. Restore only those test-written values if nobody
        // changed them again; all other fields come from the latest file, never an old backup.
        if (SamePath(NodeText(settings, "defaultDirectory"), output) && Property(settings, "defaultCapacityGiB")?.GetValue<int>() == 0)
        {
            string capacityKey = Key(settings, "defaultCapacityGiB") ?? "defaultCapacityGiB", directoryKey = Key(settings, "defaultDirectory") ?? "defaultDirectory";
            settings[capacityKey] = previousCapacity?.DeepClone() ?? JsonValue.Create(64);
            settings[directoryKey] = previousDirectory?.DeepClone();
        }
    });

    private static void TransformLatest(string path, Action<JsonNode> transform)
    {
        if (!File.Exists(path)) return;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            byte[] original = File.ReadAllBytes(path);
            var root = JsonNode.Parse(original) ?? throw new IOException("配置 JSON 无效，未执行测试记录清理。");
            var previous = root.DeepClone();
            transform(root);
            if (JsonNode.DeepEquals(previous, root)) return;
            string temporary = path + ".acceptance-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, root, SettingsStorage.Json); stream.Flush(true); }
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(original)) continue;
                File.Move(temporary, path, true); return;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        throw new IOException("配置仍在被其他进程修改，已保留测试记录以避免覆盖用户更改。");
    }
}
