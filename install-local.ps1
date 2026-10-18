<#
.SYNOPSIS
    Permanently installs NovaGet to %LOCALAPPDATA%\Programs\NovaGet,
    creates Desktop and Start Menu shortcuts, registers Windows Startup auto-run,
    registers Control Panel (Add/Remove Programs) uninstaller entry,
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

# 2. Build publish output
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

# 4. Create Uninstall script inside InstallDir
$UninstallScriptPath = Join-Path $InstallDir "uninstall.ps1"
$UninstallScriptContent = @'
$ErrorActionPreference = "SilentlyContinue"
Get-Process -Name "NovaGet", "MyIdm.Desktop" | Stop-Process -Force
Start-Sleep -Milliseconds 500

$DesktopLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "NovaGet.lnk"
$StartMenuLnk = Join-Path ([Environment]::GetFolderPath("Programs")) "NovaGet.lnk"
Remove-Item $DesktopLnk -Force
Remove-Item $StartMenuLnk -Force

Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "NovaGet"
Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NovaGet" -Recurse -Force

$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\NovaGet"
Start-Process cmd.exe -ArgumentList "/c timeout /t 1 /nobreak >nul & rmdir /s /q `"$InstallDir`"" -WindowStyle Hidden
'@
Set-Content -Path $UninstallScriptPath -Value $UninstallScriptContent -Encoding UTF8

# 5. Create Desktop and Start Menu Shortcuts
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

# 6. Register Windows Startup (HKCU\Software\Microsoft\Windows\CurrentVersion\Run)
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $RunKey -Name "NovaGet" -Value "`"$ExePath`""
Write-Host " -> Registered in Windows Startup (Run on boot)" -ForegroundColor Green

# 7. Register in Windows Control Panel / Installed Apps (HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\NovaGet)
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NovaGet"
if (-not (Test-Path $UninstallKey)) {
    New-Item -Path $UninstallKey -Force | Out-Null
}
$TotalSizeKB = [int]([math]::Round(((Get-ChildItem $InstallDir -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1KB))
Set-ItemProperty -Path $UninstallKey -Name "DisplayName" -Value "NovaGet Download Manager"
Set-ItemProperty -Path $UninstallKey -Name "DisplayVersion" -Value "1.0.0"
Set-ItemProperty -Path $UninstallKey -Name "Publisher" -Value "FatihG60"
Set-ItemProperty -Path $UninstallKey -Name "DisplayIcon" -Value $IconPath
Set-ItemProperty -Path $UninstallKey -Name "InstallLocation" -Value $InstallDir
Set-ItemProperty -Path $UninstallKey -Name "InstallDate" -Value (Get-Date -Format "yyyyMMdd")
Set-ItemProperty -Path $UninstallKey -Name "UninstallString" -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$UninstallScriptPath`""
Set-ItemProperty -Path $UninstallKey -Name "QuietUninstallString" -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$UninstallScriptPath`""
Set-ItemProperty -Path $UninstallKey -Name "EstimatedSize" -Value $TotalSizeKB -Type DWord
Set-ItemProperty -Path $UninstallKey -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $UninstallKey -Name "NoRepair" -Value 1 -Type DWord
Set-ItemProperty -Path $UninstallKey -Name "URLInfoAbout" -Value "https://github.com/FatihG60/NovaGet"
Write-Host " -> Registered in Control Panel (Program Ekle veya Kaldır)" -ForegroundColor Green

# 8. Launch installed NovaGet
Start-Process -FilePath $ExePath -WorkingDirectory $InstallDir
Write-Host "`nNovaGet has been permanently installed and launched!" -ForegroundColor Cyan
Write-Host "Installed App Path : $ExePath" -ForegroundColor White
Write-Host "Installed Extension: $ExtInstallDir" -ForegroundColor White
