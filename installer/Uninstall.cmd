@echo off
setlocal EnableExtensions
title LabelBridge Uninstall

net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -Verb RunAs -FilePath '%~f0' -ArgumentList '%*'"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1" %*

endlocal
