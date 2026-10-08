using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using OverlayDisk.Worker;

namespace OverlayDisk;

internal static class HydrationTransportSelfTests
{
    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        var checks = new List<string>();
        byte[] payload = new byte[4 * 1024 * 1024]; new Random(541).NextBytes(payload);
        string hash = Convert.ToHexString(SHA256.HashData(payload)), disk = Guid.NewGuid().ToString();
        string one = Guid.NewGuid().ToString(), two = Guid.NewGuid().ToString();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int fetched = 0;
        using (var service = new DiskHydrationService(disk, async (_, ct) =>
        { Interlocked.Increment(ref fetched); entered.TrySetResult(); await release.Task.WaitAsync(ct); return payload.ToArray(); }, EmptyNeeds, (_, _) => { }))
        {
            var first = service.GetAsync(one, hash); await entered.Task.WaitAsync(timeout.Token);
            var second = service.GetAsync(one, hash);
            Check(ReferenceEquals(first, second) && fetched == 1, "same-object requests were not merged");
            release.TrySetResult(); byte[] firstBytes = await first, secondBytes = await second;
            Check(firstBytes.AsSpan().SequenceEqual(secondBytes), "shared download bytes changed");
            Check((await service.GetAsync(one, hash)).AsSpan().SequenceEqual(payload) && fetched == 1, "small completed-object cache missed");
        }
        checks.Add("Lazy hydration merges concurrent object requests and reuses bounded completed buffers.");

        var prefetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var service = new DiskHydrationService(disk, async (request, ct) =>
        {
            if (request.Prefetch) { prefetched.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); } finally { cancelled.TrySetResult(); } }
            return payload.ToArray();
        }, EmptyNeeds, (_, _) => { }))
        {
            var speculative = service.GetAsync(one, hash, true); await prefetched.Task.WaitAsync(timeout.Token);
            Check((await service.GetAsync(two, hash).WaitAsync(timeout.Token)).Length == payload.Length, "foreground did not complete");
            await cancelled.Task.WaitAsync(timeout.Token); await RejectAsync(speculative);
        }
        checks.Add("A new demand cancels unrelated active prefetch and releases its transfer slot.");

        var seekStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seekCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int hints = 0, imports = 0;
        using (var service = new DiskHydrationService(disk, async (_, ct) =>
        { seekStarted.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); } finally { seekCancelled.TrySetResult(); } return payload; },
        (offset, length, limit) => { Check(offset == 131072 && length <= 8 * 1024 * 1024 && limit == 2, "prefetch guessed physical object adjacency"); hints++; return JsonSerializer.SerializeToElement(new { items = new[] { new { id = one, sha256 = hash } } }); },
        (_, _) => imports++))
        {
            service.AfterRead(0, 65536, 16UL << 20); Check(hints == 0, "first read initiated prefetch");
            service.AfterRead(65536, 65536, 16UL << 20); await seekStarted.Task.WaitAsync(timeout.Token);
            service.AfterRead(8UL << 20, 65536, 16UL << 20); await seekCancelled.Task.WaitAsync(timeout.Token);
        }
        Check(hints == 1 && imports == 0, "cancelled seek prefetch was imported");
        checks.Add("Sequential successful logical reads plan at most the configured two object lengths; a seek cancels speculation before import.");

        int attempt = 0;
        using (var service = new DiskHydrationService(disk, (_, _) => Task.FromResult(++attempt == 1 ? new byte[4096] : payload.ToArray()), EmptyNeeds, (_, _) => { }))
        {
            await RejectAsync(service.GetAsync(one, hash));
            Check((await service.GetAsync(one, hash)).Length == payload.Length, "failed object cannot be retried");
        }
        checks.Add("Truncated objects fail explicitly and a later request can retry; missing data is never reported as zeros.");

        var control = await PairAsync(timeout.Token); var bulk = await PairAsync(timeout.Token); var hydrate = await PairAsync(timeout.Token);
        using var controlServer = control.Server; using var controlClient = control.Client;
        using var bulkServer = bulk.Server; using var bulkClient = bulk.Client;
        using var hydrateServer = hydrate.Server; using var hydrateClient = hydrate.Client;
        string token = new('D', 64);
        var dispatcher = new FakeDispatcher(); var session = new WorkerServerSession(dispatcher, token);
        var serving = session.ServeAsync(controlClient, bulkClient);
        var networkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var networkCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new LazyObjectServer(hydrateServer, token, async (request, ct) =>
        {
            Check(request.ObjectKind == "metadata" && request.Reason == (request.ObjectId == one ? "prefetch" : "sync"),
                "download context was lost across the reverse binary channel");
            if (request.ObjectId == one)
            { networkStarted.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); } finally { networkCancelled.TrySetResult(); } }
            return payload.ToArray();
        });
        using var rpc = new WorkerConnection(controlServer, token, false);
        using var binary = new WorkerConnection(bulkServer, token, true);
        using var objects = new WorkerConnection(hydrateClient, token, true);
        var request = new LazyObjectRequest(disk, one, hash, payload.Length, true) { ObjectKind = "metadata", Reason = "prefetch" };
        Task<WorkerReply> slow = objects.CallAsync("hydrate.read", JsonSerializer.SerializeToElement(request, WorkerProtocol.JsonOptions), ReadOnlyMemory<byte>.Empty, timeout.Token);
        await networkStarted.Task.WaitAsync(timeout.Token);
        var state = await rpc.CallAsync("state", default, ReadOnlyMemory<byte>.Empty, timeout.Token).WaitAsync(TimeSpan.FromSeconds(2));
        Check(state.Header.GetProperty("data").GetProperty("available").GetBoolean() && !slow.IsCompleted, "download blocked control query");
        await objects.CallAsync("hydrate.cancel", JsonSerializer.SerializeToElement(new { diskId = disk, objectId = one }), ReadOnlyMemory<byte>.Empty, timeout.Token);
        await networkCancelled.Task.WaitAsync(timeout.Token); await RejectAsync(slow);
        var good = await objects.CallAsync("hydrate.read", JsonSerializer.SerializeToElement(request with { ObjectId = two, Prefetch = false, Reason = "sync" }, WorkerProtocol.JsonOptions), ReadOnlyMemory<byte>.Empty, timeout.Token);
        Check(good.Bytes.AsSpan().SequenceEqual(payload), "reverse channel corrupted 4 MiB object");
        await rpc.CallAsync("$shutdown", default, ReadOnlyMemory<byte>.Empty, timeout.Token); await serving.WaitAsync(timeout.Token);
        Check(dispatcher.Stopped, "control shutdown did not finish");
        checks.Add("Authenticated reverse binary reads preserve 4 MiB objects, propagate cancellation and leave control queries/shutdown available during a stalled download.");
        return checks;
    }
    private static JsonElement EmptyNeeds(ulong _, uint __, int ___) => JsonSerializer.SerializeToElement(new { items = Array.Empty<object>() });
    private static void Check(bool condition, string message) { if (!condition) throw new IOException("Hydration self-test: " + message); }
    private static async Task RejectAsync(Task task)
    {
        bool rejected = false;
        try { await task; } catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException) { rejected = true; }
        Check(rejected, "expected failure was reported successful");
    }
    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> PairAsync(CancellationToken ct)
    {
        string name = "OverlayDisk.LazyFixture." + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var wait = server.WaitForConnectionAsync(ct); await client.ConnectAsync(ct); await wait;
        return (server, client);
    }
    private sealed class FakeDispatcher : IWorkerDispatcher
    {
        internal bool Stopped;
        public Task<object?> InvokeAsync(string method, JsonElement args, CancellationToken ct) => Task.FromResult<object?>(new { available = true });
        public Task ShutdownAsync(CancellationToken ct) { Stopped = true; return Task.CompletedTask; }
    }
}
