using System.Text.Json;
using OverlayDisk.Cloud.Sync;
using OverlayDisk.Worker;

namespace OverlayDisk.Services;

// The worker obtains this descriptor from the authenticated object table, not
// from a filename, a GUI argument, or a mutable assumption about the latest root.
internal static class CacheSourcePolicy
{
    private static string Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    internal static string ValidateRequest(JsonElement source, LazyObjectRequest request, string? accountId)
    {
        request.Validate();
        if (Text(source, "object_id") != request.ObjectId || !Text(source, "sha256").Equals(request.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !source.TryGetProperty("length", out var length) || !length.TryGetInt64(out var bytes) || bytes != request.Length ||
            !source.TryGetProperty("backing", out var backing) || backing.ValueKind != JsonValueKind.Object)
            throw new IOException("缓存对象的身份、摘要或来源记录不匹配。");
        string kind = Text(source, "source");
        bool own = kind == "own";
        if (!own && kind != "origin") throw new IOException("缓存对象缺少可验证的云端来源。");
        string provider = Text(backing, "backend_id"), root = Text(backing, "remote_root"),
            volume = Text(backing, "volume_id"), pin = Text(backing, "reader_pin");
        if (provider != "baidu-private-web" || !Guid.TryParse(volume, out _) || !CloudRepository.IsRootForVolume(root, volume) ||
            !pin.StartsWith(root + "/readers/", StringComparison.Ordinal) || !pin.EndsWith(".json", StringComparison.Ordinal) ||
            pin[(root.Length + 9)..].Contains('/') || own && volume != request.DiskId)
            throw new IOException("缓存对象的账户目录或持久读取引用无效。");
        if (string.IsNullOrWhiteSpace(accountId) || Text(backing, "account_id") != accountId)
            throw new IOException("请登录这块磁盘所属的百度网盘账户，以访问未缓存内容。");
        return root;
    }
}
