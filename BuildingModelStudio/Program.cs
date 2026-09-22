using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.BuildingModel;

namespace Wanluo.BuildingModelStudio
{
    /// <summary>
    /// "建筑模型"程序（P0 骨架）。
    ///
    /// P0 只做一件事：把模型的参数算成一张张二维视图（views/*.json），供 CAD 侧的
    /// "落图"命令画进 DWG。界面故意做得很小——这一版要验证的是**链路**，
    /// 不是交互（平面草图编辑器是 P1.5，三维视口是 P4）。
    ///
    /// 程序与插件共用 Shared\BuildingModel 下的纯逻辑源码，不新增 DLL。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 命令行模式：不弹窗口，直接生成样例模型与视图（便于自动化与"我没开程序也能试"）。
            //   dotnet 万落建筑模型.dll --generate [<项目文件夹>] [<模型名称>]
            if (args != null && args.Length > 0 && string.Equals(args[0], "--generate", StringComparison.OrdinalIgnoreCase))
            {
                var folder = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
                    ? args[1]
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "万落建筑项目", "建筑模型样例");
                var name = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : "样例-两层小房子";
                try
                {
                    var lines = GenerateSample(folder, name, Console.WriteLine);
                    Console.WriteLine("完成：" + lines + " 条线。");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("生成失败：" + exception);
                    Environment.ExitCode = 1;
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        /// <summary>生成样例模型与默认视图，返回生成的线条总数。</summary>
        internal static int GenerateSample(string projectFolder, string modelName, Action<string> log)
        {
            var model = SampleModelFactory.CreateTwoStoreyHouse();
            model.Name = modelName;
            var modelPath = BuildingModelJson.ModelFilePath(projectFolder, modelName);
            BuildingModelJson.SaveModel(modelPath, model);
            log("模型：" + modelPath + "（楼层 " + model.Storeys.Count + "、墙 " + model.Walls.Count
                + "、洞口 " + model.Openings.Count + "、楼板 " + model.Slabs.Count + "、柱 " + model.Columns.Count + "）");

            var total = 0;
            foreach (var definition in SampleModelFactory.CreateDefaultViews(modelName))
            {
                var view = OrthographicProjector.Project(model, definition);
                var path = BuildingModelJson.ViewFilePath(projectFolder, modelName, view.Id);
                BuildingModelJson.SaveView(path, view);
                total += view.Lines.Count;
                log("视图：" + view.Title + " → 线 " + view.Lines.Count + "、文字 " + view.Texts.Count
                    + "、填充 " + view.Hatches.Count + " → " + path);
            }
            return total;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly TextBox _projectFolder = new TextBox();
        private readonly TextBox _modelName = new TextBox();
        private readonly TextBox _log = new TextBox();
        private BuildingModelDocument _model;

        public MainForm()
        {
            Text = "万落建筑模型 · P0（模型 → 视图）";
            Width = 900;
            Height = 620;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderRow.Controls.Add(new Label { Text = "项目文件夹", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            _projectFolder.Dock = DockStyle.Fill;
            _projectFolder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "万落建筑项目", "建筑模型样例");
            folderRow.Controls.Add(_projectFolder, 1, 0);
            var browse = new Button { Text = "选择…", AutoSize = true };
            browse.Click += (s, e) => ChooseFolder();
            folderRow.Controls.Add(browse, 2, 0);
            root.Controls.Add(folderRow, 0, 0);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 8, 0, 8) };
            actions.Controls.Add(Field("模型名称", 132));
            actions.Controls.Add(Button("新建样例模型", CreateSampleModel, true));
            actions.Controls.Add(Button("生成全部视图（4 立面 + 1 剖面）", GenerateViews, true));
            actions.Controls.Add(Button("打开模型目录", OpenModelFolder));
            actions.Controls.Add(Button("打开视图文件所在目录", OpenViewsFolder));
            root.Controls.Add(actions, 0, 1);

            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.ReadOnly = true;
            _log.BackColor = Color.FromArgb(250, 250, 252);
            root.Controls.Add(_log, 0, 2);

            Log("P0 骨架：新建样例模型 → 生成视图 → 回到 CAD 执行 LTTZ 落图。");
            Log("视图文件会写到：" + ViewsFolder());
        }

        private Control Field(string label, int width)
        {
            var host = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 3, 8, 0) };
            host.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            _modelName.Width = width;
            _modelName.Text = "样例-两层小房子";
            host.Controls.Add(_modelName);
            return host;
        }

        private Button Button(string text, Action action, bool accent = false)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 32,
                Margin = new Padding(0, 3, 8, 0),
                BackColor = accent ? Color.FromArgb(24, 167, 201) : Color.White,
                ForeColor = accent ? Color.White : Color.FromArgb(18, 52, 91),
                FlatStyle = FlatStyle.Flat
            };
            button.Click += (s, e) =>
            {
                try { action(); }
                catch (Exception exception) { Log("出错：" + exception.Message); MessageBox.Show(this, exception.Message, text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            return button;
        }

        private string ModelName { get { return string.IsNullOrWhiteSpace(_modelName.Text) ? "样例-两层小房子" : _modelName.Text.Trim(); } }
        private string ModelPath { get { return BuildingModelJson.ModelFilePath(_projectFolder.Text, ModelName); } }
        private string ViewsFolder() { return BuildingModelJson.ViewsFolder(_projectFolder.Text, ModelName); }

        private void ChooseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择项目文件夹（视图会写到 <项目文件夹>\\建筑模型\\<模型名称>\\views）", SelectedPath = _projectFolder.Text })
                if (dialog.ShowDialog(this) == DialogResult.OK) _projectFolder.Text = dialog.SelectedPath;
        }

        private void CreateSampleModel()
        {
            _model = SampleModelFactory.CreateTwoStoreyHouse();
            _model.Name = ModelName;
            BuildingModelJson.SaveModel(ModelPath, _model);
            Log("已建立样例模型：" + ModelPath);
            Log("  楼层 " + _model.Storeys.Count + " 个、墙 " + _model.Walls.Count + " 道、洞口 " + _model.Openings.Count
                + " 个、楼板 " + _model.Slabs.Count + " 块、柱 " + _model.Columns.Count + " 根");
        }

        private void GenerateViews()
        {
            if (_model == null)
            {
                if (!File.Exists(ModelPath)) { Log("还没有模型，请先点「新建样例模型」。"); return; }
                _model = BuildingModelJson.LoadModel(ModelPath);
            }
            var definitions = SampleModelFactory.CreateDefaultViews(ModelName);
            var total = 0;
            foreach (var definition in definitions)
            {
                var view = OrthographicProjector.Project(_model, definition);
                var path = BuildingModelJson.ViewFilePath(_projectFolder.Text, ModelName, view.Id);
                BuildingModelJson.SaveView(path, view);
                total += view.Lines.Count;
                Log("视图「" + view.Title + "」：线 " + view.Lines.Count + "、文字 " + view.Texts.Count
                    + "、填充 " + view.Hatches.Count + " → " + Path.GetFileName(path)
                    + (view.Warnings.Count == 0 ? string.Empty : "（提示：" + string.Join("；", view.Warnings.ToArray()) + "）"));
            }
            Log("共生成 " + definitions.Count + " 张视图，合计 " + total + " 条线。回到 CAD 执行 LTTZ 落图。");
        }

        /// <summary>样例生成（与命令行模式共用同一实现）。</summary>
        internal static int GenerateSampleFor(string projectFolder, string modelName, Action<string> log)
        {
            return Program.GenerateSample(projectFolder, modelName, log);
        }

        private void OpenModelFolder()
        {
            var folder = Path.GetDirectoryName(ModelPath);
            if (!Directory.Exists(folder)) { Log("目录还不存在：" + folder); return; }
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }

        private void OpenViewsFolder()
        {
            var folder = ViewsFolder();
            if (!Directory.Exists(folder)) { Log("视图目录还不存在：" + folder); return; }
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }

        private void Log(string message)
        {
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        }
    }
}
