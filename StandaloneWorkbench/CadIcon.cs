using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wanluo.StandaloneWorkbench
{
    public sealed class CadIcon : Image
    {
        private static readonly Dictionary<string, ImageSource> Images = new Dictionary<string, ImageSource>();
        private static readonly Dictionary<string, string> Paths = new Dictionary<string, string>
        {
            ["text"] = "M4,5 L20,5 M12,5 L12,21 M8,21 L16,21 M4,5 L4,8 M20,5 L20,8",
            ["mtext"] = "M4,4 L20,4 M4,9 L20,9 M4,14 L20,14 M4,19 L14,19",
            ["dimension"] = "M4,4 L4,21 M20,4 L20,21 M4,8 L20,8 M2,10 L6,6 M18,10 L22,6 M9,13 L15,13",
            ["angular"] = "M4,20 L20,20 M4,20 L14,4 M14,20 A10,10 0 0 0 9,11",
            ["radius"] = "M20,12 A8,8 0 1 1 12,4 M12,12 L21,3 M16,4 L21,3 L20,8",
            ["hatch"] = "M3,3 L21,3 L21,21 L3,21 Z M3,11 L11,3 M3,19 L19,3 M9,21 L21,9 M17,21 L21,17",
            ["ellipse"] = "M3,12 A9,5 0 1 0 21,12 A9,5 0 1 0 3,12",
            ["point"] = "M3,12 L9,12 M15,12 L21,12 M12,3 L12,9 M12,15 L12,21 M11,12 A1,1 0 1 0 13,12 A1,1 0 1 0 11,12",
            ["ray"] = "M3,19 L21,3 M16,3 L21,3 L21,8 M2,19 A1,1 0 1 0 4,19 A1,1 0 1 0 2,19",
            ["offset"] = "M3,18 L3,4 L17,4 M8,21 L8,9 L21,9",
            ["extend"] = "M3,20 L21,20 M12,3 L12,16 M8,12 L12,16 L16,12",
            ["chamfer"] = "M4,21 L4,9 L9,4 L21,4 M4,4 L4,7 M4,4 L7,4",
            ["break"] = "M3,20 L9,14 M15,8 L21,2 M8,8 L16,16 M10,6 L18,14",
            ["array"] = "M3,3 L9,3 L9,9 L3,9 Z M15,3 L21,3 L21,9 L15,9 Z M3,15 L9,15 L9,21 L3,21 Z M15,15 L21,15 L21,21 L15,21 Z",
            ["block"] = "M3,3 L14,3 L14,14 L3,14 Z M10,10 L21,10 L21,21 L10,21 Z",
            ["image"] = "M3,3 L21,3 L21,21 L3,21 Z M3,17 L9,11 L15,17 L18,14 L21,17 M16,7 A1,1 0 1 0 18,7 A1,1 0 1 0 16,7",
            ["table"] = "M3,3 L21,3 L21,21 L3,21 Z M3,9 L21,9 M3,15 L21,15 M9,3 L9,21 M15,3 L15,21"
        };
        public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(CadIcon), new PropertyMetadata(null, Changed));
        public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
        private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
        {
            var icon = (CadIcon)target;
            var name = args.NewValue as string;
            if (string.IsNullOrEmpty(name)) { icon.Source = null; return; }
            if (!Images.TryGetValue(name, out var image))
            {
                if (Paths.TryGetValue(name, out var path))
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromRgb(159, 211, 255)), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                    image = new DrawingImage(new GeometryDrawing(null, pen, Geometry.Parse(path)));
                }
                else image = new BitmapImage(new Uri("pack://application:,,,/Resources/Icons/" + name + ".png"));
                image.Freeze(); Images.Add(name, image);
            }
            icon.Source = image;
        }
    }

    public sealed class CadIconButton : Button
    {
        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(CadIconButton));
        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(CadIconButton), new PropertyMetadata(""));
        public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(nameof(Shortcut), typeof(string), typeof(CadIconButton), new PropertyMetadata(""));
        public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        public string Shortcut { get => (string)GetValue(ShortcutProperty); set => SetValue(ShortcutProperty, value); }
    }
}
