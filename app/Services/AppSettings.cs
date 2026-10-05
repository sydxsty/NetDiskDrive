using System.Text.Json;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

namespace OverlayDisk.Services;

public sealed class AppSettings
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    public int SyncIntervalSeconds { get; set; } = 60;
    public int MaxParallelTransfers { get; set; } = 2;
    public bool SyncOnExit { get; set; } = true;
    public PrefetchSettings Prefetch { get; set; } = new();
    public uint DefaultObjectSizeBytes { get; set; } = 4 * 1024 * 1024;
    public double BaiduRequestsPerSecond { get; set; } = 2;
    public int BaiduMaximumConcurrentRequests { get; set; } = 2;
    public int DefaultCapacityGiB { get; set; } = 64;
    public string? DefaultDirectory { get; set; }
    public CloudAccountInfo? AccountHint { get; set; }
    public Dictionary<string, CloudBinding> Bindings { get; set; } = new();
    public HashSet<string> PendingDisks { get; set; } = new();
    public HashSet<string> PausedDisks { get; set; } = new();
    public Dictionary<string, DateTimeOffset> LastSuccess { get; set; } = new();
    public Dictionary<string, RestoreRecord> Restores { get; set; } = new();
    public Dictionary<string, LocalCacheSettings> LocalCaches { get; set; } = new();
    public Dictionary<string, CacheSourceRecord> CacheSources { get; set; } = new();
}
public sealed record LocalCacheSettings(ulong LimitBytes = 0, string Policy = "lru");
public sealed class CacheSourceRecord
{
    public CloudBinding Binding { get; set; } = null!;
    public string ReaderPin { get; set; } = "";
    public string ContainerPath { get; set; } = "";
    public bool ContainerDeleted { get; set; }
}
public sealed class RestoreRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? WorkerId { get; set; }
    public string AccountId { get; set; } = "";
    public string RemoteRoot { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ReaderPin { get; set; }
    public RemoteCommit Commit { get; set; } = null!;
    public bool Begun { get; set; }
    public bool Complete { get; set; }
    // Completed lazy imports keep their source pin until explicitly detached or deleted.
    public bool Lazy { get; set; }
    public string? LocalDiskId { get; set; }
    public bool ContainerDeleted { get; set; }
    public string Mode { get; set; } = "copy";
    public bool SourcePinReplaced { get; set; }
    public bool OriginalConfirmed { get; set; }
    public CloudBinding? OriginalBinding { get; set; }
}
internal static class SettingsStorage
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverlayDisk", "current");
    // The login vault and WebView profile are stable account storage, independent of container schemas.
    internal static string SessionPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverlayDisk", "v4", "baidu-session.dpapi");
    internal static AppSettings Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? throw new IOException("设置文件格式无效，请保留原文件检查。");
    }
    internal static void Save(AppSettings settings)
    {
        string path = Path.Combine(DirectoryPath, "settings.json");
        string temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, settings, Json); file.Flush(true); }
        File.Move(temporary, path, true);
    }
}
