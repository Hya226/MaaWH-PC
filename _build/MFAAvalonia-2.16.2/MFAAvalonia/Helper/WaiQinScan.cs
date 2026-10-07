using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MaaFramework.Binding;

namespace MFAAvalonia.Helper;

/// <summary>扫描日志级别（页面按级别着色）</summary>
public enum WaiLogLevel
{
    Info,
    Warn,
    Success,
}

/// <summary>
/// 外勤见闻识别（小工具）：游戏停在【外勤见闻】列表页（模拟器内，用户预先手动进入），
/// 本类接管扫描循环。纯只读 OCR + 翻页点击，与主队列任务无关。
/// 手机端 WaiQinScan.kt 的移植：抓帧走 MaaController（原 Shizuku 虚拟屏），
/// 翻页点击走 controller.Click（原 injectTapVD），OCR 走 OcrEngine（同款 PP-OCRv4 rec 模型）。
///
/// 流程：抓帧等稳 → 裁左右两个标题框 OCR → 归一后对名单匹配（精确 → 编辑距离≤2 唯一
/// 最近邻）→ 命中置 1（内存 Set，任务结束丢弃，每次运行从全 0 开始）→ 点 (1161,352) 翻页。
/// 终止：本页两个名字与上一页完全一致累计 3 次（= 翻到底/点击失效）；最后一页只有左
/// 记录（名单奇数条时的常态）按「右框空白 + 左框同一条目连续 3 次」同样正常收尾。
/// 兜底：连续 3 页识别不完整（两框都失败/左框失败）判 OCR/页面异常退出；MAX_PAGES 防死循环。
/// 结束输出名单中本轮没出现的条目（带器者括注）到日志 + logs/waiqin_missing.txt。
///
/// 名单在发行包 assets/waiqin/roster.json（对应手机端 assets 同名文件）；坐标全部
/// 1280x720 基准，MuMu 与手机虚拟屏同分辨率直接复用。
/// 名单匹配阈值依据：名单两两编辑距离最小为 3，≤2 的纠错永不歧义。
/// </summary>
public sealed class WaiQinScan
{
    public sealed class ScanException : Exception
    {
        public ScanException(string message) : base(message) { }
    }

    public sealed record Entry(string Name, string Who)
    {
        /// <summary>展示形式「见闻名（器者）」；无器者时只报名字</summary>
        public string Display() => string.IsNullOrEmpty(Who) ? Name : $"{Name}（{Who}）";
    }

    private readonly record struct Box(int X, int Y, int W, int H);

    // ---- 几何（1280x720 帧，用户框定；翻页箭头右侧中部）----
    private static readonly Box BoxL = new(140, 110, 493, 59);
    private static readonly Box BoxR = new(654, 112, 488, 56);
    /// <summary>两框包围盒：翻页后等画面稳定用</summary>
    private static readonly Box BoxesBand = new(140, 106, 1002, 67);
    private const int NextPageX = 1161;
    private const int NextPageY = 352;

    /// <summary>与上一页完全一致累计到此即判翻到底（双框完整页与"右框空白"的单侧页共用）</summary>
    private const int MatchesToEnd = 3;
    /// <summary>连续识别不完整页到此判异常（只数两框都失败/左框失败；右框空白是最后一页常态，不算）</summary>
    private const int BadPagesToAbort = 3;
    /// <summary>42 条/每页 2 条 = 21 页 + 重合 3 页 + 余量</summary>
    private const int MaxPages = 30;

    private readonly Func<MaaController?> _controllerGetter;
    private readonly Action<string, WaiLogLevel> _onLog;
    private readonly OcrEngine _ocr;

    private List<Entry> _roster = [];
    private List<string> _normNames = [];

    public WaiQinScan(Func<MaaController?> controllerGetter, OcrEngine ocr, Action<string, WaiLogLevel> onLog)
    {
        _controllerGetter = controllerGetter;
        _ocr = ocr;
        _onLog = onLog;
    }

    /// <summary>一次扫描的统计结果（供页面渲染）</summary>
    public sealed record ScanOutcome(int RosterCount, int SeenCount, IReadOnlyList<Entry> Missing);

    /// <summary>
    /// 主入口：正常扫完返回统计结果（含缺失清单）；用户停止抛 OperationCanceledException。
    /// 异常抛 ScanException（页面/OCR/名单问题）。
    /// </summary>
    public async Task<ScanOutcome> RunAsync(CancellationToken ct)
    {
        LoadRoster();
        var seen = new HashSet<int>();
        _onLog($"外勤见闻识别：名单 {_roster.Count} 条，开始扫描当前见闻列表…", WaiLogLevel.Info);

        var prevL = -1;
        var prevR = -1;
        var matchRun = 0;
        var badRun = 0;
        // 单侧页（右框空白，最后一页常态）的重合状态：上一轮左框命中的条目 + 连续计数，
        // 与完整页的 prevL/prevR/matchRun 互斥维护（两种页面形态各自计各自的）
        var prevSingle = -1;
        var singleMatchRun = 0;
        var pages = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var frame = GrabStable(ct)
                ?? throw new ScanException("抓帧失败：模拟器连接是否还在？（先在连接设置重连）");
            pages++;
            if (pages > MaxPages)
            {
                throw new ScanException($"翻页超过 {MaxPages} 页仍未终止，已中止（页面结构或翻页坐标是否变化？）");
            }

            var (li, lraw) = OcrBox(frame, BoxL);
            var (ri, rraw) = OcrBox(frame, BoxR);

            // PC 端截图比手机端快：翻页动画未完成时会抓到中间帧——右框空白（左框已稳、
            // 右框还在滑入）、左框空白、或翻页未生效的旧页（与上页相同）。可疑帧先做
            // 一次确认帧：重抓重识别，确认帧给出完整页且与首帧不同则采用；真到底/
            // 真单侧页时确认帧判定一致，维持原判。
            var suspicious = li < 0 || ri < 0 || (li == prevL && ri == prevR);
            if (suspicious)
            {
                await Task.Delay(500, ct);
                var confirm = GrabStable(ct);
                if (confirm != null)
                {
                    var (cli2, craw2) = OcrBox(confirm, BoxL);
                    var (cri2, craw2r) = OcrBox(confirm, BoxR);
                    if (cli2 >= 0 && cri2 >= 0 && (cli2 != li || cri2 != ri))
                    {
                        _onLog($"第 {pages} 页为翻页中间帧，确认后采用：{_roster[cli2].Name}、{_roster[cri2].Name}", WaiLogLevel.Warn);
                        li = cli2;
                        ri = cri2;
                        lraw = craw2;
                        rraw = craw2r;
                    }
                }
            }

            if (li >= 0) seen.Add(li);
            if (ri >= 0) seen.Add(ri);

            if (li < 0 && ri < 0)
            {
                if (seen.Count == 0)
                {
                    throw new ScanException(
                        $"首页两框都匹配不到名单内见闻——请确认已停在【外勤见闻】列表页；" +
                        $"若确认在页，则是名单缺新见闻（识别原文：左「{lraw}」右「{rraw}」）");
                }
                badRun++;
                if (badRun >= BadPagesToAbort)
                {
                    throw new ScanException($"连续 {badRun} 页无法识别，已中止（OCR 异常或页面异常）");
                }
                _onLog($"第 {pages} 页两框都识别失败（左「{lraw}」右「{rraw}」），跳过继续（{badRun}/{BadPagesToAbort}）", WaiLogLevel.Warn);
                prevL = -1; prevR = -1; matchRun = 0; prevSingle = -1;
            }
            else if (li < 0)
            {
                // 左框失败右框成功：左框是主识别区，这不是"最后一页只有左记录"的形态
                // （列表从左往右填，末页不会有"只有右记录"），大概率框位/OCR 异常，保留兜底
                badRun++;
                if (badRun >= BadPagesToAbort)
                {
                    throw new ScanException($"连续 {badRun} 页左框识别失败（右框「{rraw}」），已中止（框位或名单是否需要更新？）");
                }
                _onLog($"第 {pages} 页左框识别失败（右「{rraw}」），本页不计重合，继续（{badRun}/{BadPagesToAbort}）", WaiLogLevel.Warn);
                prevL = -1; prevR = -1; matchRun = 0; prevSingle = -1;
            }
            else if (ri < 0)
            {
                // 右框空白：名单奇数条时最后一页只有左边有记录，是正常页面形态——
                // 左框连续命中同一条目（= 翻页点击已无效，到底了）即正常收尾，不计 badRun。
                // 右侧若 OCR 出了字但匹配不上名单（rraw 非空），日志带上原文便于发现名单缺新见闻
                badRun = 0;
                singleMatchRun = li == prevSingle ? singleMatchRun + 1 : 0;
                prevSingle = li;
                var rnote = rraw.Length == 0 ? "右侧无记录" : $"右侧「{rraw}」未匹配名单";
                if (singleMatchRun >= MatchesToEnd)
                {
                    _onLog($"第 {pages} 页{rnote}（左：{_roster[li].Name}），单侧重合 " +
                           $"{singleMatchRun}/{MatchesToEnd}——已到最后一页，扫描收尾", WaiLogLevel.Info);
                    break;
                }
                _onLog($"第 {pages} 页{rnote}（左：{_roster[li].Name}），单侧重合 {singleMatchRun}/{MatchesToEnd}", WaiLogLevel.Info);
            }
            else
            {
                badRun = 0;
                prevSingle = -1;
                var same = li == prevL && ri == prevR;
                if (same)
                {
                    matchRun++;
                    _onLog($"第 {pages} 页与上一页相同（{_roster[li].Name}、{_roster[ri].Name}），重合 {matchRun}/{MatchesToEnd}", WaiLogLevel.Info);
                }
                else
                {
                    matchRun = 0;
                    _onLog($"第 {pages} 页：{_roster[li].Name}、{_roster[ri].Name}", WaiLogLevel.Info);
                }
                if (matchRun >= MatchesToEnd) break;
                prevL = li;
                prevR = ri;
            }

            Click(NextPageX, NextPageY, ct);
            await Task.Delay(500, ct);   // 让翻页动画先跑起来，GrabStable 会等画面真正静止
        }

        return Report(seen);
    }

    // ---------------- 名单与匹配 ----------------

    /// <summary>随包名单直读 assets（装包即最新）</summary>
    private void LoadRoster()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "waiqin", "roster.json");
        if (!File.Exists(path))
            throw new ScanException($"名单读取失败：{path} 不存在");
        List<Entry> list;
        try
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(File.ReadAllText(path));
            list = arr?.Select(o => new Entry(
                    o.TryGetProperty("name", out var n) ? n.GetString()?.Trim() ?? "" : "",
                    o.TryGetProperty("who", out var w) ? w.GetString()?.Trim() ?? "" : ""))
                .Where(e => e.Name.Length > 0)
                .ToList() ?? [];
        }
        catch (Exception e)
        {
            throw new ScanException($"名单解析失败：assets/waiqin/roster.json 不是合法 JSON（{e.Message}）");
        }
        if (list.Count == 0)
            throw new ScanException("名单为空：assets/waiqin/roster.json 没有有效条目");
        _roster = list;
        _normNames = list.Select(e => Normalize(e.Name)).ToList();
    }

    /// <summary>
    /// 归一：全角→半角、转小写、去空白与引号类字符（名单「大侠在民间」已去引号，
    /// OCR 侧引号识别不稳，两侧都剔掉双保险）。
    /// </summary>
    private static string Normalize(string s)
    {
        const string quotes = "\"\uFF02\u201C\u201D\u301D\u301E\'\u2018\u2019\u2032\u300C\u300D\u300E\u300F\u300A\u300B\u3010\u3011\uFF03#";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var code = ch;
            if (code == '\u3000')
                code = ' ';
            else if (code >= '\uFF01' && code <= '\uFF5E')
                code = (char)(code - 0xFEE0);
            if (code == ' ' || quotes.Contains(code))
                continue;
            sb.Append(char.ToLowerInvariant(code));
        }
        return sb.ToString();
    }

    /// <summary>裁标题框 → OCR → 名单匹配。返回 (名单下标或 -1, 识别原文)</summary>
    private (int index, string raw) OcrBox(ToolFrame frame, Box b)
    {
        var y = Math.Max(0, b.Y - 4);
        var h = Math.Min(b.H + 8, frame.Height - y);
        if (b.X + b.W > frame.Width || h <= 0)
            return (-1, "");
        var line = _ocr.Recognize(frame.Bgra, frame.Width, frame.Height, b.X, y, b.W, h);
        var raw = line.Text.Trim();
        if (raw.Length == 0)
            return (-1, "");
        var t = Normalize(raw);
        if (t.Length == 0)
            return (-1, raw);
        var exact = _normNames.IndexOf(t);
        if (exact >= 0)
            return (exact, raw);
        // 编辑距离 ≤2 的唯一最近邻（名单两两最小距离 3，不会归错条目）
        var best = -1;
        var bestD = int.MaxValue;
        var tie = false;
        for (var i = 0; i < _normNames.Count; i++)
        {
            var d = Levenshtein(t, _normNames[i]);
            if (d < bestD)
            {
                bestD = d;
                best = i;
                tie = false;
            }
            else if (d == bestD)
            {
                tie = true;
            }
        }
        return bestD is >= 1 and <= 2 && !tie ? (best, raw) : (-1, raw);
    }

    /// <summary>编辑距离（GachaDictionary.levenshtein 同款）</summary>
    public static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // ---------------- 抓帧与稳定检测 ----------------

    /// <summary>抓一帧并等标题区连两帧哈希一致（页面静止）；超时返回最后一帧</summary>
    private ToolFrame? GrabStable(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 5000;
        ToolFrame? last = null;
        var prev = -1;
        var has = false;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var f = FrameGrabber.Grab(_controllerGetter());
            if (f == null)
            {
                Thread.Sleep(150);
                continue;
            }
            var h = FrameGrabber.RegionHash(f, new ToolRect(BoxesBand.X, BoxesBand.Y, BoxesBand.W, BoxesBand.H));
            if (has && h == prev)
                return f;
            prev = h;
            has = true;
            last = f;
            Thread.Sleep(220);
        }
        return last;
    }

    private void Click(int x, int y, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var controller = _controllerGetter()
            ?? throw new ScanException("控制器不可用：模拟器连接是否还在？");
        controller.Click(x, y).Wait();
    }

    // ---------------- 输出 ----------------

    private ScanOutcome Report(HashSet<int> seen)
    {
        var missing = _roster
            .Select((e, i) => (e, i))
            .Where(p => !seen.Contains(p.i))
            .Select(p => p.e)
            .ToList();
        _onLog($"扫描完成：名单 {_roster.Count} 条 · 本轮出现 {seen.Count} 条 · 未出现 {missing.Count} 条", WaiLogLevel.Success);
        if (missing.Count == 0)
        {
            _onLog("名单内见闻本轮全部出现，没有缺失", WaiLogLevel.Success);
        }
        else
        {
            _onLog($"—— 未出现的见闻（{missing.Count} 条）——", WaiLogLevel.Info);
            for (var i = 0; i < missing.Count; i++)
                _onLog($"{i + 1}. {missing[i].Display()}", WaiLogLevel.Info);
        }
        try
        {
            var f = Path.Combine(AppPaths.LogsDirectory, "waiqin_missing.txt");
            File.WriteAllText(f, string.Join("\n", missing.Select(e => e.Display())) + "\n");
            _onLog($"已写入 {f}", WaiLogLevel.Info);
        }
        catch (Exception e)
        {
            _onLog($"waiqin_missing.txt 写入失败：{e.Message}", WaiLogLevel.Warn);
        }
        return new ScanOutcome(_roster.Count, seen.Count, missing);
    }
}
