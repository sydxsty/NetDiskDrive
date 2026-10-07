using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Worker;

/// <summary>Independent control and binary channels, authenticated to one elevated worker.</summary>
public sealed class PrivilegedWorkerClient : IAsyncDisposable
{
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private WorkerConnection? _control, _bulk;
    private LazyObjectServer? _hydration;
    private Process? _process;
    private string? _token, _pipeName;
    private bool _disposed;
    private volatile bool _shuttingDown;
    private volatile bool _configurationReady;
    private PrefetchSettings prefetch = new();
    public PrefetchSettings Prefetch
    {
        get => Volatile.Read(ref prefetch);
        set { value.Validate(); Volatile.Write(ref prefetch, value); }
    }
    public bool IsConnected => !_disposed && _configurationReady && _control is { IsConnected: true } && _bulk is { IsConnected: true } && _hydration is { IsConnected: true } && HasRunningWorker;
    public Func<LazyObjectRequest, CancellationToken, Task<byte[]>>? ObjectProvider { get; set; }
    public bool HasRunningWorker
    {
        get { try { return _process is { HasExited: false }; } catch (InvalidOperationException) { return false; } catch (System.ComponentModel.Win32Exception) { return false; } }
    }
    public event EventHandler? ConnectionLost;
    internal int? WorkerProcessId => _process?.Id;
    internal void DisconnectForSmokeTest() => DropConnection();
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        await connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); if (!IsConnected) { DropChannels(); await ConnectAsync(ct).ConfigureAwait(false); } }
        finally { connectionGate.Release(); }
    }
    public async Task<object?> InvokeAsync(string method, JsonElement args, CancellationToken cancellationToken = default)
    {
        WorkerProtocol.ValidateMethod(method);
        if (_shuttingDown) throw new IOException("磁盘服务正在退出。");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var channel = _control ?? throw new IOException("磁盘服务连接已断开；请刷新状态后重试。");
        var reply = await channel.CallAsync(method, args, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        return reply.Header.TryGetProperty("data", out var data) ? data.Clone() : null;
    }
    public async Task<byte[]> ReadExportAsync(string id, string jobId, string objectId, CancellationToken ct = default, int objectSizeBytes = CloudObjectGeometry.DefaultSize)
    {
        CloudObjectGeometry.Validate(objectSizeBytes);
        var reply = await BulkAsync("export.read", JsonSerializer.SerializeToElement(new { id, job_id = jobId, object_id = objectId }), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        if (reply.Bytes.Length != objectSizeBytes) throw new InvalidDataException("导出对象长度与磁盘不一致。");
        return reply.Bytes;
    }
    public async Task ConfigurePrefetchAsync(PrefetchSettings value, CancellationToken ct)
    {
        value.Validate();
        await connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Prefetch = value;
            // Saving preferences without an open disk must not start an elevated worker.
            if (IsConnected)
                await _control!.CallAsync("prefetch.configure", JsonSerializer.SerializeToElement(value, WorkerProtocol.JsonOptions), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        }
        finally { connectionGate.Release(); }
    }
    public async Task<object?> AcceptRestoreObjectAsync(string taskId, string objectId, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var reply = await BulkAsync("restore.accept", JsonSerializer.SerializeToElement(new { id = taskId, object_id = objectId }), bytes, ct).ConfigureAwait(false);
        return reply.Header.TryGetProperty("data", out var data) ? data.Clone() : null;
    }
    public async Task<object?> BeginRestoreAsync(JsonElement args, ReadOnlyMemory<byte> rootObject, CancellationToken ct = default)
    {
        var reply = await BulkAsync("restore.begin", args, rootObject, ct).ConfigureAwait(false);
        return reply.Header.TryGetProperty("data", out var data) ? data.Clone() : null;
    }
    public async Task<object?> StageReplicaAsync(JsonElement args, ReadOnlyMemory<byte> rootObject, CancellationToken ct = default)
    {
        var reply = await BulkAsync("replica.stage", args, rootObject, ct).ConfigureAwait(false);
        return reply.Header.TryGetProperty("data", out var data) ? data.Clone() : null;
    }
    private async Task<WorkerReply> BulkAsync(string method, JsonElement args, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (_shuttingDown) throw new IOException("磁盘服务正在退出。");
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var channel = _bulk ?? throw new IOException("对象传输连接已断开；请刷新状态后重试。");
        return await channel.CallAsync(method, args, bytes, ct).ConfigureAwait(false);
    }
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!HasRunningWorker) { Disconnect(false); return; }
        _shuttingDown = true;
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await (_control ?? throw new IOException("磁盘服务连接已断开，尚未完成退出。")).CallAsync("$shutdown", default, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            Disconnect(false);
        }
        finally { _shuttingDown = false; }
    }
    private async Task ConnectAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        bool reconnect = _process is { HasExited: false } && _pipeName is not null && _token is not null;
        if (!reconnect) Disconnect(false);
        string pipeName = _pipeName ?? ("OverlayDisk.v4." + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.FullControl, AccessControlType.Allow));
        // CurrentUserOnly also compares elevation on Windows; an explicit same-SID ACL is
        // required because this endpoint deliberately connects medium and high integrity.
        NamedPipeServerStream pipe;
        NamedPipeServerStream? bulkPipe = null;
        NamedPipeServerStream? hydrationPipe = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | (PipeOptions)0x00080000, 64 * 1024, 64 * 1024, security);
                break;
            }
            catch (IOException) when (reconnect && attempt < 30) { await Task.Delay(100, ct).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) when (reconnect && attempt < 30) { await Task.Delay(100, ct).ConfigureAwait(false); }
        }
        Process? process = reconnect ? _process : null;
        try
        {
            bulkPipe = NamedPipeServerStreamAcl.Create(pipeName + ".bulk", PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | (PipeOptions)0x00080000, 64 * 1024, 64 * 1024, security);
            hydrationPipe = NamedPipeServerStreamAcl.Create(pipeName + ".hydrate", PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | (PipeOptions)0x00080000, 64 * 1024, 64 * 1024, security);
            if (!reconnect)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                foreach (string argument in new[] { "--worker", "--pipe", pipeName, "--parent", Environment.ProcessId.ToString() }) start.ArgumentList.Add(argument);
                // ShellExecute/UAC must not block the UI thread; this client never has a UI affinity.
                process = await Task.Run(() => Process.Start(start) ?? throw new IOException("无法启动磁盘服务。"), ct).ConfigureAwait(false);
                _process = process; _pipeName = pipeName;
                _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (WorkerProtocol.ClientProcessId(pipe) != (uint)process!.Id) throw new UnauthorizedAccessException("磁盘服务进程身份不匹配。");
            string token = _token!;
            await WorkerProtocol.WriteAsync(pipe, new { kind = "hello", token, parent = Environment.ProcessId }, timeout.Token).ConfigureAwait(false);
            using var hello = await WorkerProtocol.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (hello.RootElement.GetProperty("kind").GetString() != "ready" || !WorkerProtocol.IsTokenValid(token, hello.RootElement.GetProperty("token").GetString()))
                throw new UnauthorizedAccessException("磁盘服务身份认证失败。");
            await bulkPipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (WorkerProtocol.ClientProcessId(bulkPipe) != (uint)process!.Id) throw new UnauthorizedAccessException("对象通道进程身份不匹配。");
            await WorkerProtocol.WriteAsync(bulkPipe, new { kind = "bulk", token, parent = Environment.ProcessId }, timeout.Token).ConfigureAwait(false);
            using var bulkHello = await WorkerProtocol.ReadAsync(bulkPipe, timeout.Token).ConfigureAwait(false);
            if (bulkHello.RootElement.GetProperty("kind").GetString() != "ready" || !WorkerProtocol.IsTokenValid(token, bulkHello.RootElement.GetProperty("token").GetString()))
                throw new UnauthorizedAccessException("对象通道身份认证失败。");
            await hydrationPipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (WorkerProtocol.ClientProcessId(hydrationPipe) != (uint)process!.Id) throw new UnauthorizedAccessException("按需读取通道进程身份不匹配。");
            await WorkerProtocol.WriteAsync(hydrationPipe, new { kind = "hydrate", token, parent = Environment.ProcessId }, timeout.Token).ConfigureAwait(false);
            using var hydrateHello = await WorkerProtocol.ReadAsync(hydrationPipe, timeout.Token).ConfigureAwait(false);
            if (hydrateHello.RootElement.GetProperty("kind").GetString() != "ready" || !WorkerProtocol.IsTokenValid(token, hydrateHello.RootElement.GetProperty("token").GetString()))
                throw new UnauthorizedAccessException("按需读取通道身份认证失败。");
            _hydration = new LazyObjectServer(hydrationPipe, token, (request, cancellation) =>
                (ObjectProvider ?? throw new IOException("尚未连接原云端账户，无法读取未缓存的对象。"))(request, cancellation), LostHydration);
            _control = new WorkerConnection(pipe, token, false, Lost);
            _bulk = new WorkerConnection(bulkPipe, token, true, Lost);
            _process = process; _token = token;
            await _control.CallAsync("prefetch.configure", JsonSerializer.SerializeToElement(Prefetch, WorkerProtocol.JsonOptions), ReadOnlyMemory<byte>.Empty, timeout.Token).ConfigureAwait(false);
            _configurationReady = true;
        }
        catch
        {
            pipe.Dispose(); bulkPipe?.Dispose(); hydrationPipe?.Dispose();
            // Keep the launched process identity and session token. A subsequent explicit
            // disk action reconnects to this worker; it never starts a duplicate worker.
            // Never kill a worker: it may still own a mounted volume. Its disconnect handler
            // attempts safe shutdown and remains alive if Windows refuses to release a volume.
            throw;
        }
    }

    private void Lost(WorkerConnection channel)
    {
        if (!ReferenceEquals(_control, channel) && !ReferenceEquals(_bulk, channel)) return;
        if (_shuttingDown && !ReferenceEquals(_control, channel)) return;
        DropConnection();
    }
    private void LostHydration(LazyObjectServer server)
    {
        if (ReferenceEquals(_hydration, server) && !_shuttingDown) DropConnection();
    }
    private void DropChannels()
    {
        _configurationReady = false;
        var control = Interlocked.Exchange(ref _control, null); var bulk = Interlocked.Exchange(ref _bulk, null);
        var hydration = Interlocked.Exchange(ref _hydration, null);
        control?.Dispose(); bulk?.Dispose(); hydration?.Dispose();
    }
    private void DropConnection() { DropChannels(); ConnectionLost?.Invoke(this, EventArgs.Empty); }
    private void Disconnect(bool notify)
    {
        DropChannels(); _process?.Dispose(); _process = null; _token = null; _pipeName = null;
        if (notify) ConnectionLost?.Invoke(this, EventArgs.Empty);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await ShutdownAsync().ConfigureAwait(false); _disposed = true;
    }
}
