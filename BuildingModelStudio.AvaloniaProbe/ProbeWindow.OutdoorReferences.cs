using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private int _outdoorReferenceMode;
    private bool _outdoorUseReferenceElevation=true;
    private string? _outdoorReferenceFloor;
    private List<OutdoorReferenceEdge> _outdoorEdges=new();
    private OutdoorReferenceEdge? _outdoorHoveredEdge;
    private NumericUpDown? _outdoorOffsetInput;
    private TextBlock? _outdoorReferenceLabel;

    private void AddOutdoorReferenceProperties(OutdoorDrawingDraft draft,Control arcMode)
    {
        var mode=new ComboBox {ItemsSource=new[]{"自由绘制","逐边取线","取闭合周边"},SelectedIndex=_outdoorReferenceMode,
            HorizontalAlignment=HorizontalAlignment.Stretch};
        ToolTip.SetTip(mode,"复制当前边线几何，不建立宿主跟随关系");
        _properties.Children.Add(new TextBlock {Text="绘制方式"});_properties.Children.Add(mode);
        var elevation=new CheckBox {Content="首条边采用源标高",IsChecked=_outdoorUseReferenceElevation,IsVisible=_outdoorReferenceMode!=0};
        _properties.Children.Add(elevation);
        _outdoorReferenceLabel=new TextBlock {Text="未选择源边",TextWrapping=Avalonia.Media.TextWrapping.Wrap,Height=44,
            IsVisible=_outdoorReferenceMode!=0};_properties.Children.Add(_outdoorReferenceLabel);
        mode.SelectionChanged+=(_,_)=>{
            if(!ReferenceEquals(_outdoor,draft))return;
            _outdoorReferenceMode=mode.SelectedIndex;
            if(draft.ArcEnd!=null)draft.Previous();
            _outdoorCursor=null;ClearOutdoorReferenceHover();arcMode.IsVisible=_outdoorReferenceMode==0;
            elevation.IsVisible=_outdoorReferenceMode!=0;_outdoorReferenceLabel.IsVisible=_outdoorReferenceMode!=0;
            _status.Text=_outdoorReferenceMode==0?"继续指定轮廓点。":_outdoorReferenceMode==1?"选择相接的构件边线。":"选择闭合轮廓；点击后复制整圈到草稿。";
            UpdateOutdoorPreview();
        };
        elevation.IsCheckedChanged+=(_,_)=>{_outdoorUseReferenceElevation=elevation.IsChecked==true;ClearOutdoorReferenceHover();};
        arcMode.IsVisible=_outdoorReferenceMode==0;
    }

    private void ClearOutdoorReferenceHover()
    {
        _outdoorHoveredEdge=null;
        _planCanvas.OutdoorReference.Clear();_planCanvas.InvalidateVisual();
        if(_outdoorReferenceLabel!=null)_outdoorReferenceLabel.Text="未选择源边";
        if(_outdoorOverlay!=null){_outdoorOverlay.ReferencePoints.Clear();_outdoorOverlay.InvalidateVisual();}
    }
    private void ResetOutdoorReferences()
    {
        _outdoorReferenceFloor=null;_outdoorEdges.Clear();ClearOutdoorReferenceHover();
    }
    private OutdoorReferenceEdge? PickOutdoorReference(Point pixel,bool plan=false)
    {
        if(_outdoor is not {} draft)return null;
        if(_outdoorReferenceFloor!=draft.StoreyId)
        {
            _outdoorEdges=OutdoorReferenceEdges.Build(_session.Model,draft.StoreyId).SelectMany(r=>r.Edges()).ToList();
            _outdoorReferenceFloor=draft.StoreyId;
        }
        var best=10d;OutdoorReferenceEdge? found=null;
        foreach(var edge in _outdoorEdges)
        {
            var ring=edge.Ring;
            if(_outdoorReferenceMode==2&&!ring.Closed||
                _viewport.IsElementHidden(ring.ElementId)||
                _viewport.IsElementFrozen(ring.ElementId)||
                (plan?!_planCanvas.IsElementSelectable(ring.ElementId):
                    !_viewport.IsReferenceElementAvailable(ring.ElementId)&&draft.OriginalSlab?.Id!=ring.ElementId))continue;
            if((draft.HasDraft||!_outdoorUseReferenceElevation)&&Math.Abs(ring.Elevation-draft.Elevation(_session.Model))>.5)continue;
            var samples=edge.Samples;
            for(var i=1;i<samples.Count;i++)
            {
                Point? a=plan?_planCanvas.OutdoorScreen(samples[i-1].X,samples[i-1].Y):_viewport.ProjectModelPoint(samples[i-1].X,samples[i-1].Y,ring.Elevation);
                Point? b=plan?_planCanvas.OutdoorScreen(samples[i].X,samples[i].Y):_viewport.ProjectModelPoint(samples[i].X,samples[i].Y,ring.Elevation);
                if(a==null||b==null)continue;
                var dx=b.Value.X-a.Value.X;var dy=b.Value.Y-a.Value.Y;var length=dx*dx+dy*dy;if(length<.01)continue;
                var t=Math.Clamp(((pixel.X-a.Value.X)*dx+(pixel.Y-a.Value.Y)*dy)/length,0,1);
                var nearest=new Point(a.Value.X+t*dx,a.Value.Y+t*dy);
                var distance=Math.Sqrt(Math.Pow(pixel.X-nearest.X,2)+Math.Pow(pixel.Y-nearest.Y,2));
                if(distance>=best)continue;
                if(!plan){
                    if(!_viewport.TryScreenToPlan(nearest,ring.Elevation,out var at))continue;
                    var point=edge.Curve.At(edge.Curve.Parameter(at.X,at.Y));
                    if(!_viewport.IsModelPointVisible(point.X,point.Y,ring.Elevation,true))continue;
                }
                best=distance;found=edge;
            }
        }
        return found;
    }
    private void HoverOutdoorReference(Point pixel,bool plan=false)
    {
        _outdoorHoveredEdge=PickOutdoorReference(pixel,plan);
        _planCanvas.OutdoorReference=_outdoorHoveredEdge?.Path(_outdoorReferenceMode==2)??new();
        _planCanvas.OutdoorReferenceClosed=_outdoorReferenceMode==2&&_outdoorHoveredEdge?.Ring.Closed==true;
        _planCanvas.InvalidateVisual();
        if(_outdoorOverlay==null)return;
        _outdoorOverlay.ReferencePoints=_outdoorHoveredEdge?.Path(_outdoorReferenceMode==2)??new();
        _outdoorOverlay.ReferenceClosed=_outdoorReferenceMode==2&&_outdoorHoveredEdge?.Ring.Closed==true;
        _outdoorOverlay.ReferenceElevation=_outdoorHoveredEdge?.Ring.Elevation??0;
        _outdoorOverlay.InvalidateVisual();
        if(_outdoorReferenceLabel!=null)_outdoorReferenceLabel.Text=_outdoorHoveredEdge==null?"未选择源边":
            _outdoorHoveredEdge.Ring.Name+" · "+Mm(_outdoorHoveredEdge.Ring.Elevation)+" mm";
    }
    private bool TakeOutdoorReference(OutdoorReferenceEdge edge)
    {
        if(_outdoor is not {} draft||!_outdoorParametersValid)return false;
        var adopt=!draft.HasDraft&&_outdoorUseReferenceElevation;
        var floor=_session.Model.FindStorey(draft.StoreyId);
        if(floor==null)return false;
        if(!adopt&&Math.Abs(edge.Ring.Elevation-draft.Elevation(_session.Model))>.5)
        {_status.Text="源边不在当前工作标高，不能接入同一路径。";return false;}
        var offset=edge.Ring.Elevation-floor.Elevation;
        if(adopt&&Math.Abs(offset)>100000){_status.Text="源边标高超出当前工具偏移范围。";return false;}
        var error=draft.TakeReference(edge.Path(_outdoorReferenceMode==2),_outdoorReferenceMode==2&&edge.Ring.Closed);
        if(error!=null){_status.Text=error;return false;}
        if(adopt&&_outdoorOffsetInput!=null){_outdoorOffsetInput.Value=(decimal)offset;_outdoorOffsetInput.Text=Mm(offset);}
        ClearOutdoorConflict();_outdoorCursor=null;ClearOutdoorReferenceHover();UpdateOutdoorPreview();
        _status.Text="已复制 "+edge.Ring.Name+" 到草稿，尚未写入模型。";return true;
    }
    private void ReverseOutdoorPath()
    {
        if(_outdoor==null)return;var error=_outdoor.Reverse();
        if(error!=null){_status.Text=error;return;}
        ClearOutdoorConflict();_outdoorCursor=null;UpdateOutdoorPreview();_status.Text="已反向当前路径。";
    }
}
