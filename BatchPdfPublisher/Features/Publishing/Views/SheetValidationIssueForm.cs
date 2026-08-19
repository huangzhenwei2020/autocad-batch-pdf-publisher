using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class SheetValidationIssueForm : DpiAwareForm
    {
        private readonly List<SheetValidationIssue> _issues;
        private readonly ListBox _list = new ListBox();
        private readonly Action<SheetValidationIssue> _locate;
        private readonly Action<SheetValidationIssue> _edit;

        public SheetValidationIssueForm(IEnumerable<SheetValidationIssue> issues, Action<SheetValidationIssue> locate, Action<SheetValidationIssue> edit)
        {
            _issues = (issues ?? Enumerable.Empty<SheetValidationIssue>()).Where(x => x?.Sheet != null).ToList();
            _locate = locate; _edit = edit;
            Text = "发布前图框检查"; Width = 900; Height = 520; StartPosition = FormStartPosition.CenterScreen;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = "发现 " + _issues.Count + " 个图框问题。双击问题或点击“定位图框”可切换到对应 DWG 和布局。", AutoSize = true, ForeColor = Color.DarkRed }, 0, 0);
            _list.Dock = DockStyle.Fill; _list.HorizontalScrollbar = true; _list.DataSource = _issues.Select(x => new SheetValidationIssueDisplay(x)).ToList(); _list.DisplayMember = nameof(SheetValidationIssueDisplay.Text);
            _list.DoubleClick += (s, e) => LocateSelected(); root.Controls.Add(_list, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = "关闭", AutoSize = true }; close.Click += (s, e) => Close(); buttons.Controls.Add(close);
            var editButton = new Button { Text = "修改图框登记", AutoSize = true }; editButton.Click += (s, e) => EditSelected(); buttons.Controls.Add(editButton);
            var locateButton = new Button { Text = "定位图框", AutoSize = true }; locateButton.Click += (s, e) => LocateSelected(); buttons.Controls.Add(locateButton);
            root.Controls.Add(buttons, 0, 2); Controls.Add(root);
        }

        private SheetValidationIssue Selected => (_list.SelectedItem as SheetValidationIssueDisplay)?.Issue;
        private void LocateSelected() { if (Selected != null) _locate?.Invoke(Selected); }
        private void EditSelected() { if (Selected != null) _edit?.Invoke(Selected); }

        private sealed class SheetValidationIssueDisplay
        {
            public string Text { get; }
            public SheetValidationIssue Issue { get; }
            public SheetValidationIssueDisplay(SheetValidationIssue issue) { Issue = issue; Text = issue?.Sheet == null ? string.Empty : System.IO.Path.GetFileName(issue.Sheet.SourceFile) + " / " + (issue.Sheet.SourceLayout ?? "模型空间") + " / " + issue.Sheet.FrameDisplay + " / " + issue.Sheet.SheetNumber + " " + issue.Sheet.SheetName + "：" + issue.Message; }
        }
    }
}
