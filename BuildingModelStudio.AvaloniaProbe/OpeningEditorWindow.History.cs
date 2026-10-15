using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;
using System.Globalization;
using Avalonia.Controls;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class OpeningEditorWindow
{
    private sealed record DraftState(OpeningTypeModel Type,double Width,double Height,int Face,bool Single,
        double LayoutWidth,double LayoutHeight,string Layout,int[] Selected,double Snap,
        Dictionary<string,string?> Fields,Dictionary<string,OpeningTypeModel> Templates,string Signature);
    private readonly Stack<DraftState> _draftUndo=new(),_draftRedo=new();
    private DraftState? _acceptedDraft;
    private string? _pendingEditKey,_lastHistoryKey;
    private bool _restoringDraft,_applyingCellSize;

    private DraftState CaptureDraft()
    {
        var model=new BuildingModelDocument {OpeningTypes=new(){_draft},
            OpeningTemplates=_templates.Select(t=>{var c=OpeningConstruction.Copy(t.Value);c.Code=t.Key;return c;}).ToList(),OpeningEditorSnapStep=_canvas.Snap};
        var signature=BuildingModelJson.ToJson(model)+":"+_opening.Width.ToString("R",CultureInfo.InvariantCulture)
            +":"+_opening.Height.ToString("R",CultureInfo.InvariantCulture)+":"+OnlyInstance;
        return new(OpeningConstruction.Copy(_draft),_opening.Width,_opening.Height,_loadedFace,OnlyInstance,
            _canvas.State.Width,_canvas.State.Height,DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells),
            _canvas.Selected.ToArray(),_canvas.Snap,_fields.ToDictionary(p=>p.Key,p=>p.Value.Text),
            _templates.ToDictionary(p=>p.Key,p=>OpeningConstruction.Copy(p.Value)),signature);
    }
    private void InitializeDraftHistory()=>_acceptedDraft=CaptureDraft();
    private static double Millimetres(double input,double step=1)=>Math.Round(input/step,0,MidpointRounding.AwayFromZero)*step;
    private double NormalizeDimension(string key,double input)
    {
        var rounded=Millimetres(input,key is "width" or "height" or "leftDepth" or "rightDepth"?10:1);
        var limits=OnlyInstance?_singleSizeLimits:_groupSizeLimits;
        if(limits==null||key is not ("width" or "height"))return rounded;
        var fitted=OpeningConstruction.FitRoundedOpeningSize(rounded,limits[key=="width"?0:1]);
        if(fitted!=rounded)_sizeFitNotice=$"已按宿主范围自动适配洞口{(key=="width"?"宽":"高")}为 {fitted:0} mm";
        return fitted;
    }
    private void IntegerInput(TextBox input,string key)=>input.LostFocus+=(_,_)=>{
        if(_updating||key=="angle"||!Number(key,out var value))return;
        _updating=true;
        input.Text=NormalizeDimension(key,value).ToString("0",CultureInfo.InvariantCulture);
        _updating=false;
    };
    private double NormalizeDisplayed(string key,double original,double input)=>NormalizeDimension(key,input);
    private void IntegerLayout()
    {
        var cells=DoorWindowElevationGeometryBuilder.ParseCellLayout(DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells));
        foreach(var c in cells){c.Left=Millimetres(c.Left);c.Right=Millimetres(c.Right);c.Bottom=Millimetres(c.Bottom);c.Top=Millimetres(c.Top);}
        var w=Millimetres(_canvas.State.Width);var h=Millimetres(_canvas.State.Height);
        if(DoorWindowElevationGeometryBuilder.SerializeCellLayout(cells)!=DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells)
            ||w!=_canvas.State.Width||h!=_canvas.State.Height)_canvas.State=new(w,h,cells);
    }

    private bool ApplyLiveCellSize(bool force=false)
    {
        if(!force&&_pendingEditKey is not ("cellWidth" or "cellHeight"))return true;
        if(_canvas.Selected.Count!=1)return true;
        var index=_canvas.Selected.Single();var cell=_canvas.State.Cells[index];
        var width=cell.Right-cell.Left;var height=cell.Top-cell.Bottom;
        if((force||_pendingEditKey=="cellWidth")&&(!Number("cellWidth",out width)||width<=0)){
            _status.Text="格宽请输入有效正数。";return false;}
        if((force||_pendingEditKey=="cellHeight")&&(!Number("cellHeight",out height)||height<=0)){
            _status.Text="格高请输入有效正数。";return false;}
        width=Millimetres(width);height=Millimetres(height);
        if(Math.Abs(width-(cell.Right-cell.Left))<.001&&Math.Abs(height-(cell.Top-cell.Bottom))<.001)return true;
        if(!_canvas.State.SetCellSize(index,width,height,out var error)){_status.Text=error;return false;}
        _applyingCellSize=true;
        try{_canvas.Notify();}finally{_applyingCellSize=false;}
        return true;
    }
    private bool AcceptDraft()
    {
        if(!ReadDraft()||!ApplyLiveCellSize())return false;
        StoreFace();var error=OpeningConstruction.Validate(_opening,_draft);
        if(error!=null){_status.Text=error;return false;}
        _canvas.Type=OpeningConstruction.Copy(_draft);_canvas.InvalidateVisual();
        if(!_restoringDraft&&!_canvas.IsDraggingDivider){
            var current=CaptureDraft();
            if(_acceptedDraft!=null&&current.Signature!=_acceptedDraft.Signature){
                if(_pendingEditKey==null||_pendingEditKey!=_lastHistoryKey){
                    if(_draftUndo.Count>=128){var keep=_draftUndo.Take(127).Reverse().ToArray();_draftUndo.Clear();foreach(var state in keep)_draftUndo.Push(state);}
                    _draftUndo.Push(_acceptedDraft);
                }
                _draftRedo.Clear();_lastHistoryKey=_pendingEditKey;
            }
            _acceptedDraft=current;
        }
        return true;
    }
    private void RestoreDraft(DraftState state)
    {
        _timer.Stop();++_generation;_restoringDraft=true;_updating=true;
        try {
            _draft=OpeningConstruction.Copy(state.Type);_opening.Width=state.Width;_opening.Height=state.Height;
            _scope.SelectedIndex=state.Single?1:0;_face.SelectedIndex=state.Face;
            _templates.Clear();foreach(var p in state.Templates)_templates[p.Key]=OpeningConstruction.Copy(p.Value);
            _templateChooser.ItemsSource=_templates.Keys.ToArray();
            LoadControls();_updating=true;LoadFace();_updating=true;
            _canvas.State=new(state.LayoutWidth,state.LayoutHeight,DoorWindowElevationGeometryBuilder.ParseCellLayout(state.Layout));
            _canvas.Snap=state.Snap;_canvas.Selected.Clear();_canvas.Selected.UnionWith(state.Selected.Where(i=>i<_canvas.State.Cells.Count));
            foreach(var field in state.Fields)_fields[field.Key].Text=field.Value;
            _canvas.Notify(false);
            _acceptedDraft=state;_lastHistoryKey=null;
        }finally{_updating=false;_restoringDraft=false;}
        QueuePreview();
    }
    private void UndoDraft(bool redo=false)
    {
        if(_canvas.CancelInteraction())return;
        if(!AcceptDraft()){if(_acceptedDraft!=null)RestoreDraft(_acceptedDraft);return;}
        var from=redo?_draftRedo:_draftUndo;var to=redo?_draftUndo:_draftRedo;
        if(from.Count==0){_status.Text=redo?"没有可重做的草稿操作。":"没有可撤销的草稿操作。";return;}
        to.Push(_acceptedDraft!);RestoreDraft(from.Pop());
    }
}
