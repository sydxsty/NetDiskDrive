using System.Security.Cryptography;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Worker;

namespace OverlayDisk;

/// <summary>Two transfers per container, at most one speculative download, and demand priority.
/// Prefetch follows logical mappings, never physical object numbers. All queues and hints are bounded.</summary>
internal sealed class DiskHydrationService : IDisposable
{
    private readonly object gate = new();
    private readonly string diskId;
    private readonly int objectSize;
    private readonly Func<LazyObjectRequest, CancellationToken, Task<byte[]>> provider;
    private readonly Func<ulong, uint, int, JsonElement> needs;
    private readonly Action<string, byte[]> import;
    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);
    private readonly Queue<Flight> foreground = new(), speculative = new(), completed = new();
    private readonly SemaphoreSlim wake = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task[] workers;
    private Task? planning;
    private CancellationTokenSource? planCancellation;
    private PrefetchSettings settings;
    private ulong previousReadEnd, hintEnd, sequentialBytes;
    private int readStreak;
    private long planRevision;
    private bool hasRead, stopped;
    private int active, prefetching;
    private string? lastError;
    private sealed class Flight(LazyObjectRequest request)
    {
        internal readonly LazyObjectRequest Request = request;
        internal bool Prefetch = request.Prefetch, Started, Finished, CancelRequested;
        internal bool Demanded = !request.Prefetch;
        internal CancellationTokenSource? DownloadCancellation;
        internal readonly TaskCompletionSource<byte[]> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal DiskHydrationService(string diskId, Func<LazyObjectRequest, CancellationToken, Task<byte[]>> provider,
        Func<ulong, uint, int, JsonElement> needs, Action<string, byte[]> import,
        uint objectSizeBytes = CloudObjectGeometry.DefaultSize, PrefetchSettings? settings = null)
    {
        CloudObjectGeometry.Validate(checked((int)objectSizeBytes));
        this.settings = settings ?? new(); this.settings.Validate();
        this.diskId = diskId; objectSize = checked((int)objectSizeBytes);
        this.provider = provider; this.needs = needs; this.import = import;
        workers = Enumerable.Range(0, 2).Select(_ => Task.Run(WorkerAsync)).ToArray();
    }
    internal void Configure(PrefetchSettings value)
    {
        value.Validate(); CancellationTokenSource[] cancel;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (settings == value) return;
            settings = value; hasRead = false; hintEnd = 0; readStreak = 0; sequentialBytes = 0;
            cancel = CancelSpeculationLocked();
        }
        Cancel(cancel);
    }
    private CancellationTokenSource[] CancelSpeculationLocked()
    {
        planRevision++;
        var cancel = DetachSpeculationLocked(includeQueued: true).ToList();
        if (planCancellation is not null) cancel.Add(planCancellation);
        return cancel.ToArray();
    }
    private CancellationTokenSource[] DetachSpeculationLocked(bool includeQueued)
    {
        var cancel = new List<CancellationTokenSource>();
        foreach (var pending in flights.Values.Where(v => v.Prefetch && !v.Finished && !v.Demanded && (includeQueued || v.Started)).ToArray())
        {
            // Withdraw before releasing the lock: a subsequent real demand must
            // never inherit a cancellation already selected for this speculative flight.
            pending.CancelRequested = true; flights.Remove(pending.Request.ObjectId);
            if (pending.DownloadCancellation is { } source) cancel.Add(source);
            if (!pending.Started)
            {
                pending.Finished = true;
                pending.Completion.TrySetException(new IOException("预取设置或读取位置已变化，已取消预取。"));
            }
        }
        return cancel.ToArray();
    }
    private static void Cancel(IEnumerable<CancellationTokenSource> sources)
    { foreach (var source in sources) try { source.Cancel(); } catch (ObjectDisposedException) { } }
    internal Task<byte[]> GetAsync(string objectId, string sha256, bool prefetch = false, long? expectedPlan = null)
    {
        var request = new LazyObjectRequest(diskId, objectId, sha256, objectSize, prefetch); request.Validate();
        CancellationTokenSource[] cancel; Task<byte[]> result;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (expectedPlan is { } revision && revision != planRevision)
                return Task.FromException<byte[]>(new IOException("预取计划已失效。"));
            if (flights.TryGetValue(objectId, out var existing))
            {
                if (!existing.Request.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("同一云端对象的校验信息不一致。");
                if (!prefetch) existing.Demanded = true;
                if (!prefetch && existing.Prefetch && !existing.Started)
                { existing.Prefetch = false; foreground.Enqueue(existing); wake.Release(); }
                return existing.Completion.Task;
            }
            if (prefetch && settings.Strategy == "disabled") return Task.FromException<byte[]>(new IOException("预取已关闭。"));
            if (flights.Count >= 32) throw new IOException("按需读取队列已满，请稍后重试。");
            var flight = new Flight(request); flights.Add(objectId, flight);
            (prefetch ? speculative : foreground).Enqueue(flight); wake.Release();
            result = flight.Completion.Task;
            cancel = prefetch ? [] : DetachSpeculationLocked(includeQueued: false);
        }
        Cancel(cancel); return result;
    }
    private Flight? Take()
    {
        lock (gate)
        {
            if (stopped) return null;
            Flight? result = null;
            while (foreground.Count > 0)
            { var entry = foreground.Dequeue(); if (!entry.Started && !entry.Finished) { result = entry; break; } }
            if (result is null && prefetching == 0)
                while (speculative.Count > 0)
                { var entry = speculative.Dequeue(); if (!entry.Started && !entry.Finished) { result = entry; break; } }
            if (result is null) return null;
            result.DownloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            result.DownloadCancellation.CancelAfter((result.Request with { Prefetch = result.Prefetch }).TransferTimeout + TimeSpan.FromSeconds(10));
            result.Started = true; active++; if (result.Prefetch) prefetching++;
            return result;
        }
    }
    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await wake.WaitAsync(lifetime.Token).ConfigureAwait(false);
                var flight = Take(); if (flight is null) continue;
                bool success = false;
                try
                {
                    using var timeout = flight.DownloadCancellation!;
                    timeout.Token.ThrowIfCancellationRequested();
                    byte[] bytes = await provider(flight.Request with { Prefetch = flight.Prefetch }, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                    if (bytes.Length != objectSize || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(flight.Request.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("按需读取对象校验失败。");
                    if (flight.Prefetch)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        lock (gate) if (flight.CancelRequested) throw new IOException("过期预取已取消。");
                        import(flight.Request.ObjectId, bytes);
                    }
                    success = true; flight.Completion.TrySetResult(bytes);
                    lock (gate) { if (flight.Demanded) lastError = null; }
                }
                catch (Exception error)
                {
                    var failure = error is OperationCanceledException ? new IOException("云端对象读取已取消或超时。", error) : error;
                    lock (gate)
                    {
                        if (flights.TryGetValue(flight.Request.ObjectId, out var current) && ReferenceEquals(current, flight)) flights.Remove(flight.Request.ObjectId);
                        if (flight.Demanded) lastError = failure.Message;
                    }
                    flight.Completion.TrySetException(failure);
                }
                finally
                {
                    lock (gate)
                    {
                        active--; if (flight.Prefetch) prefetching--; flight.Finished = true; flight.DownloadCancellation = null;
                        if (!success)
                        {
                            if (flights.TryGetValue(flight.Request.ObjectId, out var current) && ReferenceEquals(current, flight)) flights.Remove(flight.Request.ObjectId);
                        }
                        else
                        {
                            completed.Enqueue(flight);
                            while (completed.Count > 2)
                            {
                                var old = completed.Dequeue();
                                if (flights.TryGetValue(old.Request.ObjectId, out var current) && ReferenceEquals(current, old)) flights.Remove(old.Request.ObjectId);
                            }
                        }
                        if (foreground.Count + speculative.Count > 0) wake.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    internal void AfterRead(ulong offset, uint length, ulong capacity)
    {
        if (length < 65536 || offset > capacity || length > capacity - offset) return;
        ulong end = offset + length; CancellationTokenSource[] cancel = [];
        lock (gate)
        {
            if (stopped || settings.Strategy == "disabled") return;
            bool sequential = hasRead && offset == previousReadEnd;
            hasRead = true; previousReadEnd = end;
            if (!sequential)
            {
                readStreak = 1; sequentialBytes = length; hintEnd = 0;
                cancel = CancelSpeculationLocked();
            }
            else
            {
                readStreak = Math.Min(readStreak + 1, 1000000);
                sequentialBytes = Math.Min(sequentialBytes + length, (ulong)objectSize * PrefetchSettings.MaximumObjectCount);
            }
            bool ready = sequential && (settings.Strategy == "sequential" || readStreak >= 3);
            if (ready && end < capacity && end >= hintEnd && planning is not { IsCompleted: false })
            {
                int count = settings.Strategy == "adaptive"
                    ? Math.Min(settings.ObjectCount, 1 + (int)(sequentialBytes / (ulong)objectSize)) : settings.ObjectCount;
                uint window = (uint)Math.Min((ulong)objectSize * (uint)count, capacity - end);
                hintEnd = end + window;
                planCancellation?.Dispose(); planCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var plan = planCancellation;
                long revision = planRevision;
                planning = Task.Run(() => PlanAsync(end, window, count, revision, plan.Token));
            }
        }
        Cancel(cancel);
    }
    private async Task PlanAsync(ulong offset, uint length, int count, long revision, CancellationToken ct)
    {
        try
        {
            ulong end = offset + length, cursor = offset;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Each native query scans at most 4096 allocated pages; this cap also
            // bounds a completely cached hint, rather than walking the whole disk.
            for (int batch = 0; batch < PrefetchSettings.MaximumObjectCount && cursor < end && seen.Count < count; batch++)
            {
                ct.ThrowIfCancellationRequested();
                var result = needs(cursor, (uint)(end - cursor), count - seen.Count);
                ct.ThrowIfCancellationRequested();
                var items = result.GetProperty("items");
                if (items.GetArrayLength() > count - seen.Count) throw new InvalidDataException("预取对象数量超过限制。");
                foreach (var item in items.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.TryGetProperty("length", out var size) && size.GetInt64() != objectSize) throw new InvalidDataException("预取对象大小与磁盘不一致。");
                    string id = item.GetProperty("id").GetString()!;
                    if (!seen.Add(id)) continue;
                    await GetAsync(id, item.GetProperty("sha256").GetString()!, true, revision).WaitAsync(ct).ConfigureAwait(false);
                }
                if (!result.TryGetProperty("next_offset", out var next) || next.ValueKind != JsonValueKind.Number || !next.TryGetUInt64(out var value) || value <= cursor || value >= end) break;
                cursor = value;
            }
        }
        catch { /* Speculation never converts an otherwise successful read into an I/O error. */ }
    }
    internal object GetState()
    {
        lock (gate) return new { activeRequests = active, pendingRequests = flights.Values.Count(v => !v.Started && !v.Finished),
            prefetching, lastError, planning = planning is { IsCompleted: false }, prefetchStrategy = settings.Strategy, prefetchObjects = settings.ObjectCount, objectSizeBytes = objectSize };
    }
    public void Dispose()
    {
        Task? planner;
        lock (gate)
        {
            if (stopped) return; stopped = true; planner = planning;
            foreach (var flight in flights.Values.Where(v => !v.Started))
                flight.Completion.TrySetException(new IOException("磁盘正在关闭，已取消未开始的云端读取。"));
        }
        lifetime.Cancel();
        Task.WhenAll(workers.Concat(planner is null ? Array.Empty<Task>() : new[] { planner })).GetAwaiter().GetResult();
        lock (gate) { flights.Clear(); completed.Clear(); foreground.Clear(); speculative.Clear(); }
        planCancellation?.Dispose(); lifetime.Dispose(); wake.Dispose();
    }
}
