@echo off
cd /d "%~dp0"
if not exist "build\Godox.Desktop.exe" (
  echo First run: execute setup.cmd
  pause
  exit /b 1
)
start "" "%~dp0build\Godox.Desktop.exe" --root "%~dp0."
