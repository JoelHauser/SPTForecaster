<#
.SYNOPSIS
    Builds both halves of SPTForecaster and zips them for release.

.PARAMETER SPTPath
    An SPT install root whose Assembly-CSharp has been patched by the SPT Launcher (it keeps the
    original as Assembly-CSharp.dll.spt-bak), with SPT_Runtime for the server assemblies.

.PARAMETER SptPluginsPath
    Folder holding spt-common.dll and spt-reflection.dll, if not <SPTPath>\BepInEx\plugins\spt.

.PARAMETER ServerPath
    SPT install root for the server assemblies, if different from SPTPath (a build mirror may only
    carry the client side).

.PARAMETER Install
    Also copy both DLLs into SPTPath.
#>
param(
    [Parameter(Mandatory = $true)][string]$SPTPath,
    [string]$SptPluginsPath = "",
    [string]$ServerPath = "",
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
if (-not $SptPluginsPath) { $SptPluginsPath = Join-Path $SPTPath 'BepInEx\plugins\spt' }
if (-not $ServerPath) { $ServerPath = $SPTPath }

$client = Join-Path $root 'src\SPTForecaster\SPTForecaster.csproj'
$server = Join-Path $root 'src\SPTForecaster.Server\SPTForecaster.Server.csproj'

# ---- version agreement -----------------------------------------------------------
# Four places name the version; a release whose DLLs disagree with its zip name is no good.
$versions = @(
    ([xml](Get-Content $client)).Project.PropertyGroup.Version | Where-Object { $_ }
    ([xml](Get-Content $server)).Project.PropertyGroup.Version | Where-Object { $_ }
    (Select-String -Path (Join-Path $root 'src\SPTForecaster\ForecasterPlugin.cs') -Pattern 'PluginVersion\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
    (Select-String -Path (Join-Path $root 'src\SPTForecaster.Server\ModMetadata.cs') -Pattern 'new\("(\d+\.\d+\.\d+)"\)').Matches[0].Groups[1].Value
)
if (($versions | Select-Object -Unique).Count -ne 1) {
    throw "Version mismatch: $($versions -join ', ')"
}
$version = $versions[0]
Write-Host "Version $version" -ForegroundColor Cyan

# ---- build -----------------------------------------------------------------------
dotnet build $client -c Release "-p:SPTPath=$SPTPath" "-p:SptPluginsPath=$SptPluginsPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Client build failed.' }
dotnet build $server -c Release "-p:SPTPath=$ServerPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }

$clientDll = Join-Path $root 'src\SPTForecaster\bin\Release\SPTForecaster.dll'
$serverDll = Join-Path $root 'src\SPTForecaster.Server\bin\Release\SPTForecaster.Server.dll'

# ---- zip -------------------------------------------------------------------------
# Only install files: the zip is unpacked over the SPT root, so anything else would be left lying in it.
$entries = [ordered]@{
    'BepInEx/plugins/SPTForecaster.dll'                              = $clientDll
    'SPT_Runtime/user/mods/SPTForecaster/SPTForecaster.Server.dll'   = $serverDll
}

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zip = Join-Path $dist "SPTForecaster-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($e in $entries.GetEnumerator()) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $e.Value, $e.Key, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
}

$check = [System.IO.Compression.ZipFile]::OpenRead($zip)
try { $check.Entries | ForEach-Object { Write-Host "  $($_.FullName)  $($_.Length) bytes" } }
finally { $check.Dispose() }
Write-Host "Wrote $zip" -ForegroundColor Green

if ($Install) {
    Copy-Item $clientDll (Join-Path $SPTPath 'BepInEx\plugins') -Force
    $modDir = Join-Path $SPTPath 'SPT_Runtime\user\mods\SPTForecaster'
    New-Item -ItemType Directory -Force -Path $modDir | Out-Null
    Copy-Item $serverDll $modDir -Force
    Write-Host "Installed into $SPTPath" -ForegroundColor Green
}
