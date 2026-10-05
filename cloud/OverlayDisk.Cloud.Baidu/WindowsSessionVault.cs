using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OverlayDisk.Cloud.Baidu;

/// <summary>Current Windows user DPAPI encryption. Use only in the ordinary-privilege CloudHost.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsSessionVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OverlayDisk.Baidu.CookieSession.v1");
    private const int MaximumSessionBytes = 1024 * 1024;

    public static async Task SaveAsync(string path, BaiduCookieSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Validation occurs before serialization/persistence. No cookies are logged.
        _ = new BaiduCookieJar(session);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[] ciphertext;
        try
        {
            if (plaintext.Length > MaximumSessionBytes) throw new ArgumentException("Cookie session is too large.", nameof(session));
            ciphertext = Transform(plaintext, protect: true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        path = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, ".session-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<BaiduCookieSession?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
        if (stream.Length is <= 0 or > MaximumSessionBytes + 8192) throw new InvalidDataException("The saved Baidu session has an invalid size.");
        var ciphertext = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(ciphertext, cancellationToken).ConfigureAwait(false);
        var plaintext = Transform(ciphertext, protect: false);
        try
        {
            var session = JsonSerializer.Deserialize<BaiduCookieSession>(plaintext) ?? throw new InvalidDataException("The saved Baidu session is empty.");
            _ = new BaiduCookieJar(session);
            return session;
        }
        catch (JsonException) { throw new InvalidDataException("The saved Baidu session is invalid."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); CryptographicOperations.ZeroMemory(ciphertext); }
    }

    public static void Delete(string path) => File.Delete(path);

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Baidu session persistence requires Windows DPAPI.");
        var input = Allocate(bytes);
        var entropy = Allocate(Entropy);
        DataBlob output = default;
        try
        {
            var success = protect ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output) :
                CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows could not protect or unlock the saved Baidu session (" + Marshal.GetLastWin32Error() + ").");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Clear(input); Marshal.FreeHGlobal(input.Data);
            Clear(entropy); Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero) { Clear(output); _ = LocalFree(output.Data); }
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }
    private static void Clear(DataBlob blob) { for (var index = 0; index < blob.Size; index++) Marshal.WriteByte(blob.Data, index, 0); }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
