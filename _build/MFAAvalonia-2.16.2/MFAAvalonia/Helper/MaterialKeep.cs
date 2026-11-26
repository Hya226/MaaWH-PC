using MaaFramework.Binding;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

/// <summary>
/// 智能刷材料：宿主编排器（手机端 MaterialKeepRunner 的 PC 移植，对标 M9A
/// 「智能均衡刷材料」的库存保持模式，引擎不参与主循环）。
/// 决策链：目标库存（data/warehouse/targets.json）→ 仓库缓存（三仓 scan_&lt;分类&gt;.json
/// 合并）→ 材料关卡目录（assets/warehouse/material_catalog.json）→ 挑覆盖缺口最多的
/// 关卡 → ceil(缺口/掉落) = 次数（单次任务上限 9 局）→ 动态构造 pipeline_override
/// 跑 VF_刷活动关/VF_清体力/VF_实验室 → 本地库存扣账重算，直到全部达标 / 刷关失败
/// （体力不足等）/ 轮数上限。计划内收尾（预算耗尽/体力不足）不算失败——对标 M9A。
/// </summary>
public sealed class MaterialKeep
{
    private readonly Func<MaaTasker?> _taskerGetter;
    private readonly Action<string, WarehouseScanner.WarehouseLogLevel> _onLog;
    private readonly Action<string>? _onProgress;
    private long _spent;
    private int _rounds;
    private string? _stopReason;

    public MaterialKeep(Func<MaaTasker?> taskerGetter,
        Action<string, WarehouseScanner.WarehouseLogLevel> onLog,
        Action<string>? onProgress = null)
    {
        _taskerGetter = taskerGetter;
        _onLog = onLog;
        _onProgress = onProgress;
    }

    /// <param name="sanityBudget">「刷取体力上限」输入值（空/0 = 不限）</param>
    /// <returns>true = 计划内结束（达标/预算耗尽/体力不足/轮数上限）；false = 配置错误</returns>
    public bool Run(long sanityBudget, CancellationToken ct)
    {
        var started = Environment.TickCount64;
        var catalog = WarehouseData.EnsureCatalog();
        if (catalog.Count == 0)
        {
            _onLog("材料目录为空——assets/warehouse/material_catalog.json 缺失或格式错误", WarehouseScanner.WarehouseLogLevel.Error);
            return false;
        }
        var targets = WarehouseData.LoadTargets();
        if (targets.Count == 0)
        {
            _onLog("未设置任何目标数量（编辑 data/warehouse/targets.json：{\"资源\":{\"材料\":数量}}），跳过刷取", WarehouseScanner.WarehouseLogLevel.Warn);
            return true;
        }
        var covered = catalog.SelectMany(e => e.Drop.Keys).ToHashSet();
        var unplannable = targets.Keys.Where(k => !covered.Contains(k)).ToList();
        if (unplannable.Count > 0)
        {
            _onLog($"以下目标材料目录未覆盖（无刷取关卡），跳过：{string.Join("、", unplannable)}", WarehouseScanner.WarehouseLogLevel.Warn);
        }
        var inventory = WarehouseData.LoadInventory();
        var deficits = new Dictionary<string, long>();
        foreach (var (name, target) in targets)
        {
            if (!inventory.TryGetValue(name, out var have))
            {
                _onLog($"{name} 没有库存读数（先跑一次仓库识别），本轮不刷它", WarehouseScanner.WarehouseLogLevel.Warn);
                continue;
            }
            var d = target - have;
            if (d > 0) deficits[name] = d;
        }
        if (deficits.Count == 0)
        {
            _onLog("目标材料均已达标，无需刷取", WarehouseScanner.WarehouseLogLevel.Success);
            return true;
        }
        _onLog(
            $"智能刷材料开始：目标 {targets.Count} 种，有缺口 {deficits.Count} 种（" +
            string.Join("、", deficits.OrderByDescending(kv => kv.Value).Take(5)
                .Select(kv => $"{kv.Key} 缺 {kv.Value}")) + "）",
            WarehouseScanner.WarehouseLogLevel.Info);

        _spent = 0;
        _rounds = 0;
        _stopReason = null;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                _stopReason = "已停止";
                break;
            }
            if (_rounds >= 30)
            {
                _stopReason = "已达轮数上限 30";
                break;
            }
            // 缺口重算（本地缓存随每轮刷取扣账）
            foreach (var (name, target) in targets)
            {
                if (inventory.TryGetValue(name, out var it))
                {
                    var d = target - it;
                    if (d > 0) deficits[name] = d; else deficits.Remove(name);
                }
            }
            if (deficits.Count == 0)
            {
                _onLog("全部目标已达标 ✓", WarehouseScanner.WarehouseLogLevel.Success);
                break;
            }
            // 挑关卡：覆盖缺口材料数最多 → 覆盖缺口总量最大 → 目录靠前者
            var picked = catalog
                .Select(e =>
                {
                    var defs = e.Drop.Keys.Select(m => deficits.TryGetValue(m, out var d) ? d : 0).ToList();
                    return (Entry: e, DefCount: defs.Count(d => d > 0), DefSum: defs.Sum());
                })
                .Where(p => p.DefCount > 0)
                .OrderByDescending(p => p.DefCount)
                .ThenByDescending(p => p.DefSum)
                .ThenBy(p => p.Entry.Index)
                .Select(p => p.Entry)
                .FirstOrDefault();
            if (picked == null)
            {
                _stopReason = "剩余缺口没有目录关卡可覆盖";
                break;
            }
            var runs = picked.Drop
                .Select(kv => deficits.TryGetValue(kv.Key, out var d)
                    ? (int)Math.Ceiling(d / (double)kv.Value) : 0)
                .DefaultIfEmpty(1).Max();
            runs = Math.Clamp(runs, 1, 9);
            if (sanityBudget > 0)
            {
                if (_spent + picked.Cost > sanityBudget)
                {
                    _stopReason = $"已达体力预算 {sanityBudget}（已消耗 {_spent}）";
                    break;
                }
                runs = Math.Min(runs, (int)((sanityBudget - _spent) / picked.Cost));
                if (runs <= 0)
                {
                    _stopReason = $"已达体力预算 {sanityBudget}（已消耗 {_spent}）";
                    break;
                }
            }
            var coveredNames = picked.Drop.Keys.Where(deficits.ContainsKey).ToList();
            _onLog(
                $"第 {_rounds + 1} 轮：{TaskDesc(picked)} ×{runs}（体力约 {picked.Cost * runs}" +
                (sanityBudget > 0 ? $"，预算已用 {_spent}/{sanityBudget}" : "") +
                $"），补 {string.Join("、", coveredNames)}",
                WarehouseScanner.WarehouseLogLevel.Info);
            _onProgress?.Invoke($"智能刷材料：第 {_rounds + 1} 轮 {TaskDesc(picked)} ×{runs}");
            var ok = RunEngineTask(picked.Task, BuildOverride(picked, runs), ct);
            if (!ok)
            {
                _stopReason = "刷关任务失败（体力不足或导航失败）——体力道具暂不支持自动使用";
                break;
            }
            foreach (var (m, per) in picked.Drop)
            {
                inventory[m] = (inventory.TryGetValue(m, out var cur) ? cur : 0) + per * runs;
            }
            _spent += picked.Cost * runs;
            _rounds++;
        }

        var remain = targets
            .Select(kv => inventory.TryGetValue(kv.Key, out var it) ? kv.Value - it : kv.Value)
            .Where(d => d > 0).ToList();
        var elapsed = (Environment.TickCount64 - started) / 1000;
        if (remain.Count == 0)
        {
            _onLog($"✓ 智能刷材料完成：{_rounds} 轮 · 耗时 {elapsed}s · 目标全部达标", WarehouseScanner.WarehouseLogLevel.Success);
        }
        else
        {
            _onLog(
                $"智能刷材料结束：{_rounds} 轮 · 耗时 {elapsed}s · 未达标 {remain.Count} 种（合计缺 {remain.Sum()}）—— {_stopReason}",
                WarehouseScanner.WarehouseLogLevel.Warn);
        }
        return true;
    }

    private string TaskDesc(WarehouseData.CatalogEntry e) => e.Task switch
    {
        "VF_刷活动关" => $"活动关 {e.Params.GetValueOrDefault("关卡名", "")}",
        "VF_清体力" => $"考核 {e.Params.GetValueOrDefault("类型", "")}-{e.Params.GetValueOrDefault("关卡", "")}",
        "VF_实验室" => "实验室",
        _ => e.Task,
    };

    /// <summary>按任务类型构造 pipeline_override（节点名与 interface.json 各 option cases 同源）。
    /// ★ 引擎 repeat 是「额外重复次数」：总刷取次数 = repeat + 1。</summary>
    private static string BuildOverride(WarehouseData.CatalogEntry e, int runs)
    {
        var parts = new List<string>();
        var repeat = Math.Max(runs - 1, 0);
        switch (e.Task)
        {
            case "VF_刷活动关":
            {
                var lv = e.Params.GetValueOrDefault("关卡名", "01");
                parts.Add($"{{\"VF_刷活动关_04\":{{\"expected\":\"{lv}\"}}}}");
                parts.Add($"{{\"VF_刷活动关_03_Hit\":{{\"expected\":\"{lv}\"}}}}");
                parts.Add($"{{\"VF_刷活动关_07\":{{\"repeat\":{repeat}}}}}");
                break;
            }
            case "VF_清体力":
            {
                var t = e.Params.GetValueOrDefault("类型", "");
                parts.Add("{\"VF_清体力_25_Hit\":{\"expected\":\"考核\"}}");
                parts.Add($"{{\"VF_清体力_30_Hit\":{{\"expected\":\"{t}\"}}}}");
                parts.Add($"{{\"VF_清体力_32_Hit\":{{\"expected\":\"{t}\"}}}}");
                parts.Add($"{{\"VF_清体力_33_Hit\":{{\"expected\":\"{e.Params.GetValueOrDefault("关卡", "01")}\"}}}}");
                parts.Add($"{{\"VF_清体力_07\":{{\"repeat\":{repeat}}}}}");
                break;
            }
            case "VF_实验室":
            {
                parts.Add($"{{\"VF_实验室_08\":{{\"repeat\":{repeat}}}}}");
                break;
            }
        }
        // 合并成单对象（各节点键不同，字符串拼接安全）
        var inner = string.Join(",", parts.Select(p => p.Trim('{', '}')));
        return "{" + inner + "}";
    }

    /// <summary>主队列 tasker 上同步跑一条引擎任务链（编排期间引擎空闲，直接复用）。
    /// 手机端 MaaBridge.runTask 每次新建 tasker；PC 端资源/控制器已就绪，复用更稳。</summary>
    private bool RunEngineTask(string entry, string param, CancellationToken ct)
    {
        var tasker = _taskerGetter();
        if (tasker == null)
        {
            _onLog("任务器不可用：模拟器连接是否还在？", WarehouseScanner.WarehouseLogLevel.Error);
            return false;
        }
        try
        {
            var job = tasker.AppendTask(entry, string.IsNullOrEmpty(param) ? "{}" : param);
            Task.Run(() => job.Wait(), ct).Wait();
            return job.Status == MaaJobStatus.Succeeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _onLog($"引擎任务异常：{ex.Message}", WarehouseScanner.WarehouseLogLevel.Warn);
            return false;
        }
    }
}
