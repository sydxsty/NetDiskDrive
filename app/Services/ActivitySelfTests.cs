using System.Text.Json;

namespace OverlayDisk.Services;

internal static class ActivitySelfTests
{
    internal static int Run(string output)
    {
        if (Directory.Exists(output)) throw new IOException("请使用新的日志测试目录。");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
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
    private static void Check(bool condition, string message) { if (!condition) throw new IOException(message); }
}
