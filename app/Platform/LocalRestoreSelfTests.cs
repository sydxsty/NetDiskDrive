using System.Text.Json;
using OverlayDisk.Services;

namespace OverlayDisk;

internal static class LocalRestoreSelfTests
{
    internal static async Task<int> RunAsync(string output)
    {
        if (Directory.Exists(output)) throw new IOException("请使用全新的本地恢复测试目录。");
        Directory.CreateDirectory(output);
        string catalog = Path.Combine(output, "settings"), sourcePath = Path.Combine(output, "source.odv4"), targetPath = Path.Combine(output, "target.odv4");
        const string? password = null;
        byte[] expected = new byte[1024 * 1024];
        new Random(71).NextBytes(expected);
        CoreDisk.Create(sourcePath, 128UL * 1024 * 1024, password);
        using (var source = new CoreDisk(sourcePath, password))
        {
            for (ulong index = 0; index < 16; index++) source.Write(index * (ulong)expected.Length, expected, expected.Length);
            source.Flush();
        }
        var checks = new List<string>();
        var service = new WorkerApplicationService(catalog);
        static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        static JsonElement Result(object? value) => value is JsonElement element ? element : Args(value!);
        async Task<JsonElement> Call(string method, object args) => Result(await service.InvokeAsync(method, Args(args), default));
        string taskId;
        try
        {
            var state = await Call("disks.import", new { path = sourcePath });
            string sourceId = state.GetProperty("disks")[0].GetProperty("id").GetString()!;
            await Call("disks.unlock", new { id = sourceId, password });
            var snapshot = await Call("snapshots.create", new { id = sourceId, name = "暂停恢复测试" });
            string snapshotId = snapshot.GetProperty("snapshot").GetProperty("id").GetString()!;
            var begun = await Call("snapshots.restore", new { id = sourceId, snapshotId, path = targetPath, name = "恢复测试", password });
            taskId = begun.GetProperty("taskId").GetString()!;
            await Call("snapshots.restorePause", new { taskId }).WaitAsync(TimeSpan.FromSeconds(20));
            state = await Call("state", new { });
            var task = state.GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetString() == taskId);
            if (task.GetProperty("kind").GetString() != "snapshotRestore" || task.GetProperty("state").GetString() != "paused")
                throw new IOException("本地恢复任务未暂停。");
            if (task.GetProperty("requiresPassword").GetBoolean() || task.GetProperty("sourceRequiresPassword").GetBoolean()) throw new IOException("已持有句柄时不应再次请求密码。");
            checks.Add("local restore pause completes without command-gate deadlock and retains open handles");
            await Call("disks.close", new { id = sourceId }).WaitAsync(TimeSpan.FromSeconds(20));
            await Call("disks.unlock", new { id = sourceId, password });
            checks.Add("closing source checkpoints the paused target and releases its retained source file lock");
        }
        finally { await service.ShutdownAsync(default); }

        service = new WorkerApplicationService(catalog);
        try
        {
            var state = await Call("state", new { });
            var task = state.GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetString() == taskId);
            if (task.GetProperty("requiresPassword").GetBoolean() || task.GetProperty("sourceRequiresPassword").GetBoolean())
                throw new IOException("重启后未请求恢复目标和源磁盘的密码。");
            string metadata = File.ReadAllText(Path.Combine(catalog, "snapshot-restores.json"));
            if (metadata.Contains("isolated-restore-fixture-password", StringComparison.Ordinal)) throw new IOException("任务元数据保存了密码。");
            checks.Add("recreated worker recovers durable task metadata without persisting passwords");
            await Call("snapshots.restoreResume", new { taskId, targetPassword = password, sourcePassword = password });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                state = await Call("state", new { });
                task = state.GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetString() == taskId);
                string phase = task.GetProperty("state").GetString()!;
                if (phase == "done") break;
                if (phase == "paused") throw new IOException("继续恢复失败：" + task.GetProperty("error").GetString());
                await Task.Delay(50, timeout.Token);
            }
            using (var restored = new CoreDisk(targetPath, password))
            {
                byte[] actual = new byte[expected.Length];
                for (ulong index = 0; index < 16; index++)
                {
                    restored.Read(index * (ulong)expected.Length, actual, actual.Length);
                    if (!actual.AsSpan().SequenceEqual(expected)) throw new IOException("恢复后的磁盘数据与快照不一致。");
                }
            }
            checks.Add("resume reattaches plaintext source and target, completes import and verifies all 16 MiB");
        }
        finally { await service.ShutdownAsync(default); }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks }));
        Console.WriteLine("LOCAL_RESTORE_SMOKE_OK"); return 0;
    }
}
