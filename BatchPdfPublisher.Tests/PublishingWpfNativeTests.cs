using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.ViewModels;
using BatchPdfPublisher.Views;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using Image = System.Windows.Controls.Image;
using Visibility = System.Windows.Visibility;

[assembly: CommandClass(typeof(PublishingWpfNativeTests))]
public sealed class PublishingWpfNativeTests
{
    private static int _desktopPass;
    private static DateTime _desktopNext;
    private static BppSelectionPublishWindow _selectionWindow;
    private static bool _desktopRunning;
    private static bool _desktopTick;
    private static Document _desktopDocument;
    private static DispatcherTimer _desktopTimer;
    private static PublisherWindow _lastClosedWindow;
    private static SheetItem _batchSecondSheet;
    private static string[] _batchPreviewPaths;
    [CommandMethod("WLWPFQAQUIT", CommandFlags.Session)]
    public void QuitTest()
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        Check(ReferenceEquals(document, _desktopDocument), "Only the drawing created by this test can be closed");
        document.CloseAndDiscard();
        AcApplication.Quit();
    }

    [CommandMethod("WLBPPWPFDESKTOPQA", CommandFlags.Session)]
    public void RunDesktop()
    {
        if (_desktopRunning)
            return;
        _desktopRunning = true;
        _desktopDocument = AcApplication.DocumentManager.MdiActiveDocument;
        var root = Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT");
        PrepareProject(root);
        _desktopPass = 0;
        _desktopNext = DateTime.UtcNow.AddSeconds(1);
        _desktopTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _desktopTimer.Tick += CheckDesktop;
        _desktopTimer.Start();
    }

    private static void CheckDesktop(object sender, EventArgs args)
    {
        if (_desktopTick || DateTime.UtcNow < _desktopNext)
            return;
        _desktopTick = true;
        var root = Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT");
        try
        {
            var field = typeof(BatchPdfPublisher.Commands).GetField("_publisherWindow", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var window = field.GetValue(null) as PublisherWindow;
            File.AppendAllText(Path.Combine(root, "desktop-steps.txt"), _desktopPass + " visible=" + window?.IsVisible + " rows=" + (window?.FindName("SheetsGrid") as DataGrid)?.Items.Count + Environment.NewLine);
            if (_desktopPass == 6)
            {
                if (((Image)window.FindName("PreviewImage")).Source == null)
                    return;
                SaveImage(window, root, "desktop-preview");
                _selectionWindow = new BppSelectionPublishWindow(AcApplication.DocumentManager.MdiActiveDocument, window.ViewModel);
                _selectionWindow.Owner = window;
                var sheets = (System.Collections.ObjectModel.ObservableCollection<SheetItem>)typeof(BppSelectionPublishWindow).GetField("_sheets", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(_selectionWindow);
                sheets.Add(window.ViewModel.SelectedSheet);
                ((TextBox)_selectionWindow.FindName("Folder")).Text = root;
                ((TextBox)_selectionWindow.FindName("FileName")).Text = "wpf-selected.pdf";
                AcApplication.ShowModelessWindow(_selectionWindow);
                Buttons(_selectionWindow).First(button => Equals(button.Content, "合并并发布 PDF")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                _desktopPass = 7;
                return;
            }

            if (_desktopPass == 7)
            {
                if (!((TextBlock)_selectionWindow.FindName("Status")).Text.StartsWith("已发布"))
                    return;
                Check(new FileInfo(Path.Combine(root, "wpf-selected.pdf")).Length > 1000, "The WPF publish action produced no PDF");
                using (var pdf = PdfSharp.Pdf.IO.PdfReader.Open(Path.Combine(root, "wpf-selected.pdf"), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                {
                    // CAD PDF drivers may round the MediaBox to whole PDF points.
                    Check(pdf.PageCount == 1 && Math.Abs(pdf.Pages[0].Width.Millimeter - 420) < 0.5 && Math.Abs(pdf.Pages[0].Height.Millimeter - 297) < 0.5, "WPF publication changed A3 paper size");
                }

                SaveImage(_selectionWindow, root, "desktop-published");
                _selectionWindow.Close();
                PrepareVisualReview(window, root);
                _desktopPass = 8;
                return;
            }

            if (_desktopPass == 8)
            {
                if (((Image)window.FindName("PreviewImage")).Source == null)
                    return;
                SaveImage(window, root, "redesign-review");
                var model = window.ViewModel;
                var grid = (DataGrid)window.FindName("SheetsGrid");
                var sheets = grid.Items.Cast<SheetItem>().Take(2).ToList();
                _batchSecondSheet = sheets[1];
                _batchSecondSheet.SourceFile = model.CadFiles[1].Path;
                var missing = new SheetItem
                {
                    SourceFile = Path.Combine(root, "missing-preview.dwg"),
                    Building = "四层",
                    SheetNumber = "BAD",
                    Frame = "A3",
                    MaxX = 42000,
                    MaxY = 29700
                };
                model.Sheets.Clear();
                foreach (var sheet in sheets)
                    model.Sheets.Add(sheet);
                model.Sheets.Add(missing);
                model.ApplySheetEdits();
                model.SelectedSheet = missing;
                Pump();
                Check(grid.Items.Count == 3, "Batch preview fixture lost rows");
                Buttons(window).First(button => Equals(button.Content, "全部预览")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                _desktopPass = 9;
                return;
            }

            if (_desktopPass == 9)
            {
                if (!(bool)typeof(PublisherWindow).GetProperty("Busy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window))
                {
                    Check(((Image)window.FindName("PreviewImage")).Source != null, "All previews left the image empty when one sheet failed");
                    Check(((TextBlock)window.FindName("PreviewNotice")).Text.Contains("成功 2 张") && ((TextBlock)window.FindName("PreviewNotice")).Text.Contains("失败 1 张"), "Batch preview results were not reported");
                    var cache = (System.Collections.Generic.Dictionary<SheetItem, System.Collections.Generic.KeyValuePair<string, string>>)typeof(PublisherWindow).GetField("_previewPaths", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window);
                    Check(cache.Keys.Count(sheet => window.ViewModel.Sheets.Contains(sheet)) == 2, "Successful batch previews were not retained");
                    _batchPreviewPaths = cache.Values.Select(entry => entry.Value).ToArray();
                    Check(_batchPreviewPaths.All(File.Exists), "Prepared previews were deleted before browsing");
                    File.Delete(_batchSecondSheet.SourceFile);
                    ((DataGrid)window.FindName("SheetsGrid")).SelectedItem = _batchSecondSheet;
                    _desktopPass = 10;
                }

                return;
            }

            if (_desktopPass == 10)
            {
                if (((Image)window.FindName("PreviewImage")).Source == null)
                    return;
                Check(ReferenceEquals(window.ViewModel.SelectedSheet, _batchSecondSheet), "Preview navigation selected the wrong row");
                Check(!File.Exists(_batchSecondSheet.SourceFile), "Cached preview test unexpectedly restored the source drawing");
                SaveImage(window, root, "all-previews-browsing");
                Buttons(window).First(button => Equals(button.Content, "上一张")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                _desktopPass = 11;
                return;
            }

            if (_desktopPass == 11)
            {
                if (((Image)window.FindName("PreviewImage")).Source == null)
                    return;
                Check(!ReferenceEquals(window.ViewModel.SelectedSheet, _batchSecondSheet), "Previous preview did not change the selected sheet");
                Buttons(window).First(button => Equals(button.Content, "下一张")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                _desktopPass = 12;
                return;
            }

            if (_desktopPass == 12)
            {
                if (((Image)window.FindName("PreviewImage")).Source == null)
                    return;
                Check(ReferenceEquals(window.ViewModel.SelectedSheet, _batchSecondSheet), "Next preview did not restore the cached second sheet");
                window.ViewModel.MarginMode = "无白边（满幅）";
                Pump();
                Check(((Image)window.FindName("PreviewImage")).Source == null, "Changing print settings left an outdated preview on screen");
                foreach (var sheet in window.ViewModel.Sheets)
                    File.Delete(sheet.SourceFile);
                Buttons(window).First(button => Equals(button.Content, "全部预览")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                _desktopPass = 13;
                return;
            }

            if (_desktopPass == 13)
            {
                if ((bool)typeof(PublisherWindow).GetProperty("Busy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window))
                    return;
                Check(((Image)window.FindName("PreviewImage")).Source == null && ((TextBlock)window.FindName("PreviewNotice")).Text.Contains("未生成可用预览"), "A failed batch displayed stale content or lost its failure status");
                window.ViewModel.Sheets.Clear();
                window.ViewModel.ApplySheetEdits();
                Pump();
                Buttons(window).First(button => Equals(button.Content, "全部预览")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((TextBlock)window.FindName("PreviewNotice")).Text.Contains("没有可预览"), "Empty preview list did not explain its state");
                window.Close();
                Check(_batchPreviewPaths.All(path => !File.Exists(path)), "Closing BPP left owned batch preview PDFs behind");
                _desktopTimer.Stop();
                File.WriteAllText(Path.Combine(root, "desktop-ok.txt"), "PUBLISH_WPF_DESKTOP_OK");
                AcApplication.DocumentManager.MdiActiveDocument.SendStringToExecute("(setvar \"TRUSTEDPATHS\" wlqatrusted)\nWLWPFQAQUIT\n", true, false, false);
                return;
            }

            if (_desktopPass % 2 == 0)
            {
                Check(window == null || !ReferenceEquals(window, _lastClosedWindow), "The closed publisher instance was reused");
                File.AppendAllText(Path.Combine(root, "desktop-steps.txt"), "request-open " + _desktopPass + Environment.NewLine);
                new BatchPdfPublisher.Commands().OpenPublisher();
            }
            else
            {
                Check(window != null && window.IsVisible, "Normal BPP entry did not show the WPF window");
                var grid = (DataGrid)window.FindName("SheetsGrid");
                Check(grid.Items.Count == 1000, "Desktop row count " + grid.Items.Count + ", project " + window.ViewModel.SelectedProject?.Name);
                grid.SelectedItems.Clear();
                grid.SelectedItems.Add(grid.Items[1]);
                grid.SelectedItems.Add(grid.Items[2]);
                Buttons(window).First(button => Equals(button.Content, "移到顶部")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(grid.SelectedItems.Count == 2, "Desktop group move lost selection");
                SaveImage(window, root, "desktop-" + _desktopPass);
                if (_desktopPass == 5)
                    Buttons(window).First(button => Equals(button.Content, "更新预览")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else
                {
                    _lastClosedWindow = window;
                    window.Close();
                    Check(!window.IsVisible, "Publisher close was canceled. Publishing=" + window.ViewModel.IsPublishing + ", scanning=" + window.ViewModel.IsScanning + ", status=" + window.ViewModel.Status);
                }
            }

            _desktopPass++;
            _desktopNext = DateTime.UtcNow.AddSeconds(2);
        }
        catch (System.Exception error)
        {
            _desktopTimer.Stop();
            File.WriteAllText(Path.Combine(root, "failure.txt"), error.ToString());
        }
        finally
        {
            _desktopTick = false;
        }
    }

    [CommandMethod("WLBPPWPFQA")]
    public void Run()
    {
        var root = Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT");
        try
        {
            if (string.IsNullOrEmpty(root))
                throw new System.Exception("Private test root required");
            PrepareProject(root);
            for (var pass = 0; pass < 3; pass++)
            {
                File.AppendAllText(Path.Combine(root, "steps.txt"), "create " + pass + Environment.NewLine);
                var window = new PublisherWindow();
                window.Show();
                Pump();
                var grid = (DataGrid)window.FindName("SheetsGrid");
                Check(grid.Items.Count == 1000, "Rows were lost");
                if (pass > 0)
                    Check(grid.Columns.First(column => Equals(column.Header, "打印样式")).Visibility == Visibility.Collapsed, "Column visibility did not survive reopening");
                if (pass == 0)
                    CheckNavigationAndScrolling(window, root);
                grid.SelectedItems.Clear();
                grid.SelectedItems.Add(grid.Items[2]);
                grid.SelectedItems.Add(grid.Items[3]);
                var selected = grid.SelectedItems.Cast<SheetItem>().ToArray();
                Buttons(window).First(button => Equals(button.Content, "移到顶部")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(ReferenceEquals(grid.Items[0], selected[0]) && ReferenceEquals(grid.Items[1], selected[1]) && grid.SelectedItems.Count == 2, "Group move failed");
                Buttons(window).First(button => Equals(button.Content, "移到尾部")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(ReferenceEquals(grid.Items[998], selected[0]) && grid.SelectedItems.Count == 2, "Bottom move failed");
                ((ComboBox)window.FindName("QuickScale")).SelectedItem = "1:75";
                Pump();
                Check(window.ViewModel.SelectedSheet.PrintScale == "1:75", "Quick scale did not commit the selected value");
                Check(grid.SelectedItems.Count == 2, "Quick settings lost multi-selection");
                window.Width = 1200;
                window.Height = 740;
                Pump();
                if (pass == 0)
                {
                    var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var file = File.Create(Path.Combine(root, "wpf-main.png")))
                        encoder.Save(file);
                }

                window.Close();
                Pump();
                File.AppendAllText(Path.Combine(root, "steps.txt"), "closed " + pass + Environment.NewLine);
            }

            var model = new PublisherViewModel();
            var document = AcApplication.DocumentManager.MdiActiveDocument;
            model.Frames.Add(new FrameDefinition { BlockName = "WPF框", PaperSize = "A3", PaperOrientation = "横向", PageNumberAttributeTag = "PAGE_NO", DefaultPageNumber = "007" });
            var projectWindow = new BppProjectWindow(model);
            CheckWindow(projectWindow, root, "project");
            var outputWindow = new BppOutputSettingsWindow(model);
            CheckWindow(outputWindow, root, "output-settings");
            Check(!string.IsNullOrEmpty(model.MarginMode), "Output settings cleared the selected margin policy");
            var frameWindow = new BppFrameManagerWindow(model);
            CheckWindow(frameWindow, root, "frames");
            var createWindow = new BppFrameCreationWindow(document, () =>
            {
            });
            CheckWindow(createWindow, root, "create");
            var selectionWindow = new BppSelectionPublishWindow(document, model);
            CheckWindow(selectionWindow, root, "selection");
            var catalogWindow = new BppCatalogWindow(document, model.Sheets.ToList(), () =>
            {
            });
            CheckWindow(catalogWindow, root, "catalog");
            var validationWindow = new BppValidationWindow(new SheetValidationIssue[0], null, null);
            CheckWindow(validationWindow, root, "validation");
            var editor = new FrameRegistrationWindow("WPF框", new FrameSizeGuess { PaperSize = "A3", PrintScale = "1:100" }, new System.Collections.Generic.Dictionary<string, string> { { "PAGE_NO", "007" } });
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                Buttons(editor).First(button => Equals(button.Content, "保存登记")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
            editor.ShowDialog();
            Pump();
            Check(editor.Definition != null && editor.Definition.DefaultPageNumber == "007" && editor.Definition.PageNumberAttributeTag == "PAGE_NO", "Page mapping lost in WPF registration");
            model.Dispose();
            AcApplication.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nPUBLISH_WPF_QA_OK\n");
        }
        catch (System.Exception error)
        {
            File.WriteAllText(Path.Combine(root, "failure.txt"), error.ToString());
            AcApplication.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nPUBLISH_WPF_QA_FAILED " + error + "\n");
        }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok)
            throw new System.Exception(message);
    }

    private static void CheckNavigationAndScrolling(PublisherWindow window, string root)
    {
        var model = window.ViewModel;
        var file = Path.Combine(root, "second-source.dwg");
        model.AddCadFiles(new[] { file });
        var extra = new SheetItem
        {
            SourceFile = file,
            SourceLayout = "Model",
            Building = "测试层",
            SheetNumber = "SECOND",
            Frame = "A3",
            PrintScale = "1:100",
            PaperOrientation = "横向",
            MaxX = 42000,
            MaxY = 29700
        };
        model.Sheets.Add(extra);
        model.ApplySheetEdits();
        Pump();
        var list = (ListBox)window.FindName("CadFilesList");
        list.SelectedItem = model.CadFiles.First(item => item.Path == file);
        Pump();
        var grid = (DataGrid)window.FindName("SheetsGrid");
        Check(grid.Items.Count == 1 && ReferenceEquals(grid.Items[0], extra), "File navigation did not filter its own sheets");
        ((TabControl)window.FindName("NavigationTabs")).SelectedIndex = 1;
        Pump();
        Check(grid.Items.Count == 1001, "Subproject mode retained the file filter");
        model.Sheets.Remove(extra);
        model.RemoveCadFile(file);
        model.ApplySheetEdits();
        Pump();
        var viewer = Descendants<ScrollViewer>(grid).First();
        viewer.ScrollToTop();
        Pump();
        var bar = Descendants<System.Windows.Controls.Primitives.ScrollBar>(grid).First(item => item.Orientation == Orientation.Vertical && item.IsVisible);
        Check(bar.ActualWidth <= 14.5 && bar.Template.FindName("PART_Track", bar) != null, "Dark scrollbar template was not applied");
        System.Windows.Controls.Primitives.ScrollBar.PageDownCommand.Execute(null, bar);
        Pump();
        Check(viewer.VerticalOffset > 0, "Scrollbar page command stopped scrolling");
        var horizontal = Descendants<System.Windows.Controls.Primitives.ScrollBar>(grid).First(entry => entry.Orientation == Orientation.Horizontal && entry.IsVisible);
        System.Windows.Controls.Primitives.ScrollBar.PageRightCommand.Execute(null, horizontal);
        Pump();
        Check(viewer.HorizontalOffset > 0, "Horizontal scrollbar command stopped scrolling");
        viewer.ScrollToHorizontalOffset(0);
        var headers = Buttons(window).Where(button => button.ContextMenu != null).SelectMany(button => button.ContextMenu.Items.OfType<MenuItem>()).Select(item => item.Header as string).ToList();
        Check(!headers.Contains("目录打印") && !Buttons(window).Any(button => Equals(button.Content, "更多工具")) && Buttons(window).Any(button => Equals(button.Content, "存入工程")) && headers.Contains("全部保存") && headers.Contains("框选发布"), "Existing tool entry coverage changed");
        var tabs = (TabControl)window.FindName("NavigationTabs");
        var firstTab = (TabItem)tabs.Items[0];
        var secondTab = (TabItem)tabs.Items[1];
        Check(Math.Abs(firstTab.ActualWidth - secondTab.ActualWidth) < 1 && Math.Abs(firstTab.ActualHeight - secondTab.ActualHeight) < 1, "Navigation tabs are not aligned equally");
        tabs.SelectedIndex = 0;
        Pump();
        var groupButton = Buttons(window).First(button => button.Tag is CadFileGroupItem);
        Check(((Grid)groupButton.Content).ActualWidth > groupButton.ActualWidth - 16, "Subproject names and counts are not constrained to the available row width");
        var columnButton = Buttons(window).First(button => Equals(button.Content, "列设置"));
        columnButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        var item = columnButton.ContextMenu.Items.OfType<MenuItem>().First(entry => Equals(entry.Header, "打印样式"));
        item.IsChecked = false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(grid.Columns.First(column => Equals(column.Header, "打印样式")).Visibility == Visibility.Collapsed, "Column menu did not apply visibility");
        columnButton.ContextMenu.IsOpen = false;
    }

    private static void PrepareVisualReview(PublisherWindow window, string root)
    {
        var model = window.ViewModel;
        model.CreateOrSelectProject("界面回归项目", Path.Combine(root, "visual-project"));
        var groups = new[]
        {
            "四层",
            "五层",
            "六层",
            "七层",
            "八层"
        };
        var counts = new[]
        {
            18,
            16,
            14,
            11,
            12
        };
        var names = new[]
        {
            "原始结构图",
            "拆除墙体图",
            "完成面尺寸图",
            "平面布置图",
            "家具尺寸图",
            "地面铺装图",
            "天花布置图",
            "天花尺寸图",
            "灯具定位图",
            "开关连线图",
            "机电点位图",
            "给排水点位图",
            "立面索引图",
            "办公区立面图"
        };
        for (var group = 0; group < groups.Length; group++)
        {
            var fileName = group == 0 ? "资源县机关第二幼儿园多媒体教室改造工程" : group == 1 ? "资源县第一小学录播室改造项目" : groups[group];
            var file = Path.Combine(root, fileName + ".dwg");
            File.Copy(Path.Combine(root, "wpf-print-fixture.dwg"), file, true);
            model.AddCadFiles(new[] { file });
            for (var index = 0; index < counts[group]; index++)
                model.Sheets.Add(new SheetItem { SourceFile = file, SourceLayout = "Model", Building = groups[group], Order = index + 1, PageNumber = (index + 1).ToString("D2"), SheetNumber = "PM-" + (index + 1).ToString("D2"), SheetName = names[index % names.Length], Frame = "A3", PrintScale = "1:100", PaperOrientation = "横向", MaxX = 42000, MaxY = 29700 });
        }

        model.ApplySheetEdits();
        model.SelectedBuilding = groups[0];
        model.PlotStyle = "monochrome.ctb";
        model.SortVisibleSheets(SheetSortMode.PageNumberAscending);
        Pump();
        var grid = (DataGrid)window.FindName("SheetsGrid");
        Check(grid.Items.Count == 18, "Visual review lost the selected group");
        grid.SelectedItems.Clear();
        grid.SelectedItems.Add(grid.Items[1]);
        grid.SelectedItems.Add(grid.Items[2]);
        grid.SelectedItems.Add(grid.Items[3]);
        window.Width = 1640;
        window.Height = 920;
        grid.ScrollIntoView(grid.Items[0]);
        Pump();
        Buttons(window).First(button => Equals(button.Content, "更新预览")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    private static string CreateDrawing(string root)
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        using (document.LockDocument())
        using (var transaction = document.Database.TransactionManager.StartTransaction())
        {
            var blocks = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
            var space = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var outline = new Polyline();
            outline.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
            outline.AddVertexAt(1, new Point2d(42000, 0), 0, 0, 0);
            outline.AddVertexAt(2, new Point2d(42000, 29700), 0, 0, 0);
            outline.AddVertexAt(3, new Point2d(0, 29700), 0, 0, 0);
            outline.Closed = true;
            space.AppendEntity(outline);
            transaction.AddNewlyCreatedDBObject(outline, true);
            var label = new DBText
            {
                TextString = "WPF print verification",
                Position = new Point3d(5000, 15000, 0),
                Height = 1000
            };
            space.AppendEntity(label);
            transaction.AddNewlyCreatedDBObject(label, true);
            foreach (var x in new[]
            {
                4000.0,
                16000.0,
                28000.0,
                35000.0
            }

            )
            {
                var wall = new Line(new Point3d(x, 4000, 0), new Point3d(x, 26000, 0))
                {
                    LineWeight = LineWeight.LineWeight030
                };
                space.AppendEntity(wall);
                transaction.AddNewlyCreatedDBObject(wall, true);
            }

            foreach (var y in new[]
            {
                4000.0,
                14500.0,
                26000.0
            }

            )
            {
                var wall = new Line(new Point3d(4000, y, 0), new Point3d(35000, y, 0))
                {
                    LineWeight = LineWeight.LineWeight030
                };
                space.AppendEntity(wall);
                transaction.AddNewlyCreatedDBObject(wall, true);
            }

            transaction.Commit();
        }

        var path = Path.Combine(root, "wpf-print-fixture.dwg");
        using (document.LockDocument())
            document.Database.SaveAs(path, DwgVersion.Current);
        return path;
    }

    private static void PrepareProject(string root)
    {
        Check(!string.IsNullOrWhiteSpace(root), "Private test root required");
        Directory.CreateDirectory(root);
        var fixture = CreateDrawing(root);
        var store = new PublishPlanStore();
        var project = store.CreateProject("WPF临时项目", Path.Combine(root, "project"));
        project.SheetSort = "Custom";
        project.CadFiles.Add(fixture);
        for (var index = 0; index < 1000; index++)
            project.SavedSheets.Add(new SheetCatalogItem { Building = "测试层", SourceFile = fixture, SourceLayout = "Model", Order = index + 1, SheetNumber = "S" + index, SheetName = "图纸" + index, Frame = "A3", PaperOrientation = "横向", PrintScale = "1:100", MaxX = 42000, MaxY = 29700 });
        store.SaveProject(project);
        store.SetActiveProject(project.Name);
    }

    private static void SaveImage(Window window, string root, string name)
    {
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(root, "wpf-" + name + ".png")))
            encoder.Save(file);
    }

    private static void CheckWindow(Window window, string root, string name)
    {
        File.AppendAllText(Path.Combine(root, "steps.txt"), "show " + name + Environment.NewLine);
        window.Show();
        Pump();
        window.Width = Math.Max(window.MinWidth, 900);
        window.Height = Math.Max(window.MinHeight, 620);
        Pump();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(root, "wpf-" + name + ".png")))
            encoder.Save(file);
        window.Close();
        Pump();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static System.Collections.Generic.IEnumerable<Button> Buttons(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Button button)
                yield return button;
            foreach (var nested in Buttons(child))
                yield return nested;
        }
    }
}
