using System.Text.Json;

namespace OverlayDisk.Services;

internal static class ActivitySelfTests
{
    internal static int Run(string output)
    {
        if (Directory.Exists(output)) throw new IOException("请使用新的日志测试目录。");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        CheckBufferedFlushes(output, checks);
        string path = Path.Combine(output, "journal");
        using (var journal = new ActivityJournal(path, 16, 4))
        {
            Parallel.For(0, 45, i => journal.Append(i % 2 == 0 ? "disk-a" : "disk-b", "run", "sync",
                "upload.confirmed", "上传完成", i % 7 == 0 ? "error" : "info", "object-" + i, "data", 4194304, 3, wireBytes: 128 + i));
            var items = journal.Read(limit: 200).Items;
            Check(items.Count == 45 && items.Select(e => e.Sequence).Distinct().Count() == 45, "并发记录丢失或重复");
            Check(items.All(e => e.Bytes == 4194304 && e.WireBytes is >= 128), "原始与压缩后大小混淆");
            long? cursor = null; var pages = new List<ActivityEntry>();
            do { var page = journal.Read(cursor, 7, "disk-a"); pages.AddRange(page.Items); cursor = page.NextCursor; } while (cursor != null);
            Check(pages.Count == 23 && pages.Select(e => e.Sequence).Distinct().Count() == 23, "分页缺失或重复");
            Check(journal.Read(level: "error").Items.Count == 7, "错误筛选未在全部保留日志上执行");
            checks.Add("concurrent append, exact object records, descending exclusive pagination and whole-journal error filtering");
        }
        string last = Directory.GetFiles(path, "*.jsonl").Order().Last();
        File.AppendAllText(last, "{\"sequence\":46,");
        using (var reopened = new ActivityJournal(path, 16, 4))
        {
            Check(reopened.Read().Items.Count == 45 && reopened.Warning != null, "截断尾记录破坏完整日志");
            Check(reopened.Read().Items.All(e => e.WireBytes is >= 128), "重开丢失压缩后大小");
            var appended = reopened.Append("disk-b", "next", "sync", "commit.confirmed", "云端版本已提交");
            Check(appended.Sequence == 46, "重启序列不连续");
            checks.Add("restart preserves records and recovers an interrupted final line without joining it to the next record");
        }
        using (var again = new ActivityJournal(path, 16, 4))
        {
            Check(again.Read().Items.Count == 46, "重开丢失新记录");
            for (int i = 0; i < 80; i++) again.Append("disk", "run", "sync", "cache.hit", "使用本地确认记录");
            Check(again.Read().Items.Count <= 64 && Directory.GetFiles(path, "*.jsonl").Length <= 4, "日志保留未限制");
            checks.Add("bounded on-disk rotation and retention survives repeated restarts");
        }
        string lockedPath = Path.Combine(output, "locked-retention");
        using (var bounded = new ActivityJournal(lockedPath, 2, 2))
        {
            for (int i = 0; i < 4; i++) bounded.Append("disk", "run", "sync", "upload.confirmed", "confirmed");
            string oldest = Directory.GetFiles(lockedPath).Order().First();
            using (var held = new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                for (int i = 0; i < 3; i++)
                {
                    bool failed = false;
                    try { bounded.Append("disk", "run", "sync", "upload.confirmed", "confirmed"); } catch (IOException) { failed = true; }
                    Check(failed && Directory.GetFiles(lockedPath).Length == 2 && bounded.Read().Items.Count == 4, "旧日志被占用时仍无界增长");
                }
            }
            bounded.Append("disk", "run", "sync", "upload.confirmed", "confirmed");
            Check(bounded.Read().Items[0].Sequence == 5, "解除占用后未恢复记录");
            checks.Add("a locked old log pauses rotation without unbounded growth; logging resumes after release");
        }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks }));
        Console.WriteLine("ACTIVITY_SMOKE_OK"); return 0;
    }
    private static void CheckBufferedFlushes(string output, List<string> checks)
    {
        string folder = Path.Combine(output, "buffered");
        var clock = new JournalClock();
        var journal = new ActivityJournal(folder, 64, 4, clock, TimeSpan.FromMilliseconds(250), 16);
        try
        {
            for (int i = 0; i < 15; i++) journal.Append("disk", "run", "sync", "upload.confirmed", "已确认 " + i);
            Check(journal.Read().Items.Count == 15 && journal.BufferDiagnostics() == (15, 0L), "缓冲期间界面不可见或每条活动都执行了flush");
            clock.Advance(TimeSpan.FromMilliseconds(249));
            Check(journal.BufferDiagnostics().Flushes == 0, "活动日志在时间/数量边界前刷写");
            journal.Append("disk", "run", "sync", "upload.confirmed", "批次边界");
            string file = Directory.GetFiles(folder).Single();
            Check(journal.BufferDiagnostics() == (0, 1L) && ReadOpenLogLines(file) == 16, "同批活动没有合并为一次flush");
            journal.Append("disk", "run", "sync", "upload.confirmed", "时间窗口开始");
            clock.Advance(TimeSpan.FromMilliseconds(100));
            journal.Append("disk", "run", "sync", "upload.confirmed", "后续记录不推迟截止时间");
            clock.Advance(TimeSpan.FromMilliseconds(150));
            Check(journal.BufferDiagnostics() == (0, 2L) && ReadOpenLogLines(file) == 18, "定时flush被后续日志无限推迟或重复执行");
            journal.Append("disk", "run", "sync", "commit.confirmed", "退出前缓冲");
            Check(journal.Read().Items.Count == 19 && journal.BufferDiagnostics() == (1, 2L), "退出前记录未立即显示");
            journal.Dispose();
            Check(journal.BufferDiagnostics() == (0, 3L), "Dispose未合并写出最后的缓冲记录");
            clock.Advance(TimeSpan.FromSeconds(1));
            Check(journal.BufferDiagnostics().Flushes == 3, "Dispose后仍运行定时写入");
            using var reopened = new ActivityJournal(folder, 64, 4);
            Check(reopened.Read().Items.Count == 19, "时间/批次/退出刷写后重开丢失完整日志");
            checks.Add("activity is immediately visible in memory; 19 records use three explicit buffer flushes at batch, fixed timer deadline and Dispose, with complete restart recovery");
        }
        finally { journal.Dispose(); }
    }

    private static int ReadOpenLogLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        int count = 0; while (reader.ReadLine() is not null) count++;
        return count;
    }

    private sealed class JournalClock : TimeProvider
    {
        private long ticks;
        private readonly List<JournalTimer> timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new JournalTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        internal void Advance(TimeSpan elapsed)
        {
            ticks += elapsed.Ticks;
            foreach (var timer in timers.ToArray()) timer.Fire(ticks);
        }
        private sealed class JournalTimer(JournalClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long? due;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Check(period == Timeout.InfiniteTimeSpan, "活动日志测试要求一次性截止时间");
                if (disposed) return false;
                due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.ticks + dueTime.Ticks; return true;
            }
            internal void Fire(long now)
            {
                if (disposed || due is not { } deadline || now < deadline) return;
                due = null; callback(state);
            }
            public void Dispose() { disposed = true; due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new IOException(message); }
}
