<#
.SYNOPSIS
    Installs LabelBridge on this Windows PC. Must run elevated.

.DESCRIPTION
    - copies the app to %ProgramFiles%\LabelBridge
    - grants normal users write access to %ProgramData%\LabelBridge
    - creates Start Menu + Desktop shortcuts
    - registers an uninstall entry in "Apps & features"
    - optionally installs the virtual printer right away (no second UAC prompt)
    - optionally opens the Windows Firewall for direct TLS mode

.PARAMETER InstallPrinter
    Install/repair the virtual printer (needs the Zebra ZDesigner ZPL driver).

.PARAMETER Driver
    Printer driver to use for the virtual printer. Defaults to the best installed
    "ZDesigner ... ZPL" driver.

.PARAMETER CapturePort
    Loopback TCP port used by the virtual printer (default 9191).

.PARAMETER NoFirewall
    Skip the inbound firewall rule for direct TLS mode.

.PARAMETER NoLaunch
    Do not start LabelBridge when the installer finishes.

.EXAMPLE
    .\Install.ps1 -InstallPrinter
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'LabelBridge'),
    [switch] $InstallPrinter,
    [string] $Driver,
    [int]    $CapturePort = 0,
    [switch] $NoFirewall,
    [switch] $NoLaunch,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
$ProductName = 'LabelBridge'
$version = '1.0.0'
$versionFile = Join-Path $PSScriptRoot 'VERSION.txt'
if (Test-Path $versionFile) {
    $first = (Get-Content $versionFile -TotalCount 1) -replace '^LabelBridge\s+', ''
    if ($first) { $version = $first.Trim() }
}

function Say($text, $color = 'Gray') { if (-not $Quiet) { Write-Host $text -ForegroundColor $color } }
function Step($text) { if (-not $Quiet) { Write-Host "`n==> $text" -ForegroundColor Cyan } }
function Warn($text) { Write-Host "WARNING: $text" -ForegroundColor Yellow }
function Fail($text) { Write-Host "`nERROR: $text" -ForegroundColor Red; if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }; exit 1 }

# ------------------------------------------------------------------ checks
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'This installer must run as administrator. Right-click Setup.cmd and choose "Run as administrator".'
}

$sourceExe = Join-Path $PSScriptRoot 'LabelBridge.exe'
if (-not (Test-Path $sourceExe)) {
    Fail "LabelBridge.exe was not found next to this script. Extract the whole ZIP first, then run Setup.cmd."
}

$os = [Environment]::OSVersion.Version
if ($os.Major -lt 10) { Fail 'LabelBridge requires Windows 10 or Windows 11.' }

Say ""
Say "  LabelBridge $version installer" 'White'
Say "  ------------------------------"
Say "  Install folder : $InstallDir"
Say "  Data folder    : $env:ProgramData\LabelBridge"

# ------------------------------------------------------------------ stop the running app
Step 'Stopping any running LabelBridge instance'
$running = Get-Process -Name 'LabelBridge' -ErrorAction SilentlyContinue
if ($running) {
    $running | ForEach-Object {
        try { $_.CloseMainWindow() | Out-Null } catch { }
    }
    Start-Sleep -Milliseconds 800
    Get-Process -Name 'LabelBridge' -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { }
    }
    Start-Sleep -Milliseconds 400
    Say '  stopped' 'DarkGray'
} else {
    Say '  not running' 'DarkGray'
}

# ------------------------------------------------------------------ copy files
Step "Installing to $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# Refuse to install over an unrelated folder that happens to have the same name.
$existingMarker = Join-Path $InstallDir 'LabelBridge.exe'
if ((Test-Path $InstallDir) -and -not (Test-Path $existingMarker)) {
    $items = @(Get-ChildItem $InstallDir -Force -ErrorAction SilentlyContinue)
    if ($items.Count -gt 0) {
        Fail "The folder $InstallDir already exists and does not contain LabelBridge. Choose another -InstallDir."
    }
}

$skip = @('Setup.cmd')   # the launcher does not need to be copied
Get-ChildItem $PSScriptRoot -File | Where-Object { $skip -notcontains $_.Name -and $_.Name -ne 'installer.log' } | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $InstallDir $_.Name) -Force
}
Say "  copied $((Get-ChildItem $PSScriptRoot -File).Count) files" 'DarkGray'

$targetExe = Join-Path $InstallDir 'LabelBridge.exe'
if (-not (Test-Path $targetExe)) { Fail 'Copying the application failed.' }

# ------------------------------------------------------------------ data folder + ACL
Step 'Preparing the shared data folder'
$dataDir = Join-Path $env:ProgramData 'LabelBridge'
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null

# Every user who prints must be able to queue jobs -> grant Modify to BUILTIN\Users.
$icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
& $icacls $dataDir /grant '*S-1-5-32-545:(OI)(CI)M' /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Warn 'Could not set permissions on the data folder; printing may fail for non-admin users.' }
Say "  $dataDir is writable for all users" 'DarkGray'

# ------------------------------------------------------------------ firewall (direct mode)
if (-not $NoFirewall) {
    Step 'Windows Firewall rule for direct TLS mode (port 9443)'
    try {
        $existing = Get-NetFirewallRule -DisplayName 'LabelBridge direct TLS' -ErrorAction SilentlyContinue
        if (-not $existing) {
            New-NetFirewallRule -DisplayName 'LabelBridge direct TLS' -Direction Inbound -Action Allow `
                -Protocol TCP -LocalPort 9443 -Profile Domain,Private -Description 'LabelBridge host: direct LAN/VPN print jobs' | Out-Null
            Say '  added' 'DarkGray'
        } else {
            Say '  already present' 'DarkGray'
        }
    } catch {
        Warn 'Could not add the firewall rule. Direct mode may need manual approval on first use.'
    }
}

# ------------------------------------------------------------------ shortcuts
Step 'Creating shortcuts'
$wsh = New-Object -ComObject WScript.Shell

function New-Shortcut([string] $Path, [string] $Target, [string] $Arguments, [string] $Description) {
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $link = $wsh.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.Arguments = $Arguments
    $link.WorkingDirectory = Split-Path -Parent $Target
    $link.Description = $Description
    $link.IconLocation = "$Target,0"
    $link.Save()
}

$startMenu = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'LabelBridge.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'LabelBridge.lnk'

try { New-Shortcut $startMenu $targetExe '' 'LabelBridge - virtual label printer bridge'; Say "  $startMenu" 'DarkGray' }
catch { Warn "Could not create the Start Menu shortcut: $($_.Exception.Message)" }

try { New-Shortcut $desktop $targetExe '' 'LabelBridge - virtual label printer bridge'; Say "  $desktop" 'DarkGray' }
catch { Warn "Could not create the Desktop shortcut: $($_.Exception.Message)" }

# ------------------------------------------------------------------ uninstall entry
Step 'Registering the uninstaller'
$uninstallCmd = Join-Path $InstallDir 'Uninstall.cmd'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LabelBridge'
New-Item -Path $uninstallKey -Force | Out-Null
$estimated = 0
try {
    $estimated = [int](((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1KB)
} catch { }

$entry = @{
    DisplayName     = 'LabelBridge'
    DisplayVersion  = $version
    Publisher       = 'LabelBridge'
    InstallLocation = $InstallDir
    DisplayIcon     = $targetExe
    UninstallString = "cmd.exe /c `"`"$uninstallCmd`"`""
    InstallDate     = (Get-Date -Format 'yyyyMMdd')
    EstimatedSize   = $estimated
    NoModify        = 1
    NoRepair        = 1
    Comments        = 'Virtual label printer bridge - forwards Windows print jobs to a remote label printer.'
}
foreach ($name in $entry.Keys) {
    $value = $entry[$name]
    $type = 'String'
    if ($value -is [int]) { $type = 'DWord' }
    New-ItemProperty -Path $uninstallKey -Name $name -Value $value -PropertyType $type -Force | Out-Null
}
Say '  added to "Apps & features"' 'DarkGray'

# ------------------------------------------------------------------ virtual printer
$printerOk = $false
if ($InstallPrinter) {
    Step 'Installing the virtual printer'

    $printerArgs = @('--printer-install')
    if ($Driver) { $printerArgs += @('--driver', $Driver) }
    if ($CapturePort -gt 0) { $printerArgs += @('--port', "$CapturePort") }

    $log = Join-Path $env:TEMP ("lb-printer-" + [Guid]::NewGuid().ToString('N') + '.log')
    $err = "$log.err"
    try {
        $proc = Start-Process -FilePath $targetExe -ArgumentList $printerArgs -Wait -PassThru -NoNewWindow `
                    -RedirectStandardOutput $log -RedirectStandardError $err
        $text = ''
        if (Test-Path $log) { $text += Get-Content $log -Raw }
        if (Test-Path $err) { $text += Get-Content $err -Raw }

        if ($proc.ExitCode -eq 0) {
            $printerOk = $true
            Say '  virtual printer installed' 'Green'
            if ($text) { Say ($text.Trim() -replace '(?m)^', '    ') 'DarkGray' }
        } else {
            Warn "The virtual printer could not be installed (exit code $($proc.ExitCode))."
            if ($text) { Say ($text.Trim() -replace '(?m)^', '    ') 'Yellow' }
            Say '  You can install it later from the app: Printer tab -> Install / repair printer.' 'Yellow'
        }
    } catch {
        Warn "Virtual printer installation failed: $($_.Exception.Message)"
        Say '  You can install it later from the app: Printer tab -> Install / repair printer.' 'Yellow'
    } finally {
        Remove-Item $log, $err -Force -ErrorAction SilentlyContinue
    }
} else {
    Say ''
    Say '  The virtual printer was not installed (no -InstallPrinter switch).' 'Yellow'
    Say '  Install it from the app later: Printer tab -> Install / repair printer.' 'Yellow'
}

# ------------------------------------------------------------------ launch
if (-not $NoLaunch) {
    Step 'Starting LabelBridge'
    try {
        Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$targetExe`"" | Out-Null
        Say '  started' 'DarkGray'
    } catch {
        Warn 'Could not start the app automatically. Use the new shortcut on the Desktop.'
    }
}

# ------------------------------------------------------------------ summary
Write-Host ''
Write-Host 'LabelBridge client is installed.' -ForegroundColor Green
Write-Host ''
Write-Host '  App        : ' -NoNewline; Write-Host $targetExe -ForegroundColor White
Write-Host '  Settings   : ' -NoNewline; Write-Host (Join-Path $dataDir 'settings.json') -ForegroundColor White
Write-Host '  Virtual pr.: ' -NoNewline
if ($printerOk) { Write-Host 'installed' -ForegroundColor Green } else { Write-Host 'not installed yet (Printer tab)' -ForegroundColor Yellow }
Write-Host ''
Write-Host '  First run (Option A):'
Write-Host '    1. Open LabelBridge and set the LabelBridge server URL (web UI/API base URL)'
Write-Host '    2. Submit a connection request and wait for admin approval in the server web UI'
Write-Host '    3. Download/install the server-provided driver/profile if required, then install/repair the local "LabelBridge" virtual printer (admin)'
Write-Host '    4. Print from any app to "LabelBridge" - jobs upload to the server and print directly to the configured network printer'
Write-Host ''
if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }
