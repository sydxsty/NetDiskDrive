using System.Buffers.Binary;
using System.Security.Cryptography;
using OverlayDisk.Cloud.Contracts;
using ZstdSharp;
using ZstdSharp.Unsafe;

internal static partial class Program
{
    private static readonly (string Name, Func<Task> Run)[] ObjectTransportTests =
    [
        ("object-transport: 4/8/16 MiB sparse sealed objects round-trip with stable canonical identity", TransportRoundTrips),
        ("object-transport: incompressible 16 MiB wire retains its fifth multipart fragment", TransportIncompressible),
        ("object-transport: raw payloads malformed envelopes truncation and digest changes reject", TransportEnvelopeRejection),
        ("object-transport: single-frame decoded-size window and dictionary bounds reject hostile frames", TransportFrameBounds),
        ("object-transport: changed short long cancelled and unsupported sources never become prepared uploads", TransportInputRejection),
        ("object-transport: source mutation owner disposal and retry preserve independently owned wire bytes", TransportOwnership)
    ];

    private static CanonicalObjectDescriptor TransportDescriptor(byte[] bytes, string name = "fixture")
        => new("/codec/" + name + ".obj", bytes.Length, Sha(bytes));

    private static async Task<byte[]> TransportWireAsync(PreparedObjectUpload upload)
    {
        using var source = upload.Wire.OpenRead(); using var destination = new MemoryStream();
        await source.CopyToAsync(destination); return destination.ToArray();
    }

    private static async Task TransportRoundTrips()
    {
        foreach (int length in new[] { 4 << 20, 8 << 20, 16 << 20 })
        {
            var sealedBytes = new byte[length]; new Random(length + 17).NextBytes(sealedBytes.AsSpan(0, 128 * 1024));
            var descriptor = TransportDescriptor(sealedBytes, "sparse-" + length);
            using var source = new TransportSource(sealedBytes);
            using var prepared = await PreparedObjectUpload.CreateAsync(descriptor, source);
            byte[] wire = await TransportWireAsync(prepared);
            Assert(source.ReadBytes == sealedBytes.Length && source.ReadCalls > 1 && !source.WasDisposed, "Codec reread or closed its caller-owned source");
            Assert(prepared.Canonical == descriptor && prepared.Wire.Descriptor.Path == descriptor.Path, "Canonical identity changed during transport preparation");
            Assert(wire.Length < sealedBytes.Length && wire.Length <= ObjectTransport.MaxWireLength(length), "Sparse zero tail was not represented by a bounded compressed wire object");
            Assert(prepared.Wire.Length == wire.Length && prepared.Wire.Sha256 == Sha(wire) && prepared.Wire.Md5 == Md5(wire), "Wire checksums or lengths describe the canonical bytes instead of the uploaded bytes");
            Assert(ObjectTransport.Decode(wire, descriptor).AsSpan().SequenceEqual(sealedBytes), "Codec altered sealed bytes during round-trip");
            using var repeated = await PreparedObjectUpload.CreateAsync(descriptor, new MemoryStream(sealedBytes, false));
            byte[] retry = await TransportWireAsync(repeated);
            Assert(wire.AsSpan().SequenceEqual(retry), "The same sealed object produced different retry bytes");
        }
    }

    private static async Task TransportIncompressible()
    {
        byte[] sealedBytes = new byte[16 << 20]; new Random(431901).NextBytes(sealedBytes);
        var descriptor = TransportDescriptor(sealedBytes, "random-16m");
        using var prepared = await PreparedObjectUpload.CreateAsync(descriptor, new MemoryStream(sealedBytes, false));
        byte[] wire = await TransportWireAsync(prepared);
        Assert(wire.Length > sealedBytes.Length && wire.Length <= ObjectTransport.MaxWireLength(sealedBytes.Length), "Random ciphertext fixture did not exercise encoded wire overhead beyond 16 MiB");
        Assert(wire.Length <= PreparedUpload.MaximumLength && prepared.Wire.PartMd5.Count == 5, "Encoded overhead was truncated or its fifth upload part omitted");
        for (int part = 0; part < prepared.Wire.PartMd5.Count; part++)
        {
            int offset = part * PreparedUpload.PartLength, count = Math.Min(PreparedUpload.PartLength, wire.Length - offset);
            string hash = Convert.ToHexString(MD5.HashData(wire.AsSpan(offset, count))).ToLowerInvariant();
            Assert(prepared.Wire.PartMd5[part] == hash && count > 0, "Multipart digest or final fragment length differs from encoded wire bytes");
        }
        Assert(ObjectTransport.Decode(wire, descriptor).AsSpan().SequenceEqual(sealedBytes), "Incompressible object round-trip failed");
    }

    private static async Task TransportEnvelopeRejection()
    {
        byte[] raw = new byte[4 << 20]; raw[27] = 11;
        var descriptor = TransportDescriptor(raw);
        using var prepared = await PreparedObjectUpload.CreateAsync(descriptor, new MemoryStream(raw, false));
        byte[] wire = await TransportWireAsync(prepared);
        TransportReject(() => ObjectTransport.Decode(raw, descriptor), "unencoded raw fallback");
        foreach (int offset in new[] { 0, 8, 12, 16, 20, 32, 64, 96, wire.Length - 1 })
        {
            byte[] corrupt = wire.ToArray(); corrupt[offset] ^= 1;
            TransportReject(() => ObjectTransport.Decode(corrupt, descriptor), "envelope or payload bit mutation at " + offset);
        }
        foreach (int length in new[] { 0, 127, 128, wire.Length - 1 })
            TransportReject(() => ObjectTransport.Decode(wire.AsSpan(0, length).ToArray(), descriptor), "truncation at " + length);
        byte[] trailing = new byte[wire.Length + 1]; wire.CopyTo(trailing, 0);
        TransportReject(() => ObjectTransport.Decode(trailing, descriptor), "unaccounted trailing byte");
        byte[] reserved = wire.ToArray(); reserved[20] = 1; TransportRehashEnvelope(reserved);
        TransportReject(() => ObjectTransport.Decode(reserved, descriptor), "reserved bits with a recomputed checksum");
        TransportReject(() => ObjectTransport.Decode(wire, descriptor with { Sha256 = new string('0', 64) }), "wrong canonical digest");
        TransportReject(() => ObjectTransport.Decode(wire, descriptor with { Codec = "raw" }), "unsupported codec downgrade");
        TransportReject(() => ObjectTransport.Decode(wire, descriptor with { Codec = "zstd-v1" }), "unknown codec version");
    }

    private static Task TransportFrameBounds()
    {
        byte[] raw = new byte[4 << 20]; var descriptor = TransportDescriptor(raw);
        byte[] frame = TransportCompress(raw);
        Assert(ObjectTransport.Decode(TransportEnvelope(frame, descriptor), descriptor).AsSpan().SequenceEqual(raw), "Independently assembled valid envelope was rejected");
        byte[] joined = new byte[frame.Length * 2]; frame.CopyTo(joined, 0); frame.CopyTo(joined, frame.Length);
        TransportReject(() => ObjectTransport.Decode(TransportEnvelope(joined, descriptor), descriptor), "concatenated zstd frames", "extra frames");
        TransportReject(() => ObjectTransport.Decode(TransportEnvelope(frame.Concat(new byte[] { 0 }).ToArray(), descriptor), descriptor), "trailing byte inside a rehashed envelope", "extra frames");
        TransportReject(() => ObjectTransport.Decode(TransportEnvelope(frame[..^1], descriptor), descriptor), "truncated frame inside a rehashed envelope");
        foreach (int actualLength in new[] { 3 << 20, 5 << 20 })
        {
            byte[] wrongLength = TransportCompress(new byte[actualLength]);
            TransportReject(() => ObjectTransport.Decode(TransportEnvelope(wrongLength, descriptor), descriptor), "declared canonical size differs from zstd content size", "decoded length");
        }
        byte[] noContentSize = TransportCompress(raw, contentSize: false);
        TransportReject(() => ObjectTransport.Decode(TransportEnvelope(noContentSize, descriptor), descriptor), "unknown frame content size", "decoded length");

        // A valid 5 MiB stream lies about its frame content size. Header checks
        // pass; the fixed 4 MiB output must still prevent decompression overflow.
        byte[] oversizedOutput = TransportCompress(new byte[5 << 20]);
        int fcs = 5 + ((oversizedOutput[4] & 0x20) == 0 ? 1 : 0) + TransportDictionaryBytes(oversizedOutput[4]);
        Assert(oversizedOutput[4] >> 6 == 2, "Fixture expected a 4-byte frame content size");
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedOutput.AsSpan(fcs), (uint)raw.Length);
        TransportReject(() => ObjectTransport.Decode(TransportEnvelope(oversizedOutput, descriptor), descriptor), "frame expanding beyond fixed destination");

        byte[] largeWindow;
        if ((frame[4] & 0x20) != 0)
        {
            largeWindow = new byte[frame.Length + 1]; frame.AsSpan(0, 5).CopyTo(largeWindow);
            largeWindow[4] &= 0xDF; largeWindow[5] = 0x78; frame.AsSpan(5).CopyTo(largeWindow.AsSpan(6));
        }
        else { largeWindow = frame.ToArray(); largeWindow[5] = 0x78; }
        TransportReject(() => ObjectTransport.Decode(TransportEnvelope(largeWindow, descriptor), descriptor), "32 MiB decompression window", "window");

        Assert(TransportDictionaryBytes(frame[4]) == 0, "Fixture unexpectedly references a dictionary");
        int dictAt = 5 + ((frame[4] & 0x20) == 0 ? 1 : 0);
        byte[] dictionary = new byte[frame.Length + 1]; frame.AsSpan(0, dictAt).CopyTo(dictionary);
        dictionary[4] |= 1; dictionary[dictAt] = 1; frame.AsSpan(dictAt).CopyTo(dictionary.AsSpan(dictAt + 1));
        TransportReject(() => ObjectTransport.Decode(TransportEnvelope(dictionary, descriptor), descriptor), "unavailable external dictionary", "dictionary");

        // Re-authenticate an envelope for different same-length content. The
        // decoder must verify the decompressed canonical digest, not only wire SHA.
        byte[] different = raw.ToArray(); different[44] = 3;
        TransportReject(() => ObjectTransport.Decode(TransportEnvelope(TransportCompress(different), descriptor), descriptor), "decoded canonical digest mismatch", "canonical digest");
        return Task.CompletedTask;
    }

    private static async Task TransportInputRejection()
    {
        byte[] raw = new byte[4 << 20]; new Random(147).NextBytes(raw.AsSpan(0, 4096));
        var descriptor = TransportDescriptor(raw);
        foreach (var (data, code) in new[] { (raw[..^1], "InputLengthMismatch"), (raw.Concat(new byte[] { 1 }).ToArray(), "InputLengthMismatch"), (new byte[raw.Length], "InputHashMismatch") })
        {
            using var source = new TransportSource(data);
            await Error(async () => { using var prepared = await PreparedObjectUpload.CreateAsync(descriptor, source); }, code);
            Assert(!source.WasDisposed, "Factory failure disposed the caller's source");
        }
        using (var changing = new TransportSource(raw, mutateSecondRead: true))
        {
            await Error(async () => { using var prepared = await PreparedObjectUpload.CreateAsync(descriptor, changing); }, "InputHashMismatch");
            Assert(changing.ReadCalls > 1 && !changing.WasDisposed, "Mutation fixture was not read or its ownership changed");
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using (var source = new TransportSource(raw))
        {
            bool rejected = false;
            try { using var prepared = await PreparedObjectUpload.CreateAsync(descriptor, source, canceled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Assert(rejected && source.ReadBytes == 0 && !source.WasDisposed, "Canceled preparation consumed or returned canonical payload");
        }
        foreach (var invalid in new[] { descriptor with { Codec = "raw" }, descriptor with { Length = 6 << 20 }, descriptor with { Path = "/codec/not-an-object.json" } })
        {
            using var source = new TransportSource(raw); bool rejected = false;
            try { using var prepared = await PreparedObjectUpload.CreateAsync(invalid, source); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected && source.ReadCalls == 0, "Invalid transport descriptor reached the canonical input stream");
        }
    }

    private static async Task TransportOwnership()
    {
        byte[] original = new byte[4 << 20]; new Random(923).NextBytes(original.AsSpan(0, 256 * 1024));
        byte[] mutableSource = original.ToArray(); var descriptor = TransportDescriptor(original);
        using var source = new TransportSource(mutableSource);
        using var prepared = await PreparedObjectUpload.CreateAsync(descriptor, source);
        byte[] first = await TransportWireAsync(prepared); Array.Fill(mutableSource, (byte)0xE2); source.Dispose();
        using var existingReader = prepared.Wire.OpenRead();
        Assert(!existingReader.CanWrite && existingReader is MemoryStream memory && !memory.TryGetBuffer(out _), "Prepared encoded buffer exposes writable caller memory");
        prepared.Dispose(); bool rejected = false;
        try { using var illegal = prepared.Wire.OpenRead(); } catch (ObjectDisposedException) { rejected = true; }
        Assert(rejected, "Disposed upload opened a new reader");
        using var copy = new MemoryStream(); await existingReader.CopyToAsync(copy); byte[] retained = copy.ToArray();
        Assert(retained.AsSpan().SequenceEqual(first), "Disposal or source mutation changed an active encoded reader");
        Assert(ObjectTransport.Decode(retained, descriptor).AsSpan().SequenceEqual(original), "Prepared lifetime did not preserve original sealed content");
        existingReader.Position = 0; using var replay = new MemoryStream(); await existingReader.CopyToAsync(replay);
        Assert(replay.ToArray().AsSpan().SequenceEqual(first), "Seeking and retrying an existing reader changed wire bytes");
    }

    private static byte[] TransportCompress(byte[] bytes, bool contentSize = true)
    {
        using var compressor = new Compressor(3);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_contentSizeFlag, contentSize ? 1 : 0);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, 0);
        return compressor.Wrap(bytes).ToArray();
    }
    private static int TransportDictionaryBytes(byte frameDescriptor) => (frameDescriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
    private static byte[] TransportEnvelope(byte[] compressed, CanonicalObjectDescriptor descriptor)
    {
        byte[] wire = new byte[128 + compressed.Length]; "ODZSTD02"u8.CopyTo(wire);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(8), 2);
        BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(12), descriptor.Length);
        BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(16), compressed.Length);
        Convert.FromHexString(descriptor.Sha256).CopyTo(wire, 32); compressed.CopyTo(wire, 128);
        SHA256.HashData(compressed).CopyTo(wire, 64); TransportRehashEnvelope(wire); return wire;
    }
    private static void TransportRehashEnvelope(byte[] wire) => SHA256.HashData(wire.AsSpan(0, 96)).CopyTo(wire, 96);
    private static void TransportReject(Action action, string scenario, string? expectedMessage = null)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            Assert(expectedMessage is null || error.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase), scenario + " failed at an unexpected guard: " + error.Message); return;
        }
        throw new Exception("Unsafe compressed object accepted: " + scenario);
    }
    private sealed class TransportSource(byte[] bytes, bool mutateSecondRead = false) : MemoryStream(bytes, false)
    {
        internal long ReadBytes; internal int ReadCalls; internal bool WasDisposed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = await base.ReadAsync(buffer, cancellationToken); ReadCalls++; ReadBytes += count;
            if (mutateSecondRead && ReadCalls == 2 && count != 0) buffer.Span[0] ^= 1;
            return count;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
