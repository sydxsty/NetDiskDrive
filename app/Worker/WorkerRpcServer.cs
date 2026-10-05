using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

namespace OverlayDisk.Worker;

public static class WorkerRpcServer
{
    public static async Task<int> RunAsync(string[] args, IWorkerDispatcher dispatcher)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("磁盘服务需要管理员权限。");
        if (args.Length != 5 || args[0] != "--worker" || args[1] != "--pipe" || args[3] != "--parent" || !int.TryParse(args[4], out int parentId) || parentId <= 0)
            throw new ArgumentException("磁盘服务启动参数无效。");
        string pipeName = args[2];
        if (!pipeName.StartsWith("OverlayDisk.v4.", StringComparison.Ordinal) || pipeName.Length != "OverlayDisk.v4.".Length + 48 || pipeName["OverlayDisk.v4.".Length..].Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("磁盘服务会话名称无效。");
        using var parent = Process.GetProcessById(parentId);
        string? sessionToken = null;
        while (!parent.HasExited)
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                if (WorkerProtocol.ServerProcessId(pipe) != (uint)parentId) throw new UnauthorizedAccessException("主界面进程身份不匹配。");
                using var hello = await WorkerProtocol.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
                var intro = hello.RootElement;
                string token = intro.GetProperty("token").GetString() ?? "";
                if (intro.GetProperty("kind").GetString() != "hello" || intro.GetProperty("parent").GetInt32() != parentId || token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c))
                    || (sessionToken is not null && !WorkerProtocol.IsTokenValid(sessionToken, token)))
                    throw new UnauthorizedAccessException("主界面身份认证失败。");
                sessionToken ??= token;
                await WorkerProtocol.WriteAsync(pipe, new { kind = "ready", token }, timeout.Token).ConfigureAwait(false);
                using var bulk = new NamedPipeClientStream(".", pipeName + ".bulk", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                await bulk.ConnectAsync(timeout.Token).ConfigureAwait(false);
                if (WorkerProtocol.ServerProcessId(bulk) != (uint)parentId) throw new UnauthorizedAccessException("对象通道主进程身份不匹配。");
                using var bulkHello = await WorkerProtocol.ReadAsync(bulk, timeout.Token).ConfigureAwait(false);
                if (bulkHello.RootElement.GetProperty("kind").GetString() != "bulk" || bulkHello.RootElement.GetProperty("parent").GetInt32() != parentId
                    || !WorkerProtocol.IsTokenValid(sessionToken, bulkHello.RootElement.GetProperty("token").GetString())) throw new UnauthorizedAccessException("对象通道认证失败。");
                await WorkerProtocol.WriteAsync(bulk, new { kind = "ready", token }, timeout.Token).ConfigureAwait(false);
                using var hydration = new NamedPipeClientStream(".", pipeName + ".hydrate", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                await hydration.ConnectAsync(timeout.Token).ConfigureAwait(false);
                if (WorkerProtocol.ServerProcessId(hydration) != (uint)parentId) throw new UnauthorizedAccessException("按需读取通道主进程身份不匹配。");
                using var hydrationHello = await WorkerProtocol.ReadAsync(hydration, timeout.Token).ConfigureAwait(false);
                if (hydrationHello.RootElement.GetProperty("kind").GetString() != "hydrate" || hydrationHello.RootElement.GetProperty("parent").GetInt32() != parentId
                    || !WorkerProtocol.IsTokenValid(sessionToken, hydrationHello.RootElement.GetProperty("token").GetString())) throw new UnauthorizedAccessException("按需读取通道认证失败。");
                await WorkerProtocol.WriteAsync(hydration, new { kind = "ready", token }, timeout.Token).ConfigureAwait(false);
                using var connectionLost = new CancellationTokenSource();
                using var objects = new WorkerConnection(hydration, token, true, _ => connectionLost.Cancel());
                var objectDispatcher = dispatcher as IWorkerObjectProviderDispatcher;
                objectDispatcher?.SetObjectProvider(async (request, cancellation) =>
                {
                    request.Validate();
                    using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellation, connectionLost.Token);
                    bound.CancelAfter(TimeSpan.FromSeconds(request.Prefetch ? 30 : 55));
                    var reading = objects.CallAsync("hydrate.read", System.Text.Json.JsonSerializer.SerializeToElement(request, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), ReadOnlyMemory<byte>.Empty, bound.Token);
                    try
                    {
                        var reply = await reading.WaitAsync(bound.Token).ConfigureAwait(false);
                        if (reply.Bytes.Length != request.Length) throw new InvalidDataException("按需读取对象长度无效。");
                        return reply.Bytes;
                    }
                    catch (OperationCanceledException)
                    {
                        // A disk's safe close cancels its own prefetch without interrupting
                        // the control-channel shutdown reply. Late immutable read responses
                        // are still consumed and bounded by WorkerConnection's 64 slots.
                        _ = reading.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        if (!cancellation.IsCancellationRequested) objects.Dispose();
                        else if (!connectionLost.IsCancellationRequested)
                        {
                            try
                            {
                                await objects.CallAsync("hydrate.cancel", System.Text.Json.JsonSerializer.SerializeToElement(new { diskId = request.DiskId, objectId = request.ObjectId }), ReadOnlyMemory<byte>.Empty, connectionLost.Token).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                            }
                            catch { /* Per-object cancellation must never interrupt safe shutdown. */ }
                        }
                        throw new IOException("云端对象读取已取消、超时或连接中断。");
                    }
                });
                var session = new WorkerServerSession(dispatcher, token);
                try { await session.ServeAsync(pipe, bulk, connectionLost.Token).ConfigureAwait(false); }
                finally { objectDispatcher?.SetObjectProvider(null); }
                if (session.ShutdownCompleted) return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
            {
                pipe.Dispose();
                // Preserve the worker and mounted volumes while its original parent is alive.
                // Only that process, authenticated with the same token, can reconnect; never
                // replay a request whose reply may have been lost.
                if (!parent.HasExited) await Task.Delay(500).ConfigureAwait(false);
            }
        }
        // A dead GUI must not cause abrupt disk removal. Retry the regular locked-volume
        // shutdown path while continuing to service Windows I/O if a volume is still busy.
        while (true)
        {
            try { await dispatcher.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); return 0; }
            catch { await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        }
    }
}
