using System.Security.Cryptography;
using System.Text.Json;
using OverlayDisk.Cloud.Baidu;
using OverlayDisk.Cloud.Sync;

namespace OverlayDisk.Services;

internal static class LiveCloudSelfTests
{
    public static async Task<int> RunAsync(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("测试输出目录必须为空。");
        Directory.CreateDirectory(output);
        string sessionPath = SettingsStorage.SessionPath;
        var session = await WindowsSessionVault.LoadAsync(sessionPath) ?? throw new IOException("请先在应用的官方登录页完成登录。");
        await using var provider = new BaiduClient(session);
        var account = await provider.ValidateAsync();
        var repository = new CloudRepository(provider);
        string path = Path.Combine(output, "source.odv4"), target = Path.Combine(output, "restored.odv4");
        const string password = "Cloud-functional-test-only-测试";
        CoreDisk.Create(path, 64UL * 1024 * 1024, password);
        string? root = null; string? volumeId = null; bool uploaded = false, restored = false, cleaned = false;
        try
        {
            using (var disk = new CoreDisk(path, password))
            {
                volumeId = disk.Id.ToString(); root = CloudRepository.RootPath(volumeId);
                if (await provider.HeadAsync(root) is not null) throw new IOException("测试目录已经存在，已拒绝写入。");
                byte[] first = new byte[8192], second = new byte[12288]; new Random(713).NextBytes(first); new Random(719).NextBytes(second);
                disk.Write(512, first, first.Length); disk.Write(4UL * 1024 * 1024 - 512, second, second.Length); disk.Trim(4096, 512); Array.Clear(first, 4096 - 512, 512); disk.Flush();
                string device = Guid.NewGuid().ToString(); var binding = new CloudBinding(provider.ProviderId, account.AccountId, root, device);
                disk.Control(new { cmd = "cloud.bind", backend_id = binding.ProviderId, account_id = binding.AccountId, remote_root = binding.RemoteRoot, device_id = binding.DeviceId, enabled = true });
                var progress = new Progress<TransferProgress>(p => Console.WriteLine("CLOUD_STAGE " + p.Phase));
                var result = await new SyncCoordinator(repository).RunAsync(new LocalVolume(disk), binding, "连接验证（自动测试）", disk.Capacity, true, 2, progress, CancellationToken.None);
                uploaded = true;
                var latest = await repository.LatestAsync(root, CancellationToken.None) ?? throw new IOException("上传后找不到云端版本。");
                if (latest.Commit != result.Commit) throw new IOException("上传版本读回不一致。");
                byte[] rootObject = await repository.ReadObjectAsync(root, result.Commit.RootObjectId, result.Commit.RootSha256, result.Commit.ObjectSizeBytes, CancellationToken.None);
                string reader = await repository.PinReaderAsync(root, result.Commit, Guid.NewGuid().ToString(), CancellationToken.None);
                using (var recovered = CoreDisk.BeginRestore(target, rootObject, password))
                {
                    while (true)
                    {
                        var status = recovered.Control(new { cmd = "restore.status", cursor = 0, limit = 128 });
                        if (status.GetProperty("phase").GetString() is "ready" or "complete") break;
                if (status.GetProperty("phase").GetString() == "building") { recovered.Control(new { cmd = "restore.step", max_pages = 128 }); continue; }
                        var needed = status.TryGetProperty("needed", out var n) ? n : status.GetProperty("items");
                        if (needed.GetArrayLength() == 0) throw new IOException("恢复任务缺少后续对象。");
                        foreach (var item in needed.EnumerateArray())
                        {
                            var obj = SyncCoordinator.ParseObject(item);
                            var data = await repository.ReadObjectAsync(root, obj.Id, obj.Sha256, (int)disk.ObjectSizeBytes, CancellationToken.None);
                            recovered.AcceptRestoreObject(obj.Id, data);
                        }
                    }
                    recovered.Control(new { cmd = "restore.finish" }); recovered.Flush();
                    byte[] actual = new byte[first.Length]; recovered.Read(512, actual, actual.Length);
                    if (!actual.SequenceEqual(first)) throw new IOException("恢复的首段内容不一致。");
                    actual = new byte[second.Length]; recovered.Read(4UL * 1024 * 1024 - 512, actual, actual.Length);
                    if (!actual.SequenceEqual(second)) throw new IOException("恢复的跨对象内容不一致。");
                    if (recovered.Id == disk.Id) throw new IOException("恢复副本不能继承源卷身份。");
                    if (recovered.Control(new { cmd = "cloud.status" }).GetProperty("bound").GetBoolean()) throw new IOException("恢复副本不能继承源云写入权。");
                    restored = true;
                }
                using (var reopened = new CoreDisk(target, password))
                { byte[] got = new byte[first.Length]; reopened.Read(512, got, got.Length); if (!got.SequenceEqual(first)) throw new IOException("恢复关闭重开后内容不一致。"); }
                await provider.DeleteAsync(reader);
            }
            await provider.DeleteAsync(root); cleaned = true;
            await WindowsSessionVault.SaveAsync(sessionPath, provider.ExportSession());
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, uploaded, restored, cleaned, tests = new[] { "saved DPAPI session", "private web upload", "immutable commit readback", "encrypted cloud restore", "new independent identity", "reopen and compare", "owned test directory cleanup" } }, SettingsStorage.Json));
            Console.WriteLine("LIVE_CLOUD_SMOKE_OK"); return 0;
        }
        catch
        {
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = false, uploaded, restored, cleaned, testRemoteRoot = root }, SettingsStorage.Json));
            throw;
        }
    }
    private sealed class LocalVolume(CoreDisk disk) : ICloudVolume
    {
        public string Id => disk.Id.ToString();
        public int ObjectSizeBytes => checked((int)disk.ObjectSizeBytes);
        public Task<JsonElement> ControlAsync(object request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(disk.Control(request)); }
        public Task<byte[]> ReadObjectAsync(string jobId, string objectId, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(disk.ReadExport(jobId, objectId)); }
    }
}
