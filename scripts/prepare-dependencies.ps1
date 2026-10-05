param([string]$CacheDirectory = (Join-Path $env:LOCALAPPDATA 'OverlayDisk\build\dependencies'))
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $project 'app\runtime'
$null = New-Item -ItemType Directory -Force -Path $CacheDirectory, $runtime
$name = 'winspd-1.0.20357.msi'
$installer = Join-Path $CacheDirectory $name
$expected = 'f1157eef805dcbec78a477f2b4ee5abc0049c8a9329444e5d18cab01d3604265'
if (!(Test-Path -LiteralPath $installer)) {
    Invoke-WebRequest -UseBasicParsing -Uri ('https://github.com/winfsp/winspd/releases/download/v1.0B1/' + $name) -OutFile $installer
}
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'WinSpd installer SHA256 mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid') { throw ('WinSpd signature verification failed: ' + $signature.Status) }
$sdk = Join-Path $CacheDirectory 'sdk'
if (!(Test-Path -LiteralPath (Join-Path $sdk 'WinSpd\sys\winspd-x64.dll'))) {
    $process = Start-Process msiexec.exe -ArgumentList @('/a', ('"' + $installer + '"'), '/qn', ('TARGETDIR="' + $sdk + '"')) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('WinSpd package extraction failed: ' + $process.ExitCode) }
}
Copy-Item -LiteralPath (Join-Path $sdk 'WinSpd\sys\winspd-x64.dll') -Destination $runtime -Force
Copy-Item -LiteralPath $installer -Destination $runtime -Force
Write-Output 'Pinned WinSpd runtime prepared. No kernel driver was installed.'
