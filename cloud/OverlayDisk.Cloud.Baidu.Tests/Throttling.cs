using System.Net;
using OverlayDisk.Cloud.Baidu;
using OverlayDisk.Cloud.Contracts;

internal static partial class Program
{
    // Legacy protocol/cache tests are functional tests, not wall-clock rate tests.
    private sealed class AdvancingClock : TimeProvider
    {
        private long stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref stamp, TimeSpan.TicksPerSecond);
    }
    private static readonly BaiduRequestScheduler TestScheduler = new(new(20, 8), new AdvancingClock());
    private static BaiduClient Scheduled(FakeBaidu server, BaiduRequestScheduler scheduler) => new(Session, new BorrowedHandler(server), new()
    {
        RequestScheduler = scheduler, AccountValidationTtl = TimeSpan.Zero,
        RetryDelay = TimeSpan.Zero, TaskPollDelay = TimeSpan.Zero, MaximumAttempts = 2
    });
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    private static async Task PcsCredentialPair()
    {
        using var server = new FakeBaidu(); int authenticatedParts = 0;
        server.Override = (request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/rest/2.0/pcs/file" && uri.Query.Contains("locateupload"))
                return Task.FromResult<HttpResponseMessage?>(FakeBaidu.Raw("{\"servers\":[{\"server\":\"https://upload.baidupcs.com\"}]}"));
            if (uri.AbsolutePath == "/rest/2.0/pcs/superfile2")
            {
                string credential = string.Join("; ", request.Headers.GetValues("Cookie"));
                if (!credential.Contains("STOKEN=pan-value", StringComparison.Ordinal))
                    return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden));
                Assert(credential == "BDUSS=fake-test-session-only; STOKEN=pan-value", "Upload forwarded another service token or the browser jar");
                Assert(!uri.Query.Contains("BDUSS") && !uri.Query.Contains("STOKEN"), "Credential was added to upload URL");
                authenticatedParts++;
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await using var client = Client(server);
        byte[] bytes = [71, 32, 18]; await Put(client, "/pair", bytes);
        Assert(authenticatedParts == 1 && server.Files["/pair"].SequenceEqual(bytes), "Credential pair upload failed");
        await using var downloaded = await client.OpenReadAsync("/pair"); await downloaded.CopyToAsync(Stream.Null);
        var transfer = server.Calls.Single(c => c.Path == "/content");
        Assert(transfer.Cookie == "BDUSS=fake-test-session-only", "Content download received the pan STOKEN");
    }
    private static async Task ForbiddenClassification()
    {
        foreach (var (body, expected, authentication) in new[] {
            ("<html>forbidden fake-private-marker</html>", "Http:403", false),
            ("{\"errno\":31045,\"message\":\"fake-private-marker\"}", "Baidu:31045", true),
            ("{\"errno\":9019}", "Baidu:9019", true),
            ("{\"error_code\":111}", "Http:403", false) })
        {
            using var server = new FakeBaidu(); int attempts = 0;
            server.Override = (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath != "/rest/2.0/pcs/superfile2") return Task.FromResult<HttpResponseMessage?>(null);
                attempts++; return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden) { Content = new StringContent(body) });
            };
            var events = new List<BaiduDiagnostic>();
            await using var client = new BaiduClient(Session, server, new() { RequestScheduler = TestScheduler, Diagnostic = events.Add, RetryDelay = TimeSpan.Zero });
            try { await Put(client, "/denied", [9, 7]); throw new Exception("Forbidden upload succeeded"); }
            catch (CloudProviderException error)
            {
                Assert(error.Code == expected && error.AuthenticationRequired == authentication && !error.IsTransient, "Forbidden response was misclassified");
                Assert(!error.Message.Contains("fake-private-marker") && events.All(e => !e.ToString().Contains("fake-private-marker")), "Provider body escaped diagnostics");
            }
            Assert(attempts == 1 && !server.Files.ContainsKey("/denied"), "Forbidden upload was retried or committed");
        }
    }

    private static async Task GlobalRate()
    {
        var clock = new ManualClock(); var scheduler = new BaiduRequestScheduler(new(2, 2), clock);
        using var server = new FakeBaidu(); await using var a = Scheduled(server, scheduler); await using var b = Scheduled(server, scheduler); await using var c = Scheduled(server, scheduler);
        await a.ValidateAsync();
        var second = b.ValidateAsync(); await Until(() => scheduler.Snapshot().QueuedRequests == 1 && clock.TimerCount > 0);
        var third = c.ValidateAsync(); await Until(() => scheduler.Snapshot().QueuedRequests == 2 && clock.TimerCount > 0);
        clock.Advance(TimeSpan.FromMilliseconds(499)); await Task.Yield();
        Assert(Calls(server, "account") == 1 && !second.IsCompleted, "A second client bypassed the shared rate interval");
        clock.Advance(TimeSpan.FromMilliseconds(1)); await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(!third.IsCompleted && c.GetRequestCounts().Count == 0, "Later FIFO waiter passed the earlier admission");
        scheduler.Configure(new(1, 1));
        await Until(() => scheduler.Snapshot().QueuedRequests == 1 && clock.TimerCount > 0);
        clock.Advance(TimeSpan.FromMilliseconds(999)); await Task.Yield();
        Assert(!third.IsCompleted && Calls(server, "account") == 2, "Configuration did not slow an existing client");
        clock.Advance(TimeSpan.FromMilliseconds(1)); await third.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(scheduler.Snapshot() is { StartedRequests: 3, ActiveRequests: 0, QueuedRequests: 0 }, "Request counters or release differ");
        foreach (var invalid in new[] { new BaiduRequestLimits(0, 1), new(double.NaN, 2), new(2, 0), new(2, 9) })
        { try { scheduler.Configure(invalid); throw new Exception("Invalid global limits accepted"); } catch (ArgumentOutOfRangeException) { } }
    }

    private static async Task GlobalBodies()
    {
        var scheduler = new BaiduRequestScheduler(new(20, 1), new AdvancingClock());
        using var server = new FakeBaidu(); server.Files["/stream"] = [1, 2, 3];
        await using var a = Scheduled(server, scheduler); await using var b = Scheduled(server, scheduler);
        var stream = await a.OpenReadAsync("/stream");
        Assert(scheduler.Snapshot().ActiveRequests == 1, "Download released its slot after headers");
        var next = b.ValidateAsync(); await Until(() => scheduler.Snapshot().QueuedRequests == 1);
        Assert(!next.IsCompleted, "Another client passed a held download body");
        await stream.CopyToAsync(Stream.Null); // Verified EOF releases before Dispose.
        await next.WaitAsync(TimeSpan.FromSeconds(10)); await stream.DisposeAsync();
        Assert(scheduler.Snapshot().ActiveRequests == 0, "Verified EOF or repeated dispose leaked/doubled release");
        var second = await a.OpenReadAsync("/stream"); var waiting = b.ValidateAsync();
        await Until(() => scheduler.Snapshot().QueuedRequests == 1); await second.DisposeAsync(); await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        server.DownloadLengthDelta = -1;
        await using var shortStream = await a.OpenReadAsync("/stream");
        await Error(() => shortStream.CopyToAsync(Stream.Null), "TruncatedDownload");
        Assert(scheduler.Snapshot().ActiveRequests == 0, "Failed body kept its slot");
        server.DownloadLengthDelta = 0; scheduler.Configure(new(20, 2));
        var left = await a.OpenReadAsync("/stream"); var right = await b.OpenReadAsync("/stream");
        var reducedWaiter = a.ValidateAsync(); await Until(() => scheduler.Snapshot().QueuedRequests == 1);
        scheduler.Configure(new(20, 1)); await left.DisposeAsync();
        Assert(scheduler.Snapshot().ActiveRequests == 1 && !reducedWaiter.IsCompleted, "Reducing concurrency aborted a body or admitted above its new limit");
        await right.DisposeAsync(); await reducedWaiter.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task GlobalCancellation()
    {
        var scheduler = new BaiduRequestScheduler(new(20, 1), new AdvancingClock());
        using var server = new FakeBaidu(); server.Files["/stream"] = [1, 2];
        await using var a = Scheduled(server, scheduler); await using var b = Scheduled(server, scheduler);
        await using var stream = await a.OpenReadAsync("/stream");
        long before = scheduler.Snapshot().StartedRequests;
        using var cancel = new CancellationTokenSource(); var pending = b.ValidateAsync(cancel.Token);
        await Until(() => scheduler.Snapshot().QueuedRequests == 1); cancel.Cancel();
        try { await pending; throw new Exception("Queued cancellation ignored"); } catch (OperationCanceledException) { }
        Assert(scheduler.Snapshot() is { QueuedRequests: 0, ActiveRequests: 1 } && scheduler.Snapshot().StartedRequests == before, "Canceled wait consumed an HTTP admission");
        var c = Scheduled(server, scheduler); var disposed = c.ValidateAsync();
        await Until(() => scheduler.Snapshot().QueuedRequests == 1); await c.DisposeAsync();
        try { await disposed; throw new Exception("Disposed queued client continued"); }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        Assert(scheduler.Snapshot().QueuedRequests == 0 && scheduler.Snapshot().StartedRequests == before, "Disposal left a queued network operation");
        await stream.DisposeAsync();
        using var transferCancellation = new CancellationTokenSource();
        var canceledBody = await a.OpenReadAsync("/stream", cancellationToken: transferCancellation.Token);
        transferCancellation.Cancel(); await Until(() => scheduler.Snapshot().ActiveRequests == 0);
        try { await canceledBody.ReadAsync(new byte[1]); throw new Exception("Canceled body read continued"); } catch (OperationCanceledException) { }
        await canceledBody.DisposeAsync();
        var owner = Scheduled(server, scheduler); var abandonedBody = await owner.OpenReadAsync("/stream");
        await owner.DisposeAsync(); await Until(() => scheduler.Snapshot().ActiveRequests == 0); await abandonedBody.DisposeAsync();
    }

    private static async Task GlobalAttemptCoverage()
    {
        var scheduler = new BaiduRequestScheduler(new(20, 2), new AdvancingClock());
        using var server = new FakeBaidu { FailFirstPart = true }; bool redirected = false;
        server.Override = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/content" && !redirected)
            {
                redirected = true; var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri(request.RequestUri.AbsoluteUri.Replace("download.baidupcs.com", "other.baidupcs.com", StringComparison.Ordinal));
                return Task.FromResult<HttpResponseMessage?>(response);
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await using var client = Scheduled(server, scheduler); await Put(client, "/attempts", [4, 9, 1]);
        await using var stream = await client.OpenReadAsync("/attempts"); await stream.CopyToAsync(Stream.Null);
        Assert(server.PartAttempts == 2 && redirected && scheduler.Snapshot().StartedRequests == server.Calls.Count && scheduler.Snapshot().ActiveRequests == 0,
            "HTTP retry, upload, redirect or download escaped the global budget");
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new(); private long ticks;
        private readonly List<Timer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public int TimerCount { get { lock (gate) return timers.Count(t => !t.Disposed); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { lock (gate) { var timer = new Timer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; } }
        public void Advance(TimeSpan duration)
        {
            List<Timer> due;
            lock (gate) { ticks += duration.Ticks; due = timers.Where(t => !t.Disposed && t.Due <= ticks).ToList(); foreach (var t in due) t.Disposed = true; }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback; internal readonly object? State = state;
            internal bool Disposed; internal long Due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (clock.gate) { if (Disposed) return false; Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks; return true; } }
            public void Dispose() { lock (clock.gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
