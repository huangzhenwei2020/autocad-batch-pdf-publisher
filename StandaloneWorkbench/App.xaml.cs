using System;
using System.IO;
using System.Linq;
using System.Windows;
using BatchPdfPublisher.Services;

namespace Wanluo.StandaloneWorkbench
{
    public partial class App : System.Windows.Application
    {
        internal static bool IsSelfTest { get; private set; }
        protected override void OnStartup(StartupEventArgs e)
        {
            IsSelfTest = e.Args.Contains("--self-test");
            base.OnStartup(e);
            DispatcherUnhandledException += (sender, args) =>
            {
                if (e.Args.Contains("--self-test"))
                {
                    File.WriteAllText(Path.Combine(UserDataPaths.RootDirectory, "selftest-result.txt"), args.Exception.ToString());
                    Shutdown(1);
                }
                else MessageBox.Show(args.Exception.Message, "万落独立工作台", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            try
            {
                var package = Value(e.Args, "--package-root") ?? Environment.GetEnvironmentVariable(UserDataPaths.PortableRootEnvironmentVariable)
                    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
                WorkbenchRuntime.PackageRoot = Path.GetFullPath(package);
                Environment.SetEnvironmentVariable(UserDataPaths.PortableRootEnvironmentVariable, WorkbenchRuntime.PackageRoot);
                if (e.Args.Contains("--self-test"))
                {
                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UserDataPaths.UserDataRootEnvironmentVariable)))
                        throw new InvalidOperationException("自测需要独立用户配置目录。");
                    ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    SelfTest.RunAsync(Value(e.Args, "--drawing")).ContinueWith(task => Dispatcher.Invoke(() =>
                    {
                        var result = task.IsFaulted ? task.Exception.ToString() : "STANDALONE_WORKBENCH_OK";
                        File.WriteAllText(Path.Combine(UserDataPaths.RootDirectory, "selftest-result.txt"), result);
                        Shutdown(task.IsFaulted ? 1 : 0);
                    }));
                    return;
                }
                new MainWindow(Value(e.Args, "--project")).Show();
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "启动独立工作台", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        private static string Value(string[] arguments, string name)
        {
            var index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
    }
}
