using System.Buffers.Binary;
using System.Text;

namespace OverlayDisk;

/// <summary>Reads the fixed-layout NTFS allocation bitmap through the owned core device.
/// The caller must hold a successful FSCTL_LOCK_VOLUME throughout construction and use.
/// It is deliberately strict: unsupported metadata layouts stop the explicit reclaim.</summary>
internal sealed class NtfsAllocationBitmap
{
    private const ulong ClusterBytes = 4096;
    private const int SectorBytes = 512, MaximumPageBytes = 65536;
    private readonly Func<ulong, int, byte[]> readSectors;
    private readonly ulong capacity, partitionOffset;
    private StreamMap bitmap = null!;
    internal ulong Clusters { get; private set; }
    private sealed record Run(ulong Vcn, ulong Lcn, ulong Count);
    private sealed record StreamMap(IReadOnlyList<Run> Runs, ulong Length, ulong Initialized);

    private NtfsAllocationBitmap(Func<ulong, int, byte[]> reader, ulong capacity, ulong offset)
    { readSectors = reader; this.capacity = capacity; partitionOffset = offset; }

    internal static NtfsAllocationBitmap Open(Func<ulong, int, byte[]> readSectors, ulong capacity,
        ulong partitionOffset, ulong partitionLength, ulong expectedClusters, ulong expectedSerial, uint expectedRecordSize)
    {
        try
        {
            Require(partitionOffset >= 1024 * 1024 && partitionOffset % (1024 * 1024) == 0 && capacity > partitionOffset && partitionLength != 0
                && partitionLength <= capacity - partitionOffset && partitionLength % SectorBytes == 0, "分区范围");
            var result = new NtfsAllocationBitmap(readSectors, capacity, partitionOffset);
            result.Initialize(partitionLength, expectedClusters, expectedSerial, expectedRecordSize);
            return result;
        }
        catch (Exception error) when (error is OverflowException or ArgumentException or IndexOutOfRangeException)
        { throw new IOException("NTFS 回收元数据长度或数值无效，未使用该位图。", error); }
    }

    private void Initialize(ulong partitionLength, ulong expectedClusters, ulong expectedSerial, uint expectedRecordSize)
    {
        byte[] boot = ReadDisk(partitionOffset, SectorBytes);
        Require(boot.AsSpan(3, 8).SequenceEqual("NTFS    "u8) && U16(boot, 510) == 0xAA55, "引导扇区签名");
        Require(U16(boot, 11) == SectorBytes && boot[13] == ClusterBytes / SectorBytes, "扇区或簇大小");
        ulong sectors = U64(boot, 40);
        Require(sectors != 0 && sectors <= partitionLength / SectorBytes, "引导扇区卷长度");
        Clusters = sectors / (ClusterBytes / SectorBytes);
        Require(Clusters != 0 && Clusters == expectedClusters && U64(boot, 72) == expectedSerial, "锁前后 NTFS 身份或几何变化");
        int encodedSize = unchecked((sbyte)boot[64]);
        int recordSize;
        if (encodedSize < 0)
        {
            Require(encodedSize >= -16 && encodedSize <= -9, "MFT 记录大小");
            recordSize = 1 << -encodedSize;
        }
        else recordSize = checked(encodedSize * (int)ClusterBytes);
        Require(recordSize is >= 512 and <= 65536 && (recordSize & (recordSize - 1)) == 0 && recordSize == expectedRecordSize, "MFT 记录大小");
        ulong mftLcn = U64(boot, 48), mirrorLcn = U64(boot, 56);
        Require(mftLcn < Clusters && mirrorLcn < Clusters, "MFT 起始簇");
        byte[] first = ReadDisk(ClusterOffset(mftLcn), recordSize);
        var mft = ParseRecord(first, 0, "$MFT");
        Require(mft.Runs[0].Vcn == 0 && mft.Runs[0].Lcn == mftLcn && mft.Runs[0].Count * ClusterBytes >= (ulong)recordSize, "$MFT 自身映射");
        // $MFT may have preallocated, uninitialized records beyond this required prefix.
        byte[] sixth = ReadStream(mft, checked(6UL * (ulong)recordSize), recordSize);
        bitmap = ParseRecord(sixth, 6, "$Bitmap");
        ulong bitmapBytes = checked((Clusters + 7) / 8);
        Require(bitmap.Length >= bitmapBytes && bitmap.Initialized >= bitmapBytes, "$Bitmap 有效数据长度");

        // Validate these bits before returning any bitmap page: a damaged runlist/bitmap
        // must never cause reclaim to erase the metadata being used to make its decision.
        ulong mirrorClusters = Math.Max(ClusterBytes, checked((ulong)recordSize * 4)) / ClusterBytes;
        var metadata = new List<(ulong Start, ulong Count)> { (0, 1), (mirrorLcn, mirrorClusters) };
        metadata.AddRange(mft.Runs.Select(run => (run.Lcn, run.Count)));
        metadata.AddRange(bitmap.Runs.Select(run => (run.Lcn, run.Count)));
        metadata.Sort((a, b) => a.Start.CompareTo(b.Start));
        ulong previousEnd = 0;
        foreach (var (start, count) in metadata)
        {
            Require(count != 0 && start >= previousEnd && start < Clusters && count <= Clusters - start, "关键元数据簇重叠或越界");
            RequireAllocated(start, count);
            previousEnd = checked(start + count);
        }
    }

    /// <summary>A bounded VOLUME_BITMAP_BUFFER-shaped page, including any rounded-down prefix.</summary>
    internal byte[] ReadPage(ulong requested, int maximumBytes = MaximumPageBytes)
    {
        Require(requested < Clusters && maximumBytes is > 0 and <= MaximumPageBytes, "位图分页参数");
        ulong start = requested / 8 * 8, byteOffset = start / 8;
        int count = checked((int)Math.Min((ulong)maximumBytes, (Clusters + 7) / 8 - byteOffset));
        byte[] bits = ReadStream(bitmap, byteOffset, count), output = new byte[16 + count];
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(0, 8), start);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(8, 8), Clusters - start);
        bits.CopyTo(output, 16);
        return output;
    }

    private void RequireAllocated(ulong start, ulong count)
    {
        ulong end = checked(start + count);
        for (ulong cursor = start; cursor < end;)
        {
            ulong byteStart = cursor / 8;
            int length = checked((int)Math.Min((ulong)MaximumPageBytes, (end + 7) / 8 - byteStart));
            byte[] bits = ReadStream(bitmap, byteStart, length);
            ulong stop = Math.Min(end, checked((byteStart + (ulong)length) * 8));
            for (; cursor < stop; cursor++)
                Require((bits[(int)(cursor / 8 - byteStart)] & (1 << (int)(cursor % 8))) != 0, "位图将关键元数据簇标为空闲");
        }
    }

    private StreamMap ParseRecord(byte[] record, uint expectedNumber, string expectedName)
    {
        Require(record.Length >= 512 && record.Length % SectorBytes == 0 && record.AsSpan(0, 4).SequenceEqual("FILE"u8), "MFT 记录签名");
        int usa = U16(record, 4), usaCount = U16(record, 6);
        Require(usa >= 48 && usaCount == record.Length / SectorBytes + 1 && usa + usaCount * 2 <= SectorBytes - 2, "MFT USA 边界");
        ushort sequence = U16(record, usa);
        Require(sequence != 0, "MFT USA 序列号");
        for (int i = 1; i < usaCount; i++)
        {
            int tail = i * SectorBytes - 2;
            Require(U16(record, tail) == sequence, "MFT 记录扇区尾 USA 不一致");
            record[tail] = record[usa + i * 2]; record[tail + 1] = record[usa + i * 2 + 1];
        }
        Require(U16(record, 16) != 0 && (U16(record, 22) & 3) == 1 && U64(record, 32) == 0 && U32(record, 44) == expectedNumber, "MFT 记录身份或状态");
        int at = U16(record, 20), used = checked((int)U32(record, 24));
        Require(U32(record, 28) == record.Length && at >= 48 && at % 8 == 0 && at >= usa + usaCount * 2 && used <= record.Length && at < used, "MFT 属性边界");
        StreamMap? data = null; bool nameMatches = false, terminated = false;
        for (int attributes = 0; at + 4 <= used && attributes < 128; attributes++)
        {
            uint type = U32(record, at);
            if (type == uint.MaxValue) { terminated = true; break; }
            Require(at + 24 <= used, "MFT 属性头被截断");
            int size = checked((int)U32(record, at + 4));
            Require(size >= 24 && size % 8 == 0 && size <= used - at, "MFT 属性长度");
            byte[] attr = record.AsSpan(at, size).ToArray();
            Require(attr[8] is 0 or 1 && (attr[8] != 1 || size >= 64), "MFT 属性驻留标志");
            Require(type != 0x20, "不支持带 ATTRIBUTE_LIST 的关键元数据文件");
            if (type == 0x30)
            {
                Require(attr[8] == 0, "元数据文件名属性必须驻留");
                int valueAt = U16(attr, 20), valueSize = checked((int)U32(attr, 16));
                Require(valueAt >= 24 && valueSize >= 66 && valueAt <= size && valueSize <= size - valueAt, "元数据文件名属性边界");
                int chars = attr[valueAt + 64];
                Require(chars > 0 && 66 + chars * 2 <= valueSize, "元数据文件名长度");
                if (Encoding.Unicode.GetString(attr, valueAt + 66, chars * 2) == expectedName) nameMatches = true;
            }
            if (type == 0x80 && attr[9] == 0)
            {
                Require(data is null && attr[8] == 1, "关键元数据需要唯一非驻留 unnamed DATA");
                data = ParseRuns(attr);
            }
            at += size;
        }
        Require(terminated && nameMatches && data is not null, "关键元数据缺少有效名称、数据属性或终止符");
        return data!;
    }

    private StreamMap ParseRuns(byte[] attr)
    {
        Require(U16(attr, 12) == 0 && U16(attr, 34) == 0, "不支持压缩、加密或稀疏关键元数据");
        Require(U64(attr, 16) == 0, "不支持关键元数据的扩展属性片段");
        ulong lastVcn = U64(attr, 24), allocated = U64(attr, 40), length = U64(attr, 48), initialized = U64(attr, 56);
        Require(allocated != 0 && allocated % ClusterBytes == 0 && length <= allocated && initialized <= length, "非驻留数据长度");
        int at = U16(attr, 32);
        Require(at >= 64 && at < attr.Length, "runlist 起始位置");
        var runs = new List<Run>(); ulong vcn = 0; long lcn = 0; bool terminated = false;
        while (at < attr.Length && runs.Count < 4096)
        {
            byte header = attr[at++]; if (header == 0) { terminated = true; break; }
            int countBytes = header & 15, deltaBytes = header >> 4;
            Require(countBytes is >= 1 and <= 8 && deltaBytes is >= 1 and <= 8 && countBytes + deltaBytes <= attr.Length - at, "runlist 包含空洞或无效编码");
            ulong count = LittleUnsigned(attr, at, countBytes); at += countBytes;
            ulong rawDelta = LittleUnsigned(attr, at, deltaBytes);
            if (deltaBytes < 8 && (attr[at + deltaBytes - 1] & 0x80) != 0) rawDelta |= ulong.MaxValue << (deltaBytes * 8);
            lcn = checked(lcn + unchecked((long)rawDelta)); at += deltaBytes;
            Require(count != 0 && lcn >= 0 && (ulong)lcn < Clusters && count <= Clusters - (ulong)lcn, "runlist 物理簇范围");
            ulong end = checked((ulong)lcn + count);
            Require(runs.All(run => end <= run.Lcn || (ulong)lcn >= run.Lcn + run.Count), "runlist 物理范围重叠");
            runs.Add(new(vcn, (ulong)lcn, count)); vcn = checked(vcn + count);
        }
        Require(terminated && runs.Count > 0 && lastVcn < ulong.MaxValue && vcn == lastVcn + 1 && checked(vcn * ClusterBytes) == allocated, "runlist 未完整覆盖声明的分配范围");
        return new(runs, length, initialized);
    }

    private byte[] ReadStream(StreamMap stream, ulong offset, int length)
    {
        Require(length is > 0 and <= MaximumPageBytes && offset <= stream.Initialized && (ulong)length <= stream.Initialized - offset, "读取范围超过元数据已初始化长度");
        byte[] output = new byte[length]; int done = 0;
        while (done < length)
        {
            ulong at = checked(offset + (ulong)done), vcn = at / ClusterBytes, within = at % ClusterBytes;
            var run = stream.Runs.FirstOrDefault(value => vcn >= value.Vcn && vcn - value.Vcn < value.Count);
            Require(run is not null, "元数据流缺少映射");
            ulong available = checked((run!.Vcn + run.Count - vcn) * ClusterBytes - within);
            int take = checked((int)Math.Min((ulong)(length - done), available));
            byte[] bytes = ReadDisk(checked(ClusterOffset(run.Lcn + vcn - run.Vcn) + within), take);
            bytes.CopyTo(output, done); done += take;
        }
        return output;
    }

    private byte[] ReadDisk(ulong offset, int length)
    {
        Require(length is > 0 and <= MaximumPageBytes && offset <= capacity && (ulong)length <= capacity - offset, "原始读取范围");
        ulong start = offset / SectorBytes * SectorBytes, end = checked((offset + (ulong)length + SectorBytes - 1) / SectorBytes * SectorBytes);
        Require(end <= capacity, "原始读取扇区越界");
        byte[] raw = readSectors(start, checked((int)(end - start)));
        Require(raw is not null && raw.Length == (int)(end - start), "原始读取返回长度");
        return raw!.AsSpan(checked((int)(offset - start)), length).ToArray();
    }
    private ulong ClusterOffset(ulong lcn)
    { Require(lcn < Clusters, "元数据物理簇越界"); return checked(partitionOffset + lcn * ClusterBytes); }
    private static ushort U16(byte[] value, int at) => BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(at, 2));
    private static uint U32(byte[] value, int at) => BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(at, 4));
    private static ulong U64(byte[] value, int at) => BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(at, 8));
    private static ulong LittleUnsigned(byte[] value, int at, int size)
    { ulong result = 0; for (int i = 0; i < size; i++) result |= (ulong)value[at + i] << (i * 8); return result; }
    private static void Require(bool condition, string reason)
    { if (!condition) throw new IOException("NTFS 手动回收已停止：" + reason + "。现有数据和未处理空间已保留。"); }
}
