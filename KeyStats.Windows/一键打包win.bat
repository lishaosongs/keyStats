@echo off
setlocal
cd /d "%~dp0"

echo === KeyStats Windows Release Packaging ===
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Configuration Release
if errorlevel 1 (
    echo.
    echo Packaging failed.
    pause
    exit /b 1
)

echo.
echo Packaging completed successfully.
pause
