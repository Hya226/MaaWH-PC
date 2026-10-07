@echo off
chcp 65001 >nul
cd /d %~dp0

echo [MaaWH-PC] 同步任务包清单（whmx/interface.json -^> .\interface.json）...
D:\python\python.exe scripts\sync_interface.py
if errorlevel 1 (
    echo [MaaWH-PC] 清单同步失败，请检查 Python 环境后重试。
    pause
    exit /b 1
)

if not exist "%~dp0MFAAvalonia.exe" (
    echo [MaaWH-PC] 未找到 MFAAvalonia.exe，GUI 壳未部署。
    pause
    exit /b 1
)

rem 便携 .NET 10 运行时（工程内 dotnet\，免系统安装/免管理员）
if exist "%~dp0dotnet\dotnet.exe" set "DOTNET_ROOT=%~dp0dotnet"

echo [MaaWH-PC] 启动 GUI...
start "" "%~dp0MFAAvalonia.exe"
