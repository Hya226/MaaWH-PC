using Avalonia.Media;
using MaaFramework.Binding;
using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Brushes = Avalonia.Media.Brushes;
using MaaTasker = MaaFramework.Binding.MaaTasker;

namespace MFAAvalonia.Helper;

/// <summary>
/// 宿主编排型任务的 PC 分发器（对应手机端 QueueRunner 的 entry 特判分支）。
/// 「仓库识别」= 依次识别 资源→徽章→票券 三仓：每仓先跑导航 pipeline
/// （VF_仓库识别 / VF_仓库识别徽章 / VF_仓库识别票券，引擎负责跳转到对应
/// 物资库页），再由 WarehouseScanner 宿主抓帧识别并落盘 data/warehouse/；
/// 单仓失败记汇总继续下一仓，有失败则任务整体失败。
/// 「智能刷材料」= MaterialKeep 编排（读目标/库存算缺口，循环刷关）。
/// </summary>
public static class OrchestratedTasks
{
    /// <summary>编排型任务名单（与 sync_interface.py 的 ORCHESTRATED_TASKS 清单对应；
    /// 这两个 entry 在清单里，任务包 pipeline 无同名入口节点，必须走宿主编排）</summary>
    public static readonly HashSet<string> Entries = new() { "仓库识别", "智能刷材料" };

    public static bool IsOrchestrated(string? entry) =>
        entry != null && Entries.Contains(entry);

    /// <summary>队列里的编排任务执行入口（返回引擎任务状态口径）</summary>
    public static async Task<MaaJobStatus> RunAsync(MaaProcessor proc, string entry, CancellationToken ct)
    {
        MaaJobStatus status;
        try
        {
            status = entry switch
            {
                "仓库识别" => await RunWarehouseRecognitionAsync(proc, ct),
                "智能刷材料" => RunMaterialKeep(proc, ct),
                _ => MaaJobStatus.Invalid,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(proc, $"任务异常: {ex.Message}", WarehouseScanner.WarehouseLogLevel.Error);
            status = MaaJobStatus.Failed;
        }
        return status;
    }

    // ---------------- 仓库识别 ----------------

    private static async Task<MaaJobStatus> RunWarehouseRecognitionAsync(MaaProcessor proc, CancellationToken ct)
    {
        var tasker = proc.MaaTasker;
        if (tasker?.Controller == null)
        {
            Log(proc, "仓库识别：任务器/控制器不可用——先在连接设置重连模拟器", WarehouseScanner.WarehouseLogLevel.Error);
            return MaaJobStatus.Failed;
        }
        var ocr = ViewModels.Pages.ToolsViewModel.GetSharedOcrEngine();
        var cats = new (string Cat, string NavEntry)[]
        {
            (WarehouseData.CatResource, "VF_仓库识别"),
            (WarehouseData.CatBadge, "VF_仓库识别徽章"),
            (WarehouseData.CatTicket, "VF_仓库识别票券"),
        };
        var failedCats = new List<string>();
        foreach (var (cat, navEntry) in cats)
        {
            ct.ThrowIfCancellationRequested();
            var catName = WarehouseData.CatName(cat);
            Log(proc, $"仓库识别：{catName}——导航到【物资库-{catName}】页…", WarehouseScanner.WarehouseLogLevel.Info);
            var navOk = await RunNavigationAsync(proc, navEntry, ct);
            if (!navOk)
            {
                failedCats.Add($"{catName}（导航失败）");
                Log(proc, $"↷ {catName} 导航失败，跳过继续下一仓", WarehouseScanner.WarehouseLogLevel.Warn);
                continue;
            }
            try
            {
                var scanner = new WarehouseScanner(cat,
                    () => tasker.Controller,
                    () => proc.MaaTasker,
                    ocr,
                    (msg, lv) => Log(proc, msg, lv));
                var r = await scanner.RunAsync(ct);
                WarehouseData.Save(cat, r.Counts);
                Log(proc, $"✓ {catName} 识别完成：{r.Counts.Count} 种（{r.Pages} 页），已保存", WarehouseScanner.WarehouseLogLevel.Success);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedCats.Add($"{catName}（{ex.Message}）");
                Log(proc, $"↷ {catName} 识别失败：{ex.Message}，继续下一仓", WarehouseScanner.WarehouseLogLevel.Warn);
            }
        }
        if (failedCats.Count > 0)
        {
            Log(proc, $"✗ 仓库识别部分未完成：{string.Join("、", failedCats)}", WarehouseScanner.WarehouseLogLevel.Error);
            return MaaJobStatus.Failed;
        }
        return MaaJobStatus.Succeeded;
    }

    /// <summary>跑导航 pipeline（VF_ 入口在任务包 pipeline 里真实存在）：主队列 tasker 上
    /// 同步执行一条链，导航完成即返回（识别由宿主接管）。</summary>
    private static async Task<bool> RunNavigationAsync(MaaProcessor proc, string navEntry, CancellationToken ct)
    {
        var tasker = proc.MaaTasker;
        if (tasker == null) return false;
        try
        {
            var job = tasker.AppendTask(navEntry, "{}");
            await Task.Run(() => job.Wait(), ct);
            return job.Status == MaaJobStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(proc, $"导航异常：{ex.Message}", WarehouseScanner.WarehouseLogLevel.Warn);
            return false;
        }
    }

    // ---------------- 智能刷材料 ----------------

    private static MaaJobStatus RunMaterialKeep(MaaProcessor proc, CancellationToken ct)
    {
        // 「刷取体力上限」输入：v1 暂不接通任务 option 输入链，按不限（0）处理；
        // 接通后从任务选项的 input 值读取
        var keep = new MaterialKeep(
            () => proc.MaaTasker,
            (msg, lv) => Log(proc, msg, lv),
            text => Log(proc, text, WarehouseScanner.WarehouseLogLevel.Info));
        var ok = keep.Run(0, ct);
        return ok ? MaaJobStatus.Succeeded : MaaJobStatus.Failed;
    }

    // ---------------- 日志桥 ----------------

    private static void Log(MaaProcessor proc, string msg, WarehouseScanner.WarehouseLogLevel lv)
    {
        var content = lv switch
        {
            WarehouseScanner.WarehouseLogLevel.Trace => MaaProcessor.TRACE + msg,
            WarehouseScanner.WarehouseLogLevel.Success => MaaProcessor.SUCCESS + msg,
            WarehouseScanner.WarehouseLogLevel.Warn => "warn:" + msg,
            WarehouseScanner.WarehouseLogLevel.Error => "err:" + msg,
            _ => msg,
        };
        proc.AddLog(content, (IBrush?)null);
    }
}
