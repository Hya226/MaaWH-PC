using Avalonia;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MaaFramework.Binding;

namespace MFAAvalonia.Helper;

/// <summary>
/// 【仓库识别】资源仓库扫描器（手机端 ResourceWarehouseScan 的 PC 移植——宿主侧
/// 宿主抓帧识别，引擎只负责导航到物资库对应页）。
/// 识别思路对标 MAA 的 DepotImageAnalyzer：720p 等间距网格（6 列、列周期 150、
/// 首格 ≈356,122、卡面 114×116），按列竖带（106×560）0.5 缩放后做灰度模板 NCC
/// 滑窗（积分图加速，阈值 0.72）识别物品种类；数量 = 卡面右下角 7 条密集窗
/// OCR（OTSU/反色双通道级联 + 等价类众数投票）。翻页 = (800,630)→(800,350)
/// 竖直滑动，到底判据 = 滑动前后画面哈希不变（兜底：连续 2 页识别集合无新增）；
/// 收尾 = BACK 回主页（按物资库深绿侧栏特征决定是否补按）。
/// 模板库 = assets/warehouse/&lt;cat&gt;/*.png（文件名即材料名），PC 直接读发行包。
/// </summary>
public sealed class WarehouseScanner
{
    public sealed class ScanException(string message) : Exception(message);

    public sealed record ScanResult(Dictionary<string, string> Counts, int Pages, long ElapsedMs);

    // ---- 网格参数（用户按帧逐格校准：首格 (356,122) 卡面 114×116，列周期 150 行周期 130）----
    private const int GridX0 = 356;
    private const int GridY0 = 122;
    private const int ColPeriod = 150;
    private const int CellH = 116;
    private const int Cols = 6;
    private const int StripW = 106;      // 竖带宽 = 卡面内缩后物品区
    private const int StripXInset = 4;
    private const int StripTop = 100;    // Y0_SCAN
    private const int ScanBottom = 660;
    private const float SampleScale = 0.5f;
    private const float MatchThreshold = 0.72f;
    private const int MaxPages = 60;     // 防死循环保险（手机端无，PC 加上）

    private const int QtyModeOtsu = 0;
    private const int QtyModeInvert = 1;

    private readonly Func<MaaController?> _controllerGetter;
    private readonly Func<MaaTasker?> _taskerGetter;
    private readonly OcrEngine _ocr;
    private readonly Action<string, WarehouseLogLevel> _onLog;
    private readonly string _category;

    public WarehouseScanner(string category, Func<MaaController?> controllerGetter,
        Func<MaaTasker?> taskerGetter, OcrEngine ocr, Action<string, WarehouseLogLevel> onLog)
    {
        _category = category;
        _controllerGetter = controllerGetter;
        _taskerGetter = taskerGetter;
        _ocr = ocr;
        _onLog = onLog;
    }

    public enum WarehouseLogLevel { Trace, Info, Warn, Error, Success }

    private sealed class Template(
        string name, float[] gray, int grayW, int grayH, float mt, float dt)
    {
        public string Name { get; } = name;
        public float[] Gray { get; } = gray;
        public int GrayW { get; } = grayW;
        public int GrayH { get; } = grayH;
        public float Mt { get; } = mt;
        public float Dt { get; } = dt;
    }

    private readonly record struct MatchResult(float Score, int Ox, int Oy);

    // ---------------- 主流程 ----------------

    public async Task<ScanResult> RunAsync(CancellationToken ct,
        Action<string>? onProgress = null, Action<Dictionary<string, string>>? onCounts = null)
    {
        var started = Environment.TickCount64;
        var templates = LoadTemplates();
        if (templates.Count == 0)
        {
            throw new ScanException($"图标模板库为空（assets/warehouse/{_category} 缺失或格式错误）");
        }
        _onLog($"仓库识别开始（{WarehouseData.CatName(_category)}仓库）：模板 {templates.Count} 个", WarehouseLogLevel.Info);

        var counts = new Dictionary<string, string>();
        var pages = 0;
        var retries = 0;
        var prevKeys = new HashSet<string>();
        var stableRuns = 0;
        var firstPageChecked = false;
        int? lastSwipeHash = null;
        var maxBy = (int)(Environment.TickCount64 + 30 * 60 * 1000L); // 全程 30 分钟硬上限

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 > maxBy)
            {
                throw new ScanException("扫描超过 30 分钟仍未终止，已中止");
            }

            var frame = GrabStable(ct);
            if (frame == null)
            {
                _onLog("抓帧失败，重试…", WarehouseLogLevel.Warn);
                await Task.Delay(800, ct);
                continue;
            }
            // 翻页后画面与滑动前完全一致 = 没滚动 = 到底
            if (lastSwipeHash != null && QuickHash(frame) == lastSwipeHash)
            {
                _onLog("画面未滚动——已到底", WarehouseLogLevel.Info);
                break;
            }

            var hits = ScanPage(frame, templates, counts, onProgress, pages == 0);
            onCounts?.Invoke(counts);
            var keys = hits.Select(h => h.Name).ToHashSet();
            pages++;
            var newCount = keys.Count(k => !prevKeys.Contains(k));
            _onLog($"第 {pages} 页：识别 {keys.Count} 种资源" +
                   (keys.Count > 0 ? $"（新增 {newCount}）" : ""), WarehouseLogLevel.Info);

            if (!firstPageChecked)
            {
                firstPageChecked = true;
                if (hits.Count == 0)
                {
                    // 第一页 0 命中：可能是切换页面的过渡帧——重抓重试再下结论
                    retries++;
                    if (retries <= 2)
                    {
                        _onLog($"第 1 页未识别到资源（第 {retries} 次重试）——可能是过渡帧，重抓…", WarehouseLogLevel.Warn);
                        prevKeys = new HashSet<string>();
                        stableRuns = 0;
                        pages--;
                        await Task.Delay(1200, ct);
                        continue;
                    }
                    throw new ScanException(
                        $"未识别到任何资源——请确认游戏已停在【物资库-{WarehouseData.CatName(_category)}】页（可先跑【仓库识别】流程导航）");
                }
            }

            if (keys.SetEquals(prevKeys)) stableRuns++; else stableRuns = 0;
            if (stableRuns >= 2)
            {
                _onLog("连续 2 页识别集合无新增——资源仓库已到底", WarehouseLogLevel.Info);
                break;
            }
            prevKeys = keys;

            ct.ThrowIfCancellationRequested();
            lastSwipeHash = QuickHash(frame);
            Swipe(800, 630, 800, 350, 600, ct);
            await Task.Delay(900, ct); // 等滑动惯性+页面稳定
        }

        await BackToHome(ct);

        var elapsedMs = Environment.TickCount64 - started;
        _onLog(
            $"仓库识别完成（{WarehouseData.CatName(_category)}）：共 {pages} 页，{counts.Count} 种" +
            $"（数量识别成功 {counts.Count(kv => !string.IsNullOrEmpty(kv.Value))}）" +
            $" · 耗时 {elapsedMs / 1000}s · 结果已保存 data/warehouse/{_category}_scan.json",
            WarehouseLogLevel.Success);
        return new ScanResult(counts, pages, elapsedMs);
    }

    /// <summary>单页识别：列竖带全高滑窗（x+y 二维滑动，滚动列表适配——滑动翻页后
    /// 物品停在任意滚动偏移，x 方向也必须滑）。同列同 y 去重保留分高者；
    /// 同名资源跨页数量恒定，数量 OCR 只对「本页新识别」做，控制次数。</summary>
    private List<(string Name, float Score, int CardX, int Gy)> ScanPage(
        ToolFrame frame, List<Template> templates,
        Dictionary<string, string> counts,
        Action<string>? onProgress, bool verbose)
    {
        var hits = new List<(string Name, float Score, int CardX, int Gy)>();
        var newOnes = 0;
        var dstW = (int)(StripW * SampleScale);          // 53
        var dstH = (ScanBottom - StripTop) * (int)SampleScale; // 280

        for (var c = 0; c < Cols; c++)
        {
            var gx = GridX0 + c * ColPeriod + StripXInset;
            if (gx + StripW > frame.Width) continue;
            var cardX = GridX0 + c * ColPeriod;          // 卡面左缘物理 x（数量 OCR 锚点）
            var strip = GrayOf(frame, gx, StripTop, StripW, ScanBottom - StripTop, dstW, dstH);

            var colHits = new List<(string Name, float Score, int Gy)>();
            foreach (var t in templates)
            {
                var m = NccBest(strip, dstW, dstH, t.Gray, t.GrayW, t.GrayH);
                if (m == null || m.Value.Score < MatchThreshold) continue;
                colHits.Add((t.Name, m.Value.Score, StripTop + m.Value.Oy * 2));
            }

            // 同 y 去重（同列内不同模板认同一物品时保留分高者；列间独立）
            var kept = new List<(string Name, float Score, int CardX, int Gy)>();
            foreach (var h in colHits.OrderByDescending(h => h.Score))
            {
                if (kept.All(k => Math.Abs(k.Gy - h.Gy) >= CellH * 2 / 3))
                    kept.Add((h.Name, h.Score, cardX, h.Gy));
            }

            foreach (var h in kept)
            {
                if (!counts.ContainsKey(h.Name))
                {
                    newOnes++;
                    counts[h.Name] = OcrQuantity(frame, h.CardX, h.Gy, h.Name);
                }
            }
            if (verbose) _onLog($"列{c + 1}：命中 {kept.Count} 种", WarehouseLogLevel.Trace);
            hits.AddRange(kept);
        }

        if (hits.Count > 0)
            onProgress?.Invoke($"仓库识别：累计 {counts.Count} 种 · 本页 {hits.Count} 格");
        if (newOnes > 0) _onLog($"本页新增资源 {newOnes} 种", WarehouseLogLevel.Trace);
        return hits;
    }

    /// <summary>数量 OCR：卡面右下角数量白字。7 条密集窗（步长 6px）投票，
    /// 数字带无论漂移到哪都有窗完整覆盖；OTSU 票少或置信低再补反色通道；
    /// 全部读数按等价类（重复数字同类/数值同类）众数投票取最高置信读数。</summary>
    private string OcrQuantity(ToolFrame frame, int cardX, int gy, string name)
    {
        try
        {
            if (cardX < 0 || gy < 0) return "";
            var votes = new List<(string Key, string Text, float Conf)>();
            var dyes = new[] { 76, 82, 88, 94, 100, 106, 112 };
            foreach (var dy in dyes)
            {
                var (qs, qconf) = OcrDigits(frame, cardX + 16, gy + dy, 98, 24, 2, QtyModeOtsu);
                if (qs != null) votes.Add((VoteClass(qs), qs, qconf));
            }
            if (votes.Count < 2 || (votes.Count > 0 && votes.Max(v => v.Conf) < 0.75f))
            {
                foreach (var dy in dyes)
                {
                    var (qs, qconf) = OcrDigits(frame, cardX + 16, gy + dy, 98, 24, 2, QtyModeInvert);
                    if (qs != null) votes.Add((VoteClass(qs), qs, qconf));
                }
            }
            if (votes.Count == 0)
            {
                _onLog($"数量 OCR 失败：{name}（可对照 data/warehouse 截图排查）", WarehouseLogLevel.Warn);
                return "";
            }
            var bestGroup = votes.GroupBy(v => v.Key)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Max(v => v.Conf))
                .First();
            var display = bestGroup.OrderByDescending(v => v.Conf).First().Text;
            return display;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>投票等价类：重复数字（1/11/111）同类；其余按数值化 key（万折算同值同类）</summary>
    private static string VoteClass(string s)
    {
        if (s.Length > 0 && s.All(ch => char.IsDigit(ch) && ch == s[0])) return $"rep:{s[0]}";
        return $"n:{WarehouseData.QtyKey(s)}";
    }

    /// <summary>窗口 OCR：裁窗 → 放大 scale 倍（最近邻）→ 通道预处理 → rec → 取最长数量串。</summary>
    private (string? Text, float Conf) OcrDigits(
        ToolFrame frame, int rx, int ry, int rw, int rh, int scale, int mode)
    {
        if (rx < 0 || ry < 0 || rx + rw > frame.Width || ry + rh > frame.Height) return (null, 0);
        var crop = CropBgra(frame, rx, ry, rw, rh);
        crop = UpscaleNearest(crop, rw, rh, scale);
        crop = mode == QtyModeInvert ? Invert(crop) : OtsuBinarize(crop);
        if (crop == null) return (null, 0); // OTSU 白占比过高，弃窗
        var line = _ocr.Recognize(crop, rw * scale, rh * scale, 0, 0, rw * scale, rh * scale);
        var text = ParseQuantity(line.Text);
        return string.IsNullOrEmpty(text) ? (null, 0) : (text, line.Score);
    }

    /// <summary>数量显示串（保留「万」字）：取最长「数字+可选小数点+可选万」组</summary>
    private static string ParseQuantity(string text)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"[0-9]+(?:[.．,，][0-9]+)?\s*[万萬]?");
        string? best = null;
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (!m.Value.Any(char.IsDigit)) continue;
            if (best == null || m.Value.Length > best.Length) best = m.Value;
        }
        return (best ?? "").Replace(" ", "")
            .Replace('．', '.').Replace('，', '.').Replace(',', '.').Replace('萬', '万');
    }

    // ---------------- 图像原语 ----------------

    private static byte[] CropBgra(ToolFrame f, int x, int y, int w, int h)
    {
        var outBuf = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
        {
            Buffer.BlockCopy(f.Bgra, ((y + row) * f.Width + x) * 4, outBuf, row * w * 4, w * 4);
        }
        return outBuf;
    }

    private static byte[] UpscaleNearest(byte[] bgra, int w, int h, int scale)
    {
        if (scale <= 1) return bgra;
        var ow = w * scale;
        var oh = h * scale;
        var outBuf = new byte[ow * oh * 4];
        for (var y = 0; y < oh; y++)
        {
            var sy = y / scale;
            for (var x = 0; x < ow; x++)
            {
                var sx = x / scale;
                Buffer.BlockCopy(bgra, (sy * w + sx) * 4, outBuf, (y * ow + x) * 4, 4);
            }
        }
        return outBuf;
    }

    private static byte[] Invert(byte[] bgra)
    {
        var outBuf = (byte[])bgra.Clone();
        for (var i = 0; i + 3 < outBuf.Length; i += 4)
        {
            outBuf[i] = (byte)(255 - outBuf[i]);
            outBuf[i + 1] = (byte)(255 - outBuf[i + 1]);
            outBuf[i + 2] = (byte)(255 - outBuf[i + 2]);
            // alpha 不动
        }
        return outBuf;
    }

    /// <summary>OTSU 自适应阈值二值化：亮（数量白字）→白、其余→黑。
    /// 白占比 &gt;55% = 数字泡在大片浅色背景，OTSU 会吞字形——返回 null 弃窗。</summary>
    private static byte[]? OtsuBinarize(byte[] bgra)
    {
        var n = bgra.Length / 4;
        var gray = new byte[n];
        var hist = new int[256];
        for (var i = 0; i < n; i++)
        {
            var o = i * 4;
            var g = (int)(0.299f * bgra[o + 2] + 0.587f * bgra[o + 1] + 0.114f * bgra[o]);
            g = Math.Clamp(g, 0, 255);
            gray[i] = (byte)g;
            hist[g]++;
        }
        double sumAll = 0;
        for (var t = 0; t < 256; t++) sumAll += (long)t * hist[t];
        long sumB = 0;
        int wB = 0, bestT = 128;
        double bestVar = -1;
        for (var t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            var wF = n - wB;
            if (wF == 0) break;
            sumB += (long)t * hist[t];
            var mB = (double)sumB / wB;
            var mF = (sumAll - sumB) / wF;
            var v = (double)wB * wF * (mB - mF) * (mB - mF);
            if (v > bestVar)
            {
                bestVar = v;
                bestT = t;
            }
        }
        var whiteCnt = 0;
        var outBuf = new byte[bgra.Length];
        for (var i = 0; i < n; i++)
        {
            var o = i * 4;
            if (gray[i] > bestT)
            {
                outBuf[o] = outBuf[o + 1] = outBuf[o + 2] = 255;
                whiteCnt++;
            }
            outBuf[o + 3] = 255;
        }
        return (long)whiteCnt * 100 > (long)n * 55 ? null : outBuf;
    }

    /// <summary>裁 (x,y,w,h) → 0.5 等比缩放 → 灰度 float 数组（0.299/0.587/0.114）</summary>
    private static float[] GrayOf(ToolFrame f, int x, int y, int cropW, int cropH, int dstW, int dstH)
    {
        var outBuf = new float[dstW * dstH];
        var safeW = Math.Min(cropW, f.Width - x);
        var safeH = Math.Min(cropH, f.Height - y);
        if (safeW <= 0 || safeH <= 0) return outBuf;
        var dstStride = dstW * 4;
        var srcBgra = new byte[dstStride * dstH];
        // 采样：直接按比例取源像素（nearest），等价 createScaledBitmap 的有效信息
        for (var dy = 0; dy < dstH; dy++)
        {
            var sy = y + Math.Min(safeH - 1, dy * safeH / dstH);
            for (var dx = 0; dx < dstW; dx++)
            {
                var sx = x + Math.Min(safeW - 1, dx * safeW / dstW);
                var so = (sy * f.Width + sx) * 4;
                var lum = 0.299f * f.Bgra[so + 2] + 0.587f * f.Bgra[so + 1] + 0.114f * f.Bgra[so];
                var o = dy * dstStride + dx * 4;
                var b = (byte)Math.Clamp((int)lum, 0, 255);
                srcBgra[o] = b;
                srcBgra[o + 1] = b;
                srcBgra[o + 2] = b;
                srcBgra[o + 3] = 255;
            }
        }
        for (var i = 0; i < dstW * dstH; i++)
        {
            var o = i * 4;
            outBuf[i] = 0.299f * srcBgra[o + 2] + 0.587f * srcBgra[o + 1] + 0.114f * srcBgra[o];
        }
        return outBuf;
    }

    /// <summary>滑窗 NCC 最佳命中（积分图加速均值/方差，分子逐像素累加），负相关按 0</summary>
    private static MatchResult? NccBest(
        float[] roi, int roiW, int roiH, float[] tpl, int tplW, int tplH)
    {
        if (tplW > roiW || tplH > roiH)
        {
            // 防御分支：模板大于 ROI → 降采样模板到 ROI 尺寸整幅 NCC（无位置语义）
            var tw = Math.Min(tplW, roiW);
            var th = Math.Min(tplH, roiH);
            var t2 = new float[tw * th];
            for (var y = 0; y < th; y++)
                for (var x = 0; x < tw; x++)
                    t2[y * tw + x] = tpl[(y * tplH / th) * tplW + (x * tplW / tw)];
            return new MatchResult(NccFlat(roi, t2), 0, 0);
        }
        var iw = roiW + 1;
        var sum = new float[iw * (roiH + 1)];
        var sq = new float[iw * (roiH + 1)];
        for (var y = 0; y < roiH; y++)
        {
            float rs = 0, rq = 0;
            for (var x = 0; x < roiW; x++)
            {
                var v = roi[y * roiW + x];
                rs += v;
                rq += v * v;
                sum[(y + 1) * iw + (x + 1)] = sum[y * iw + (x + 1)] + rs;
                sq[(y + 1) * iw + (x + 1)] = sq[y * iw + (x + 1)] + rq;
            }
        }
        float mt = 0;
        foreach (var v in tpl) mt += v;
        mt /= tpl.Length;
        float dt = 0;
        foreach (var v in tpl)
        {
            var d = v - mt;
            dt += d * d;
        }
        float best = 0;
        var bx = 0;
        var by = 0;
        var n = tplW * tplH;
        for (var oy = 0; oy <= roiH - tplH; oy++)
        {
            for (var ox = 0; ox <= roiW - tplW; ox++)
            {
                var s = AreaSum(sum, iw, ox, oy, tplW, tplH);
                var q = AreaSum(sq, iw, ox, oy, tplW, tplH);
                var mr = s / n;
                float num = 0;
                for (var ty = 0; ty < tplH; ty++)
                {
                    var rrow = (oy + ty) * roiW + ox;
                    var trow = ty * tplW;
                    for (var tx = 0; tx < tplW; tx++)
                    {
                        num += (roi[rrow + tx] - mr) * (tpl[trow + tx] - mt);
                    }
                }
                var wVar = q - s * s / n;
                var den = (float)Math.Sqrt(wVar * dt);
                var v = den <= 0f ? 0f : num / den;
                if (v > best)
                {
                    best = v;
                    bx = ox;
                    by = oy;
                }
            }
        }
        return new MatchResult(Math.Clamp(best, 0f, 1f), bx, by);
    }

    private static float AreaSum(float[] sum, int iw, int x, int y, int w, int h) =>
        sum[(y + h) * iw + (x + w)] - sum[y * iw + (x + w)] - sum[(y + h) * iw + x] + sum[y * iw + x];

    private static float NccFlat(float[] a, float[] b)
    {
        float ma = 0, mb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            ma += a[i];
            mb += b[i];
        }
        ma /= a.Length;
        mb /= b.Length;
        float num = 0, da = 0, dbb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i] - ma;
            var y = b[i] - mb;
            num += x * y;
            da += x * x;
            dbb += y * y;
        }
        var den = (float)Math.Sqrt(da * dbb);
        return den <= 0f ? 0f : Math.Clamp(num / den, 0f, 1f);
    }

    /// <summary>全帧粗哈希（24px 抽样亮度），仅用于稳定/到底判定</summary>
    private static int QuickHash(ToolFrame f)
    {
        long h = 17;
        for (var y = 0; y < f.Height; y += 24)
        {
            for (var x = 0; x < f.Width; x += 24)
            {
                var o = (y * f.Width + x) * 4;
                h = h * 31 + f.Bgra[o] + f.Bgra[o + 1] + f.Bgra[o + 2];
            }
        }
        return (int)h;
    }

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
            var h = QuickHash(f);
            if (has && h == prev) return f;
            prev = h;
            has = true;
            last = f;
            Thread.Sleep(220);
        }
        return last;
    }

    private void Swipe(int x1, int y1, int x2, int y2, int duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var controller = _controllerGetter()
            ?? throw new ScanException("控制器不可用：模拟器连接是否还在？");
        controller.Swipe(x1, y1, x2, y2, duration).Wait();
    }

    private void PressBack(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var controller = _controllerGetter()
            ?? throw new ScanException("控制器不可用：模拟器连接是否还在？");
        controller.ClickKey(4); // android BACK
    }

    /// <summary>收尾：BACK 资源页→物资库；若帧里仍是物资库（左侧深绿侧栏）再补按一次</summary>
    private async Task BackToHome(CancellationToken ct)
    {
        _onLog("仓库识别：返回主页…", WarehouseLogLevel.Info);
        PressBack(ct);
        await Task.Delay(1500, ct);
        var frame = GrabStable(ct);
        if (frame != null && LooksLikeWarehouse(frame))
        {
            PressBack(ct); // 仍在深层（物资库）→ 再按一次
            await Task.Delay(1500, ct);
        }
    }

    /// <summary>物资库页特征：左侧深绿分类栏（x 30..300 深绿占比高），主页没有</summary>
    private static bool LooksLikeWarehouse(ToolFrame f)
    {
        var darkGreen = 0;
        var total = 0;
        for (var y = 80; y < 600; y += 8)
        {
            for (var x = 40; x < 290; x += 8)
            {
                if (x >= f.Width || y >= f.Height) continue;
                var o = (y * f.Width + x) * 4;
                var r = f.Bgra[o + 2];
                var g = f.Bgra[o + 1];
                var b = f.Bgra[o];
                total++;
                if (g > r && g > b * 0.8f && g < 150 && r < 110) darkGreen++;
            }
        }
        return total == 0 || darkGreen * 100 / Math.Max(total, 1) >= 30;
    }

    // ---------------- 模板库 ----------------

    /// <summary>assets/warehouse/&lt;category&gt;/*.png → 灰度模板（PNG 文件名 = 材料名）。
    /// 透明角合成中性灰底（118）后统一 0.5 等比缩放；预计算均值 mt 与 Σ(t-mt)²。</summary>
    private List<Template> LoadTemplates()
    {
        var dir = WarehouseData.AssetDir(_category);
        var outList = new List<Template>();
        if (!Directory.Exists(dir))
        {
            _onLog($"模板目录缺失：{dir}", WarehouseLogLevel.Warn);
            return outList;
        }
        foreach (var file in Directory.GetFiles(dir, "*.png").OrderBy(f => f))
        {
            try
            {
                using var bmp = new Bitmap(file);
                var g = ToGrayNormalized(bmp);
                float mt = 0;
                foreach (var v in g.Gray) mt += v;
                mt /= g.Gray.Length;
                float dt = 0;
                foreach (var v in g.Gray)
                {
                    var d = v - mt;
                    dt += d * d;
                }
                outList.Add(new Template(
                    Path.GetFileNameWithoutExtension(file), g.Gray, g.W, g.H, mt, dt));
            }
            catch (Exception ex)
            {
                _onLog($"模板加载失败 {Path.GetFileName(file)}：{ex.Message}", WarehouseLogLevel.Warn);
            }
        }
        return outList;
    }

    private static (float[] Gray, int W, int H) ToGrayNormalized(Bitmap bmp)
    {
        var w = Math.Max(12, (int)(bmp.PixelSize.Width * SampleScale));
        var h = Math.Max(12, (int)(bmp.PixelSize.Height * SampleScale));
        // 先铺中性灰底消透明角，再缩放
        var flat = new byte[bmp.PixelSize.Width * bmp.PixelSize.Height * 4];
        var fw = bmp.PixelSize.Width;
        var fh = bmp.PixelSize.Height;
        var stride = fw * 4;
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(flat, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bmp.CopyPixels(new PixelRect(0, 0, fw, fh), handle.AddrOfPinnedObject(), flat.Length, stride);
        }
        finally
        {
            handle.Free();
        }
        for (var i = 0; i + 3 < flat.Length; i += 4)
        {
            var a = flat[i + 3];
            if (a >= 255) continue;
            var g = (byte)118;
            flat[i] = Blend(flat[i], g, a);
            flat[i + 1] = Blend(flat[i + 1], g, a);
            flat[i + 2] = Blend(flat[i + 2], g, a);
            flat[i + 3] = 255;
        }
        var small = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var sy = Math.Min(fh - 1, y * fh / h);
            for (var x = 0; x < w; x++)
            {
                var sx = Math.Min(fw - 1, x * fw / w);
                Buffer.BlockCopy(flat, (sy * fw + sx) * 4, small, (y * w + x) * 4, 4);
            }
        }
        var gray = new float[w * h];
        for (var i = 0; i < w * h; i++)
        {
            var o = i * 4;
            gray[i] = 0.299f * small[o + 2] + 0.587f * small[o + 1] + 0.114f * small[o];
        }
        return (gray, w, h);
    }

    private static byte Blend(byte src, byte bg, byte alpha) =>
        (byte)((src * alpha + bg * (255 - alpha)) / 255);
}
