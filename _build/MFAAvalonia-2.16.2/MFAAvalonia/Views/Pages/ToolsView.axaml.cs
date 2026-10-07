using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;

namespace MFAAvalonia.Views.Pages;

public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();
        if (!Design.IsDesignMode)
        {
            DataContext = Instances.ToolsViewModel;
            // 扫描日志自动滚底：新行到达时跟随最新内容
            Instances.ToolsViewModel.WaiQin.Logs.CollectionChanged += (_, args) =>
            {
                if (args.Action != NotifyCollectionChangedAction.Add || WaiQinLogList.ItemCount == 0)
                    return;
                Dispatcher.UIThread.Post(() =>
                    WaiQinLogList.ScrollIntoView(WaiQinLogList.Items[^1]), DispatcherPriority.Background);
            };
            Instances.ToolsViewModel.Gacha.Logs.CollectionChanged += (_, args) =>
            {
                if (args.Action != NotifyCollectionChangedAction.Add || GachaLogList.ItemCount == 0)
                    return;
                Dispatcher.UIThread.Post(() =>
                    GachaLogList.ScrollIntoView(GachaLogList.Items[^1]), DispatcherPriority.Background);
            };
        }
    }
}
