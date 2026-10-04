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
        foreach(var item in new[] {(AxisEditMode.LabelVisibility,"隐藏轴号","eye-off"),
            (AxisEditMode.LabelShow,"显示轴号","eye"),
            (AxisEditMode.LabelDeletion,"删除轴号","square-x"),
            (AxisEditMode.LabelRestore,"恢复轴号","undo-2"),
            (AxisEditMode.LineVisibility,"隐藏轴线","eye-off"),
            (AxisEditMode.LineShow,"显示轴线","eye"),
            (AxisEditMode.LineDeletion,"删除轴线","trash"),
            (AxisEditMode.LineRestore,"恢复轴线","undo-2"),
            (AxisEditMode.Add,"添加轴线","file-plus")}) {
            var button=SlabButton(item.Item2,item.Item3);button.Margin=new Thickness(2);
            button.Padding=new Thickness(8,0);
            ToolTip.SetTip(button,item.Item2+"：点选或框选，空格 / 回车确认，Esc 取消");
            button.Click+=(_,_)=>BeginAxisEditMode(item.Item1);_axisModeButtons[item.Item1]=button;tools.Children.Add(button);
        }
        var direction=new ComboBox {ItemsSource=new[] {"竖轴 · X 坐标","横轴 · Y 坐标"},SelectedIndex=0,Width=140,Height=32,Margin=new Thickness(8,2)};
        direction.Background=new SolidColorBrush(Color.Parse("#192D3D"));
        direction.Foreground=new SolidColorBrush(Color.Parse("#E3EDF5"));
        direction.BorderBrush=new SolidColorBrush(Color.Parse("#345267"));direction.BorderThickness=new Thickness(1);
        direction.SelectionChanged+=(_,_)=>{_planCanvas.AxisAddVertical=direction.SelectedIndex==0;_planCanvas.InvalidateVisual();};tools.Children.Add(direction);
        _independentAxes.IsCheckedChanged+=async(_,_)=>{if(!_syncAxisScope)await SetIndependentAxesAsync(_independentAxes.IsChecked==true);};
        tools.Children.Add(_independentAxes);
        var batch=SlabButton("批量设置","settings");batch.Margin=new Thickness(2);batch.Click+=async(_,_)=>await OpenAxisSettingsAsync();tools.Children.Add(batch);
        var exit=SlabButton("完成","check");exit.Margin=new Thickness(2);exit.Click+=(_,_)=>SetPlanTool(PlanTool.Select);tools.Children.Add(exit);
        content.Children.Add(tools);
        _axisEditStrip=new Border {IsVisible=false,Background=new SolidColorBrush(Color.Parse("#192B3B")),
            BorderBrush=new SolidColorBrush(Color.Parse("#345169")),BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(8),Child=content};return _axisEditStrip;
    }
    private void BeginAxisEditing()=>BeginAxisEditMode(AxisEditMode.LabelVisibility);
    private void BeginAxisEditMode(AxisEditMode mode)
    {
        if(_axisEditBusy)return;
        SetPlanTool(PlanTool.Select);_workspaces.SelectedIndex=1;
        SyncAxisScope();_planCanvas.SetAxisEditMode(mode);if(_axisEditStrip!=null)_axisEditStrip.IsVisible=true;
        foreach(var pair in _axisModeButtons) {
            var destructive=mode is AxisEditMode.LabelDeletion or AxisEditMode.LineDeletion;
            pair.Value.Background=new SolidColorBrush(Color.Parse(pair.Key==mode?destructive?"#4A3720":"#1768A7":"#20364A"));
            pair.Value.BorderBrush=new SolidColorBrush(Color.Parse(pair.Key==mode&&destructive?"#D7A751":"#345267"));
        }
        _axisModeHelp.Text=mode switch {
            AxisEditMode.LabelVisibility=>"点选或框选要隐藏的轴号；隐藏不重编号。",
            AxisEditMode.LabelShow=>"点选或框选灰色轴号，批量恢复显示。",
            AxisEditMode.LabelDeletion=>"点选或框选要删除的轴号；确认后线端缩至墙外 500 mm。",
            AxisEditMode.LabelRestore=>"点选或框选带叉轴号，批量恢复。",
            AxisEditMode.LineVisibility=>"点选或框选要隐藏的轴线，编号保持不变。",
            AxisEditMode.LineShow=>"点选或框选灰色虚线轴线，批量恢复显示。",
            AxisEditMode.LineDeletion=>"点选或框选要删除的轴线；确认后自动轴号重排。",
            AxisEditMode.LineRestore=>"点选或框选删除虚线，批量恢复轴线。",
            _=>"选择竖轴或横轴，移动鼠标预览位置，点击创建。添加到已删除轴线的位置可恢复该轴线。"};
        _status.Text="轴网动态编辑 · "+_axisModeHelp.Text;_planCanvas.Focus();
    }
    private void EndAxisEditing() {
        if(_axisEditBusy)return;
        _planCanvas.SetAxisEditMode(AxisEditMode.Off);if(_axisEditStrip!=null)_axisEditStrip.IsVisible=false;
    }
    private async Task ApplyAxisBatchAsync(AxisEditBatch batch)
    {
        var success=false;
        try {
            if(_axisEditBusy||batch.FloorId!=AxisFloorId||batch.Mode!=_planCanvas.AxisMode)return;
            var axes=EditableAxes();var changed=0;
            foreach(var pick in batch.Picks.Distinct()) {
                var axis=axes.FirstOrDefault(a=>a.Id==pick.Id);
                if(axis==null){_status.Text="轴网已改变，请重新选择。";return;}
                switch(batch.Mode) {
                    case AxisEditMode.LabelVisibility:
                    case AxisEditMode.LabelShow:
                        if(axis.Deleted||axis.Hidden||(pick.Start?axis.StartRemoved:axis.EndRemoved)) {
                            _status.Text="所选轴号当前不可编辑，请先恢复轴线。";return;
                        }
                        var hidden=batch.Mode==AxisEditMode.LabelVisibility;
                        if((pick.Start?axis.StartHidden:axis.EndHidden)==hidden)continue;
                        if(pick.Start)axis.StartHidden=hidden;else axis.EndHidden=hidden;break;
                    case AxisEditMode.LabelDeletion:
                    case AxisEditMode.LabelRestore:
                        if(axis.Deleted){_status.Text="请先恢复所选轴线。";return;}
                        var removed=batch.Mode==AxisEditMode.LabelDeletion;
                        if((pick.Start?axis.StartRemoved:axis.EndRemoved)==removed)continue;
                        if(pick.Start){axis.StartRemoved=removed;if(!removed)axis.StartHidden=false;}
                        else {axis.EndRemoved=removed;if(!removed)axis.EndHidden=false;}break;
                    case AxisEditMode.LineVisibility:
                    case AxisEditMode.LineShow:
                        if(axis.Deleted){_status.Text="请先恢复所选轴线。";return;}
                        var lineHidden=batch.Mode==AxisEditMode.LineVisibility;
                        if(axis.Hidden==lineHidden)continue;axis.Hidden=lineHidden;break;
                    case AxisEditMode.LineDeletion:
                    case AxisEditMode.LineRestore:
                        var deleted=batch.Mode==AxisEditMode.LineDeletion;
                        if(axis.Deleted==deleted)continue;axis.Deleted=deleted;if(!deleted)axis.Hidden=false;break;
                    default:return;
                }
                changed++;
            }
            if(changed==0){success=true;return;}
            if(!_session.TryReplaceAxes(axes,out var error,AxisScope)){_status.Text=error;return;}
            _axisEditBusy=true;
            if(_axisEditStrip!=null)_axisEditStrip.IsEnabled=false;
            _storeyChooser.IsEnabled=false;
            await RefreshModelAsync($"轴网批量修改 {changed} 项 · 可撤销，继续选择");
            success=true;
        }
        catch(Exception ex){_status.Text="轴网批量修改未完成："+ex.Message;}
        finally{_axisEditBusy=false;if(_axisEditStrip!=null)_axisEditStrip.IsEnabled=true;
            _storeyChooser.IsEnabled=true;_planCanvas.CompleteAxisBatch(success);}
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
        if(_axisEditBusy||request.Mode!=AxisEditMode.Add)return;
        var axes=EditableAxes();string message;
            if(!double.IsFinite(request.Position))return;
            var axis=axes.FirstOrDefault(a=>a.Vertical==request.Vertical&&Math.Abs(a.Position-request.Position)<=.5);
            if(axis!=null) {
                if(!axis.Deleted&&!axis.Hidden){_status.Text="该位置已有轴线，请选择其他位置。";return;}
                axis.Deleted=false;axis.Hidden=false;message="已恢复该位置的轴线";
            } else {
                axis=new AxisModel {Id="axis-"+Guid.NewGuid().ToString("N"),Vertical=request.Vertical,Position=request.Position,AutomaticNumber=true};
                axes.Add(axis);message="已添加"+(request.Vertical?"竖轴 X=":"横轴 Y=")+request.Position.ToString("0.##")+" mm";
            }
        if(!_session.TryReplaceAxes(axes,out var error,AxisScope)){_status.Text=error;return;}
        _axisEditBusy=true;
        try {await RefreshModelAsync(message+" · 可撤销，继续点击操作");}
        finally {_axisEditBusy=false;}
    }
}
