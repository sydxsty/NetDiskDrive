using System.Security.Cryptography;
using System.Text.Json;

namespace OverlayDisk.Worker;

/// <summary>A connection owns framing, not the lifetime of accepted disk mutations.</summary>
internal sealed class WorkerServerSession(IWorkerDispatcher dispatcher, string token)
{
    private readonly object gate = new();
    private readonly HashSet<Task> operations = new();
    private bool stopping;
    private int active;
    private TaskCompletionSource idle = Completed();
    internal bool ShutdownCompleted { get; private set; }
    private static TaskCompletionSource Completed() { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }

    internal async Task ServeAsync(Stream control, Stream bulk, CancellationToken connectionCancellation = default)
    {
        using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation);
        var controlTask = ServeChannelAsync(control, false, disconnected);
        var bulkTask = ServeChannelAsync(bulk, true, disconnected);
        await Task.WhenAny(controlTask, bulkTask).ConfigureAwait(false);
        disconnected.Cancel(); control.Dispose(); bulk.Dispose();
        try { await Task.WhenAll(controlTask, bulkTask).ConfigureAwait(false); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        Task[] remaining; lock (gate) remaining = operations.ToArray();
        try { await Task.WhenAll(remaining).ConfigureAwait(false); } catch { /* Replies may be lost; completed mutations are never replayed. */ }
    }
    private async Task ServeChannelAsync(Stream stream, bool binary, CancellationTokenSource disconnected)
    {
        using var writes = new SemaphoreSlim(1, 1);
        using var slots = new SemaphoreSlim(binary ? 4 : 64);
        try
        {
            while (!disconnected.IsCancellationRequested)
            {
                var packet = await WorkerProtocol.ReadPacketAsync(stream, binary, disconnected.Token).ConfigureAwait(false);
                if (!WorkerProtocol.IsTokenValid(token, packet.Header.GetProperty("token").GetString())) throw new UnauthorizedAccessException("会话认证失败。");
                long requestId = packet.Header.GetProperty("requestId").GetInt64();
                string method = packet.Header.GetProperty("method").GetString() ?? "";
                JsonElement args = packet.Header.TryGetProperty("args", out var a) ? a.Clone() : default;
                if (method == "$shutdown" && !binary)
                {
                    Task wait; lock (gate) { stopping = true; wait = idle.Task; }
                    try
                    {
                        await wait.ConfigureAwait(false);
                        await dispatcher.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                        await ReplyAsync(new { requestId, ok = true, binaryLength = 0, data = (object?)null }, Array.Empty<byte>()).ConfigureAwait(false);
                        ShutdownCompleted = true; return;
                    }
                    catch (Exception error)
                    {
                        lock (gate) stopping = false;
                        await ReplyAsync(new { requestId, ok = false, binaryLength = 0, error = error.Message }, Array.Empty<byte>()).ConfigureAwait(false);
                    }
                    continue;
                }
                await slots.WaitAsync(disconnected.Token).ConfigureAwait(false);
                bool admitted;
                lock (gate)
                {
                    admitted = !stopping;
                    if (admitted && active++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                if (!admitted)
                {
                    slots.Release(); CryptographicOperations.ZeroMemory(packet.Bytes);
                    await ReplyAsync(new { requestId, ok = false, binaryLength = 0, error = "磁盘服务正在退出。" }, Array.Empty<byte>()).ConfigureAwait(false); continue;
                }
                var task = Task.Run(async () =>
                {
                    byte[] responseBytes = Array.Empty<byte>();
                    try
                    {
                        object? result;
                        if (binary)
                        {
                            if (method is not ("export.read" or "restore.accept" or "restore.begin" or "replica.stage") || dispatcher is not IWorkerBulkDispatcher bulkDispatcher)
                                throw new InvalidOperationException("不支持的对象传输操作。");
                            var response = await bulkDispatcher.InvokeBulkAsync(method, args, packet.Bytes, CancellationToken.None).ConfigureAwait(false);
                            result = response.Data; responseBytes = response.Bytes;
                        }
                        else
                        {
                            WorkerProtocol.ValidateMethod(method);
                            result = await dispatcher.InvokeAsync(method, args, CancellationToken.None).ConfigureAwait(false);
                        }
                        await ReplyAsync(new { requestId, ok = true, binaryLength = responseBytes.Length, data = result }, responseBytes).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        try { await ReplyAsync(new { requestId, ok = false, binaryLength = 0, error = error.Message }, Array.Empty<byte>()).ConfigureAwait(false); }
                        catch { disconnected.Cancel(); }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(packet.Bytes); CryptographicOperations.ZeroMemory(responseBytes);
                        lock (gate) { if (--active == 0) idle.TrySetResult(); }
                        slots.Release();
                    }
                });
                lock (gate) operations.Add(task);
                _ = task.ContinueWith(completed => { lock (gate) operations.Remove(completed); }, TaskScheduler.Default);
            }
        }
        finally
        {
            disconnected.Cancel();
            Task[] remaining; lock (gate) remaining = operations.ToArray();
            // Keep frame locks and slot semaphores alive until every accepted operation leaves.
            try { await Task.WhenAll(remaining).ConfigureAwait(false); } catch { }
        }
        async Task ReplyAsync(object header, byte[] bytes)
        {
            await writes.WaitAsync(disconnected.Token).ConfigureAwait(false);
            try { await WorkerProtocol.WritePacketAsync(stream, header, bytes, disconnected.Token).ConfigureAwait(false); }
            finally { writes.Release(); }
        }
    }
}
