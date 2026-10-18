<#
.SYNOPSIS
    Builds and packages NovaGet Desktop (Standalone Single-File .exe + Portable .zip + Optional Setup.exe)
    and packages the Chromium MV3 Browser Extension (.zip + .crx).
#>

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$DistDir = Join-Path $Root "dist"
$PublishDir = Join-Path $DistDir "publish-win-x64"
$ExtDir = Join-Path $Root "extension"

Write-Host "=== [1/4] Cleaning dist/ directory ===" -ForegroundColor Cyan
if (Test-Path $DistDir) {
    Remove-Item $DistDir -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishDir | Out-Null

Write-Host "=== [2/4] Publishing NovaGet.exe (Self-Contained Single-File win-x64) ===" -ForegroundColor Cyan
dotnet publish "$Root\desktop\MyIdm.Desktop.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $PublishDir

$PortableZip = Join-Path $DistDir "NovaGet-Portable-win-x64.zip"
Compress-Archive -Path "$PublishDir\*" -DestinationPath $PortableZip -Force
Write-Host " -> Portable package ready: $PortableZip" -ForegroundColor Green

Write-Host "=== [3/4] Packaging Browser Extension (.zip & .crx) ===" -ForegroundColor Cyan
$ExtZip = Join-Path $DistDir "NovaGet-Extension-v1.0.0.zip"
Compress-Archive -Path "$ExtDir\*" -DestinationPath $ExtZip -Force
Write-Host " -> Extension ZIP ready (Web Store / Unpacked): $ExtZip" -ForegroundColor Green

$ChromePaths = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
)
$BrowserExe = $ChromePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($BrowserExe) {
    $TempExtParent = Join-Path $DistDir "ext_pack_tmp"
    $TempExtCopy = Join-Path $TempExtParent "NovaGet-Extension"
    Copy-Item -Path $ExtDir -Destination $TempExtCopy -Recurse -Force
    & $BrowserExe "--pack-extension=$TempExtCopy" --no-message-box | Out-Null
    Start-Sleep -Seconds 2
    $CrxFile = Join-Path $TempExtParent "NovaGet-Extension.crx"
    if (Test-Path $CrxFile) {
        Move-Item $CrxFile (Join-Path $DistDir "NovaGet-Extension-v1.0.0.crx") -Force
        Write-Host " -> Signed Extension CRX ready: $(Join-Path $DistDir 'NovaGet-Extension-v1.0.0.crx')" -ForegroundColor Green
    }
    Remove-Item $TempExtParent -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "=== [4/4] Checking for Inno Setup Compiler (ISCC.exe) ===" -ForegroundColor Cyan
$IsccPaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$IsccExe = $IsccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($IsccExe) {
    & $IsccExe "$Root\installer\NovaGet.iss"
    Write-Host " -> Windows Installer ready: $(Join-Path $DistDir 'NovaGet-Setup-v1.0.0.exe')" -ForegroundColor Green
} else {
    Write-Host " -> Inno Setup 6 not found. Skipping Setup.exe generation (install via: winget install JRSoftware.InnoSetup)." -ForegroundColor Yellow
}

Write-Host "`nAll release artifacts generated in: $DistDir" -ForegroundColor Cyan
Get-ChildItem $DistDir -File | Select-Object Name, @{Name="SizeMB";Expression={[math]::Round($_.Length / 1MB, 2)}} | Format-Table -AutoSize
