using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadPerspectiveViewportSelfTest
    {
        internal static async Task RunAsync(CadWorkspace workspace, string root)
        {
            var client = workspace.Client;
            var previous = client.State.ActiveId;
            var file = Path.Combine(Environment.GetEnvironmentVariable("WANLUO_NATIVE_FIXTURE_ROOT"), "perspective-projection.dwg");
            ulong document = 0;
            try
            {
                foreach (var suffix in new[] { "源图", "DWG", "DXF" })
                {
                    await client.OpenAsync(file);
                    document = client.State.ActiveId;
                    CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == "Layout1").Id));
                    var viewport = CadNative.PaperViewports(client.Handle).Single(item => item.Perspective);
                    if (viewport.CanInlineEdit || viewport.CanMaximizeEdit) throw new InvalidOperationException("Perspective viewport offered orthographic editing.");
                    client.Command("@fit");
                    await Task.Delay(100);
                    var image = Path.Combine(root, $"透视视口-{suffix}.png");
                    client.Capture(image);
                    var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(image), BitmapCreateOptions.IgnoreImageCache,
                        BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
                    bool ColorAt(double x, double y, Func<byte, byte, byte, bool> matches)
                    {
                        CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var px, out var py));
                        var area = new Int32Rect(px - 3, py - 3, 7, 7);
                        var pixels = new byte[7 * 7 * 4];
                        bitmap.CopyPixels(area, pixels, 7 * 4, 0);
                        for (var i = 0; i < pixels.Length; i += 4)
                            if (matches(pixels[i + 2], pixels[i + 1], pixels[i])) return true;
                        return false;
                    }
                    var focal = Math.Sqrt(300 * 300 + 200 * 200);
                    bool red = ColorAt(200, 100 + 50 * focal / 700, (r,g,b) => r > 160 && g < 100 && b < 100);
                    bool green = ColorAt(180, 100 - 80 * focal / 1200, (r,g,b) => g > 160 && r < 100 && b < 100);
                    bool beyondFarEnd = ColorAt(200, 100 - 80 * focal / 1200, (r,g,b) => g > 160 && r < 100 && b < 100);
                    bool depthClipped = !ColorAt(150, 100, (r,g,b) => b > 150 || (r > 150 && g > 150));
                    if (!red || !green || beyondFarEnd || !depthClipped)
                        throw new InvalidOperationException($"Perspective projection pixel check failed ({suffix}, near={red}, far={green}, overflow={beyondFarEnd}, clipped={depthClipped}).");

                    if (suffix == "源图")
                    {
                        // Change one viewport property to exercise the native serializer, not just unmodified-entity passthrough.
                        var parameters = new CadViewportParameters
                        {
                            X = viewport.X, Y = viewport.Y, Width = viewport.Width, Height = viewport.Height,
                            ModelX = viewport.ModelX, ModelY = viewport.ModelY, Ratio = viewport.Ratio,
                            Twist = viewport.Twist, Locked = 0
                        };
                        CadNative.Check(CadNative.wl_viewport_apply(client.Handle, document, viewport.Id, ref parameters,
                            viewport.Frozen.ToArray(), viewport.Frozen.Count));
                        await client.SaveAsync(Path.Combine(root, "透视视口回读.dwg"));
                        await client.SaveAsync(Path.Combine(root, "透视视口回读.dxf"));
                    }
                    file = Path.Combine(root, suffix == "源图" ? "透视视口回读.dwg" : "透视视口回读.dxf");
                    client.Close(document);
                    document = 0;
                }
            }
            finally
            {
                if (document != 0) client.Close(document);
                client.Activate(previous);
                workspace.RefreshState();
            }
        }
    }
}
