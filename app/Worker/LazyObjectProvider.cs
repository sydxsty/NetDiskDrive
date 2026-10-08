using System.Collections.Concurrent;
using OverlayDisk.Cloud.Contracts;
using System.Security.Cryptography;
using System.Text.Json;

namespace OverlayDisk.Worker;

public sealed record LazyObjectRequest(string DiskId, string ObjectId, string Sha256, int Length = 4 * 1024 * 1024, bool Prefetch = false)
{
    public string ObjectKind { get; init; } = "unknown";
    public string Reason { get; init; } = "read";
    internal TimeSpan TransferTimeout => TimeSpan.FromSeconds((Prefetch ? 25 : 50) * (Length / CloudObjectGeometry.DefaultSize));
    internal void Validate()
    {
        if (!Guid.TryParse(DiskId, out _) || !Guid.TryParse(ObjectId, out _) || !CloudObjectGeometry.IsSupported(Length)
            || Sha256.Length != 64 || Sha256.Any(c => !Uri.IsHexDigit(c))
            || ObjectKind is not ("unknown" or "data" or "metadata")
            || Reason is not ("read" or "write" or "sync" or "prefetch" or "reclaim" or "replica" or "copy_publish"))
            throw new InvalidDataException("云端对象读取身份或长度无效。");
    }
}

public interface IWorkerObjectProviderDispatcher
{
    void SetObjectProvider(Func<LazyObjectRequest, CancellationToken, Task<byte[]>>? provider);
}

/// <summary>The unelevated GUI serves only immutable object reads on a separately authenticated pipe.</summary>
internal sealed class LazyObjectServer : IDisposable
{
    private readonly Stream stream;
    private readonly string token;
    private readonly Func<LazyObjectRequest, CancellationToken, Task<byte[]>> provider;
    private readonly Action<LazyObjectServer>? disconnected;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writes = new(1, 1), slots = new(4, 4);
    private readonly ConcurrentDictionary<long, Task> requests = new();
    private readonly ConcurrentDictionary<long, (LazyObjectRequest Request, CancellationTokenSource Cancellation)> activeReads = new();
    private readonly Task serving;
    private int stopped;
    internal bool IsConnected => Volatile.Read(ref stopped) == 0;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal LazyObjectServer(Stream stream, string token, Func<LazyObjectRequest, CancellationToken, Task<byte[]>> provider, Action<LazyObjectServer>? disconnected = null)
    {
        this.stream = stream; this.token = token; this.provider = provider; this.disconnected = disconnected;
        serving = Task.Run(ServeAsync);
    }
    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                var packet = await WorkerProtocol.ReadPacketAsync(stream, true, lifetime.Token).ConfigureAwait(false);
                if (!WorkerProtocol.IsTokenValid(token, packet.Header.GetProperty("token").GetString()) || packet.Bytes.Length != 0)
                    throw new UnauthorizedAccessException("按需读取通道只允许已认证的对象读取。");
                long id = packet.Header.GetProperty("requestId").GetInt64();
                string? method = packet.Header.GetProperty("method").GetString();
                if (method == "hydrate.cancel")
                {
                    var args = packet.Header.GetProperty("args");
                    string? disk = args.GetProperty("diskId").GetString(), objectId = args.GetProperty("objectId").GetString();
                    if (!Guid.TryParse(disk, out _) || !Guid.TryParse(objectId, out _)) throw new InvalidDataException("取消对象身份无效。");
                    foreach (var entry in activeReads.Values)
                        if (entry.Request.DiskId == disk && entry.Request.ObjectId == objectId)
                            try { entry.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
                    await ReplyAsync(new { requestId = id, ok = true, binaryLength = 0, data = (object?)null }, Array.Empty<byte>()).ConfigureAwait(false);
                    continue;
                }
                if (method != "hydrate.read") throw new UnauthorizedAccessException("不支持的按需读取操作。");
                var request = packet.Header.GetProperty("args").Deserialize<LazyObjectRequest>(Json)
                    ?? throw new InvalidDataException("缺少按需读取参数。");
                request.Validate();
                if (requests.Count >= 32)
                {
                    await ReplyAsync(new { requestId = id, ok = false, binaryLength = 0, error = "按需读取队列已满，请稍后重试。" }, Array.Empty<byte>()).ConfigureAwait(false);
                    continue;
                }
                if (!requests.TryAdd(id, Task.CompletedTask)) throw new InvalidDataException("重复按需读取请求标识。");
                var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(request.TransferTimeout);
                activeReads[id] = (request, timeout);
                var work = Task.Run(() => RespondAsync(id, request, timeout));
                requests[id] = work;
                _ = work.ContinueWith(_ => requests.TryRemove(id, out var ignored), TaskScheduler.Default);
            }
        }
        catch { Stop(); }
    }
    private async Task RespondAsync(long id, LazyObjectRequest request, CancellationTokenSource timeout)
    {
        byte[] bytes = Array.Empty<byte>();
        bool acquired = false;
        try
        {
            await slots.WaitAsync(timeout.Token).ConfigureAwait(false); acquired = true;
            bytes = await provider(request, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length != request.Length || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("云端对象长度或校验和不一致。");
            await ReplyAsync(new { requestId = id, ok = true, binaryLength = bytes.Length, data = (object?)null }, bytes).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try { await ReplyAsync(new { requestId = id, ok = false, binaryLength = 0, error = error is OperationCanceledException ? "云端对象读取超时或已取消，请稍后重试。" : error.Message }, Array.Empty<byte>()).ConfigureAwait(false); }
            catch { Stop(); }
        }
        finally { activeReads.TryRemove(id, out _); timeout.Dispose(); CryptographicOperations.ZeroMemory(bytes); if (acquired) slots.Release(); }
    }
    private async Task ReplyAsync(object header, byte[] bytes)
    {
        await writes.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { await WorkerProtocol.WritePacketAsync(stream, header, bytes, lifetime.Token).ConfigureAwait(false); }
        finally { writes.Release(); }
    }
    private void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        lifetime.Cancel(); stream.Dispose(); disconnected?.Invoke(this);
    }
    public void Dispose() => Stop();
}
