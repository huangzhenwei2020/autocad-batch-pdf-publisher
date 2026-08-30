using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class CloudSyncCenterForm : Form
    {
        private readonly CloudSyncCenterService _service = new CloudSyncCenterService();
        private readonly Label _summary = new Label { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 8, 0, 0) };
        private readonly ListView _pending = CreateList("状态", "逻辑文件", "更新时间");
        private readonly ListView _conflicts = CreateList("逻辑文件", "本机副本", "共享副本", "发生时间");
        private readonly ListView _history = CreateList("来源", "逻辑文件", "版本时间");

        public CloudSyncCenterForm()
        {
            Text = "万落建筑工具 · 同步中心";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(920, 580);
            Size = new Size(1080, 680);
            Font = new Font("Microsoft YaHei UI", 9F);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildPendingTab());
            tabs.TabPages.Add(BuildConflictTab());
            tabs.TabPages.Add(BuildHistoryTab());
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var close = ButtonFor("关闭"); close.Click += delegate { Close(); };
            var refresh = ButtonFor("刷新"); refresh.Click += delegate { ReloadData(); };
            footer.Controls.Add(close); footer.Controls.Add(refresh);
            Controls.Add(tabs); Controls.Add(_summary); Controls.Add(footer);
            Shown += delegate { ReloadData(); };
        }

        private TabPage BuildPendingTab()
        {
            var page = Page("待应用"); page.Controls.Add(_pending);
            var bar = ActionBar();
            var apply = ButtonFor("应用所有可用项"); apply.Click += ApplyPending;
            var discard = ButtonFor("放弃所选待应用项"); discard.Click += DiscardPending;
            var open = ButtonFor("打开位置"); open.Click += delegate { OpenSelected(_pending); };
            bar.Controls.Add(apply); bar.Controls.Add(discard); bar.Controls.Add(open);
            page.Controls.Add(bar); return page;
        }

        private TabPage BuildConflictTab()
        {
            var page = Page("冲突"); page.Controls.Add(_conflicts);
            var bar = ActionBar();
            var local = ButtonFor("采用本机版本"); local.Click += delegate { ResolveConflict(true); };
            var remote = ButtonFor("采用共享版本"); remote.Click += delegate { ResolveConflict(false); };
            var open = ButtonFor("打开位置"); open.Click += delegate { OpenSelected(_conflicts); };
            bar.Controls.Add(local); bar.Controls.Add(remote); bar.Controls.Add(open);
            page.Controls.Add(bar); return page;
        }

        private TabPage BuildHistoryTab()
        {
            var page = Page("历史版本"); page.Controls.Add(_history);
            var bar = ActionBar();
            var restore = ButtonFor("恢复所选版本到本机"); restore.Click += RestoreHistory;
            var open = ButtonFor("打开位置"); open.Click += delegate { OpenSelected(_history); };
            bar.Controls.Add(restore); bar.Controls.Add(open);
            page.Controls.Add(bar); return page;
        }

        private void ReloadData()
        {
            try
            {
                var data = _service.Load();
                _pending.BeginUpdate(); _pending.Items.Clear();
                foreach (var item in data.Pending)
                {
                    var row = new ListViewItem(new[] { item.Kind, item.LogicalPath, item.ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss") }) { Tag = item };
                    _pending.Items.Add(row);
                }
                _pending.EndUpdate();

                _conflicts.BeginUpdate(); _conflicts.Items.Clear();
                foreach (var item in data.Conflicts)
                {
                    var row = new ListViewItem(new[] { item.LogicalPath, ExistsText(item.LocalCopyPath), ExistsText(item.RemoteCopyPath), item.ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss") }) { Tag = item };
                    _conflicts.Items.Add(row);
                }
                _conflicts.EndUpdate();

                _history.BeginUpdate(); _history.Items.Clear();
                foreach (var item in data.History)
                {
                    var row = new ListViewItem(new[] { item.Kind, item.LogicalPath, item.ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss") }) { Tag = item };
                    _history.Items.Add(row);
                }
                _history.EndUpdate();
                _summary.Text = string.Format("待应用 {0} 项　冲突 {1} 项　历史版本 {2} 项", data.Pending.Count, data.Conflicts.Count, data.History.Count);
                _summary.ForeColor = data.Pending.Count > 0 || data.Conflicts.Count > 0 ? Color.DarkOrange : Color.FromArgb(34, 120, 72);
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void ApplyPending(object sender, EventArgs e)
        {
            try
            {
                var applied = _service.ApplyPending();
                MessageBox.Show(this, applied == 0 ? "没有可应用的项目；正在 CAD 中打开的 DWG 会继续保留。" : "已安全应用 " + applied + " 项。", Text);
                ReloadData();
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void DiscardPending(object sender, EventArgs e)
        {
            var item = SelectedTag<CloudSyncCenterItem>(_pending); if (item == null) return;
            if (MessageBox.Show(this, "确定放弃该待应用版本？\r\n\r\n" + item.LogicalPath, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { _service.DiscardPending(item); ReloadData(); }
            catch (Exception exception) { ShowError(exception); }
        }

        private void ResolveConflict(bool useLocal)
        {
            var item = SelectedTag<CloudSyncConflictItem>(_conflicts); if (item == null) return;
            var choice = useLocal ? "本机版本" : "共享版本";
            if (MessageBox.Show(this, "确定采用“" + choice + "”作为正式版本？\r\n当前正式文件会先自动备份。\r\n\r\n" + item.LogicalPath,
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { _service.ResolveConflict(item, useLocal); ReloadData(); }
            catch (Exception exception) { ShowError(exception); }
        }

        private void RestoreHistory(object sender, EventArgs e)
        {
            var item = SelectedTag<CloudSyncCenterItem>(_history); if (item == null) return;
            if (MessageBox.Show(this, "确定恢复这个历史版本到本机？\r\n当前版本会先自动备份。\r\n\r\n" + item.LogicalPath + "\r\n" + item.ModifiedAt,
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { _service.RestoreHistory(item); ReloadData(); }
            catch (Exception exception) { ShowError(exception); }
        }

        private void OpenSelected(ListView list)
        {
            if (list.SelectedItems.Count == 0) return;
            var center = list.SelectedItems[0].Tag as CloudSyncCenterItem;
            var conflict = list.SelectedItems[0].Tag as CloudSyncConflictItem;
            var path = center != null ? center.FilePath : conflict != null ? (conflict.LocalCopyPath ?? conflict.RemoteCopyPath) : null;
            try
            {
                var folder = _service.FolderFor(path);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) Process.Start("explorer.exe", folder);
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void ShowError(Exception exception)
        {
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static T SelectedTag<T>(ListView list) where T : class
        {
            return list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as T;
        }

        private static string ExistsText(string path) { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? "有" : "无"; }
        private static TabPage Page(string text) { return new TabPage(text) { Padding = new Padding(8) }; }
        private static FlowLayoutPanel ActionBar() { return new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 8, 0, 0) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 8, 0) }; }
        private static ListView CreateList(params string[] columns)
        {
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
            foreach (var column in columns) list.Columns.Add(column, column.Contains("逻辑") ? 430 : column.Contains("副本") ? 100 : 150);
            return list;
        }
    }
}
