<#
.SYNOPSIS
    Builds a self-contained LabelBridge Windows package plus an optional Linux relay binary.

.DESCRIPTION
    Produces:

      dist\LabelBridge\                 <- everything the installer needs
          LabelBridge.exe               (single file, self-contained, win-x64)
          Install.ps1, Uninstall.ps1, Setup.cmd
          README.txt
      dist\LabelBridge-<version>-win-x64.zip
      dist\relay-linux-x64\LabelBridge.Relay   (optional, for LXC installs)

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\Build.ps1

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

$root = Split-Path -Parent $PSScriptRoot          # LabelBridge\
$dist = Join-Path $root 'dist'
$app  = Join-Path $dist 'LabelBridge'
$zip  = Join-Path $dist "LabelBridge-$Version-$Runtime.zip"

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'The .NET SDK (8.0 or newer) is not installed.' }

$sdk = (& dotnet --version).Trim()
Write-Host "LabelBridge build" -ForegroundColor White
Write-Host "  root    : $root"
Write-Host "  SDK     : $sdk"
Write-Host "  version : $Version"

# ---------------------------------------------------------------- clean
Step 'Cleaning previous output'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory -Path $app -Force | Out-Null
if (-not $SkipZip -and (Test-Path $zip)) { Remove-Item $zip -Force }

# ---------------------------------------------------------------- build
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

Step "Publishing the Windows app ($Runtime, self-contained=$selfContained)"
$publishArgs = @(
    'publish', (Join-Path $root 'src\LabelBridge.Windows\LabelBridge.Windows.csproj'),
    '-c', $Configuration,
    '-r', $Runtime,
    "--self-contained", $selfContained,
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

# Drop the pdb if it slipped through; keep the folder tidy.
Get-ChildItem $app -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- installer files
Step 'Copying installer scripts'
foreach ($file in 'Install.ps1', 'Uninstall.ps1', 'Setup.cmd', 'Uninstall.cmd') {
    $source = Join-Path $PSScriptRoot $file
    if (-not (Test-Path $source)) { Fail "Missing installer file: $file" }
    Copy-Item $source (Join-Path $app $file) -Force
}

if (Test-Path (Join-Path $root 'README.md')) {
    Copy-Item (Join-Path $root 'README.md') (Join-Path $app 'README.md') -Force
}

# A version marker the app/installer can show without reading assembly metadata.
@"
LabelBridge $Version
Built $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Runtime $Runtime (self-contained: $selfContained)
"@ | Set-Content (Join-Path $app 'VERSION.txt') -Encoding UTF8

# ---------------------------------------------------------------- linux relay
if (-not $SkipLinuxRelay) {
    Step 'Publishing the relay for linux-x64 (for LXC / non-Docker installs)'
    $relayOut = Join-Path $dist 'relay-linux-x64'
    if (Test-Path $relayOut) { Remove-Item $relayOut -Recurse -Force }

    & dotnet publish (Join-Path $root 'src\LabelBridge.Relay\LabelBridge.Relay.csproj') `
        -c $Configuration -r linux-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
        "-p:Version=$Version" -o $relayOut --nologo -v minimal

    if ($LASTEXITCODE -ne 0) {
        Write-Host 'WARNING: the linux-x64 relay could not be published; Dockerfiles still work.' -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------- zip
if (-not $SkipZip) {
    Step 'Creating the ZIP package'
    Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -CompressionLevel Optimal -Force
}

# ---------------------------------------------------------------- summary
$exe = Join-Path $app 'LabelBridge.exe'
Write-Host ''
Write-Host 'Build complete.' -ForegroundColor Green
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ("  app    : {0} ({1} MB)" -f $exe, $size)
}
if (-not $SkipZip) { Write-Host ("  zip    : {0}" -f $zip) }
Write-Host ''
Write-Host 'Next steps:'
Write-Host "  1. copy the folder (or ZIP) to the target PC"
Write-Host "  2. run Setup.cmd there and approve the administrator prompt"
Write-Host "  3. on the printer PC choose 'Host', on remote PCs choose 'Client'"
Write-Host ''
