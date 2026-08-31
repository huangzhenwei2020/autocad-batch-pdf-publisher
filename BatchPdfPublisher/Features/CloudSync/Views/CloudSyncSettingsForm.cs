using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class CloudSyncSettingsForm : Form
    {
        private readonly CheckBox _enabled = new CheckBox { Text = "启用云同步", AutoSize = true };
        private readonly ComboBox _provider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly TextBox _folder = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _clientId = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _clientSecret = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly TextBox _redirectUri = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _remoteFolder = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _device = new TextBox { Dock = DockStyle.Fill };
        private readonly CheckBox _general = new CheckBox { Text = "通用设置", AutoSize = true };
        private readonly CheckBox _projects = new CheckBox { Text = "项目配置", AutoSize = true };
        private readonly CheckBox _templates = new CheckBox { Text = "图框与方案库", AutoSize = true };
        private readonly CheckBox _drawings = new CheckBox { Text = "项目文件与 DWG（仅登记的工程目录）", AutoSize = true };
        private readonly CheckBox _auto = new CheckBox { Text = "保存或检测到变化后自动同步", AutoSize = true };
        private readonly ComboBox _initialPreference = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
        private readonly NumericUpDown _days = new NumericUpDown { Minimum = 1, Maximum = 3650, Value = 30, Width = 90 };
        private readonly NumericUpDown _versions = new NumericUpDown { Minimum = 1, Maximum = 200, Value = 20, Width = 90 };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = Color.FromArgb(34, 98, 60), Padding = new Padding(0, 8, 0, 0) };
        private readonly ProgressBar _progress = new ProgressBar { Dock = DockStyle.Top, Height = 18, Visible = false, Style = ProgressBarStyle.Marquee };
        private readonly ListBox _details = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        private readonly Button _syncNow;
        private readonly Button _close;
        private CloudSyncSettings _settings;
        private CancellationTokenSource _syncCancellation;
        private CloudSyncProgress _latestProgress;
        private int _progressVersion;
        private int _progressUpdateQueued;
        private int _providerStatusVersion;

        public CloudSyncSettingsForm()
        {
            Text = "万落建筑工具 · 云同步";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 620);
            Size = new Size(840, 680);
            Font = new Font("Microsoft YaHei UI", 9F);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 7, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var title = new Label { Text = "云同步（V5：插件直连云盘）", Font = new Font(Font, FontStyle.Bold), AutoSize = true };
            root.Controls.Add(title);
            var description = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(780, 0),
                ForeColor = Color.DimGray,
                Text = "百度网盘直连由插件通过官方授权和文件接口上传、下载，无需安装网盘客户端。通用同步文件夹继续作为兼容模式；版本、冲突、历史和打开中 DWG 的保护规则保持不变。"
            };
            root.Controls.Add(description);

            var settingsPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 12, 0, 8) };
            settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settingsPanel.Controls.Add(_enabled, 0, 0); settingsPanel.SetColumnSpan(_enabled, 3);
            settingsPanel.Controls.Add(LabelFor("存储提供商"), 0, 1); settingsPanel.Controls.Add(_provider, 1, 1);
            var portal = ButtonFor("开放平台"); portal.Click += OpenProviderPortal; settingsPanel.Controls.Add(portal, 2, 1);
            settingsPanel.Controls.Add(LabelFor("云盘同步文件夹"), 0, 2); settingsPanel.Controls.Add(_folder, 1, 2);
            var folderActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            var detect = ButtonFor("自动识别"); detect.Click += DetectFolders; folderActions.Controls.Add(detect);
            var browse = ButtonFor("选择…"); browse.Click += Browse; folderActions.Controls.Add(browse);
            settingsPanel.Controls.Add(folderActions, 2, 2);
            settingsPanel.Controls.Add(LabelFor("App Key / Client ID"), 0, 3); settingsPanel.Controls.Add(_clientId, 1, 3);
            var baiduActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            var acquireKey = ButtonFor("获取 App Key"); acquireKey.Click += AcquireBaiduAppKey; baiduActions.Controls.Add(acquireKey);
            var connect = ButtonFor("连接百度网盘"); connect.Click += ConnectBaidu; baiduActions.Controls.Add(connect);
            settingsPanel.Controls.Add(baiduActions, 2, 3);
            settingsPanel.Controls.Add(LabelFor("Secret Key（本机加密）"), 0, 4); settingsPanel.Controls.Add(_clientSecret, 1, 4);
            settingsPanel.Controls.Add(LabelFor("不会上传"), 2, 4);
            settingsPanel.Controls.Add(LabelFor("OAuth 回调地址"), 0, 5); settingsPanel.Controls.Add(_redirectUri, 1, 5);
            settingsPanel.Controls.Add(LabelFor("须与应用登记一致"), 2, 5);
            settingsPanel.Controls.Add(LabelFor("云端应用目录"), 0, 6); settingsPanel.Controls.Add(_remoteFolder, 1, 6);
            settingsPanel.Controls.Add(LabelFor("例：/apps/万落建筑工具"), 2, 6);
            settingsPanel.Controls.Add(LabelFor("本机设备名称"), 0, 7); settingsPanel.Controls.Add(_device, 1, 7);
            var open = ButtonFor("打开目录"); open.Click += OpenFolder; settingsPanel.Controls.Add(open, 2, 7);
            settingsPanel.Controls.Add(LabelFor("首次连接时"), 0, 8); settingsPanel.Controls.Add(_initialPreference, 1, 8);
            root.Controls.Add(settingsPanel);

            var scope = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            scope.Controls.Add(LabelFor("同步内容：")); scope.Controls.Add(_general); scope.Controls.Add(_projects); scope.Controls.Add(_templates); scope.Controls.Add(_drawings);
            scope.Controls.Add(_auto); scope.Controls.Add(LabelFor("历史天数")); scope.Controls.Add(_days); scope.Controls.Add(LabelFor("每文件版本数")); scope.Controls.Add(_versions);
            root.Controls.Add(scope);

            var progressPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 4, 0, 4) };
            _status.Dock = DockStyle.Fill;
            progressPanel.Controls.Add(_status, 0, 0); progressPanel.Controls.Add(_progress, 0, 1);
            root.Controls.Add(progressPanel);

            var detailsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 8) };
            detailsPanel.Controls.Add(_details);
            root.Controls.Add(detailsPanel);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            _close = ButtonFor("关闭");
            _close.DialogResult = DialogResult.Cancel;
            _close.Click += delegate { CloseImmediately(); };
            CancelButton = _close;
            var save = ButtonFor("保存设置"); save.Click += SaveSettings;
            var center = ButtonFor("同步中心"); center.Click += delegate { using (var form = new CloudSyncCenterForm()) form.ShowDialog(this); };
            _syncNow = ButtonFor("立即同步"); _syncNow.Click += SynchronizeNow;
            actions.Controls.Add(_close); actions.Controls.Add(center); actions.Controls.Add(_syncNow); actions.Controls.Add(save);
            root.Controls.Add(actions);

            Load += delegate { LoadSettings(); CloudSyncCoordinator.SynchronizationCompleted += OnSynchronizationCompleted; };
            FormClosed += delegate
            {
                CloudSyncCoordinator.SynchronizationCompleted -= OnSynchronizationCompleted;
                CancelSynchronizationWithoutBlockingUi();
            };
            _folder.TextChanged += delegate { if (_provider.SelectedIndex == 1) UpdateProviderUi(); };
        }

        private void LoadSettings()
        {
            _settings = new CloudSyncSettingsStore().LoadSettings();
            _provider.Items.Clear();
            _provider.Items.Add("百度网盘直连（无需客户端，推荐）");
            _provider.Items.Add("通用云盘同步文件夹（兼容模式）");
            _provider.Items.Add("115 官方 OpenAPI（申请审核后启用）");
            _provider.SelectedIndexChanged += delegate { UpdateProviderUi(); };
            _clientId.TextChanged += delegate { if (_provider.SelectedIndex != 1) UpdateProviderUi(); };
            _redirectUri.TextChanged += delegate { if (_provider.SelectedIndex == 0) UpdateProviderUi(); };
            _initialPreference.Items.Clear();
            _initialPreference.Items.Add("云端优先（先备份本机，推荐）");
            _initialPreference.Items.Add("本机优先（先备份云端）");
            _initialPreference.Items.Add("不选择，全部保留为冲突");
            _enabled.Checked = _settings.Enabled;
            _provider.SelectedIndex = string.Equals(_settings.Provider, "115OpenApi", StringComparison.OrdinalIgnoreCase) ? 2
                : string.Equals(_settings.Provider, "LocalFolder", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _folder.Text = _settings.SyncFolder ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_folder.Text))
            {
                var detected = CloudSyncFolderDetector.Discover();
                if (detected.Count == 1) _folder.Text = detected[0].FolderPath;
            }
            _clientId.Text = _settings.ProviderClientId ?? string.Empty;
            _redirectUri.Text = string.IsNullOrWhiteSpace(_settings.ProviderRedirectUri) ? BaiduNetdiskClient.DefaultRedirectUri : _settings.ProviderRedirectUri;
            _remoteFolder.Text = string.IsNullOrWhiteSpace(_settings.ProviderRemoteFolder) ? "/apps/万落建筑工具" : _settings.ProviderRemoteFolder;
            _device.Text = string.IsNullOrWhiteSpace(_settings.DeviceName) ? Environment.MachineName : _settings.DeviceName;
            _general.Checked = _settings.SyncGeneralSettings;
            _projects.Checked = _settings.SyncProjectConfigurations;
            _templates.Checked = _settings.SyncTemplatesAndSchemes;
            _drawings.Checked = _settings.SyncProjectFiles;
            _drawings.Enabled = true;
            _auto.Checked = _settings.AutoSync;
            _initialPreference.SelectedIndex = string.Equals(_settings.InitialSyncPreference, "Local", StringComparison.OrdinalIgnoreCase) ? 1
                : string.Equals(_settings.InitialSyncPreference, "Conflict", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            _days.Value = Math.Max(_days.Minimum, Math.Min(_days.Maximum, _settings.HistoryRetentionDays));
            _versions.Value = Math.Max(_versions.Minimum, Math.Min(_versions.Maximum, _settings.KeepVersionsPerFile));
            _status.Text = _settings.Enabled ? "同步已启用。" : "同步默认关闭；启用并保存后才会读写同步目录。";
            UpdateProviderUi();
        }

        private bool PersistSettings(bool requestAutomaticSync = true, bool reloadCoordinator = true)
        {
            var folder = _folder.Text.Trim();
            var providerId = _provider.SelectedIndex == 2 ? "115OpenApi" : _provider.SelectedIndex == 1 ? "LocalFolder" : "BaiduNetdisk";
            if (_enabled.Checked && providerId == "LocalFolder" && string.IsNullOrWhiteSpace(folder))
            {
                MessageBox.Show(this, "启用同步前请选择同步文件夹。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (_enabled.Checked && providerId == "LocalFolder")
            {
                try { Path.GetFullPath(folder); }
                catch (Exception exception)
                {
                    MessageBox.Show(this, "同步文件夹路径无效：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            if (_enabled.Checked && providerId == "115OpenApi")
            {
                MessageBox.Show(this, "115 官方直连需要先完成开发者认证和应用审核。\r\n\r\n当前可以保存 Client ID，但在取得审核后台的正式接口参数前不能启用直连；请继续使用“通用云盘同步文件夹”模式。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (_enabled.Checked && providerId == "BaiduNetdisk")
            {
                var candidate = new CloudSyncSettings { Provider = providerId, ProviderClientId = _clientId.Text.Trim(), ProviderRedirectUri = _redirectUri.Text.Trim() };
                using (var provider = CloudSyncProviderFactory.Create(candidate))
                    if (!provider.IsReady)
                    {
                        MessageBox.Show(this, provider.Status, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
            }
            _settings.Enabled = _enabled.Checked;
            _settings.Provider = providerId;
            _settings.SyncFolder = folder;
            _settings.ProviderClientId = _clientId.Text.Trim();
            _settings.ProviderRedirectUri = _redirectUri.Text.Trim();
            _settings.ProviderRemoteFolder = _remoteFolder.Text.Trim();
            _settings.DeviceName = string.IsNullOrWhiteSpace(_device.Text) ? Environment.MachineName : _device.Text.Trim();
            _settings.SyncGeneralSettings = _general.Checked;
            _settings.SyncProjectConfigurations = _projects.Checked;
            _settings.SyncTemplatesAndSchemes = _templates.Checked;
            _settings.SyncProjectFiles = _drawings.Checked;
            _settings.ProjectMappings = ProjectSyncProjectionStore.BuildMappings(
                new PublishPlanStore().LoadProjects(), _settings.ProjectMappings);
            _settings.AutoSync = _auto.Checked;
            _settings.InitialSyncPreference = _initialPreference.SelectedIndex == 1 ? "Local" : _initialPreference.SelectedIndex == 2 ? "Conflict" : "Remote";
            _settings.HistoryRetentionDays = (int)_days.Value;
            _settings.KeepVersionsPerFile = (int)_versions.Value;
            new CloudSyncSettingsStore().SaveSettings(_settings);
            var synchronize = requestAutomaticSync && _settings.Enabled && _settings.AutoSync;
            if (reloadCoordinator) CloudSyncCoordinator.QueueReload(synchronize);
            else if (synchronize) CloudSyncCoordinator.RequestSynchronization(false);
            return true;
        }

        private void SaveSettings(object sender, EventArgs e)
        {
            try
            {
                if (!PersistSettings()) return;
                _status.Text = _settings.Enabled
                    ? "设置已保存，后台同步已启动；已登记 " + (_settings.ProjectMappings == null ? 0 : _settings.ProjectMappings.Count) + " 个工程目录。"
                    : "设置已保存，同步已关闭。";
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private async void SynchronizeNow(object sender, EventArgs e)
        {
            if (_syncCancellation != null)
            {
                _syncCancellation.Cancel();
                _status.Text = "正在取消同步，请稍候…";
                _status.ForeColor = Color.DarkOrange;
                return;
            }
            var manualStarted = false;
            try
            {
                if (!PersistSettings(false, false)) return;
                if (!_settings.Enabled) { MessageBox.Show(this, "请先启用云同步。", Text); return; }
                if (!CloudSyncCoordinator.TryBeginManualSynchronization())
                {
                    _status.Text = "后台同步正在运行，本次没有重复启动；请等待当前任务完成。";
                    _status.ForeColor = Color.DarkOrange;
                    return;
                }
                manualStarted = true;
                _syncCancellation = new CancellationTokenSource();
                _syncNow.Text = "取消同步"; _status.Text = "正在准备同步…"; _details.Items.Clear();
                _progress.Visible = true; _progress.Style = ProgressBarStyle.Marquee;
                var settings = _settings;
                var cancellationToken = _syncCancellation.Token;
                var result = await Task.Run(() => CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore(), QueueProgress, cancellationToken));
                ShowResult(result);
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) { _status.Text = "同步已取消，现有正式文件未被不完整文件覆盖。"; _status.ForeColor = Color.DarkOrange; }
            }
            catch (Exception exception) { ShowError(exception); }
            finally
            {
                if (manualStarted) CloudSyncCoordinator.EndManualSynchronization();
                if (_syncCancellation != null) { _syncCancellation.Dispose(); _syncCancellation = null; }
                if (!IsDisposed)
                {
                    _progress.Visible = false;
                    _syncNow.Text = "立即同步";
                    CloudSyncCoordinator.QueueReload(false);
                }
            }
        }

        private void ShowProgress(CloudSyncProgress progress)
        {
            if (progress == null || IsDisposed) return;
            _status.Text = progress.Stage + (string.IsNullOrWhiteSpace(progress.LogicalPath) ? string.Empty : "：" + progress.LogicalPath);
            _status.ForeColor = Color.FromArgb(34, 98, 160);
            if (progress.Total > 0)
            {
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = progress.Percentage;
            }
            else _progress.Style = ProgressBarStyle.Marquee;
        }

        private void QueueProgress(CloudSyncProgress progress)
        {
            if (progress == null || IsDisposed || Disposing) return;
            _latestProgress = progress;
            Interlocked.Increment(ref _progressVersion);
            if (Interlocked.CompareExchange(ref _progressUpdateQueued, 1, 0) != 0) return;
            try
            {
                if (!IsHandleCreated)
                {
                    Interlocked.Exchange(ref _progressUpdateQueued, 0);
                    return;
                }
                BeginInvoke((Action)DrainProgress);
            }
            catch
            {
                Interlocked.Exchange(ref _progressUpdateQueued, 0);
            }
        }

        private void DrainProgress()
        {
            if (IsDisposed || Disposing)
            {
                Interlocked.Exchange(ref _progressUpdateQueued, 0);
                return;
            }
            var displayedVersion = Volatile.Read(ref _progressVersion);
            ShowProgress(_latestProgress);
            Interlocked.Exchange(ref _progressUpdateQueued, 0);
            if (displayedVersion != Volatile.Read(ref _progressVersion)) QueueProgress(_latestProgress);
        }

        private void CloseImmediately()
        {
            if (IsDisposed || Disposing) return;
            Hide();
            CancelSynchronizationWithoutBlockingUi();
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void CancelSynchronizationWithoutBlockingUi()
        {
            var cancellation = _syncCancellation;
            if (cancellation == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            });
        }

        private void OnSynchronizationCompleted(CloudSyncResult result, Exception failure)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)delegate
            {
                if (failure != null) ShowError(failure);
                else if (result != null) ShowResult(result);
            });
        }

        private void ShowResult(CloudSyncResult result)
        {
            _status.Text = "同步完成：" + result.Summary;
            _status.ForeColor = result.Errors > 0 || result.Conflicts > 0 ? Color.DarkOrange : Color.FromArgb(34, 120, 72);
            _details.Items.Clear();
            foreach (var operation in result.Operations)
                _details.Items.Add(operation.Kind + " · " + operation.LogicalPath + " · " + operation.Message);
            if (!result.Operations.Any()) _details.Items.Add("所有文件均已是最新版本。");
        }

        private void Browse(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择云盘客户端或同步工具维护的本地文件夹" })
                if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
        }

        private void DetectFolders(object sender, EventArgs e)
        {
            try
            {
                var candidates = CloudSyncFolderDetector.Discover();
                if (candidates.Count == 0)
                {
                    MessageBox.Show(this, "没有自动发现常见云盘目录。请先安装并登录云盘客户端，或点击“选择…”指定 Syncthing/NAS 等同步目录。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (candidates.Count == 1)
                {
                    _folder.Text = candidates[0].FolderPath;
                    return;
                }

                using (var dialog = new Form
                {
                    Text = "选择检测到的同步目录",
                    StartPosition = FormStartPosition.CenterParent,
                    Size = new Size(680, 360),
                    MinimumSize = new Size(560, 300),
                    Font = Font
                })
                {
                    var list = new ListBox { Dock = DockStyle.Fill, DataSource = candidates };
                    var ok = ButtonFor("使用此目录"); ok.DialogResult = DialogResult.OK;
                    var cancel = ButtonFor("取消"); cancel.DialogResult = DialogResult.Cancel;
                    var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
                    actions.Controls.Add(ok); actions.Controls.Add(cancel);
                    dialog.Controls.Add(list); dialog.Controls.Add(actions);
                    dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                    list.DoubleClick += delegate { if (list.SelectedItem != null) dialog.DialogResult = DialogResult.OK; };
                    if (dialog.ShowDialog(this) == DialogResult.OK && list.SelectedItem is CloudSyncFolderCandidate selected)
                        _folder.Text = selected.FolderPath;
                }
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void OpenFolder(object sender, EventArgs e)
        {
            try
            {
                var path = _folder.Text.Trim();
                if (string.IsNullOrWhiteSpace(path)) return;
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", path);
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void UpdateProviderUi()
        {
            var local = _provider.SelectedIndex == 1;
            var baidu = _provider.SelectedIndex == 0;
            _folder.Enabled = local;
            _clientId.Enabled = !local;
            _clientSecret.Enabled = baidu;
            _redirectUri.Enabled = baidu;
            _remoteFolder.Enabled = baidu;
            var version = Interlocked.Increment(ref _providerStatusVersion);
            var folder = _folder.Text.Trim();
            var clientId = _clientId.Text.Trim();
            var redirectUri = _redirectUri.Text.Trim();
            _status.Text = local ? "正在检查同步目录…" : baidu ? "正在检查百度网盘授权…" : "正在检查 115 开放平台配置…";
            _status.ForeColor = Color.FromArgb(34, 98, 160);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string message;
                Color color;
                if (!local)
                {
                    var providerId = baidu ? "BaiduNetdisk" : "115OpenApi";
                    using (var provider = CloudSyncProviderFactory.Create(new CloudSyncSettings { Provider = providerId, ProviderClientId = clientId, ProviderRedirectUri = redirectUri }))
                        message = provider.Status;
                    color = providerId == "BaiduNetdisk" && message.Contains("已授权") ? Color.FromArgb(34, 120, 72) : Color.DarkOrange;
                }
                else
                {
                    message = CloudSyncFolderDetector.Describe(folder);
                    color = string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)
                        ? Color.DarkOrange : Color.FromArgb(34, 120, 72);
                }
                if (IsDisposed || Disposing || version != Volatile.Read(ref _providerStatusVersion)) return;
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (IsDisposed || Disposing || version != Volatile.Read(ref _providerStatusVersion)) return;
                        _status.Text = message;
                        _status.ForeColor = color;
                    });
                }
                catch { }
            });
        }

        private void OpenProviderPortal(object sender, EventArgs e)
        {
            try
            {
                var url = _provider.SelectedIndex == 2 ? OneOneFiveOpenApiProvider.DeveloperPortal : BaiduNetdiskProvider.DeveloperPortal;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void AcquireBaiduAppKey(object sender, EventArgs e)
        {
            if (_provider.SelectedIndex != 0)
            {
                MessageBox.Show(this, "请先选择“百度网盘直连”。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dialog = new Form
            {
                Text = "获取百度网盘 App Key",
                StartPosition = FormStartPosition.CenterParent,
                MinimumSize = new Size(700, 500),
                Size = new Size(760, 560),
                Font = Font
            })
            {
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 7 };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                dialog.Controls.Add(root);

                var heading = new Label { Text = "百度官方签发 App Key", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 8) };
                root.Controls.Add(heading, 0, 0); root.SetColumnSpan(heading, 2);
                var explanation = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(690, 0),
                    ForeColor = Color.DimGray,
                    Text = "App Key 只能由百度开放平台在完成开发者认证、创建应用后签发，插件不能代替用户绕过官方认证。点击下方按钮会打开官方页面；取得参数后填入本向导即可自动带回云同步设置。"
                };
                root.Controls.Add(explanation, 0, 1); root.SetColumnSpan(explanation, 2);

                var appKey = new TextBox { Dock = DockStyle.Top, Text = _clientId.Text.Trim() };
                var secret = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true, Text = _clientSecret.Text };
                var redirect = new TextBox { Dock = DockStyle.Top, Text = string.IsNullOrWhiteSpace(_redirectUri.Text) ? BaiduNetdiskClient.DefaultRedirectUri : _redirectUri.Text.Trim() };
                var remote = new TextBox { Dock = DockStyle.Top, Text = string.IsNullOrWhiteSpace(_remoteFolder.Text) ? "/apps/万落建筑工具" : _remoteFolder.Text.Trim() };
                AddWizardField(root, "App Key", appKey, 2);
                AddWizardField(root, "Secret Key", secret, 3);
                AddWizardField(root, "OAuth 回调地址", redirect, 4);

                var remotePanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
                remotePanel.Controls.Add(remote);
                remotePanel.Controls.Add(new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "应用目录名称必须与百度后台创建的应用一致，例如 /apps/万落建筑工具。", Margin = new Padding(0, 5, 0, 0) });
                root.Controls.Add(LabelFor("云端应用目录"), 0, 5); root.Controls.Add(remotePanel, 1, 5);

                var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
                var apply = ButtonFor("填写完成"); apply.DialogResult = DialogResult.OK;
                var cancel = ButtonFor("取消"); cancel.DialogResult = DialogResult.Cancel;
                var open = ButtonFor("打开百度开放平台");
                open.Click += delegate
                {
                    try { Process.Start(new ProcessStartInfo(BaiduNetdiskProvider.DeveloperPortal) { UseShellExecute = true }); }
                    catch (Exception exception) { MessageBox.Show(dialog, exception.Message, dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                };
                actions.Controls.Add(apply); actions.Controls.Add(cancel); actions.Controls.Add(open);
                root.Controls.Add(actions, 0, 6); root.SetColumnSpan(actions, 2);
                dialog.AcceptButton = apply; dialog.CancelButton = cancel;
                dialog.FormClosing += delegate(object closingSender, FormClosingEventArgs closingArgs)
                {
                    if (dialog.DialogResult != DialogResult.OK) return;
                    if (!string.IsNullOrWhiteSpace(appKey.Text) && !string.IsNullOrWhiteSpace(secret.Text) && !string.IsNullOrWhiteSpace(redirect.Text)) return;
                    closingArgs.Cancel = true;
                    MessageBox.Show(dialog, "App Key、Secret Key 和 OAuth 回调地址都不能为空。", dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                };

                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _clientId.Text = appKey.Text.Trim(); _clientSecret.Text = secret.Text;
                _redirectUri.Text = redirect.Text.Trim(); _remoteFolder.Text = remote.Text.Trim();
                _status.Text = "应用参数已填写；下一步点击“连接百度网盘”完成账号授权。";
                _status.ForeColor = Color.FromArgb(34, 98, 160);
            }
        }

        private async void ConnectBaidu(object sender, EventArgs e)
        {
            if (_provider.SelectedIndex != 0)
            {
                MessageBox.Show(this, "请先选择“百度网盘直连”。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var clientId = _clientId.Text.Trim(); var secret = _clientSecret.Text; var redirect = _redirectUri.Text.Trim();
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(redirect))
            {
                MessageBox.Show(this, "请填写百度 App Key、Secret Key 和已登记的 OAuth 回调地址。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                var state = Guid.NewGuid().ToString("N");
                Process.Start(new ProcessStartInfo(BaiduNetdiskClient.BuildAuthorizationUri(clientId, redirect, state).AbsoluteUri) { UseShellExecute = true });
                var callback = Microsoft.VisualBasic.Interaction.InputBox("请在浏览器完成百度网盘授权，然后复制并粘贴浏览器地址栏中的完整回调地址。", "连接百度网盘", string.Empty);
                if (string.IsNullOrWhiteSpace(callback)) return;
                var code = BaiduNetdiskClient.ExtractAuthorizationCode(callback, state);
                _status.Text = "正在向百度网盘交换授权令牌…"; _status.ForeColor = Color.FromArgb(34, 98, 160);
                var credential = await Task.Run(async () =>
                {
                    using (var client = new BaiduNetdiskClient()) return await client.ExchangeCodeAsync(clientId, secret, redirect, code.Trim(), CancellationToken.None);
                });
                new CloudSyncCredentialStore().Save("BaiduNetdisk", credential);
                _settings.Provider = "BaiduNetdisk"; _settings.ProviderClientId = clientId; _settings.ProviderRedirectUri = redirect;
                _settings.ProviderRemoteFolder = string.IsNullOrWhiteSpace(_remoteFolder.Text) ? "/apps/万落建筑工具" : _remoteFolder.Text.Trim();
                new CloudSyncSettingsStore().SaveSettings(_settings);
                _clientSecret.Clear();
                _status.Text = "百度网盘连接成功；令牌和 Secret Key 已在本机加密保存。"; _status.ForeColor = Color.FromArgb(34, 120, 72);
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void ShowError(Exception exception)
        {
            if (IsDisposed || Disposing) return;
            _status.Text = "同步失败：" + exception.Message;
            _status.ForeColor = Color.Firebrick;
        }

        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 8, 3) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(6, 2, 0, 2) }; }
        private static void AddWizardField(TableLayoutPanel panel, string label, Control input, int row)
        {
            panel.Controls.Add(LabelFor(label), 0, row); panel.Controls.Add(input, 1, row);
        }
    }
}
