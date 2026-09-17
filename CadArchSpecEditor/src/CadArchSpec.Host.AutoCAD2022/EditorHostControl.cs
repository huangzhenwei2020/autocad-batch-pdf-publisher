using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using CadArchSpec.EditorBridge;
using CadArchSpec.Host.Contracts;
using CadArchSpec.Host.Shared;
using CadArchSpec.RuleEngine;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.Host.AutoCAD2022
{
    internal sealed class EditorHostControl : UserControl
    {
        private const string VirtualHostName = "cadarchspec.local";
        private static readonly object WebViewEnvironmentSync = new object();
        private static IntPtr _webViewLoaderHandle;
        private static Task<CoreWebView2Environment> _webViewEnvironmentTask;
        private readonly Label _statusLabel;
        private readonly WebView2 _webView;
        private readonly JsonModelSerializer _serializer = new JsonModelSerializer();
        private readonly ProjectFileService _projectFiles = new ProjectFileService();
        private bool _initializationStarted;
        private bool _disposed;
        private bool _webReady;
        private bool _waitingForCadTableVisible;
        private bool _deferredStandalonePayloadLoading;
        private System.Windows.Forms.Timer _cadTableRevealFallback;
        private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
        private string _startupSessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
        private int _cadTableRequestVersion;
        private JObject _pendingCadTablePayload;
        private string _pendingCadTableError;
        private string _currentProjectPath = string.Empty;
        private CancellationTokenSource _imageTableCancellation;

        public EditorHostControl()
        {
            EnsureWebViewLoaderLoaded();
            BackColor = Color.White;
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                // Keep the browser compositor active behind the status label. A hidden
                // WebView may throttle JavaScript/timers and made the CE ready handshake
                // routinely fall through to its two-second fallback.
                Visible = true
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

        public void StartCadTableEdit(JObject payload)
        {
            _startupSessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            _startupStopwatch.Restart();
            _cadTableRequestVersion++;
            _deferredStandalonePayloadLoading = false;
            _pendingCadTableError = null;
            _pendingCadTablePayload = payload == null ? null : (JObject)payload.DeepClone();
            if (_pendingCadTablePayload != null) _pendingCadTablePayload["standaloneEditor"] = true;
            _waitingForCadTableVisible = _pendingCadTablePayload != null;
            WriteStartupDiagnostic("CE payload queued");
            if (_waitingForCadTableVisible)
            {
                _statusLabel.Text = "正在准备 CAD 表格编辑/Excel…";
                _statusLabel.Visible = true;
                _statusLabel.BringToFront();
            }
            if (IsDeferredStandalonePayload(_pendingCadTablePayload))
            {
                QueueDeferredCadTablePayloadPreparation();
            }
            else if (_webReady)
            {
                ShowPendingCadTable();
            }
        }

        private void RevealWebView()
        {
            _waitingForCadTableVisible = false;
            _cadTableRevealFallback?.Stop();
            _statusLabel.Visible = false;
            _webView.Visible = true;
            _webView.BringToFront();
            WriteStartupDiagnostic("CE WebView revealed");
        }

        private void ShowPendingCadTable()
        {
            if (_pendingCadTablePayload == null || !_webReady) return;
            var payload = _pendingCadTablePayload;
            if (IsDeferredStandalonePayload(payload))
            {
                TryStartDeferredCadTablePayloadPreparation();
                return;
            }
            _pendingCadTablePayload = null;
            WriteStartupDiagnostic("Posting cad.tableRead payload");
            PostMessage("cad.tableRead", payload);
            WriteStartupDiagnostic("cad.tableRead payload posted");
            // The CE page renders its own lightweight loading state while it
            // receives the payload; do not wait for a second visibility handshake.
            RevealWebView();
        }

        private static bool IsDeferredStandalonePayload(JObject payload)
        {
            return (bool?)payload?["deferredStandalonePayload"] == true;
        }

        private void QueueDeferredCadTablePayloadPreparation()
        {
            if (!_initializationStarted || !IsHandleCreated || _disposed) return;
            BeginInvoke(new Action(TryStartDeferredCadTablePayloadPreparation));
        }

        private void TryStartDeferredCadTablePayloadPreparation()
        {
            if (_disposed || _deferredStandalonePayloadLoading ||
                !IsDeferredStandalonePayload(_pendingCadTablePayload)) return;
            _pendingCadTablePayload = null;
            _deferredStandalonePayloadLoading = true;
            var requestVersion = _cadTableRequestVersion;
            WriteStartupDiagnostic("Deferred CE payload preparation started");
            PrepareDeferredCadTablePayloadAsync(requestVersion);
        }

        private async void PrepareDeferredCadTablePayloadAsync(int requestVersion)
        {
            try
            {
                var payload = await CadArchSpec.Host.Shared.CadTable.CadTableExchange.CreateStandaloneEditorPayloadAsync();
                if (_disposed || requestVersion != _cadTableRequestVersion) return;
                _deferredStandalonePayloadLoading = false;
                _pendingCadTablePayload = payload;
                WriteStartupDiagnostic("Deferred CE payload preparation completed");
                if (_webReady) ShowPendingCadTable();
            }
            catch (Exception exception)
            {
                if (_disposed || requestVersion != _cadTableRequestVersion) return;
                _deferredStandalonePayloadLoading = false;
                WriteStartupDiagnostic("Deferred CE payload preparation failed");
                _pendingCadTableError = "CAD 表格数据准备失败：" + exception.GetBaseException().Message;
                if (_webReady) PostPendingCadTableError();
            }
        }

        private void PostPendingCadTableError()
        {
            if (string.IsNullOrWhiteSpace(_pendingCadTableError) || !_webReady) return;
            var message = _pendingCadTableError;
            _pendingCadTableError = null;
            PostMessage("project.error", new JObject { ["message"] = message });
        }

        private void ArmCadTableRevealFallback()
        {
            _cadTableRevealFallback?.Stop();
            _cadTableRevealFallback?.Dispose();
            _cadTableRevealFallback = new System.Windows.Forms.Timer { Interval = 2000 };
            _cadTableRevealFallback.Tick += (sender, args) =>
            {
                _cadTableRevealFallback.Stop();
                if (_waitingForCadTableVisible) RevealWebView();
            };
            _cadTableRevealFallback.Start();
        }

        internal static void WarmUpWebViewEnvironment()
        {
            try
            {
                EnsureWebViewLoaderLoaded();
                var warmUpTask = GetOrCreateWebViewEnvironmentAsync();
                warmUpTask.ContinueWith(
                    task => TryWriteStartupDiagnostic("prewarm", 0,
                        "WebView2 environment prewarm failed", task.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
            catch (Exception exception)
            {
                TryWriteStartupDiagnostic("prewarm", 0,
                    "WebView2 environment prewarm failed", exception);
            }
        }

        private static Task<CoreWebView2Environment> GetOrCreateWebViewEnvironmentAsync()
        {
            lock (WebViewEnvironmentSync)
            {
                if (_webViewEnvironmentTask == null ||
                    _webViewEnvironmentTask.IsCanceled ||
                    _webViewEnvironmentTask.IsFaulted)
                {
                    var userDataPath = Path.Combine(
                        PortableDataPaths.DirectoryFor("WebView2"), "AutoCAD2022");
                    Directory.CreateDirectory(userDataPath);
                    _webViewEnvironmentTask = CoreWebView2Environment.CreateAsync(null, userDataPath);
                }

                return _webViewEnvironmentTask;
            }
        }

        private static void EnsureWebViewLoaderLoaded()
        {
            if (_webViewLoaderHandle != IntPtr.Zero)
            {
                return;
            }

            var hostDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrWhiteSpace(hostDirectory))
            {
                throw new FileNotFoundException("无法确定建筑设计说明助手的插件目录。");
            }

            var loaderPath = Path.Combine(
                hostDirectory,
                "runtimes",
                Environment.Is64BitProcess ? "win-x64" : "win-x86",
                "native",
                "WebView2Loader.dll");
            if (!File.Exists(loaderPath))
            {
                throw new FileNotFoundException("找不到 WebView2Loader.dll。", loaderPath);
            }

            _webViewLoaderHandle = LoadLibrary(loaderPath);
            if (_webViewLoaderHandle == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法加载 WebView2Loader.dll。");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                Load -= OnLoaded;
                if (disposing)
                {
                    _imageTableCancellation?.Cancel();
                    _imageTableCancellation?.Dispose();
                    _cadTableRevealFallback?.Stop();
                    _cadTableRevealFallback?.Dispose();
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
            WriteStartupDiagnostic("Host control loaded; WebView2 initialization started");
            try
            {
                TryStartDeferredCadTablePayloadPreparation();
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
            WriteStartupDiagnostic("Web assets resolved");

            var environment = await GetOrCreateWebViewEnvironmentAsync();
            WriteStartupDiagnostic("WebView2 environment ready");
            await _webView.EnsureCoreWebView2Async(environment);
            WriteStartupDiagnostic("WebView2 controller ready");
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
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.Navigate("https://" + VirtualHostName + "/index.html" +
                (_waitingForCadTableVisible ? "?mode=cad-table" : string.Empty));
            WriteStartupDiagnostic("CE page navigation requested");
            // Reveal as soon as navigation is requested. The dedicated CE page
            // owns its loading screen and can initialize while the document loads.
            RevealWebView();
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            WriteStartupDiagnostic(e.IsSuccess
                ? "CE page navigation completed"
                : "CE page navigation failed: " + e.WebErrorStatus);
            // Navigation completion is diagnostic only; the page was revealed as
            // soon as navigation started so startup never waits for this callback.
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
                        WriteStartupDiagnostic("editor.ready received");
                        _webReady = true;
                        SendHostReady();
                        PostPendingCadTableError();
                        ShowPendingCadTable();
                        break;
                    case "cad.table.visible":
                        WriteStartupDiagnostic("cad.table.visible received");
                        RevealWebView();
                        break;
                    case "cad.table.window.close":
                        HostPalette.CloseCadTableWindowFromHost();
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
                    case "cad.table.read":
                        PostMessage("cad.tableRead", await CadArchSpec.Host.Shared.CadTable.CadTableExchange.ReadSelectedTableAsync(
                            (bool?)message.Payload["includeHiddenLayers"] == true));
                        break;
                    case "cad.table.insert":
                        PostMessage("cad.tableInserted", await CadArchSpec.Host.Shared.CadTable.CadTableExchange.InsertTableAsync(message.Payload));
                        break;
                    case "cad.table.repick":
                        PostMessage("cad.tableRead", CadArchSpec.Host.Shared.CadTable.CadTableExchange.PrepareStandaloneEditorPayload(
                            await CadArchSpec.Host.Shared.CadTable.CadTableExchange.ReadSelectedTableForUpdateAsync(false)));
                        break;
                    case "cad.table.pick":
                        PostMessage("cad.tableRead", CadArchSpec.Host.Shared.CadTable.CadTableExchange.PrepareStandaloneEditorPayload(
                            await CadArchSpec.Host.Shared.CadTable.CadTableExchange.ReadSelectedTableAsync(false)));
                        break;
                    case "cad.table.cellObjects.pick":
                        PostMessage("cad.tableRead", await CadArchSpec.Host.Shared.CadTable.CadTableExchange.CaptureCellCadObjectsAsync(message.Payload));
                        break;
                    case "cad.table.template.save":
                        PostMessage("cad.table.templatesChanged", CadArchSpec.Host.Shared.CadTable.CadTableExchange.SaveTemplateForEditor(message.Payload));
                        break;
                    case "cad.table.template.delete":
                        PostMessage("cad.table.templatesChanged", CadArchSpec.Host.Shared.CadTable.CadTableExchange.DeleteTemplateForEditor(message.Payload));
                        break;
                    case "image.table.read":
                        _imageTableCancellation?.Cancel();
                        _imageTableCancellation?.Dispose();
                        _imageTableCancellation = new CancellationTokenSource();
                        try
                        {
                            PostMessage("image.tableRead", await CadArchSpec.Host.Shared.CadTable.ImageTableExchange.ReadImageTableAsync(this, _imageTableCancellation.Token));
                        }
                        catch (OperationCanceledException)
                        {
                            PostMessage("image.tableRead", new JObject { ["cancelled"] = true });
                        }
                        finally
                        {
                            _imageTableCancellation.Dispose();
                            _imageTableCancellation = null;
                        }
                        break;
                    case "image.table.cancel":
                        _imageTableCancellation?.Cancel();
                        break;
                    case "cad.table.locate":
                        PostMessage("cad.tableLocated", await CadArchSpec.Host.Shared.CadTable.CadTableExchange.LocateSourcesAsync(message.Payload));
                        break;
                    case "table.xlsx.export":
                        PostMessage("table.xlsxExported", CadArchSpec.Host.Shared.CadTable.CadTableXlsxExchange.Export(message.Payload, this));
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
                RuntimeVersion = Environment.Version.ToString(),
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

        private void WriteStartupDiagnostic(string phase)
        {
            TryWriteStartupDiagnostic(_startupSessionId, _startupStopwatch.ElapsedMilliseconds,
                phase, null);
        }

        private static void TryWriteStartupDiagnostic(
            string sessionId, long elapsedMilliseconds, string phase, Exception exception)
        {
            try
            {
                var logDirectory = PortableDataPaths.DirectoryFor("Logs");
                var logPath = Path.Combine(logDirectory,
                    "ce-startup-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                lock (WebViewEnvironmentSync)
                {
                    File.AppendAllText(logPath,
                        DateTime.Now.ToString("O") + " | " + sessionId + " | +" +
                        elapsedMilliseconds + " ms | " + phase +
                        (exception == null ? string.Empty : Environment.NewLine + exception) +
                        Environment.NewLine);
                }
            }
            catch
            {
                // Startup diagnostics must never delay or prevent the editor opening.
            }
        }
    }
}
