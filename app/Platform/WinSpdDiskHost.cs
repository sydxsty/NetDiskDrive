using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Spd;
using Spd.Interop;

namespace OverlayDisk;

/// <summary>Owns the WinSpd dispatcher, but does not own or dispose CoreDisk.</summary>
public sealed class WinSpdDiskHost : IDisposable
{
    public const string DiskProductId = "OverlayDisk";
    public const uint SectorSize = 512;
    public const uint MaximumTransfer = 1024 * 1024;
    public const ulong MaximumCapacity = 8UL * 1024 * 1024 * 1024 * 1024;
    private readonly CoreDisk _disk;
    private StorageUnitHost? _host;
    private AsyncRequestBridge? _bridge;
    private bool _disposed;
    private bool _registered;
    public Exception? LastIoError { get; private set; }
    public bool IsStarted => _host is not null;

    public WinSpdDiskHost(CoreDisk disk)
    {
        _disk = disk ?? throw new ArgumentNullException(nameof(disk));
        ValidateCapacity(disk.Capacity);
        if (disk.Id == Guid.Empty) throw new ArgumentException("磁盘 ID 不能为空。");
    }

    internal static void ValidateCapacity(ulong capacity)
    {
        if (capacity < 64UL * 1024 * 1024 || capacity > MaximumCapacity || capacity % SectorSize != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "磁盘容量需为 64 MiB 至 8 TiB，且按 512 字节对齐。");
    }

    public void Start(string? pipeName = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WinSpdDiskHost));
        if (_host is not null) throw new InvalidOperationException("磁盘已启动。");
        if (pipeName is null && !WinSpdRuntime.IsAdministrator)
            throw new UnauthorizedAccessException("真实磁盘挂载需要以管理员身份运行。");
        if (pipeName is not null && (!pipeName.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) || pipeName.Length < 10))
            throw new ArgumentException(@"测试管道名称必须以 \\.\pipe\ 开头。", nameof(pipeName));

        ActiveDisks.Gate.Wait();
        try
        {
            if (ActiveDisks.Items.ContainsKey(_disk.Id)) throw new InvalidOperationException("本进程中磁盘已挂载。");
            var bridge = new AsyncRequestBridge(new CoreQueueAdapter(_disk), StorageUnitHost.CompleteDeferred,
                error => LastIoError = error, _disk.IsReadOnly);
            var host = new StorageUnitHost(new Backend(_disk, bridge))
            {
                Guid = _disk.Id, BlockCount = _disk.Capacity / SectorSize, BlockLength = SectorSize,
                ProductId = DiskProductId, ProductRevisionLevel = "0006", MaxTransferLength = MaximumTransfer,
                CacheSupported = true, UnmapSupported = false, EjectDisabled = true,
                WriteProtected = _disk.IsReadOnly,
            };
            bridge.StartPump();
            int error;
            try { error = host.Start(pipeName, DispatcherThreadCount: 1); }
            catch { bridge.CloseAndDrain(); host.Dispose(); bridge.StopPump(); throw; }
            if (error != 0)
            {
                bridge.CloseAndDrain(); host.Dispose(); bridge.StopPump();
                throw new Win32Exception(error, "WinSpd 磁盘启动失败。");
            }
            _bridge = bridge;
            _host = host;
            if (pipeName is null)
            {
                // PnP can automount a previously formatted volume immediately after Start.
                // Treat even a host with no preparation result as needing verified removal.
                var registration = new ActiveDisks.Registration(_disk.Capacity, _disk.IsReadOnly);
                registration.BeginPreparation();
                ActiveDisks.Items.Add(_disk.Id, registration);
                _registered = true;
            }
        }
        finally { ActiveDisks.Gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        using var lease = _registered ? ActiveDisks.AcquireAsync(_disk.Id).GetAwaiter().GetResult() : null;
        if (_disposed) return;
        var registration = lease?.Registration;
        registration?.EnsureSafeForRemoval();
        // The per-volume lease excludes preparation/flush/removal on this disk only.
        _bridge?.CloseAndDrain();
        _host?.Dispose();
        _host = null;
        _bridge?.StopPump(); _bridge = null;
        registration?.LockedVolume?.Dispose();
        if (_registered)
        {
            ActiveDisks.Gate.Wait();
            try { ActiveDisks.Items.Remove(_disk.Id); }
            finally { ActiveDisks.Gate.Release(); }
        }
        _registered = false; _disposed = true;
    }

    private sealed class Backend(CoreDisk disk, AsyncRequestBridge bridge) : AsyncStorageUnitBase
    {
        public override bool ReadDeferred(DeferredStorageOperation operation, IntPtr data,
            ulong lba, uint count, bool forceUnitAccess, ref StorageUnitStatus status)
            => bridge.SubmitBlocks(operation, 1, lba, count, IntPtr.Zero, forceUnitAccess, ref status);
        public override bool WriteDeferred(DeferredStorageOperation operation, IntPtr data,
            ulong lba, uint count, bool forceUnitAccess, ref StorageUnitStatus status)
            => bridge.SubmitBlocks(operation, 2, lba, count, data, forceUnitAccess, ref status);
        public override bool FlushDeferred(DeferredStorageOperation operation,
            ulong lba, uint count, ref StorageUnitStatus status)
            => bridge.SubmitBlocks(operation, 3, 0, 0, IntPtr.Zero, false, ref status);
        public override void Flush(ulong lba, uint count, ref StorageUnitStatus status)
        {
            try { disk.Flush(); } // Hint=0 native dispatcher shutdown hook, never deferred.
            catch (Exception error) { bridge.Report(error); status.SetSense(SCSI_SENSE_MEDIUM_ERROR, SCSI_ADSENSE_WRITE_ERROR); }
        }
        public override void Unmap(UnmapDescriptor[] descriptors, ref StorageUnitStatus status)
            => status.SetSense(SCSI_SENSE_ILLEGAL_REQUEST, SCSI_ADSENSE_ILLEGAL_COMMAND);
    }

    private sealed class CoreQueueAdapter(CoreDisk disk) : IAsyncDiskQueue
    {
        public ulong Capacity => disk.Capacity;
        public void Submit(uint op, ulong offset, uint length, IntPtr data, uint flags, ulong token)
            => disk.Submit(op, offset, length, data, flags, token);
        public bool TryNextCompletion(out CoreCompletion completion, uint timeoutMs)
            => disk.TryNextCompletion(out completion, timeoutMs);
        public void ReleaseCompletion(ulong token) => disk.ReleaseCompletion(token);
        public void CancelSubmissions() => disk.CancelSubmissions();
        public void Drain() => disk.Drain();
        public void Flush() => disk.Flush();
        public void Trim(ulong offset, ulong length) => disk.Trim(offset, length);
    }

}

internal interface IAsyncDiskQueue
{
    ulong Capacity { get; }
    void Submit(uint op, ulong offset, uint length, IntPtr data, uint flags, ulong token);
    bool TryNextCompletion(out CoreCompletion completion, uint timeoutMs);
    void ReleaseCompletion(ulong token);
    void CancelSubmissions();
    void Drain();
    void Flush();
    void Trim(ulong offset, ulong length);
}

/// <summary>A single native ingress submits in order; a separate pump owns deferred replies.</summary>
internal sealed class AsyncRequestBridge
{
    private readonly record struct Pending(DeferredStorageOperation Operation, uint Op, uint Length);
    private readonly IAsyncDiskQueue _disk;
    private readonly Action<DeferredStorageOperation, StorageUnitStatus, IntPtr> _send;
    private readonly Action<Exception> _report;
    private readonly object _admission = new(), _pendingGate = new();
    private readonly Dictionary<ulong, Pending> _pending = new(64);
    private readonly ManualResetEventSlim _idle = new(true);
    private readonly ManualResetEventSlim _pumpExited = new(false);
    private readonly Thread _pump;
    private ulong _nextToken;
    private volatile bool _accepting = true, _stopPump;
    private volatile Exception? _pumpError;
    private Exception? _cancellationError;
    private bool _pumpStarted;
    private readonly bool _readOnly;

    internal AsyncRequestBridge(IAsyncDiskQueue disk,
        Action<DeferredStorageOperation, StorageUnitStatus, IntPtr> send, Action<Exception> report, bool readOnly = false)
    {
        _disk = disk; _send = send; _report = report; _readOnly = readOnly;
        _pump = new Thread(Pump) { IsBackground = true, Name = "OverlayDisk completion pump" };
    }
    internal void StartPump()
    {
        if (_pumpStarted) throw new InvalidOperationException("Completion pump already started.");
        _pumpStarted = true;
        _pump.Start();
    }
    internal int PendingCount { get { lock (_pendingGate) return _pending.Count; } }
    internal void Report(Exception error) => _report(_pumpError ?? error);

    internal bool SubmitBlocks(DeferredStorageOperation operation, uint op, ulong lba, uint count,
        IntPtr data, bool forceUnitAccess, ref StorageUnitStatus status)
    {
        if (_readOnly && op == 2)
        {
            status.SetSense(StorageUnitBase.SCSI_SENSE_DATA_PROTECT, StorageUnitBase.SCSI_ADSENSE_WRITE_PROTECT);
            return true;
        }
        try
        {
            ulong offset = checked(lba * WinSpdDiskHost.SectorSize);
            ulong length64 = checked((ulong)count * WinSpdDiskHost.SectorSize);
            if (operation.Request.Hint == 0 || operation.Response.Hint != operation.Request.Hint ||
                operation.Request.Kind != op || operation.Response.Kind != op || op is < 1 or > 3)
                throw new IOException("Invalid deferred WinSpd request identity.");
            if (length64 > WinSpdDiskHost.MaximumTransfer || offset > _disk.Capacity || length64 > _disk.Capacity - offset)
                throw new IOException("磁盘请求超出范围。");
            if (op == 2 && length64 != 0 && data == IntPtr.Zero) throw new IOException("Write buffer is null.");
            lock (_admission)
            {
                if (!_accepting)
                {
                    status.SetSense(StorageUnitBase.SCSI_SENSE_NOT_READY, StorageUnitBase.SCSI_ADSENSE_LUN_NOT_READY);
                    return true;
                }
                ulong token = checked(++_nextToken);
                lock (_pendingGate)
                {
                    _pending.Add(token, new(operation, op, (uint)length64));
                    _idle.Reset();
                }
                try
                {
                    // Rust copies a write's borrowed native buffer before returning.
                    // Submit success means accepted, never "I/O completed".
                    _disk.Submit(op, offset, (uint)length64, data, forceUnitAccess ? 1u : 0u, token);
                }
                catch { Forget(token); throw; }
                return false;
            }
        }
        catch (Exception error)
        {
            Report(error);
            status.SetSense(StorageUnitBase.SCSI_SENSE_MEDIUM_ERROR,
                op == 1 ? StorageUnitBase.SCSI_ADSENSE_UNRECOVERED_ERROR : StorageUnitBase.SCSI_ADSENSE_WRITE_ERROR);
            return true;
        }
    }

    internal void TrimInOrder(UnmapDescriptor[] descriptors, ref StorageUnitStatus status)
    {
        if (_readOnly)
        {
            status.SetSense(StorageUnitBase.SCSI_SENSE_DATA_PROTECT, StorageUnitBase.SCSI_ADSENSE_WRITE_PROTECT);
            return;
        }
        try
        {
            // Validate all ranges before applying any descriptor. Sync Trim uses the same Rust queue.
            foreach (var descriptor in descriptors)
            {
                ulong offset = checked(descriptor.BlockAddress * WinSpdDiskHost.SectorSize);
                ulong length = (ulong)descriptor.BlockCount * WinSpdDiskHost.SectorSize;
                if (offset > _disk.Capacity || length > _disk.Capacity - offset)
                    throw new IOException("释放块范围无效。");
            }
            lock (_admission)
            {
                if (!_accepting) { status.SetSense(StorageUnitBase.SCSI_SENSE_NOT_READY, StorageUnitBase.SCSI_ADSENSE_LUN_NOT_READY); return; }
                foreach (var descriptor in descriptors)
                    _disk.Trim(descriptor.BlockAddress * WinSpdDiskHost.SectorSize,
                        (ulong)descriptor.BlockCount * WinSpdDiskHost.SectorSize);
            }
        }
        catch (Exception error)
        {
            Report(error);
            status.SetSense(StorageUnitBase.SCSI_SENSE_MEDIUM_ERROR, StorageUnitBase.SCSI_ADSENSE_WRITE_ERROR);
        }
    }

    private void Pump()
    {
        try
        {
            while (!_stopPump)
            {
                if (!_disk.TryNextCompletion(out var completion, 100)) continue;
                Pending pending; bool found;
                lock (_pendingGate) found = _pending.TryGetValue(completion.Token, out pending);
                try
                {
                    if (!found) throw new IOException("Rust returned an unknown or duplicate completion token.");
                    var status = default(StorageUnitStatus);
                    IntPtr readData = IntPtr.Zero;
                    if (completion.Status != 0)
                    {
                        Report(new IOException(Marshal.PtrToStringUTF8(completion.Error) ?? "异步存储请求失败。"));
                        status.SetSense(StorageUnitBase.SCSI_SENSE_MEDIUM_ERROR,
                            pending.Op == 1 ? StorageUnitBase.SCSI_ADSENSE_UNRECOVERED_ERROR : StorageUnitBase.SCSI_ADSENSE_WRITE_ERROR);
                    }
                    else if (pending.Op == 1)
                    {
                        if (completion.Length != pending.Length || (completion.Length != 0 && completion.Data == IntPtr.Zero))
                        {
                            Report(new IOException("Rust read completion has an invalid length or buffer."));
                            status.SetSense(StorageUnitBase.SCSI_SENSE_MEDIUM_ERROR, StorageUnitBase.SCSI_ADSENSE_UNRECOVERED_ERROR);
                        }
                        else readData = completion.Data;
                    }
                    // Rust guarantees readData has >=1 MiB allocation, even for a smaller read.
                    // The native transaction probes MaxTransferLength, not just completion.Length.
                    _send(pending.Operation, status, readData);
                }
                catch (Exception error) { Fault(error); }
                finally
                {
                    try { _disk.ReleaseCompletion(completion.Token); }
                    catch (Exception error) { Fault(error); }
                    Forget(completion.Token); // Only idle after native delivery AND buffer release.
                }
            }
        }
        catch (Exception error) { Fault(error); }
        finally { _pumpExited.Set(); }
    }

    private void Fault(Exception error)
    {
        _accepting = false;
        if (_pumpError is null)
        {
            _pumpError = error;
            // This must not take _admission: the native ingress can hold it while
            // waiting for Rust budget credits that this failed pump can no longer release.
            try { _disk.CancelSubmissions(); }
            catch (Exception cancellationError) { _cancellationError = cancellationError; }
        }
        Report(error);
    }
    private void Forget(ulong token)
    {
        lock (_pendingGate)
        {
            _pending.Remove(token);
            if (_pending.Count == 0) _idle.Set();
        }
    }

    internal void CloseAndDrain()
    {
        if (!Monitor.TryEnter(_admission, TimeSpan.FromSeconds(30)))
            throw new IOException("磁盘提交入口未能停止；已保留主机和缓存，请检查错误后重试。", FaultCause());
        try { _accepting = false; }
        finally { Monitor.Exit(_admission); }
        // Drain doesn't depend on consumer release; the separate pump stays alive during this wait.
        _disk.Drain();
        WaitHandle.WaitAny(new[] { _idle.WaitHandle, _pumpExited.WaitHandle }, TimeSpan.FromSeconds(30));
        // Flush has a reserved control admission in Rust, even if a failed pump
        // left completion leases occupying the ordinary request budget.
        try { _disk.Flush(); }
        catch (Exception flushError)
        {
            throw new IOException("最终刷新失败；保留主机和缓存，未进行强制移除。",
                _pumpError is null ? flushError : new AggregateException(_pumpError, flushError));
        }
        if (PendingCount != 0)
            throw new IOException("异步磁盘请求尚未全部回复或完成线程已退出；保留主机和缓存，请检查错误后重试。", FaultCause());
        if (_pumpError is not null)
            throw new IOException("异步回复通道失败；缓存已尝试刷新，主机资源仍保留。", FaultCause());
    }
    private Exception? FaultCause() => _cancellationError is null ? _pumpError
        : new AggregateException("完成线程和取消提交均失败。", _pumpError!, _cancellationError);
    internal void StopPump()
    {
        if (PendingCount != 0) throw new InvalidOperationException("Cannot stop completion pump with pending I/O.");
        _stopPump = true;
        if (_pumpStarted && !_pump.Join(TimeSpan.FromSeconds(5)))
            throw new IOException("Completion pump did not stop; core resources must remain alive.");
    }
}

internal static class ActiveDisks
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
    internal static readonly Dictionary<Guid, Registration> Items = new();
    internal sealed class Registration(ulong capacity, bool readOnly = false)
    {
        internal ulong Capacity { get; } = capacity;
        internal bool ReadOnly { get; } = readOnly;
        internal readonly SemaphoreSlim OperationGate = new(1, 1);
        internal MountedVolume? Mounted;
        internal SafeFileHandle? LockedVolume;
        internal int ActiveFlushes;
        internal bool PreparationStarted;
        internal bool PreparedSafelyForRemoval;
        internal void BeginPreparation()
        {
            PreparationStarted = true;
            PreparedSafelyForRemoval = false;
        }
        internal void EnsureSafeForRemoval()
        {
            if (ActiveFlushes != 0) throw new IOException("磁盘仍有文件系统刷新操作，不能停止设备。");
            if (PreparationStarted && !PreparedSafelyForRemoval)
                throw new InvalidOperationException("卷可能已上线。请先安全卸载（包括未完成准备的卷），再停止虚拟磁盘。");
        }
    }
    internal sealed class Lease(Registration registration) : IDisposable
    {
        internal Registration Registration { get; } = registration;
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Registration.OperationGate.Release(); }
    }
    internal static async Task<Lease> AcquireAsync(Guid id, ulong? capacity = null)
    {
        Registration registration;
        await Gate.WaitAsync().ConfigureAwait(false);
        try { registration = Require(id, capacity); }
        finally { Gate.Release(); }
        await registration.OperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try { if (!ReferenceEquals(registration, Require(id, capacity))) throw new IOException("挂载实例已变化。"); }
            finally { Gate.Release(); }
            return new Lease(registration);
        }
        catch { registration.OperationGate.Release(); throw; }
    }
    internal static Registration Require(Guid id, ulong? capacity = null)
    {
        if (!Items.TryGetValue(id, out var item) || (capacity.HasValue && item.Capacity != capacity))
            throw new InvalidOperationException("拒绝操作：该磁盘不属于本进程成功启动的 WinSpd 实例。");
        return item;
    }
}
