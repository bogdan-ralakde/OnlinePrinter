@echo off
setlocal EnableExtensions
title LabelBridge Setup

rem ---------------------------------------------------------------- elevate
net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -Verb RunAs -FilePath '%~f0' -ArgumentList '%*'"
    exit /b
)

echo.
echo   ==========================================
echo      LabelBridge - virtual label printer
echo   ==========================================
echo.

rem ---------------------------------------------------------------- options
set "OPTIONS="
set "PRINTER=Y"
set /p PRINTER=Install the virtual printer on this PC? [Y/n]:
if /i "%PRINTER%"=="n" (set "OPTIONS=") else (set "OPTIONS=-InstallPrinter")

rem ---------------------------------------------------------------- run
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %OPTIONS% %*

if errorlevel 1 (
    echo.
    echo Setup did not finish successfully. See the messages above.
    pause
)

endlocal
