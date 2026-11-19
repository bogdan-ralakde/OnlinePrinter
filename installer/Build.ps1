<#
.SYNOPSIS
    Builds the Online Printer Windows client package and the Linux server bundle.

.DESCRIPTION
    Produces:

      dist\LabelBridge\                          <- temporary publish folder
          OnlinePrinter.Client.exe              (single file, self-contained, win-x64)
      dist\OnlinePrinterClient-Setup.exe         <- the EXE users run to install the client
      dist\OnlinePrinterClient-<version>-win-x64.zip
      dist\relay-linux-x64\LabelBridge.Relay     (Linux server binary; name kept for upgrades)
      dist\labelbridge-relay-linux-x64.tar.gz    (Linux server release asset; name kept for upgrades)

    The Windows release contains one setup EXE. There are no PowerShell, CMD or
    batch install scripts for end users.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\Build.ps1 -Version 1.0.0

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\Build.ps1 -SkipLinuxRelay -SkipZip
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $Version = '1.0.0',
    [switch] $SkipLinuxRelay,
    [switch] $SkipZip,
    [switch] $FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$app  = Join-Path $dist 'LabelBridge'
$setupOut = Join-Path $dist 'setup-build'
$zip  = Join-Path $dist "OnlinePrinterClient-$Version-$Runtime.zip"
$setupExeName = 'OnlinePrinterClient-Setup.exe'
$setupExeDist = Join-Path $dist $setupExeName

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'The .NET SDK (8.0 or newer) is not installed.' }

$sdk = (& dotnet --version).Trim()
Write-Host "Online Printer build" -ForegroundColor White
Write-Host "  root    : $root"
Write-Host "  SDK     : $sdk"
Write-Host "  version : $Version"

# ---------------------------------------------------------------- clean
Step 'Cleaning previous output'
foreach ($dir in @($app, $setupOut)) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
New-Item -ItemType Directory -Path $app -Force | Out-Null
New-Item -ItemType Directory -Path $dist -Force | Out-Null
foreach ($file in @($zip, $setupExeDist)) { if (Test-Path $file) { Remove-Item $file -Force } }

# ---------------------------------------------------------------- client app
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

Step "Publishing the Windows client ($Runtime, self-contained=$selfContained)"
$publishArgs = @(
    'publish', (Join-Path $root 'src\LabelBridge.Windows\LabelBridge.Windows.csproj'),
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', $selfContained,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=none',
    "-p:Version=$Version",
    "-p:FileVersion=$Version.0",
    '-o', $app,
    '--nologo',
    '-v', 'minimal'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed.' }

Get-ChildItem $app -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue
$clientExe = Join-Path $app 'OnlinePrinter.Client.exe'
if (-not (Test-Path $clientExe)) { Fail "Expected client executable not found: $clientExe" }

# ---------------------------------------------------------------- single EXE setup
Step 'Building the single-EXE installer'
& dotnet publish (Join-Path $root 'installer\OnlinePrinter.Setup\OnlinePrinter.Setup.csproj') `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    "-p:ClientExePath=$clientExe" `
    "-p:Version=$Version" "-p:FileVersion=$Version.0" `
    -o $setupOut --nologo -v minimal
if ($LASTEXITCODE -ne 0) { Fail 'Setup publish failed.' }

$setupBuilt = Join-Path $setupOut $setupExeName
if (-not (Test-Path $setupBuilt)) { Fail "Expected setup executable not found: $setupBuilt" }
Copy-Item $setupBuilt $setupExeDist -Force
Copy-Item $setupBuilt (Join-Path $app $setupExeName) -Force
Get-ChildItem $setupOut -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue

if (Test-Path (Join-Path $root 'README.md')) {
    Copy-Item (Join-Path $root 'README.md') (Join-Path $app 'README.md') -Force
}

@"
Online Printer Client $Version
Built $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Runtime $Runtime (self-contained: $selfContained)
"@ | Set-Content (Join-Path $app 'VERSION.txt') -Encoding UTF8

# ---------------------------------------------------------------- linux server
if (-not $SkipLinuxRelay) {
    Step 'Publishing the Linux server for linux-x64'
    $relayOut = Join-Path $dist 'relay-linux-x64'
    $tarPath  = Join-Path $dist 'labelbridge-relay-linux-x64.tar.gz'
    if (Test-Path $relayOut) { Remove-Item $relayOut -Recurse -Force }
    if (Test-Path $tarPath) { Remove-Item $tarPath -Force -ErrorAction SilentlyContinue }

    & dotnet publish (Join-Path $root 'src\LabelBridge.Relay\LabelBridge.Relay.csproj') `
        -c $Configuration -r linux-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
        "-p:Version=$Version" -o $relayOut --nologo -v minimal

    if ($LASTEXITCODE -ne 0) {
        Write-Host 'WARNING: the linux-x64 server could not be published.' -ForegroundColor Yellow
    } else {
        $installScript = Join-Path $root 'scripts\install-linux.sh'
        $installDoc = Join-Path $root 'docs\linux-install.md'
        if (Test-Path $installScript) { Copy-Item $installScript (Join-Path $relayOut 'install-linux.sh') -Force }
        if (Test-Path $installDoc) { Copy-Item $installDoc (Join-Path $relayOut 'linux-install.md') -Force }

        try {
            Push-Location $dist
            $tarExe = Get-Command tar.exe -ErrorAction SilentlyContinue
            if (-not $tarExe) { $tarExe = Get-Command tar -ErrorAction SilentlyContinue }
            if ($tarExe) {
                & $tarExe.Source -czf 'labelbridge-relay-linux-x64.tar.gz' -C 'relay-linux-x64' '.'
                if ($LASTEXITCODE -eq 0) { Write-Host "  tarball : $tarPath" -ForegroundColor DarkGray }
            } else {
                Write-Host 'WARNING: tar not found; skipping labelbridge-relay-linux-x64.tar.gz (required for Linux release installs).' -ForegroundColor Yellow
            }
        } finally { Pop-Location }
    }
}

# ---------------------------------------------------------------- zip
if (-not $SkipZip) {
    Step 'Creating the Windows package'
    Compress-Archive -Path $setupExeDist -DestinationPath $zip -CompressionLevel Optimal -Force
}

# ---------------------------------------------------------------- summary
Write-Host ''
Write-Host 'Build complete.' -ForegroundColor Green
if (Test-Path $setupExeDist) {
    $size = [math]::Round((Get-Item $setupExeDist).Length / 1MB, 1)
    Write-Host ("  setup  : {0} ({1} MB)" -f $setupExeDist, $size)
}
if (-not $SkipZip -and (Test-Path $zip)) { Write-Host ("  zip    : {0}" -f $zip) }
if (-not $SkipLinuxRelay -and (Test-Path (Join-Path $dist 'labelbridge-relay-linux-x64.tar.gz'))) {
    Write-Host ("  server : {0}" -f (Join-Path $dist 'labelbridge-relay-linux-x64.tar.gz'))
}
Write-Host ''
Write-Host 'Next steps:'
Write-Host "  1. Deploy/update server: publish labelbridge-relay-linux-x64.tar.gz, then run scripts/install-linux.sh on the server"
Write-Host "  2. Open the server web UI, complete first-run admin setup/login, configure printer and driver/profile"
Write-Host "  3. On Windows, run OnlinePrinterClient-Setup.exe (single EXE installer), then set the server URL and request approval"
Write-Host "  4. Approve the client in the web UI; install/repair the local 'Online Printer' printer in the client"
Write-Host ''
