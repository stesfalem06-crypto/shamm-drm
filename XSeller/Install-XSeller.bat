@echo off
title X Seller - Install
set "DEST=%LOCALAPPDATA%\XSeller"
mkdir "%DEST%" 2>nul
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
echo.
echo Done. Double-click "X Seller" on your Desktop.
echo Folders auto-created under Documents\XSeller on first run.
pause
start "" "%DEST%\XSeller.exe"
