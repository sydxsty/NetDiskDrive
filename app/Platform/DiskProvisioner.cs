using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OverlayDisk;

public sealed record MountedVolume(Guid VolumeId, uint DiskNumber, uint PartitionNumber,
    char DriveLetter, string VolumePath);

/// <summary>Never accepts a disk number as authority. Only process-owned WinSpd GUIDs are eligible.</summary>
public static class DiskProvisioner
{
    internal static async Task<bool> HasMountedIdentityAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty) throw new ArgumentException("磁盘身份不能为空。");
        ct.ThrowIfCancellationRequested();
        await ActiveDisks.Gate.WaitAsync(ct).ConfigureAwait(false);
        try { if (ActiveDisks.Items.ContainsKey(id)) return true; }
        finally { ActiveDisks.Gate.Release(); }
        // Read-only inspection. No disk number supplied by the caller is used as authority.
        string output = await RunStorageScriptAsync(new { id = id.ToString("D"), mode = "identity-exists" }).ConfigureAwait(false);
        using var result = JsonDocument.Parse(output);
        ct.ThrowIfCancellationRequested();
        return result.RootElement.GetProperty("Exists").GetBoolean();
    }
    public static Task<MountedVolume> PrepareNewAsync(Guid volumeId, ulong capacityBytes, char driveLetter, string label, bool allowEmptyInitialized = false)
        => PrepareAsync(volumeId, capacityBytes, driveLetter, label, true, allowEmptyInitialized);

    public static Task<MountedVolume> AttachExistingAsync(Guid volumeId, ulong capacityBytes, char driveLetter, string label = "OverlayDisk")
        => PrepareAsync(volumeId, capacityBytes, driveLetter, label, false);

    private static async Task<MountedVolume> PrepareAsync(Guid id, ulong capacity, char letter, string label, bool create, bool allowEmptyInitialized = false)
    {
        ValidateRequest(id, capacity, letter, label);
        if (!WinSpdRuntime.IsAdministrator) throw new UnauthorizedAccessException("卷准备需要管理员权限。");
        var lease = await ActiveDisks.AcquireAsync(id, capacity).ConfigureAwait(false);
        try
        {
            var registration = lease.Registration;
            if (create && registration.ReadOnly) throw new IOException("只读磁盘不能初始化或格式化，只能挂载已有 NTFS 卷。");
            if (registration.Mounted is not null) throw new InvalidOperationException("该卷已完成挂载。");
            registration.BeginPreparation();
            var output = await RunStorageScriptAsync(new
            {
                id = id.ToString("D"), capacity, letter = char.ToUpperInvariant(letter).ToString(), label,
                mode = create ? "create" : "attach", readOnly = registration.ReadOnly, allowEmptyInitialized
            }).ConfigureAwait(false);
            using var result = JsonDocument.Parse(output);
            var mounted = ParseMountedVolume(result.RootElement, id, char.ToUpperInvariant(letter));
            registration.Mounted = mounted;
            return mounted;
        }
        finally { lease.Dispose(); }
    }

    internal static void ValidateRequest(Guid id, ulong capacity, char letter, string label)
    {
        WinSpdDiskHost.ValidateCapacity(capacity);
        if (id == Guid.Empty) throw new ArgumentException("磁盘 ID 不能为空。");
        letter = char.ToUpperInvariant(letter);
        if (letter < 'D' || letter > 'Z') throw new ArgumentException("请选择 D 至 Z 的空闲盘符。");
        if (string.IsNullOrWhiteSpace(label) || label.Length > 32 || label.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c)))
            throw new ArgumentException("卷标需为 1 至 32 个字符，且不含文件名保留字符。");
    }

    public static async Task FlushAsync(MountedVolume volume, Action<string>? stage = null)
    {
        stage?.Invoke("registry_wait");
        using var lease = await ActiveDisks.AcquireAsync(volume.VolumeId).ConfigureAwait(false);
        var registration = lease.Registration;
        if (registration.Mounted != volume || registration.LockedVolume is not null || registration.PreparedSafelyForRemoval)
            throw new IOException("卷不属于当前可刷新的挂载实例。");
        registration.ActiveFlushes++;
        try
        {
            await Task.Run(() =>
            {
                stage?.Invoke("volume_identity");
                using var handle = CreateFileW(volume.VolumePath.TrimEnd('\\'), registration.ReadOnly ? 0x80000000u : 0xC0000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开卷以保存文件系统缓存。");
                if (!DeviceIoControlNumber(handle, 0x002D1080, IntPtr.Zero, 0, out var device, 12, out _, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核对需要刷新的卷。");
                if (device.DeviceType != 7 || device.DeviceNumber != volume.DiskNumber || device.PartitionNumber != volume.PartitionNumber)
                    throw new IOException("卷所属磁盘或分区已变化，已停止刷新。");
                using var disk = CreateFileW(@"\\.\PhysicalDrive" + device.DeviceNumber, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (disk.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法验证虚拟磁盘身份。");
                byte[] query = new byte[12], descriptor = new byte[4096];
                if (!DeviceIoControlBytes(disk, 0x002D1400, query, (uint)query.Length, descriptor, (uint)descriptor.Length, out uint returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取虚拟磁盘身份。");
                ValidateDeviceDescriptor(descriptor, checked((int)returned), volume.VolumeId);
                byte[] geometry = new byte[256];
                if (!DeviceIoControlBytes(disk, 0x000700A0, Array.Empty<byte>(), 0, geometry, (uint)geometry.Length, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取磁盘扇区信息。");
                if (returned < 32 || BitConverter.ToUInt32(geometry, 20) != 512 || BitConverter.ToUInt64(geometry, 24) != registration.Capacity)
                    throw new IOException("虚拟磁盘容量或扇区信息已变化。");
                stage?.Invoke(registration.ReadOnly ? "ntfs_read_only" : "ntfs_flush");
                // A host exposed as write-protected from Start cannot have accepted
                // guest dirty writes. Its internal downloaded cache is flushed by Core.
                if (!registration.ReadOnly && !FlushFileBuffers(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "文件系统缓存保存失败，未创建新版本。");
            }).ConfigureAwait(false);
        }
        finally { registration.ActiveFlushes--; }
    }
    internal static void ValidateDeviceDescriptor(byte[] descriptor, int returned, Guid expected)
    {
        if (returned < 36 || returned > descriptor.Length || BitConverter.ToUInt32(descriptor, 4) > descriptor.Length)
            throw new IOException("磁盘身份描述长度无效。");
        string Field(int position)
        {
            uint offset = BitConverter.ToUInt32(descriptor, position);
            if (offset < 36 || offset >= returned) throw new IOException("磁盘身份字段无效。");
            int end = Array.IndexOf(descriptor, (byte)0, (int)offset, returned - (int)offset);
            if (end < 0) throw new IOException("磁盘身份字段未终止。");
            return Encoding.ASCII.GetString(descriptor, (int)offset, end - (int)offset).Trim();
        }
        if (BitConverter.ToUInt32(descriptor, 28) != 14 || Field(16) != WinSpdDiskHost.DiskProductId
            || !Guid.TryParse(Field(24), out var actual) || actual != expected)
            throw new IOException("磁盘序列号、产品或虚拟总线身份不符，已停止刷新。");
    }

    public static async Task DismountAsync(MountedVolume volume)
    {
        var lease = await ActiveDisks.AcquireAsync(volume.VolumeId).ConfigureAwait(false);
        try
        {
            var registration = lease.Registration;
            if (registration.Mounted != volume) throw new InvalidOperationException("卷不属于本进程的挂载记录。");
            if (registration.ActiveFlushes != 0) throw new IOException("磁盘正在保存文件系统缓存，请稍候再卸载。");
            if (registration.LockedVolume is null)
            {
                await RunStorageScriptAsync(new
                {
                    id = volume.VolumeId.ToString("D"), capacity = registration.Capacity,
                    letter = volume.DriveLetter.ToString(), label = "", mode = "inspect"
                }).ConfigureAwait(false);
                // Native I/O is kept off the UI thread, and the lock survives until host shutdown.
                registration.LockedVolume = await Task.Run(() => LockAndDismount(volume, registration.ReadOnly)).ConfigureAwait(false);
            }
            await Task.Run(() => RemoveOwnedDriveLetter(volume)).ConfigureAwait(false);
            registration.PreparedSafelyForRemoval = true;
        }
        finally { lease.Dispose(); }
    }

    /// <summary>Recovery after ANY preparation failure, including lost or invalid PowerShell output.</summary>
    public static async Task DismountFailedPreparationAsync(Guid volumeId, ulong capacityBytes, CoreDisk? core = null)
    {
        var lease = await ActiveDisks.AcquireAsync(volumeId, capacityBytes).ConfigureAwait(false);
        try
        {
            var registration = lease.Registration;
            if (registration.PreparedSafelyForRemoval) return;
            if (registration.ActiveFlushes != 0) throw new IOException("磁盘正在保存缓存，不能停止设备。");
            if (registration.LockedVolume is not null && registration.Mounted is not null)
            {
                await Task.Run(() => RemoveOwnedDriveLetter(registration.Mounted)).ConfigureAwait(false);
                registration.PreparedSafelyForRemoval = true;
                return;
            }
            // Re-discover by exact owned GUID, never trust the incomplete operation's result.
            bool allowStopEmpty = core is not null && core.Id == volumeId && core.Capacity == capacityBytes
                && await Task.Run(() => DiskInitializationProof.IsEmptyInitialized(core)).ConfigureAwait(false);
            var output = await RunStorageScriptAsync(new
            {
                id = volumeId.ToString("D"), capacity = capacityBytes,
                letter = "", label = "", mode = "cleanup", allowStopEmpty
            }).ConfigureAwait(false);
            using var result = JsonDocument.Parse(output);
            var root = result.RootElement;
            if (root.GetProperty("CanStopWithoutVolume").GetBoolean())
            {
                registration.PreparedSafelyForRemoval = true;
                return;
            }
            var volume = ParseMountedVolume(root, volumeId, '\0');
            registration.Mounted = volume;
            registration.LockedVolume = await Task.Run(() => LockAndDismount(volume, registration.ReadOnly)).ConfigureAwait(false);
            await Task.Run(() => RemoveOwnedDriveLetter(volume)).ConfigureAwait(false);
            registration.PreparedSafelyForRemoval = true;
        }
        finally { lease.Dispose(); }
    }

    private static MountedVolume ParseMountedVolume(JsonElement root, Guid id, char letter)
    {
        string path = root.GetProperty("VolumePath").GetString() ?? throw new IOException("缺少卷路径。");
        if (!path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(@"}\") ||
            !Guid.TryParseExact(path.Substring(11, path.Length - 13), "D", out _))
            throw new IOException("Windows 返回的卷路径不符合预期；磁盘仍保留运行，需重新核验后安全卸载。");
        uint partition = root.GetProperty("PartitionNumber").GetUInt32();
        if (partition == 0) throw new IOException("Windows 返回了无效分区编号。");
        if (letter == '\0' && root.TryGetProperty("DriveLetter", out var property))
        {
            string reported = property.GetString() ?? "";
            if (reported.Length == 1 && reported[0] is >= 'A' and <= 'Z') letter = reported[0];
        }
        return new(id, root.GetProperty("DiskNumber").GetUInt32(), partition, letter, path);
    }

    private static void RemoveOwnedDriveLetter(MountedVolume volume)
    {
        if (volume.DriveLetter == '\0') return;
        string root = char.ToUpperInvariant(volume.DriveLetter) + @":\";
        var actual = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPointW(root, actual, (uint)actual.Capacity))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3 or 15) return; // The verified drive letter has already disappeared.
            throw new Win32Exception(error, "无法核验盘符当前指向的卷；保留虚拟磁盘和卷锁。");
        }
        if (!string.Equals(actual.ToString(), volume.VolumePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("盘符已指向另一个卷，已拒绝删除该盘符；请重新检查挂载状态。");
        if (!DeleteVolumeMountPointW(root))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法释放本卷盘符；保留虚拟磁盘和卷锁，可重试安全卸载。");
    }

    private static SafeFileHandle LockAndDismount(MountedVolume volume, bool readOnly)
    {
        var handle = CreateFileW(volume.VolumePath.TrimEnd('\\'), readOnly ? 0x80000000u : 0xC0000000u,
            3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开卷。"); }
        try
        {
            if (!DeviceIoControlNumber(handle, 0x002D1080, IntPtr.Zero, 0, out var device, 12, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核对卷所属磁盘。");
            if (device.DeviceType != 7 || device.DeviceNumber != volume.DiskNumber || device.PartitionNumber != volume.PartitionNumber)
                throw new IOException("卷对应的磁盘或分区已变化，已拒绝卸载。");
            Control(handle, 0x00090018, "卷仍在使用中。请关闭此盘内打开的文件、程序和资源管理器窗口后重试。"); // FSCTL_LOCK_VOLUME
            if (!readOnly && !FlushFileBuffers(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "卷刷新失败，尚未卸载。");
            Control(handle, 0x00090020, "卸载卷失败。"); // FSCTL_DISMOUNT_VOLUME
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void Control(SafeFileHandle handle, uint code, string error)
    {
        if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), error);
    }

    internal static string BuildStorageScript(object request)
    {
        string payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));
        return StorageScript.Replace("__PAYLOAD__", payload, StringComparison.Ordinal);
    }

    private static async Task<string> RunStorageScriptAsync(object request)
    {
        string script = BuildStorageScript(request);
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        // The script is larger than Windows' command-line limit after base64
        // encoding. The only command-line code is a fixed stdin bootstrap.
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::InputEncoding = New-Object Text.UTF8Encoding($false); & ([ScriptBlock]::Create([Console]::In.ReadToEnd()))")));
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Windows 存储管理工具。");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(script).ConfigureAwait(false); process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new IOException("卷操作超时。数据已保留；请重新检查卷状态，勿重复格式化。");
        }
        string stdout = (await output.ConfigureAwait(false)).Trim(), stderr = (await error.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0) throw new IOException("Windows 卷操作失败：" + stderr + "\n数据未自动删除，请保留容器并重试挂载。");
        if (stdout.Length == 0) throw new IOException("Windows 未返回卷状态。");
        return stdout;
    }

    // User strings travel only in base64 JSON, never as executable PowerShell text.
    private const string StorageScript = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$r = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__PAYLOAD__')) | ConvertFrom-Json
function Get-OwnedDisk {
    $matches = @(Get-Disk | Where-Object { $_.SerialNumber -and $_.SerialNumber.Trim().Trim([char]0) -ieq $r.id })
    if ($matches.Count -ne 1) { throw 'Exact WinSpd serial was not uniquely found.' }
    $d = $matches[0]
    # WinSpd uses SCSI commands over a StorPort VirtualDevice, reported as BusType Virtual.
    if ($d.BusType -ne 'Virtual' -or $d.FriendlyName -notmatch '^WinSpd\s+OverlayDisk\s*$' -or
        [UInt64]$d.Size -ne [UInt64]$r.capacity -or $d.LogicalSectorSize -ne 512 -or $d.IsBoot -or $d.IsSystem) {
        $diagnostic = @{ SerialNumber = [string]$d.SerialNumber; FriendlyName = [string]$d.FriendlyName;
            BusType = [string]$d.BusType; Size = [UInt64]$d.Size;
            LogicalSectorSize = $d.LogicalSectorSize; IsBoot = $d.IsBoot; IsSystem = $d.IsSystem;
            ExpectedCapacity = [UInt64]$r.capacity } | ConvertTo-Json -Compress
        throw ('Disk identity, geometry or safety checks did not match the registered OverlayDisk. Matched serial diagnostics: ' + $diagnostic)
    }
    return $d
}
function Get-OwnedPartitions($d) {
    # A scoped Get-Partition query emits ObjectNotFound for a legitimate empty
    # initialized disk. Enumerate successfully first; never suppress CIM errors.
    $parts = @(Get-Partition -ErrorAction Stop | Where-Object { $_.DiskNumber -eq $d.Number })
    $current = Get-OwnedDisk
    if ($current.Number -ne $d.Number) { throw 'Disk number changed while inspecting owned partitions.' }
    return $parts
}
function Get-OwnedDataPartition($d, $parts) {
    if ($d.PartitionStyle -eq 'MBR') {
        if ($parts.Count -ne 1 -or $parts[0].Offset -ne 1MB -or [string]$parts[0].MbrType -notin @('IFS','7')) { throw 'Unexpected managed MBR partition layout.' }
        $data = @($parts[0])
    } elseif ($d.PartitionStyle -eq 'GPT') {
        $data = @($parts | Where-Object { [Guid]$_.GptType -eq [Guid]'EBD0A0A2-B9E5-4433-87C0-68B6B72699C7' })
        $reserved = @($parts | Where-Object { [Guid]$_.GptType -eq [Guid]'E3C9E316-0B5C-4DB8-817D-F92DF00215AE' })
        if ($data.Count -ne 1 -or $reserved.Count -gt 1 -or $parts.Count -ne ($data.Count + $reserved.Count)) { throw 'Only one GPT data volume and an optional Microsoft reserved partition are supported.' }
        if ($data[0].Offset -lt 1MB -or $data[0].Offset % 1MB -ne 0) { throw 'GPT data partition is not aligned.' }
        if ($reserved.Count -eq 1 -and ($reserved[0].Offset -lt 17408 -or $reserved[0].Offset % 512 -ne 0 -or $reserved[0].Size -le 0 -or $reserved[0].Size -gt 128MB -or ($reserved[0].Offset + $reserved[0].Size) -gt $data[0].Offset)) { throw 'Unexpected GPT reserved partition range.' }
    } else { throw 'The container does not have a supported partition table.' }
    if ($data[0].Size -le 0 -or $data[0].Offset -ge $d.Size -or $data[0].Size -gt ($d.Size - $data[0].Offset)) { throw 'Data partition exceeds the registered disk.' }
    return $data[0]
}
function Wait-OwnedStyle($style) {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $disk = Get-OwnedDisk
        if ($disk.PartitionStyle -eq $style) { return $disk }
        if ($disk.PartitionStyle -ne 'RAW') { throw 'Unexpected partition table after initialization.' }
        Start-Sleep -Milliseconds 250
    }
    throw 'Timed out waiting for the initialized partition table. Preserve the container and retry.'
}
function Wait-OwnedDataPartition {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $disk = Get-OwnedDisk
        $parts = @(Get-OwnedPartitions $disk)
        $onlyReserved = $disk.PartitionStyle -eq 'GPT' -and $parts.Count -eq 1 -and [Guid]$parts[0].GptType -eq [Guid]'E3C9E316-0B5C-4DB8-817D-F92DF00215AE'
        if ($parts.Count -gt 0 -and -not $onlyReserved) { return Get-OwnedDataPartition $disk $parts }
        Start-Sleep -Milliseconds 250
    }
    throw 'Timed out waiting for the data partition. Preserve this container; an existing or damaged filesystem will not be formatted.'
}
function Wait-OwnedVolume($number, $ntfs, $letter) {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $disk = Get-OwnedDisk
        $parts = @(Get-OwnedPartitions $disk)
        $onlyReserved = $disk.PartitionStyle -eq 'GPT' -and $parts.Count -eq 1 -and [Guid]$parts[0].GptType -eq [Guid]'E3C9E316-0B5C-4DB8-817D-F92DF00215AE'
        if ($parts.Count -ne 0 -and -not $onlyReserved) {
            $partition = Get-OwnedDataPartition $disk $parts
            if ($partition.PartitionNumber -ne $number) { throw 'Partition identity changed while waiting for its volume.' }
            $volume = $null
            try { $volume = $partition | Get-Volume -ErrorAction Stop }
            catch { if ($_.CategoryInfo.Category -ne [System.Management.Automation.ErrorCategory]::ObjectNotFound) { throw } }
            if ($volume) {
                if ($ntfs -and $volume.FileSystemType -notin @('Unknown','RAW','','NTFS')) { throw 'Unexpected filesystem; refusing to format or attach it.' }
                if ((-not $ntfs -or ($volume.FileSystemType -eq 'NTFS' -and $volume.AllocationUnitSize -eq 4096)) -and
                    (-not $letter -or $partition.DriveLetter -eq $letter)) {
                    return @{ Partition = $partition; Volume = $volume; Disk = $disk }
                }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Timed out waiting for NTFS or drive-letter publication. Preserve the container and retry; no existing filesystem was reformatted.'
}
try {
    if ($r.mode -eq 'identity-exists') {
        $matches = @(Get-Disk | Where-Object { $_.SerialNumber -and $_.SerialNumber.Trim().Trim([char]0) -ieq $r.id })
        @{ Exists = [bool]($matches.Count -gt 0) } | ConvertTo-Json -Compress
        exit 0
    }
    $d = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $matches = @(Get-Disk | Where-Object { $_.SerialNumber -and $_.SerialNumber.Trim().Trim([char]0) -ieq $r.id })
        if ($matches.Count -gt 0) { $d = Get-OwnedDisk; break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $d) { throw 'Timed out waiting for the registered WinSpd disk.' }
    if ($r.mode -eq 'inspect') {
        @{ DiskNumber = [UInt32]$d.Number; Serial = $d.SerialNumber } | ConvertTo-Json -Compress
        exit 0
    }
    if ($r.mode -eq 'cleanup') {
        $parts = @(Get-OwnedPartitions $d)
        if ($parts.Count -eq 0 -and $d.PartitionStyle -in @('RAW','MBR','GPT')) {
            if (-not $r.allowStopEmpty) { throw 'Partition enumeration is empty but the container has data; retaining the disk until its volume can be verified.' }
            @{ CanStopWithoutVolume = $true; DiskNumber = [UInt32]$d.Number } | ConvertTo-Json -Compress
            exit 0
        }
        if ($d.PartitionStyle -eq 'GPT' -and $parts.Count -eq 1 -and [Guid]$parts[0].GptType -eq [Guid]'E3C9E316-0B5C-4DB8-817D-F92DF00215AE') {
            if (-not $r.allowStopEmpty) { throw 'No data volume was enumerated, but the container is not proven empty. Disk remains running.' }
            @{ CanStopWithoutVolume = $true; DiskNumber = [UInt32]$d.Number } | ConvertTo-Json -Compress
            exit 0
        }
        $p = Get-OwnedDataPartition $d $parts
        # Lookup errors are fatal. Unknown/RAW must be an explicit result, never inferred from an error.
        $v = $p | Get-Volume
        if (-not $v) { throw 'Could not verify the volume state. Disk remains running.' }
        if ($v.FileSystemType -in @('Unknown','RAW')) {
            @{ CanStopWithoutVolume = $true; DiskNumber = [UInt32]$d.Number } | ConvertTo-Json -Compress
            exit 0
        }
        if ($v.FileSystemType -ne 'NTFS') { throw 'Unexpected filesystem; safe removal requires manual verification. Disk remains running.' }
        $null = Get-OwnedDisk
        @{ CanStopWithoutVolume = $false; DiskNumber = [UInt32]$d.Number; PartitionNumber = [UInt32]$p.PartitionNumber; VolumePath = [string]$v.Path; DriveLetter = [string]$p.DriveLetter } | ConvertTo-Json -Compress
        exit 0
    }
    $letter = [char]$r.letter
    $occupied = @(Get-Partition | Where-Object { $_.DriveLetter -eq $letter -and $_.DiskNumber -ne $d.Number })
    if ($occupied.Count -ne 0) { throw 'Requested drive letter is in use by another disk.' }
    $psDrive = Get-PSDrive -Name $r.letter -ErrorAction SilentlyContinue
    if ($psDrive -and -not @(Get-Partition | Where-Object { $_.DriveLetter -eq $letter -and $_.DiskNumber -eq $d.Number }).Count) {
        throw 'Requested drive letter is already assigned.'
    }
    $initial = @(Get-OwnedPartitions $d)
    $onlyReserved = $d.PartitionStyle -eq 'GPT' -and $initial.Count -eq 1 -and [Guid]$initial[0].GptType -eq [Guid]'E3C9E316-0B5C-4DB8-817D-F92DF00215AE'
    $resumeEmpty = $r.allowEmptyInitialized -and $d.PartitionStyle -in @('MBR','GPT') -and ($initial.Count -eq 0 -or $onlyReserved)
    if ($r.mode -eq 'create' -and ($d.PartitionStyle -eq 'RAW' -or $resumeEmpty)) {
        if ($r.readOnly) { throw 'A read-only disk cannot be initialized or formatted.' }
        $d = Get-OwnedDisk
        if (-not $resumeEmpty -and ($d.PartitionStyle -ne 'RAW' -or @(Get-OwnedPartitions $d).Count -ne 0)) {
            throw 'Refusing to format an initialized disk. Use attach-existing to recover a previous operation.'
        }
        if ($d.IsOffline) { Get-OwnedDisk | Set-Disk -IsOffline $false }
        if ($d.IsReadOnly) { Get-OwnedDisk | Set-Disk -IsReadOnly $false }
        $d = Get-OwnedDisk
        $style = if ([UInt64]$r.capacity -ge 2199023255552) { 'GPT' } else { 'MBR' }
        if (-not $resumeEmpty) {
            if ($d.PartitionStyle -ne 'RAW') { throw 'Disk changed before initialization.' }
            $d | Initialize-Disk -PartitionStyle $style | Out-Null
            $d = Wait-OwnedStyle $style
        }
        $initial = @(Get-OwnedPartitions $d)
        if ($d.PartitionStyle -ne $style) { throw 'Unexpected partition table.' }
        if ($style -eq 'MBR') {
            if ($initial.Count -ne 0) { throw 'Unexpected MBR partition after initialization.' }
            $p = New-Partition -DiskNumber $d.Number -UseMaximumSize -Offset 1MB -MbrType IFS
        } else {
            $offset = [UInt64]1MB
            if ($initial.Count -gt 1) { throw 'Unexpected GPT partitions after initialization.' }
            if ($initial.Count -eq 1) {
                if ([Guid]$initial[0].GptType -ne [Guid]'E3C9E316-0B5C-4DB8-817D-F92DF00215AE' -or $initial[0].Offset -lt 17408 -or $initial[0].Offset % 512 -ne 0 -or $initial[0].Size -le 0 -or $initial[0].Size -gt 128MB) { throw 'Unexpected GPT reserved partition.' }
                $offset = [UInt64]([Math]::Ceiling(($initial[0].Offset + $initial[0].Size) / 1MB) * 1MB)
            }
            $p = New-Partition -DiskNumber $d.Number -UseMaximumSize -Offset $offset -GptType '{EBD0A0A2-B9E5-4433-87C0-68B6B72699C7}'
        }
        $verified = Wait-OwnedDataPartition
        if ($verified.PartitionNumber -ne $p.PartitionNumber) { throw 'Unexpected partition before formatting.' }
        $blank = Wait-OwnedVolume $p.PartitionNumber $false $null
        $verified = $blank.Partition; $existing = $blank.Volume
        if ($existing.FileSystemType -notin @('Unknown','RAW','')) { throw 'Refusing to format an existing filesystem.' }
        $verified | Format-Volume -FileSystem NTFS -AllocationUnitSize 4096 -NewFileSystemLabel $r.label -Confirm:$false | Out-Null
    } elseif ($r.mode -eq 'attach' -or $r.mode -eq 'create') {
        # A retried create may already contain NTFS. Validate and attach below; never reformat it.
        if ($d.IsOffline) { Get-OwnedDisk | Set-Disk -IsOffline $false }
        if (-not $r.readOnly -and $d.IsReadOnly) { Get-OwnedDisk | Set-Disk -IsReadOnly $false }
    } else { throw 'Unknown operation.' }
    $d = Get-OwnedDisk
    if ($r.readOnly -and -not $d.IsReadOnly) { throw 'The driver did not report the requested read-only protection.' }
    $p = Wait-OwnedDataPartition
    $ready = Wait-OwnedVolume $p.PartitionNumber $true $null
    $p = $ready.Partition; $v = $ready.Volume
    if ($p.DriveLetter -ne $letter) {
        $null = Get-OwnedDisk
        $p | Set-Partition -NewDriveLetter $letter
    }
    $ready = Wait-OwnedVolume $p.PartitionNumber $true $letter
    $d = $ready.Disk; $p = $ready.Partition; $v = $ready.Volume
    @{ DiskNumber = [UInt32]$d.Number; PartitionNumber = [UInt32]$p.PartitionNumber; VolumePath = [string]$v.Path } | ConvertTo-Json -Compress
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
""";

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber { public uint DeviceType, DeviceNumber, PartitionNumber; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder volumeName, uint bufferLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteVolumeMountPointW(string root);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlBytes(SafeFileHandle handle, uint code, byte[] input, uint inputLength, [Out] byte[] output, uint outputLength, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlNumber(SafeFileHandle handle, uint code, IntPtr input, uint inputLength, out StorageDeviceNumber output, uint outputLength, out uint returned, IntPtr overlapped);
}
