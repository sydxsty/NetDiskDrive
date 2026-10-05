using System.Buffers;
using System.Text.Json;

namespace OverlayDisk;

/// <summary>Owns temporary native JSON storage; returned elements never borrow a pooled buffer.</summary>
internal static class NativeJsonBuffer
{
    internal static JsonElement Read(Action<byte[]> fill, int minimumLength = 131072, ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumLength);
        pool ??= ArrayPool<byte>.Shared;
        byte[] buffer = pool.Rent(minimumLength);
        try
        {
            if (buffer.Length < minimumLength) throw new IOException("本机输出缓冲区长度无效。");
            // Shared-pool storage may have been rented by another component. Keep
            // partial/failed native output independent of every previous renter.
            buffer.AsSpan().Clear();
            fill(buffer);
            int end = Array.IndexOf(buffer, (byte)0);
            if (end < 0) throw new IOException("本机 JSON 输出缺少终止符。");
            using var document = JsonDocument.Parse(buffer.AsMemory(0, end));
            return document.RootElement.Clone();
        }
        finally { pool.Return(buffer, clearArray: true); }
    }
}
