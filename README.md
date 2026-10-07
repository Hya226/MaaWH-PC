# MaaWH-PC

《物华弥新》自动化 **PC 端**（MaaFramework 生态，对标 MAA 的使用方式）：连 **MuMu 模拟器**，复用手机端 MaaWH 的同一个任务包（`E:\MaaWH\whmx`，目录联接共享、改一处两端生效）。

> GUI 是基于 MFAAvalonia 自建的换皮版：标题 **MaaWH**、图标与手机端同款。换皮/升级壳的流程见 `AGENTS.md`「品牌换皮」。

## 环境要求（本机已就绪）

- MuMu 模拟器（物华弥新定制版，`D:\Program Files\YXWuHuaMiXin-12.0`），adb 地址 `127.0.0.1:16384`
- 游戏 B 服包已安装并登录过一次（`com.cipaishe.wuhua.bilibili`）
- Python 3.12 + `pip install MaaFw==5.14.2`（跑脚本用；GUI 不需要）

## 用法

**GUI（日常用）**：双击 `启动 MaaWH-PC.bat` → MFAAvalonia 打开后控制器类型选「ADB（PC 端 / 模拟器）」、设备一般会自动识别出 `物华弥新 (127.0.0.1:16384)` → 勾任务 → 开始任务。

> GUI 需要 .NET 10：工程已内置便携运行时（`dotnet\`），bat 会自动设 `DOTNET_ROOT`，**不需要**也不建议往系统里装 .NET。

**命令行（调试用）**：

```bat
cd /d E:\MaaWH-PC
python scripts\maawh_pc.py --list        :: 列出全部任务与入口
python scripts\maawh_pc.py               :: 冒烟：连模拟器 + 加载任务包 + 截帧到 logs\
python scripts\maawh_pc.py VF_启动       :: 跑指定入口
```

引擎日志：`logs\debug\maafw.log`（识别命中搜 `reco hit`，模板分搜 `"score":`）。

## 注意

- **测试纪律**：刷体力类任务（清体力/演训/遗境等）先只验证识别，别跑完整流程——和手机端同一规矩。
- 模板/坐标基准 1280x720，模拟器与手机虚拟屏同分辨率通用，**不要改 MuMu 分辨率/DPI**（实测 240dpi 无影响）。
- `interface.json`（工程根）是生成物，别手改；源头在 `E:\MaaWH\whmx\interface.json`。
- 手机端交接文档：`E:\MaaWH\AGENTS.md`；本目录交接文档：`AGENTS.md`（环境事实、坑、待办都在那）。
