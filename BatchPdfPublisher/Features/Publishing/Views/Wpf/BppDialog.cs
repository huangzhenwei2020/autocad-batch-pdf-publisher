using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BatchPdfPublisher.ViewModels;
using BatchPdfPublisher.Services;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPdfPublisher.Views
{
    internal static class BppDialog
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
        internal static void ConfigureWindow(Window window)
        {
            EventHandler initialized = null;
            initialized = (sender, args) =>
            {
                window.SourceInitialized -= initialized;
                var enabled = 1;
                var handle = new WindowInteropHelper(window).Handle;
                if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            };
            window.SourceInitialized += initialized;
        }

        internal static void OpenOutputDirectory(PublisherViewModel viewModel, Window owner)
        {
            var path = viewModel.OutputDirectory;
            if (viewModel.OutputNextToCadFile)
            {
                var file = viewModel.CadFiles.FirstOrDefault(item => item.IsSelected) ?? viewModel.CadFiles.FirstOrDefault();
                path = file == null ? null : System.IO.Path.GetDirectoryName(file.Path);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(owner, "请先选择输出目录或扫描 CAD 文件。", "批量 PDF 发布");
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(owner, error.Message, "打开输出目录");
            }
        }

        internal static Window Create(string title, double width, double height)
        {
            var window = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                MinWidth = Math.Min(640, width),
                MinHeight = Math.Min(450, height),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BatchPdfPublisher;component/Features/Publishing/Views/Wpf/BppTheme.xaml", UriKind.Relative) });
            return window;
        }

        internal static bool? Show(Window window, Window owner)
        {
            ConfigureWindow(window);
            window.Width = Math.Min(window.Width, SystemParameters.WorkArea.Width);
            window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height);
            if (owner != null && owner.IsVisible)
            {
                window.Owner = owner;
                return window.ShowDialog();
            }

            return AcApplication.ShowModalWindow(window);
        }

        internal static string ChooseFolder(Window owner, string initial)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                SelectedPath = initial ?? string.Empty
            }

            )
                return dialog.ShowDialog(new NativeOwner(new WindowInteropHelper(owner).Handle)) == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
        }

        private sealed class NativeOwner : System.Windows.Forms.IWin32Window
        {
            internal NativeOwner(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }

        internal static void ShowProjects(PublisherViewModel vm, Window owner)
        {
            Show(new BppProjectWindow(vm), owner);
        }

        internal static async void ShowFrames(PublisherViewModel vm, Window owner)
        {
            var dialog = new BppFrameManagerWindow(vm);
            Show(dialog, owner);
            if (dialog.RequestedAction == BppFrameManagerWindow.CadAction.Edit)
            {
                var restoreOwner = owner?.IsVisible == true;
                if (restoreOwner)
                    owner.Hide();
                try
                {
                    await CadCommandContext.ExecuteAsync(() => vm.EditFrameCommand.Execute(null));
                }
                catch (Exception error)
                {
                    MessageBox.Show(error.Message, "修改图框登记", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    if (restoreOwner)
                    {
                        owner.Show();
                        owner.Activate();
                    }
                }
            }

            if (dialog.RequestedAction == BppFrameManagerWindow.CadAction.Pick)
                vm.RegisterFrameCommand.Execute(null);
            else if (dialog.RequestedAction == BppFrameManagerWindow.CadAction.Create)
                ShowFrameCreation(vm, owner);
        }

        internal static void ShowFrameCreation(PublisherViewModel vm, Window owner)
        {
            var document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
                ShowModeless(new BppFrameCreationWindow(document, () =>
                {
                }), owner);
        }

        internal static void ShowSelectionPublisher(PublisherViewModel vm, Window owner)
        {
            var document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
                ShowModeless(new BppSelectionPublishWindow(document, vm), owner);
        }

        internal static void ShowCatalog(PublisherViewModel vm, Window owner, Action refresh)
        {
            var document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;
            if (vm.Sheets.Count == 0)
            {
                MessageBox.Show("请先扫描项目图纸。", "插入目录");
                return;
            }

            ShowModeless(new BppCatalogWindow(document, new System.Collections.Generic.List<Models.SheetItem>(vm.Sheets), refresh), owner);
        }

        private static void ShowModeless(Window window, Window owner)
        {
            if (owner?.IsVisible == true)
                window.Owner = owner;
            AcApplication.ShowModelessWindow(window);
        }
    }
}
