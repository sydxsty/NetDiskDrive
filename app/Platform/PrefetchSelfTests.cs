using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk;

internal static class PrefetchSelfTests
{
    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        var checks = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string disk = Guid.NewGuid().ToString();
        foreach (int size in new[] { 8 << 20, 16 << 20 })
        {
            byte[] raw = new byte[size]; raw[raw.Length - 1] = 29;
            string hash = Convert.ToHexString(SHA256.HashData(raw));
            using var service = new DiskHydrationService(disk, (request, _) =>
            {
                Check(request.Length == size, "object geometry lost before download");
                return Task.FromResult(raw.ToArray());
            }, Empty, (_, _) => { }, (uint)size);
            Check((await service.GetAsync(Guid.NewGuid().ToString(), hash)).AsSpan().SequenceEqual(raw), "large object truncated or altered");
        }
        checks.Add("8/16 MiB demand reads preserve the exact per-volume object length and content.");

        byte[] payload = new byte[4 << 20]; payload[13] = 47;
        string digest = Convert.ToHexString(SHA256.HashData(payload));
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var requested = new ConcurrentBag<string>(); int hints = 0;
        using (var service = new DiskHydrationService(disk, (request, _) =>
        { requested.Add(request.ObjectId); return Task.FromResult(payload.ToArray()); },
        (offset, length, limit) =>
        {
            Check(offset == 131072 && length == 12 << 20 && limit == 3, "sequential prefetch ignored its configured window/count");
            Interlocked.Increment(ref hints);
            return JsonSerializer.SerializeToElement(new { items = ids.Select(id => new { id, sha256 = digest, length = payload.Length }), next_offset = (ulong?)null });
        }, (_, _) => { }, settings: new("sequential", 3)))
        {
            service.AfterRead(0, 65536, 64UL << 20); Check(hints == 0, "one isolated read triggered speculation");
            service.AfterRead(65536, 65536, 64UL << 20);
            await IdleAsync(service, timeout.Token);
            Check(requested.Count == 3 && requested.ToHashSet().SetEquals(ids), "prefetch guessed object adjacency or exceeded/missed the configured count");
            service.Configure(new("disabled", 3));
            service.AfterRead(131072, 65536, 64UL << 20); await IdleAsync(service, timeout.Token);
            Check(hints == 1, "disabled strategy kept looking up objects");
        }
        checks.Add("Configured sequential prefetch follows exactly three logical object descriptors; disabling stops mapping lookups and downloads.");

        foreach (int budget in new[] { 1, 2 })
        {
            int metadataImports = 0, dataImports = 0, queries = 0;
            var descriptors = new ConcurrentQueue<Worker.LazyObjectRequest>();
            using var service = new DiskHydrationService(disk, (request, _) =>
            {
                Check(request.Prefetch && request.Reason == "prefetch", "prefetch lost its download reason");
                descriptors.Enqueue(request); return Task.FromResult(payload.ToArray());
            }, (offset, length, limit) =>
            {
                Check(offset == 131072 && length == budget * payload.Length, "metadata retry moved outside its logical window");
                Interlocked.Increment(ref queries);
                bool index = Volatile.Read(ref metadataImports) == 0;
                Check(limit == (index ? budget : budget - 1), "metadata did not consume the shared prefetch budget");
                return JsonSerializer.SerializeToElement(new
                {
                    items = new[] { new { id = ids[index ? 0 : 1], sha256 = digest, length = payload.Length, kind = index ? "metadata" : "data" } },
                    waiting_for_index = index, next_offset = index ? (ulong?)offset : null
                });
            }, (id, _) => { if (id == ids[0]) Interlocked.Increment(ref metadataImports); else Interlocked.Increment(ref dataImports); },
            settings: new("sequential", budget));
            service.AfterRead(0, 65536, 64UL << 20); service.AfterRead(65536, 65536, 64UL << 20);
            await IdleAsync(service, timeout.Token);
            Check(queries == budget && descriptors.Count == budget && metadataImports == 1 && dataImports == budget - 1,
                "cold-index retry either stopped early or exceeded the combined object budget");
            Check(descriptors.First().ObjectKind == "metadata" && (budget == 1 || descriptors.Last().ObjectKind == "data"), "index/data classification lost");
            var state = JsonSerializer.SerializeToElement(service.GetState());
            Check(state.GetProperty("metadataDownloadedBytes").GetInt64() == payload.Length
                && state.GetProperty("dataDownloadedBytes").GetInt64() == (budget - 1L) * payload.Length, "separate canonical download counters disagree");
        }
        checks.Add("Cold metadata is prefetched and imported before retrying the same logical range; metadata and data share a strict object budget and distinct counters.");

        var limits = new ConcurrentQueue<int>();
        using (var service = new DiskHydrationService(disk, (_, _) => throw new IOException("empty hints must not download"),
        (_, length, limit) => { Check(length == (uint)limit * payload.Length, "adaptive window disagrees with object count"); limits.Enqueue(limit); return Empty(0, 0, 0); },
        (_, _) => { }, settings: new("adaptive", 4)))
        {
            service.AfterRead(0, 65536, 64UL << 20); service.AfterRead(65536, 65536, 64UL << 20);
            Check(limits.IsEmpty, "adaptive strategy did not wait for sustained sequential access");
            service.AfterRead(131072, 65536, 64UL << 20); await IdleAsync(service, timeout.Token);
            service.AfterRead(196608, 4 << 20, 64UL << 20); await IdleAsync(service, timeout.Token);
            service.AfterRead(196608 + (4UL << 20), 8 << 20, 64UL << 20); await IdleAsync(service, timeout.Token);
            Check(limits.SequenceEqual(new[] { 1, 2, 4 }), "adaptive prefetch did not grow within its configured cap");
            service.AfterRead(32UL << 20, 65536, 64UL << 20); await IdleAsync(service, timeout.Token);
            Check(limits.Count == 3, "seek should reset adaptive confidence");
        }
        checks.Add("Adaptive prefetch waits for a sequential run, grows from one to the configured maximum, and resets after a seek.");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int imports = 0;
        using (var service = new DiskHydrationService(disk, async (_, _) =>
        { started.TrySetResult(); await release.Task; return payload.ToArray(); }, Empty, (_, _) => Interlocked.Increment(ref imports)))
        {
            var prefetch = service.GetAsync(ids[0], digest, true); await started.Task.WaitAsync(timeout.Token);
            var demand = service.GetAsync(ids[0], digest);
            service.Configure(new("disabled", 2)); release.TrySetResult();
            Check(ReferenceEquals(prefetch, demand) && (await demand.WaitAsync(timeout.Token)).Length == payload.Length, "settings change cancelled a promoted foreground demand");
        }
        checks.Add("Disabling speculation preserves an in-flight object once a real foreground read requires it.");

        var cancelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newDemand = new TaskCompletionSource<Task<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int transfers = 0;
        DiskHydrationService? racing = null;
        using (racing = new DiskHydrationService(disk, async (request, ct) =>
        {
            Interlocked.Increment(ref transfers);
            if (request.Prefetch)
            {
                using var registration = ct.Register(() => newDemand.TrySetResult(racing!.GetAsync(ids[0], digest)));
                cancelStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return payload.ToArray();
        }, Empty, (_, _) => { }))
        {
            var old = racing.GetAsync(ids[0], digest, true); await cancelStarted.Task.WaitAsync(timeout.Token);
            racing.Configure(new("disabled", 2));
            var demand = await newDemand.Task.WaitAsync(timeout.Token);
            Check(!ReferenceEquals(old, demand) && (await demand.WaitAsync(timeout.Token)).Length == payload.Length,
                "a foreground request inherited an already-selected speculative cancellation");
            bool cancelled = false;
            try { await old.WaitAsync(timeout.Token); } catch (IOException) { cancelled = true; }
            Check(cancelled, "obsolete speculative flight was not cancelled");
            await IdleAsync(racing, timeout.Token);
            Check((await racing.GetAsync(ids[0], digest)).Length == payload.Length && transfers == 2,
                "completion of an old cancelled flight removed its replacement from the cache");
        }
        checks.Add("A real read arriving during cancellation gets a fresh flight; the cancelled flight cannot erase the replacement result.");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unblock = new ManualResetEventSlim(); int downloads = 0;
        using (var service = new DiskHydrationService(disk, (_, _) => { Interlocked.Increment(ref downloads); return Task.FromResult(payload); },
        (_, _, _) => { entered.TrySetResult(); Check(unblock.Wait(TimeSpan.FromSeconds(10)), "fixture planner was not released"); return JsonSerializer.SerializeToElement(new { items = new[] { new { id = ids[0], sha256 = digest, length = payload.Length } } }); }, (_, _) => { }))
        {
            try
            {
                service.AfterRead(0, 65536, 64UL << 20); service.AfterRead(65536, 65536, 64UL << 20);
                await entered.Task.WaitAsync(timeout.Token); service.Configure(new("disabled", 2));
            }
            finally { unblock.Set(); }
            await IdleAsync(service, timeout.Token);
            Check(downloads == 0, "a stale mapping response started a download after disabling prefetch");
        }
        checks.Add("A delayed mapping result cannot enqueue a stale prefetch after the setting changes.");
        return checks;
    }
    private static JsonElement Empty(ulong _, uint __, int ___) => JsonSerializer.SerializeToElement(new { items = Array.Empty<object>(), next_offset = (ulong?)null });
    private static async Task IdleAsync(DiskHydrationService service, CancellationToken ct)
    {
        while (true)
        {
            var state = JsonSerializer.SerializeToElement(service.GetState());
            if (!state.GetProperty("planning").GetBoolean() && state.GetProperty("activeRequests").GetInt32() == 0) return;
            await Task.Delay(10, ct);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new IOException("Prefetch fixture: " + message); }
}
