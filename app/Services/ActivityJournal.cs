using System.Text;
using System.Text.Json;

namespace OverlayDisk.Services;

public sealed record ActivityEntry(long Sequence, DateTimeOffset TimestampUtc, string DiskId, string RunId,
    string Kind, string Action, string Level, string Message, string? ObjectId = null,
    string? ObjectKind = null, long? Bytes = null, ulong? Generation = null, long? WireBytes = null);
public sealed record ActivityPage(IReadOnlyList<ActivityEntry> Items, long? NextCursor, bool HasMore,
    DateTimeOffset? OldestTimestampUtc, int RetentionCount, string? Warning);

/// <summary>Bounded append-only local activity, independent from authoritative upload receipts.
/// Each restart starts a fresh segment, so an interrupted last line is never joined to a new record.</summary>
public sealed class ActivityJournal : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string directory;
    private readonly int segmentEntries, maximumSegments;
    private readonly SortedDictionary<long, List<ActivityEntry>> segments = new();
    private StreamWriter? writer;
    private long sequence, currentSegment;
    private bool disposed;
    public string? Warning { get; private set; }

    public ActivityJournal(string directory, int segmentEntries = 4096, int maximumSegments = 16)
    {
        if (segmentEntries < 2 || maximumSegments < 2) throw new ArgumentOutOfRangeException(nameof(segmentEntries));
        this.directory = directory; this.segmentEntries = segmentEntries; this.maximumSegments = maximumSegments;
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "activity-*.jsonl").OrderBy(p => p, StringComparer.Ordinal).TakeLast(maximumSegments))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(file).AsSpan("activity-".Length), out var first) || first <= 0) continue;
            var entries = new List<ActivityEntry>();
            if (new FileInfo(file).Length > (long)segmentEntries * 16384) { Warning = "部分日志文件异常，已保留原文件。"; continue; }
            foreach (string line in File.ReadLines(file, Encoding.UTF8))
            {
                if (line.Length > 12000 || entries.Count >= segmentEntries) { Warning = "部分日志记录异常，已保留原文件。"; break; }
                try
                {
                    var entry = JsonSerializer.Deserialize<ActivityEntry>(line, Json);
                    if (entry is null || entry.Sequence < first || entry.Sequence <= sequence || entry.Message is null) { Warning = "部分日志记录异常，已保留原文件。"; continue; }
                    entries.Add(entry); sequence = entry.Sequence;
                }
                catch (JsonException) { Warning = "上次退出时有一条日志未写完，已保留其余记录。"; }
            }
            sequence = Math.Max(sequence, first);
            segments[first] = entries;
        }
    }

    public ActivityEntry Append(string diskId, string runId, string kind, string action, string message,
        string level = "info", string? objectId = null, string? objectKind = null, long? bytes = null,
        ulong? generation = null, DateTimeOffset? timestampUtc = null, long? wireBytes = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Callers pass structured operation descriptions, never request bodies or credentials.
            static string Bound(string value, int length) => value.Length <= length ? value : value[..length];
            var entry = new ActivityEntry(checked(sequence + 1), timestampUtc ?? DateTimeOffset.UtcNow,
                Bound(diskId, 128), Bound(runId, 128), Bound(kind, 48), Bound(action, 64),
                level is "error" or "warning" ? level : "info", Bound(message, 1200),
                objectId is null ? null : Bound(objectId, 128), objectKind is null ? null : Bound(objectKind, 32), bytes, generation, wireBytes);
            if (writer is null || segments[currentSegment].Count >= segmentEntries)
            {
                var previousWriter = writer; writer = null; previousWriter?.Dispose();
                // Prune before opening another segment. If an old file is locked, pause
                // logging with an error instead of growing memory/disk without a bound.
                while (segments.Count >= maximumSegments)
                {
                    long oldest = segments.Keys.First();
                    File.Delete(FileName(oldest)); segments.Remove(oldest);
                }
                currentSegment = entry.Sequence;
                var stream = new FileStream(FileName(currentSegment), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16384);
                writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                segments[currentSegment] = new();
            }
            try { writer.WriteLine(JsonSerializer.Serialize(entry, Json)); }
            catch
            {
                var failedWriter = writer; writer = null; sequence = Math.Max(sequence, entry.Sequence);
                try { failedWriter.Dispose(); } catch (IOException) { }
                throw;
            }
            sequence = entry.Sequence; segments[currentSegment].Add(entry);
            return entry;
        }
    }

    public ActivityPage Read(long? before = null, int limit = 200, string? diskId = null, string? level = null)
    {
        lock (gate)
        {
            limit = Math.Clamp(limit, 1, 200);
            var all = segments.Values.SelectMany(s => s);
            var selected = all.Reverse().Where(e => (!before.HasValue || e.Sequence < before.Value)
                && (string.IsNullOrEmpty(diskId) || e.DiskId == diskId)
                && (level != "error" || e.Level == "error")).Take(limit + 1).ToArray();
            bool more = selected.Length > limit;
            var page = selected.Take(limit).ToArray();
            return new(page, more ? page[^1].Sequence : null, more, all.FirstOrDefault()?.TimestampUtc,
                checked(segmentEntries * maximumSegments), Warning);
        }
    }
    private string FileName(long first) => Path.Combine(directory, $"activity-{first:D20}.jsonl");
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; var previous = writer; writer = null; previous?.Dispose(); } }
}
