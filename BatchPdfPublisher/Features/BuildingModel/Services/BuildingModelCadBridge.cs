using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;      // DoorWindowElevationPreference / DoorWindowElevationTemplate
using BatchPdfPublisher.Views;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// CAD 侧的两个搬运工（P0）：
    ///
    /// - <see cref="PlaceView"/>（命令 <c>LTTZ</c>）：读建模程序产出的视图 JSON，
    ///   按视图图层表建成 CAD 实体（线 / 文字 / 填充）。
    /// - <see cref="ExportDrawing"/>（命令 <c>TQTZ</c>）：把当前 DWG 的图元导成中间格式，
    ///   交给建模程序去识别（识别本身是 P3，这版只负责把数据搬出去）。
    ///
    /// 设计约束（见开发计划 v3）：模型与投影都在程序侧，插件只做"进出"两件事，
    /// 因此这里刻意不引入任何几何算法；数据库访问全部在宿主线程内、用事务包住。
    /// </summary>
    internal static class BuildingModelCadBridge
    {
        private const string PlacementRegApp = "WL_BUILDING_VIEW";
        private static bool IsHeadlessHost => string.Equals(
            System.Diagnostics.Process.GetCurrentProcess().ProcessName, "accoreconsole",
            StringComparison.OrdinalIgnoreCase);

        public static void PlaceView(Document document)
        {
            if (document == null) return;
            if (!IsHeadlessHost)
            {
                var batch = PickViewBatch(document);
                if (batch == null) return;
                var cursor = new BatchPlacementCursor();
                var completed = 0;
                foreach (var entry in batch.SelectedEntries)
                {
                    if (!PlaceBatchEntry(document, entry.FilePath, entry.Id, batch.FrameMode, batch.SelectedFrame, cursor)) break;
                    completed++;
                }
                document.Editor.WriteMessage("\n批量落图完成 " + completed + " / " + batch.SelectedEntries.Count + " 张。");
                return;
            }
            var editor = document.Editor;
            SelectedViewId = null;                       // 每次落图重新记
            var path = PickViewFile(document, editor);
            if (string.IsNullOrWhiteSpace(path)) return;
            PlaceOneView(document, path, SelectedViewId, null, null);
        }

        private static bool PlaceOneView(Document document, string path, string placedViewId,
            BuildingFrameMode? frameMode, FrameDefinition selectedFrame)
        { return PlaceBatchEntry(document, path, placedViewId, frameMode, selectedFrame, null); }

        private sealed class BatchPlacementCursor
        {
            public Point3d? Next;
        }

        private static bool PlaceBatchEntry(Document document, string path, string placedViewId,
            BuildingFrameMode? frameMode, FrameDefinition selectedFrame, BatchPlacementCursor cursor)
        {
            var editor = document.Editor;

            ViewDocument view;
            try { view = BuildingModelJson.LoadView(path); }
            catch (Exception exception)
            {
                editor.WriteMessage("\n视图文件读取失败：" + exception.Message);
                return false;
            }
            SaveLastViewFolder(Path.GetDirectoryName(path));

            if (string.IsNullOrWhiteSpace(view.Id))
            {
                editor.WriteMessage("\n视图缺少稳定 ID，无法安全更新，请重新生成视图。");
                return false;
            }
            var sourceViewId = view.Id;
            if (view.Kind == ViewKind.Sheet && !view.ModelSpaceSheet)
            {
                editor.WriteMessage("\n这是旧版纸面缩放图纸，请在建筑模型中重新生成视图后落图；建筑必须保持 1:1。");
                return false;
            }
            if (frameMode == BuildingFrameMode.Project && selectedFrame == null)
            {
                editor.WriteMessage("\n尚未选择项目图框，落图已取消。");
                return false;
            }
            if (frameMode == BuildingFrameMode.Project && view.Kind == ViewKind.Sheet
                && !FrameMatches(view, selectedFrame))
            {
                editor.WriteMessage("\n“" + (view.Title ?? view.Id) + "”与所选项目图框的规格不匹配，落图已取消。");
                return false;
            }
            if (frameMode.HasValue && frameMode != BuildingFrameMode.None && view.Kind != ViewKind.Sheet)
            {
                try { view = ComposeSingleViewSheet(view, selectedFrame); }
                catch (Exception exception)
                {
                    editor.WriteMessage("\n视图排版失败：" + exception.Message);
                    return false;
                }
            }
            var sourceKey = ViewSourceKey(path, sourceViewId);
            var placements = FindPlacements(document.Database, sourceKey);
            PlacedView replacing = null;
            if (placements.Count > 0)
            {
                var choice = cursor != null && placements.Count == 1 ? 1 : PickPlacement(placements, editor);
                if (!choice.HasValue) return false;
                if (choice.Value > 0) replacing = placements[choice.Value - 1];
            }
            Point3d anchor;
            if (replacing != null && (cursor == null || !cursor.Next.HasValue)) anchor = replacing.Anchor;
            else
            {
                if (cursor != null && cursor.Next.HasValue) anchor = cursor.Next.Value;
                else
                {
                    var pointResult = editor.GetPoint(cursor == null
                        ? "\n指定视图插入点（视图左下角）："
                        : "\n指定批量落图起点（全部勾选视图自动向右排列）：");
                    if (pointResult.Status != PromptStatus.OK) return false;
                    anchor = pointResult.Value;
                    if (cursor != null) cursor.Next = anchor;
                }
                if (cursor != null)
                {
                    var bounds = BatchViewBounds(view);
                    anchor = new Point3d(anchor.X - bounds[0], anchor.Y - bounds[1], anchor.Z);
                }
            }
            var placementId = replacing == null ? Guid.NewGuid().ToString("N") : replacing.Id;

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hatchIds = new List<ObjectId>();
            var generatedIds = new List<ObjectId>();
            double[] placedBounds = null;
            var skippedGeneratedElements = 0;
            try
            {
              using (document.LockDocument())
              using (var transaction = document.Database.TransactionManager.StartTransaction())
              {
                var layerIds = EnsureViewLayers(document.Database, transaction);
                var space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite);

                // 图纸（Kind=Sheet）：先看看项目里有没有同纸张的登记图框，有就套用它，
                // 并跳过图纸自带的图框与标题栏（那一层叫 WL-模型-图纸框），避免双层图框。
                var skipLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ObjectId frameId = ObjectId.Null;
                var insertProject = view.Kind == ViewKind.Sheet &&
                    (!frameMode.HasValue || frameMode == BuildingFrameMode.Project);
                var frame = insertProject
                    ? InsertProjectFrame(document, transaction, space, view, anchor, out frameId,
                        selectedFrame, frameMode == BuildingFrameMode.Project) : null;
                if (!frameId.IsNull) generatedIds.Add(frameId);
                if (frame != null || frameMode == BuildingFrameMode.None) skipLayers.Add(ViewLayers.SheetFrame);

                foreach (var line in view.Lines ?? new List<ViewLine>())
                {
                    if(line.OpeningArcId!=null)continue;
                    if (!string.IsNullOrWhiteSpace(line.Layer) && skipLayers.Contains(line.Layer)) continue;
                    var entity=CreateViewLineEntity(line,anchor);
                    ApplyViewLineAppearance(entity,line,layerIds);
                    space.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    generatedIds.Add(entity.ObjectId);
                    Bump(counts, line.Layer);
                }

                foreach(var chain in BuildOpeningPathChains(view.Lines??new List<ViewLine>()))
                {
                    var first=chain.Appearance;if(skipLayers.Contains(first.Layer??string.Empty))continue;
                    var entity=CreateOpeningPathPolyline(chain,anchor);
                    ApplyOpeningPathAppearance(entity,first,layerIds,document.Database,transaction,view.Scale);
                    space.AppendEntity(entity);transaction.AddNewlyCreatedDBObject(entity,true);
                    generatedIds.Add(entity.ObjectId);Bump(counts,first.Layer);
                }

                foreach (var text in view.Texts ?? new List<ViewText>())
                {
                    if (!string.IsNullOrWhiteSpace(text.Layer) && skipLayers.Contains(text.Layer)) continue;
                    var entity = new DBText
                    {
                        TextString = text.Text ?? string.Empty,
                        Height = text.Height > 0.5d ? text.Height : 250d,
                        WidthFactor = text.WidthFactor>0?text.WidthFactor:1,
                        Rotation = text.Rotation * Math.PI / 180d,
                        Position = new Point3d(anchor.X + text.X, anchor.Y + text.Y, 0d)
                    };
                    ApplyLayer(entity, text.Layer, layerIds);
                    ApplyLineWeight(entity,text.LineWeight);
                    space.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    generatedIds.Add(entity.ObjectId);
                    Bump(counts, text.Layer);
                }

                // 圆（轴号圆圈等）
                foreach (var circle in view.Circles ?? new List<ViewCircle>())
                {
                    if (circle == null || Math.Abs(circle.Radius) < 0.5d) continue;
                    if (!string.IsNullOrWhiteSpace(circle.Layer) && skipLayers.Contains(circle.Layer)) continue;
                    var entity = new Circle(new Point3d(anchor.X + circle.X, anchor.Y + circle.Y, 0d), Vector3d.ZAxis,
                        Math.Abs(circle.Radius));
                    ApplyLayer(entity, circle.Layer, layerIds);
                    ApplyLineWeight(entity,circle.LineWeight);
                    space.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    generatedIds.Add(entity.ObjectId);
                    Bump(counts, circle.Layer);
                }

                // 尺寸标注：建成**真的 CAD 标注**（可拉伸、可改），不是拆成线 +
                // 文字。样式用插件统一的"万落建筑工具 1:N 标注样式"，与门窗立面一致。
                if (view.Dimensions != null && view.Dimensions.Count > 0)
                {
                    ObjectId dimensionStyle;
                    try
                    {
                        dimensionStyle = DraftingStandardService.EnsureDimensionStyleForScale(
                            document.Database, transaction, Math.Max(1, view.Scale));
                    }
                    catch (Exception exception)
                    {
                        dimensionStyle = document.Database.Dimstyle;
                        editor.WriteMessage("\n标注样式创建失败，改用当前标注样式：" + exception.Message);
                    }
                    var dimensionSettings = (DimStyleTableRecord)transaction.GetObject(dimensionStyle,OpenMode.ForRead);
                    SheetComposer.LayoutDimensionText(view);
                    var variableTextStyle=EnsureVariableDimensionTextStyle(document.Database,transaction,dimensionSettings.Dimtxsty);
                    var dimensionTextStyle=(TextStyleTableRecord)transaction.GetObject(variableTextStyle,OpenMode.ForRead);
                    var styleWidth=Math.Max(.01,dimensionTextStyle.XScale);
                    foreach (var dimension in view.Dimensions)
                    {
                        if (dimension == null) continue;
                        if (!string.IsNullOrWhiteSpace(dimension.Layer) && skipLayers.Contains(dimension.Layer)) continue;
                        var span = Math.Abs(dimension.To - dimension.From);
                        if (span < 0.5d) continue;
                        try
                        {
                            var entity = CreateDimension(dimension, anchor, dimensionStyle);
                            using(var overrides=(DimStyleTableRecord)dimensionSettings.Clone()) {
                                overrides.Dimtxsty=variableTextStyle;entity.SetDimstyleData(overrides);
                            }
                            entity.Dimscale=Math.Max(1,view.Scale);
                            entity.Dimtmove=2;
                            entity.Dimtxt=dimension.TextHeight/Math.Max(1,view.Scale);
                            entity.Dimdec=0;entity.Dimrnd=1;entity.Dimtix=true;entity.Dimtofl=true;
                            entity.Dimlfac=1;entity.Dimlunit=2;entity.Dimalt=false;
                            entity.Dimjust=0;entity.Dimtih=false;entity.Dimtoh=false;
                            entity.DimensionText="{\\W"+(dimension.TextWidthFactor/styleWidth).ToString("0.######",System.Globalization.CultureInfo.InvariantCulture)+";"+
                                (string.IsNullOrWhiteSpace(dimension.Text)?"<>":dimension.Text)+"}";
                            ApplyLayer(entity, string.IsNullOrWhiteSpace(dimension.Layer) ? ViewLayers.Dimension : dimension.Layer, layerIds);
                            ApplyLineWeight(entity,dimension.LineWeight);
                            if(dimension.LineWeight.HasValue) {
                                entity.Dimlwd=entity.LineWeight;entity.Dimlwe=entity.LineWeight;
                            }
                            space.AppendEntity(entity);
                            transaction.AddNewlyCreatedDBObject(entity, true);
                            generatedIds.Add(entity.ObjectId);
                            Bump(counts, string.IsNullOrWhiteSpace(dimension.Layer) ? ViewLayers.Dimension : dimension.Layer);
                        }
                        catch (Exception exception)
                        {
                            skippedGeneratedElements++;
                            editor.WriteMessage("\n一条尺寸标注失败已跳过（" + dimension.Note + "）：" + exception.Message);
                        }
                    }
                }

                foreach (var hatch in view.Hatches ?? new List<ViewHatch>())
                {
                    if(view.Kind==ViewKind.Plan || (hatch?.Layer==ViewLayers.CutHatch && string.Equals(hatch.Pattern,"SOLID",StringComparison.OrdinalIgnoreCase)))continue;
                    if (hatch == null || hatch.Boundary == null || hatch.Boundary.Count < 3) continue;
                    if (!string.IsNullOrWhiteSpace(hatch.Layer) && skipLayers.Contains(hatch.Layer)) continue;
                    var entity = new Hatch { Associative = false };
                    space.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    entity.SetDatabaseDefaults(document.Database);
                    ApplyLayer(entity, hatch.Layer, layerIds);
                    ApplyLineWeight(entity,hatch.LineWeight);
                    ApplyHatchPattern(entity, hatch, editor);
                    var loop = new Point2dCollection();
                    foreach (var point in hatch.Boundary) loop.Add(new Point2d(anchor.X + point.X, anchor.Y + point.Y));
                    try
                    {
                        entity.AppendLoop(HatchLoopTypes.External, loop, new DoubleCollection());
                        entity.EvaluateHatch(true);
                        hatchIds.Add(entity.ObjectId);
                        generatedIds.Add(entity.ObjectId);
                        Bump(counts, hatch.Layer);
                    }
                    catch (Exception exception)
                    {
                        entity.Erase();
                        skippedGeneratedElements++;
                        editor.WriteMessage("\n一处剖切填充失败已跳过：" + exception.Message);
                    }
                }

                // 填充压到最底层：否则实心/图案会把断面轮廓线盖住，看着"糊成一团"。
                if (hatchIds.Count > 0)
                {
                    try
                    {
                        var order = (DrawOrderTable)transaction.GetObject(space.DrawOrderTableId, OpenMode.ForWrite);
                        order.MoveToBottom(new ObjectIdCollection(hatchIds.ToArray()));
                    }
                    catch (Exception exception)
                    {
                        editor.WriteMessage("\n提示：填充置底未成功（不影响出图）：" + exception.Message);
                    }
                }
                if (generatedIds.Count == 0)
                    throw new InvalidOperationException("新视图没有可放置的实体，原图保持不变。");
                if (replacing != null && skippedGeneratedElements > 0)
                    throw new InvalidOperationException("新视图有 " + skippedGeneratedElements
                        + " 个生成元素失败；旧视图已保留，请修正后重试。");
                EnsurePlacementRegApp(document.Database, transaction);
                foreach (var id in generatedIds)
                {
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForWrite);
                    TagPlacement(entity, sourceKey, placementId, anchor);
                }
                // 批量间距以实际生成实体为准，覆盖项目图框和 CAD 标注样式产生的文字/引线。
                var expectedBounds = BatchViewBounds(view);
                placedBounds = new[] { anchor.X+expectedBounds[0],anchor.Y+expectedBounds[1],
                    anchor.X+expectedBounds[2],anchor.Y+expectedBounds[3] };
                foreach (var id in generatedIds)
                {
                    var entity = (Entity)transaction.GetObject(id,OpenMode.ForRead);
                    try
                    {
                        var dimension = entity as Dimension;
                        if (dimension != null) dimension.RecomputeDimensionBlock(true);
                        var extents = entity.GeometricExtents;
                        placedBounds[0] = Math.Min(placedBounds[0],extents.MinPoint.X);
                        placedBounds[1] = Math.Min(placedBounds[1],extents.MinPoint.Y);
                        placedBounds[2] = Math.Max(placedBounds[2],extents.MaxPoint.X);
                        placedBounds[3] = Math.Max(placedBounds[3],extents.MaxPoint.Y);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
                }
                if (replacing != null)
                {
                    // Erase only entities carrying this exact placement identity. Untagged
                    // user annotations and other instances of the same view stay untouched.
                    var newIds = new HashSet<ObjectId>(generatedIds);
                    var removeIds = new List<ObjectId>();
                    foreach (ObjectId id in space)
                    {
                        Entity entity;
                        try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                        var tag = ReadPlacement(entity);
                        if (tag == null || tag.SourceKey != sourceKey || tag.Id != placementId
                            || newIds.Contains(id)) continue;
                        removeIds.Add(id);
                    }
                    foreach (var id in removeIds)
                        ((Entity)transaction.GetObject(id, OpenMode.ForWrite)).Erase();
                }
                transaction.Commit();
              }
            }
            catch (Exception exception)
            {
                editor.WriteMessage("\n落图事务失败，未替换原视图：" + exception.Message);
                return false;
            }

            if (cursor != null)
            {
                var bounds = placedBounds;
                var width = Math.Max(1d, bounds[2] - bounds[0]);
                cursor.Next = new Point3d(bounds[2] + Math.Max(1000d, width * 0.1d),bounds[1],anchor.Z);
            }

            editor.WriteMessage("\n" + (replacing == null ? "落图完成：" : "视图更新完成：")
                + (view.Title ?? view.Id) + "（1:" + view.Scale + "）");
            foreach (var pair in counts.OrderByDescending(x => x.Value))
                editor.WriteMessage("\n  " + pair.Key + "：" + pair.Value + " 个实体");
            if (view.Warnings != null && view.Warnings.Count > 0)
                editor.WriteMessage("\n提示：" + string.Join("；", view.Warnings.ToArray()));

            // 落完图把这一条的"待落图"标记去掉（建模程序推过来的那几张，落一张少一张）
            if (!string.IsNullOrWhiteSpace(placedViewId))
            {
                try
                {
                    var project = new PublishPlanStore().GetActiveProject();
                    var modelFolder = StudioLaunch.FindModelFolder(project == null ? null : project.ProjectFolder,
                        project == null ? null : project.Name);
                    var pending = StudioLaunch.ReadPending(modelFolder);
                    if (pending != null && pending.Entries.Any(entry => entry != null
                        && string.Equals(entry.Id, placedViewId, StringComparison.OrdinalIgnoreCase)))
                    {
                        StudioLaunch.RemovePending(modelFolder, placedViewId);
                        var left = StudioLaunch.ReadPending(modelFolder);
                        var remaining = left == null ? 0 : left.Entries.Count;
                        editor.WriteMessage(remaining > 0
                            ? "\n（还有 " + remaining + " 张待落图：再执行 LTTZ 即可，带 ★ 的就是）\n"
                            : "\n（建模程序推过来的图纸已全部落图）\n");
                    }
                }
                catch
                {
                    // 清标记失败不影响已经落好的图
                }
            }
            return true;
        }

        private static double[] BatchViewBounds(ViewDocument view)
        {
            var points = new List<Point2d>();
            foreach (var line in view.Lines ?? new List<ViewLine>())
            { points.Add(new Point2d(line.X1, line.Y1)); points.Add(new Point2d(line.X2, line.Y2));
                if(Math.Abs(line.Bulge)>1e-10)foreach(var p in CurveGeometry.ForLine(line).BoundsPoints())points.Add(new Point2d(p.X,p.Y)); }
            foreach (var circle in view.Circles ?? new List<ViewCircle>())
            {
                var r = Math.Abs(circle.Radius);
                points.Add(new Point2d(circle.X - r, circle.Y - r));
                points.Add(new Point2d(circle.X + r, circle.Y + r));
            }
            foreach (var hatch in view.Hatches ?? new List<ViewHatch>())
                foreach (var p in hatch.Boundary ?? new List<PointModel>()) points.Add(new Point2d(p.X, p.Y));
            foreach (var text in view.Texts ?? new List<ViewText>())
            {
                var angle = text.Rotation*Math.PI/180;
                var width = Math.Max(1,(text.Text ?? "").Length)*text.Height;
                foreach(var p in new[] {new Point2d(0,0),new Point2d(width,0),new Point2d(0,text.Height),new Point2d(width,text.Height)})
                    points.Add(new Point2d(text.X+p.X*Math.Cos(angle)-p.Y*Math.Sin(angle),text.Y+p.X*Math.Sin(angle)+p.Y*Math.Cos(angle)));
            }
            foreach (var d in view.Dimensions ?? new List<ViewDimension>())
            {
                if (d.TextX.HasValue && d.TextY.HasValue)
                {
                    var pad = Math.Max(1,view.Scale)*4;
                    points.Add(new Point2d(d.TextX.Value-pad,d.TextY.Value-pad));
                    points.Add(new Point2d(d.TextX.Value+pad,d.TextY.Value+pad));
                }
                points.Add(d.Vertical ? new Point2d(d.LinePosition, d.From) : new Point2d(d.From, d.LinePosition));
                points.Add(d.Vertical ? new Point2d(d.AnchorPosition, d.To) : new Point2d(d.To, d.AnchorPosition));
            }
            if (view.Kind == ViewKind.Sheet)
            {
                points.Add(Point2d.Origin);
                points.Add(new Point2d(view.PaperWidth * Math.Max(1, view.Scale),
                    view.PaperHeight * Math.Max(1, view.Scale)));
            }
            return points.Count == 0 ? new[] { 0d, 0d, 1000d, 1000d }
                : new[] { points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y) };
        }

        private sealed class PlacedView
        {
            public string SourceKey;
            public string Id;
            public Point3d Anchor;
            public int EntityCount;
        }

        private static int? PickPlacement(IReadOnlyList<PlacedView> placements, Editor editor)
        {
            if (!IsHeadlessHost)
            {
                var choices = placements.Select((placement, index) => new BuildingModelChoice
                {
                    Value = index + 1,
                    Title = "更新已有位置 " + (index + 1),
                    Subtitle = "插入点 (" + placement.Anchor.X.ToString("0.##") + ", "
                        + placement.Anchor.Y.ToString("0.##") + ") · " + placement.EntityCount + " 个生成实体",
                    Badge = "更新"
                }).ToList();
                choices.Add(new BuildingModelChoice
                {
                    Value = 0, Title = "另插一份", Subtitle = "保留已有位置，在图上指定新的插入点", Badge = "新增"
                });
                var dialog = new BuildingModelChoiceWindow("选择落图方式",
                    "此视图已放入当前图纸。更新会替换所选位置的生成图元，保留手工补绘。",
                    choices, false, "继续");
                return AcApplication.ShowModalWindow(dialog) == true ? dialog.SelectedValue : null;
            }

            editor.WriteMessage("\n此视图在当前 DWG 中已有 " + placements.Count + " 处登记放置：");
            for (var index = 0; index < placements.Count; index++)
                editor.WriteMessage("\n  " + (index + 1) + "）更新位置 (" + placements[index].Anchor.X.ToString("0.##")
                    + ", " + placements[index].Anchor.Y.ToString("0.##") + ")，" + placements[index].EntityCount + " 个生成实体");
            editor.WriteMessage("\n  0）另插一份");
            var result = editor.GetInteger(new PromptIntegerOptions("\n输入序号")
            {
                DefaultValue = 1, AllowNone = false, AllowZero = true, UseDefaultValue = true
            });
            if (result.Status != PromptStatus.OK) return null;
            if (result.Value >= 0 && result.Value <= placements.Count) return result.Value;
            editor.WriteMessage("\n序号无效，未修改图纸。");
            return null;
        }

        private static string ViewSourceKey(string path, string viewId)
        {
            // A path scopes identical view IDs from different models. The digest fits
            // XData's per-string limit and does not expose project paths in the DWG.
            var source = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()
                + "\n" + viewId.Trim().ToUpperInvariant();
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(source))).Replace("-", "");
        }

        private static HashSet<string> FindPlacedSourceKeys(Database database)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                    var tag = ReadPlacement(entity);
                    if (tag != null) keys.Add(tag.SourceKey);
                }
            }
            return keys;
        }

        private static ViewDocument ComposeSingleViewSheet(ViewDocument source, FrameDefinition frame)
        {
            var paper = frame == null || string.IsNullOrWhiteSpace(frame.PaperSize) ? "A3" : frame.PaperSize;
            var orientation = frame == null || string.IsNullOrWhiteSpace(frame.PaperOrientation)
                ? "横向" : frame.PaperOrientation;
            var size = frame == null ? new[] { 420d, 297d }
                : PaperSizeCatalog.GetSize(paper, frame.Extension, orientation);
            var sheet = new SheetDefinitionModel
            {
                Id = "placed-" + source.Id,
                Number = source.Id,
                Title = source.Title,
                Paper = paper,
                PaperWidth = size[0],
                PaperHeight = size[1],
                Landscape = size[0] >= size[1],
                FrameTemplate = frame == null ? null : frame.BlockName,
                ViewIds = new List<string> { source.Id }
            };
            return SheetComposer.ComposeModelSpace(new[] { source }, sheet);
        }

        private static bool FrameMatches(ViewDocument view, FrameDefinition frame)
        {
            if (frame == null) return false;
            if (!string.IsNullOrWhiteSpace(view.PaperName)
                && !string.Equals(view.PaperName.Trim(), (frame.PaperSize ?? string.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase)) return false;
            if (view.PaperWidth <= 0d || view.PaperHeight <= 0d) return true;
            var orientation = string.IsNullOrWhiteSpace(frame.PaperOrientation)
                ? PaperSizeCatalog.DefaultOrientation(frame.PaperSize) : frame.PaperOrientation;
            var size = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, orientation);
            return Math.Abs(view.PaperWidth - size[0]) < 2d && Math.Abs(view.PaperHeight - size[1]) < 2d;
        }

        private static List<PlacedView> FindPlacements(Database database, string sourceKey)
        {
            var found = new Dictionary<string, PlacedView>(StringComparer.Ordinal);
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                    var tag = ReadPlacement(entity);
                    if (tag == null || tag.SourceKey != sourceKey) continue;
                    PlacedView placed;
                    if (!found.TryGetValue(tag.Id, out placed))
                    {
                        placed = tag;
                        found.Add(tag.Id, placed);
                    }
                    placed.EntityCount++;
                }
            }
            return found.Values.OrderBy(p => p.Anchor.X).ThenBy(p => p.Anchor.Y).ToList();
        }

        private static PlacedView ReadPlacement(Entity entity)
        {
            if (entity == null || entity.IsErased) return null;
            try
            {
                using (var data = entity.GetXDataForApplication(PlacementRegApp))
                {
                    if (data == null) return null;
                    var values = data.AsArray();
                    if (values.Length != 6 || values[0].Value as string != PlacementRegApp) return null;
                    var sourceKey = values[1].Value as string;
                    var id = values[2].Value as string;
                    if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(id)) return null;
                    return new PlacedView
                    {
                        SourceKey = sourceKey, Id = id,
                        Anchor = new Point3d(Convert.ToDouble(values[3].Value), Convert.ToDouble(values[4].Value),
                            Convert.ToDouble(values[5].Value))
                    };
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
            catch (FormatException) { return null; }
            catch (InvalidCastException) { return null; }
        }

        private static void EnsurePlacementRegApp(Database database, Transaction transaction)
        {
            var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
            if (table.Has(PlacementRegApp)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = PlacementRegApp };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void TagPlacement(Entity entity, string sourceKey, string placementId, Point3d anchor)
        {
            using (var data = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, PlacementRegApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sourceKey),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, placementId),
                new TypedValue((int)DxfCode.ExtendedDataReal, anchor.X),
                new TypedValue((int)DxfCode.ExtendedDataReal, anchor.Y),
                new TypedValue((int)DxfCode.ExtendedDataReal, anchor.Z)))
                entity.XData = data;
        }

        public static void ExportDrawing(Document document)
        {
            if (document == null) return;
            var editor = document.Editor;
            string path;
            using (var dialog = new SaveFileDialog
            {
                Title = "导出给建模程序的中间文件",
                Filter = "提取文件 (*.json)|*.json",
                FileName = "import.json",
                InitialDirectory = LastViewFolder()
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                path = dialog.FileName;
            }

            var payload = new DrawingImportDocument
            {
                DrawingName = Path.GetFileName(document.Name ?? string.Empty),
                DrawingPath = document.Name,
                ExtractedAt = DateTime.Now.ToString("O")
            };

            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var record in (LayerTable)transaction.GetObject(document.Database.LayerTableId, OpenMode.ForRead))
                {
                    var layer = (LayerTableRecord)transaction.GetObject(record, OpenMode.ForRead);
                    payload.Layers.Add(new LayerInfoModel
                    {
                        Name = layer.Name,
                        Color = (short)layer.Color.ColorIndex,
                        LineType = LinetypeName(transaction, layer.LinetypeObjectId)
                    });
                }

                var space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead);
                foreach (var id in space)
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null) continue;
                    var model = Describe(entity, transaction);
                    if (model != null) payload.Entities.Add(model);
                }
                transaction.Commit();
            }

            try
            {
                BuildingModelJson.SaveImport(path, payload);
                editor.WriteMessage("\n提取完成：" + payload.Entities.Count + " 个图元、"
                    + payload.Layers.Count + " 个图层 → " + path);
                editor.WriteMessage("\n在建模程序里用这个文件做识别（P3 之前仅用于核对数据）。");
            }
            catch (Exception exception)
            {
                editor.WriteMessage("\n提取文件写入失败：" + exception.Message);
            }
            SaveLastViewFolder(Path.GetDirectoryName(path));
        }

        private static DrawingEntityModel Describe(Entity entity, Transaction transaction)
        {
            var model = new DrawingEntityModel { Layer = entity.Layer, Handle = entity.Handle.ToString() };
            var line = entity as Line;
            if (line != null)
            {
                model.Type = "LINE";
                model.Points.Add(new PointModel(line.StartPoint.X, line.StartPoint.Y));
                model.Points.Add(new PointModel(line.EndPoint.X, line.EndPoint.Y));
                return model;
            }
            var polyline = entity as Polyline;
            if (polyline != null)
            {
                model.Type = "POLYLINE";
                for (var index = 0; index < polyline.NumberOfVertices; index++)
                {
                    var point = polyline.GetPoint2dAt(index);
                    model.Points.Add(new PointModel(point.X, point.Y));
                }
                model.X = polyline.Elevation;
                return model;
            }
            var lightweight = entity as Polyline2d;
            if (lightweight != null)
            {
                model.Type = "POLYLINE2D";
                foreach (ObjectId vertexId in lightweight)
                {
                    var vertex = transaction.GetObject(vertexId, OpenMode.ForRead, false) as Vertex2d;
                    if (vertex == null) continue;
                    model.Points.Add(new PointModel(vertex.Position.X, vertex.Position.Y));
                }
                return model;
            }
            var circle = entity as Circle;
            if (circle != null)
            {
                model.Type = "CIRCLE";
                model.X = circle.Center.X;
                model.Y = circle.Center.Y;
                model.Radius = circle.Radius;
                return model;
            }
            var arc = entity as Arc;
            if (arc != null)
            {
                model.Type = "ARC";
                model.X = arc.Center.X;
                model.Y = arc.Center.Y;
                model.Radius = arc.Radius;
                return model;
            }
            var block = entity as BlockReference;
            if (block != null)
            {
                model.Type = "INSERT";
                model.X = block.Position.X;
                model.Y = block.Position.Y;
                model.Rotation = block.Rotation;
                model.Scale = block.ScaleFactors.X;
                try
                {
                    var record = (BlockTableRecord)transaction.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                    model.BlockName = record.Name;
                }
                catch { model.BlockName = string.Empty; }
                return model;
            }
            var text = entity as DBText;
            if (text != null)
            {
                model.Type = "TEXT";
                model.Text = text.TextString;
                model.X = text.Position.X;
                model.Y = text.Position.Y;
                model.TextHeight = text.Height;
                return model;
            }
            var mtext = entity as MText;
            if (mtext != null)
            {
                model.Type = "MTEXT";
                model.Text = mtext.Contents;
                model.X = mtext.Location.X;
                model.Y = mtext.Location.Y;
                model.TextHeight = mtext.TextHeight;
                return model;
            }
            return null;
        }

        private static string LinetypeName(Transaction transaction, ObjectId id)
        {
            try
            {
                if (id.IsNull) return string.Empty;
                var record = (LinetypeTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                return record.Name;
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// 导出门窗类型库（命令 <c>TQLX</c>）：把**插件里已有的**门窗参数与立面模板写成
        /// <c>openings.json</c>，让建模程序放门窗时直接选类型，而不是重新录一遍。
        ///
        /// 参数来源：
        /// - 洞口尺寸与做法：当前项目的 `DoorWindowElevationPreference`（门窗立面的"按工程记忆"）；
        /// - 做法模板：`DoorWindowElevationTemplate`（普通双扇推拉窗之类）。
        /// </summary>
        public static void ExportOpeningLibrary(Document document)
        {
            if (document == null) return;
            var editor = document.Editor;

            string projectName;
            List<DoorWindowElevationPreference> preferences;
            List<DoorWindowElevationTemplate> templates;
            try
            {
                var project = new PublishPlanStore().GetActiveProject();
                projectName = project == null ? "默认项目" : project.Name;
                preferences = new DoorWindowElevationStore().LoadForActiveProject();
                templates = new DoorWindowElevationTemplateStore().Load();
            }
            catch (Exception exception)
            {
                editor.WriteMessage("\n读取门窗参数失败：" + exception.Message);
                return;
            }

            var library = new OpeningTypeLibraryDocument
            {
                ProjectName = projectName,
                ExportedAt = DateTime.Now.ToString("O")
            };
            foreach (var template in templates ?? new List<DoorWindowElevationTemplate>())
            {
                if (template == null || string.IsNullOrWhiteSpace(template.Name)) continue;
                library.Templates.Add(new OpeningTemplateModel
                {
                    Name = template.Name,
                    ElevationType = template.ElevationType,
                    DivisionPreset = template.DivisionPreset,
                    OpeningMode = template.OpeningMode
                });
            }

            // 同一编号可能有多个尺寸（改过洞口尺寸的历史记录）：保留最后处理到的那条
            var byCode = new Dictionary<string, OpeningTypeModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var preference in preferences ?? new List<DoorWindowElevationPreference>())
            {
                if (preference == null || string.IsNullOrWhiteSpace(preference.Code)) continue;
                var code = preference.Code.Trim();
                byCode[code] = OpeningType(preference, projectName);
            }
            library.Types.AddRange(byCode.Values.OrderBy(t => t.Code, StringComparer.OrdinalIgnoreCase));

            string path;
            using (var dialog = new SaveFileDialog
            {
                Title = "导出门窗类型库（建模程序读取它来放门窗）",
                Filter = "门窗类型库 (*.json)|*.json",
                FileName = "openings.json",
                InitialDirectory = LastViewFolder()
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                path = dialog.FileName;
            }

            try
            {
                BuildingModelJson.SaveOpeningLibrary(path, library);
                editor.WriteMessage("\n门窗类型库已导出：" + library.Types.Count + " 个类型、"
                    + library.Templates.Count + " 个做法模板 → " + path);
                if (library.Types.Count == 0)
                    editor.WriteMessage("\n提示：当前项目还没有门窗参数。可先在“门窗立面（MCLM）”里录一次，"
                        + "再导出；也可以先导出空库供程序使用。");
            }
            catch (Exception exception)
            {
                editor.WriteMessage("\n门窗类型库写入失败：" + exception.Message);
            }
            SaveLastViewFolder(Path.GetDirectoryName(path));
        }

        // Preserve every history record for registration. Duplicate codes require
        // an explicit choice; the older export command retains its existing policy.
        internal static OpeningTypeLibraryDocument RegistrationOpeningLibrary()
        {
            var project = new PublishPlanStore().GetActiveProject();
            var library = new OpeningTypeLibraryDocument { ProjectName = project == null ? "默认项目" : project.Name,
                ExportedAt = DateTimeOffset.Now.ToString("O") };
            foreach (var preference in new DoorWindowElevationStore().LoadForActiveProject())
                if (preference != null && !string.IsNullOrWhiteSpace(preference.Code))
                    library.Types.Add(OpeningType(preference, library.ProjectName));
            return library;
        }

        internal static List<CadOpeningSillEntry> RegistrationSills()
        {
            var store = new DoorWindowElevationStore();
            var entries = new List<CadOpeningSillEntry>();
            var session = store.LoadSession();
            if (session != null)
            {
                AddScheduleSills(entries, session.BaseItems, null, "已保存门窗表");
                foreach (var floor in session.FloorSources ?? new List<DoorWindowFloorSourcePreference>())
                    if (floor != null) AddScheduleSills(entries, floor.Items, floor.FloorName, "门窗表 · " + floor.FloorName);
            }
            var preferences = store.LoadForActiveProject().Where(p => p != null && !string.IsNullOrWhiteSpace(p.Code)).ToList();
            for (var i = 0; i < preferences.Count; i++)
            {
                var p = preferences[i];
                entries.Add(new CadOpeningSillEntry { Code = p.Code, TypeIndex = i,
                    Height = p.HasSillHeight && !p.SillHeightSuppressed ? (double?)p.SillHeight : null,
                    Source = "所选门窗立面已保存参数" });
            }
            return entries;
        }

        private static void AddScheduleSills(List<CadOpeningSillEntry> entries,
            IEnumerable<DoorWindowScheduleItem> items, string floor, string source)
        {
            foreach (var item in items ?? Enumerable.Empty<DoorWindowScheduleItem>())
                if (item != null && !string.IsNullOrWhiteSpace(item.Code))
                    entries.Add(new CadOpeningSillEntry { Code = item.Code, FloorName = floor, FromSchedule = true,
                        Height = item.SillHeightSuppressed ? null : (double?)item.SillHeight, Source = source });
        }

        private static OpeningTypeModel OpeningType(DoorWindowElevationPreference preference, string projectName)
        {
            var code = preference.Code.Trim();
            var kind = KindOf(preference, code);
            return new OpeningTypeModel
                {
                    Code = code,
                    Kind = kind,
                    Width = preference.Width > 0.5d ? preference.Width : DefaultWidth(kind),
                    Height = preference.Height > 0.5d ? preference.Height : DefaultHeight(kind),
                    Sill = preference.HasSillHeight && !preference.SillHeightSuppressed ? preference.SillHeight : DefaultSill(kind),
                    ElevationType = preference.ElevationType,
                    DivisionPreset = preference.DivisionPreset,
                    OpeningMode = preference.OpeningMode,
                    HasOuterFrame = preference.HasOuterFrame,
                    OuterFrameWidth = preference.OuterFrameWidth,
                    HasMullion = preference.HasMullion,
                    MullionWidth = preference.MullionWidth,
                    HasInstallationGap = preference.HasInstallationGap,
                    InstallationGap = preference.InstallationGap,
                    DoorFrameType = preference.DoorFrameType,
                    DoorFrameWidth = preference.DoorFrameWidth,
                    CustomColumnRatios = preference.CustomColumnRatios,
                    CustomRowRatios = preference.CustomRowRatios,
                    CustomColumnWidths = preference.CustomColumnWidths,
                    CustomRowHeights = preference.CustomRowHeights,
                    CustomCellLayout = preference.CustomCellLayout,
                    CellOpeningModes = preference.CellOpeningModes,
                    DoorPlacement = preference.DoorPlacement,
                    DoorEdgeDistance = preference.DoorEdgeDistance,
                    BayLeftSide = preference.BayLeftSide,
                    BayRightSide = preference.BayRightSide,
                    BayLeftDepth = preference.BayLeftDepth,
                    BayRightDepth = preference.BayRightDepth,
                    BayLeftCellLayout = preference.BayLeftCellLayout,
                    BayRightCellLayout = preference.BayRightCellLayout,
                    Material = preference.Material,
                    AtlasName = preference.AtlasName,
                    Remarks = preference.Remarks,
                    Source = "项目参数：" + projectName
                };
        }

        private static string KindOf(DoorWindowElevationPreference preference, string code)
        {
            var type = (preference.ElevationType ?? string.Empty).Trim();
            if (type.Length > 0) return type;
            if (!string.IsNullOrWhiteSpace(preference.DoorFrameType)) return "门";
            if (code.StartsWith("M", StringComparison.OrdinalIgnoreCase)) return "门";
            if (code.StartsWith("C", StringComparison.OrdinalIgnoreCase)) return "窗";
            return "窗";
        }

        private static double DefaultWidth(string kind)
        {
            return string.Equals(kind, "门", StringComparison.OrdinalIgnoreCase) ? 900d : 1500d;
        }

        private static double DefaultHeight(string kind)
        {
            return string.Equals(kind, "门", StringComparison.OrdinalIgnoreCase) ? 2100d : 1800d;
        }

        private static double DefaultSill(string kind)
        {
            return string.Equals(kind, "门", StringComparison.OrdinalIgnoreCase) ? 0d : 900d;
        }

        /// <summary>
        /// 设置填充图案。
        /// 优先用"用户定义 45° 细线 + 模型单位间距"——图纸上看起来就是常规的剖面斜线填充，
        /// 而且间距是我们自己给的毫米值，不依赖图案自身的基准间距；失败则退到预定义图案，最后退到实心。
        /// </summary>
        private static void ApplyHatchPattern(Hatch entity, ViewHatch hatch, Editor editor)
        {
            var pattern = string.IsNullOrWhiteSpace(hatch.Pattern) ? "ANSI31" : hatch.Pattern;
            if (hatch.Spacing > 0.01d)
            {
                try
                {
                    entity.SetHatchPattern(HatchPatternType.UserDefined, pattern);
                    entity.PatternAngle = hatch.Angle != 0d ? hatch.Angle * Math.PI / 180d : Math.PI / 4d;
                    entity.PatternSpace = hatch.Spacing;
                    entity.PatternDouble = false;
                    entity.PatternScale = 1d;
                    return;
                }
                catch (Exception exception)
                {
                    editor.WriteMessage("\n提示：用户定义填充不可用，改用预定义图案：" + exception.Message);
                }
            }
            try
            {
                entity.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                // ANSI31 的基准间距约 0.125 图形单位，按目标间距反算比例
                entity.PatternScale = hatch.Scale > 0.01d
                    ? hatch.Scale
                    : Math.Max(1d, hatch.Spacing > 0.01d ? hatch.Spacing / 0.125d : 1d);
                entity.PatternAngle = hatch.Angle * Math.PI / 180d;
            }
            catch
            {
                entity.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            }
        }

        private static Entity CreateViewLineEntity(ViewLine line, Point3d anchor)
        {
            if(Math.Abs(line.Bulge)>1e-10) {
                var arc=new Polyline();
                arc.AddVertexAt(0,new Point2d(anchor.X+line.X1,anchor.Y+line.Y1),line.Bulge,0,0);
                arc.AddVertexAt(1,new Point2d(anchor.X+line.X2,anchor.Y+line.Y2),0,0,0);
                return arc;
            }
            return new Line(new Point3d(anchor.X+line.X1,anchor.Y+line.Y1,0),
                new Point3d(anchor.X+line.X2,anchor.Y+line.Y2,0));
        }

        internal sealed class OpeningPathChain
        {
            public ViewLine Appearance;
            public readonly List<Point2d> Points=new List<Point2d>();
            public readonly List<double> Bulges=new List<double>();
        }

        internal static List<OpeningPathChain> BuildOpeningPathChains(IEnumerable<ViewLine> source)
        {
            var result=new List<OpeningPathChain>();
            foreach(var group in source.Where(l=>l!=null&&l.OpeningArcId!=null)
                .GroupBy(l=>new {l.OpeningArcId,l.Layer,l.LineType,l.LineWeight,l.OpeningRole,l.OpeningUsesWindowAppearance}))
            {
                var edges=group.Where(l=>l.X1!=l.X2||l.Y1!=l.Y2).ToArray();
                var used=new bool[edges.Length];
                var nodes=new Dictionary<Tuple<double,double>,List<int>>();
                Tuple<double,double> Key(double x,double y)=>Tuple.Create(x,y);
                void Add(Tuple<double,double> key,int index)
                {
                    if(!nodes.TryGetValue(key,out var incident))nodes[key]=incident=new List<int>();
                    incident.Add(index);
                }
                for(var i=0;i<edges.Length;i++)
                {
                    Add(Key(edges[i].X1,edges[i].Y1),i);Add(Key(edges[i].X2,edges[i].Y2),i);
                }
                void Trace(int index,Tuple<double,double> start)
                {
                    var chain=new OpeningPathChain {Appearance=edges[index]};
                    chain.Points.Add(new Point2d(start.Item1,start.Item2));
                    var current=start;
                    while(!used[index])
                    {
                        var edge=edges[index];used[index]=true;
                        var reverse=Key(edge.X2,edge.Y2).Equals(current);
                        current=reverse?Key(edge.X1,edge.Y1):Key(edge.X2,edge.Y2);
                        chain.Bulges.Add(reverse?-edge.Bulge:edge.Bulge);
                        chain.Points.Add(new Point2d(current.Item1,current.Item2));
                        var incident=nodes[current];
                        if(current.Equals(start)||incident.Count!=2)break;
                        index=-1;
                        foreach(var candidate in incident)if(!used[candidate]){index=candidate;break;}
                        if(index<0)break;
                    }
                    result.Add(chain);
                }
                // Exact endpoint keys preserve even tiny clipping gaps; branches stop a chain.
                foreach(var node in nodes.Where(n=>n.Value.Count!=2))
                    foreach(var index in node.Value)if(!used[index])Trace(index,node.Key);
                for(var i=0;i<edges.Length;i++)if(!used[i])Trace(i,Key(edges[i].X1,edges[i].Y1));
            }
            return result;
        }

        internal static Polyline CreateOpeningPathPolyline(OpeningPathChain chain,Point3d anchor)
        {
            var entity=new Polyline(chain.Points.Count) {Closed=false,Plinegen=true,Elevation=anchor.Z};
            for(var i=0;i<chain.Points.Count;i++)
                entity.AddVertexAt(i,new Point2d(anchor.X+chain.Points[i].X,anchor.Y+chain.Points[i].Y),
                    i<chain.Bulges.Count?chain.Bulges[i]:0,0,0);
            return entity;
        }

        private static void ApplyOpeningPathAppearance(Polyline entity,ViewLine line,Dictionary<string,ObjectId> layers,
            Database database,Transaction transaction,int scale)
        {
            ApplyViewLineAppearance(entity,line,layers);
            if(line.OpeningRole!=null)return;
            entity.Color=Autodesk.AutoCAD.Colors.Color.FromRgb(145,160,171);
            entity.LinetypeId=EnsureOpeningArcLineType(database,transaction);
            entity.LinetypeScale=Math.Max(1,scale);
        }

        public static void RunCurveNativeCheck(Document document)
        {
            var dbmod=Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"));
            var model=new BuildingModelDocument { Name="Curve QA" };
            model.Storeys.Add(new StoreyModel { Id="1F",Height=3000 });
            model.Walls.Add(new WallModel { Id="arc",StoreyId="1F",X1=1000,X2=-1000,Bulge=1,Thickness=200 });
            model.Slabs.Add(new SlabModel { Id="round",StoreyId="1F",Thickness=150,TopElevation=150,
                Outline=new List<PointModel> { new PointModel(1500,-2000) { Bulge=1 },
                    new PointModel(-1500,-2000),new PointModel(-1500,-3000),new PointModel(1500,-3000) } });
            var view=OrthographicProjector.Project(model,new ViewDefinitionModel { Id="qa",Kind=ViewKind.Plan,
                Scale=100,StoreyIds=new List<string>{"1F"} });
            var curves=view.Lines.Where(l=>Math.Abs(l.Bulge)>1e-10).ToList();
            if(curves.Count!=3)throw new InvalidDataException("墙和板未生成三个独立圆弧。");
            using(var db=new Database(true,true))
            using(var tx=db.TransactionManager.StartTransaction()) {
                var layers=EnsureViewLayers(db,tx);
                var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
                var anchor=new Point3d(12345,6789,0);
                foreach(var line in curves) {
                    var native=(Polyline)CreateViewLineEntity(line,anchor);
                    ApplyLayer(native,line.Layer,layers);ApplyLineWeight(native,line.LineWeight);
                    space.AppendEntity(native);tx.AddNewlyCreatedDBObject(native,true);
                    var expected=CurveGeometry.ForLine(line);var actual=native.GetArcSegment2dAt(0);
                    if(native.NumberOfVertices!=2||Math.Abs(actual.Radius-expected.Radius)>.000001
                        || Math.Abs(native.Length-expected.Length)>.000001
                        || native.GetPoint2dAt(0).GetDistanceTo(new Point2d(anchor.X+line.X1,anchor.Y+line.Y1))>.000001
                        || native.LayerId!=layers[line.Layer])
                        throw new InvalidDataException("CAD 圆弧落图尺寸、基点或规范图层错误。");
                }
                tx.Commit();
            }
            if(Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"))!=dbmod)
                throw new InvalidDataException("圆弧检查修改了当前图纸。");
            document.Editor.WriteMessage("\nCAD_CURVE_NATIVE_OK wall slab nativeBulge radius arcLength 1to1 translatedAnchor standardLayers unchangedDrawing");
        }

        public static void RunActiveViewsNativeCheck(Document document)
        {
            var dbmod=Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"));
            var root=Path.Combine(Path.GetTempPath(),"WanLuoActiveViews-"+Guid.NewGuid().ToString("N"));
            try
            {
                foreach(var name in new[]{"Old","Edited"})
                {
                    var folder=Path.Combine(root,StudioLaunch.ModelFolderName,name);
                    BuildingModelJson.SaveModel(Path.Combine(folder,"model.json"),new BuildingModelDocument { Name=name });
                    var view=new ViewDocument { Id="same-id",Title=name,Kind=ViewKind.Plan };
                    view.Lines.Add(new ViewLine { X2=name=="Old"?1000:7600,Layer=ViewLayers.Cut });
                    BuildingModelJson.SaveView(Path.Combine(folder,StudioLaunch.ViewsFolderName,"same-id.json"),view);
                }
                var modelPath=Path.Combine(root,StudioLaunch.ModelFolderName,"Edited","model.json");
                StudioLaunch.RememberActiveModel(root,modelPath);
                var source=StudioLaunch.ActiveViewSource(root,"Old");
                var entry=source.Entries.Single();
                var oldView=Path.Combine(root,StudioLaunch.ModelFolderName,"Old",StudioLaunch.ViewsFolderName,"same-id.json");
                if(source.ModelPath!=modelPath||ViewSourceKey(entry.FilePath,entry.Id)==ViewSourceKey(oldView,entry.Id))
                    throw new InvalidDataException("当前模型来源或同 ID 隔离错误。");
                using(var db=new Database(true,true))
                using(var tx=db.TransactionManager.StartTransaction())
                {
                    var layers=EnsureViewLayers(db,tx);
                    var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
                    var view=BuildingModelJson.LoadView(entry.FilePath);
                    var native=(Line)CreateViewLineEntity(view.Lines.Single(),new Point3d(12345,6789,0));
                    ApplyLayer(native,ViewLayers.Cut,layers);
                    space.AppendEntity(native);tx.AddNewlyCreatedDBObject(native,true);
                    if(Math.Abs(native.Length-7600)>1e-6||Math.Abs(native.StartPoint.X-12345)>1e-6)
                        throw new InvalidDataException("落图读到了旧模型的几何。");
                    tx.Commit();
                }
                StudioLaunch.RememberActiveModel(root,Path.Combine(root,"missing.json"));
                try { StudioLaunch.ActiveViewSource(root,"Old");throw new InvalidOperationException("失效当前模型回退旧模型。"); }
                catch(FileNotFoundException) { }
                if(Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"))!=dbmod)
                    throw new InvalidDataException("当前模型落图检查修改了图纸。");
                document.Editor.WriteMessage("\nCAD_ACTIVE_VIEWS_NATIVE_OK currentModel sameIdIsolation actualGeometry 1to1 invalidSourceNoFallback unchangedDrawing");
            }
            finally { if(Directory.Exists(root))Directory.Delete(root,true); }
        }

        private static Dictionary<string, ObjectId> EnsureViewLayers(Database database, Transaction transaction)
        {
            var profile = DraftingStandardService.LoadProfile();
            var layers = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            layers.Add("Hidden", DraftingStandardService.EnsureLayerFor(database, transaction,
                DraftingStandardProfile.HiddenKey, profile));
            foreach(var key in new[]{OpeningElevationAdapter.WindowLayerKey,OpeningElevationAdapter.DoorLayerKey,OpeningElevationAdapter.MarkLayerKey})
                layers.Add(key,DraftingStandardService.EnsureLayerFor(database,transaction,key,profile));
            foreach (var style in ViewLayers.All)
            {
                var key = style.Name == ViewLayers.SheetFrame ? DraftingStandardProfile.FrameKey
                    : style.Name == ViewLayers.Cut ? DraftingStandardProfile.StructureKey
                    : style.Name == ViewLayers.Opening ? DraftingStandardProfile.FineKey
                    : style.Name == ViewLayers.CutHatch ? DraftingStandardProfile.HatchKey
                    : style.Name == ViewLayers.Dimension ? DraftingStandardProfile.AnnotationDimensionLayerKey
                    : style.Name == ViewLayers.Axis ? DraftingStandardProfile.StairAxisLayerKey
                    : style.Name == ViewLayers.Stair ? DraftingStandardProfile.StairOutlineLayerKey
                    : style.Name == ViewLayers.Title || style.Name == ViewLayers.LevelText
                        || style.Name == ViewLayers.Room ? DraftingStandardProfile.AnnotationTextLayerKey
                    : style.Name == ViewLayers.Ground || style.Name == ViewLayers.Schedule
                        ? DraftingStandardProfile.FineKey : DraftingStandardProfile.OutlineKey;
                layers.Add(style.Name, DraftingStandardService.EnsureLayerFor(database, transaction, key, profile));
            }
            return layers;
        }

        private static BuildingModelBatchWindow PickViewBatch(Document document)
        {
            string projectFolder = null;
            string projectName = null;
            try
            {
                var project = new PublishPlanStore().GetActiveProject();
                projectFolder = project == null ? null : project.ProjectFolder;
                projectName = project == null ? null : project.Name;
            }
            catch (Exception exception)
            {
                document.Editor.WriteMessage("\n读取当前项目失败：" + exception.Message);
            }
            StudioViewSource source;
            try { source = StudioLaunch.ActiveViewSource(projectFolder, projectName,document.Database.FingerprintGuid.ToString()); }
            catch (Exception exception)
            {
                document.Editor.WriteMessage("\n读取当前模型视图失败：" + exception.Message);
                return null;
            }
            var entries = source.Entries;
            try
            {
                var placed = FindPlacedSourceKeys(document.Database);
                foreach (var entry in entries)
                    entry.Placed = placed.Contains(ViewSourceKey(entry.FilePath, entry.Id));
            }
            catch (Exception exception)
            {
                document.Editor.WriteMessage("\n读取已落图状态失败：" + exception.Message);
            }
            List<FrameDefinition> frames;
            try { frames = new PublishPlanStore().LoadFrames(); }
            catch (Exception exception)
            {
                document.Editor.WriteMessage("\n读取项目图框失败：" + exception.Message);
                frames = new List<FrameDefinition>();
            }
            var dialog = new BuildingModelBatchWindow(projectName, source, frames, () => {
                var refreshed=StudioLaunch.ActiveViewSource(projectFolder, projectName,document.Database.FingerprintGuid.ToString());
                var placed=FindPlacedSourceKeys(document.Database);
                foreach(var entry in refreshed.Entries)entry.Placed=placed.Contains(ViewSourceKey(entry.FilePath,entry.Id));
                return refreshed;
            });
            return AcApplication.ShowModalWindow(dialog) == true ? dialog : null;
        }

        /// <summary>无界面的 core console 保留序号输入，供自动验收。</summary>
        private static string PickViewFile(Document document, Editor editor)
        {
            string projectFolder = null;
            string modelName = null;
            try
            {
                var project = new PublishPlanStore().GetActiveProject();
                projectFolder = project == null ? null : project.ProjectFolder;
                modelName = project == null ? null : project.Name;
            }
            catch
            {
                // 读项目失败就当没有项目，直接走文件浏览
            }
            StudioViewSource source;
            try { source = StudioLaunch.ActiveViewSource(projectFolder, modelName,document.Database.FingerprintGuid.ToString()); }
            catch (Exception exception)
            {
                editor.WriteMessage("\n读取当前模型视图失败：" + exception.Message);
                return null;
            }
            var modelFolder = source.ModelPath == null ? null : Path.GetDirectoryName(source.ModelPath);
            var entries = source.Entries;
            if (entries.Count == 0)
            {
                if (modelFolder != null)
                    editor.WriteMessage("\n当前模型的目录里还没有视图：" + modelFolder
                        + "\n先在建模程序里「生成全部视图」（CAD 里执行 JZMX 打开建模程序）。");
                return null;
            }

            editor.WriteMessage("\n请选择要落图的图纸/视图（当前模型：" + source.ModelName + "）：\n" + source.ModelPath);
            for (var index = 0; index < entries.Count; index++)
                editor.WriteMessage("\n  " + (index + 1).ToString("00") + "）" + entries[index].Display);
            editor.WriteMessage("\n  00）浏览其它文件…");
            var pendingCount = entries.Count(entry => entry.Pending);
            if (pendingCount > 0)
                editor.WriteMessage("\n（★ = 建模程序刚推过来、还没落图的 " + pendingCount + " 张，默认落第一张）");
            var options = new PromptIntegerOptions("\n输入序号")
            {
                DefaultValue = 1, AllowNone = false, AllowZero = true, UseDefaultValue = true
            };
            var result = editor.GetInteger(options);
            if (result.Status != PromptStatus.OK) return null;
            if (result.Value <= 0) return BrowseViewFile();
            if (result.Value > entries.Count) return entries[entries.Count - 1].FilePath;
            // 记下选的是哪一条：落图成功后要把它的"待落图"标记去掉
            SelectedViewId = entries[result.Value - 1].Id;
            return entries[result.Value - 1].FilePath;
        }

        /// <summary>刚刚选中的视图 id（落图成功后用来清"待落图"标记）。</summary>
        [ThreadStatic]
        private static string SelectedViewId;

        private static string BrowseViewFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择建模程序生成的视图文件（views\\*.json）",
                Filter = "视图文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                InitialDirectory = LastViewFolder()
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        /// <summary>
        /// 图纸落图时套用项目已登记的图框模板：
        /// 按图纸的纸张规格（<see cref="ViewDocument.PaperName"/>，或用 <see cref="ViewDocument.FrameTemplate"/> 指定块名）
        /// 在项目图框里找一条匹配的，按出图比例放大图框，建筑几何保持模型空间 1:1。
        /// 找不到就返回 null —— 调用方照常使用图纸自带的图框。
        /// </summary>
        private static FrameDefinition InsertProjectFrame(Document document, Transaction transaction,
            BlockTableRecord space, ViewDocument view, Point3d anchor, out ObjectId insertedId,
            FrameDefinition selectedFrame, bool strict)
        {
            insertedId = ObjectId.Null;
            try
            {
                var frames = selectedFrame == null
                    ? new PublishPlanStore().LoadFrames()
                        .Where(f => f != null && !string.IsNullOrWhiteSpace(f.BlockName)).ToList()
                    : new List<FrameDefinition> { selectedFrame };
                if (frames.Count == 0)
                {
                    document.Editor.WriteMessage("\n（项目里还没有登记图框：本次用图纸自带的图框；"
                        + "在「图框登记」里登记 " + (view.PaperName ?? "同规格") + " 图框后会自动套用。）");
                    return null;
                }
                var frame = selectedFrame ?? PickFrame(frames, view);
                if (frame == null)
                {
                    document.Editor.WriteMessage("\n（项目里没有 " + (view.PaperName ?? "同规格")
                        + " 的登记图框：本次用图纸自带的图框。）");
                    return null;
                }
                FrameTemplateStore.EnsureAvailable(document, frame);
                var blockTable = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                if (!blockTable.Has(frame.BlockName))
                {
                    if (strict) throw new InvalidOperationException("所选图框块“" + frame.BlockName + "”不在当前图纸里。");
                    document.Editor.WriteMessage("\n（图框块“" + frame.BlockName + "”不在当前图纸里：本次用图纸自带的图框。）");
                    return null;
                }
                var definitionId = blockTable[frame.BlockName];
                var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
                var bounds = DefinitionBounds(definition, transaction);
                if (bounds == null)
                {
                    if (strict) throw new InvalidOperationException("所选图框没有有效边界。");
                    return null;
                }

                var paperWidth = view.PaperWidth > 1d ? view.PaperWidth : 420d;
                var paperHeight = view.PaperHeight > 1d ? view.PaperHeight : 297d;
                var factor = Math.Min(paperWidth / bounds.Width, paperHeight / bounds.Height) * Math.Max(1, view.Scale);
                if (!(factor > 0d) || double.IsInfinity(factor))
                {
                    if (strict) throw new InvalidOperationException("所选图框尺寸无效。");
                    return null;
                }

                var position = new Point3d(anchor.X - bounds.MinX * factor, anchor.Y - bounds.MinY * factor, anchor.Z);
                var reference = new BlockReference(position, definitionId) { ScaleFactors = new Scale3d(factor) };
                // 图框放到制图标准的图框图层上（与门窗立面、大样插入保持一致）
                try { reference.LayerId = DraftingStandardService.EnsureFrameLayer(document.Database, transaction); } catch { }
                space.AppendEntity(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);
                insertedId = reference.ObjectId;
                FillFrameAttributes(definition, transaction, reference, frame, view);
                document.Editor.WriteMessage("\n已套用项目图框：“" + frame.DisplayName + "”（按纸张缩放到 "
                    + paperWidth.ToString("0") + "×" + paperHeight.ToString("0") + "，图纸自带图框已跳过）。");
                return frame;
            }
            catch (Exception exception)
            {
                if (strict) throw new InvalidOperationException("套用所选图框失败：" + exception.Message, exception);
                if (!insertedId.IsNull)
                {
                    try { ((Entity)transaction.GetObject(insertedId, OpenMode.ForWrite)).Erase(); }
                    catch { }
                    insertedId = ObjectId.Null;
                }
                document.Editor.WriteMessage("\n套用项目图框失败（将使用图纸自带图框）：" + exception.Message);
                return null;
            }
        }

        /// <summary>按纸张规格与横竖方向挑一条项目图框（<see cref="ViewDocument.FrameTemplate"/> 指定了块名就优先用它）。</summary>
        private static FrameDefinition PickFrame(List<FrameDefinition> frames, ViewDocument view)
        {
            if (!string.IsNullOrWhiteSpace(view.FrameTemplate))
            {
                var named = frames.FirstOrDefault(f => string.Equals(f.BlockName, view.FrameTemplate, StringComparison.OrdinalIgnoreCase));
                if (named != null) return named;
            }
            var wanted = (view.PaperName ?? string.Empty).Trim();
            if (wanted.Length == 0) return frames.FirstOrDefault();
            var landscape = view.PaperWidth >= view.PaperHeight;
            var byPaper = frames.Where(f => string.Equals((f.PaperSize ?? string.Empty).Trim(), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byPaper.Count == 0) return null;
            var byOrientation = byPaper.Where(f => SameOrientation(f.PaperOrientation, landscape)).ToList();
            return (byOrientation.Count > 0 ? byOrientation : byPaper).First();
        }

        private static bool SameOrientation(string orientation, bool landscape)
        {
            var text = (orientation ?? string.Empty).Trim();
            if (text.Length == 0) return true;                       // 没登记方向的先用着
            var isLandscape = text.IndexOf("横", StringComparison.Ordinal) >= 0;
            return isLandscape == landscape;
        }

        private static void FillFrameAttributes(BlockTableRecord definition, Transaction transaction,
            BlockReference reference, FrameDefinition frame, ViewDocument view)
        {
            foreach (ObjectId id in definition)
            {
                var attributeDefinition = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeDefinition;
                if (attributeDefinition == null || attributeDefinition.Constant) continue;
                var attribute = new AttributeReference();
                attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                var tag = (attributeDefinition.Tag ?? string.Empty).Trim();
                var value = attributeDefinition.TextString;
                if (TagMatches(tag, frame.PrintScaleAttributeTag, "比例")) value = "1:" + Math.Max(1, view.Scale);
                else if (TagMatches(tag, frame.SheetNameAttributeTag, "图纸名称", "图名")) value = view.Title ?? string.Empty;
                else if (TagMatches(tag, frame.SheetNumberAttributeTag, "图号")) value = SheetNumberOf(view);
                else if (TagMatches(tag, frame.BuildingAttributeTag, "子项目名称") && !string.IsNullOrWhiteSpace(frame.DefaultBuilding))
                    value = frame.DefaultBuilding;
                attribute.TextString = string.IsNullOrWhiteSpace(value) || value.StartsWith("<", StringComparison.Ordinal) ? tag : value;
                reference.AttributeCollection.AppendAttribute(attribute);
                transaction.AddNewlyCreatedDBObject(attribute, true);
            }
        }

        /// <summary>从图纸标题里取图号（标题形如"建施-01　一层 平面图　A3 横…"）。</summary>
        private static string SheetNumberOf(ViewDocument view)
        {
            var title = view.Title ?? string.Empty;
            var parts = title.Split(new[] { '　', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 && parts[0].IndexOf("建施", StringComparison.Ordinal) >= 0 ? parts[0] : (view.Id ?? "SHEET");
        }

        private static bool TagMatches(string tag, string configured, params string[] fallbacks)
        {
            var value = (tag ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(configured) && string.Equals(value, configured.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var fallback in fallbacks)
                if (!string.IsNullOrWhiteSpace(fallback) && value.IndexOf(fallback, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private sealed class DefinitionBoundsInfo
        {
            public double MinX, MinY, Width, Height;
        }

        private static DefinitionBoundsInfo DefinitionBounds(BlockTableRecord definition, Transaction transaction)
        {
            var first = true;
            var extents = new Extents3d();
            foreach (ObjectId id in definition)
            {
                var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity == null) continue;
                try
                {
                    if (first) { extents = entity.GeometricExtents; first = false; }
                    else extents.AddExtents(entity.GeometricExtents);
                }
                catch { }
            }
            if (first) return null;
            var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
            var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            if (width < 1e-6d || height < 1e-6d) return null;
            return new DefinitionBoundsInfo
            {
                MinX = extents.MinPoint.X, MinY = extents.MinPoint.Y, Width = width, Height = height
            };
        }

        /// <summary>
        /// 把一条视图尺寸变成 CAD 的 `RotatedDimension`：
        /// 竖直尺寸旋转 90°（量 Z），水平尺寸旋转 0°（量 U）；
        /// 两个被量点在 (AnchorPosition, From/To)，尺寸线摆在 LinePosition 处。
        /// 文字留空 → CAD 按实际距离自己量出数值（改了模型重算视图，数值也跟着对）。
        /// </summary>
        private static ObjectId EnsureVariableDimensionTextStyle(Database database,Transaction transaction,ObjectId source)
        {
            if(source.IsNull)source=database.Textstyle;
            var original=(TextStyleTableRecord)transaction.GetObject(source,OpenMode.ForRead);
            if(original.TextSize==0)return source;
            // A fixed-height CAD text style ignores DIMTXT. Derive an owned variable-height
            // style with the same fonts, without changing the user's drafting standard.
            var table=(TextStyleTable)transaction.GetObject(database.TextStyleTableId,OpenMode.ForRead);
            var name="WL-模型-标注-"+source.Handle;
            TextStyleTableRecord target;
            if(table.Has(name))target=(TextStyleTableRecord)transaction.GetObject(table[name],OpenMode.ForWrite);
            else {
                table.UpgradeOpen();target=new TextStyleTableRecord {Name=name};table.Add(target);
                transaction.AddNewlyCreatedDBObject(target,true);
            }
            target.Font=original.Font;target.FileName=original.FileName;target.BigFontFileName=original.BigFontFileName;
            target.TextSize=0;target.XScale=original.XScale;target.ObliquingAngle=original.ObliquingAngle;
            return target.ObjectId;
        }

        private static RotatedDimension CreateDimension(ViewDimension dimension, Point3d anchor, ObjectId dimensionStyle)
        {
            Point3d first, second, linePoint;
            double rotation;
            if (dimension.Vertical)
            {
                rotation = Math.PI / 2d;
                first = new Point3d(anchor.X + dimension.AnchorPosition, anchor.Y + dimension.From, 0d);
                second = new Point3d(anchor.X + dimension.AnchorPosition, anchor.Y + dimension.To, 0d);
                linePoint = new Point3d(anchor.X + dimension.LinePosition, anchor.Y + (dimension.From + dimension.To) / 2d, 0d);
            }
            else
            {
                rotation = 0d;
                first = new Point3d(anchor.X + dimension.From, anchor.Y + dimension.AnchorPosition, 0d);
                second = new Point3d(anchor.X + dimension.To, anchor.Y + dimension.AnchorPosition, 0d);
                linePoint = new Point3d(anchor.X + (dimension.From + dimension.To) / 2d, anchor.Y + dimension.LinePosition, 0d);
            }
            var entity = new RotatedDimension(rotation, first, second, linePoint, dimension.Text ?? string.Empty, dimensionStyle);
            if (dimension.TextX.HasValue && dimension.TextY.HasValue)
            {
                entity.Dimtmove = 2;
                entity.TextPosition = new Point3d(anchor.X+dimension.TextX.Value,anchor.Y+dimension.TextY.Value,0);
            }
            return entity;
        }

        private static void ApplyLayer(Entity entity, string layer, Dictionary<string, ObjectId> layerIds)
        {
                ObjectId id;
                if (entity is DBText && layer != ViewLayers.SheetFrame) layer = ViewLayers.LevelText;
                if (!layerIds.TryGetValue(layer ?? string.Empty, out id))
                    id = layerIds[ViewLayers.Elevation];
                entity.LayerId = id;
                entity.ColorIndex = 256;      // 随层
                entity.LineWeight = LineWeight.ByLayer;
                entity.Linetype = "ByLayer";
        }

        private static void ApplyLineWeight(Entity entity,int? weight)
        {
            if(weight.HasValue && DrawingLineWeights.Values.Contains(weight.Value))
                entity.LineWeight=(LineWeight)weight.Value;
        }
        private static void ApplyViewLineAppearance(Entity entity,ViewLine line,Dictionary<string,ObjectId> layers)
        {
            var layer=OpeningElevationAdapter.LayerKey(line)??(line.LineType=="DASHED"||line.LineType=="HIDDEN"?"Hidden":line.Layer);
            ApplyLayer(entity,layer,layers);
            if(line.OpeningRole=="SashFrame")entity.ColorIndex=8;
            ApplyLineWeight(entity,line.LineWeight);
        }
        public static void RunElevationStyleNativeCheck(Document document)
        {
            var dbmod=Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"));
            var model=new BuildingModelDocument {Name="Elevation QA"};model.Storeys.Add(new StoreyModel {Id="1F",Height=3000});
            model.Walls.Add(new WallModel {Id="host",StoreyId="1F",X2=5000,Thickness=200});
            model.Openings.Add(new OpeningModel {Id="window",HostWallId="host",Code="C1215",Kind="窗",Width=1200,Height=1500,Sill=900,Offset=1500});
            model.Openings.Add(new OpeningModel {Id="door",HostWallId="host",Code="M0921",Kind="门",Width=900,Height=2100,Offset=3500});
            foreach(var opening in model.Openings){var type=OpeningConstruction.Default(opening);type.DoorFrameWidth=40;
                type.OpeningMode=opening.Kind=="窗"?"双扇平开":"左平开";model.OpeningTypes.Add(type);}
            var view=OrthographicProjector.ProjectOpeningElevations(model,new ViewDefinitionModel {Id="style-check",Kind=ViewKind.OpeningElevation,Scale=50},OpeningConstruction.Library(model));
            var typed=view.Lines.Where(l=>l.OpeningRole!=null).ToList();var marks=0;var windows=0;var doors=0;var sashes=0;
            using(var db=new Database(true,true))using(var tx=db.TransactionManager.StartTransaction())
            {
                var layers=EnsureViewLayers(db,tx);var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
                foreach(var line in typed.Where(l=>l.OpeningArcId==null))
                {
                    var entity=CreateViewLineEntity(line,new Point3d(1234,5678,0));ApplyViewLineAppearance(entity,line,layers);
                    space.AppendEntity(entity);tx.AddNewlyCreatedDBObject(entity,true);
                    var key=OpeningElevationAdapter.LayerKey(line);
                    var expected=DraftingStandardService.EnsureLayerFor(db,tx,key);
                    if(entity.LayerId!=expected||entity.ColorIndex!=(line.OpeningRole=="SashFrame"?8:256)||entity.Linetype!="ByLayer")
                        throw new InvalidDataException("建筑立面门窗样式与门窗立面图层不一致。");
                    if(key==OpeningElevationAdapter.MarkLayerKey)
                    {
                        if(line.LineType!="DASHED")throw new InvalidDataException("开启线没有保留虚线。");marks++;
                        var layer=(LayerTableRecord)tx.GetObject(expected,OpenMode.ForRead);
                        var style=DraftingStandardService.LoadProfile();
                        var setting=style.Layers.First(p=>p.Key==key);
                        if(setting.TrueColorRgb<0&&layer.Color.ColorIndex!=setting.ColorIndex)throw new InvalidDataException("门窗开启图层颜色未随制图标准。");
                    }
                    if(key==OpeningElevationAdapter.WindowLayerKey)windows++;
                    if(key==OpeningElevationAdapter.DoorLayerKey)doors++;
                    if(line.OpeningRole=="SashFrame")sashes++;
                }
                RunOpeningPathNativeCheck(db,tx,space,layers,typed,view.Scale);
                marks+=typed.Count(l=>l.OpeningArcId!=null&&OpeningElevationAdapter.LayerKey(l)==OpeningElevationAdapter.MarkLayerKey);
                tx.Commit();
            }
            if(marks==0||windows==0||doors==0||sashes==0||Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"))!=dbmod)
                throw new InvalidDataException("立面样式检查缺少门、窗、开启线或修改了当前图纸。");
            document.Editor.WriteMessage("\nELEVATION_STYLE_NATIVE_OK sharedLayers byLayerColor sashAci8 dashedOpening cleanGeometry pairedOpenPL C1121 centeredMLC manualHingesAllowed exactEndpoints disconnectedNoBridge unchangedDrawing");
            RunWallColumnNativeCheck(document);
        }

        private static void RunOpeningPathNativeCheck(Database db,Transaction tx,BlockTableRecord space,
            Dictionary<string,ObjectId> layers,List<ViewLine> projected,int scale)
        {
            var anchor=new Point3d(1234,5678,0);
            var markLayer=DraftingStandardService.EnsureLayerFor(db,tx,OpeningElevationAdapter.MarkLayerKey);
            var layer=(LayerTableRecord)tx.GetObject(markLayer,OpenMode.ForRead);
            var lineType=(LinetypeTableRecord)tx.GetObject(layer.LinetypeObjectId,OpenMode.ForRead);
            if(!string.Equals(lineType.Name,"DASHED",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Opening path layer must retain DASHED.");

            List<Polyline> Emit(IEnumerable<ViewLine> source)
            {
                var entities=new List<Polyline>();
                foreach(var chain in BuildOpeningPathChains(source))
                {
                    var entity=CreateOpeningPathPolyline(chain,anchor);
                    ApplyOpeningPathAppearance(entity,chain.Appearance,layers,db,tx,scale);
                    space.AppendEntity(entity);tx.AddNewlyCreatedDBObject(entity,true);entities.Add(entity);
                    if(entity.LayerId!=markLayer||entity.Linetype!="ByLayer"||chain.Appearance.LineType!="DASHED")
                        throw new InvalidDataException("Opening path changed layer or line type.");
                }
                return entities;
            }
            Tuple<double,double,double,double,double> EdgeKey(double x,double y,double xx,double yy,double bulge)
            {
                if(x>xx||x==xx&&y>yy)return Tuple.Create(xx,yy,x,y,-bulge);
                return Tuple.Create(x,y,xx,yy,bulge);
            }
            void CheckEdges(List<Polyline> entities,IEnumerable<ViewLine> source)
            {
                var expected=source.Where(l=>l.OpeningArcId!=null&&(l.X1!=l.X2||l.Y1!=l.Y2))
                    .GroupBy(l=>EdgeKey(anchor.X+l.X1,anchor.Y+l.Y1,anchor.X+l.X2,anchor.Y+l.Y2,l.Bulge))
                    .ToDictionary(g=>g.Key,g=>g.Count());
                foreach(var entity in entities)
                {
                    if(entity.Closed||!entity.Plinegen||entity.Elevation!=anchor.Z)
                        throw new InvalidDataException("Opening path must be an open native PL with continuous dashes.");
                    for(var i=0;i<entity.NumberOfVertices-1;i++)
                    {
                        var a=entity.GetPoint2dAt(i);var b=entity.GetPoint2dAt(i+1);
                        var key=EdgeKey(a.X,a.Y,b.X,b.Y,entity.GetBulgeAt(i));
                        if(!expected.TryGetValue(key,out var count)||count==0)
                            throw new InvalidDataException("Opening PL introduced a phantom link or moved an endpoint.");
                        expected[key]=count-1;
                    }
                }
                if(expected.Values.Any(count=>count!=0))throw new InvalidDataException("Opening PL lost a visible segment.");
            }
            ViewLine Reversed(ViewLine l)=>new ViewLine {Layer=l.Layer,LineType=l.LineType,LineWeight=l.LineWeight,
                OpeningRole=l.OpeningRole,OpeningUsesWindowAppearance=l.OpeningUsesWindowAppearance,OpeningArcId=l.OpeningArcId,
                X1=l.X2,Y1=l.Y2,X2=l.X1,Y2=l.Y1,Bulge=-l.Bulge};

            var projectedPaths=projected.Where(l=>l.OpeningArcId!=null).ToList();
            if(projectedPaths.Count==0)throw new InvalidDataException("Projected elevation lost opening path identities.");
            CheckEdges(Emit(projectedPaths.AsEnumerable().Reverse().Select(Reversed)),projectedPaths);
            var pair=new DoorWindowScheduleItem {Code="PLPAIR",Width=1500,Height=2500,ElevationType="普通门",
                DivisionPreset="双扇等分",OpeningMode="双扇平开",HasInstallationGap=false,HasOuterFrame=true,OuterFrameWidth=50,
                HasMullion=true,MullionWidth=50,DoorFrameType="N型",DoorFrameWidth=40};
            var pairLines=OpeningElevationAdapter.CleanGeometryLines(DoorWindowElevationGeometryBuilder.Build(pair).Lines)
                .Where(l=>l.OpeningPathId!=null).Select(l=>OpeningElevationAdapter.ElevationLine(l,false)).ToList();
            var nativePair=Emit(pairLines.AsEnumerable().Reverse().Select(Reversed));
            if(pairLines.Select(l=>l.OpeningArcId).Distinct().Count()!=2||nativePair.Count!=2
                ||nativePair.Any(p=>p.NumberOfVertices!=3)
                ||!nativePair.Any(p=>p.GetPoint2dAt(1).X==anchor.X+50)
                ||!nativePair.Any(p=>p.GetPoint2dAt(1).X==anchor.X+1450))
                throw new InvalidDataException("Paired leaves require distinct three-vertex PLs with exterior hinge tips.");
            CheckEdges(nativePair,pairLines);

            void CheckDirect(DoorWindowScheduleItem item,List<ViewLine> paths)
            {
                var before=new HashSet<ObjectId>(space.Cast<ObjectId>());
                DoorWindowElevationInsertionService.AppendGeometryForNativeCheck(item,anchor,space,tx);
                var direct=space.Cast<ObjectId>().Where(id=>!before.Contains(id)).Select(id=>(Entity)tx.GetObject(id,OpenMode.ForRead))
                    .Where(e=>e.LayerId==markLayer&&e is Polyline p&&p.Plinegen).Cast<Polyline>().ToList();
                if(direct.Count!=2||direct.Any(p=>p.NumberOfVertices!=3||p.Linetype!="ByLayer"
                    ||!DoorWindowElevationMetadataService.TryRead(p,out var metadata)||metadata.Code!=item.Code))
                    throw new InvalidDataException(item.Code+": direct CAD elevation lost a leaf PL or its metadata.");
                CheckEdges(direct,paths);
            }
            CheckDirect(pair,pairLines);
            var hung=new DoorWindowScheduleItem {Code="HUNG",Width=1500,Height=1500,ElevationType="普通窗",
                DivisionPreset="双扇等分",OpeningMode="上悬",HasInstallationGap=false};
            var hungPaths=OpeningElevationAdapter.CleanGeometryLines(DoorWindowElevationGeometryBuilder.Build(hung).Lines)
                .Where(l=>l.OpeningPathId!=null).Select(l=>OpeningElevationAdapter.ElevationLine(l,true)).ToList();
            var nativeHung=Emit(hungPaths.AsEnumerable().Reverse().Select(Reversed));
            if(nativeHung.Count!=2||nativeHung.Any(p=>p.NumberOfVertices!=3||p.GetPoint2dAt(1).Y!=anchor.Y+1450))
                throw new InvalidDataException("Top-hung leaves require separate three-vertex PLs with top hinge tips.");
            CheckEdges(nativeHung,hungPaths);CheckDirect(hung,hungPaths);

            void CheckDefault(DoorWindowScheduleItem item,bool combined,bool inward=false)
            {
                var geometry=DoorWindowElevationGeometryBuilder.Build(item);
                var leaves=geometry.Cells.Where(c=>c.Opening=="左平开"||c.Opening=="右平开").OrderBy(c=>c.Left).ToList();
                if(leaves.Count!=2||leaves[0].Opening!=(inward?"右平开":"左平开")||leaves[1].Opening!=(inward?"左平开":"右平开"))
                    throw new InvalidDataException(item.Code+": default/user hinge directions were not preserved.");
                if(combined&&(item.DoorPlacement!="居中"||leaves.Any(c=>!c.IsDoor)
                    ||leaves.Any(c=>c.Right-c.Left!=750||c.Top-c.Bottom!=2200)
                    ||(leaves[0].Left+leaves[1].Right)/2!=item.Width/2))
                    throw new InvalidDataException(item.Code+": centered 1500 x 2200 door defaults changed.");
                var paths=OpeningElevationAdapter.CleanGeometryLines(geometry.Lines)
                    .Where(l=>l.OpeningPathId!=null).Select(l=>OpeningElevationAdapter.ElevationLine(l,OpeningElevationAdapter.UsesWindowAppearance(item))).ToList();
                var native=Emit(paths.AsEnumerable().Reverse().Select(Reversed));
                if(paths.Count!=4||paths.Select(l=>l.OpeningArcId).Distinct().Count()!=2
                    ||native.Count!=2||native.Any(p=>p.NumberOfVertices!=3))
                    throw new InvalidDataException(item.Code+": default leaves require two three-vertex PLs.");
                for(var i=0;i<leaves.Count;i++)
                {
                    var bounds=DoorWindowElevationGeometryBuilder.PanelBounds(leaves[i],geometry.Cells,item);
                    var tip=new Point2d(anchor.X+bounds[leaves[i].Opening=="左平开"?0:2],anchor.Y+(bounds[1]+bounds[3])/2);
                    if(!native.Any(p=>p.GetPoint2dAt(1).Equals(tip)))
                        throw new InvalidDataException(item.Code+": native PL tip moved from its configured hinge.");
                }
                CheckEdges(native,paths);CheckDirect(item,paths);
            }
            var windowDefault=new DoorWindowScheduleItem {Code="C1121",Width=1100,Height=2100};
            DoorWindowElevationSuggestionService.Apply(windowDefault);CheckDefault(windowDefault,false);
            foreach(var width in new[]{2000d,2400d,7600d})
            {
                var combined=new DoorWindowScheduleItem {Code="MLC"+(width/100).ToString("00")+"27",Width=width,Height=2700,
                    HasInstallationGap=false,InstallationGap=0};
                DoorWindowElevationSuggestionService.Apply(combined);CheckDefault(combined,true);
                var oldLayout=DoorWindowElevationGeometryBuilder.ParseCellLayout(combined.CustomCellLayout);
                foreach(var cell in oldLayout.Where(c=>c.IsDoor))cell.Opening=cell.Opening=="左平开"?"右平开":"左平开";
                combined.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(oldLayout);
                combined.CellOpeningModes=string.Join("|",oldLayout.Select(c=>c.Opening));CheckDefault(combined,true,true);
            }

            ViewLine Piece(double x,double y,double xx,double yy,string id="clipped")=>new ViewLine {
                Layer=ViewLayers.Opening,OpeningRole="Opening",LineType="DASHED",OpeningArcId=id,X1=x,Y1=y,X2=xx,Y2=yy};
            var clipped=new[] {Piece(0,0,5,5),Piece(10,10,20,20),Piece(20,20,30,0),
                Piece(30,0,40,5,"other-leaf"),Piece(30.001,0,40,5)};
            var disconnected=Emit(clipped.Reverse().Select(Reversed));
            if(disconnected.Count!=4||disconnected.Count(p=>p.NumberOfVertices==3)!=1)
                throw new InvalidDataException("Clipped paths crossed a gap or joined adjacent leaves.");
            CheckEdges(disconnected,clipped);
        }
        private static void RunWallColumnNativeCheck(Document document)
        {
            var dbmod=Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"));
            var model=new BuildingModelDocument {Name="Column drawing QA",Storeys=new List<StoreyModel> {new StoreyModel {Id="1F",Height=3000}}};
            model.Walls.Add(new WallModel {Id="wall",StoreyId="1F",X1=-2000,X2=2000,Thickness=800});
            model.Columns.Add(new ColumnModel {Id="column",StoreyId="1F",Width=400,Depth=400});
            var without=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model));without.Columns.Clear();
            using(var db=new Database(true,true))using(var tx=db.TransactionManager.StartTransaction()) {
                var layers=EnsureViewLayers(db,tx);var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
                foreach(var definition in new[] {new ViewDefinitionModel {Id="plan",Kind=ViewKind.Plan,StoreyIds=new List<string>{"1F"}},
                    new ViewDefinitionModel {Id="elevation",Kind=ViewKind.Elevation},new ViewDefinitionModel {Id="section",Kind=ViewKind.Section},
                    new ViewDefinitionModel {Id="axon",Kind=ViewKind.Axonometric}}) {
                    var view=OrthographicProjector.Project(model,definition);var reference=OrthographicProjector.Project(without,definition);
                    if(view.Lines.Count!=reference.Lines.Count || !view.Lines.Zip(reference.Lines,(a,b)=>a.Layer==b.Layer
                        && Math.Abs(a.X1-b.X1)<.001 && Math.Abs(a.Y1-b.Y1)<.001 && Math.Abs(a.X2-b.X2)<.001 && Math.Abs(a.Y2-b.Y2)<.001).All(b=>b)
                        || view.Hatches.Count!=reference.Hatches.Count)throw new InvalidDataException("墙柱重叠仍产生柱线或重复剖面填充。");
                    foreach(var line in view.Lines) {
                        var entity=CreateViewLineEntity(line,new Point3d(1234,5678,0));ApplyViewLineAppearance(entity,line,layers);
                        space.AppendEntity(entity);tx.AddNewlyCreatedDBObject(entity,true);
                    }
                }
                tx.Commit();
            }
            if(!BuildingVolumeBuilder.Build(model).Faces.Any(f=>f.Kind=="column") || Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"))!=dbmod)
                throw new InvalidDataException("图纸去柱影响了模型柱或当前图纸。");
            document.Editor.WriteMessage("\nWALL_COLUMN_NATIVE_OK plan elevation section axon noCoveredColumn noDuplicateHatch nativeEntities semanticColumn unchangedDrawing");
        }

        private static ObjectId EnsureOpeningArcLineType(Database database,Transaction transaction)
        {
            const string name="WL_OPENING_SWING";
            var table=(LinetypeTable)transaction.GetObject(database.LinetypeTableId,OpenMode.ForRead);
            if(table.Has(name))return table[name];
            table.UpgradeOpen();
            var record=new LinetypeTableRecord {Name=name,AsciiDescription="Door swing: 2 mm dash, 1 mm gap",NumDashes=2,PatternLength=3};
            record.SetDashLengthAt(0,2);record.SetDashLengthAt(1,-1);
            var id=table.Add(record);transaction.AddNewlyCreatedDBObject(record,true);return id;
        }

        private static void ApplyLineType(Transaction transaction, Database database, Entity entity, string lineType)
        {
            if (string.IsNullOrWhiteSpace(lineType)) return;
            try
            {
                var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
                if (!table.Has(lineType))
                {
                    try { database.LoadLineTypeFile(lineType, "acad.lin"); } catch { }
                    table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
                }
                if (table.Has(lineType)) entity.LinetypeId = table[lineType];
            }
            catch { }
        }

        private static void Bump(Dictionary<string, int> counts, string layer)
        {
            var key = string.IsNullOrWhiteSpace(layer) ? "(随层)" : layer;
            int value;
            counts.TryGetValue(key, out value);
            counts[key] = value + 1;
        }

        private static string LastViewFolder()
        {
            try
            {
                var path = Path.Combine(UserDataPaths.SettingsDirectory, "building-model-last-folder.txt");
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path).Trim();
                    if (Directory.Exists(text)) return text;
                }
            }
            catch { }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private static void SaveLastViewFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            try
            {
                Directory.CreateDirectory(UserDataPaths.SettingsDirectory);
                File.WriteAllText(Path.Combine(UserDataPaths.SettingsDirectory, "building-model-last-folder.txt"), folder);
            }
            catch { }
        }
    }
}
