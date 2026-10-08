namespace OverlayDisk.Cloud.Sync;

/// <summary>The budget belongs to one disk's preparation task, not the shared uploader.</summary>
public static class SyncPreparationLimits
{
    public const int DefaultCacheMiB = 64;
    public const int MinimumCacheMiB = 16;
    public const int MaximumCacheMiB = 1024;

    public static int ValidateCacheMiB(int value)
        => value is >= MinimumCacheMiB and <= MaximumCacheMiB ? value
            : throw new IOException("同步整理缓存应为 16–1024 MiB 的整数，每个正在整理的磁盘分别使用该上限。");
}
