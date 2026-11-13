using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BatchPdfPublisher.Views
{
    public static class BppIcons
    {
        private static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>();
        public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(string), typeof(BppIcons), new PropertyMetadata(null, KindChanged));
        public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached("Source", typeof(ImageSource), typeof(BppIcons));
        public static string GetKind(DependencyObject target) => (string)target.GetValue(KindProperty);
        public static void SetKind(DependencyObject target, string value) => target.SetValue(KindProperty, value);
        public static ImageSource GetSource(DependencyObject target) => (ImageSource)target.GetValue(SourceProperty);
        private static void KindChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
        {
            var name = args.NewValue as string;
            if (string.IsNullOrEmpty(name))
                return;
            if (!Cache.TryGetValue(name, out var source))
            {
                if (!Enum.TryParse(name, out PublisherForm.UiIcon icon))
                    return;
                using (var bitmap = PublisherForm.DrawUiIcon(icon, System.Drawing.Color.FromArgb(218, 232, 243)))
                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    stream.Position = 0;
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    source = image;
                }

                Cache[name] = source;
            }

            target.SetValue(SourceProperty, source);
            if (target is MenuItem menu)
                menu.Icon = new Image
                {
                    Source = source,
                    Width = 16,
                    Height = 16
                };
        }
    }
}
