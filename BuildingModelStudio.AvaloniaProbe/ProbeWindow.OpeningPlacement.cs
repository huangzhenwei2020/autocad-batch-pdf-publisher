using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private OpeningPlacementWindow? _openingPlacementWindow;
    private string? _lastPlacementCode;
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
