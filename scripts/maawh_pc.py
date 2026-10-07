# -*- coding: utf-8 -*-
"""
MaaWH-PC 冒烟/运行脚本（MaaFramework Python 绑定版宿主最小实现）

用法:
  python scripts/maawh_pc.py              # 只连 MuMu + 加载任务包 + 截一帧保存
  python scripts/maawh_pc.py VF_启动      # 加载后跑指定入口（流水线节点名）
  python scripts/maawh_pc.py VF_清体力 --list   # 列出清单里的任务与入口
"""
import argparse
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WHMX = ROOT / "whmx"          # 目录联接 -> E:\MaaWH\whmx（单一事实源）
LOGS = ROOT / "logs"

ADB = r"D:\android-studio\Sdk\platform-tools\adb.exe"
ADDR = "127.0.0.1:16384"      # MuMu 12 / 物华弥新定制版 默认 adb 端口


def save_image(img, out: Path) -> None:
    """numpy(BGR) 落盘，cv2 优先，PIL 兜底"""
    try:
        import cv2

        cv2.imwrite(str(out), img)
        return
    except ImportError:
        pass
    from PIL import Image

    Image.fromarray(img[:, :, ::-1]).save(out)


def find_device(toolkits, addr: str):
    """扫描 adb 设备，取 MuMu（拿到最优截图/输入方式与模拟器增强配置）"""
    try:
        devices = toolkits.find_adb_devices(ADB)
    except TypeError:
        devices = toolkits.find_adb_devices()
    for d in devices:
        if d.address == addr:
            return d
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("entry", nargs="?", default=None, help="要跑的入口（流水线节点名）")
    parser.add_argument("--list", action="store_true", help="列出 interface.json 的任务清单")
    parser.add_argument("--addr", default=ADDR)
    args = parser.parse_args()

    from maa.controller import AdbController
    from maa.resource import Resource
    from maa.tasker import Tasker
    from maa.toolkit import Toolkit

    LOGS.mkdir(exist_ok=True)

    if args.list or not args.entry:
        iface = json.loads((WHMX / "interface.json").read_text(encoding="utf-8"))
        print(f"任务包: {iface.get('label')} v{iface.get('version')}")
        print("-" * 46)
        for t in iface.get("task", []):
            opts = ",".join(t.get("option", []))
            print(f"  {t['name']:<10} entry={t.get('entry')}  {opts}")
        print("-" * 46)
        if not args.entry:
            print("(不传 entry 则只做连接+加载+截帧冒烟)")

    Toolkit.init_option(str(LOGS))

    print(f"[*] 连接 {args.addr} ...")
    dev = find_device(Toolkit, args.addr)
    if dev is not None:
        print(f"    扫描到设备: {dev.name} screencap=0x{dev.screencap_methods:x} input=0x{dev.input_methods:x}")
        ctrl = AdbController(
            ADB,
            args.addr,
            screencap_methods=dev.screencap_methods,
            input_methods=dev.input_methods,
            config=dev.config,
        )
    else:
        print("    扫描未命中（用默认方式直连）")
        ctrl = AdbController(ADB, args.addr)
    if not ctrl.post_connection().wait().succeeded or not ctrl.connected:
        print("✗ 控制器连接失败")
        return 1
    print("✓ 已连接")

    print(f"[*] 加载任务包 {WHMX} ...")
    res = Resource()
    if not res.post_bundle(WHMX).wait().succeeded:
        print("✗ 任务包加载失败（看 logs/debug/maa.log）")
        return 1
    print("✓ 任务包加载成功")

    tasker = Tasker()
    if not tasker.bind(res, ctrl):
        print("✗ Tasker 绑定失败")
        return 1

    ctrl.post_screencap().wait()
    img = ctrl.cached_image
    if img is not None:
        out = LOGS / f"pc_frame_{time.strftime('%H%M%S')}.png"
        save_image(img, out)
        print(f"✓ 已截帧: {out}  shape={img.shape}")
    else:
        print("⚠ 截图为空")

    if args.entry:
        print(f"[*] 跑任务: {args.entry}")
        t0 = time.time()
        job = tasker.post_task(args.entry)
        job.wait()
        ok = job.succeeded
        print(f"{'✓' if ok else '✗'} {args.entry} status={job.status} 耗时 {time.time()-t0:.1f}s")
        return 0 if ok else 2

    return 0


if __name__ == "__main__":
    sys.exit(main())
