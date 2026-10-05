namespace OverlayDisk.Cloud.Baidu;

public sealed record BaiduRequestLimits(double RequestsPerSecond = 2, int MaximumConcurrentRequests = 2);
public sealed record BaiduRequestSnapshot(BaiduRequestLimits Limits, int QueuedRequests, int ActiveRequests, long StartedRequests);

/// <summary>One FIFO budget for all clients, accounts, API calls and transfer bodies in this process.
/// Configure changes future admissions; reducing concurrency never aborts an active response.</summary>
public sealed class BaiduRequestScheduler
{
    public static BaiduRequestScheduler Shared { get; } = new();
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly LinkedList<Waiter> queue = new();
    private TaskCompletionSource changed = Signal();
    private BaiduRequestLimits limits;
    private bool pumping, hasStarted;
    private long lastStarted, started;
    private int active;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Waiter(CancellationToken cancellation)
    {
        internal readonly CancellationToken Cancellation = cancellation;
        internal readonly TaskCompletionSource<IDisposable> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter>? Node;
    }
    public BaiduRequestScheduler(BaiduRequestLimits? limits = null, TimeProvider? timeProvider = null)
    {
        this.limits = Validate(limits ?? new()); clock = timeProvider ?? TimeProvider.System;
    }
    private static BaiduRequestLimits Validate(BaiduRequestLimits value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!double.IsFinite(value.RequestsPerSecond) || value.RequestsPerSecond is < 0.1 or > 20 || value.MaximumConcurrentRequests is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(value), "Baidu limits require 0.1–20 requests/second and 1–8 concurrent requests.");
        return value;
    }
    public void Configure(BaiduRequestLimits value)
    { Validate(value); lock (gate) { limits = value; Pulse(); } }
    public BaiduRequestSnapshot Snapshot()
    { lock (gate) return new(limits, queue.Count, active, started); }
    private void Pulse() { var previous = changed; changed = Signal(); previous.TrySetResult(); }

    internal async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var waiter = new Waiter(cancellation);
        lock (gate)
        {
            waiter.Node = queue.AddLast(waiter); Pulse();
            if (!pumping) { pumping = true; _ = Task.Run(PumpAsync); }
        }
        using var registration = cancellation.Register(() => Cancel(waiter));
        return await waiter.Completion.Task.ConfigureAwait(false);
    }
    private void Cancel(Waiter waiter)
    {
        lock (gate)
        {
            if (waiter.Node is null) return;
            queue.Remove(waiter.Node); waiter.Node = null;
            waiter.Completion.TrySetCanceled(waiter.Cancellation); Pulse();
        }
    }
    private async Task PumpAsync()
    {
        try
        {
            for (;;)
            {
                Task signal; TimeSpan delay;
                lock (gate)
                {
                    if (queue.First is null) { pumping = false; return; }
                    var waiter = queue.First.Value;
                    if (waiter.Cancellation.IsCancellationRequested) { Cancel(waiter); continue; }
                    signal = changed.Task;
                    delay = active >= limits.MaximumConcurrentRequests ? Timeout.InfiniteTimeSpan :
                        hasStarted ? TimeSpan.FromTicks(Math.Max(0, (TimeSpan.FromSeconds(1 / limits.RequestsPerSecond) - clock.GetElapsedTime(lastStarted, clock.GetTimestamp())).Ticks)) : TimeSpan.Zero;
                    if (delay != Timeout.InfiniteTimeSpan && delay <= TimeSpan.Zero)
                    {
                        queue.RemoveFirst(); waiter.Node = null;
                        lastStarted = clock.GetTimestamp(); hasStarted = true; active++; started++;
                        waiter.Completion.SetResult(new Lease(this)); continue;
                    }
                }
                if (delay == Timeout.InfiniteTimeSpan) await signal.ConfigureAwait(false);
                else
                {
                    using var timer = new CancellationTokenSource();
                    var elapsed = Task.Delay(delay, clock, timer.Token);
                    if (await Task.WhenAny(signal, elapsed).ConfigureAwait(false) == signal) timer.Cancel();
                    else await elapsed.ConfigureAwait(false);
                }
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                foreach (var waiter in queue) { waiter.Node = null; waiter.Completion.TrySetException(error); }
                queue.Clear(); pumping = false;
            }
        }
    }
    private sealed class Lease(BaiduRequestScheduler owner) : IDisposable
    {
        private BaiduRequestScheduler? scheduler = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref scheduler, null); if (current is null) return;
            lock (current.gate) { current.active--; current.Pulse(); }
        }
    }
}
