using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace OverlayDisk.Cloud.Contracts;

public sealed record CanonicalObjectDescriptor(string Path, int Length, string Sha256, string Codec = ObjectTransport.Codec, string? EncryptionId = null);

/// <summary>Receipt identity is canonical; the acknowledged CloudObjectInfo describes encoded wire bytes.</summary>
public interface ICloudEncodedObjectStore
{
    Task<CloudObjectInfo?> TryGetEncodedReceiptAsync(CanonicalObjectDescriptor descriptor, CancellationToken cancellationToken = default);
    Task<CloudObjectInfo> PutEncodedAsync(PreparedObjectUpload upload, CancellationToken cancellationToken = default);
}

/// <summary>Read an unknown exact wire length without metadata lookup; the stream enforces a strict upper bound and HTTP success.</summary>
public interface ICloudBoundedObjectReader
{
    Task<Stream> OpenReadBoundedAsync(string path, int maximumLength, CancellationToken cancellationToken = default);
}

/// <summary>An owned canonical validation plus its deterministic zstd transport representation.</summary>
public sealed class PreparedObjectUpload : IDisposable
{
    private PreparedObjectUpload(CanonicalObjectDescriptor canonical, PreparedUpload wire) { Canonical = canonical; Wire = wire; }
    public CanonicalObjectDescriptor Canonical { get; }
    public PreparedUpload Wire { get; }
    public static Task<PreparedObjectUpload> CreateAsync(CanonicalObjectDescriptor descriptor, Stream source, CancellationToken cancellationToken = default)
        => CreateAsync(descriptor, source, null, cancellationToken);
    public static async Task<PreparedObjectUpload> CreateAsync(CanonicalObjectDescriptor descriptor, Stream source, CloudEncryptionContext? encryption, CancellationToken cancellationToken = default)
    {
        ObjectTransport.ValidateDescriptor(descriptor); ObjectTransport.ValidateEncryption(descriptor, encryption); ArgumentNullException.ThrowIfNull(source);
        byte[] raw = new byte[descriptor.Length];
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); int position = 0;
            while (position < raw.Length)
            {
                int count = await source.ReadAsync(raw.AsMemory(position, Math.Min(64 * 1024, raw.Length - position)), cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new CloudProviderException("InputLengthMismatch", "Canonical object was truncated.");
                sha.AppendData(raw, position, count); position += count;
            }
            if (await source.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0) throw new CloudProviderException("InputLengthMismatch", "Canonical object exceeds its declared length.");
            if (!CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), Convert.FromHexString(descriptor.Sha256))) throw new CloudProviderException("InputHashMismatch", "Canonical object digest differs from the authenticated descriptor.");
            cancellationToken.ThrowIfCancellationRequested();
            byte[] frame = ObjectTransport.EncodeVerified(raw, descriptor, encryption);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(descriptor with { Sha256 = descriptor.Sha256.ToLowerInvariant() }, PreparedUpload.FromOwnedBytes(descriptor.Path, frame));
            }
            catch { CryptographicOperations.ZeroMemory(frame); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
    public void Dispose() => Wire.Dispose();
}

/// <summary>One bounded zstd frame, optionally encrypted after compression. Authentication precedes decompression.</summary>
public static class ObjectTransport
{
    public const string Codec = "zstd-v2";
    public const string EncryptedCodec = "zstd-aes256gcm-v2";
    public static bool IsSupportedCodec(string? codec) => codec is Codec or EncryptedCodec;
    public const int HeaderLength = 128;
    public const int MaximumWireLength = CloudObjectGeometry.MaximumSize + 128 * 1024;
    private static ReadOnlySpan<byte> Magic => "ODZSTD02"u8;
    public static int MaxWireLength(int canonicalLength) { CloudObjectGeometry.Validate(canonicalLength); return checked(Compressor.GetCompressBound(canonicalLength) + HeaderLength); }
    public static void ValidateDescriptor(CanonicalObjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor); CloudObjectGeometry.Validate(descriptor.Length);
        if (!IsSupportedCodec(descriptor.Codec)) throw new ArgumentException("Unsupported object transport codec.", nameof(descriptor));
        if (descriptor.Codec == EncryptedCodec ? descriptor.EncryptionId is not { Length: 64 } || !descriptor.EncryptionId.All(Uri.IsHexDigit) : descriptor.EncryptionId is not null)
            throw new ArgumentException("Object encryption identity does not match its transport codec.", nameof(descriptor));
        PreparedUpload.ValidateDescriptor(new(descriptor.Path, descriptor.Length, descriptor.Sha256));
        if (!descriptor.Path.EndsWith(".obj", StringComparison.Ordinal)) throw new ArgumentException("Encoded transport is only for immutable .obj payloads.", nameof(descriptor));
    }
    internal static byte[] EncodeVerified(byte[] raw, CanonicalObjectDescriptor descriptor, CloudEncryptionContext? encryption)
    {
        using var compressor = new Compressor(3);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_contentSizeFlag, 1);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 0);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, 0);
        var compressed = compressor.Wrap(raw);
        try
        {
        if (encryption is not null) return EncodeEncrypted(compressed, descriptor, encryption);
        byte[] frame = new byte[HeaderLength + compressed.Length]; var header = frame.AsSpan(0, HeaderLength);
        Magic.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], descriptor.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], compressed.Length);
        // Bytes 20..31 are reserved and must stay zero.
        Convert.FromHexString(descriptor.Sha256).CopyTo(header[32..64]);
        SHA256.HashData(compressed).CopyTo(header[64..96]);
        SHA256.HashData(header[..96]).CopyTo(header[96..128]);
        compressed.CopyTo(frame.AsSpan(HeaderLength)); return frame;
        }
        finally { CryptographicOperations.ZeroMemory(compressed); }
    }
    public static byte[] Decode(byte[] wire, CanonicalObjectDescriptor descriptor, CloudEncryptionContext? encryption = null)
    {
        ValidateDescriptor(descriptor); ValidateEncryption(descriptor, encryption); ArgumentNullException.ThrowIfNull(wire);
        if (encryption is not null) return DecodeEncrypted(wire, descriptor, encryption);
        if (wire.Length < HeaderLength + 4 || wire.Length > MaxWireLength(descriptor.Length)) throw new IOException("Compressed object length is outside the bounded envelope.");
        ReadOnlySpan<byte> header = wire.AsSpan(0, HeaderLength), payload = wire.AsSpan(HeaderLength);
        if (!header[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != 2 ||
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != descriptor.Length || BinaryPrimitives.ReadInt32LittleEndian(header[16..]) != payload.Length ||
            header[20..32].IndexOfAnyExcept((byte)0) >= 0 ||
            !CryptographicOperations.FixedTimeEquals(header[32..64], Convert.FromHexString(descriptor.Sha256)) ||
            !CryptographicOperations.FixedTimeEquals(header[96..128], SHA256.HashData(header[..96])) ||
            !CryptographicOperations.FixedTimeEquals(header[64..96], SHA256.HashData(payload))) throw new IOException("Compressed object envelope identity or checksum is invalid.");
        return Decompress(payload, descriptor);
    }
    private static unsafe byte[] Decompress(ReadOnlySpan<byte> payload, CanonicalObjectDescriptor descriptor)
    {
        if (payload.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(payload) != 0xfd2fb528) throw new IOException("A single standard zstd frame is required.");
        fixed (byte* encoded = payload)
        {
            ZSTD_frameHeader parsed = default;
            if (Methods.ZSTD_getFrameHeader(&parsed, encoded, (nuint)payload.Length) != 0 || parsed.windowSize > CloudObjectGeometry.MaximumSize || parsed.dictID != 0)
                throw new IOException("Compressed object window or dictionary exceeds the supported profile.");
            nuint frameSize = Methods.ZSTD_findFrameCompressedSize(encoded, (nuint)payload.Length);
            if (Methods.ZSTD_isError(frameSize) || frameSize != (nuint)payload.Length || Methods.ZSTD_getFrameContentSize(encoded, (nuint)payload.Length) != (ulong)descriptor.Length)
                throw new IOException("Compressed object has extra frames, trailing data or an invalid decoded length.");
        }
        byte[] raw = new byte[descriptor.Length];
        try
        {
            using var decompressor = new Decompressor(); decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 24);
            if (decompressor.Unwrap(payload, raw) != raw.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw), Convert.FromHexString(descriptor.Sha256)))
                throw new IOException("Decoded object length or canonical digest is invalid.");
            return raw;
        }
        catch (ZstdException error) { CryptographicOperations.ZeroMemory(raw); throw new IOException("Compressed object data is invalid.", error); }
        catch { CryptographicOperations.ZeroMemory(raw); throw; }
    }
    internal static void ValidateEncryption(CanonicalObjectDescriptor descriptor, CloudEncryptionContext? encryption)
    {
        if (descriptor.Codec == EncryptedCodec ? encryption is null || descriptor.EncryptionId != encryption.Settings.Id : encryption is not null)
            throw new IOException("云端对象加密设置与当前解锁密钥不一致。");
    }
    private static byte[] Identity(CanonicalObjectDescriptor descriptor) => Encoding.UTF8.GetBytes(
        $"OverlayDisk/object-nonce/v2\n{descriptor.Path}\n{descriptor.Length}\n{descriptor.Sha256.ToLowerInvariant()}\n{descriptor.EncryptionId}");
    private static byte[] Aad(ReadOnlySpan<byte> header, CanonicalObjectDescriptor descriptor)
    {
        byte[] identity = Identity(descriptor); byte[] aad = new byte[header.Length + identity.Length];
        header.CopyTo(aad); identity.CopyTo(aad, header.Length); return aad;
    }
    private static byte[] EncodeEncrypted(ReadOnlySpan<byte> compressed, CanonicalObjectDescriptor descriptor, CloudEncryptionContext encryption)
    {
        byte[] wire = new byte[HeaderLength + compressed.Length]; var header = wire.AsSpan(0, HeaderLength);
        "ODZGCM02"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], descriptor.Length); BinaryPrimitives.WriteInt32LittleEndian(header[16..], compressed.Length);
        byte[] identity = Identity(descriptor), compressedHash = SHA256.HashData(compressed);
        byte[] nonceInput = new byte[identity.Length + compressedHash.Length]; identity.CopyTo(nonceInput, 0); compressedHash.CopyTo(nonceInput, identity.Length);
        encryption.Nonce(nonceInput).CopyTo(header[20..32]); Convert.FromHexString(encryption.Settings.Id).CopyTo(header[32..64]);
        // The nonce is pseudorandom per immutable identity and stable for retry after lost acknowledgement.
        // Bytes 64..79 hold the GCM tag; remaining reserved bytes are authenticated and zero.
        byte[] aad = Aad(header, descriptor);
        encryption.Encrypt(header[20..32], compressed, wire.AsSpan(HeaderLength), header[64..80], aad);
        return wire;
    }
    private static byte[] DecodeEncrypted(byte[] wire, CanonicalObjectDescriptor descriptor, CloudEncryptionContext encryption)
    {
        if (wire.Length < HeaderLength + 4 || wire.Length > MaxWireLength(descriptor.Length)) throw new IOException("Encrypted object length is outside the bounded envelope.");
        var header = wire.AsSpan(0, HeaderLength); var cipher = wire.AsSpan(HeaderLength);
        if (!header[..8].SequenceEqual("ODZGCM02"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != 2 ||
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != descriptor.Length || BinaryPrimitives.ReadInt32LittleEndian(header[16..]) != cipher.Length ||
            header[80..].IndexOfAnyExcept((byte)0) >= 0 || !CryptographicOperations.FixedTimeEquals(header[32..64], Convert.FromHexString(encryption.Settings.Id)))
            throw new IOException("Encrypted object identity is invalid.");
        byte[] authenticatedHeader = header.ToArray(); authenticatedHeader.AsSpan(64, 16).Clear();
        byte[] compressed = new byte[cipher.Length];
        try
        {
            encryption.Decrypt(header[20..32], cipher, header[64..80], compressed, Aad(authenticatedHeader, descriptor));
            // Never inspect or decompress unauthenticated bytes.
            return Decompress(compressed, descriptor);
        }
        catch (CryptographicException error) { throw new IOException("云端对象解密认证失败：密码、对象身份或内容不匹配。", error); }
        finally { CryptographicOperations.ZeroMemory(compressed); }
    }

}
