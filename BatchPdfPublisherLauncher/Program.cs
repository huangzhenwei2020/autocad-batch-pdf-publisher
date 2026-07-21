using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BatchPdfPublisherLauncher
{
    internal static class Program
    {
        private const string PluginAssemblyName = "BatchPdfPublisher.dll";
        private const string PdfDependencyName = "PdfSharp.dll";
        private const string LastPlatformFileName = "BatchPdfPublisher.last-platform.txt";
        private static readonly string LaunchLogPath = Path.Combine(Path.GetTempPath(), "BatchPdfPublisher.launcher.log");

        [STAThread]
        private static void Main()
        {
            try
            {
                Log("启动器开始运行");
                var launcherDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var sourceAssembly = Path.Combine(launcherDirectory, PluginAssemblyName);
                if (!File.Exists(sourceAssembly)) throw new FileNotFoundException("启动器旁边缺少 BatchPdfPublisher.dll，请重新编译完整项目。", sourceAssembly);
                var sourcePdfDependency = Path.Combine(launcherDirectory, PdfDependencyName);
                if (!File.Exists(sourcePdfDependency)) throw new FileNotFoundException("启动器旁边缺少 PdfSharp.dll，请重新编译完整项目。", sourcePdfDependency);

                var platforms = FindPlatforms();
                if (platforms.Count == 0) throw new FileNotFoundException("未找到 AutoCAD 2022、AutoCAD 2024 或 T20 天正建筑。请先安装兼容平台。");
                PlatformOption selected;
                using (var picker = new PlatformPicker(platforms, LoadLastPlatform()))
                {
                    if (picker.ShowDialog() != DialogResult.OK) return;
                    selected = picker.SelectedPlatform;
                }

                File.WriteAllText(LastPlatformPath(), selected.Id);
                var installedAssembly = InstallPlugin(sourceAssembly, sourcePdfDependency);
                Log("已安装插件: " + installedAssembly);
                StartPlatform(selected, installedAssembly);
            }
            catch (Exception exception)
            {
                Log("启动失败: " + exception);
                MessageBox.Show(exception.Message, "批量打印插件启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static List<PlatformOption> FindPlatforms()
        {
            var result = new List<PlatformOption>();
            AddIfExists(result, new PlatformOption("T20 天正建筑 V9（AutoCAD 2022）", "t20", @"C:\Tangent\TArchT20V9\TGStart.exe", "", @"C:\Tangent\TArchT20V9", "AutoCAD.Application.24.1"));
            // The machine may have T20 as the default AutoCAD profile. Force
            // the unnamed vanilla profile so launching plain AutoCAD cannot
            // pull the T20 ARX/LSP startup chain into the session.
            AddIfExists(result, new PlatformOption("AutoCAD 2022", "acad2022", @"C:\Program Files\Autodesk\AutoCAD 2022\acad.exe", "/nologo /p \"<<Unnamed Profile>>\"", @"C:\Program Files\Autodesk\AutoCAD 2022", "AutoCAD.Application.24.1"));
            AddIfExists(result, new PlatformOption("AutoCAD 2024", "acad2024", @"C:\Program Files\Autodesk\AutoCAD 2024\acad.exe", "/nologo /p \"<<Unnamed Profile>>\"", @"C:\Program Files\Autodesk\AutoCAD 2024", "AutoCAD.Application.24.3"));
            return result;
        }

        private static void AddIfExists(List<PlatformOption> platforms, PlatformOption option)
        {
            if (File.Exists(option.Executable)) platforms.Add(option);
        }

        private static void StartPlatform(PlatformOption platform, string installedAssembly)
        {
            Log("启动平台: " + platform.DisplayName);
            Process.Start(new ProcessStartInfo
            {
                FileName = platform.Executable,
                Arguments = platform.Arguments,
                WorkingDirectory = platform.WorkingDirectory,
                UseShellExecute = true
            });

            if (!WaitForCadProcess(45))
                throw new InvalidOperationException(platform.DisplayName + " 启动后没有检测到 AutoCAD 进程。请确认平台可以单独正常启动。\r\n\r\n插件文件已经安装，进入 CAD 后仍可手工执行 BPPUBLISH。\r\n\r\n如使用天正，请从选择列表中选择“T20 天正建筑”，不要选择普通 AutoCAD 2022。 ");

            // AutoCAD/T20 may expose COM before its command processor is ready.
            // Waiting longer and loading only the assembly avoids the fatal
            // c000041d crashes caused by sending a second command too early.
            Thread.Sleep(string.Equals(platform.Id, "t20", StringComparison.OrdinalIgnoreCase) ? 25000 : 15000);
            Log("AutoCAD 进程已就绪，开始发送加载命令");
            if (!TrySendLoad(platform.ProgId, installedAssembly))
            {
                MessageBox.Show("CAD 已启动，插件文件已经安装。请在 CAD 命令行输入 BPPUBLISH 加载并打开面板。", "批量打印插件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static bool WaitForCadProcess(int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                if (Process.GetProcessesByName("acad").Length > 0) return true;
                Thread.Sleep(1000);
            }
            return false;
        }

        private static bool TrySendLoad(string progId, string installedAssembly)
        {
            var progIds = new[] { progId, "AutoCAD.Application.24.0", "AutoCAD.Application.24.1", "AutoCAD.Application.24.2", "AutoCAD.Application.24.3", "AutoCAD.Application.25.0" }
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            for (var attempt = 0; attempt < 15; attempt++)
            {
                try
                {
                    object application = null;
                    Exception last = null;
                    foreach (var candidate in progIds)
                    {
                        try { application = Marshal.GetActiveObject(candidate); break; }
                        catch (Exception exception) { last = exception; }
                    }
                    if (application == null) throw last ?? new InvalidOperationException("AutoCAD COM 服务尚未注册");
                    var document = application.GetType().InvokeMember("ActiveDocument", BindingFlags.GetProperty, null, application, null);
                    var directory = Path.GetDirectoryName(installedAssembly).Replace('\\', '/');
                    // Queue the commands in a single command stream. AutoCAD
                    // executes NETLOAD fully before BPPUBLISH, avoiding the
                    // startup race that previously caused fatal errors while
                    // still giving the user a true double-click experience.
                    var assemblyPath = installedAssembly.Replace('\\', '/').Replace("\\\"", "\\\\\"");
                    SendCommand(document,
                        "(setvar \"TRUSTEDPATHS\" (strcat (getvar \"TRUSTEDPATHS\") \";" + directory + "\")) " +
                        "(command \"_.NETLOAD\" \"" + assemblyPath + "\") " +
                        "(command \"BPPUBLISH\") \r\n");
                    Log("已向 AutoCAD 发送 NETLOAD + BPPUBLISH");
                    return true;
                }
                catch (Exception exception) { Log("COM 尝试 " + (attempt + 1) + " 失败: " + exception.Message); Thread.Sleep(1000); }
            }
            return false;
        }

        private static void SendCommand(object document, string command)
        {
            document.GetType().InvokeMember("SendCommand", BindingFlags.InvokeMethod, null, document, new object[] { command });
        }

        private static void Log(string message)
        {
            try { File.AppendAllText(LaunchLogPath, DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } catch { }
        }

        private static string LoadLastPlatform()
        {
            var path = LastPlatformPath();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }

        private static string LastPlatformPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LastPlatformFileName);

        private static string InstallPlugin(string sourceAssembly, string sourcePdfDependency)
        {
            var contentsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatchPdfPublisher", "releases");
            Directory.CreateDirectory(contentsDirectory);
            var hash = GetFileHash(sourceAssembly).Substring(0, 12);
            var installedFileName = "BatchPdfPublisher." + hash + ".dll";
            var installedAssembly = Path.Combine(contentsDirectory, installedFileName);
            if (!File.Exists(installedAssembly) || new FileInfo(installedAssembly).Length != new FileInfo(sourceAssembly).Length)
                File.Copy(sourceAssembly, installedAssembly, true);
            InstallAutoLoadBundle(installedAssembly, sourcePdfDependency);
            return installedAssembly;
        }

        private static void InstallAutoLoadBundle(string installedAssembly, string sourcePdfDependency)
        {
            var bundle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "ApplicationPlugins", "BatchPdfPublisher.bundle");
            var contents = Path.Combine(bundle, "Contents");
            Directory.CreateDirectory(contents);
            File.Copy(installedAssembly, Path.Combine(contents, PluginAssemblyName), true);
            File.Copy(sourcePdfDependency, Path.Combine(contents, PdfDependencyName), true);
            var package = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<ApplicationPackage SchemaVersion=\"1.0\" AutodeskProduct=\"AutoCAD\" Name=\"BatchPdfPublisher\" AppVersion=\"1.0.0\" ProductCode=\"{BPP-7B9E2D72-1C3E-4F3D-9C0C-7D5D3E5A0A01}\">\r\n" +
                "  <CompanyDetails Name=\"BatchPdfPublisher\" />\r\n" +
                "  <Components>\r\n" +
                "    <RuntimeRequirements OS=\"Win64\" Platform=\"AutoCAD*\" SeriesMin=\"R24.0\" SeriesMax=\"R25.9\" />\r\n" +
                "    <ComponentEntry AppName=\"BatchPdfPublisher\" ModuleName=\"Contents\\BatchPdfPublisher.dll\" AppDescription=\"批量 PDF 发布\" LoadReasons=\"LoadOnStartup\" />\r\n" +
                "  </Components>\r\n" +
                "</ApplicationPackage>\r\n";
            File.WriteAllText(Path.Combine(bundle, "PackageContents.xml"), package, Encoding.UTF8);
        }

        private static string GetFileHash(string path)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var hash = sha256.ComputeHash(stream);
                var value = new StringBuilder(hash.Length * 2);
                foreach (var item in hash) value.Append(item.ToString("x2"));
                return value.ToString();
            }
        }

    }

    internal sealed class PlatformOption
    {
        public PlatformOption(string displayName, string id, string executable, string arguments, string workingDirectory, string progId)
        {
            DisplayName = displayName; Id = id; Executable = executable; Arguments = arguments; WorkingDirectory = workingDirectory; ProgId = progId;
        }
        public string DisplayName { get; }
        public string Id { get; }
        public string Executable { get; }
        public string Arguments { get; }
        public string WorkingDirectory { get; }
        public string ProgId { get; }
        public override string ToString() => DisplayName;
    }

    internal sealed class PlatformPicker : Form
    {
        private readonly ComboBox _platformBox;
        public PlatformOption SelectedPlatform => _platformBox.SelectedItem as PlatformOption;

        public PlatformPicker(IList<PlatformOption> platforms, string lastPlatform)
        {
            Text = "选择 CAD 平台";
            Width = 500; Height = 190; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            var label = new Label { Left = 18, Top = 20, Width = 440, Text = "请选择要启动并加载批量打印插件的平台：" };
            _platformBox = new ComboBox { Left = 18, Top = 52, Width = 440, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var platform in platforms) _platformBox.Items.Add(platform);
            var last = platforms.FirstOrDefault(x => string.Equals(x.Id, lastPlatform, StringComparison.OrdinalIgnoreCase));
            _platformBox.SelectedItem = last ?? platforms[0];
            var startButton = new Button { Left = 278, Top = 96, Width = 85, Text = "启动并加载", DialogResult = DialogResult.OK };
            var cancelButton = new Button { Left = 373, Top = 96, Width = 85, Text = "取消", DialogResult = DialogResult.Cancel };
            Controls.Add(label); Controls.Add(_platformBox); Controls.Add(startButton); Controls.Add(cancelButton);
            AcceptButton = startButton; CancelButton = cancelButton;
        }
    }
}
