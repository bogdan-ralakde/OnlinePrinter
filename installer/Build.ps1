<#
.SYNOPSIS
    Builds LabelBridge Windows client package and Linux server (LXC) bundle.

.DESCRIPTION
    Produces:

      dist\LabelBridge\                     <- Windows client + installer
          LabelBridge.exe                   (single file, self-contained, win-x64)
          Install.ps1, Uninstall.ps1, Setup.cmd, Uninstall.cmd
          README.md, VERSION.txt
      dist\LabelBridge-<version>-win-x64.zip
      dist\relay-linux-x64\LabelBridge.Relay        (Linux server binary for LXC)
      dist\labelbridge-relay-linux-x64.tar.gz       (LXC install asset for GitHub releases)

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

# ---------------------------------------------------------------- linux server (LXC)
if (-not $SkipLinuxRelay) {
    Step 'Publishing the Linux server for linux-x64 (for LXC / non-Docker installs)'
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
        $lxcScript = Join-Path $root 'scripts\install-lxc.sh'
        $lxcDoc = Join-Path $root 'docs\lxc-install.md'
        if (Test-Path $lxcScript) { Copy-Item $lxcScript (Join-Path $relayOut 'install-lxc.sh') -Force }
        if (Test-Path $lxcDoc) { Copy-Item $lxcDoc (Join-Path $relayOut 'lxc-install.md') -Force }

        # Create GitHub release asset expected by scripts/install-lxc.sh
        try {
            Push-Location $dist
            $tarExe = Get-Command tar.exe -ErrorAction SilentlyContinue
            if (-not $tarExe) { $tarExe = Get-Command tar -ErrorAction SilentlyContinue }
            if ($tarExe) {
                & $tarExe.Source -czf 'labelbridge-relay-linux-x64.tar.gz' -C 'relay-linux-x64' '.'
                if ($LASTEXITCODE -eq 0) { Write-Host "  tarball : $tarPath" -ForegroundColor DarkGray }
            } else {
                Write-Host 'WARNING: tar not found; skipping labelbridge-relay-linux-x64.tar.gz (required for LXC auto-install releases).' -ForegroundColor Yellow
            }
        } finally { Pop-Location }
    }
}

# ---------------------------------------------------------------- zip
if (-not $SkipZip) {
    Step 'Creating the ZIP package'
    Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -CompressionLevel Optimal -Force
}

# ---------------------------------------------------------------- summary
$exe = Join-Path $app 'LabelBridge.exe'
$tar = Join-Path $dist 'labelbridge-relay-linux-x64.tar.gz'
Write-Host ''
Write-Host 'Build complete.' -ForegroundColor Green
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ("  client : {0} ({1} MB)" -f $exe, $size)
}
if (-not $SkipZip -and (Test-Path $zip)) { Write-Host ("  zip    : {0}" -f $zip) }
if (-not $SkipLinuxRelay -and (Test-Path $tar)) { Write-Host ("  server : {0}" -f $tar) }
Write-Host ''
Write-Host 'Next steps:'
Write-Host "  1. Deploy/update LXC server: publish labelbridge-relay-linux-x64.tar.gz release, then run scripts/install-lxc.sh on server"
Write-Host "  2. Open server web UI, login as admin, configure printer and upload/select driver/profile as needed"
Write-Host "  3. Install LabelBridge client on Windows (Setup.cmd), set server URL, request approval, install/repair virtual printer"
Write-Host "  4. Approve client in web UI; print from any app to the local 'LabelBridge' printer"
Write-Host ''


