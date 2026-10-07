# -*- coding: utf-8 -*-
"""
MaaWH-PC 便携版打包：组装解压即用的 zip（供 GitHub Release 上传）。

用法：python scripts/make_release.py [--installer PATH]
- 组装 _dist/MaaWH-PC/（运行必需 + 任务包实体 + 一键启动脚本）
- 压缩为 _dist/MaaWH-PC-v0.2.0.zip

运行必需目录：libs / runtimes / dotnet / assets / scripts
任务包：whmx（实体拷贝，非联接）
前提：部署根已完成构建产物部署与 exe 改名（MaaWH.exe）。
"""
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "_dist"
STAGING = DIST / "MaaWH-PC"
INSTALLER_CACHE = DIST / "runtime-installer"
INSTALLER_NAME = "windowsdesktop-runtime-10.0.12-win-x64.exe"
INSTALLER_URL = (
    "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/"
    "windowsdesktop-runtime-10.0.12-win-x64.exe"
)
ZIP = DIST / "MaaWH-PC-v0.2.0.zip"

COPY_DIRS = ["libs", "runtimes", "dotnet", "assets", "scripts"]
COPY_FILES = [
    "MaaWH.exe",
    "MFAAvalonia.dll",
    "MFAAvalonia.deps.json",
    "MFAAvalonia.runtimeconfig.json",
    "libloader.dll",
    "appsettings.json",
    "interface.json",
    "DependencySetup_依赖库安装_win.bat",
    "start_maawh.bat",
    "启动 MaaWH-PC.bat",
    "启动 MaaWH-PC（静默）.vbs",
]

NOTE = """MaaWH-PC —— 《物华弥新》PC 端自动化宿主

【启动方式（二选一）】
1. 双击「第一次启动请点我.bat」：自动安装 .NET 桌面运行时（弹 UAC 点「是」），
   之后日常直接双击 MaaWH.exe 即可
2. 双击「启动 MaaWH-PC（静默）.vbs」：无需安装任何东西（无黑窗静默启动）

【环境要求】
- MuMu 模拟器 12（物华弥新定制版），adb 端口 127.0.0.1:16384
- 模拟器内游戏已登录（B 服包），保持模拟器开机

【内置】
- 任务包 whmx/（全部流程与模板，更新时整体替换本目录并重启）
- OCR 模型与器者名单（assets/）
- 引擎 MaaFramework v5.14.2

【小工具】
- 外勤见闻识别：游戏停在【外勤见闻】列表第一页后运行
- 抽卡识别：游戏停在【招集记录】页后运行（记录累积在 data/gacha/）
- 额外队列：博物研学、冬谷竞赛等，在主页任务列表勾选运行

【注意】
- 任务运行失败后是否继续，在「设置 → 游戏设置」里切换
- 更新源已默认 GitHub，无需 Mirror 酱
"""

FIRST_RUN_BAT = (
    "@echo off\r\n"
    "chcp 936 >nul\r\n"
    "title MaaWH 首次启动\r\n"
    'cd /d "%~dp0"\r\n'
    "set FOUND=\r\n"
    "for /f \"tokens=*\" %%i in ('dotnet --list-runtimes 2^>nul ^| findstr /c:\"Microsoft.WindowsDesktop.App 10.\"') do set FOUND=1\r\n"
    "if defined FOUND goto :sync\r\n"
    "echo ==============================================\r\n"
    "echo  首次运行：需要安装 .NET 桌面运行时（微软官方组件）\r\n"
    "echo  弹出的用户账户控制窗口请点「是」，安装约 30 秒\r\n"
    "echo ==============================================\r\n"
    "powershell -NoProfile -Command \"Start-Process -FilePath '%~dp0"
    + INSTALLER_NAME + "' -ArgumentList '/install','/quiet','/norestart' -Verb RunAs -Wait\"\r\n"
    "if errorlevel 1 (\r\n"
    "    echo 安装未完成。若取消了授权，可改用「启动 MaaWH-PC（静默）.vbs」启动（无需安装）。\r\n"
    "    pause\r\n"
    ")\r\n"
    ":sync\r\n"
    'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\\sync_interface.ps1"\r\n'
    "echo 正在启动 MaaWH...\r\n"
    'start "" "%~dp0MaaWH.exe"\r\n'
    "exit /b 0\r\n"
)


def main() -> int:
    installer = Path(sys.argv[sys.argv.index("--installer") + 1]) if "--installer" in sys.argv \
        else INSTALLER_CACHE / INSTALLER_NAME
    if not installer.exists():
        print(f"下载运行时安装器 -> {installer}")
        installer.parent.mkdir(parents=True, exist_ok=True)
        r = subprocess.run(["curl", "-sL", "-o", str(installer), INSTALLER_URL])
        if r.returncode != 0 or not installer.exists() or installer.stat().st_size < 10_000_000:
            print("安装器下载失败", file=sys.stderr)
            return 1

    # 重建 staging
    if STAGING.exists():
        shutil.rmtree(STAGING)
    STAGING.mkdir(parents=True)
    for d in COPY_DIRS:
        shutil.copytree(ROOT / d, STAGING / d)
    for f in COPY_FILES:
        shutil.copy2(ROOT / f, STAGING / f)
    # 任务包实体拷贝（联接会自动展开为内容）
    shutil.copytree(ROOT / "whmx", STAGING / "whmx")

    # 一键启动与说明
    (STAGING / "第一次启动请点我.bat").write_bytes(FIRST_RUN_BAT.encode("gbk"))
    shutil.copy2(installer, STAGING / INSTALLER_NAME)
    (STAGING / "使用说明.txt").write_text(NOTE, encoding="utf-8")

    # 压缩
    if ZIP.exists():
        ZIP.unlink()
    subprocess.run(
        [r"C:\Windows\System32\tar.exe", "-a", "-cf", str(ZIP), "MaaWH-PC"],
        cwd=DIST, check=True,
    )
    mb = ZIP.stat().st_size / 1024 / 1024
    print(f"完成：{ZIP}（{mb:.1f} MB，{sum(len(f) for _, _, f in os.walk(STAGING))} 个文件）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
