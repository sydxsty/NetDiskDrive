using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OverlayDisk.Worker;

internal static class WorkerProtocol
{
    internal const int MaximumFrameBytes = 1024 * 1024;
    internal const int MaximumBulkBytes = 16 * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };
    internal static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        "state", "disks.create", "disks.import", "disks.mount", "disks.unlock", "disks.flush", "disks.quiesce", "disks.close", "disks.settings", "disks.open", "disks.delete",
        "driver.install", "snapshots.list", "snapshots.create", "snapshots.rename", "snapshots.delete", "snapshots.restore", "snapshots.restorePause", "snapshots.restoreResume", "blocks.list",
        "reclaim.start", "reclaim.pause", "reclaim.resume", "compact.start", "compact.pause", "compact.resume", "compact.cancel", "compact.status", "cloud.status", "cloud.bind", "cloud.prepare", "cloud.objects",
        "cloud.delta", "cloud.transfer", "cloud.read", "cloud.receipts", "cloud.commit", "cloud.abandon", "cloud.commitBytes", "restore.begin", "restore.accept", "restore.status",
        "restore.preflight", "restore.step", "restore.finish", "restore.cancel", "blocks.summary", "blocks.query", "blocks.changes", "sync.diagnostics", "lazy.status",
        "prefetch.configure", "cache.status", "cache.source", "cache.configure", "cache.bind", "cache.online", "cache.step", "cloud.gc_candidates",
        "replica.prepare", "replica.apply", "replica.status", "replica.cancel", "replica.stageCurrent", "replica.stageCached"
    };

    internal static void ValidateMethod(string method)
    {
        if (!AllowedMethods.Contains(method)) throw new InvalidOperationException("不支持的磁盘服务操作。");
    }

    internal static async Task WriteAsync(Stream stream, object value, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length == 0 || payload.Length > MaximumFrameBytes) throw new InvalidDataException("磁盘服务消息超出大小限制。");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        try
        {
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    internal static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumFrameBytes) throw new InvalidDataException("磁盘服务消息长度无效。");
        byte[] payload = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            // JsonDocument.Parse(ReadOnlyMemory) retains that memory, so copy via Utf8JsonReader
            // before clearing the transport buffer (which may contain a disk password).
            return ParseOwned(payload);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    internal static async Task WritePacketAsync(Stream stream, object header, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length > MaximumBulkBytes) throw new InvalidDataException("对象传输超过 16 MiB。");
        await WriteAsync(stream, header, ct).ConfigureAwait(false);
        if (!bytes.IsEmpty) await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
    internal static async Task<WorkerReply> ReadPacketAsync(Stream stream, bool allowBinary, CancellationToken ct)
    {
        using var document = await ReadAsync(stream, ct).ConfigureAwait(false);
        var header = document.RootElement;
        int length = header.TryGetProperty("binaryLength", out var value) ? value.GetInt32() : 0;
        if (length < 0 || length > MaximumBulkBytes || (!allowBinary && length != 0)) throw new InvalidDataException("对象传输长度无效。");
        var bytes = new byte[length];
        try { if (length != 0) await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false); }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        return new(header.Clone(), bytes);
    }

    private static JsonDocument ParseOwned(byte[] payload)
    {
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 64 });
        return JsonDocument.ParseValue(ref reader);
    }

    internal static bool IsTokenValid(string expected, string? supplied)
    {
        if (supplied is null || supplied.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied));
    }

    internal static uint ClientProcessId(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) throw new System.ComponentModel.Win32Exception();
        return pid;
    }
    internal static uint ServerProcessId(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) throw new System.ComponentModel.Win32Exception();
        return pid;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
