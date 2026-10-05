using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OverlayDisk.Worker;

internal static class WorkerTransportSelfTests
{
    internal static async Task RunAsync()
    {
        foreach (int invalid in new[] { 0, -1, WorkerProtocol.MaximumFrameBytes + 1 })
        {
            byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, invalid);
            using var stream = new MemoryStream(bytes);
            try { using var unexpected = await WorkerProtocol.ReadAsync(stream, default); throw new Exception("Invalid frame accepted."); }
            catch (InvalidDataException) { }
        }
        string token = new('A', 64);
        if (!WorkerProtocol.IsTokenValid(token, token) || WorkerProtocol.IsTokenValid(token, new string('B', 64)) || WorkerProtocol.IsTokenValid(token, null))
            throw new Exception("Session token validation failed.");
        bool refused = false;
        try { WorkerProtocol.ValidateMethod("process.execute"); } catch (InvalidOperationException) { refused = true; }
        if (!refused) throw new Exception("Arbitrary worker method allowed.");

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        string name = "OverlayDisk.TransportSmoke." + Guid.NewGuid().ToString("N");
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (PipeOptions)0x00080000, 65536, 65536, security);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task wait = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token); await wait;
        if (WorkerProtocol.ClientProcessId(server) != Environment.ProcessId || WorkerProtocol.ServerProcessId(client) != Environment.ProcessId)
            throw new Exception("Named-pipe PID verification failed.");
        byte[] body = new byte[WorkerProtocol.MaximumBulkBytes]; Random.Shared.NextBytes(body);
        Task write = WorkerProtocol.WritePacketAsync(server, new { requestId = 7, token, method = "restore.accept", binaryLength = body.Length }, body, timeout.Token);
        var received = await WorkerProtocol.ReadPacketAsync(client, true, timeout.Token);
        await write;
        if (!received.Bytes.AsSpan().SequenceEqual(body) || received.Header.GetProperty("requestId").GetInt32() != 7)
            throw new Exception("16 MiB binary round trip failed.");
        if (!WorkerProtocol.IsTokenValid(token, received.Header.GetProperty("token").GetString())) throw new Exception("Frame buffer ownership failed.");
        await MultiplexingAsync();
    }
    private static async Task MultiplexingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var control = await PairAsync(timeout.Token); var bulk = await PairAsync(timeout.Token);
        using var controlServer = control.Server; using var controlClient = control.Client;
        using var bulkServer = bulk.Server; using var bulkClient = bulk.Client;
        string token = new('C', 64);
        var fake = new FakeDispatcher();
        var session = new WorkerServerSession(fake, token);
        Task serving = session.ServeAsync(controlClient, bulkClient);
        using var rpc = new WorkerConnection(controlServer, token, false);
        using var transfer = new WorkerConnection(bulkServer, token, true);
        var empty = System.Text.Json.JsonSerializer.SerializeToElement(new { });
        Task<WorkerReply> slow = rpc.CallAsync("cloud.prepare", empty, ReadOnlyMemory<byte>.Empty, timeout.Token);
        await fake.Started.Task.WaitAsync(timeout.Token);
        var query = await rpc.CallAsync("state", empty, ReadOnlyMemory<byte>.Empty, timeout.Token).WaitAsync(TimeSpan.FromSeconds(2));
        if (!query.Header.GetProperty("data").GetProperty("ready").GetBoolean() || slow.IsCompleted) throw new Exception("Query was serialized behind slow work.");
        var binary = await transfer.CallAsync("export.read", empty, ReadOnlyMemory<byte>.Empty, timeout.Token).WaitAsync(TimeSpan.FromSeconds(2));
        if (binary.Bytes.Length != WorkerProtocol.MaximumBulkBytes || binary.Bytes[123] != 71) throw new Exception("Independent binary channel failed.");
        await transfer.CallAsync("restore.accept", empty, binary.Bytes, timeout.Token);
        if (!fake.Accepted) throw new Exception("Binary restore payload was not preserved.");
        Task<WorkerReply> stop = rpc.CallAsync("$shutdown", empty, ReadOnlyMemory<byte>.Empty, timeout.Token);
        await Task.Delay(50, timeout.Token);
        if (stop.IsCompleted || fake.Stopped) throw new Exception("Shutdown bypassed accepted work.");
        fake.Release.TrySetResult(); await slow.WaitAsync(timeout.Token); await stop.WaitAsync(timeout.Token); await serving.WaitAsync(timeout.Token);
        if (!fake.Stopped || !session.ShutdownCompleted) throw new Exception("Safe session drain failed.");

        // A peer disconnect fails the outstanding mutation once; it is never replayed.
        var pair = await PairAsync(timeout.Token);
        using var abandonedServer = pair.Server; using var abandonedClient = pair.Client;
        using var connection = new WorkerConnection(abandonedServer, token, false);
        Task<WorkerReply> mutation = connection.CallAsync("snapshots.create", empty, ReadOnlyMemory<byte>.Empty, timeout.Token);
        _ = await WorkerProtocol.ReadPacketAsync(abandonedClient, false, timeout.Token);
        abandonedClient.Dispose();
        try { await mutation.WaitAsync(timeout.Token); throw new Exception("Lost mutation was reported as success."); }
        catch (IOException) { }
        if (connection.IsConnected) throw new Exception("Lost connection remains active.");
    }
    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> PairAsync(CancellationToken ct)
    {
        string name = "OverlayDisk.V4.Transport." + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var wait = server.WaitForConnectionAsync(ct); await client.ConnectAsync(ct); await wait;
        return (server, client);
    }
    private sealed class FakeDispatcher : IWorkerDispatcher, IWorkerBulkDispatcher
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Accepted, Stopped;
        public async Task<object?> InvokeAsync(string method, System.Text.Json.JsonElement args, CancellationToken ct)
        {
            if (method == "cloud.prepare") { Started.TrySetResult(); await Release.Task; return new { prepared = true }; }
            return new { ready = true };
        }
        public Task<WorkerBulkResult> InvokeBulkAsync(string method, System.Text.Json.JsonElement args, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            if (method == "export.read") { var result = new byte[WorkerProtocol.MaximumBulkBytes]; result[123] = 71; return Task.FromResult(new WorkerBulkResult(null, result)); }
            Accepted = bytes.Length == WorkerProtocol.MaximumBulkBytes && bytes.Span[123] == 71;
            return Task.FromResult(new WorkerBulkResult(new { ok = Accepted }, Array.Empty<byte>()));
        }
        public Task ShutdownAsync(CancellationToken ct) { Stopped = true; return Task.CompletedTask; }
    }
}
