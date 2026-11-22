<#
.SYNOPSIS
  Builds the installer and the update package of Apparatus.

.DESCRIPTION
  1. publishes the app self-contained for win-x64 (Release),
  2. packs it with Velopack ("vpk"): Setup.exe, the full package and, when an earlier release is in the output folder, a delta.

  Requires:  dotnet tool install -g vpk --version 1.2.161   (the version of the Velopack package the app uses)

  Optional code signing (without it Windows SmartScreen warns on first run): pass -SignParams with the arguments for signtool,
  for example  -SignParams '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /f cert.pfx /p secret'.

.EXAMPLE
  .\build\release.ps1 -Version 0.7.1
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Output = "artifacts\releases",
    [string] $SignParams = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# The id decides the install folder (%LOCALAPPDATA%\ApparatusApp). It must differ from the data folder
# (%LOCALAPPDATA%\Apparatus): uninstalling removes the whole install folder and must never take the user's data with it.
$packId = "ApparatusApp"
$publish = Join-Path $root "artifacts\publish"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src/Apparatus.App -c Release -r win-x64 --self-contained -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

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
