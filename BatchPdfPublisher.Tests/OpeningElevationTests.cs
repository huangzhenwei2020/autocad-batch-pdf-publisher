using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

/// <summary>
/// 立面门窗分格与开启线的护栏测试（纯逻辑，不需要 AutoCAD）。
///
/// 这一层的关键承诺：**立面里的门窗是插件那套门窗立面生成器画的**（同一份源码），
/// 程序只负责"按编号查类型库 → 给出洞口尺寸 → 把生成结果裁进洞口并做遮挡"。
/// 所以这里钉住四件事：
///   1. 分格/开启线确实按类型库画出来了，而且坐标是算得出来的（不是"差不多有线条"）；
///   2. 背立面是镜像的，门的合页侧不会画反；
///   3. 被前面的墙挡住的部分不画（北立面的窗不会透到南墙上）；
///   4. 参数不合法 / 编号查不到时不炸，退回单格并给提示。
/// </summary>
internal static class OpeningElevationTests
{
    private const double Tolerance = 0.5d;

    public static void Run()
    {
        DrawsMullionsAndOpeningLines();
        MirrorsOnBackElevation();
        ClipsHiddenParts();
        HonoursCustomCellLayout();
        SimplifiesAtHundredthScale();
        FallsBackWhenParametersInvalid();
        AnchorsAndLabels();
        DimensionsAndSchedule();
        PlanProjection();
        SheetComposition();
        StudioLaunchChecks();
        Console.WriteLine("PASS 立面门窗：分格与开启线 / 背立面镜像 / 遮挡裁剪 / 自定义分格 / 1:100-1:50 详简 / 参数兜底 / 可点选锚点与编号 / 尺寸与门窗表 / 平面图 / 排版出图 / 启动与取件");
    }

    // ───────────────────────── 11. 从 CAD 启动建模程序 / 落图取件 ─────────────────────────

    /// <summary>
    /// CAD 里「建筑模型」命令（JZMX）要能把项目文件夹与模型名称传给建模程序，
    /// 落图（LTTZ）要能直接列出本项目已生成的图纸/视图让人挑序号 —— 这两件事的纯逻辑在这里钉住。
    /// </summary>
    private static void StudioLaunchChecks()
    {
        // 1) 启动参数
        var arguments = StudioLaunch.BuildArguments(@"H:\项目\万落示例\", " 样例-两层小房子 ");
        Assert(arguments == "--project \"H:\\项目\\万落示例\" --model \"样例-两层小房子\"",
            "启动参数不对：" + arguments);
        Assert(StudioLaunch.BuildArguments(null, null) == string.Empty, "空参数应得到空字符串");
        Assert(StudioLaunch.BuildArguments(@"C:\a", null) == "--project \"C:\\a\"", "只有项目文件夹时不该带 --model");

        // 2) 找程序：按候选顺序取第一个存在的
        var root = Path.Combine(Path.GetTempPath(), "WanluoStudioLaunchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var exe = Path.Combine(root, StudioLaunch.ExecutableName);
            File.WriteAllText(exe, "stub");
            Assert(StudioLaunch.FindExecutable(new[] { null, Path.Combine(root, "没有.exe"), exe }) == Path.GetFullPath(exe),
                "应在候选里找到存在的程序");
            Assert(StudioLaunch.FindExecutable(new[] { Path.Combine(root, "没有.exe") }) == null, "都不存在时应返回 null");

            // 发布目录候选：<根>\CadApi\R24 → <根>\建筑模型\万落建筑模型.exe
            var band = Path.Combine(root, "CadApi", "R24");
            Directory.CreateDirectory(band);
            var published = Path.Combine(root, StudioLaunch.RelativeFolder, StudioLaunch.ExecutableName);
            Directory.CreateDirectory(Path.GetDirectoryName(published));
            File.WriteAllText(published, "stub");
            Assert(StudioLaunch.DefaultCandidates(band).Any(c => string.Equals(c, published, StringComparison.OrdinalIgnoreCase)),
                "发布目录候选里应包含 <发布根>\\建筑模型\\万落建筑模型.exe");
            Assert(StudioLaunch.FindExecutable(StudioLaunch.DefaultCandidates(band)) == Path.GetFullPath(published),
                "应能在发布目录候选里找到建模程序");

            // 3) 找模型目录 + 列视图清单
            var projectFolder = Path.Combine(root, "项目A");
            var modelFolder = Path.Combine(projectFolder, StudioLaunch.ModelFolderName, "住宅楼");
            Directory.CreateDirectory(Path.Combine(modelFolder, StudioLaunch.ViewsFolderName));
            Assert(StudioLaunch.FindModelFolder(projectFolder, "住宅楼") == modelFolder, "按模型名称应找到模型目录");
            Assert(StudioLaunch.FindModelFolder(projectFolder, "不存在") == null, "模型名不存在时应返回 null");
            Assert(StudioLaunch.FindModelFolder(projectFolder, null) == modelFolder, "只有一个模型时应自动用它");
            Assert(StudioLaunch.FindModelFolder(Path.Combine(root, "没有项目"), null) == null, "没有项目目录时返回 null");

            var south = new ViewDocument { Id = "elev-south", Title = "住宅楼 南立面图", Kind = ViewKind.Elevation, Scale = 100 };
            south.Lines.Add(new ViewLine { Layer = ViewLayers.Elevation, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 0 });
            BuildingModelJson.SaveView(Path.Combine(modelFolder, StudioLaunch.ViewsFolderName, "elev-south.json"), south);
            var sheet = new ViewDocument { Id = "sheet-1", Title = "建施-01", Kind = ViewKind.Sheet, Scale = 1, PaperName = "A3" };
            sheet.Lines.Add(new ViewLine { Layer = ViewLayers.SheetFrame, X1 = 0, Y1 = 0, X2 = 420, Y2 = 0 });
            BuildingModelJson.SaveView(Path.Combine(modelFolder, StudioLaunch.ViewsFolderName, "sheet-1.json"), sheet);
            File.WriteAllText(Path.Combine(modelFolder, StudioLaunch.ViewsFolderName, "坏文件.json"), "{ 这不是 json");

            var entries = StudioLaunch.ListViews(modelFolder);
            Assert(entries.Count == 2, "坏文件应被跳过，只列出两张，实际 " + entries.Count);
            Assert(entries[0].Kind == ViewKind.Sheet && entries[0].Title == "建施-01", "图纸应排在最前面");
            Assert(entries[1].Id == "elev-south" && entries[1].Display.IndexOf("立面", StringComparison.Ordinal) >= 0,
                "清单里应能看出类型：" + entries[1].Display);

            // 4) "待落图"标记：写 → 读 → 清单里带 ★ 且排最前 → 落完一张少一张
            var sheetPath = Path.Combine(modelFolder, StudioLaunch.ViewsFolderName, "sheet-1.json");
            Assert(StudioLaunch.PendingFilePath(modelFolder) == Path.Combine(modelFolder, StudioLaunch.ViewsFolderName, "待落图.txt"),
                "待落图文件应放在 views 目录下");
            Assert(StudioLaunch.ReadPending(modelFolder) == null, "还没推过时应返回 null");
            Assert(StudioLaunch.WritePending(modelFolder, new[]
            {
                new StudioPendingEntry { Id = "sheet-1", FilePath = sheetPath },
                new StudioPendingEntry { Id = "不存在的", FilePath = "" }        // 空路径应被跳过
            }), "写待落图清单应成功");

            var pendingList = StudioLaunch.ReadPending(modelFolder);
            Assert(pendingList != null && pendingList.Entries.Count == 1 && pendingList.Entries[0].Id == "sheet-1",
                "读回来的待落图清单应只有 1 条（空路径被跳过）");
            entries = StudioLaunch.ListViews(modelFolder);
            Assert(entries[0].Pending && entries[0].Id == "sheet-1", "待落图的那张应排在清单最前并带 ★");
            Assert(entries[0].Display.StartsWith("★", StringComparison.Ordinal), "清单显示应带 ★：" + entries[0].Display);
            Assert(entries.Count(entry => entry.Pending) == 1, "只有一张是待落图");

            StudioLaunch.RemovePending(modelFolder, "sheet-1");
            Assert(StudioLaunch.ReadPending(modelFolder) == null, "落完图后待落图文件应被删掉");
            Assert(StudioLaunch.ListViews(modelFolder).All(entry => !entry.Pending), "落完图后不该再有 ★");
            Console.WriteLine("   启动与取件：参数、发布目录候选、模型目录定位、视图清单（跳过坏文件）、待落图写读清 都符合预期");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ───────────────────────── 10. 排版出图（图纸） ─────────────────────────

    /// <summary>
    /// 图纸 = 一份 Kind=Sheet 的视图产物（单位是图纸毫米，落图后按 1:1 出图）：
    /// 图框留边（装订边 25、其余 5）、右下角标题栏、视图按各自比例缩到纸面并排进格子、
    /// 尺寸转成带显式文字的尺寸（纸面距离不再是真实尺寸）、超格的视图出提示。
    /// </summary>
    private static void SheetComposition()
    {
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var library = SampleModelFactory.CreateDemoOpeningLibrary();
        var views = new List<ViewDocument>();
        foreach (var definition in SampleModelFactory.CreateDefaultViews(model.Name))
            views.Add(OrthographicProjector.Project(model, definition, library));
        foreach (var storey in model.Storeys)
            views.Add(OrthographicProjector.Project(model, SampleModelFactory.CreatePlanView(storey), library));
        views.Add(OrthographicProjector.ProjectSchedule(model, library, "门窗表"));

        var sheets = SampleModelFactory.CreateDefaultSheets(model);
        Assert(sheets.Count == 4, "默认套图应有 4 张（两层平面 + 立面 + 剖面/门窗表），实际 " + sheets.Count);
        Assert(sheets[0].Paper == "A3" && sheets[0].Landscape, "默认图纸应为 A3 横");

        // 只排一张平面：便于按算得出来的数字核对
        var one = new SheetDefinitionModel
        {
            Id = "sheet-test", Number = "建施-99", Title = "一层平面图", Paper = "A3", Landscape = true
        };
        one.ViewIds.Add("plan-1F");
        var sheet = SheetComposer.Compose(views, one);

        Assert(sheet.Kind == ViewKind.Sheet && sheet.Scale == 1, "图纸应是 Kind=Sheet、比例 1（图纸毫米）");
        Assert(sheet.PaperName == "A3" && Math.Abs(sheet.PaperWidth - 420d) < 0.01d && Math.Abs(sheet.PaperHeight - 297d) < 0.01d,
            "图纸应记录纸张规格（落图时按它匹配项目图框）：实际 " + sheet.PaperName + " " + sheet.PaperWidth + "×" + sheet.PaperHeight);
        Assert(sheet.Lines.Any(l => l.Layer == ViewLayers.SheetFrame), "自带图框应画在 WL-模型-图纸框 图层上（套用项目图框时好跳过）");
        Assert(sheet.Texts.Where(t => t.Text == "一层平面图" || (t.Text ?? "").StartsWith("图号 ", StringComparison.Ordinal))
            .All(t => t.Layer == ViewLayers.SheetFrame), "标题栏文字也应在 WL-模型-图纸框 图层上");
        Assert(sheet.Title.IndexOf("建施-99", StringComparison.Ordinal) >= 0
            && sheet.Title.IndexOf("A3", StringComparison.Ordinal) >= 0, "图纸标题应含图号与纸张：实际 " + sheet.Title);
        Assert(sheet.Texts.Any(t => t.Text == "一层平面图" && Math.Abs(t.Height - 7d) < 0.01d), "标题栏缺少图名");
        Assert(sheet.Texts.Any(t => t.Text != null && t.Text.StartsWith("图号 建施-99", StringComparison.Ordinal)), "标题栏缺少图号");
        Assert(sheet.Texts.Any(t => t.Text != null && t.Text.IndexOf("万落建筑工具", StringComparison.Ordinal) >= 0), "标题栏缺少单位/日期");

        // 图框：A3 横 = 420×297；外框 0..420/0..297，图框 25..415 / 5..292
        Assert(HasViewLine(sheet, 0d, 0d, 420d, 0d) && HasViewLine(sheet, 0d, 297d, 420d, 297d), "缺少纸边外框");
        Assert(HasViewLine(sheet, 25d, 5d, 415d, 5d) && HasViewLine(sheet, 25d, 292d, 415d, 292d), "缺少图框（留边 25/5）");
        Assert(HasViewLine(sheet, 235d, 5d, 415d, 5d) || sheet.Lines.Any(l => Math.Abs(l.X1 - 235d) < Tolerance && Math.Abs(l.X2 - 235d) < Tolerance),
            "缺少标题栏左边线（右下角 180 宽）");

        // 视图按 1:100 缩到纸面：墙断面（Cut 层）轮廓 7440×5640 → 74.4×56.4 mm，且都在图框内
        var walls = sheet.Lines.Where(l => l.Layer == ViewLayers.Cut).ToList();
        Assert(walls.Count > 0, "图纸上应有墙断面（Cut 层）");
        var width = walls.Max(l => Math.Max(l.X1, l.X2)) - walls.Min(l => Math.Min(l.X1, l.X2));
        var height = walls.Max(l => Math.Max(l.Y1, l.Y2)) - walls.Min(l => Math.Min(l.Y1, l.Y2));
        Assert(Math.Abs(width - 74.4d) < 1d, "平面图在图纸上宽应约 74.4mm（7440 的 1:100），实际 " + Math.Round(width, 2));
        Assert(Math.Abs(height - 56.4d) < 1d, "平面图在图纸上高应约 56.4mm（5640 的 1:100），实际 " + Math.Round(height, 2));
        // 轴线也要一起排进图纸（比建筑范围更长），并且所有内容都在图框内
        var axisLines = sheet.Lines.Where(l => l.Layer == ViewLayers.Axis).ToList();
        Assert(axisLines.Count == 5, "图纸上应有 5 条轴线，实际 " + axisLines.Count);
        Assert(axisLines.Max(l => Math.Max(l.X1, l.X2)) - axisLines.Min(l => Math.Min(l.X1, l.X2)) > width,
            "轴线应比建筑范围更长");
        // 视图内容（不含图框/标题栏那两层）都应落在图框内
        var inside = sheet.Lines.Where(l => l.Layer != ViewLayers.Title && l.Layer != ViewLayers.SheetFrame);
        Assert(inside.All(l => Math.Min(l.X1, l.X2) >= 24.5d && Math.Max(l.X1, l.X2) <= 415.5d
            && Math.Min(l.Y1, l.Y2) >= 4.5d && Math.Max(l.Y1, l.Y2) <= 292.5d), "视图内容应排在图框内");

        // 尺寸转成显式文字（纸面距离不再是真实尺寸）：轴线 3600 的尺寸文字要是 "3600"
        Assert(sheet.Dimensions.Count > 0, "图纸上应保留尺寸");
        Assert(sheet.Dimensions.All(d => !string.IsNullOrWhiteSpace(d.Text)), "图纸上的尺寸必须有显式文字（否则 CAD 量出来的是纸面距离）");
        Assert(sheet.Dimensions.Any(d => d.Text == "3600"), "图纸上应能读到 3600 这一档轴线尺寸");

        // 超格提示：四个立面挤进 A4 竖排（2×2 格，每格约 84mm 宽），1:100 的立面应报超格
        var tight = new SheetDefinitionModel { Id = "sheet-tight", Number = "建施-98", Title = "挤一挤", Paper = "A4", Landscape = false };
        foreach (var direction in new[] { "elev-south", "elev-north", "elev-east", "elev-west" })
            tight.ViewIds.Add(direction);
        var tightSheet = SheetComposer.Compose(views, tight);
        Assert(tightSheet.Warnings.Any(w => w.IndexOf("超出图纸格", StringComparison.Ordinal) >= 0), "塞不下时应给出超格提示：" + string.Join(" / ", tightSheet.Warnings.ToArray()));

        // 找不到的视图要提示，而不是崩
        var missing = new SheetDefinitionModel { Id = "sheet-missing", Number = "建施-97", Title = "缺视图" };
        missing.ViewIds.Add("不存在的视图");
        var missingSheet = SheetComposer.Compose(views, missing);
        Assert(missingSheet.Warnings.Any(w => w.IndexOf("找不到视图", StringComparison.Ordinal) >= 0), "找不到视图时应给出提示");
        Console.WriteLine("   排版出图：A3 横图框 25/5、标题栏 180×40、平面 74.4×56.4mm 落在图框内、尺寸带显式文字，超格/缺视图都有提示");
    }

    private static bool HasViewLine(ViewDocument view, double x1, double y1, double x2, double y2)
    {
        foreach (var line in view.Lines)
        {
            if (Math.Abs(line.X1 - x1) < Tolerance && Math.Abs(line.Y1 - y1) < Tolerance
                && Math.Abs(line.X2 - x2) < Tolerance && Math.Abs(line.Y2 - y2) < Tolerance) return true;
            if (Math.Abs(line.X1 - x2) < Tolerance && Math.Abs(line.Y1 - y2) < Tolerance
                && Math.Abs(line.X2 - x1) < Tolerance && Math.Abs(line.Y2 - y1) < Tolerance) return true;
        }
        return false;
    }

    // ───────────────────────── 9. 平面图投影 ─────────────────────────

    /// <summary>
    /// 平面图 v1：墙两条面线（洞口断开 + 封口）、窗两条玻璃线、门一条扇线 + 90° 弧、柱断面矩形，
    /// 外围横向/竖向定位链与总尺寸。
    /// </summary>
    private static void PlanProjection()
    {
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var storey = model.Storeys[0];                       // 一层
        var definition = SampleModelFactory.CreatePlanView(storey);
        var plan = OrthographicProjector.Project(model, definition, null);

        Assert(plan.Kind == ViewKind.Plan, "应生成平面类视图");
        Assert(plan.Texts.Any(t => t.Layer == ViewLayers.Title && t.Text.IndexOf("平面图", StringComparison.Ordinal) >= 0), "平面图缺图名");

        var cut = plan.Lines.Where(l => l.Layer == ViewLayers.Cut).ToList();
        var opening = plan.Lines.Where(l => l.Layer == ViewLayers.Opening).ToList();
        // 视图坐标 = 模型坐标 - 视图原点（Normalize 会把整张图平移到 0 起）
        var originX = plan.OriginX;
        var originY = plan.OriginY;
        Func<double, double> X = value => value - originX;
        Func<double, double> Y = value => value - originY;

        // 南墙（y=0，厚 240）：外面线 y=-120，在 1450-2950（窗）与 4950-5850（门）处断开
        Assert(HasPlanLine(cut, X(0d), Y(-120d), X(1450d), Y(-120d)), "南墙外面线应从 x=0 画到窗左 1450");
        Assert(HasPlanLine(cut, X(2950d), Y(-120d), X(4950d), Y(-120d)), "窗与门之间的墙面线（2950→4950）没画出来");
        Assert(HasPlanLine(cut, X(5850d), Y(-120d), X(7200d), Y(-120d)), "门右到墙端（5850→7200）没画出来");
        Assert(!HasPlanLine(cut, X(1450d), Y(-120d), X(2950d), Y(-120d)), "窗洞范围内的墙面线不该画出来");
        Assert(HasPlanLine(cut, X(1450d), Y(-120d), X(1450d), Y(120d)), "窗左门垛封口没画出来");
        // 窗：两条玻璃线（墙厚内侧 ±42）
        Assert(HasPlanLine(opening, X(1450d), Y(42d), X(2950d), Y(42d)) && HasPlanLine(opening, X(1450d), Y(-42d), X(2950d), Y(-42d)),
            "窗的两条玻璃线没画出来");
        // 门：扇线（从门垛沿墙法线出 900）+ 8 段开启弧（弧终点落在另一侧门垛上）
        Assert(HasPlanLine(opening, X(4950d), Y(0d), X(4950d), Y(900d)), "门的扇线（4950,0 → 4950,900）没画出来");
        var arcReachesJamb = opening.Any(l => (Math.Abs(l.X1 - X(5850d)) < Tolerance && Math.Abs(l.Y1 - Y(0d)) < Tolerance)
            || (Math.Abs(l.X2 - X(5850d)) < Tolerance && Math.Abs(l.Y2 - Y(0d)) < Tolerance));
        Assert(arcReachesJamb, "门的 90° 开启弧应扫到另一侧门垛（5850,0）");
        Assert(opening.Count == 15, "一层平面的门窗图例应有 15 条线（3 窗×2 + 1 门×9），实际 " + opening.Count);
        // 柱断面
        var column = model.Columns.First(c => string.Equals(c.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase));
        Assert(HasPlanLine(cut, X(column.X - column.Width / 2d), Y(column.Y - column.Depth / 2d),
            X(column.X + column.Width / 2d), Y(column.Y - column.Depth / 2d)), "柱断面矩形没画出来");

        // 外围尺寸：三道（洞口定位 / 轴线 / 总尺寸），总长 7440（外皮 -120→7320）、总宽 5640（-120→5520）
        var totalWidth = plan.Dimensions.FirstOrDefault(d => d.Note == "总长");
        Assert(totalWidth != null && Math.Abs(totalWidth.From + originX - (-120d)) < Tolerance && Math.Abs(totalWidth.To + originX - 7320d) < Tolerance,
            "平面图总长应为 -120→7320（7440），实际 " + (totalWidth == null ? "缺失" : (totalWidth.From + originX) + "→" + (totalWidth.To + originX)));
        var totalDepth = plan.Dimensions.FirstOrDefault(d => d.Note == "总宽");
        Assert(totalDepth != null && Math.Abs(totalDepth.From + originY - (-120d)) < Tolerance && Math.Abs(totalDepth.To + originY - 5520d) < Tolerance,
            "平面图总宽应为 -120→5520（5640），实际 " + (totalDepth == null ? "缺失" : (totalDepth.From + originY) + "→" + (totalDepth.To + originY)));
        var horizontal = plan.Dimensions.Where(d => !d.Vertical && d.Note == "洞口定位（横向）").ToList();
        // 洞口边线都要成为链上的分界点（南墙窗 1450/2950、南墙门 4950/5850）
        foreach (var edge in new[] { 1450d, 2950d, 4950d, 5850d })
            Assert(horizontal.Any(d => Math.Abs(d.From + originX - edge) < Tolerance || Math.Abs(d.To + originX - edge) < Tolerance),
                "平面横向洞口定位链应含洞口边线 " + edge);
        Assert(Math.Abs(horizontal.Sum(d => d.To - d.From) - 7440d) < 0.01d, "洞口定位链各段之和应等于总长 7440");

        // 中层：轴线尺寸（1/2/3 → 0、3600、7200）
        var axisRun = plan.Dimensions.Where(d => !d.Vertical && d.Note == "轴线（横向）").ToList();
        Assert(axisRun.Count > 0, "有轴网时平面应出一条轴线尺寸链");
        Assert(axisRun.Any(d => Math.Abs(d.From + originX - 0d) < Tolerance && Math.Abs(d.To + originX - 3600d) < Tolerance)
            && axisRun.Any(d => Math.Abs(d.From + originX - 3600d) < Tolerance && Math.Abs(d.To + originX - 7200d) < Tolerance),
            "轴线链应含 0→3600 与 3600→7200（开间）");
        Assert(axisRun.All(d => Math.Abs(d.LinePosition + originY - (-2120d)) < Tolerance), "轴线链应画在中层（外皮下 2000）");

        // 轴网：3 条竖轴 + 2 条横轴（点划线）+ 每端一个轴号圆圈 + 轴号文字
        var axisLines = plan.Lines.Where(l => l.Layer == ViewLayers.Axis).ToList();
        Assert(axisLines.Count == 5, "轴网应有 5 条轴线，实际 " + axisLines.Count);
        Assert(axisLines.All(l => string.Equals(l.LineType, "CENTER", StringComparison.OrdinalIgnoreCase)), "轴线应用点划线（CENTER）");
        Assert(axisLines.Any(l => Math.Abs(l.X1 - l.X2) < Tolerance && Math.Abs(l.X1 + originX - 3600d) < Tolerance),
            "应有 x=3600 的竖轴（2 号轴）");
        Assert(axisLines.Any(l => Math.Abs(l.Y1 - l.Y2) < Tolerance && Math.Abs(l.Y1 + originY - 5400d) < Tolerance),
            "应有 y=5400 的横轴（B 号轴）");
        Assert(plan.Circles.Count == 10, "5 条轴线应各带 2 个轴号圆圈，实际 " + plan.Circles.Count);
        Assert(plan.Circles.All(c => Math.Abs(c.Radius - 400d) < Tolerance), "轴号圆圈半径应为 400");
        foreach (var name in new[] { "1", "2", "3", "A", "B" })
            Assert(plan.Texts.Any(t => t.Layer == ViewLayers.Axis && t.Text == name), "缺少轴号 " + name);

        // 房间：轮廓 + 名称 + 面积（起居室 3360×5160 = 17.34 m²）
        var roomLines = plan.Lines.Where(l => l.Layer == ViewLayers.Room).ToList();
        Assert(roomLines.Count == 8, "一层两个房间应有 8 条轮廓线，实际 " + roomLines.Count);
        Assert(plan.Texts.Any(t => t.Layer == ViewLayers.Room && t.Text == "起居室"), "缺少房间名「起居室」");
        var expectedArea = (3360d * 5160d / 1_000_000d).ToString("0.00") + " m²";
        Assert(plan.Texts.Any(t => t.Layer == ViewLayers.Room && t.Text == expectedArea),
            "缺少房间面积 " + expectedArea + "（实际面积文字："
            + string.Join("、", plan.Texts.Where(t => t.Layer == ViewLayers.Room).Select(t => t.Text).ToArray()) + "）");
        Assert(plan.Anchors.Count(a => a.Kind == "axis") == 5, "轴线也应有锚点（5 条）");
        Assert(plan.Anchors.Count(a => a.Kind == "room") == 2, "一层应有 2 个房间锚点");

        // 可点选：一层 4 个洞口 + 5 条轴线 + 2 个房间都有锚点
        Assert(plan.Anchors.Count(a => a.Kind == "opening") == 4, "一层平面应有 4 个门窗锚点，实际 " + plan.Anchors.Count(a => a.Kind == "opening"));
        var anchor = plan.Anchors.First(a => string.Equals(a.ElementId, "1F-S-C1518", StringComparison.OrdinalIgnoreCase));
        Assert(anchor.X1 + originX <= 1450d && anchor.X2 + originX >= 2950d && anchor.Y2 - anchor.Y1 >= 240d, "窗锚点框应盖住洞口与墙厚");
        Console.WriteLine("   平面图：墙面线在洞口断开、窗 2 条玻璃线、门扇线 + 弧、柱断面、5 条轴线 + 10 个轴号圈、"
            + "2 个房间（起居室 " + expectedArea + "），总长 7440 / 总宽 5640，锚点 11 个");
    }

    /// <summary>平面上有没有一条给定端点（顺序无关，容差内）的线。</summary>
    private static bool HasPlanLine(List<ViewLine> lines, double x1, double y1, double x2, double y2)
    {
        foreach (var line in lines)
        {
            if (Math.Abs(line.X1 - x1) < Tolerance && Math.Abs(line.Y1 - y1) < Tolerance
                && Math.Abs(line.X2 - x2) < Tolerance && Math.Abs(line.Y2 - y2) < Tolerance) return true;
            if (Math.Abs(line.X1 - x2) < Tolerance && Math.Abs(line.Y1 - y2) < Tolerance
                && Math.Abs(line.X2 - x1) < Tolerance && Math.Abs(line.Y2 - y1) < Tolerance) return true;
        }
        return false;
    }

    // ───────────────────────── 8. 竖向尺寸与门窗表 ─────────────────────────

    /// <summary>
    /// 竖向尺寸两条链 + 总高，以及按编号汇总的门窗表：数值、位置、汇总数都要对得上模型与类型库。
    /// </summary>
    private static void DimensionsAndSchedule()
    {
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var library = SampleModelFactory.CreateDemoOpeningLibrary();
        var definition = SampleModelFactory.CreateDefaultViews(model.Name)[0];      // 南立面，1:100
        var view = OrthographicProjector.Project(model, definition, library);

        // 只取"建筑几何"那批线（标高符号线在图名/轮廓之外，算进去总长会变成 8840）
        var geometry = view.Lines.Where(l => l.Layer != ViewLayers.LevelText && l.Layer != ViewLayers.Title).ToList();
        var originX = view.OriginX;
        var originY = view.OriginY;
        // 视图里的坐标 = 模型坐标 - 原点；下面统一换算回模型坐标来核对
        var minU = geometry.Min(l => Math.Min(l.X1, l.X2)) + originX;
        var maxU = geometry.Max(l => Math.Max(l.X1, l.X2)) + originX;
        Func<double, double> modelU = value => value + originX;
        Func<double, double> modelZ = value => value + originY;
        var storeyDimensions = view.Dimensions.Where(d => d.Note != null && d.Note.StartsWith("层高", StringComparison.Ordinal)).ToList();
        Assert(storeyDimensions.Count == 2, "两层应各有 1 条层高尺寸，实际 " + storeyDimensions.Count);
        Assert(storeyDimensions.Any(d => Math.Abs(modelZ(d.From) - 0d) < Tolerance && Math.Abs(modelZ(d.To) - 3600d) < Tolerance),
            "一层层高尺寸应为 0→3600");
        Assert(storeyDimensions.Any(d => Math.Abs(modelZ(d.From) - 3600d) < Tolerance && Math.Abs(modelZ(d.To) - 6900d) < Tolerance),
            "二层层高尺寸应为 3600→6900");
        Assert(storeyDimensions.All(d => Math.Abs(modelU(d.LinePosition) - (maxU + 2000d)) < Tolerance),
            "层高尺寸应画在立面右侧 maxU+2000 处");

        var total = view.Dimensions.FirstOrDefault(d => d.Note == "总高");
        Assert(total != null, "缺少总高尺寸");
        Assert(Math.Abs(modelZ(total.From) - 0d) < Tolerance && Math.Abs(modelZ(total.To) - 6900d) < Tolerance
            && Math.Abs(modelU(total.LinePosition) - (maxU + 3000d)) < Tolerance, "总高应为 0→6900，画在 maxU+3000 处");

        // 一层南墙有 1500×1800@900 的窗与 900×2100 的门 → 定位链 0→900→2100→2700
        //（注意只算这张图上画出来的洞口：北面/东面的窗不该出现在南立面的尺寸链里）
        var firstFloor = view.Dimensions.Where(d => d.Note != null
            && d.Note.StartsWith("洞口定位", StringComparison.Ordinal)
            && d.Note.IndexOf("一层", StringComparison.Ordinal) >= 0).ToList();
        Assert(firstFloor.Count == 3, "一层洞口定位链应有 3 段（0-900-2100-2700），实际 " + firstFloor.Count);
        Assert(firstFloor.Any(d => Math.Abs(modelZ(d.From) - 0d) < Tolerance && Math.Abs(modelZ(d.To) - 900d) < Tolerance), "缺少窗台 900 那一段");
        Assert(firstFloor.Any(d => Math.Abs(modelZ(d.From) - 900d) < Tolerance && Math.Abs(modelZ(d.To) - 2100d) < Tolerance), "缺少 900→2100 那一段");
        Assert(firstFloor.Any(d => Math.Abs(modelZ(d.From) - 2100d) < Tolerance && Math.Abs(modelZ(d.To) - 2700d) < Tolerance), "缺少 2100→2700 那一段");
        Assert(firstFloor.All(d => Math.Abs(modelU(d.LinePosition) - (minU - 1200d)) < Tolerance), "一层定位链应画在立面左侧 minU-1200 处");
        Assert(firstFloor.All(d => string.IsNullOrEmpty(d.Text)), "尺寸文字应留给 CAD 自己量（Text 留空）");

        // 横向：内层洞口定位链 + 外层总长。
        // 立面参考：一层南窗 1450..2950、一层南门 4950..5850、二层南窗 2850..4350、两端墙外皮 -120 / 7320
        var minZ = geometry.Min(l => Math.Min(l.Y1, l.Y2)) + originY;
        var horizontal = view.Dimensions.Where(d => !d.Vertical).ToList();
        var innerRun = horizontal.Where(d => d.Note == "洞口定位（横向）").ToList();
        Assert(innerRun.Count == 7, "横向洞口定位链应有 7 段（-120｜1450｜2850｜2950｜4350｜4950｜5850｜7320），实际 " + innerRun.Count);
        Assert(innerRun.All(d => Math.Abs(modelZ(d.AnchorPosition) - minZ) < Tolerance && Math.Abs(modelZ(d.LinePosition) - (minZ - 1200d)) < Tolerance),
            "横向定位链应画在建筑底边下方 1200 处");
        var expectedEdges = new[] { -120d, 1450d, 2850d, 2950d, 4350d, 4950d, 5850d, 7320d };
        for (var i = 0; i + 1 < expectedEdges.Length; i++)
            Assert(innerRun.Any(d => Math.Abs(modelU(d.From) - expectedEdges[i]) < Tolerance && Math.Abs(modelU(d.To) - expectedEdges[i + 1]) < Tolerance),
                "横向定位链缺少 " + expectedEdges[i] + "→" + expectedEdges[i + 1] + " 那一段；实际是："
                + string.Join("、", innerRun.OrderBy(d => d.From).Select(d => Math.Round(modelU(d.From)) + "→" + Math.Round(modelU(d.To))).ToArray()));
        var totalWidth = horizontal.FirstOrDefault(d => d.Note == "总长");
        Assert(totalWidth != null, "缺少总长尺寸");
        Assert(Math.Abs(modelU(totalWidth.From) - (-120d)) < Tolerance && Math.Abs(modelU(totalWidth.To) - 7320d) < Tolerance
            && Math.Abs(modelZ(totalWidth.LinePosition) - (minZ - 2000d)) < Tolerance,
            "总长应为 -120→7320（7440），画在建筑底边下方 2000 处");
        Assert(Math.Abs(innerRun.Sum(d => d.To - d.From) - 7440d) < 0.01d, "横向定位链各段之和应等于总长 7440");

        // 门窗表：按编号汇总（样例里 C1215×2、C1518×2、M0921×1）
        var schedule = OrthographicProjector.ProjectSchedule(model, library, "门窗表");
        Assert(schedule.Kind == ViewKind.Schedule, "门窗表应是 Schedule 类型视图");
        var texts = schedule.Texts.Select(t => t.Text).ToList();
        foreach (var header in new[] { "编号", "类型", "洞口尺寸", "窗台/落地", "樘数", "做法（分格 / 开启）" })
            Assert(texts.Contains(header), "门窗表缺少表头：" + header);
        Assert(texts.Contains("C1215") && texts.Contains("C1518") && texts.Contains("M0921"), "门窗表缺少编号行");
        Assert(texts.Contains("1200×1500") && texts.Contains("1500×1800") && texts.Contains("900×2100"), "门窗表尺寸列不对");
        Assert(texts.Count(t => t == "2") == 2 && texts.Count(t => t == "1") == 1, "门窗表樘数统计不对（C1215/C1518 各 2、M0921 为 1）");
        Assert(texts.Any(t => t != null && t.IndexOf("双扇等分 / 双向推拉", StringComparison.Ordinal) >= 0), "门窗表的做法列应来自类型库");
        Assert(texts.Any(t => t != null && t.IndexOf("类型库里没有这一条", StringComparison.Ordinal) >= 0) == false, "样例里的编号都应在类型库中");
        var width = schedule.Lines.Max(l => Math.Max(l.X1, l.X2)) - schedule.Lines.Min(l => Math.Min(l.X1, l.X2));
        Assert(Math.Abs(width - 14800d) < Tolerance, "门窗表总宽应为 14800（各列之和），实际 " + width);
        Console.WriteLine("   尺寸与门窗表：层高 2 段 + 总高 6900、一层定位链 3 段（0-900-2100-2700）、门窗表 3 行（宽 "
            + Math.Round(width) + "）");
    }

    // ───────────────────────── 7. 可点选锚点与洞口编号 ─────────────────────────

    /// <summary>
    /// 视图里要有"这个矩形对应模型里哪一樘门窗"的锚点（预览点选、门窗表联动都靠它），
    /// 还要把洞口编号写在图上（有窗台写下方、落地门写上方，字高按比例）。
    /// </summary>
    private static void AnchorsAndLabels()
    {
        var library = Library(Type("C1518", "双扇等分", "双向推拉"));
        var view = Project(SingleWindowModel("C1518"), ElevationDirection.South, library);
        var u = view.OriginX;
        var z = view.OriginY;

        Assert(view.Anchors.Count == 1, "南立面应有一个门窗锚点，实际 " + view.Anchors.Count);
        var anchor = view.Anchors[0];
        Assert(anchor.Kind == "opening" && anchor.ElementId == "o-1", "锚点应指向洞口 o-1，实际 " + anchor.Kind + "/" + anchor.ElementId);
        Assert(Math.Abs(anchor.X1 - (750d - u)) < Tolerance && Math.Abs(anchor.X2 - (2250d - u)) < Tolerance
            && Math.Abs(anchor.Y1 - (900d - z)) < Tolerance && Math.Abs(anchor.Y2 - (2700d - z)) < Tolerance,
            "锚点矩形应与洞口一致：(750,900)-(2250,2700) + 视图原点，实际 ("
            + (anchor.X1 + u) + "," + (anchor.Y1 + z) + ")-(" + (anchor.X2 + u) + "," + (anchor.Y2 + z) + ")");

        var label = view.Texts.FirstOrDefault(t => t.Layer == ViewLayers.Opening && t.Text == "C1518");
        Assert(label != null, "立面上应标出洞口编号 C1518");
        Assert(Math.Abs(label.Height - 200d) < Tolerance, "1:100 的编号字高应为 200mm，实际 " + label.Height);
        Assert(Math.Abs(label.Y - (580d - z)) < Tolerance, "有窗台的窗，编号应写在洞口下方（模型 y=580），实际 " + (label.Y + z));
        var estimated = label.Height * 0.62d * "C1518".Length;
        Assert(Math.Abs(label.X + estimated / 2d - (1500d - u)) < 1d, "编号应大致居中在洞口上（模型 x=1500）");

        // 落地门（窗台 0）：洞口下方是墙脚/地面，编号改写在洞口上方（这樘门高 1800 → 上方即 1800 以上）
        var door = SingleWindowModel("M0921");
        door.Openings[0].Kind = "门";
        door.Openings[0].Sill = 0d;
        door.Openings[0].Height = 2100d;
        var doorView = Project(door, ElevationDirection.South, null);
        var doorLabel = doorView.Texts.FirstOrDefault(t => t.Layer == ViewLayers.Opening && t.Text == "M0921");
        Assert(doorLabel != null, "落地门也要标编号");
        var doorLabelY = doorLabel.Y + doorView.OriginY;
        Assert(doorLabelY > 2100d && doorLabelY < 3000d, "落地门的编号应写在洞口上方（2100 以上、层高以内），实际 " + doorLabelY);
        Console.WriteLine("   锚点与编号：洞口锚点 1 个（750,900)-(2250,2700)、编号 C1518 字高 200 写在窗下、门编号写在门上方（"
            + Math.Round(doorLabelY) + "）");
    }

    // ───────────────────────── 1. 分格与开启线 ─────────────────────────

    /// <summary>
    /// 一道 3000 长的墙、中间一樘 1500×1800@900 的窗（编号 C1518，做法「双扇等分 + 左平开」）。
    /// 生成器给出的几何是可以手算的：两格各 750 宽；外框 50；两扇都是可开启扇，
    /// 所以中间的竖挺合成一条线；每扇的开启线是"合页角 → 对侧中点 → 另一角"的两段折线。
    /// </summary>
    private static void DrawsMullionsAndOpeningLines()
    {
        var model = SingleWindowModel("C1518");
        var library = Library(Type("C1518", "双扇等分", "左平开"));

        var plain = Project(model, ElevationDirection.South, library: null);
        var detailed = Project(model, ElevationDirection.South, library: library);

        // 没有类型库时只画洞口轮廓：4 条边
        var plainEdges = plain.Lines.Count(l => l.Layer == ViewLayers.Opening);
        Assert(plainEdges == 4, "没有类型库时门窗应只有洞口 4 条边，实际 " + plainEdges);
        Assert(plain.Warnings.Any(w => w.IndexOf("不在类型库里", StringComparison.Ordinal) >= 0),
            "没有类型库时应提示信息缺失");

        Assert(detailed.Lines.Count(l => l.Layer == ViewLayers.Opening) > plainEdges + 6,
            "有类型库时门窗线条应明显多于洞口轮廓，实际 " + detailed.Lines.Count(l => l.Layer == ViewLayers.Opening));
        Assert(!detailed.Warnings.Any(w => w.IndexOf("不在类型库里", StringComparison.Ordinal) >= 0),
            "有类型库时不应再提示信息缺失");

        var u = detailed.OriginX;
        var z = detailed.OriginY;
        var detailLines = detailed.Lines.Count(l => l.Layer == ViewLayers.Opening);
        // 洞口 4 条边 + 11 条做法线（外框内边 6 条、中挺 1 条、开启线 4 段）
        Assert(detailLines == 15, "门窗线条应为 15 条（洞口 4 + 做法 11），实际 " + detailLines);
        // 中挺：洞口中间 x=1500 的竖线（两扇都可开启 → 框线并成一条，落在上下框内边之间）
        Assert(HasLine(detailed, 1500d - u, 950d - z, 1500d - u, 2650d - z),
            "缺少中间的分格竖线（x=1500、y=950→2650）");
        // 外框内边：左边 50、上边 1750，第二格靠右 1450
        Assert(HasLine(detailed, 800d - u, 950d - z, 1500d - u, 950d - z), "缺少第一格的下框线（y=950）");
        Assert(HasLine(detailed, 1500d - u, 950d - z, 2200d - u, 950d - z), "缺少第二格的下框线（y=950）");
        Assert(HasLine(detailed, 2200d - u, 950d - z, 2200d - u, 2650d - z), "缺少第二格的右框线（x=2200）");
        // 开启线（左平开）：合页在左，折线打到右中
        Assert(HasLine(detailed, 800d - u, 950d - z, 1500d - u, 1800d - z), "第一扇缺少「左下 → 右中」的开启线");
        Assert(HasLine(detailed, 1500d - u, 1800d - z, 800d - u, 2650d - z), "第一扇缺少「右中 → 左上」的开启线");
        Assert(HasLine(detailed, 1500d - u, 950d - z, 2200d - u, 1800d - z), "第二扇缺少「左下 → 右中」的开启线");
        Assert(HasLine(detailed, 2200d - u, 1800d - z, 1500d - u, 2650d - z), "第二扇缺少「右中 → 左上」的开启线");

        Console.WriteLine("   分格与开启线：洞口 4 条边 + 做法 11 条 = " + detailLines
            + " 条（中挺 x=1500、外框内边 6 条、开启线 4 段）");
    }

    // ───────────────────────── 2. 背立面镜像 ─────────────────────────

    /// <summary>北立面看到的是墙的背面：U 轴与墙轴反向，门的合页侧必须跟着镜像，不能画反。</summary>
    private static void MirrorsOnBackElevation()
    {
        var model = SingleWindowModel("C1518");
        var library = Library(Type("C1518", "双扇等分", "左平开"));
        var north = Project(model, ElevationDirection.North, library);
        var u = north.OriginX;
        var z = north.OriginY;

        // 洞口 x∈[750,2250]；北立面 U=-x → 洞口局部 x=0 对应 U=-750（视图右边）。
        // 第一扇（局部 50..750）的合页在局部 50 → U=-800。
        Assert(HasLine(north, -800d - u, 950d - z, -1500d - u, 1800d - z), "北立面第一扇开启线没有镜像");
        Assert(HasLine(north, -1500d - u, 1800d - z, -800d - u, 2650d - z), "北立面第一扇开启线没有镜像（第二段）");
        // 不镜像的话会画成 U = ou0 + x = -2250 + x：这两段就是错的画法
        Assert(!HasLine(north, -2200d - u, 950d - z, -1500d - u, 1800d - z), "北立面开启线画成了南立面的方向");
        Console.WriteLine("   背立面镜像：开启线合页侧随视线反向（U=-800 一侧）");
    }

    // ───────────────────────── 3. 遮挡裁剪 ─────────────────────────

    /// <summary>前面再加一道墙：被挡住的部分（含分格与开启线）一条都不许冒出来。</summary>
    private static void ClipsHiddenParts()
    {
        var model = SingleWindowModel("C1518");
        var library = Library(Type("C1518", "双扇等分", "左平开"));

        // 完全挡住（新墙在更近的一侧，覆盖洞口整个范围，高 3000）
        var covered = WithFrontWall(model, 0d, 3000d);
        var hidden = Project(covered, ElevationDirection.South, library);
        var hiddenOpening = hidden.Lines.Where(l => l.Layer == ViewLayers.Opening).ToList();
        Assert(hiddenOpening.Count == 0, "洞口被前面的墙完全挡住时不应再画门窗线，实际 " + hiddenOpening.Count);

        // 只挡住左边一段（x∈[0,1200]）：右侧 1200..2250 的分格与开启线要保留，且不许越界
        // 注意用一份干净的模型：上面那道"全挡"的墙要是留在里面，就什么都看不见了
        var partial = WithFrontWall(SingleWindowModel("C1518"), 0d, 1200d);
        var visible = Project(partial, ElevationDirection.South, library);
        var visibleOpening = visible.Lines.Where(l => l.Layer == ViewLayers.Opening).ToList();
        Assert(visibleOpening.Count > 0, "只挡住左边一段时右侧门窗线应保留");
        var limit = 1200d - visible.OriginX;
        var leaking = visibleOpening
            .Where(l => Math.Min(l.X1, l.X2) < limit - Tolerance && l.Layer == ViewLayers.Opening)
            .ToList();
        Assert(leaking.Count == 0, "被挡住的那一段仍有 " + leaking.Count + " 条线冒出来（应在 x≥1200 以内）");
        Console.WriteLine("   遮挡裁剪：全挡 0 条 / 半挡保留 " + visibleOpening.Count + " 条且都在 x≥1200");
    }

    // ───────────────────────── 4. 自定义分格 ─────────────────────────

    /// <summary>
    /// 自定义分格是插件里"一格一格摆"的做法（left,bottom,right,top,开启,是否门[,删除[,材料]]）。
    /// 程序侧只是把它原样交给生成器，这里验证格式没串味：左边固定扇、右边平开扇。
    /// </summary>
    private static void HonoursCustomCellLayout()
    {
        var model = SingleWindowModel("C1500X");
        var type = Type("C1500X", "自定义", "未设置");
        type.CustomCellLayout = "0,0,900,1800,固定,0,0,玻璃|900,0,1500,1800,左平开,1,0,木质";
        var library = Library(type);
        var view = Project(model, ElevationDirection.South, library);
        var u = view.OriginX;
        var z = view.OriginY;

        // 左固定扇的右边框（局部 875 → 视图 1625）、右平开扇的左边框（局部 925 → 视图 1675）：
        // 一个固定扇挨着一个可开启扇时，中挺是"两条线"（各画各的框），不是并成一条。
        Assert(HasLine(view, 1625d - u, 950d - z, 1625d - u, 2650d - z), "左扇右边框（U=1625）没有画出来");
        Assert(HasLine(view, 1675d - u, 900d - z, 1675d - u, 2650d - z), "右扇左边框（U=1675）没有画出来");
        // 左边固定扇（局部 50..875）不应有开启线：这两段就是"如果它是平开扇"会有的线
        Assert(!HasLine(view, 800d - u, 950d - z, 1625d - u, 1800d - z), "固定扇不应有开启线");
        Assert(!HasLine(view, 1625d - u, 1800d - z, 800d - u, 2650d - z), "固定扇不应有开启线（第二段）");
        // 右边平开扇：门扇（IsDoor=1）底边落地（N 型门框不画下框），合页在左 → 折线打到右中
        Assert(HasLine(view, 1675d - u, 900d - z, 2200d - u, 1775d - z), "右扇缺少「左下 → 右中」的开启线");
        Assert(HasLine(view, 2200d - u, 1775d - z, 1675d - u, 2650d - z), "右扇缺少「右中 → 左上」的开启线");
        // 固定扇的材料是玻璃 → 生成器会画 3 条玻璃符号线（也说明"材料"字段传到了生成器）
        var glassMarks = view.Lines.Count(l => l.Layer == ViewLayers.Opening
            && Math.Abs(l.X2 - l.X1) > 1d && Math.Abs(l.Y2 - l.Y1) > 1d
            && (l.X1 + l.X2) / 2d < 1625d - u);
        Assert(glassMarks == 3, "固定玻璃扇应有 3 条玻璃符号线，实际 " + glassMarks);
        Console.WriteLine("   自定义分格：固定玻璃扇 + 平开门扇 → 中挺双线 1625/1675、玻璃符号 3 条、开启线只在右扇");
    }

    // ───────────────────────── 5. 1:100 与 1:50 的详简之分 ─────────────────────────

    /// <summary>
    /// 20mm 安装缝、50mm 门扇内框、玻璃材料符号在 1:50 的门窗立面详图里是内容，
    /// 到 1:100 的立面图上只有零点几毫米，只会把洞口糊成一团 —— 所以 1:100 不画这三样，
    /// 但分格与开启线一律保留（那正是立面图要表达的）。
    /// </summary>
    private static void SimplifiesAtHundredthScale()
    {
        var model = SingleWindowModel("C1518");
        var type = Type("C1518", "单扇", "左平开");
        type.HasInstallationGap = true;
        type.InstallationGap = 20d;
        type.DoorFrameWidth = 50d;
        type.Material = "玻璃";
        var library = Library(type);

        var at100 = Project(model, ElevationDirection.South, library, scale: 100);
        var at50 = Project(model, ElevationDirection.South, library, scale: 50);

        var count100 = at100.Lines.Count(l => l.Layer == ViewLayers.Opening);
        var count50 = at50.Lines.Count(l => l.Layer == ViewLayers.Opening);
        Assert(count50 > count100, "1:50 的详图线条应多于 1:100（实际 " + count50 + " vs " + count100 + "）");

        // 洞口 x∈[750,2250]、z∈[900,2700]。
        // 1:50：安装缝 20 → 分格内边 770；再进外框 50 → 外框内边 820；再进 50 门扇内框 → 870。
        // 1:100：安装缝与门扇内框都不画，只剩外框内边 800。
        var u = at100.OriginX;
        var z = at100.OriginY;
        Assert(!HasLine(at100, 770d - u, 920d - z, 770d - u, 2680d - z), "1:100 不应画 20mm 安装缝");
        Assert(!HasLine(at100, 870d - u, 1020d - z, 870d - u, 2580d - z), "1:100 不应画 50mm 门扇内框");
        var u50 = at50.OriginX;
        var z50 = at50.OriginY;
        Assert(HasLine(at50, 770d - u50, 920d - z50, 770d - u50, 2680d - z50), "1:50 应画出 20mm 安装缝");
        Assert(HasLine(at50, 870d - u50, 1020d - z50, 870d - u50, 2580d - z50), "1:50 应画出 50mm 门扇内框");
        // 玻璃材料符号是 3 条"短斜线"（长度约 260mm）；开启线也是斜线但长度约 1600mm，
        // 所以用"斜且短"来数材料符号。
        Assert(ShortDiagonalCount(at100) == 0, "1:100 不应画玻璃材料符号，实际 " + ShortDiagonalCount(at100));
        Assert(ShortDiagonalCount(at50) == 3, "1:50 应有 3 条玻璃材料符号线，实际 " + ShortDiagonalCount(at50));
        // 两种比例都要有外框内边与开启线
        Assert(HasLine(at100, 800d - u, 950d - z, 2200d - u, 950d - z), "1:100 缺少外框内边");
        Assert(HasLine(at100, 800d - u, 950d - z, 2200d - u, 1800d - z), "1:100 缺少开启线");
        Assert(HasLine(at50, 820d - u50, 970d - z50, 2180d - u50, 1800d - z50), "1:50 缺少开启线");
        Console.WriteLine("   详简之分：1:100 " + count100 + " 条（无安装缝/门扇内框/材料符号）/ 1:50 " + count50 + " 条（全画）");
    }

    // ───────────────────────── 6. 参数不合法时的兜底 ─────────────────────────

    /// <summary>分格参数不合法（超出洞口）时，视图必须照常出来：退回单格 + 一条提示。</summary>
    private static void FallsBackWhenParametersInvalid()
    {
        var model = SingleWindowModel("C1500X");
        var type = Type("C1500X", "自定义", "未设置");
        type.CustomCellLayout = "0,0,9999,9999,固定,0,0,玻璃";
        var view = Project(model, ElevationDirection.South, Library(type));

        Assert(view.Lines.Count(l => l.Layer == ViewLayers.Opening) == 4, "参数不合法时应退回洞口 4 条边");
        Assert(view.Warnings.Any(w => w.IndexOf("分格参数无效", StringComparison.Ordinal) >= 0),
            "参数不合法时应给出提示，实际提示：" + string.Join(" / ", view.Warnings.ToArray()));
        Console.WriteLine("   参数兜底：非法分格 → 洞口 4 条边 + 提示「分格参数无效」");
    }

    // ───────────────────────── 脚手架 ─────────────────────────

    /// <summary>一道 3000 长的南墙 + 中间一樘 1500×1800@900 的窗（轴线无偏）。</summary>
    private static BuildingModelDocument SingleWindowModel(string code)
    {
        var model = SampleModelFactory.CreateEmptyModel("门窗分格测试");
        var storey = model.Storeys[0].Id;
        model.Walls.Add(new WallModel { Id = "w-s", StoreyId = storey, X1 = 0d, Y1 = 0d, X2 = 3000d, Y2 = 0d, Thickness = 200d });
        model.Openings.Add(new OpeningModel
        {
            Id = "o-1", HostWallId = "w-s", Code = code, Kind = "窗",
            Offset = 1500d, Width = 1500d, Height = 1800d, Sill = 900d
        });
        return model;
    }

    /// <summary>在更近的一侧（南）加一道墙，从 x1 到 x2（用于测试遮挡）。</summary>
    private static BuildingModelDocument WithFrontWall(BuildingModelDocument model, double x1, double x2)
    {
        var storey = model.Storeys[0].Id;
        model.Walls.Add(new WallModel { Id = "w-front", StoreyId = storey, X1 = x1, Y1 = -1000d, X2 = x2, Y2 = -1000d, Thickness = 200d });
        return model;
    }

    private static OpeningTypeModel Type(string code, string division, string openingMode)
    {
        return new OpeningTypeModel
        {
            Code = code, Kind = "窗", Width = 1500d, Height = 1800d, Sill = 900d,
            ElevationType = "普通窗", DivisionPreset = division, OpeningMode = openingMode,
            HasOuterFrame = true, OuterFrameWidth = 50d,
            HasMullion = true, MullionWidth = 50d,
            HasInstallationGap = false, InstallationGap = 0d,
            Material = "铝合金"
        };
    }

    private static OpeningTypeLibraryDocument Library(params OpeningTypeModel[] types)
    {
        var library = new OpeningTypeLibraryDocument { ProjectName = "测试" };
        library.Types.AddRange(types);
        return library;
    }

    private static ViewDocument Project(BuildingModelDocument model, ElevationDirection direction,
        OpeningTypeLibraryDocument library, int scale = 100)
    {
        return OrthographicProjector.Project(model, new ViewDefinitionModel
        {
            Id = "test", Title = "测试立面", Kind = ViewKind.Elevation, Scale = scale, Direction = direction
        }, library);
    }

    /// <summary>数"短斜线"（玻璃材料符号）：斜的、而且长度不到 400mm。</summary>
    private static int ShortDiagonalCount(ViewDocument view)
    {
        var count = 0;
        foreach (var line in view.Lines)
        {
            if (!string.Equals(line.Layer, ViewLayers.Opening, StringComparison.Ordinal)) continue;
            var dx = line.X2 - line.X1;
            var dy = line.Y2 - line.Y1;
            if (Math.Abs(dx) < 1d || Math.Abs(dy) < 1d) continue;
            if (Math.Sqrt(dx * dx + dy * dy) < 400d) count++;
        }
        return count;
    }

    private static bool HasLine(ViewDocument view, double x1, double y1, double x2, double y2)
    {
        foreach (var line in view.Lines)
        {
            if (!string.Equals(line.Layer, ViewLayers.Opening, StringComparison.Ordinal)) continue;
            var forward = Math.Abs(line.X1 - x1) < Tolerance && Math.Abs(line.Y1 - y1) < Tolerance
                && Math.Abs(line.X2 - x2) < Tolerance && Math.Abs(line.Y2 - y2) < Tolerance;
            var backward = Math.Abs(line.X1 - x2) < Tolerance && Math.Abs(line.Y1 - y2) < Tolerance
                && Math.Abs(line.X2 - x1) < Tolerance && Math.Abs(line.Y2 - y1) < Tolerance;
            if (forward || backward) return true;
        }
        return false;
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
