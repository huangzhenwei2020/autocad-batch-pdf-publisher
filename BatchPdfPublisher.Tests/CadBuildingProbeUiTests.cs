using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;

internal static class CadBuildingProbeUiTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var report = new CadBuildingProbeDocument { DbmodBefore = 1, DbmodAfter = 1 };
            for (var i = 0; i < 100; i++)
            {
                var entity = new CadBuildingProbeEntity { Handle = (256 + i).ToString("X"),
                    DxfName = "TCH_WALL", ComType = "TDbWall", Category = "天正墙候选", Layer = "WALL",
                    CurveStatus = "原生曲线采样已读取；是否为墙定位线及直线须核验",
                    CurveStart = new CadProbePoint { X = 12500, Y = 25000 },
                    CurveEnd = new CadProbePoint { X = 18500, Y = 25000 } };
                entity.Fields.Add(CadBuildingProbeRules.Field("LeftWidth", true, 20d, null));
                entity.Fields.Add(CadBuildingProbeRules.Field("RightWidth", true, 100d, null));
                entity.Fields.Add(CadBuildingProbeRules.Field("Height", false, null, "MissingMemberException: 对象未提供该属性"));
                CadBuildingProbeRules.DeriveWall(entity); report.Entities.Add(entity);
            }
            foreach (var size in new[] { new Size(1060, 640), new Size(560, 320) })
            {
                var window = new TianzhengBuildingProbeWindow(report);
                var root = (Grid)window.Content;
                root.Background = window.Background;
                root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                var body = root.Children.OfType<Grid>().First();
                var footer = root.Children.OfType<Grid>().Last();
                var list = body.Children.OfType<ListBox>().Single();
                var right = body.Children.OfType<Grid>().Single();
                var table = right.Children.OfType<DataGrid>().Single();
                if (table.Items.Count != 3 || list.Items.Count != 100) throw new Exception("核验数据没有绑定");
                if (table.ActualHeight < 100) throw new Exception("小窗口字段表被挤没了");
                var footerBounds = footer.TransformToAncestor(root).TransformBounds(new Rect(footer.RenderSize));
                if (footerBounds.Bottom > root.ActualHeight + 1) throw new Exception("按钮越出窗口");
                list.SelectedIndex = 99;
                if (((CadProbeField)table.Items[0]).Number != 20) throw new Exception("切换对象未更新表格");
                root.UpdateLayout();
                if (args.Length > 0)
                {
                    Directory.CreateDirectory(args[0]);
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(Path.Combine(args[0], "probe-" + (int)size.Width + ".png"))) encoder.Save(file);
                }
                window.Close();
                Console.WriteLine("PASS 核验窗口 " + size.Width + "×" + size.Height + "：100对象、字段切换、表格可见、按钮在边界内");
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
