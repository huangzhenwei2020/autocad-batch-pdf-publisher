using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task BeginFilletAsync(double? suppliedRadius=null)
    {
        var slabId=_session.Model.Slabs.Any(s=>s.Id==_selectedId)?_selectedId:null;
        var radius=suppliedRadius;
        if(radius==null)
        {
            var number=new NumericUpDown {Value=(decimal)_planCanvas.FilletRadius,Minimum=0,Maximum=1000000,
                Increment=100,FormatString="0.###",Height=34,HorizontalAlignment=HorizontalAlignment.Stretch};
            var ok=InspectorButton("开始圆角");var cancel=InspectorButton("取消");
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=8};
            actions.Children.Add(cancel);actions.Children.Add(ok);
            var panel=new StackPanel {Spacing=14,Margin=new Thickness(20)};
            panel.Children.Add(new TextBlock {Text=slabId==null?"墙体圆角":"楼板角部圆角",FontSize=18,FontWeight=FontWeight.SemiBold});
            panel.Children.Add(new TextBlock {Text="半径 mm"});panel.Children.Add(number);
            var sync=new CheckBox {Content="同步相接楼板圆角",IsChecked=_roundRelatedSlabs,IsVisible=slabId==null};
            panel.Children.Add(sync);panel.Children.Add(actions);
            var dialog=new Window {Title="圆角 F",Width=340,SizeToContent=SizeToContent.Height,CanResize=false,
                WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=new SolidColorBrush(Color.Parse("#101E2B")),Content=panel};
            ok.Click+=(_,_)=>{if(number.Value.HasValue){_roundRelatedSlabs=sync.IsChecked==true;dialog.Close((double?)number.Value.Value);}};
            cancel.Click+=(_,_)=>dialog.Close(null);
            radius=await dialog.ShowDialog<double?>(this);
            if(radius==null)return;
        }
        if(!double.IsFinite(radius.Value)||radius<0||radius>1000000)
        {_status.Text="圆角半径应为 0～1000000 mm。";return;}
        _workspaces.SelectedIndex=1;SetPlanTool(PlanTool.Fillet);
        _planCanvas.FilletRadius=radius.Value;_planCanvas.BeginFillet(slabId);_planCanvas.Focus();
    }

    private void ConnectCurveEditing()
    {
        _planCanvas.ArcWallRequested+=(start,end,bulge)=>AddDrawnWall(start,end,bulge);
        _planCanvas.WallFilletRequested+=(a,pa,b,pb,r)=>
        {
            if(!_session.TryFilletWalls(a,pa,b,pb,r,out var id,out var error,_roundRelatedSlabs)){_status.Text=error;return false;}
            _selectedId=id;_curveApplyTask=RefreshModelAsync("已完成墙圆角 · Ctrl+Z 可撤销");return true;
        };
        _planCanvas.SlabFilletRequested+=(id,index,r)=>
        {
            if(!_session.TryFilletSlab(id,index,r,out var error)){_status.Text=error;return false;}
            _selectedId=id;_slabDraft=null;_curveApplyTask=RefreshModelAsync("已完成楼板圆角 · Ctrl+Z 可撤销");return true;
        };
    }
    private Task? _curveApplyTask;
    private bool _roundRelatedSlabs=true;
}
