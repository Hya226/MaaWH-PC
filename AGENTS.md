# MaaWH-PC 开发交接文档（给后续 AI/开发者）

项目：MaaWH-PC —— 《物华弥新》自动化的 **PC 端宿主**，与手机端 MaaWH（`E:\MaaWH`，Kotlin/安卓）共享同一个任务包 `whmx/`。连 **MuMu 模拟器**（同 MAA 的连接方式），**不需要** Shizuku / 虚拟屏 / 保活那一整套安卓机制。

> 参照系：MaaFramework 生态的通用做法（M9A 等）=「现成 GUI 壳 + 任务包」。PC 端正走这条路：引擎用 MaaFramework 官方运行时，GUI 用 MFAAvalonia 通用壳，任务包就是仓库里那份 `whmx/`。

## 〇、当前状态（2026-10-06 冒烟通过）

- **已验证**：MuMu（127.0.0.1:16384，物华弥新定制版）→ AdbController 连接 → 加载 `whmx` 任务包 → `VF_启动` 全链成功（58.5s），13 个识别节点全部一次命中，**模板最高匹配分 0.9907**。手机虚拟屏做的模板在模拟器上近乎完美匹配。
- **已验证（GUI）**：MFAAvalonia v2.16.2 装在工程根（与 interface.json 同目录），免系统安装——**.NET 10 运行时**解压在 `dotnet\`，启动脚本/手动启动前 `set DOTNET_ROOT=%~dp0dotnet`（本机只有 .NET 6/7/8，MFAAvalonia 是 net10.0 框架依赖版；deps.json 无 WindowsDesktop 引用，基础 runtime 够用）。GUI 实测：自动加载《物华弥新 v0.2.0》清单、控制器类型=「ADB（PC 端 / 模拟器）」（interface.json 新增的那条预设）、自动识别设备 `物华弥新 (127.0.0.1:16384)`、任务列表 16 项带默认勾选、实时画面出图（FPS ~30）。
- **EmulatorExtras 观察**：MFAAvalonia 日志出现过「实时画面已连接但无法获取画面 (EmulatorExtras)」→ 已自动回落普通截图方式，实时视图照常出图。MuMu 增强截图（~10ms 级）要 MuMu 窗口非最小化才可用；不强求，普通 adb 截图对流水线足够（模板识别靠引擎而非 GUI 预览）。
- **分辨率结论**：MuMu 当前 1280x720@240dpi（平板模式），与手机虚拟屏基准 1280x720@320dpi 只有 DPI 差异——**实测无影响**（该游戏 UI 按分辨率像素排版，不按 dp），不要去改 MuMu 配置。若将来真要改：`D:\Program Files\YXWuHuaMiXin-12.0\vms\YXWuHuaMiXin-12.0-0\configs\customer_config.json` 里 `setting.resolution.mode.custom` = `"1280:720:320"`（格式 宽:高:dpi），改完重启模拟器。
- **未验证**：其余 15 个任务在模拟器上的回归（清单见 `python scripts/maawh_pc.py --list`）；**刷体力类任务照搬手机端测试纪律——只验证识别，别跑完整流程**。

## 一、目录结构

```
E:\MaaWH-PC\
├─ whmx -> E:\MaaWH\whmx        # ★ Windows 目录联接（mklink /J）：任务包单一事实源在本仓库
│                               #   流程编辑器（E:\MaaWH Studio）产出 vf_*.json → 手机和 PC 同时生效，无需同步
├─ interface.json               # 【生成物】scripts/sync_interface.py 从 whmx/interface.json 生成，
│                               #   唯一差异是 resource path 改为 {PROJECT_DIR}/whmx。勿手改。
├─ scripts/
│  ├─ maawh_pc.py               # 冒烟/无头运行：python scripts/maawh_pc.py [入口名]（不传入口=只连+载+截帧）
│  └─ sync_interface.py         # 生成 PC 版 interface.json（启动 GUI 前自动跑）
├─ MFAAvalonia.exe + libs/ 等   # MaaWH 换皮版壳（基于 MFAAvalonia v2.16.2 自建，标题=MaaWH、图标已换）
│                               #   ★ exe 必须与 interface.json 同目录（它读 ./interface.json，M9A 同款布局）
├─ dotnet\                      # .NET 10.0.12 便携运行时（启动前 set DOTNET_ROOT=此目录；bat 已带）
├─ _sdk\                        # .NET 10 SDK 10.0.401 便携版（只用于重新编译壳，约 1.5GB，可删）
├─ _build\                      # 壳源码 v2.16.2 + 构建产物 publish\ + NuGet 缓存（重新换皮用，可删）
├─ maafw_runtime\               # MaaFramework v5.14.2 官方运行时（备用；Python 绑定与 GUI 各自带了运行时）
├─ logs\                        # 引擎日志 logs/debug/maafw.log（对应手机端 files/maa_logs/maafw.log）+ 截帧
└─ _dl\                         # 下载缓存（安装 zip + 清单缓存，可删）
```

## 二、环境事实（本机）

- **MuMu**：`D:\Program Files\YXWuHuaMiXin-12.0\`（物华弥新游戏定制版），游戏**已预装且 B 服包已登录**：`com.cipaishe.wuhua.bilibili`（与手机端 `MaaConst.GAME_PKG` 一致；模拟器上另有一个 `com.cipaishe.wuhua.google`，别混）。adb 端口 **127.0.0.1:16384**（实例 0；多开每实例 +32）。手机真机 adb 用另一条 `-s 2c92e197`，别搞混。
- **adb**：`D:\android-studio\Sdk\platform-tools\adb.exe`（本机 PATH 没有）。
- **Python 绑定**：`pip install MaaFw==5.14.2`（注意包名是 `MaaFw` 不是 `maa-framework`；**自带全套二进制** `site-packages/maa/bin`，不依赖解压的 release）。绑定的 API 与手机端 JNA 那套名字不同：连接是 `ctrl.post_connection().wait()`，资源 `res.post_bundle(path).wait().succeeded`，任务 `tasker.post_task(entry).wait().succeeded`，截图 `ctrl.post_screencap().wait()` + `ctrl.cached_image`（numpy BGR）。
- **GitHub 下载**：直连超时，用镜像 `https://ghfast.top/https://github.com/...`（实测 700KB/s）。
- **引擎截图方式**：`MaaToolkitAdbDeviceFind` 自动探测（含 MuMu 增强截图）；`scripts/maawh_pc.py` 已按扫描结果传最优方式。

## 三、GUI 壳（MFAAvalonia）

- 双击 `启动 MaaWH-PC.bat`：先跑 sync_interface.py 刷新清单，再设 `DOTNET_ROOT` 指向 `dotnet\`，拉起工程根的 `MFAAvalonia.exe`。
- **exe 必须与 interface.json 同目录**（MFAAvalonia 默认读自身目录的 interface.json）。资源路径由生成脚本写成 `{PROJECT_DIR}/whmx`，指向联接目录。
- 首次启动若弹「You must install or update .NET」= 没带 DOTNET_ROOT（用 bat 启动即可）；本机 .NET 只有 6/7/8，别去升级系统，便携方案已解决。
- 连接：控制器类型选「ADB（PC 端 / 模拟器）」，设备自动扫描（已实测选中 127.0.0.1:16384）。
- **品牌换皮（2026-10-06 已完成）**：窗口标题已从「MFA 任务管理器」换成 **MaaWH**（现标题 = `MaaWH v2.16.2 | 物华弥新 v0.2.0`，后半截来自 interface.json 的 label+version）。做法 = 官方壳源码构建换皮：
  - 源码：`_build\MFAAvalonia-2.16.2\`（v2.16.2 tag 包）。改动仅两处：① `MFAAvalonia/Assets/Localization/Strings*.resx` 四个语言的 `AppTitle` 值 → `MaaWH`；② `MFAAvalonia/Assets/logo.ico` 替换为我们的图标（csproj 的 ApplicationIcon 指向它，编译进 exe）。**汇编名没改**（仍叫 MFAAvalonia.exe）——改 AssemblyName 会牵连 deps.json/libloader/自更新等内部机制，不值得。
  - 构建：`_sdk\dotnet.exe`（便携 SDK 10.0.401，免安装）→ 在源码目录 `set NUGET_PACKAGES=E:\MaaWH-PC\_build\nupackages && dotnet publish MFAAvalonia.Desktop/MFAAvalonia.Desktop.csproj -c Release -r win-x64 --self-contained false -o E:\MaaWH-PC\_build\publish`。产物约 1 分钟出（增量）。
  - 部署：关掉运行中的壳 → 删根目录的 libs/ plugins/ runtimes/ MaaAgentBinary/ 和 MFAAvalonia.*、libloader.dll、DependencySetup bat → 拷 `_build\publish\*` 过来。config/（用户配置）、Assets/（logo）、interface.json、whmx、dotnet/ 都不动。
  - ⚠ 源码 tar 包里三个 `DependencySetup_依赖库安装_*` 文件名是 GBK，Windows tar 解压成乱码名，csproj 按原名引用 → publish 报 MSB3030 找不到文件。解法：把正确文件名的三个补回去（部署目录里就有 win.bat；sh 两个从乱码文件改名即可）。
  - 升级壳版本 = 换新 tag 源码包，重打这两个补丁 + 重新 publish。`_sdk`（约 1.5GB）与 `_build` 不用时可以整目录删除，不影响运行。
- **自定义页面「小工具」（2026-10-07 二次改版：顶部双栏目 Tab）**：左侧栏第二项（扳手）。顶部 TabControl 切「外勤见闻识别 / 抽卡识别」两个栏目，各栏目下方是自己的页面；运行日志和结果分析直接展示在页内（不再只进日志页/文件）。interface.json tools 组的队列任务（博物研学等）按用户要求不放这里（那是主页队列的事）。实现：`ToolsViewModel.cs`（`WaiQinToolViewModel` = 状态行 + 实时日志集合 `Logs` + 结果区 `Result` + 复制命令；`GachaToolViewModel` 占位；`IsAnyToolRunning` 栏目互斥信号）+ `ToolsView.axaml(.cs)`（构造函数里 `DataContext = Instances.ToolsViewModel`——★这个壳的页面都不走自动注入，必须自己设，漏了就是"页面渲染但所有绑定空转"；code-behind 订阅 `Logs.CollectionChanged` 做 ScrollIntoView 自动滚底）+ Instances.cs 字段（LazyStatic 生成器产出 `Instances.ToolsViewModel`）+ App.axaml.cs 注册 + RootViewContent.axaml 侧边栏项。★`WaiQinScan` 的 onLog 回调来自后台线程，页面集合更新必须 `Dispatcher.UIThread.Post`；日志级别由 `WaiLogLevel` 传递，页面按级别着色。

- **★ 小工具移植（Phase 1 外勤见闻识别：2026-10-07 已完成并实测通过）**：手机端两个识别工具搬进 PC 壳；Phase 1 已上线，Phase 2（抽卡识别）未开工。
  - **实现落点**：`Helper/OcrEngine.cs`（PP-OCR v4 rec ONNX 推理，进程级缓存）+ `Helper/WaiQinScan.cs`（状态机原样移植）+ `MaaProcessor.GetToolController()`（公开截图/点击控制器，复用实时画面那套独立截图 tasker 生命周期）+ `ToolsViewModel`（运行中按钮变「停止」、防重入、启动时查主队列 `ViewModel.IsRunning` 互斥、ScanException→Toast）。
  - **OCR 要点**：`Microsoft.ML.OnnxRuntime 1.19.2`；字典在 `InferenceSession.ModelMetadata.CustomMetadataMap["character"]`（6623 字）；帧全流程走 BGRA `byte[]`——★Avalonia 的 `CroppedBitmap` 是 `IImage` 不是 `Bitmap` 子类（没有 `CreateScaledBitmap`），裁剪缩放要走「数组裁剪 → `new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, ptr, size, dpi, stride)` → `CreateScaledBitmap` 高 48 → `CopyPixels`」这条链；CHW RGB 归一 `(x/255-0.5)/0.5` + CTC 贪心（0=blank、重复折叠、charset.size+1=空格），与手机端/`gacha/_ocr_validate.py` 同款。
  - **实测结论（2026-10-07，MuMu 1280x720）**：42 条名单扫 20 页 ~45s，34 条出现全部命中，missing 8 条与 Python 离线基准（同模型同算法）完全一致；坐标/名单直接复用手机端，零适配。
  - **★ 踩过的坑（PC 端特有）**：MaaController 截图比手机虚拟屏快，翻页点击后 350ms 游戏动画可能还没开始，GrabStable 连抓两帧旧页判「稳定」→ 旧页被记成重合、**跳过一页**（首次实测丢 2 条）。修法：主循环「与上页相同」计数前加**确认帧**（等 500ms 重抓重识别，内容变了按新页走并重置重合）；翻页后 delay 350→500ms。手机端截图慢天然规避，不需要此补丁。
  - **物料与分发**：模型 `assets\ocr_models\ch_PP-OCRv4_rec_infer.onnx`（10.9MB）+ 名单 `assets\waiqin\roster.json`（42 条）在工程根 `assets\`，运行时从 `AppContext.BaseDirectory/assets/...` 直读；OnnxRuntime native（onnxruntime.dll）publish 落在 `libs/`，壳的 `PrivatePathHelper`（AddDllDirectory + PATH 前置 + ResolvingUnmanagedDll 兜底）能解析到，无需手动处理。
  - **使用语义（与手机端一致）**：游戏手动停在【外勤见闻】列表页**第一页**再运行；工具从当前页开始翻页扫到底，从中间开始会漏前半列表。与主队列互斥是单向检查（工具启动时查队列；队列启动不查工具——别同时在跑）。
  - **Phase 2 抽卡识别（未开工）**：`GachaCrawler.kt`(695) + `GachaStore.kt`(748) + `gacha/points.json`（池子/坐标/稀有度色相）——行定位（饱和度聚类）、翻页终止（区域哈希）、池子下拉切换、uid 锚点续爬、记录存储与统计面板。工程量数倍于 Phase 1；OcrEngine/WaiQinScan 的基建（帧获取、BGRA 链、OCR、编辑距离）Phase 2 直接复用；面板 UI 可先用简化版（日志+文本统计）。
- interface.json 顶层有自定义字段 `removed`（手机端装包合并用），通用壳会忽略，无害。

## 四、与手机端的边界（重要）

1. **改任务只改一处**：流水线/模板/清单都在 `E:\MaaWH\whmx`（或经流程编辑器回写），PC 端通过联接直接吃。唯一不能共享的是 `interface.json` 的 resource 路径，已用生成脚本解决。
2. **whmx/interface.json 的 controller 段**（2026-10-06 加了 `"Adb"` 预设）：手机端 TaskPack 完全不解析该字段，加了不影响手机；PC/通用壳用它识别控制器类型。
3. **模板基准**：1280x720（虚拟屏帧 = 模拟器帧，同分辨率）。以后做新模板在两边都通用，不用做两套。
4. **不移植的 App 侧功能**：抽卡记录抓取（App 侧 OCR 基建）、定时任务（手机端 ScheduleManager；PC 可用 MFAAvalonia 自带定时或 Windows 计划任务调 `scripts/maawh_pc.py`）、虚拟屏预览/悬浮窗（PC 直接看模拟器窗口）。
5. **测试纪律**：刷冬谷币/演训等耗体力任务同样适用于 PC 端——先只验证识别（截帧 + 本地复算匹配分），别跑完整流程。

## 五、已知坑

- **别 git init 这个目录**（除非先 .gitignore 掉 `whmx/`）：git 会把联接当普通目录递归，把整个任务包重复收进来。真要建仓先写 `.gitignore`（whmx/、dotnet/、libs/、plugins/、runtimes/、MaaAgentBinary/、logs/、_dl/、maafw_runtime/、interface.json、MFAAvalonia.*）。
- 删除/搬家 `E:\MaaWH` 会弄断联接，重建：`cmd /c mklink /J E:\MaaWH-PC\whmx E:\MaaWH\whmx`。
- 引擎日志在 `logs\debug\maafw.log`（Toolkit.init_option 指到 logs\）；识别命中行格式 `reco hit [result.name=xxx]`，模板分在 `"score":0.xxxx`。
- 游戏在模拟器上首次登录要走一次手动 B 服登录；之后会话保持。公告/活动弹窗与手机端同期可能不同，识别不过先截帧看画面。
- **GitHub 直连超时**：release 下载用 `https://ghfast.top/https://github.com/...` 前缀（实测 ~700KB/s）；微软 CDN（builds.dotnet.microsoft.com）国内可直连。
- **解压 MFAAvalonia zip 的坑**：包内有一个 GBK 编码文件名的 bat，`unzip` 会中途报错退出（已解的文件留在原地，**看似解完实则不完整**）；用 Windows 自带 `C:\Windows\System32\tar.exe -xf`（注意 Git Bash 的 tar 是 GNU 版不认 zip）。校验清单时注意 Windows 原生工具输出带 CRLF，`grep '/$'` 过滤目录项前先 `tr -d '\r'`，否则会误报一堆"缺文件"。
- **绑定 API 与手机端 JNA 不同名**：连接是 `ctrl.post_connection().wait()`（没有 `connect()`）；截图 `post_screencap().wait()` + `ctrl.cached_image`；Python 包名 `MaaFw`（import 名 `maa`），PyPI 上没有 `maa-framework`。
