using Avalonia.Media;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MFAAvalonia.Helper;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Brushes = Avalonia.Media.Brushes;
using MaaTasker = MaaFramework.Binding.MaaTasker;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// 【OCR记录】（interface.json 中 type=record 的 option）PC 端实现。
/// 与手机端 QueueRunner.kt 的 recordVars 机制同源：清单里 record 参数的
/// pipeline_override 键是记录节点名，任务运行时识别命中这些节点就把 OCR
/// 文本归属到参数名下，实时打一行日志，任务收口时打一行汇总。
/// 引擎本身没有 record 概念，节点文本一律经 GetRecognitionDetail 取出。
/// </summary>
public static class OcrRecordStore
{
    /// <summary>体力回满换算口径：MAA 同款 6 分钟/点，从识别时刻外推。</summary>
    private const double SanityRegenMinutesPerPoint = 6.0;

    /// <summary>「当前/上限」形体力值（如 857/1000）。</summary>
    private static readonly Regex SanityRegex = new(@"^(\d{1,5})\s*/\s*(\d{1,5})$", RegexOptions.Compiled);

    /// <summary>循环体里展开份节点名带 _L&lt;k&gt; 后缀（VF_x_03_L2_Hit），剥掉再查映射。</summary>
    private static readonly Regex LoopSuffixRegex = new(@"(_L\d+)+(?=_Hit$|$)", RegexOptions.Compiled);

    private class Bucket
    {
        /// <summary>记录节点名 → 参数显示名（任务启动时解析，队列运行期只读）。</summary>
        public volatile Dictionary<string, string>? NodeToLabel;
        /// <summary>本队列已记录值（参数名 → 展示串，含回满预计），收口汇总后取走。</summary>
        public readonly ConcurrentDictionary<string, string> Records = new();
    }

    private static readonly ConcurrentDictionary<string, Bucket> Buckets = new();

    private static Bucket BucketFor(string instanceId) =>
        Buckets.GetOrAdd(instanceId, _ => new Bucket());

    /// <summary>清单里存在 record 映射（回调热路径判断，miss 即零开销）。</summary>
    public static bool HasMappings(string instanceId) =>
        BucketFor(instanceId).NodeToLabel?.Count > 0;

    /// <summary>
    /// 队列开始：重置本实例记录，并从清单 option 解析「记录节点名 → 参数名」映射。
    /// 映射只在清单首次可用时打一行日志，之后静默。
    /// </summary>
    public static void ResetForRun(string instanceId, MaaInterface? iface, MaaProcessor proc)
    {
        var bucket = BucketFor(instanceId);
        bucket.Records.Clear();
        try
        {
            var mappings = new Dictionary<string, string>();
            var labels = new SortedSet<string>();
            foreach (var kv in iface?.Option ?? Enumerable.Empty<KeyValuePair<string, MaaInterface.MaaInterfaceOption>>())
            {
                var opt = kv.Value;
                if (opt is null || !string.Equals(opt.OptionType, "record", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var label = string.IsNullOrWhiteSpace(opt.DisplayName) ? kv.Key : opt.DisplayName;
                labels.Add(label);
                foreach (var node in opt.PipelineOverride?.Keys ?? (IEnumerable<string>)Array.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(node))
                    {
                        mappings[node] = label;
                    }
                }
            }
            if (mappings.Count > 0 && bucket.NodeToLabel is null or { Count: 0 })
            {
                proc.AddLog($"📋 OCR记录：清单映射 {mappings.Count} 个节点（参数：{string.Join("、", labels)}）",
                    Brushes.Black, changeColor: false);
            }
            bucket.NodeToLabel = mappings;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"[OCR记录] 映射解析失败：{ex.Message}");
            bucket.NodeToLabel = new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// 识别命中回调：节点名在映射里则取识别文本记入参数并实时打一行。
    /// 跑在引擎回调线程，任何一步失败静默返回，绝不影响任务执行。
    /// </summary>
    public static void TryRecord(string instanceId, MaaTasker tasker, long recoId, string nodeName, MaaProcessor proc)
    {
        try
        {
            var bucket = BucketFor(instanceId);
            var map = bucket.NodeToLabel;
            if (map is null or { Count: 0 } || string.IsNullOrWhiteSpace(nodeName))
            {
                return;
            }
            if (!map.TryGetValue(nodeName, out var label)
                && !map.TryGetValue(LoopSuffixRegex.Replace(nodeName, string.Empty), out label))
            {
                return;
            }
            if (recoId <= 0)
            {
                return;
            }

            using var rect = new MaaRectBuffer();
            using var imageBuffer = new MaaImageBuffer();
            using var imageListBuffer = new MaaImageListBuffer();
            tasker.GetRecognitionDetail(recoId, out _, out _, out _, rect, out var detailJson,
                imageBuffer, imageListBuffer);

            var text = ExtractOcrText(detailJson);
            // 「当前/上限」形体力值在命中时固化回满预计（实时/汇总两处同一串）；
            // 单值（如主页体力 999）原样
            var shown = string.IsNullOrWhiteSpace(text) ? "（未识别到文本）"
                : text + (SanityEtaSuffix(text) is { } eta ? $"（{eta}）" : string.Empty);
            bucket.Records[label] = shown;
            proc.AddLog($"📋 {label} = {shown}", Brushes.Black, changeColor: false);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"[OCR记录] 命中处理失败：{ex.Message}");
        }
    }

    /// <summary>任务收口：有记录则打一行汇总（成功/失败都打），取走后幂等。</summary>
    public static void EmitSummary(string instanceId, MaaProcessor proc)
    {
        try
        {
            var bucket = BucketFor(instanceId);
            if (bucket.Records.IsEmpty)
            {
                return;
            }
            var pairs = bucket.Records.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {kv.Value}");
            proc.AddLog($"📋 OCR记录汇总：{string.Join("；", pairs)}", Brushes.Black, changeColor: false);
            bucket.Records.Clear();
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"[OCR记录] 汇总输出失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 从识别详情 JSON 抠 OCR 文本：detail.all[] 里取分数最高、文本非空的一条
    /// （与手机端 QueueRunner.ocrTextOf 同一张明细表）。明细形状随引擎版本可能变化，
    /// 任何一步取不到都返回空串。
    /// </summary>
    private static string ExtractOcrText(string? detailJson)
    {
        if (string.IsNullOrWhiteSpace(detailJson))
        {
            return string.Empty;
        }
        try
        {
            var reco = JObject.Parse(detailJson);
            var all = reco["detail"]?["all"] as JArray;
            if (all != null)
            {
                JToken? best = null;
                var bestScore = double.NegativeInfinity;
                foreach (var item in all)
                {
                    var text = item?["text"]?.ToString();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }
                    var score = item["score"]?.ToObject<double?>() ?? double.NegativeInfinity;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = item;
                    }
                }
                if (best != null)
                {
                    return best["text"]?.ToString()?.Trim() ?? string.Empty;
                }
            }
            return reco["detail"]?["best"]?["text"]?.ToString()?.Trim()
                ?? reco["best_result"]?["text"]?.ToString()?.Trim()
                ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 「当前/上限」形体力值在未满时给出预计回满时刻（识别时刻 + 差值×6 分钟）；
    /// 已满、单值、非数字返回 null。≥24 小时带日期。
    /// </summary>
    private static string? SanityEtaSuffix(string text)
    {
        try
        {
            var m = SanityRegex.Match(text.Trim());
            if (!m.Success)
            {
                return null;
            }
            var cur = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var max = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (max <= 0 || cur >= max)
            {
                return null;
            }
            var minutes = (long)((max - cur) * SanityRegenMinutesPerPoint);
            var eta = DateTime.Now.AddMinutes(minutes);
            return minutes >= 24 * 60
                ? $"预计 {eta.ToString("M月d日HH:mm", CultureInfo.InvariantCulture)} 回满"
                : $"预计 {eta.ToString("HH:mm", CultureInfo.InvariantCulture)} 回满";
        }
        catch
        {
            return null;
        }
    }
}
