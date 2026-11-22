<#
.SYNOPSIS
  Browsing the snippets or actions list must not raise "Alterações não salvas" unless something was really edited.

.DESCRIPTION
  Starts Apparatus with a throw-away profile (APPARATUS_PROFILE_DIR) and drives its Snippets or Actions window through
  UI Automation: no mouse, no keys. First a CONTROL (edit the name, change the selection: the dialog must appear, which proves
  that the detection works), then eight changes of selection without editing: no dialog may appear.
  It runs next to a normal Apparatus (a separate instance) and touches only the process it starts.

.EXAMPLE
  .\tests\Apparatus.UI\browse-without-edits.ps1 -Exe src\Apparatus.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Apparatus.exe `
      -Profile $env:TEMP\apparatus-ui-test -Out $env:TEMP\result.txt              # Snippets
  ... -Switch --actions -Title "A"                                                   # Actions
#>
param([string]$Exe, [string]$Profile, [string]$Out, [string]$Switch = "--snippets", [string]$Title = "Snippets")
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$log = New-Object System.Collections.Generic.List[string]; function L($s) { $log.Add($s) }
if (Test-Path $Profile) { Remove-Item $Profile -Recurse -Force }
New-Item -ItemType Directory (Join-Path $Profile "config") | Out-Null
[IO.File]::WriteAllText((Join-Path $Profile "config\settings.json"), '{"IndexFiles":false,"SnippetExpansion":false,"ExtensionSampleInstalled":true,"Hotkey":"Ctrl+Alt+F10","StartMenuEnabled":false,"RunAtStartup":false}')
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe; $psi.Arguments = "--background $Switch"; $psi.UseShellExecute = $false
$psi.EnvironmentVariables["APPARATUS_PROFILE_DIR"] = $Profile
$p = [System.Diagnostics.Process]::Start($psi)
function ProcWindows() { $AE::RootElement.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id))) }
$win = $null
for ($i = 0; $i -lt 60 -and -not $win; $i++) { Start-Sleep -Milliseconds 500; foreach ($w in (ProcWindows)) { if ($w.Current.Name -like "$Title*") { $win = $w } } }
if (-not $win) { L "no window"; try { $p.Kill() } catch {}; $log | Out-File $Out -Encoding utf8; exit 3 }
Start-Sleep 2
function Named($name) { $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
$items = $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
$items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1
$save = Named "Salvar"
L ("Salvar enabled before edit: " + $(if ($save) { $save.Current.IsEnabled } else { "(button not found)" }))
$edit = Named "Nome"
L ("Nome field found: " + [bool]$edit + "  type=" + $(if ($edit) { $edit.Current.ControlType.ProgrammaticName }))
if ($edit) {
  $vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  L ("value before: [" + $vp.Current.Value + "]")
  $vp.SetValue($vp.Current.Value + " editado"); Start-Sleep 1
  L ("value after:  [" + $vp.Current.Value + "]")
  L ("Salvar enabled after edit: " + $(if ($save) { (Named "Salvar").Current.IsEnabled }))
}
$items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1.5
L "--- top-level windows of the process after selecting another item"
foreach ($w in (ProcWindows)) { L ("  [{0}] class={1}" -f $w.Current.Name, $w.Current.ClassName) }
L "--- elements whose name looks like the dialog"
foreach ($w in (ProcWindows)) { foreach ($e in $w.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { $n = $e.Current.Name; if ($n -match "Altera|Deseja|Descartar|Cancelar|ContentDialog") { L ("  {0} | {1} | {2}" -f $e.Current.ControlType.ProgrammaticName, $n, $e.Current.ClassName) } } }

function DialogUp() {
    foreach ($w in (ProcWindows)) { foreach ($e in $w.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { if ($e.Current.Name -match "^Altera.*salvas$") { return $true } } }
    return $false
}
function Click($label) {
    foreach ($w in (ProcWindows)) { $b = $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $label))); if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } }
    return $false
}
L ("CONTROL: dialog up after a real edit and a change of selection: " + (DialogUp))
[void](Click "Descartar"); Start-Sleep 1
L ("dialog gone after Descartar: " + (-not (DialogUp)))
$items = $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
$n = 0
foreach ($i in 0, 2, 1, 3, 0, 2, 3, 1) { $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 1300; if (DialogUp) { $n++; [void](Click "Cancelar"); Start-Sleep -Milliseconds 500 } }
L "BROWSING 8 times without editing: dialogs = $n (must be 0)"
try { $p.Kill() } catch {}
$control = ($log | Where-Object { $_ -like "CONTROL:*True" }).Count -eq 1
$quiet = ($log | Where-Object { $_ -like "BROWSING*dialogs = 0 *" }).Count -eq 1
$log.Add($(if ($control -and $quiet) { "RESULT: PASS" } else { "RESULT: FAIL (control=$control, quiet=$quiet)" }))
$log | Out-File $Out -Encoding utf8
