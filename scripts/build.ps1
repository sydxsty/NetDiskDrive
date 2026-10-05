param(
    [string]$DotNetPath,
    [string]$CargoPath,
    [string]$BuildDirectory = (Join-Path $env:LOCALAPPDATA 'OverlayDisk\build'),
    [string]$OutputDirectory,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $project 'artifacts\OverlayDisk-v0.7.0' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
if (!$DotNetPath) {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) { $DotNetPath = $command.Source }
}
if (!$CargoPath) {
    $command = Get-Command cargo.exe -ErrorAction SilentlyContinue
    if ($command) { $CargoPath = $command.Source }
    else { $CargoPath = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe' }
}
if (!$DotNetPath -or !(Test-Path -LiteralPath $DotNetPath -PathType Leaf)) { throw 'Install .NET 8 SDK on PATH or pass -DotNetPath.' }
if (!(Test-Path -LiteralPath $CargoPath -PathType Leaf)) { throw 'Install Rust stable x64 MSVC on PATH or pass -CargoPath.' }
# Never merge a fresh release into a directory containing earlier profiles,
# test results, symbols or historical documentation. Existing files are preserved.
if (Test-Path -LiteralPath $OutputDirectory) {
    if (!(Test-Path -LiteralPath $OutputDirectory -PathType Container) -or @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
        throw 'The release output must be a new or empty directory. Choose another -OutputDirectory; existing files were not changed.'
    }
}
$documents = @('USER-GUIDE.zh-CN.md', 'BENCHMARK.zh-CN.md', 'DEMO.zh-CN.md', 'VALIDATION.md', 'THIRD-PARTY.md', 'BUILD.zh-CN.md', 'ZSTD-PROVENANCE.md', 'RUST-DEPENDENCY-LICENSES.txt')
foreach ($file in @('README.md', 'LICENSE', 'NOTICE.md', 'engine\LICENSE', 'app\ThirdParty\WinSpd\License.txt') + @($documents | ForEach-Object { 'docs\' + $_ })) {
    if (!(Test-Path -LiteralPath (Join-Path $project $file) -PathType Leaf)) { throw ('Missing required public release document: ' + $file) }
}
& (Join-Path $PSScriptRoot 'prepare-dependencies.ps1') -CacheDirectory (Join-Path $BuildDirectory 'dependencies')
$target = Join-Path $BuildDirectory 'rust'
$manifest = Join-Path $project 'engine\Cargo.toml'
# CARGO_ENCODED_RUSTFLAGS preserves each remapping argument even when the source
# path contains spaces. Cargo gives it precedence over whitespace-split RUSTFLAGS.
# Preserve the caller's flags and restore both environment values on every exit.
$originalRustFlags = $env:RUSTFLAGS
$originalEncodedRustFlags = $env:CARGO_ENCODED_RUSTFLAGS
$rustArguments = @()
if (![string]::IsNullOrEmpty($originalEncodedRustFlags)) { $rustArguments += $originalEncodedRustFlags.Split([char]31) }
elseif (![string]::IsNullOrWhiteSpace($originalRustFlags)) { $rustArguments += @($originalRustFlags -split '\s+' | Where-Object { $_.Length -gt 0 }) }
$rustArguments += ('--remap-path-prefix=' + [IO.Path]::GetFullPath($env:USERPROFILE) + '=/_/user')
$rustArguments += ('--remap-path-prefix=' + $project + '=/_/src')
try {
    $env:CARGO_ENCODED_RUSTFLAGS = $rustArguments -join [char]31
    if (!$SkipTests) {
        & $CargoPath test --manifest-path $manifest --target-dir $target --release --locked
        if ($LASTEXITCODE -ne 0) { throw 'Rust tests failed.' }
        & $DotNetPath run --project (Join-Path $project 'cloud\OverlayDisk.Cloud.Baidu.Tests') -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Baidu protocol tests failed.' }
        & $DotNetPath run --project (Join-Path $project 'cloud\tests\OverlayDisk.Cloud.Sync.Tests') -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Cloud synchronization tests failed.' }
    }
    & $CargoPath build --manifest-path $manifest --target-dir $target --release --locked --examples
    if ($LASTEXITCODE -ne 0) { throw 'Rust examples build failed.' }
    & $CargoPath build --manifest-path $manifest --target-dir $target --release --locked --lib
    if ($LASTEXITCODE -ne 0) { throw 'Rust library build failed.' }
    Copy-Item -LiteralPath (Join-Path $target 'release\overlaydisk_core.dll') -Destination (Join-Path $project 'app\runtime\overlaydisk_core.dll') -Force
} finally {
    $env:RUSTFLAGS = $originalRustFlags
    $env:CARGO_ENCODED_RUSTFLAGS = $originalEncodedRustFlags
}
# Escape MSBuild's property-list delimiters before passing the complete PathMap
# as one argument. Mapping the project first keeps its paths independent of home.
$pathMap = $project + '=/_/src,' + [IO.Path]::GetFullPath($env:USERPROFILE) + '=/_/user'
$escapedPathMap = $pathMap.Replace('%', '%25').Replace(';', '%3B').Replace(',', '%2C')
$publishArguments = @('publish', (Join-Path $project 'app\OverlayDisk.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
    '-o', $OutputDirectory, '--nologo', ('-p:PathMap=' + $escapedPathMap), '-p:DebugType=None', '-p:DebugSymbols=false', '-p:Deterministic=true')
& $DotNetPath @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Windows application build failed.' }
foreach ($file in @('README.md', 'LICENSE', 'NOTICE.md')) { Copy-Item -LiteralPath (Join-Path $project $file) -Destination $OutputDirectory -Force }
Copy-Item -LiteralPath (Join-Path $project 'docs\THIRD-PARTY.md') -Destination $OutputDirectory -Force
$docsOutput = Join-Path $OutputDirectory 'docs'
$null = New-Item -ItemType Directory -Path $docsOutput -Force
foreach ($file in $documents) { Copy-Item -LiteralPath (Join-Path $project ('docs\' + $file)) -Destination $docsOutput -Force }
Get-ChildItem -LiteralPath (Join-Path $project 'docs') -Filter '*LICENSE.txt' -File | Copy-Item -Destination $docsOutput -Force
$images = Join-Path $project 'docs\images'
if (Test-Path -LiteralPath $images -PathType Container) { Copy-Item -LiteralPath $images -Destination $docsOutput -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $project 'engine\README-v4.md') -Destination (Join-Path $docsOutput 'STORAGE-FORMAT.md') -Force
Copy-Item -LiteralPath (Join-Path $project 'cloud\OverlayDisk.Cloud.Baidu\README.md') -Destination (Join-Path $docsOutput 'BAIDU-BACKEND.md') -Force
Copy-Item -LiteralPath (Join-Path $project 'cloud\OverlayDisk.Cloud.Sync\README.md') -Destination (Join-Path $docsOutput 'SYNC-CACHE.md') -Force
Copy-Item -LiteralPath (Join-Path $project 'engine\LICENSE') -Destination (Join-Path $docsOutput 'ENGINE-LICENSE.txt') -Force
$engineOutput = Join-Path $OutputDirectory 'engine'
$null = New-Item -ItemType Directory -Path $engineOutput -Force
Copy-Item -LiteralPath (Join-Path $project 'engine\LICENSE') -Destination $engineOutput -Force
Copy-Item -LiteralPath (Join-Path $project 'app\ThirdParty\WinSpd\License.txt') -Destination (Join-Path $OutputDirectory 'WinSpd-LICENSE.txt') -Force
# A fresh publish is the only source of runtime files. Personal verification JSON,
# previous validation reports and browser profiles are never copied by this script.
$hashes = Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse | Where-Object Name -ne 'SHA256SUMS.json' | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ File=$_.FullName.Substring($OutputDirectory.Length).TrimStart('\'); SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.json') -Encoding UTF8
Write-Output ('Built: ' + (Join-Path $OutputDirectory 'OverlayDisk.exe'))
