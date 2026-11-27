<#
.SYNOPSIS
  Builds the installer and the update package of Apparatus.

.DESCRIPTION
  1. publishes the app self-contained for win-x64 (Release),
  1b. adds the license files (LICENSE, THIRD-PARTY-NOTICES.md and a licenses folder) to the published folder,
  2. packs it with Velopack ("vpk"): Setup.exe, the full package and, when an earlier release is in the output folder, a delta.

  Requires:  dotnet tool install -g vpk --version 1.2.161   (the version of the Velopack package the app uses)

  Optional code signing (without it Windows SmartScreen warns on first run): pass -SignParams with the arguments for signtool,
  for example  -SignParams '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /f cert.pfx /p secret'.

  The version is the <Version> of Directory.Build.props; -Version replaces it for a throw-away build (the pull request check does).

.EXAMPLE
  .\build\release.ps1
#>
param(
    [string] $Version = "",
    [string] $Output = "artifacts\releases",
    [string] $SignParams = "",
    # By default a missing license file stops the build: an installer without the licenses of what it carries must not be released.
    [switch] $AllowMissingLicenses
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (-not $Version) { $Version = & (Join-Path $PSScriptRoot "get-version.ps1") }
Write-Host "Building Apparatus $Version"

# The id decides the install folder (%LOCALAPPDATA%\ApparatusApp). It must differ from the data folder
# (%LOCALAPPDATA%\Apparatus): uninstalling removes the whole install folder and must never take the user's data with it.
$packId = "ApparatusApp"
$publish = Join-Path $root "artifacts\publish"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src/Apparatus.App -c Release -r win-x64 --self-contained -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Licenses of what the installer carries (THIRD-PARTY-NOTICES.md explains why). The app's own license and the notices go next to the
# program; the full texts of the .NET runtime and of the Windows App SDK come from the NuGet cache into a "licenses" folder.
Copy-Item (Join-Path $root "LICENSE") $publish
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") $publish
$licenses = Join-Path $publish "licenses"
New-Item -ItemType Directory -Force $licenses | Out-Null
$nugetCache = (dotnet nuget locals global-packages -l) -replace '^\s*global-packages:\s*', ''
$missingLicenses = @()
function Copy-PackageFile([string] $package, [string] $versionPrefix, [string] $file, [string] $as) {
    $dir = Join-Path $nugetCache.Trim() $package
    $version = Get-ChildItem $dir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "$versionPrefix*" } | Sort-Object { [version]($_.Name -replace '[^\d.].*$', '') } -Descending | Select-Object -First 1
    $source = if ($version) { Join-Path $version.FullName $file } else { "" }
    if ($source -and (Test-Path $source)) { Copy-Item $source (Join-Path $licenses $as) }
    else {
        Write-Warning "License file not found: $package $versionPrefix* $file. The installer would be missing licenses\$as."
        $script:missingLicenses += $as
    }
}
Copy-PackageFile "microsoft.netcore.app.runtime.win-x64" "10." "LICENSE.TXT" "dotnet-runtime-LICENSE.TXT"
Copy-PackageFile "microsoft.netcore.app.runtime.win-x64" "10." "THIRD-PARTY-NOTICES.TXT" "dotnet-runtime-THIRD-PARTY-NOTICES.TXT"
Copy-PackageFile "microsoft.windowsappsdk.foundation" "2." "license.txt" "WindowsAppSDK-license.txt"
# The third-party notices that the Windows App SDK itself ships (the open source code it incorporates), copied unchanged.
Copy-PackageFile "microsoft.windowsappsdk.base" "2." "NOTICE.txt" "WindowsAppSDK-NOTICE.txt"
Copy-PackageFile "microsoft.windowsappsdk.winui" "2." "NOTICE.txt" "WindowsAppSDK-WinUI-NOTICE.txt"
if ($missingLicenses.Count -gt 0 -and -not $AllowMissingLicenses) {
    throw "License files missing from the package: $($missingLicenses -join ', '). Restore the NuGet packages first, or pass -AllowMissingLicenses for a test build."
}

$vpkArgs = @(
    "pack",
    "--packId", $packId,
    "--packVersion", $Version,
    "--packDir", $publish,
    "--runtime", "win-x64",
    "--mainExe", "Apparatus.exe",
    "--packTitle", "Apparatus",
    "--icon", (Join-Path $root "src\Apparatus.App\Assets\apparatus.ico"),
    "--outputDir", (Join-Path $root $Output)
)
if ($SignParams) { $vpkArgs += @("--signParams", $SignParams) }

vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Get-ChildItem (Join-Path $root $Output) | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
