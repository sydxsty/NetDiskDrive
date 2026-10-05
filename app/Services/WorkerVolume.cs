using System.Text.Json;
using OverlayDisk.Cloud.Sync;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Worker;

namespace OverlayDisk.Services;

internal sealed class WorkerVolume(PrivilegedWorkerClient worker, string id, int objectSizeBytes) : ICloudVolume
{
    public string Id => id;
    public int ObjectSizeBytes { get; } = CloudObjectGeometry.Validate(objectSizeBytes);
    public async Task<JsonElement> ControlAsync(object request, CancellationToken cancellationToken)
    {
        var node = JsonSerializer.SerializeToElement(request);
        var cmd = node.GetProperty("cmd").GetString()!;
        string method = cmd switch
        {
            "cloud.list" => "cloud.objects", "cloud.receipt" => "cloud.receipts",
            "cloud.pause" => "cloud.abandon", "cloud.published_objects" => "cloud.commitBytes", _ => cmd
        };
        var args = node.EnumerateObject().Where(p => p.Name != "cmd").ToDictionary(p => p.Name, p => (object?)p.Value.Clone()); args["id"] = id;
        var result = await worker.InvokeAsync(method, JsonSerializer.SerializeToElement(args), cancellationToken);
        return result is JsonElement element ? element : JsonSerializer.SerializeToElement(result, SettingsStorage.Json);
    }
    public Task<byte[]> ReadObjectAsync(string jobId, string objectId, CancellationToken cancellationToken)
        => worker.ReadExportAsync(id, jobId, objectId, cancellationToken, ObjectSizeBytes);
}
