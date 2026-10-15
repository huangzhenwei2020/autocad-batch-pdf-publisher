using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private WallDrawingWindow? _wallDrawingWindow;
    private WallDrawingChoice _wallDrawingChoice=new();
    private Task? _wallDrawingTask;

    private async Task OpenWallDrawingAsync(bool? curved=null)
    {
        if(_wallDrawingWindow!=null){_wallDrawingWindow.Activate();return;}
        if(_openingEditorBusy)return;
        CancelParameterPreview();CancelMove();
        var storey=(_storeyChooser.SelectedItem as StoreyItem)?.Id??_session.Model.Storeys.FirstOrDefault()?.Id;
        var height=_session.Model.FindStorey(storey)?.Height??3000;
        var choice=curved.HasValue?_wallDrawingChoice with {Curved=curved.Value}:_wallDrawingChoice;
        var window=new WallDrawingWindow(choice,height);_wallDrawingWindow=window;
        try {
            var result=await window.ShowDialog<WallDrawingChoice?>(this);if(result==null)return;
            _wallDrawingChoice=result;_planCanvas.SetWallDrawing(result);
            _workspaces.SelectedIndex=1;SetPlanTool(result.Curved?PlanTool.ArcWall:PlanTool.Wall);
        }finally{_wallDrawingWindow=null;}
    }
    private bool AddDrawnWall(PointModel start,PointModel end,double bulge=0)
    {
        var error=_wallDrawingChoice.Validate();if(error!=null){_status.Text=error;return false;}
        var wall=_wallDrawingChoice.CreateWall((_storeyChooser.SelectedItem as StoreyItem)?.Id??"1F",start,end,bulge);
        if(!_session.TryAddWall(wall,out var id,out error)){_status.Text=error;return false;}
        _selectedId=id;_curveApplyTask=RefreshModelAsync("已新增墙 · Ctrl+Z 可撤销");return true;
    }
}
