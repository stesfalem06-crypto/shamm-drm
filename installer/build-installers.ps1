# Build Inno Setup installers after publish. Invoked from the .csproj on the Windows CI runner
# (no workflow file edit required). Args: -App xseller|xamamaster -SourceDir <publish> [-X86Dir <publish\x86>]
param(
  [Parameter(Mandatory=$true)][ValidateSet('xseller','xamamaster')][string]$App,
  [Parameter(Mandatory=$true)][string]$SourceDir,
  [string]$X86Dir = ""
)
$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
$Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (!(Test-Path $Iscc)) {
  Write-Host "Installing Inno Setup silently..."
  $setup = Join-Path $env:TEMP "innosetup-install.exe"
  Invoke-WebRequest -Uri "https://jrsoftware.org/download.php/is.exe" -OutFile $setup
  Start-Process -FilePath $setup -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/SP-" -Wait
}
if (!(Test-Path $Iscc)) { throw "ISCC.exe not found after install" }

# Ensure crypto DLL is beside the exe
$dllName = "shamm_crypto.dll"
if ($App -eq 'xseller') {
  $exe = "XSeller.exe"; $iss = Join-Path $PSScriptRoot "XSeller.iss"
} else {
  $exe = "XamaMaster.exe"; $iss = Join-Path $PSScriptRoot "XamaMaster.iss"
}
foreach ($dir in @($SourceDir, $X86Dir) | Where-Object { $_ -and (Test-Path $_) }) {
  $dll = Join-Path $dir $dllName
  $exep = Join-Path $dir $exe
  if (!(Test-Path $dll)) { throw "MISSING $dll — installer refuses to ship without the crypto DLL" }
  if (!(Test-Path $exep)) { throw "MISSING $exep" }
  Write-Host "OK $dll ($((Get-Item $dll).Length) bytes) next to $exe"
}

& $Iscc "/DArch=x64" "/DSourceDir=$SourceDir" $iss
if ($X86Dir -and (Test-Path $X86Dir)) {
  & $Iscc "/DArch=x86" "/DSourceDir=$X86Dir" $iss
}
Get-ChildItem (Join-Path $PSScriptRoot "output\*.exe") | ForEach-Object { Write-Host "Built $($_.FullName) ($([math]::Round($_.Length/1MB,1)) MB)" }
