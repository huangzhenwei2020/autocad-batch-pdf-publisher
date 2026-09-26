using System;
using System.Collections.Generic;
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
        Console.WriteLine("PASS 立面门窗：分格与开启线 / 背立面镜像 / 遮挡裁剪 / 自定义分格 / 1:100-1:50 详简 / 参数兜底");
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
