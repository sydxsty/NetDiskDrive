using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OverlayDisk;

public sealed class DiskController : IDisposable
{
    private sealed record Session(CoreDisk Core, WinSpdDiskHost Host, MountedVolume? Volume);
    private readonly List<DiskEntry> disks;
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CoreDisk> unlocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim catalogOperations = new(1, 1);
    private readonly object catalogGate = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> diskOperations = new();
    private SemaphoreSlim OperationFor(string id) => diskOperations.GetOrAdd(id, _ => new(1, 1));
    private sealed record FlushObservation(string Stage, DateTimeOffset StartedUtc, DateTimeOffset StageStartedUtc, double ElapsedMs, bool Complete, string? Error);
    private readonly ConcurrentDictionary<string, FlushObservation> flushObservations = new();
    public object? FlushDiagnostics(string id) => flushObservations.TryGetValue(id, out var value)
        ? new { stage = value.Stage, startedUtc = value.StartedUtc, stageStartedUtc = value.StageStartedUtc,
            elapsedMs = value.Complete ? value.ElapsedMs : (DateTimeOffset.UtcNow - value.StartedUtc).TotalMilliseconds, complete = value.Complete, error = value.Error } : null;
    private readonly string catalogPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public IReadOnlyList<DiskEntry> Disks { get { lock (catalogGate) return disks.ToArray(); } }
    public event EventHandler? Changed;
    internal void EnsureIdentityNotCataloged(Guid id, string? sameClosedTarget = null)
    {
        var matches = Disks.Where(d => Guid.TryParse(d.Id, out var known) && known == id).ToArray();
        if (matches.Length == 1 && sameClosedTarget is not null && !matches[0].Mounted && !matches[0].Unlocked
            && TryGetCore(matches[0].Id) is null && File.Exists(sameClosedTarget)
            && string.Equals(Path.GetFullPath(matches[0].ContainerPath), Path.GetFullPath(sameClosedTarget), StringComparison.OrdinalIgnoreCase)) return;
        if (matches.Length != 0)
            throw new IOException("本机列表中已有这块原磁盘，即使文件已移走也不能再次接回同一身份。请先安全关闭，并明确从列表移除旧项后重试。");
    }
    public Action<CoreDisk>? CoreInitializer { get; set; }
    internal CoreDisk ConfigureCore(CoreDisk core)
    {
        try { CoreInitializer?.Invoke(core); return core; }
        catch { core.Dispose(); throw; }
    }
    private CoreDisk OpenCore(string path, string? password, bool readOnly = false)
    {
        var core = ConfigureCore(new CoreDisk(path, password));
        try { core.SetReadOnly(readOnly); return core; }
        catch { core.Dispose(); throw; }
    }
    public bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public bool DriverAvailable
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WinSpd");
            return key != null;
        }
    }

    public DiskController(string? settingsDirectory = null)
    {
        var folder = settingsDirectory ?? Services.SettingsStorage.DirectoryPath;
        System.IO.Directory.CreateDirectory(folder);
        catalogPath = Path.Combine(folder, "disks.json");
        disks = File.Exists(catalogPath)
            ? JsonSerializer.Deserialize<List<DiskEntry>>(File.ReadAllText(catalogPath)) ?? new()
            : new();
        foreach (var disk in disks)
        {
            disk.Mounted = false;
            disk.Status = File.Exists(disk.ContainerPath)
                ? disk.Initialized ? "未挂载" : "创建未完成"
                : "容器文件不可用";
        }
    }

    public async Task CreateAsync(CreateDiskRequest request, string? password)
    {
        var operations = catalogOperations;
        RequireMountPrerequisites();
        var path = Path.GetFullPath(request.ContainerPath);
        ValidateRequest(request, path, password);
        await operations.WaitAsync();
        DiskEntry? entry = null;
        SemaphoreSlim? entryOperation = null;
        try
        {
            if (Disks.Any(d => string.Equals(Path.GetFullPath(d.ContainerPath), path, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("这个容器文件已经在列表中。");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await Task.Run(() => CoreDisk.Create(path, request.CapacityBytes, request.Encrypted ? password : null, request.ObjectSizeBytes));
            using (var core = await Task.Run(() => OpenCore(path, request.Encrypted ? password : null)))
            {
                entry = new DiskEntry { Id = core.Id.ToString(), Name = request.Name.Trim(), ContainerPath = path,
                    CapacityBytes = core.Capacity, ObjectSizeBytes = core.ObjectSizeBytes, DriveLetter = char.ToUpperInvariant(request.DriveLetter),
                    Encrypted = core.Encrypted, ReadOnly = request.ReadOnly, FormatVersion = 4, Status = "正在准备磁盘" };
            }
            entryOperation = OperationFor(entry.Id); await entryOperation.WaitAsync();
            lock (catalogGate) { disks.Add(entry); SaveCatalog(); }
            Changed?.Invoke(this, EventArgs.Empty);
            // Only this newly created container may temporarily use RW to format NTFS.
            await MountInternalAsync(entry, password, initialFormat: true);
            if (request.ReadOnly)
            {
                await UnmountInternalAsync(entry);
                await MountInternalAsync(entry, password);
            }
        }
        catch
        {
            if (entry != null) { entry.Status = entry.Mounted ? "创建未完成，请安全卸载" : "创建未完成，数据已保留"; SaveCatalog(); }
            throw;
        }
        finally { entryOperation?.Release(); operations.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task MountAsync(DiskEntry entry, string? password, bool? readOnly = null)
    {
        var operations = OperationFor(entry.Id);
        RequireMountPrerequisites();
        await operations.WaitAsync();
        try { if (readOnly is { } selected) SetMountMode(entry, selected); await MountInternalAsync(entry, password); }
        catch { entry.Status = "挂载失败"; throw; }
        finally { operations.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    private void SetMountMode(DiskEntry entry, bool readOnly)
    {
        if (entry.ReadOnly == readOnly) return;
        if (entry.Mounted || sessions.ContainsKey(entry.Id)) throw new IOException("请先安全卸载磁盘，再切换只读或读写模式。");
        var core = TryGetCore(entry.Id); bool previous = entry.ReadOnly;
        core?.SetReadOnly(readOnly);
        try { lock (catalogGate) { entry.ReadOnly = readOnly; try { SaveCatalog(); } catch { entry.ReadOnly = previous; throw; } } }
        catch { core?.SetReadOnly(previous); throw; }
    }

    private async Task MountInternalAsync(DiskEntry entry, string? password, bool initialFormat = false)
    {
        if (sessions.ContainsKey(entry.Id)) return;
        if (entry.FormatVersion != 4 || System.IO.Directory.Exists(entry.ContainerPath))
            throw new IOException("请选择本版本新建的 .odv4 磁盘。旧版容器请使用对应旧程序打开。");
        EnsureDriveAvailable(entry.DriveLetter);
        if (char.ToUpperInvariant(Path.GetPathRoot(entry.ContainerPath)![0]) == entry.DriveLetter)
            throw new IOException("容器文件不能放在将要挂载的虚拟盘内。");
        entry.Status = "正在挂载";
        Changed?.Invoke(this, EventArgs.Empty);
        CoreDisk? core = null;
        WinSpdDiskHost? host = null;
        bool readOnly = entry.ReadOnly && !initialFormat;
        try
        {
            core = unlocked.TryRemove(entry.Id, out var retained) ? retained : await Task.Run(() => OpenCore(entry.ContainerPath, password, readOnly));
            if (core.Id != Guid.Parse(entry.Id) || core.Capacity != entry.CapacityBytes || core.Encrypted != entry.Encrypted || core.ObjectSizeBytes != entry.ObjectSizeBytes)
                throw new IOException("磁盘身份与保存的配置不一致，已停止挂载。");
            core.SetReadOnly(readOnly);
            var mountInfo = core.GetInfo();
            if (mountInfo.TryGetProperty("restore_incomplete", out var incomplete) && incomplete.GetBoolean()) throw new IOException("恢复尚未完成，不能挂载此磁盘。");
            // Before exposing the device, prove a retried initialization has no
            // logical data outside its partition tables. Catalog flags alone are
            // never permission to format an existing or damaged filesystem.
            bool allowEmptyInitialized = !entry.Initialized && !readOnly
                && await Task.Run(() => DiskInitializationProof.IsEmptyInitialized(core));
            host = new WinSpdDiskHost(core);
            await Task.Run(() => host.Start());
            // Track ownership before any Windows partition or filesystem operation can succeed.
            if (!sessions.TryAdd(entry.Id, new Session(core, host, null))) throw new IOException("磁盘已经挂载。");
            entry.Mounted = true;
            entry.Unlocked = true;
            var mounted = entry.Initialized || readOnly
                ? await DiskProvisioner.AttachExistingAsync(core.Id, core.Capacity, entry.DriveLetter, entry.Name)
                : await DiskProvisioner.PrepareNewAsync(core.Id, core.Capacity, entry.DriveLetter, entry.Name, allowEmptyInitialized);
            sessions[entry.Id] = new Session(core, host, mounted);
            // Ownership passes to sessions before profile persistence, so a write error cannot abandon an active disk.
            core = null;
            host = null;
            entry.Initialized = true;
            entry.Mounted = true;
            entry.Status = readOnly ? "已只读挂载" : "已挂载";
            SaveCatalog();
        }
        catch (Exception original)
        {
            if (sessions.ContainsKey(entry.Id))
            {
                try { await UnmountInternalAsync(entry); }
                catch (Exception cleanup)
                {
                    entry.Status = "准备未完成，磁盘仍在线，请安全卸载";
                    throw new IOException(original.Message + "\n磁盘仍由本程序管理，尚未强行移除。" + cleanup.Message, original);
                }
            }
            else { host?.Dispose(); core?.Dispose(); }
            throw;
        }
    }

    public async Task UnmountAsync(DiskEntry entry)
    {
        var operations = OperationFor(entry.Id);
        await operations.WaitAsync();
        try { await UnmountInternalAsync(entry); }
        finally { operations.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    private async Task UnmountInternalAsync(DiskEntry entry, bool keepOpen = false)
    {
        if (!sessions.TryGetValue(entry.Id, out var session)) return;
        entry.Status = "正在安全卸载";
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            if (session.Volume is null) await DiskProvisioner.DismountFailedPreparationAsync(Guid.Parse(entry.Id), entry.CapacityBytes, session.Core);
            else await DiskProvisioner.DismountAsync(session.Volume);
            // Host shutdown drains accepted I/O and flushes before removing the
            // device. A second flush immediately beforehand duplicates that fence.
            await Task.Run(session.Host.Dispose);
            if (keepOpen) unlocked[entry.Id] = session.Core;
            else session.Core.Dispose();
            entry.Unlocked = keepOpen;
            sessions.TryRemove(entry.Id, out _);
            entry.Mounted = false;
            // StorPort removal and Mount Manager notifications can finish after the dispatcher stops.
            var releaseDeadline = Stopwatch.StartNew();
            while (DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == entry.DriveLetter))
            {
                if (releaseDeadline.Elapsed > TimeSpan.FromSeconds(10))
                    throw new IOException("磁盘已经卸载，但 Windows 尚未释放盘符。请稍候再挂载。");
                await Task.Delay(100);
            }
            entry.Status = "未挂载";
        }
        catch { entry.Status = entry.Mounted ? "卸载失败，请关闭占用程序" : "已卸载，盘符待释放"; throw; }
    }
    public async Task UnmountAllAsync()
    {
        foreach (var disk in Disks.Where(d => d.Mounted)) await UnmountAsync(disk);
        foreach (var disk in Disks.Where(d => d.Unlocked)) await CloseAsync(disk);
    }

    public CoreDisk? TryGetCore(string id) => sessions.TryGetValue(id, out var active) ? active.Core : unlocked.GetValueOrDefault(id);

    public async Task UnlockAsync(DiskEntry entry, string? password)
    {
        var operations = OperationFor(entry.Id);
        await operations.WaitAsync();
        try
        {
            if (TryGetCore(entry.Id) != null) return;
            var core = await Task.Run(() => OpenCore(entry.ContainerPath, password, entry.ReadOnly));
            if (core.Id.ToString() != entry.Id || core.Capacity != entry.CapacityBytes || core.Encrypted != entry.Encrypted || core.ObjectSizeBytes != entry.ObjectSizeBytes)
            { core.Dispose(); throw new IOException("磁盘身份与保存的配置不一致。"); }
            if (!unlocked.TryAdd(entry.Id, core)) { core.Dispose(); throw new IOException("磁盘已经解锁。"); } entry.Unlocked = true; entry.Status = "已解锁";
        }
        finally { operations.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task<ManualReclaimResult> ReclaimFileSystemAsync(DiskEntry entry, Action<ulong,ulong>? progress = null, CancellationToken ct = default)
    {
        var operations=OperationFor(entry.Id);await operations.WaitAsync(ct);
        try
        {
            if (entry.ReadOnly) throw new IOException("只读磁盘不能回收逻辑空闲扇区，请安全卸载后切换为读写模式。");
            if (!sessions.TryGetValue(entry.Id,out var session)||session.Volume is null) throw new IOException("请先挂载磁盘，再手动回收 NTFS 空闲空间。");
            return await NtfsSpaceReclaimer.RunAsync(session.Volume,session.Core,progress,ct);
        }
        finally {operations.Release();Changed?.Invoke(this,EventArgs.Empty);}
    }

    public async Task FlushFileSystemAsync(DiskEntry entry)
    {
        var operations = OperationFor(entry.Id);
        var started = DateTimeOffset.UtcNow; var watch = Stopwatch.StartNew();
        void Stage(string stage) => flushObservations[entry.Id] = new(stage, started, DateTimeOffset.UtcNow, watch.Elapsed.TotalMilliseconds, false, null);
        Stage("disk_lifecycle_wait");
        await operations.WaitAsync();
        try
        {
            if (sessions.TryGetValue(entry.Id, out var session))
            {
                if (session.Volume is null) throw new IOException("磁盘尚未准备完成，不能捕获完整文件系统状态。");
                await DiskProvisioner.FlushAsync(session.Volume, Stage);
                Stage("core_flush"); session.Core.Flush();
            }
            else { Stage("core_flush"); (TryGetCore(entry.Id) ?? throw new IOException("请先解锁磁盘。")).Flush(); }
            flushObservations[entry.Id] = new("complete", started, DateTimeOffset.UtcNow, watch.Elapsed.TotalMilliseconds, true, null);
        }
        catch (Exception error)
        {
            var previous = flushObservations[entry.Id];
            flushObservations[entry.Id] = previous with { Complete = true, ElapsedMs = watch.Elapsed.TotalMilliseconds, Error = error.Message }; throw;
        }
        finally { operations.Release(); }
    }

    public async Task QuiesceAsync(DiskEntry entry)
    {
        var operations = OperationFor(entry.Id);
        await operations.WaitAsync();
        try { if (sessions.ContainsKey(entry.Id)) await UnmountInternalAsync(entry, true); else TryGetCore(entry.Id)?.Flush(); }
        finally { operations.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task CloseAsync(DiskEntry entry)
    {
        var operations = OperationFor(entry.Id);
        await operations.WaitAsync();
        try
        {
            if (entry.Mounted) throw new IOException("请先安全卸载磁盘。");
            if (unlocked.TryGetValue(entry.Id, out var core)) { core.Flush(); core.Dispose(); unlocked.TryRemove(entry.Id, out _); }
            entry.Unlocked = false; entry.Status = "未挂载";
        }
        finally { operations.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task ImportAsync(string containerPath)
    {
        var operations = catalogOperations;
        containerPath = Path.GetFullPath(containerPath);
        var info = await Task.Run(() => CoreDisk.Inspect(containerPath));
        if (info.GetProperty("format_version").GetInt32() != 4)
            throw new IOException("此版本导入 .odv4 单文件磁盘；不自动迁移旧版容器。");
        string name = Path.GetFileNameWithoutExtension(containerPath);
        if (string.IsNullOrWhiteSpace(name)) name = "导入的磁盘";
        if (name.Length > 32) name = name[..32];
        var entry = new DiskEntry
        {
            Id = info.GetProperty("id").GetString()!, Name = name,
            ContainerPath = containerPath, CapacityBytes = info.GetProperty("capacity_bytes").GetUInt64(),
            ObjectSizeBytes = CoreDisk.ObjectSizeFromInfo(info),
            Encrypted = info.GetProperty("encrypted").GetBoolean(), Initialized = false,
            FormatVersion = 4, Status = "未挂载"
        };
        if (!Guid.TryParse(entry.Id, out _) || entry.CapacityBytes < 64UL * 1024 * 1024 || entry.CapacityBytes > WinSpdDiskHost.MaximumCapacity)
            throw new IOException("磁盘配置无效。");
        await operations.WaitAsync();
        try
        {
            if (Disks.Any(d => d.Id == entry.Id || string.Equals(d.ContainerPath, containerPath, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("这块磁盘已经在列表中。");
            entry.DriveLetter = FindFreeLetter();
            lock (catalogGate)
            {
                // Publish the in-memory entry only after its catalog is durable. A failed
                // import must remain retryable without an unsaved duplicate in the list.
                AtomicSave(catalogPath, disks.Append(entry).ToArray());
                disks.Add(entry);
            }
        }
        finally { operations.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task CompactAsync(DiskEntry entry, string? password)
    {
        var operations = OperationFor(entry.Id);
        await operations.WaitAsync();
        try
        {
            if (entry.Mounted) throw new IOException("请先安全卸载，再整理存储空间。");
            entry.Status = "正在整理空间";
            Changed?.Invoke(this, EventArgs.Empty);
            await Task.Run(() => { using var core = OpenCore(entry.ContainerPath, password, entry.ReadOnly); core.Compact(); });
            entry.Status = "未挂载";
        }
        catch { entry.Status = "整理失败"; throw; }
        finally { operations.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task UpdateSettingsAsync(DiskEntry entry, string name, char driveLetter, bool? readOnly = null)
    {
        var operations = OperationFor(entry.Id);
        name = name.Trim();
        driveLetter = char.ToUpperInvariant(driveLetter);
        if (string.IsNullOrEmpty(name) || name.Length > 32 || name.Any(char.IsControl) || name.IndexOfAny("\\/:*?\"<>|".ToCharArray()) >= 0)
            throw new ArgumentException("名称应为 1–32 个字符，且不能包含文件名特殊字符。");
        if (driveLetter is < 'D' or > 'Z') throw new ArgumentException("请选择 D 至 Z 中的可用盘符。");
        await operations.WaitAsync();
        try
        {
            if (entry.Mounted) throw new IOException("请先安全卸载，再修改设置。");
            EnsureDriveAvailable(driveLetter);
            if (Disks.Any(d => d.Id != entry.Id && d.DriveLetter == driveLetter)) throw new IOException("这个盘符已分配给列表中的另一块磁盘。");
            bool oldReadOnly = entry.ReadOnly, selected = readOnly ?? entry.ReadOnly;
            var core = TryGetCore(entry.Id); core?.SetReadOnly(selected);
            try
            {
                lock (catalogGate)
                {
                    if (disks.Any(d => d.Id != entry.Id && d.DriveLetter == driveLetter)) throw new IOException("这个盘符已分配给另一块磁盘。");
                    var oldName = entry.Name; var oldLetter = entry.DriveLetter;
                    entry.Name = name; entry.DriveLetter = driveLetter; entry.ReadOnly = selected;
                    try { SaveCatalog(); }
                    catch { entry.Name = oldName; entry.DriveLetter = oldLetter; entry.ReadOnly = oldReadOnly; throw; }
                }
            }
            catch { core?.SetReadOnly(oldReadOnly); throw; }
        }
        finally { operations.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task DeleteAsync(DiskEntry entry, bool deleteContainer, string confirmation)
    {
        var operations = OperationFor(entry.Id);
        await operations.WaitAsync();
        try
        {
            if (!Disks.Contains(entry)) throw new IOException("这块磁盘已不在列表中。");
            if (entry.Mounted || entry.Unlocked || sessions.ContainsKey(entry.Id) || unlocked.ContainsKey(entry.Id))
                throw new IOException("请先安全卸载并关闭磁盘，再删除。");
            var remaining = Disks.Where(d => d != entry).ToArray();
            if (!deleteContainer) { lock (catalogGate) { AtomicSave(catalogPath, disks.Where(d => d != entry).ToArray()); disks.Remove(entry); } return; }
            if (!string.Equals(confirmation, entry.Name, StringComparison.Ordinal)) throw new IOException("请输入完整磁盘名称，确认删除本地容器。");
            string path = Path.GetFullPath(entry.ContainerPath);
            if (entry.FormatVersion != 4 || !Path.GetExtension(path).Equals(".odv4", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
                throw new IOException("只能删除此版本管理的本地 .odv4 容器。");
            for (string? part = path; part != null; part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("容器路径包含链接或重解析点，请仅从列表移除后手动处理文件。");
            // Deny concurrent writes, renames and deletes while checking the registered identity.
            // Delete the opened file by handle; never re-resolve the path after checking it.
            using var handle = OpenDeleteHandle(path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException("容器正被占用或无法删除。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            var info = CoreDisk.Inspect(path);
            if (info.GetProperty("format_version").GetInt32() != 4 || info.GetProperty("id").GetString() != entry.Id
                || info.GetProperty("capacity_bytes").GetUInt64() != entry.CapacityBytes || info.GetProperty("encrypted").GetBoolean() != entry.Encrypted)
                throw new IOException("容器身份已变化，已停止删除。");
            // A catalog failure must leave the data untouched. A crash before the following
            // disposition can leave an unlisted file, which remains recoverable by importing it.
            lock (catalogGate)
            {
            AtomicSave(catalogPath, disks.Where(d => d != entry).ToArray());
            byte disposition = 1;
            if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
            {
                var error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                SaveCatalog();
                throw new IOException("未能删除容器，文件已保留。", error);
            }
            disks.Remove(entry);
            }
        }
        finally { operations.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDeleteHandle(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte information, uint size);

    public void OpenFolder(DiskEntry entry)
    {
        if (!entry.Mounted) throw new IOException("请先挂载磁盘。");
        Process.Start(new ProcessStartInfo("explorer.exe", $"{entry.DriveLetter}:\\") { UseShellExecute = true });
    }

    public async Task InstallDriverAsync()
    {
        var msi = Path.Combine(AppContext.BaseDirectory, "drivers", "winspd-1.0.20357.msi");
        if (!File.Exists(msi)) throw new FileNotFoundException("驱动安装包缺失，请重新下载完整的本地版。", msi);
        await using (var stream = File.OpenRead(msi))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            if (hash != "f1157eef805dcbec78a477f2b4ee5abc0049c8a9329444e5d18cab01d3604265")
                throw new IOException("驱动安装包校验失败，已停止安装。");
        }
        using var process = Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\"") { UseShellExecute = true, Verb = "runas" })
            ?? throw new IOException("无法启动驱动安装程序。");
        await process.WaitForExitAsync();
        if (process.ExitCode is not (0 or 3010)) throw new IOException($"驱动安装未完成（代码 {process.ExitCode}）。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RestartElevatedAsync()
    {
        if (IsAdministrator) return;
        await UnmountAllAsync();
        var exe = Path.Combine(AppContext.BaseDirectory, "OverlayDisk.exe");
        using var process = Process.Start(new ProcessStartInfo(exe, $"--wait-pid {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas" });
        if (process == null) throw new IOException("无法以管理员身份重新打开。");
        // The UI closes the old instance after this returns; the elevated instance waits for that PID.
    }

    private void RequireMountPrerequisites()
    {
        if (!IsAdministrator) throw new IOException("请先点击“以管理员重新打开”，再创建或挂载磁盘。");
        if (!DriverAvailable) throw new IOException("请先安装 WinSpd 虚拟磁盘驱动。");
    }
    private static void ValidateRequest(CreateDiskRequest request, string path, string? password)
    {
        CoreDisk.ValidateObjectSize(request.ObjectSizeBytes);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 32 || request.Name.Any(char.IsControl) || request.Name.IndexOfAny("\\/:*?\"<>|".ToCharArray()) >= 0)
            throw new ArgumentException("磁盘名称应为 1–32 个字符，且不能包含文件名特殊字符。");
        if (request.CapacityBytes < 64UL * 1024 * 1024 || request.CapacityBytes > WinSpdDiskHost.MaximumCapacity || request.CapacityBytes % 4096 != 0)
            throw new ArgumentException("磁盘容量应在 64 MiB 至 8 TiB 之间，并对齐到 4 KiB。");
        if (request.DriveLetter is < 'D' or > 'Z') throw new ArgumentException("请选择 D 至 Z 中的可用盘符。");
        EnsureDriveAvailable(request.DriveLetter);
        if (!string.Equals(Path.GetExtension(path), ".odv4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 .odv4 容器文件。");
        if (File.Exists(path) || System.IO.Directory.Exists(path)) throw new IOException("数据文件已存在。请另选名称；已有磁盘请使用导入。");
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("本地版的容器文件必须位于本机磁盘。");
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) throw new ArgumentException("本地版不支持将容器保存在网络映射盘。");
        if (request.Encrypted && string.IsNullOrEmpty(password)) throw new ArgumentException("加密磁盘需要设置密码。");
    }
    private static void EnsureDriveAvailable(char letter)
    {
        if (DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == char.ToUpperInvariant(letter)))
            throw new IOException($"盘符 {letter}: 已被占用，请释放后重试。");
    }
    private char FindFreeLetter() => Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i)
        .FirstOrDefault(c => !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c) && !Disks.Any(d => d.DriveLetter == c)) is var letter && letter != default
        ? letter : throw new IOException("没有可用盘符。");
    private void SaveCatalog() { lock (catalogGate) AtomicSave(catalogPath, disks); }
    private static void AtomicSave<T>(string path, T value)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value, JsonOptions); stream.Flush(true); }
        File.Move(temp, path, true);
    }
    public void Dispose()
    {
        // Normal exits use UnmountAllAsync. Never silently force-remove a busy mounted volume.
        if (sessions.Count != 0) return;
        foreach (var core in unlocked.Values) core.Dispose();
        unlocked.Clear();
        catalogOperations.Dispose();
        foreach (var operation in diskOperations.Values) operation.Dispose();
    }
}
