@echo off
cd /d "%~dp0"
if not exist "test\.venv\Scripts\python.exe" (
  python -m venv test\.venv
  if errorlevel 1 goto failed
)
test\.venv\Scripts\python.exe -m pip install -r test\requirements.txt
if errorlevel 1 goto failed
dotnet publish src\Godox.Desktop\Godox.Desktop.csproj -c Release -o build
if errorlevel 1 goto failed
echo Ready. Run start.cmd
pause
exit /b 0
:failed
echo Setup failed. See the error above.
pause
exit /b 1
