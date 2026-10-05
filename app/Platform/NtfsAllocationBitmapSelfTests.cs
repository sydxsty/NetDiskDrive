using System.Buffers.Binary;
using System.Text;

namespace OverlayDisk;

/// <summary>Pure in-memory NTFS fixtures. Never opens a file, device, volume or native handle.</summary>
internal static class NtfsAllocationBitmapSelfTests
{
    internal static IReadOnlyList<string> Run()
    {
        var results = new List<string>();
        var fragmented = new Fixture();
        var bitmap = fragmented.Open();
        Check(bitmap.Clusters == Fixture.ClusterCount && fragmented.StoredSectors < 128, "sparse fixture geometry");
        VerifyAllPages(fragmented, bitmap, 37);
        VerifyPage(fragmented, bitmap, 32765, 17); // Crosses the two noncontiguous bitmap extents.
        Check(fragmented.Reads.Any(r => r.Offset <= fragmented.RecordOffset(6) && r.Offset + (ulong)r.Length > fragmented.RecordOffset(6)),
            "record 6 must be fetched through the second MFT extent");
        results.Add("Raw NTFS bitmap: fragmented MFT and bitmap streams match an independent allocation model using aligned bounded reads only.");

        var backwards = new Fixture(bitmapRuns: [new(96, 1), new(80, 1)]);
        VerifyAllPages(backwards, backwards.Open(), 511);
        results.Add("Raw NTFS bitmap: negative runlist deltas preserve the logical bitmap order.");

        VerifyPage(fragmented, bitmap, Fixture.ClusterCount - 1, 1);
        byte[] last = bitmap.ReadPage(Fixture.ClusterCount - 1, 1);
        var decoded = NtfsSpaceReclaimer.DecodeBitmap(last, last.Length, Fixture.ClusterCount - 1, Fixture.ClusterCount);
        Check(decoded.Next == Fixture.ClusterCount && decoded.Ranges.Count == 1
            && decoded.Ranges[0] == (Fixture.ClusterCount - 1, 1UL), "partial last byte cannot expose padding clusters");
        Reject(() => bitmap.ReadPage(Fixture.ClusterCount), "out-of-range bitmap cursor");
        Reject(() => bitmap.ReadPage(0, 0), "empty bitmap page");
        Reject(() => bitmap.ReadPage(0, 65537), "unbounded bitmap page");
        results.Add("Raw NTFS bitmap: rounded cursors, cross-extent pages and the final five valid bits never return a range beyond the filesystem.");

        foreach (uint record in new uint[] { 0, 6 })
        {
            RejectFixture(f => f.EditRecord(record, bytes => bytes[510] ^= 1), $"record {record} torn first sector");
            RejectFixture(f => f.EditRecord(record, bytes => bytes[^2] ^= 1), $"record {record} torn final sector");
            RejectFixture(f => f.EditRecord(record, bytes => W16(bytes, 6, 2)), $"record {record} USA count");
            RejectFixture(f => f.EditRecord(record, bytes =>
            {
                W16(bytes, 48, 0);
                for (int tail = 510; tail < bytes.Length; tail += 512) W16(bytes, tail, 0);
            }), $"record {record} zero USA sequence");
            RejectFixture(f => f.EditRecord(record, bytes =>
            {
                int at = U16(bytes, 20), used = checked((int)U32(bytes, 24));
                byte[] attributes = bytes.AsSpan(at, used - at).ToArray();
                attributes.CopyTo(bytes, 520);
                W16(bytes, 4, 508); W16(bytes, 508, 0xBEEF); W16(bytes, 512, 0);
                W16(bytes, 20, 520); W32(bytes, 24, checked((uint)(520 + attributes.Length)));
            }), $"record {record} USA table crosses first sector trailer");
        }
        results.Add("Raw NTFS bitmap: torn USA trailers, invalid counts, zero sequences and tables crossing the first sector fail during Open.");

        foreach (uint record in new uint[] { 0, 6 })
        {
            RejectFixture(f => f.EditData(record, (bytes, at) => W16(bytes, at + 12, 1)), $"record {record} compressed flag");
            RejectFixture(f => f.EditData(record, (bytes, at) => W16(bytes, at + 34, 4)), $"record {record} compression unit");
            RejectFixture(f => f.EditData(record, (bytes, at) => W16(bytes, at + 12, 0x8000)), $"record {record} sparse flag");
            RejectFixture(f => f.EditData(record, (bytes, at) => bytes[at + U16(bytes, at + 32)] = 0x01), $"record {record} sparse mapping pair");
            RejectFixture(f => f.EditData(record, (bytes, at) => bytes[at + 9] = 1), $"record {record} named DATA only");
            RejectFixture(f => f.EditData(record, (bytes, at) => bytes[at + 8] = 0), $"record {record} resident DATA");
            RejectFixture(f => f.EditData(record, (bytes, at) => W64(bytes, at + 16, 1)), $"record {record} nonzero first VCN");
            RejectFixture(f => f.EditRecord(record, AppendAttributeList), $"record {record} ATTRIBUTE_LIST");
        }
        results.Add("Raw NTFS bitmap: sparse, compressed, resident/named or extent-based critical DATA and ATTRIBUTE_LIST layouts are rejected before any reclaim page is returned.");

        RejectFixture(f => f.EditBoot(bytes => bytes[3] = (byte)'F'), "wrong OEM");
        RejectFixture(f => f.EditBoot(bytes => bytes[510] = 0), "wrong boot signature");
        RejectFixture(f => f.EditBoot(bytes => W16(bytes, 11, 4096)), "wrong sector size");
        RejectFixture(f => f.EditBoot(bytes => bytes[13] = 4), "wrong cluster size");
        RejectFixture(f => f.EditBoot(bytes => W64(bytes, 40, Fixture.PartitionLength / 512 + 1)), "sectors exceed partition");
        RejectFixture(f => f.EditBoot(bytes => W64(bytes, 48, Fixture.ClusterCount)), "MFT starts outside partition");
        RejectFixture(f => f.EditBoot(bytes => W64(bytes, 56, Fixture.ClusterCount)), "MFT mirror starts outside partition");
        RejectFixture(f => f.EditBoot(bytes => W64(bytes, 72, Fixture.Serial ^ 1)), "changed serial");
        RejectFixture(f => f.EditBoot(bytes => bytes[64] = unchecked((byte)-11)), "changed record size");
        var wrongExpected = new Fixture();
        Reject(() => wrongExpected.Open(expectedClusters: Fixture.ClusterCount + 1), "external geometry mismatch");
        Reject(() => NtfsAllocationBitmap.Open(wrongExpected.ReadSectors, Fixture.Capacity, Fixture.PartitionOffset + 512,
            Fixture.PartitionLength, Fixture.ClusterCount, Fixture.Serial, 1024), "unexpected partition offset");
        results.Add("Raw NTFS bitmap: boot signatures, disk/partition bounds and the independently supplied NTFS identity are checked.");

        foreach (uint record in new uint[] { 0, 6 })
        {
            RejectFixture(f => f.EditRecord(record, bytes => W32(bytes, 44, record + 1)), $"record {record} identity");
            RejectFixture(f => f.EditRecord(record, bytes => W16(bytes, 16, 0)), $"record {record} unused sequence");
            RejectFixture(f => f.EditRecord(record, bytes => W16(bytes, 22, 0)), $"record {record} not in use");
            RejectFixture(f => f.EditRecord(record, bytes => W16(bytes, 22, 3)), $"record {record} directory flag");
            RejectFixture(f => f.EditRecord(record, bytes => W64(bytes, 32, 1)), $"record {record} extension record");
            RejectFixture(f => f.EditRecord(record, bytes =>
            {
                int name = FindAttribute(bytes, 0x30), value = name + U16(bytes, name + 20);
                W16(bytes, value + 66, '@');
            }), $"record {record} wrong metadata filename");
        }
        results.Add("Raw NTFS bitmap: record numbers, base references, in-use state and resident $MFT/$Bitmap names cannot be substituted.");

        RejectFixture(f => f.EditData(6, (bytes, at) => W64(bytes, at + 24, 7)), "runlist does not cover declared VCNs");
        RejectFixture(f => f.EditData(6, (bytes, at) => W64(bytes, at + 40, 3 * 4096)), "runlist allocation mismatch");
        RejectFixture(f => f.EditData(6, (bytes, at) =>
        {
            int run = at + U16(bytes, at + 32); bytes[run + 1] = 0;
        }), "zero length run");
        RejectFixture(f => f.EditData(6, (bytes, at) =>
        {
            int run = at + U16(bytes, at + 32); bytes[run + 2] = 0xFF;
        }), "negative absolute LCN");
        RejectFixture(f => f.ReplaceData(6, [new(Fixture.ClusterCount - 1, 2)], 8160, 8160), "run crosses volume end");
        Reject(() => new Fixture(mftRuns: [new(4, 1), new(4, 3)]).Open(), "overlapping MFT extents");
        Reject(() => new Fixture(bitmapRuns: [new(34, 1), new(96, 1)]).Open(), "bitmap overlaps otherwise valid MFT extent");
        RejectFixture(f => f.EditBoot(bytes => W64(bytes, 56, 33)), "mirror overlaps MFT extent");
        results.Add("Raw NTFS bitmap: incomplete, zero, negative, overflowing or overlapping physical run mappings cannot be used for reclaim.");

        var initializedPrefix = new Fixture();
        initializedPrefix.EditData(0, (bytes, at) => W64(bytes, at + 56, 7 * 1024));
        VerifyPage(initializedPrefix, initializedPrefix.Open(), 0, 32);
        RejectFixture(f => f.EditData(0, (bytes, at) => W64(bytes, at + 56, 7 * 1024 - 1)), "record 6 crosses initialized MFT prefix");
        RejectFixture(f => f.EditData(6, (bytes, at) => W64(bytes, at + 56, 8159)), "bitmap is not fully initialized");
        RejectFixture(f => f.EditData(6, (bytes, at) => { W64(bytes, at + 48, 8159); W64(bytes, at + 56, 8159); }), "bitmap logical stream too short");
        results.Add("Raw NTFS bitmap: an initialized MFT prefix is sufficient, but every required record and allocation bit must be initialized.");

        foreach (ulong cluster in new ulong[] { 0, 4, 32, 33, 34, 80, 96, 12 })
            RejectFixture(f => f.SetAllocated(cluster, false), $"critical cluster {cluster} marked free");
        var largerRecords = new Fixture(recordSize: 4096);
        VerifyPage(largerRecords, largerRecords.Open(), 0, 64);
        largerRecords.SetAllocated(15, false);
        Reject(() => largerRecords.Open(), "four-record MFT mirror tail marked free");
        var splitFirstRecord = new Fixture(recordSize: 8192, mftRuns: [new(4, 1), new(32, 31)]);
        splitFirstRecord.CopyRecordZeroContiguously();
        Reject(() => splitFirstRecord.Open(), "boot read of record 0 extends beyond the first MFT run");
        results.Add("Raw NTFS bitmap: Boot, every MFT/bitmap extent and the entire required MFT mirror are preflighted as allocated; unsafe record-0 bootstrap layouts fail closed.");
        return results;
    }

    private static void VerifyAllPages(Fixture fixture, NtfsAllocationBitmap bitmap, int maximumBytes)
    {
        ulong cursor = 0, free = 0;
        for (int steps = 0; cursor < Fixture.ClusterCount; steps++)
        {
            Check(steps < 4096, "finite bitmap pagination");
            var page = VerifyPage(fixture, bitmap, cursor, maximumBytes);
            free += page.Ranges.Aggregate(0UL, (sum, range) => sum + range.Count);
            cursor = page.Next;
        }
        Check(cursor == Fixture.ClusterCount && free == Fixture.ClusterCount - (ulong)fixture.Allocated.Count,
            "complete free-cluster count matches independent model");
    }
    private static NtfsSpaceReclaimer.BitmapPage VerifyPage(Fixture fixture, NtfsAllocationBitmap bitmap, ulong requested, int maximumBytes)
    {
        byte[] bytes = bitmap.ReadPage(requested, maximumBytes);
        Check(bytes.Length is >= 17 && bytes.Length <= maximumBytes + 16, "bounded VOLUME_BITMAP_BUFFER");
        var actual = NtfsSpaceReclaimer.DecodeBitmap(bytes, bytes.Length, requested, Fixture.ClusterCount);
        Check(actual.Next > requested && actual.Next <= Fixture.ClusterCount, "strict cursor progress");
        var expected = new List<(ulong Start, ulong Count)>(); ulong? start = null;
        for (ulong cluster = requested; cluster < actual.Next; cluster++)
        {
            if (!fixture.Allocated.Contains(cluster)) start ??= cluster;
            else if (start is { } at) { expected.Add((at, cluster - at)); start = null; }
        }
        if (start is { } last) expected.Add((last, actual.Next - last));
        Check(actual.Ranges.SequenceEqual(expected), "bitmap free ranges match independent allocation model");
        return actual;
    }
    private static void RejectFixture(Action<Fixture> mutate, string reason)
    { var fixture = new Fixture(); mutate(fixture); Reject(() => fixture.Open(), reason); }
    private static void Reject(Action action, string reason)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("NTFS bitmap self-test accepted unsafe input: " + reason);
    }
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException("NTFS bitmap self-test failed: " + reason); }

    private readonly record struct Extent(ulong Lcn, ulong Count);
    private sealed class Fixture
    {
        internal const ulong Capacity = 256UL * 1024 * 1024, PartitionOffset = 1024 * 1024, ClusterCount = 65277;
        internal const ulong PartitionLength = ClusterCount * 4096, Serial = 0x1984120619920718;
        private const ulong MirrorLcn = 12;
        private readonly Dictionary<ulong, byte[]> sectors = new();
        private readonly Extent[] mftRuns, bitmapRuns;
        private readonly int recordSize;
        private readonly Dictionary<uint, byte[]> records = new();
        internal HashSet<ulong> Allocated { get; } = new();
        internal List<(ulong Offset, int Length)> Reads { get; } = new();
        internal int StoredSectors => sectors.Count;
        internal Fixture(int recordSize = 1024, Extent[]? mftRuns = null, Extent[]? bitmapRuns = null)
        {
            this.recordSize = recordSize;
            ulong mftClusters = checked((ulong)recordSize * 16 / 4096), first = Math.Max(1UL, (ulong)recordSize / 4096);
            this.mftRuns = mftRuns ?? [new(4, first), new(32, mftClusters - first)];
            this.bitmapRuns = bitmapRuns ?? [new(80, 1), new(96, 1)];
            byte[] boot = new byte[512]; "NTFS    "u8.CopyTo(boot.AsSpan(3));
            W16(boot, 11, 512); boot[13] = 8; W64(boot, 40, ClusterCount * 8);
            W64(boot, 48, this.mftRuns[0].Lcn); W64(boot, 56, MirrorLcn);
            int power = 0; for (int n = recordSize; n > 1; n >>= 1) power++;
            boot[64] = unchecked((byte)-power); W64(boot, 72, Serial); W16(boot, 510, 0xAA55);
            WriteBytes(PartitionOffset, boot);
            records[0] = BuildRecord(0, "$MFT", this.mftRuns, mftClusters * 4096, mftClusters * 4096, recordSize);
            records[6] = BuildRecord(6, "$Bitmap", this.bitmapRuns, (ClusterCount + 7) / 8, (ClusterCount + 7) / 8, recordSize);
            WriteStream(this.mftRuns, 0, records[0]); WriteStream(this.mftRuns, checked(6UL * (ulong)recordSize), records[6]);
            WriteBytes(ClusterOffset(MirrorLcn), records[0]);
            Allocated.Add(0);
            foreach (Extent run in this.mftRuns.Concat(this.bitmapRuns))
                for (ulong i = 0; i < run.Count; i++) Allocated.Add(run.Lcn + i);
            for (ulong i = 0; i < Math.Max(1UL, (ulong)recordSize * 4 / 4096); i++) Allocated.Add(MirrorLcn + i);
            foreach (ulong cluster in new ulong[] { 1, 7, 17, 1234, 32767, 32768, 32769, ClusterCount - 2 }) Allocated.Add(cluster);
            RebuildBitmap();
        }
        internal NtfsAllocationBitmap Open(ulong? expectedClusters = null) => NtfsAllocationBitmap.Open(ReadSectors,
            Capacity, PartitionOffset, PartitionLength, expectedClusters ?? ClusterCount, Serial, checked((uint)recordSize));
        internal byte[] ReadSectors(ulong offset, int length)
        {
            Check(offset % 512 == 0 && length > 0 && length % 512 == 0 && length <= 66048, "sector-aligned bounded raw read");
            Check(offset >= PartitionOffset && offset <= PartitionOffset + PartitionLength
                && (ulong)length <= PartitionOffset + PartitionLength - offset, "raw read stays inside owned partition");
            Reads.Add((offset, length)); var output = new byte[length];
            for (int at = 0; at < length; at += 512)
                if (sectors.TryGetValue((offset + (ulong)at) / 512, out var sector)) sector.CopyTo(output, at);
            return output;
        }
        internal ulong RecordOffset(uint number) => MapOffset(mftRuns, checked((ulong)number * (ulong)recordSize));
        internal void EditBoot(Action<byte[]> edit)
        { byte[] bytes = ReadSectors(PartitionOffset, 512); edit(bytes); WriteBytes(PartitionOffset, bytes); }
        internal void EditRecord(uint number, Action<byte[]> edit)
        { byte[] bytes = records[number].ToArray(); edit(bytes); records[number] = bytes; WriteStream(mftRuns, (ulong)number * (ulong)recordSize, bytes); }
        internal void EditData(uint number, Action<byte[], int> edit) => EditRecord(number, bytes => edit(bytes, FindAttribute(bytes, 0x80)));
        internal void ReplaceData(uint number, Extent[] runs, ulong length, ulong initialized)
        {
            records[number] = BuildRecord(number, number == 0 ? "$MFT" : "$Bitmap", runs, length, initialized, recordSize);
            WriteStream(mftRuns, (ulong)number * (ulong)recordSize, records[number]);
        }
        internal void SetAllocated(ulong cluster, bool allocated)
        { if (allocated) Allocated.Add(cluster); else Allocated.Remove(cluster); RebuildBitmap(); }
        internal void CopyRecordZeroContiguously() => WriteBytes(ClusterOffset(mftRuns[0].Lcn), records[0]);
        private void RebuildBitmap()
        {
            var bytes = new byte[checked((int)((ClusterCount + 7) / 8))];
            foreach (ulong cluster in Allocated) bytes[checked((int)(cluster / 8))] |= (byte)(1 << (int)(cluster % 8));
            WriteStream(bitmapRuns, 0, bytes);
        }
        private void WriteStream(Extent[] runs, ulong offset, byte[] bytes)
        {
            for (int at = 0; at < bytes.Length;)
            {
                ulong logical = offset + (ulong)at, within = logical % 4096;
                int take = Math.Min(bytes.Length - at, checked((int)(4096 - within)));
                WriteBytes(MapOffset(runs, logical), bytes.AsSpan(at, take)); at += take;
            }
        }
        private void WriteBytes(ulong offset, ReadOnlySpan<byte> bytes)
        {
            for (int at = 0; at < bytes.Length;)
            {
                ulong position = offset + (ulong)at, key = position / 512; int within = (int)(position % 512);
                if (!sectors.TryGetValue(key, out var sector)) sectors[key] = sector = new byte[512];
                int take = Math.Min(bytes.Length - at, 512 - within); bytes.Slice(at, take).CopyTo(sector.AsSpan(within)); at += take;
            }
        }
        private static ulong MapOffset(Extent[] runs, ulong offset)
        {
            ulong vcn = offset / 4096, start = 0;
            foreach (Extent run in runs)
            {
                if (vcn >= start && vcn - start < run.Count) return ClusterOffset(run.Lcn + vcn - start) + offset % 4096;
                start += run.Count;
            }
            throw new InvalidOperationException("Fixture stream mapping missing.");
        }
        private static ulong ClusterOffset(ulong cluster) => PartitionOffset + cluster * 4096;
    }

    private static byte[] BuildRecord(uint number, string name, Extent[] runs, ulong length, ulong initialized, int recordSize)
    {
        var record = new byte[recordSize]; "FILE"u8.CopyTo(record);
        int usaCount = recordSize / 512 + 1, at = Align8(48 + usaCount * 2);
        W16(record, 4, 48); W16(record, 6, checked((ushort)usaCount)); W16(record, 16, 1); W16(record, 18, 1);
        W16(record, 20, checked((ushort)at)); W16(record, 22, 1); W32(record, 28, checked((uint)recordSize));
        W16(record, 40, 2); W32(record, 44, number);
        byte[] encodedName = Encoding.Unicode.GetBytes(name); int valueLength = 66 + encodedName.Length, nameSize = Align8(24 + valueLength);
        W32(record, at, 0x30); W32(record, at + 4, checked((uint)nameSize)); W32(record, at + 16, checked((uint)valueLength)); W16(record, at + 20, 24);
        W64(record, at + 24, 5UL | 1UL << 48); record[at + 24 + 64] = checked((byte)name.Length); record[at + 24 + 65] = 1;
        encodedName.CopyTo(record, at + 24 + 66); at += nameSize;
        byte[] mapping = EncodeRuns(runs); int dataSize = Align8(64 + mapping.Length);
        W32(record, at, 0x80); W32(record, at + 4, checked((uint)dataSize)); record[at + 8] = 1; W16(record, at + 14, 1);
        ulong clusters = runs.Aggregate(0UL, (sum, run) => sum + run.Count);
        W64(record, at + 24, clusters - 1); W16(record, at + 32, 64); W64(record, at + 40, clusters * 4096);
        W64(record, at + 48, length); W64(record, at + 56, initialized); mapping.CopyTo(record, at + 64); at += dataSize;
        W32(record, at, uint.MaxValue); W32(record, 24, checked((uint)(at + 8)));
        W16(record, 48, 0xBEEF);
        for (int i = 1; i < usaCount; i++)
        { int tail = i * 512 - 2; W16(record, 48 + i * 2, U16(record, tail)); W16(record, tail, 0xBEEF); }
        return record;
    }
    private static byte[] EncodeRuns(Extent[] runs)
    {
        var bytes = new List<byte>(); long prior = 0;
        foreach (Extent run in runs)
        {
            long current = checked((long)run.Lcn), delta = checked(current - prior); prior = current;
            int countSize = 1; while (countSize < 8 && run.Count >> (countSize * 8) != 0) countSize++;
            int deltaSize = 1;
            while (deltaSize < 8 && (delta < -(1L << (deltaSize * 8 - 1)) || delta > (1L << (deltaSize * 8 - 1)) - 1)) deltaSize++;
            bytes.Add((byte)(deltaSize << 4 | countSize));
            for (int i = 0; i < countSize; i++) bytes.Add((byte)(run.Count >> (i * 8)));
            for (int i = 0; i < deltaSize; i++) bytes.Add((byte)(unchecked((ulong)delta) >> (i * 8)));
        }
        bytes.Add(0); return bytes.ToArray();
    }
    private static int FindAttribute(byte[] record, uint type)
    {
        int at = U16(record, 20), used = checked((int)U32(record, 24));
        while (at + 4 <= used)
        {
            uint current = U32(record, at); if (current == type) return at;
            if (current == uint.MaxValue) break;
            at += checked((int)U32(record, at + 4));
        }
        throw new InvalidOperationException("Fixture attribute missing.");
    }
    private static void AppendAttributeList(byte[] record)
    {
        int at = FindAttribute(record, uint.MaxValue);
        W32(record, at, 0x20); W32(record, at + 4, 24); W16(record, at + 20, 24);
        W32(record, at + 24, uint.MaxValue); W32(record, 24, checked((uint)(at + 32)));
    }
    private static int Align8(int value) => (value + 7) & ~7;
    private static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    private static void W16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at, 2), value);
    private static void W32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value);
    private static void W64(byte[] bytes, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at, 8), value);
}
