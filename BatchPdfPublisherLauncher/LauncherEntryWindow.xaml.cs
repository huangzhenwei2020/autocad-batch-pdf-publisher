using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisherLauncher
{
    internal enum LauncherEntryMode { Standalone, Cad }

    internal partial class LauncherEntryWindow : Window
    {
        private readonly PublishPlanStore _store = new PublishPlanStore();
        private bool _refreshing;
        public LauncherEntryMode Mode { get; private set; }
        public ProjectProfile Project => Projects.SelectedItem as ProjectProfile;
        private static string ModePath => UserDataPaths.SettingsFile("启动入口.txt");

        public LauncherEntryWindow()
        {
            InitializeComponent();
            Version.Text = WanluoArchitectureTools.ProductVersion.Display;
            ReloadProjects();
            Loaded += (sender, args) =>
            {
                MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
                MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
                Width = Math.Min(Width, SystemParameters.WorkArea.Width);
                Height = Math.Min(Height, SystemParameters.WorkArea.Height);
                try
                {
                    if (File.Exists(ModePath) && File.ReadAllText(ModePath).Trim() == "cad") CadButton.Focus();
                    else StandaloneButton.Focus();
                }
                catch (IOException) { StandaloneButton.Focus(); }
            };
        }

        private void ReloadProjects()
        {
            _refreshing = true;
            var projects = _store.LoadProjects(false);
            Projects.ItemsSource = projects;
            RecentProjects.ItemsSource = projects;
            Projects.SelectedItem = projects.FirstOrDefault(project => project.Name == _store.LoadActiveProjectName()) ?? projects.FirstOrDefault();
            RecentProjects.SelectedItem = Projects.SelectedItem;
            _refreshing = false;
        }

        private void Projects_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_refreshing) RecentProjects.SelectedItem = Projects.SelectedItem;
        }

        private void Recent_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_refreshing && RecentProjects.SelectedItem != null) Projects.SelectedItem = RecentProjects.SelectedItem;
        }

        private void Standalone_Click(object sender, RoutedEventArgs e) => Choose(LauncherEntryMode.Standalone);
        private void Cad_Click(object sender, RoutedEventArgs e) => Choose(LauncherEntryMode.Cad);

        private void Choose(LauncherEntryMode mode)
        {
            try
            {
                if (Project != null) _store.SetActiveProject(Project.Name);
                File.WriteAllText(ModePath, mode == LauncherEntryMode.Cad ? "cad" : "standalone");
                Mode = mode;
                DialogResult = true;
            }
            catch (Exception error) { Status.Text = error.Message; }
        }

        private void ProjectManager_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new ProjectManagerForm()) dialog.ShowDialog(new NativeOwner(new WindowInteropHelper(this).Handle));
            ReloadProjects();
        }

        private sealed class NativeOwner : System.Windows.Forms.IWin32Window
        {
            public NativeOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }

        public static void Smoke(string outputFolder)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UserDataPaths.UserDataRootEnvironmentVariable)))
                throw new InvalidOperationException("入口自测需要独立配置目录。");
            Directory.CreateDirectory(outputFolder);
            new CloudSyncSettingsStore().SaveSettings(new CloudSyncSettings { Enabled = false });
            var store = new PublishPlanStore();
            store.SaveProjects(new System.Collections.Generic.List<ProjectProfile> { new ProjectProfile { Name = "独立版入口测试", ProjectFolder = Path.Combine(outputFolder, "中文项目") } });
            foreach (var mode in new[] { LauncherEntryMode.Standalone, LauncherEntryMode.Cad })
            {
                var window = new LauncherEntryWindow();
                window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    window.UpdateLayout();
                    var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var file = File.Create(Path.Combine(outputFolder, "launcher-" + mode + ".png"))) encoder.Save(file);
                    window.Choose(mode);
                }));
                if (window.ShowDialog() != true || window.Mode != mode) throw new InvalidOperationException("启动入口选择失败。");
            }
            File.WriteAllText(Path.Combine(outputFolder, "launcher-result.txt"), "LAUNCHER_ENTRY_OK");
        }
    }
}
