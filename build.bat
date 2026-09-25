@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-release.ps1" %*
if errorlevel 1 (
  echo.
  echo Build failed. See the error above.
  exit /b 1
)
echo.
echo Build complete. See the dist folder.
exit /b 0
