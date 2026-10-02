@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\ProjectSync\Start-ProjectSync.ps1"
if errorlevel 1 pause
