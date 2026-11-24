<#
.SYNOPSIS
  With "AssistantKeepOpen" on, the launcher in AI mode stays visible after the focus goes elsewhere; with it off it hides.
.DESCRIPTION
  USES REAL INPUT (the hotkey and one Tab key, plus a Notepad window that takes the focus), for about a minute.
  Starts Apparatus with a throw-away profile, so it does not touch the installed one. Never sends Esc to the desktop:
  the window is closed through its "x" button (UI Automation). Run it with the hands off the keyboard and mouse.
#>
param([string]$Exe, [string]$Profile, [string]$Out)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint flags, UIntPtr extra);
  public static List<IntPtr> Of(uint pid) { var r = new List<IntPtr>(); EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid) r.Add(h); return true; }, IntPtr.Zero); return r; }
  public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }
  public static void Chord(byte[] mods, byte key) { foreach (var m in mods) keybd_event(m, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 2, UIntPtr.Zero); foreach (var m in mods) keybd_event(m, 0, 2, UIntPtr.Zero); }
}
"@
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$log = New-Object System.Collections.Generic.List[string]; function L($s) { $log.Add($s) }
$results = @{}

function Run-Case([bool]$keepOpen) {
  $name = "keepOpen=$keepOpen"
  $prof = "$Profile-$keepOpen"
  if (Test-Path $prof) { Remove-Item $prof -Recurse -Force }
  New-Item -ItemType Directory (Join-Path $prof "config") | Out-Null
  $json = '{"IndexFiles":false,"SnippetExpansion":false,"ExtensionSampleInstalled":true,"Hotkey":"Ctrl+Alt+F10","StartMenuEnabled":false,"RunAtStartup":false,"AssistantEnabled":true,"AssistantKeepOpen":' + $keepOpen.ToString().ToLower() + '}'
  [IO.File]::WriteAllText((Join-Path $prof "config\settings.json"), $json)
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $Exe; $psi.Arguments = "--background"; $psi.UseShellExecute = $false
  $psi.EnvironmentVariables["APPARATUS_PROFILE_DIR"] = $prof
  $p = [System.Diagnostics.Process]::Start($psi)
  $note = $null
  try {
    Start-Sleep 6
    [W]::Chord(@(0x11, 0x12), 0x79)   # Ctrl+Alt+F10
    Start-Sleep 2
    $vis = @([W]::Of($p.Id) | Where-Object { [W]::IsWindowVisible($_) })
    L "$name : visible windows after hotkey = $($vis.Count), foreground is Apparatus = $([W]::ForegroundPid() -eq $p.Id)"
    if ($vis.Count -eq 0 -or [W]::ForegroundPid() -ne $p.Id) { return "INCONCLUSIVE (launcher did not open or lacks focus)" }
    $launcher = $vis[0]
    [W]::Chord(@(), 0x09)   # Tab: enter AI mode (box is empty)
    Start-Sleep 1.5
    $root = $AE::FromHandle($launcher)
    $x = $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Fechar o launcher")))
    L "$name : close button present (AI mode on) = $([bool]$x)"
    if (-not $x) { return "INCONCLUSIVE (AI mode did not start)" }
    $n = Start-Process notepad.exe -PassThru
    Start-Sleep 3
    $still = [W]::IsWindowVisible($launcher)
    L "$name : launcher still visible after focus went to Notepad = $still (foreground pid is Apparatus: $([W]::ForegroundPid() -eq $p.Id))"
    if ($keepOpen -and $still) {
      # the hotkey must bring it back to the front without hiding it
      [W]::Chord(@(0x11, 0x12), 0x79); Start-Sleep 2
      $back = [W]::IsWindowVisible($launcher) -and ([W]::ForegroundPid() -eq $p.Id)
      L "$name : hotkey while unfocused brought it back = $back"
      $x = $AE::FromHandle($launcher).FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Fechar o launcher")))
      if ($x) { $x.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1 }
      $closed = -not [W]::IsWindowVisible($launcher)
      L "$name : x button closed it = $closed"
      if (-not ($back -and $closed)) { return "FAIL (back=$back, closed=$closed)" }
    }
    try { $n.Kill() } catch {}
    if ($keepOpen) { if ($still) { return "PASS" } else { return "FAIL (hid on focus loss)" } }
    else { if (-not $still) { return "PASS" } else { return "FAIL (stayed open although the option is off)" } }
  } finally {
    try { $p.Kill() } catch {}
    Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddMinutes(-2) } | ForEach-Object { try { $_.Kill() } catch {} }
  }
}
foreach ($k in $false, $true) { $r = Run-Case $k; L "RESULT keepOpen=$k : $r" }
$log | Out-File $Out -Encoding utf8
