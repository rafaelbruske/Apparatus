<#
.SYNOPSIS
  Prints the version of Apparatus, which is written in one place only: <Version> in Directory.Build.props.

.DESCRIPTION
  release.ps1 and verify-release.ps1 use it when no -Version is given, and the release workflow refuses a tag that says another
  number. Fails when the file does not hold exactly one version in the X.Y.Z form.

.EXAMPLE
  .\build\get-version.ps1
#>
$ErrorActionPreference = "Stop"
$props = Join-Path (Split-Path $PSScriptRoot -Parent) "Directory.Build.props"
$nodes = @(([xml](Get-Content $props -Raw -Encoding UTF8)).SelectNodes("/Project/PropertyGroup/Version"))
if ($nodes.Count -ne 1) { throw "Directory.Build.props must have exactly one <Version>, found $($nodes.Count)." }
$version = $nodes[0].InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "The <Version> '$version' in Directory.Build.props is not in the X.Y.Z form." }
$version
