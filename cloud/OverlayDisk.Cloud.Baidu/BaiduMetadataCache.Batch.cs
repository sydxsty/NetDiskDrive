using System.Runtime.CompilerServices;
using OverlayDisk.Cloud.Contracts;

[assembly: InternalsVisibleTo("OverlayDisk.Cloud.Baidu.Tests")]

namespace OverlayDisk.Cloud.Baidu;

internal sealed record MetadataJournalDiagnostics(long Records, long Flushes, int MaximumRecordsPerFlush);

internal sealed class MetadataJournalTestHooks
{
    internal Func<Task>? BeforeBatchAsync { get; init; }
    internal Func<string, int, Task>? BeforeFlushAsync { get; init; }
}

internal sealed partial class BaiduMetadataCache
{
    private const int MaximumBatchOperations = 64;
    private readonly object journalAdmission = new();
    private readonly Queue<JournalOperation> journalPending = new();
    private readonly MetadataJournalTestHooks? journalTestHooks;
    private Task? journalPump;
    private bool stopping;
    // Only the pump accesses this field, while owning gate. Readers and other
    // cache operations cannot observe state which precedes its durability barrier.
    private JournalBatch? journalBatch;
    private long journalRecordsWritten, journalFlushes;
    private int maximumRecordsPerFlush;

    private sealed class JournalOperation(Func<Task<object?>> execute, CancellationToken cancellation)
    {
        internal readonly Func<Task<object?>> Execute = execute;
        internal readonly CancellationToken Cancellation = cancellation;
        internal readonly TaskCompletionSource<object?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal object? Result;
    }
    private sealed class PendingJournal(string parent, DirectoryState state)
    {
        internal readonly string Parent = parent;
        internal readonly DirectoryState State = state;
        internal readonly long ExpectedBytes = state.JournalBytes;
        internal readonly List<byte[]> Frames = [];
    }
    private sealed class JournalBatch
    {
        internal readonly Dictionary<string, PendingJournal> Files = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Touched = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Checkpoints = new(StringComparer.Ordinal);
        internal readonly List<string[]> UnreturnedBegins = [];
        internal long Bytes;
    }

    internal MetadataJournalDiagnostics JournalDiagnostics()
        => new(Interlocked.Read(ref journalRecordsWritten), Interlocked.Read(ref journalFlushes), Volatile.Read(ref maximumRecordsPerFlush));

    private async Task<T> QueueJournalAsync<T>(Func<Task<T>> execute, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var operation = new JournalOperation(async () => await execute().ConfigureAwait(false), cancellation);
        lock (journalAdmission)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            journalPending.Enqueue(operation);
            journalPump ??= Task.Run(PumpJournalAsync);
        }
        // Once admitted to a batch, cancellation cannot release the caller before
        // its BEGIN is durable (or its complete directory proof is invalidated).
        return (T)(await operation.Completion.Task.ConfigureAwait(false))!;
    }

    private async Task QueueJournalAsync(Func<Task> execute, CancellationToken cancellation)
        => _ = await QueueJournalAsync(async () => { await execute().ConfigureAwait(false); return true; }, cancellation).ConfigureAwait(false);

    private async Task PumpJournalAsync()
    {
        try
        {
            while (true)
            {
                // Admission uses its own short lock, so concurrent completions can
                // join while this worker waits for the cache gate or writes a batch.
                await Task.Delay(2).ConfigureAwait(false);
                if (journalTestHooks?.BeforeBatchAsync is { } beforeBatch) await beforeBatch().ConfigureAwait(false);
                var operations = new List<JournalOperation>(MaximumBatchOperations);
                var batch = new JournalBatch();
                Exception? failure = null;
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    journalBatch = batch;
                    while (operations.Count < MaximumBatchOperations && batch.Bytes < MaximumRecordBytes)
                    {
                        JournalOperation operation;
                        lock (journalAdmission)
                        {
                            if (!journalPending.TryDequeue(out operation!)) break;
                        }
                        if (operation.Cancellation.IsCancellationRequested)
                        {
                            operation.Completion.TrySetCanceled(operation.Cancellation);
                            continue;
                        }
                        operations.Add(operation);
                        operation.Result = await operation.Execute().ConfigureAwait(false);
                    }
                    await FlushJournalBatchAsync(batch).ConfigureAwait(false);
                    journalBatch = null;
                    foreach (string parent in batch.Checkpoints)
                        if (states.TryGetValue(parent, out var state)) await MaybeCheckpointAsync(parent, state, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failure = error;
                    // No caller in this batch was acknowledged. Unreturned BEGINs
                    // have no owner that could later decrement their mutation count.
                    foreach (var parents in batch.UnreturnedBegins)
                        foreach (string parent in parents)
                            if (states.TryGetValue(parent, out var state)) state.Mutations--;
                    foreach (string parent in batch.Touched)
                    {
                        if (states.TryGetValue(parent, out var state)) { state.Entries = null; state.PartialEntries = null; }
                        try { InvalidateFile(parent); } catch (CloudProviderException) { }
                    }
                }
                finally
                {
                    journalBatch = null;
                    try { Evict(); }
                    catch (Exception error) { failure ??= error; }
                    finally { gate.Release(); }
                }
                foreach (var operation in operations)
                    if (failure is null) operation.Completion.TrySetResult(operation.Result);
                    else operation.Completion.TrySetException(failure);
                lock (journalAdmission)
                {
                    if (journalPending.Count == 0) { journalPump = null; return; }
                }
            }
        }
        catch (Exception error)
        {
            // Admission remains safe even if a test hook or an unexpected worker
            // failure occurs before acquiring the gate; no queued Task is stranded.
            lock (journalAdmission)
            {
                while (journalPending.TryDequeue(out var operation)) operation.Completion.TrySetException(error);
                journalPump = null;
            }
        }
    }

    private async Task FlushJournalBatchAsync(JournalBatch batch)
    {
        foreach (var pending in batch.Files.Values)
        {
            // A failed operation or directory deletion in the same batch may have
            // already invalidated this proof durably. Its obsolete WAL is irrelevant.
            if (pending.State.Entries is null) continue;
            byte[] bytes = new byte[pending.Frames.Sum(frame => frame.Length)];
            int offset = 0;
            foreach (var frame in pending.Frames) { frame.CopyTo(bytes, offset); offset += frame.Length; }
            try
            {
                await using var file = new FileStream(JournalPath(pending.Parent), FileMode.Open, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous);
                if (file.Length != pending.ExpectedBytes) throw new IOException("Directory journal changed.");
                file.Position = file.Length;
                await file.WriteAsync(bytes).ConfigureAwait(false);
                if (journalTestHooks?.BeforeFlushAsync is { } beforeFlush) await beforeFlush(pending.Parent, pending.Frames.Count).ConfigureAwait(false);
                file.Flush(true);
                Interlocked.Add(ref journalRecordsWritten, pending.Frames.Count);
                Interlocked.Increment(ref journalFlushes);
                maximumRecordsPerFlush = Math.Max(maximumRecordsPerFlush, pending.Frames.Count);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw Failure("MetadataCacheWriteFailed"); }
        }
    }
}
