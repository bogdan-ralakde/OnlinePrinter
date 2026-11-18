<#
.SYNOPSIS
    Removes LabelBridge from this Windows PC. Must run elevated.

.DESCRIPTION
    Removes the virtual printer, firewall rule, shortcuts, "Apps & features"
    entry and the program files. Queued/history data is kept unless -RemoveData
    is given, so a reinstall keeps working.

.EXAMPLE
    .\Uninstall.ps1

.EXAMPLE
    .\Uninstall.ps1 -RemoveData -Quiet
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'LabelBridge'),
    [switch] $RemoveData,
    [switch] $Quiet
)

$ErrorActionPreference = 'Continue'   # uninstall should keep going on individual failures

function Step($text) { if (-not $Quiet) { Write-Host "`n==> $text" -ForegroundColor Cyan } }
function Warn($text) { Write-Host "WARNING: $text" -ForegroundColor Yellow }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'ERROR: the uninstaller must run as administrator.' -ForegroundColor Red
    if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}

Write-Host ''
Write-Host '  LabelBridge uninstaller' -ForegroundColor White
Write-Host '  -----------------------'

# ------------------------------------------------------------------ stop
Step 'Stopping LabelBridge'
Get-Process -Name 'LabelBridge' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.CloseMainWindow() | Out-Null } catch { }
}
Start-Sleep -Milliseconds 900
Get-Process -Name 'LabelBridge' -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { }
}
Start-Sleep -Milliseconds 400

# ------------------------------------------------------------------ printer
$targetExe = Join-Path $InstallDir 'LabelBridge.exe'
if (Test-Path $targetExe) {
    Step 'Removing the virtual printer'
    $log = Join-Path $env:TEMP ("lb-printer-remove-" + [Guid]::NewGuid().ToString('N') + '.log')
    try {
        $proc = Start-Process -FilePath $targetExe -ArgumentList @('--printer-remove') -Wait -PassThru -NoNewWindow `
                    -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        if (Test-Path $log) {
            $text = (Get-Content $log -Raw)
            if ($text) { Write-Host ($text.Trim() -replace '(?m)^', '    ') -ForegroundColor DarkGray }
        }
        if ($proc.ExitCode -ne 0) { Warn "Printer removal reported exit code $($proc.ExitCode) - check 'Printers & scanners'." }
    } catch {
        Warn "Could not run the printer removal helper: $($_.Exception.Message)"
    } finally {
        Remove-Item $log, "$log.err" -Force -ErrorAction SilentlyContinue
    }
}

# ------------------------------------------------------------------ firewall
Step 'Removing the firewall rule'
try {
    $rule = Get-NetFirewallRule -DisplayName 'LabelBridge direct TLS' -ErrorAction SilentlyContinue
    if ($rule) { $rule | Remove-NetFirewallRule -ErrorAction SilentlyContinue; Write-Host '  removed' -ForegroundColor DarkGray }
} catch { Warn "Could not remove the firewall rule: $($_.Exception.Message)" }

# ------------------------------------------------------------------ shortcuts
Step 'Removing shortcuts'
$links = @(
    (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'LabelBridge.lnk'),
    (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'LabelBridge.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'LabelBridge.lnk'),
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'LabelBridge.lnk')
)
foreach ($link in $links) {
    if (Test-Path $link) {
        try { Remove-Item $link -Force; Write-Host "  removed $link" -ForegroundColor DarkGray } catch { Warn "Could not delete $link" }
    }
}

# ------------------------------------------------------------------ registry
Step 'Removing the uninstall entry'
try {
    Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LabelBridge' -Recurse -Force -ErrorAction Stop
    Write-Host '  removed' -ForegroundColor DarkGray
} catch { Warn "Could not remove the uninstall registry entry: $($_.Exception.Message)" }

# Autostart value (normally managed by the app, but be thorough).
try {
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LabelBridge' -ErrorAction SilentlyContinue
} catch { }

# ------------------------------------------------------------------ files
Step 'Removing program files'
if (Test-Path $InstallDir) {
    $removed = $false
    for ($i = 0; $i -lt 5 -and -not $removed; $i++) {
        try {
            Remove-Item $InstallDir -Recurse -Force -ErrorAction Stop
            $removed = $true
        } catch {
            Start-Sleep -Milliseconds 600
        }
    }

    if ($removed) {
        Write-Host "  removed $InstallDir" -ForegroundColor DarkGray
    } else {
        $leftover = Join-Path $env:TEMP 'LabelBridge-leftover'
        Warn "Some files are still in use. Restart Windows and delete `"$InstallDir`" manually."
        Write-Host "  (You can also schedule a delete with: cmd /c rmdir /s /q `"$InstallDir`")" -ForegroundColor DarkGray
        $null = $leftover
    }
}

# ------------------------------------------------------------------ data
$dataDir = Join-Path $env:ProgramData 'LabelBridge'
if ($RemoveData) {
    Step 'Removing queued jobs, history and settings'
    try {
        Remove-Item $dataDir -Recurse -Force -ErrorAction Stop
        Write-Host "  removed $dataDir" -ForegroundColor DarkGray
    } catch {
        Warn "Could not remove $dataDir - $($_.Exception.Message)"
    }
} else {
    Write-Host ''
    Write-Host "  Data kept in $dataDir" -ForegroundColor DarkGray
    Write-Host '  (settings, pairing, queued jobs and logs - delete the folder to start fresh)' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'LabelBridge has been uninstalled.' -ForegroundColor Green
if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }
