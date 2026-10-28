@echo off
setlocal
if exist "%~dp0dist\MicStudio-win-x64\MicStudio.exe" (
  start "" "%~dp0dist\MicStudio-win-x64\MicStudio.exe"
  exit /b 0
)
if not exist "%~dp0src\MicStudio.App\bin\Release\net10.0-windows\MicStudio.exe" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build.ps1"
  if errorlevel 1 exit /b 1
)
start "" "%~dp0src\MicStudio.App\bin\Release\net10.0-windows\MicStudio.exe"
exit /b 0
