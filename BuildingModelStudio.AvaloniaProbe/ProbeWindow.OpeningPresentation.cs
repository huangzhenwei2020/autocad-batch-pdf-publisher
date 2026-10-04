using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;
using System.Globalization;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private void BuildOpeningPresentationProperties(OpeningModel opening)
    {
        var codeRow=new Grid {ColumnDefinitions=new ColumnDefinitions("*,140")};
        codeRow.Children.Add(new TextBlock {Text="门窗编号",VerticalAlignment=VerticalAlignment.Center});
        var code=new TextBox {Name="OpeningCode",Text=opening.Code,MaxLength=64,MinHeight=36,
            Background=new SolidColorBrush(Color.Parse("#20364A")),BorderBrush=new SolidColorBrush(Color.Parse("#38556E"))};
        Grid.SetColumn(code,1);codeRow.Children.Add(code);_properties.Children.Add(codeRow);
        ToolTip.SetTip(code,"门窗编号");
        var type=OpeningConstruction.Resolve(_session.Model,opening);
        var swing=OpeningPlanGeometry.HasSwingDoor(opening,type);
        TextBox? angle=null;CheckBox? open=null;
        if(swing) {
            angle=AddNumberField("平面开启角度 °",opening.PlanOpenAngle,"OpeningPlanAngle");
            var presets=new ComboBox {Name="OpeningAnglePresets",ItemsSource=new[]{"90°","45°","30°","15°"},MinHeight=36,
                HorizontalAlignment=HorizontalAlignment.Stretch,PlaceholderText="自定义角度",
                SelectedIndex=Array.IndexOf(new[]{90d,45,30,15},opening.PlanOpenAngle),
                Background=new SolidColorBrush(Color.Parse("#20364A")),BorderBrush=new SolidColorBrush(Color.Parse("#38556E"))};
            presets.SelectionChanged+=(_,_)=> {
                if(presets.SelectedIndex>=0)angle.Text=new[]{90,45,30,15}[presets.SelectedIndex].ToString(CultureInfo.InvariantCulture);
            };
            ToolTip.SetTip(presets,"平面开启角度预设");_properties.Children.Add(presets);
            open=new CheckBox {Name="OpeningOpenIn3D",Content="三维显示开启",IsChecked=opening.OpenIn3D??((type.OpenAngle??0)>0),MinHeight=34};
            _properties.Children.Add(open);
        }
        var apply=InspectorButton("应用编号与开启设置");apply.Name="ApplyOpeningPresentation";
        async Task Apply() {
            var value=opening.PlanOpenAngle;
            if(angle!=null&&!double.TryParse(angle.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out value)
                &&!double.TryParse(angle.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)) {
                _status.Text="请输入 0～180° 的有效开启角度。";angle.Focus();return;
            }
            var original=_session.Model;
            if(!_session.TrySetOpeningPresentation(opening.Id,code.Text??"",value,open?.IsChecked??(opening.OpenIn3D??((type.OpenAngle??0)>0)),out var error)) {
                _status.Text=error;return;
            }
            await RefreshModelAsync("已更新门窗编号与开启设置",original,opening.Id);
        }
        apply.Click+=async(_,_)=>await Apply();
        foreach(var field in new[]{code,angle}.OfType<TextBox>())field.KeyDown+=async(_,e)=> {
            if(e.Key==Key.Enter){e.Handled=true;await Apply();}
        };
        _properties.Children.Add(apply);
        var center=InspectorButton("沿墙居中");SetCommandVisual(center,"沿墙居中",true);
        ToolTip.SetTip(center,"洞口中心对齐宿主墙段中点");center.Click+=async(_,_)=>await CenterSelectedOpeningAsync();
        _properties.Children.Add(center);
    }
}
