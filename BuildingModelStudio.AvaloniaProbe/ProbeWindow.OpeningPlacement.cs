using Avalonia;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private OpeningPlacementWindow? _openingPlacementWindow;
    private string? _lastPlacementCode;
    private string? _openingCopySourceId;

    private void BeginOpeningCopy(OpeningModel source,BuildingModelDocument model)
    {
        CancelParameterPreview();CancelMove();_gizmo.Cancel();
        if(_workspaces.SelectedIndex==1)
        {
            if(IsOpeningCopyPlanReadOnly())
            {_status.Text="当前为引用楼层，请切换到来源层再复制门窗。";return;}
            var type=OpeningConstruction.Copy(OpeningConstruction.Resolve(model,source));
            OpeningConstruction.ResizeType(type,source.Width,source.Height);
            type.Kind=source.Kind;
            SetPlanTool(PlanTool.Opening);
            _planCanvas.SetOpeningPlacement(new OpeningPlacementChoice(type,source.Sill,source.ThresholdHeight,
                source.PlanOpenAngle,source.OpenIn3D??false)
                {FlipAlong=source.PlanFlipAlong,FlipNormal=source.PlanFlipNormal});
        }
        _openingCopySourceId=source.Id;
        _status.Text="复制门窗 CO · 点选目标墙上的洞口中心，可连续复制，Esc 退出。";
    }

    private bool IsOpeningCopyPlanReadOnly()=>_storeyChooser.SelectedItem is StoreyItem floor
        && !string.IsNullOrWhiteSpace(_session.Model.FindStorey(floor.Id)?.TemplateStoreyId);

    private void CancelOpeningCopy()
    {
        if(_openingCopySourceId==null)return;
        _openingCopySourceId=null;
        if(_planCanvas.Tool==PlanTool.Opening)
        {
            _planCanvas.Tool=PlanTool.Select;
            _planCanvas.SetOpeningPlacement(null);
        }
    }

    private Task CopyOpeningInViewportAsync(Point point)
    {
        var wallId=_viewport.PickPlanSurface(point,out var world);
        var wall=_session.Model.Walls.FirstOrDefault(w=>w.Id==wallId);
        if(wall==null){_status.Text="请点选可编辑的目标墙。";return Task.CompletedTask;}
        var axis=CurveGeometry.ForWall(wall);
        return CopyOpeningAtAsync(wall.Id,axis.Parameter(world.X,world.Y)*axis.Length);
    }

    private async Task CopyOpeningAtAsync(string wallId,double offset)
    {
        if(_openingCopySourceId is not string sourceId)return;
        if(_workspaces.SelectedIndex==1&&IsOpeningCopyPlanReadOnly())
        {_status.Text="当前为引用楼层，请切换到来源层再复制门窗。";return;}
        if(!_session.TryCopyOpening(sourceId,wallId,offset,out var id,out var error))
        {_status.Text=error;return;}
        _selectedId=id;
        await RefreshModelAsync("已复制门窗 · 继续点墙复制，Esc 退出，Ctrl+Z 撤销。");
    }

    private async Task OpenOpeningPlacementAsync(string? category=null)
    {
        if(_openingEditorBusy)return;
        if(_openingPlacementWindow!=null){_openingPlacementWindow.Activate();return;}
        CancelParameterPreview();CancelMove();
        var revision=_session.Revision;
        var window=new OpeningPlacementWindow(_session.Model,_lastPlacementCode,category,
            type=>_session.TrySaveOpeningTemplate(type.Code,type,out var error)?null:error);
        _openingPlacementWindow=window;
        try {
            var choice=await window.ShowDialog<OpeningPlacementChoice?>(this);
            if(revision!=_session.Revision)await RefreshModelAsync("已保存门窗模板");
            if(choice==null)return;
            var type=choice.Type;
            _workspaces.SelectedIndex=1;SetPlanTool(PlanTool.Opening);_planCanvas.SetOpeningPlacement(choice);
            _lastPlacementCode=type.Code;
            _status.Text=$"门窗 MM · {type.Code} · {type.Width:0.#} × {type.Height:0.#} mm · 点墙放置，Esc 退出";
        } finally {_openingPlacementWindow=null;}
    }
}
