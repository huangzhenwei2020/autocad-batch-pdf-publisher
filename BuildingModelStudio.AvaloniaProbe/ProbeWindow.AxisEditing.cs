using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;
internal sealed partial class ProbeWindow
{
    private Border? _axisEditStrip;
    private readonly Dictionary<AxisEditMode,Button> _axisModeButtons=new();
    private readonly TextBlock _axisModeHelp=new() {TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.Parse("#91AABC"))};
    private bool _axisEditBusy;
    private bool _syncAxisScope;
    private readonly CheckBox _independentAxes=new() {Content="本层独立轴网",Margin=new Thickness(8,2),VerticalAlignment=VerticalAlignment.Center};
    private string AxisFloorId => (_storeyChooser.SelectedItem as StoreyItem)?.Id ?? _session.Model.Storeys.First().Id;
    private string? AxisScope => _session.Model.StoreyAxes?.ContainsKey(AxisFloorId)==true ? AxisFloorId : null;
    private void SyncAxisScope() { _syncAxisScope=true;_independentAxes.IsChecked=AxisScope!=null;_syncAxisScope=false; }
    private async Task SetIndependentAxesAsync(bool independent) {
        if(_axisEditBusy)return;
        if(!_session.TrySetIndependentAxes(AxisFloorId,independent,out var error)){_status.Text=error;return;}
        _axisEditBusy=true;try {await RefreshModelAsync(independent?"本层轴网已独立 · 可撤销":"本层已恢复共用轴网 · 可撤销");SyncAxisScope();}finally{_axisEditBusy=false;}
    }
    private Control BuildAxisEditStrip()
    {
        var content=new StackPanel {Spacing=8};var tools=new WrapPanel {Orientation=Orientation.Horizontal};
        foreach(var item in new[] {(AxisEditMode.LabelVisibility,"轴号显隐","eye"),
            (AxisEditMode.LabelDeletion,"轴号删 / 恢复","square-x"),
            (AxisEditMode.LineVisibility,"轴线显隐","eye-off"),
            (AxisEditMode.LineDeletion,"删轴 / 恢复","trash"),
            (AxisEditMode.Add,"添加轴线","file-plus")}) {
            var label=new StackPanel {Orientation=Orientation.Horizontal,Spacing=6};label.Children.Add(CommandIcon(item.Item3,17));
            label.Children.Add(new TextBlock {Text=item.Item2,VerticalAlignment=VerticalAlignment.Center});
            var button=new Button {Content=label,Margin=new Thickness(2),Padding=new Thickness(10,6)};
            button.Click+=(_,_)=>BeginAxisEditMode(item.Item1);_axisModeButtons[item.Item1]=button;tools.Children.Add(button);
        }
        var direction=new ComboBox {ItemsSource=new[] {"竖轴 · X 坐标","横轴 · Y 坐标"},SelectedIndex=0,Width=140,Margin=new Thickness(8,2)};
        direction.SelectionChanged+=(_,_)=>{_planCanvas.AxisAddVertical=direction.SelectedIndex==0;_planCanvas.InvalidateVisual();};tools.Children.Add(direction);
        _independentAxes.IsCheckedChanged+=async(_,_)=>{if(!_syncAxisScope)await SetIndependentAxesAsync(_independentAxes.IsChecked==true);};
        tools.Children.Add(_independentAxes);
        var batch=new Button {Content="批量设置",Margin=new Thickness(2)};batch.Click+=async(_,_)=>await OpenAxisSettingsAsync();tools.Children.Add(batch);
        var exit=new Button {Content="完成 · Esc",Margin=new Thickness(2)};exit.Click+=(_,_)=>SetPlanTool(PlanTool.Select);tools.Children.Add(exit);
        content.Children.Add(tools);content.Children.Add(_axisModeHelp);
        _axisEditStrip=new Border {IsVisible=false,Background=new SolidColorBrush(Color.Parse("#192B3B")),
            BorderBrush=new SolidColorBrush(Color.Parse("#345169")),BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(8),Child=content};return _axisEditStrip;
    }
    private void BeginAxisEditing()=>BeginAxisEditMode(AxisEditMode.LabelVisibility);
    private void BeginAxisEditMode(AxisEditMode mode)
    {
        SetPlanTool(PlanTool.Select);_workspaces.SelectedIndex=1;
        SyncAxisScope();_planCanvas.SetAxisEditMode(mode);if(_axisEditStrip!=null)_axisEditStrip.IsVisible=true;
        foreach(var pair in _axisModeButtons)pair.Value.Background=new SolidColorBrush(Color.Parse(pair.Key==mode?"#1768A7":"#20364A"));
        _axisModeHelp.Text=mode switch {
            AxisEditMode.LabelVisibility=>"点击任一端轴号显示 / 隐藏。灰色轴号仅在编辑时显示，可再次点击恢复；隐藏不重编号。",
            AxisEditMode.LabelDeletion=>"点击任一端轴号删除 / 恢复。删除后线端缩至墙外 500 mm；点击带叉的灰色轴号恢复。",
            AxisEditMode.LineVisibility=>"点击轴线或轴号隐藏 / 显示整条轴线。隐藏轴线以灰色虚线提示，编号保持不变。",
            AxisEditMode.LineDeletion=>"点击轴线或轴号删除 / 恢复整条轴线。删除项以灰色虚线和叉号提示，自动轴号实时重排。",
            _=>"选择竖轴或横轴，移动鼠标预览位置，点击创建。添加到已删除轴线的位置可恢复该轴线。"};
        _status.Text="轴网动态编辑 · "+_axisModeHelp.Text;_planCanvas.Focus();
    }
    private void EndAxisEditing() {
        _planCanvas.SetAxisEditMode(AxisEditMode.Off);if(_axisEditStrip!=null)_axisEditStrip.IsVisible=false;
    }
    private List<AxisModel> EditableAxes()
    {
        var axes=BuildingAxisLayout.Resolve(_session.Model, AxisFloorId);var vi=0;var hi=0;
        foreach(var axis in axes.OrderBy(a=>a.Vertical?0:1).ThenBy(a=>a.Position)) {
            var expected=axis.Deleted?"":axis.Vertical?(++vi).ToString():PlanEditing.LetterName(hi++);
            if(axis.AutomaticNumber==null) {
                var original=(AxisScope==null?_session.Model.Axes:_session.Model.StoreyAxes[AxisScope]).FirstOrDefault(a=>a.Id==axis.Id);
                axis.AutomaticNumber=original==null||((string.IsNullOrWhiteSpace(original.Name)||original.Name==expected)
                    &&string.IsNullOrWhiteSpace(original.StartName)&&string.IsNullOrWhiteSpace(original.EndName));
            }
        }
        return axes;
    }
    private async Task ApplyAxisClickAsync(AxisEditRequest request)
    {
        if(_axisEditBusy)return;
        var axes=EditableAxes();var axis=axes.FirstOrDefault(a=>a.Id==request.Id);string message;
        if(request.Mode==AxisEditMode.Add) {
            if(!double.IsFinite(request.Position))return;
            axis=axes.FirstOrDefault(a=>a.Vertical==request.Vertical&&Math.Abs(a.Position-request.Position)<=.5);
            if(axis!=null) {
                if(!axis.Deleted&&!axis.Hidden){_status.Text="该位置已有轴线，请选择其他位置。";return;}
                axis.Deleted=false;axis.Hidden=false;message="已恢复该位置的轴线";
            } else {
                axis=new AxisModel {Id="axis-"+Guid.NewGuid().ToString("N"),Vertical=request.Vertical,Position=request.Position,AutomaticNumber=true};
                axes.Add(axis);message="已添加"+(request.Vertical?"竖轴 X=":"横轴 Y=")+request.Position.ToString("0.##")+" mm";
            }
        } else {
            if(axis==null)return;
            switch(request.Mode) {
                case AxisEditMode.LabelVisibility:
                    if(axis.Deleted||axis.Hidden){_status.Text="请先用轴线显隐或删轴 / 恢复显示整条轴线。";return;}
                    if(request.Start?axis.StartRemoved:axis.EndRemoved){_status.Text="该端轴号已删除，请用轴号删 / 恢复。";return;}
                    if(request.Start)axis.StartHidden=!axis.StartHidden;else axis.EndHidden=!axis.EndHidden;
                    message="轴号显示状态已更新";break;
                case AxisEditMode.LabelDeletion:
                    if(request.Start)axis.StartRemoved=!axis.StartRemoved;else axis.EndRemoved=!axis.EndRemoved;
                    if(request.Start&&!axis.StartRemoved)axis.StartHidden=false;if(!request.Start&&!axis.EndRemoved)axis.EndHidden=false;
                    message="轴号删除状态已更新";break;
                case AxisEditMode.LineVisibility:
                    if(axis.Deleted){_status.Text="该轴线已删除，请用删轴 / 恢复。";return;}
                    axis.Hidden=!axis.Hidden;message="轴线显示状态已更新";break;
                default:
                    axis.Deleted=!axis.Deleted;if(!axis.Deleted)axis.Hidden=false;
                    message=axis.Deleted?"已删除轴线，自动轴号已重排":"已恢复轴线，自动轴号已重排";break;
            }
        }
        if(!_session.TryReplaceAxes(axes,out var error,AxisScope)){_status.Text=error;return;}
        _axisEditBusy=true;
        try {await RefreshModelAsync(message+" · 可撤销，继续点击操作");}
        finally {_axisEditBusy=false;}
    }
}
