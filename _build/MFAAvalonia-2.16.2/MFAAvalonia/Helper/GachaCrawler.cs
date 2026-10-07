using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MaaFramework.Binding;
using Newtonsoft.Json.Linq;

namespace MFAAvalonia.Helper;

/// <summary>
/// 抽卡记录爬虫：游戏停在「招集记录」页（模拟器内），本类接管取数循环。
/// 手机端 GachaCrawler.kt 的移植：行定位/取色/翻页判定全部在本层做（固定坐标 +
/// 像素分析，assets/gacha/points.json 提供几何），OCR 走 OcrEngine，
/// 点击/截帧走 MaaController（原 Shizuku 虚拟屏）。
///
/// 停止条件三重保险：锚点命中 / 空页（连续两帧无行带）/ 翻页后表格区域画面不变
/// （= 最后一页）。锚点截断失败（记录过期 &gt;30 天）自然退化为全量抓取，
/// GachaStore.CommitCrawl 按 uid 去重合并。
/// </summary>
public sealed class GachaCrawler
{
    public sealed class CrawlException : Exception
    {
        public CrawlException(string message) : base(message) { }
    }

    /// <summary>抓取结果：新增条数 + 每池新增 + 收口后的库内全量（统计展示用）</summary>
    public sealed record CrawlReport(int Added, Dictionary<string, int> PerPool, List<GachaStore.Record> Library);

    private readonly Func<MaaController?> _controllerGetter;
    private readonly OcrEngine _ocr;
    private readonly Action<string, WaiLogLevel> _onLog;

    // ---------------- points.json ----------------

    public sealed class Points
    {
        public sealed class Pt
        {
            public int X, Y;
        }

        public List<string> Pools = [];
        public Pt Dropdown = new();
        public Dictionary<string, Pt> PoolOption = [];
        public Pt NextPage = new();
        public Pt PageOne = new();
        public ToolRect NameCol, TimeCol, RarityStrip, FullRegion, PagerStrip;
        public ToolRect? ZhaoCol;
        public Dictionary<string, int[]> Colors = [];
        public int AfterNextMs = 400, AfterDropdownMs = 500, AfterPoolSwitchMs = 600;
        public int Upscale = 2;

        public static Points Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "gacha", "points.json");
            if (!File.Exists(path))
                throw new CrawlException($"缺少配置 {path}");
            // 允许 // 注释行/行尾注释，读取时剥掉
            var clean = string.Join("\n", File.ReadAllLines(path).Select(l => l.Split("//")[0]));
            JToken root;
            try
            {
                root = JObject.Parse(clean);
            }
            catch (Exception e)
            {
                throw new CrawlException($"points.json 解析失败：{e.Message}");
            }
            var p = new Points();
            var click = root["click"]!;
            p.Pools = root["pools"]!.Select(t => (string)t!).ToList();
            p.Dropdown = PtOf(click["dropdown"]!);
            if (click["poolOption"] is JObject opts)
            {
                foreach (var (key, token) in opts)
                    p.PoolOption[key] = PtOf(token!);
            }
            p.NextPage = PtOf(click["nextPage"]!);
            p.PageOne = PtOf(click["pageOne"]!);
            var crop = root["crop"]!;
            p.NameCol = RectOf(crop["nameCol"]!);
            p.TimeCol = RectOf(crop["timeCol"]!);
            p.ZhaoCol = crop["zhaoCol"] is { } zhao
                ? new ToolRect((int)zhao["x"]!, (int)zhao["y"]!, (int)zhao["w"]!, (int)zhao["h"]!)
                : null;
            p.RarityStrip = RectOf(crop["rarityStrip"]!);
            p.FullRegion = RectOf(root["table"]!["fullRegion"]!);
            if (root["pagerStrip"] is { } pager)
                p.PagerStrip = new ToolRect((int)pager["x"]!, (int)pager["y"]!, (int)pager["w"]!, (int)pager["h"]!);
            if (root["colors"] is JObject colors)
            {
                foreach (var (key, token) in colors)
                    p.Colors[key] = ((JArray)token!).Select(t => (int)t!).ToArray();
            }
            if (root["timings"] is { } timings)
            {
                p.AfterNextMs = (int?)timings["afterNextMs"] ?? 400;
                p.AfterDropdownMs = (int?)timings["afterDropdownMs"] ?? 500;
                p.AfterPoolSwitchMs = (int?)timings["afterPoolSwitchMs"] ?? 600;
            }
            if (root["ocr"]?["upscale"] is { } up)
                p.Upscale = (int)up;
            return p;
        }

        private static Pt PtOf(JToken t) => new() { X = (int)t[0]!, Y = (int)t[1]! };

        private static ToolRect RectOf(JToken t) =>
            new((int)t["x"]!, (int)t["y"]!, (int)t["w"]!, (int)t["h"]!);
    }

    /// <summary>稀有度竖条里扫出的一行</summary>
    private readonly record struct RowBand(int Y1, int Y2, int R, int G, int B);

    public GachaCrawler(Func<MaaController?> controllerGetter, OcrEngine ocr, Action<string, WaiLogLevel> onLog)
    {
        _controllerGetter = controllerGetter;
        _ocr = ocr;
        _onLog = onLog;
    }

    // ---------------- 主流程 ----------------

    public Task<CrawlReport> RunAsync(CancellationToken ct) => Task.Run(() => Crawl(ct), ct);

    private CrawlReport Crawl(CancellationToken ct)
    {
        var pts = Points.Load();
        ResetNameDict();   // 名单可能刚编辑过
        _onLog("抽卡记录抓取开始", WaiLogLevel.Info);
        // 不做"当前页必须有行"的预检：当前池子可能是空的。页面正确性由
        // SwitchPool 后的 VerifyPool（OCR 下拉框文字）把关，行扫描只管取数。

        var cfg = GachaStore.LoadConfig();
        if (cfg.IsFirstRun) _onLog("首次运行（无锚点）：全量抓取所有池子", WaiLogLevel.Info);
        var fresh = new Dictionary<string, List<GachaStore.Record>>();
        var perPool = new Dictionary<string, int>();
        // banner 纠错参照集：库内已知写法 + 本会话增量。招集列没有字典纠错，
        // 「万嶂烟峦」OCR 错一字成「万蟑烟峦」会在面板裂成两个卡池，就地归并。
        var bannerDict = new Dictionary<string, HashSet<string>>();
        foreach (var r in GachaStore.LoadRecords())
        {
            if (r.Banner.Length > 0)
            {
                if (!bannerDict.TryGetValue(r.Pool, out var set))
                    bannerDict[r.Pool] = set = [];
                set.Add(r.Banner);
            }
        }

        foreach (var pool in pts.Pools)
        {
            ct.ThrowIfCancellationRequested();
            SwitchPool(pool, pts, ct);
            VerifyPool(pool, pts, ct);
            var n = CrawlPool(pool, pts, cfg, fresh, bannerDict, ct);
            perPool[pool] = n;
            _onLog($"池[{pool}] 完成：新增 {n} 条", WaiLogLevel.Success);
        }

        var added = GachaStore.CommitCrawl(fresh);
        _onLog($"抓取收口：新增 {added} 条，{string.Join("，", perPool.Select(kv => $"{kv.Key} +{kv.Value}"))}", WaiLogLevel.Success);
        return new CrawlReport(added, perPool, GachaStore.LoadRecords());
    }

    /// <summary>一个池子：翻页循环 + 锚点截断，返回新增条数</summary>
    private int CrawlPool(
        string pool,
        Points pts,
        GachaStore.Config cfg,
        Dictionary<string, List<GachaStore.Record>> fresh,
        Dictionary<string, HashSet<string>> bannerDict,
        CancellationToken ct)
    {
        List<GachaStore.Anchor> anchors = cfg.Anchors.TryGetValue(pool, out var a) ? a : [];
        var seqMap = new Dictionary<long, int>();
        var seenPages = new List<List<(long ts, string rarity, string name)>>();
        List<(long ts, string rarity, string name)>? lastPage = null;
        var missed = 0;
        var added = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // banner 在入库前就近归并（uid 不含 banner）
            var rows = ScanPage(pool, pts, seqMap, ct)
                .Select(r => r.Banner.Length == 0 ? r : r with { Banner = NormalizeBanner(pool, r.Banner, bannerDict) })
                .ToList();
            if (rows.Count == 0)
            {
                _onLog($"池[{pool}] 无记录行，本池到底", WaiLogLevel.Info);
                break;
            }
            // 页面指纹 = 前 3 行（时间+稀有度+名字），名字比对容忍 OCR 小误差。
            var head = rows.Take(3).Select(r => (r.Ts, r.Rarity, r.Name)).ToList();
            if (lastPage != null && PageMatch(head, lastPage))
            {
                missed++;
                if (missed >= 3)
                {
                    _onLog($"池[{pool}] 连续 {missed} 次内容未变化——判定已到最后一页", WaiLogLevel.Info);
                    break;
                }
                _onLog($"池[{pool}] 翻页未生效（{missed}/3），重试", WaiLogLevel.Warn);
                ClickNextPage(pts, ct);
                continue;
            }
            if (seenPages.Any(it => PageMatch(head, it)))
            {
                _onLog($"池[{pool}] 绕回了已抓过的页面——全部记录页已覆盖", WaiLogLevel.Info);
                break;
            }
            seenPages.Add(head);
            lastPage = head;
            missed = 0;
            var hit = false;
            foreach (var r in rows)
            {
                if (!cfg.IsFirstRun && HitsAnchor(r, anchors))
                {
                    _onLog($"池[{pool}] 命中锚点（{r.Time} {r.Name}），停止翻页", WaiLogLevel.Info);
                    hit = true;
                    break;
                }
                if (!fresh.TryGetValue(pool, out var list))
                    fresh[pool] = list = [];
                list.Add(r);
                added++;
            }
            if (hit) break;
            if (seenPages.Count >= MaxPages)
            {
                _onLog($"池[{pool}] 已扫 {seenPages.Count} 页仍未命中锚点（30 天前的记录已过期？），按全量收尾", WaiLogLevel.Warn);
                break;
            }
            ClickNextPage(pts, ct);
        }
        return added;
    }

    /// <summary>页面指纹比对：行数一致，且每行时间、稀有度相同，名字编辑距离 ≤2</summary>
    private static bool PageMatch(
        List<(long ts, string rarity, string name)> a,
        List<(long ts, string rarity, string name)> b)
    {
        if (a.Count != b.Count || a.Count == 0) return false;
        return a.Zip(b, (x, y) =>
            x.ts == y.ts && x.rarity == y.rarity &&
            (x.name == y.name || WaiQinScan.Levenshtein(x.name, y.name) <= 2)).All(ok => ok);
    }

    /// <summary>
    /// 锚点命中：同一分钟 + 名字相近（OCR 单行偶发掉字/变形）。短名（≤3字）容差 1 字，长名容差 2 字。
    /// </summary>
    private static bool HitsAnchor(GachaStore.Record r, List<GachaStore.Anchor> anchors) =>
        anchors.Any(a => a.T == r.Ts && (
            a.N == r.Name ||
            WaiQinScan.Levenshtein(a.N, r.Name) <= (Math.Min(a.N.Length, r.Name.Length) <= 3 ? 1 : 2)));

    /// <summary>
    /// 扫描当前页 → 行记录。内部含两个自适应：
    /// - 空页判定：连续两帧稀有度竖条都无行带才算空页（防公告/加载动画误判）；
    /// - 单页重试：有行但时间解析失败 ≥1 行时重抓一帧重识别一次（JPEG 抖动/遮挡）。
    /// </summary>
    private List<GachaStore.Record> ScanPage(
        string pool, Points pts, Dictionary<long, int> seqMap, CancellationToken ct)
    {
        var emptyStrikes = 0;
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var frame = GrabStable(pts, ct) ?? throw new CrawlException("抓帧失败：模拟器连接是否还在？");
            var bands = DetectRows(frame, pts);
            if (bands.Count == 0)
            {
                emptyStrikes++;
                if (emptyStrikes >= 2) return [];
                Thread.Sleep(700);
                continue;
            }
            var bad = 0;
            var badTime = false;
            var raws = new List<GachaStore.RawRow>(bands.Count);
            foreach (var b in bands)
            {
                var rarity = ClassifyRarity(b, pts.Colors);
                if (rarity == null)
                {
                    _onLog($"行 y={(b.Y1 + b.Y2) / 2} 稀有度取色异常（RGB {b.R},{b.G},{b.B}），跳过", WaiLogLevel.Warn);
                    bad++;
                    continue;
                }
                var nameRaw = OcrLine(frame, pts.NameCol.X, b.Y1, pts.NameCol.W, b.Y2 - b.Y1, pts.Upscale, BinarizeThreshold);
                var (name, trusted) = CorrectName(nameRaw);
                if (nameRaw.Length > 0 && !trusted)
                    _onLog($"名字「{nameRaw}」不在名单内（保留原值）", WaiLogLevel.Warn);
                if (nameRaw.Length == 0)
                {
                    _onLog($"行 y={(b.Y1 + b.Y2) / 2} 名字识别为空，跳过", WaiLogLevel.Warn);
                    bad++;
                    continue;
                }
                var timeRaw = OcrLine(frame, pts.TimeCol.X, b.Y1, pts.TimeCol.W, b.Y2 - b.Y1, pts.Upscale, BinarizeThreshold);
                var ts = ParseTime(timeRaw);
                if (ts == null)
                {
                    _onLog($"时间解析失败：\"{timeRaw}\"，跳过该行", WaiLogLevel.Warn);
                    bad++;
                    badTime = true;
                    continue;
                }
                // 招集列（小类）：识别失败不影响本行入库，仅归到"未识别"组
                var banner = pts.ZhaoCol is { } zhao
                    ? OcrLine(frame, zhao.X, b.Y1, zhao.W, b.Y2 - b.Y1, pts.Upscale, BinarizeThreshold).Trim()
                    : "";
                raws.Add(new GachaStore.RawRow(ts.Value, timeRaw, name, rarity, banner));
            }
            if (bad == 0 || attempt >= 1)
                return GachaStore.BuildRecords(pool, raws, seqMap);
            if (badTime)
            {
                // 时间列识别失败最常见的原因：下拉面板没收起、遮住了时间列——先点面板外收起再重抓整页
                _onLog($"存在时间解析失败：点 ({PanelDismiss.X},{PanelDismiss.Y}) 关闭可能未收起的下拉面板，重新识别本页", WaiLogLevel.Warn);
                Tap(PanelDismiss, ct);
                Thread.Sleep(pts.AfterDropdownMs);
                WaitStableRegion(pts, ct);
            }
            _onLog($"本页 {bad} 行识别异常，重抓一帧重试", WaiLogLevel.Warn);
            attempt++;
            Thread.Sleep(500);
        }
    }

    /**
     * banner 就近归并：参照集（库内已知 + 本会话已见）里取容差内最近者；
     * 没有参照时登记为新写法，后续行向它归并。
     */
    private string NormalizeBanner(string pool, string raw, Dictionary<string, HashSet<string>> dict)
    {
        if (!dict.TryGetValue(pool, out var known))
            dict[pool] = known = [];
        string? best = null;
        var bestD = int.MaxValue;
        foreach (var it in known)
        {
            if (!GachaStore.BannerMatch(raw, it)) continue;
            var d = WaiQinScan.Levenshtein(raw, it);
            if (d < bestD)
            {
                bestD = d;
                best = it;
            }
        }
        if (best != null) return best;
        known.Add(raw);
        return raw;
    }

    // ---------------- 像素分析 ----------------

    /**
     * 稀有度竖条 → 行带。饱和色像素（稀有度文字是竖条里唯一的彩色来源）按 y
     * 投影聚类，每带的均值色交给 ClassifyRarity。
     */
    private static List<RowBand> DetectRows(ToolFrame f, Points pts)
    {
        var s = pts.RarityStrip;
        var x1 = Math.Clamp(s.X, 0, f.Width - 1);
        var x2 = Math.Min(s.X + s.W, f.Width);
        var y1 = Math.Clamp(s.Y, 0, f.Height - 1);
        var y2 = Math.Min(s.Y + s.H, f.Height);
        if (x2 <= x1 || y2 <= y1) return [];
        var stride = f.Width * 4;

        var counts = new int[y2 - y1];
        for (var y = y1; y < y2; y++)
        {
            var c = 0;
            for (var x = x1; x < x2; x++)
            {
                var o = y * stride + x * 4;
                var r = f.Bgra[o + 2];
                var g = f.Bgra[o + 1];
                var b = f.Bgra[o];
                var mx = Math.Max(r, Math.Max(g, b));
                var mn = Math.Min(r, Math.Min(g, b));
                if (mx - mn > 50 && mx > 70) c++;
            }
            counts[y - y1] = c;
        }
        // 聚带（间隙 ≤3 行并成一带，带上至少 2 个彩色像素行）
        var bands = new List<RowBand>();
        var start = -1;
        var last = -1;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] >= 2)
            {
                if (start < 0) start = i;
                last = i;
            }
            else if (start >= 0 && i - last > 3)
            {
                bands.Add(ColorOf(f, x1, x2, y1 + start, y1 + last));
                start = -1;
            }
        }
        if (start >= 0) bands.Add(ColorOf(f, x1, x2, y1 + start, y1 + last));
        return bands;
    }

    /// <summary>对已定带重新扫描均值色</summary>
    private static RowBand ColorOf(ToolFrame f, int x1, int x2, int ya, int yb)
    {
        long sr = 0, sg = 0, sb = 0, n = 0;
        var stride = f.Width * 4;
        for (var y = ya; y <= yb; y++)
        {
            for (var x = x1; x < x2; x++)
            {
                var o = y * stride + x * 4;
                var r = f.Bgra[o + 2];
                var g = f.Bgra[o + 1];
                var b = f.Bgra[o];
                var mx = Math.Max(r, Math.Max(g, b));
                var mn = Math.Min(r, Math.Min(g, b));
                if (mx - mn > 50 && mx > 70)
                {
                    sr += r;
                    sg += g;
                    sb += b;
                    n++;
                }
            }
        }
        if (n == 0) return new RowBand(ya, yb, 0, 0, 0);
        return new RowBand(ya, yb, (int)(sr / n), (int)(sg / n), (int)(sb / n));
    }

    /**
     * 均值色 → 稀有度名。按 HSV 色相角就近匹配（基准色在运行时算色相），
     * JPEG/渲染差异只影响明度饱和度，色相稳定。
     * 三个基准的色相：特出≈353°、优异≈52°、新生≈173°，彼此相距 60°+，容差 ±35°。
     */
    private static string? ClassifyRarity(RowBand band, Dictionary<string, int[]> colors)
    {
        var mx = Math.Max(band.R, Math.Max(band.G, band.B));
        var mn = Math.Min(band.R, Math.Min(band.G, band.B));
        if (mx - mn < 25) return null;
        var hue = HueOf(band.R, band.G, band.B);
        if (hue == null) return null;
        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var (name, @ref) in colors)
        {
            var rh = HueOf(@ref[0], @ref[1], @ref[2]);
            if (rh == null) continue;
            // 色相环距离（0~180）
            var d = Math.Abs(hue.Value - rh.Value);
            if (d > 180) d = 360 - d;
            if (d < bestDist)
            {
                bestDist = d;
                best = name;
            }
        }
        return bestDist <= 35 ? best : null;
    }

    /// <summary>色相角 0~360；灰色返回 null</summary>
    private static int? HueOf(int r, int g, int b)
    {
        var mx = Math.Max(r, Math.Max(g, b));
        var mn = Math.Min(r, Math.Min(g, b));
        var d = mx - mn;
        if (d == 0) return null;
        double h;
        if (mx == r) h = 60.0 * (g - b) / d;
        else if (mx == g) h = 120.0 + 60.0 * (b - r) / d;
        else h = 240.0 + 60.0 * (r - g) / d;
        return (int)((h % 360 + 360) % 360);
    }

    // ---------------- OCR 辅助与字典 ----------------

    /// <summary>
    /// 裁一行小图（上下扩 5px）→ 放大 → OCR，取最高分行文本。
    /// binarize：记录页表格是半透明底（MuMu），底层页面文字会透出来——行文字
    /// 按亮度二值化滤掉；下拉按钮等深底浅字控件亮度不足，不能开（传 null）。
    /// </summary>
    private string OcrLine(ToolFrame f, int x, int y, int w, int h, int upscale, int? binarize = null)
    {
        var yy1 = Math.Max(0, y - 5);
        var yy2 = Math.Min(y + h + 5, f.Height);
        var xx2 = Math.Min(x + w, f.Width);
        if (xx2 <= x || yy2 <= yy1) return "";
        return _ocr.Recognize(f.Bgra, f.Width, f.Height, x, yy1, xx2 - x, yy2 - yy1, upscale, binarize).Text.Trim();
    }

    /// <summary>下拉展开面板的区域（判"面板是否还开着"）</summary>
    private static readonly ToolRect DropdownPanel = new(1047, 178, 185, 226);

    /// <summary>下拉面板的「面板外收起」点击点（面板右上外侧空白）</summary>
    private static readonly Points.Pt PanelDismiss = new() { X = 1209, Y = 155 };

    /// <summary>游戏单池最多 18 页左右；60 是保险上限</summary>
    private const int MaxPages = 60;

    /// <summary>行文字主体灰度 ≥151、底层透字大多 ~31；个别底层亮笔画（>150）混入的
    /// 单字前缀交给名字字典纠错（编辑距离 ≤2）消化</summary>
    private const int BinarizeThreshold = 150;

    /** 器者名单（data/gacha/names.json 本地副本 + 随包合并），OCR 纠错字典 */
    private static string[]? _nameDict;

    /// <summary>名单编辑后重置缓存，下次抓取生效</summary>
    public static void ResetNameDict() => _nameDict = null;

    /// <summary>
    /// 名字纠错：编辑距离 ≤2 就近对字典（无并列检查，与手机端一致）。
    /// 返回 (名字, 可信)——可信 = 名单内或纠错成功；超出容差保留原值并提示。
    /// </summary>
    private (string name, bool trusted) CorrectName(string raw)
    {
        if (raw.Length == 0) return (raw, true);
        _nameDict ??= GachaStore.LoadNames().ToArray();
        if (_nameDict!.Length == 0) return (raw, true);
        string best = raw;
        var bestD = int.MaxValue;
        foreach (var n in _nameDict)
        {
            var d = WaiQinScan.Levenshtein(raw, n);
            if (d < bestD)
            {
                bestD = d;
                best = n;
            }
        }
        return bestD is >= 1 and <= 2 ? (best, true) : (raw, bestD == 0);
    }

    /// <summary>时间列正则："2026年9月10日10时46分" → epoch 分钟（本地时区）</summary>
    private static readonly Regex TimeRegex = new(@"(\d{4})年(\d{1,2})月(\d{1,2})日(\d{1,2})时(\d{1,2})分", RegexOptions.Compiled);

    private static long? ParseTime(string text)
    {
        var m = TimeRegex.Match(text);
        if (!m.Success) return null;
        try
        {
            var dt = new DateTime(
                int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), 0);
            return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)).ToUnixTimeSeconds() / 60;
        }
        catch
        {
            return null;
        }
    }


    // ---------------- 切池与翻页 ----------------

    /// <summary>切池后校验下拉按钮文字（OCR 可用时）；对不上说明点错地方了</summary>
    private void VerifyPool(string pool, Points pts, CancellationToken ct)
    {
        var f = GrabStable(pts, ct);
        if (f == null) return;
        // 下拉按钮框：按钮中心 ±（92,17）≈ 实测按钮 184x34
        var text = OcrLine(f, pts.Dropdown.X - 92, pts.Dropdown.Y - 17, 184, 34, pts.Upscale);
        if (text.Length == 0) return; // 识别失败：不阻塞流程
        if (!text.Contains(pool.Length >= 2 ? pool[..2] : pool))
        {
            throw new CrawlException($"切池校验失败：下拉框显示「{text}」，期望「{pool}」。请核对 points.json 的 poolOption 坐标");
        }
    }

    private void SwitchPool(string pool, Points pts, CancellationToken ct)
    {
        Tap(pts.Dropdown, ct);
        Thread.Sleep(pts.AfterDropdownMs);
        WaitStableRegion(pts, ct);
        if (!pts.PoolOption.TryGetValue(pool, out var opt))
            throw new CrawlException($"points.json 缺少池子「{pool}」的选项坐标（poolOption）");
        // 面板区域快照：点选项后面板是否收起（点的是当前已选中池子时，游戏不会自动收起）
        int? panelBefore = FrameGrabber.Grab(_controllerGetter()) is { } f1
            ? FrameGrabber.RegionHash(f1, DropdownPanel) : null;
        Tap(opt, ct);
        Thread.Sleep(pts.AfterPoolSwitchMs);
        WaitStableRegion(pts, ct);
        // 收起复验：哈希仍相同 = 面板还开着（点按钮收不起/点击丢失），点面板外收起并重试
        int? panelAfter = FrameGrabber.Grab(_controllerGetter()) is { } f2
            ? FrameGrabber.RegionHash(f2, DropdownPanel) : null;
        var retries = 0;
        while (panelBefore != null && panelAfter != null && panelBefore == panelAfter && retries < 3)
        {
            retries++;
            _onLog($"下拉面板未自动收起（选中的是当前池子），点面板外收起（第 {retries} 次）", WaiLogLevel.Info);
            Tap(PanelDismiss, ct);
            Thread.Sleep(pts.AfterDropdownMs);
            WaitStableRegion(pts, ct);
            panelAfter = FrameGrabber.Grab(_controllerGetter()) is { } f3
                ? FrameGrabber.RegionHash(f3, DropdownPanel) : (int?)null;
        }
        if (panelBefore != null && panelAfter != null && panelBefore == panelAfter)
        {
            _onLog($"下拉面板收起失败（已重试 {retries} 次），面板会遮挡时间列导致识别异常", WaiLogLevel.Warn);
        }
        // 兜底点页码 1：有的切池会记住上次页码
        Tap(pts.PageOne, ct);
        Thread.Sleep(300);
        WaitStableRegion(pts, ct);
    }

    /**
     * 点「下一页」：pagerStrip 是用户框定的按钮精确范围——在范围内找亮色
     * 内容（按钮文字）的包围盒点其中心；范围内没有内容（按钮置灰/消失，
     * 常见于最后一页）就点范围中心。
     */
    private void TapNextPageSmart(Points pts, CancellationToken ct)
    {
        var band = pts.PagerStrip;
        if (band.W == 0 && band.H == 0)
        {
            Tap(pts.NextPage, ct);
            return;
        }
        var target = FrameGrabber.Grab(_controllerGetter()) is { } f
            ? BrightContentCenter(f, band) ?? new Points.Pt { X = band.X + band.W / 2, Y = band.Y + band.H / 2 }
            : new Points.Pt { X = band.X + band.W / 2, Y = band.Y + band.H / 2 };
        Tap(target, ct);
    }

    /**
     * 区域内亮色像素按列投影分块（块间暗隙 ≥6 列即切开），取最右亮块的包围盒
     * 中心——「下一页」按钮固定在 pagerStrip 最右端。最右块亮像素过少视为噪声
     * 返回 null（调用方回退区域中心）。绝不取次右块，防止点到页码。
     */
    private static Points.Pt? BrightContentCenter(ToolFrame f, ToolRect band)
    {
        var x1 = Math.Max(0, band.X);
        var x2 = Math.Min(band.X + band.W, f.Width);
        var y1 = Math.Max(0, band.Y);
        var y2 = Math.Min(band.Y + band.H, f.Height);
        if (x2 <= x1 || y2 <= y1) return null;
        var cols = x2 - x1;
        var stride = f.Width * 4;
        var colHas = new bool[cols];
        var colCount = new int[cols];
        var colMinY = new int[cols];
        var colMaxY = new int[cols];
        Array.Fill(colMinY, int.MaxValue);
        Array.Fill(colMaxY, -1);
        for (var y = y1; y < y2; y++)
        {
            for (var x = x1; x < x2; x++)
            {
                var o = y * stride + x * 4;
                if (f.Bgra[o + 2] + f.Bgra[o + 1] + f.Bgra[o] > 270)  // 亮度均值>90
                {
                    var ci = x - x1;
                    colHas[ci] = true;
                    colCount[ci]++;
                    if (y < colMinY[ci]) colMinY[ci] = y;
                    if (y > colMaxY[ci]) colMaxY[ci] = y;
                }
            }
        }
        // 从右往左扫：最近一个亮块，允许块内 <6 列的暗隙（笔画间隙），≥6 列即块边界
        var right = -1;
        var left = -1;
        var gapRun = 0;
        for (var i = cols - 1; i >= 0; i--)
        {
            if (colHas[i])
            {
                if (right == -1) right = i;
                left = i;
                gapRun = 0;
            }
            else if (right != -1)
            {
                gapRun++;
                if (gapRun >= 6) break;
            }
        }
        if (right == -1) return null;
        var miny = int.MaxValue;
        var maxy = -1;
        var n = 0;
        for (var ci = left; ci <= right; ci++)
        {
            if (!colHas[ci]) continue;
            n += colCount[ci];
            if (colMinY[ci] < miny) miny = colMinY[ci];
            if (colMaxY[ci] > maxy) maxy = colMaxY[ci];
        }
        if (n < 20 || maxy < miny) return null;
        return new Points.Pt { X = x1 + (left + right) / 2, Y = (miny + maxy) / 2 };
    }

    /**
     * 点下一页并等画面过渡。翻页是否真正生效由 CrawlPool 拿页面内容比对判定。
     */
    private void ClickNextPage(Points pts, CancellationToken ct)
    {
        TapNextPageSmart(pts, ct);
        Thread.Sleep(pts.AfterNextMs);
    }

    private void WaitStableRegion(Points pts, CancellationToken ct, int timeoutMs = 4000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
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
            var h = FrameGrabber.RegionHash(f, pts.FullRegion);
            if (has && h == prev) return;
            prev = h;
            has = true;
            Thread.Sleep(250);
        }
    }

    /// <summary>抓一帧并等表格区域连续两帧一致（页面静止）；超时返回最后一帧</summary>
    private ToolFrame? GrabStable(Points pts, CancellationToken ct, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
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
            var h = FrameGrabber.RegionHash(f, pts.FullRegion);
            if (has && h == prev) return f;
            prev = h;
            has = true;
            last = f;
            Thread.Sleep(220);
        }
        return last;
    }

    private void Tap(Points.Pt p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var controller = _controllerGetter()
            ?? throw new CrawlException("控制器不可用：模拟器连接是否还在？");
        controller.Click(p.X, p.Y).Wait();
    }
}
