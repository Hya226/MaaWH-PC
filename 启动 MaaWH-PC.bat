@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\sync_interface.ps1"
if not exist "%~dp0MFAAvalonia.exe" (echo GUI not found & pause & exit /b 1)
set "DOTNET_ROOT=%~dp0dotnet"
start "" "%~dp0MFAAvalonia.exe"
