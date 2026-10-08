using System.Diagnostics;
using OverlayDisk.Cloud.Baidu;
using OverlayDisk.Cloud.Contracts;

internal static partial class Program
{
    private static readonly (string Name, Func<Task> Run)[] JournalGroupTests =
    [
        ("group-commit: same-directory begins and receipts share durable barriers", JournalGroupDurability),
        ("group-commit: different directory journals retain separate flush barriers", JournalGroupDirectories),
        ("group-commit: write failure rejects all affected tasks and invalidates proofs", JournalGroupFailure),
        ("group-commit: queued cancellation leaves no unfinished begin or stale state", JournalGroupCancellation),
        ("group-commit: Dispose drains pending receipts without holding the cache gate", JournalGroupDispose),
        ("group-commit: unexpected pump failure completes every queued task", JournalGroupPumpFailure),
        ("group-commit: acknowledged concurrent receipts survive an actual process kill", JournalGroupCrash)
    ];
    private static CachedCloudObject Receipt(string path, byte data = 1) => new(new(path, 1, false), Sha([data]));

    private sealed class JournalPause
    {
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task Wait() { Reached.TrySetResult(); await Release.Task.ConfigureAwait(false); }
    }
    private sealed class JournalFixture : IDisposable
    {
        internal const string Provider = "batch-fixture", Account = "batch-account";
        internal readonly BaiduMetadataCache Cache;
        internal JournalPause? Admission, Flushing;
        internal string? FailFlushParent;
        internal bool FailPump;
        internal JournalFixture(string folder)
        {
            Cache = new(folder, Provider, Account, 8192, new()
            {
                BeforeBatchAsync = async () =>
                {
                    if (Admission is { } pause) await pause.Wait();
                    if (FailPump) throw new InvalidOperationException("Injected batch worker failure");
                },
                BeforeFlushAsync = async (parent, count) =>
                {
                    Assert(count > 0, "Empty journal flush");
                    if (Flushing is { } pause) await pause.Wait();
                    if (parent == FailFlushParent) throw new IOException("Injected journal flush failure");
                }
            });
        }
        internal async Task Seed(params string[] parents)
        {
            foreach (string parent in parents)
                await Cache.RecordListingAsync(parent, [], await Cache.ListingRevisionAsync(default), default);
        }
        internal async Task Group(Func<Task[]> start, Action<Task[]>? whileFlushing = null, bool allowCancelled = false)
        {
            var admission = Admission = new(); var flushing = Flushing = new();
            Task[] tasks = start();
            try
            {
                await admission.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Admission = null; admission.Release.TrySetResult();
                await flushing.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(tasks.All(t => !t.IsCompleted || allowCancelled && t.IsCanceled), "Journal operation completed before its flush barrier");
                whileFlushing?.Invoke(tasks);
                Flushing = null; flushing.Release.TrySetResult();
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                Admission = null; Flushing = null;
                admission.Release.TrySetResult(); flushing.Release.TrySetResult();
            }
        }
        public void Dispose() { Admission?.Release.TrySetResult(); Flushing?.Release.TrySetResult(); Cache.Dispose(); }
    }

    private static async Task JournalGroupDurability()
    {
        string folder = CacheFolder();
        try
        {
            using (var f = new JournalFixture(folder))
            {
                await f.Seed("/bucket");
                Task<BaiduMetadataCache.Mutation>[] begin = [];
                await f.Group(() => begin = Enumerable.Range(0, 8).Select(i => f.Cache.BeginMutationAsync([$"/bucket/{i}"], false, default)).ToArray());
                Assert(f.Cache.JournalDiagnostics() == new MetadataJournalDiagnostics(8, 1, 8), "Eight same-directory BEGINs did not use one real journal flush");
                Task<CloudCacheLookup>? during = null;
                await f.Group(() => begin.Select((task, i) => task.Result.CompleteAsync([Receipt($"/bucket/{i}")])).ToArray(), _ =>
                {
                    during = f.Cache.LookupAsync("/bucket/0", default);
                    Assert(!during.IsCompleted, "Lookup observed an unflushed receipt instead of waiting behind the cache gate");
                });
                Assert((await during!).Item?.Sha256 == Sha([1]), "Lookup did not observe the durable receipt after the barrier");
                Assert(f.Cache.JournalDiagnostics() == new MetadataJournalDiagnostics(16, 2, 8), "Eight COMMITs did not share their own durable flush");
                await Task.WhenAll(Enumerable.Range(0, 8).Select(i => f.Cache.RememberVerifiedAsync(Receipt($"/bucket/{i}"), default)));
                Assert(f.Cache.JournalDiagnostics().Records == 16, "Identical trusted receipts generated redundant patch writes");
            }
            using var reopened = new BaiduMetadataCache(folder, JournalFixture.Provider, JournalFixture.Account, 1);
            for (int i = 0; i < 8; i++) Assert((await reopened.LookupAsync($"/bucket/{i}", default)).Item?.Sha256 == Sha([1]), "Grouped receipt did not survive restart/RAM eviction");
            Assert((await reopened.LookupAsync("/bucket/missing", default)) is { Known: true, Item: null }, "Grouped journal lost its complete directory proof");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task JournalGroupDirectories()
    {
        string folder = CacheFolder();
        try
        {
            using var f = new JournalFixture(folder); await f.Seed("/first", "/second");
            Task<BaiduMetadataCache.Mutation>[] begin = [];
            await f.Group(() => begin = Enumerable.Range(0, 8).Select(i => f.Cache.BeginMutationAsync([$"/{(i < 4 ? "first" : "second")}/{i}"], false, default)).ToArray());
            Assert(f.Cache.JournalDiagnostics() == new MetadataJournalDiagnostics(8, 2, 4), "Different WAL files incorrectly shared a durability claim");
            await f.Group(() => begin.Select((task, i) => task.Result.CompleteAsync([Receipt($"/{(i < 4 ? "first" : "second")}/{i}")])).ToArray());
            Assert(f.Cache.JournalDiagnostics() == new MetadataJournalDiagnostics(16, 4, 4), "Each directory needs its own COMMIT flush");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task JournalGroupFailure()
    {
        foreach (bool failBegin in new[] { true, false })
        {
            string folder = CacheFolder();
            try
            {
                using (var f = new JournalFixture(folder))
                {
                    await f.Seed("/bucket"); Task<BaiduMetadataCache.Mutation>[] begin = [];
                    if (failBegin) f.FailFlushParent = "/bucket";
                    Task[] affected = [];
                    await Error(async () =>
                    {
                        await f.Group(() => affected = begin = Enumerable.Range(0, 4).Select(i => f.Cache.BeginMutationAsync([$"/bucket/{i}"], false, default)).ToArray());
                        f.FailFlushParent = "/bucket";
                        await f.Group(() => affected = begin.Select((task, i) => task.Result.CompleteAsync([Receipt($"/bucket/{i}")])).ToArray());
                    }, "MetadataCacheWriteFailed");
                    Assert(affected.All(t => t.IsFaulted), "Failed flush stranded or acknowledged part of its batch");
                    Assert(!(await f.Cache.LookupAsync("/bucket/missing", default)).Known, "Failed batch retained an in-memory absence proof");
                    f.FailFlushParent = null;
                    // A rejected BEGIN must not leak a mutation count that would
                    // permanently block later confirmed full listings/checkpoints.
                    await f.Cache.RecordListingAsync("/bucket", [], await f.Cache.ListingRevisionAsync(default), default);
                    Assert((await f.Cache.LookupAsync("/bucket/missing", default)).Known, "Rejected batch leaked an unowned mutation count");
                }
                using var reopened = new BaiduMetadataCache(folder, JournalFixture.Provider, JournalFixture.Account, 8192);
                Assert((await reopened.LookupAsync("/bucket/0", default)) is { Known: true, Item: null }, "Failed batch or repaired listing reopened with a false receipt");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }

    private static async Task JournalGroupCancellation()
    {
        string folder = CacheFolder();
        try
        {
            using (var f = new JournalFixture(folder))
            {
                await f.Seed("/bucket"); using var cancellation = new CancellationTokenSource();
                Task<BaiduMetadataCache.Mutation>[] begin = []; bool cancelled = false;
                try
                {
                    await f.Group(() =>
                    {
                        begin = Enumerable.Range(0, 4).Select(i => f.Cache.BeginMutationAsync([$"/bucket/{i}"], false,
                            i == 0 ? cancellation.Token : default)).ToArray();
                        cancellation.Cancel(); return begin;
                    }, allowCancelled: true);
                }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && begin[0].IsCanceled && begin.Skip(1).All(t => t.IsCompletedSuccessfully), "Queued cancellation damaged unrelated admissions");
                await f.Group(() => begin.Skip(1).Select((task, i) => task.Result.CompleteAsync([Receipt($"/bucket/{i + 1}")])).ToArray());
                Assert(f.Cache.JournalDiagnostics() == new MetadataJournalDiagnostics(6, 2, 3), "Cancelled BEGIN wrote a journal record or caused independent flushes");
            }
            using var reopened = new BaiduMetadataCache(folder, JournalFixture.Provider, JournalFixture.Account, 8192);
            Assert((await reopened.LookupAsync("/bucket/0", default)) is { Known: true, Item: null }, "Cancelled BEGIN survived as unfinished durable work");
            Assert((await reopened.LookupAsync("/bucket/1", default)).Item is not null, "Cancellation lost a different committed receipt");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task JournalGroupDispose()
    {
        string folder = CacheFolder();
        try
        {
            using (var f = new JournalFixture(folder))
            {
                await f.Seed("/bucket"); Task<BaiduMetadataCache.Mutation>[] begin = [];
                await f.Group(() => begin = Enumerable.Range(0, 4).Select(i => f.Cache.BeginMutationAsync([$"/bucket/{i}"], false, default)).ToArray());
                Task? closing = null;
                await f.Group(() => begin.Select((task, i) => task.Result.CompleteAsync([Receipt($"/bucket/{i}")])).ToArray(), _ => closing = Task.Run(f.Cache.Dispose));
                await closing!.WaitAsync(TimeSpan.FromSeconds(10));
                bool rejected = false;
                try { await f.Cache.BeginMutationAsync(["/bucket/later"], false, default); } catch (ObjectDisposedException) { rejected = true; }
                Assert(rejected, "Disposed cache admitted another journal operation");
                rejected = false;
                try { await f.Cache.InvalidateAsync(); } catch (ObjectDisposedException) { rejected = true; }
                Assert(rejected, "Disposed cache invalidated files after releasing its account lease");
            }
            using var reopened = new BaiduMetadataCache(folder, JournalFixture.Provider, JournalFixture.Account, 8192);
            for (int i = 0; i < 4; i++) Assert((await reopened.LookupAsync($"/bucket/{i}", default)).Item is not null, "Dispose returned before pending receipts became durable");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task JournalGroupPumpFailure()
    {
        string folder = CacheFolder();
        try
        {
            using var f = new JournalFixture(folder); await f.Seed("/bucket");
            var pause = f.Admission = new(); f.FailPump = true;
            var tasks = Enumerable.Range(0, 4).Select(i => f.Cache.BeginMutationAsync([$"/bucket/{i}"], false, default)).ToArray();
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); f.Admission = null; pause.Release.TrySetResult();
            bool failed = false;
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)); } catch (InvalidOperationException) { failed = true; }
            Assert(failed && tasks.All(t => t.IsFaulted) && f.Cache.JournalDiagnostics().Records == 0, "Unexpected pump failure left queue tasks pending");
            f.FailPump = false;
            await using var next = await f.Cache.BeginMutationAsync(["/bucket/recovered"], false, default);
            await next.CompleteAsync([Receipt("/bucket/recovered")]);
            Assert((await f.Cache.LookupAsync("/bucket/recovered", default)).Item is not null, "Queue did not recover after a worker failure");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task<int> JournalGroupCrashChild(string folder)
    {
        using var f = new JournalFixture(folder); Task<BaiduMetadataCache.Mutation>[] begin = [];
        await f.Group(() => begin = Enumerable.Range(0, 8).Select(i => f.Cache.BeginMutationAsync([$"/bucket/{i}"], false, default)).ToArray());
        await f.Group(() => begin.Select((task, i) => task.Result.CompleteAsync([Receipt($"/bucket/{i}")])).ToArray());
        Assert(f.Cache.JournalDiagnostics() == new MetadataJournalDiagnostics(16, 2, 8), "Child did not combine its durable records");
        Console.WriteLine("journal-group-durable"); Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan); return 2;
    }
    private static async Task JournalGroupCrash()
    {
        string folder = CacheFolder();
        try
        {
            using (var initial = new JournalFixture(folder)) await initial.Seed("/bucket");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--journal-group-crash-helper"); start.ArgumentList.Add(folder);
            using var child = Process.Start(start) ?? throw new IOException("Could not start group crash helper");
            try
            {
                Assert(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) == "journal-group-durable", "Child did not reach its grouped receipt barrier");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            using var reopened = new BaiduMetadataCache(folder, JournalFixture.Provider, JournalFixture.Account, 8192);
            for (int i = 0; i < 8; i++) Assert((await reopened.LookupAsync($"/bucket/{i}", default)).Item?.Sha256 == Sha([1]), "Process kill lost an acknowledged grouped receipt");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
