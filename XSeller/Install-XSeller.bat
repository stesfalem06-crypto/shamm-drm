@echo off
title X Seller - Install
set "DEST=%LOCALAPPDATA%\XSeller"
mkdir "%DEST%" 2>nul
:: Stop a running X Seller / adb so files (adb.exe!) are not locked during copy.
taskkill /F /IM XSeller.exe >nul 2>&1
taskkill /F /IM adb.exe >nul 2>&1
echo Copying files to %DEST% ...
xcopy /E /I /Y "%~dp0*" "%DEST%" >nul
:: Desktop shortcut
powershell -NoProfile -Command ^
  "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\X Seller.lnk'); ^
   $s.TargetPath='%DEST%\XSeller.exe'; $s.WorkingDirectory='%DEST%'; $s.Save()"
:: Start menu
set "SM=%APPDATA%\Microsoft\Windows\Start Menu\Programs"
powershell -NoProfile -Command ^
  "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%SM%\X Seller.lnk'); ^
   $s.TargetPath='%DEST%\XSeller.exe'; $s.WorkingDirectory='%DEST%'; $s.Save()"
if not exist "%DEST%\platform-tools\adb.exe" echo WARNING: platform-tools\adb.exe was not copied - phones cannot be detected.
:: Phone USB drivers (Windows asks for permission once - click Yes).
if exist "%DEST%\drivers\install-drivers.cmd" (
  echo Installing phone USB drivers - click Yes on the Windows prompt ...
  powershell -NoProfile -Command "Start-Process -FilePath '%DEST%\drivers\install-drivers.cmd' -ArgumentList '/quiet' -Verb RunAs -Wait -WindowStyle Hidden"
)
echo.
echo Done. Double-click "X Seller" on your Desktop.
echo Folders auto-created under Documents\XSeller on first run.
pause
start "" "%DEST%\XSeller.exe"
