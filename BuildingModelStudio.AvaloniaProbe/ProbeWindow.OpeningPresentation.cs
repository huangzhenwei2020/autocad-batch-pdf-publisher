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
        codeRow.Children.Add(new TextBlock {Name="OpeningCodeCaption",Text="门窗编号",VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});
        var code=new TextBox {Name="OpeningCode",Text=opening.Code,MaxLength=64,MinHeight=36,
            Background=new SolidColorBrush(Color.Parse("#20364A")),BorderBrush=new SolidColorBrush(Color.Parse("#38556E"))};
        Grid.SetColumn(code,1);codeRow.Children.Add(code);_properties.Children.Add(codeRow);
        ToolTip.SetTip(code,"门窗编号");
        var type=OpeningConstruction.Resolve(_session.Model,opening);
        var swing=OpeningPlanGeometry.HasPlanSwing(opening,type);
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
            if(OpeningPlanGeometry.HasSwingDoor(opening,type)) {
                open=new CheckBox {Name="OpeningOpenIn3D",Content="三维显示开启",IsChecked=opening.OpenIn3D??((type.OpenAngle??0)>0),MinHeight=34};
                _properties.Children.Add(open);
            }
        }
        var apply=InspectorButton("应用编号与开启设置");apply.Name="ApplyOpeningPresentation";
        async Task Apply() {
            var preview=_parameterPreview?.Openings.FirstOrDefault(o=>o.Id==opening.Id);
            if(_openingParameterDraftInvalid||code.IsReadOnly||(preview!=null&&(preview.Width!=opening.Width||preview.Height!=opening.Height))) {
                _status.Text="请先应用或取消洞口尺寸修改。";return;
            }
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
        var host=_session.Model.Walls.FirstOrDefault(w=>w.Id==opening.HostWallId);
        if(host!=null&&OpeningPlanGeometry.DirectionHandle(opening,type,host.Thickness)!=null) {
            var directions=new Grid {ColumnDefinitions=new("*,*"),ColumnSpacing=6};
            for(var i=0;i<2;i++) {
                var along=i==0;var label=along?"左右翻转":"内外翻转";
                var button=InspectorButton(label);button.Name=along?"FlipOpeningAlong":"FlipOpeningNormal";
                button.HorizontalAlignment=HorizontalAlignment.Stretch;SetCommandVisual(button,label,true);
                ToolTip.SetTip(button,along?"翻转开启扇的左右方向":"切换开启扇向内或向外");
                button.Click+=(_,_)=>_openingApplyTask=FlipSelectedOpeningAsync(along);
                Grid.SetColumn(button,i);directions.Children.Add(button);
            }
            _properties.Children.Add(directions);
        }
        var center=InspectorButton("沿墙居中");SetCommandVisual(center,"沿墙居中",true);
        ToolTip.SetTip(center,"洞口中心对齐宿主墙段中点");center.Click+=async(_,_)=>await CenterSelectedOpeningAsync();
        _properties.Children.Add(center);
    }
}
