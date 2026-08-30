using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class CloudSyncSettingsForm : Form
    {
        private readonly CheckBox _enabled = new CheckBox { Text = "启用云同步", AutoSize = true };
        private readonly TextBox _folder = new TextBox { Dock = DockStyle.Fill };
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
        private readonly ListBox _details = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        private readonly Button _syncNow;
        private CloudSyncSettings _settings;

        public CloudSyncSettingsForm()
        {
            Text = "万落建筑工具 · 云同步";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 620);
            Size = new Size(840, 680);
            Font = new Font("Microsoft YaHei UI", 9F);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 6, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var title = new Label { Text = "云同步（V2：项目文件与 DWG 安全同步）", Font = new Font(Font, FontStyle.Bold), AutoSize = true };
            root.Controls.Add(title);
            var description = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(780, 0),
                ForeColor = Color.DimGray,
                Text = "项目参数会自动转换为跨电脑可用的相对路径。DWG 保存成功后才进入同步；远端 DWG 若仍在 AutoCAD 中打开，只会进入待应用区，关闭图纸后再安全替换。"
            };
            root.Controls.Add(description);

            var settingsPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 12, 0, 8) };
            settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settingsPanel.Controls.Add(_enabled, 0, 0); settingsPanel.SetColumnSpan(_enabled, 3);
            settingsPanel.Controls.Add(LabelFor("同步文件夹"), 0, 1); settingsPanel.Controls.Add(_folder, 1, 1);
            var browse = ButtonFor("选择…"); browse.Click += Browse; settingsPanel.Controls.Add(browse, 2, 1);
            settingsPanel.Controls.Add(LabelFor("本机设备名称"), 0, 2); settingsPanel.Controls.Add(_device, 1, 2);
            var open = ButtonFor("打开目录"); open.Click += OpenFolder; settingsPanel.Controls.Add(open, 2, 2);
            settingsPanel.Controls.Add(LabelFor("首次连接时"), 0, 3); settingsPanel.Controls.Add(_initialPreference, 1, 3);
            root.Controls.Add(settingsPanel);

            var scope = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            scope.Controls.Add(LabelFor("同步内容：")); scope.Controls.Add(_general); scope.Controls.Add(_projects); scope.Controls.Add(_templates); scope.Controls.Add(_drawings);
            scope.Controls.Add(_auto); scope.Controls.Add(LabelFor("历史天数")); scope.Controls.Add(_days); scope.Controls.Add(LabelFor("每文件版本数")); scope.Controls.Add(_versions);
            root.Controls.Add(scope);

            var detailsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 8) };
            detailsPanel.Controls.Add(_details); detailsPanel.Controls.Add(_status); _status.Dock = DockStyle.Top;
            root.Controls.Add(detailsPanel);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var close = ButtonFor("关闭"); close.Click += delegate { Close(); };
            var save = ButtonFor("保存设置"); save.Click += SaveSettings;
            _syncNow = ButtonFor("立即同步"); _syncNow.Click += SynchronizeNow;
            actions.Controls.Add(close); actions.Controls.Add(_syncNow); actions.Controls.Add(save);
            root.Controls.Add(actions);

            Load += delegate { LoadSettings(); CloudSyncCoordinator.SynchronizationCompleted += OnSynchronizationCompleted; };
            FormClosed += delegate { CloudSyncCoordinator.SynchronizationCompleted -= OnSynchronizationCompleted; };
        }

        private void LoadSettings()
        {
            _settings = new CloudSyncSettingsStore().LoadSettings();
            _initialPreference.Items.Clear();
            _initialPreference.Items.Add("云端优先（先备份本机，推荐）");
            _initialPreference.Items.Add("本机优先（先备份云端）");
            _initialPreference.Items.Add("不选择，全部保留为冲突");
            _enabled.Checked = _settings.Enabled;
            _folder.Text = _settings.SyncFolder ?? string.Empty;
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
        }

        private bool PersistSettings()
        {
            var folder = _folder.Text.Trim();
            if (_enabled.Checked && string.IsNullOrWhiteSpace(folder))
            {
                MessageBox.Show(this, "启用同步前请选择同步文件夹。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (_enabled.Checked) Directory.CreateDirectory(folder);
            _settings.Enabled = _enabled.Checked;
            _settings.SyncFolder = folder;
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
            CloudSyncCoordinator.Reload();
            if (_settings.Enabled && _settings.AutoSync) CloudSyncCoordinator.RequestSynchronization(false);
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
            try
            {
                if (!PersistSettings()) return;
                if (!_settings.Enabled) { MessageBox.Show(this, "请先启用云同步。", Text); return; }
                _syncNow.Enabled = false; _status.Text = "正在核对本机与同步文件夹…"; _details.Items.Clear();
                var settings = _settings;
                var result = await Task.Run(() => new LocalFolderSyncEngine(new CloudSyncSettingsStore())
                    .Synchronize(settings, CloudSyncCatalog.CreateDefault(settings)));
                ShowResult(result);
            }
            catch (Exception exception) { ShowError(exception); }
            finally { _syncNow.Enabled = true; }
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
            using (var dialog = new FolderBrowserDialog { Description = "选择本地同步文件夹" })
                if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
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

        private void ShowError(Exception exception)
        {
            _status.Text = "同步失败：" + exception.Message;
            _status.ForeColor = Color.Firebrick;
        }

        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 8, 3) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(6, 2, 0, 2) }; }
    }
}
