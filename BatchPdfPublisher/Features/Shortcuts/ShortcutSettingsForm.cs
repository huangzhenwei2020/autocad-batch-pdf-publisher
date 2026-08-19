using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class ShortcutSettingsForm : DpiAwareForm
    {
        private readonly DataGridView _grid = new DataGridView();
        private readonly BindingSource _source = new BindingSource();

        public ShortcutSettingsForm()
        {
            Text = "快捷键设置";
            Width = 780; Height = 600; MinimumSize = new Size(620, 420);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9F);
            BuildUi();
            LoadRows(BatchPdfPublisher.Services.ShortcutSettingsService.Load());
        }

        private void BuildUi()
        {
            var hint = new Label { Dock = DockStyle.Top, Height = 50, Padding = new Padding(12, 10, 12, 4), Text = "修改后立即在当前 CAD 生效。新功能只需加入统一功能登记表，就会自动出现在这里。快捷键必须以字母开头，长度 2–16 位。" };
            _grid.Dock = DockStyle.Fill; _grid.AutoGenerateColumns = false; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; _grid.DataSource = _source;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "功能模块", DataPropertyName = "Group", ReadOnly = true, FillWeight = 24 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "功能名称", DataPropertyName = "Name", ReadOnly = true, FillWeight = 28 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "内部命令", DataPropertyName = "Command", ReadOnly = true, FillWeight = 22 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "快捷键", DataPropertyName = "Shortcut", FillWeight = 20 });
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
            var save = new Button { Text = "保存并应用", AutoSize = true }; save.Click += Save;
            var close = new Button { Text = "关闭", AutoSize = true }; close.Click += (s, e) => Close();
            var reset = new Button { Text = "恢复默认", AutoSize = true }; reset.Click += (s, e) => LoadRows(BatchPdfPublisher.Services.ShortcutSettingsService.Defaults());
            buttons.Controls.Add(save); buttons.Controls.Add(close); buttons.Controls.Add(reset);
            Controls.Add(_grid); Controls.Add(buttons); Controls.Add(hint);
        }

        private void LoadRows(IDictionary<string, string> values)
        {
            var rows = BatchPdfPublisher.Services.FeatureRegistry.All.Select(x => new ShortcutRow
            {
                Id = x.Id, Group = x.Group, Name = x.Name, Command = x.Command,
                Shortcut = values.ContainsKey(x.Id) ? values[x.Id] : x.DefaultShortcut
            }).ToList();
            _source.DataSource = rows;
        }

        private void Save(object sender, EventArgs e)
        {
            try
            {
                _grid.EndEdit(); _source.EndEdit();
                var values = _source.List.Cast<ShortcutRow>().ToDictionary(x => x.Id, x => x.Shortcut, StringComparer.OrdinalIgnoreCase);
                BatchPdfPublisher.Services.ShortcutSettingsService.Save(values);
                BatchPdfPublisher.Services.ShortcutAliasService.Refresh();
                MessageBox.Show("快捷键已保存并应用到当前 CAD。Ribbon 和菜单中的快捷键文字也已刷新。", "快捷键设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception) { MessageBox.Show(exception.Message, "快捷键设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private sealed class ShortcutRow
        {
            public string Id { get; set; }
            public string Group { get; set; }
            public string Name { get; set; }
            public string Command { get; set; }
            public string Shortcut { get; set; }
        }
    }
}
