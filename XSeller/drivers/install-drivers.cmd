@echo off
:: Installs ADB USB drivers so Android phones appear as "ADB Interface".
:: Must run elevated (Install-XSeller.bat / X Seller request UAC).
setlocal
set "HERE=%~dp0"
set "LOG=%ProgramData%\XSeller\driver-install.log"
mkdir "%ProgramData%\XSeller" 2>nul
echo ==== %DATE% %TIME% ==== >> "%LOG%"

:: 1) Google USB Driver (signed INF, Pixel/Nexus + generic Google VIDs)
if exist "%HERE%usb_driver\android_winusb.inf" (
  pnputil /add-driver "%HERE%usb_driver\android_winusb.inf" /install >> "%LOG%" 2>&1
)
:: 2) ClockworkMod Universal ADB Driver (signed; Samsung, Xiaomi, Tecno/Infinix, etc.)
if exist "%HERE%UniversalAdbDriverSetup.msi" (
  msiexec /i "%HERE%UniversalAdbDriverSetup.msi" /qn /norestart /l*v "%ProgramData%\XSeller\uadb-msi.log"
  echo msiexec exit %ERRORLEVEL% >> "%LOG%"
)
:: Install INFs the MSI dropped into the driver store for already-plugged phones, then rescan.
for /r "%ProgramFiles(x86)%\ClockworkMod\Universal Adb Driver" %%F in (*.inf) do pnputil /add-driver "%%F" /install >> "%LOG%" 2>&1
for /r "%ProgramFiles%\ClockworkMod\Universal Adb Driver" %%F in (*.inf) do pnputil /add-driver "%%F" /install >> "%LOG%" 2>&1
pnputil /scan-devices >> "%LOG%" 2>&1

echo %DATE% %TIME% > "%ProgramData%\XSeller\drivers-installed.txt"
if /i not "%~1"=="/quiet" echo Phone drivers installed.
exit /b 0
