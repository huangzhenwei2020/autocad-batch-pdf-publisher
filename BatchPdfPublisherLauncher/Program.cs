using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace BatchPdfPublisherLauncher
{
    internal static class Program
    {
        private const string PluginAssemblyName = "BatchPdfPublisher.dll";
        private const string PdfDependencyName = "PdfSharp.dll";
        private const string PlotterConfigName = "BatchPdfPublisher.pc3";
        private const string PlotterMediaName = "BatchPdfPublisher.pmp";
        private const string LastPlatformFileName = "BatchPdfPublisher.last-platform.txt";
        private static readonly string LaunchLogPath = Path.Combine(Path.GetTempPath(), "BatchPdfPublisher.launcher.log");

        [STAThread]
        private static void Main()
        {
            try
            {
                Log("启动器开始运行");
                var launcherDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var platforms = FindPlatforms();
                LauncherOptions options;
                using (var picker = new PlatformPicker(platforms, LoadLastPlatform(), HasRunningCad()))
                {
                    if (picker.ShowDialog() != DialogResult.OK) return;
                    if (picker.UninstallRequested)
                    {
                        UninstallPlugin();
                        MessageBox.Show("批量打印插件及永久自动加载配置已卸载。工程文件和 CAD 图纸不会删除。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    if (picker.Options.Platform == null) throw new FileNotFoundException("未找到可用的 AutoCAD 2021-2024。AutoCAD 2025/2026 使用 .NET 8，需使用对应版本插件。 ");
                    options = picker.Options;
                }
                var sourceAssembly = Path.Combine(launcherDirectory, PluginAssemblyName);
                if (!File.Exists(sourceAssembly)) throw new FileNotFoundException("启动器旁边缺少 BatchPdfPublisher.dll，请重新编译完整项目。", sourceAssembly);
                var sourcePdfDependency = Path.Combine(launcherDirectory, PdfDependencyName);
                if (!File.Exists(sourcePdfDependency)) throw new FileNotFoundException("启动器旁边缺少 PdfSharp.dll，请重新编译完整项目。", sourcePdfDependency);
                var sourcePlotterConfig = Path.Combine(launcherDirectory, PlotterConfigName);
                var sourcePlotterMedia = Path.Combine(launcherDirectory, PlotterMediaName);
                if (!File.Exists(sourcePlotterConfig) || !File.Exists(sourcePlotterMedia))
                    throw new FileNotFoundException("启动器旁边缺少 BatchPdfPublisher.pc3/pmp 毫米纸张库，请使用完整发布包。");

                File.WriteAllText(LastPlatformPath(), options.Platform.Id);
                InstallPlotterProfiles(sourcePlotterConfig, sourcePlotterMedia);
                var installedAssembly = InstallPlugin(sourceAssembly, sourcePdfDependency, options.InstallPermanently);
                Log("已安装插件: " + installedAssembly);
                if (options.LoadIntoRunningCad)
                {
                    if (!TrySendLoad(options.Platform.ProgId, installedAssembly))
                        throw new InvalidOperationException("没有连接到已启动的 CAD。请确认目标 CAD 已完全打开，或取消“加载到已启动 CAD”后重新运行启动器。");
                }
                else StartPlatform(options.Platform, installedAssembly);
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
            AddIfExists(result, new PlatformOption("T20 天正建筑 V9（AutoCAD 2022）", "t20v9", @"C:\Tangent\TArchT20V9\TGStart.exe", "", @"C:\Tangent\TArchT20V9", "AutoCAD.Application.24.1"));
            AddIfExists(result, new PlatformOption("T20 天正建筑 V9（AutoCAD 2024）", "t20v9-2024", @"C:\Tangent\TArchT20V9\TGStart2024.exe", "", @"C:\Tangent\TArchT20V9", "AutoCAD.Application.24.3"));
            // The machine may have T20 as the default AutoCAD profile. Force
            // the unnamed vanilla profile so launching plain AutoCAD cannot
            // pull the T20 ARX/LSP startup chain into the session.
            var versions = new[] { new { Year = 2021, Release = "24.0" }, new { Year = 2022, Release = "24.1" }, new { Year = 2023, Release = "24.2" }, new { Year = 2024, Release = "24.3" } };
            foreach (var version in versions)
            {
                var root = FindAcadInstallDirectory(version.Release) ?? (@"C:\Program Files\Autodesk\AutoCAD " + version.Year);
                AddIfExists(result, new PlatformOption("AutoCAD " + version.Year + "（R" + version.Release + "）", "acad" + version.Year, Path.Combine(root, "acad.exe"), "/nologo /p \"<<Unnamed Profile>>\"", root, "AutoCAD.Application." + version.Release));
            }
            return result;
        }

        private static string FindAcadInstallDirectory(string release)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var releaseKey = baseKey.OpenSubKey("SOFTWARE\\Autodesk\\AutoCAD\\R" + release))
                    {
                        if (releaseKey == null) continue;
                        foreach (var product in releaseKey.GetSubKeyNames())
                        using (var install = releaseKey.OpenSubKey(product + "\\Install"))
                        {
                            var value = install?.GetValue("INSTALLDIR") as string;
                            if (!string.IsNullOrWhiteSpace(value) && File.Exists(Path.Combine(value, "acad.exe"))) return value;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static void AddIfExists(List<PlatformOption> platforms, PlatformOption option)
        {
            if (File.Exists(option.Executable)) platforms.Add(option);
        }

        private static void StartPlatform(PlatformOption platform, string installedAssembly)
        {
            Log("启动平台: " + platform.DisplayName);
            var startupScript = Path.Combine(Path.GetTempPath(), "BatchPdfPublisher." + Guid.NewGuid().ToString("N") + ".scr");
            var trustedDirectory = Path.GetDirectoryName(installedAssembly).Replace('\\', '/');
            var assemblyPath = installedAssembly.Replace('\\', '/');
            File.WriteAllText(startupScript,
                "(setvar \"TRUSTEDPATHS\" (strcat (getvar \"TRUSTEDPATHS\") \";" + trustedDirectory + "\"))\r\n" +
                "_.NETLOAD\r\n\"" + assemblyPath + "\"\r\nBPPUBLISH065\r\n", Encoding.Default);
            Process.Start(new ProcessStartInfo
            {
                FileName = platform.Executable,
                Arguments = platform.Arguments + " /b \"" + startupScript + "\"",
                WorkingDirectory = platform.WorkingDirectory,
                UseShellExecute = true
            });

            if (!WaitForCadProcess(45))
                throw new InvalidOperationException(platform.DisplayName + " 启动后没有检测到 AutoCAD 进程。请确认平台可以单独正常启动。\r\n\r\n插件文件已经安装，进入 CAD 后仍可手工执行 BPPUBLISH。\r\n\r\n如使用天正，请从选择列表中选择“T20 天正建筑”，不要选择普通 AutoCAD 2022。 ");

            Log("已通过启动脚本安排 NETLOAD + BPPUBLISH065");
        }

        private static void UninstallPlugin()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var bundle = Path.Combine(appData, "Autodesk", "ApplicationPlugins", "BatchPdfPublisher.bundle");
            if (Directory.Exists(bundle)) Directory.Delete(bundle, true);
            var releases = Path.Combine(appData, "BatchPdfPublisher", "releases");
            if (Directory.Exists(releases)) Directory.Delete(releases, true);
            var autodeskRoot = Path.Combine(appData, "Autodesk");
            if (Directory.Exists(autodeskRoot))
                foreach (var plotters in Directory.GetDirectories(autodeskRoot, "Plotters", SearchOption.AllDirectories))
                {
                    TryDelete(Path.Combine(plotters, PlotterConfigName));
                    TryDelete(Path.Combine(plotters, "PMP Files", PlotterMediaName));
                }
            TryDelete(LastPlatformPath());
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        private static bool WaitForCadProcess(int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                if (Process.GetProcessesByName("acad").Length > 0) return true;
                Thread.Sleep(1000);
            }
            return false;
        }

        private static bool HasRunningCad()
        {
            if (Process.GetProcessesByName("acad").Length > 0) return true;
            foreach (var progId in new[] { "AutoCAD.Application.24.0", "AutoCAD.Application.24.1", "AutoCAD.Application.24.2", "AutoCAD.Application.24.3", "AutoCAD.Application.25.0", "AutoCAD.Application.25.1" })
                try { if (Marshal.GetActiveObject(progId) != null) return true; } catch { }
            return false;
        }

        private static bool TrySendLoad(string progId, string installedAssembly)
        {
            var progIds = new[] { progId, "AutoCAD.Application.24.0", "AutoCAD.Application.24.1", "AutoCAD.Application.24.2", "AutoCAD.Application.24.3", "AutoCAD.Application.25.0", "AutoCAD.Application.25.1" }
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
                        "(command \"BPPUBLISH065\") \r\n");
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

        private static void InstallPlotterProfiles(string sourcePlotterConfig, string sourcePlotterMedia)
        {
            var autodeskRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk");
            if (!Directory.Exists(autodeskRoot)) throw new DirectoryNotFoundException("未找到 AutoCAD 用户配置目录。");
            var plotterDirectories = Directory.GetDirectories(autodeskRoot, "Plotters", SearchOption.AllDirectories)
                .Where(path => path.IndexOf("AutoCAD ", StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (plotterDirectories.Count == 0) throw new DirectoryNotFoundException("未找到 AutoCAD Plotters 目录，请先启动一次 CAD。");

            foreach (var plotterDirectory in plotterDirectories)
            {
                var pmpDirectory = Path.Combine(plotterDirectory, "PMP Files");
                Directory.CreateDirectory(pmpDirectory);
                var targetPc3 = Path.Combine(plotterDirectory, PlotterConfigName);
                var targetPmp = Path.Combine(pmpDirectory, PlotterMediaName);
                File.Copy(sourcePlotterConfig, targetPc3, true);
                File.Copy(sourcePlotterMedia, targetPmp, true);
                BindPmp(targetPc3, targetPmp);
                BindPmpSelfPath(targetPmp);
                Log("已部署毫米纸张库: " + plotterDirectory);
            }
        }

        private static void BindPmpSelfPath(string pmpPath)
        {
            RewriteCompressedPlotterFile(pmpPath, text =>
            {
                var rewritten = Regex.Replace(text, "user_defined_model_pathname=\\\"[^\\r\\n]*", "user_defined_model_pathname=\"" + pmpPath);
                if (rewritten.IndexOf("user_defined_model_pathname=\"" + pmpPath, StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidDataException("写入 BatchPdfPublisher.pmp 自身路径失败。");
                return rewritten;
            });
        }

        private static void BindPmp(string pc3Path, string pmpPath)
        {
            RewriteCompressedPlotterFile(pc3Path, text =>
            {
                // AutoCAD's PC3/PMP text grammar uses an opening quote and the
                // end of the line as the value terminator. A closing quote is
                // treated as part of the filename (for example "file.pmp\"").
                var rewritten = Regex.Replace(text, "user_defined_model_pathname=\\\"[^\\r\\n]*", "user_defined_model_pathname=\"" + pmpPath);
                var expectedBinding = "user_defined_model_pathname=\"" + pmpPath;
                if (rewritten.IndexOf(expectedBinding, StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidDataException("写入 BatchPdfPublisher.pc3 的 PMP 路径时产生了非法转义语法。");
                return rewritten;
            });
        }

        private static void RewriteCompressedPlotterFile(string path, Func<string, string> rewrite)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 64 || Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 60)).IndexOf("PIAFILEVERSION_2.0,PC3VER1,compress", StringComparison.Ordinal) < 0)
                throw new InvalidDataException(Path.GetFileName(path) + " 不是可识别的 AutoCAD 压缩配置文件。");
            var compressed = new MemoryStream(bytes, 62, bytes.Length - 62, false);
            var decoded = new MemoryStream();
            using (var inflater = new DeflateStream(compressed, CompressionMode.Decompress)) inflater.CopyTo(decoded);
            var raw = decoded.ToArray();
            var encoding = Encoding.GetEncoding(936);
            raw = encoding.GetBytes(rewrite(encoding.GetString(raw)));
            var packed = new MemoryStream();
            packed.WriteByte(0x78); packed.WriteByte(0xDA);
            using (var deflater = new DeflateStream(packed, CompressionMode.Compress, true)) deflater.Write(raw, 0, raw.Length);
            var checksum = Adler32(raw);
            packed.Write(checksum, 0, checksum.Length);
            var prefix = new byte[60]; Array.Copy(bytes, prefix, prefix.Length);
            Array.Copy(BitConverter.GetBytes(raw.Length), 0, prefix, 52, 4);
            Array.Copy(BitConverter.GetBytes((int)packed.Length), 0, prefix, 56, 4);
            using (var output = new MemoryStream())
            {
                output.Write(prefix, 0, prefix.Length);
                var payload = packed.ToArray(); output.Write(payload, 0, payload.Length);
                File.WriteAllBytes(path, output.ToArray());
            }
        }

        private static byte[] Adler32(byte[] data)
        {
            var a = 1; var b = 0;
            foreach (var value in data) { a = (a + value) % 65521; b = (b + a) % 65521; }
            return new[] { (byte)(b >> 8), (byte)b, (byte)(a >> 8), (byte)a };
        }

        private static string InstallPlugin(string sourceAssembly, string sourcePdfDependency, bool installPermanently)
        {
            var contentsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatchPdfPublisher", "releases");
            Directory.CreateDirectory(contentsDirectory);
            var hash = GetFileHash(sourceAssembly).Substring(0, 12);
            var installedFileName = "BatchPdfPublisher." + hash + ".dll";
            var installedAssembly = Path.Combine(contentsDirectory, installedFileName);
            if (!File.Exists(installedAssembly) || new FileInfo(installedAssembly).Length != new FileInfo(sourceAssembly).Length)
                File.Copy(sourceAssembly, installedAssembly, true);
            File.Copy(sourcePdfDependency, Path.Combine(contentsDirectory, PdfDependencyName), true);
            if (installPermanently) InstallAutoLoadBundle(installedAssembly, sourcePdfDependency);
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

    internal sealed class LauncherOptions
    {
        public PlatformOption Platform { get; set; }
        public bool LoadIntoRunningCad { get; set; }
        public bool InstallPermanently { get; set; }
    }

    internal sealed class PlatformPicker : Form
    {
        private readonly ComboBox _platformBox;
        private readonly CheckBox _runningCad;
        private readonly CheckBox _permanentInstall;
        private readonly Button _uninstallButton;
        public bool UninstallRequested { get; private set; }
        public LauncherOptions Options => new LauncherOptions
        {
            Platform = _platformBox.SelectedItem as PlatformOption,
            LoadIntoRunningCad = _runningCad.Checked,
            InstallPermanently = _permanentInstall.Checked
        };

        public PlatformPicker(IList<PlatformOption> platforms, string lastPlatform, bool hasRunningCad)
        {
            Text = "启动批量打印插件";
            Width = 540; Height = 290; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            var label = new Label { Left = 18, Top = 20, Width = 480, Text = "选择要加载插件的 CAD 平台：" };
            _platformBox = new ComboBox { Left = 18, Top = 52, Width = 440, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var platform in platforms) _platformBox.Items.Add(platform);
            var last = platforms.FirstOrDefault(x => string.Equals(x.Id, lastPlatform, StringComparison.OrdinalIgnoreCase));
            if (platforms.Count > 0) _platformBox.SelectedItem = last ?? platforms[0];
            _runningCad = new CheckBox { Left = 18, Top = 94, Width = 480, Text = hasRunningCad ? "加载到已启动的 CAD（已检测到 AutoCAD 进程）" : "加载到已启动的 CAD（当前未检测到，可稍后重试）", Checked = hasRunningCad, Enabled = hasRunningCad };
            _permanentInstall = new CheckBox { Left = 18, Top = 128, Width = 480, Text = "永久自动加载（以后每次启动 CAD 都加载插件）", Checked = false };
            var tip = new Label { Left = 18, Top = 160, Width = 480, Height = 38, ForeColor = System.Drawing.Color.FromArgb(80, 90, 105), Text = "默认仅本次加载，不会写入永久自动加载配置。已永久安装时，取消勾选不会自动卸载旧配置。" };
            var startButton = new Button { Left = 318, Top = 215, Width = 90, Text = "继续", DialogResult = DialogResult.OK, Enabled = platforms.Count > 0 };
            var cancelButton = new Button { Left = 418, Top = 215, Width = 85, Text = "取消", DialogResult = DialogResult.Cancel };
            _uninstallButton = new Button { Left = 18, Top = 215, Width = 90, Text = "卸载" };
            _uninstallButton.Click += (s, e) => { UninstallRequested = true; DialogResult = DialogResult.OK; };
            Controls.Add(label); Controls.Add(_platformBox); Controls.Add(_runningCad); Controls.Add(_permanentInstall); Controls.Add(tip); Controls.Add(_uninstallButton); Controls.Add(startButton); Controls.Add(cancelButton);
            AcceptButton = startButton; CancelButton = cancelButton;
        }
    }
}
