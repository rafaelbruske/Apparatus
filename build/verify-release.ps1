<#
.SYNOPSIS
  Checks that what build\release.ps1 produced is complete before it is published. Any problem stops with an error.

.DESCRIPTION
  Used by the release workflow (.github/workflows/release.yml) and safe to run by hand after release.ps1:
    - the installer, the portable zip, the full package and the update feed exist, and the installer has a plausible size;
    - the feed (releases.win.json and RELEASES) lists exactly this version, with the size and the SHA-256 of the file on disk;
    - Apparatus.exe carries this version;
    - the license files are in the published folder AND inside the package that the installer and the updates deliver.

  Without -Version it checks the version written in Directory.Build.props.

.EXAMPLE
  .\build\verify-release.ps1
#>
param(
    [string] $Version = "",
    [string] $Output = "artifacts\releases",
    [string] $Publish = "artifacts\publish"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = & (Join-Path $PSScriptRoot "get-version.ps1") }
function Resolve-Folder([string] $path) { if ([System.IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $root $path } }
$out = Resolve-Folder $Output
$pub = Resolve-Folder $Publish
$problems = [System.Collections.Generic.List[string]]::new()
function Fail([string] $message) { $problems.Add($message) }

# Same list as in release.ps1 (kept here on purpose: this is the second, independent check).
$licenseFiles = @(
    "dotnet-runtime-LICENSE.TXT", "dotnet-runtime-THIRD-PARTY-NOTICES.TXT",
    "WindowsAppSDK-license.txt", "WindowsAppSDK-NOTICE.txt", "WindowsAppSDK-WinUI-NOTICE.txt")

# --- files that are delivered
$full = "ApparatusApp-$Version-full.nupkg"
foreach ($name in @("ApparatusApp-win-Setup.exe", "ApparatusApp-win-Portable.zip", $full, "RELEASES", "releases.win.json")) {
    $path = Join-Path $out $name
    if (-not (Test-Path $path)) { Fail "Missing in ${Output}: $name"; continue }
    if ((Get-Item $path).Length -eq 0) { Fail "Empty file in ${Output}: $name" }
}
$setup = Join-Path $out "ApparatusApp-win-Setup.exe"
if ((Test-Path $setup) -and (Get-Item $setup).Length -lt 40MB) {
    Fail "Setup.exe is only $([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB; it was about 80 MB. Something was left out."
}

# --- the update feed lists this version and describes the file on disk
$feed = Join-Path $out "releases.win.json"
$fullPath = Join-Path $out $full
if ((Test-Path $feed) -and (Test-Path $fullPath)) {
    $assets = @((Get-Content $feed -Raw | ConvertFrom-Json).Assets)
    $entry = $assets | Where-Object { $_.FileName -eq $full -and $_.Type -eq "Full" -and $_.Version -eq $Version }
    if (-not $entry) { Fail "releases.win.json has no Full asset $full for version $Version." }
    else {
        $size = (Get-Item $fullPath).Length
        $sha256 = (Get-FileHash $fullPath -Algorithm SHA256).Hash
        if ($entry.Size -ne $size) { Fail "releases.win.json says $($entry.Size) bytes for $full, the file has $size." }
        if ($entry.SHA256 -ne $sha256) { Fail "releases.win.json has another SHA-256 than $full." }
    }
}
$legacyFeed = Join-Path $out "RELEASES"
if ((Test-Path $legacyFeed) -and -not (Select-String -Path $legacyFeed -SimpleMatch $full -Quiet)) {
    Fail "RELEASES does not list $full."
}

# --- the program carries the version
$exe = Join-Path $pub "Apparatus.exe"
if (-not (Test-Path $exe)) { Fail "Missing in ${Publish}: Apparatus.exe" }
else {
    $fileVersion = (Get-Item $exe).VersionInfo.ProductVersion
    if (-not $fileVersion -or -not $fileVersion.StartsWith($Version)) { Fail "Apparatus.exe says version '$fileVersion', expected $Version." }
}

# --- the licenses, in the published folder and inside the package
foreach ($name in @("LICENSE", "THIRD-PARTY-NOTICES.md")) {
    if (-not (Test-Path (Join-Path $pub $name))) { Fail "Missing in ${Publish}: $name" }
}
foreach ($name in $licenseFiles) {
    $path = Join-Path $pub "licenses\$name"
    if (-not (Test-Path $path)) { Fail "Missing in ${Publish}\licenses: $name" }
    elseif ((Get-Item $path).Length -eq 0) { Fail "Empty license file: $name" }
}
if (Test-Path $fullPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($fullPath)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })
        $expected = @("lib/app/Apparatus.exe", "lib/app/LICENSE", "lib/app/THIRD-PARTY-NOTICES.md") + ($licenseFiles | ForEach-Object { "lib/app/licenses/$_" })
        foreach ($name in $expected) {
            if ($entries -notcontains $name) { Fail "The package $full does not contain $name." }
        }
    }
    finally { $zip.Dispose() }
}

if ($problems.Count -gt 0) {
    Write-Host "Release check FAILED:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    throw "The release for $Version is incomplete ($($problems.Count) problem(s))."
}
Write-Host "Release check OK for ${Version}: installer, portable, full package, feed, version and licenses." -ForegroundColor Green
