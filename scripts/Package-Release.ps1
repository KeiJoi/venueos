<#
.SYNOPSIS
    Builds VenueOS in Release and stages a minimal, deterministic distribution ZIP for the Dalamud
    Experimental Plugin Repository -everything VenueOS actually needs at runtime, nothing DalamudPackager's
    own default zip pulls in only because a transitive NuGet dependency (SQLitePCLRaw) ships every platform's
    native asset. Local development (loading the DLL straight from bin/Debug or bin/Release) is untouched by
    this script -it only reads already-built output and copies it elsewhere.

.PARAMETER Configuration
    Build configuration to package. Always Release for a public release; Debug is accepted for local testing
    of the packaging step itself, but the resulting ZIP must never be distributed.
#>
param(
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $repoRoot "src\VenueOS.Plugin\VenueOS.Plugin.csproj"
$releaseBin = Join-Path $repoRoot "src\VenueOS.Plugin\bin\$Configuration"
$stagingRoot = Join-Path $repoRoot "release\staging"
$stagingDir = Join-Path $stagingRoot "VenueOS"
$outputDir = Join-Path $repoRoot "release"

Write-Host "Building $pluginProject ($Configuration)..."
dotnet build $pluginProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

if (Test-Path $stagingRoot) { Remove-Item $stagingRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

# VenueOS's own assemblies, plus every third-party runtime dependency it actually loads and Dalamud does not
# provide. Dalamud.Bindings.ImGui and FFXIVClientStructs are deliberately absent -they're referenced with
# Private="false" in VenueOS.Plugin.csproj because the Dalamud host already provides them; shipping them here
# would just duplicate (and risk conflicting with) the host's own copies. PDBs are intentionally excluded from
# the public package -the developer's local Debug/Release bin output still has them for local debugging.
$requiredFiles = @(
    "VenueOS.dll", "VenueOS.json", "VenueOS.deps.json",
    "VenueOS.Core.dll", "VenueOS.Venues.dll", "VenueOS.Services.dll", "VenueOS.UI.dll", "VenueOS.Modules.Operations.dll",
    "ECommons.dll",
    "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.batteries_v2.dll", "SQLitePCLRaw.provider.e_sqlite3.dll",
    "ClosedXML.dll", "ClosedXML.Parser.dll", "DocumentFormat.OpenXml.dll", "DocumentFormat.OpenXml.Framework.dll",
    "ExcelNumberFormat.dll", "RBush.dll", "SixLabors.Fonts.dll", "System.IO.Packaging.dll"
)

foreach ($file in $requiredFiles) {
    $source = Join-Path $releaseBin $file
    if (-not (Test-Path $source)) { throw "Required release file missing: $file (looked in $releaseBin -did the build actually produce it?)" }
    Copy-Item $source -Destination $stagingDir
}

# Only the Windows x64 native SQLite library is needed: Dalamud/XIVLauncher only ever runs FFXIV as a 64-bit
# Windows process, so DalamudPackager's own default zip (see bin/Release/VenueOS/latest.zip) dragging in
# linux-*/osx-*/win-x86/win-arm64/browser-wasm native libraries for every SQLitePCLRaw-supported platform is
# pure dead weight -roughly 25MB of it.
$nativeSource = Join-Path $releaseBin "runtimes\win-x64\native\e_sqlite3.dll"
if (-not (Test-Path $nativeSource)) { throw "Required native asset missing: runtimes\win-x64\native\e_sqlite3.dll" }
New-Item -ItemType Directory -Path (Join-Path $stagingDir "runtimes\win-x64\native") -Force | Out-Null
Copy-Item $nativeSource -Destination (Join-Path $stagingDir "runtimes\win-x64\native\e_sqlite3.dll")

$csprojContent = Get-Content $pluginProject -Raw
if ($csprojContent -notmatch '<Version>([^<]+)</Version>') { throw "Could not read <Version> from $pluginProject." }
$version = $Matches[1]

if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }
$zipPath = Join-Path $outputDir "VenueOS-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$zipSize = (Get-Item $zipPath).Length
Write-Host ""
Write-Host "Staged package: $stagingDir"
Write-Host "Release ZIP:    $zipPath ($([math]::Round($zipSize / 1MB, 2)) MB)"
Write-Host ""
Write-Host "Contents:"
Get-ChildItem -Recurse $stagingDir | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
    $relative = $_.FullName.Substring($stagingDir.Length + 1)
    Write-Host ("  {0,10:N0}  {1}" -f $_.Length, $relative)
}
