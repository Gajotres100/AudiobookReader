<#
.SYNOPSIS
    Sets epub3maker up on a Windows server to make EPUB 3s every night, as a scheduled task.

.DESCRIPTION
    Run from the folder the Windows release was unzipped into (epub3maker.exe next to this script
    or one folder up), in an elevated PowerShell:

        .\install-windows.ps1 -Library "D:\Audiobooks" -Output "D:\ReadAlong"

    It copies the program to -InstallDir, writes the settings file, and registers a task named
    "epub3maker" that runs as SYSTEM every night when the window opens, and at startup — a server
    rebooted in the middle of the night carries on with the book it was making.

    Change settings later in <Data>\epub3maker.conf; no need to run this again.
    Remove everything with: .\install-windows.ps1 -Uninstall
#>
param(
    [string] $Library,
    [string] $Output,
    [ValidateSet("light", "better", "best")]
    [string] $Quality = "better",
    [string] $Window = "01:00-07:00",
    [string] $InstallDir = "C:\Epub3Maker",
    [string] $Data = "C:\Epub3Maker\data",
    [string] $Ffmpeg,
    [switch] $Uninstall
)

$ErrorActionPreference = "Stop"
$taskName = "epub3maker"

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this in an elevated PowerShell (Run as administrator): it registers a scheduled task."
}

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Task removed. The program ($InstallDir) and its data ($Data) were left in place; delete them by hand if you want."
    return
}

if (-not $Library -or -not $Output) { throw "Give -Library (where the audiobooks are) and -Output (where the EPUB 3s go)." }
if (-not (Test-Path $Library)) { throw "No such folder: $Library" }

# ---- The program ----

$source = @($PSScriptRoot, (Split-Path $PSScriptRoot -Parent)) |
    Where-Object { Test-Path (Join-Path $_ "epub3maker.exe") } | Select-Object -First 1
if (-not $source) { throw "epub3maker.exe not found next to this script or one folder up." }

New-Item -ItemType Directory -Force $InstallDir, $Data, $Output | Out-Null

if ((Resolve-Path $source).Path.TrimEnd('\') -ne (Resolve-Path $InstallDir).Path.TrimEnd('\')) {
    Copy-Item (Join-Path $source "*") $InstallDir -Recurse -Force -Exclude "data"
}

# ---- FFmpeg ----
# The task runs as SYSTEM, whose PATH does not include a per-user install, so the full path is
# written into the settings.

if (-not $Ffmpeg) {
    $found = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if (-not $found) {
        throw "FFmpeg not found. Install it (winget install Gyan.FFmpeg) and open a new PowerShell, or give -Ffmpeg <path to ffmpeg.exe>."
    }
    $Ffmpeg = $found.Source
}

# ---- Settings ----

$config = Join-Path $Data "epub3maker.conf"

if (Test-Path $config) {
    Write-Host "Keeping the existing $config"
} else {
    @"
# epub3maker settings - one 'name = value' per line. Every option of 'epub3maker scan --help' works here.

library = $Library
output = $Output

# light | better | best   (best listens to every letter: a night or more per book on a small server)
quality = $Quality
# anchors = medium        # light/better only: sparse | medium | dense | seconds between probes

# Only between these times; the book in progress carries on the next night.
window = $Window

ffmpeg = $Ffmpeg
# threads = 2
"@ | Set-Content -Path $config -Encoding UTF8
    Write-Host "Settings written to $config"
}

# ---- The task ----

$from, $to = $Window.Split("-")
$length = ([TimeSpan]$to - [TimeSpan]$from)
if ($length -le [TimeSpan]::Zero) { $length += [TimeSpan]::FromDays(1) }

$action = New-ScheduledTaskAction -Execute (Join-Path $InstallDir "epub3maker.exe") `
    -Argument "scan --data `"$Data`"" -WorkingDirectory $InstallDir
$triggers = @(
    (New-ScheduledTaskTrigger -Daily -At $from),
    (New-ScheduledTaskTrigger -AtStartup)
)
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ($length + [TimeSpan]::FromMinutes(30)) `
    -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggers -Settings $settings `
    -Principal $principal -Description "Makes EPUB 3 read-along books from $Library into $Output" -Force | Out-Null

Write-Host ""
Write-Host "Done. The task '$taskName' runs every day at $from and stops at $to."
Write-Host ""
Write-Host "  See what it found:   & `"$InstallDir\epub3maker.exe`" scan --data `"$Data`" --dry-run"
Write-Host "  Start it now:        Start-ScheduledTask $taskName    (it only works inside the window)"
Write-Host "  Progress:            Get-Content `"$Data\log.txt`" -Tail 30 -Wait"
Write-Host "  Summary:             Get-Content `"$Data\report.txt`""
