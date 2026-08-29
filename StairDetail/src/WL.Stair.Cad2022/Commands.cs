using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using WL.Stair.Core.Calculation;
using WL.Stair.Core.Domain;
using WL.Stair.Core.Geometry;
using WL.Stair.Core.Layout;
using WL.Stair.Core.Validation;
using WL.Stair.CadShared.PlanCapture;

namespace WL.Stair.Cad2022
{
    public sealed class Commands
    {
        private static StairSettingsWindow _settingsWindow;

        [CommandMethod("LTDY", CommandFlags.Modal)]
        public void GenerateStairDetail()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            if (_settingsWindow != null)
            {
                if (_settingsWindow.WindowState == System.Windows.WindowState.Minimized)
                    _settingsWindow.WindowState = System.Windows.WindowState.Normal;
                _settingsWindow.Activate();
                return;
            }

            var settingsWindow = new StairSettingsWindow();
            _settingsWindow = settingsWindow;
            settingsWindow.Completed += OnSettingsWindowCompleted;
            Application.ShowModelessWindow(settingsWindow);
        }

        private static async void OnSettingsWindowCompleted(object sender, EventArgs eventArgs)
        {
            var settingsWindow = sender as StairSettingsWindow;
            if (settingsWindow == null) return;
            settingsWindow.Completed -= OnSettingsWindowCompleted;
            if (ReferenceEquals(_settingsWindow, settingsWindow)) _settingsWindow = null;

            var document = Application.DocumentManager.MdiActiveDocument;
            if (!settingsWindow.IsConfirmed)
            {
                if (document != null) document.Editor.WriteMessage("\n已取消生成楼梯大样。\n");
                return;
            }

            var project = settingsWindow.Project;
            var calculation = settingsWindow.ConfirmedCalculation;
            var generateCombinedLayout = settingsWindow.GenerateCombinedLayout;
            var selectedLayoutFrame = settingsWindow.SelectedLayoutFrame;
            System.Exception captured = null;
            try
            {
                await Application.DocumentManager.ExecuteInCommandContextAsync(
                    unused =>
                    {
                        try
                        {
                            var activeDocument = Application.DocumentManager.MdiActiveDocument;
                            if (activeDocument == null)
                                throw new InvalidOperationException("当前没有打开的 CAD 图纸。");
                            if (generateCombinedLayout)
                                GenerateCombinedLayout(activeDocument, project, calculation,
                                    selectedLayoutFrame);
                            else
                                GenerateProject(activeDocument, project, calculation, null);
                        }
                        catch (System.Exception exception)
                        {
                            captured = exception;
                        }
                        return Task.FromResult(0);
                    },
                    null);
            }
            catch (System.Exception exception)
            {
                captured = exception;
            }

            if (captured == null) return;
            document = Application.DocumentManager.MdiActiveDocument;
            if (document != null)
                document.Editor.WriteMessage("\n楼梯大样操作失败: " + captured.Message + "\n");
            Application.ShowAlertDialog("楼梯大样操作失败：" + captured.Message);
        }

        private static void GenerateCombinedLayout(
            Document document,
            StairProjectDefinition project,
            StairProjectCalculationResult calculation,
            StairSettingsWindow.LayoutFrameOption frame)
        {
            var editor = document.Editor;
            if (frame == null) { editor.WriteMessage("\n没有选择可用的登记图框。\n"); return; }
            var point = editor.GetPoint("\n指定整套楼梯大样第一张图框的左下角插入点: ");
            if (point.Status != PromptStatus.OK) { editor.WriteMessage("\n已取消整套插入。\n"); return; }
            var stage = "准备数据";
            HashSet<ObjectId> objectsBeforeInsert = null;
            try
            {
                WriteCombinedLayoutLog("开始", "图框=" + frame.DisplayName
                    + "，比例=" + project.DrawingScale);
                var scale = Math.Max(1, project.DrawingScale);
                var entries = new List<CombinedEntry>();
                var cache = new StairPlanCacheService();
                foreach (var floor in project.Floors.Where(value => value != null))
                {
                    var source = FindPlanSource(project, floor.Id);
                    if (source == null || source.CropBoundaryPoints == null
                        || source.CropBoundaryPoints.Count < 3) continue;
                    var label = !string.IsNullOrWhiteSpace(floor.PlanFloorLabel)
                        ? floor.PlanFloorLabel
                        : (!string.IsNullOrWhiteSpace(source.FloorLabel) ? source.FloorLabel : floor.Name);
                    var title = (label ?? string.Empty) + "楼梯平面图";
                    source.TargetScale = scale;
                    if (!cache.IsValid(source, title))
                        throw new InvalidOperationException(title
                            + " 的小平面缓存不存在或参数已变化，请在楼层设置中重新拾取该层平面。"
                            + "整套插入不会重新裁剪天正墙。" );
                    double layoutOffsetX, layoutOffsetY, layoutWidth, layoutHeight;
                    StairPlanCacheService.GetLayoutRange(source, out layoutOffsetX,
                        out layoutOffsetY, out layoutWidth, out layoutHeight);
                    entries.Add(new CombinedEntry
                    {
                        Source = source,
                        FloorTitle = title,
                        CacheLayoutOffsetX = layoutOffsetX,
                        CacheLayoutOffsetY = layoutOffsetY,
                        LayoutItem = new StairLayoutItem
                        {
                            Key = floor.Id,
                            Name = title,
                            Width = layoutWidth,
                            Height = layoutHeight
                        }
                    });
                }
                var section = new StairProjectGeometryBuilder().BuildSection(project, calculation);
                double minSectionX, minSectionY, maxSectionX, maxSectionY;
                GetBounds(section, out minSectionX, out minSectionY, out maxSectionX, out maxSectionY);
                var sectionEntry = new CombinedEntry
                {
                    Section = section,
                    SectionMinX = minSectionX,
                    SectionMinY = minSectionY,
                    LayoutItem = new StairLayoutItem
                    {
                        Key = "SECTION",
                        Name = (project.StairNumber ?? "LT") + " 楼梯剖面图",
                        Width = Math.Max(1.0, maxSectionX - minSectionX),
                        Height = Math.Max(1.0, maxSectionY - minSectionY),
                        IsSection = true
                    }
                };
                entries.Add(sectionEntry);
                entries = ApplyCombinedLayoutOrder(entries,
                    project.CombinedLayoutItemOrder);
                var layout = StairCombinedLayout.Compute(entries.Select(value => value.LayoutItem),
                    new StairLayoutOptions
                    {
                        PageWidth = frame.PageWidth * scale,
                        PageHeight = frame.PageHeight * scale,
                        LeftMargin = frame.LeftMargin * scale,
                        RightMargin = frame.RightMargin * scale,
                        TopMargin = frame.TopMargin * scale,
                        BottomMargin = frame.BottomMargin * scale,
                        ItemGap = 10.0 * scale,
                        GridColumns = project.CombinedLayoutGridColumns,
                        GridRows = project.CombinedLayoutGridRows,
                        ColumnRatios = project.CombinedLayoutColumnRatios,
                        RowRatios = project.CombinedLayoutRowRatios
                    });
                StairCombinedLayout.ApplyPlacements(layout,
                    project.CombinedLayoutPlacements);
                const double pageGapPaper = 25.0;
                objectsBeforeInsert = SnapshotCurrentSpace(document.Database);
                stage = "插入图框";
                InsertRegisteredFrames(frame.RegistrationId, scale, point.Value,
                    layout.PageCount, pageGapPaper);
                WriteCombinedLayoutLog(stage, "成功，页数=" + layout.PageCount);
                foreach (var slot in layout.Slots)
                {
                    var entry = entries.First(value => ReferenceEquals(value.LayoutItem, slot.Item));
                    var pageOrigin = new Point3d(
                        point.Value.X + slot.Page * (layout.PageWidth + pageGapPaper * scale),
                        point.Value.Y, point.Value.Z);
                    var target = new Point3d(pageOrigin.X + slot.X, pageOrigin.Y + slot.Y, pageOrigin.Z);
                    if (entry.Source != null)
                    {
                        stage = "插入平面缓存：" + entry.FloorTitle;
                        var inserted = cache.Insert(document, entry.Source,
                            new Point3d(target.X - entry.CacheLayoutOffsetX,
                                target.Y - entry.CacheLayoutOffsetY, target.Z));
                        if (inserted <= 0)
                            throw new InvalidOperationException(entry.FloorTitle
                                + " 的缓存文件没有可插入对象。" );
                        editor.WriteMessage("\n已插入 " + entry.FloorTitle
                            + "：" + inserted + " 个对象。\n");
                        WriteCombinedLayoutLog(stage, "成功，对象数=" + inserted);
                    }
                    else
                    {
                        stage = "插入楼梯剖面";
                        using (var transaction = document.Database.TransactionManager.StartTransaction())
                        {
                            new CadLineRenderer().Render(document.Database, transaction, entry.Section,
                                new Point3d(target.X - entry.SectionMinX,
                                    target.Y - entry.SectionMinY, target.Z));
                            transaction.Commit();
                        }
                        WriteCombinedLayoutLog(stage, "成功");
                    }
                }
                stage = "插入排版分格";
                DrawCombinedLayoutGrid(document.Database, layout, point.Value,
                    pageGapPaper * scale, scale);
                WriteCombinedLayoutLog(stage, "成功（普通虚线，不创建组和夹点编辑）");
                editor.WriteMessage("\n整套楼梯大样已插入：" + (entries.Count - 1)
                    + " 个平面、1 个剖面、" + layout.PageCount + " 张图框。\n");
            }
            catch (System.Exception exception)
            {
                if (objectsBeforeInsert != null)
                    EraseObjectsCreatedAfter(document, objectsBeforeInsert);
                WriteCombinedLayoutLog(stage + "失败", exception.ToString());
                editor.WriteMessage("\n整套插入失败（" + stage + "）："
                    + exception.Message + "\n已回滚本次插入产生的图框和构件。\n");
            }
        }

        private static void DrawCombinedLayoutGrid(Database database,
            StairLayoutPlan layout, Point3d origin, double pageGap, int scale)
        {
            if (database == null || layout == null || layout.PageCount <= 0) return;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var layerId = EnsureLayoutGridLayer(database, transaction);
                var lineTypeId = EnsureLayoutGridLineType(database, transaction);
                var space = (BlockTableRecord)transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForWrite);
                for (var page = 0; page < layout.PageCount; page++)
                {
                    var pageOffset = page * (layout.PageWidth + pageGap);
                    var left = origin.X + pageOffset + layout.ContentLeft;
                    var right = origin.X + pageOffset + layout.ContentRight;
                    var bottom = origin.Y + layout.ContentBottom;
                    var top = origin.Y + layout.ContentTop;
                    AddLayoutGridSegment(space, transaction, layerId, lineTypeId,
                        new Point3d(left, bottom, origin.Z), new Point3d(right, bottom, origin.Z), scale);
                    AddLayoutGridSegment(space, transaction, layerId, lineTypeId,
                        new Point3d(right, bottom, origin.Z), new Point3d(right, top, origin.Z), scale);
                    AddLayoutGridSegment(space, transaction, layerId, lineTypeId,
                        new Point3d(right, top, origin.Z), new Point3d(left, top, origin.Z), scale);
                    AddLayoutGridSegment(space, transaction, layerId, lineTypeId,
                        new Point3d(left, top, origin.Z), new Point3d(left, bottom, origin.Z), scale);

                    var running = 0.0;
                    for (var column = 0; column < layout.ColumnWidths.Count - 1; column++)
                    {
                        running += layout.ColumnWidths[column];
                        var gaps = layout.Slots.Where(slot => slot.Page == page
                                && slot.Column <= column
                                && slot.Column + slot.ColumnSpan > column + 1)
                            .Select(slot => Tuple.Create(origin.Y + slot.CellY,
                                origin.Y + slot.CellY + slot.CellHeight));
                        foreach (var interval in SubtractIntervals(bottom, top, gaps))
                            AddLayoutGridSegment(space, transaction, layerId, lineTypeId,
                                new Point3d(left + running, interval.Item1, origin.Z),
                                new Point3d(left + running, interval.Item2, origin.Z), scale);
                    }

                    running = 0.0;
                    for (var row = 0; row < layout.RowHeights.Count - 1; row++)
                    {
                        running += layout.RowHeights[row];
                        var y = top - running;
                        var gaps = layout.Slots.Where(slot => slot.Page == page
                                && slot.Row <= row
                                && slot.Row + slot.RowSpan > row + 1)
                            .Select(slot => Tuple.Create(origin.X + pageOffset + slot.CellX,
                                origin.X + pageOffset + slot.CellX + slot.CellWidth));
                        foreach (var interval in SubtractIntervals(left, right, gaps))
                            AddLayoutGridSegment(space, transaction, layerId, lineTypeId,
                                new Point3d(interval.Item1, y, origin.Z),
                                new Point3d(interval.Item2, y, origin.Z), scale);
                    }
                }
                transaction.Commit();
            }
        }

        private static IEnumerable<Tuple<double, double>> SubtractIntervals(
            double start, double end, IEnumerable<Tuple<double, double>> exclusions)
        {
            var cursor = start;
            foreach (var value in (exclusions ?? Enumerable.Empty<Tuple<double, double>>())
                .Select(item => Tuple.Create(Math.Max(start, Math.Min(item.Item1, item.Item2)),
                    Math.Min(end, Math.Max(item.Item1, item.Item2))))
                .Where(item => item.Item2 > item.Item1 + 0.01)
                .OrderBy(item => item.Item1))
            {
                if (value.Item1 > cursor + 0.01)
                    yield return Tuple.Create(cursor, value.Item1);
                cursor = Math.Max(cursor, value.Item2);
                if (cursor >= end - 0.01) yield break;
            }
            if (cursor < end - 0.01) yield return Tuple.Create(cursor, end);
        }

        private static void AddLayoutGridSegment(BlockTableRecord space,
            Transaction transaction, ObjectId layerId, ObjectId lineTypeId,
            Point3d start, Point3d end, int scale)
        {
            if (start.DistanceTo(end) <= 0.01) return;
            var line = new Line(start, end)
            {
                LayerId = layerId,
                LinetypeScale = Math.Max(1.0, scale),
                LineWeight = LineWeight.LineWeight013
            };
            if (!lineTypeId.IsNull) line.LinetypeId = lineTypeId;
            space.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
        }

        private static ObjectId EnsureLayoutGridLayer(Database database,
            Transaction transaction)
        {
            const string name = "WL-图框";
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (table.Has(name))
            {
                var existing = (LayerTableRecord)transaction.GetObject(table[name], OpenMode.ForWrite);
                existing.Color = Color.FromColorIndex(ColorMethod.ByAci, 250);
                return existing.ObjectId;
            }
            table.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 250)
            };
            var id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static ObjectId EnsureLayoutGridLineType(Database database,
            Transaction transaction)
        {
            const string name = "DASHED";
            var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
            if (!table.Has(name))
            {
                try
                {
                    database.LoadLineTypeFile(name, "acad.lin");
                    table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId,
                        OpenMode.ForRead);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception) { }
            }
            return table.Has(name) ? table[name] : ObjectId.Null;
        }

        private static HashSet<ObjectId> SnapshotCurrentSpace(Database database)
        {
            var result = new HashSet<ObjectId>();
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                var space = transaction.GetObject(database.CurrentSpaceId,
                    OpenMode.ForRead, false) as BlockTableRecord;
                if (space != null)
                    foreach (var id in space.Cast<ObjectId>())
                    {
                        try
                        {
                            var value = transaction.GetObject(id, OpenMode.ForRead, false);
                            if (value != null && !value.IsErased) result.Add(id);
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception) { }
                    }
            }
            return result;
        }

        private static void EraseObjectsCreatedAfter(Document document,
            HashSet<ObjectId> before)
        {
            try
            {
                var created = SnapshotCurrentSpace(document.Database)
                    .Where(id => !before.Contains(id)).ToList();
                if (created.Count == 0) return;
                using (document.LockDocument())
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (var id in created)
                    {
                        DBObject value;
                        try { value = transaction.GetObject(id, OpenMode.ForWrite, true); }
                        catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                        if (value != null && !value.IsErased) value.Erase();
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                WriteCombinedLayoutLog("回滚警告", exception.ToString());
            }
        }

        private static void WriteCombinedLayoutLog(string stage, string message)
        {
            try
            {
                var root = Environment.GetEnvironmentVariable("WANLUO_ARCHITECTURE_TOOLS_ROOT");
                if (string.IsNullOrWhiteSpace(root))
                {
                    root = Path.GetDirectoryName(typeof(Commands).Assembly.Location);
                    for (var index = 0; index < 2 && !string.IsNullOrWhiteSpace(root); index++)
                        root = Path.GetDirectoryName(root);
                }
                if (string.IsNullOrWhiteSpace(root)) return;
                var directory = Path.Combine(root, "用户配置文件", "Logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "stair-combined-layout.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + stage + "] "
                    + (message ?? string.Empty) + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private static StairPlanSourceDefinition FindPlanSource(StairProjectDefinition project, string floorId)
        {
            var source = (project.PlanSources ?? new List<StairPlanSourceDefinition>())
                .FirstOrDefault(value => value != null && string.Equals(value.FloorId, floorId,
                    StringComparison.OrdinalIgnoreCase));
            if (source != null) return source;
            var storey = project.Storeys.FirstOrDefault(value => value != null
                && string.Equals(value.LowerFloorId, floorId, StringComparison.OrdinalIgnoreCase));
            return storey == null ? null : (project.PlanSources ?? new List<StairPlanSourceDefinition>())
                .FirstOrDefault(value => value != null && string.IsNullOrWhiteSpace(value.FloorId)
                    && string.Equals(value.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase));
        }

        private static List<CombinedEntry> ApplyCombinedLayoutOrder(
            IEnumerable<CombinedEntry> source, IEnumerable<string> savedOrder)
        {
            var entries = (source ?? Enumerable.Empty<CombinedEntry>()).ToList();
            var keys = entries.Select(value => value.LayoutItem.Key).ToList();
            var order = (savedOrder ?? Enumerable.Empty<string>())
                .Where(value => keys.Contains(value, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            order.AddRange(keys.Where(value => !order.Contains(value,
                StringComparer.OrdinalIgnoreCase)));
            return order.Select(key => entries.First(value => string.Equals(
                value.LayoutItem.Key, key, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        private static void InsertRegisteredFrames(string registrationId, int scale,
            Point3d origin, int pageCount, double pageGap)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("BatchPdfPublisher.Views.StairLayoutFrameBridge", false))
                .FirstOrDefault(value => value != null);
            var method = type == null ? null : type.GetMethod("InsertFrames", BindingFlags.Public | BindingFlags.Static);
            if (method == null) throw new InvalidOperationException("主插件尚未提供整套图框插入服务，请重新加载最新版插件。");
            try
            {
                method.Invoke(null, new object[] { registrationId, scale, origin.X, origin.Y,
                    origin.Z, pageCount, pageGap });
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException ?? exception;
            }
        }

        private static void GetBounds(DrawingView view, out double minX, out double minY,
            out double maxX, out double maxY)
        {
            var points = new List<Point2D>();
            points.AddRange(view.Lines.SelectMany(line => new[] { line.Start, line.End }));
            points.AddRange(view.HatchRegions.SelectMany(region => region.Boundary));
            points.AddRange(view.Texts.Select(text => text.Position));
            points.AddRange(view.Dimensions.SelectMany(value => new[] { value.FirstExtensionOrigin,
                value.SecondExtensionOrigin, value.DimensionLinePoint }));
            points.AddRange(view.Leaders.SelectMany(value => value.Vertices));
            if (view.Title != null) { points.Add(view.Title.Position); points.Add(new Point2D(
                view.Title.Position.X + view.Title.TargetWidth, view.Title.Position.Y)); }
            if (points.Count == 0) points.Add(new Point2D(0, 0));
            minX = points.Min(value => value.X); minY = points.Min(value => value.Y);
            maxX = points.Max(value => value.X); maxY = points.Max(value => value.Y);
        }

        private sealed class CombinedEntry
        {
            public StairLayoutItem LayoutItem;
            public StairPlanSourceDefinition Source;
            public string FloorTitle;
            public DrawingView Section;
            public double SectionMinX;
            public double SectionMinY;
            public double CacheLayoutOffsetX;
            public double CacheLayoutOffsetY;
        }

        [CommandMethod("WLSTAIRTEST", CommandFlags.Modal)]
        public void GenerateStairDetailForTest()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var project = StairProjectDefinition.CreateDefault();
            var constraints = new StairProjectConstraintService();
            constraints.Normalize(project);
            constraints.Apply(project);
            GenerateProject(document, project, null, Point3d.Origin);
        }

        [CommandMethod("WLSTAIRCACHESELFTEST", CommandFlags.Modal)]
        public void ValidateStairPlanCaches()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var editor = document.Editor;
            var project = new StairProjectStorage().LoadOrDefault();
            var cache = new StairPlanCacheService();
            var sources = new List<Tuple<StairPlanSourceDefinition, string>>();
            foreach (var floor in project.Floors.Where(value => value != null))
            {
                var source = FindPlanSource(project, floor.Id);
                if (source == null || source.CropBoundaryPoints == null
                    || source.CropBoundaryPoints.Count < 3) continue;
                var label = !string.IsNullOrWhiteSpace(floor.PlanFloorLabel)
                    ? floor.PlanFloorLabel
                    : (!string.IsNullOrWhiteSpace(source.FloorLabel)
                        ? source.FloorLabel : floor.Name);
                var title = (label ?? string.Empty) + "楼梯平面图";
                source.TargetScale = Math.Max(1, project.DrawingScale);
                if (!cache.IsValid(source, title))
                    throw new InvalidOperationException(title + " 的缓存无效，不能执行自检。");
                sources.Add(Tuple.Create(source, title));
            }

            if (sources.Count == 0)
            {
                editor.WriteMessage("\nWLSTAIRCACHESELFTEST：没有找到可验证的楼梯平面缓存。\n");
                return;
            }

            var allBefore = SnapshotCurrentSpace(document.Database);
            try
            {
                for (var index = 0; index < sources.Count; index++)
                {
                    var item = sources[index];
                    var previewLines = cache.ReadPreviewLines(item.Item1, 1800);
                    if (previewLines.Count < 4)
                        throw new InvalidOperationException(item.Item2
                            + " 的缓存无法提取平面预览线。" );
                    var target = new Point3d(index * 10000.0, -50000.0, 0.0);
                    var before = SnapshotCurrentSpace(document.Database);
                    var count = cache.Insert(document, item.Item1, target);
                    var created = SnapshotCurrentSpace(document.Database)
                        .Where(id => !before.Contains(id)).ToList();
                    Extents3d extents;
                    if (count <= 0 || !TryGetCurrentObjectExtents(document.Database,
                        created, out extents))
                        throw new InvalidOperationException(item.Item2
                            + " 插入后没有可验证的几何范围。");

                    const double tolerance = 2.0;
                    if (Math.Abs(extents.MinPoint.X - target.X) > tolerance
                        || Math.Abs(extents.MinPoint.Y - target.Y) > tolerance
                        || extents.MaxPoint.X > target.X + item.Item1.CacheWidth + tolerance
                        || extents.MaxPoint.Y > target.Y + item.Item1.CacheHeight + tolerance)
                        throw new InvalidOperationException(string.Format(
                            "{0} 坐标归一化失败：目标=({1:F3},{2:F3})，实际范围="
                            + "({3:F3},{4:F3})~({5:F3},{6:F3})，记录尺寸={7:F3}×{8:F3}。",
                            item.Item2, target.X, target.Y, extents.MinPoint.X,
                            extents.MinPoint.Y, extents.MaxPoint.X, extents.MaxPoint.Y,
                            item.Item1.CacheWidth, item.Item1.CacheHeight));
                    editor.WriteMessage("\n[通过] {0}：{1} 个对象，{2} 条预览线，范围 {3:F1}×{4:F1}。",
                        item.Item2, count, previewLines.Count, extents.MaxPoint.X - extents.MinPoint.X,
                        extents.MaxPoint.Y - extents.MinPoint.Y);
                }
                editor.WriteMessage("\nWLSTAIRCACHESELFTEST PASS：{0} 个楼层缓存坐标、尺寸和插入位置均正确。\n",
                    sources.Count);
            }
            finally
            {
                EraseObjectsCreatedAfter(document, allBefore);
            }
        }

        private static bool TryGetCurrentObjectExtents(Database database,
            IEnumerable<ObjectId> ids, out Extents3d combined)
        {
            combined = new Extents3d();
            var found = false;
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in ids ?? Enumerable.Empty<ObjectId>())
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                    if (entity == null || entity.IsErased) continue;
                    try
                    {
                        var extents = entity.GeometricExtents;
                        if (!found) { combined = extents; found = true; }
                        else combined.AddExtents(extents);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
                }
            }
            return found;
        }

        private static void GenerateProject(
            Document document,
            StairProjectDefinition project,
            StairProjectCalculationResult confirmedCalculation,
            Point3d? fixedInsertionPoint)
        {
            var editor = document.Editor;
            var calculation = confirmedCalculation;
            if (calculation == null)
            {
                var outcome = new StairProjectCalculator().Calculate(project);
                WriteIssues(editor, outcome);
                if (!outcome.IsSuccess)
                {
                    editor.WriteMessage("\n参数存在错误，未生成图形。\n");
                    return;
                }
                calculation = outcome.Result;
            }

            Point3d insertionPoint;
            if (fixedInsertionPoint.HasValue)
            {
                insertionPoint = fixedInsertionPoint.Value;
            }
            else
            {
                var pointResult = editor.GetPoint("\n指定楼梯大样插入点: ");
                if (pointResult.Status != PromptStatus.OK)
                {
                    editor.WriteMessage("\n已取消生成楼梯大样。\n");
                    return;
                }
                insertionPoint = pointResult.Value;
            }

            try
            {
                var section = new StairProjectGeometryBuilder().BuildSection(project, calculation);
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    new CadLineRenderer().Render(
                        document.Database,
                        transaction,
                        section,
                        insertionPoint);
                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\n构件化楼梯剖面已生成：{0} 个楼层段，总高度 {1:F0} mm。\n",
                    calculation.Storeys.Count,
                    calculation.TotalHeight);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n生成失败：{0}\n", exception.Message);
            }
        }

        private static void Generate(
            Document document,
            StairDefinition definition,
            int floorCount,
            Point3d? fixedInsertionPoint)
        {
            var editor = document.Editor;

            var outcome = new StairCalculator().Calculate(definition);
            WriteIssues(editor, outcome);

            if (!outcome.IsSuccess)
            {
                editor.WriteMessage("\n参数存在错误，未生成图形。\n");
                return;
            }

            Point3d insertionPoint;
            if (fixedInsertionPoint.HasValue)
            {
                insertionPoint = fixedInsertionPoint.Value;
            }
            else
            {
                var pointResult = editor.GetPoint("\n指定楼梯大样插入点: ");
                if (pointResult.Status != PromptStatus.OK)
                {
                    editor.WriteMessage("\n已取消生成楼梯大样。\n");
                    return;
                }

                insertionPoint = pointResult.Value;
            }

            try
            {
                var geometryBuilder = new StairGeometryBuilder();
                var firstFloorPlan = geometryBuilder.BuildPlan(
                    definition,
                    outcome.Result,
                    StairPlanLevel.FirstFloor);
                var intermediateFloorPlan = geometryBuilder.BuildPlan(
                    definition,
                    outcome.Result,
                    StairPlanLevel.IntermediateFloor);
                var topFloorPlan = geometryBuilder.BuildPlan(
                    definition,
                    outcome.Result,
                    StairPlanLevel.TopFloor);
                var section = geometryBuilder.BuildMultiFloorSection(
                    definition,
                    outcome.Result,
                    floorCount);
                var renderer = new CadLineRenderer();
                var planSpacing = outcome.Result.PlanWidth + 1000.0;
                var intermediateFloorPoint = insertionPoint + new Vector3d(0.0, -planSpacing, 0.0);
                var topFloorPoint = insertionPoint + new Vector3d(0.0, -(planSpacing * 2.0), 0.0);
                var sectionPoint = insertionPoint + new Vector3d(
                    outcome.Result.PlanLength + 2000.0,
                    -(planSpacing * 2.0),
                    0.0);

                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    renderer.Render(document.Database, transaction, firstFloorPlan, insertionPoint);
                    renderer.Render(document.Database, transaction, intermediateFloorPlan, intermediateFloorPoint);
                    renderer.Render(document.Database, transaction, topFloorPlan, topFloorPoint);
                    renderer.Render(document.Database, transaction, section, sectionPoint);
                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\n楼梯大样已生成：{0} 个踢面，踢面高 {1:F1}，踏步宽 {2:F1}。\n",
                    outcome.Result.TotalRiserCount,
                    outcome.Result.RiserHeight,
                    definition.TreadDepth);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n生成失败：{0}\n", exception.Message);
            }
        }

        private static void WriteIssues(Editor editor, StairCalculationOutcome outcome)
        {
            foreach (var issue in outcome.Issues)
            {
                var severity = issue.Severity == ValidationSeverity.Error ? "错误" : "提示";
                editor.WriteMessage("\n[{0}][{1}] {2}\n", severity, issue.Code, issue.Message);
            }
        }

        private static void WriteIssues(Editor editor, StairProjectCalculationOutcome outcome)
        {
            foreach (var issue in outcome.Issues)
            {
                var severity = issue.Severity == ValidationSeverity.Error ? "错误" : "提示";
                editor.WriteMessage("\n[{0}][{1}] {2}: {3}\n", severity, issue.Code, issue.ParameterName, issue.Message);
            }
        }
    }
}
