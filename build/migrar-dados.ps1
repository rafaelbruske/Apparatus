<#
.SYNOPSIS
  Moves the Apparatus settings and data that were kept inside the Claude app's private storage into the real folders.

.DESCRIPTION
  WHY THIS EXISTS. Programs started by the Claude desktop app run inside that app's sandbox (an MSIX package). Everything they write
  under %LOCALAPPDATA%, %APPDATA% and HKCU lands in a private copy that only they can see, not in the real folders. An Apparatus run that way
  has settings, snippets, actions, pinned items and history that Windows itself (and a normal Apparatus started from the
  Start menu) never sees.

  This script copies that private copy over the real folders, after backing the real ones up. Run it ONCE, in a normal
  PowerShell window that you opened yourself (Win+X, Terminal), NOT from the Claude app, and BEFORE installing Apparatus with Setup.exe.

  Without -Aplicar it only shows what it would do.

.EXAMPLE
  .\build\migrar-dados.ps1            # shows the plan
  .\build\migrar-dados.ps1 -Aplicar   # does it
#>
param([switch] $Aplicar)

$ErrorActionPreference = "Stop"

# Refuse to run from inside the sandbox: there the real folders cannot even be seen.
$parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId
$chain = @()
for ($i = 0; $i -lt 6 -and $parent; $i++) {
    $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$parent" -ErrorAction SilentlyContinue
    if (-not $proc) { break }
    $chain += $proc.Name
    $parent = $proc.ParentProcessId
}
if ($chain -contains "claude.exe") {
    throw "Abra um PowerShell proprio (Win+X > Terminal) e rode de la: aqui dentro o script so enxergaria a copia privada do Claude."
}

if (Get-Process Apparatus -ErrorAction SilentlyContinue) {
    throw "Feche o Apparatus antes (icone da bandeja > Sair), para os bancos de dados nao estarem em uso."
}

$packages = Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter "Claude_*" -ErrorAction SilentlyContinue
if (-not $packages) { throw "Nao achei a pasta privada do Claude em $env:LOCALAPPDATA\Packages. Nada a migrar." }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path $env:USERPROFILE "Apparatus-backup-dados-$stamp"
$pairs = @()
foreach ($pkg in $packages) {
    $pairs += @{ Name = "Local";   Real = "$env:LOCALAPPDATA\Apparatus"; Private = "$($pkg.FullName)\LocalCache\Local\Apparatus" }
    $pairs += @{ Name = "Roaming"; Real = "$env:APPDATA\Apparatus";      Private = "$($pkg.FullName)\LocalCache\Roaming\Apparatus" }
}
$pairs = $pairs | Where-Object { Test-Path $_.Private }
if (-not $pairs) { throw "A pasta privada do Claude nao tem dados do Apparatus. Nada a migrar." }

foreach ($p in $pairs) {
    $count = (Get-ChildItem $p.Private -Recurse -File | Measure-Object).Count
    $realCount = if (Test-Path $p.Real) { (Get-ChildItem $p.Real -Recurse -File | Measure-Object).Count } else { 0 }
    Write-Host ("[{0}] copia privada ({1} arquivos) -> {2} (hoje com {3} arquivos)" -f $p.Name, $count, $p.Real, $realCount)
}
Write-Host "A pasta real atual sera guardada em: $backup"

if (-not $Aplicar) {
    Write-Host "`nNada foi alterado. Rode com -Aplicar para fazer a migracao."
    return
}

foreach ($p in $pairs) {
    if (Test-Path $p.Real) {
        robocopy $p.Real (Join-Path $backup $p.Name) /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Falhou o backup de $($p.Real) (robocopy $LASTEXITCODE). Nada foi alterado depois disso." }
    }
}
foreach ($p in $pairs) {
    # /MIR makes the real folder an exact copy (database files and their -wal/-shm companions stay a consistent set)
    robocopy $p.Private $p.Real /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Falhou a copia para $($p.Real) (robocopy $LASTEXITCODE). Seu backup esta em $backup." }
}
Write-Host "`nPronto. Dados migrados; o backup do que havia antes esta em $backup."
Write-Host "Agora instale o Apparatus: de um duplo clique em artifacts\releases\ApparatusApp-win-Setup.exe no Explorador de Arquivos."
