using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MFAAvalonia.Helper;

/// <summary>
/// 仓库识别结果持久化 + 材料关卡目录 + 目标/库存存取（手机端 WarehouseStore /
/// MaterialCatalog 的 PC 移植）。数据目录 = 数据根下 data/warehouse/，文件形状
/// 与手机端完全一致（&lt;cat&gt;_scan.json / targets.json / material_catalog.json），
/// 手工拷贝互通。数量显示串口径（「万」折算）与手机端同源。
/// </summary>
public static class WarehouseData
{
    public const string CatResource = "resource";
    public const string CatBadge = "badge";
    public const string CatTicket = "ticket";

    public static readonly string[] AllCats = [CatTicket, CatBadge, CatResource];

    public static string DataDir =>
        Path.Combine(MFAAvalonia.Helper.AppPaths.DataRoot, "data", "warehouse");

    public static string AssetDir(string category) =>
        Path.Combine(AppContext.BaseDirectory, "assets", "warehouse", category);

    public static string CatName(string cat) => cat switch
    {
        CatBadge => "徽章",
        CatTicket => "票券",
        _ => "资源",
    };

    // ---------------- 快照（counts + prev） ----------------

    public sealed class Snapshot
    {
        public string ScannedAt { get; init; } = "";
        /// <summary>数量显示串（保留「万」，如 "3.99万"）；"" = 数量识别失败（x?）</summary>
        public Dictionary<string, string> Counts { get; init; } = new();
        public Dictionary<string, string> PrevCounts { get; init; } = new();
        public int ChangedFromPrev { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 中文材料名原样写入（默认转义 \uXXXX 也能读，但可读性差）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private static string ScanFile(string cat) => Path.Combine(DataDir, $"{cat}_scan.json");

    /// <summary>保存：当场与上一份对比差异条数，并把旧值留档为 prev。</summary>
    public static Snapshot Save(string cat, Dictionary<string, string> counts)
    {
        var prev = Load(cat);
        var prevCounts = prev?.Counts ?? new Dictionary<string, string>();
        var changed = prevCounts.Keys.Union(counts.Keys)
            .Count(k => (!prevCounts.TryGetValue(k, out var a) ? "" : a) != (!counts.TryGetValue(k, out var b) ? "" : b));
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(DataDir);
        var o = new Dictionary<string, object?>
        {
            ["scannedAt"] = ts,
            ["changedFromPrev"] = changed,
            ["counts"] = counts,
        };
        if (prevCounts.Count > 0) o["prev"] = prevCounts;
        File.WriteAllText(ScanFile(cat), JsonSerializer.Serialize(o, JsonOpts));
        return new Snapshot
        {
            ScannedAt = ts,
            Counts = new Dictionary<string, string>(counts),
            PrevCounts = prevCounts,
            ChangedFromPrev = changed,
        };
    }

    public static Snapshot? Load(string cat)
    {
        try
        {
            var f = ScanFile(cat);
            if (!File.Exists(f)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var root = doc.RootElement;
            var counts = ReadCounts(root, "counts");
            var prev = ReadCounts(root, "prev");
            return new Snapshot
            {
                ScannedAt = root.TryGetProperty("scannedAt", out var t) ? t.GetString() ?? "" : "",
                Counts = counts,
                PrevCounts = prev,
                ChangedFromPrev = root.TryGetProperty("changedFromPrev", out var c) && c.TryGetInt32(out var i) ? i : 0,
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadCounts(JsonElement root, string key)
    {
        var m = new Dictionary<string, string>();
        if (root.TryGetProperty(key, out var o) && o.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in o.EnumerateObject())
            {
                var v = p.Value.ValueKind == JsonValueKind.Number
                    && p.Value.TryGetInt64(out var n) ? n.ToString() : p.Value.GetString() ?? "";
                // 旧版 Int 值 -1 视为识别失败空串
                m[p.Name] = v == "-1" ? "" : v;
            }
        }
        return m;
    }

    // ---------------- 数量串口径（与手机端同源） ----------------

    /// <summary>数量显示串 → 数值（「3.99万」=39900；纯整数是字面值；""/无法解析 = null）</summary>
    public static long? QtyValue(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var wan = s.Contains('万');
        var body = s.Replace("万", "");
        var dot = body.IndexOf('.');
        var digits = body.Replace(".", "");
        if (!long.TryParse(digits, out var d)) return null;
        var frac = wan && dot < 0 ? 2 : dot >= 0 ? body.Length - dot - 1 : 0;
        if (wan) return d * (10000L / Pow10((int)frac));
        if (dot >= 0) return d * 100L;
        return d;
    }

    /// <summary>数值 → 紧凑显示：≥1万 用「X.XX万」（去尾 0），否则纯数字</summary>
    public static string FmtQty(long v)
    {
        if (v is < 10000 and > -10000) return v.ToString();
        var wan = Math.Abs(v) / 10000.0;
        var s = wan.ToString("0.00", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.') + "万";
        return (v < 0 ? "-" : "") + s;
    }

    private static long Pow10(int n)
    {
        long r = 1;
        for (var i = 0; i < n; i++) r *= 10;
        return r;
    }

    /// <summary>数值化投票 key（重复数字归同类；「1531.04万」「153104万」归同值）</summary>
    public static long QtyKey(string s)
    {
        var wan = s.Contains('万');
        var body = s.Replace("万", "");
        var dot = body.IndexOf('.');
        var digits = body.Replace(".", "");
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var d)) return -1;
        var frac = wan && dot < 0 ? 2 : dot >= 0 ? body.Length - dot - 1 : 0;
        return wan ? d * (10000L / Pow10((int)frac)) : d;
    }

    // ---------------- 材料关卡目录 ----------------

    public sealed class CatalogEntry
    {
        public string Task { get; init; } = "";
        public Dictionary<string, string> Params { get; init; } = new();
        public int Cost { get; init; }
        public bool Expiring { get; init; }
        public Dictionary<string, long> Drop { get; init; } = new();
        public int Index { get; init; }
    }

    private static List<CatalogEntry>? _catalogCache;

    /// <summary>读发行包 assets/warehouse/material_catalog.json（PC 端 assets 即定稿，无需指纹释放）</summary>
    public static List<CatalogEntry> EnsureCatalog()
    {
        if (_catalogCache != null) return _catalogCache;
        var outList = new List<CatalogEntry>();
        try
        {
            var f = Path.Combine(AppContext.BaseDirectory, "assets", "warehouse", "material_catalog.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var i = 0;
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                var task = o.TryGetProperty("task", out var t) ? t.GetString() ?? "" : "";
                var ps = new Dictionary<string, string>();
                if (o.TryGetProperty("params", out var pj) && pj.ValueKind == JsonValueKind.Object)
                    foreach (var p in pj.EnumerateObject())
                        ps[p.Name] = p.Value.ToString() ?? "";
                var drop = new Dictionary<string, long>();
                if (o.TryGetProperty("drop", out var dj) && dj.ValueKind == JsonValueKind.Object)
                    foreach (var p in dj.EnumerateObject())
                        drop[p.Name] = p.Value.TryGetInt64(out var n) ? n : 1;
                outList.Add(new CatalogEntry
                {
                    Task = task,
                    Params = ps,
                    Cost = o.TryGetProperty("cost", out var c) && c.TryGetInt32(out var ci) ? ci : 0,
                    Expiring = o.TryGetProperty("expiring", out var e) && e.ValueKind == JsonValueKind.True,
                    Drop = drop,
                    Index = i++,
                });
            }
        }
        catch
        {
            // 目录缺失/损坏：返回空表，编排器按「材料目录为空」收尾
        }
        _catalogCache = outList;
        return outList;
    }

    /// <summary>目标库存：data/warehouse/targets.json {"分类":{"材料":数量}}，0/负值视为未设置</summary>
    public static Dictionary<string, long> LoadTargets()
    {
        var outMap = new Dictionary<string, long>();
        try
        {
            var f = Path.Combine(DataDir, "targets.json");
            if (!File.Exists(f)) return outMap;
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            foreach (var cat in doc.RootElement.EnumerateObject())
            {
                if (cat.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var n in cat.Value.EnumerateObject())
                    if (n.Value.TryGetInt64(out var v) && v > 0)
                        outMap[n.Name] = v;
            }
        }
        catch
        {
        }
        return outMap;
    }

    /// <summary>读所选小仓库段的目标（未设置返回空表）</summary>
    public static Dictionary<string, long> GetTargetsFor(string cat)
    {
        var outMap = new Dictionary<string, long>();
        try
        {
            var f = Path.Combine(DataDir, "targets.json");
            if (!File.Exists(f)) return outMap;
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            if (doc.RootElement.TryGetProperty(cat, out var seg) && seg.ValueKind == JsonValueKind.Object)
                foreach (var n in seg.EnumerateObject())
                    if (n.Value.TryGetInt64(out var v) && v > 0)
                        outMap[n.Name] = v;
        }
        catch
        {
        }
        return outMap;
    }

    /// <summary>保存所选小仓库段的目标（保留其他仓段；数量 ≤0 视为清除）</summary>
    public static void SetTargetsFor(string cat, Dictionary<string, long> targets)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var root = new Dictionary<string, Dictionary<string, long>>();
            var f = Path.Combine(DataDir, "targets.json");
            if (File.Exists(f))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    foreach (var c in doc.RootElement.EnumerateObject())
                    {
                        if (c.Name == cat || c.Value.ValueKind != JsonValueKind.Object) continue;
                        var seg = new Dictionary<string, long>();
                        foreach (var n in c.Value.EnumerateObject())
                            if (n.Value.TryGetInt64(out var v) && v > 0)
                                seg[n.Name] = v;
                        root[c.Name] = seg;
                    }
                }
                catch
                {
                }
            }
            var clean = targets.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (clean.Count > 0) root[cat] = clean;
            else root.Remove(cat);
            File.WriteAllText(f, JsonSerializer.Serialize(root, JsonOpts));
        }
        catch
        {
        }
    }

    /// <summary>三仓识别缓存合并（同名材料以 resource 为准；读取失败的分仓静默跳过）</summary>
    public static Dictionary<string, long> LoadInventory()
    {
        var outMap = new Dictionary<string, long>();
        foreach (var cat in AllCats)
        {
            try
            {
                var f = ScanFile(cat);
                if (!File.Exists(f)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (!doc.RootElement.TryGetProperty("counts", out var c) ||
                    c.ValueKind != JsonValueKind.Object) continue;
                foreach (var p in c.EnumerateObject())
                {
                    var v = p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var n)
                        ? n
                        : QtyValue(p.Value.GetString());
                    if (v is > 0 or 0) outMap[p.Name] = v.Value;
                }
            }
            catch
            {
            }
        }
        return outMap;
    }
}
