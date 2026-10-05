using System.Collections.Concurrent;
using System.Text.Json;

namespace OverlayDisk.Worker;

/// <summary>One response reader, independently correlated calls, and a lock only around frame writes.
/// Once a request has been sent its response is always consumed. A disconnected mutation is never replayed.</summary>
internal sealed class WorkerConnection : IDisposable
{
    private readonly Stream stream;
    private readonly string token;
    private readonly bool bulk;
    private readonly SemaphoreSlim writes = new(1, 1), capacity = new(64, 64);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<WorkerReply>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Action<WorkerConnection>? disconnected;
    private readonly Task reader;
    private long sequence;
    private int stopped;
    internal bool IsConnected => Volatile.Read(ref stopped) == 0;

    internal WorkerConnection(Stream stream, string token, bool bulk, Action<WorkerConnection>? disconnected = null)
    {
        this.stream = stream; this.token = token; this.bulk = bulk; this.disconnected = disconnected;
        reader = Task.Run(ReadLoopAsync);
    }
    internal async Task<WorkerReply> CallAsync(string method, JsonElement args, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (!bulk && bytes.Length != 0) throw new InvalidDataException("控制通道不能传输对象数据。");
        await capacity.WaitAsync(ct).ConfigureAwait(false);
        long id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<WorkerReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!IsConnected) throw Disconnected();
            if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("重复请求标识。");
            await writes.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!IsConnected) throw Disconnected();
                // Cancellation is respected before transmission. Never leave a partial frame,
                // and never abandon an accepted mutation merely because its caller cancels.
                await WorkerProtocol.WritePacketAsync(stream, new { requestId = id, method, args = args.ValueKind == JsonValueKind.Undefined ? (object?)null : args, token, binaryLength = bytes.Length }, bytes, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && IsConnected) { throw; }
            catch { Stop(); throw Disconnected(); }
            finally { writes.Release(); }
            var reply = await completion.Task.ConfigureAwait(false);
            if (!reply.Header.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException(reply.Header.GetProperty("error").GetString() ?? "磁盘服务操作失败。");
            return reply;
        }
        finally { pending.TryRemove(id, out _); capacity.Release(); }
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var reply = await WorkerProtocol.ReadPacketAsync(stream, bulk, lifetime.Token).ConfigureAwait(false);
                long id = reply.Header.GetProperty("requestId").GetInt64();
                if (!pending.TryRemove(id, out var completion)) throw new InvalidDataException("磁盘服务响应标识无效。");
                completion.TrySetResult(reply);
            }
        }
        catch { Stop(); }
    }
    private static IOException Disconnected() => new("磁盘服务连接已断开；未自动重试本次操作，请刷新状态后确认结果。");
    private void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        lifetime.Cancel(); stream.Dispose();
        foreach (var entry in pending) if (pending.TryRemove(entry.Key, out var completion)) completion.TrySetException(Disconnected());
        disconnected?.Invoke(this);
    }
    public void Dispose() => Stop();
}
internal sealed record WorkerReply(JsonElement Header, byte[] Bytes);
