using System.Text.Json;

namespace OverlayDisk.Services;

internal static class DiskDeletionSelfTests
{
    internal static async Task<int> RunAsync(string output)
    {
        if (Directory.Exists(output)) throw new IOException("请使用新的删除测试目录。");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        string catalog = Path.Combine(output, "catalog"), path = Path.Combine(output, "test.odv4");
        CoreDisk.Create(path, 64UL * 1024 * 1024, null);
        using (var controller = new DiskController(catalog))
        {
            await controller.ImportAsync(path); var entry = controller.Disks.Single();
            await Reject(() => controller.DeleteAsync(entry, true, "incorrect"));
            entry.Mounted = true;
            await Reject(() => controller.DeleteAsync(entry, true, entry.Name));
            entry.Mounted = false;
            await controller.UnlockAsync(entry, null);
            await Reject(() => controller.DeleteAsync(entry, false, ""));
            await controller.CloseAsync(entry);
            using (var busy = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                await Reject(() => controller.DeleteAsync(entry, true, entry.Name));
            Check(File.Exists(path) && controller.Disks.Count == 1, "拒绝删除时文件或目录项改变");
            checks.Add("wrong confirmation, mounted/unlocked disks and external file handles reject deletion without changing data");

            string temporary = Path.Combine(catalog, "disks.json.tmp"); Directory.CreateDirectory(temporary);
            await Reject(() => controller.DeleteAsync(entry, true, entry.Name));
            Directory.Delete(temporary);
            Check(File.Exists(path), "目录写入失败时删除了容器");
            checks.Add("catalog persistence failure preserves the container");

            string saved = Path.Combine(output, "saved.odv4"); File.Move(path, saved);
            CoreDisk.Create(path, 64UL * 1024 * 1024, null);
            await Reject(() => controller.DeleteAsync(entry, true, entry.Name));
            Check(File.Exists(path), "身份不符的文件被删除");
            File.Delete(path); File.Move(saved, path);
            checks.Add("a replacement container with a different identity is not deleted");

            await controller.DeleteAsync(entry, false, "");
            Check(File.Exists(path) && controller.Disks.Count == 0, "从列表移除时改变了容器");
        }
        using (var reopened = new DiskController(catalog))
        {
            Check(reopened.Disks.Count == 0, "移除未持久化");
            await reopened.ImportAsync(path);
            var entry = reopened.Disks.Single(); await reopened.DeleteAsync(entry, true, entry.Name);
            Check(!File.Exists(path) && reopened.Disks.Count == 0, "确认后未删除本地文件");
        }
        using (var check = new DiskController(catalog)) Check(check.Disks.Count == 0, "删除后目录未持久化");
        checks.Add("remove-only preserves an importable file; confirmed deletion removes that exact file and persists the catalog");

        string dependencyPath = Path.Combine(output, "dependency.odv4");
        CoreDisk.Create(dependencyPath, 64UL * 1024 * 1024, null);
        string sourceId;
        using (var setup = new DiskController(catalog)) { await setup.ImportAsync(dependencyPath); sourceId = setup.Disks.Single().Id; }
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        File.WriteAllText(Path.Combine(catalog, "snapshot-restores.json"), JsonSerializer.Serialize(new[] { new
        {
            id = Guid.NewGuid().ToString(), sourceId, sourcePath = dependencyPath, targetPath = Path.Combine(output, "unfinished.odv4"), name = "unfinished"
        } }, json));
        var worker = new WorkerApplicationService(catalog);
        try
        {
            await Reject(async () => { await worker.InvokeAsync("disks.delete", JsonSerializer.SerializeToElement(new { id = sourceId, deleteContainer = false }, json), default); });
            Check(File.Exists(dependencyPath), "未完成恢复的源盘被删除");
            checks.Add("durable unfinished restore references prevent source removal after worker restart");
        }
        finally { await worker.ShutdownAsync(default); }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks }));
        Console.WriteLine("DELETE_SMOKE_OK"); return 0;
    }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("危险删除操作未被拒绝。");
    }
    private static void Check(bool result, string message) { if (!result) throw new IOException(message); }
}
