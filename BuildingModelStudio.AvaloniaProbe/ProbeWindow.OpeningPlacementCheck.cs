using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunOpeningPlacementCheckAsync()
    {
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        var saved=_session;var selection=_selectedId;
        var model=SampleModelFactory.CreateEmptyModel("门窗 MM 选型放置");model.Storeys[0].Height=6000;
        model.Walls.Add(new WallModel {Id="host",StoreyId="1F",X2=24000,Thickness=200});
        var project=OpeningPlacementCatalog.BuiltIns().First(e=>e.Type.Code=="M0921").Type;
        project=OpeningConstruction.Copy(project);OpeningConstruction.ResizeType(project,1000,2100);project.Source="项目";project.Remarks="项目自定义门";
        model.OpeningTypes.Add(project);
        var template=OpeningConstruction.Copy(project);template.Code="项目模板-M1000";model.OpeningTemplates.Add(template);
        try {
            _session=new BuildingModelEditSession(model);await RefreshModelAsync("门窗选型放置校对");SetPlanTool(PlanTool.Select);
            var before=BuildingModelJson.ToJson(_session.Model);
            _commandInput.Clear();_planCanvas.Focus();
            _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.M});
            Check(_commandInput.Text=="M","视口输入 M 没有转入命令框");
            _commandInput.Text+="m";
            _commandInput.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(180);
            var window=_openingPlacementWindow??throw new InvalidOperationException("MM 没有打开统一门窗窗口");
            window.Width=Width<1000?720:900;window.Height=Width<1000?560:700;await Task.Delay(100);
            Check(window.Entries.Count==25&&window.Entries.Count(e=>e.Type.Code=="M0921")==1,"项目、模板与内置类型没有正确去重");
            Check(window.Entries.Single(e=>e.Type.Code=="M0921").Type.Width==1000,"内置类型覆盖项目定义");
            foreach(var entry in window.Entries){window.Choices.SelectedItem=entry;
                Check(window.PlanPreview.Lines.Count>0&&window.ElevationPreview.Lines.Count>0,"类型平面/立面没有预览 "+entry.Type.Code);
            }
            window.Category.SelectedItem="窗";
            Check(window.Choices.Items.OfType<OpeningPlacementEntry>().All(e=>!e.IsDoor)&&window.Choices.ItemCount==12,"窗筛选混入门");
            window.Category.SelectedItem="门";
            Check(window.Choices.Items.OfType<OpeningPlacementEntry>().All(e=>e.IsDoor),"门筛选混入窗");
            window.SourceFilter.SelectedItem="项目";Check(window.Choices.ItemCount==1,"项目来源筛选错误");
            window.Search.Text="不存在的编号";Check(window.Choices.ItemCount==0&&!window.Place.IsEnabled&&window.PlanPreview.Lines.Count==0,"空搜索仍能放置隐藏类型");
            window.Search.Text="";window.SourceFilter.SelectedIndex=0;window.Category.SelectedIndex=0;
            window.Choices.SelectedItem=window.Entries.Single(e=>e.Type.Code=="M0921");window.WidthInput.Text="1100";
            Check(!window.Place.IsEnabled&&window.PlanPreview.Lines.Count>0,"已有编号尺寸冲突未提示或清空有效草稿预览");
            window.CodeInput.Text="M-新1100";Check(window.Place.IsEnabled,"改成新编号后仍不能放置");
            window.Choices.SelectedItem=window.Entries.Single(e=>e.Type.Code=="TLM1525");await Task.Delay(150);
            window.CodeInput.Text="TLM-自测1600";window.WidthInput.Text="1600";window.HeightInput.Text="2400";window.ThresholdInput.Text="80";
            Check(window.Place.IsEnabled&&window.PlanPreview.Lines.Take(2).All(l=>Math.Abs(l.Y1)==100&&l.X2==1600),"插入前门槛参数没有反映到墙边平面线");
            window.ThresholdInput.Text="-1";Check(!window.Place.IsEnabled&&!window.SaveTemplate.IsEnabled,"非法门槛仍可放置/保存模板");window.ThresholdInput.Text="80";
            Check(window.Search.Bounds.Height>=32&&window.Place.Bounds.Height==32,"门窗窗口控件高度不统一");
            Check(window.PlanPreview.Bounds.Width>100&&window.PlanPreview.Bounds.Height>=100&&window.ElevationPreview.Bounds.Height>=100,"小窗口预览被挤没");
            var checkPoint=window.OpenIn3DInput.TranslatePoint(new Point(0,window.OpenIn3DInput.Bounds.Height),window)!.Value;
            Check(checkPoint.Y<window.Height-64,"小窗口的三维开启控件被固定底栏裁切");
            foreach(var field in new[]{window.CodeInput,window.WidthInput,window.HeightInput,window.SillInput,window.ThresholdInput,window.AngleInput})Check(field.Bounds.Width>100&&field.Bounds.Height==32,"插入参数尺寸不统一");
            Check(window.SaveTemplate.Bounds.Height==32&&window.SaveTemplate.Bounds.Right<window.Width,"保存模板按钮被裁切");
            var visual=ElementComposition.GetElementVisual(window)!;
            var bitmap=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
            bitmap.Save(System.IO.Path.GetFullPath($".artifacts/opening-symbols/{(int)window.Width}x{(int)window.Height}-opening-parameters.png"),PngBitmapEncoderOptions.Default);
            window.Place.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for(var i=0;i<50&&_openingPlacementWindow!=null;i++)await Task.Delay(30);
            Check(_openingPlacementWindow==null&&_planCanvas.Tool==PlanTool.Opening&&_planCanvas.PlacementOpeningType?.Code=="TLM-自测1600","选择类型后没有进入统一放置模式");
            Check(BuildingModelJson.ToJson(_session.Model)==before,"仅选择类型就写入了模型");
            _planCanvas.Fit();var pointer=new Pointer(995,PointerType.Mouse,true);
            void Click(double along){var p=_planCanvas.ModelToScreen(new PointModel(along,0));var root=_planCanvas.TranslatePoint(p,this)!.Value;
                _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,root,0,new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
                _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,root,0,new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
            }
            Click(4000);await Task.Delay(220);
            Check(_session.Model.Openings.Count==1&&_session.Model.Openings[0].Code=="TLM-自测1600"&&_session.Model.Openings[0].Width==1600&&_session.Model.Openings[0].Height==2400&&_session.Model.Openings[0].ThresholdHeight==80,"实际点墙丢失插入前门参数");
            var expected=OpeningConstruction.Copy(OpeningPlacementCatalog.BuiltIns().Single(e=>e.Type.Code=="TLM1525").Type);OpeningConstruction.ResizeType(expected,1600,2400);
            Check(OpeningConstruction.Resolve(_session.Model,_session.Model.Openings[0]).CustomCellLayout==expected.CustomCellLayout,"放置丢失缩放后的推拉分格");
            var first=BuildingModelJson.ToJson(_session.Model);Click(4000);await Task.Delay(100);
            Check(BuildingModelJson.ToJson(_session.Model)==first&&_planCanvas.Tool==PlanTool.Opening,"冲突放置写入或退出连续放置");
            Click(9000);await Task.Delay(220);Check(_session.Model.Openings.Count==2,"无法连续放置第二樘");
            Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==first,"连续放置不是逐次撤销");
            Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"类型和首樘不是一起撤销");
            await RefreshModelAsync("窗选型校对");ExecuteCommand("WN");await Task.Delay(100);
            window=_openingPlacementWindow??throw new InvalidOperationException("旧窗命令没有转到新窗口");
            Check(window.Category.SelectedItem?.ToString()=="窗","旧窗命令没有预选窗分类");
            window.Choices.SelectedItem=window.Entries.Single(e=>e.Type.Code=="C1518");window.SillInput.Text="1050";
            Check(!window.ThresholdInput.IsEnabled,"窗仍可设置门槛");window.Confirm();
            for(var i=0;i<50&&_openingPlacementWindow!=null;i++)await Task.Delay(30);
            Click(12000);await Task.Delay(220);Check(_session.Model.Openings.Single().Code=="C1518"&&_session.Model.Openings.Single().Sill==1050,"窗编号/插入前窗台参数丢失");
            SetPlanTool(PlanTool.Select);Check(_planCanvas.PlacementOpeningType==null,"退出工具后仍保留放置状态");
            var placed=BuildingModelJson.ToJson(_session.Model);_commandInput.Text="mm";
            _commandInput.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Space});await Task.Delay(100);
            window=_openingPlacementWindow!;window.Choices.SelectedItem=window.Entries.Single(e=>e.Type.Code=="M0921"&&e.Source=="项目");
            window.ThresholdInput.Text="120";window.AnglePresets.SelectedIndex=3;window.OpenIn3DInput.IsChecked=true;
            window.SaveTemplate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);
            Check(window.SourceFilter.SelectedItem?.ToString()=="模板"&&window.Choices.ItemCount==2,"保存模板没有出现在模板来源中");
            var savedTemplate=_session.Model.OpeningTemplates.Single(t=>t.Code=="M0921");
            Check(savedTemplate.ThresholdHeight==120&&savedTemplate.PlanOpenAngle==15&&savedTemplate.DefaultOpenIn3D,"模板没保存插入设置");
            Check(_session.Model.Openings.Count==1,"保存模板意外插入了门窗");
            var templateJson=BuildingModelJson.ToJson(_session.Model);
            _openingPlacementWindow!.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Escape});await Task.Delay(100);
            Check(_openingPlacementWindow==null&&BuildingModelJson.ToJson(_session.Model)==templateJson&&_planCanvas.Tool==PlanTool.Select,"取消选型丢失显式保存的模板或意外放置");
            ExecuteCommand("MM");await Task.Delay(120);window=_openingPlacementWindow!;
            Check(window.Entries.Count(e=>e.Type.Code=="M0921")==2,"项目同编号掩盖了保存的模板");
            window.SourceFilter.SelectedItem="模板";window.Choices.SelectedItem=window.Entries.Single(e=>e.Type.Code=="M0921"&&e.Source=="模板");
            Check(window.ThresholdInput.Text=="120"&&window.AngleInput.Text=="15"&&window.OpenIn3DInput.IsChecked==true&&window.Place.IsEnabled,"重新选模板没恢复门槛和开启默认值");
            window.Confirm();for(var i=0;i<50&&_openingPlacementWindow!=null;i++)await Task.Delay(30);
            Click(18000);await Task.Delay(240);var door=_session.Model.Openings.Single(o=>o.Code=="M0921");
            Check(door.ThresholdHeight==120&&door.PlanOpenAngle==15&&door.OpenIn3D==true&&_session.Model.OpeningTypes.Single(t=>t.Code=="M0921").ThresholdHeight==0,"模板插入没应用实例默认值或覆盖原类型");
            SetPlanTool(PlanTool.Select);SelectById(door.Id);await Task.Delay(120);
            T Property<T>(string name) where T:Control=>_properties.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
            Property<TextBox>("OpeningThresholdHeight").Text="60";
            Property<Button>("ApplyOpeningGeometry").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(260);
            Check(_session.Model.Openings.Single(o=>o.Id==door.Id).ThresholdHeight==60,"属性栏门槛高没有提交");
            Check(_session.Undo()&&_session.Model.Openings.Single(o=>o.Id==door.Id).ThresholdHeight==120,"属性门槛没有一次撤销");
            Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==templateJson,"模板插入不能原子撤销");
            Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==placed,"保存模板不能单独撤销");
            Console.WriteLine("OPENING_PICKER_OK MM enter space builtIns24 editableCode dimensions sill threshold previews templateSave filter reopen instanceDefaults projectIsolation wallClicks invalidRollback undo propertyThreshold escape");
        } finally {
            _openingPlacementWindow?.Close();_session=saved;SetPlanTool(PlanTool.Select);await RefreshModelAsync("门窗选型校对完成");SelectById(selection);
        }
    }
}
