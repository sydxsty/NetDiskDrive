using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OverlayDisk;

/// <summary>Changes only a restored copy's disk/partition identities, never filesystem data.</summary>
internal static class DiskIdentityRewriter
{
    internal static readonly Guid BasicData = Guid.Parse("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    internal static readonly Guid Reserved = Guid.Parse("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    internal sealed record WriteRegion(ulong Offset, byte[] Bytes);
    private sealed record Table(byte[] Header, byte[] Entries, uint Count, uint Size, ulong First, ulong Last, Guid DiskId);

    internal static IReadOnlyList<(ulong Offset, int Length)>? EmptyInitializationRegions(ulong capacity, Func<ulong, int, byte[]> read)
    {
        if (capacity < 64UL << 20 || capacity % 4096 != 0) return null;
        byte[] mbr = read(0, 512);
        if (mbr.Length != 512 || mbr[510] != 0x55 || mbr[511] != 0xAA) return null;
        if (mbr.AsSpan(446, 64).IndexOfAnyExcept((byte)0) < 0) return [(0, 512)];
        ulong lastLba = capacity / 512 - 1;
        if (mbr[446] != 0 || mbr[450] != 0xEE || BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(454)) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(458)) != Math.Min(lastLba, uint.MaxValue)
            || mbr.AsSpan(462, 48).IndexOfAnyExcept((byte)0) >= 0) return null;
        Table? first = TryReadTable(read, 1, lastLba), last = TryReadTable(read, lastLba, lastLba);
        // Recovery of an empty initialization requires both complete tables. A
        // damaged table is not evidence that the volume contains no file data.
        if (first is null || last is null || first.DiskId != last.DiskId || first.First != last.First || first.Last != last.Last
            || !first.Entries.SequenceEqual(last.Entries)) return null;
        int reserved = 0;
        for (int i = 0; i < first.Count; i++)
        {
            var entry = first.Entries.AsSpan(i * (int)first.Size, (int)first.Size);
            Guid type = new(entry[..16]);
            if (type == Guid.Empty) { if (entry.IndexOfAnyExcept((byte)0) >= 0) return null; continue; }
            ulong begin = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]), end = BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]);
            if (type != Reserved || ++reserved > 1 || new Guid(entry.Slice(16, 16)) == Guid.Empty
                || begin < first.First || end > first.Last || begin > end || end - begin + 1 > 128UL * 1024 * 1024 / 512) return null;
        }
        return [(0, 1024 + first.Entries.Length), (capacity - (ulong)last.Entries.Length - 512, last.Entries.Length + 512)];
    }

    internal static void AssignCopyIdentity(CoreDisk disk)
    {
        disk.RequireWritable();
        var writes = Plan(disk.Id, disk.Capacity, (offset, length) => { var bytes = new byte[length]; disk.Read(offset, bytes, length); return bytes; });
        foreach (var write in writes)
        {
            disk.Write(write.Offset, write.Bytes, write.Bytes.Length);
            // Complete the backup before replacing the primary. On an interrupted
            // retry, either fully validated copy can reconstruct its damaged mirror.
            disk.Flush();
        }
    }
    internal static IReadOnlyList<WriteRegion> Plan(Guid identity, ulong capacity, Func<ulong, int, byte[]> read)
    {
        if (identity == Guid.Empty || capacity < 64UL << 20 || capacity % 512 != 0) throw new IOException("磁盘身份或扇区范围无效。");
        byte[] mbr = read(0, 512); if (mbr.Length != 512) throw new IOException("MBR 读取长度无效。");
        if (mbr[510] != 0x55 || mbr[511] != 0xAA) return [];
        if (mbr[450] == 7 && BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(454, 4)) == 2048)
        {
            identity.ToByteArray().AsSpan(0, 4).CopyTo(mbr.AsSpan(440, 4)); return [new(0, mbr)];
        }
        if (mbr[450] != 0xEE) return [];
        ulong lastLba = capacity / 512 - 1;
        if (mbr[446] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(454, 4)) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(458, 4)) != Math.Min(lastLba, uint.MaxValue)
            || mbr.AsSpan(462, 48).IndexOfAnyExcept((byte)0) >= 0) throw new IOException("不支持混合 GPT/MBR 分区表。");
        Table? first = TryReadTable(read, 1, lastLba), last = TryReadTable(read, lastLba, lastLba);
        var source = first ?? last ?? throw new IOException("GPT 主表和备份表均未通过校验，未修改恢复容器。");
        if (first is not null && last is not null)
        {
            if (first.Count != last.Count || first.Size != last.Size || first.First != last.First || first.Last != last.Last
                || first.DiskId != last.DiskId && first.DiskId != identity && last.DiskId != identity
                || !WithoutIdentities(first).SequenceEqual(WithoutIdentities(last)))
                throw new IOException("GPT 主表和备份表的分区范围不一致，未修改恢复容器。");
        }
        ValidatePartitions(source);
        byte[] entries = source.Entries.ToArray();
        Span<byte> input = stackalloc byte[20]; identity.TryWriteBytes(input);
        for (int i = 0; i < source.Count; i++)
        {
            int at = checked(i * (int)source.Size);
            if (new Guid(entries.AsSpan(at, 16)) == Guid.Empty) continue;
            BinaryPrimitives.WriteInt32LittleEndian(input[16..], i);
            SHA256.HashData(input).AsSpan(0, 16).CopyTo(entries.AsSpan(at + 16, 16));
        }
        uint arrayCrc = Crc32(entries); ulong tableSectors = (ulong)entries.Length / 512;
        byte[] Header(ulong lba, ulong backup, ulong tableLba)
        {
            byte[] header = source.Header.ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), lba);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), backup);
            identity.TryWriteBytes(header.AsSpan(56, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(72), tableLba);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(88), arrayCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), Crc32(header.AsSpan(0, 92)));
            return header;
        }
        byte[] backupBytes = new byte[entries.Length + 512]; entries.CopyTo(backupBytes, 0);
        Header(lastLba, 1, lastLba - tableSectors).CopyTo(backupBytes, entries.Length);
        byte[] primaryBytes = new byte[1024 + entries.Length];
        identity.ToByteArray().AsSpan(0, 4).CopyTo(mbr.AsSpan(440, 4)); mbr.CopyTo(primaryBytes, 0);
        Header(1, lastLba, 2).CopyTo(primaryBytes, 512); entries.CopyTo(primaryBytes, 1024);
        return [new((lastLba - tableSectors) * 512, backupBytes), new(0, primaryBytes)];
    }
    private static Table? TryReadTable(Func<ulong, int, byte[]> read, ulong lba, ulong lastLba)
    {
        byte[] h = read(lba * 512, 512);
        if (h.Length != 512 || !h.AsSpan(0, 8).SequenceEqual("EFI PART"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8)) != 0x00010000
            || BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12)) != 92 || BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(20)) != 0) return null;
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16)); var checkedHeader = h.ToArray(); checkedHeader.AsSpan(16, 4).Clear();
        if (Crc32(checkedHeader.AsSpan(0, 92)) != crc || BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(24)) != lba
            || BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(32)) != (lba == 1 ? lastLba : 1)) return null;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(80)), size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(84));
        // The Windows-created layout uses 128 entries of 128 bytes. Bound all reads
        // before trusting a restored header or allocating its partition array.
        if (count != 128 || size != 128) return null;
        ulong sectors = (ulong)count * size / 512, tableLba = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(72));
        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(40)), last = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(48));
        if (tableLba != (lba == 1 ? 2UL : lastLba - sectors) || first < 2 + sectors || last >= lastLba - sectors || first > last) return null;
        Guid diskId = new(h.AsSpan(56, 16)); if (diskId == Guid.Empty) return null;
        byte[] entries = read(tableLba * 512, checked((int)(count * size)));
        if (entries.Length != count * size || Crc32(entries) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(88))) return null;
        return new(h, entries, count, size, first, last, diskId);
    }
    private static byte[] WithoutIdentities(Table table)
    {
        byte[] entries = table.Entries.ToArray();
        for (int i = 0; i < table.Count; i++) entries.AsSpan(i * (int)table.Size + 16, 16).Clear();
        return entries;
    }
    private static void ValidatePartitions(Table table)
    {
        int data = 0, reserved = 0; var ranges = new List<(ulong First, ulong Last, bool Data)>(); var ids = new HashSet<Guid>();
        for (int i = 0; i < table.Count; i++)
        {
            var entry = table.Entries.AsSpan(i * (int)table.Size, (int)table.Size); Guid type = new(entry[..16]);
            if (type == Guid.Empty) { if (entry.IndexOfAnyExcept((byte)0) >= 0) throw new IOException("GPT 空分区项包含未知内容。"); continue; }
            bool isData = type == BasicData; if (isData) data++; else if (type == Reserved) reserved++; else throw new IOException("GPT 含不受支持的分区类型。");
            Guid id = new(entry.Slice(16, 16)); ulong begin = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]), end = BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]);
            if (id == Guid.Empty || !ids.Add(id) || begin < Math.Max(table.First, isData ? 2048UL : 34UL) || end > table.Last || begin > end
                || isData && begin % 2048 != 0 || !isData && end - begin + 1 > 128UL * 1024 * 1024 / 512) throw new IOException("GPT 分区身份或范围无效。");
            ranges.Add((begin, end, isData));
        }
        if (data != 1 || reserved > 1) throw new IOException("仅支持一个 GPT 数据卷和可选保留分区。");
        var sorted = ranges.OrderBy(r => r.First).ToArray();
        if (sorted.Length == 2 && (sorted[0].Data || sorted[0].Last >= sorted[1].First)) throw new IOException("GPT 分区范围重叠或保留分区位置异常。");
    }
    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) { crc ^= value; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); }
        return ~crc;
    }
}
