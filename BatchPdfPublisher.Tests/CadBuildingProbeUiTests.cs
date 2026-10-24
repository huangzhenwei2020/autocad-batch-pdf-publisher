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
                entity.GeometryPaths.Add(new CadProbeGeometryPath { Kind = "Polyline", Closed = true,
                    PreviewPoints = new System.Collections.Generic.List<CadProbePoint> {
                        new CadProbePoint { X = 1000000, Y = 2000000, Z = 3000 },
                        new CadProbePoint { X = 1003000, Y = 2000000, Z = 3000 },
                        new CadProbePoint { X = 1003000, Y = 2001200, Z = 3000 },
                        new CadProbePoint { X = 1000000, Y = 2001200, Z = 3000 } } });
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
                var tabs = right.Children.OfType<TabControl>().Single();
                var table = (DataGrid)((TabItem)tabs.Items[0]).Content;
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
                tabs.SelectedIndex = 1; root.UpdateLayout();
                var geometryPanel = (DockPanel)((TabItem)tabs.Items[1]).Content;
                var tools = geometryPanel.Children.OfType<WrapPanel>().Single();
                var reference = tools.Children.OfType<ComboBox>().First();
                reference.SelectedIndex = 1;
                if (report.Entities[99].ReferenceKind != "阳台" || report.Entities[0].ReferenceKind != null)
                    throw new Exception("对照类别串到其他对象");
                var preview = geometryPanel.Children.OfType<FrameworkElement>().Last();
                if (preview.ActualWidth < 50 || preview.ActualHeight < 50) throw new Exception("小窗口原始几何预览不可见");
                var image = new RenderTargetBitmap((int)preview.ActualWidth, (int)preview.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(preview), null, new Rect(preview.RenderSize));
                image.Render(drawing);
                var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                var foreground = 0;
                for (var p = 0; p < pixels.Length; p += 4)
                    if (pixels[p] > 150 && pixels[p + 1] > 150 && pixels[p + 2] > 90) foreground++;
                if (args.Length > 0)
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var file = File.Create(Path.Combine(args[0], "probe-geometry-" + (int)size.Width + ".png"))) encoder.Save(file);
                }
                if (foreground < 50) throw new Exception("实际几何预览空白 " + preview.ActualWidth + "x" + preview.ActualHeight + " pixels=" + foreground);
                reference.SelectedIndex = 0;
                window.Close();
                Console.WriteLine("PASS 核验窗口 " + size.Width + "×" + size.Height + "：100对象、字段切换、表格可见、按钮在边界内");
            }
            var legacy = new CadBuildingProbeDocument { Entities = new System.Collections.Generic.List<CadBuildingProbeEntity> {
                new CadBuildingProbeEntity { Handle = "legacy", GeometryPaths = null, Fields = new System.Collections.Generic.List<CadProbeField>() } } };
            var legacyWindow = new TianzhengBuildingProbeWindow(legacy); legacyWindow.Close();
            Console.WriteLine("PASS 旧核验记录缺少几何仍可打开");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
