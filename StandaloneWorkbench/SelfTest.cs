using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace Wanluo.StandaloneWorkbench
{
    internal static class SelfTest
    {
        public static async Task RunAsync(string drawingPath)
        {
            var root = UserDataPaths.RootDirectory;
            Directory.CreateDirectory(root);
            await CadStartupSelfTest.BeforeWindowAsync(root);
            new CloudSyncSettingsStore().SaveSettings(new CloudSyncSettings { Enabled = false });
            var store = new PublishPlanStore();
            store.SaveProjects(new List<ProjectProfile> { new ProjectProfile { Name = "自测初始项目", ProjectFolder = Path.Combine(root, "initial") } });
            var session = new WorkbenchSession();
            var projectFolder = Path.Combine(root, "中文项目 与 空格");
            var project = session.CreateProject("独立版试用项目", projectFolder);
            var identity = project.CloudId;
            var modelPath = session.NewModel("示例两层模型", false);
            session.Model.Storeys.Add(new StoreyModel { Id = "2F", Name = "二层", Elevation = 3300, Height = 3300 });
            foreach (var floor in session.Model.Storeys)
            {
                var points = new[] { (0d, 0d), (6000d, 0d), (6000d, 4000d), (0d, 4000d) };
                for (var index = 0; index < points.Length; index++)
                {
                    var start = points[index];
                    var end = points[(index + 1) % points.Length];
                    session.Model.Walls.Add(new WallModel { Id = Guid.NewGuid().ToString("N"), StoreyId = floor.Id, X1 = start.Item1, Y1 = start.Item2, X2 = end.Item1, Y2 = end.Item2, Thickness = 200 });
                }
            }
            session.SaveModel();
            var wallIds = session.Model.Walls.Select(wall => wall.Id).ToArray();
            session.OpenModel(modelPath);
            Check(session.Model.Storeys.Count == 2 && session.Model.Walls.Select(wall => wall.Id).SequenceEqual(wallIds), "Model content or identity changed on reopening");
            var external = BuildingModelJson.LoadModel(modelPath);
            external.Storeys[0].Height = 3700;
            BuildingModelJson.SaveModel(modelPath, external);
            var conflict = false;
            try { session.SaveModel(); }
            catch (IOException) { conflict = true; }
            Check(conflict && BuildingModelJson.LoadModel(modelPath).Storeys[0].Height == 3700, "External model edit was overwritten");
            session.OpenModel(modelPath);
            session.SaveCopy("示例副本");
            Check(File.Exists(modelPath) && File.Exists(session.ModelPath), "Save copy lost a model");
            var duplicate = false;
            try { session.NewModel("示例两层模型", false); }
            catch (IOException) { duplicate = true; }
            Check(duplicate, "Unconfirmed duplicate model was replaced");
            session.AddDrawing(drawingPath);
            Check(store.LoadProjects(false).First(item => item.Name == project.Name).CloudId == identity, "Project identity changed");

            var window = new MainWindow(projectFolder);
            window.Show();
            await Task.Delay(150);
            await CadStartupSelfTest.InWindowAsync(window, root, SaveImage);
            var models = (ListBox)window.FindName("Models");
            Check(models.Items.Count == 2, "Existing model catalog was not reused");
            models.SelectedItem = models.Items.Cast<BuildingModelCatalogEntry>().First(item => item.ModelPath == modelPath);
            Check(((DataGrid)window.FindName("Floors")).Items.Count == 2, "Model opening through the browser failed");
            await Task.Delay(200);
            SaveImage(window, Path.Combine(root, "models.png"));
            await StudioHostSelfTest.RunAsync(window, root, SaveImage);
            await window.OpenDrawingAsync(drawingPath);
            var cad = (CadWorkspace)window.FindName("CadEditor");
            Check(cad.Client.State.EntityCount > 0, "Native drawing document is empty");
            await CadSelfTest.RunAsync(cad, root);
            await CadInteractionSelfTest.RunAsync(cad, root);
            await CadCursorSelfTest.RunAsync(cad, root);
            await CadExtendedSelfTest.RunAsync(cad, root, SaveImage);
            await window.OpenDrawingAsync(drawingPath);
            await CadUiSelfTest.RunAsync(window, cad, root, SaveImage);
            await Task.Delay(150);
            SaveImage(window, Path.Combine(root, "drawing.png"));
            window.Width = 1000;
            window.Height = 700;
            await Task.Delay(100);
            SaveImage(window, Path.Combine(root, "compact.png"));
            var content = (FrameworkElement)window.Content;
            foreach (var scale in new[] { 1.25, 1.5 })
            {
                window.Width = Math.Max(1440, 980 * scale);
                window.Height = Math.Max(900, 700 * scale);
                content.LayoutTransform = new ScaleTransform(scale, scale);
                window.UpdateLayout();
                await Task.Delay(150);
                Check(string.IsNullOrEmpty(cad.Client.State.Error), "Scaled viewport reported a rendering error");
                var viewport = (CadViewport)cad.FindName("Viewport");
                Check(viewport.ActualWidth > 200 && viewport.ActualHeight > 100, "Scaled drawing area collapsed");
                SaveImage(window, Path.Combine(root, "scale-" + (int)(scale * 100) + ".png"));
            }
            content.LayoutTransform = Transform.Identity;
            var invalid = Path.Combine(root, "invalid.dwg");
            File.WriteAllText(invalid, "invalid drawing");
            var rejected = false;
            try { await DrawingSnapshot.ReadAsync(invalid, CancellationToken.None); }
            catch (IOException) { rejected = true; }
            Check(rejected, "Damaged drawing did not return an error");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name.StartsWith("Autodesk", StringComparison.OrdinalIgnoreCase) || assembly.GetName().Name == "AcMgd" || assembly.GetName().Name == "AcDbMgd"), "Independent mode loaded a CAD assembly");
            foreach (var document in cad.Client.State.Documents) cad.Client.Close(document.Id);
            window.Close();

            var editorStart = WorkbenchRuntime.EditorStart(modelPath);
            Check(editorStart.ArgumentList.Contains(modelPath), "Model handoff arguments are invalid");
            var snapshot = Path.Combine(root, "three-dimensional.png");
            editorStart.ArgumentList.Add("--snapshot");
            editorStart.ArgumentList.Add(snapshot);
            using (var editor = Process.Start(editorStart) ?? throw new IOException("Three-dimensional editor did not start"))
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                {
                    try { await editor.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException)
                    {
                        if (!editor.HasExited) editor.Kill(true);
                        throw;
                    }
                }
                Check(editor.ExitCode == 0 && File.Exists(snapshot), "Three-dimensional handoff did not complete");
            }
        }

        private static void SaveImage(Window window, string path)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var cad = (CadWorkspace)window.FindName("CadEditor");
            var viewport = (CadViewport)cad?.FindName("Viewport");
            if (viewport?.IsVisible == true && viewport.Handle != IntPtr.Zero)
            {
                var nativePath = Path.Combine(Path.GetDirectoryName(path), "viewport-" + Path.GetFileName(path));
                cad.Client.Capture(nativePath);
                var native = new BitmapImage();
                native.BeginInit(); native.CacheOption = BitmapCacheOption.OnLoad; native.UriSource = new Uri(nativePath); native.EndInit(); native.Freeze();
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawImage(bitmap, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
                    var bounds = viewport.TransformToAncestor(window).TransformBounds(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
                    context.DrawImage(native, bounds);
                }
                var composite = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                composite.Render(visual); bitmap = composite;
            }
            var studio = (StudioHost)window.FindName("StudioEditor");
            var studioImage = Path.Combine(Path.GetDirectoryName(path), "studio-embedded.png");
            if (studio?.IsVisible == true && studio.ChildWindow != IntPtr.Zero && File.Exists(studioImage))
            {
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(studioImage); image.EndInit();
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawImage(bitmap, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
                    context.DrawImage(image, studio.TransformToAncestor(window).TransformBounds(new Rect(studio.RenderSize)));
                }
                var composite = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                composite.Render(visual); bitmap = composite;
            }
            var encoder = new PngBitmapEncoder();
            var content = (FrameworkElement)window.Content;
            var clientBounds = content.TransformToAncestor(window).TransformBounds(new Rect(content.RenderSize));
            var cropped = new CroppedBitmap(bitmap, new Int32Rect(0, 0, Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(clientBounds.Right)), Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(clientBounds.Bottom))));
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
