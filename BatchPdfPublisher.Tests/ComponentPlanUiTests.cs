using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatchPdfPublisher.Views;
using BatchPdfPublisher.BuildingModel;

internal static class ComponentPlanUiTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        try {
            CheckSharedLibrary(args);
            CheckDoorEdit(args);
            CheckRegionEdit(args);
            foreach (var size in new[] { new Size(960, 680), new Size(1240, 840), new Size(1600, 960) }) {
                var exports = 0; var cancelPick = false; CadComponentPlanSettings saved = null;
                var window = new CadComponentPlanWindow("Door", owner => cancelPick ? null : new CadComponentPlanBlock { Handle = "ABC", Name = "门平面",BasePoint=new[]{2300d,4200d,0d} },
                    owner => cancelPick ? null : new[] { 12345.125, 6789.5, 0d }, (owner, settings) => { exports++; saved = settings; return "已入库"; },
                    inspect: (owner, settings) => AuthorPlan(settings));
                var root = (Grid)window.Content; root.Background = window.Background; root.Margin = new Thickness(0);
                void Layout() { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
                void Click(string name) { ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                Layout(); Click("Export"); Require(exports == 0, "未选择块不能导出");
                Click("DoorPickBlock"); Require(window.ReadSettings().X==2300&&window.ReadSettings().Y==4200,"选择块未自动带入基点"); Click("DoorPickBase");
                ((RadioButton)window.FindName("DoorYAxis")).IsChecked = true; Click("Export");
                Require(exports == 0, "方向改变必须先核对预览"); Click("DoorRefreshPreview"); Click("Export");
                Require(exports == 1 && saved.Category == "Door" && saved.Axis == "Y" && saved.X == 12345.125 && saved.Width == 900, "门输入/拾取/Y方向");
                Require(saved.Code==""&&saved.Name=="单开装修门", "入库先用名称，不强制项目编号");
                window.SelectPart("Frame");window.AssignPrimitives(new[]{0,1});
                window.SelectPart("PrimaryLeaf");window.AssignPrimitives(new[]{2,3});
                try{window.AssignPrimitives(new[]{0});throw new Exception("重复部件归属未拒绝");}catch(InvalidDataException){}
                window.SelectPart("OpeningSymbol");window.AssignPrimitives(new[]{4});
                window.SelectPart("PrimaryLeaf");
                var settingsWithParts=window.ReadSettings();
                Require(settingsWithParts.Parts.Single(p=>p.Role=="PrimaryLeaf").Primitives.SequenceEqual(new[]{2,3}),"当前门扇图元错误");
                var preview=(CadComponentPartPreview)window.FindName("DoorPreview");
                Require(preview.Highlight.SequenceEqual(new[]{2,3}),"切换当前部件未同步高亮");
                window.AssignPrimitives(new[]{3},true);Require(window.ReadSettings().Parts.Single(p=>p.Role=="PrimaryLeaf").Primitives.Count==1,"减选未生效");
                window.AssignPrimitives(new[]{3});
                var originalFrameId=window.ReadSettings().Parts.Single(p=>p.Role=="Frame").PartId;
                window.SelectPart("Frame");Click("DoorDeletePart");
                Require(!window.ReadSettings().Parts.Any(p=>p.Role=="Frame")&&preview.Symbol.Primitives.Count==5,"删除部件不能删除原始几何");
                ((ComboBox)window.FindName("DoorNewRole")).SelectedIndex=0;Click("DoorAddPart");window.SelectPart("Frame");window.AssignPrimitives(new[]{0,1});
                Require(window.ReadSettings().Parts.Single(p=>p.Role=="Frame").PartId!=originalFrameId,"重新添加部件应获得新身份");window.SelectPart("PrimaryLeaf");
                cancelPick = true; Click("DoorPickBlock"); Click("DoorPickBase"); Click("Export");
                Require(exports == 2 && saved.BlockHandle == "ABC" && saved.X == 12345.125&&saved.Parts.Sum(p=>p.Primitives.Count)==5, "取消拾取不丢失草稿或部件");
                var tabs = root.Children.OfType<TabControl>().Single(); tabs.SelectedIndex = 1; Layout();
                Click("Export"); Require(exports == 2, "窗独立选择块"); cancelPick = false; Click("WindowPickBlock");
                Click("Export"); Require(exports == 3 && saved.Category == "Window" && saved.Code == "C1216" && saved.Axis == "X" && saved.X == 2300, "门窗草稿独立");
                var height = (TextBox)window.FindName("WindowHeight");
                foreach (var text in new[] { "NaN", "-1", "0", "100001", "abc", "1,600" }) {
                    height.Text = text; Click("Export"); Require(exports == 3, "拒绝错误尺寸：" + text);
                }
                height.Text = "1600";
                var units = (TextBox)window.FindName("WindowUnits"); units.Text = "0"; Click("Export"); Require(exports == 3, "拒绝零单位倍率");
                units.Text = "25.4";Click("WindowRefreshPreview"); Click("Export"); Require(exports == 4 && saved.MillimetresPerCadUnit == 25.4, "单位输入");
                tabs.SelectedIndex = 0; Layout(); Require(window.ReadSettings().Axis == "Y", "返回门保留设置");
                foreach (var kind in new[] { "Door", "Window" }) {
                    tabs.SelectedIndex = kind == "Door" ? 0 : 1; Layout();
                    var content = (Grid)((TabItem)tabs.SelectedItem).Content;
                    var contentBounds = content.TransformToAncestor(tabs).TransformBounds(new Rect(content.RenderSize));
                    Require(contentBounds.Bottom <= tabs.ActualHeight + 1, "页签内表单被裁切");
                    var scroll=(ScrollViewer)window.FindName(kind+"Fields");scroll.ScrollToTop();Layout();
                    Require(scroll.ActualHeight>180&&scroll.ActualWidth>=250,"属性栏不可使用");
                    foreach (var name in new[] { "Code", "Name", "Width", "Height", "Units", "X", "Y", "Z" }) {
                        var element=(TextBox)window.FindName(kind+name);Require(element.ActualHeight==36&&element.ActualWidth>=36,"输入框过窄或高度错误："+name);
                    }
                    scroll.ScrollToBottom();Layout();var refresh=(Button)window.FindName(kind+"RefreshPreview");
                    var refreshBounds=refresh.TransformToAncestor(scroll).TransformBounds(new Rect(refresh.RenderSize));
                    Require(refreshBounds.Bottom<=scroll.ActualHeight+1&&refreshBounds.Top>=-1,"滚动后末尾操作不可达");scroll.ScrollToTop();Layout();
                    var button = (Button)window.FindName("Export");
                    Require(button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize)).Bottom <= root.ActualHeight + 1, "导出按钮越界");
                    if (args.Length > 0) {
                        Directory.CreateDirectory(args[0]);
                        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var file = File.Create(Path.Combine(args[0], kind + "-" + size.Width + ".png"))) encoder.Save(file);
                    }
                }
                window.Close(); Console.WriteLine("COMPONENT_PLAN_UI_OK " + size + " names optionalCode parts highlighter conflict remove cancel axes stalePreview validation scroll bounds");
            }
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static ComponentPlanSymbol AuthorPlan(CadComponentPlanSettings settings)
    {
        return new ComponentPlanSymbol {Category=settings.Category,Code="QA",Name="实际 CAD 门套 / 门扇",Width=settings.Width,Height=settings.Height,Primitives={
            new ComponentPlanPrimitive {Kind="Line",SourcePath="frame-left",Y2=900},
            new ComponentPlanPrimitive {Kind="Line",SourcePath="frame-right",X1=900,X2=900,Y2=900},
            new ComponentPlanPrimitive {Kind="Line",SourcePath="leaf-a",X2=850,Y1=35,Y2=35},
            new ComponentPlanPrimitive {Kind="Line",SourcePath="leaf-b",X2=850,Y1=65,Y2=65},
            new ComponentPlanPrimitive {Kind="Arc",SourcePath="swing",Radius=850,StartDegrees=0,SweepDegrees=90}}};
    }
    private static void CheckSharedLibrary(string[] args)
    {
        var catalog=new ComponentCatalog(Path.Combine(Path.GetTempPath(),"WanLuoLibraryUi-"+Guid.NewGuid().ToString("N")));
        var symbol=new ComponentPlanSymbol {Name="双扇平开门",Code="M1825",Category="Door",Width=1800,Height=2500,
            Primitives={new ComponentPlanPrimitive {Kind="Line",X2=1800},new ComponentPlanPrimitive {Kind="Line",Y2=900},
                new ComponentPlanPrimitive {Kind="Arc",Radius=900,StartDegrees=0,SweepDegrees=90},new ComponentPlanPrimitive {Kind="Circle",X1=900,Radius=20}}};
        var saved=catalog.SavePlan(symbol);
        for(var i=0;i<15;i++){var copy=ComponentPlanSymbols.Load(ComponentPlanSymbols.Bytes(symbol));copy.Code="QA-"+i;copy.Name="实际门型 "+i;catalog.SavePlan(copy);}
        foreach(var size in new[]{new Size(760,560),new Size(960,640),new Size(1180,780),new Size(1600,960)}) {
            var window=new CadComponentLibraryWindow(catalog);var root=(Grid)window.Content;root.Background=window.Background;root.Margin=new Thickness(0);
            void Layout(){root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();}
            Layout();var buttons=Descendants(root).OfType<Button>().ToArray();var inserted=0;var stored=0;
            window.SetInsertionBase(new PointModel(100,0));window.Reload(saved.AssetId);
            window.InsertPlan=(record,axis,units,basePoint)=>{Require(record.AssetId==saved.AssetId&&axis=="X"&&units==1&&basePoint.X==100&&basePoint.Y==0,"插入共享身份、参数或预览基点错误");inserted++;return true;};
            window.StorePlan=(category,record)=>{Require(category=="Door","入库类别错误");stored++;};
            foreach(var caption in new[]{"插入 CAD","平面入库","更新所选平面"})buttons.Single(b=>(string)b.Content==caption).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(inserted==1&&stored==2,"图库 UI 操作未接通");
            foreach(var button in buttons) {
                var scroller=Descendants(root).OfType<ScrollViewer>().FirstOrDefault(view=>view.Content is StackPanel panel&&Descendants(panel).Contains(button));
                if(scroller!=null){scroller.ScrollToVerticalOffset(button.TransformToAncestor((StackPanel)scroller.Content).Transform(new Point()).Y);Layout();}
                var bounds=button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                Require(bounds.Left>=0&&bounds.Right<=size.Width+1&&bounds.Bottom<=size.Height+1&&bounds.Height>=32,"CAD 图库按钮越界");
            }
            var inputs=Descendants(root).OfType<TextBox>().ToArray();inputs.Single(t=>t.Text=="1").Text="NaN";
            buttons.Single(b=>(string)b.Content=="插入 CAD").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(inserted==1,"无效单位插入");
            inputs.Single(t=>t.Text=="NaN").Text="1";
            Require(Descendants(root).OfType<WrapPanel>().Any()&&Descendants(root).OfType<CadComponentPartPreview>().Count()>2,"图库必须用实际几何网格缩略图");
            window.ConfirmDelete=()=>true;window.DeleteSelected();Require(!catalog.Load().Records.Any(r=>r.AssetId==saved.AssetId),"图库删除无效");var deleted=Descendants(root).OfType<CheckBox>().Single(check=>(string)check.Content=="已删除资源");deleted.IsChecked=true;window.Reload(saved.AssetId);Require(window.SelectedRecord.IsDeleted,"已删除资源未显示");window.DeleteSelected();deleted.IsChecked=false;window.Reload(saved.AssetId);Require(catalog.Get(saved.AssetId).AssetId==saved.AssetId,"恢复丢失共享身份");Layout();
            if(args.Length>0) {
                foreach(var scroller in Descendants(root).OfType<ScrollViewer>())scroller.ScrollToTop();Layout();
                Directory.CreateDirectory(args[0]);var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(args[0],"CadLibrary-"+size.Width+".png")))encoder.Save(file);
            }
            window.Close();
        }
        Console.WriteLine("CAD_LIBRARY_UI_OK sharedRecord categories realGeometryGrid delete restore pinnedActions insertionFields anglePresets preview store update insert units bounds");
    }
    private static void CheckDoorEdit(string[] args)
    {
        var plan=AuthorPlan(new CadComponentPlanSettings {Category="Door",Width=900,Height=2100});plan.SchemaVersion=3;plan.Code="";plan.DoorAssembly="SingleSwing";
        plan.Parts=new System.Collections.Generic.List<ComponentPlanPart> {new ComponentPlanPart {Name="门框",Role="Frame",Primitives=new System.Collections.Generic.List<int>{0,1}},new ComponentPlanPart {Name="门扇",Role="PrimaryLeaf",Primitives=new System.Collections.Generic.List<int>{2,3}},new ComponentPlanPart {Name="开启",Role="OpeningSymbol",Primitives=new System.Collections.Generic.List<int>{4}}};
        foreach(var size in new[]{new Size(640,540),new Size(740,590)}) {
            var window=new CadLibraryDoorEditWindow(plan,new PointModel(),900,2100,false,false);var root=(Grid)window.Content;root.Margin=new Thickness(0);
            void Layout(){root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();}Layout();
            var fields=Descendants(root).OfType<TextBox>().ToArray();fields.Single(t=>t.Text=="900").Text="1200";
            var preview=Descendants(root).OfType<CadComponentPartPreview>().Single();Require(preview.Symbol.Width==1200&&preview.Symbol.Primitives[0].X1==0,"变尺预览未保持左框");
            var flips=Descendants(root).OfType<CheckBox>().ToArray();flips.Single(check=>(string)check.Content=="左右换向").IsChecked=true;
            Require(preview.Symbol.Primitives[0].X1==1200,"换向未动态预览");
            var applied=0;window.ApplyRequested=(_,move)=>{Require(window.DoorWidth==1200&&window.FlipAlong,"编辑参数未传递");applied++;return true;};
            var buttons=Descendants(root).OfType<Button>().ToArray();buttons.Single(button=>(string)button.Content=="应用尺寸与方向").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(applied==1,"编辑应用入口无效");
            fields.Single(field=>field.Text=="1200").Text="NaN";buttons.Single(button=>(string)button.Content=="应用尺寸与方向").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(applied==1,"非法尺寸不能应用");fields.Single(field=>field.Text=="NaN").Text="1200";Layout();
            var close=buttons.Single(button=>(string)button.Content=="关闭");Require(close.TransformToAncestor(root).TransformBounds(new Rect(close.RenderSize)).Bottom<=size.Height+1,"编辑底栏越界");
            if(args.Length>0){var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(args[0],"DoorEdit-"+size.Width+".png")))encoder.Save(file);}window.Close();
        }
        Console.WriteLine("CAD_DOOR_EDIT_UI_OK dynamicSize flip apply invalidInput compactBounds");
    }
    private static void CheckRegionEdit(string[] args)
    {
        var plan=new ComponentPlanSymbol {SchemaVersion=3,Category="Door",DoorAssembly="SingleSwing",Name="单开装修门",Code="M0921",Width=900,Height=2100,
            Primitives={new ComponentPlanPrimitive {Kind="Line",Y1=-100,Y2=100},new ComponentPlanPrimitive {Kind="Line",X1=900,X2=900,Y1=-100,Y2=100},new ComponentPlanPrimitive {Kind="Line",Y2=900},new ComponentPlanPrimitive {Kind="Line",X1=40,X2=40,Y2=900},new ComponentPlanPrimitive {Kind="Arc",Radius=900,SweepDegrees=90}},
            Parts=new System.Collections.Generic.List<ComponentPlanPart> {new ComponentPlanPart {Name="门套",Role="Casing",Primitives={0,1}},new ComponentPlanPart {Name="主门扇",Role="PrimaryLeaf",Primitives={2,3}},new ComponentPlanPart {Name="开启",Role="OpeningSymbol",Primitives={4}}}};
        foreach(var size in new[]{new Size(900,600),new Size(1180,780)}){
            var drafts=new[]{new CadPlanDoorDraft {Key="A",Name=plan.Name,Code="M0921",Source=plan,X=1200,Y=0,Width=900,Height=2100,WallEndX=4200,WallThickness=200}};
            var window=new CadPlanRegionWindow(drafts);var root=(Grid)window.Content;root.Margin=new Thickness(0);var submitted=0;
            window.PreviewRequested=all=>{foreach(var d in all){if(d.Deleted)continue;var p=ComponentPlanSymbols.DoorPlanVariant(d.Source,new PointModel(),d.Width,d.Height);if(d.OpeningAngle.HasValue)p=ComponentPlanSymbols.DoorOpeningVariant(p,d.OpeningAngle.Value);foreach(var line in p.Primitives){line.X1+=d.X;line.Y1+=d.Y;if(line.Kind=="Line"){line.X2+=d.X;line.Y2+=d.Y;}}d.Preview=p;}return new[]{new ComponentPlanPrimitive {Kind="Line",X2=1200,Y1=-100,Y2=-100},new ComponentPlanPrimitive {Kind="Line",X2=1200,Y1=100,Y2=100},new ComponentPlanPrimitive {Kind="Line",X1=2100,X2=4200,Y1=-100,Y2=-100},new ComponentPlanPrimitive {Kind="Line",X1=2100,X2=4200,Y1=100,Y2=100}};};
            window.SubmitRequested=all=>{Require(all.Single().Code=="D-01"&&all.Single().OpeningAngle==45,"提交属性错误");submitted++;};
            void Layout(){root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();}Layout();window.RefreshPreview();window.Select("A");window.Scene.Fit();Layout();
            var cursor=new Point(170,230);var beforeZoom=window.Scene.Unmap(cursor);window.Scene.ZoomAt(cursor,2);Require((window.Scene.Unmap(cursor)-beforeZoom).Length<1e-6,"缩放必须保持鼠标位置世界坐标");window.Scene.Fit();
            var fields=Descendants(root).OfType<TextBox>().ToArray();fields.Single(f=>f.Text=="M0921").Text="D-01";fields.Single(f=>f.Text=="900").Text="1200";fields.Single(f=>f.Text=="90").Text="45";
            var apply=Descendants(root).OfType<Button>().Single(b=>(string)b.Content=="应用属性");apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(window.Selected.Width==1200&&window.Selected.OpeningAngle==45&&submitted==0,"属性应留在草稿并动态预览");Require(Math.Abs(window.Selected.Preview.Primitives[4].SweepDegrees-45)<1e-6,"开启角度预览错误");
            Descendants(root).OfType<Button>().Single(b=>(string)b.Content=="30°").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(window.Selected.OpeningAngle==30,"角度预设未动态应用");window.Undo();Require(window.Selected.OpeningAngle==45,"角度预设未进入撤销");
            window.Undo();Require(window.Selected.Width==900&&window.Selected.Code=="M0921","草稿撤销未恢复属性");window.Redo();Require(window.Selected.Width==1200,"草稿重做失败");
            Require(!window.Edit(d=>d.Width=-1)&&window.Selected.Width==1200,"非法尺寸应恢复整个草稿");var gesture=window.Snapshot();window.Edit(d=>d.X+=200,false);window.CommitGesture(gesture);window.Undo();Require(window.Selected.X==1200,"夹点松开后的草稿撤销错误");
            Require(CadPlanRegionCanvas.Grips(window.Selected).Length==3,"只允许位置、方向、编号三个夹点");
            var handled=ComponentPlanSymbols.Load(ComponentPlanSymbols.Bytes(window.Selected.Preview));handled.Primitives.Add(new ComponentPlanPrimitive {Kind="Line",X1=1700,X2=1800,Y1=900,Y2=900});handled.Parts.Add(new ComponentPlanPart {Name="真实把手",Role="Handle",Primitives={5}});var atHandle=ComponentPlanSymbols.DirectionHandle(handled);Require(Math.Abs(atHandle.X-1750)<50&&Math.Abs(atHandle.Y-900)<50,"方向夹点必须靠近真实把手");
            window.Scene.BeginGrip(0);var moveStart=CadPlanRegionCanvas.Grips(window.Selected)[0];window.Scene.MoveGrip(moveStart+new Vector(250,0));window.Scene.ConfirmDrag();Require(window.Selected.X==1450&&window.Selected.Width==1200,"位置夹点不能改变宽度");window.Undo();
            window.Scene.BeginGrip(2);var labelStart=CadPlanRegionCanvas.Grips(window.Selected)[2];window.Scene.MoveGrip(labelStart+new Vector(180,-80));window.Scene.ConfirmDrag();Require(window.Selected.LabelAlong==180&&window.Selected.LabelNormal==-80&&window.Selected.X==1200,"编号夹点不得移动门");window.Undo();
            window.Scene.BeginGrip(1);var gripPoints=CadPlanRegionCanvas.Grips(window.Selected);var reference=gripPoints[1]-gripPoints[0];window.Scene.MoveGrip(gripPoints[0]+new Vector(reference.X<0?900:-900,reference.Y<0?900:-900));window.Scene.ConfirmDrag();Require(window.Selected.FlipAlong&&window.Selected.FlipAcross&&window.Selected.Width==1200,"方向夹点只改变开启方向");window.Undo();
            window.Scene.BeginMove();Require(window.Scene.CommitDistance("500",false)&&window.Selected.X==2500&&!window.Scene.HasGesture,"从较近终点输入净距错误");window.Undo();
            window.CenterRequested=()=>window.Edit(d=>d.X=(4200-d.Width)/2,!window.Scene.HasGesture);window.Scene.BeginMove();window.Scene.Center();Require(window.Selected.X==1500&&!window.Scene.HasGesture,"移动时右键居中未直接确认");window.Undo();
            var saved=window.Snapshot();var angle=Math.PI/5;var along=new Vector(Math.Cos(angle),Math.Sin(angle));var normal=new Vector(-along.Y,along.X);window.Edit(d=>{d.Angle=angle;d.X=3000+along.X*1200;d.Y=4000+along.Y*1200;d.WallStartX=3000;d.WallStartY=4000;d.WallEndX=3000+along.X*4200;d.WallEndY=4000+along.Y*4200;},false);
            window.Scene.BeginMove();Require(window.Scene.CommitDistance("450",true),"斜墙净距输入失败");Require((new Point(window.Selected.X,window.Selected.Y)-(new Point(3000,4000)+along*450)).Length<1e-6,"斜墙净距必须沿切向计算");
            window.Scene.BeginGrip(2);labelStart=CadPlanRegionCanvas.Grips(window.Selected)[2];window.Scene.MoveGrip(labelStart+along*180+normal*70);window.Scene.ConfirmDrag();Require(Math.Abs(window.Selected.LabelAlong-180)<1e-6&&Math.Abs(window.Selected.LabelNormal-70)<1e-6,"斜墙编号偏移必须沿切向与法向计算");
            window.Scene.BeginMove();moveStart=CadPlanRegionCanvas.Grips(window.Selected)[0];window.Scene.MoveGrip(moveStart+along*200);window.Scene.CancelDrag();Require((new Point(window.Selected.X,window.Selected.Y)-(new Point(3000,4000)+along*450)).Length<1e-6,"Esc 未恢复未确认的夹点草稿");window.Restore(saved);
            window.Edit(d=>d.Deleted=true);Require(window.Selected.Deleted,"删除草稿失败");window.Undo();Require(!window.Selected.Deleted,"恢复门草稿失败");Layout();
            var submit=Descendants(root).OfType<Button>().Single(b=>(string)b.Content=="提交到 CAD");Require(submit.TransformToAncestor(root).TransformBounds(new Rect(submit.RenderSize)).Bottom<=size.Height+1,"提交按钮越界");
            if(args.Length>0){var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(args[0],"RegionEdit-"+size.Width+".png")))encoder.Save(file);}
            window.Submit();Require(submitted==1,"区域提交未调用 CAD 回调");
        }
        Console.WriteLine("CAD_REGION_UI_OK threeGrips widthProperties nearEndDistance diagonalLabel diagonalMove rightCenter escDraft deferredCommit parameters angle undo redo invalidInput gestureUndo cursorZoom compactBounds");
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var descendant in Descendants(child))yield return descendant;}
    }
}
