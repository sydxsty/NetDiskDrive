using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using OverlayDisk.Worker;

namespace OverlayDisk;

[StructLayout(LayoutKind.Sequential)]
public struct CoreCompletion
{
    public ulong Token;
    public int Status;
    public uint Length;
    public IntPtr Data;
    public IntPtr Error;
}

public sealed class CoreDisk : IDisposable
{
    private readonly ReaderWriterLockSlim lifetime = new();
    private IntPtr handle;
    private readonly object hydrationGate = new();
    private readonly object mountModeGate = new();
    private volatile bool readOnly;
    private DiskHydrationService? hydration;
    private ObjectProviderCallback? objectProviderCallback;
    private readonly ConcurrentDictionary<ulong, (ulong Offset, uint Length)> lazyReads = new();
    private bool disposing;
    private PrefetchSettings prefetchSettings = new();
    private long prefetchRevision;
    public bool IsLazy { get; private set; }
    public Guid Id { get; }
    public ulong Capacity { get; }
    public uint ObjectSizeBytes { get; }
    public bool Encrypted { get; }
    public bool IsReadOnly => readOnly;
    internal Action? BeforeDispose { get; set; }

    public void SetReadOnly(bool enabled)
    {
        lock (mountModeGate)
        {
            if (readOnly == enabled) return;
            ActiveDisks.Gate.Wait();
            try { if (ActiveDisks.Items.ContainsKey(Id)) throw new IOException("请先安全卸载磁盘，再切换只读或读写模式。"); }
            finally { ActiveDisks.Gate.Release(); }
            // The native queue barrier drains earlier writes and changes the logical
            // write guard. Keep a shared lifetime lease so hydration can still complete.
            WithHandle(h => Check(Native.od_v4_set_read_only(h, enabled ? 1u : 0u)));
            readOnly = enabled;
        }
    }
    internal void RequireWritable()
    {
        if (IsReadOnly) throw new IOException("这块磁盘当前为只读，不能写入或回收逻辑扇区。请安全卸载后切换为读写模式。");
    }

    public static void Create(string directory, ulong capacity, string? password, uint objectSizeBytes = 4 * 1024 * 1024)
    {
        ValidateObjectSize(objectSizeBytes);
        Check(Native.od_v4_create_sized(directory, capacity, password, objectSizeBytes));
    }

    internal static void ValidateObjectSize(uint size)
    {
        if (size is not (4 * 1024 * 1024 or 8 * 1024 * 1024 or 16 * 1024 * 1024))
            throw new ArgumentOutOfRangeException(nameof(size), "对象大小请选择 4、8 或 16 MiB。");
    }

    internal static uint ObjectSizeFromInfo(JsonElement info)
    {
        if (!info.TryGetProperty("object_size", out var value) || !value.TryGetUInt32(out uint size))
            throw new IOException("磁盘缺少有效对象大小；此版本不自动兼容或迁移旧格式。");
        ValidateObjectSize(size); return size;
    }

    public static JsonElement Inspect(string path)
    {
        if (Directory.Exists(path)) throw new IOException("这是旧版目录格式。请使用 0.1.1 打开，或创建新的 .odv4 磁盘。");
        var bytes = new byte[16384];
        Check(Native.od_v4_inspect(path, bytes, (uint)bytes.Length));
        return ParseJson(bytes);
    }

    public CoreDisk(string directory, string? password)
    {
        handle = Native.od_v4_open(directory, password);
        if (handle == IntPtr.Zero) throw Error();
        try
        {
            var info = GetInfo();
            Id = Guid.Parse(info.GetProperty("id").GetString()!);
            Capacity = Native.od_v4_capacity(handle);
            Encrypted = info.GetProperty("encrypted").GetBoolean();
            ObjectSizeBytes = ObjectSizeFromInfo(info);
        }
        catch { Native.od_v4_close(handle); handle = IntPtr.Zero; throw; }
    }

    public JsonElement GetInfo()
    {
        lifetime.EnterReadLock();
        try
        {
            EnsureOpen();
            return NativeJsonBuffer.Read(bytes => Check(Native.od_v4_info(handle, bytes, (uint)bytes.Length)), 16384);
        }
        finally { lifetime.ExitReadLock(); }
    }

    public void Read(ulong offset, byte[] buffer, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(length));
        lifetime.EnterReadLock();
        try { EnsureOpen(); Check(Native.od_v4_read(handle, offset, buffer, (uint)length)); }
        finally { lifetime.ExitReadLock(); }
        hydration?.AfterRead(offset, (uint)length, Capacity);
    }
    public void Write(ulong offset, byte[] buffer, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(length));
        lifetime.EnterReadLock();
        try { EnsureOpen(); RequireWritable(); Check(Native.od_v4_write(handle, offset, buffer, (uint)length)); }
        finally { lifetime.ExitReadLock(); }
    }
    public void Flush() => WithHandle(h => Check(Native.od_v4_flush(h)));
    public void Trim(ulong offset, ulong length)
    {
        lifetime.EnterReadLock();
        try { EnsureOpen(); RequireWritable(); Check(Native.od_v4_trim(handle, offset, length)); }
        finally { lifetime.ExitReadLock(); }
    }
    public void Compact()
    {
        var status = Control(new { cmd = "compact.status" });
        if (status.TryGetProperty("state", out var state) && state.GetString() is "running" or "paused"
            && status.TryGetProperty("mode", out var mode) && mode.GetString() == "deep")
            throw new IOException("已有深度整理任务尚未完成，不能自动改为普通整理或继续深度整理。");
        Control(new { cmd = "compact.start", mode = "normal" });
        Control(new { cmd = "compact.pause", paused = false });
        while (true)
        {
            var result = Control(new { cmd = "compact.step", max_objects = 4 });
            if (result.GetProperty("state").GetString() is "idle" or "done") return;
        }
    }
    public void Drain() => WithHandle(h => Check(Native.od_v4_drain(h)));
    public void CancelSubmissions() => WithHandle(h => Check(Native.od_v4_cancel_submissions(h)));
    public void Submit(uint op, ulong offset, uint length, IntPtr data, uint flags, ulong token)
    {
        bool tracked = IsLazy && op == 1;
        if (tracked && !lazyReads.TryAdd(token, (offset, length))) throw new IOException("重复的按需读取请求标识。");
        try
        {
            lifetime.EnterReadLock();
            try { EnsureOpen(); if (op is 2 or 4) RequireWritable(); Check(Native.od_v4_submit(handle, op, offset, length, data, flags, token)); }
            finally { lifetime.ExitReadLock(); }
        }
        catch { if (tracked) lazyReads.TryRemove(token, out _); throw; }
    }
    public bool TryNextCompletion(out CoreCompletion completion, uint timeoutMs)
    {
        lifetime.EnterReadLock();
        try
        {
            EnsureOpen();
            int result = Native.od_v4_next_completion(handle, out completion, timeoutMs);
            if (result == 1) return false;
            Check(result);
            if (lazyReads.TryRemove(completion.Token, out var read) && completion.Status == 0)
                hydration?.AfterRead(read.Offset, read.Length, Capacity);
            return true;
        }
        finally { lifetime.ExitReadLock(); }
    }
    public void ReleaseCompletion(ulong token)
    {
        lifetime.EnterReadLock();
        try { EnsureOpen(); Check(Native.od_v4_release_completion(handle, token)); }
        finally { lifetime.ExitReadLock(); }
    }

    public string CreateSnapshot()
    {
        string value = "";
        WithHandle(h =>
        {
            var bytes = new byte[256];
            Check(Native.od_v4_snapshot_create(h, bytes, (uint)bytes.Length));
            value = Encoding.UTF8.GetString(bytes, 0, Array.IndexOf(bytes, (byte)0));
        });
        return value;
    }
    public JsonElement SnapshotManifest(string snapshot)
    {
        JsonElement value = default;
        WithHandle(h =>
        {
            int size = Native.od_v4_snapshot_manifest(h, snapshot, null, 0);
            if (size <= 0 || size > 64 * 1024 * 1024) throw Error();
            var bytes = new byte[size];
            int result = Native.od_v4_snapshot_manifest(h, snapshot, bytes, (uint)bytes.Length);
            if (result <= 0 || result > bytes.Length) throw Error();
            value = ParseJson(bytes);
        });
        return value;
    }
    public void ReadObject(string snapshot, string objectId, ulong offset, byte[] buffer)
        => WithHandle(h => Check(Native.od_v4_object_read(h, snapshot, objectId, offset, buffer, (uint)buffer.Length)));
    public void ReleaseSnapshot(string snapshot) => WithHandle(h => Check(Native.od_v4_snapshot_release(h, snapshot)));

    public JsonElement Control(object request)
    {
        string json = request is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(request);
        lifetime.EnterReadLock();
        try
        {
            EnsureOpen();
            return NativeJsonBuffer.Read(output => Check(Native.od_v4_control(handle, json, output, (uint)output.Length)));
        }
        finally { lifetime.ExitReadLock(); }
    }

    public byte[] ReadExport(string jobId, string objectId, ulong offset = 0, int? length = null)
    {
        int count = length ?? checked((int)ObjectSizeBytes);
        if (count < 0 || offset > ObjectSizeBytes || (ulong)count > ObjectSizeBytes - offset) throw new ArgumentOutOfRangeException(nameof(length));
        var result = new byte[count];
        WithHandle(h => Check(Native.od_v4_read_export(h, jobId, objectId, offset, result, (uint)count)));
        return result;
    }

    private CoreDisk(IntPtr restored)
    {
        handle = restored;
        if (handle == IntPtr.Zero) throw Error();
        try
        {
            var info = GetInfo(); Id = Guid.Parse(info.GetProperty("id").GetString()!);
            Capacity = Native.od_v4_capacity(handle); Encrypted = info.GetProperty("encrypted").GetBoolean();
            ObjectSizeBytes = ObjectSizeFromInfo(info);
        }
        catch { Native.od_v4_close(handle); handle = IntPtr.Zero; throw; }
    }

    public static CoreDisk BeginRestore(string path, byte[] rootObject, string? password)
    {
        ValidateObjectSize(checked((uint)rootObject.Length));
        return new(Native.od_v4_restore_begin(path, rootObject, (uint)rootObject.Length, password));
    }

    public static CoreDisk BeginLazyRestore(string path, byte[] rootObject, string? password, JsonElement backing)
    {
        ValidateObjectSize(checked((uint)rootObject.Length));
        return new(Native.od_v4_lazy_begin(path, rootObject, (uint)rootObject.Length, password, backing.GetRawText()));
    }

    public static CoreDisk BeginRestore(string path, byte[] rootObject, string? password, JsonElement options)
    {
        ValidateObjectSize(checked((uint)rootObject.Length));
        return new(Native.od_v4_restore_begin_options(path, rootObject, (uint)rootObject.Length, password, options.GetRawText()));
    }

    public JsonElement GetLazyStatus() => Control(new { cmd = "lazy.status" });
    public object? GetHydrationState() => hydration?.GetState();
    public void ConfigurePrefetch(PrefetchSettings settings, long revision = 0)
    {
        lock (hydrationGate)
        {
            ArgumentNullException.ThrowIfNull(settings);
            settings.Validate();
            ArgumentOutOfRangeException.ThrowIfNegative(revision);
            ObjectDisposedException.ThrowIf(disposing, this);
            // Global broadcasts and a newly opened core may finish in a
            // different order; an earlier revision cannot undo a later choice.
            // Zero remains an explicit standalone configuration for fixtures.
            if (revision > 0 && revision < prefetchRevision) return;
            hydration?.Configure(settings);
            prefetchSettings = settings;
            if (revision > 0) prefetchRevision = revision;
        }
    }
    public void SetObjectProvider(Func<LazyObjectRequest, CancellationToken, Task<byte[]>> provider)
    {
        lock (hydrationGate)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            if (hydration is not null) return;
            IsLazy = GetLazyStatus().GetProperty("enabled").GetBoolean();
            if (!IsLazy) return;
            hydration = new DiskHydrationService(Id.ToString(), provider,
                (offset, length, limit) => Control(new { cmd = "lazy.needs", offset, length, operation = "read", limit }), ImportLazyObject, ObjectSizeBytes, prefetchSettings);
            objectProviderCallback = FetchObject;
            try { WithHandle(h => Check(Native.od_v4_set_object_provider(h, objectProviderCallback, IntPtr.Zero))); }
            catch { hydration.Dispose(); hydration = null; objectProviderCallback = null; throw; }
        }
    }
    public void ImportLazyObject(string objectId, byte[] bytes)
    {
        if (bytes.Length != ObjectSizeBytes) throw new IOException("云端对象长度与磁盘格式不符。");
        WithHandle(h => Check(Native.od_v4_lazy_import(h, objectId, bytes, (uint)bytes.Length)));
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectProviderCallback(IntPtr context, IntPtr objectId, IntPtr sha256, IntPtr output, uint length, IntPtr error, uint errorLength);
    private int FetchObject(IntPtr context, IntPtr objectId, IntPtr sha256, IntPtr output, uint length, IntPtr error, uint errorLength)
    {
        try
        {
            if (length != ObjectSizeBytes || output == IntPtr.Zero) throw new IOException("内核按需读取缓冲区无效。");
            string id = Marshal.PtrToStringUTF8(objectId) ?? throw new IOException("缺少对象标识。");
            string hash = Marshal.PtrToStringUTF8(sha256) ?? throw new IOException("缺少对象校验信息。");
            byte[] bytes = (hydration ?? throw new IOException("未配置云端对象读取服务。")).GetAsync(id, hash).GetAwaiter().GetResult();
            if (bytes.Length != length) throw new IOException("按需下载的对象长度与磁盘格式不符。");
            Marshal.Copy(bytes, 0, output, bytes.Length); return 0;
        }
        catch (Exception failure)
        {
            if (error != IntPtr.Zero && errorLength != 0)
            {
                byte[] message = Encoding.UTF8.GetBytes(failure.Message);
                int count = (int)Math.Min((uint)message.Length, errorLength - 1);
                Marshal.Copy(message, 0, error, count); Marshal.WriteByte(error, count, 0);
            }
            return -1;
        }
    }

    public static CoreDisk BeginSnapshotRestore(CoreDisk source, string snapshotId, string target, string? password)
    {
        IntPtr created = IntPtr.Zero;
        source.WithHandle(h => created = Native.od_v4_restore_snapshot_begin(h, snapshotId, target, password));
        return new CoreDisk(created);
    }

    public static void ResumeSnapshotRestore(CoreDisk target, CoreDisk source)
        => source.WithHandle(s => target.WithHandle(t => Check(Native.od_v4_restore_snapshot_resume(t, s))));

    public void AcceptRestoreObject(string objectId, byte[] bytes)
    {
        if (bytes.Length != ObjectSizeBytes) throw new IOException("恢复对象长度与磁盘格式不符。");
        WithHandle(h => Check(Native.od_v4_restore_accept(h, objectId, bytes, (uint)bytes.Length)));
    }

    private static JsonElement ParseJson(byte[] bytes)
    {
        int end = Array.IndexOf(bytes, (byte)0);
        using var doc = JsonDocument.Parse(bytes.AsMemory(0, end < 0 ? bytes.Length : end));
        return doc.RootElement.Clone();
    }
    private void WithHandle(Action<IntPtr> action)
    {
        lifetime.EnterReadLock();
        try { EnsureOpen(); action(handle); }
        finally { lifetime.ExitReadLock(); }
    }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
    public void Dispose()
    {
        lock (hydrationGate)
        {
            disposing = true;
            // Worker-owned maintenance must finish while this handle can still
            // service its final bounded control call and provider callbacks.
            BeforeDispose?.Invoke(); BeforeDispose = null;
            // Wake callback waits before taking the write lock. Serializing disposers
            // also prevents a second close from overtaking a prefetch that is draining.
            hydration?.Dispose();
            lifetime.EnterWriteLock();
            try
            {
                if (handle == IntPtr.Zero) return;
                if (objectProviderCallback is not null) Check(Native.od_v4_set_object_provider(handle, null, IntPtr.Zero));
                Native.od_v4_close(handle);
                handle = IntPtr.Zero;
                objectProviderCallback = null; hydration = null; lazyReads.Clear();
            }
            finally { lifetime.ExitWriteLock(); }
        }
        GC.SuppressFinalize(this);
    }
    private static IOException Error() => new(Marshal.PtrToStringUTF8(Native.od_v4_last_error()) ?? "本地存储操作失败。");
    private static void Check(int status) { if (status != 0) throw Error(); }

    private static class Native
    {
        private const string Lib = "overlaydisk_core";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_create_sized([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ulong capacity, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password, uint objectSizeBytes);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_set_read_only(IntPtr handle, uint enabled);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr od_v4_restore_begin_options([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] root, uint length, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password, [MarshalAs(UnmanagedType.LPUTF8Str)] string optionsJson);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_set_object_provider(IntPtr handle, ObjectProviderCallback? callback, IntPtr context);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr od_v4_lazy_begin([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] root, uint length, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password, [MarshalAs(UnmanagedType.LPUTF8Str)] string backingJson);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_lazy_import(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectId, byte[] data, uint length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_create([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ulong capacity, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr od_v4_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_inspect([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] output, uint length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_control(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string request, [Out] byte[] output, uint length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_read_export(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string jobId, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectId, ulong offset, [Out] byte[] output, uint length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr od_v4_restore_begin([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] rootObject, uint length, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr od_v4_restore_snapshot_begin(IntPtr source, [MarshalAs(UnmanagedType.LPUTF8Str)] string snapshotId, [MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_restore_snapshot_resume(IntPtr target, IntPtr source);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_restore_accept(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectId, byte[] bytes, uint length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_read(IntPtr handle, ulong offset, [Out] byte[] buffer, uint length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_write(IntPtr handle, ulong offset, byte[] buffer, uint length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_flush(IntPtr handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_trim(IntPtr handle, ulong offset, ulong length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_compact(IntPtr handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong od_v4_capacity(IntPtr handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_info(IntPtr handle, [Out] byte[] buffer, uint length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void od_v4_close(IntPtr handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr od_v4_last_error();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_submit(IntPtr handle, uint op, ulong offset, uint length, IntPtr data, uint flags, ulong token);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_next_completion(IntPtr handle, out CoreCompletion completion, uint timeoutMs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_release_completion(IntPtr handle, ulong token);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_drain(IntPtr handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_cancel_submissions(IntPtr handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_snapshot_create(IntPtr handle, [Out] byte[] id, uint length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_snapshot_manifest(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, [Out] byte[]? json, uint length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_object_read(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string snapshot, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectId, ulong offset, [Out] byte[] buffer, uint length);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int od_v4_snapshot_release(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string snapshot);
    }
}
