using System.Collections.Concurrent;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Sync;

public sealed partial class CloudRepository
{
    // The application owns context lifetime and persists only a DPAPI protected key.
    private readonly ConcurrentDictionary<string, CloudEncryptionContext> encryption = new(StringComparer.Ordinal);
    public void RegisterEncryption(string root, CloudEncryptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context); ValidateRoot(root);
        CloudEncryptionContext.ValidateSettings(context.Settings);
        if (!IsRootForVolume(root, context.Settings.VolumeId)) throw new IOException("云端密钥不属于这块磁盘。");
        if (encryption.TryGetValue(root, out var prior) && prior.Settings != context.Settings) throw new IOException("同一云端磁盘不能切换加密密钥。");
        encryption[root] = context;
    }
    public CloudEncryptionSettings? EncryptionSettings(string root) => encryption.TryGetValue(root, out var context) ? context.Settings : null;
    internal CloudEncryptionContext? EncryptionContext(string root) => encryption.TryGetValue(root, out var context) ? context : null;
    internal CanonicalObjectDescriptor ObjectDescriptor(string root, string id, int size, string hash)
    {
        var context = EncryptionContext(root);
        return new(ObjectPath(root, id), size, hash, context is null ? ObjectTransport.Codec : ObjectTransport.EncryptedCodec, context?.Settings.Id);
    }
    internal RemoteCommit ProtectCommit(string root, RemoteCommit commit)
    {
        var context = EncryptionContext(root);
        var result = commit with { Encrypted = context is not null, Encryption = context?.Settings,
            TransportCodec = context is null ? ObjectTransport.Codec : ObjectTransport.EncryptedCodec, Authentication = null };
        if (context is not null) result = result with { Authentication = context.Authenticate(CommitAuthenticationBytes(result)) };
        ValidateCommit(root, CommitPath(root, result), result); return result;
    }
    /// <summary>Call before using any selected remote root. Registered encrypted disks reject a plaintext downgrade.</summary>
    public void AuthenticateCommit(string root, RemoteCommit commit)
    {
        ValidateCommit(root, CommitPath(root, commit), commit);
        var context = EncryptionContext(root);
        if (commit.Encrypted)
        {
            if (context is null) throw new IOException("请先输入密码解锁云端磁盘。");
            if (context.Settings != commit.Encryption || !context.Verify(CommitAuthenticationBytes(commit), commit.Authentication))
                throw new IOException("云端版本描述认证失败，已停止加载。");
        }
        else if (context is not null) throw new IOException("云端加密磁盘出现未加密版本，已停止加载。");
    }
    private static byte[] CommitAuthenticationBytes(RemoteCommit commit)
    {
        byte[] domain = "OverlayDisk/commit-authentication/v1\n"u8.ToArray();
        byte[] value = JsonSerializer.SerializeToUtf8Bytes(commit with { Authentication = null }, Json);
        byte[] combined = new byte[domain.Length + value.Length]; domain.CopyTo(combined, 0); value.CopyTo(combined, domain.Length); return combined;
    }
    internal static void ValidateCommitEncryption(RemoteCommit commit)
    {
        if (commit.Encrypted)
        {
            if (commit.Encryption is null || commit.TransportCodec != ObjectTransport.EncryptedCodec || commit.Authentication is not { Length: 44 })
                throw new IOException("云端加密版本缺少完整的加密参数或认证。");
            CloudEncryptionContext.ValidateSettings(commit.Encryption);
            if (commit.Encryption.VolumeId != commit.VolumeId) throw new IOException("云端加密参数与磁盘身份不一致。");
            Span<byte> tag = stackalloc byte[32];
            if (!Convert.TryFromBase64String(commit.Authentication, tag, out int count) || count != tag.Length) throw new IOException("云端版本认证字段无效。");
        }
        else if (commit.Encryption is not null || commit.Authentication is not null || commit.TransportCodec != ObjectTransport.Codec)
            throw new IOException("云端版本的加密状态相互矛盾。");
    }
}
