using System.Text.Json;

namespace OverlayDisk.Worker;

public interface IWorkerDispatcher
{
    Task<object?> InvokeAsync(string method, JsonElement args, CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);
}

public interface IWorkerBulkDispatcher
{
    Task<WorkerBulkResult> InvokeBulkAsync(string method, JsonElement args, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}
public sealed record WorkerBulkResult(object? Data, byte[] Bytes);
