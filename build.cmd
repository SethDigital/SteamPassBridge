@echo off
rem Builds bin\SteamPassBridge.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
rem The ViGEmBus driver installer is embedded so end users don't have to download it separately.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find %CSC%
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\fetch-vigem.ps1" || exit /b 1
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" -nologo -target:winexe -platform:x64 -optimize+ -out:"%~dp0bin\SteamPassBridge.exe" ^
  -win32icon:"%~dp0assets\app.ico" -win32manifest:"%~dp0assets\app.manifest" ^
  -resource:"%~dp0vendor\ViGEmBus_1.22.0_x64_x86_arm64.exe",ViGEmBusSetup.exe ^
  -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Xml.dll -r:System.Xml.Linq.dll -r:System.Core.dll -r:System.Web.Extensions.dll ^
  -recurse:"%~dp0src\*.cs"
