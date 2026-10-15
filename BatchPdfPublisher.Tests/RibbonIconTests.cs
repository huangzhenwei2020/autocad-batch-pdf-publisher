using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class RibbonIconTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Supply a compiled plugin DLL.");
            var assembly = Assembly.Load(File.ReadAllBytes(Path.GetFullPath(args[0])));
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var assets = assembly.GetType("BatchPdfPublisher.Services.RibbonIconAssets", true);
            var registry = assembly.GetType("BatchPdfPublisher.Services.FeatureRegistry", true);
            var features = ((IEnumerable)registry.GetProperty("Items", flags).GetValue(null)).Cast<object>().ToList();
            var ids = features.Select(x => (string)x.GetType().GetProperty("Id").GetValue(x)).ToList();
            Check(ids.Count > 0 && ids.Distinct().Count() == ids.Count, "Empty/duplicate registry");
            foreach (var name in new[] { "Images", "SmallImages", "ScaledImages" })
            {
                var map = (IDictionary)assets.GetField(name, flags).GetValue(null);
                var size = name == "Images" ? 32 : name == "SmallImages" ? 16 : 128;
                foreach (var id in ids)
                {
                    Check(map.Contains(id), "Missing " + name + " icon: " + id);
                    var image = (BitmapSource)map[id];
                    Check(image.IsFrozen && Math.Abs(image.Width-size) < .01 && Math.Abs(image.Height-size) < .01, "Invalid dimensions: " + id);
                    var presenter = new Image { Source = image, Stretch = Stretch.None, Width = size, Height = size };
                    presenter.Measure(new Size(size, size)); presenter.Arrange(new Rect(0, 0, size, size)); presenter.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(presenter);
                    var pixels = new byte[size * size * 4]; bitmap.CopyPixels(pixels, size * 4, 0);
                    var visible = 0;
                    for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] > 128) visible++;
                    Check(visible >= size * size * .25, "Blank/clipped icon: " + id);
                }
            }
            var images = (IDictionary)assets.GetField("Images", flags).GetValue(null);
            Check(ReferenceEquals(images["room_statistics"], images["cad_table_xlsx"]), "Room statistics icon mapping");
            Check(ReferenceEquals(images["room_statistics_summary"], images["catalog"]), "Room summary icon mapping");
            Console.WriteLine("RIBBON_ICONS_OK " + ids.Count + " features large small preview frozen nonblank roomMappings");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
