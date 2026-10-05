using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;

namespace OverlayDisk;

/// <summary>Runs the actual provisioning script against in-process PowerShell mocks only.</summary>
internal static class DiskPreparationSelfTests
{
    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        var checks = new List<string>();
        const ulong capacity = 256UL << 20;
        byte[] first = new byte[4096]; first[510] = 0x55; first[511] = 0xAA; first[440] = 7;
        byte[] Read(ulong offset, int length) => offset < 4096 ? first.AsSpan((int)offset, length).ToArray() : new byte[length];
        IReadOnlyList<ulong> Pages(ulong start, int _) => start == 0 ? [0] : [];
        Check(DiskInitializationProof.Validate(capacity, 1, Read, Pages), "MBR-only interrupted initialization can continue");
        Check(!DiskInitializationProof.Validate(capacity, 2, Read, Pages), "unaccounted data pages refuse recovery");
        first[1000] = 1;
        Check(!DiskInitializationProof.Validate(capacity, 1, Read, Pages), "data beyond the MBR refuses recovery"); first[1000] = 0;
        first[450] = 7;
        Check(!DiskInitializationProof.Validate(capacity, 1, Read, Pages), "an existing partition cannot be reformatted");
        CheckEmptyGpt();
        foreach (uint size in new uint[] { 4u << 20, 8u << 20, 16u << 20 })
        {
            CoreDisk.ValidateObjectSize(size);
            using var info = JsonDocument.Parse(JsonSerializer.Serialize(new { object_size = size }));
            Check(CoreDisk.ObjectSizeFromInfo(info.RootElement) == size, "dynamic core object size");
        }
        using (var legacy = JsonDocument.Parse("{\"segment_size\":4194304}"))
        {
            bool refused = false;
            try { CoreDisk.ObjectSizeFromInfo(legacy.RootElement); } catch (IOException) { refused = true; }
            Check(refused, "missing object_size must not silently infer legacy geometry");
        }
        checks.Add("Interrupted initialization recovery requires complete partition tables and proof that all allocated logical pages contain only table bytes.");

        var oldPattern = await RunScriptAsync("$case='resume-empty-mbr'\n" + MockStorage + "\n$ErrorActionPreference='Stop'; $null = @(Get-Partition -DiskNumber 5)").ConfigureAwait(false);
        Check(oldPattern.ExitCode != 0 && oldPattern.Error.Contains("MSFT_Partition", StringComparison.Ordinal), "old scoped query reproduces ObjectNotFound for a legal empty disk");

        foreach (var (name, success, creates) in new (string, bool, int)[] {
            ("raw-mbr", true, 1), ("raw-gpt", true, 1), ("resume-empty-mbr", true, 1),
            ("existing-ntfs", true, 0), ("readonly-ntfs", true, 0),
            ("enumeration-denied", false, 0), ("changed-serial", false, 0),
            ("unexpected-filesystem", false, 0), ("unproven-empty", false, 0) })
        {
            var request = new { id = "799f48d2-d080-4342-97ed-6d98c2907596", capacity = name == "raw-gpt" ? 4UL << 40 : capacity,
                letter = "R", label = "Test O'Brien", mode = name.Contains("ntfs") ? "attach" : "create",
                readOnly = name == "readonly-ntfs", allowEmptyInitialized = name == "resume-empty-mbr" };
            string setup = "$case='" + name + "'\n" + MockStorage;
            string script = setup + "\n" + DiskProvisioner.BuildStorageScript(request);
            var result = await RunScriptAsync(script).ConfigureAwait(false);
            Check((result.ExitCode == 0) == success, $"{name}: {result.Error}");
            if (success)
            {
                using var parsed = JsonDocument.Parse(result.Output);
                Check(parsed.RootElement.GetProperty("DiskNumber").GetUInt32() == 5 && parsed.RootElement.GetProperty("PartitionNumber").GetUInt32() == (name == "raw-gpt" ? 2u : 1u), name + " owned partition");
            }
            // Mocks emit only operation counts, never access real disks or credentials.
            Check(result.Error.Contains("formatted=" + creates, StringComparison.Ordinal), name + " exactly-once formatting guard: " + result.Error);
        }
        checks.Add("Actual provisioning script: empty MBR/GPT, delayed partition/volume/letter publication, safe retry, read-only attach, denied enumeration, changed serial and existing-filesystem guards passed with mocked Storage cmdlets.");
        return checks;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunScriptAsync(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::InputEncoding = New-Object Text.UTF8Encoding($false); try { & ([ScriptBlock]::Create([Console]::In.ReadToEnd())) } finally { [Console]::Error.WriteLine('formatted=' + $global:fixture.formats) }")));
        using var process = Process.Start(start) ?? throw new IOException("Cannot start fixture PowerShell.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(script).ConfigureAwait(false); process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return (process.ExitCode, (await output.ConfigureAwait(false)).Trim(), (await error.ConfigureAwait(false)).Trim());
    }
    private static void Check(bool condition, string message) { if (!condition) throw new IOException("Provisioning fixture: " + message); }

    private static void CheckEmptyGpt()
    {
        const ulong capacity = 4UL << 40; ulong last = capacity / 512 - 1;
        var pages = new Dictionary<ulong, byte[]>();
        void Put(ulong offset, byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0) continue;
                ulong page = (offset + (uint)i) / 4096;
                if (!pages.TryGetValue(page, out var data)) pages[page] = data = new byte[4096];
                data[(int)((offset + (uint)i) % 4096)] = bytes[i];
            }
        }
        byte[] Read(ulong offset, int length)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) if (pages.TryGetValue((offset + (uint)i) / 4096, out var page)) bytes[i] = page[(int)((offset + (uint)i) % 4096)];
            return bytes;
        }
        byte[] mbr = new byte[512]; mbr[450] = 0xEE; mbr[510] = 0x55; mbr[511] = 0xAA;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(454), 1); BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(458), uint.MaxValue); Put(0, mbr);
        byte[] entries = new byte[16384]; DiskIdentityRewriter.Reserved.TryWriteBytes(entries.AsSpan(0, 16)); Guid.NewGuid().TryWriteBytes(entries.AsSpan(16, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(32), 34); BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(40), 34 + 32768 - 1);
        Guid diskId = Guid.NewGuid();
        byte[] Header(ulong at, ulong backup, ulong table)
        {
            byte[] header = new byte[512]; "EFI PART"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0x10000); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 92);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), at); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), backup);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40), 34); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), last - 33);
            diskId.TryWriteBytes(header.AsSpan(56, 16)); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(72), table);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), 128); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(88), DiskIdentityRewriter.Crc32(entries));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), DiskIdentityRewriter.Crc32(header.AsSpan(0, 92))); return header;
        }
        Put(512, Header(1, last, 2)); Put(1024, entries); Put((last - 32) * 512, entries); Put(last * 512, Header(last, 1, last - 32));
        IReadOnlyList<ulong> Mapped(ulong start, int _) => pages.ContainsKey(start) ? [start] : [];
        Check(DiskInitializationProof.Validate(capacity, (ulong)pages.Count, Read, Mapped), "GPT/MSR-only initialization is recoverable with both CRC-valid tables");
        Put(17UL << 20, [42]);
        Check(!DiskInitializationProof.Validate(capacity, (ulong)pages.Count, Read, Mapped), "GPT proof rejects payload outside both table regions"); pages.Remove((17UL << 20) / 4096);
        pages[(last * 512) / 4096][(int)(last * 512 % 4096) + 16] ^= 1;
        Check(!DiskInitializationProof.Validate(capacity, (ulong)pages.Count, Read, Mapped), "a damaged GPT mirror never authorizes formatting");
    }

    // Every Storage command used by the product script is shadowed here. A
    // scoped empty Get-Partition deliberately reproduces CIM ObjectNotFound.
    private const string MockStorage = """
$global:fixture = @{ style = 'RAW'; exists = $false; hidden = 0; volumeWait = 0; formatWait = 0; letterWait = 0; letter = ''; fs = 'Unknown'; formats = 0; initializes = 0; serial = '799f48d2-d080-4342-97ed-6d98c2907596' }
if ($case -in @('resume-empty-mbr','unproven-empty','existing-ntfs','readonly-ntfs')) { $global:fixture.style = 'MBR' }
if ($case -in @('existing-ntfs','readonly-ntfs')) { $global:fixture.exists = $true; $global:fixture.fs = 'NTFS' }
function Get-Disk { [pscustomobject]@{ Number=5; SerialNumber=$global:fixture.serial; BusType='Virtual'; FriendlyName='WinSpd OverlayDisk'; Size=[UInt64]$r.capacity; LogicalSectorSize=512; IsBoot=$false; IsSystem=$false; IsOffline=$false; IsReadOnly=($case -eq 'readonly-ntfs'); PartitionStyle=$global:fixture.style } }
function Get-Partition {
    [CmdletBinding()]param([int]$DiskNumber=-1, [int]$PartitionNumber=-1)
    if ($case -eq 'enumeration-denied') { Write-Error 'Fixture access denied' -Category PermissionDenied; return }
    if ($DiskNumber -ge 0 -and -not $global:fixture.exists) { Write-Error ('No MSFT_Partition with DiskNumber=' + $DiskNumber) -Category ObjectNotFound; return }
    if ($global:fixture.hidden -gt 0) { $global:fixture.hidden--; return }
    if ($global:fixture.letterWait -gt 0) { $global:fixture.letterWait--; if ($global:fixture.letterWait -eq 0) { $global:fixture.letter='R' } }
    if ($global:fixture.style -eq 'GPT') { [pscustomobject]@{ DiskNumber=5; PartitionNumber=1; Offset=[UInt64]17408; Size=16MB; GptType='E3C9E316-0B5C-4DB8-817D-F92DF00215AE'; DriveLetter='' } }
    if ($global:fixture.exists) { [pscustomobject]@{ DiskNumber=5; PartitionNumber=$(if($global:fixture.style -eq 'GPT'){2}else{1}); Offset=$(if($global:fixture.style -eq 'GPT'){17MB}else{1MB}); Size=128MB; MbrType='IFS'; GptType='EBD0A0A2-B9E5-4433-87C0-68B6B72699C7'; DriveLetter=$global:fixture.letter } }
}
function Initialize-Disk { param([Parameter(ValueFromPipeline=$true)]$InputObject, $PartitionStyle) process { if ($global:fixture.initializes -ne 0) { throw 'Duplicate initialization' }; $global:fixture.initializes++; $global:fixture.style=$PartitionStyle } }
function New-Partition {
    param($DiskNumber,[switch]$UseMaximumSize,$Offset,$MbrType,$GptType)
    if ($DiskNumber -ne 5 -or $global:fixture.exists) { throw 'Unsafe partition mutation' }
    $global:fixture.exists=$true; $global:fixture.hidden=2; $global:fixture.volumeWait=2
    if ($case -eq 'changed-serial') { $global:fixture.serial='another-device' }
    [pscustomobject]@{ PartitionNumber=$(if($global:fixture.style -eq 'GPT'){2}else{1}) }
}
function Get-Volume {
    [CmdletBinding()]param([Parameter(ValueFromPipeline=$true)]$InputObject)
    process {
        if ($InputObject.DiskNumber -ne 5) { throw 'Foreign volume query' }
        if ($global:fixture.volumeWait -gt 0) { $global:fixture.volumeWait--; Write-Error 'Volume not yet enumerated' -Category ObjectNotFound; return }
        if ($global:fixture.formatWait -gt 0) { $global:fixture.formatWait--; if ($global:fixture.formatWait -eq 0) { $global:fixture.fs='NTFS' } }
        [pscustomobject]@{ FileSystemType=$(if($case -eq 'unexpected-filesystem'){'NTFS'}else{$global:fixture.fs}); AllocationUnitSize=4096; Path='\\?\Volume{c602c24c-f8ae-41d3-9b11-c71fb0603356}\' }
    }
}
function Format-Volume {
    [CmdletBinding(SupportsShouldProcess=$true)]param([Parameter(ValueFromPipeline=$true)]$InputObject,$FileSystem,$AllocationUnitSize,$NewFileSystemLabel)
    process { if ($InputObject.DiskNumber -ne 5 -or $global:fixture.formats -ne 0 -or $FileSystem -ne 'NTFS' -or $AllocationUnitSize -ne 4096) { throw 'Unsafe format' }; $global:fixture.formats++; $global:fixture.formatWait=2 }
}
function Set-Partition { param([Parameter(ValueFromPipeline=$true)]$InputObject,$NewDriveLetter) process { if ($InputObject.DiskNumber -ne 5 -or $NewDriveLetter -ne 'R') { throw 'Unsafe mount point' }; $global:fixture.letterWait=2 } }
function Set-Disk { throw 'Unexpected disk attribute mutation' }
function Get-PSDrive { [CmdletBinding()]param($Name) }
function Start-Sleep { param($Milliseconds) }
""";
}
