using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace OverlayDisk.Cloud.Contracts;

/// <summary>Public per-disk cloud key derivation parameters. No secret is stored here.</summary>
public sealed record CloudEncryptionSettings(int Version, string VolumeId, string Algorithm, string Kdf, int Iterations, string Salt, string KeyCheck)
{
    [JsonIgnore] public string Id => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"OverlayDisk/cloud-key-id/v1\n{Version}\n{VolumeId}\n{Algorithm}\n{Kdf}\n{Iterations}\n{Salt}\n{KeyCheck}"))).ToLowerInvariant();
}

/// <summary>One unlocked cloud disk key. Local containers and native objects remain plaintext.</summary>
public sealed class CloudEncryptionContext : IDisposable
{
    public const int Iterations = 600_000;
    private byte[]? key;
    private readonly object gate = new();
    private CloudEncryptionContext(CloudEncryptionSettings settings, byte[] key) { Settings = settings; this.key = key; }
    public CloudEncryptionSettings Settings { get; }

    public static CloudEncryptionContext Create(string volumeId, string password)
    {
        if (!Guid.TryParse(volumeId, out _)) throw new ArgumentException("A disk UUID is required.", nameof(volumeId));
        ArgumentException.ThrowIfNullOrEmpty(password);
        var settings = new CloudEncryptionSettings(1, volumeId, "aes-256-gcm", "pbkdf2-sha256", Iterations,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "");
        byte[] key = Derive(settings, password);
        settings = settings with { KeyCheck = Convert.ToBase64String(HMACSHA256.HashData(key, CheckInput(settings))) };
        return new(settings, key);
    }
    public static CloudEncryptionContext Unlock(CloudEncryptionSettings settings, string password)
    {
        ValidateSettings(settings); ArgumentException.ThrowIfNullOrEmpty(password);
        byte[] key = Derive(settings, password);
        try { ValidateKey(settings, key); return new(settings, key); }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
    }
    public static CloudEncryptionContext FromKey(CloudEncryptionSettings settings, ReadOnlySpan<byte> key)
    {
        ValidateSettings(settings);
        if (key.Length != 32) throw new IOException("云端密钥长度无效。");
        ValidateKey(settings, key); return new(settings, key.ToArray());
    }
    public byte[] ExportKey() { lock (gate) { ObjectDisposedException.ThrowIf(key is null, this); return key.ToArray(); } }
    public static void ValidateSettings(CloudEncryptionSettings settings)
    {
        if (settings is null || settings.Version != 1 || !Guid.TryParse(settings.VolumeId, out _) || settings.Algorithm != "aes-256-gcm" ||
            settings.Kdf != "pbkdf2-sha256" || settings.Iterations != Iterations || !IsBase64(settings.Salt, 32) || !IsBase64(settings.KeyCheck, 32))
            throw new IOException("云端加密参数无效或不受支持。");
    }
    private static bool IsBase64(string? text, int length)
    {
        if (text is null || text.Length != ((length + 2) / 3) * 4) return false;
        Span<byte> value = stackalloc byte[length]; return Convert.TryFromBase64String(text, value, out int count) && count == length;
    }
    private static byte[] Derive(CloudEncryptionSettings settings, string password)
        => Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(settings.Salt), Iterations, HashAlgorithmName.SHA256, 32);
    private static byte[] CheckInput(CloudEncryptionSettings settings) => Encoding.UTF8.GetBytes(
        $"OverlayDisk/cloud-key-check/v1\n{settings.Version}\n{settings.VolumeId}\n{settings.Algorithm}\n{settings.Kdf}\n{settings.Iterations}\n{settings.Salt}");
    private static void ValidateKey(CloudEncryptionSettings settings, ReadOnlySpan<byte> key)
    {
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, CheckInput(settings)), Convert.FromBase64String(settings.KeyCheck)))
            throw new IOException("云端密码不正确或加密参数已损坏。");
    }
    internal byte[] Nonce(ReadOnlySpan<byte> identity)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(key is null, this); return HMACSHA256.HashData(key, identity).AsSpan(0, 12).ToArray(); }
    }
    internal void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plain, Span<byte> cipher, Span<byte> tag, ReadOnlySpan<byte> aad)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(key is null, this); byte[] aesKey = HMACSHA256.HashData(key, "OverlayDisk/aes-key/v1"u8); try { using var aes = new AesGcm(aesKey, 16); aes.Encrypt(nonce, plain, cipher, tag, aad); } finally { CryptographicOperations.ZeroMemory(aesKey); } }
    }
    internal void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, Span<byte> plain, ReadOnlySpan<byte> aad)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(key is null, this); byte[] aesKey = HMACSHA256.HashData(key, "OverlayDisk/aes-key/v1"u8); try { using var aes = new AesGcm(aesKey, 16); aes.Decrypt(nonce, cipher, tag, plain, aad); } finally { CryptographicOperations.ZeroMemory(aesKey); } }
    }
    public string Authenticate(ReadOnlySpan<byte> message)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(key is null, this); return Convert.ToBase64String(HMACSHA256.HashData(key, message)); }
    }
    public bool Verify(ReadOnlySpan<byte> message, string? authentication)
    {
        if (!IsBase64(authentication, 32)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(Authenticate(message)), Convert.FromBase64String(authentication!));
    }
    public void Dispose() { lock (gate) { if (key is not null) CryptographicOperations.ZeroMemory(key); key = null; } }
}
