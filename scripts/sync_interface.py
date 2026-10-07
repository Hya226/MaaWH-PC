# -*- coding: utf-8 -*-
"""
从仓库 whmx（单一事实源，经 whmx 目录联接读取）生成 PC 版 interface.json。

唯一差异：resource[].path 从 {PROJECT_DIR}（手机端布局，接口与 pipeline 同目录）
改写为 {PROJECT_DIR}/whmx（PC 布局，接口在工程根、资源在联接目录里）。
每次启动 GUI 前跑一遍（启动 MaaWH-PC.bat 已带），保证与仓库定稿零漂移。
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "whmx" / "interface.json"
DST = ROOT / "interface.json"


def main() -> int:
    data = json.loads(SRC.read_text(encoding="utf-8"))
    for r in data.get("resource", []):
        r["path"] = ["{PROJECT_DIR}/whmx"]
    data["description"] = (
        "MaaWH 的《物华弥新》任务包（PC 端：MaaFramework + MuMu 模拟器；"
        "与本机 E:\\MaaWH\\whmx 共享同一源，本文件由 scripts/sync_interface.py 生成，勿手改）"
    )
    DST.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"已生成 {DST}（源自 {SRC}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
