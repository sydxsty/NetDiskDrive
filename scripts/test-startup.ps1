param(
    [string]$ApplicationDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\OverlayDisk-v0.7.0'),
    [string]$ReportPath,
    [ValidateRange(15,180)][int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
# The actual WebView host runs its isolated self-test with no inherited console
# handles. An elevated launch may relay into a different ordinary-user process;
# its result file is authoritative, never the relay PID or window enumeration.
if (-not ('OverlayDiskDetachedProbeV3' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OverlayDiskDetachedProbeV3 {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  struct SI { public uint cb; public string reserved, desktop, title; public uint x,y,cx,cy,xc,yc,fill,flags; public ushort show,reserved2; public IntPtr bytes,stdin,stdout,stderr; }
  [StructLayout(LayoutKind.Sequential)]
  struct PI { public IntPtr process,thread; public uint pid,tid; }
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern bool CreateProcessW(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref SI si, out PI pi);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
  static string Quote(string value) {
    var b=new StringBuilder("\""); int slashes=0;
    foreach(char c in value) {
      if(c=='\\') { slashes++; continue; }
      b.Append('\\',c=='"'?slashes*2+1:slashes); b.Append(c); slashes=0;
    }
    return b.Append('\\',slashes*2).Append('"').ToString();
  }
  public static IntPtr Launch(string exe, string output, out int processId) {
    SI si=new SI();si.cb=(uint)Marshal.SizeOf(typeof(SI));PI pi;
    var command=new StringBuilder(Quote(exe)+" --ui-smoke "+Quote(output));
    // DETACHED_PROCESS, bInheritHandles=false, no STARTF_USESTDHANDLES.
    if(!CreateProcessW(exe,command,IntPtr.Zero,IntPtr.Zero,false,8,IntPtr.Zero,System.IO.Path.GetDirectoryName(exe),ref si,out pi))
      throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    CloseHandle(pi.thread);processId=(int)pi.pid;return pi.process;
  }
  public static uint ExitCode(IntPtr process) {
    uint code;if(!GetExitCodeProcess(process,out code))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return code;
  }
}
'@
}
$exe = [IO.Path]::GetFullPath((Join-Path $ApplicationDirectory 'OverlayDisk.exe'))
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Published OverlayDisk.exe was not found.' }
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$mode = if ($admin) { 'administrator' } else { 'ordinary' }
$reportDirectory = if ($ReportPath) { Split-Path -Parent ([IO.Path]::GetFullPath($ReportPath)) } else { Join-Path ([IO.Path]::GetTempPath()) 'OverlayDisk-startup' }
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
$testOutput = Join-Path $reportDirectory ('startup-' + $mode + '-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testOutput) | Out-Null
$resultPath = Join-Path $testOutput 'result.json'
$testProcessId = 0
$processHandle = [OverlayDiskDetachedProbeV3]::Launch($exe, $testOutput, [ref]$testProcessId)
$hostResult = $null
$failure = $null
$exitCode = 259
try {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
            try { $hostResult = [IO.File]::ReadAllText($resultPath,[Text.Encoding]::UTF8) | ConvertFrom-Json; break }
            catch { Start-Sleep -Milliseconds 100; continue }
        }
        $exitCode = [OverlayDiskDetachedProbeV3]::ExitCode($processHandle)
        if ($exitCode -ne 259 -and $exitCode -ne 0) { throw ('Detached launch exited before host verification; code=' + $exitCode) }
        # A successful administrator relay can exit long before its host is ready.
        Start-Sleep -Milliseconds 100
    }
    if ($null -eq $hostResult) { throw 'Timed out waiting for the isolated WebView host result.' }
    if ($hostResult.passed -ne $true -or $hostResult.elevated -ne $false -or -not $hostResult.runtime) { throw 'The actual ordinary-user WebView host self-test failed.' }
    $exitDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $exitCode = [OverlayDiskDetachedProbeV3]::ExitCode($processHandle)
        if ($exitCode -ne 259) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $exitDeadline)
    if ($exitCode -ne 0) { throw ('Detached launch did not exit normally; code=' + $exitCode) }
} catch { $failure = $_.Exception.Message }
finally { [OverlayDiskDetachedProbeV3]::CloseHandle($processHandle) | Out-Null }
$report = [pscustomobject]@{
    Passed=($null -eq $failure); Detached=$true; LauncherAdministrator=$admin;
    LaunchedProcessId=$testProcessId; LaunchExitCode=$exitCode; Host=$hostResult;
    OutputDirectory=$testOutput; Error=$failure
}
$json = $report | ConvertTo-Json -Depth 8
if ($ReportPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath),$json,[Text.UTF8Encoding]::new($false)) }
else { [IO.File]::WriteAllText((Join-Path $testOutput 'startup-result.json'),$json,[Text.UTF8Encoding]::new($false)) }
if ($failure) { throw ('Detached startup failed: ' + $failure + '; diagnostic directory: ' + $testOutput) }
Write-Output ('Detached startup passed. LauncherAdministrator=' + $admin + '; HostElevated=' + $hostResult.elevated)
