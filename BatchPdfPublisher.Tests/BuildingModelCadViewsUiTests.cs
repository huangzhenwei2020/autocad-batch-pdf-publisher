using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;

internal static class BuildingModelCadViewsUiTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "WanLuoCadViews-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var original = Model(root, "Old", "旧模型图纸", 1000);
            var current = Model(root, "Edited", "当前编辑模型图纸", 7600);
            foreach (var size in new[] { new Size(780, 570), new Size(1280, 790) })
            {
                StudioLaunch.RememberActiveModel(root, current);
                var fail = false;
                StudioViewSource Reload() => fail ? throw new IOException("来源读取失败") : StudioLaunch.ActiveViewSource(root, "Old");
                var window = new BuildingModelBatchWindow("Old", Reload(), null, Reload) { Width = size.Width, Height = size.Height };
                window.Show();
                void Layout() { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
                void Click(string name) { ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(); }
                BuildingModelBatchRow[] Rows() => ((ListBox)window.FindName("ViewsList")).Items.Cast<BuildingModelBatchRow>().ToArray();
                Layout();
                Require(Rows().Single().Name == "当前编辑模型图纸", "初始清单仍来自旧模型");
                Rows()[0].IsChecked = true;
                Click("RefreshButton");
                Require(Rows()[0].IsChecked, "同一模型刷新丢失勾选");
                StudioLaunch.RememberActiveModel(root, original);
                Click("StartButton");
                Require(window.IsVisible && Rows().Single().Name == "旧模型图纸" && !Rows()[0].IsChecked,
                    "开始落图时切换模型应刷新并清除相同 ID 的旧勾选");
                Rows()[0].IsChecked = true;
                StudioLaunch.RememberActiveModel(root, current);
                Click("RefreshButton");
                Require(!Rows()[0].IsChecked && Rows()[0].Name == "当前编辑模型图纸", "刷新切换混用勾选");
                Rows()[0].IsChecked = true;
                fail = true;
                Click("RefreshButton");
                Require(Rows().Length == 0 && !((Button)window.FindName("StartButton")).IsEnabled, "读取失败保留可落图的旧清单");
                fail = false;
                Click("RefreshButton");
                var label = (TextBlock)window.FindName("ProjectText");
                Require(label.Text.Contains("Edited") && (string)label.ToolTip == current, "模型名称和真实路径不可见");
                var content = (Grid)window.Content;
                content.Background = window.Background;
                if (args.Length > 0)
                {
                    Directory.CreateDirectory(args[0]);
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(args[0], "cad-views-" + size.Width + ".png"))) png.Save(stream);
                }
                window.Close();
                var accepted = new BuildingModelBatchWindow("Old", Reload(), null, Reload);
                accepted.Loaded += (_, __) => accepted.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var row = ((ListBox)accepted.FindName("ViewsList")).Items.Cast<BuildingModelBatchRow>().Single();
                    row.IsChecked = true;
                    ((Button)accepted.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                Require(accepted.ShowDialog() == true && accepted.SelectedEntries.Single().FilePath.StartsWith(Path.GetDirectoryName(current)),
                    "确认后落图来源不是当前模型");
                Console.WriteLine("CAD_VIEWS_UI_OK " + size + " currentModel sameId refresh clearSelection validateBeforeAccept errorRecovery actualPath");
            }
            app.Shutdown();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Model(string root, string name, string title, double length)
    {
        var path = Path.Combine(root, StudioLaunch.ModelFolderName, name, "model.json");
        BuildingModelJson.SaveModel(path, new BuildingModelDocument { Name = name });
        var view = new ViewDocument { Id = "same-id", Title = title, Kind = ViewKind.Plan };
        view.Lines.Add(new ViewLine { X2 = length, Layer = ViewLayers.Cut });
        BuildingModelJson.SaveView(Path.Combine(Path.GetDirectoryName(path), StudioLaunch.ViewsFolderName, view.Id + ".json"), view);
        return path;
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
