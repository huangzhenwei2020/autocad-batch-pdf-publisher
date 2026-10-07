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
            foreach (var size in new[] { new Size(620, 670), new Size(480, 640) }) {
                var exports = 0; var cancelPick = false; CadComponentPlanSettings saved = null;
                var window = new CadComponentPlanWindow("Door", owner => cancelPick ? null : new CadComponentPlanBlock { Handle = "ABC", Name = "门平面" },
                    owner => cancelPick ? null : new[] { 12345.125, 6789.5, 0d }, (owner, settings) => { exports++; saved = settings; return "已导出"; });
                var root = (Grid)window.Content; root.Background = window.Background; root.Margin = new Thickness(0);
                void Layout() { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
                void Click(string name) { ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                Layout(); Click("Export"); Require(exports == 0, "未选择块不能导出");
                Click("DoorPickBlock"); Click("DoorPickBase");
                ((RadioButton)window.FindName("DoorYAxis")).IsChecked = true; Click("Export");
                Require(exports == 1 && saved.Category == "Door" && saved.Axis == "Y" && saved.X == 12345.125 && saved.Width == 900, "门输入/拾取/Y方向");
                cancelPick = true; Click("DoorPickBlock"); Click("DoorPickBase"); Click("Export");
                Require(exports == 2 && saved.BlockHandle == "ABC" && saved.X == 12345.125, "取消拾取不丢失草稿");
                var tabs = root.Children.OfType<TabControl>().Single(); tabs.SelectedIndex = 1; Layout();
                Click("Export"); Require(exports == 2, "窗独立选择块"); cancelPick = false; Click("WindowPickBlock");
                Click("Export"); Require(exports == 3 && saved.Category == "Window" && saved.Code == "C1216" && saved.Axis == "X" && saved.X == 0, "门窗草稿独立");
                var height = (TextBox)window.FindName("WindowHeight");
                foreach (var text in new[] { "NaN", "-1", "0", "100001", "abc", "1,600" }) {
                    height.Text = text; Click("Export"); Require(exports == 3, "拒绝错误尺寸：" + text);
                }
                height.Text = "1600";
                var units = (TextBox)window.FindName("WindowUnits"); units.Text = "0"; Click("Export"); Require(exports == 3, "拒绝零单位倍率");
                units.Text = "25.4"; Click("Export"); Require(exports == 4 && saved.MillimetresPerCadUnit == 25.4, "单位输入");
                tabs.SelectedIndex = 0; Layout(); Require(window.ReadSettings().Axis == "Y", "返回门保留设置");
                foreach (var kind in new[] { "Door", "Window" }) {
                    tabs.SelectedIndex = kind == "Door" ? 0 : 1; Layout();
                    var content = (Grid)((TabItem)tabs.SelectedItem).Content;
                    var contentBounds = content.TransformToAncestor(tabs).TransformBounds(new Rect(content.RenderSize));
                    Require(contentBounds.Bottom <= tabs.ActualHeight + 1, "页签内表单被裁切");
                    foreach (var name in new[] { "PickBlock", "PickBase", "Code", "Width", "Height", "Units", "X", "Y", "Z" }) {
                        var element = (FrameworkElement)window.FindName(kind + name);
                        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                        Require(bounds.Left >= 0 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1 && bounds.Height == 36, "控件被裁切或高度不一致：" + name);
                        if (element is TextBox) Require(element.ActualWidth >= 36, "输入框过窄：" + name);
                    }
                    var button = (Button)window.FindName("Export");
                    Require(button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize)).Bottom <= root.ActualHeight + 1, "导出按钮越界");
                    if (args.Length > 0) {
                        Directory.CreateDirectory(args[0]);
                        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var file = File.Create(Path.Combine(args[0], kind + "-" + size.Width + ".png"))) encoder.Save(file);
                    }
                }
                window.Close(); Console.WriteLine("COMPONENT_PLAN_UI_OK " + size + " categories input pick cancel axes validation bounds");
            }
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void CheckSharedLibrary(string[] args)
    {
        var catalog=new ComponentCatalog(Path.Combine(Path.GetTempPath(),"WanLuoLibraryUi-"+Guid.NewGuid().ToString("N")));
        var symbol=new ComponentPlanSymbol {Name="双扇平开门",Code="M1825",Category="Door",Width=1800,Height=2500,
            Primitives={new ComponentPlanPrimitive {Kind="Line",X2=1800},new ComponentPlanPrimitive {Kind="Line",Y2=900},
                new ComponentPlanPrimitive {Kind="Arc",Radius=900,StartDegrees=0,SweepDegrees=90},new ComponentPlanPrimitive {Kind="Circle",X1=900,Radius=20}}};
        var saved=catalog.SavePlan(symbol);
        foreach(var size in new[]{new Size(760,560),new Size(960,640)}) {
            var window=new CadComponentLibraryWindow(catalog);var root=(Grid)window.Content;root.Background=window.Background;root.Margin=new Thickness(0);
            void Layout(){root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();}
            Layout();var buttons=Descendants(root).OfType<Button>().ToArray();var inserted=0;var stored=0;
            window.InsertPlan=(record,axis,units)=>{Require(record.AssetId==saved.AssetId&&axis=="X"&&units==1,"插入共享身份或参数错误");inserted++;return true;};
            window.StorePlan=(category,record)=>{Require(category=="Door","入库类别错误");stored++;};
            foreach(var caption in new[]{"插入 CAD","平面入库","更新所选平面"})buttons.Single(b=>(string)b.Content==caption).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(inserted==1&&stored==2,"图库 UI 操作未接通");
            foreach(var button in buttons) {
                var bounds=button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                Require(bounds.Left>=0&&bounds.Right<=size.Width+1&&bounds.Bottom<=size.Height+1&&bounds.Height>=32,"CAD 图库按钮越界");
            }
            var inputs=Descendants(root).OfType<TextBox>().ToArray();inputs.Single(t=>t.Text=="1").Text="NaN";
            buttons.Single(b=>(string)b.Content=="插入 CAD").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(inserted==1,"无效单位插入");
            inputs.Single(t=>t.Text=="NaN").Text="1";
            if(args.Length>0) {
                Directory.CreateDirectory(args[0]);var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(args[0],"CadLibrary-"+size.Width+".png")))encoder.Save(file);
            }
            window.Close();
        }
        Console.WriteLine("CAD_LIBRARY_UI_OK sharedRecord categories preview store update insert units bounds");
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var descendant in Descendants(child))yield return descendant;}
    }
}
