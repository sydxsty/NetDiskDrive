using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Spd.Interop;

namespace OverlayDisk;

/// <summary>Non-destructive platform checks. Does not start, format, mount or dismount any disk.</summary>
public static class PlatformSelfTests
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var results = new List<string>();
        results.AddRange(await ManagedIoSelfTests.RunAsync().ConfigureAwait(false));
        results.AddRange(ManualReclaimSelfTests.Run());
        results.AddRange(await HydrationTransportSelfTests.RunAsync().ConfigureAwait(false));
        results.AddRange(await PrefetchSelfTests.RunAsync().ConfigureAwait(false));
        results.AddRange(await DiskPreparationSelfTests.RunAsync().ConfigureAwait(false));
        await Worker.WorkerTransportSelfTests.RunAsync().ConfigureAwait(false);
        results.Add("Worker transport: current-user pipe ACL, peer PID checks, concurrent request IDs, independent 4 MiB binary transfer, drain and unknown mutation outcome, frame limits and fixed methods verified.");
        Assert(Marshal.SizeOf<StorageUnitParams>() == 128, "WinSpd parameter ABI");
        Assert(Marshal.SizeOf<StorageUnitStatus>() == 32, "WinSpd status ABI");
        Assert(Marshal.SizeOf<UnmapDescriptor>() == 16, "WinSpd UNMAP ABI");
        Assert(Marshal.SizeOf<StorageRequest>() == 32, "WinSpd deferred request ABI");
        Assert(Marshal.OffsetOf<StorageRequest>(nameof(StorageRequest.BlockAddress)).ToInt32() == 16, "WinSpd request union offset");
        Assert(Marshal.SizeOf<StorageResponse>() == 48, "WinSpd deferred response ABI");
        Assert(Marshal.OffsetOf<StorageResponse>(nameof(StorageResponse.Status)).ToInt32() == 16, "WinSpd response status offset");
        Assert(Marshal.SizeOf<CoreCompletion>() == 32, "Rust V4 completion ABI");
        results.Add("WinSpd x64 ABI sizes verified.");
        using (var queue = new FakeQueue())
        {
            var bridge = new AsyncRequestBridge(queue, (_, _, _) => throw new IOException("RO rejection must be synchronous."), _ => { }, readOnly: true);
            var status = default(StorageUnitStatus);
            Assert(bridge.SubmitBlocks(Operation(601, 2), 2, 0, 1, IntPtr.Zero, false, ref status)
                && status.SenseKey == Spd.StorageUnitBase.SCSI_SENSE_DATA_PROTECT && status.ASC == Spd.StorageUnitBase.SCSI_ADSENSE_WRITE_PROTECT,
                "read-only bridge returns explicit SCSI write protection");
            status = default; bridge.TrimInOrder([new UnmapDescriptor { BlockAddress = 0, BlockCount = 1 }], ref status);
            Assert(status.SenseKey == Spd.StorageUnitBase.SCSI_SENSE_DATA_PROTECT && queue.TrimCalls == 0 && queue.Submitted.Count == 0,
                "read-only write and trim never reach the storage queue");
        }
        results.Add("Write-protected WinSpd bridge rejects guest writes/TRIM before admission with DATA_PROTECT.");
        WinSpdDiskHost.ValidateCapacity(8UL << 40);
        Reject(() => WinSpdDiskHost.ValidateCapacity((8UL << 40) + 512));
        ulong capacity = 128UL * 1024 * 1024;
        Guid id = Guid.NewGuid();
        DiskProvisioner.ValidateRequest(id, capacity, 'R', "磁盘测试 O'Brien");
        Reject(() => DiskProvisioner.ValidateRequest(id, capacity, 'C', "test"));
        Reject(() => DiskProvisioner.ValidateRequest(id, capacity + 1, 'R', "test"));
        Reject(() => DiskProvisioner.ValidateRequest(Guid.Empty, capacity, 'R', "test"));
        Reject(() => DiskProvisioner.ValidateRequest(id, capacity, 'R', "bad/label"));
        Reject(() => ActiveDisks.Require(id));
        results.Add("Invalid geometry, protected drive letters and unregistered disks rejected.");

        byte[] descriptor = new byte[256];
        BitConverter.GetBytes(256u).CopyTo(descriptor, 4); BitConverter.GetBytes(14u).CopyTo(descriptor, 28);
        BitConverter.GetBytes(40u).CopyTo(descriptor, 16); BitConverter.GetBytes(80u).CopyTo(descriptor, 24);
        Encoding.ASCII.GetBytes(WinSpdDiskHost.DiskProductId).CopyTo(descriptor, 40);
        Encoding.ASCII.GetBytes(id.ToString("D")).CopyTo(descriptor, 80);
        DiskProvisioner.ValidateDeviceDescriptor(descriptor, descriptor.Length, id);
        Reject(() => DiskProvisioner.ValidateDeviceDescriptor(descriptor, descriptor.Length, Guid.NewGuid()));
        BitConverter.GetBytes(1u).CopyTo(descriptor, 28);
        Reject(() => DiskProvisioner.ValidateDeviceDescriptor(descriptor, descriptor.Length, id));
        results.Add("Native flush identity rejects a mismatched serial or bus without enumerating other disks.");

        // Regression: a failed/lost preparation result must not make Dispose treat an online disk as empty.
        var provisional = new ActiveDisks.Registration(capacity);
        provisional.BeginPreparation();
        Assert(provisional.Mounted is null, "provisional fixture");
        Reject(provisional.EnsureSafeForRemoval);
        provisional.PreparedSafelyForRemoval = true;
        provisional.ActiveFlushes = 1; Reject(provisional.EnsureSafeForRemoval);
        provisional.ActiveFlushes = 0; provisional.EnsureSafeForRemoval();
        provisional.BeginPreparation();
        Reject(provisional.EnsureSafeForRemoval);
        results.Add("Provisional and retried preparation cannot bypass verified safe removal.");
        await TestVolumeLeasesAsync();
        results.Add("Volume lifecycle leases serialize one disk while leaving another disk available.");
        await TestDeferredBridgeAsync().ConfigureAwait(false);
        results.Add("Deferred bridge: copied write ownership, FUA forwarding, out-of-order replies, error propagation and close/drain lifecycle.");
        await TestPumpFailureUnderBackpressureAsync().ConfigureAwait(false);
        results.Add("Completion-pump failure cancels a budget-blocked submit and returns a bounded close diagnostic without dropping pending resources.");
        TestFinalFlushFailure();
        results.Add("A failed final flush rejects host close and remains retryable; removing a preceding duplicate flush does not bypass durability.");

        string payloadText = "O'Brien ; $(Write-Output unexpected)";
        string script = DiskProvisioner.BuildStorageScript(new { id = id.ToString("D"), capacity, letter = "R", label = payloadText, mode = "create" });
        Assert(!script.Contains(payloadText, StringComparison.Ordinal), "label isolation");
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        string parseOnly = "$tokens=$null; $errors=$null; $s=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded +
            "')); $null=[System.Management.Automation.Language.Parser]::ParseInput($s,[ref]$tokens,[ref]$errors); if($errors.Count){$errors|ForEach-Object{[Console]::Error.WriteLine($_.Message)};exit 1}; [Console]::WriteLine('PowerShell syntax verified without execution.')";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command"); start.ArgumentList.Add("-");
        using var process = Process.Start(start) ?? throw new IOException("Cannot launch PowerShell parser.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(parseOnly).ConfigureAwait(false); process.StandardInput.Close();
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException(await error.ConfigureAwait(false));
        results.Add((await output.ConfigureAwait(false)).Trim());
        return results;
    }

    private static async Task TestVolumeLeasesAsync()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        await ActiveDisks.Gate.WaitAsync();
        try { ActiveDisks.Items.Add(first, new(128UL << 20)); ActiveDisks.Items.Add(second, new(128UL << 20)); }
        finally { ActiveDisks.Gate.Release(); }
        ActiveDisks.Lease? held = null;
        try
        {
            held = await ActiveDisks.AcquireAsync(first);
            var pending = ActiveDisks.AcquireAsync(first);
            using var independent = await ActiveDisks.AcquireAsync(second).WaitAsync(TimeSpan.FromSeconds(2));
            Assert(!pending.IsCompleted, "same volume lease excludes concurrent removal");
            held.Dispose(); held = null;
            using var resumed = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            held?.Dispose(); await ActiveDisks.Gate.WaitAsync();
            try { ActiveDisks.Items.Remove(first); ActiveDisks.Items.Remove(second); }
            finally { ActiveDisks.Gate.Release(); }
        }
    }

    private static void Assert(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("Platform self-test failed: " + name);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (IOException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Platform self-test: unsafe input was accepted.");
    }

    private static async Task TestDeferredBridgeAsync()
    {
        using var queue = new FakeQueue();
        var replies = new ConcurrentDictionary<ulong, byte>();
        var reported = new ConcurrentQueue<Exception>();
        var bridge = new AsyncRequestBridge(queue, (operation, status, data) =>
        {
            Assert(operation.Request.Hint == operation.Response.Hint, "copied native response identity");
            if (operation.Request.Kind == 1 && status.ScsiStatus == 0)
            {
                Assert(data != IntPtr.Zero && Marshal.ReadByte(data) == 0x71, "owned read result survives native reply");
                Assert(!queue.Released.ContainsKey(2), "read result not released before native delivery");
            }
            Assert(replies.TryAdd(operation.Response.Hint, status.ScsiStatus), "exactly one native reply");
        }, reported.Enqueue);
        bridge.StartPump();
        IntPtr input = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.Copy(Enumerable.Repeat((byte)0x42, 512).ToArray(), 0, input, 512);
            var status = default(StorageUnitStatus);
            Assert(!bridge.SubmitBlocks(Operation(101, 2), 2, 0, 1, input, false, ref status), "write defers completion");
            Marshal.WriteByte(input, 0x99); // Simulate the native dispatcher reusing its next-request buffer.
            Assert(queue.Writes[1][0] == 0x42, "write was copied before callback returned");
            Assert(!bridge.SubmitBlocks(Operation(102, 1), 1, 0, 1, IntPtr.Zero, true, ref status), "read defers completion");
            Assert(!bridge.SubmitBlocks(Operation(103, 3), 3, 0, 0, IntPtr.Zero, false, ref status), "flush defers completion");
            Assert(queue.Submitted.Select(x => x.Op).SequenceEqual(new uint[] { 2, 1, 3 }), "single-ingress submit order");
            Assert(queue.Submitted[1].Flags == 1, "read FUA forwarded to ordered queue");

            queue.RejectSubmit = true;
            status = default;
            Assert(bridge.SubmitBlocks(Operation(104, 2), 2, 1, 1, input, false, ref status) && status.ScsiStatus != 0,
                "rejected submit completes synchronously with SCSI error");
            Assert(bridge.PendingCount == 3, "failed submit does not leak pending request");
            queue.RejectSubmit = false;

            status = default;
            bridge.TrimInOrder(new[] { new UnmapDescriptor { BlockAddress = 0, BlockCount = 1 },
                new UnmapDescriptor { BlockAddress = ulong.MaxValue, BlockCount = 1 } }, ref status);
            Assert(status.ScsiStatus != 0 && queue.TrimCalls == 0, "UNMAP validates whole list before mutation");

            Task closing = Task.Run(bridge.CloseAndDrain);
            Assert(queue.DrainEntered.Wait(TimeSpan.FromSeconds(5)), "close reached ordered drain");
            Assert(!closing.IsCompleted, "close waits for native replies, not just Rust execution");
            status = default;
            Assert(bridge.SubmitBlocks(Operation(105, 1), 1, 0, 1, IntPtr.Zero, false, ref status) &&
                status.SenseKey == Spd.StorageUnitBase.SCSI_SENSE_NOT_READY, "close stops new admission");

            queue.Complete(2, 512, 0x71);
            queue.Complete(3, 0, 0);
            queue.Complete(1, 0, 0, "injected write error");
            await closing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert(replies.Count == 3 && replies[101] != 0 && replies[102] == 0 && replies[103] == 0,
                "out-of-order completions route by saved Hint and preserve per-request errors");
            Assert(queue.Released.Count == 3 && queue.FlushCalls == 1 && bridge.PendingCount == 0,
                "drain covers release and final durability barrier");
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            // Tests must leave no mock completion worker alive even if an assertion failed.
            foreach (ulong token in queue.Submitted.Select(x => x.Token).Where(x => !queue.Completed.ContainsKey(x)).ToArray())
                queue.Complete(token, 0, 0, "test cleanup");
            SpinWait.SpinUntil(() => bridge.PendingCount == 0, TimeSpan.FromSeconds(5));
            bridge.StopPump();
        }
    }

    private static DeferredStorageOperation Operation(ulong hint, byte kind)
        => new(new IntPtr(1), new StorageRequest { Hint = hint, Kind = kind }, new StorageResponse { Hint = hint, Kind = kind });

    private static void TestFinalFlushFailure()
    {
        using var queue = new FakeQueue { RejectFlush = true };
        var bridge = new AsyncRequestBridge(queue, (_, _, _) => { }, _ => { });
        bridge.StartPump();
        try
        {
            bool failed = false;
            try { bridge.CloseAndDrain(); } catch (IOException) { failed = true; }
            Assert(failed && queue.FlushCalls == 1, "host close must not succeed after a failed durability barrier");
            queue.RejectFlush = false;
            bridge.CloseAndDrain();
            Assert(queue.FlushCalls == 2 && bridge.PendingCount == 0, "failed final flush can be retried without fabricating completion");
        }
        finally { bridge.StopPump(); }
    }

    private sealed class FakeQueue : IAsyncDiskQueue, IDisposable
    {
        internal readonly record struct Submission(uint Op, uint Flags, ulong Token);
        private readonly BlockingCollection<CoreCompletion> _ready = new();
        private readonly ConcurrentDictionary<ulong, (IntPtr Data, IntPtr Error)> _buffers = new();
        internal readonly List<Submission> Submitted = new();
        internal readonly Dictionary<ulong, byte[]> Writes = new();
        internal readonly ConcurrentDictionary<ulong, bool> Released = new(), Completed = new();
        internal readonly ManualResetEventSlim DrainEntered = new();
        internal bool RejectSubmit, RejectFlush;
        internal int FlushCalls, TrimCalls;
        public ulong Capacity => 128UL * 1024 * 1024;
        public void Submit(uint op, ulong offset, uint length, IntPtr data, uint flags, ulong token)
        {
            if (RejectSubmit) throw new IOException("injected admission error");
            if (op == 2)
            {
                var copy = new byte[length];
                Marshal.Copy(data, copy, 0, checked((int)length));
                Writes.Add(token, copy);
            }
            Submitted.Add(new(op, flags, token));
        }
        public bool TryNextCompletion(out CoreCompletion completion, uint timeoutMs)
            => _ready.TryTake(out completion, checked((int)timeoutMs));
        internal void Complete(ulong token, uint length, byte first, string? error = null)
        {
            if (!Completed.TryAdd(token, true)) return;
            IntPtr data = length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal((int)WinSpdDiskHost.MaximumTransfer);
            if (data != IntPtr.Zero) Marshal.WriteByte(data, first);
            IntPtr text = error is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(error);
            _buffers[token] = (data, text);
            _ready.Add(new CoreCompletion { Token = token, Status = error is null ? 0 : -1, Length = length, Data = data, Error = text });
        }
        public void ReleaseCompletion(ulong token)
        {
            Assert(Released.TryAdd(token, true), "completion released once");
            if (_buffers.TryRemove(token, out var buffer))
            {
                if (buffer.Data != IntPtr.Zero) Marshal.FreeHGlobal(buffer.Data);
                if (buffer.Error != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer.Error);
            }
        }
        public void Drain() => DrainEntered.Set();
        public void CancelSubmissions() => RejectSubmit = true;
        public void Flush()
        {
            Interlocked.Increment(ref FlushCalls);
            if (RejectFlush) throw new IOException("injected final flush failure");
        }
        public void Trim(ulong offset, ulong length) => Interlocked.Increment(ref TrimCalls);
        public void Dispose()
        {
            foreach (var pair in _buffers.ToArray()) ReleaseCompletion(pair.Key);
            _ready.Dispose(); DrainEntered.Dispose();
        }
    }

    private static async Task TestPumpFailureUnderBackpressureAsync()
    {
        using var queue = new FullQueue();
        var reported = new ConcurrentQueue<Exception>();
        var bridge = new AsyncRequestBridge(queue, (_, _, _) => throw new InvalidOperationException("No completion should be delivered."), reported.Enqueue);
        bridge.StartPump();
        var firstStatus = default(StorageUnitStatus);
        Assert(!bridge.SubmitBlocks(Operation(201, 1), 1, 0, 1, IntPtr.Zero, false, ref firstStatus), "first request fills mock budget");
        Task<bool> blocked = Task.Run(() =>
        {
            var status = default(StorageUnitStatus);
            return bridge.SubmitBlocks(Operation(202, 1), 1, 1, 1, IntPtr.Zero, false, ref status) && status.ScsiStatus != 0;
        });
        Assert(queue.SubmitWaiting.Wait(TimeSpan.FromSeconds(5)), "second submit is blocked while holding admission");
        queue.FailPump.Set();
        Assert(await blocked.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false), "pump cancellation releases blocked submit with error");
        Assert(queue.CancelCalls == 1 && bridge.PendingCount == 1, "accepted request remains owned; rejected request is removed");
        try
        {
            await Task.Run(bridge.CloseAndDrain).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            throw new InvalidOperationException("Close incorrectly ignored undelivered accepted I/O.");
        }
        catch (IOException error)
        {
            Assert(error.InnerException?.Message == FullQueue.OriginalError, "close retains the original pump failure");
        }
        Assert(queue.FlushCalls == 1 && bridge.PendingCount == 1, "reserved final flush runs without pretending native replies completed");
        Assert(reported.All(error => error.Message == FullQueue.OriginalError), "cancellation error does not overwrite original diagnostic");
        // Pump has exited; its undelivered request deliberately remains tracked, just as in production.
    }

    private sealed class FullQueue : IAsyncDiskQueue, IDisposable
    {
        internal const string OriginalError = "injected completion pump failure";
        internal readonly ManualResetEventSlim SubmitWaiting = new(), FailPump = new(), Cancelled = new();
        internal int CancelCalls, FlushCalls;
        private int _submissions;
        public ulong Capacity => 128UL * 1024 * 1024;
        public void Submit(uint op, ulong offset, uint length, IntPtr data, uint flags, ulong token)
        {
            if (Interlocked.Increment(ref _submissions) == 1) return;
            SubmitWaiting.Set();
            if (!Cancelled.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Cancellation did not wake budget waiter.");
            throw new IOException("submission cancelled");
        }
        public bool TryNextCompletion(out CoreCompletion completion, uint timeoutMs)
        {
            completion = default;
            if (FailPump.Wait(checked((int)timeoutMs))) throw new IOException(OriginalError);
            return false;
        }
        public void CancelSubmissions() { Interlocked.Increment(ref CancelCalls); Cancelled.Set(); }
        public void ReleaseCompletion(ulong token) => throw new InvalidOperationException("Undelivered completion cannot be released.");
        public void Drain() { }
        public void Flush() => Interlocked.Increment(ref FlushCalls);
        public void Trim(ulong offset, ulong length) => throw new NotSupportedException();
        public void Dispose() { Cancelled.Set(); FailPump.Set(); SubmitWaiting.Dispose(); FailPump.Dispose(); Cancelled.Dispose(); }
    }
}
