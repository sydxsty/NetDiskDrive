param(
    [string]$ApplicationDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\OverlayDisk-v0.7.0'),
    [string]$OutputDirectory = (Join-Path $env:TEMP ('OverlayDisk-test-' + [Guid]::NewGuid().ToString('N'))),
    [string]$DotNetPath,
    [switch]$IncludeMount
)
$ErrorActionPreference = 'Stop'
if (!$DotNetPath) {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) { $DotNetPath = $command.Source }
    else { $DotNetPath = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
}
if (!(Test-Path -LiteralPath $DotNetPath -PathType Leaf)) { throw 'Install the .NET 8 Desktop Runtime or pass -DotNetPath.' }
$app = Join-Path $ApplicationDirectory 'OverlayDisk.dll'
if (!(Test-Path -LiteralPath $app)) { throw 'Build the application first.' }
if ($IncludeMount -and -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as administrator for -IncludeMount. It only creates new isolated fixture containers.'
}
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
& (Join-Path $PSScriptRoot 'test-startup.ps1') -ApplicationDirectory $ApplicationDirectory -ReportPath (Join-Path $OutputDirectory 'startup.json')
# Core smoke includes the platform/transport/prefetch/provisioning mocks. The
# remaining groups exercise current compressed geometry and protected caching.
foreach ($test in @(
    @('--core-smoke', 'core'),
    @('--activity-smoke', 'activity'),
    @('--sync-size-smoke', 'sync-size'),
    @('--cache-smoke', 'cache')
)) {
    & $DotNetPath $app $test[0] (Join-Path $OutputDirectory $test[1])
    if ($LASTEXITCODE -ne 0) { throw ('Functional test failed: ' + $test[0]) }
}
if ($IncludeMount) {
    # New 256 MiB encrypted 4/8/16 MiB-object disks, NTFS/file hashes/remount,
    # plus a new empty-MBR interrupted-create fixture. No existing path/disk number.
    & $DotNetPath $app --object-size-mount-smoke (Join-Path $OutputDirectory 'object-size-mount')
    if ($LASTEXITCODE -ne 0) { throw 'Object-size NTFS creation/remount and initialization-retry tests failed.' }
}
Write-Output ('Test artifacts: ' + $OutputDirectory)
