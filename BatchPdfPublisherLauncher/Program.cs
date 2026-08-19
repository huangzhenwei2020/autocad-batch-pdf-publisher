using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
        private const string ArrowLibraryName = "WanLuoArrowSymbols.dwg";
        private static readonly string ArrowLibraryRelativePath = Path.Combine("Resources", "Blocks", ArrowLibraryName);
        private static readonly string PlotterResourceDirectory = Path.Combine("Resources", "Plotters");
        private const string PlotterConfigName = "BatchPdfPublisher.pc3";
        private const string PlotterMediaName = "BatchPdfPublisher.pmp";
        private const string LastPlatformFileName = "BatchPdfPublisher.last-platform.txt";
        private const string ArchitecturePayloadResourceName = "WanluoArchitectureTools.CadArchSpecEditor.bundle.zip";
        private const string StairPayloadR24ResourceName = "WanluoArchitectureTools.StairDetail.R24.zip";
        private const string StairPayloadR25ResourceName = "WanluoArchitectureTools.StairDetail.R25.zip";
        private static readonly string UserDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WanluoArchitectureTools");
        private static readonly string LaunchLogPath = Path.Combine(EnsureDirectory(Path.Combine(UserDataRoot, "Logs")), "launcher.log");
        private static readonly string LoadReceiptPath = Path.Combine(Path.GetTempPath(), "WanluoArchitectureTools.loaded.log");

        [STAThread]
        private static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
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
                        MessageBox.Show("“万落建筑工具”及永久自动加载配置已卸载。工程文件和 CAD 图纸不会删除。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    if (picker.Options.Platform == null) throw new FileNotFoundException("未找到可用的 AutoCAD 安装。请先安装 AutoCAD，或检查安装权限。");
                    options = picker.Options;
                }
                var payload = ResolvePluginPayload(launcherDirectory, options.Platform.Release);
                var sourcePlotterConfig = ResolveResourceFile(launcherDirectory, PlotterResourceDirectory, PlotterConfigName);
                var sourcePlotterMedia = ResolveResourceFile(launcherDirectory, PlotterResourceDirectory, PlotterMediaName);
                if (!File.Exists(sourcePlotterConfig) || !File.Exists(sourcePlotterMedia))
                    throw new FileNotFoundException("发布包的 Resources\\Plotters 中缺少 BatchPdfPublisher.pc3/pmp 毫米纸张库，请使用完整发布包。");

                File.WriteAllText(LastPlatformPath(), options.Platform.Id);
                InstallPlotterProfiles(sourcePlotterConfig, sourcePlotterMedia);
                // A previous standalone BPP/spec installation may still be configured
                // for LoadOnStartup. Loading the same commands again from the suite
                // path creates two assembly instances and can terminate AutoCAD/T20
                // in native UI code. Always remove legacy/unified autoload bundles
                // first; permanent installation recreates one clean unified bundle.
                RemoveAutoLoadBundles();
                var pluginAssembly = InstallPlugin(payload, options.InstallPermanently);
                var architectureAssembly = InstallArchitectureAssistant(launcherDirectory, payload.Band, options.InstallPermanently);
                var stairAssembly = InstallStairDetail(launcherDirectory, payload.Band, options.InstallPermanently);
                ValidateInstalledComponents(payload.Band, pluginAssembly, architectureAssembly, stairAssembly);
                if (options.InstallPermanently) InstallAutoLoadBundle(pluginAssembly, payload.PdfDependencyPath, payload.Band, architectureAssembly, stairAssembly);
                Log((options.InstallPermanently ? "已部署插件: " : "便携加载插件: ") + pluginAssembly);
                if (!string.IsNullOrWhiteSpace(architectureAssembly)) Log((options.InstallPermanently ? "已部署建筑说明助手: " : "便携加载建筑说明助手: ") + architectureAssembly);
                if (!string.IsNullOrWhiteSpace(stairAssembly)) Log((options.InstallPermanently ? "已部署一键楼梯大样: " : "便携加载一键楼梯大样: ") + stairAssembly);
                var assemblies = new[] { pluginAssembly, architectureAssembly, stairAssembly }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                if (options.LoadIntoRunningCad)
                {
                    if (!TrySendLoad(options.Platform.ProgId, assemblies))
                        throw new InvalidOperationException("没有连接到已启动的 CAD。请确认目标 CAD 已完全打开，或取消“加载到已启动 CAD”后重新运行启动器。");
                }
                else StartPlatform(options.Platform, options.InstallPermanently ? new string[0] : assemblies);
            }
            catch (Exception exception)
            {
                Log("启动失败: " + exception);
                MessageBox.Show(exception.Message, "万落建筑工具启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static List<PlatformOption> FindPlatforms()
        {
            var result = new List<PlatformOption>();
            // The machine may have T20 as the default AutoCAD profile. Force
            // the unnamed vanilla profile so launching plain AutoCAD cannot
            // pull the T20 ARX/LSP startup chain into the session.
            var cadPlatforms = new List<PlatformOption>();
            foreach (var version in FindAutoCadInstallations())
            {
                cadPlatforms.Add(new PlatformOption(version.DisplayName, "acad-" + version.Release, version.Executable, "/nologo /p \"<<Unnamed Profile>>\"", version.WorkingDirectory, version.ProgId, "无天正", version.DisplayName, version.Release));
            }
            result.AddRange(cadPlatforms);
            foreach (var tz in FindTianzhengInstallations())
                foreach (var cad in cadPlatforms)
                    result.Add(new PlatformOption(tz.Name + " + " + cad.CadName, tz.Id + "-" + cad.Id, tz.Executable, "", Path.GetDirectoryName(tz.Executable), cad.ProgId, tz.Name, cad.CadName, cad.Release));
            if (result.Count == 0 && cadPlatforms.Count > 0) result.AddRange(cadPlatforms);
            return result;
        }

        private static List<AcadInstallation> FindAutoCadInstallations()
        {
            var result = new List<AcadInstallation>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var root = baseKey.OpenSubKey("SOFTWARE\\Autodesk\\AutoCAD"))
                    {
                        if (root == null) continue;
                        foreach (var release in root.GetSubKeyNames().Where(x => x.StartsWith("R", StringComparison.OrdinalIgnoreCase)))
                        using (var releaseKey = root.OpenSubKey(release))
                        {
                            if (!IsSupportedRelease(release)) continue;
                            if (releaseKey == null) continue;
                            foreach (var product in releaseKey.GetSubKeyNames())
                            using (var productKey = releaseKey.OpenSubKey(product))
                            using (var install = productKey?.OpenSubKey("Install"))
                            {
                                var dir = ReadAcadInstallDirectory(productKey, install);
                                var exe = string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, "acad.exe");
                                if (!File.Exists(exe)) continue;
                                var progId = "AutoCAD.Application." + release.Substring(1);
                                var display = ReadCadDisplayName(productKey, install, release);
                                if (!result.Any(x => string.Equals(x.Executable, exe, StringComparison.OrdinalIgnoreCase)))
                                    result.Add(new AcadInstallation(display, release.Substring(1), exe, dir, progId));
                            }
                        }
                    }
                }
                catch { }
            }
            return result.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsSupportedRelease(string release)
        {
            Version version;
            return Version.TryParse((release ?? string.Empty).TrimStart('R', 'r'), out version)
                && (version.Major == 24 || version.Major == 25);
        }

        private static string ReadCadDisplayName(RegistryKey productKey, RegistryKey install, string release)
        {
            var name = ReadRegistryString(productKey, "ProductName", "ProductNameGlob", "ProductNameForDisplay")
                ?? ReadRegistryString(install, "ProductName", "ProductNameForDisplay");
            var yearMatch = Regex.Match((name ?? string.Empty) + " " + release, @"(?:20)(2[0-9])");
            if (yearMatch.Success) return "AutoCAD " + yearMatch.Value;
            var releaseNumber = release.StartsWith("R", StringComparison.OrdinalIgnoreCase) ? release.Substring(1) : release;
            var knownYears = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["24.0"] = 2021, ["24.1"] = 2022, ["24.2"] = 2023, ["24.3"] = 2024,
                ["25.0"] = 2025, ["25.1"] = 2026
            };
            if (knownYears.TryGetValue(releaseNumber, out var year)) return "AutoCAD " + year;
            return string.IsNullOrWhiteSpace(name) ? "AutoCAD" : name.Trim();
        }

        private static string ReadAcadInstallDirectory(RegistryKey productKey, RegistryKey install)
        {
            // Different AutoCAD/Tianzheng installers store the directory on
            // either the localized product key or the nested Install key.
            var value = ReadRegistryString(install, "INSTALLDIR", "InstallLocation", "Location", "AcadLocation")
                ?? ReadRegistryString(productKey, "INSTALLDIR", "InstallLocation", "AcadLocation", "Location");
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string ReadRegistryString(RegistryKey key, params string[] names)
        {
            if (key == null) return null;
            foreach (var name in names)
            {
                var value = key.GetValue(name) as string;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        private static PluginPayload ResolvePluginPayload(string launcherDirectory, string release)
        {
            var band = GetApiBand(release);
            var bandDirectory = Path.Combine(launcherDirectory, "CadApi", band);
            var assembly = Path.Combine(bandDirectory, PluginAssemblyName);
            // CAD locks a loaded managed assembly until the process exits. During
            // development/update, allow a freshly compiled side-by-side payload
            // to take precedence without trying to overwrite that locked file.
            var sideBySideAssembly = Directory.Exists(bandDirectory)
                ? Directory.GetFiles(bandDirectory, "BatchPdfPublisher*.dll")
                    .Where(x => !string.Equals(Path.GetFileName(x), PluginAssemblyName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (!string.IsNullOrWhiteSpace(sideBySideAssembly) && (!File.Exists(assembly)
                || File.GetLastWriteTimeUtc(sideBySideAssembly) > File.GetLastWriteTimeUtc(assembly)))
                assembly = sideBySideAssembly;
            var dependency = Path.Combine(bandDirectory, PdfDependencyName);
            var arrowLibrary = ResolveResourceFile(launcherDirectory, Path.Combine("Resources", "Blocks"), ArrowLibraryName);
            if (string.Equals(band, "R24", StringComparison.OrdinalIgnoreCase) && !File.Exists(assembly))
            {
                assembly = Path.Combine(launcherDirectory, PluginAssemblyName);
                dependency = Path.Combine(launcherDirectory, PdfDependencyName);
            }
            if (!File.Exists(assembly) || !File.Exists(dependency) || !File.Exists(arrowLibrary))
                throw new FileNotFoundException(
                    "已识别 " + FormatRelease(release) + "，但发布包中缺少对应的 " + band + " 插件组件。\r\n\r\n"
                    + "预期目录：" + bandDirectory + "\r\n"
                    + "请使用包含 AutoCAD 2021–2026 分代 DLL 的完整发布包。");
            return new PluginPayload(band, assembly, dependency, arrowLibrary);
        }

        private static string ResolveResourceFile(string launcherDirectory, string resourceDirectory, string fileName)
        {
            var organized = Path.Combine(launcherDirectory, resourceDirectory, fileName);
            if (File.Exists(organized)) return organized;
            // Compatibility with packages created before resources were grouped.
            return Path.Combine(launcherDirectory, fileName);
        }

        private static string GetApiBand(string release)
        {
            if (!Version.TryParse((release ?? string.Empty).TrimStart('R', 'r'), out var version))
                throw new InvalidOperationException("无法识别 AutoCAD 内部版本号：" + release);
            if (version.Major == 24) return "R24";
            if (version.Major == 25) return "R25";
            throw new NotSupportedException("当前启动器只支持 AutoCAD 2021–2026，检测到内部版本：" + release);
        }

        private static string FormatRelease(string release)
        {
            var display = ReadCadDisplayName(null, null, "R" + (release ?? string.Empty).TrimStart('R', 'r'));
            return display + "（R" + (release ?? string.Empty).TrimStart('R', 'r') + "）";
        }

        private static List<TianzhengInstallation> FindTianzhengInstallations()
        {
            var result = new List<TianzhengInstallation>();
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
            {
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Tangent"));
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files", "Tangent"));
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Tangent"));
            }
            foreach (var root in FindTianzhengUninstallLocations()) roots.Add(root);
            foreach (var root in roots)
            {
                ScanTianzhengDirectory(result, root, root);
            }
            // Installed Tianzheng products publish their real launcher in the
            // Start Menu. Reading shortcuts also covers custom drives and
            // T20-Elec/T20-Water/T20-Hvac/T20-Struct product folders.
            var shortcutRoots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) };
            foreach (var root in shortcutRoots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (var link in Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories))
                    {
                        var target = ResolveShortcut(link);
                        if (!string.IsNullOrWhiteSpace(target) && File.Exists(target)) AddTianzheng(result, target, link);
                    }
                }
                catch { }
            }
            return result;
        }

        private static IEnumerable<string> FindTianzhengUninstallLocations()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var uninstall = baseKey.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        foreach (var name in uninstall.GetSubKeyNames())
                        using (var product = uninstall.OpenSubKey(name))
                        {
                            var displayName = ReadRegistryString(product, "DisplayName") ?? string.Empty;
                            var publisher = ReadRegistryString(product, "Publisher") ?? string.Empty;
                            if (!Regex.IsMatch(displayName + " " + publisher, "天正|T20|T30|Tangent", RegexOptions.IgnoreCase)) continue;
                            var location = ReadRegistryString(product, "InstallLocation", "InstallPath", "Path");
                            if (!string.IsNullOrWhiteSpace(location)) result.Add(location.Trim().Trim('"'));
                            var icon = ReadRegistryString(product, "DisplayIcon");
                            if (!string.IsNullOrWhiteSpace(icon))
                            {
                                var executable = icon.Trim().Trim('"').Split(',')[0].Trim().Trim('"');
                                if (File.Exists(executable)) result.Add(Path.GetDirectoryName(executable));
                            }
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        private static void ScanTianzhengDirectory(List<TianzhengInstallation> result, string root, string hint)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            try
            {
                foreach (var file in Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    if (!(name.Contains("tgstart") || name.Contains("t20start") || name.Contains("t30start") || name.Contains("telec") || name.Contains("tmelec") || name.Contains("tarch"))) continue;
                    var version = FileVersionInfo.GetVersionInfo(file);
                    AddTianzheng(result, file, hint + " " + version.ProductName + " " + version.FileDescription);
                }
            }
            catch { }
        }

        internal static TianzhengInstallation CreateTianzhengInstallation(string executable)
        {
            var result = new List<TianzhengInstallation>();
            var version = FileVersionInfo.GetVersionInfo(executable);
            AddTianzheng(result, executable, executable + " " + version.ProductName + " " + version.FileDescription);
            if (result.Count == 0 && Path.GetFileNameWithoutExtension(executable).IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0)
                result.Add(new TianzhengInstallation("天正 建筑", "manual-" + GetFileHash(executable).Substring(0, 8), executable));
            return result.FirstOrDefault();
        }

        private static void AddTianzheng(List<TianzhengInstallation> result, string executable, string hint)
        {
            if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) return;
            var fullName = (executable + " " + hint).ToLowerInvariant();
            if (!(fullName.Contains("t20") || fullName.Contains("t30") || fullName.Contains("tangent") || fullName.Contains("天正"))) return;
            var product = fullName.Contains("elec") || fullName.Contains("电气") ? "天正电气" :
                fullName.Contains("water") || fullName.Contains("给排水") ? "天正给排水" :
                fullName.Contains("hvac") || fullName.Contains("暖通") ? "天正暖通" :
                fullName.Contains("struct") || fullName.Contains("结构") ? "天正结构" : "天正建筑";
            var match = Regex.Match(fullName, @"(?:t20|t30)[^v\d]{0,2}v?(\d+)(?:[\._](\d+))?", RegexOptions.IgnoreCase);
            var version = match.Success ? " V" + match.Groups[1].Value + (match.Groups[2].Success ? "." + match.Groups[2].Value : ".0") : "";
            var generation = fullName.Contains("t30") ? "T30" : fullName.Contains("t20") ? "T20" : "天正";
            var display = generation + " " + product + version;
            var id = display + "-" + GetFileHash(executable).Substring(0, 8);
            if (!result.Any(x => string.Equals(x.Executable, executable, StringComparison.OrdinalIgnoreCase))) result.Add(new TianzhengInstallation(display, id, executable));
        }

        private static string ResolveShortcut(string link)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                var shell = Activator.CreateInstance(shellType);
                var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                return shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            }
            catch { return null; }
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
                        using (var productKey = releaseKey.OpenSubKey(product))
                        using (var install = productKey?.OpenSubKey("Install"))
                        {
                            var value = ReadAcadInstallDirectory(productKey, install);
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

        private static void StartPlatform(PlatformOption platform, IList<string> installedAssemblies)
        {
            Log("启动平台: " + platform.DisplayName);
            TryDelete(LoadReceiptPath);
            var startupScript = Path.Combine(Path.GetTempPath(), "BatchPdfPublisher." + Guid.NewGuid().ToString("N") + ".scr");
            var loadableAssemblies = installedAssemblies.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var trustedDirectories = string.Join(";", loadableAssemblies.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Select(x => x.Replace('\\', '/')));
            var loadCommands = BuildNetloadCommandStream(loadableAssemblies);
            File.WriteAllText(startupScript,
                "(setvar \"FILEDIA\" 0)\r\n" +
                "(setvar \"TRUSTEDPATHS\" (strcat (getvar \"TRUSTEDPATHS\") \";" + EscapeLispString(trustedDirectories) + "\"))\r\n" +
                loadCommands +
                "(setvar \"FILEDIA\" 1)\r\n" +
                "BPPSTARTUP\r\n", Encoding.Default);
            Process.Start(new ProcessStartInfo
            {
                FileName = platform.Executable,
                Arguments = platform.Arguments + " /b \"" + startupScript + "\"",
                WorkingDirectory = platform.WorkingDirectory,
                UseShellExecute = true
            });

            if (!WaitForCadProcess(45))
                throw new InvalidOperationException(platform.DisplayName + " 启动后没有检测到 AutoCAD 进程。请确认平台可以单独正常启动。\r\n\r\n插件文件已经安装，进入 CAD 后仍可手工执行 BPPUBLISH。\r\n\r\n如使用天正，请从选择列表中选择“T20 天正建筑”，不要选择普通 AutoCAD 2022。 ");

            if (!WaitForLoadReceipt(60))
                throw new InvalidOperationException(platform.DisplayName + " 已启动，但插件在 60 秒内没有返回加载成功信息。\r\n\r\n请关闭 CAD 后重试；日志：" + LaunchLogPath);
            Log(loadableAssemblies.Length > 0 ? "插件已通过启动脚本确认加载成功" : "插件已通过统一 Bundle 确认加载成功");
        }

        private static void UninstallPlugin()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            RemoveAutoLoadBundles();
            var releases = Path.Combine(appData, "BatchPdfPublisher", "releases");
            if (Directory.Exists(releases)) Directory.Delete(releases, true);
            TryDelete(LastPlatformPath());
            var suiteFiles = Path.Combine(appData, "WanluoArchitectureTools");
            if (Directory.Exists(suiteFiles)) Directory.Delete(suiteFiles, true);
            var autodeskRoot = Path.Combine(appData, "Autodesk");
            if (Directory.Exists(autodeskRoot))
                foreach (var plotters in Directory.GetDirectories(autodeskRoot, "Plotters", SearchOption.AllDirectories))
                {
                    TryDelete(Path.Combine(plotters, PlotterConfigName));
                    TryDelete(Path.Combine(plotters, "PMP Files", PlotterMediaName));
                }
        }

        private static void RemoveAutoLoadBundles()
        {
            var pluginRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "ApplicationPlugins");
            foreach (var name in new[] { "WanluoArchitectureTools.bundle", "BatchPdfPublisher.bundle", "CadArchSpecEditor.bundle", "WanLuoArchitecture2022.bundle" })
            {
                var bundle = Path.Combine(pluginRoot, name);
                if (Directory.Exists(bundle)) Directory.Delete(bundle, true);
            }
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
            foreach (var progId in KnownProgIds())
                try { if (Marshal.GetActiveObject(progId) != null) return true; } catch { }
            return false;
        }

        private static bool TrySendLoad(string progId, IList<string> installedAssemblies)
        {
            TryDelete(LoadReceiptPath);
            var progIds = new[] { progId }.Concat(KnownProgIds())
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
                    var loadableAssemblies = installedAssemblies.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var directories = string.Join(";", loadableAssemblies.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Select(x => x.Replace('\\', '/')));
                    SendCommand(document,
                        "(setvar \"FILEDIA\" 0)\r\n" +
                        "(setvar \"TRUSTEDPATHS\" (strcat (getvar \"TRUSTEDPATHS\") \";" + EscapeLispString(directories) + "\"))\r\n" +
                        BuildNetloadCommandStream(loadableAssemblies) +
                        "(setvar \"FILEDIA\" 1)\r\n" +
                        "BPPSTARTUP\r\n");
                    if (!WaitForLoadReceipt(30)) throw new InvalidOperationException("已发送 NETLOAD，但目标 CAD 没有返回加载成功信息");
                    Log("已向运行中的 AutoCAD 加载插件并收到成功回执；未自动打开 BPP 面板");
                    return true;
                }
                catch (Exception exception) { Log("COM 尝试 " + (attempt + 1) + " 失败: " + exception.Message); Thread.Sleep(1000); }
            }
            return false;
        }

        private static IEnumerable<string> KnownProgIds()
        {
            var releases = new[] { "19.1", "20.0", "20.1", "21.0", "22.0", "23.0", "23.1", "24.0", "24.1", "24.2", "24.3", "25.0", "25.1" };
            return releases.Select(x => "AutoCAD.Application." + x);
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

        private static string LastPlatformPath()
        {
            var target = Path.Combine(EnsureDirectory(Path.Combine(UserDataRoot, "Settings")), "last-platform.txt");
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LastPlatformFileName);
            try { if (!File.Exists(target) && File.Exists(legacy)) File.Copy(legacy, target, false); } catch { }
            return target;
        }

        private static string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }

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
                EnsureStandardExtendedMedia(targetPmp);
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

        private static void EnsureStandardExtendedMedia(string pmpPath)
        {
            // Keep the bundled PMP usable on machines where the file came
            // from an older release.  AutoCAD caches the PMP media list, so
            // adding the standard GB/T 50001 extended lengths at deployment
            // time is safer than silently falling back to A1/A0.
            var media = new[]
            {
                new { Paper = "A1", W = 594, H = 1682 },
                new { Paper = "A2", W = 420, H = 891 },
                new { Paper = "A2", W = 420, H = 1338 },
                new { Paper = "A2", W = 420, H = 1486 },
                new { Paper = "A2", W = 420, H = 1635 },
                new { Paper = "A2", W = 420, H = 1783 },
                new { Paper = "A2", W = 420, H = 1932 },
                new { Paper = "A2", W = 420, H = 2080 },
                new { Paper = "A3", W = 297, H = 630 },
                new { Paper = "A3", W = 297, H = 841 },
                new { Paper = "A3", W = 297, H = 1051 },
                new { Paper = "A3", W = 297, H = 1261 },
                new { Paper = "A3", W = 297, H = 1471 },
                new { Paper = "A3", W = 297, H = 1682 },
                new { Paper = "A3", W = 297, H = 1892 }
            };
            RewriteCompressedPlotterFile(pmpPath, text =>
            {
                var sizeInsert = new StringBuilder();
                var descriptionInsert = new StringBuilder();
                var nextSize = Regex.Matches(text, "\\n   (\\d+)\\{\\r?\\n    caps_type=2")
                    .Cast<Match>().Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max() + 1;
                var nextDescription = Regex.Matches(text, "\\n   (\\d+)\\{\\r?\\n    caps_type=2\\r?\\n    name=\\\"UserDefinedMetric")
                    .Cast<Match>().Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max() + 1;
                foreach (var item in media)
                {
                    var id = "BPP_" + item.Paper + "_" + item.W + "x" + item.H + "_MM_FULL_BLEED";
                    if (text.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var area = item.W * item.H;
                    sizeInsert.AppendLine("   " + nextSize++ + "{");
                    sizeInsert.AppendLine("    caps_type=2");
                    sizeInsert.AppendLine("    name=\"UserDefinedMetric (" + item.W.ToString("0.00") + " x " + item.H.ToString("0.00") + "毫米)\"");
                    sizeInsert.AppendLine("    localized_name=\"" + id + "\"");
                    sizeInsert.AppendLine("    media_description_name=\"UserDefinedMetric " + item.W.ToString("0.00") + "W x " + item.H.ToString("0.00") + "H\"");
                    sizeInsert.AppendLine("    media_group=15");
                    sizeInsert.AppendLine("    landscape_mode=FALSE");
                    sizeInsert.AppendLine("   }");
                    descriptionInsert.AppendLine("   " + nextDescription++ + "{");
                    descriptionInsert.AppendLine("    caps_type=2");
                    descriptionInsert.AppendLine("    name=\"UserDefinedMetric " + item.W.ToString("0.00") + "W x " + item.H.ToString("0.00") + "H\"");
                    descriptionInsert.AppendLine("    media_bounds_urx=" + item.W.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    descriptionInsert.AppendLine("    media_bounds_ury=" + item.H.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    descriptionInsert.AppendLine("    printable_bounds_llx=0.0");
                    descriptionInsert.AppendLine("    printable_bounds_lly=0.0");
                    descriptionInsert.AppendLine("    printable_bounds_urx=" + item.W.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    descriptionInsert.AppendLine("    printable_bounds_ury=" + item.H.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    descriptionInsert.AppendLine("    printable_area=" + area.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture));
                    descriptionInsert.AppendLine("    dimensional=TRUE");
                    descriptionInsert.AppendLine("   }");
                }
                if (sizeInsert.Length == 0) return text;
                var sizeMarker = text.IndexOf("   description{", StringComparison.Ordinal);
                if (sizeMarker < 0) return text;
                text = text.Insert(sizeMarker, sizeInsert.ToString());
                var closeMarker = text.LastIndexOf("\n  }\n }\n}", StringComparison.Ordinal);
                if (closeMarker >= 0) text = text.Insert(closeMarker, descriptionInsert.ToString());
                return text;
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

        private static string InstallPlugin(PluginPayload payload, bool installPermanently)
        {
            if (!installPermanently) return payload.AssemblyPath;
            var contentsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WanluoArchitectureTools", "releases", payload.Band);
            Directory.CreateDirectory(contentsDirectory);
            var hash = GetFileHash(payload.AssemblyPath).Substring(0, 12);
            var installedFileName = "BatchPdfPublisher." + hash + ".dll";
            var installedAssembly = Path.Combine(contentsDirectory, installedFileName);
            if (!File.Exists(installedAssembly) || new FileInfo(installedAssembly).Length != new FileInfo(payload.AssemblyPath).Length)
                File.Copy(payload.AssemblyPath, installedAssembly, true);
            File.Copy(payload.PdfDependencyPath, Path.Combine(contentsDirectory, PdfDependencyName), true);
            var resourceDirectory = Path.Combine(contentsDirectory, "Resources", "Blocks");
            Directory.CreateDirectory(resourceDirectory);
            File.Copy(payload.ArrowLibraryPath, Path.Combine(resourceDirectory, ArrowLibraryName), true);
            var legacyArrowLibrary = Path.Combine(contentsDirectory, ArrowLibraryName);
            if (File.Exists(legacyArrowLibrary)) File.Delete(legacyArrowLibrary);
            return installedAssembly;
        }

        private static string EscapeLispString(string value)
        {
            return (value ?? string.Empty).Replace("\\", "/").Replace("\"", "\\\"");
        }

        private static string BuildNetloadCommandStream(IEnumerable<string> assemblies)
        {
            return string.Join(string.Empty, assemblies.Select(x =>
                "_.NETLOAD\r\n\"" + x.Replace('\\', '/') + "\"\r\n"));
        }

        private static bool WaitForLoadReceipt(int seconds)
        {
            for (var i = 0; i < seconds * 2; i++)
            {
                try
                {
                    if (File.Exists(LoadReceiptPath) && new FileInfo(LoadReceiptPath).Length > 0) return true;
                }
                catch { }
                Thread.Sleep(500);
            }
            return false;
        }

        private static string InstallArchitectureAssistant(string launcherDirectory, string band, bool installPermanently)
        {
            var hostName = string.Equals(band, "R24", StringComparison.OrdinalIgnoreCase) ? "CadArchSpec.Host.AutoCAD2022.dll" :
                string.Equals(band, "R25", StringComparison.OrdinalIgnoreCase) ? "CadArchSpec.Host.AutoCAD2026.dll" : null;
            if (hostName == null) return null;
            var source = Path.Combine(launcherDirectory, "ArchitectureAssistant", band);
            var sourceHost = Path.Combine(source, hostName);
            if (!File.Exists(sourceHost))
            {
                source = ExtractEmbeddedArchitectureAssistant(band, hostName);
                sourceHost = Path.Combine(source, hostName);
            }
            if (!File.Exists(sourceHost))
                throw new FileNotFoundException("未能准备建筑设计说明助手的 " + band + " 组件。", sourceHost);
            if (!installPermanently) return sourceHost;
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WanluoArchitectureTools", "ArchitectureAssistant", band);
            CopyDirectory(source, target);
            return Path.Combine(target, hostName);
        }

        private static string InstallStairDetail(string launcherDirectory, string band, bool installPermanently)
        {
            var hostName = string.Equals(band, "R24", StringComparison.OrdinalIgnoreCase) ? "WL.Stair.Cad2022.dll" :
                string.Equals(band, "R25", StringComparison.OrdinalIgnoreCase) ? "WL.Stair.Cad2026.dll" : null;
            if (hostName == null) return null;
            var source = Path.Combine(launcherDirectory, "StairDetail", band);
            var sourceHost = Path.Combine(source, hostName);
            if (!File.Exists(sourceHost))
            {
                source = ExtractEmbeddedStairDetail(band, hostName);
                sourceHost = Path.Combine(source, hostName);
            }
            if (!File.Exists(sourceHost))
                throw new FileNotFoundException("未能准备一键楼梯大样的 " + band + " 组件。", sourceHost);
            if (!installPermanently) return sourceHost;
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WanluoArchitectureTools", "StairDetail", band);
            CopyDirectory(source, target);
            return Path.Combine(target, hostName);
        }

        private static string ExtractEmbeddedStairDetail(string band, string hostName)
        {
            var resourceName = string.Equals(band, "R24", StringComparison.OrdinalIgnoreCase) ? StairPayloadR24ResourceName :
                string.Equals(band, "R25", StringComparison.OrdinalIgnoreCase) ? StairPayloadR25ResourceName : null;
            if (resourceName == null)
                throw new NotSupportedException("一键楼梯大样当前没有 " + band + " 专用运行组件。");
            return ExtractFlatPayload(resourceName, "StairDetail", band, hostName);
        }

        private static string ExtractFlatPayload(string resourceName, string componentName, string band, string hostName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var resource = assembly.GetManifestResourceStream(resourceName))
            {
                if (resource == null) throw new FileNotFoundException("启动器内未找到 " + componentName + " 资源。");
                string payloadHash;
                using (var sha256 = SHA256.Create())
                {
                    payloadHash = string.Concat(sha256.ComputeHash(resource).Take(8).Select(x => x.ToString("x2")));
                    resource.Position = 0;
                }
                var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WanluoArchitectureTools", "PayloadCache", componentName, payloadHash, band);
                var cachedHost = Path.Combine(cacheRoot, hostName);
                var completionMarker = Path.Combine(cacheRoot, ".complete");
                var mutexName = "Local\\WanluoPayload_" + componentName + "_" + payloadHash + "_" + band;
                using (var extractionMutex = new Mutex(false, mutexName))
                {
                    var lockTaken = false;
                    try
                    {
                        try { lockTaken = extractionMutex.WaitOne(TimeSpan.FromSeconds(30)); }
                        catch (AbandonedMutexException) { lockTaken = true; }
                        if (!lockTaken) throw new TimeoutException("等待 " + componentName + " 资源准备超时，请稍后重试。");
                        if (File.Exists(cachedHost) && File.Exists(completionMarker)) return cacheRoot;
                        if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, true);
                        Directory.CreateDirectory(cacheRoot);
                        var normalizedRoot = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        using (var archive = new ZipArchive(resource, ZipArchiveMode.Read, false))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                                if (string.IsNullOrWhiteSpace(relativePath)) continue;
                                var destination = Path.GetFullPath(Path.Combine(cacheRoot, relativePath));
                                if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidDataException(componentName + " 资源包含不安全路径：" + entry.FullName);
                                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(destination); continue; }
                                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                                using (var input = entry.Open())
                                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                            }
                        }
                        if (!File.Exists(cachedHost)) throw new InvalidDataException("内置资源中缺少 " + hostName + "。");
                        File.WriteAllText(completionMarker, payloadHash, Encoding.ASCII);
                        return cacheRoot;
                    }
                    finally { if (lockTaken) extractionMutex.ReleaseMutex(); }
                }
            }
        }

        private static void ValidateInstalledComponents(string band, string pluginAssembly, string architectureAssembly, string stairAssembly)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(pluginAssembly) || !File.Exists(pluginAssembly)) missing.Add("批量 PDF / 图框 / 目录 / 属性 / 制图 / 门窗 / 房间主模块");
            if (string.Equals(band, "R24", StringComparison.OrdinalIgnoreCase) || string.Equals(band, "R25", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(architectureAssembly) || !File.Exists(architectureAssembly)) missing.Add("建筑设计说明助手");
                if (string.IsNullOrWhiteSpace(stairAssembly) || !File.Exists(stairAssembly)) missing.Add("一键楼梯大样");
            }
            if (missing.Count > 0)
                throw new InvalidDataException("安装包组件不完整，以下功能没有成功部署：\r\n\r\n- " + string.Join("\r\n- ", missing) + "\r\n\r\n安装已停止，请重新取得完整发布包。");
            Log("组件完整性检查通过: " + band);
        }

        private static string ExtractEmbeddedArchitectureAssistant(string band, string hostName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var resource = assembly.GetManifestResourceStream(ArchitecturePayloadResourceName))
            {
                if (resource == null)
                    throw new FileNotFoundException("启动器内未找到建筑设计说明助手资源。");

                string payloadHash;
                using (var sha256 = SHA256.Create())
                {
                    payloadHash = string.Concat(sha256.ComputeHash(resource).Take(8).Select(x => x.ToString("x2")));
                    resource.Position = 0;
                }

                var cacheRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WanluoArchitectureTools", "PayloadCache", "ArchitectureAssistant", payloadHash, band);
                var cachedHost = Path.Combine(cacheRoot, hostName);
                var completionMarker = Path.Combine(cacheRoot, ".complete");
                var mutexName = "Local\\WanluoArchitecturePayload_" + payloadHash + "_" + band;
                using (var extractionMutex = new Mutex(false, mutexName))
                {
                    var lockTaken = false;
                    try
                    {
                        try { lockTaken = extractionMutex.WaitOne(TimeSpan.FromSeconds(30)); }
                        catch (AbandonedMutexException) { lockTaken = true; }
                        if (!lockTaken) throw new TimeoutException("等待建筑设计说明助手资源准备超时，请稍后重试。");
                        if (File.Exists(cachedHost) && File.Exists(completionMarker)) return cacheRoot;

                        if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, true);
                        Directory.CreateDirectory(cacheRoot);
                        var versionDirectory = string.Equals(band, "R24", StringComparison.OrdinalIgnoreCase) ? "R24.1" : "R25.1";
                        var prefix = "Contents/" + versionDirectory + "/";
                        var normalizedRoot = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        using (var archive = new ZipArchive(resource, ZipArchiveMode.Read, false))
                        {
                            foreach (var entry in archive.Entries.Where(x => x.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                            {
                                var relativePath = entry.FullName.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                                if (string.IsNullOrWhiteSpace(relativePath)) continue;
                                var destination = Path.GetFullPath(Path.Combine(cacheRoot, relativePath));
                                if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidDataException("建筑设计说明助手资源包含不安全路径：" + entry.FullName);
                                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                                {
                                    Directory.CreateDirectory(destination);
                                    continue;
                                }
                                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                                using (var input = entry.Open())
                                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                                    input.CopyTo(output);
                            }
                        }
                        if (!File.Exists(cachedHost))
                            throw new InvalidDataException("内置资源中缺少 " + hostName + "。");
                        File.WriteAllText(completionMarker, payloadHash, Encoding.ASCII);
                        return cacheRoot;
                    }
                    finally
                    {
                        if (lockTaken) extractionMutex.ReleaseMutex();
                    }
                }
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(target, directory.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, true);
            }
        }

        private static void InstallAutoLoadBundle(string installedAssembly, string sourcePdfDependency, string band, string architectureAssembly, string stairAssembly)
        {
            var applicationPlugins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "ApplicationPlugins");
            var bundle = Path.Combine(applicationPlugins, "WanluoArchitectureTools.bundle");
            var contents = Path.Combine(bundle, "Contents", band);
            Directory.CreateDirectory(contents);
            File.Copy(installedAssembly, Path.Combine(contents, PluginAssemblyName), true);
            File.Copy(sourcePdfDependency, Path.Combine(contents, PdfDependencyName), true);
            var installedArrowLibrary = Path.Combine(Path.GetDirectoryName(installedAssembly), ArrowLibraryRelativePath);
            if (!File.Exists(installedArrowLibrary)) installedArrowLibrary = Path.Combine(Path.GetDirectoryName(installedAssembly), ArrowLibraryName);
            if (File.Exists(installedArrowLibrary))
            {
                var resourceDirectory = Path.Combine(contents, "Resources", "Blocks");
                Directory.CreateDirectory(resourceDirectory);
                File.Copy(installedArrowLibrary, Path.Combine(resourceDirectory, ArrowLibraryName), true);
            }
            if (!string.IsNullOrWhiteSpace(architectureAssembly))
                CopyDirectory(Path.GetDirectoryName(architectureAssembly), Path.Combine(contents, "ArchitectureAssistant"));
            if (!string.IsNullOrWhiteSpace(stairAssembly))
                CopyDirectory(Path.GetDirectoryName(stairAssembly), Path.Combine(contents, "StairDetail"));
            ValidateBundleContents(contents, band);
            var oldBatchBundle = Path.Combine(applicationPlugins, "BatchPdfPublisher.bundle");
            var oldArchitectureBundle = Path.Combine(applicationPlugins, "CadArchSpecEditor.bundle");
            if (Directory.Exists(oldBatchBundle)) Directory.Delete(oldBatchBundle, true);
            if (Directory.Exists(oldArchitectureBundle)) Directory.Delete(oldArchitectureBundle, true);
            var installedBands = Directory.GetDirectories(Path.Combine(bundle, "Contents"))
                .Select(Path.GetFileName)
                .Where(x => File.Exists(Path.Combine(bundle, "Contents", x, PluginAssemblyName)))
                .Where(x => new[] { "R24", "R25" }.Contains(x, StringComparer.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var package = new StringBuilder();
            package.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            package.AppendLine("<ApplicationPackage SchemaVersion=\"1.0\" AutodeskProduct=\"AutoCAD\" Name=\"WanluoArchitectureTools\" AppVersion=\"1.1.1\" ProductCode=\"{7B9E2D72-1C3E-4F3D-9C0C-7D5D3E5A0A01}\">");
            package.AppendLine("  <CompanyDetails Name=\"万落建筑工具\" />");
            foreach (var installedBand in installedBands)
            {
                var range = GetSeriesRangeForBand(installedBand);
                package.AppendLine("  <Components>");
                package.AppendLine("    <RuntimeRequirements OS=\"Win64\" Platform=\"AutoCAD*\" SeriesMin=\"" + range.Item1 + "\" SeriesMax=\"" + range.Item2 + "\" />");
                package.AppendLine("    <ComponentEntry AppName=\"WanluoArchitectureTools-PDF-" + installedBand + "\" ModuleName=\"Contents\\" + installedBand + "\\BatchPdfPublisher.dll\" AppDescription=\"万落建筑工具·批量 PDF 发布\" LoadReasons=\"LoadOnStartup\" />");
                var architectureDirectory = Path.Combine(bundle, "Contents", installedBand, "ArchitectureAssistant");
                var architectureHost = string.Equals(installedBand, "R24", StringComparison.OrdinalIgnoreCase) ? "CadArchSpec.Host.AutoCAD2022.dll" :
                    string.Equals(installedBand, "R25", StringComparison.OrdinalIgnoreCase) ? "CadArchSpec.Host.AutoCAD2026.dll" : null;
                if (architectureHost != null && File.Exists(Path.Combine(architectureDirectory, architectureHost)))
                    package.AppendLine("    <ComponentEntry AppName=\"WanluoArchitectureTools-Spec-" + installedBand + "\" ModuleName=\"Contents\\" + installedBand + "\\ArchitectureAssistant\\" + architectureHost + "\" AppDescription=\"万落建筑工具·建筑设计说明\" LoadReasons=\"LoadOnStartup\" />");
                var stairDirectory = Path.Combine(bundle, "Contents", installedBand, "StairDetail");
                var stairHost = string.Equals(installedBand, "R24", StringComparison.OrdinalIgnoreCase) ? "WL.Stair.Cad2022.dll" :
                    string.Equals(installedBand, "R25", StringComparison.OrdinalIgnoreCase) ? "WL.Stair.Cad2026.dll" : null;
                if (stairHost != null && File.Exists(Path.Combine(stairDirectory, stairHost)))
                    package.AppendLine("    <ComponentEntry AppName=\"WanluoArchitectureTools-Stair-" + installedBand + "\" ModuleName=\"Contents\\" + installedBand + "\\StairDetail\\" + stairHost + "\" AppDescription=\"万落建筑工具·一键楼梯大样\" LoadReasons=\"LoadOnStartup\" />");
                package.AppendLine("  </Components>");
            }
            package.AppendLine("</ApplicationPackage>");
            File.WriteAllText(Path.Combine(bundle, "PackageContents.xml"), package.ToString(), Encoding.UTF8);
        }

        private static void ValidateBundleContents(string contents, string band)
        {
            var missing = new List<string>();
            foreach (var file in new[] { PluginAssemblyName, PdfDependencyName, ArrowLibraryRelativePath })
                if (!File.Exists(Path.Combine(contents, file))) missing.Add(file);
            if (string.Equals(band, "R24", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(contents, "ArchitectureAssistant", "CadArchSpec.Host.AutoCAD2022.dll"))) missing.Add("ArchitectureAssistant\\CadArchSpec.Host.AutoCAD2022.dll");
                if (!File.Exists(Path.Combine(contents, "StairDetail", "WL.Stair.Cad2022.dll"))) missing.Add("StairDetail\\WL.Stair.Cad2022.dll");
            }
            if (string.Equals(band, "R25", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(contents, "ArchitectureAssistant", "CadArchSpec.Host.AutoCAD2026.dll"))) missing.Add("ArchitectureAssistant\\CadArchSpec.Host.AutoCAD2026.dll");
                if (!File.Exists(Path.Combine(contents, "StairDetail", "WL.Stair.Cad2026.dll"))) missing.Add("StairDetail\\WL.Stair.Cad2026.dll");
            }
            if (missing.Count > 0) throw new InvalidDataException("自动加载目录组件校验失败：" + string.Join("、", missing));
        }

        private static Tuple<string, string> GetSeriesRangeForBand(string band)
        {
            switch (band)
            {
                case "R24": return Tuple.Create("R24.0", "R24.3");
                case "R25": return Tuple.Create("R25.0", "R25.1");
                default: throw new NotSupportedException("不支持的 AutoCAD API 分代：" + band);
            }
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
        public PlatformOption(string displayName, string id, string executable, string arguments, string workingDirectory, string progId, string release)
        {
            DisplayName = displayName; Id = id; Executable = executable; Arguments = arguments; WorkingDirectory = workingDirectory; ProgId = progId; TianzhengName = "无天正"; CadName = displayName; Release = release;
        }
        public PlatformOption(string displayName, string id, string executable, string arguments, string workingDirectory, string progId, string tianzhengName, string cadName, string release)
        { DisplayName = displayName; Id = id; Executable = executable; Arguments = arguments; WorkingDirectory = workingDirectory; ProgId = progId; TianzhengName = tianzhengName; CadName = cadName; Release = release; }
        public string DisplayName { get; }
        public string Id { get; }
        public string Executable { get; }
        public string Arguments { get; }
        public string WorkingDirectory { get; }
        public string ProgId { get; }
        public string TianzhengName { get; }
        public string CadName { get; }
        public string Release { get; }
        public override string ToString() => DisplayName;
    }

    internal sealed class PluginPayload
    {
        public PluginPayload(string band, string assemblyPath, string pdfDependencyPath, string arrowLibraryPath)
        {
            Band = band; AssemblyPath = assemblyPath; PdfDependencyPath = pdfDependencyPath; ArrowLibraryPath = arrowLibraryPath;
        }
        public string Band { get; }
        public string AssemblyPath { get; }
        public string PdfDependencyPath { get; }
        public string ArrowLibraryPath { get; }
    }

    internal sealed class TianzhengInstallation
    {
        public TianzhengInstallation(string name, string id, string executable) { Name = name; Id = id; Executable = executable; }
        public string Name { get; }
        public string Id { get; }
        public string Executable { get; }
    }

    internal sealed class AcadInstallation
    {
        public AcadInstallation(string displayName, string release, string executable, string workingDirectory, string progId) { DisplayName = displayName; Release = release; Executable = executable; WorkingDirectory = workingDirectory; ProgId = progId; }
        public string DisplayName { get; }
        public string Release { get; }
        public string Executable { get; }
        public string WorkingDirectory { get; }
        public string ProgId { get; }
    }

    internal sealed class LauncherOptions
    {
        public PlatformOption Platform { get; set; }
        public bool LoadIntoRunningCad { get; set; }
        public bool InstallPermanently { get; set; }
    }

    internal sealed class PlatformPicker : Form
    {
        private static readonly Color Navy = Color.FromArgb(18, 52, 91);
        private static readonly Color Cyan = Color.FromArgb(24, 167, 201);
        private static readonly Color Canvas = Color.FromArgb(244, 247, 250);
        private static readonly Color Muted = Color.FromArgb(92, 108, 124);
        private readonly IList<PlatformOption> _platforms;
        private readonly ComboBox _tianzhengBox;
        private readonly ComboBox _cadBox;
        private readonly CheckBox _runningCad;
        private readonly CheckBox _permanentInstall;
        private readonly Button _uninstallButton;
        private readonly Button _startButton;
        public bool UninstallRequested { get; private set; }
        public LauncherOptions Options => new LauncherOptions
        {
            Platform = _platforms.FirstOrDefault(x => string.Equals(x.TianzhengName, _tianzhengBox.SelectedItem as string, StringComparison.OrdinalIgnoreCase) && string.Equals(x.CadName, _cadBox.SelectedItem as string, StringComparison.OrdinalIgnoreCase)),
            LoadIntoRunningCad = _runningCad.Checked,
            InstallPermanently = _permanentInstall.Checked
        };

        public PlatformPicker(IList<PlatformOption> platforms, string lastPlatform, bool hasRunningCad)
        {
            _platforms = platforms;
            Text = "万落建筑工具 · 启动器";
            Icon = LoadIcon();
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(660, 488); MinimumSize = new Size(600, 460);
            FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            BackColor = Canvas; Font = new Font("Microsoft YaHei UI", 9F);
            Padding = new Padding(0);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Color.White };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));

            var header = new Panel { Dock = DockStyle.Fill, BackColor = Navy, Padding = new Padding(28, 18, 24, 16) };
            var emblem = new PictureBox
            {
                Location = new Point(28, 18),
                Size = new Size(78, 78),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = LoadBrandImage(),
                BackColor = Color.Transparent
            };
            var title = new Label
            {
                Location = new Point(126, 24),
                AutoSize = true,
                Text = "万落建筑工具",
                Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold),
                ForeColor = Color.White
            };
            var subtitle = new Label
            {
                Location = new Point(129, 69),
                AutoSize = true,
                Text = "图纸发布、图框工具与建筑设计说明的一体化 CAD 插件",
                Font = new Font("Microsoft YaHei UI", 9.5F),
                ForeColor = Color.FromArgb(197, 222, 236)
            };
            var detected = new Label
            {
                AutoSize = false,
                Size = new Size(104, 28),
                Location = new Point(526, 25),
                TextAlign = ContentAlignment.MiddleCenter,
                Text = platforms.Count > 0 ? "已识别 " + platforms.Count + " 项" : "未识别平台",
                ForeColor = platforms.Count > 0 ? Color.FromArgb(211, 247, 255) : Color.FromArgb(255, 220, 214),
                BackColor = platforms.Count > 0 ? Color.FromArgb(27, 85, 118) : Color.FromArgb(112, 55, 55),
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold)
            };
            header.Controls.Add(emblem);
            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Controls.Add(detected);
            root.Controls.Add(header, 0, 0);

            var bodyHost = new Panel { Dock = DockStyle.Fill, BackColor = Canvas, Padding = new Padding(28, 24, 28, 18) };
            var card = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(24, 18, 24, 16),
                ColumnCount = 2,
                RowCount = 6,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var tzLabel = CreateFieldLabel("天正产品");
            _tianzhengBox = CreateComboBox();
            var cadLabel = CreateFieldLabel("AutoCAD 版本");
            _cadBox = CreateComboBox();
            foreach (var value in platforms.Select(x => x.TianzhengName).Distinct(StringComparer.OrdinalIgnoreCase)) _tianzhengBox.Items.Add(value);
            var last = platforms.FirstOrDefault(x => string.Equals(x.Id, lastPlatform, StringComparison.OrdinalIgnoreCase));
            _tianzhengBox.SelectedIndexChanged += (s, e) => RefreshCadOptions(last?.CadName);
            if (_tianzhengBox.Items.Count > 0) _tianzhengBox.SelectedItem = last?.TianzhengName ?? _tianzhengBox.Items[0];
            _runningCad = CreateOptionCheckBox(hasRunningCad ? "加载到当前已启动的 CAD" : "加载到当前已启动的 CAD（当前未检测到）", hasRunningCad, hasRunningCad);
            _permanentInstall = CreateOptionCheckBox("永久安装（不勾选则直接从当前目录便携运行）", false, true);
            card.Controls.Add(tzLabel, 0, 0); card.Controls.Add(_tianzhengBox, 1, 0);
            card.Controls.Add(cadLabel, 0, 1); card.Controls.Add(_cadBox, 1, 1);
            card.Controls.Add(_runningCad, 1, 2); card.Controls.Add(_permanentInstall, 1, 3);
            var separator = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(224, 231, 237), Margin = new Padding(0, 10, 0, 0) };
            card.Controls.Add(separator, 0, 4); card.SetColumnSpan(separator, 2);
            var tip = new Label
            {
                Dock = DockStyle.Fill,
                Text = platforms.Count > 0
                    ? "已按本机实际安装目录识别平台。天正名称会完整显示 T20/T30、专业及版本；AutoCAD 显示产品年份。"
                    : "没有识别到可用的 AutoCAD。请确认 AutoCAD 已正确安装，或以管理员身份修复其注册信息。",
                ForeColor = platforms.Count > 0 ? Muted : Color.FromArgb(181, 66, 49),
                AutoSize = false,
                Padding = new Padding(0, 8, 0, 0)
            };
            card.Controls.Add(tip, 0, 5); card.SetColumnSpan(tip, 2);
            bodyHost.Controls.Add(card);
            root.Controls.Add(bodyHost, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 14, 28, 14), ColumnCount = 5, BackColor = Color.White };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _startButton = CreateButton("启动并加载", 124, Cyan, Color.White);
            _startButton.DialogResult = DialogResult.OK;
            _startButton.Enabled = platforms.Count > 0;
            var browseButton = CreateButton("手动选择程序", 112, Color.White, Navy);
            browseButton.Margin = new Padding(10, 0, 0, 0);
            browseButton.Click += (s, e) => AddManualProgram();
            var cancelButton = CreateButton("取消", 88, Color.White, Navy);
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.Margin = new Padding(10, 0, 0, 0);
            _uninstallButton = CreateButton("卸载插件", 102, Color.White, Color.FromArgb(157, 66, 61));
            _uninstallButton.Click += (s, e) => { UninstallRequested = true; DialogResult = DialogResult.OK; };
            footer.Controls.Add(_uninstallButton, 0, 0); footer.Controls.Add(browseButton, 1, 0); footer.Controls.Add(_startButton, 3, 0); footer.Controls.Add(cancelButton, 4, 0); root.Controls.Add(footer, 0, 2); Controls.Add(root);

            var toolTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 350, ReshowDelay = 150, ShowAlways = true };
            toolTip.SetToolTip(_tianzhengBox, "选择要启动的天正专业和版本；不使用天正时选择“无天正”。");
            toolTip.SetToolTip(_cadBox, "选择插件要加载到的 AutoCAD 产品年份。");
            toolTip.SetToolTip(_runningCad, "将插件直接载入已经打开的本机 AutoCAD，不再启动新的 CAD 进程。");
            toolTip.SetToolTip(_permanentInstall, "默认不安装程序文件，直接从启动器所在目录加载；勾选后写入 Autodesk ApplicationPlugins，以后启动 CAD 时自动加载。");
            toolTip.SetToolTip(_uninstallButton, "删除本插件的自动加载配置和安装副本，不会删除工程文件或 DWG 图纸。");
            toolTip.SetToolTip(browseButton, "自动识别失败时，手动选择 acad.exe 或天正启动程序；安装盘符和目录不受限制。");
            AcceptButton = _startButton; CancelButton = cancelButton;
        }

        private void AddManualProgram()
        {
            using (var dialog = new OpenFileDialog { Filter = "CAD/天正启动程序 (*.exe)|*.exe", Title = "选择 acad.exe 或天正启动程序" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var executable = dialog.FileName;
                if (string.Equals(Path.GetFileName(executable), "acad.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var version = FileVersionInfo.GetVersionInfo(executable);
                    var release = version.FileMajorPart + "." + version.FileMinorPart;
                    var cadName = AutoCadNameFromRelease(release);
                    if (cadName == null)
                    {
                        MessageBox.Show(this, "无法从所选 acad.exe 判断受支持的 AutoCAD 2021–2026 版本。", "手动选择程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    AddManualCad(executable, release, cadName);
                    _tianzhengBox.SelectedItem = "无天正";
                    RefreshCadOptions(cadName);
                }
                else
                {
                    var tianzheng = Program.CreateTianzhengInstallation(executable);
                    if (tianzheng == null)
                    {
                        MessageBox.Show(this, "所选文件不像可识别的 T20/T30 天正启动程序。", "手动选择程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    AddManualTianzheng(tianzheng);
                    if (!_tianzhengBox.Items.Cast<object>().Any(x => string.Equals(x as string, tianzheng.Name, StringComparison.OrdinalIgnoreCase)))
                        _tianzhengBox.Items.Add(tianzheng.Name);
                    _tianzhengBox.SelectedItem = tianzheng.Name;
                }
                _startButton.Enabled = _platforms.Count > 0;
            }
        }

        private void AddManualCad(string executable, string release, string cadName)
        {
            var progId = "AutoCAD.Application." + release;
            var directory = Path.GetDirectoryName(executable);
            var cadOptions = _platforms.Where(x => string.Equals(x.TianzhengName, "无天正", StringComparison.OrdinalIgnoreCase)).ToList();
            if (!cadOptions.Any(x => string.Equals(x.Executable, executable, StringComparison.OrdinalIgnoreCase)))
                _platforms.Add(new PlatformOption(cadName, "acad-" + release, executable, "/nologo /p \"<<Unnamed Profile>>\"", directory, progId, "无天正", cadName, release));
            foreach (var tianzheng in _platforms.Where(x => !string.Equals(x.TianzhengName, "无天正", StringComparison.OrdinalIgnoreCase)).GroupBy(x => x.TianzhengName, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList())
                if (!_platforms.Any(x => string.Equals(x.TianzhengName, tianzheng.TianzhengName, StringComparison.OrdinalIgnoreCase) && string.Equals(x.CadName, cadName, StringComparison.OrdinalIgnoreCase)))
                    _platforms.Add(new PlatformOption(tianzheng.TianzhengName + " + " + cadName, tianzheng.Id + "-acad-" + release, tianzheng.Executable, "", Path.GetDirectoryName(tianzheng.Executable), progId, tianzheng.TianzhengName, cadName, release));
            if (!_tianzhengBox.Items.Cast<object>().Any(x => string.Equals(x as string, "无天正", StringComparison.OrdinalIgnoreCase))) _tianzhengBox.Items.Insert(0, "无天正");
        }

        private void AddManualTianzheng(TianzhengInstallation tianzheng)
        {
            foreach (var cad in _platforms.Where(x => string.Equals(x.TianzhengName, "无天正", StringComparison.OrdinalIgnoreCase)).ToList())
                if (!_platforms.Any(x => string.Equals(x.TianzhengName, tianzheng.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(x.CadName, cad.CadName, StringComparison.OrdinalIgnoreCase)))
                    _platforms.Add(new PlatformOption(tianzheng.Name + " + " + cad.CadName, tianzheng.Id + "-" + cad.Id, tianzheng.Executable, "", Path.GetDirectoryName(tianzheng.Executable), cad.ProgId, tianzheng.Name, cad.CadName, cad.Release));
        }

        private static string AutoCadNameFromRelease(string release)
        {
            var years = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["24.0"] = 2021, ["24.1"] = 2022, ["24.2"] = 2023,
                ["24.3"] = 2024, ["25.0"] = 2025, ["25.1"] = 2026
            };
            int year;
            return years.TryGetValue(release, out year) ? "AutoCAD " + year : null;
        }

        private static Label CreateFieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = Navy,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold)
            };
        }

        private static ComboBox CreateComboBox()
        {
            return new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.System,
                Font = new Font("Microsoft YaHei UI", 10F),
                Margin = new Padding(0, 10, 0, 9)
            };
        }

        private static CheckBox CreateOptionCheckBox(string text, bool isChecked, bool enabled)
        {
            return new CheckBox
            {
                Dock = DockStyle.Fill,
                Text = text,
                Checked = isChecked,
                Enabled = enabled,
                AutoSize = true,
                ForeColor = enabled ? Color.FromArgb(47, 63, 78) : Color.FromArgb(145, 154, 163),
                Padding = new Padding(0, 3, 0, 0)
            };
        }

        private static Button CreateButton(string text, int width, Color backColor, Color foreColor)
        {
            var button = new Button
            {
                Width = width,
                Height = 40,
                Text = text,
                BackColor = backColor,
                ForeColor = foreColor,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = backColor == Color.White ? Color.FromArgb(202, 213, 222) : backColor;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = backColor == Color.White ? Color.FromArgb(244, 247, 250) : Color.FromArgb(19, 143, 174);
            return button;
        }

        private void RefreshCadOptions(string preferredCad)
        {
            var tianzheng = _tianzhengBox.SelectedItem as string;
            var options = _platforms.Where(x => string.Equals(x.TianzhengName, tianzheng, StringComparison.OrdinalIgnoreCase)).Select(x => x.CadName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _cadBox.BeginUpdate(); _cadBox.Items.Clear(); foreach (var value in options) _cadBox.Items.Add(value); _cadBox.EndUpdate();
            if (_cadBox.Items.Count > 0) _cadBox.SelectedItem = options.FirstOrDefault(x => string.Equals(x, preferredCad, StringComparison.OrdinalIgnoreCase)) ?? _cadBox.Items[0];
        }

        private static Icon LoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return SystemIcons.Application; }
        }

        private static Image LoadBrandImage()
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BatchPdfPublisherLauncher.BatchPdfPublisherIcon.png"))
                {
                    if (stream == null) return LoadIcon().ToBitmap();
                    using (var source = Image.FromStream(stream)) return new Bitmap(source);
                }
            }
            catch { return LoadIcon().ToBitmap(); }
        }
    }
}
