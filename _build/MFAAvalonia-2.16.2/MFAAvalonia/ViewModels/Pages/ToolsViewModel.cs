using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;

namespace MFAAvalonia.ViewModels.Pages;

/// <summary>
/// 小工具页：顶部栏目（外勤见闻识别 / 抽卡识别）切换，各栏目自己的页面内嵌
/// 操作区与结果区，运行日志和统计分析直接展示在页面上。
/// 外勤见闻 = WaiQinScan.kt 的移植（WaiQinScan.cs + OcrEngine.cs）；抽卡识别待移植（占位）。
/// </summary>
public partial class ToolsViewModel : ObservableObject
{
    public WaiQinToolViewModel WaiQin { get; } = new();

    public GachaToolViewModel Gacha { get; } = new();

    /// <summary>任一工具运行中（栏目间互斥的依据）</summary>
    [ObservableProperty]
    private bool _isAnyToolRunning;

    /// <summary>OCR 引擎进程级缓存（模型加载一次，两个小工具共用）</summary>
    private static OcrEngine? _ocrEngine;

    public static OcrEngine GetSharedOcrEngine()
    {
        if (_ocrEngine != null) return _ocrEngine;
        var ocr = new OcrEngine();
        LoggerHelper.Info($"OCR：本地 PP-OCR v4 已加载（{ocr.CharsetSize} 字）");
        _ocrEngine = ocr;
        return _ocrEngine;
    }
}

/// <summary>日志行（页面按级别着色）</summary>
public sealed class ToolLogLine
{
    public string Time { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public WaiLogLevel Level { get; init; }

    public IBrush Color => Level switch
    {
        WaiLogLevel.Warn => Brushes.Orange,
        WaiLogLevel.Success => Brushes.Green,
        _ => Brushes.Gray,
    };
}

/// <summary>一次扫描的结果（页面结果区渲染）</summary>
public sealed class ToolResultView
{
    public string Summary { get; init; } = string.Empty;
    public ObservableCollection<string> Missing { get; init; } = [];
    public string ExportNote { get; init; } = string.Empty;
}

public partial class WaiQinToolViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public WaiQinToolViewModel()
    {
        CopyResultCommand = new RelayCommand(CopyResult);
        RunCommand = new RelayCommand(() => _ = RunOrStopAsync());
    }

    public string Intro =>
        "停在游戏【外勤见闻】列表页第一页后运行：自动翻页 OCR 全部标题，对比名单报告本轮未出现的见闻（手机端 WaiQinScan 的移植）。全程只读浏览，不做领取操作。";

    /// <summary>是否可运行（抽卡栏目占位置灰的对照）</summary>
    public bool IsAvailable => true;

    /// <summary>运行中：按钮变「停止」，防重入</summary>
    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "未运行";

    [ObservableProperty]
    private string _runLabel = "运行";

    partial void OnIsRunningChanged(bool value)
    {
        RunLabel = value ? "停止" : "运行";
        if (Instances.ToolsViewModel is { } tools)
            tools.IsAnyToolRunning = value;
    }

    public ObservableCollection<ToolLogLine> Logs { get; } = [];

    [ObservableProperty]
    private ToolResultView? _result;

    partial void OnResultChanged(ToolResultView? value) => OnPropertyChanged(nameof(HasResult));

    public bool HasResult => Result != null;

    public IRelayCommand RunCommand { get; }

    public IRelayCommand CopyResultCommand { get; }

    private async Task RunOrStopAsync()
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            StatusText = "正在停止…（等当前页扫完）";
            return;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        Logs.Clear();
        Result = null;
        StatusText = "正在连接控制器…";
        try
        {
            var outcome = await RunScanAsync(_cts.Token);
            Result = new ToolResultView
            {
                Summary = $"名单 {outcome.RosterCount} 条 · 本轮出现 {outcome.SeenCount} 条 · 未出现 {outcome.Missing.Count} 条",
                Missing = [.. outcome.Missing.Select((e, i) => $"{i + 1}. {e.Display()}")],
                ExportNote = "缺失清单已同步写入 logs\\waiqin_missing.txt",
            };
            StatusText = outcome.Missing.Count == 0
                ? "完成：本轮没有缺失"
                : $"完成：缺失 {outcome.Missing.Count} 条";
            ToastHelper.Success("外勤见闻识别", StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止";
            ToastHelper.Info("外勤见闻识别", "已停止");
        }
        catch (WaiQinScan.ScanException ex)
        {
            StatusText = "失败";
            LoggerHelper.Error($"外勤见闻识别失败：{ex.Message}");
            ToastHelper.Error("外勤见闻识别", ex.Message);
        }
        catch (Exception ex)
        {
            StatusText = "失败";
            LoggerHelper.Error($"外勤见闻识别失败：{ex.Message}", ex);
            ToastHelper.Error("外勤见闻识别", $"运行异常：{ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }


    private async Task<WaiQinScan.ScanOutcome> RunScanAsync(CancellationToken ct)
    {
        var processor = MaaProcessorManager.Instance.Current;
        if (processor?.ViewModel is { IsRunning: true })
            throw new InvalidOperationException("主页队列正在运行，请先停止队列再使用小工具");
        var controller = processor?.GetToolController();
        if (controller == null || !controller.IsConnected)
            throw new WaiQinScan.ScanException("模拟器未连接：请先在主页连接设备");

        var ocr = ToolsViewModel.GetSharedOcrEngine();
        var scan = new WaiQinScan(
            () => processor.GetToolController(),
            ocr,
            (msg, level) =>
            {
                // 扫描在后台线程跑，页面集合的更新必须回 UI 线程
                Dispatcher.UIThread.Post(() =>
                {
                    Logs.Add(new ToolLogLine
                    {
                        Time = DateTime.Now.ToString("HH:mm:ss"),
                        Text = msg,
                        Level = level,
                    });
                    StatusText = msg;
                }, DispatcherPriority.Background);
                if (level == WaiLogLevel.Warn)
                    LoggerHelper.Warn(msg);
                else
                    LoggerHelper.Info(msg);
            });
        return await Task.Run(() => scan.RunAsync(ct), ct);
    }

    public async void CopyResult()
    {
        if (Result == null) return;
        var text = Result.Summary + "\n" + string.Join("\n", Result.Missing);
        if (Instances.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            ToastHelper.Success("已复制", "结果清单已复制到剪贴板");
        }
    }
}

/// <summary>特出条目（可标注为所属小类的 UP）</summary>
public sealed class TeItemView
{
    public string Name { get; init; } = string.Empty;
    public string Time { get; init; } = string.Empty;
    public string Cost { get; init; } = string.Empty;
    public bool IsUp { get; init; }
    public GachaToolViewModel? Owner { get; init; }
    public string Pool { get; init; } = string.Empty;
    public string Banner { get; init; } = string.Empty;

    public IRelayCommand MarkCommand => new RelayCommand(() => Owner?.ToggleUp(Pool, Banner, Name));
}

/// <summary>池内的卡池小类（banner）子卡：『小类』· N 抽 · UP xxx + 该组特出明细</summary>
public sealed class BannerStatsView
{
    public string Label { get; init; } = string.Empty;

    /// <summary>banner 原文（UP 标注 key）</summary>
    public string Raw { get; init; } = string.Empty;

    /// <summary>『label』 · N 抽 · UP xxx</summary>
    public string HeadLine { get; init; } = string.Empty;
    public ObservableCollection<TeItemView> TeList { get; init; } = [];
    public bool HasTe => TeList.Count > 0;
}
/// <summary>手动补录的记录（可删除）</summary>
public sealed class ManualItemView
{
    public string Uid { get; init; } = string.Empty;
    public string Display { get; init; } = string.Empty;
    public GachaToolViewModel? Owner { get; init; }

    public IRelayCommand DeleteCommand => new RelayCommand(() => Owner?.DeleteManual(Uid));
}

/// <summary>账号下拉条目</summary>
public sealed class AccountView
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

/// <summary>器者名单条目（可删除）</summary>
public sealed class NameItemView
{
    public string Name { get; init; } = string.Empty;
    public GachaToolViewModel? Owner { get; init; }

    public IRelayCommand DeleteCommand => new RelayCommand(() => Owner?.RemoveName(Name));
}

/// <summary>顶部总览小卡（对标手机端横滑统计行：池名/总抽数大字/出卡(歪)）</summary>
public sealed class OverviewCardView
{
    public string Pool { get; init; } = string.Empty;

    /// <summary>去掉「渠道」后缀的短名</summary>
    public string Short { get; init; } = string.Empty;
    public string Total { get; init; } = string.Empty;

    /// <summary>出卡 N（歪 M）/ 出卡 N</summary>
    public string TeLine { get; init; } = string.Empty;
    public bool HasWai { get; init; }
}

/// <summary>每个池子的统计卡片</summary>
public sealed class PoolStatsView
{
    public GachaToolViewModel? Owner { get; init; }
    public string Pool { get; init; } = string.Empty;
    public string StatsLine { get; init; } = string.Empty;

    /// <summary>标注过 UP 时显示「UP 出卡 N · 歪 M · UP 平均 Y」；未标注为空</summary>
    public string UpLine { get; init; } = string.Empty;
    public bool HasUpLine => UpLine.Length > 0;

    /// <summary>当前垫抽（≥50 提示垫刀警戒）</summary>
    public int Dian { get; init; }
    public string DianLine => $"当前垫抽：{Dian} 抽";
    public bool DianWarn => Dian >= 50;

    /// <summary>按卡池小类（banner）分组的子卡</summary>
    public ObservableCollection<BannerStatsView> Banners { get; init; } = [];

    /// <summary>该池手动补录的条目</summary>
    public ObservableCollection<ManualItemView> ManualList { get; init; } = [];
    public bool HasManual => ManualList.Count > 0;
}
/// <summary>
/// 抽卡识别（GachaCrawler.kt 的移植）：自动切 5 个池子翻页 OCR，
/// 记录累积在 data/gacha/（与手机端格式兼容），完成后展示每池统计。
/// </summary>
public partial class GachaToolViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public GachaToolViewModel()
    {
        RunCommand = new RelayCommand(() => _ = RunOrStopAsync());
        RefreshAccounts();
        RefreshSummary();
        RefreshStats();   // 启动即展示库内已有数据的统计
    }

    public string Intro =>
        "停在游戏【招集记录】页后运行：自动切换 5 个池子、翻页 OCR 全部记录入库（与手机端格式兼容，累积在 data 目录）。完成后此处展示每池统计。";

    public bool IsAvailable => true;

    /// <summary>运行中：按钮变「停止」，防重入</summary>
    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "未运行";

    [ObservableProperty]
    private string _runLabel = "开始抓取";

    [ObservableProperty]
    private string _librarySummary = string.Empty;

    partial void OnIsRunningChanged(bool value)
    {
        RunLabel = value ? "停止" : "开始抓取";
        if (Instances.ToolsViewModel is { } tools)
            tools.IsAnyToolRunning = value;
    }

    public ObservableCollection<ToolLogLine> Logs { get; } = [];

    public ObservableCollection<PoolStatsView> Pools { get; } = [];

    /// <summary>顶部总览小卡行</summary>
    public ObservableCollection<OverviewCardView> Overview { get; } = [];

    [ObservableProperty]
    private bool _hasResult;

    public IRelayCommand RunCommand { get; }

    // ---- 账号 ----

    public ObservableCollection<AccountView> Accounts { get; } = [];

    private AccountView? _selectedAccount;

    public AccountView? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value) && value != null
                && value.Id != GachaStore.ActiveAccountId())
            {
                GachaStore.SetActiveAccount(value.Id);
                RefreshStats();
                ToastHelper.Info("已切换账号", value.Name);
            }
        }
    }

    [ObservableProperty]
    private string _deleteAccountLabel = "删除";

    public IRelayCommand CreateAccountCommand => new RelayCommand(() =>
    {
        var info = GachaStore.CreateAccount(null);
        RefreshAccounts();
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == info.Id);
        RefreshStats();
        ToastHelper.Success("已新建账号", info.Name + "（当前激活）");
    });

    public IRelayCommand DeleteAccountCommand => new RelayCommand(() =>
    {
        // 二次点击确认：防误删；删除前自动收进 data/gacha/trash/ 备份
        if (DeleteAccountLabel != "确认删除")
        {
            DeleteAccountLabel = "确认删除";
            return;
        }
        DeleteAccountLabel = "删除";
        var id = SelectedAccount?.Id;
        if (id == null) return;
        var name = SelectedAccount?.Name ?? "";
        if (GachaStore.DeleteAccount(id))
        {
            RefreshAccounts();
            SelectedAccount = Accounts.FirstOrDefault();
            RefreshStats();
            ToastHelper.Success("已删除", $"{name}（数据备份在 data\\gacha\\trash）");
        }
        else
        {
            ToastHelper.Warn("无法删除", "至少要保留一个账号");
        }
    });

    partial void OnDeleteAccountLabelChanged(string value) => OnPropertyChanged(nameof(DeleteAccountLabel));

    public void RefreshAccounts()
    {
        var list = GachaStore.ListAccounts();
        var active = GachaStore.ActiveAccountId();
        Accounts.Clear();
        foreach (var a in list)
            Accounts.Add(new AccountView { Id = a.Id, Name = a.Name });
        _selectedAccount = Accounts.FirstOrDefault(a => a.Id == active);
        OnPropertyChanged(nameof(SelectedAccount));
    }

    // ---- 器者名单（OCR 纠错字典） ----

    [ObservableProperty]
    private bool _isNameListVisible;

    [ObservableProperty]
    private string _nameListLabel = "器者名单";

    [ObservableProperty]
    private string _nameListHeader = "器者名单";

    public ObservableCollection<NameItemView> NameItems { get; } = [];

    [ObservableProperty]
    private string _newName = string.Empty;

    public IRelayCommand ToggleNameListCommand => new RelayCommand(() =>
    {
        IsNameListVisible = !IsNameListVisible;
        NameListLabel = IsNameListVisible ? "收起名单" : "器者名单";
        if (IsNameListVisible) RefreshNames();
    });

    public IRelayCommand AddNameCommand => new RelayCommand(() =>
    {
        if (GachaStore.AddName(NewName))
        {
            NewName = string.Empty;
            RefreshNames();
            ToastHelper.Success("已添加", "新名字会参与下次抓取的 OCR 纠错");
        }
        else
        {
            ToastHelper.Warn("添加失败", "名字为空或已存在");
        }
    });

    public void RemoveName(string name)
    {
        GachaStore.RemoveName(name);
        RefreshNames();
    }

    public void RefreshNames()
    {
        NameItems.Clear();
        foreach (var n in GachaStore.LoadNames())
            NameItems.Add(new NameItemView { Name = n, Owner = this });
        NameListHeader = $"器者名单（{NameItems.Count} 个，OCR 纠错用）";
    }

    // ---- 手动补录表单 ----

    public static string[] PoolNames => ["限时渠道", "限定渠道", "招集渠道", "征集渠道", "赛季渠道"];
    public static string[] RarityNames => [GachaStore.RarityTop, GachaStore.RarityMid, GachaStore.RarityLow];

    [ObservableProperty]
    private bool _isAddFormVisible;

    [ObservableProperty]
    private string _formPool = PoolNames[0];

    [ObservableProperty]
    private string _formBanner = string.Empty;

    [ObservableProperty]
    private string _formName = string.Empty;

    [ObservableProperty]
    private string _formRarity = GachaStore.RarityLow;

    [ObservableProperty]
    private string _formTime = string.Empty;

    public IRelayCommand ShowAddFormCommand => new RelayCommand<string>(pool =>
    {
        // toggle 语义：浮层开着时再点（含另一张卡）无论 dismiss 与按钮点击的时序
        // 谁先谁后，状态都只会翻转一次，不会出现「点了没反应」
        FormPool = pool ?? PoolNames[0];
        IsAddFormVisible = !IsAddFormVisible;
    });

    public IRelayCommand CancelAddCommand => new RelayCommand(() => IsAddFormVisible = false);

    public IRelayCommand ConfirmAddCommand => new RelayCommand(() => _ = ConfirmAddAsync());

    /// <summary>库内现状摘要（启动时与抓取后刷新）</summary>
    public void RefreshSummary()
    {
        var all = GachaStore.LoadRecords();
        var cfg = GachaStore.LoadConfig();
        LibrarySummary = all.Count == 0
            ? "库内暂无记录（首次抓取将全量拉取 5 个池子）"
            : $"库内 {all.Count} 条 · 上次抓取 {(cfg.LastCrawlMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(cfg.LastCrawlMs).LocalDateTime.ToString("MM-dd HH:mm") : "—")} · 命中锚点只抓增量";
    }

    private async Task RunOrStopAsync()
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            StatusText = "正在停止…（等当前池收尾）";
            return;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        Logs.Clear();
        Pools.Clear();
        HasResult = false;
        StatusText = "正在连接控制器…";
        try
        {
            var report = await RunCrawlAsync(_cts.Token);
            StatusText = $"完成：新增 {report.Added} 条";
            RefreshStats(report.Library);
            ToastHelper.Success("抽卡识别", $"抓取完成，新增 {report.Added} 条");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止（已识别部分下次自动去重合并）";
            RefreshSummary();
            ToastHelper.Info("抽卡识别", "已停止");
        }
        catch (GachaCrawler.CrawlException ex)
        {
            StatusText = "失败";
            LoggerHelper.Error($"抽卡识别失败：{ex.Message}");
            ToastHelper.Error("抽卡识别", ex.Message);
        }
        catch (Exception ex)
        {
            StatusText = "失败";
            LoggerHelper.Error($"抽卡识别失败：{ex.Message}", ex);
            ToastHelper.Error("抽卡识别", $"运行异常：{ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task<GachaCrawler.CrawlReport> RunCrawlAsync(CancellationToken ct)
    {
        var processor = MaaProcessorManager.Instance.Current;
        if (processor?.ViewModel is { IsRunning: true })
            throw new InvalidOperationException("主页队列正在运行，请先停止队列再使用小工具");
        var controller = processor?.GetToolController();
        if (controller == null || !controller.IsConnected)
            throw new GachaCrawler.CrawlException("模拟器未连接：请先在主页连接设备");

        var ocr = ToolsViewModel.GetSharedOcrEngine();
        var crawler = new GachaCrawler(
            () => processor.GetToolController(),
            ocr,
            (msg, level) =>
            {
                // 抓取在后台线程跑，页面集合的更新必须回 UI 线程
                Dispatcher.UIThread.Post(() =>
                {
                    Logs.Add(new ToolLogLine
                    {
                        Time = DateTime.Now.ToString("HH:mm:ss"),
                        Text = msg,
                        Level = level,
                    });
                    StatusText = msg;
                }, DispatcherPriority.Background);
                if (level == WaiLogLevel.Warn)
                    LoggerHelper.Warn(msg);
                else
                    LoggerHelper.Info(msg);
            });
        return await crawler.RunAsync(ct);
    }

    /// <summary>库内全量 → 总览行 + 每池统计卡（抓取/补录/删除/标UP 后重刷）</summary>
    public void RefreshStats(List<GachaStore.Record>? library = null)
    {
        library ??= GachaStore.LoadRecords();
        var upMarks = GachaStore.LoadUpMarks();
        Overview.Clear();
        Pools.Clear();
        var totalWai = 0;
        foreach (var g in library.GroupBy(r => r.Pool))
        {
            // 新→旧
            var rs = g.OrderByDescending(r => r.Ts).ToList();
            var st = GachaStore.Stats(rs);
            var poolUps = upMarks.TryGetValue(g.Key, out var ups) ? ups : new Dictionary<string, string>();
            var up = GachaStore.UpStatistics(rs, poolUps);
            totalWai += up.WaiCount;

            // 顶部总览小卡
            Overview.Add(new OverviewCardView
            {
                Pool = g.Key,
                Short = g.Key.RemoveSuffix("渠道"),
                Total = st.Total.ToString(),
                TeLine = up.Marked
                    ? $"出卡 {up.TeCount - up.WaiCount}" + (up.WaiCount > 0 ? $" · 歪 {up.WaiCount}" : "")
                    : $"出卡 {st.TeCount}",
                HasWai = up.Marked && up.WaiCount > 0,
            });

            var card = new PoolStatsView
            {
                Owner = this,
                Pool = g.Key,
                StatsLine = $"总抽数 {st.Total} · 特出 {st.TeCount} · 平均 {st.AvgText}",
                UpLine = up.Marked
                    ? $"UP 出卡 {up.TeCount - up.WaiCount} · 歪 {up.WaiCount} · UP 平均 {up.UpAvgText}"
                    : "",
                Dian = st.Dian,
            };

            // 特出按小类（招集列）分组，组内消耗明细——对标手机端子卡；标注 UP 的组加歪数
            string KeyOf(GachaStore.Record x) => string.IsNullOrEmpty(x.Banner) ? "未识别" : x.Banner;
            var banners = new Dictionary<string, BannerStatsView>();
            foreach (var r in rs)
            {
                var key = KeyOf(r);
                if (banners.ContainsKey(key)) continue;
                var group = rs.Where(x => KeyOf(x) == key).ToList();
                var groupTe = group.Count(x => x.Rarity == GachaStore.RarityTop);
                var upName = poolUps.TryGetValue(key, out var u) && !string.IsNullOrWhiteSpace(u) ? u : null;
                var groupWai = upName != null ? group.Count(x => x.Rarity == GachaStore.RarityTop && x.Name != upName) : 0;
                var bv = new BannerStatsView
                {
                    Raw = key,
                    Label = key == "未识别" ? "未识别" : key.Split('/')[^1],
                    HeadLine = $"『{key.Split('/')[^1]}』 · {group.Count} 抽 · 特出 {groupTe}"
                               + (upName != null
                                   ? (groupWai > 0 ? $" · UP {upName} · 歪 {groupWai}" : $" · UP {upName} · 没歪")
                                   : ""),
                };
                banners[key] = bv;
            }
            var te = GachaStore.TeListWithCost(rs);
            foreach (var (rec, cost) in te)
            {
                var key = string.IsNullOrEmpty(rec.Banner) ? "未识别" : rec.Banner;
                var isUp = poolUps.TryGetValue(key, out var markedUp) && markedUp == rec.Name;
                banners[key].TeList.Add(new TeItemView
                {
                    Name = rec.Name,
                    Time = GachaStore.ShortTime(rec.Ts),
                    Cost = cost,
                    IsUp = isUp,
                    Owner = this,
                    Pool = g.Key,
                    Banner = key,
                });
            }
            foreach (var bv in banners.Values) card.Banners.Add(bv);

            foreach (var rec in rs.Where(r => r.Manual))
            {
                var banner = string.IsNullOrEmpty(rec.Banner) ? "" : $" · {rec.Banner}";
                card.ManualList.Add(new ManualItemView
                {
                    Uid = rec.Uid,
                    Display = $"{rec.Name} · {GachaStore.ShortTime(rec.Ts)}{banner} · {rec.Rarity}",
                    Owner = this,
                });
            }
            Pools.Add(card);
        }
        // 合计卡放总览行首位
        if (Pools.Count > 0)
        {
            var allTotal = library.Count;
            var allTe = library.Count(r => r.Rarity == GachaStore.RarityTop);
            var markedAny = upMarks.Values.Any(m => m.Values.Any(v => !string.IsNullOrWhiteSpace(v)));
            Overview.Insert(0, new OverviewCardView
            {
                Pool = "合计",
                Short = "合计",
                Total = allTotal.ToString(),
                TeLine = markedAny
                    ? $"出卡 {allTe - totalWai}" + (totalWai > 0 ? $" · 歪 {totalWai}" : "")
                    : $"出卡 {allTe}",
                HasWai = markedAny && totalWai > 0,
            });
        }
        HasResult = Pools.Count > 0;
        RefreshSummary();
    }

    /// <summary>标注/取消某小类的 UP 器者（再点同一人 = 取消）</summary>
    public void ToggleUp(string pool, string banner, string name)
    {
        if (string.IsNullOrEmpty(banner)) return;
        var up = GachaStore.ToggleUpMark(pool, banner, name);
        ToastHelper.Info("UP 标注", string.IsNullOrEmpty(up)
            ? $"已取消 {pool}「{banner}」的 UP"
            : $"已标注 {pool}「{banner}」UP = {up}");
        RefreshStats();
    }

    public void DeleteManual(string uid)
    {
        if (GachaStore.DeleteManualRecord(uid))
        {
            ToastHelper.Success("已删除", "手动补录的记录已移除");
            RefreshStats();
        }
    }

    private async Task ConfirmAddAsync()
    {
        var name = FormName.Trim();
        if (name.Length == 0)
        {
            ToastHelper.Warn("补录失败", "器者名不能为空");
            return;
        }
        if (!TryParseManualTime(FormTime.Trim(), out var ts))
        {
            ToastHelper.Warn("补录失败", "时间格式不对：用 2026-08-01 10:30 或 2026/8/1 10:30");
            return;
        }
        try
        {
            var pool = FormPool;
            await Task.Run(() => GachaStore.AddManualRecord(pool, FormBanner.Trim(), name, FormRarity, ts));
            IsAddFormVisible = false;
            FormBanner = FormName = FormTime = string.Empty;
            RefreshStats();
            ToastHelper.Success("补录成功", $"{name}（{pool}）已入库");
        }
        catch (Exception ex)
        {
            ToastHelper.Error("补录失败", ex.Message);
        }
    }

    /// <summary>手动补录时间解析：2026-08-01 10:30 / 2026/8/1 10:30 / 2026-08-01（默认 12:00）</summary>
    private static bool TryParseManualTime(string text, out long tsMinutes)
    {
        tsMinutes = 0;
        if (text.Length == 0) return false;
        var formats = new[] { "yyyy-MM-dd HH:mm", "yyyy/M/d H:mm", "yyyy-MM-dd", "yyyy/M/d" };
        if (DateTime.TryParseExact(text, formats, null, System.Globalization.DateTimeStyles.None, out var dt))
        {
            if (text.Length <= 10) dt = dt.AddHours(12);
            tsMinutes = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)).ToUnixTimeSeconds() / 60;
            return true;
        }
        return false;
    }

}

/// <summary>字符串扩展（面板展示用）</summary>
public static class ToolStringExtensions
{
    public static string RemoveSuffix(this string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.Ordinal) ? s[..^suffix.Length] : s;
}
