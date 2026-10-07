using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MFAAvalonia.Helper;

/// <summary>
/// 抽卡记录数据层（PC 端单账号简化版）。存储在 exe 旁 data/gacha/：
/// - all_records.json：全量记录，新→旧。游戏只留 30 天，这里永久累积。
/// - config.json：每池最新 5 条的（时间, 名字）作「锚点」+ 上次抓取时间。
///   后续抓取命中锚点即截断翻页；翻到最后一页都没命中（记录过期 &gt;30 天）
///   就靠 uid 去重全量合并兜底。
///
/// JSON 字段与手机端 GachaStore 完全一致（uid 规则相同），手机端导出的
/// all_records.json / config.json 可直接拷进 data/gacha 继续累积。
/// uid 规则：[纯数字时间]_[池子]_[器者名]_[序号]，序号按「同分钟同池」分组编号。
/// </summary>
public static class GachaStore
{
    public const string RarityTop = "特出";
    public const string RarityMid = "优异";
    public const string RarityLow = "新生";

    private const int AnchorsPerPool = 5;

    public sealed record Record(
        string Uid, long Ts, string Time, string Pool, string Banner, string Name, string Rarity,
        bool Manual = false);

    /// <summary>爬虫产出的一行（uid 未定），交 BuildRecords 编号</summary>
    public sealed record RawRow(long Ts, string Time, string Name, string Rarity, string Banner);

    public sealed record Anchor(long T, string N);

    public sealed class Config
    {
        public Dictionary<string, List<Anchor>> Anchors { get; init; } = [];
        public long LastCrawlMs { get; init; }
        public bool IsFirstRun => Anchors.Count == 0;
    }

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "data", "gacha");

    // ---- 多账号（与手机端 accounts.json 同格式；记录/锚点/UP 标注按账号目录隔离）----

    public sealed record AccountInfo(string Id, string Name, long CreatedAt);

    private static string RegistryFile => Path.Combine(Dir, "accounts.json");
    private static string AccountsDir => Path.Combine(Dir, "accounts");

    private static string? _activeIdCache;

    /// <summary>当前活跃账号的数据目录。首次访问把散装旧数据（all_records.json 等）
    /// 整体迁移成「默认」账号，用户无感（手机端同款逻辑）。</summary>
    private static string CurrentDir()
    {
        var reg = EnsureRegistry();
        return Path.Combine(AccountsDir, reg.ActiveId);
    }

    private static (string ActiveId, List<AccountInfo> Accounts) EnsureRegistry()
    {
        lock (IoLock)
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(RegistryFile))
            {
                try
                {
                    var o = JObject.Parse(File.ReadAllText(RegistryFile));
                    var list = (o["accounts"] as JArray)?.Select(a => new AccountInfo(
                        a.Value<string?>("id") ?? "",
                        a.Value<string?>("name") ?? "",
                        a.Value<long?>("createdAt") ?? 0)).ToList() ?? [];
                    if (list.Count > 0)
                    {
                        var active = o.Value<string?>("activeId") ?? "";
                        if (list.All(a => a.Id != active)) active = list[0].Id;
                        _activeIdCache = active;
                        return (active, list);
                    }
                }
                catch
                {
                    /* 注册表损坏则走重建 */
                }
            }
            // 首次/重建：散装数据（旧版单账号布局）迁移为「默认」账号
            var hasLegacy = File.Exists(Path.Combine(Dir, "all_records.json"))
                            || File.Exists(Path.Combine(Dir, "config.json"));
            var id = hasLegacy ? "default" : "a" + DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var name = hasLegacy ? "默认" : "账号1";
            var d = Path.Combine(AccountsDir, id);
            Directory.CreateDirectory(d);
            if (hasLegacy)
            {
                foreach (var f in new[] { "all_records.json", "config.json", "up_marks.json" })
                {
                    var src = Path.Combine(Dir, f);
                    if (File.Exists(src))
                        File.Move(src, Path.Combine(d, f));
                }
            }
            var accounts = new List<AccountInfo> { new(id, name, DateTimeOffset.Now.ToUnixTimeMilliseconds()) };
            SaveRegistryLocked(id, accounts);
            _activeIdCache = id;
            return (id, accounts);
        }
    }

    private static void SaveRegistryLocked(string activeId, List<AccountInfo> accounts)
    {
        var o = new JObject
        {
            ["activeId"] = activeId,
            ["accounts"] = new JArray(accounts.Select(a => new JObject
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["createdAt"] = a.CreatedAt,
            })),
        };
        AtomicWrite(RegistryFile, o.ToString(Newtonsoft.Json.Formatting.None));
    }

    public static List<AccountInfo> ListAccounts() => EnsureRegistry().Accounts;

    public static string ActiveAccountId() => EnsureRegistry().ActiveId;

    public static string ActiveAccountName()
    {
        var (active, list) = EnsureRegistry();
        return list.FirstOrDefault(a => a.Id == active)?.Name ?? "";
    }

    public static void SetActiveAccount(string id)
    {
        lock (IoLock)
        {
            var (_, list) = EnsureRegistry();
            if (list.Any(a => a.Id == id) && _activeIdCache != id)
            {
                SaveRegistryLocked(id, list);
                _activeIdCache = id;
            }
        }
    }

    public static AccountInfo CreateAccount(string? name)
    {
        lock (IoLock)
        {
            var (_, list) = EnsureRegistry();
            var info = new AccountInfo(
                "a" + DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                string.IsNullOrWhiteSpace(name) ? $"账号{list.Count + 1}" : name.Trim(),
                DateTimeOffset.Now.ToUnixTimeMilliseconds());
            Directory.CreateDirectory(Path.Combine(AccountsDir, info.Id));
            list.Add(info);
            SaveRegistryLocked(info.Id, list);
            _activeIdCache = info.Id;
            return info;
        }
    }

    public static void RenameAccount(string id, string name)
    {
        lock (IoLock)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var (active, list) = EnsureRegistry();
            var idx = list.FindIndex(a => a.Id == id);
            if (idx < 0) return;
            list[idx] = list[idx] with { Name = name.Trim() };
            SaveRegistryLocked(active, list);
        }
    }

    /// <summary>
    /// 删除账号及其全部记录；至少保留一个。删除前整目录收进 trash/（滚动保留最近 3 份），
    /// 可手动挪回 accounts/ 恢复。返回是否成功。
    /// </summary>
    public static bool DeleteAccount(string id)
    {
        lock (IoLock)
        {
            var (active, list) = EnsureRegistry();
            if (list.Count <= 1) return false;
            var acc = list.FirstOrDefault(a => a.Id == id);
            if (acc == null || !list.Remove(acc)) return false;
            var newActive = active == id ? list[0].Id : active;
            SaveRegistryLocked(newActive, list);
            _activeIdCache = newActive;

            var d = Path.Combine(AccountsDir, id);
            if (Directory.Exists(d))
            {
                var trash = Path.Combine(Dir, "trash");
                Directory.CreateDirectory(trash);
                var dest = Path.Combine(trash, $"{id}_{DateTimeOffset.Now.ToUnixTimeSeconds()}");
                try { Directory.Move(d, dest); } catch { /* 目录不在则忽略 */ }
                // 滚动清理：只留最近 3 份
                var dirs = Directory.GetDirectories(trash).OrderByDescending(x => x).ToList();
                foreach (var old in dirs.Skip(3))
                {
                    try { Directory.Delete(old, true); } catch { /* ignore */ }
                }
            }
            return true;
        }
    }

    private static string RecordsFile => Path.Combine(CurrentDir(), "all_records.json");
    private static string ConfigFile => Path.Combine(CurrentDir(), "config.json");

    // ---------- uid ----------

    public static string UidOf(long ts, string pool, string name, int seq) =>
        $"{FormatMinute(ts)}_{pool}_{name}_{seq}";

    /// <summary>epoch 分钟 → uid 用纯数字串 "202609101046"</summary>
    public static string FormatMinute(long ts) =>
        DateTimeOffset.FromUnixTimeSeconds(ts * 60).LocalDateTime.ToString("yyyyMMddHHmm");

    /// <summary>
    /// 卡池小类就近归并判定：OCR 错 1 字的变形要并回正字。短串（≤6 字符）容差 1、
    /// 长串容差 2——四字池名带前缀后两两距离 ≥3，容差 2 不会误并两个真实池。
    /// </summary>
    public static bool BannerMatch(string a, string b) =>
        WaiQinScan.Levenshtein(a, b) <= (Math.Min(a.Length, b.Length) <= 6 ? 1 : 2);

    /// <summary>
    /// 把一个池子本页抓到的行（按游戏列表顺序，新→旧）编成 Record。
    /// 序号在（分钟, 池子）内从 1 递增；runningSeq 由爬虫跨页持有。
    /// </summary>
    public static List<Record> BuildRecords(
        string pool, List<RawRow> rows, Dictionary<long, int> runningSeq)
    {
        var list = new List<Record>(rows.Count);
        foreach (var r in rows)
        {
            var n = (runningSeq.TryGetValue(r.Ts, out var v) ? v : 0) + 1;
            runningSeq[r.Ts] = n;
            list.Add(new Record(UidOf(r.Ts, pool, r.Name, n), r.Ts, r.Time, pool, r.Banner, r.Name, r.Rarity));
        }
        return list;
    }

    /// <summary>epoch 分钟 → 面板短格式 "09-10 10:46"</summary>
    public static string ShortTime(long ts) =>
        DateTimeOffset.FromUnixTimeSeconds(ts * 60).LocalDateTime.ToString("MM-dd HH:mm");

    // ---------- 读写 ----------

    private static void AtomicWrite(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, text);
        try
        {
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }
        catch (IOException)
        {
            File.WriteAllText(file, text);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static readonly object IoLock = new();

    public static List<Record> LoadRecords()
    {
        lock (IoLock)
        {
            try
            {
                if (!File.Exists(RecordsFile)) return [];
                var arr = JArray.Parse(File.ReadAllText(RecordsFile));
                return arr.Select(o => new Record(
                    o.Value<string?>("uid") ?? "",
                    o.Value<long?>("ts") ?? 0,
                    o.Value<string?>("time") ?? "",
                    o.Value<string?>("pool") ?? "",
                    o.Value<string?>("banner") ?? "",
                    o.Value<string?>("name") ?? "",
                    o.Value<string?>("rarity") ?? "",
                    o.Value<bool?>("manual") ?? false
                )).ToList();
            }
            catch
            {
                return [];
            }
        }
    }

    public static Config LoadConfig()
    {
        lock (IoLock)
        {
            try
            {
                if (!File.Exists(ConfigFile)) return new Config();
                var o = JObject.Parse(File.ReadAllText(ConfigFile));
                var map = new Dictionary<string, List<Anchor>>();
                if (o["anchors"] is JObject anchors)
                {
                    foreach (var (key, token) in anchors)
                    {
                        if (token is not JArray arr) continue;
                        map[key] = arr.Select(e => new Anchor(
                            e.Value<long?>("t") ?? 0,
                            e.Value<string?>("n") ?? ""
                        )).ToList();
                    }
                }
                return new Config { Anchors = map, LastCrawlMs = o.Value<long?>("lastCrawlMs") ?? 0 };
            }
            catch
            {
                return new Config();
            }
        }
    }

    /// <summary>
    /// 抓取收口：合并新记录（uid 去重）→ 刷新每池锚点 → 记时间。返回新增条数。
    /// </summary>
    public static int CommitCrawl(Dictionary<string, List<Record>> freshByPool)
    {
        lock (IoLock)
        {
            var existing = LoadRecords();
            var seen = new HashSet<string>(existing.Select(r => r.Uid));
            var added = 0;
            foreach (var fresh in freshByPool.Values)
            {
                foreach (var r in fresh)
                {
                    if (!seen.Add(r.Uid)) continue;
                    existing.Add(r);
                    added++;
                }
            }
            // 新→旧；同分钟保持抓取顺序（List.Sort 不稳定，改用 OrderByDescending 稳定排序）
            existing = existing.OrderByDescending(r => r.Ts).ToList();
            AtomicWrite(RecordsFile, SerializeRecords(existing));

            var anchors = new JObject();
            foreach (var g in existing.GroupBy(r => r.Pool))
            {
                anchors[g.Key] = new JArray(g.Take(AnchorsPerPool).Select(r => new JObject
                {
                    ["t"] = r.Ts,
                    ["n"] = r.Name,
                }));
            }
            AtomicWrite(ConfigFile, new JObject
            {
                ["anchors"] = anchors,
                ["lastCrawlMs"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            }.ToString(Newtonsoft.Json.Formatting.None));

            NormalizeBanners();
            return added;
        }
    }

    /// <summary>
    /// 卡池小类（banner）存量归并：同池内按条数降序选正字，容差内（BannerMatch）
    /// 的少数派写法全部并入。uid/锚点不含 banner，改写零副作用。
    /// </summary>
    public static int NormalizeBanners()
    {
        lock (IoLock)
        {
            var all = LoadRecords();
            var counts = new Dictionary<string, Dictionary<string, int>>();
            foreach (var r in all)
            {
                if (string.IsNullOrEmpty(r.Banner)) continue;
                var m = counts.TryGetValue(r.Pool, out var v) ? v : counts[r.Pool] = [];
                m[r.Banner] = m.TryGetValue(r.Banner, out var c) ? c + 1 : 1;
            }
            var mapping = new Dictionary<string, Dictionary<string, string>>();
            foreach (var (pool, cnt) in counts)
            {
                var reps = new List<string>();
                var m = new Dictionary<string, string>();
                foreach (var b in cnt.OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
                {
                    var rep = reps.FirstOrDefault(it => BannerMatch(b, it));
                    if (rep != null) m[b] = rep;
                    else reps.Add(b);
                }
                if (m.Count > 0) mapping[pool] = m;
            }
            if (mapping.Count == 0) return 0;
            var changed = 0;
            for (var i = 0; i < all.Count; i++)
            {
                var r = all[i];
                if (r.Banner.Length == 0) continue;
                var nb = mapping.TryGetValue(r.Pool, out var mm) && mm.TryGetValue(r.Banner, out var n2) ? n2 : null;
                if (nb == null) continue;
                all[i] = r with { Banner = nb };
                changed++;
            }
            if (changed > 0)
                AtomicWrite(RecordsFile, SerializeRecords(all));
            return changed;
        }
    }

    private static string SerializeRecords(IReadOnlyList<Record> list)
    {
        var arr = new JArray();
        foreach (var r in list)
        {
            var o = new JObject
            {
                ["uid"] = r.Uid,
                ["ts"] = r.Ts,
                ["time"] = r.Time,
                ["pool"] = r.Pool,
                ["banner"] = r.Banner,
                ["name"] = r.Name,
                ["rarity"] = r.Rarity,
            };
            if (r.Manual) o["manual"] = true;
            arr.Add(o);
        }
        return arr.ToString(Newtonsoft.Json.Formatting.None);
    }

    // ---------- UP 标注（pool → banner → UP 器者名；与手机端 up_marks.json 同格式） ----------

    private static string UpMarksFile => Path.Combine(CurrentDir(), "up_marks.json");

    /// <summary>读取 UP 标注；值为空串视同未标注</summary>
    public static Dictionary<string, Dictionary<string, string>> LoadUpMarks()
    {
        lock (IoLock)
        {
            try
            {
                if (!File.Exists(UpMarksFile)) return [];
                var o = JObject.Parse(File.ReadAllText(UpMarksFile));
                var outMap = new Dictionary<string, Dictionary<string, string>>();
                foreach (var (pk, po) in o)
                {
                    if (po is not JObject m) continue;
                    var inner = new Dictionary<string, string>();
                    foreach (var (bk, bv) in m)
                        inner[bk] = bv.Value<string>() ?? "";
                    outMap[pk] = inner;
                }
                return outMap;
            }
            catch
            {
                return [];
            }
        }
    }

    public static void SaveUpMarks(Dictionary<string, Dictionary<string, string>> marks)
    {
        lock (IoLock)
        {
            var o = new JObject();
            foreach (var (pk, m) in marks)
            {
                var po = new JObject();
                foreach (var (bk, v) in m)
                    if (!string.IsNullOrWhiteSpace(v)) po[bk] = v.Trim();
                if (po.Count > 0) o[pk] = po;
            }
            AtomicWrite(UpMarksFile, o.ToString(Newtonsoft.Json.Formatting.None));
        }
    }

    /// <summary>
    /// 标注/取消某池某小类的 UP 器者：再点同一人 = 取消。返回标注生效后的 UP 名（空串=未标注）。
    /// </summary>
    public static string ToggleUpMark(string pool, string banner, string name)
    {
        lock (IoLock)
        {
            var marks = LoadUpMarks();
            if (!marks.TryGetValue(pool, out var m))
                marks[pool] = m = [];
            m.TryGetValue(banner, out var cur);
            if (cur == name) m.Remove(banner);
            else m[banner] = name;
            SaveUpMarks(marks);
            return cur == name ? "" : name;
        }
    }

    // ---------- 手动补录 / 删除 ----------

    /// <summary>
    /// 手动补录一条记录（30 天窗口外的旧卡池）。不改锚点——补录多为旧数据，
    /// 不应影响下次抓取的截断点。序号取该（分钟,池）组内已有最大序号 +1。
    /// </summary>
    public static Record AddManualRecord(string pool, string banner, string name, string rarity, long ts)
    {
        lock (IoLock)
        {
            var all = LoadRecords();
            var maxSeq = 0;
            foreach (var r in all)
            {
                if (r.Pool == pool && r.Ts == ts)
                {
                    var tail = r.Uid.Split('_')[^1];
                    if (int.TryParse(tail, out var seqNum) && seqNum > maxSeq) maxSeq = seqNum;
                }
            }
            var taken = new HashSet<string>(all.Select(r => r.Uid));
            var seq = maxSeq + 1;
            var uid = UidOf(ts, pool, name, seq);
            while (!taken.Add(uid))
            {
                seq++;
                uid = UidOf(ts, pool, name, seq);
            }
            var rec = new Record(uid, ts, ChineseTime(ts), pool, banner, name, rarity, Manual: true);
            all.Add(rec);
            all = all.OrderByDescending(r => r.Ts).ToList();
            AtomicWrite(RecordsFile, SerializeRecords(all));
            return rec;
        }
    }

    /// <summary>删除手动补录的记录（非补录条目拒绝删除）。返回是否成功</summary>
    public static bool DeleteManualRecord(string uid)
    {
        lock (IoLock)
        {
            var all = LoadRecords();
            var target = all.FirstOrDefault(r => r.Uid == uid);
            if (target == null || !target.Manual) return false;
            all.Remove(target);
            AtomicWrite(RecordsFile, SerializeRecords(all));
            return true;
        }
    }

    /// <summary>epoch 分钟 → 游戏风格原文 "2026年8月1日10时30分"（手动补录的 time 字段用）</summary>
    public static string ChineseTime(long ts) =>
        DateTimeOffset.FromUnixTimeSeconds(ts * 60).LocalDateTime.ToString("yyyy年M月d日H时m分");

    // ---------- 器者名单（OCR 纠错字典；全局共享，与账号无关） ----------

    /// <summary>本地名单副本（用户可编辑）；随包 assets/gacha/names.json 只做差集合并</summary>
    private static string NamesFile => Path.Combine(Dir, "names.json");

    private static readonly object NamesLock = new();

    public static List<string> LoadNames()
    {
        lock (NamesLock)
        {
            Directory.CreateDirectory(Dir);
            var local = new List<string>();
            var dirty = false;
            if (File.Exists(NamesFile))
            {
                try
                {
                    local = JArray.Parse(File.ReadAllText(NamesFile))
                        .Select(t => (string?)t ?? "").Where(x => x.Length > 0).ToList();
                }
                catch
                {
                    local = [];
                }
            }
            // 随包名单升级走差集合并：内置新增的名字补进本地，用户手加的条目不动
            try
            {
                var bundledPath = Path.Combine(AppContext.BaseDirectory, "assets", "gacha", "names.json");
                if (File.Exists(bundledPath))
                {
                    var bundled = JArray.Parse(File.ReadAllText(bundledPath))
                        .Select(t => (string?)t ?? "").Where(x => x.Length > 0).ToList();
                    var set = new HashSet<string>(local);
                    foreach (var n in bundled)
                    {
                        if (set.Add(n))
                        {
                            local.Add(n);
                            dirty = true;
                        }
                    }
                }
            }
            catch
            {
                /* 内置缺失不阻塞 */
            }
            if (dirty || local.Count == 0 && !File.Exists(NamesFile))
                AtomicWrite(NamesFile, new JArray(local).ToString(Newtonsoft.Json.Formatting.None));
            return local;
        }
    }

    public static void SaveNames(IEnumerable<string> names)
    {
        lock (NamesLock)
        {
            var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().ToList();
            AtomicWrite(NamesFile, new JArray(list).ToString(Newtonsoft.Json.Formatting.None));
        }
    }

    /// <summary>添加名字（已存在返回 false）</summary>
    public static bool AddName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (NamesLock)
        {
            var list = LoadNames();
            if (!list.Contains(name.Trim())) list.Add(name.Trim());
            else return false;
            SaveNames(list);
            return true;
        }
    }

    /// <summary>删除名字</summary>
    public static void RemoveName(string name)
    {
        lock (NamesLock)
        {
            SaveNames(LoadNames().Where(n => n != name).ToList());
        }
    }

    // ---------- 统计（rs 必须新→旧） ----------

    public sealed record PoolStats(int Total, int TeCount, string AvgText, int Dian);

    public sealed record UpStats(int Total, int TeCount, int WaiCount, string UpAvgText, bool Marked);

    /// <summary>
    /// UP 统计：歪 = 特出中与所属小类标注 UP 不符的数量（未标注的小类不判歪）。
    /// marked = 该池标注过 UP 才显示「UP 平均」，否则只出「特出平均」。
    /// </summary>
    public static UpStats UpStatistics(IReadOnlyList<Record> rs, Dictionary<string, string> poolUpMarks)
    {
        var total = rs.Count;
        var te = rs.Count(r => r.Rarity == RarityTop);
        var wai = 0;
        foreach (var r in rs)
        {
            if (r.Rarity != RarityTop) continue;
            if (!poolUpMarks.TryGetValue(r.Banner, out var up)) continue;
            if (!string.IsNullOrWhiteSpace(up) && up != r.Name) wai++;
        }
        var marked = poolUpMarks.Values.Any(v => !string.IsNullOrWhiteSpace(v));
        var upCount = te - wai;
        var upAvg = !marked || upCount <= 0 ? "—" : (total / (double)upCount).ToString("F1");
        return new UpStats(total, te, wai, upAvg, marked);
    }

    public static PoolStats Stats(IEnumerable<Record> rs)
    {
        var list = rs as IList<Record> ?? rs.ToList();
        var total = list.Count;
        var te = list.Count(r => r.Rarity == RarityTop);
        var dian = 0;
        foreach (var r in list)
        {
            if (r.Rarity == RarityTop) break;
            dian++;
        }
        return new PoolStats(total, te, te == 0 ? "—" : (total / (double)te).ToString("F1"), dian);
    }

    /// <summary>
    /// 每条特出的消耗抽数 = 从上一个（更旧的）特出之后到这一发（含）的抽数，
    /// 即两特出在列表里的位置差。窗口内最早的特出按「窗口首条 = 第一抽」计。
    /// </summary>
    public static List<(Record Rec, string Cost)> TeListWithCost(IReadOnlyList<Record> rs)
    {
        var teIdx = new List<int>();
        for (var i = 0; i < rs.Count; i++)
            if (rs[i].Rarity == RarityTop) teIdx.Add(i);
        var outList = new List<(Record, string)>(teIdx.Count);
        for (var k = 0; k < teIdx.Count; k++)
        {
            // rs 新→旧，更旧的特出索引更大
            var idx = teIdx[k];
            var cost = k + 1 < teIdx.Count ? $"{teIdx[k + 1] - idx} 抽" : $"{rs.Count - idx} 抽";
            outList.Add((rs[idx], cost));
        }
        return outList;
    }
}
