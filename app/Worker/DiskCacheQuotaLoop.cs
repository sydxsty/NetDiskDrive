using System.Text.Json;

namespace OverlayDisk.Worker;

/// <summary>One bounded cache-maintenance task per open core; stopped before core disposal.</summary>
internal sealed class DiskCacheQuotaLoop
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task running;
    private string? error;
    internal string? Error => Volatile.Read(ref error);
    internal bool IsCompleted => running.IsCompleted;
    internal DiskCacheQuotaLoop(Func<JsonElement> status, Func<JsonElement> step, Func<bool>? connected = null)
        => running = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var value = status(); bool progress = false;
                    if ((connected?.Invoke() ?? true) && Eligible(value))
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        ulong before = Number(value, "evicted_bytes");
                        value = step();
                        progress = Number(value, "evicted_bytes") > before && Flag(value, "more_work");
                    }
                    await Task.Delay(progress ? 50 : 1000, cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception failure) { Volatile.Write(ref error, failure.Message); }
        });
    internal static bool Eligible(JsonElement status) => Number(status, "max_bytes") > 0 && Flag(status, "online")
        && (status.TryGetProperty("eviction_ready", out var ready) ? ready.ValueKind == JsonValueKind.True : Flag(status, "source_ready") || Flag(status, "origin_ready"))
        && Number(status, "over_limit_bytes") > 0;
    private static ulong Number(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.TryGetUInt64(out var count) ? count : 0;
    private static bool Flag(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;
    internal async Task StopAsync()
    {
        cancellation.Cancel(); await running.ConfigureAwait(false);
        // The worker can race independent shutdown notifications; the CTS stays
        // owned by this bounded task object until collection instead of double-dispose.
    }
}
