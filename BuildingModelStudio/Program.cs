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
    /// "建筑模型"程序（P1.5：平面草图编辑器）。
    ///
    /// 这一版的核心承诺：**不用开 CAD，就能把平面画出来，而且画出来的就是模型**。
    /// 画完直接生成视图（views/*.json），回到 CAD 用 LTTZ 落图。
    ///
    /// 分层：画布只负责交互与显示，编辑规则在 Shared\BuildingModel\PlanEditing（纯逻辑、可测）。
    /// </summary>
    internal static class Program
    {
        /// <summary>命令行模式（生成/自检）：出错只写日志，不弹对话框。</summary>
        internal static bool Headless;

        [STAThread]
        private static void Main(string[] args)
        {
            // 命令行模式：不弹窗口，直接生成样例模型与视图。
            //   dotnet 万落建筑模型.dll --generate [<项目文件夹>] [<模型名称>]
            if (args != null && args.Length > 0 && string.Equals(args[0], "--generate", StringComparison.OrdinalIgnoreCase))
            {
                Headless = true;
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

            // 界面线程兜底：以后任何没被处理的异常都写日志 + 中文提示，
            // 不再弹 .NET 那串"应用程序的组件中发生了未经处理的异常"英文堆栈。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, args) => CrashLog.Report(args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (sender, args) => CrashLog.Report(args.ExceptionObject as Exception);

            // 画布自检：不弹窗口，把平面画布真正画到离屏位图上（含极端比例、坏模型、坏 Graphics）。
            //   dotnet 万落建筑模型.dll --selftest-canvas
            if (args != null && args.Length > 0 && string.Equals(args[0], "--selftest-canvas", StringComparison.OrdinalIgnoreCase))
            {
                Headless = true;
                try
                {
                    CanvasSelfTest.Run(Console.WriteLine);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("FAIL " + exception);
                    Environment.ExitCode = 1;
                }
                return;
            }

            // 自检模式：把主窗口真正构造并显示一次，确认布局与画布不会抛异常。
            //   dotnet 万落建筑模型.dll --selftest
            if (args != null && args.Length > 0 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                Headless = true;
                try
                {
                    using (var form = new MainForm())
                    {
                        form.Show();
                        Application.DoEvents();
                        var canvas = FindCanvas(form);
                        if (canvas == null) throw new InvalidOperationException("主窗口里没有找到画布控件。");
                        if (canvas.Model == null) throw new InvalidOperationException("画布没有加载模型。");
                        if (canvas.Model.Storeys.Count == 0) throw new InvalidOperationException("模型没有楼层。");
                        // 真正走一次窗口绘制（WM_PAINT）：以前 DrawGrid 会在这里死循环并抛 OverflowException
                        canvas.Refresh();
                        Application.DoEvents();
                        if (canvas.LastPaintError != null)
                            throw new InvalidOperationException("画布真实绘制失败：" + canvas.LastPaintError);
                        Console.WriteLine("PASS 主窗口构造与画布装载：楼层 " + canvas.Model.Storeys.Count
                            + "、墙 " + canvas.Model.Walls.Count + "、洞口 " + canvas.Model.Openings.Count
                            + "（已真实绘制一次，无异常）");
                        form.Close();
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("FAIL " + exception);
                    Environment.ExitCode = 1;
                }
                return;
            }

            Application.Run(new MainForm());
        }

        private static PlanCanvas FindCanvas(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                var canvas = child as PlanCanvas;
                if (canvas != null) return canvas;
                var nested = FindCanvas(child);
                if (nested != null) return nested;
            }
            return null;
        }

        internal static int GenerateSample(string projectFolder, string modelName, Action<string> log)
        {
            var model = SampleModelFactory.CreateTwoStoreyHouse();
            model.Name = modelName;
            var modelPath = BuildingModelJson.ModelFilePath(projectFolder, modelName);
            BuildingModelJson.SaveModel(modelPath, model);
            log("模型：" + modelPath + "（楼层 " + model.Storeys.Count + "、墙 " + model.Walls.Count
                + "、洞口 " + model.Openings.Count + "、楼板 " + model.Slabs.Count + "、柱 " + model.Columns.Count + "）");

            // 顺便放一份演示门窗类型库（只在没有时写，不覆盖 CAD 导出的真库）
            var libraryPath = BuildingModelJson.OpeningLibraryPath(projectFolder, modelName);
            if (!File.Exists(libraryPath))
            {
                var library = SampleModelFactory.CreateDemoOpeningLibrary();
                BuildingModelJson.SaveOpeningLibrary(libraryPath, library);
                log("演示门窗类型库：" + libraryPath + "（类型 " + library.Types.Count + " 个；真实项目请用 CAD 的 TQLX 导出）");
            }
            return GenerateViews(projectFolder, modelName, model, log);
        }

        /// <summary>按默认视图集合生成 views/*.json，返回线条总数。</summary>
        internal static int GenerateViews(string projectFolder, string modelName, BuildingModelDocument model, Action<string> log)
        {
            var total = 0;
            foreach (var definition in SampleModelFactory.CreateDefaultViews(modelName))
            {
                var view = OrthographicProjector.Project(model, definition);
                var path = BuildingModelJson.ViewFilePath(projectFolder, modelName, view.Id);
                BuildingModelJson.SaveView(path, view);
                total += view.Lines.Count;
                log("视图：" + view.Title + " → 线 " + view.Lines.Count + "、文字 " + view.Texts.Count
                    + "、填充 " + view.Hatches.Count + " → " + Path.GetFileName(path));
            }
            return total;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly PlanCanvas _canvas = new PlanCanvas();
        private readonly ListBox _storeys = new ListBox();
        private readonly TextBox _log = new TextBox();
        private readonly TextBox _projectFolder = new TextBox();
        private readonly TextBox _modelName = new TextBox();
        private readonly Label _status = new Label();
        private readonly Panel _properties = new Panel();
        private readonly ListBox _openingTypes = new ListBox();
        private readonly Label _openingLibraryInfo = new Label();
        private readonly Dictionary<string, Button> _toolButtons = new Dictionary<string, Button>();
        private BuildingModelDocument _model;
        private OpeningTypeLibraryDocument _openingLibrary;

        public MainForm()
        {
            Text = "万落建筑模型 · 平面草图（P1.5）";
            Width = 1280;
            Height = 800;
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // ── 顶部：文件与工具条 ───────────────────────────────────────────
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3, Padding = new Padding(8, 8, 8, 0) };
            var fileRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            fileRow.Controls.Add(FieldLabel("项目文件夹"));
            _projectFolder.Width = 300;
            _projectFolder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "万落建筑项目", "建筑模型样例");
            fileRow.Controls.Add(_projectFolder);
            fileRow.Controls.Add(Button("选择…", ChooseFolder, false));
            fileRow.Controls.Add(FieldLabel("模型名称"));
            _modelName.Width = 150;
            _modelName.Text = "样例-两层小房子";
            fileRow.Controls.Add(_modelName);
            fileRow.Controls.Add(Button("打开/新建", OpenOrCreateModel, true));
            fileRow.Controls.Add(Button("保存", SaveModel, true));
            fileRow.Controls.Add(Button("生成全部视图", GenerateViewsNow, true));
            fileRow.Controls.Add(Button("打开模型目录", OpenModelFolder, false));
            top.Controls.Add(fileRow, 0, 0);

            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 6) };
            tools.Controls.Add(ToolButton("选择", "select", true));
            tools.Controls.Add(ToolButton("画墙", "wall", false));
            tools.Controls.Add(ToolButton("放窗", "window", false));
            tools.Controls.Add(ToolButton("放门", "door", false));
            tools.Controls.Add(ToolButton("布柱", "column", false));
            tools.Controls.Add(Button("删除选中(Delete)", () => _canvas.DeleteSelection(), false));
            tools.Controls.Add(Button("撤销(Ctrl+Z)", () => _canvas.Undo(), false));
            tools.Controls.Add(Button("重做(Ctrl+Y)", () => _canvas.Redo(), false));
            tools.Controls.Add(Button("缩放适应(Ctrl+A)", () => { _canvas.ZoomExtents(); _canvas.Invalidate(); }, false));
            top.Controls.Add(tools, 0, 1);

            top.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(105, 112, 122),
                Text = "左键：执行当前工具　中键/右键拖动：平移　滚轮：缩放　Esc：取消　Delete：删除"
                    + "　（捕捉自动生效：端点 / 中点 / 正交 / 100mm 轴网）"
            }, 0, 2);
            root.Controls.Add(top, 0, 0);

            // ── 中部：画布 + 右侧面板 ────────────────────────────────────────
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6 };
            split.HandleCreated += (s, e) => { try { split.SplitterDistance = Math.Max(560, split.Width - 400); } catch { } };
            root.Controls.Add(split, 0, 1);
            _canvas.Dock = DockStyle.Fill;
            split.Panel1.Controls.Add(_canvas);

            var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(6) };
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 0 楼层标题
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));     // 1 楼层列表
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 2 楼层按钮
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 3 类型库标题
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));     // 4 类型库列表
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 5 类型库按钮
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 6 类型库说明
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // 7 构件属性
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));     // 8 日志
            side.Controls.Add(Title("楼层（列表按标高排序）"), 0, 0);
            _storeys.Dock = DockStyle.Fill;
            _storeys.IntegralHeight = false;
            _storeys.SelectedIndexChanged += (s, e) => SwitchStorey();
            side.Controls.Add(_storeys, 0, 1);
            var storeyButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            storeyButtons.Controls.Add(Button("增加楼层", AddStorey, false));
            storeyButtons.Controls.Add(Button("编辑标高/层高", EditStorey, false));
            storeyButtons.Controls.Add(Button("删除楼层", RemoveStorey, false));
            side.Controls.Add(storeyButtons, 0, 2);

            side.Controls.Add(Title("门窗类型库（来自 CAD 的 TQLX 导出）"), 0, 3);
            _openingTypes.Dock = DockStyle.Fill;
            _openingTypes.IntegralHeight = false;
            _openingTypes.SelectedIndexChanged += (s, e) => UseSelectedOpeningType();
            side.Controls.Add(_openingTypes, 0, 4);
            var openingButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            openingButtons.Controls.Add(Button("导入类型库…", ImportOpeningLibrary, true));
            openingButtons.Controls.Add(Button("刷新（模型目录）", () => LoadOpeningLibraryFromModelFolder(true), false));
            openingButtons.Controls.Add(Button("套用到选中洞口", ApplyTypeToSelection, false));
            openingButtons.Controls.Add(Button("取消选择类型", ClearOpeningType, false));
            side.Controls.Add(openingButtons, 0, 5);
            _openingLibraryInfo.AutoSize = true;
            _openingLibraryInfo.MaximumSize = new Size(360, 44);
            _openingLibraryInfo.ForeColor = Color.FromArgb(105, 112, 122);
            _openingLibraryInfo.Text = "未导入类型库：放门窗用默认尺寸（窗 1500×1800@900，门 900×2100）。";
            side.Controls.Add(_openingLibraryInfo, 0, 6);

            var propsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            propsPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            propsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            propsPanel.Controls.Add(Title("构件属性"), 0, 0);
            _properties.Dock = DockStyle.Fill;
            _properties.AutoScroll = true;
            propsPanel.Controls.Add(_properties, 0, 1);
            side.Controls.Add(propsPanel, 0, 7);

            var logPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            logPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var propsTools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            propsTools.Controls.Add(Button("应用属性", ApplyProperties, true));
            propsTools.Controls.Add(Button("删除构件", () => _canvas.DeleteSelection(), false));
            logPanel.Controls.Add(propsTools, 0, 0);
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.BackColor = Color.FromArgb(250, 250, 252);
            logPanel.Controls.Add(_log, 0, 1);
            side.Controls.Add(logPanel, 0, 4);
            split.Panel2.Controls.Add(side);

            // ── 底部状态栏 ───────────────────────────────────────────────────
            _status.AutoSize = true;
            _status.ForeColor = Color.FromArgb(70, 78, 88);
            _status.Padding = new Padding(8, 4, 8, 4);
            root.Controls.Add(_status, 0, 2);

            _canvas.StatusChanged += text => _status.Text = text;
            _canvas.SelectionChanged += ShowProperties;
            _canvas.StructureChanged += RefreshStoreys;
            _canvas.SaveRequested += SaveModel;

            Log("P1.5 平面草图：用「画墙 / 放窗 / 放门 / 布柱」把平面画出来，画的就是模型。");
            Log("门窗类型库：CAD 里执行 TQLX 导出 → 本程序「刷新（模型目录）」即可用它放门窗。");
            Log("画完点「生成全部视图」，回到 CAD 执行 LTTZ 落图。");
            OpenOrCreateModel();
        }

        private static Label Title(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), ForeColor = Color.FromArgb(20, 54, 99), Margin = new Padding(0, 6, 0, 4) };
        }

        private static Label FieldLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(6, 8, 2, 0) };
        }

        private Button Button(string text, Action action, bool accent)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 30,
                Margin = new Padding(0, 3, 6, 3),
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? Color.FromArgb(24, 167, 201) : Color.White,
                ForeColor = accent ? Color.White : Color.FromArgb(18, 52, 91)
            };
            button.Click += (s, e) =>
            {
                try { action(); }
                catch (Exception exception) { Log("出错：" + exception.Message); }
            };
            return button;
        }

        private Button ToolButton(string text, string tool, bool accent)
        {
            var button = Button(text, () => SelectTool(tool), accent);
            _toolButtons[tool] = button;
            return button;
        }

        private void SelectTool(string tool)
        {
            _canvas.Tool = tool;
            foreach (var pair in _toolButtons)
            {
                var active = pair.Key == tool;
                pair.Value.BackColor = active ? Color.FromArgb(24, 167, 201) : Color.White;
                pair.Value.ForeColor = active ? Color.White : Color.FromArgb(18, 52, 91);
            }
            Log("当前工具：" + ToolName(tool));
        }

        private static string ToolName(string tool)
        {
            if (tool == "wall") return "画墙（点两下画一道，可连续）";
            if (tool == "window") return "放窗（点在墙上）";
            if (tool == "door") return "放门（点在墙上）";
            if (tool == "column") return "布柱（点位置）";
            return "选择（点选/拖夹点）";
        }

        private string ModelName { get { return string.IsNullOrWhiteSpace(_modelName.Text) ? "样例-两层小房子" : _modelName.Text.Trim(); } }
        private string ModelPath { get { return BuildingModelJson.ModelFilePath(_projectFolder.Text, ModelName); } }

        private void ChooseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择项目文件夹", SelectedPath = _projectFolder.Text })
                if (dialog.ShowDialog(this) == DialogResult.OK) _projectFolder.Text = dialog.SelectedPath;
        }

        private void OpenOrCreateModel()
        {
            try
            {
                if (File.Exists(ModelPath))
                {
                    _model = BuildingModelJson.LoadModel(ModelPath);
                    Log("已打开模型：" + ModelPath);
                }
                else
                {
                    _model = SampleModelFactory.CreateEmptyModel(ModelName);
                    BuildingModelJson.SaveModel(ModelPath, _model);
                    Log("新建空模型（一层/二层，还没有墙）：" + ModelPath);
                }
                _model.Name = ModelName;
                _canvas.Model = _model;
                RefreshStoreys();
                if (_model.Storeys.Count > 0) _canvas.StoreyId = _model.Storeys[0].Id;
                if (_storeys.Items.Count > 0) _storeys.SelectedIndex = 0;
                Log("构件：墙 " + _model.Walls.Count + "、洞口 " + _model.Openings.Count
                    + "、柱 " + _model.Columns.Count + "、楼板 " + _model.Slabs.Count
                    + "、楼层 " + _model.Storeys.Count);
                LoadOpeningLibraryFromModelFolder(true);
            }
            catch (Exception exception) { Log("打开模型失败：" + exception.Message); }
        }

        private void SaveModel()
        {
            if (_model == null) return;
            try
            {
                BuildingModelJson.SaveModel(ModelPath, _model);
                _canvas.MarkSaved();
                Log("已保存：" + ModelPath + "（" + new FileInfo(ModelPath).Length + " 字节）");
            }
            catch (Exception exception) { Log("保存失败：" + exception.Message); }
        }

        private void GenerateViewsNow()
        {
            if (_model == null) { Log("还没有模型。"); return; }
            SaveModel();
            var total = Program.GenerateViews(_projectFolder.Text, ModelName, _model, Log);
            Log("生成完成，共 " + total + " 条线。回到 CAD 执行 LTTZ 落图（选 views 目录下的 json）。");
        }

        private void OpenModelFolder()
        {
            var folder = Path.GetDirectoryName(ModelPath);
            if (!Directory.Exists(folder)) { Log("目录还不存在：" + folder); return; }
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }

        // ───────────────────────── 门窗类型库 ─────────────────────────

        /// <summary>从模型目录读取 openings.json（CAD 里用 TQLX 导出的那份）。</summary>
        private void LoadOpeningLibraryFromModelFolder(bool log)
        {
            var path = BuildingModelJson.OpeningLibraryPath(_projectFolder.Text, ModelName);
            if (!File.Exists(path))
            {
                _openingLibrary = null;
                RefreshOpeningTypeList();
                _openingLibraryInfo.Text = "模型目录里还没有 openings.json：可在 CAD 里执行 TQLX 导出，或点「导入类型库…」。";
                if (log) Log("没有找到类型库：" + path);
                return;
            }
            try
            {
                _openingLibrary = BuildingModelJson.LoadOpeningLibrary(path);
                RefreshOpeningTypeList();
                _openingLibraryInfo.Text = "已载入：" + (_openingLibrary.ProjectName ?? "未命名项目")
                    + "，类型 " + _openingLibrary.Types.Count + " 个、做法模板 " + _openingLibrary.Templates.Count
                    + " 个（" + File.GetLastWriteTime(path).ToString("MM-dd HH:mm") + "）";
                if (log) Log("已载入门窗类型库：" + path + "（类型 " + _openingLibrary.Types.Count + " 个）");
            }
            catch (Exception exception)
            {
                _openingLibrary = null;
                RefreshOpeningTypeList();
                _openingLibraryInfo.Text = "类型库读取失败：" + exception.Message;
                if (log) Log("类型库读取失败：" + exception.Message);
            }
        }

        private void ImportOpeningLibrary()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "选择 CAD 导出的门窗类型库（openings.json）",
                Filter = "门窗类型库 (*.json)|*.json|所有文件 (*.*)|*.*",
                InitialDirectory = Path.GetDirectoryName(BuildingModelJson.OpeningLibraryPath(_projectFolder.Text, ModelName))
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var library = BuildingModelJson.LoadOpeningLibrary(dialog.FileName);
                    var target = BuildingModelJson.OpeningLibraryPath(_projectFolder.Text, ModelName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    BuildingModelJson.SaveOpeningLibrary(target, library);      // 复制到模型目录，下次自动载入
                    _openingLibrary = library;
                    RefreshOpeningTypeList();
                    _openingLibraryInfo.Text = "已导入并保存到模型目录：类型 " + library.Types.Count + " 个。";
                    Log("已导入类型库：" + dialog.FileName + " → " + target);
                }
                catch (Exception exception) { Log("导入失败：" + exception.Message); }
            }
        }

        private void RefreshOpeningTypeList()
        {
            var previous = _canvas.CurrentType == null ? null : _canvas.CurrentType.Code;
            _openingTypes.Items.Clear();
            if (_openingLibrary == null || _openingLibrary.Types == null) return;
            foreach (var type in _openingLibrary.Types)
            {
                if (type == null) continue;
                _openingTypes.Items.Add(type.Code + "　" + (type.Kind ?? "窗") + "　"
                    + type.Width.ToString("0") + "×" + type.Height.ToString("0")
                    + (type.Sill > 0.5d ? "＠" + type.Sill.ToString("0") : "　落地"));
            }
            if (previous != null)
            {
                var index = _openingLibrary.Types.FindIndex(t => t != null
                    && string.Equals(t.Code, previous, StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && index < _openingTypes.Items.Count) _openingTypes.SelectedIndex = index;
            }
        }

        private void UseSelectedOpeningType()
        {
            if (_openingLibrary == null || _openingTypes.SelectedIndex < 0) return;
            var type = _openingLibrary.Types.ElementAtOrDefault(_openingTypes.SelectedIndex);
            if (type == null) return;
            _canvas.CurrentType = type;
            _canvas.OpeningWidth = type.Width;
            _canvas.OpeningHeight = type.Height;
            _canvas.OpeningSill = type.Sill;
            Log("当前门窗类型：" + type.Code + "（" + type.Kind + " " + type.Width.ToString("0") + "×"
                + type.Height.ToString("0") + (type.Sill > 0.5d ? "，窗台 " + type.Sill.ToString("0") : "，落地") + "）");
        }

        private void ApplyTypeToSelection()
        {
            if (_canvas.CurrentType == null) { Log("先在列表里选一个门窗类型。"); return; }
            var hit = _canvas.Selection;
            if (hit == null || hit.Kind != "opening") { Log("先用「选择」工具选中一樘门窗。"); return; }
            var opening = (_model.Openings ?? new List<OpeningModel>()).FirstOrDefault(o => o != null
                && string.Equals(o.Id, hit.Id, StringComparison.OrdinalIgnoreCase));
            var wall = opening == null ? null : _canvas.FindWall(opening.HostWallId);
            if (opening == null || wall == null) { Log("没找到这樘门窗或它的宿主墙。"); return; }
            var backup = new OpeningModel
            {
                Id = opening.Id, HostWallId = opening.HostWallId, Code = opening.Code, Kind = opening.Kind,
                Offset = opening.Offset, Width = opening.Width, Height = opening.Height, Sill = opening.Sill
            };
            PlanEditing.ApplyType(opening, _canvas.CurrentType);
            var error = PlanEditing.ValidateOpening(_model, wall, opening);
            if (error != null)
            {
                // 放不下就退回原样，别把模型改坏
                opening.Code = backup.Code; opening.Kind = backup.Kind; opening.Width = backup.Width;
                opening.Height = backup.Height; opening.Sill = backup.Sill;
                Log("套用失败：" + error);
                return;
            }
            _canvas.Invalidate();
            SaveModel();
            ShowProperties();
            Log("已套用类型：" + _canvas.CurrentType.Code + "（洞口 " + opening.Width.ToString("0") + "×"
                + opening.Height.ToString("0") + "）");
        }

        private void ClearOpeningType()
        {
            _canvas.CurrentType = null;
            _openingTypes.ClearSelected();
            Log("已取消当前门窗类型，放门窗改回默认尺寸。");
        }

        // ───────────────────────── 楼层 ─────────────────────────

        private void RefreshStoreys()
        {
            if (_model == null) return;
            var selected = _canvas.StoreyId;
            var ordered = _model.Storeys.OrderByDescending(s => s.Elevation).ToList();
            _storeys.Items.Clear();
            foreach (var storey in ordered)
                _storeys.Items.Add(storey.Name + "　标高 " + (storey.Elevation / 1000d).ToString("0.000")
                    + "　层高 " + storey.Height.ToString("0")
                    + "　（墙 " + _model.Walls.Count(w => w != null && string.Equals(w.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase)) + "）");
            if (selected == null) return;
            var index = ordered.FindIndex(s => string.Equals(s.Id, selected, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index < _storeys.Items.Count) _storeys.SelectedIndex = index;
        }

        private void SwitchStorey()
        {
            if (_model == null || _storeys.SelectedIndex < 0) return;
            var storey = _model.Storeys.OrderByDescending(s => s.Elevation).ElementAtOrDefault(_storeys.SelectedIndex);
            if (storey == null) return;
            _canvas.StoreyId = storey.Id;
        }

        private void AddStorey()
        {
            if (_model == null) return;
            var top = _model.Storeys.Count == 0 ? 0d : _model.Storeys.Max(s => s.Elevation + s.Height);
            var storey = new StoreyModel
            {
                Id = "S" + Guid.NewGuid().ToString("N").Substring(0, 4),
                Name = "楼层" + (_model.Storeys.Count + 1),
                Elevation = top,
                Height = 3300d
            };
            _model.Storeys.Add(storey);
            RefreshStoreys();
            _canvas.StoreyId = storey.Id;
            SaveModel();
            Log("已增加楼层：" + storey.Name + "（标高 " + (top / 1000d).ToString("0.000") + "，层高 3300）");
        }

        private void EditStorey()
        {
            if (_model == null || _storeys.SelectedIndex < 0) { Log("先选一个楼层。"); return; }
            var storey = _model.Storeys.OrderByDescending(s => s.Elevation).ElementAtOrDefault(_storeys.SelectedIndex);
            if (storey == null) return;
            using (var dialog = new StoreyEditForm(storey))
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    RefreshStoreys();
                    _canvas.Invalidate();
                    SaveModel();
                }
        }

        private void RemoveStorey()
        {
            if (_model == null || _storeys.SelectedIndex < 0) { Log("先选一个楼层。"); return; }
            var storey = _model.Storeys.OrderByDescending(s => s.Elevation).ElementAtOrDefault(_storeys.SelectedIndex);
            if (storey == null) return;
            var wallIds = _model.Walls.Where(w => w != null && string.Equals(w.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase))
                .Select(w => w.Id).ToList();
            if (MessageBox.Show(this, "删除楼层“" + storey.Name + "”？该层的 " + wallIds.Count + " 道墙与墙上门窗会一起删除。",
                "删除楼层", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _model.Openings.RemoveAll(o => o != null && wallIds.Contains(o.HostWallId));
            _model.Walls.RemoveAll(w => w != null && string.Equals(w.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase));
            _model.Columns.RemoveAll(c => c != null && string.Equals(c.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase));
            _model.Slabs.RemoveAll(s => s != null && string.Equals(s.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase));
            _model.Storeys.Remove(storey);
            RefreshStoreys();
            _canvas.Invalidate();
            SaveModel();
        }

        // ───────────────────────── 属性面板 ─────────────────────────

        private void ShowProperties()
        {
            _properties.Controls.Clear();
            var host = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Width = 350 };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _properties.Controls.Add(host);
            var hit = _canvas.Selection;
            if (hit == null || _model == null)
            {
                var label = new Label { Text = "未选中构件。用「选择」工具点墙 / 门窗 / 柱。", AutoSize = true, MaximumSize = new Size(330, 60) };
                host.Controls.Add(label, 0, 0);
                host.SetColumnSpan(label, 2);
                return;
            }
            var row = 0;
            if (hit.Kind == "wall")
            {
                var wall = _canvas.FindWall(hit.Id);
                if (wall == null) return;
                AddField(host, ref row, "墙编号", wall.Id, v => wall.Id = v);
                AddField(host, ref row, "起点 X", wall.X1.ToString("0"), v => wall.X1 = Num(v, wall.X1));
                AddField(host, ref row, "起点 Y", wall.Y1.ToString("0"), v => wall.Y1 = Num(v, wall.Y1));
                AddField(host, ref row, "终点 X", wall.X2.ToString("0"), v => wall.X2 = Num(v, wall.X2));
                AddField(host, ref row, "终点 Y", wall.Y2.ToString("0"), v => wall.Y2 = Num(v, wall.Y2));
                AddField(host, ref row, "墙厚", wall.Thickness.ToString("0"), v => wall.Thickness = Num(v, wall.Thickness));
                AddField(host, ref row, "墙高(0=层高)", wall.Height.ToString("0"), v => wall.Height = Num(v, wall.Height));
                AddField(host, ref row, "材质", wall.Material ?? string.Empty, v => wall.Material = v);
                AddInfo(host, ref row, "墙长 " + Math.Round(PlanEditing.WallLength(wall)) + " mm");
            }
            else if (hit.Kind == "opening")
            {
                var opening = (_model.Openings ?? new List<OpeningModel>()).FirstOrDefault(o => o != null && Same(o.Id, hit.Id));
                if (opening == null) return;
                AddField(host, ref row, "编号", opening.Code ?? string.Empty, v => opening.Code = v);
                AddField(host, ref row, "类型", opening.Kind ?? "窗", v => opening.Kind = v);
                AddField(host, ref row, "洞口宽", opening.Width.ToString("0"), v => opening.Width = Num(v, opening.Width));
                AddField(host, ref row, "洞口高", opening.Height.ToString("0"), v => opening.Height = Num(v, opening.Height));
                AddField(host, ref row, "窗台高", opening.Sill.ToString("0"), v => opening.Sill = Num(v, opening.Sill));
                AddField(host, ref row, "沿墙定位", opening.Offset.ToString("0"), v => opening.Offset = Num(v, opening.Offset));
                var wall = _canvas.FindWall(opening.HostWallId);
                AddInfo(host, ref row, "宿主墙 " + (wall == null ? "（缺失）" : wall.Id + "，墙长 " + Math.Round(PlanEditing.WallLength(wall)) + "mm"));
            }
            else if (hit.Kind == "column")
            {
                var column = (_model.Columns ?? new List<ColumnModel>()).FirstOrDefault(c => c != null && Same(c.Id, hit.Id));
                if (column == null) return;
                AddField(host, ref row, "柱编号", column.Id, v => column.Id = v);
                AddField(host, ref row, "中心 X", column.X.ToString("0"), v => column.X = Num(v, column.X));
                AddField(host, ref row, "中心 Y", column.Y.ToString("0"), v => column.Y = Num(v, column.Y));
                AddField(host, ref row, "宽", column.Width.ToString("0"), v => column.Width = Num(v, column.Width));
                AddField(host, ref row, "深", column.Depth.ToString("0"), v => column.Depth = Num(v, column.Depth));
            }
        }

        private static void AddField(TableLayoutPanel host, ref int row, string label, string value, Action<string> apply)
        {
            host.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, row);
            host.Controls.Add(new TextBox { Text = value, Width = 150, Tag = apply }, 1, row);
            row++;
        }

        private static void AddInfo(TableLayoutPanel host, ref int row, string text)
        {
            var label = new Label { Text = text, AutoSize = true, ForeColor = Color.FromArgb(105, 112, 122), MaximumSize = new Size(240, 40) };
            host.Controls.Add(label, 0, row);
            host.SetColumnSpan(label, 2);
            row++;
        }

        private void ApplyProperties()
        {
            foreach (var box in Descendants(_properties).OfType<TextBox>())
            {
                var apply = box.Tag as Action<string>;
                if (apply != null) apply(box.Text);
            }
            _canvas.Invalidate();
            SaveModel();
            ShowProperties();
            Log("属性已应用。");
        }

        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var grand in Descendants(child)) yield return grand;
            }
        }

        private static double Num(string text, double fallback)
        {
            double value;
            return double.TryParse(text, out value) ? value : fallback;
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private void Log(string message)
        {
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        }
    }

    /// <summary>楼层标高/层高的编辑对话框。</summary>
    internal sealed class StoreyEditForm : Form
    {
        public StoreyEditForm(StoreyModel storey)
        {
            Text = "编辑楼层";
            Width = 380;
            Height = 230;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9F);

            var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), RowCount = 4 };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var name = new TextBox { Text = storey.Name ?? string.Empty, Dock = DockStyle.Fill };
            var elevation = new TextBox { Text = storey.Elevation.ToString("0"), Dock = DockStyle.Fill };
            var height = new TextBox { Text = storey.Height.ToString("0"), Dock = DockStyle.Fill };
            host.Controls.Add(new Label { Text = "名称", AutoSize = true }, 0, 0);
            host.Controls.Add(name, 1, 0);
            host.Controls.Add(new Label { Text = "标高(mm)", AutoSize = true }, 0, 1);
            host.Controls.Add(elevation, 1, 1);
            host.Controls.Add(new Label { Text = "层高(mm)", AutoSize = true }, 0, 2);
            host.Controls.Add(height, 1, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            host.Controls.Add(buttons, 0, 3);
            host.SetColumnSpan(buttons, 2);
            Controls.Add(host);
            AcceptButton = ok;
            CancelButton = cancel;
            ok.Click += (s, e) =>
            {
                double value;
                if (!string.IsNullOrWhiteSpace(name.Text)) storey.Name = name.Text.Trim();
                if (double.TryParse(elevation.Text, out value)) storey.Elevation = value;
                if (double.TryParse(height.Text, out value) && value > 0d) storey.Height = value;
            };
        }
    }
}
