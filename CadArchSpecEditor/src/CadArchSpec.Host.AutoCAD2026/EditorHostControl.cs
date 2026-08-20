using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using CadArchSpec.EditorBridge;
using CadArchSpec.Host.Contracts;
using CadArchSpec.Host.Shared;
using CadArchSpec.RuleEngine;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.Host.AutoCAD2026
{
    internal sealed class EditorHostControl : UserControl
    {
        private const string VirtualHostName = "cadarchspec.local";
        private readonly Label _statusLabel;
        private readonly WebView2 _webView;
        private readonly JsonModelSerializer _serializer = new JsonModelSerializer();
        private readonly ProjectFileService _projectFiles = new ProjectFileService();
        private static bool _nativeResolverConfigured;
        private bool _initializationStarted;
        private bool _disposed;
        private string _currentProjectPath = string.Empty;

        public EditorHostControl()
        {
            ConfigureNativeDependencyResolution();
            BackColor = Color.White;

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Visible = false
            };
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(247, 248, 250),
                ForeColor = Color.FromArgb(55, 65, 81),
                Font = new Font("Microsoft YaHei UI", 10F),
                Text = "正在准备建筑设计说明助手…",
                TextAlign = ContentAlignment.MiddleCenter
            };

            Controls.Add(_webView);
            Controls.Add(_statusLabel);
            Load += OnLoaded;
        }

        private static void ConfigureNativeDependencyResolution()
        {
            if (_nativeResolverConfigured)
            {
                return;
            }

            var webViewAssembly = typeof(CoreWebView2Environment).Assembly;
            NativeLibrary.SetDllImportResolver(webViewAssembly, ResolveWebView2NativeLibrary);
            _nativeResolverConfigured = true;
        }

        private static IntPtr ResolveWebView2NativeLibrary(
            string libraryName,
            Assembly assembly,
            DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, "WebView2Loader.dll", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(libraryName, "WebView2Loader", StringComparison.OrdinalIgnoreCase))
            {
                return IntPtr.Zero;
            }

            var hostDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrWhiteSpace(hostDirectory))
            {
                return IntPtr.Zero;
            }

            var loaderPath = Path.Combine(
                hostDirectory,
                "runtimes",
                Environment.Is64BitProcess ? "win-x64" : "win-x86",
                "native",
                "WebView2Loader.dll");
            return File.Exists(loaderPath)
                ? NativeLibrary.Load(loaderPath)
                : IntPtr.Zero;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                Load -= OnLoaded;
                if (disposing)
                {
                    _webView.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private async void OnLoaded(object sender, EventArgs e)
        {
            if (_initializationStarted || _disposed)
            {
                return;
            }

            _initializationStarted = true;
            try
            {
                await InitializeWebViewAsync();
            }
            catch (Exception exception)
            {
                ShowFailure(exception);
            }
        }

        private async Task InitializeWebViewAsync()
        {
            var webAssetsPath = WebAssetLocator.Find(Assembly.GetExecutingAssembly().Location);
            var userDataPath = Path.Combine(PortableDataPaths.DirectoryFor("WebView2"), "AutoCAD2026");
            Directory.CreateDirectory(userDataPath);

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataPath);
            await _webView.EnsureCoreWebView2Async(environment);
            if (_disposed || _webView.CoreWebView2 == null)
            {
                return;
            }

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                webAssetsPath,
                CoreWebView2HostResourceAccessKind.DenyCors);
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.ProcessFailed += OnWebViewProcessFailed;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.Navigate("https://" + VirtualHostName + "/index.html");
            _statusLabel.Visible = false;
            _webView.Visible = true;
            _webView.BringToFront();
        }

        private void OnWebViewProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            BeginInvoke(new Action(() =>
                ShowFailure(new InvalidOperationException("WebView2 进程异常：" + e.ProcessFailedKind))));
        }

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = _serializer.Deserialize<EditorMessage>(e.WebMessageAsJson);
                switch (message.Type)
                {
                    case "editor.ready":
                        SendHostReady();
                        break;
                    case "project.new":
                        _currentProjectPath = string.Empty;
                        PostMessage("project.newed", CreateProjectStatePayload());
                        break;
                    case "project.open":
                        OpenProject();
                        break;
                    case "project.openRecent":
                        LoadProject((string)message.Payload["filePath"]);
                        break;
                    case "project.save":
                        SaveProject(message.Payload, false);
                        break;
                    case "project.saveAs":
                        SaveProject(message.Payload, true);
                        break;
                    case "project.historyList":
                        SendProjectHistory();
                        break;
                    case "project.historyLoad":
                        LoadProjectSnapshot((string)message.Payload["snapshotPath"]);
                        break;
                    case "project.historyRestore":
                        RestoreProjectSnapshot((string)message.Payload["snapshotPath"]);
                        break;
                    case "review.run":
                        RunNationalFoundationReview(message.Payload);
                        break;
                    case "cad.frame.pick":
                        PostMessage("cad.framePicked", await CadDrawingExchange.PickFrameAndTextAreaAsync());
                        break;
                    case "cad.text.read":
                        PostMessage("cad.textRead", await CadDrawingExchange.ReadSelectedTextAsync((string)message.Payload["sectionId"]));
                        break;
                    case "cad.section.insert":
                        PostMessage("cad.sectionInserted", await CadDrawingExchange.InsertSectionAsync(message.Payload));
                        break;
                }
            }
            catch (Exception exception)
            {
                TryWriteDiagnostic(exception);
                PostMessage("project.error", new JObject
                {
                    ["message"] = GetFriendlyError(exception)
                });
            }
        }

        private void SendHostReady()
        {
            var runtimeInfo = JObject.FromObject(new HostRuntimeInfo
            {
                ProductName = "AutoCAD",
                ProductVersion = Autodesk.AutoCAD.ApplicationServices.Application.Version.ToString(),
                RuntimeVersion = RuntimeEnvironment.GetSystemVersion(),
                WebView2Version = CoreWebView2Environment.GetAvailableBrowserVersionString(),
                WebAssetsPath = WebAssetLocator.Find(Assembly.GetExecutingAssembly().Location)
            });
            runtimeInfo["currentProjectPath"] = _currentProjectPath;
            runtimeInfo["recentProjects"] = JArray.FromObject(_projectFiles.GetRecentProjects());
            PostMessage("host.ready", runtimeInfo);
        }

        private void OpenProject()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "打开建筑设计说明项目",
                Filter = "建筑设计说明项目 (*.jzsmproj)|*.jzsmproj|JSON 文件 (*.json)|*.json",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    PostMessage("project.cancelled", new JObject { ["operation"] = "open" });
                    return;
                }
                LoadProject(dialog.FileName);
            }
        }

        private void LoadProject(string filePath)
        {
            var result = _projectFiles.Load(filePath);
            _currentProjectPath = result.FilePath;
            PostMessage("project.loaded", new JObject
            {
                ["filePath"] = result.FilePath,
                ["workspace"] = result.Workspace,
                ["recentProjects"] = JArray.FromObject(result.RecentProjects)
            });
        }

        private void SaveProject(JObject payload, bool forceSaveAs)
        {
            var workspace = payload["workspace"] as JObject;
            if (workspace == null)
            {
                throw new InvalidDataException("编辑器没有提交可保存的项目数据。");
            }

            var targetPath = _currentProjectPath;
            if (forceSaveAs || string.IsNullOrWhiteSpace(targetPath))
            {
                using (var dialog = new SaveFileDialog
                {
                    Title = forceSaveAs ? "建筑设计说明项目另存为" : "保存建筑设计说明项目",
                    Filter = "建筑设计说明项目 (*.jzsmproj)|*.jzsmproj",
                    AddExtension = true,
                    DefaultExt = ProjectFileService.ProjectExtension.TrimStart('.'),
                    FileName = MakeSafeFileName((string)workspace["projectName"])
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                    {
                        PostMessage("project.cancelled", new JObject { ["operation"] = "save" });
                        return;
                    }
                    targetPath = dialog.FileName;
                }
            }

            var result = _projectFiles.Save(
                targetPath,
                workspace,
                (bool?)payload["createSnapshot"] == true);
            _currentProjectPath = result.FilePath;
            PostMessage("project.saved", new JObject
            {
                ["filePath"] = result.FilePath,
                ["savedAt"] = result.SavedAt,
                ["snapshotPath"] = result.SnapshotPath,
                ["recentProjects"] = JArray.FromObject(result.RecentProjects)
            });
        }

        private void SendProjectHistory()
        {
            var snapshots = _projectFiles.GetSnapshots(_currentProjectPath);
            PostMessage("project.historyListed", new JObject
            {
                ["filePath"] = _currentProjectPath,
                ["snapshots"] = JArray.FromObject(snapshots)
            });
        }

        private void LoadProjectSnapshot(string snapshotPath)
        {
            var result = _projectFiles.LoadSnapshot(_currentProjectPath, snapshotPath);
            PostMessage("project.historyLoaded", new JObject
            {
                ["snapshotPath"] = result.FilePath,
                ["workspace"] = result.Workspace
            });
        }

        private void RestoreProjectSnapshot(string snapshotPath)
        {
            var result = _projectFiles.RestoreSnapshot(_currentProjectPath, snapshotPath);
            PostMessage("project.historyRestored", new JObject
            {
                ["filePath"] = result.FilePath,
                ["workspace"] = result.Workspace,
                ["safetySnapshotPath"] = result.SafetySnapshotPath,
                ["snapshots"] = JArray.FromObject(result.Snapshots)
            });
        }

        private void RunNationalFoundationReview(JObject payload)
        {
            var workspace = payload["workspace"] as JObject;
            if (workspace == null)
            {
                throw new InvalidDataException("编辑器没有提交可检查的项目数据。");
            }
            var hostDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            var packagePath = Path.Combine(hostDirectory, "Rules", "CN", "common", "package.json");
            var package = new RulePackageLoader().LoadFile(packagePath);
            var result = new WorkspaceRuleEvaluator().Evaluate(package, workspace);
            PostMessage("review.result", JObject.Parse(_serializer.Serialize(result)));
        }

        private JObject CreateProjectStatePayload()
        {
            return new JObject
            {
                ["currentProjectPath"] = _currentProjectPath,
                ["recentProjects"] = JArray.FromObject(_projectFiles.GetRecentProjects())
            };
        }

        private void PostMessage(string type, JObject payload)
        {
            if (_webView.CoreWebView2 == null)
            {
                return;
            }
            _webView.CoreWebView2.PostWebMessageAsJson(_serializer.Serialize(new EditorMessage
            {
                Type = type,
                Payload = payload ?? new JObject()
            }));
        }

        private static string MakeSafeFileName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "建筑设计说明项目" : value.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }
            return name + ProjectFileService.ProjectExtension;
        }

        private void ShowFailure(Exception exception)
        {
            if (_disposed)
            {
                return;
            }

            _webView.Visible = false;
            _statusLabel.Visible = true;
            _statusLabel.BringToFront();
            _statusLabel.Text =
                "编辑器网页组件未能启动，AutoCAD 本身可以继续使用。\r\n\r\n" +
                GetFriendlyError(exception) +
                "\r\n\r\n可关闭面板后检查 WebView2 Runtime 和 Web 静态资源。";
            TryWriteDiagnostic(exception);
        }

        private static string GetFriendlyError(Exception exception)
        {
            if (exception is FileNotFoundException)
            {
                return exception.Message;
            }

            if (exception is WebView2RuntimeNotFoundException)
            {
                return "未检测到 Microsoft Edge WebView2 Runtime。";
            }

            return exception.GetType().Name + "：" + exception.Message;
        }

        private static void TryWriteDiagnostic(Exception exception)
        {
            try
            {
                var logDirectory = PortableDataPaths.DirectoryFor("Logs");
                Directory.CreateDirectory(logDirectory);
                var logPath = Path.Combine(logDirectory, "host-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                File.AppendAllText(
                    logPath,
                    string.Format(
                        "{0:O} | AutoCAD {1} | CLR {2} | WebView2 {3}{4}{5}{4}",
                        DateTime.Now,
                        Autodesk.AutoCAD.ApplicationServices.Application.Version,
                        RuntimeEnvironment.GetSystemVersion(),
                        CoreWebView2Environment.GetAvailableBrowserVersionString(),
                        Environment.NewLine,
                        exception));
            }
            catch
            {
                // 诊断写入失败不能继续影响 AutoCAD 宿主。
            }
        }
    }
}
