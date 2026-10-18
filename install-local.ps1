<#
.SYNOPSIS
    Permanently installs NovaGet to %LOCALAPPDATA%\Programs\NovaGet,
    creates Desktop and Start Menu shortcuts, registers Windows Startup auto-run,
    and launches the installed application.
#>

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$PublishDir = Join-Path $Root "dist\publish-win-x64"
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\NovaGet"
$ExtInstallDir = Join-Path $InstallDir "Extension"

# 1. Stop any running NovaGet / MyIdm.Desktop instances before copying
Get-Process -Name "NovaGet", "MyIdm.Desktop" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# 2. Build publish output if not already present
if (-not (Test-Path (Join-Path $PublishDir "NovaGet.exe"))) {
    Write-Host "Publishing standalone NovaGet.exe..." -ForegroundColor Cyan
    dotnet publish "$Root\desktop\MyIdm.Desktop.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $PublishDir
}

# 3. Copy files to %LOCALAPPDATA%\Programs\NovaGet
Write-Host "Installing NovaGet to: $InstallDir" -ForegroundColor Cyan
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
Copy-Item -Path "$PublishDir\*" -Destination $InstallDir -Recurse -Force

if (-not (Test-Path $ExtInstallDir)) {
    New-Item -ItemType Directory -Path $ExtInstallDir -Force | Out-Null
}
Copy-Item -Path "$Root\extension\*" -Destination $ExtInstallDir -Recurse -Force

$ExePath = Join-Path $InstallDir "NovaGet.exe"
$IconPath = Join-Path $InstallDir "Assets\app.ico"
if (-not (Test-Path $IconPath)) {
    $IconPath = $ExePath
}

# 4. Create Desktop and Start Menu Shortcuts
$WshShell = New-Object -ComObject WScript.Shell

$DesktopShortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "NovaGet.lnk"
$DesktopShortcut = $WshShell.CreateShortcut($DesktopShortcutPath)
$DesktopShortcut.TargetPath = $ExePath
$DesktopShortcut.WorkingDirectory = $InstallDir
$DesktopShortcut.IconLocation = "$IconPath,0"
$DesktopShortcut.Description = "NovaGet Download Manager"
$DesktopShortcut.Save()
Write-Host " -> Desktop shortcut created: $DesktopShortcutPath" -ForegroundColor Green

$StartMenuDir = [Environment]::GetFolderPath("Programs")
$StartMenuShortcutPath = Join-Path $StartMenuDir "NovaGet.lnk"
$StartMenuShortcut = $WshShell.CreateShortcut($StartMenuShortcutPath)
$StartMenuShortcut.TargetPath = $ExePath
$StartMenuShortcut.WorkingDirectory = $InstallDir
$StartMenuShortcut.IconLocation = "$IconPath,0"
$StartMenuShortcut.Description = "NovaGet Download Manager"
$StartMenuShortcut.Save()
Write-Host " -> Start Menu shortcut created: $StartMenuShortcutPath" -ForegroundColor Green

# 5. Register Windows Startup (HKCU\Software\Microsoft\Windows\CurrentVersion\Run)
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $RunKey -Name "NovaGet" -Value "`"$ExePath`""
Write-Host " -> Registered in Windows Startup (Run on boot)" -ForegroundColor Green

# 6. Launch installed NovaGet
Start-Process -FilePath $ExePath -WorkingDirectory $InstallDir
Write-Host "`nNovaGet has been permanently installed and launched!" -ForegroundColor Cyan
Write-Host "Installed App Path : $ExePath" -ForegroundColor White
Write-Host "Installed Extension: $ExtInstallDir" -ForegroundColor White
