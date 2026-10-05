using OverlayDisk.Cloud.Sync;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Worker;

namespace OverlayDisk.Services;

/// <summary>Validates the scope of a native object request without network access or mutable settings.
/// This is not an authentication boundary for the digest: native must derive ObjectId/Sha256
/// from its authenticated fixed root, and both download verification and native AEAD remain required.</summary>
internal static class LazySourcePolicy
{
    internal static void ValidateRequest(RestoreRecord record, LazyObjectRequest request, string? currentAccountId)
    {
        if (record is null || request is null || !record.Lazy || !record.Begun || record.ContainerDeleted || record.Commit is null)
            throw new IOException("此磁盘没有可用的按需云端来源记录。");
        if (!Identity(request.DiskId) || request.DiskId != record.LocalDiskId &&
            (record.Complete || request.DiskId != record.WorkerId))
            throw new IOException("云端对象读取请求不属于此本地磁盘。");
        var commit = record.Commit;
        if (commit.FormatVersion != 4 || commit.RootSlot != 0 || commit.TransportCodec != ObjectTransport.Codec || !Identity(commit.VolumeId) || !Identity(commit.RootObjectId) ||
            !Digest(commit.RootSha256) || (commit.CapacityBytes < 64UL * 1024 * 1024 || commit.CapacityBytes > CloudRepository.MaximumCapacityBytes) || commit.CapacityBytes % 512 != 0 ||
            string.IsNullOrEmpty(record.RemoteRoot) || !CloudRepository.IsRootForVolume(record.RemoteRoot, commit.VolumeId) ||
            !Identity(record.Id) || record.ReaderPin != record.RemoteRoot + "/readers/" + record.Id + ".json")
            throw new IOException("按需磁盘的云端来源、版本或持久读取引用无效。");
        if (!CloudObjectGeometry.IsSupported(commit.ObjectSizeBytes) || request.Length != commit.ObjectSizeBytes || !Identity(request.ObjectId) || !Digest(request.Sha256))
            throw new IOException("云端按需读取的对象标识、长度或摘要无效。");
        if (string.IsNullOrWhiteSpace(record.AccountId) || string.IsNullOrWhiteSpace(currentAccountId) ||
            !string.Equals(record.AccountId, currentAccountId, StringComparison.Ordinal))
            throw new IOException("请登录此磁盘所属的百度网盘账户，以访问未缓存内容。");
    }

    private static bool Identity(string? value) => Guid.TryParseExact(value, "D", out _) || Guid.TryParseExact(value, "N", out _);
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
