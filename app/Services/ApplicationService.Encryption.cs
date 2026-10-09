using System.Security.Cryptography;
using System.Text;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

namespace OverlayDisk.Services;

public sealed partial class ApplicationService
{
    private readonly Dictionary<string, CloudEncryptionContext> cloudKeys = new();
    private static string CloudKeyId(string accountId, string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId + "\n" + root))).ToLowerInvariant();

    private bool CloudEncrypted(string diskId)
    {
        lock (gate)
        {
            if (settings.Bindings.TryGetValue(diskId, out var binding) &&
                settings.CloudKeys.TryGetValue(CloudKeyId(binding.AccountId, binding.RemoteRoot), out var saved))
                return saved.Encryption is not null;
            return settings.Restores.Values.Any(r => r.LocalDiskId == diskId && !r.ContainerDeleted && r.Commit.Encrypted);
        }
    }

    private CloudRepository RegisterCloudKeys(CloudRepository repository, string accountId)
    {
        lock (gate)
        foreach (var (id, record) in settings.CloudKeys)
        {
            if (record.AccountId != accountId || record.Encryption is null) continue;
            if (!cloudKeys.TryGetValue(id, out var context))
            {
                if (record.ProtectedKey is null) continue;
                byte[]? raw = null;
                try
                {
                    raw = WindowsCloudKeyVault.Unprotect(record.ProtectedKey);
                    context = CloudEncryptionContext.FromKey(record.Encryption, raw);
                    cloudKeys.Add(id, context);
                }
                catch (Exception error) when (error is CryptographicException or FormatException or IOException)
                { continue; } // Import can re-unlock this source using the cloud password.
                finally { if (raw is not null) CryptographicOperations.ZeroMemory(raw); }
            }
            repository.RegisterEncryption(record.RemoteRoot, context);
        }
        return repository;
    }

    private bool CanReadCloudRoot(string accountId, string root)
    {
        lock (gate)
        {
            string id = CloudKeyId(accountId, root);
            if (!settings.CloudKeys.TryGetValue(id, out var record) || record.AccountId != accountId || record.RemoteRoot != root) return false;
            if (record.Encryption is null)
                return !settings.Restores.Values.Any(r => r.AccountId == accountId && r.RemoteRoot == root && r.Commit.Encrypted);
            if (!CloudRepository.IsRootForVolume(root, record.Encryption.VolumeId)) return false;
            if (cloudKeys.ContainsKey(id)) return true;
            if (record.ProtectedKey is null) return false;
            byte[]? raw = null;
            try
            {
                raw = WindowsCloudKeyVault.Unprotect(record.ProtectedKey);
                cloudKeys.Add(id, CloudEncryptionContext.FromKey(record.Encryption, raw)); return true;
            }
            catch (Exception error) when (error is CryptographicException or FormatException or IOException) { return false; }
            finally { if (raw is not null) CryptographicOperations.ZeroMemory(raw); }
        }
    }

    private (string Account, string Root, CloudEncryptionSettings? Encryption)[] DiskCloudSources(string diskId)
    {
        lock (gate)
        {
            var sources = settings.Restores.Values.Where(r => r.LocalDiskId == diskId && !r.ContainerDeleted)
                .Select(r => (r.AccountId, r.RemoteRoot, r.Commit.Encryption)).ToList();
            if (settings.Bindings.TryGetValue(diskId, out var binding))
                sources.Add((binding.AccountId, binding.RemoteRoot, settings.CloudKeys.GetValueOrDefault(CloudKeyId(binding.AccountId, binding.RemoteRoot))?.Encryption));
            return sources.DistinctBy(s => (s.AccountId, s.RemoteRoot)).ToArray();
        }
    }

    private bool CloudKeyRequired(string diskId) => DiskCloudSources(diskId)
        .Any(s => s.Encryption is not null && !CanReadCloudRoot(s.Account, s.Root));

    private void UnlockDiskCloudKey(string diskId, string? password)
    {
        if (string.IsNullOrEmpty(password)) throw new IOException("请输入云端密码。");
        var source = DiskCloudSources(diskId).FirstOrDefault(s => s.Encryption is not null && !CanReadCloudRoot(s.Account, s.Root));
        if (source.Encryption is null) return;
        if (source.Account != account?.AccountId) throw new IOException("请先登录磁盘来源对应的网盘账户。");
        using var context = CloudEncryptionContext.Unlock(source.Encryption, password);
        SaveCloudKey(source.Account, source.Root, context);
        RegisterCloudKeys(Repository(), source.Account);
    }

    private void SaveCloudKey(string accountId, string root, CloudEncryptionContext? context)
    {
        string id = CloudKeyId(accountId, root);
        byte[]? raw = context?.ExportKey();
        string? protectedKey;
        try { protectedKey = raw is null ? null : WindowsCloudKeyVault.Protect(raw); }
        finally { if (raw is not null) CryptographicOperations.ZeroMemory(raw); }
        lock (gate)
        {
            var previous = settings.CloudKeys.GetValueOrDefault(id);
            if (previous is { Encryption: null, ProtectedKey: null } && context is null) return;
            settings.CloudKeys[id] = new(accountId, root, context?.Settings, protectedKey);
            try { SettingsStorage.Save(settings); }
            catch
            {
                if (previous is null) settings.CloudKeys.Remove(id); else settings.CloudKeys[id] = previous;
                throw;
            }
        }
    }

    private void UnlockCloudCommit(CloudRepository repository, string accountId, string root, RemoteCommit commit, string? password)
    {
        string id = CloudKeyId(accountId, root);
        lock (gate)
            if (settings.CloudKeys.TryGetValue(id, out var saved) && saved.Encryption != commit.Encryption)
                throw new IOException("云端加密参数与已保存的磁盘不一致，已停止加载。");
        if (!commit.Encrypted)
        {
            repository.AuthenticateCommit(root, commit);
            SaveCloudKey(accountId, root, null);
            return;
        }
        if (commit.Encryption is null) throw new IOException("云端加密参数缺失。");
        if (!string.IsNullOrEmpty(password))
        {
            using var unlocked = CloudEncryptionContext.Unlock(commit.Encryption, password);
            repository.RegisterEncryption(root, unlocked);
            repository.AuthenticateCommit(root, commit);
            SaveCloudKey(accountId, root, unlocked);
        }
        // Register a retained DPAPI-backed context, never the temporary password context.
        RegisterCloudKeys(repository, accountId);
        repository.AuthenticateCommit(root, commit);
    }

    private void ConfigureCloudEncryption(CloudRepository repository, string accountId, string root, string volumeId, bool encrypted, string? password)
    {
        lock (gate)
        {
            if (settings.CloudKeys.TryGetValue(CloudKeyId(accountId, root), out var existing))
            {
                if ((existing.Encryption is not null) != encrypted)
                    throw new IOException("云端加密方式在首次启用时固定；如需更改，请新建磁盘副本。");
                if (existing.Encryption is not null && !CanReadCloudRoot(accountId, root))
                {
                    if (string.IsNullOrEmpty(password)) throw new IOException("请重新输入云端密码。");
                    using var unlocked = CloudEncryptionContext.Unlock(existing.Encryption, password);
                    SaveCloudKey(accountId, root, unlocked);
                }
                RegisterCloudKeys(repository, accountId);
                return;
            }
        }
        if (encrypted)
        {
            if (string.IsNullOrEmpty(password)) throw new IOException("请设置云端加密密码。");
            using var context = CloudEncryptionContext.Create(volumeId, password);
            SaveCloudKey(accountId, root, context);
            RegisterCloudKeys(repository, accountId);
        }
        else SaveCloudKey(accountId, root, null);
    }

    private void DisposeCloudKeys()
    {
        lock (gate) { foreach (var context in cloudKeys.Values) context.Dispose(); cloudKeys.Clear(); }
    }
}
