using System.Buffers.Binary;

namespace OverlayDisk;

internal static class DiskIdentityRewriterSelfTests
{
    internal static IReadOnlyList<string> Run()
    {
        const ulong capacity = 4UL << 40; ulong lastLba = capacity / 512 - 1;
        var sectors = new Dictionary<ulong, byte[]>(); Guid source = Guid.NewGuid(), target = Guid.NewGuid();
        byte[] mbr = new byte[512]; mbr[450] = 0xEE; mbr[510] = 0x55; mbr[511] = 0xAA;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(454), 1); BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(458), uint.MaxValue);
        byte[] entries = new byte[16384];
        void Partition(int index, Guid type, ulong first, ulong last)
        {
            var row = entries.AsSpan(index * 128, 128); type.TryWriteBytes(row); Guid.NewGuid().TryWriteBytes(row[16..]);
            BinaryPrimitives.WriteUInt64LittleEndian(row[32..], first); BinaryPrimitives.WriteUInt64LittleEndian(row[40..], last);
        }
        Partition(0, DiskIdentityRewriter.Reserved, 2048, 34815); Partition(1, DiskIdentityRewriter.BasicData, 34816, lastLba - 33);
        byte[] Header(ulong at, ulong other, ulong table)
        {
            byte[] h = new byte[512]; "EFI PART"u8.CopyTo(h); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x10000);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92); BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(24), at);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(32), other); BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), 34);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(48), lastLba - 33); source.TryWriteBytes(h.AsSpan(56));
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(72), table); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), 128); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), DiskIdentityRewriter.Crc32(entries));
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), DiskIdentityRewriter.Crc32(h.AsSpan(0, 92))); return h;
        }
        void Write(ulong offset, byte[] bytes)
        { for (int i = 0; i < bytes.Length; i += 512) sectors[(offset + (ulong)i) / 512] = bytes.AsSpan(i, 512).ToArray(); }
        byte[] Read(ulong offset, int length)
        {
            Check(offset % 512 == 0 && length % 512 == 0 && length <= 16384 && offset + (ulong)length <= capacity, "bounded GPT read");
            byte[] bytes = new byte[length]; for (int i = 0; i < length; i += 512) sectors[(offset + (ulong)i) / 512].CopyTo(bytes, i); return bytes;
        }
        void Reset()
        {
            sectors.Clear(); Write(0, mbr); Write(512, Header(1, lastLba, 2)); Write(1024, entries);
            Write((lastLba - 32) * 512, entries); Write(lastLba * 512, Header(lastLba, 1, lastLba - 32));
        }
        Reset(); var before = sectors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var plan = DiskIdentityRewriter.Plan(target, capacity, Read);
        Check(plan.Count == 2 && plan[0].Offset == (lastLba - 32) * 512 && plan[1].Offset == 0, "backup published before primary");
        foreach (var part in plan) Write(part.Offset, part.Bytes);
        Check(new Guid(sectors[1].AsSpan(56, 16)) == target && new Guid(sectors[lastLba].AsSpan(56, 16)) == target, "both disk GUIDs changed");
        for (int i = 0; i < 2; i++)
        {
            var oldRow = entries.AsSpan(i * 128, 128); var newRow = sectors[2].AsSpan(i * 128, 128);
            Check(!oldRow.Slice(16, 16).SequenceEqual(newRow.Slice(16, 16)) && oldRow[..16].SequenceEqual(newRow[..16]) && oldRow[32..].SequenceEqual(newRow[32..]), "only partition GUID changed");
        }
        var stable = DiskIdentityRewriter.Plan(target, capacity, Read);
        Check(stable.Zip(plan).All(pair => pair.First.Offset == pair.Second.Offset && pair.First.Bytes.SequenceEqual(pair.Second.Bytes)), "repeated identity assignment is stable and CRC-valid");
        Reset(); Write(plan[0].Offset, plan[0].Bytes);
        var resumed = DiskIdentityRewriter.Plan(target, capacity, Read);
        Check(resumed[1].Bytes.SequenceEqual(plan[1].Bytes), "backup-only durable interruption resumes");
        sectors[1][16] ^= 1;
        Check(DiskIdentityRewriter.Plan(target, capacity, Read)[1].Bytes.SequenceEqual(plan[1].Bytes), "valid backup recovers torn primary header");
        sectors[lastLba][16] ^= 1; bool rejected = false;
        try { DiskIdentityRewriter.Plan(target, capacity, Read); } catch (IOException) { rejected = true; }
        Check(rejected, "two invalid GPT copies cannot authorize a write");
        Reset(); Check(before.All(pair => sectors[pair.Key].SequenceEqual(pair.Value)), "fixture restoration");
        BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(32), 34);
        BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(40), 34 + 32768 - 1);
        Reset(); Check(DiskIdentityRewriter.Plan(target, capacity, Read).Count == 2, "valid LBA34 reserved partition was rejected");
        return ["4 TiB GPT copy changes disk/partition GUIDs, preserves ranges, validates both CRCs and safely resumes a backup-only or torn-primary update."];
    }
    private static void Check(bool value, string message) { if (!value) throw new IOException("GPT identity fixture: " + message); }
}
