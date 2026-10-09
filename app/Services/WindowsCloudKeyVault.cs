using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OverlayDisk.Services;

internal static class WindowsCloudKeyVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OverlayDisk.CloudKey.v1");
    internal static string Protect(byte[] key)
    {
        if (key.Length != 32) throw new CryptographicException("云端密钥长度无效。");
        byte[] protectedBytes = Transform(key, true);
        try { return Convert.ToBase64String(protectedBytes); }
        finally { CryptographicOperations.ZeroMemory(protectedBytes); }
    }
    internal static byte[] Unprotect(string saved)
    {
        if (saved.Length > 16384) throw new CryptographicException("保存的云端密钥无效。");
        byte[] protectedBytes = Convert.FromBase64String(saved);
        try
        {
            byte[] key = Transform(protectedBytes, false);
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("保存的云端密钥长度无效。");
        }
        finally { CryptographicOperations.ZeroMemory(protectedBytes); }
    }
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cloud key persistence requires Windows DPAPI.");
        var input = Allocate(bytes);
        var entropy = Allocate(Entropy);
        DataBlob output = default;
        try
        {
            var success = protect ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output) :
                CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows could not protect or unlock the saved cloud key (" + Marshal.GetLastWin32Error() + ").");
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
