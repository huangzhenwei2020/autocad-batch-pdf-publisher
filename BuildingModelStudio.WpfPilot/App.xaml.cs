using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Wanluo.BuildingModelStudio.WpfPilot
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var window = new MainWindow();
            MainWindow = window;
            var snapshotIndex = Array.IndexOf(e.Args, "--snapshot");
            var snapshotPath = snapshotIndex >= 0 && snapshotIndex + 1 < e.Args.Length
                ? e.Args[snapshotIndex + 1] : null;
            if (e.Args.Contains("--smoke") || snapshotPath != null)
            {
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (sender, args) =>
                {
                    timer.Stop();
                    try
                    {
                        if (snapshotPath != null)
                        {
                            window.UpdateLayout();
                            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
                                96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(window);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using (var stream = File.Create(snapshotPath)) encoder.Save(stream);
                            File.WriteAllText(snapshotPath + ".txt", window.DebugInfo);
                        }
                        Environment.ExitCode = window.PreviewReady ? 0 : 1;
                    }
                    catch { Environment.ExitCode = 2; }
                    window.Close();
                };
                timer.Start();
            }
            window.Show();
        }
    }
}
