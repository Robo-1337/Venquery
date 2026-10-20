# Venquery FEATURE checks: drive the real app through its main features with throwaway test data and check the
# results on disk / on screen, not just that pages open. Called by scripts\test.ps1 and runnable
# on its own:
#
#   scripts\feature-check.ps1 -Exe "dist\Venquery\Venquery.exe" [-SettingsDir Venquery-Community]
#
# Safe by construction:
#   - runs one build with its OWN settings folder (default "Venquery-Community"), whose settings.json + Organizer history are backed up first and restored afterwards;
#   - every file it touches lives in test-output\FeatureTest (created fresh, deleted at the end);
#   - it never presses anything that installs, runs scripts, elevates or downloads;
#   - scripts the app writes into Downloads during the run (repair / restore-point / app backup) are deleted
#     afterwards: only NEW top-level entries named venquery* that appeared during this run.
# Output: one object per check (Area, Check, Status = PASS/FAIL/SKIP, Detail).
#
# COVERAGE (keep this list in step with the app; add a check for every new feature):
#   Automated here: Devices & Drivers scan + tiles, device search, device detail, show-older toggle, select all ·
#   theme switch · window maximise/restore · View Log + Save log · Export report · Windows Update drivers window ·
#   Health overview tiles · Showcase · restore-point + SFC/DISM script generation · Repair Kit script generation ·
#   Startup & Tasks lists · Organize scan, rule (.ext → folder), organize by type, Undo, duplicates, Keep changes,
#   rule removal · What's using space scan · Portable apps scan + backup + receipt · Documents scan + backup ·
#   Installed apps read + save · Restore tab · Prepare for Reset wizard open/close · Settings left-hand mode.
#   NOT automated (would change the PC, need UAC, download, or need a second PC) — test by hand before a release:
#   running any generated script (repair, restore point, SFC/DISM, winget install, driver restore),
#   the Repair Kit full read (UAC), startup-program / scheduled-task on/off switches, driver downloads, a real
#   Restore into your folders, background-image import (file dialog).
#   Unit tests separately cover the engines behind these (script syntax, restore locks, organizer, tiles...).
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$SettingsDir = 'Venquery-Community'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'test-output\FeatureTest'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class FcWin2 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  // Visible windows WITH a title: the main window, dialogs and message boxes, but not tooltips / popups.
  public static int VisibleWindows(int pid) {
    int n = 0;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h) && GetWindowTextLength(h) > 0) n++; return true; }, IntPtr.Zero);
    return n;
  }
}
"@ -ErrorAction SilentlyContinue

$UA = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function New-Check([string]$Check, [bool]$Ok, [string]$Detail = '') {
    [pscustomobject]@{ Area = 'Feature'; Check = $Check; Status = $(if ($Ok) { 'PASS' } else { 'FAIL' }); Detail = $Detail }
}
function Wait-Until([scriptblock]$Cond, [int]$Seconds = 30) {
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) { try { if (& $Cond) { return $true } } catch { }; Start-Sleep -Milliseconds 500 }
    return $false
}
function El([string]$Id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($UA::AutomationIdProperty, $Id)
    $all = @($script:win.FindAll($TS::Descendants, $c))
    # Prefer an on-screen match; a control scrolled out of view is still usable (collapsed pages aren't in the tree).
    $on = @($all | Where-Object { -not $_.Current.IsOffscreen })
    if ($on.Count) { $on[0] } elseif ($all.Count) { $all[0] } else { $null }
}
# Is the control actually showing? (Collapsed WPF controls stay in the UIA tree, marked off-screen.)
function Visible([string]$Id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($UA::AutomationIdProperty, $Id)
    @($script:win.FindAll($TS::Descendants, $c) | Where-Object { -not $_.Current.BoundingRectangle.IsEmpty }).Count -gt 0
}
function Click([string]$Id) { (El $Id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Pick([string]$Id) { (El $Id).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 1200 }
function TextLike([string]$Pattern) {
    $c = New-Object System.Windows.Automation.PropertyCondition($UA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    @($script:win.FindAll($TS::Descendants, $c) | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.Name -like $Pattern })
}
function TextOf([string]$Id) { $e = El $Id; if ($e) { $e.Current.Name } else { '' } }
function Dialogs() { [FcWin2]::VisibleWindows($script:proc.Id) - 1 }
# Close a secondary window of ours (Showcase, WU drivers, a modal) by title with a key (Esc by default).
function Send-To([string]$Title, [string]$Keys = '{ESC}') {
    $ws = New-Object -ComObject WScript.Shell
    [void]$ws.AppActivate($Title); Start-Sleep -Milliseconds 500; $ws.SendKeys($Keys); Start-Sleep -Milliseconds 800
}
# Close any Explorer window the app opened on a folder under $Under (Export reveals the report in Explorer).
function Close-ExplorerUnder([string]$Under) {
    try {
        foreach ($w in @((New-Object -ComObject Shell.Application).Windows())) {
            try { if ($w.Document.Folder.Self.Path -like "$Under*") { $w.Quit() } } catch { }
        }
    } catch { }
}
# Answer a Yes/No MessageBox with "Yes" (its buttons don't expose InvokePattern; Y is the Yes accelerator).
function Answer-Yes([string]$Title) {
    if (-not (Wait-Until { (Dialogs) -gt 0 } 10)) { return $false }
    $ws = New-Object -ComObject WScript.Shell
    [void]$ws.AppActivate($Title); Start-Sleep -Milliseconds 400; $ws.SendKeys('y'); Start-Sleep -Milliseconds 800
    return (Wait-Until { (Dialogs) -eq 0 } 10)
}

# ---- set up: test data + isolated settings ------------------------------------------------------------
$cfgDir = Join-Path $env:LOCALAPPDATA $SettingsDir
$bak = Join-Path $env:TEMP ("vq-featurecheck-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $bak | Out-Null
$hadSettings = Test-Path (Join-Path $cfgDir 'settings.json')
if ($hadSettings) { Copy-Item -LiteralPath (Join-Path $cfgDir 'settings.json') -Destination $bak -Force }
$hadOrg = Test-Path (Join-Path $cfgDir 'Organizer')
if ($hadOrg) { Copy-Item -LiteralPath (Join-Path $cfgDir 'Organizer') -Destination $bak -Recurse -Force }

if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force }
$org = Join-Path $work 'Organize'; $port = Join-Path $work 'Portable'; $outDir = Join-Path $work 'Out'; $docs = Join-Path $work 'Docs'
New-Item -ItemType Directory -Force -Path $org, (Join-Path $port 'TestTool'), $outDir, $docs | Out-Null
'report' | Set-Content (Join-Path $org 'report.pdf'); 'photo' | Set-Content (Join-Path $org 'photo.jpg'); 'song' | Set-Content (Join-Path $org 'song.mp3')
'same bytes in both' | Set-Content (Join-Path $org 'dupA.txt'); 'same bytes in both' | Set-Content (Join-Path $org 'dupB.txt')   # a duplicate pair
'letter' | Set-Content (Join-Path $docs 'letter.pdf'); 'holiday' | Set-Content (Join-Path $docs 'holiday.jpg')
$exeDir = Split-Path -Parent (Resolve-Path $Exe)
$hadReports = Test-Path (Join-Path $exeDir 'SystemReports')
Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\where.exe') -Destination (Join-Path $port 'TestTool\TestTool.exe')   # a small signed exe as a "portable app"

New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$s = if ($hadSettings) { Get-Content (Join-Path $cfgDir 'settings.json') -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
foreach ($kv in @{ LastView = 'drivers'; Theme = 'dark'; OrganizerFolder = $org; PortableScanFolders = @($port);
                   PortableBackupParent = $outDir; DocScanFolders = @($docs); DocBackupParent = $outDir;
                   BackupReceipts = $true; NavigationOnRight = $false; OrganizerUndoDays = 2 }.GetEnumerator()) {
    $s | Add-Member -NotePropertyName $kv.Key -NotePropertyValue $kv.Value -Force
}
$s | ConvertTo-Json -Depth 10 | Out-File (Join-Path $cfgDir 'settings.json') -Encoding utf8

# Downloads before the run, so only the files this run makes there are cleaned up afterwards.
$dlDir = (New-Object -ComObject Shell.Application).NameSpace('shell:Downloads').Self.Path
$dlBefore = @{}; foreach ($e in @(Get-ChildItem -LiteralPath $dlDir -Force -ErrorAction SilentlyContinue)) { $dlBefore[$e.Name] = $true }
function New-InDownloads() { @(Get-ChildItem -LiteralPath $dlDir -Force -ErrorAction SilentlyContinue | Where-Object { -not $dlBefore.ContainsKey($_.Name) -and $_.Name -like 'venquery*' }) }

$results = New-Object System.Collections.Generic.List[object]
try {
    $script:proc = Start-Process -FilePath $Exe -PassThru
    if (-not (Wait-Until { $script:proc.Refresh(); $script:proc.MainWindowHandle -ne 0 } 30)) { $results.Add((New-Check 'App starts' $false 'no main window')); throw 'stop' }
    $script:win = $UA::FromHandle($script:proc.MainWindowHandle)

    # 1. Devices & Drivers: the scan finishes, lists devices and fills the summary tiles.
    $listed = Wait-Until { (TextLike '*devices listed*').Count -gt 0 -or (TextLike '*device listed*').Count -gt 0 } 90
    $items = 0
    $dl = El 'DeviceList'
    if ($dl) { $items = @($dl.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)).Count }
    $results.Add((New-Check 'Drivers: scan lists devices + tiles' ($listed -and $items -gt 0) "$items device row(s)"))
    $okTiles = (TextLike '*driver update*').Count -gt 0 -and (TextLike '*official source*').Count -gt 0
    $results.Add((New-Check 'Drivers: update + source tiles shown' $okTiles))

    # 1b. Device search filters the list, a device opens its detail, "Show older drivers" and Select All work.
    if ($items -gt 1) {
        $sb = El 'SearchBox'
        $sb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('zzzz-no-such-device'); Start-Sleep 1
        $none = @($dl.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)).Count
        $sb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue(''); Start-Sleep 1
        $all = @($dl.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)).Count
        $results.Add((New-Check 'Drivers: search filters the list' ($none -eq 0 -and $all -eq $items) "no-match=$none, cleared=$all"))
        $rows = @($dl.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition))
        $rows[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1
        $results.Add((New-Check 'Drivers: selecting a device opens its detail' ((TextLike '*Driver Options*').Count -gt 0)))
    }
    $older = El 'OlderVersionsToggle'
    if ($older) {
        $older.SetFocus(); (New-Object -ComObject WScript.Shell).SendKeys(' '); Start-Sleep 1
        $on = $older.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On'
        $older.SetFocus(); (New-Object -ComObject WScript.Shell).SendKeys(' '); Start-Sleep 1
        $results.Add((New-Check 'Drivers: Show older drivers toggles' ($on -and (Dialogs) -eq 0)))
    }
    $before = TextOf 'DownloadLabel'
    Click 'SelectAllButton'; Start-Sleep 1; $after = TextOf 'DownloadLabel'
    Click 'SelectAllButton'; Start-Sleep 1
    $results.Add((New-Check 'Drivers: Select All changes the download selection' ($before -ne $after) "'$before' -> '$after'"))

    # 2. Theme switch and back (every brush recolours live; must not error).
    Click 'ThemeButton'; Start-Sleep 1; Click 'ThemeButton'; Start-Sleep 1
    $results.Add((New-Check 'Theme: switch light and back' ((Dialogs) -eq 0 -and -not $script:proc.HasExited)))

    # 3. Window buttons: maximise then restore through the app's own caption buttons.
    $wp = $script:win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    Click 'MaxButton'; $maxOk = Wait-Until { $wp.Current.WindowVisualState -eq 'Maximized' } 5
    Click 'MaxButton'; $resOk = Wait-Until { $wp.Current.WindowVisualState -eq 'Normal' } 5
    $results.Add((New-Check 'Title bar: maximise + restore buttons' ($maxOk -and $resOk)))

    # 4. View Log flyout opens.
    Click 'ViewLogButton'; Start-Sleep 1
    $logOk = Visible 'LogBox'
    $results.Add((New-Check 'View Log opens' $logOk))
    # Save writes the session log next to the reports (SystemReports) and shows it in Explorer.
    if ($logOk) {
        Click 'SaveLogButton'
        $rep = Join-Path $exeDir 'SystemReports'
        $saved = Wait-Until { @(Get-ChildItem -LiteralPath $rep -Filter '*log*.txt' -Recurse -ErrorAction SilentlyContinue).Count -gt 0 } 20
        Start-Sleep 2; Close-ExplorerUnder $rep
        $results.Add((New-Check 'View Log: Save writes the session log' $saved))
    }

    # 4b. Export: the options dialog opens, Export writes a report into SystemReports next to the exe.
    Click 'ExportButton'
    if (Wait-Until { (Dialogs) -gt 0 } 10) {
        Send-To 'Export report' '{ENTER}'
        $rep = Join-Path $exeDir 'SystemReports'
        $wrote = Wait-Until { @(Get-ChildItem -LiteralPath $rep -Filter '*.txt' -Recurse -ErrorAction SilentlyContinue).Count -gt 0 } 60
        Start-Sleep 2; Close-ExplorerUnder $rep
        $results.Add((New-Check 'Export: writes the report' $wrote))
    } else { $results.Add((New-Check 'Export: writes the report' $false 'options dialog did not open')) }

    # 4c. Windows Update drivers window opens (read-only check) and closes.
    Click 'HeaderWuDrivers'
    $wuOpen = Wait-Until { (Dialogs) -gt 0 } 15
    if ($wuOpen) { Start-Sleep 2; Send-To 'Windows Update drivers' }
    $results.Add((New-Check 'Windows Update drivers: window opens + closes' ($wuOpen -and (Wait-Until { (Dialogs) -eq 0 } 15))))

    # 5. Health & Repair overview reads health (tiles appear).
    Pick 'NavHealth'; Pick 'TabHealthOverview'
    $results.Add((New-Check 'Health: overview tiles load' (Wait-Until { (TextLike '*memory*').Count -gt 0 -and (TextLike '*storage*').Count -gt 0 } 60)))

    # 5b. Showcase opens fullscreen and Esc closes it.
    Click 'HeaderShowcase'
    $scOpen = Wait-Until { (Dialogs) -gt 0 } 20
    if ($scOpen) { Start-Sleep 2; Send-To 'Showcase' }
    $results.Add((New-Check 'Showcase: opens + Esc closes' ($scOpen -and (Wait-Until { (Dialogs) -eq 0 } 10))))

    # 5c. Health: "Create a restore point" and "System file repair" WRITE their guarded scripts (never run here).
    foreach ($g in @(@('RpGenerateButton', 'restore-point'), @('SfcGenerateButton', 'SFC + DISM'))) {
        $n0 = (New-InDownloads).Count
        $b = El $g[0]
        if (-not $b) { $results.Add((New-Check "Health: $($g[1]) script is written" $false 'button not found')); continue }
        Click $g[0]
        $results.Add((New-Check "Health: $($g[1]) script is written" (Wait-Until { (New-InDownloads).Count -gt $n0 } 15)))
    }

    # 6. Startup & Tasks loads both lists.
    Pick 'TabHealthStartup'
    $results.Add((New-Check 'Startup & Tasks: lists load' (Wait-Until { (TextLike '*startup program*').Count -gt 0 -and (TextLike '*scheduled task*').Count -gt 0 } 60)))

    # 6b. Repair Kit (editions with it): tick one repair and generate its script (written, never run).
    if (El 'TabHealthTools') {
        Pick 'TabHealthTools'
        $fl = El 'FixList'
        $cbCond = New-Object System.Windows.Automation.PropertyCondition($UA::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
        $first = if ($fl) { @($fl.FindAll($TS::Descendants, $cbCond))[0] } else { $null }
        if ($first) {
            $first.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep 1
            $n0 = (New-InDownloads).Count
            Click 'GenerateSelectedButton'
            $results.Add((New-Check 'Repair Kit: generates the selected repair script' (Wait-Until { (New-InDownloads).Count -gt $n0 } 15)))
        } else { $results.Add((New-Check 'Repair Kit: generates the selected repair script' $false 'no repair checkboxes found')) }
    }

    # 7. Files > Organize: scan, preview, move, check on disk, then Undo and check everything is back.
    Pick 'NavFiles'; Pick 'TabFilesOrganize'
    Click 'ScanButton'
    $scanned = Wait-Until { (TextOf 'StatFiles') -eq '5' } 30
    $results.Add((New-Check 'Organize: scan finds the 5 test files' $scanned ("found '" + (TextOf 'StatFiles') + "'")))
    if ($scanned) {
        # A user rule (.pdf -> Docs) is added first, so organizing also proves rules win over the type folder.
        (El 'RuleExtBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('.pdf')
        (El 'RuleFolderBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Docs')
        Click 'AddRuleButton'; Start-Sleep 1
        $ruled = (TextLike '.pdf*Docs').Count -gt 0
        $results.Add((New-Check 'Organize: a rule (.pdf to Docs) is added' $ruled))
        Click 'OrganizeButton'; Start-Sleep 1; Click 'ConfirmButton'
        $moved = Wait-Until { (Test-Path (Join-Path $org 'Docs\report.pdf')) -and (Test-Path (Join-Path $org 'Pictures\photo.jpg')) -and (Test-Path (Join-Path $org 'Music\song.mp3')) } 20
        $results.Add((New-Check 'Organize: files moved (rule folder + type folders)' $moved))
        if ($moved) {
            # The run is listed under Recent runs with its own Keep changes + Undo (found by label: the banner has a
            # "Keep changes" too, so a row adds a second one).
            $keepCond = New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition($UA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)),
                (New-Object System.Windows.Automation.PropertyCondition($UA::NameProperty, 'Keep changes')))
            $keeps = @($script:win.FindAll($TS::Descendants, $keepCond) | Where-Object { -not $_.Current.BoundingRectangle.IsEmpty }).Count
            $results.Add((New-Check 'Organize: Recent runs offers Keep changes and Undo for the run' ($keeps -ge 2) "visible Keep buttons: $keeps"))
            Start-Sleep 1; Click 'UndoLastButton'
            $answered = Answer-Yes 'Undo organize'
            $back = Wait-Until { (Test-Path (Join-Path $org 'report.pdf')) -and (Test-Path (Join-Path $org 'photo.jpg')) -and (Test-Path (Join-Path $org 'song.mp3')) -and -not (Test-Path (Join-Path $org 'Pictures')) } 20
            $results.Add((New-Check 'Organize: Undo puts every file back' ($answered -and $back)))

            # Duplicates: find the identical pair, move the extra copy, then "Keep changes" makes it final.
            Start-Sleep 2; Click 'FindDupesButton'
            $dupFound = Wait-Until { Visible 'MoveDupesButton' } 30
            $results.Add((New-Check 'Organize: finds the duplicate pair' $dupFound))
            if ($dupFound) {
                Click 'MoveDupesButton'; Start-Sleep 1; Click 'ConfirmButton'
                $oneLeft = Wait-Until { @((Test-Path (Join-Path $org 'dupA.txt')), (Test-Path (Join-Path $org 'dupB.txt')) | Where-Object { $_ }).Count -eq 1 } 20
                $results.Add((New-Check 'Organize: moves the extra copy, keeps one' $oneLeft))
                if ($oneLeft) {
                    Start-Sleep 1; Click 'KeepButton'
                    $kept = Wait-Until { -not (Visible 'KeepButton') -and -not (Visible 'UndoLastButton') } 10
                    $results.Add((New-Check 'Organize: Keep changes makes the run final' $kept))
                }
            }
        }
    }

    # 7b. Remove the rule again (its Remove button in the rules list).
    if ($scanned) {
        # Found by its label: on .NET Framework (Legacy) a plain ItemsControl like RulesList isn't exposed to UIA.
        $btnCond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($UA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)),
            (New-Object System.Windows.Automation.PropertyCondition($UA::NameProperty, 'Remove')))
        $rm = @($script:win.FindAll($TS::Descendants, $btnCond))[0]
        if ($rm) { $rm.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1 }
        $results.Add((New-Check 'Organize: a rule can be removed' ([bool]$rm -and (TextLike '.pdf*Docs').Count -eq 0)))
    }

    # 7c. What's using space: point it at the test folder (through the real folder picker) and measure it.
    Pick 'TabFilesSpace'
    Click 'SpaceChooseButton'
    if (Wait-Until { (Dialogs) -gt 0 } 10) {
        Start-Sleep 1
        $ws = New-Object -ComObject WScript.Shell
        $ws.SendKeys($org); Start-Sleep -Milliseconds 400; $ws.SendKeys('{ENTER}'); Start-Sleep 1
        if ((Dialogs) -gt 0) { $ws.SendKeys('{ENTER}'); Start-Sleep 1 }   # first Enter may only open the folder
        $targeted = Wait-Until { (TextOf 'SpaceTargetText') -like "*FeatureTest*Organize*" } 10
        if ((Dialogs) -gt 0) { $ws.SendKeys('{ESC}'); Start-Sleep 1 }
        if ($targeted) {
            Click 'SpaceScanButton'
            # The summary line reads e.g. "84 B in 5 file(s)": all 5 test files (one now in the duplicates folder).
            $measured = Wait-Until { (TextOf 'SpaceNodeText') -like '*5 file*' } 30
            $results.Add((New-Check "What's using space: measures a chosen folder" $measured ("'" + (TextOf 'SpaceNodeText') + "'")))
        } else { $results.Add((New-Check "What's using space: measures a chosen folder" $false ("target = '" + (TextOf 'SpaceTargetText') + "'"))) }
    } else { $results.Add((New-Check "What's using space: measures a chosen folder" $false 'folder picker did not open')) }

    # 8. Backup & Restore > Portable apps: scan the seeded folder, back up the found app, check copy + receipt.
    Pick 'NavBackup'; Pick 'TabBackupPortable'
    Click 'PortableScanButton'
    $found = Wait-Until { (TextOf 'PortableExeText') -eq '1' } 60
    $results.Add((New-Check 'Portable: scan finds the test app' $found ("found '" + (TextOf 'PortableExeText') + "'")))
    if ($found) {
        Click 'PortableSelectAllButton'; Start-Sleep 1
        Click 'BackupPortableButton'
        $answered = Answer-Yes 'Back up portable apps'
        $copied = Wait-Until { Test-Path (Join-Path $outDir 'Portable Apps\TestTool\TestTool.exe') } 30
        $receipt = Wait-Until { @(Get-ChildItem -LiteralPath (Join-Path $outDir 'Portable Apps') -Filter 'Backup receipt*.txt' -Recurse -ErrorAction SilentlyContinue).Count -gt 0 } 15
        $results.Add((New-Check 'Portable: backup copies the app + writes a receipt' ($answered -and $copied -and $receipt) "copied=$copied receipt=$receipt"))
    }

    # 8b. Documents: scan the seeded folder, back up both files (sorted into type folders), check the copies.
    Pick 'TabBackupDocuments'
    Click 'DocScanButton'
    $dfound = Wait-Until { (TextOf 'DocItemsText') -eq '2' } 60
    $results.Add((New-Check 'Documents: scan finds the 2 test files' $dfound ("found '" + (TextOf 'DocItemsText') + "'")))
    if ($dfound) {
        Click 'DocSelectAllButton'; Start-Sleep 1
        Click 'BackupDocsButton'
        $answered = Answer-Yes 'Back up loose documents'
        $dest = Join-Path $outDir 'Portable Documents'
        $dcopied = Wait-Until { @(Get-ChildItem -LiteralPath $dest -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -in 'letter.pdf', 'holiday.jpg' }).Count -eq 2 } 30
        $results.Add((New-Check 'Documents: backup copies both files' ($answered -and $dcopied)))
    }

    # 8c. Installed apps (editions with winget): reading the app list works (read-only, nothing is saved).
    if (El 'TabBackupApps') {
        # 1.4.2: the list is read automatically when the tab opens (winget present); "Read apps" re-reads it.
        Pick 'TabBackupApps'
        $countOk = { $n = 0; (Visible 'BackupCountFound') -and [int]::TryParse(((TextOf 'BackupCountFound') -replace '[^\d]', ''), [ref]$n) -and $n -gt 0 }
        $auto = Wait-Until $countOk 180
        $results.Add((New-Check 'Installed apps: list loads when the tab opens' $auto ("found '" + (TextOf 'BackupCountFound') + "'")))
        Click 'CreateBackupButton'; Start-Sleep 2
        $apps = Wait-Until { (El 'CreateBackupButton').Current.IsEnabled -and (& $countOk) } 180
        $results.Add((New-Check 'Installed apps: winget list is read' $apps ("found '" + (TextOf 'BackupCountFound') + "'")))
        if ($apps) {
            # Save writes backup.json + the guarded restore script (to Downloads; cleaned up at the end).
            $n0 = (New-InDownloads).Count
            Click 'SaveBackupButton'
            $results.Add((New-Check 'Installed apps: saves the backup + restore script' (Wait-Until { (New-InDownloads).Count -gt $n0 -or (Visible 'BackupSavedBar') } 30)))
        }
    } else { $results.Add([pscustomobject]@{ Area = 'Feature'; Check = 'Installed apps: winget list is read'; Status = 'SKIP'; Detail = 'no winget in this edition' }) }

    # 8d. Restore tab opens and looks for backups without errors.
    if (El 'TabBackupRestore') {
        Pick 'TabBackupRestore'; Start-Sleep 3
        $results.Add((New-Check 'Restore: tab opens and searches for backups' ((Dialogs) -eq 0)))
    }

    # 8e. Prepare for Reset wizard (editions with winget): opens from the page header and closes without doing anything.
    if (El 'HeaderBackupAll') {
        Click 'HeaderBackupAll'
        $wz = Wait-Until { (Dialogs) -gt 0 } 15
        if ($wz) { Start-Sleep 1; Send-To 'Prepare for Reset' '%{F4}' }
        $results.Add((New-Check 'Prepare for Reset: wizard opens + closes' ($wz -and (Wait-Until { (Dialogs) -eq 0 } 10))))
    }


    # 9. Settings: left-hand mode on and off (layout swap) without errors.
    Pick 'NavSettings'
    $t = El 'NavRightToggle'
    if ($t) {
        $tp = $t.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $tp.Toggle(); Start-Sleep 1; $tp.Toggle(); Start-Sleep 1
    }
    $results.Add((New-Check 'Settings: left-hand mode toggles' ([bool]$t -and (Dialogs) -eq 0)))

    # 10. No error dialog left open, app still alive, closes gracefully.
    $script:proc.Refresh()
    $results.Add((New-Check 'No error dialogs during the feature run' ((Dialogs) -eq 0 -and -not $script:proc.HasExited)))
}
catch { if ($_.ToString() -ne 'stop') { $results.Add((New-Check 'Feature run' $false $_.ToString())) } }
finally {
    if ($script:proc -and -not $script:proc.HasExited) {
        [void]$script:proc.CloseMainWindow(); [void]$script:proc.WaitForExit(15000)
        if (-not $script:proc.HasExited) { $script:proc.Kill() }
    }
    # Restore the edition's own settings + Organizer history exactly as they were.
    Start-Sleep -Milliseconds 800
    if ($hadSettings) { Copy-Item -LiteralPath (Join-Path $bak 'settings.json') -Destination (Join-Path $cfgDir 'settings.json') -Force }
    else { Remove-Item -LiteralPath (Join-Path $cfgDir 'settings.json') -Force -ErrorAction SilentlyContinue }
    if (Test-Path (Join-Path $cfgDir 'Organizer')) { Remove-Item -LiteralPath (Join-Path $cfgDir 'Organizer') -Recurse -Force -ErrorAction SilentlyContinue }
    if ($hadOrg) { Copy-Item -LiteralPath (Join-Path $bak 'Organizer') -Destination $cfgDir -Recurse -Force }
    Remove-Item -LiteralPath $bak -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    # Scripts / backups this run wrote into Downloads (only new venquery* entries; anything else is untouched).
    foreach ($e in (New-InDownloads)) { Remove-Item -LiteralPath $e.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    # The Export test's report (only if this run created the SystemReports folder).
    if (-not $hadReports) { Remove-Item -LiteralPath (Join-Path $exeDir 'SystemReports') -Recurse -Force -ErrorAction SilentlyContinue }
}
$results
