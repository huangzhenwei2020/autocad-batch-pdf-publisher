using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class CloudSyncCenterForm : Form
    {
        private CloudSyncCenterService _service = new CloudSyncCenterService();
        private readonly Label _summary = new Label { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 8, 0, 0) };
        private readonly ListView _pending = CreateList("分类", "状态", "文件", "用途", "更新时间");
        private readonly ListView _conflicts = CreateList("分类", "文件", "用途", "本机副本", "共享副本", "发生时间");
        private readonly ListView _history = CreateList("分类", "来源", "文件", "用途", "版本时间");
        private readonly ListView _projects = CreateList("项目名称", "本机状态", "云端状态", "本机目录");
        private readonly TextBox _workspaceRoot = new TextBox { Dock = DockStyle.Fill };
        private readonly CheckBox _showArchived = new CheckBox { Text = "显示已归档项目", AutoSize = true, Margin = new Padding(12, 7, 3, 3) };

        public CloudSyncCenterForm()
        {
            Text = "万落建筑工具 · 同步中心";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(920, 580);
            Size = new Size(1080, 680);
            Font = new Font("Microsoft YaHei UI", 9F);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildProjectTab());
            tabs.TabPages.Add(BuildPendingTab());
            tabs.TabPages.Add(BuildConflictTab());
            tabs.TabPages.Add(BuildHistoryTab());
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var close = ButtonFor("关闭"); close.Click += delegate { Close(); };
            var refresh = ButtonFor("刷新"); refresh.Click += delegate { ReloadData(); };
            var backups = ButtonFor("打开备份文件夹"); backups.Click += OpenBackupFolder;
            footer.Controls.Add(close); footer.Controls.Add(refresh); footer.Controls.Add(backups);
            Controls.Add(tabs); Controls.Add(_summary); Controls.Add(footer);
            Shown += delegate { ReloadData(); };
        }

        private TabPage BuildPendingTab()
        {
            var page = Page("待应用"); page.Controls.Add(_pending);
            page.Controls.Add(HelpText("CAD 正在使用的文件不会被直接覆盖，会先放在这里；关闭对应图纸后可安全应用。"));
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
            page.Controls.Add(HelpText("同一文件在两台电脑都被修改时会保留两个副本，请确认采用哪一个，原文件会先备份。"));
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
            page.Controls.Add(HelpText("这里保存同步前的旧版本。恢复时当前文件仍会先备份，不会直接丢失。"));
            var bar = ActionBar();
            var restore = ButtonFor("恢复所选版本到本机"); restore.Click += RestoreHistory;
            var open = ButtonFor("打开位置"); open.Click += delegate { OpenSelected(_history); };
            bar.Controls.Add(restore); bar.Controls.Add(open);
            page.Controls.Add(bar); return page;
        }

        private TabPage BuildProjectTab()
        {
            var page = Page("项目同步");
            _projects.CheckBoxes = true;
            page.Controls.Add(_projects);
            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 72, ColumnCount = 3, Padding = new Padding(0, 0, 0, 6) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(new Label { Text = "统一工作总目录", AutoSize = true, Margin = new Padding(3, 8, 8, 3) }, 0, 0);
            top.Controls.Add(_workspaceRoot, 1, 0);
            var choose = ButtonFor("选择目录"); choose.Click += BrowseWorkspace; top.Controls.Add(choose, 2, 0);
            var hint = HelpText("先同步项目资料即可看到云端项目；只勾选这台电脑需要工作的项目，DWG 和资料才会下载到工作总目录。", 34);
            top.Controls.Add(hint, 0, 1); top.SetColumnSpan(hint, 3);
            page.Controls.Add(top);
            var bar = ActionBar();
            var save = ButtonFor("保存选择并同步"); save.Click += SaveProjectSelection;
            var all = ButtonFor("全选"); all.Click += delegate { SetAllProjects(true); };
            var none = ButtonFor("全不选"); none.Click += delegate { SetAllProjects(false); };
            var consolidate = ButtonFor("一键统一目录"); consolidate.Click += ConsolidateProjects;
            var archive = ButtonFor("归档云端项目"); archive.Click += delegate { SetSelectedProjectArchived(true); };
            var restore = ButtonFor("恢复归档项目"); restore.Click += delegate { SetSelectedProjectArchived(false); };
            _showArchived.CheckedChanged += delegate { ReloadProjects(); };
            bar.Controls.Add(save); bar.Controls.Add(all); bar.Controls.Add(none); bar.Controls.Add(consolidate); bar.Controls.Add(archive); bar.Controls.Add(restore); bar.Controls.Add(_showArchived);
            page.Controls.Add(bar);
            return page;
        }

        private void ReloadData()
        {
            try
            {
                _service = new CloudSyncCenterService();
                var data = _service.Load();
                ReloadProjects();
                _pending.BeginUpdate(); _pending.Items.Clear();
                foreach (var item in data.Pending)
                {
                    var row = new ListViewItem(new[] { item.Category, item.Kind, item.DisplayPath, item.Purpose, item.ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss") }) { Tag = item };
                    _pending.Items.Add(row);
                }
                _pending.EndUpdate();

                _conflicts.BeginUpdate(); _conflicts.Items.Clear();
                foreach (var item in data.Conflicts)
                {
                    var row = new ListViewItem(new[] { item.Category, item.DisplayPath, item.Purpose, ExistsText(item.LocalCopyPath), ExistsText(item.RemoteCopyPath), item.ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss") }) { Tag = item };
                    _conflicts.Items.Add(row);
                }
                _conflicts.EndUpdate();

                _history.BeginUpdate(); _history.Items.Clear();
                foreach (var item in data.History)
                {
                    var row = new ListViewItem(new[] { item.Category, item.Kind, item.DisplayPath, item.Purpose, item.ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss") }) { Tag = item };
                    _history.Items.Add(row);
                }
                _history.EndUpdate();
                _summary.Text = string.Format("待应用 {0} 项　冲突 {1} 项　历史版本 {2} 项", data.Pending.Count, data.Conflicts.Count, data.History.Count);
                _summary.ForeColor = data.Pending.Count > 0 || data.Conflicts.Count > 0 ? Color.DarkOrange : Color.FromArgb(34, 120, 72);
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void ReloadProjects()
        {
            var settings = new CloudSyncSettingsStore().LoadSettings();
            var root = CloudProjectWorkspaceService.GetWorkspaceRoot(settings);
            _workspaceRoot.Text = root;
            var local = new PublishPlanStore().LoadProjects();
            var cloud = ProjectSyncProjectionStore.DiscoverCloudProjects(_showArchived.Checked);
            var mappings = settings.ProjectMappings ?? new System.Collections.Generic.List<CloudSyncProjectMapping>();
            var rows = local.Select(project => new ProjectSelectionRow
            {
                Name = project.Name,
                CloudId = ProjectSyncProjectionStore.StableProjectId(project.Name),
                Folder = project.ProjectFolder,
                IsLocal = true,
                IsArchived = ProjectSyncProjectionStore.IsCloudProjectArchived(ProjectSyncProjectionStore.StableProjectId(project.Name)),
                IsCloud = cloud.Any(remote => string.Equals(remote.ProjectName, project.Name, StringComparison.OrdinalIgnoreCase))
            }).Concat(cloud.Where(remote => !local.Any(project => string.Equals(project.Name, remote.ProjectName, StringComparison.OrdinalIgnoreCase)))
                .Select(remote => new ProjectSelectionRow
                {
                    Name = remote.ProjectName,
                    CloudId = remote.CloudId,
                    Folder = CloudProjectWorkspaceService.ProjectFolderFor(settings, remote.ProjectName),
                    IsLocal = false,
                    IsCloud = true,
                    IsArchived = remote.IsArchived
                })).OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _projects.BeginUpdate(); _projects.Items.Clear();
            foreach (var row in rows)
            {
                var mapping = mappings.FirstOrDefault(candidate => candidate != null && string.Equals(candidate.CloudId, row.CloudId, StringComparison.OrdinalIgnoreCase));
                var unified = CloudProjectWorkspaceService.IsUnderWorkspace(row.Folder, root);
                var localStatus = row.IsLocal ? (Directory.Exists(row.Folder) ? (unified ? "已有" : "目录待统一") : "登记存在，目录缺失") : "无";
                var cloudStatus = row.IsArchived ? "已归档" : row.IsCloud ? "已有，可下载" : "无，勾选后上传";
                var item = new ListViewItem(new[] { row.Name, localStatus, cloudStatus, row.Folder }) { Tag = row, Checked = mapping != null && mapping.Enabled };
                if (row.IsArchived) { item.ForeColor = Color.DimGray; item.Checked = false; }
                else if (!unified) item.ForeColor = Color.DarkOrange;
                _projects.Items.Add(item);
            }
            _projects.EndUpdate();
        }

        private void SaveProjectSelection(object sender, EventArgs e)
        {
            try
            {
                var settingsStore = new CloudSyncSettingsStore();
                var settings = settingsStore.LoadSettings();
                settings.ProjectWorkspaceRoot = Path.GetFullPath(_workspaceRoot.Text.Trim());
                var mappings = new System.Collections.Generic.List<CloudSyncProjectMapping>();
                foreach (ListViewItem item in _projects.Items)
                {
                    var row = item.Tag as ProjectSelectionRow;
                    if (row == null) continue;
                    if (row.IsArchived && item.Checked) throw new InvalidOperationException("项目“" + row.Name + "”已归档，请先恢复后再同步。");
                    var folder = row.IsLocal ? row.Folder : CloudProjectWorkspaceService.ProjectFolderFor(settings, row.Name);
                    if (item.Checked && !CloudProjectWorkspaceService.IsUnderWorkspace(folder, settings.ProjectWorkspaceRoot))
                        throw new InvalidOperationException("项目“" + row.Name + "”不在统一工作总目录中，请先点击“一键统一目录”。");
                    mappings.Add(new CloudSyncProjectMapping { ProjectName = row.Name, CloudId = row.CloudId, LocalFolder = folder, Enabled = item.Checked });
                }
                settings.ProjectMappings = mappings;
                settings.SyncProjectFiles = mappings.Any(item => item.Enabled);
                settingsStore.SaveSettings(settings);
                new PublishPlanStore().LoadProjects();
                CloudSyncCoordinator.QueueReload(settings.Enabled);
                MessageBox.Show(this, "已保存。勾选的项目会同步到统一工作总目录；未勾选项目只保留云端，不占用本机空间。", Text);
                ReloadData();
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void ConsolidateProjects(object sender, EventArgs e)
        {
            try
            {
                var settingsStore = new CloudSyncSettingsStore();
                var settings = settingsStore.LoadSettings();
                settings.ProjectWorkspaceRoot = Path.GetFullPath(_workspaceRoot.Text.Trim());
                var projects = new PublishPlanStore().LoadProjects();
                var preview = CloudProjectWorkspaceService.AnalyzeConsolidation(projects, settings);
                if (MessageBox.Show(this, "准备归拢 " + preview.ProjectCount + " 个项目，共约 " + preview.RequiredText + "。\r\n"
                    + "目标磁盘可用 " + preview.AvailableText + "。\r\n\r\n原目录不会删除，成功后将自动选中全部本机项目。",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Cursor = Cursors.WaitCursor;
                var result = CloudProjectWorkspaceService.ConsolidateAll(new PublishPlanStore(), settings);
                settings.SyncProjectFiles = true;
                settingsStore.SaveSettings(settings);
                CloudSyncCoordinator.QueueReload(settings.Enabled);
                var message = "已归拢 " + result.MovedProjects.Count + " 个项目。";
                if (result.Errors.Count > 0) message += "\r\n\r\n未处理：\r\n" + string.Join("\r\n", result.Errors);
                MessageBox.Show(this, message, Text, MessageBoxButtons.OK, result.Errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                ReloadData();
            }
            catch (Exception exception) { ShowError(exception); }
            finally { Cursor = Cursors.Default; }
        }

        private void BrowseWorkspace(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择所有 BPP 项目统一存放的工作总目录" })
                if (dialog.ShowDialog(this) == DialogResult.OK) _workspaceRoot.Text = dialog.SelectedPath;
        }

        private void OpenBackupFolder(object sender, EventArgs e)
        {
            try
            {
                var settings = new CloudSyncSettingsStore().LoadSettings();
                var path = CloudBackupService.GetBackupRoot(settings);
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception exception) { ShowError(exception); }
        }

        private void SetAllProjects(bool value)
        {
            foreach (ListViewItem item in _projects.Items)
            {
                var row = item.Tag as ProjectSelectionRow;
                item.Checked = value && (row == null || !row.IsArchived);
            }
        }

        private void SetSelectedProjectArchived(bool archived)
        {
            if (_projects.SelectedItems.Count == 0) { MessageBox.Show(this, "请先选择一个项目。", Text); return; }
            var row = _projects.SelectedItems[0].Tag as ProjectSelectionRow;
            if (row == null || row.IsArchived == archived) return;
            var action = archived ? "归档" : "恢复";
            var explanation = archived
                ? "归档后该项目不会出现在新电脑的默认下载列表中，云端文件和本机文件都不会删除。"
                : "恢复后项目会重新出现在云端项目列表中，但不会自动下载，需要重新勾选。";
            if (MessageBox.Show(this, "确定" + action + "项目“" + row.Name + "”？\r\n\r\n" + explanation,
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                ProjectSyncProjectionStore.SetCloudProjectArchived(row.CloudId, archived);
                var store = new CloudSyncSettingsStore(); var settings = store.LoadSettings();
                foreach (var mapping in settings.ProjectMappings ?? new System.Collections.Generic.List<CloudSyncProjectMapping>())
                    if (mapping != null && string.Equals(mapping.CloudId, row.CloudId, StringComparison.OrdinalIgnoreCase)) mapping.Enabled = false;
                store.SaveSettings(settings); CloudSyncCoordinator.QueueReload(settings.Enabled); ReloadData();
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
        private sealed class ProjectSelectionRow
        {
            public string Name { get; set; }
            public string CloudId { get; set; }
            public string Folder { get; set; }
            public bool IsLocal { get; set; }
            public bool IsArchived { get; set; }
            public bool IsCloud { get; set; }
        }

        private static TabPage Page(string text) { return new TabPage(text) { Padding = new Padding(8) }; }
        private static Label HelpText(string text, int height = 42) { return new Label { Text = text, Dock = DockStyle.Top, Height = height, Padding = new Padding(4, 7, 4, 4), ForeColor = Color.DimGray }; }
        private static FlowLayoutPanel ActionBar() { return new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 8, 0, 0) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 8, 0) }; }
        private static ListView CreateList(params string[] columns)
        {
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
            foreach (var column in columns)
                list.Columns.Add(column, column == "文件" || column == "本机目录" ? 330 : column == "用途" ? 300 : column.Contains("状态") ? 135 : column.Contains("副本") ? 90 : 130);
            return list;
        }
    }
}
