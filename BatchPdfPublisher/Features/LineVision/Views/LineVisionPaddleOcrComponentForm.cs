using BatchPdfPublisher.Services;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class LineVisionPaddleOcrComponentForm : DpiAwareForm
    {
        private readonly Label _state = new Label { AutoSize = true, Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold) };
        private readonly TextBox _location = new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
        private readonly Label _detail = new Label { AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = Color.DimGray };
        private readonly ProgressBar _progress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
        private readonly Button _online = ButtonFor("在线安装/更新");
        private readonly Button _offline = ButtonFor("选择离线安装包");
        private readonly Button _changeLocation = ButtonFor("更改安装位置");
        private readonly Button _open = ButtonFor("打开文件夹");
        private readonly Button _uninstall = ButtonFor("卸载");
        private readonly Button _cancel = ButtonFor("取消任务");
        private CancellationTokenSource _cancellation;

        public LineVisionPaddleOcrComponentForm()
        {
            Text = "PaddleOCR 增强组件";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(720, 285);
            MinimumSize = new Size(640, 275);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.White;
            Build();
            RefreshStatus();
            FormClosing += (sender, args) =>
            {
                if (_cancellation == null) return;
                var answer = MessageBox.Show(this, "组件任务仍在进行，确定取消并关闭吗？", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) { args.Cancel = true; return; }
                _cancellation.Cancel();
                args.Cancel = true;
            };
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(_state, 0, 0);
            var locationRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 12, 0, 8) };
            locationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            locationRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            locationRow.Controls.Add(_location, 0, 0);
            locationRow.Controls.Add(_changeLocation, 1, 0);
            root.Controls.Add(locationRow, 0, 1);
            root.Controls.Add(_detail, 0, 2);
            root.Controls.Add(_progress, 0, 3);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
            actions.Controls.Add(_online); actions.Controls.Add(_offline); actions.Controls.Add(_open);
            actions.Controls.Add(_uninstall); actions.Controls.Add(_cancel);
            var close = ButtonFor("关闭"); close.Click += (sender, args) => Close(); actions.Controls.Add(close);
            root.Controls.Add(actions, 0, 4);
            Controls.Add(root);

            _online.Click += async (sender, args) => await InstallOnlineAsync();
            _offline.Click += async (sender, args) => await InstallOfflineAsync();
            _changeLocation.Click += ChangeLocation;
            _open.Click += (sender, args) => { try { LineVisionPaddleOcrComponentService.OpenInstallDirectory(); } catch (Exception exception) { ShowError(exception); } };
            _uninstall.Click += Uninstall;
            _cancel.Click += (sender, args) => { if (_cancellation != null) _cancellation.Cancel(); };
            _cancel.Enabled = false;
        }

        private async Task InstallOnlineAsync()
        {
            await RunTaskAsync(async (progress, token) =>
            {
                var package = await LineVisionPaddleOcrComponentService.DownloadLatestPackageAsync(progress, token);
                try { await Task.Run(() => LineVisionPaddleOcrComponentService.InstallPackage(package, progress, token), token); }
                finally { try { if (File.Exists(package)) File.Delete(package); } catch { } }
            });
        }

        private async Task InstallOfflineAsync()
        {
            using (var dialog = new OpenFileDialog { Title = "选择 PaddleOCR 离线安装包", Filter = "PaddleOCR 组件包 (*.zip)|*.zip", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                await RunTaskAsync((progress, token) => Task.Run(() => LineVisionPaddleOcrComponentService.InstallPackage(dialog.FileName, progress, token), token));
            }
        }

        private async Task RunTaskAsync(Func<IProgress<PaddleOcrComponentProgress>, CancellationToken, Task> action)
        {
            if (_cancellation != null) return;
            _cancellation = new CancellationTokenSource();
            SetBusy(true);
            var progress = new Progress<PaddleOcrComponentProgress>(value =>
            {
                _detail.Text = string.IsNullOrWhiteSpace(value.Stage) ? "正在处理……" : value.Stage;
                _progress.Value = value.Total > 0 ? value.Percentage : 0;
            });
            try
            {
                await action(progress, _cancellation.Token);
                _progress.Value = 100;
                MessageBox.Show(this, "PaddleOCR 增强组件已安装，可以直接使用。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { MessageBox.Show(this, "组件任务已取消，原有组件未受影响。", Text); }
            catch (Exception exception) { ShowError(exception); }
            finally
            {
                _cancellation.Dispose(); _cancellation = null;
                SetBusy(false); RefreshStatus();
            }
        }

        private void ChangeLocation(object sender, EventArgs args)
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择 PaddleOCR 增强组件的存放位置" })
            {
                var current = LineVisionPaddleOcrComponentService.GetInstallDirectory();
                dialog.SelectedPath = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var installed = LineVisionPaddleOcrComponentService.GetStatus().IsInstalled;
                if (installed && MessageBox.Show(this, "更改位置不会自动移动当前组件。确定切换到新位置，并在新位置重新安装吗？",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                try { LineVisionPaddleOcrComponentService.SetInstallParent(dialog.SelectedPath); RefreshStatus(); }
                catch (Exception exception) { ShowError(exception); }
            }
        }

        private void Uninstall(object sender, EventArgs args)
        {
            if (!LineVisionPaddleOcrComponentService.GetStatus().CanUninstall) return;
            if (MessageBox.Show(this, "确定卸载 PaddleOCR 增强组件吗？\r\n卸载后会自动使用 Windows OCR。",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { LineVisionPaddleOcrComponentService.Uninstall(); RefreshStatus(); }
            catch (Exception exception) { ShowError(exception); }
        }

        private void RefreshStatus()
        {
            var status = LineVisionPaddleOcrComponentService.GetStatus();
            _location.Text = status.InstallDirectory;
            _state.Text = status.IsInstalled ? "已安装 · " + status.Version : "未安装";
            _state.ForeColor = status.IsInstalled ? Color.FromArgb(28, 112, 73) : Color.FromArgb(175, 88, 35);
            _detail.Text = status.IsInstalled
                ? "增强组件独立于主程序，更新万落建筑工具时不需要重复下载。"
                : status.Message + " 未安装时自动使用 Windows OCR，不影响其他制图功能。";
            _uninstall.Enabled = status.CanUninstall;
            _progress.Value = 0;
        }

        private void SetBusy(bool busy)
        {
            _online.Enabled = !busy; _offline.Enabled = !busy; _changeLocation.Enabled = !busy;
            _open.Enabled = !busy; _uninstall.Enabled = !busy && LineVisionPaddleOcrComponentService.GetStatus().CanUninstall;
            _cancel.Enabled = busy;
        }

        private void ShowError(Exception exception)
        {
            MessageBox.Show(this, exception == null ? "组件操作失败。" : exception.GetBaseException().Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static Button ButtonFor(string text)
        {
            return new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(5, 2, 0, 2), FlatStyle = FlatStyle.Flat };
        }
    }
}
