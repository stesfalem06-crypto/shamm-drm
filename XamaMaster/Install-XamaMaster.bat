@echo off
title Xama Master - Install
set "DEST=%LOCALAPPDATA%\XamaMaster"
mkdir "%DEST%" 2>nul
xcopy /E /I /Y "%~dp0*" "%DEST%" >nul
powershell -NoProfile -Command ^
  "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\Xama Master.lnk'); ^
   $s.TargetPath='%DEST%\XamaMaster.exe'; $s.WorkingDirectory='%DEST%'; $s.Save()"
set "SM=%APPDATA%\Microsoft\Windows\Start Menu\Programs"
powershell -NoProfile -Command ^
  "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%SM%\Xama Master.lnk'); ^
   $s.TargetPath='%DEST%\XamaMaster.exe'; $s.WorkingDirectory='%DEST%'; $s.Save()"
echo Done. Launching Xama Master...
start "" "%DEST%\XamaMaster.exe"
