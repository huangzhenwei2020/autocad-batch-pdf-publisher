using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    internal static class DoorWindowElevationInsertionService
    {
        /// <summary>
        /// 门窗/幕墙设计说明（勾选“同时插入门窗设计说明”时排在第一页门窗表下方）。
        /// 来源为图纸说明原文，已清理 Word 格式残留（^U2^U 上标、全角句点、^C52^C 等）。
        /// </summary>
        private static readonly string[] ScheduleNotes =
        {
            "1、门窗类型、规格、用料在门窗表中表述，门窗生产厂家由甲方、施工方、设计方共同认可，厂家负责提供安装详图，并配套提供五金配件。所有门窗周边按有关规定留预埋件，用于固定门窗，预埋件位置视产品而定，但每边不得少于二个，推拉窗应有防止窗扇脱落的有效装置。门窗规格应符合国家现行产品标准的质量要求；门窗安装应满足其强度、热工、声学及安全等要求；门窗玻璃应符合《建筑玻璃应用技术规程》(JGJ 113-2015)、《建筑安全玻璃管理规定》发改运行[2003]2116号及地方主管部门的相关规定的要求。",
            "2、外门窗的气密性等级为6级，水密性等级为3级。门窗玻璃厚度、框料尺寸由厂家根据门窗尺寸、高度经风压计算后确定。",
            "3、本图中门窗大样仅提供门窗立面形式要求，拼樘料规格及预埋件连接件，门窗加工尺寸要按照装修面厚度由承包商予以调整，制作门窗前应重新核对门窗洞口尺寸，特殊门窗应参照相关行业规范进行设计和安装、施工。安装铝合金门窗（不含玻璃幕墙）采用预留洞口的办法，洞口每边预留安装间隙20。",
            "4、本图中门窗大样仅为分格及开启方式示意，门窗开启线表示方法：实线表示外开，虚线表示内开，箭头表示推拉门窗，无线表示固定窗。",
            "5、疏散用的平开防火门应设闭门器，双扇平开防火门应安装闭门器和顺序器，常开防火门须安装信号控制关闭和反馈装置。",
            "6、首层外门窗均由建设单位根据管理的具体情况做符合公安部门要求的防盗措施。",
            "7、所有外窗框均为深灰色喷涂铝合金窗框。",
            "8、门窗立樘（不含玻璃幕墙）如无特别说明，外门窗立樘居墙中安装；内门窗立樘除图中另有注明外，双向平开门立樘墙中，单向平开门立樘开启方向与墙面平。",
            "9、本工程窗的距地高度均从楼地面起算；临空的窗台低于900时，应采取防护措施，楼梯间护窗栏杆及其扶手做法参中南标图集11ZJ401 2B/34、4/37。",
            "10、建筑的出入口、门厅、阳台门等部位的玻璃门以及单扇面积大于1.5m²的窗玻璃，应采用钢化安全玻璃，钢化玻璃的选用应符合《建筑玻璃应用技术规程》(JGJ 113-2015)的相关规定。",
            "11、一层建筑入口处供轮椅通行的门扇，安装视线观察玻璃、横执把手和关门拉手，在门扇的下方应安装高0.35m的护门板，如右侧大样图所示。",
            "12、本工程所有玻璃门应设置防撞提示标志。",
            "13、图中玻璃幕墙分格样式仅供参考，本工程玻璃幕墙工程具体由建设单位另行委托专业幕墙公司设计及施工；幕墙设计单位负责幕墙具体设计，并向建筑设计单位提供预埋件的设置要求。",
            "14、玻璃幕墙的设计、制作和安装应执行《建筑玻璃应用技术规程》(JGJ 113-2015)、《玻璃幕墙工程技术规范》(JGJ102-2003)、《建筑设计防火规范》(GB 50016-2014)2018年版、《建筑安全玻璃管理规定》发改运行[2003]2116号等相关规范规定及地方主管部门的相关规定。",
            "15、本工程玻璃幕墙的玻璃采用水晶灰钢化夹胶安全玻璃，所有上悬窗开启角度不小于70°。",
            "16、幕墙层间防火节点做法参03J103-2第37页大样52。跨层幕墙与每层楼板、隔墙处的缝隙应采用防火封堵材料封堵，窗槛墙、窗间墙的填充材料应采用不燃材料。",
            "17、采用6+0.76+6系列铝合金钢化夹胶玻璃幕墙，部分隐框，明框、隐框具体部位详相应大样图。",
            "18、幕墙大样图的开启位置仅为示意，二次设计时，应保证各功能间的可开启面积满足自然采光通风要求。"
        };

        /// <summary>估算设计说明文字排版高度（模型单位）：按内容区宽度折行，字高 3.5mm、行距 1.5。</summary>
        private static double EstimateScheduleNotesHeight(double contentWidth, int scale)
        {
            var charWidth = 3.5d * scale;
            var perLine = Math.Max(1, (int)Math.Floor(contentWidth / charWidth));
            var totalLines = 0;
            foreach (var note in ScheduleNotes)
                totalLines += (int)Math.Ceiling((double)Math.Max(1, note.Length) / perLine);
            return totalLines * charWidth * 1.5d;
        }
        private sealed class ElevationPlacement
        {
            public DoorWindowScheduleItem Item;
            public double InnerGap, OuterGap, LowerExtent, UpperExtent, BayLeftExtent, BayRightExtent;
            public double TotalWidth { get { return BayLeftExtent + Item.Width + BayRightExtent; } }
        }

        /// <summary>排版参数（纸面毫米，插入时按出图比例换算）。</summary>
        public sealed class DoorWindowLayoutOptions
        {
            public double LeftMargin = 20d;
            public double RightMargin = 20d;
            public double TopMargin = 15d;
            public double BottomMargin = 20d;
            public double PageGap = 30d;
            public double ItemGap = 12d;
            /// <summary>横向排版时标题栏占用宽度（纸面毫米），0 表示不预留。</summary>
            public double TitleBlockWidth = 0d;
            /// <summary>是否插入独立门窗表；不占用图框排版范围。</summary>
            public bool IncludeSchedule = false;
            /// <summary>是否插入独立门窗设计说明；不占用图框排版范围。</summary>
            public bool IncludeScheduleNotes = false;
            public Point3d? SchedulePosition;
            public Point3d? NotesPosition;
            /// <summary>图名是否使用天正图名标注样式（图名+下划线+比例）；false 时用普通两行文字。</summary>
            public bool UseTianzhengTitle = true;
        }

        /// <summary>单个门窗在排版计划中的槽位（相对该页原点，模型单位）。</summary>
        public sealed class DoorWindowLayoutSlot
        {
            public DoorWindowScheduleItem Item;
            public int Page;
            public double X, Y;
            public double FootprintWidth, FootprintHeight;
        }

        /// <summary>整页排版计划：分页、每页纸张尺寸与内容区、每个门窗的槽位。</summary>
        public sealed class DoorWindowLayoutPlan
        {
            public double PageWidth, PageHeight;
            public double ContentLeft, ContentRight, ContentBottom, ContentTop;
            public int PageCount;
            public readonly List<DoorWindowLayoutSlot> Slots = new List<DoorWindowLayoutSlot>();
            /// <summary>第一页顶部门窗表占用高度（模型单位），0 表示不插入门窗表。</summary>
            public double ScheduleHeight;
            /// <summary>第一页门窗表下方设计说明占用高度（模型单位），0 表示不插入说明。</summary>
            public double ScheduleNotesHeight;
        }

        /// <summary>按登记图框纸张与排版参数分页计算每个门窗的槽位。占位含标注外框。</summary>
        public static DoorWindowLayoutPlan ComputeLayout(IList<DoorWindowScheduleItem> source, int drawingScale, FrameDefinition frame, DoorWindowLayoutOptions options)
        {
            if (frame == null || string.IsNullOrWhiteSpace(frame.BlockName)) throw new InvalidOperationException("请选择有效的登记图框。");
            var scale = Math.Max(1, drawingScale);
            var paper = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, string.IsNullOrWhiteSpace(frame.PaperOrientation) ? "横向" : frame.PaperOrientation);
            if (paper == null || paper.Length < 2 || double.IsNaN(paper[0]) || double.IsInfinity(paper[0]) || double.IsNaN(paper[1]) || double.IsInfinity(paper[1]) || paper[0] <= 0d || paper[1] <= 0d)
                throw new InvalidOperationException("图框“" + frame.PaperDisplay + "”的纸张尺寸无效。");
            var pageWidth = paper[0] * scale; var pageHeight = paper[1] * scale;
            var opts = options ?? new DoorWindowLayoutOptions();
            var leftMargin = Math.Max(0d, opts.LeftMargin) * scale;
            var rightMargin = (Math.Max(0d, opts.RightMargin) + Math.Max(0d, opts.TitleBlockWidth)) * scale;
            var bottomMargin = Math.Max(0d, opts.BottomMargin) * scale;
            var topMargin = Math.Max(0d, opts.TopMargin) * scale;
            var contentLeft = leftMargin; var contentRight = pageWidth - rightMargin; var contentBottom = bottomMargin; var contentTop = pageHeight - topMargin;
            if (contentRight <= contentLeft || contentTop <= contentBottom) throw new InvalidOperationException("登记图框的可排版区域无效。");
            var plan = new DoorWindowLayoutPlan { PageWidth = pageWidth, PageHeight = pageHeight, ContentLeft = contentLeft, ContentRight = contentRight, ContentBottom = contentBottom, ContentTop = contentTop };
            var items = DoorWindowTypeOrdering.Sort(source);
            var itemGap = Math.Max(0d, opts.ItemGap) * scale;
            // 门窗表和设计说明是独立、可移动的 CAD 对象，不参与图框内的门窗排版占位。
            plan.ScheduleHeight = 0d;
            plan.ScheduleNotesHeight = 0d;
            // 顺序：先左右后上下——第一行在顶部，同一行从左到右排满后换到下一行。
            // slot.X/slot.Y 统一为立面插入原点（左下角，相对页原点，模型单位）。
            // 每页独立游标：锁定项先放入指定页，未锁定项再按顺序流式填充（自动跳过已被占用的页/行）。
            var pageCursors = new Dictionary<int, PageCursor>();
            var maxPage = 0;
            PageCursor CursorFor(int page)
            {
                PageCursor cursor;
                if (!pageCursors.TryGetValue(page, out cursor)) { cursor = new PageCursor { X = contentLeft, Y = contentTop }; pageCursors[page] = cursor; }
                return cursor;
            }
            // 放置单个门窗。锁定项放不下指定页时抛错；未锁定项返回 false 表示该页放不下需换页。
            Func<DoorWindowScheduleItem, int, bool, bool> place = (item, page, locked) =>
            {
                if (double.IsNaN(item.Width) || double.IsInfinity(item.Width) || double.IsNaN(item.Height) || double.IsInfinity(item.Height) || item.Width <= 0d || item.Height <= 0d)
                    throw new InvalidOperationException("门窗“" + (item.Code ?? "未编号") + "”的洞口尺寸无效，请重新读取门窗表。");
                var placement = CreatePlacement(item, drawingScale);
                var footprintWidth = placement.OuterGap * 2d + placement.TotalWidth + itemGap;
                var footprintHeight = placement.UpperExtent + placement.LowerExtent + item.Height + itemGap;
                // 任何一页都放不下（含未锁定项换页到后续页）才报错；锁定项在指定页放不下由下方页内检查专门提示。
                if (footprintWidth > contentRight - contentLeft || footprintHeight > contentTop - contentBottom)
                    throw new InvalidOperationException("门窗“" + item.Code + "”在 1:" + scale + " 时放不进所选 " + frame.PaperDisplay + " 图框，请选择更大或加长图框。");
                var cursor = CursorFor(page);
                var bottomLimit = contentBottom;
                if (cursor.X + footprintWidth > contentRight)
                {
                    cursor.X = contentLeft; cursor.Y -= cursor.RowHeight + itemGap; cursor.RowHeight = 0d;
                }
                if (cursor.Y - footprintHeight < bottomLimit)
                {
                    if (locked) throw new InvalidOperationException("门窗“" + item.Code + "”锁定在第 " + (page + 1) + " 页，但该页剩余空间放不下。请解锁该门窗或调整顺序。");
                    return false;
                }
                plan.Slots.Add(new DoorWindowLayoutSlot { Item = item, Page = page, X = cursor.X + placement.OuterGap + placement.BayLeftExtent, Y = cursor.Y - placement.UpperExtent - item.Height, FootprintWidth = footprintWidth, FootprintHeight = footprintHeight });
                cursor.X += footprintWidth + itemGap; cursor.RowHeight = Math.Max(cursor.RowHeight, footprintHeight);
                if (page > maxPage) maxPage = page;
                return true;
            };
            // 先放锁定项：按锁定页升序，页内保持原顺序。
            foreach (var group in items.Where(x => x.LockedPage > 0).OrderBy(x => x.LockedPage).GroupBy(x => x.LockedPage))
            {
                var page = Math.Max(0, group.Key - 1);
                foreach (var item in group) place(item, page, true);
            }
            // 再放未锁定项：从第一页开始流式排，放不下自动换页。
            var currentPage = 0;
            foreach (var item in items.Where(x => x.LockedPage <= 0))
                while (!place(item, currentPage, false)) currentPage++;
            plan.PageCount = maxPage + 1;
            return plan;
        }

        /// <summary>排版游标：记录某页当前排到的位置（先左右后上下）。</summary>
        private sealed class PageCursor
        {
            public double X, Y, RowHeight;
        }

        /// <summary>
        /// 在候选图框中自动选择最小方案：先比页数（少者优先），页数相同再比纸张面积（小者优先）。
        /// 放不下所选门窗（ComputeLayout 抛错）或纸张无效的图框自动跳过。返回 null 表示无可用图框。
        /// </summary>
        public static FrameDefinition PickSmallestFrame(IList<DoorWindowScheduleItem> source, int drawingScale, IEnumerable<FrameDefinition> candidates, DoorWindowLayoutOptions options)
        {
            if (candidates == null) return null;
            var scale = Math.Max(1, drawingScale);
            var opts = options ?? new DoorWindowLayoutOptions();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            FrameDefinition best = null; var bestPages = int.MaxValue; var bestArea = double.MaxValue;
            foreach (var frame in candidates)
            {
                if (frame == null || string.IsNullOrWhiteSpace(frame.BlockName)) continue;
                var key = (frame.PaperSize ?? string.Empty) + "|" + (frame.Extension ?? string.Empty) + "|" + (frame.PaperOrientation ?? string.Empty);
                if (!seen.Add(key)) continue; // 同纸张同方向的多个块只比一次
                double area;
                try
                {
                    var paper = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, string.IsNullOrWhiteSpace(frame.PaperOrientation) ? "横向" : frame.PaperOrientation);
                    if (paper == null || paper.Length < 2 || paper[0] <= 0d || paper[1] <= 0d) continue;
                    area = paper[0] * paper[1];
                }
                catch { continue; }
                DoorWindowLayoutPlan plan;
                try { plan = ComputeLayout(source, scale, frame, opts); }
                catch { continue; } // 放不下该图框，跳过
                if (plan.PageCount < bestPages || (plan.PageCount == bestPages && area < bestArea))
                { best = frame; bestPages = plan.PageCount; bestArea = area; }
            }
            return best;
        }

        public static int Insert(Document document, IList<DoorWindowScheduleItem> source, int drawingScale, FrameDefinition frame, Action<int, int, string> progress = null, DoorWindowLayoutOptions layoutOptions = null)
        {
            if (document == null) throw new ArgumentNullException("document");
            var items = DoorWindowTypeOrdering.Sort((source ?? new List<DoorWindowScheduleItem>()).Where(x => x.Selected && (x.Status ?? string.Empty).Contains("可生成")));
            if (items.Count == 0) throw new InvalidOperationException("没有勾选参数完整的门窗。");
            drawingScale = Math.Max(1, drawingScale);
            var useTianzhengTitle = layoutOptions == null || layoutOptions.UseTianzhengTitle;
            if (progress != null) progress(0, items.Count, "等待指定插入点…");
            if (frame == null)
            {
                var pointResult = document.Editor.GetPoint("\n指定批量门窗立面左下角插入点: ");
                if (pointResult.Status != PromptStatus.OK) return 0;
                // 无图框：连续排列。
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    var database = document.Database;
                    DoorWindowElevationMetadataService.EnsureRegistered(database, transaction);
                    var profile = DraftingStandardService.LoadProfile();
                    var resources = DraftingStandardService.EnsureAll(database, transaction, profile, profile.UpdateExisting);
                    var dimensionStyle = DraftingStandardService.EnsureDimensionStyleForScale(database, transaction, drawingScale, profile, resources, true);
                    var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                    var elevations = items.Select(item => CreatePlacement(item, drawingScale)).ToList();
                    InsertContinuous(elevations, pointResult.Value, drawingScale, space, transaction, resources, dimensionStyle, progress, useTianzhengTitle);
                    transaction.Commit();
                }
                document.Editor.Regen();
                document.Editor.WriteMessage("\n已插入 " + items.Count + " 个可独立编辑的门窗立面。几何按实际毫米 1:1 绘制，标注采用万落建筑工具 1:" + drawingScale + " 标注样式。\n");
                return items.Count;
            }

            // 有图框：指定第一张图框左下角插入点，门窗按图框纸张分页排版并插入图框块。
            var framePoint = document.Editor.GetPoint("\n指定第一张门窗立面图框左下角插入点: ");
            if (framePoint.Status != PromptStatus.OK) return 0;
            var paper = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, string.IsNullOrWhiteSpace(frame.PaperOrientation) ? "横向" : frame.PaperOrientation);
            var pageWidth = paper[0] * drawingScale;
            var layoutPageCount = ComputeLayout(items, drawingScale, frame, layoutOptions).PageCount;
            var pageGap = Math.Max(20d, layoutOptions == null ? 30d : layoutOptions.PageGap) * drawingScale;
            var defaultAccessoryX = framePoint.Value.X + Math.Max(1, layoutPageCount) * (pageWidth + pageGap);
            if (layoutOptions != null && layoutOptions.IncludeSchedule)
            {
                var schedulePoint = document.Editor.GetPoint(new PromptPointOptions("\n指定独立门窗表左上角插入点（回车放在图框右侧）: ") { AllowNone = true });
                layoutOptions.SchedulePosition = schedulePoint.Status == PromptStatus.OK ? schedulePoint.Value : new Point3d(defaultAccessoryX, framePoint.Value.Y + 200d * drawingScale, framePoint.Value.Z);
            }
            if (layoutOptions != null && layoutOptions.IncludeScheduleNotes)
            {
                var notesPoint = document.Editor.GetPoint(new PromptPointOptions("\n指定独立门窗设计说明左上角插入点（回车放在图框右侧）: ") { AllowNone = true });
                layoutOptions.NotesPosition = notesPoint.Status == PromptStatus.OK ? notesPoint.Value : new Point3d(defaultAccessoryX, framePoint.Value.Y, framePoint.Value.Z);
            }
            var pageCount = 0;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var database = document.Database;
                DoorWindowElevationMetadataService.EnsureRegistered(database, transaction);
                var profile = DraftingStandardService.LoadProfile();
                var resources = DraftingStandardService.EnsureAll(database, transaction, profile, profile.UpdateExisting);
                var dimensionStyle = DraftingStandardService.EnsureDimensionStyleForScale(database, transaction, drawingScale, profile, resources, true);
                var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForWrite);
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                var elevations = items.Select(item => CreatePlacement(item, drawingScale)).ToList();
                pageCount = InsertPaged(elevations, framePoint.Value, drawingScale, frame, blocks, space, transaction, resources, dimensionStyle, progress, layoutOptions);
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\n已插入 " + items.Count + " 个可独立编辑的门窗立面，自动排入 " + pageCount + " 张 " + frame.PaperDisplay + " 图框。几何按实际毫米 1:1 绘制，标注采用万落建筑工具 1:" + drawingScale + " 标注样式。\n");
            return items.Count;
        }

        private sealed class DoorWindowLayers { public ObjectId Window, Door, OpeningHole; }

        public static int InsertSchedule(Document document, IList<DoorWindowScheduleItem> source, int drawingScale)
        {
            if (document == null) throw new ArgumentNullException("document");
            var items = DoorWindowTypeOrdering.Sort((source ?? new List<DoorWindowScheduleItem>()).Where(x => x.Selected));
            if (items.Count == 0) throw new InvalidOperationException("没有勾选要写入门窗表的数据。 ");
            var point = document.Editor.GetPoint("\n指定门窗表左上角插入点: ");
            if (point.Status != PromptStatus.OK) return 0;
            var scale = Math.Max(1, drawingScale);
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var table = BuildScheduleTable(document.Database, items, scale, point.Value);
                var space = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
                space.AppendEntity(table); transaction.AddNewlyCreatedDBObject(table, true); table.GenerateLayout(); transaction.Commit();
            }
            document.Editor.Regen(); return items.Count;
        }

        /// <summary>构建紧凑门窗表 Table（未加入图形）。Position 为表格左上角；插入后可直接 MOVE。</summary>
        private static Table BuildScheduleTable(Database database, IList<DoorWindowScheduleItem> items, int scale, Point3d position)
        {
            var floorNames = items.SelectMany(x => x.FloorQuantities).Select(x => x.FloorName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            var floorMode = floorNames.Count > 0;
            var columnCount = 8 + floorNames.Count;
            var table = new Table { TableStyle = database.Tablestyle, Position = position };
            table.SetSize(items.Count + 2, columnCount);
            var rowHeight = 5.5d * scale;
            table.SetRowHeight(rowHeight);
            // 按实际内容估算列宽，同时设置上下限，避免短内容浪费空间、长内容把表格撑满图纸。
            var widths = new List<double>
            {
                8d,
                ContentWidth(items.Select(x => x.ElevationType), 16d, 25d),
                ContentWidth(items.Select(x => x.Code), 15d, 25d),
                ContentWidth(items.Select(x => x.SizeText), 22d, 30d),
                16d
            };
            foreach (var floorName in floorNames) widths.Add(Math.Max(13d, Math.Min(24d, 5d + floorName.Length * 2.2d)));
            widths.Add(floorMode ? 14d : 10d);
            widths.Add(ContentWidth(items.Select(x => string.IsNullOrWhiteSpace(x.AtlasName) ? DoorWindowElevationSuggestionService.InferAtlas(x.Code, x.ElevationType, x.SourceNote) : x.AtlasName), 30d, 42d));
            widths.Add(ContentWidth(items.Select(x => x.Remarks ?? x.SourceNote), 20d, 34d));
            var total = widths.Sum();
            var maximumPaperWidth = floorMode ? Math.Max(165d, 145d + floorNames.Count * 15d) : 165d;
            if (total > maximumPaperWidth)
            {
                var fixedWidth = widths[0] + widths[4] + widths.Skip(5).Take(floorNames.Count + 1).Sum();
                var factor = (maximumPaperWidth - fixedWidth) / Math.Max(1d, total - fixedWidth);
                foreach (var column in new[] { 1, 2, 3, columnCount - 2, columnCount - 1 }) widths[column] *= factor;
            }
            for (var column = 0; column < widths.Count; column++) table.Columns[column].Width = widths[column] * scale;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columnCount - 1)); table.Cells[0, 0].TextString = "门窗表";
            var headers = new List<string> { "序号", "类型", "编号", "洞口尺寸", "离地高度" };
            headers.AddRange(floorNames); headers.Add(floorMode ? "总数量" : "数量"); headers.Add("图集名称"); headers.Add("备注");
            for (var column = 0; column < headers.Count; column++) table.Cells[1, column].TextString = headers[column];
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index]; var row = index + 2;
                table.Cells[row, 0].TextString = (index + 1).ToString(CultureInfo.InvariantCulture);
                table.Cells[row, 1].TextString = item.ElevationType ?? string.Empty;
                table.Cells[row, 2].TextString = item.Code ?? string.Empty;
                table.Cells[row, 3].TextString = item.SizeText;
                table.Cells[row, 4].TextString = item.SillHeightSuppressed || !(item.ElevationType ?? string.Empty).Contains("窗") ? "—" : item.SillHeight.ToString("0.##", CultureInfo.InvariantCulture);
                var column = 5;
                foreach (var floorName in floorNames)
                {
                    var quantity = item.FloorQuantities.FirstOrDefault(x => string.Equals(x.FloorName, floorName, StringComparison.CurrentCultureIgnoreCase));
                    table.Cells[row, column++].TextString = quantity == null || quantity.PerFloorQuantity <= 0 ? "—" : quantity.DisplayText;
                }
                table.Cells[row, column++].TextString = (floorMode ? Math.Max(0, item.Quantity) : Math.Max(1, item.Quantity)).ToString(CultureInfo.InvariantCulture);
                table.Cells[row, column++].TextString = string.IsNullOrWhiteSpace(item.AtlasName) ? DoorWindowElevationSuggestionService.InferAtlas(item.Code, item.ElevationType, item.SourceNote) : DoorWindowElevationSuggestionService.NormalizeAtlasName(item.AtlasName);
                table.Cells[row, column].TextString = item.Remarks ?? item.SourceNote ?? string.Empty;
            }
            for (var row = 0; row < items.Count + 2; row++) for (var column = 0; column < columnCount; column++)
            { table.Cells[row, column].TextHeight = 2.2d * scale; table.Cells[row, column].Alignment = CellAlignment.MiddleCenter; }
            table.Rows[0].Height = 6.5d * scale;
            table.Rows[1].Height = 6d * scale;
            return table;
        }

        private static double ContentWidth(IEnumerable<string> values, double minimum, double maximum)
        {
            var longest = (values ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().Length).DefaultIfEmpty(0).Max();
            return Math.Max(minimum, Math.Min(maximum, 4d + longest * 2.15d));
        }

        public static int Update(Document document, DoorWindowElevationMetadata metadata, IList<DoorWindowScheduleItem> source, int drawingScale)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (metadata == null) throw new ArgumentNullException("metadata");
            var item = (source ?? new List<DoorWindowScheduleItem>()).FirstOrDefault(x =>
                string.Equals(x.Code, metadata.Code, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(x.Width - metadata.Width) < 0.01 && Math.Abs(x.Height - metadata.Height) < 0.01) ?? metadata.ToItem();
            DoorWindowElevationGeometryBuilder.Build(item);
            drawingScale = Math.Max(1, drawingScale);
            var replaced = 0;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var database = document.Database;
                DoorWindowElevationMetadataService.EnsureRegistered(database, transaction);
                var profile = DraftingStandardService.LoadProfile();
                var resources = DraftingStandardService.EnsureAll(database, transaction, profile, profile.UpdateExisting);
                var dimensionStyle = DraftingStandardService.EnsureDimensionStyleForScale(database, transaction, drawingScale, profile, resources, true);
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                var ids = DoorWindowElevationMetadataService.FindGroup(space, transaction, metadata.GroupId);
                foreach (var id in ids)
                {
                    var entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (entity == null) continue;
                    entity.Erase(); replaced++;
                }
                InsertElevation(CreatePlacement(item, drawingScale), metadata.Origin, drawingScale, space, transaction, resources, dimensionStyle, metadata.GroupId);
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\n门窗立面“" + (item.Code ?? "未编号") + "”已原位更新，替换 " + replaced + " 个插件生成对象。\n");
            return replaced;
        }

        private static ElevationPlacement CreatePlacement(DoorWindowScheduleItem item, int drawingScale)
        {
            var geometry = DoorWindowElevationGeometryBuilder.Build(item);
            // 尺寸线保持原有纸面距离：内层 4 mm、外层 8 mm。
            var innerGap = 4d * drawingScale;
            var outerGap = 8d * drawingScale;
            var upperExtent = geometry.BayLeftExtent > 0d || geometry.BayRightExtent > 0d ? 11d * drawingScale : 0d;
            return new ElevationPlacement { Item = item, InnerGap = innerGap, OuterGap = outerGap, LowerExtent = outerGap + 18d * drawingScale, UpperExtent = upperExtent, BayLeftExtent = geometry.BayLeftExtent, BayRightExtent = geometry.BayRightExtent };
        }

        private static void InsertElevation(ElevationPlacement elevation, Point3d origin, int drawingScale, BlockTableRecord space, Transaction transaction, DraftingStandardResources resources, ObjectId dimensionStyle, string existingGroupId = null, bool useTianzhengTitle = true)
        {
            var item = elevation.Item; var geometry = DoorWindowElevationGeometryBuilder.Build(item);
            var metadata = DoorWindowElevationMetadata.Create(item, origin, drawingScale, existingGroupId);
            AppendGeometry(geometry, origin, space, transaction, resources, metadata, item);
            var innerGap = elevation.InnerGap; var outerGap = elevation.OuterGap;
            // 内层：安装缝与全部分段连成一条连续标注直线；外层：总宽/总高。
            AddLayerDimensions(geometry, origin, innerGap, outerGap, space, transaction, resources.AnnotationDimensionLayerId, dimensionStyle, metadata, item);
            AddBayFoldLeaders(geometry, origin, drawingScale, space, transaction, resources, metadata);
            var overallLeft = origin.X - geometry.BayLeftExtent; var overallRight = origin.X + item.Width + geometry.BayRightExtent;
            // 只下移图名，尺寸线位置不变，避免图名与最外层尺寸文字打架。
            var titleCenter = new Point3d((overallLeft + overallRight) / 2d, origin.Y - outerGap - 11d * drawingScale, origin.Z);
            // 图名只写编号（不附加" 立面"）：与天正图名标注模板（如 MLC7427 / 1:50）一致。
            var titleText = item.Code ?? "未编号";
            if (useTianzhengTitle)
            {
                // 天正图名标注：优先克隆图纸中已有的 TCH_DRAWINGNAME 模板并写入文字/比例
                // （更新比例功能已验证的 COM 写法），无模板或写入失败时回退"天正图名样式"。
                var insertedNative = TianzhengTitleService.TryInsertNativeTitle(space.Database, space, transaction, titleCenter, titleText, drawingScale, overallRight - overallLeft, metadata);
                if (!insertedNative)
                {
                    TianzhengTitleService.InsertTitle(space, transaction, titleCenter, titleText, drawingScale, resources.TitleTextStyleId, resources.AnnotationTextStyleId, resources.AnnotationTextLayerId, metadata);
                }
            }
            else
            {
                // 普通图名：两行文字（图名 + 比例）。
                var titleHeight = Math.Max(3.5d * drawingScale, 70d); var noteHeight = Math.Max(2.5d * drawingScale, 50d);
                AddCenteredText(space, transaction, titleText, titleCenter, titleHeight, resources.TitleTextStyleId, resources.AnnotationTextLayerId, metadata);
                AddCenteredText(space, transaction, "1:" + drawingScale.ToString(CultureInfo.InvariantCulture), new Point3d(titleCenter.X, titleCenter.Y - 3.6d * drawingScale, titleCenter.Z), noteHeight, resources.AnnotationTextStyleId, resources.AnnotationTextLayerId, metadata);
            }
        }

        private static void AppendGeometry(DoorWindowElevationGeometry geometry, Point3d origin, BlockTableRecord space, Transaction transaction, DraftingStandardResources resources, DoorWindowElevationMetadata metadata, DoorWindowScheduleItem item)
        {
            const double tolerance = .01d;
            var doorWindowLayers = EnsureDoorWindowLayers(space.Database, transaction);
            var mainLayer = (item.ElevationType ?? string.Empty).Contains("窗") ? doorWindowLayers.Window : doorWindowLayers.Door;
            foreach (var roleGroup in geometry.Lines.GroupBy(x => x.Role))
            {
                var segments = roleGroup.ToList(); var used = new bool[segments.Count];
                // 按端点哈希索引，把"找下一条相连线段"从 O(n) 扫描降为 O(1) 查找，
                // 避免复杂门窗（上千线段）时 O(n³) 级卡顿。
                var byStart = new Dictionary<string, List<int>>();
                var byEnd = new Dictionary<string, List<int>>();
                for (var index = 0; index < segments.Count; index++)
                {
                    var segment = segments[index];
                    AddIndex(byStart, Key(segment.X1, segment.Y1), index);
                    AddIndex(byEnd, Key(segment.X2, segment.Y2), index);
                }
                for (var seed = 0; seed < segments.Count; seed++)
                {
                    if (used[seed]) continue; used[seed] = true;
                    // head/tail 分开收集，避免头插 O(n) 导致二次卡顿；完成后合并。
                    var head = new List<Point2d> { new Point2d(segments[seed].X1, segments[seed].Y1) };
                    var tail = new List<Point2d> { new Point2d(segments[seed].X2, segments[seed].Y2) };
                    bool extended;
                    do
                    {
                        extended = false;
                        var tailPoint = tail[tail.Count - 1];
                        var next = FindUnused(byStart, byEnd, used, segments, tailPoint);
                        if (next >= 0)
                        {
                            used[next] = true;
                            var s = segments[next];
                            if (Matches(tailPoint, s.X1, s.Y1)) tail.Add(new Point2d(s.X2, s.Y2));
                            else tail.Add(new Point2d(s.X1, s.Y1));
                            extended = true;
                            continue;
                        }
                        var headPoint = head[head.Count - 1];
                        var previous = FindUnused(byStart, byEnd, used, segments, headPoint);
                        if (previous >= 0)
                        {
                            used[previous] = true;
                            var s = segments[previous];
                            if (Matches(headPoint, s.X2, s.Y2)) head.Add(new Point2d(s.X1, s.Y1));
                            else head.Add(new Point2d(s.X2, s.Y2));
                            extended = true;
                        }
                    } while (extended);
                    head.Reverse(); head.AddRange(tail);

                    var layer = roleGroup.Key == DoorWindowLineRole.Hole || roleGroup.Key == DoorWindowLineRole.Opening ? doorWindowLayers.OpeningHole : mainLayer;
                    var closed = head.Count > 2 && Near(head[0], head[head.Count - 1]);
                    if (closed) head.RemoveAt(head.Count - 1);
                    if (head.Count > 2)
                    {
                        var polyline = new Polyline(head.Count) { LayerId = layer, Closed = closed };
                        if (roleGroup.Key == DoorWindowLineRole.SashFrame) polyline.ColorIndex = 8;
                        for (var index = 0; index < head.Count; index++) polyline.AddVertexAt(index, new Point2d(origin.X + head[index].X, origin.Y + head[index].Y), 0d, 0d, 0d);
                        AppendTagged(space, transaction, polyline, metadata);
                    }
                    else if (head.Count == 2)
                    {
                        var line = new Line(new Point3d(origin.X + head[0].X, origin.Y + head[0].Y, origin.Z), new Point3d(origin.X + head[1].X, origin.Y + head[1].Y, origin.Z)) { LayerId = layer };
                        if (roleGroup.Key == DoorWindowLineRole.SashFrame) line.ColorIndex = 8;
                        AppendTagged(space, transaction, line, metadata);
                    }
                }
            }

            void AddIndex(Dictionary<string, List<int>> map, string key, int index)
            {
                List<int> list;
                if (!map.TryGetValue(key, out list)) { list = new List<int>(); map[key] = list; }
                list.Add(index);
            }
            // 键取整到容差整数倍：距离 ≤ tolerance 的端点必然落在同一个键内，
            // 保证哈希查找与 Near 判定一致。
            string Key(double x, double y) { return Bucket(x).ToString(CultureInfo.InvariantCulture) + "," + Bucket(y).ToString(CultureInfo.InvariantCulture); }
            long Bucket(double value) { return (long)Math.Floor(value / tolerance + 0.5d); }
            int FindUnused(Dictionary<string, List<int>> startMap, Dictionary<string, List<int>> endMap, bool[] usedFlags, List<DoorWindowLineSegment> all, Point2d point)
            {
                // 查 3×3 bucket 邻域，覆盖容差边界上的端点，保证与 Near 判定一致。
                var bx = Bucket(point.X); var by = Bucket(point.Y);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var key = (bx + dx).ToString(CultureInfo.InvariantCulture) + "," + (by + dy).ToString(CultureInfo.InvariantCulture);
                        List<int> candidates;
                        if (startMap.TryGetValue(key, out candidates))
                            foreach (var candidate in candidates) if (!usedFlags[candidate] && Matches(point, all[candidate].X1, all[candidate].Y1)) return candidate;
                        if (endMap.TryGetValue(key, out candidates))
                            foreach (var candidate in candidates) if (!usedFlags[candidate] && Matches(point, all[candidate].X2, all[candidate].Y2)) return candidate;
                    }
                return -1;
            }
            bool Near(Point2d first, Point2d second) { return first.GetDistanceTo(second) <= tolerance; }
            bool Matches(Point2d point, double x, double y) { return point.GetDistanceTo(new Point2d(x, y)) <= tolerance; }
        }

        private static DoorWindowLayers EnsureDoorWindowLayers(Database database, Transaction transaction)
        {
            var dash = EnsureDashLineType(database, transaction);
            return new DoorWindowLayers
            {
                Window = EnsureLayer(database, transaction, "WL-门窗-窗", 4, ObjectId.Null, LineWeight.LineWeight025),
                Door = EnsureLayer(database, transaction, "WL-门窗-门", 7, ObjectId.Null, LineWeight.LineWeight025),
                OpeningHole = EnsureLayer(database, transaction, "WL-门窗-开启洞口", 8, dash, LineWeight.LineWeight013)
            };
        }

        private static ObjectId EnsureDashLineType(Database database, Transaction transaction)
        {
            var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
            foreach (var name in new[] { "DASH", "DASHED" })
            {
                if (table.Has(name)) return table[name];
                try { database.LoadLineTypeFile(name, "acadiso.lin"); } catch { try { database.LoadLineTypeFile(name, "acad.lin"); } catch { } }
                table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
                if (table.Has(name)) return table[name];
            }
            return ObjectId.Null;
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name, short colorIndex, ObjectId lineType, LineWeight lineWeight)
        {
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead); LayerTableRecord record;
            if (table.Has(name)) record = (LayerTableRecord)transaction.GetObject(table[name], OpenMode.ForWrite);
            else { table.UpgradeOpen(); record = new LayerTableRecord { Name = name }; table.Add(record); transaction.AddNewlyCreatedDBObject(record, true); }
            record.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex); record.LineWeight = lineWeight;
            if (!lineType.IsNull) record.LinetypeObjectId = lineType;
            return record.ObjectId;
        }

        private static void InsertContinuous(IList<ElevationPlacement> elevations, Point3d origin, int scale, BlockTableRecord space, Transaction transaction, DraftingStandardResources resources, ObjectId dimensionStyle, Action<int, int, string> progress, bool useTianzhengTitle = true)
        {
            var x = origin.X; var completed = 0;
            foreach (var elevation in elevations)
            {
                InsertElevation(elevation, new Point3d(x + elevation.BayLeftExtent, origin.Y, origin.Z), scale, space, transaction, resources, dimensionStyle, null, useTianzhengTitle);
                // 连续排列按左右两侧标注外框共同占位。
                x += elevation.OuterGap * 2d + elevation.TotalWidth + Math.Max(16d * scale, 800d);
                completed++; if (progress != null) progress(completed, elevations.Count, elevation.Item.Code);
            }
        }

        /// <summary>按登记图框纸张分页排版：每页插入图框块，门窗按 ComputeLayout 槽位排入。</summary>
        private static int InsertPaged(IList<ElevationPlacement> elevations, Point3d origin, int scale, FrameDefinition frame, BlockTable blockTable, BlockTableRecord space, Transaction transaction, DraftingStandardResources resources, ObjectId dimensionStyle, Action<int, int, string> progress, DoorWindowLayoutOptions layoutOptions = null)
        {
            if (frame == null || string.IsNullOrWhiteSpace(frame.BlockName)) throw new InvalidOperationException("请选择有效的登记图框。");
            if (!blockTable.Has(frame.BlockName)) throw new InvalidOperationException("当前图纸不存在已登记图框块“" + frame.BlockName + "”。请先把该图框插入当前图纸，或重新登记当前图纸中的图框。");
            var useTianzhengTitle = layoutOptions == null || layoutOptions.UseTianzhengTitle;
            var frameDefinitionId = blockTable[frame.BlockName];
            var frameRecord = (BlockTableRecord)transaction.GetObject(frameDefinitionId, OpenMode.ForRead);
            Point3d definitionMin; double definitionWidth, definitionHeight;
            GetDefinitionBounds(frameRecord, transaction, out definitionMin, out definitionWidth, out definitionHeight);
            var paper = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, string.IsNullOrWhiteSpace(frame.PaperOrientation) ? "横向" : frame.PaperOrientation);
            var pageWidth = paper[0] * scale; var pageHeight = paper[1] * scale;
            var frameFactor = Math.Min(pageWidth / definitionWidth, pageHeight / definitionHeight);
            var pageGap = (layoutOptions == null ? 30d : Math.Max(0d, layoutOptions.PageGap)) * scale;
            var plan = ComputeLayout(elevations.Select(x => x.Item).ToList(), scale, frame, layoutOptions);
            var completed = 0;
            for (var pageIndex = 0; pageIndex < plan.PageCount; pageIndex++)
            {
                var pageOrigin = new Point3d(origin.X + pageIndex * (pageWidth + pageGap), origin.Y, origin.Z);
                AddFrameReference(space, transaction, frameRecord, frameDefinitionId, frame, pageOrigin, definitionMin, frameFactor, scale, pageIndex + 1, resources.FrameLayerId);
                foreach (var slot in plan.Slots.Where(x => x.Page == pageIndex))
                {
                    // slot.X/slot.Y 是立面插入原点（左下角，相对页左下角，模型单位）。
                    var insertion = new Point3d(pageOrigin.X + slot.X, pageOrigin.Y + slot.Y, origin.Z);
                    var elevation = elevations.First(x => ReferenceEquals(x.Item, slot.Item));
                    InsertElevation(elevation, insertion, scale, space, transaction, resources, dimensionStyle, null, useTianzhengTitle);
                    completed++; if (progress != null) progress(completed, elevations.Count, elevation.Item.Code);
                }
            }
            InsertIndependentScheduleAndNotes(space.Database, space, transaction, elevations.Select(x => x.Item).ToList(), scale, layoutOptions, resources, origin, pageWidth, plan.PageCount, pageGap);
            return plan.PageCount;
        }

        private static void InsertIndependentScheduleAndNotes(Database database, BlockTableRecord space, Transaction transaction, IList<DoorWindowScheduleItem> items, int scale, DoorWindowLayoutOptions options, DraftingStandardResources resources, Point3d frameOrigin, double pageWidth, int pageCount, double pageGap)
        {
            if (options == null || (!options.IncludeSchedule && !options.IncludeScheduleNotes)) return;
            var suggestedX = frameOrigin.X + Math.Max(1, pageCount) * (pageWidth + pageGap);
            if (options.IncludeSchedule)
            {
                var position = options.SchedulePosition ?? new Point3d(suggestedX, frameOrigin.Y + 200d * scale, frameOrigin.Z);
                var table = BuildScheduleTable(database, items, scale, position);
                space.AppendEntity(table); transaction.AddNewlyCreatedDBObject(table, true); table.GenerateLayout();
                suggestedX = position.X;
            }
            if (options.IncludeScheduleNotes)
            {
                var position = options.NotesPosition ?? new Point3d(suggestedX, frameOrigin.Y, frameOrigin.Z);
                var notes = new MText
                {
                    Contents = string.Join("\\P", ScheduleNotes), Location = position, Attachment = AttachmentPoint.TopLeft,
                    TextHeight = 3.5d * scale, LineSpacingFactor = 1.35d, LineSpacingStyle = LineSpacingStyle.AtLeast,
                    Width = 180d * scale, TextStyleId = resources.AnnotationTextStyleId, LayerId = resources.AnnotationTextLayerId
                };
                space.AppendEntity(notes); transaction.AddNewlyCreatedDBObject(notes, true);
            }
        }

        private static void AddFrameReference(BlockTableRecord space, Transaction transaction, BlockTableRecord definition, ObjectId definitionId, FrameDefinition frame, Point3d pageOrigin, Point3d definitionMin, double factor, int scale, int pageNumber, ObjectId layer)
        {
            var position = new Point3d(pageOrigin.X - definitionMin.X * factor, pageOrigin.Y - definitionMin.Y * factor, pageOrigin.Z - definitionMin.Z * factor);
            var reference = new BlockReference(position, definitionId) { ScaleFactors = new Scale3d(factor), LayerId = layer };
            space.AppendEntity(reference); transaction.AddNewlyCreatedDBObject(reference, true);
            foreach (ObjectId id in definition)
            {
                var attributeDefinition = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeDefinition;
                if (attributeDefinition == null || attributeDefinition.Constant) continue;
                var attribute = new AttributeReference(); attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                var tag = (attributeDefinition.Tag ?? string.Empty).Trim(); var value = attributeDefinition.TextString;
                if (TagMatches(tag, frame.PrintScaleAttributeTag, "比例")) value = "1:" + scale;
                else if (TagMatches(tag, frame.SheetNameAttributeTag, "图纸名称", "图名")) value = "门窗立面图（" + pageNumber + "）";
                else if (TagMatches(tag, frame.SheetNumberAttributeTag, "图号")) value = "MCLM-" + pageNumber.ToString("00", CultureInfo.InvariantCulture);
                else if (TagMatches(tag, frame.BuildingAttributeTag, "子项目名称")) value = string.IsNullOrWhiteSpace(frame.DefaultBuilding) ? value : frame.DefaultBuilding;
                attribute.TextString = string.IsNullOrWhiteSpace(value) || value.StartsWith("<", StringComparison.Ordinal) ? tag : value;
                reference.AttributeCollection.AppendAttribute(attribute); transaction.AddNewlyCreatedDBObject(attribute, true);
            }
        }

        private static bool TagMatches(string tag, string configured, params string[] aliases)
        {
            if (!string.IsNullOrWhiteSpace(configured) && string.Equals(tag, configured.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return aliases.Any(x => string.Equals(tag, x, StringComparison.OrdinalIgnoreCase) || tag.Contains(x));
        }

        private static void GetDefinitionBounds(BlockTableRecord definition, Transaction transaction, out Point3d min, out double width, out double height)
        {
            var first = true; var extents = new Extents3d();
            foreach (ObjectId id in definition)
            {
                var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; if (entity == null) continue;
                try { if (first) { extents = entity.GeometricExtents; first = false; } else extents.AddExtents(entity.GeometricExtents); } catch { }
            }
            if (first) throw new InvalidOperationException("登记图框块“" + definition.Name + "”没有可计算范围的图形。");
            min = extents.MinPoint; width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X); height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            if (width < 1e-6 || height < 1e-6) throw new InvalidOperationException("登记图框块尺寸无效。");
        }

        /// <summary>
        /// 标注分两层：
        /// 内层（innerY/innerX）——安装缝与全部分段连成一条连续标注直线，
        /// 端点取洞口边缘、安装缝边界与所有分格边界；
        /// 外层（outerY/outerX）——洞口总宽、总高。
        /// 界线长与层间距由 CreatePlacement 按 1:1 纸面值换算（内层 4、间距 4）。
        /// </summary>
        private static void AddLayerDimensions(DoorWindowElevationGeometry geometry, Point3d origin, double innerGap, double outerGap, BlockTableRecord space, Transaction transaction, ObjectId layer, ObjectId style, DoorWindowElevationMetadata metadata, DoorWindowScheduleItem item)
        {
            const double tolerance = .05d;
            var gap = item.HasInstallationGap ? Math.Max(0d, item.InstallationGap) : 0d;
            var innerY = origin.Y - innerGap; var outerY = origin.Y - outerGap;
            var overallLeft = -geometry.BayLeftExtent;
            var overallRight = item.Width + geometry.BayRightExtent;
            var innerX = origin.X + overallLeft - innerGap; var outerX = origin.X + overallLeft - outerGap;

            // 水平连续标注把左右转折面与正面作为同一个凸窗整体处理。
            var xs = new List<double> { overallLeft, 0d, gap, item.Width - gap, item.Width, overallRight };
            foreach (var cell in geometry.Cells) { xs.Add(cell.Left); xs.Add(cell.Right); }
            AddContinuousRun(space, transaction, origin, xs, true, innerY, 0d, layer, style, metadata, tolerance);

            // 普通门窗在左侧标注；凸窗左右展开面各自标注竖向分格，避免不同面的分格尺寸混在一条链里。
            if (geometry.BayLeftExtent <= tolerance && geometry.BayRightExtent <= tolerance)
            {
                var ys = new List<double> { 0d, gap, item.Height - gap, item.Height };
                foreach (var cell in geometry.Cells) { ys.Add(cell.Bottom); ys.Add(cell.Top); }
                AddContinuousRun(space, transaction, origin, ys, false, innerX, 0d, layer, style, metadata, tolerance);
            }
            else
            {
                var leftYs = new List<double> { 0d, item.Height };
                foreach (var cell in geometry.Cells.Where(cell => cell.Right <= tolerance)) { leftYs.Add(cell.Bottom); leftYs.Add(cell.Top); }
                AddContinuousRun(space, transaction, origin, leftYs, false, innerX, overallLeft, layer, style, metadata, tolerance);

                var rightYs = new List<double> { 0d, item.Height };
                foreach (var cell in geometry.Cells.Where(cell => cell.Left >= item.Width - tolerance)) { rightYs.Add(cell.Bottom); rightYs.Add(cell.Top); }
                var rightDimensionX = origin.X + overallRight + innerGap;
                AddContinuousRun(space, transaction, origin, rightYs, false, rightDimensionX, overallRight, layer, style, metadata, tolerance);
            }

            // 外层：总宽取左右展开面的总和，总高以整体最左边为尺寸基准。
            var overallStart = new Point3d(origin.X + overallLeft, origin.Y, origin.Z);
            var overallEnd = new Point3d(origin.X + overallRight, origin.Y, origin.Z);
            AppendTagged(space, transaction, new RotatedDimension(0d, overallStart, overallEnd, new Point3d((overallStart.X + overallEnd.X) / 2d, outerY, origin.Z), string.Empty, style) { LayerId = layer }, metadata);
            AppendTagged(space, transaction, new RotatedDimension(Math.PI / 2d, overallStart, new Point3d(overallStart.X, origin.Y + item.Height, origin.Z), new Point3d(outerX, origin.Y + item.Height / 2d, origin.Z), string.Empty, style) { LayerId = layer }, metadata);
        }

        /// <summary>在凸窗正面与两侧展开面的分界线上分别用引线注明“展开线”。</summary>
        private static void AddBayFoldLeaders(DoorWindowElevationGeometry geometry, Point3d origin, int scale, BlockTableRecord space, Transaction transaction, DraftingStandardResources resources, DoorWindowElevationMetadata metadata)
        {
            if (geometry.BayLeftExtent <= .01d && geometry.BayRightExtent <= .01d) return;
            var textHeight = 2.5d * scale;
            var elbowRise = 5d * scale;
            var horizontal = 7d * scale;
            Add(0d, true);
            Add(geometry.HoleWidth, false);

            void Add(double localX, bool toLeft)
            {
                if (toLeft && geometry.BayLeftExtent <= .01d || !toLeft && geometry.BayRightExtent <= .01d) return;
                var target = new Point3d(origin.X + localX, origin.Y + geometry.HoleHeight * .82d, origin.Z);
                var elbow = new Point3d(target.X + (toLeft ? -horizontal : horizontal), origin.Y + geometry.HoleHeight + elbowRise, origin.Z);
                var textPoint = new Point3d(elbow.X + (toLeft ? -horizontal * .55d : horizontal * .55d), elbow.Y, origin.Z);
                var leader = new Leader { LayerId = resources.AnnotationTextLayerId, HasArrowHead = true };
                leader.AppendVertex(target); leader.AppendVertex(elbow); leader.AppendVertex(textPoint);
                AppendTagged(space, transaction, leader, metadata);
                var note = new MText
                {
                    Contents = "展开线", Location = textPoint,
                    Attachment = toLeft ? AttachmentPoint.MiddleRight : AttachmentPoint.MiddleLeft,
                    TextHeight = textHeight, TextStyleId = resources.AnnotationTextStyleId,
                    LayerId = resources.AnnotationTextLayerId
                };
                AppendTagged(space, transaction, note, metadata);
            }
        }

        private static void AddContinuousRun(BlockTableRecord space, Transaction transaction, Point3d origin, IEnumerable<double> coordinates, bool horizontal, double lineCoordinate, double extensionBase, ObjectId layer, ObjectId style, DoorWindowElevationMetadata metadata, double tolerance)
        {
            var points = coordinates.Select(x => Math.Round(x, 3)).Distinct().OrderBy(x => x).ToList();
            for (var index = 0; index + 1 < points.Count; index++)
            {
                var first = points[index]; var second = points[index + 1];
                if (Math.Abs(second - first) <= tolerance) continue;
                var start = horizontal ? new Point3d(origin.X + first, origin.Y + extensionBase, origin.Z) : new Point3d(origin.X + extensionBase, origin.Y + first, origin.Z);
                var end = horizontal ? new Point3d(origin.X + second, origin.Y + extensionBase, origin.Z) : new Point3d(origin.X + extensionBase, origin.Y + second, origin.Z);
                var textPoint = horizontal
                    ? new Point3d(origin.X + (first + second) / 2d, lineCoordinate, origin.Z)
                    : new Point3d(lineCoordinate, origin.Y + (first + second) / 2d, origin.Z);
                var rotation = horizontal ? 0d : Math.PI / 2d;
                AppendTagged(space, transaction, new RotatedDimension(rotation, start, end, textPoint, string.Empty, style) { LayerId = layer }, metadata);
            }
        }

        private static void AddCenteredText(BlockTableRecord owner, Transaction transaction, string value, Point3d point, double height, ObjectId style, ObjectId layer, DoorWindowElevationMetadata metadata)
        {
            // DBText recalculates AlignmentPoint when a SHX/TTF text style is
            // assigned, which caused the scale labels to jump far away from the
            // elevation. MText uses Location as its stable anchor.
            var text = new MText
            {
                Contents = value ?? string.Empty,
                Location = point,
                Attachment = AttachmentPoint.MiddleCenter,
                TextHeight = height,
                TextStyleId = style,
                LayerId = layer
            };
            AppendTagged(owner, transaction, text, metadata);
        }
        private static void AppendTagged(BlockTableRecord owner, Transaction transaction, Entity entity, DoorWindowElevationMetadata metadata)
        { Append(owner, transaction, entity); DoorWindowElevationMetadataService.Attach(entity, metadata); }
        private static void Append(BlockTableRecord owner, Transaction transaction, Entity entity)
        { owner.AppendEntity(entity); transaction.AddNewlyCreatedDBObject(entity, true); }
    }
}
