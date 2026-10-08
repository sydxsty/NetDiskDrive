using System.Text.Json;

namespace OverlayDisk.Cloud.Sync;

/// <summary>Consumes existing prepare replies. Diagnostics must never cause another disk or cloud request.</summary>
internal sealed class PreparationLogTracker(IProgress<SyncLogEntry>? sink)
{
    private static readonly string[] Stages = ["freeze", "sealing", "indexing", "root"];
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);
    private string? active;
    private string jobId = "";
    private ulong? generation;
    private JsonElement diagnostics;
    private bool observed;

    internal void BeginFreeze() => Begin("freeze", false);

    internal void Observe(JsonElement job)
    {
        bool resumed = !observed && active is null;
        observed = true;
        jobId = Text(job, "id");
        generation = job.TryGetProperty("generation", out var value) && value.TryGetUInt64(out var g) ? g : null;
        diagnostics = job.TryGetProperty("preparation_diagnostics", out var report)
            && report.ValueKind == JsonValueKind.Object && Text(report, "job_id") == jobId
            && Text(report, "scope") == "process_session" ? report.Clone() : default;
        string stage = Text(job, "phase") == "preparing" ? Text(job, "prepare_stage") : "ready";
        int reached = stage == "ready" ? Stages.Length : Array.IndexOf(Stages, stage);
        if (resumed)
            for (int i = 0; i < reached; i++) completed.Add(Stages[i]);
        if (active is { } prior && (prior == "freeze" || reached >= 0 && prior != stage)) Complete(prior, false);

        // A single native step can finish more than one stage. Report those
        // results without inventing a wall-clock start time. A resumed job does
        // not replay phases which had finished before this coordinator started.
        if (!resumed)
            for (int i = 0; i < reached; i++)
                if (!completed.Contains(Stages[i]) && Metrics(Stages[i]) is { } metrics && Count(metrics, "steps") > 0)
                    Complete(Stages[i], true);
        if (reached is >= 0 and < 4 && active != stage) Begin(stage, resumed);
    }

    internal void NoChanges()
    {
        if (active is not null) Complete(active, false, "没有新增版本");
    }

    internal void Interrupt(Exception error)
    {
        string stage = active ?? "preparing";
        string reason = error is OperationCanceledException ? "已暂停" : "未完成：" + error.Message;
        Write(stage, "interrupted", $"{Name(stage)}{reason}；{Counts(stage)}；计数截至上次已返回的检查点，本次未返回的工作不计入",
            error is OperationCanceledException ? "info" : "error");
    }

    private void Begin(string stage, bool resumed)
    {
        active = stage;
        Write(stage, "started", $"{(resumed ? "继续" : "开始")}{Name(stage)}；任务读写计数仅累计本次进程，重启后重新计数");
    }

    private void Complete(string stage, bool completedWithinStep, string? detail = null)
    {
        if (!completed.Add(stage)) return;
        Write(stage, "completed", $"{Name(stage)}完成{(completedWithinStep ? "（在本次原生调用内完成）" : "")}{(detail is null ? "" : "，" + detail)}；{Counts(stage)}");
        if (active == stage) active = null;
    }

    private string Counts(string stage)
    {
        if (Metrics(stage) is not { } m) return "原生尚未提供此阶段的任务读写计数";
        return $"本地读取 {Count(m, "local_read_bytes"):N0} B / {Count(m, "read_calls"):N0} 次，"
            + $"本地写入 {Count(m, "local_write_bytes"):N0} B / {Count(m, "write_calls"):N0} 次，"
            + $"刷盘 {Count(m, "flush_count"):N0} 次，耗时 {Count(m, "duration_ms"):N0} ms，{Count(m, "steps"):N0} 批；"
            + "仅本任务本次进程累计，重启后重新计数";
    }

    private JsonElement? Metrics(string stage)
        => diagnostics.ValueKind == JsonValueKind.Object && diagnostics.TryGetProperty("stages", out var stages)
            && stages.ValueKind == JsonValueKind.Object && stages.TryGetProperty(stage, out var value)
            && value.ValueKind == JsonValueKind.Object ? value : null;

    private void Write(string stage, string action, string message, string level = "info")
        => sink?.Report(new(DateTimeOffset.UtcNow, "preparation." + stage + "." + action,
            jobId.Length == 0 ? message : $"固定版本任务 {jobId}：{message}", level, Generation: generation));

    private static string Name(string stage) => stage switch
    {
        "freeze" => "固定本次版本", "sealing" => "封口活动对象", "indexing" => "整理增量索引", "root" => "生成版本描述", _ => "准备本次版本"
    };
    private static string Text(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
    private static long Count(JsonElement value, string key)
        => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long number) ? Math.Max(0, number) : 0;
}
