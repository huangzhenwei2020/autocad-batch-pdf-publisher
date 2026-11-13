using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Rendering.Composition;
using Avalonia.Media.Imaging;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task<StudioWorkspaceState> CheckWorkbenchEditingAsync()
    {
        var before = _session.Model.Walls.Count;
        if (!_session.TryAddWall(new WallModel { StoreyId = _session.Model.Storeys[0].Id,
                X1 = 1000, Y1 = 1000, X2 = 4000, Y2 = 1000, Thickness = 200 }, out _, out var error))
            throw new InvalidOperationException(error);
        if (_session.Model.Walls.Count != before + 1 || !_session.Undo()
            || _session.Model.Walls.Count != before || !_session.Redo())
            throw new InvalidOperationException("集成后的模型编辑或撤销重做失败。");
        await RefreshModelAsync("工作台集成绘制检查");
        _workspaces.SelectedIndex = 1;
        await Task.Delay(80);
        _workspaces.SelectedIndex = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var dialog = OwnedWindows.FirstOrDefault(window => window.Title == "未保存的修改");
            var cancel = dialog?.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Content as string == "取消");
            if (cancel == null) return;
            cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            timer.Stop();
        };
        timer.Start();
        try
        {
            if (await ConfirmSavedAsync() || !HasChanges)
                throw new InvalidOperationException("取消保存确认未保留模型草稿。");
        }
        finally { timer.Stop(); }
        var visual = ElementComposition.GetElementVisual((Avalonia.Visual)Content!)!;
        using var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
        bitmap.Save(Path.Combine(Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT")!, "studio-embedded.png"), PngBitmapEncoderOptions.Default);
        return WorkbenchState(true, "STUDIO_EMBED_EDIT_OK wall plan3D undo redo cancelPreservesDraft");
    }
}
