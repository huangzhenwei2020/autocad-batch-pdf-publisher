using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BatchPdfPublisher.Services
{
    /// <summary>The approved iOS artwork is embedded in both CAD runtime builds.</summary>
    internal static class RibbonIconAssets
    {
        internal const string StyleVersion = "WL_IOS_BADGE_3_";
        private static readonly string[] FeatureIds =
        {
            "publisher", "frame", "catalog", "architecture_spec", "stair_detail", "door_window",
            "detail_layout", "line_vision", "drafting_standard", "layer_assignment", "drawing_scale", "cloud_sync",
            "attribute_batch", "attribute_definition", "cad_table_xlsx", "room_rename", "shortcut_settings", "menubar"
        };
        private static readonly Dictionary<string, BitmapSource> SmallImages = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);
        private static readonly Dictionary<string, BitmapSource> ScaledImages = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);
        private static readonly Dictionary<string, BitmapSource> Images = Load();
        // The exported atlas includes outer margins. These measured sprite bounds
        // keep the original artwork centered without including adjacent glow.

        internal static BitmapSource ForFeature(string featureId)
        {
            BitmapSource image;
            return Images.TryGetValue(featureId ?? string.Empty, out image) ? image : Images["shortcut_settings"];
        }

        internal static BitmapSource ForScaledFeature(string id) { BitmapSource image; return ScaledImages.TryGetValue(id ?? string.Empty, out image) ? image : ScaledImages["shortcut_settings"]; }

        internal static BitmapSource SmallForFeature(string featureId)
        {
            BitmapSource image;
            return SmallImages.TryGetValue(featureId ?? string.Empty, out image) ? image : SmallImages["shortcut_settings"];
        }

        private static BitmapSource Rasterize(BitmapSource source, int size)
        {
            // AutoCAD's Ribbon image presenter can use Stretch=None. Passing the
            // 258px crop clips it to a corner instead of scaling the whole icon.
            // Materialize independent 96-DPI bitmaps at the actual native sizes.
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var drawing = visual.RenderOpen())
                drawing.DrawImage(source, new Rect(0, 0, size, size));
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static Dictionary<string, BitmapSource> Load()
        {
            using (var stream = typeof(RibbonIconAssets).Assembly.GetManifestResourceStream("Wanluo.Ribbon.IosIcons.png"))
            {
                if (stream == null) throw new InvalidDataException("缺少功能区图标资源。");
                var atlas = new BitmapImage();
                atlas.BeginInit();
                atlas.CacheOption = BitmapCacheOption.OnLoad;
                atlas.StreamSource = stream;
                atlas.EndInit();
                atlas.Freeze();
                if (atlas.PixelWidth != 1774 || atlas.PixelHeight != 887)
                    throw new InvalidDataException("功能区图标图集尺寸与切片定义不一致。");
                var columns = new[] { 40, 327, 615, 900, 1189, 1476 };
                var rows = new[] { 47, 314, 583 };
                var images = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);
                for (var index = 0; index < FeatureIds.Length; index++)
                {
                    var column = index % 6;
                    var row = index / 6;
                    var image = new CroppedBitmap(atlas, new Int32Rect(columns[column], rows[row], 258, 258));
                    image.Freeze();
                    ScaledImages.Add(FeatureIds[index], Rasterize(image, 128));
                    images.Add(FeatureIds[index], Rasterize(image, 32));
                    SmallImages.Add(FeatureIds[index], Rasterize(image, 16));
                }
                return images;
            }
        }
    }
}
