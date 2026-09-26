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

        /// <summary>启动时要打开的项目文件夹 / 模型名称（CAD 里执行 JZMX 时会带上这两个参数）。</summary>
        internal static string StartupProjectFolder;
        internal static string StartupModelName;

        [STAThread]
        private static void Main(string[] args)
        {
            // 先取 --project / --model（可以和其它参数一起给）：
            // CAD 里「建筑模型」命令就是这样把当前项目的模型直接打开的。
            for (var index = 0; index < (args == null ? 0 : args.Length); index++)
            {
                var value = index + 1 < args.Length ? args[index + 1] : null;
                if (string.Equals(args[index], "--project", StringComparison.OrdinalIgnoreCase)) StartupProjectFolder = value;
                else if (string.Equals(args[index], "--model", StringComparison.OrdinalIgnoreCase)) StartupModelName = value;
            }
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

            // 预览快照：把默认视图渲染成 PNG（不开窗口、不用 CAD，方便核对/留档）
            //   dotnet 万落建筑模型.dll --snapshot [<项目文件夹>] [<模型名称>] [<输出目录>]
            if (args != null && args.Length > 0 && string.Equals(args[0], "--snapshot", StringComparison.OrdinalIgnoreCase))
            {
                Headless = true;
                try
                {
                    var folder = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
                        ? args[1]
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "万落建筑项目", "建筑模型样例");
                    var name = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : "样例-两层小房子";
                    var output = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3]) ? args[3] : null;
                    WriteSnapshots(folder, name, output, Console.WriteLine);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("快照失败：" + exception);
                    Environment.ExitCode = 1;
                }
                return;
            }

            // 推到 CAD（不弹窗口、不切窗口，只生成 + 写"待落图"清单，便于脚本/自检核对）
            //   dotnet 万落建筑模型.dll --push [<项目文件夹>] [<模型名称>]
            if (args != null && args.Length > 0 && string.Equals(args[0], "--push", StringComparison.OrdinalIgnoreCase))
            {
                Headless = true;
                try
                {
                    var folder = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
                        ? args[1]
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "万落建筑项目", "建筑模型样例");
                    var name = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : "样例-两层小房子";
                    var lines = GenerateViewsForExistingModel(folder, name, Console.WriteLine);
                    var marked = MarkPendingForCad(folder, name);
                    Console.WriteLine("完成：" + lines + " 条线；待落图 " + marked + " 张 → "
                        + StudioLaunch.PendingFilePath(ModelFolderOf(folder, name)));
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("推送失败：" + exception);
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

            // 画布自检：不弹窗口，把平面画布与立面预览真正画到离屏位图上
            //   dotnet 万落建筑模型.dll --selftest-canvas
            if (args != null && args.Length > 0 && string.Equals(args[0], "--selftest-canvas", StringComparison.OrdinalIgnoreCase))
            {
                Headless = true;
                // 自检日志逐行刷出去：万一某一步卡住，从输出最后一行就能看出卡在哪
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                try
                {
                    CanvasSelfTest.Run(Console.WriteLine);
                    PlanInteractionSelfTest.Run(Console.WriteLine);
                    ViewPreviewSelfTest.Run(Console.WriteLine);
                    VolumeSelfTest.Run(Console.WriteLine);
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
                        // 自检用一份临时工程，绝不碰用户的模型（下面会套用类型、会保存）
                        var temp = Path.Combine(Path.GetTempPath(), "WanluoStudioSelfTest", Guid.NewGuid().ToString("N"));
                        try
                        {
                            GenerateSample(temp, "自检模型", text => { });
                            form.UseProjectForTest(temp, "自检模型");
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

                            // 预览页：切过去、真的画一次（走窗口 WM_PAINT，与用户点开预览是同一条路）
                            var preview = FindControl<ViewPreviewCanvas>(form);
                            if (preview == null) throw new InvalidOperationException("主窗口里没有找到立面预览控件。");
                            var tabs = FindControl<TabControl>(form);
                            if (tabs != null && tabs.TabPages.Count > 1) tabs.SelectedIndex = 1;
                            Application.DoEvents();
                            preview.Refresh();
                            Application.DoEvents();
                            if (preview.LastPaintError != null)
                                throw new InvalidOperationException("立面预览真实绘制失败：" + preview.LastPaintError);
                            if (preview.LastLineCount <= 0)
                                throw new InvalidOperationException("立面预览没有画出任何线条（视图选择或重算没生效）。");
                            Console.WriteLine("PASS 立面预览：已真实绘制一次，" + preview.View.Title
                                + " 画了 " + preview.LastLineCount + " 条线 / " + preview.LastTextCount + " 个文字（无异常）");

                            // 预览里点一樘门窗 → 换类型库里的另一条 → 模型与预览都要跟着变
                            CheckPreviewPickAndApply(form, preview, canvas);
                        }
                        finally
                        {
                            try { Directory.Delete(temp, true); } catch { }
                        }
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

            // 主窗口自检模式：把主窗口构造并显示一次，确认布局、画布与预览都不会抛异常。
            //   dotnet 万落建筑模型.dll --snapshot-ui [<输出 png>]
            if (args != null && args.Length > 0 && string.Equals(args[0], "--snapshot-ui", StringComparison.OrdinalIgnoreCase))
            {
                Headless = true;
                try
                {
                    using (var form = new MainForm())
                    {
                        form.Show();
                        Application.DoEvents();
                        var tabs = FindControl<TabControl>(form);
                        if (tabs != null && tabs.TabPages.Count > 1) tabs.SelectedIndex = 1;   // 切到预览页
                        var preview = FindControl<ViewPreviewCanvas>(form);
                        if (preview != null) preview.Refresh();
                        Application.DoEvents();
                        // 布局还没完全稳定时（脚本里 Show 完马上截图）重新适应一次，保证整张图都在画面里
                        form.PerformLayout();
                        if (preview != null)
                        {
                            preview.PerformLayout();
                            preview.ZoomExtents();
                            preview.Refresh();
                        }
                        Application.DoEvents();
                        // 截图里顺手点中第一樘门窗，好把"点选高亮 + 信息行"一起拍进去
                        if (preview != null && preview.View != null)
                        {
                            var anchor = (preview.View.Anchors ?? new List<ViewAnchor>()).FirstOrDefault(a => a != null);
                            if (anchor != null)
                            {
                                preview.SimulateClick(preview.ModelToScreenForTest(
                                    (anchor.X1 + anchor.X2) / 2d, (anchor.Y1 + anchor.Y2) / 2d));
                                Application.DoEvents();
                            }
                        }
                        var path = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
                            ? args[1]
                            : Path.Combine(Path.GetTempPath(), "万落建筑模型-界面.png");
                        // 允许在后面附加 --project/--model（CAD 启动时就是这样带的），位置参数照样识别
                        var positional = PositionalArguments(args, 1);
                        if (positional.Count > 0 && !string.IsNullOrWhiteSpace(positional[0])) path = positional[0];
                        if (positional.Count > 1) form.SelectViewForTest(positional[1]);
                        // 按**客户区**尺寸截：用窗口尺寸截会把右边和下边截掉（截图里画布看着被切了一半）
                        var client = form.ClientSize;
                        using (var bitmap = new Bitmap(client.Width, client.Height))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(0, 0, client.Width, client.Height));
                            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Console.WriteLine("界面快照：" + path + "（预览 "
                            + (preview == null ? "未找到" : preview.LastLineCount + " 条线")
                            + (preview == null ? "" : "；画布 " + preview.Width + "×" + preview.Height
                                + "，1px≈" + Math.Round(1d / Math.Max(1e-9d, preview.ViewScale), 1) + "mm")
                            + "；模型 " + (form.ModelNameForTest ?? "?") + "）");
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
            return FindControl<PlanCanvas>(parent);
        }

        /// <summary>
        /// 把默认视图渲染成 PNG（与程序里预览用的是同一个控件、同一条绘制路径）。
        /// 用途：不开 CAD 也能核对立面长什么样，也方便把结果发给别人看。
        /// </summary>
        internal static void WriteSnapshots(string projectFolder, string modelName, string outputFolder, Action<string> log)
        {
            var modelPath = BuildingModelJson.ModelFilePath(projectFolder, modelName);
            if (!File.Exists(modelPath)) throw new FileNotFoundException("找不到模型：" + modelPath);
            var model = BuildingModelJson.LoadModel(modelPath);
            var libraryPath = BuildingModelJson.OpeningLibraryPath(projectFolder, modelName);
            var library = File.Exists(libraryPath) ? BuildingModelJson.LoadOpeningLibrary(libraryPath) : null;
            if (outputFolder == null)
                outputFolder = Path.Combine(Path.GetDirectoryName(modelPath) ?? projectFolder, "预览");
            Directory.CreateDirectory(outputFolder);

            using (var canvas = new ViewPreviewCanvas { Size = new Size(1500, 1000) })
            {
                var definitions = SampleModelFactory.CreateDefaultViews(modelName);
                foreach (var storey in (model.Storeys ?? new List<StoreyModel>()).Where(s => s != null))
                    definitions.Add(SampleModelFactory.CreatePlanView(storey));
                definitions.Add(SampleModelFactory.CreateScheduleView(modelName));
                var allViews = definitions
                    .Select(definition => definition.Kind == ViewKind.Schedule
                        ? OrthographicProjector.ProjectSchedule(model, library, definition.Title)
                        : OrthographicProjector.Project(model, definition, library))
                    .ToList();
                foreach (var sheet in SampleModelFactory.CreateDefaultSheets(model))
                {
                    var sheetView = SheetComposer.Compose(allViews, sheet);
                    canvas.View = sheetView;
                    using (var bitmap = new Bitmap(canvas.Width, canvas.Height))
                    {
                        using (var graphics = Graphics.FromImage(bitmap)) canvas.Render(graphics);
                        var path = Path.Combine(outputFolder, "图纸-" + sheet.Number + " " + sheet.Title + ".png");
                        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        log("快照：" + path + "（线 " + sheetView.Lines.Count + "、文字 " + sheetView.Texts.Count
                            + "、尺寸 " + sheetView.Dimensions.Count + "）");
                    }
                }
                foreach (var definition in definitions)
                {
                    var view = definition.Kind == ViewKind.Schedule
                        ? OrthographicProjector.ProjectSchedule(model, library, definition.Title)
                        : OrthographicProjector.Project(model, definition, library);
                    canvas.View = view;
                    using (var bitmap = new Bitmap(canvas.Width, canvas.Height))
                    {
                        using (var graphics = Graphics.FromImage(bitmap)) canvas.Render(graphics);
                        var path = Path.Combine(outputFolder, definition.Title + ".png");
                        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        log("快照：" + path + "（线 " + view.Lines.Count + "、文字 " + view.Texts.Count
                            + "、尺寸 " + view.Dimensions.Count + "、填充 " + view.Hatches.Count + "）");
                    }
                }

                // 三维轴测图（体量预览）
                WriteVolumeSnapshot(model, outputFolder, log);
            }
        }

        /// <summary>
        /// 自检：在预览里点中一樘门窗 → 换成类型库里的另一条 → 断言模型改对了、视图按新做法重算了。
        /// 走的就是用户点按钮的那两条方法（点选事件回调 + 套用类型），不是另写一套。
        /// </summary>
        private static void CheckPreviewPickAndApply(MainForm form, ViewPreviewCanvas preview, PlanCanvas canvas)
        {
            var model = canvas.Model;
            var opening = model.Openings.FirstOrDefault(o => o != null && !string.IsNullOrWhiteSpace(o.Code));
            if (opening == null) throw new InvalidOperationException("自检模型里没有带编号的洞口。");
            var anchor = (preview.View.Anchors ?? new List<ViewAnchor>())
                .FirstOrDefault(a => a != null && string.Equals(a.ElementId, opening.Id, StringComparison.OrdinalIgnoreCase));
            if (anchor == null) throw new InvalidOperationException("视图里没有 " + opening.Id + " 的锚点，预览无法点选。");

            preview.SimulateClick(preview.ModelToScreenForTest(
                (anchor.X1 + anchor.X2) / 2d, (anchor.Y1 + anchor.Y2) / 2d));
            Application.DoEvents();
            if (!string.Equals(preview.SelectedElementId, opening.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("点中洞口后应选中 " + opening.Id + "，实际 " + preview.SelectedElementId);
            if (form.ViewInfoForTest.IndexOf(opening.Code, StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("信息行里应显示被选中门窗的编号：" + form.ViewInfoForTest);
            Console.WriteLine("PASS 预览点选：点中 " + opening.Code + "（" + opening.Id + "），信息行已显示它的尺寸与做法");

            // 换一条编号不同的类型，然后走"套用到选中洞口"
            var library = form.OpeningLibraryForTest;
            if (library == null) throw new InvalidOperationException("自检工程没有载入门窗类型库。");
            var index = library.Types.FindIndex(t => t != null && !string.Equals(t.Code, opening.Code, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidOperationException("类型库里没有第二个可换的类型。");
            var target = library.Types[index];
            var linesBefore = preview.View.Lines.Count;

            form.SelectOpeningTypeForTest(index);
            form.ApplyTypeToSelectionForTest();
            Application.DoEvents();

            if (!string.Equals(opening.Code, target.Code, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("套用后洞口编号应为 " + target.Code + "，实际 " + opening.Code);
            if (Math.Abs(opening.Width - target.Width) > 0.5d || Math.Abs(opening.Height - target.Height) > 0.5d)
                throw new InvalidOperationException("套用后洞口尺寸应跟类型库一致：" + opening.Width + "×" + opening.Height);
            if (preview.View == null || !preview.View.Texts.Any(t => t != null && t.Text == target.Code))
                throw new InvalidOperationException("重算后的立面里应标出新编号 " + target.Code);
            if (preview.SelectedElementId == null)
                throw new InvalidOperationException("重算后应仍选中刚才那一樘门窗。");
            Console.WriteLine("PASS 预览改做法：把 " + opening.Id + " 换成 " + target.Code + "（"
                + target.Width.ToString("0") + "×" + target.Height.ToString("0") + "）→ 模型已改、视图已重算并标出新编号"
                + "（线条 " + linesBefore + " → " + preview.View.Lines.Count + "）");
        }

        /// <summary>把三维轴测图渲染成 PNG（与三维预览页同一个控件、同一条绘制路径）。</summary>
        internal static void WriteVolumeSnapshot(BuildingModelDocument model, string outputFolder, Action<string> log)
        {
            if (model == null || string.IsNullOrWhiteSpace(outputFolder)) return;
            using (var canvas = new VolumeCanvas { Size = new Size(1400, 900) })
            {
                canvas.SetModel(model);
                using (var bitmap = new Bitmap(canvas.Width, canvas.Height))
                {
                    using (var graphics = Graphics.FromImage(bitmap)) canvas.Render(graphics);
                    var path = Path.Combine(outputFolder, "三维轴测.png");
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    var volume = BuildingVolumeBuilder.Build(model, null);
                    log("快照：" + path + "（体量面 " + volume.Faces.Count + "、范围 "
                        + Math.Round(volume.Width) + "×" + Math.Round(volume.Depth) + "×" + Math.Round(volume.Height)
                        + " mm、画出 " + canvas.LastFaceCount + " 个面）");
                }
            }
        }

        /// <summary>把参数里的位置参数挑出来（跳过模式名与 --project/--model 这类带值的开关）。</summary>
        private static List<string> PositionalArguments(string[] args, int start)
        {
            var result = new List<string>();
            for (var index = Math.Max(0, start); index < (args == null ? 0 : args.Length); index++)
            {
                var value = args[index] ?? string.Empty;
                if (string.Equals(value, "--project", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "--model", StringComparison.OrdinalIgnoreCase))
                {
                    index++;        // 跳过它的值
                    continue;
                }
                result.Add(value);
            }
            return result;
        }

        /// <summary>在控件树里找第一个指定类型的控件（自检用）。</summary>
        private static T FindControl<T>(Control parent) where T : Control
        {
            foreach (Control child in parent.Controls)
            {
                var match = child as T;
                if (match != null) return match;
                var nested = FindControl<T>(child);
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

            // 顺便放一份演示门窗类型库（CAD 导出的真库绝不覆盖：只有"还是演示库"时才重写）
            var libraryPath = BuildingModelJson.OpeningLibraryPath(projectFolder, modelName);
            if (!File.Exists(libraryPath) || IsStillDemoLibrary(libraryPath))
            {
                var library = SampleModelFactory.CreateDemoOpeningLibrary();
                BuildingModelJson.SaveOpeningLibrary(libraryPath, library);
                log("演示门窗类型库：" + libraryPath + "（类型 " + library.Types.Count + " 个；真实项目请用 CAD 的 TQLX 导出）");
            }
            return GenerateViews(projectFolder, modelName, model, log,
                File.Exists(libraryPath) ? BuildingModelJson.LoadOpeningLibrary(libraryPath) : null);
        }

        /// <summary>
        /// 这份类型库是不是"还没被替换过的演示库"。
        /// 演示库要能随程序升级更新做法参数（外框/中挺/安装缝），
        /// 但 CAD 用 TQLX 导出的真库、或用户手工导入的类型库，一个字段都不能动。
        /// </summary>
        private static bool IsStillDemoLibrary(string path)
        {
            try
            {
                var library = BuildingModelJson.LoadOpeningLibrary(path);
                if (library == null || library.Types == null || library.Types.Count == 0) return true;
                return library.Types.All(type => type != null && type.Source == "演示类型库");
            }
            catch
            {
                return false;      // 读不出来就当作用户的东西，别覆盖
            }
        }

        /// <summary>项目的模型目录：<c>&lt;项目文件夹&gt;\建筑模型\&lt;模型名称&gt;</c>。</summary>
        internal static string ModelFolderOf(string projectFolder, string modelName)
        {
            try { return BuildingModelJson.ModelFolder(projectFolder, modelName); }
            catch { return null; }
        }

        /// <summary>对**已存在的模型**重新生成全部视图与图纸（不动模型本身，保住用户的编辑）。</summary>
        internal static int GenerateViewsForExistingModel(string projectFolder, string modelName, Action<string> log)
        {
            var modelPath = BuildingModelJson.ModelFilePath(projectFolder, modelName);
            if (!File.Exists(modelPath)) throw new FileNotFoundException("找不到模型：" + modelPath);
            var model = BuildingModelJson.LoadModel(modelPath);
            var libraryPath = BuildingModelJson.OpeningLibraryPath(projectFolder, modelName);
            var library = File.Exists(libraryPath) ? BuildingModelJson.LoadOpeningLibrary(libraryPath) : null;
            return GenerateViews(projectFolder, modelName, model, log, library);
        }

        /// <summary>
        /// 写下"待落图"清单（优先图纸，没有图纸就推所有视图），返回标记的张数。
        /// 「推到 CAD」按钮与 <c>--push</c> 共用这一段。
        /// </summary>
        internal static int MarkPendingForCad(string projectFolder, string modelName)
        {
            var modelFolder = ModelFolderOf(projectFolder, modelName);
            var entries = StudioLaunch.ListViews(modelFolder);
            var sheets = entries.Where(entry => entry.Kind == ViewKind.Sheet).ToList();
            var marked = sheets.Count > 0 ? sheets : entries;
            StudioLaunch.WritePending(modelFolder, marked.Select(entry => new StudioPendingEntry
            {
                Id = entry.Id, FilePath = entry.FilePath
            }));
            return marked.Count;
        }

        /// <summary>
        /// 把默认视图集合生成 views/*.json，返回线条总数。
        /// <paramref name="library"/> 是门窗类型库：立面的门窗分格与开启线按编号查它取做法（可为空）。
        /// </summary>
        internal static int GenerateViews(string projectFolder, string modelName, BuildingModelDocument model,
            Action<string> log, OpeningTypeLibraryDocument library)
        {
            var total = 0;
            if (library == null) log("提示：没有门窗类型库，立面只画洞口轮廓（用 CAD 的 TQLX 导出后可补上分格与开启线）。");
            else log("门窗类型库：" + library.Types.Count + " 个类型，立面的分格与开启线按编号取用。");
            var generated = new List<ViewDocument>();
            foreach (var definition in SampleModelFactory.CreateDefaultViews(modelName))
            {
                var view = OrthographicProjector.Project(model, definition, library);
                generated.Add(view);
                var path = BuildingModelJson.ViewFilePath(projectFolder, modelName, view.Id);
                BuildingModelJson.SaveView(path, view);
                total += view.Lines.Count;
                var openingLines = view.Lines.Count(line => line.Layer == ViewLayers.Opening);
                log("视图：" + view.Title + " → 线 " + view.Lines.Count + "（门窗 " + openingLines + "）、文字 "
                    + view.Texts.Count + "、填充 " + view.Hatches.Count + "、尺寸 " + view.Dimensions.Count
                    + " → " + Path.GetFileName(path));
                foreach (var warning in view.Warnings) log("  提示：" + warning);
            }

            // 平面图：每层一张（平面也由模型投影生成，不开 CAD 就能看）
            foreach (var storey in (model.Storeys ?? new List<StoreyModel>()).Where(s => s != null))
            {
                var definition = SampleModelFactory.CreatePlanView(storey);
                var plan = OrthographicProjector.Project(model, definition, library);
                generated.Add(plan);
                var path = BuildingModelJson.ViewFilePath(projectFolder, modelName, plan.Id);
                BuildingModelJson.SaveView(path, plan);
                total += plan.Lines.Count;
                log("视图：" + plan.Title + " → 线 " + plan.Lines.Count + "、文字 " + plan.Texts.Count
                    + "、尺寸 " + plan.Dimensions.Count + "、可点选门窗 " + plan.Anchors.Count
                    + " → " + Path.GetFileName(path));
                foreach (var warning in plan.Warnings) log("  提示：" + warning);
            }

            // 门窗表：按编号汇总模型里的洞口（做法取自类型库），与立面/剖面一样落图
            var schedule = OrthographicProjector.ProjectSchedule(model, library, "门窗表");
            generated.Add(schedule);
            var schedulePath = BuildingModelJson.ViewFilePath(projectFolder, modelName, schedule.Id);
            BuildingModelJson.SaveView(schedulePath, schedule);
            total += schedule.Lines.Count;
            log("视图：" + schedule.Title + " → 线 " + schedule.Lines.Count + "、文字 " + schedule.Texts.Count
                + "（按编号汇总，做法来自类型库）→ " + Path.GetFileName(schedulePath));

            // 排版出图：把视图按纸张排成图纸（图纸本身就是一份视图产物，落图后按 1:1 出图）
            foreach (var sheet in SampleModelFactory.CreateDefaultSheets(model))
            {
                var composed = SheetComposer.Compose(generated, sheet);
                var path = BuildingModelJson.ViewFilePath(projectFolder, modelName, composed.Id);
                BuildingModelJson.SaveView(path, composed);
                total += composed.Lines.Count;
                log("图纸：" + composed.Title + " → 线 " + composed.Lines.Count + "、文字 " + composed.Texts.Count
                    + "、尺寸 " + composed.Dimensions.Count + " → " + Path.GetFileName(path));
                foreach (var warning in composed.Warnings) log("  提示：" + warning);
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
        private readonly TabControl _tabs = new TabControl();
        private readonly ViewPreviewCanvas _viewPreview = new ViewPreviewCanvas();
        private readonly VolumeCanvas _volumeCanvas = new VolumeCanvas();
        private readonly CheckBox _volumeOnlyStorey = new CheckBox();
        private readonly ComboBox _viewChooser = new ComboBox();
        private readonly Label _viewInfo = new Label();
        private BuildingModelDocument _model;
        private OpeningTypeLibraryDocument _openingLibrary;

        public MainForm()
        {
            Text = "万落建筑模型 · 平面草图 + 立面预览（P1.5）";
            Width = 1280;
            Height = 800;
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            // 单列必须显式占满：默认按内容取宽（AutoSize）时，只要有一行内容偏宽，
            // 整个窗口里的画布就会跟着变宽（右边被侧栏盖住），预览的"缩放适应"也会算错。
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // ── 顶部：文件与工具条 ───────────────────────────────────────────
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3, Padding = new Padding(8, 8, 8, 0) };
            var fileRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            fileRow.Controls.Add(FieldLabel("项目文件夹"));
            _projectFolder.Width = 300;
            _projectFolder.Text = string.IsNullOrWhiteSpace(Program.StartupProjectFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "万落建筑项目", "建筑模型样例")
                : Program.StartupProjectFolder;
            fileRow.Controls.Add(_projectFolder);
            fileRow.Controls.Add(Button("选择…", ChooseFolder, false));
            fileRow.Controls.Add(FieldLabel("模型名称"));
            _modelName.Width = 150;
            _modelName.Text = string.IsNullOrWhiteSpace(Program.StartupModelName) ? "样例-两层小房子" : Program.StartupModelName;
            fileRow.Controls.Add(_modelName);
            fileRow.Controls.Add(Button("打开/新建", OpenOrCreateModel, true));
            fileRow.Controls.Add(Button("保存", SaveModel, true));
            fileRow.Controls.Add(Button("生成全部视图", GenerateViewsNow, true));
            fileRow.Controls.Add(Button("推到 CAD", PushToCad, true));
            fileRow.Controls.Add(Button("预览立面", ShowViewPreview, true));
            fileRow.Controls.Add(Button("打开模型目录", OpenModelFolder, false));
            top.Controls.Add(fileRow, 0, 0);

            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 6) };
            tools.Controls.Add(ToolButton("选择", "select", true));
            tools.Controls.Add(ToolButton("画墙", "wall", false));
            tools.Controls.Add(ToolButton("放窗", "window", false));
            tools.Controls.Add(ToolButton("放门", "door", false));
            tools.Controls.Add(ToolButton("布柱", "column", false));
            tools.Controls.Add(ToolButton("拉轴线", "axis", false));
            tools.Controls.Add(ToolButton("画房间", "room", false));
            tools.Controls.Add(Button("删除选中(Delete)", () => _canvas.DeleteSelection(), false));
            tools.Controls.Add(Button("撤销(Ctrl+Z)", () => _canvas.Undo(), false));
            tools.Controls.Add(Button("重做(Ctrl+Y)", () => _canvas.Redo(), false));
            tools.Controls.Add(Button("缩放适应(Ctrl+A)", () => { _canvas.ZoomExtents(); _canvas.Invalidate(); }, false));
            top.Controls.Add(tools, 0, 1);

            top.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(105, 112, 122),
                Text = "左键：执行当前工具　中键/右键拖动：平移　滚轮：缩放　Esc：取消／闭合房间　Delete：删除"
                    + "　（捕捉自动生效：端点 / 中点 / 正交 / 100mm 轴网）　「拉轴线」：拉一条定方向与位置；「画房间」：连续点轮廓，点回起点或 Esc 闭合"
            }, 0, 2);
            root.Controls.Add(top, 0, 0);

            // ── 中部：左侧「平面草图 / 立面预览」两个页签 + 右侧面板 ──────────
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6 };
            split.HandleCreated += (s, e) => { try { split.SplitterDistance = Math.Max(560, split.Width - 400); } catch { } };
            root.Controls.Add(split, 0, 1);

            _canvas.Dock = DockStyle.Fill;
            var planPage = new TabPage("平面草图") { BackColor = Color.FromArgb(24, 26, 30), Padding = new Padding(0) };
            planPage.Controls.Add(_canvas);
            _tabs.Dock = DockStyle.Fill;
            _tabs.TabPages.Add(planPage);
            _tabs.TabPages.Add(BuildPreviewPage());
            _tabs.TabPages.Add(BuildVolumePage());
            _tabs.SelectedIndexChanged += (s, e) =>
            {
                if (_tabs.SelectedIndex == 1) RefreshViewPreview(true);
                if (_tabs.SelectedIndex == 2) RefreshVolume();
            };
            split.Panel1.Controls.Add(_tabs);

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
            _canvas.StructureChanged += () => { RefreshStoreys(); if (_tabs.SelectedIndex == 2) RefreshVolume(); };
            _canvas.SaveRequested += SaveModel;

            Log("P1.5 平面草图：用「画墙 / 放窗 / 放门 / 布柱」把平面画出来，画的就是模型。");
            Log("门窗类型库：CAD 里执行 TQLX 导出 → 本程序「刷新（模型目录）」即可用它放门窗。");
            Log("立面/剖面预览：切到「立面 / 剖面预览」页签，选一张视图点「重算当前视图」——不用开 CAD 就能看分格与开启线。");
            Log("画完点「生成全部视图」，回到 CAD 执行 LTTZ 落图（预览里看到什么，落下去就是什么）。");
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
                RefreshViewChoices();
                RefreshViewPreview(false);
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
            var total = Program.GenerateViews(_projectFolder.Text, ModelName, _model, Log, _openingLibrary);
            Log("生成完成，共 " + total + " 条线。回到 CAD 执行 LTTZ 落图（选 views 目录下的 json）。");
            RefreshViewPreview(false);
        }

        // ───────────────────────── 自检用的接口（只给 --selftest 用） ─────────────────────────

        /// <summary>自检用：切到指定工程（自检会在临时目录里造一份，绝不碰用户的模型）。</summary>
        internal void UseProjectForTest(string folder, string modelName)
        {
            _projectFolder.Text = folder;
            _modelName.Text = modelName;
            OpenOrCreateModel();
        }

        internal string ViewInfoForTest { get { return _viewInfo.Text; } }
        internal string ModelNameForTest { get { return ModelName; } }
        internal OpeningTypeLibraryDocument OpeningLibraryForTest { get { return _openingLibrary; } }

        /// <summary>自检/截图用：按视图 id 选中预览的下拉项（例如 "schedule" 门窗表）。</summary>
        internal void SelectViewForTest(string viewId)
        {
            if (string.IsNullOrWhiteSpace(viewId)) return;
            for (var index = 0; index < _viewChooser.Items.Count; index++)
            {
                var choice = _viewChooser.Items[index] as ViewChoice;
                if (choice != null && string.Equals(choice.Definition.Id, viewId, StringComparison.OrdinalIgnoreCase))
                {
                    _viewChooser.SelectedIndex = index;
                    return;
                }
            }
        }

        /// <summary>自检用：在右侧类型库里选中第 index 条（等于用户点列表）。</summary>
        internal void SelectOpeningTypeForTest(int index)
        {
            _openingTypes.SelectedIndex = index;
        }

        /// <summary>自检用：点「套用到选中洞口」。</summary>
        internal void ApplyTypeToSelectionForTest()
        {
            ApplyTypeToSelection();
        }

        // ───────────────────────── 三维预览 ─────────────────────────

        /// <summary>三维预览页：一行工具 + 整块体量画布。</summary>
        private TabPage BuildVolumePage()
        {
            var page = new TabPage("三维预览") { BackColor = Color.FromArgb(24, 26, 30), Padding = new Padding(0) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(6, 6, 6, 0) };
            row.Controls.Add(Button("重算体量", () => RefreshVolume(true), true));
            row.Controls.Add(Button("复位视角(Ctrl+A)", () => { _volumeCanvas.ZoomExtents(); _volumeCanvas.Focus(); }, false));
            row.Controls.Add(Button("轴测视角", () =>
            {
                _volumeCanvas.Camera.AzimuthDegrees = 35d;
                _volumeCanvas.Camera.ElevationDegrees = 28d;
                _volumeCanvas.Rebuild();
            }, false));
            row.Controls.Add(Button("俯视", () =>
            {
                _volumeCanvas.Camera.AzimuthDegrees = 0d;
                _volumeCanvas.Camera.ElevationDegrees = 88d;
                _volumeCanvas.Rebuild();
            }, false));
            _volumeOnlyStorey.AutoSize = true;
            _volumeOnlyStorey.ForeColor = Color.FromArgb(180, 186, 196);
            _volumeOnlyStorey.Text = "只看当前楼层";
            _volumeOnlyStorey.CheckedChanged += (s, e) =>
            {
                _volumeCanvas.OnlyCurrentStorey = _volumeOnlyStorey.Checked;
                RefreshVolume(true);
            };
            row.Controls.Add(_volumeOnlyStorey);
            layout.Controls.Add(row, 0, 0);
            layout.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(620, 0),
                ForeColor = Color.FromArgb(105, 112, 122),
                Padding = new Padding(8, 2, 8, 4),
                Text = "自研轴测投影：背面剔除 + 按深度从远到近填充（画家算法）。门窗洞口还没在体量上开洞，楼梯与坡屋面还没做。"
            }, 0, 1);

            _volumeCanvas.Dock = DockStyle.Fill;
            _volumeCanvas.StatusChanged += text => _status.Text = text;
            layout.Controls.Add(_volumeCanvas, 0, 2);
            page.Controls.Add(layout);
            return page;
        }

        /// <summary>重算三维体量（模型改动、切楼层、切页签时调用）。</summary>
        private void RefreshVolume(bool log = false)
        {
            if (_model == null) { _volumeCanvas.SetModel(null); return; }
            _volumeCanvas.StoreyId = _canvas.StoreyId;
            _volumeCanvas.SetModel(_model);
            if (log)
            {
                var volume = BuildingVolumeBuilder.Build(_model, _volumeOnlyStorey.Checked ? _canvas.StoreyId : null);
                Log("三维体量：" + volume.Faces.Count + " 个面，范围 " + Math.Round(volume.Width) + "×"
                    + Math.Round(volume.Depth) + "×" + Math.Round(volume.Height) + " mm（"
                    + (_volumeOnlyStorey.Checked ? "只看当前楼层" : "整栋") + "）");
            }
        }

        // ───────────────────────── 推到 CAD ─────────────────────────

        /// <summary>
        /// 「推到 CAD」：生成全部视图与图纸 → 写下"待落图"清单（CAD 里 LTTZ 会带 ★ 列出来、
        /// 并默认落第一张）→ 把 AutoCAD 窗口切到前台，并**自动输入 LTTZ**；
        /// 切不过去或没找到 CAD 就只留清单，让用户自己回 CAD 敲 LTTZ。
        /// </summary>
        private void PushToCad()
        {
            if (_model == null) { Log("还没有模型。"); return; }
            var total = Program.GenerateViews(_projectFolder.Text, ModelName, _model, Log, _openingLibrary);
            var modelFolder = Program.ModelFolderOf(_projectFolder.Text, ModelName);
            var marked = Program.MarkPendingForCad(_projectFolder.Text, ModelName);
            var sheets = StudioLaunch.ListViews(modelFolder).Where(entry => entry.Kind == ViewKind.Sheet).ToList();

            Log(marked > 0
                ? "已标记待落图 " + marked + " 张" + (sheets.Count > 0 ? "（图纸）" : "（视图）") + "：" + modelFolder
                : "没有可推的视图/图纸（先生成一次）");
            Log("生成完成，共 " + total + " 条线。");

            var activated = CadWindow.TryActivate();
            if (!activated)
            {
                Log("没找到正在运行的 AutoCAD：请在 CAD 里执行 LTTZ（会列出带 ★ 的待落图，默认就是刚推过去的）。");
                return;
            }
            Log("已切到 AutoCAD 并自动输入 LTTZ；在提示里选序号、点插入点即可。若没反应，手动敲 LTTZ 即可。");
        }

        // ───────────────────────── 立面/剖面预览 ─────────────────────────

        /// <summary>切到预览页并立刻按当前模型重算一张（顶部「预览立面」按钮）。</summary>
        private void ShowViewPreview()
        {
            if (_tabs.TabPages.Count > 1) _tabs.SelectedIndex = 1;
            RefreshViewPreview(true);
            _viewPreview.Focus();
        }

        /// <summary>预览页：上面一行选视图 + 重算，下面整块是预览画布。</summary>
        private TabPage BuildPreviewPage()
        {
            var page = new TabPage("立面 / 剖面预览") { BackColor = Color.FromArgb(24, 26, 30), Padding = new Padding(0) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(6, 6, 6, 0) };
            row.Controls.Add(new Label { Text = "视图", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            _viewChooser.DropDownStyle = ComboBoxStyle.DropDownList;
            _viewChooser.Width = 220;
            _viewChooser.SelectedIndexChanged += (s, e) => RefreshViewPreview(true);
            row.Controls.Add(_viewChooser);
            row.Controls.Add(Button("重算当前视图", () => RefreshViewPreview(true), true));
            row.Controls.Add(Button("生成全部视图", GenerateViewsNow, false));
            row.Controls.Add(Button("缩放适应(Ctrl+A)", () => { _viewPreview.ZoomExtents(); _viewPreview.Invalidate(); _viewPreview.Focus(); }, false));
            layout.Controls.Add(row, 0, 0);

            var hint = new Label
            {
                AutoSize = true,
                // 关键：不给最大宽度的话，这行长提示会把整页撑得比窗口还宽，
                // 里面的预览画布跟着变宽（右边被侧栏盖住），"缩放适应"就会按错误的宽度算。
                MaximumSize = new Size(620, 0),
                ForeColor = Color.FromArgb(105, 112, 122),
                Padding = new Padding(8, 2, 8, 4),
                Text = "预览用的是 CAD 落图读的那份视图数据：这里看到什么，LTTZ 落到 DWG 里就是什么。"
                    + "　左键点门窗：选中它（右侧类型库选一条后点「套用到选中洞口」即可改做法并立刻重算）"
                    + "　中键/右键拖动：平移　滚轮：缩放"
            };
            _viewInfo.AutoSize = true;
            _viewInfo.MaximumSize = new Size(620, 0);
            _viewInfo.ForeColor = Color.FromArgb(150, 200, 170);
            _viewInfo.Padding = new Padding(8, 2, 8, 0);
            _viewInfo.Text = "还没有视图：点「重算当前视图」。";
            layout.Controls.Add(row, 0, 0);
            layout.Controls.Add(_viewInfo, 0, 1);
            layout.Controls.Add(hint, 0, 2);

            _viewPreview.Dock = DockStyle.Fill;
            _viewPreview.StatusChanged += text => _status.Text = text;
            _viewPreview.AnchorSelected += OnPreviewAnchorSelected;
            layout.Controls.Add(_viewPreview, 0, 3);
            page.Controls.Add(layout);
            return page;
        }

        private void RefreshViewChoices()
        {
            var wanted = SelectedViewId();
            _viewChooser.Items.Clear();
            foreach (var definition in SampleModelFactory.CreateDefaultViews(ModelName))
                _viewChooser.Items.Add(new ViewChoice(definition));
            if (_model != null)
                foreach (var storey in (_model.Storeys ?? new List<StoreyModel>()).Where(s => s != null))
                    _viewChooser.Items.Add(new ViewChoice(SampleModelFactory.CreatePlanView(storey)));
            _viewChooser.Items.Add(new ViewChoice(SampleModelFactory.CreateScheduleView(ModelName)));   // 门窗表也能预览
            if (_model != null)
                foreach (var sheet in SampleModelFactory.CreateDefaultSheets(_model))
                    _viewChooser.Items.Add(new ViewChoice(new ViewDefinitionModel
                    {
                        Id = sheet.Id, Title = sheet.Title + "（图纸）", Kind = ViewKind.Sheet, Scale = 1
                    }));
            if (_viewChooser.Items.Count == 0) return;
            for (var index = 0; index < _viewChooser.Items.Count; index++)
                if (!string.IsNullOrEmpty(wanted)
                    && string.Equals(((ViewChoice)_viewChooser.Items[index]).Definition.Id, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    _viewChooser.SelectedIndex = index;
                    return;
                }
            _viewChooser.SelectedIndex = 0;
        }

        private string SelectedViewId()
        {
            var choice = _viewChooser.SelectedItem as ViewChoice;
            return choice == null ? null : choice.Definition.Id;
        }

        /// <summary>
        /// 按**当前模型**（不是磁盘上的旧视图）重算选中的那一张，直接画到预览里。
        /// 所以"改完平面就能立刻看立面"，不必先生成、再落图。
        /// </summary>
        private void RefreshViewPreview(bool log)
        {
            if (_model == null) { _viewInfo.Text = "还没有模型。"; return; }
            var choice = _viewChooser.SelectedItem as ViewChoice;
            if (choice == null) { RefreshViewChoices(); choice = _viewChooser.SelectedItem as ViewChoice; }
            if (choice == null) return;
            try
            {
                ViewDocument view;
                if (choice.Definition.Kind == ViewKind.Schedule)
                {
                    view = OrthographicProjector.ProjectSchedule(_model, _openingLibrary, choice.Definition.Title);
                }
                else if (choice.Definition.Kind == ViewKind.Sheet)
                {
                    // 图纸：把当前模型的所有视图现算一遍再排版（保证图纸永远与模型一致）
                    view = SheetComposer.Compose(ComposeAllViews(), FindSheet(choice.Definition.Id));
                }
                else
                {
                    view = OrthographicProjector.Project(_model, choice.Definition, _openingLibrary);
                }
                _viewPreview.View = view;
                var openingLines = view.Lines.Count(line => line.Layer == ViewLayers.Opening);
                // 之前选中的那一樘如果还在这张视图里，继续显示它的信息（改完做法看得见效果）
                var keepSelected = _viewPreview.SelectedAnchor;
                if (keepSelected == null) UpdateViewInfo(); else OnPreviewAnchorSelected(keepSelected);
                if (log)
                {
                    Log("预览 " + view.Title + "：线 " + view.Lines.Count + "（门窗 " + openingLines + "）、文字 "
                        + view.Texts.Count + "、填充 " + view.Hatches.Count + "。");
                    foreach (var warning in view.Warnings) Log("  提示：" + warning);
                }
            }
            catch (Exception exception)
            {
                _viewInfo.Text = "预览失败：" + exception.Message;
                Log("预览失败：" + exception.GetType().Name + "：" + exception.Message);
            }
        }

        /// <summary>下拉框里的一项：把视图定义显示成图名。</summary>
        private sealed class ViewChoice
        {
            public ViewChoice(ViewDefinitionModel definition) { Definition = definition; }
            public ViewDefinitionModel Definition { get; private set; }
            public override string ToString() { return Definition.Title; }
        }

        // ───────────────────────── 预览里点选门窗 ─────────────────────────

        /// <summary>预览里点中了某个洞口：把它的信息显示出来，并说明可以怎么改。</summary>
        private void OnPreviewAnchorSelected(ViewAnchor anchor)
        {
            if (anchor == null)
            {
                UpdateViewInfo();
                return;
            }
            var opening = FindOpening(anchor.ElementId);
            if (opening == null)
            {
                _viewInfo.Text = "选中的构件已经不在模型里了（可能被删除或撤销），已取消选择。";
                return;
            }
            var type = _openingLibrary == null ? null : _openingLibrary.FindType(opening.Code);
            var kind = string.IsNullOrWhiteSpace(opening.Kind) ? "洞口" : opening.Kind;
            var size = Math.Round(opening.Width) + "×" + Math.Round(opening.Height)
                + (opening.Sill > 0.5d ? "@窗台 " + Math.Round(opening.Sill) : "，落地");
            var typeText = type == null
                ? "类型库里没有「" + (opening.Code ?? "未编号") + "」"
                : (type.DivisionPreset ?? "—") + " / " + (type.OpeningMode ?? "—")
                    + (string.IsNullOrWhiteSpace(type.ElevationType) ? "" : "（" + type.ElevationType + "）");
            _viewInfo.Text = "已选中 " + (opening.Code ?? "未编号") + "：" + kind + " " + size + "　做法：" + typeText
                + "　→ 在右侧类型库选一条后点「套用到选中洞口」即可改做法";
            Log("预览选中：" + (opening.Code ?? "未编号") + "（" + kind + " " + size + "）　做法：" + typeText);
        }

        /// <summary>按模型里的洞口 id 找洞口（预览与平面共用）。</summary>
        private OpeningModel FindOpening(string id)
        {
            if (_model == null || string.IsNullOrWhiteSpace(id)) return null;
            return (_model.Openings ?? new List<OpeningModel>()).FirstOrDefault(o => o != null
                && string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>刷新预览页那句"线 N / 门窗 N"的信息行。</summary>
        private void UpdateViewInfo()
        {
            var view = _viewPreview.View;
            if (view == null) { _viewInfo.Text = "还没有视图：点「重算当前视图」。"; return; }
            var openingLines = view.Lines.Count(line => line.Layer == ViewLayers.Opening);
            _viewInfo.Text = view.Title + "：线 " + view.Lines.Count + "（门窗 " + openingLines + "）、文字 "
                + view.Texts.Count + "、填充 " + view.Hatches.Count
                + (_openingLibrary == null ? "　（没有类型库：门窗只画洞口轮廓）" : "　（做法来自类型库）");
        }

        /// <summary>把当前模型的全部视图现算一遍（图纸排版要用它们）。</summary>
        private List<ViewDocument> ComposeAllViews()
        {
            var views = new List<ViewDocument>();
            if (_model == null) return views;
            foreach (var definition in SampleModelFactory.CreateDefaultViews(ModelName))
                views.Add(OrthographicProjector.Project(_model, definition, _openingLibrary));
            foreach (var storey in (_model.Storeys ?? new List<StoreyModel>()).Where(s => s != null))
                views.Add(OrthographicProjector.Project(_model, SampleModelFactory.CreatePlanView(storey), _openingLibrary));
            views.Add(OrthographicProjector.ProjectSchedule(_model, _openingLibrary, "门窗表"));
            return views;
        }

        private SheetDefinitionModel FindSheet(string sheetId)
        {
            var sheets = SampleModelFactory.CreateDefaultSheets(_model);
            return sheets.FirstOrDefault(s => string.Equals(s.Id, sheetId, StringComparison.OrdinalIgnoreCase)) ?? sheets.FirstOrDefault();
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
                RefreshViewPreview(false);
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
            RefreshViewPreview(false);      // 换了类型库，立面的分格/开启线要跟着重算
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
                    RefreshViewPreview(false);
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
            // 选中的洞口可以来自平面画布（选择工具），也可以来自**立面预览里点中的那一樘**
            var hit = _canvas.Selection;
            var opening = hit != null && hit.Kind == "opening" ? FindOpening(hit.Id) : null;
            var fromPreview = false;
            if (opening == null)
            {
                var anchor = _viewPreview.SelectedAnchor;
                if (anchor != null)
                {
                    opening = FindOpening(anchor.ElementId);
                    fromPreview = opening != null;
                }
            }
            if (opening == null) { Log("先选中一樘门窗：平面里用「选择」工具点它，或切到「立面 / 剖面预览」点它。"); return; }
            var wall = _canvas.FindWall(opening.HostWallId);
            if (wall == null) { Log("没找到这樘门窗的宿主墙。"); return; }
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
            if (fromPreview) RefreshViewPreview(false);      // 预览里改的：立刻按新做法重算，还是选中这一樘
            Log("已套用类型：" + _canvas.CurrentType.Code + "（洞口 " + opening.Width.ToString("0") + "×"
                + opening.Height.ToString("0") + "，" + (fromPreview ? "来自立面预览选中" : "来自平面选中") + "）");
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
            _model.Rooms.RemoveAll(r => r != null && string.Equals(r.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase));
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
            else if (hit.Kind == "axis")
            {
                var axis = (_model.Axes ?? new List<AxisModel>()).FirstOrDefault(a => a != null && Same(a.Id, hit.Id));
                if (axis == null) return;
                AddField(host, ref row, "轴号", axis.Name ?? string.Empty, v => { axis.Name = v; });
                AddInfo(host, ref row, axis.Vertical ? "方向：竖轴（沿 Y，标 X）" : "方向：横轴（沿 X，标 Y）");
                AddField(host, ref row, axis.Vertical ? "位置 X" : "位置 Y", axis.Position.ToString("0"), v => axis.Position = Num(v, axis.Position));
                AddField(host, ref row, "延伸起", axis.ExtentStart.ToString("0"), v => axis.ExtentStart = Num(v, axis.ExtentStart));
                AddField(host, ref row, "延伸止", axis.ExtentEnd.ToString("0"), v => axis.ExtentEnd = Num(v, axis.ExtentEnd));
                AddInfo(host, ref row, "（延伸填 0 = 按建筑范围自动；轴号会按位置自动重排）");
            }
            else if (hit.Kind == "room")
            {
                var room = (_model.Rooms ?? new List<RoomModel>()).FirstOrDefault(r => r != null && Same(r.Id, hit.Id));
                if (room == null) return;
                AddField(host, ref row, "房间名", room.Name ?? string.Empty, v => room.Name = v);
                AddInfo(host, ref row, "面积 " + room.AreaSquareMetres.ToString("0.00") + " m²（按轮廓现算）");
                AddInfo(host, ref row, "轮廓 " + (room.Outline ?? new List<PointModel>()).Count + " 个点（拖房间名可整体移动）");
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

