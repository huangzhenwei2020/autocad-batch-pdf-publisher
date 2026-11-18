using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadMediaClippingSelfTest
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

        internal static async Task RunAsync(CadWorkspace workspace, string root, Action<Window, string> capture)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            var fixtures = Environment.GetEnvironmentVariable("WANLUO_NATIVE_FIXTURE_ROOT");
            var pixels = new byte[16 * 16 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            { pixels[i] = 30; pixels[i + 1] = 200; pixels[i + 2] = 40; pixels[i + 3] = 255; }
            var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(fixtures, "detailed-checker.png"))) encoder.Save(output);

            BitmapSource Canvas(string name)
            {
                SendMessage(((CadViewport)workspace.FindName("Viewport")).Handle, 0x02A3, IntPtr.Zero, IntPtr.Zero);
                var path = Path.Combine(root, name + ".png");
                client.Capture(path);
                return new FormatConvertedBitmap(BitmapFrame.Create(new Uri(path), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            }
            byte[] Pixel(BitmapSource image, double x, double y)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var px, out var py));
                var color = new byte[4];
                image.CopyPixels(new Int32Rect(px, py, 1, 1), color, 4, 0);
                return color;
            }
            void Background(BitmapSource image, double x, double y)
            {
                var color = Pixel(image, x, y);
                Check(Math.Abs(color[0] - 47) < 6 && Math.Abs(color[1] - 40) < 6 && Math.Abs(color[2] - 33) < 6,
                    "Media leaked beyond viewport at " + x + "," + y);
            }
            void Green(BitmapSource image, double x, double y)
            {
                var color = Pixel(image, x, y);
                Check(color[1] > 110 && color[1] > color[0] + 35 && color[1] > color[2] + 35,
                    "Visible image fragment disappeared at " + x + "," + y);
            }
            int Bright(BitmapSource image, double x0, double y0, double x1, double y1)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x0, y0, out var ax, out var ay));
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x1, y1, out var bx, out var by));
                var bounds = new Int32Rect(Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(bx - ax), Math.Abs(by - ay));
                var data = new byte[bounds.Width * bounds.Height * 4];
                image.CopyPixels(bounds, data, bounds.Width * 4, 0);
                var bright = 0;
                for (var i = 0; i < data.Length; i += 4)
                    if (data[i] > 190 && data[i + 1] > 190 && data[i + 2] > 190) ++bright;
                return bright;
            }
            foreach (var name in new[] { "viewport-media", "viewport-media-outside", "viewport-media-text" })
            {
                await client.OpenAsync(Path.Combine(fixtures, name + ".dwg"));
                var document = client.State.ActiveId;
                CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == "媒体裁剪").Id));
                File.AppendAllText(Path.Combine(root, "media-state.txt"), name + " " + System.Text.Json.JsonSerializer.Serialize(client.State) + "\n" + System.Text.Json.JsonSerializer.Serialize(CadNative.PaperViewports(client.Handle)) + "\n");
                client.Command("@fit");
                await Task.Delay(120);
                var image = Canvas(name);
                Background(image, 50, 150);
                Background(image, 320, 150);
                if (name == "viewport-media") { Green(image, 250, 150); Green(image, 50, 20); }
                if (name == "viewport-media-outside") Green(image, 160, 150);
                if (name == "viewport-media-text")
                {
                    Check(Bright(image, 120, 136, 148, 193) == 0, "Boundary glyph leaked into the concave notch");
                    Check(Bright(image, 152, 136, 290, 193) > 15, "Clipping discarded the visible text fragment");
                }
                capture(Window.GetWindow(workspace), Path.Combine(root, name + "-window.png"));
                foreach (var extension in new[] { ".dwg", ".dxf" })
                {
                    await client.SaveAsync(Path.Combine(root, name + "-saved" + extension));
                    client.Close(document);
                    await client.OpenAsync(Path.Combine(root, name + "-saved" + extension));
                    document = client.State.ActiveId;
                    CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == "媒体裁剪").Id));
                    client.Command("@fit");
                    await Task.Delay(80);
                    image = Canvas(name + "-reopened" + extension.Replace(".", "-"));
                    Background(image, 50, 150);
                    if (name == "viewport-media-outside") Green(image, 160, 150);
                    if (name == "viewport-media-text") Check(Bright(image, 152, 136, 290, 193) > 15, "Reopened text lost its visible fragment");
                }
                if (name != "viewport-media-outside")
                {
                    var viewport = CadNative.PaperViewports(client.Handle).Single();
                    var parameters = new CadViewportParameters { X = viewport.X, Y = viewport.Y, Width = viewport.Width, Height = viewport.Height,
                        ModelX = viewport.ModelX, ModelY = viewport.ModelY, Ratio = viewport.Ratio, Twist = 30 };
                    CadNative.Check(CadNative.wl_viewport_apply(client.Handle, document, viewport.Id, ref parameters, Array.Empty<uint>(), 0));
                    await Task.Delay(80);
                    image = Canvas(name + "-rotated");
                    Background(image, 50, 150);
                    if (name == "viewport-media-text") Check(Bright(image, 120, 120, 148, 193) == 0, "Rotated text leaked into the notch");
                    else Green(image, 250, 150);
                    client.Command("@undo"); client.Command("@redo");
                }
                client.Close(document);
            }
            client.Activate(original);
            workspace.RefreshState();
            File.WriteAllText(Path.Combine(root, "cad-media-clipping-result.txt"), "CAD_MEDIA_CLIPPING_OK image glyph pixels concave rotation save reopen undo redo");
        }
        private static void Check(bool result, string message)
        { if (!result) throw new InvalidOperationException(message); }
    }
}
