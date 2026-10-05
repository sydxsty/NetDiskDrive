using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OverlayDisk;

public sealed record ManualReclaimResult(ulong ExaminedClusters, ulong FileSystemFreeBytes, ulong ClearedPages);

/// <summary>Explicit user action only. The NTFS allocation bitmap is used solely while the
/// owned volume remains exclusively locked; a failed lock never results in any Trim call.</summary>
internal static class NtfsSpaceReclaimer
{
    internal static async Task<ManualReclaimResult> RunAsync(MountedVolume volume, CoreDisk core,
        Action<ulong, ulong>? progress = null, CancellationToken ct = default)
    {
        core.RequireWritable();
        using var lease = await ActiveDisks.AcquireAsync(volume.VolumeId, core.Capacity).ConfigureAwait(false);
        var registration = lease.Registration;
        if (core.Id != volume.VolumeId || registration.Mounted != volume || registration.LockedVolume is not null || registration.PreparedSafelyForRemoval)
            throw new IOException("卷不属于当前挂载实例，不能回收。");
        registration.ActiveFlushes++;
        try { return await Task.Run(() => ReclaimLocked(volume, core, progress, ct), ct).ConfigureAwait(false); }
        finally { registration.ActiveFlushes--; }
    }

    private static ManualReclaimResult ReclaimLocked(MountedVolume volume, CoreDisk core, Action<ulong, ulong>? progress, CancellationToken ct)
    {
        using var handle = CreateFileW(volume.VolumePath.TrimEnd('\\'), 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开自有卷以执行手动回收。");
        var number = Query(handle, 0x002D1080, [], 12);
        if (number.Length < 12 || BitConverter.ToUInt32(number, 0) != 7 || BitConverter.ToUInt32(number, 4) != volume.DiskNumber || BitConverter.ToUInt32(number, 8) != volume.PartitionNumber)
            throw new IOException("卷的磁盘或分区身份已经变化。");
        using var disk = CreateFileW(@"\\.\PhysicalDrive" + volume.DiskNumber, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (disk.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核对自有虚拟磁盘。");
        var identity = Query(disk, 0x002D1400, new byte[12], 4096);
        DiskProvisioner.ValidateDeviceDescriptor(identity, identity.Length, volume.VolumeId);
        var geometry = Query(disk, 0x000700A0, [], 256);
        if (geometry.Length < 32 || BitConverter.ToUInt32(geometry, 20) != 512 || BitConverter.ToUInt64(geometry, 24) != core.Capacity)
            throw new IOException("磁盘容量或扇区大小不符合注册信息。");
        var partition = Query(handle, 0x00070048, [], 256);
        if (partition.Length < 32 || BitConverter.ToUInt32(partition, 24) != volume.PartitionNumber)
            throw new IOException("分区格式不符合本程序的单分区布局。");
        uint style = BitConverter.ToUInt32(partition, 0);
        if (style == 0)
        {
            if (partition.Length < 40 || partition[32] != 7) throw new IOException("MBR 分区不是受支持的 NTFS 数据分区。");
        }
        else if (style == 1)
        {
            if (partition.Length < 80 || new Guid(partition.AsSpan(32, 16)) != Guid.Parse("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")
                || new Guid(partition.AsSpan(48, 16)) == Guid.Empty) throw new IOException("GPT 分区不是受支持的数据分区。");
        }
        else throw new IOException("分区表格式不受支持。");
        ulong partitionOffset = BitConverter.ToUInt64(partition, 8), partitionLength = BitConverter.ToUInt64(partition, 16);
        if (partitionOffset < 1024 * 1024 || partitionOffset % (1024 * 1024) != 0 || style == 0 && partitionOffset != 1024 * 1024
            || partitionOffset >= core.Capacity || partitionLength == 0 || partitionLength > core.Capacity - partitionOffset)
            throw new IOException("分区范围不符合本程序布局。");

        // NTFS treats a locked volume as dismounted, so mounted-filesystem geometry
        // must be queried beforehand. Only immutable geometry is retained here;
        // allocation bits are always read from the core AFTER exclusive lock + flush.
        var ntfs = Query(handle, 0x00090064, [], 128);
        if (ntfs.Length < 96 || BitConverter.ToUInt32(ntfs, 40) != 512 || BitConverter.ToUInt32(ntfs, 44) != 4096)
            throw new IOException("NTFS 几何参数不受支持，已停止回收。");
        ulong clusters = BitConverter.ToUInt64(ntfs, 16), serial = BitConverter.ToUInt64(ntfs, 0);
        uint recordSize = BitConverter.ToUInt32(ntfs, 48);
        if (clusters == 0 || clusters > partitionLength / 4096) throw new IOException("NTFS 簇范围无效。");

        if (!DeviceIoControl(handle, 0x00090018, [], 0, [], 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "请先关闭此盘内的文件、程序和资源管理器窗口，再手动回收；尚未修改任何块。");
        try
        {
            if (!FlushFileBuffers(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "文件系统缓存保存失败，未执行回收。");
            core.Flush();
            ulong before = core.GetInfo().GetProperty("allocated_pages").GetUInt64();
            var allocation = NtfsAllocationBitmap.Open((offset, length) =>
            {
                ct.ThrowIfCancellationRequested();
                byte[] bytes = new byte[length]; core.Read(offset, bytes, length); return bytes;
            }, core.Capacity, partitionOffset, partitionLength, clusters, serial, recordSize);
            ulong cursor = 0, freeBytes = 0;
            while (cursor < clusters)
            {
                ct.ThrowIfCancellationRequested();
                byte[] output = allocation.ReadPage(cursor);
                var page = DecodeBitmap(output, output.Length, cursor, clusters);
                foreach (var range in page.Ranges)
                {
                    ulong at = range.Start, left = range.Count;
                    while (left != 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        ulong count = Math.Min(left, 16384); // One bounded 64 MiB logical trim at a time.
                        core.Trim(checked(partitionOffset + at * 4096), count * 4096);
                        freeBytes = checked(freeBytes + count * 4096); at += count; left -= count;
                    }
                }
                cursor = page.Next; progress?.Invoke(cursor, clusters);
            }
            core.Flush();
            ulong after = core.GetInfo().GetProperty("allocated_pages").GetUInt64();
            return new(cursor, freeBytes, before > after ? before - after : 0);
        }
        finally
        {
            // Cancellation also commits completed safe trims before releasing the allocation lock.
            try { core.Flush(); }
            finally { _ = DeviceIoControl(handle, 0x0009001c, [], 0, [], 0, out _, IntPtr.Zero); }
        }
    }
    internal sealed record BitmapPage(ulong Next, IReadOnlyList<(ulong Start, ulong Count)> Ranges);
    internal static BitmapPage DecodeBitmap(byte[] output, int returned, ulong requested, ulong total)
    {
        if (returned < 17 || returned > output.Length) throw new IOException("NTFS 位图响应被截断。");
        long signedStart = BitConverter.ToInt64(output, 0), signedSize = BitConverter.ToInt64(output, 8);
        if (signedStart < 0 || signedSize <= 0) throw new IOException("NTFS 位图范围无效。");
        ulong start = (ulong)signedStart, declared = (ulong)signedSize;
        if (start > requested || requested >= total || start >= total || declared > total - start)
            throw new IOException("NTFS 位图范围与卷不一致。");
        ulong bits = Math.Min(declared, checked((ulong)(returned - 16) * 8)), end = checked(start + bits);
        if (end <= requested) throw new IOException("NTFS 位图游标没有前进。");
        var ranges = new List<(ulong Start, ulong Count)>(); ulong? free = null;
        for (ulong position = requested; position < end;)
        {
            ulong bit = position - start; byte value = output[16 + checked((int)(bit / 8))];
            if (bit % 8 == 0 && position + 8 <= end && value is 0 or 255)
            {
                if (value == 0) free ??= position;
                else if (free is { } first) { ranges.Add((first, position - first)); free = null; }
                position += 8; continue;
            }
            bool allocated = (value & (1 << (int)(bit % 8))) != 0;
            if (!allocated) free ??= position;
            else if (free is { } first) { ranges.Add((first, position - first)); free = null; }
            position++;
        }
        if (free is { } last) ranges.Add((last, end - last));
        return new(end, ranges);
    }
    private static byte[] Query(SafeFileHandle handle, uint code, byte[] input, int size)
    {
        byte[] output = new byte[size];
        if (!DeviceIoControl(handle, code, input, (uint)input.Length, output, (uint)output.Length, out uint returned, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"自有卷校验失败（IOCTL 0x{code:X8}，Win32 {error}），未执行回收。");
        }
        if (returned > output.Length) throw new IOException("卷信息响应长度无效。");
        return output[..checked((int)returned)];
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);
}
