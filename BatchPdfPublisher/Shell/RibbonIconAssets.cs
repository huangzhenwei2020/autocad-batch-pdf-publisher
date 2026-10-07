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
        internal const string StyleVersion = "WL_IOS_NATIVE_BADGE_8_";
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
            // Keep the native 32/16 DIP size, but retain twice as many pixels.
            // A 32px/96-DPI bitmap is enlarged by Windows at 150%-200% scaling
            // and its edges go soft. 64px/192-DPI remains 32 DIP in the Ribbon.
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var drawing = visual.RenderOpen())
                drawing.DrawImage(source, new Rect(0, 0, size, size));
            var bitmap = new RenderTargetBitmap(size * 2, size * 2, 192, 192, PixelFormats.Pbgra32);
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
                foreach (var id in new[] { "building_export", "building_place_view", "building_opening_types", "building_open_studio" })
                {
                    var icon = CreateBuildingIcon(id);
                    ScaledImages.Add(id, icon);
                    images.Add(id, Rasterize(icon, 32));
                    SmallImages.Add(id, Rasterize(icon, 16));
                }
                images.Add("building_component_library", images["building_opening_types"]);
                ScaledImages.Add("building_component_library", ScaledImages["building_opening_types"]);
                SmallImages.Add("building_component_library", SmallImages["building_opening_types"]);
                foreach (var feature in FeatureRegistry.Items)
                    if (!images.ContainsKey(feature.Id))
                        throw new InvalidDataException("功能区缺少图标：" + feature.Id);
                return images;
            }
        }

        private static BitmapSource CreateBuildingIcon(string id)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.PushTransform(new ScaleTransform(4, 4));
                var top = id == "building_export" ? Color.FromRgb(49, 191, 250)
                    : id == "building_place_view" ? Color.FromRgb(96, 155, 255)
                    : id == "building_opening_types" ? Color.FromRgb(48, 218, 191)
                    : Color.FromRgb(144, 125, 255);
                var bottom = id == "building_export" ? Color.FromRgb(0, 95, 220)
                    : id == "building_place_view" ? Color.FromRgb(35, 79, 206)
                    : id == "building_opening_types" ? Color.FromRgb(0, 141, 172)
                    : Color.FromRgb(80, 54, 188);
                drawing.DrawRoundedRectangle(new LinearGradientBrush(top, bottom, 90),
                    new Pen(new SolidColorBrush(Color.FromArgb(150, 171, 227, 255)), .55),
                    new Rect(1.5, 1.5, 29, 29), 7, 7);
                drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), null,
                    new Rect(3.5, 3.5, 25, 10), 5, 5);
                var white = new Pen(Brushes.White, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (id == "building_export")
                {
                    DrawPath(drawing, white, "M 9,7 L 19,7 23,11 23,25 9,25 Z M 19,7 L 19,11 23,11 M 12,15 L 19,15 M 12,19 L 17,19");
                    DrawPath(drawing, white, "M 17,21 L 20,24 24,20 M 20,24 L 20,18");
                }
                else if (id == "building_place_view")
                {
                    DrawPath(drawing, white, "M 7,9 L 20,9 20,23 7,23 Z M 11,13 L 16,13 M 11,17 L 16,17 M 11,21 L 16,21");
                    DrawPath(drawing, white, "M 21,13 L 25,17 21,21 M 25,17 L 17,17");
                }
                else if (id == "building_opening_types")
                {
                    DrawPath(drawing, white, "M 7,7 L 25,7 25,25 7,25 Z M 16,7 L 16,25 M 7,16 L 25,16 M 10,10 L 13,10 M 19,10 L 22,10");
                }
                else
                {
                    DrawPath(drawing, white, "M 8,12 L 16,7 24,12 24,23 16,27 8,23 Z M 8,12 L 16,16 24,12 M 16,16 L 16,27 M 11,16 L 13,17 M 19,19 L 21,18");
                }
                drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static void DrawPath(DrawingContext drawing, Pen pen, string path)
        {
            var geometry = Geometry.Parse(path);
            geometry.Freeze();
            drawing.DrawGeometry(null, pen, geometry);
        }
    }
}
