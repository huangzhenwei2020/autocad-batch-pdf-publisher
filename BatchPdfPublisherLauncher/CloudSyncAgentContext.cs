using BatchPdfPublisher.Services;
using Microsoft.Win32;
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace BatchPdfPublisherLauncher
{
    internal sealed class CloudSyncAgentContext : ApplicationContext
    {
        private const string RunValueName = "WanluoArchitectureToolsCloudSync";
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _status;
        private readonly ToolStripMenuItem _automaticSync;
        private readonly ToolStripMenuItem _autoStart;
        private readonly Control _dispatcher;
        private CloudSyncHistoryForm _historyForm;

        public CloudSyncAgentContext()
        {
            _dispatcher = new Control();
            _dispatcher.CreateControl();
            var settings = new CloudSyncSettingsStore().LoadSettings();
            _status = new ToolStripMenuItem("等待同步") { Enabled = false };
            _automaticSync = new ToolStripMenuItem("后台自动同步")
            {
                Checked = settings.Enabled && settings.AutoSync,
                CheckOnClick = true
            };
            _automaticSync.CheckedChanged += AutomaticSyncChanged;
            _autoStart = new ToolStripMenuItem("开机自动运行") { Checked = IsAutoStartEnabled(), CheckOnClick = true };
            _autoStart.CheckedChanged += delegate { SetAutoStart(_autoStart.Checked); };
            var sync = new ToolStripMenuItem("立即同步");
            sync.Click += delegate { CloudSyncCoordinator.RequestSynchronization(true); };
            var history = new ToolStripMenuItem("同步记录");
            history.Click += delegate { ShowHistory(); };
            var exit = new ToolStripMenuItem("退出后台同步");
            exit.Click += delegate { ExitThread(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add(_status);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_automaticSync);
            menu.Items.Add(sync);
            menu.Items.Add(history);
            menu.Items.Add(_autoStart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);

            _tray = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
                Text = "万落建筑云同步：正在启动",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += delegate { CloudSyncCoordinator.RequestSynchronization(true); };
            CloudSyncCoordinator.SynchronizationProgress += OnProgress;
            CloudSyncCoordinator.SynchronizationCompleted += OnCompleted;
            CloudSyncCoordinator.Install();

            _status.Text = settings.Enabled ? "后台同步已启用" : "云同步尚未启用";
            SetToolTip(_status.Text);
            if (settings.Enabled)
            {
                if (settings.AutoSync) CloudSyncCoordinator.RequestSynchronization(false);
                ShowBalloon("云同步在后台运行", "关闭 CAD 后仍会继续同步；双击托盘图标可立即同步。", ToolTipIcon.Info);
            }
        }

        private void AutomaticSyncChanged(object sender, EventArgs e)
        {
            var store = new CloudSyncSettingsStore();
            var settings = store.LoadSettings();
            if (_automaticSync.Checked && !settings.Enabled)
            {
                _automaticSync.CheckedChanged -= AutomaticSyncChanged;
                _automaticSync.Checked = false;
                _automaticSync.CheckedChanged += AutomaticSyncChanged;
                ShowBalloon("尚未启用云同步", "请先在云同步设置中登录并启用云同步。", ToolTipIcon.Warning);
                return;
            }

            settings.AutoSync = _automaticSync.Checked;
            store.SaveSettings(settings);
            CloudSyncCoordinator.QueueReload(_automaticSync.Checked);
            _status.Text = _automaticSync.Checked ? "后台自动同步已开启" : "后台自动同步已暂停";
            SetToolTip(_status.Text);
            ShowBalloon(_automaticSync.Checked ? "后台自动同步已开启" : "后台自动同步已暂停",
                _automaticSync.Checked ? "文件变化后会自动同步。" : "当前任务完成后暂停；仍可使用“立即同步”。",
                ToolTipIcon.Info);
        }

        protected override void ExitThreadCore()
        {
            CloudSyncCoordinator.SynchronizationProgress -= OnProgress;
            CloudSyncCoordinator.SynchronizationCompleted -= OnCompleted;
            CloudSyncCoordinator.Remove();
            _tray.Visible = false;
            _tray.Dispose();
            _dispatcher.Dispose();
            base.ExitThreadCore();
        }

        private void OnProgress(CloudSyncProgress progress)
        {
            if (progress == null) return;
            Post(delegate
            {
                var detail = progress.Percentage > 0 ? progress.Percentage + "%" : progress.Completed > 0 && progress.Total > 0 ? progress.Completed + "/" + progress.Total : string.Empty;
                var text = string.Join(" ", new[] { progress.Stage, detail, progress.SpeedText }.Where(value => !string.IsNullOrWhiteSpace(value)));
                _status.Text = string.IsNullOrWhiteSpace(text) ? "正在同步" : text;
                SetToolTip(_status.Text);
            });
        }

        private void OnCompleted(CloudSyncResult result, Exception failure)
        {
            CloudSyncHistoryStore.Append(result, failure);
            Post(delegate
            {
                var warning = failure != null || (result != null && (result.Errors > 0 || result.Conflicts > 0 || result.Pending > 0));
                _status.Text = warning ? "同步需要处理" : "同步完成";
                SetToolTip(_status.Text);
                if (failure != null)
                    ShowBalloon("云同步失败", failure.GetBaseException().Message, ToolTipIcon.Error);
                else if (result != null && (result.Uploaded > 0 || result.Downloaded > 0 || result.Deleted > 0 || warning))
                    ShowBalloon(warning ? "云同步需要处理" : "云同步完成", result.Summary, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            });
        }

        private void ShowHistory()
        {
            if (_historyForm == null || _historyForm.IsDisposed)
            {
                _historyForm = new CloudSyncHistoryForm();
                _historyForm.FormClosed += delegate { _historyForm = null; };
            }
            _historyForm.RefreshRecords();
            _historyForm.Show();
            if (_historyForm.WindowState == FormWindowState.Minimized) _historyForm.WindowState = FormWindowState.Normal;
            _historyForm.Activate();
        }

        private void Post(Action action)
        {
            if (action == null) return;
            if (!_dispatcher.IsDisposed && _dispatcher.IsHandleCreated) _dispatcher.BeginInvoke(action);
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(5000);
        }

        private void SetToolTip(string text)
        {
            var value = "万落建筑云同步：" + (text ?? string.Empty);
            _tray.Text = value.Length > 63 ? value.Substring(0, 63) : value;
        }

        private static bool IsAutoStartEnabled()
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return key != null && key.GetValue(RunValueName) != null; }
            catch { return false; }
        }

        private static void SetAutoStart(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (enabled) key.SetValue(RunValueName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --cloud-sync-agent");
                    else key.DeleteValue(RunValueName, false);
                }
            }
            catch { }
        }

    }
}
