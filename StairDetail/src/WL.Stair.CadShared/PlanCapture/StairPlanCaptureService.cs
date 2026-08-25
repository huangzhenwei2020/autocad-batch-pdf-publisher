using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using WL.Stair.Core.Domain;
using WL.Stair.Core.Geometry;

namespace WL.Stair.CadShared.PlanCapture
{
    internal sealed class StairPlanCaptureService
    {
        private const double AlignmentTolerance = 0.0871557427476582; // sin(5 degrees)
        private const string TemporaryTrimLayer = "WL_楼梯_临时裁切";

        public StairPlanSourceDefinition CaptureTianzhengStair(
            Document document,
            string storeyId,
            string displayName,
            double cropOffset)
        {
            if (document == null) return null;
            var editor = document.Editor;
            var options = new PromptEntityOptions("\n请选择本层原生天正楼梯对象或已画好的闭合多段线：");
            var picked = editor.GetEntity(options);
            if (picked.Status != PromptStatus.OK) return null;

            StairPlanSourceDefinition definition;
            string failure;
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var stair = transaction.GetObject(picked.ObjectId, OpenMode.ForRead, false) as Entity;
                if (stair == null)
                    throw new InvalidOperationException("所选对象无法读取。");
                var directBoundary = stair as Autodesk.AutoCAD.DatabaseServices.Polyline;
                if (directBoundary != null)
                {
                    definition = BuildManualDefinition(
                        document,
                        null,
                        directBoundary,
                        storeyId,
                        displayName,
                        cropOffset);
                }
                else
                {
                    if (!IsTianzhengStair(stair))
                        throw new InvalidOperationException(
                            "所选对象既不是已识别的原生天正楼梯，也不是闭合多段线。");

                    Extents3d stairExtents;
                    BoundarySolution solution;
                    var solved = TryGetExtents(stair, out stairExtents);
                    failure = solved ? string.Empty : "无法取得天正楼梯几何范围";
                    if (solved)
                    {
                        var walls = ReadNearbyWalls(document.Database, transaction, stairExtents);
                        solved = TrySolveBoundary(stairExtents, walls, out solution, out failure);
                    }
                    else
                    {
                        solution = null;
                    }

                    if (solved)
                    {
                        definition = BuildDefinition(
                            document,
                            stair,
                            storeyId,
                            displayName,
                            cropOffset,
                            solution);
                    }
                    else
                    {
                        editor.WriteMessage(
                            "\n天正楼梯已识别，但墙轴线自动闭合失败：" + failure
                            + "。现在可直接框选范围，或拾取已有闭合多段线。\n");
                        IList<Point2d> manualPoints;
                        string boundaryHandle;
                        if (!TryPromptManualBoundary(
                            editor,
                            transaction,
                            out manualPoints,
                            out boundaryHandle)) return null;
                        definition = BuildManualDefinition(
                            document,
                            stair,
                            manualPoints,
                            boundaryHandle,
                            storeyId,
                            displayName,
                            cropOffset);
                    }
                }
            }

            if (!ConfirmPreview(editor, definition)) return null;
            AppendLog(definition);
            return definition;
        }

        public string InspectRegisteredSource(
            Document document,
            StairPlanSourceDefinition definition)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (definition == null
                || definition.CropBoundaryPoints == null
                || definition.CropBoundaryPoints.Count < 3)
                throw new InvalidOperationException("登记记录缺少有效裁剪外框，请重新拾取本层平面。");

            var polygon = definition.CropBoundaryPoints
                .Select(point => new Point2d(point.X, point.Y))
                .ToList();
            var cropEnvelope = new Extents3d(
                new Point3d(polygon.Min(point => point.X), polygon.Min(point => point.Y), 0.0),
                new Point3d(polygon.Max(point => point.X), polygon.Max(point => point.Y), 0.0));
            var inside = 0;
            var crossing = 0;
            var outside = 0;
            var tianzhengInside = 0;
            var tianzhengCrossing = 0;
            var crossingBlocks = 0;
            var crossingExtents = new List<Extents3d>();
            var clippedOrdinarySegments = new List<PlanClipSegment>();
            var clippedOrdinaryObjects = 0;
            var preservedTianzhengOpenings = 0;
            var pendingAdapterObjects = 0;
            var wallSplitProbe = new TianzhengWallSplitProbeResult();
            wallSplitProbe.NoTouchPreview = true;
            var insideObjectIds = new List<ObjectId>();
            var crossingObjectIds = new List<ObjectId>();
            var copyProbeCandidates = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var space = transaction.GetObject(
                    document.Database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space == null) throw new InvalidOperationException("无法读取当前模型空间。");
                foreach (ObjectId objectId in space)
                {
                    var entity = transaction.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity.IsErased || !entity.Visible) continue;
                    if (!string.IsNullOrWhiteSpace(definition.BoundarySourceHandle)
                        && string.Equals(entity.Handle.ToString(), definition.BoundarySourceHandle,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    Extents3d extents;
                    if (!TryGetExtents(entity, out extents)) continue;
                    // Do not classify or clone the rest of the building. Only
                    // objects whose extents touch the crop envelope enter the
                    // extraction pipeline.
                    if (!Intersects(cropEnvelope, extents)) continue;
                    var classification = ClassifyExtents(extents, polygon);
                    var isTianzheng = IsTianzhengObject(entity);
                    if (classification != BoundaryClassification.Outside && !isTianzheng)
                    {
                        var probeKey = (isTianzheng ? "TCH:" : "CAD:")
                            + SafeDxfName(entity) + ":" + entity.GetType().FullName;
                        if (!copyProbeCandidates.ContainsKey(probeKey))
                            copyProbeCandidates.Add(probeKey, objectId);
                    }
                    if (classification == BoundaryClassification.Inside)
                    {
                        inside++;
                        insideObjectIds.Add(objectId);
                        if (isTianzheng) tianzhengInside++;
                    }
                    else if (classification == BoundaryClassification.Crossing)
                    {
                        crossing++;
                        crossingObjectIds.Add(objectId);
                        if (isTianzheng) tianzhengCrossing++;
                        if (entity is BlockReference) crossingBlocks++;
                        var clipped = !isTianzheng
                            ? PreviewClipOrdinaryEntity(entity, definition.CropBoundaryPoints)
                            : null;
                        if (clipped != null)
                        {
                            clippedOrdinaryObjects++;
                            clippedOrdinarySegments.AddRange(clipped);
                        }
                        else if (isTianzheng && IsTianzhengOpening(entity))
                        {
                            preservedTianzhengOpenings++;
                        }
                        else
                        {
                            pendingAdapterObjects++;
                            if (crossingExtents.Count < 80) crossingExtents.Add(extents);
                        }
                    }
                    else
                    {
                        outside++;
                    }
                }
            }

            var copyProbe = ProbeCopyCompatibility(document, copyProbeCandidates.Values);
            var assemblyProbe = ValidateWorkingCopyAssembly(
                document,
                definition,
                insideObjectIds,
                crossingObjectIds);

            var transients = new List<Entity>();
            try
            {
                AddDashedBoundary(definition.CropBoundaryPoints, transients);
                foreach (var segment in clippedOrdinarySegments)
                {
                    var line = new Line(
                        new Point3d(segment.Start.X, segment.Start.Y, 0.0),
                        new Point3d(segment.End.X, segment.End.Y, 0.0))
                    {
                        Color = Color.FromColorIndex(ColorMethod.ByAci, 3)
                    };
                    AddTransient(line, transients);
                }
                foreach (var extents in crossingExtents)
                {
                    var box = new Autodesk.AutoCAD.DatabaseServices.Polyline(4)
                    {
                        Closed = true,
                        Color = Color.FromColorIndex(ColorMethod.ByAci, 6)
                    };
                    box.AddVertexAt(0, new Point2d(extents.MinPoint.X, extents.MinPoint.Y), 0.0, 0.0, 0.0);
                    box.AddVertexAt(1, new Point2d(extents.MaxPoint.X, extents.MinPoint.Y), 0.0, 0.0, 0.0);
                    box.AddVertexAt(2, new Point2d(extents.MaxPoint.X, extents.MaxPoint.Y), 0.0, 0.0, 0.0);
                    box.AddVertexAt(3, new Point2d(extents.MinPoint.X, extents.MaxPoint.Y), 0.0, 0.0, 0.0);
                    AddTransient(box, transients);
                }
                var summary = string.Format(
                    CultureInfo.CurrentCulture,
                    "提取范围检查：框内 {0} 个（天正 {1}）；穿越外框 {2} 个（天正 {3}、块参照 {4}）；其中普通直线/多段线可裁剪 {5} 个，天正门窗完整保留 {6} 个，待专用适配 {7} 个；邻近但框外忽略 {8} 个。复制兼容性：{9}。天正墙切段保真：{10}。工作副本组装：{11}。",
                    inside,
                    tianzhengInside,
                    crossing,
                    tianzhengCrossing,
                    crossingBlocks,
                    clippedOrdinaryObjects,
                    preservedTianzhengOpenings,
                    pendingAdapterObjects,
                    outside,
                    copyProbe.Summary,
                    wallSplitProbe.Summary,
                    assemblyProbe.Summary);
                document.Editor.WriteMessage("\n" + summary
                    + "\n红色虚线=裁剪外框；绿色线=普通直线/直段多段线的真实裁剪预览；紫色框=仍需天正、块、圆弧等专用适配的穿越对象。兼容性副本已全部回滚，当前图纸没有新增、删除或修改任何对象。\n");
                AppendCopyProbeLog(
                    definition,
                    copyProbe,
                    wallSplitProbe,
                    assemblyProbe,
                    inside,
                    crossing,
                    outside);
                var close = new PromptKeywordOptions("\n检查完成 [返回(R)] <返回>: ");
                close.AllowNone = true;
                close.Keywords.Add("Return", "R", "返回");
                document.Editor.GetKeywords(close);
                return summary;
            }
            finally
            {
                var manager = TransientManager.CurrentTransientManager;
                foreach (var entity in transients)
                {
                    try { manager.EraseTransient(entity, new IntegerCollection()); }
                    catch { }
                    entity.Dispose();
                }
                document.Editor.Regen();
            }
        }

        public string CreateWorkingCopy(
            Document document,
            StairPlanSourceDefinition definition,
            Point3d insertionPoint,
            string title)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (definition == null
                || definition.CropBoundaryPoints == null
                || definition.CropBoundaryPoints.Count < 3)
                throw new InvalidOperationException("登记记录缺少有效裁剪外框，请重新拾取本层平面。");

            var currentFingerprint = SafeFingerprint(document.Database);
            if (!string.IsNullOrWhiteSpace(definition.SourceDrawingFingerprint)
                && !string.Equals(definition.SourceDrawingFingerprint, currentFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前图纸不是登记该层平面来源的图纸，请切换到原图后重试。");

            var polygon = definition.CropBoundaryPoints
                .Select(point => new Point2d(point.X, point.Y))
                .ToList();
            var corePolygon = definition.CropBoundaryPoints
                .Select(point => new WL.Stair.Core.Geometry.Point2D(point.X, point.Y))
                .ToList();
            var minX = polygon.Min(point => point.X);
            var minY = polygon.Min(point => point.Y);
            var cropEnvelope = new Extents3d(
                new Point3d(minX, minY, 0.0),
                new Point3d(polygon.Max(point => point.X), polygon.Max(point => point.Y), 0.0));
            var displacement = insertionPoint - new Point3d(minX, minY, 0.0);
            var transform = Matrix3d.Displacement(displacement);
            var rootsToClone = new List<ObjectId>();
            var crossingOrdinary = new List<ObjectId>();
            var ignored = 0;
            var tianzhengRoots = 0;
            var ordinaryRoots = 0;
            var clippedPieces = 0;
            var blockPieces = 0;
            var blockers = new WorkingCopyAssemblyResult();
            var wallTrimPickPoints = new List<Point3d>();
            var crossingWallSourceIds = new HashSet<ObjectId>();
            var copiedCrossingWallIds = new List<ObjectId>();
            ObjectId cropBoundaryId = ObjectId.Null;
            var candidateIds = SelectCropCandidates(document, definition.CropBoundaryPoints);

            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var space = transaction.GetObject(document.Database.CurrentSpaceId,
                    OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) throw new InvalidOperationException("无法打开小平面工作副本目标空间。");

                // Snapshot before appending. Newly generated objects must never
                // enter the same extraction pass.
                var sourceIds = candidateIds.Count > 0
                    ? candidateIds
                    : space.Cast<ObjectId>().ToList();
                foreach (var objectId in sourceIds)
                {
                    var entity = transaction.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity.IsErased || !entity.Visible) continue;
                    if (!string.IsNullOrWhiteSpace(definition.BoundarySourceHandle)
                        && string.Equals(entity.Handle.ToString(), definition.BoundarySourceHandle,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    Extents3d extents;
                    if (!TryGetExtents(entity, out extents) || !Intersects(cropEnvelope, extents)) continue;
                    var classification = ClassifyExtents(extents, polygon);
                    if (classification == BoundaryClassification.Outside) continue;
                    var identity = SafeDxfName(entity).ToUpperInvariant();
                    if (!identity.Contains("TCH_") && !identity.Contains("TDB"))
                        identity = (identity + " " + SafeComProperty(entity, "ObjectName"))
                            .ToUpperInvariant();
                    var isRoomSpace = identity.Contains("TCH_SPACE") || identity.Contains("TDBSPACE");
                    var isOpening = identity.Contains("TCH_OPENING") || identity.Contains("TDBOPENING");
                    var isWall = identity.Contains("TCH_WALL") || identity.Contains("TDBWALL");
                    var isStair = identity.Contains("TCH_RECTSTAIR")
                        || identity.Contains("TDBRECTSTAIR")
                        || (identity.Contains("TCH_") && identity.Contains("STAIR"));
                    var isTianzheng = identity.Contains("TCH_") || identity.Contains("TDB");

                    if (isRoomSpace)
                    {
                        // Room name/area is an indivisible annotation. Keep it
                        // only when its representative centre is inside; never
                        // clip or retain a room whose label belongs outside.
                        var center = new WL.Stair.Core.Geometry.Point2D(
                            (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                            (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5);
                        if (PlanPolygonClipper.Contains(center, corePolygon))
                        {
                            rootsToClone.Add(objectId);
                            tianzhengRoots++;
                        }
                        else ignored++;
                    }
                    else if (isOpening)
                    {
                        // A door/window touching the crop line is retained as a
                        // complete Tianzheng object.
                        rootsToClone.Add(objectId);
                        tianzhengRoots++;
                    }
                    else if (isWall || isStair)
                    {
                        // A Tianzheng wall/opening/stair association must be
                        // cloned in one batch. Crossing professional objects are
                        // kept complete at this validation stage; physical
                        // clipping is deliberately deferred.
                        rootsToClone.Add(objectId);
                        tianzhengRoots++;
                        if (classification == BoundaryClassification.Crossing
                            && isWall)
                        {
                            crossingWallSourceIds.Add(objectId);
                            CollectWallTrimPickPoints(entity as Curve,
                                definition.CropBoundaryPoints,
                                transform,
                                wallTrimPickPoints);
                        }
                    }
                    else if (isTianzheng)
                    {
                        // Other professional annotations/objects must be fully
                        // inside. Crossing objects are not useful in the small
                        // plan and are the main source of unnecessary work.
                        if (classification == BoundaryClassification.Inside)
                        {
                            rootsToClone.Add(objectId);
                            tianzhengRoots++;
                        }
                        else ignored++;
                    }
                    else if (classification == BoundaryClassification.Inside)
                    {
                        rootsToClone.Add(objectId);
                        ordinaryRoots++;
                    }
                    else
                    {
                        crossingOrdinary.Add(objectId);
                    }
                }

                if (rootsToClone.Count > 0)
                {
                    var mapping = new IdMapping();
                    var distinctRoots = rootsToClone.Distinct().ToList();
                    document.Database.DeepCloneObjects(
                        new ObjectIdCollection(distinctRoots.ToArray()),
                        document.Database.CurrentSpaceId,
                        mapping,
                        false);
                    var rootSet = new HashSet<ObjectId>(distinctRoots);
                    foreach (IdPair pair in mapping)
                    {
                        if (!pair.IsCloned || !rootSet.Contains(pair.Key)) continue;
                        var clone = transaction.GetObject(pair.Value, OpenMode.ForWrite, false) as Entity;
                        if (clone != null) clone.TransformBy(transform);
                        if (crossingWallSourceIds.Contains(pair.Key))
                            copiedCrossingWallIds.Add(pair.Value);
                    }
                }

                foreach (var objectId in crossingOrdinary.Distinct())
                {
                    var entity = transaction.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                    if (entity == null) continue;
                    var block = entity as BlockReference;
                    if (block != null)
                    {
                        var count = AppendExplodedBlockPieces(block,
                            definition.CropBoundaryPoints, space, transaction, blockers, 0,
                            transform, true);
                        if (count >= 0) blockPieces += count;
                        else ignored++;
                        continue;
                    }
                    var segments = PreviewClipOrdinaryEntity(entity, definition.CropBoundaryPoints);
                    if (segments == null)
                    {
                        ignored++;
                        blockers.AddBlocker(entity);
                        continue;
                    }
                    clippedPieces += AppendOrdinaryClipPieces(entity, segments,
                        space, transaction, transform, true);
                }

                var boundary = new Autodesk.AutoCAD.DatabaseServices.Polyline(
                    definition.CropBoundaryPoints.Count)
                {
                    Closed = true,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, 1),
                    LinetypeScale = Math.Max(1, definition.TargetScale)
                };
                boundary.SetDatabaseDefaults(document.Database);
                boundary.Layer = EnsureTemporaryTrimLayer(document.Database, transaction);
                TryAssignDashedLinetype(document.Database, transaction, boundary);
                foreach (var point in definition.CropBoundaryPoints)
                    boundary.AddVertexAt(boundary.NumberOfVertices,
                        new Point2d(point.X + displacement.X, point.Y + displacement.Y),
                        0.0, 0.0, 0.0);
                space.AppendEntity(boundary);
                transaction.AddNewlyCreatedDBObject(boundary, true);
                cropBoundaryId = boundary.ObjectId;

                var width = polygon.Max(point => point.X) - minX;
                var titleCenter = new Point3d(
                    insertionPoint.X + width * 0.5,
                    insertionPoint.Y - Math.Max(180.0, definition.TargetScale * 12.0),
                    insertionPoint.Z);
                StairTitleService.Insert(document.Database, space, transaction,
                    titleCenter,
                    string.IsNullOrWhiteSpace(title) ? "楼梯平面图" : title,
                    definition.TargetScale > 0 ? definition.TargetScale : 30,
                    width,
                    "0");

                transaction.Commit();
            }

            var trimmedWallSides = TrimCopiedTianzhengWalls(
                document,
                cropBoundaryId,
                wallTrimPickPoints,
                copiedCrossingWallIds,
                definition.CropBoundaryPoints,
                transform,
                blockers);
            RestoreDisplayCropBoundary(document, definition, displacement);

            var summary = string.Format(CultureInfo.CurrentCulture,
                "小平面工作副本已生成：天正关联对象 {0} 个、框内普通对象 {1} 个、裁剪普通线段 {2} 条、分解裁剪块内对象 {3} 个、TRIM 修剪天正墙外伸段 {4} 处；暂不支持的穿越对象 {5} 个。源平面未移动、未删除。",
                tianzhengRoots, ordinaryRoots, clippedPieces, blockPieces,
                trimmedWallSides, ignored);
            AppendWorkingCopyLog(definition, summary, blockers);
            document.Editor.WriteMessage("\n" + summary + "\n");
            return summary;
        }

        private static List<ObjectId> SelectCropCandidates(
            Document document,
            IList<StairPlanPointDefinition> cropBoundary)
        {
            var result = new List<ObjectId>();
            if (document == null || cropBoundary == null || cropBoundary.Count < 3) return result;
            try
            {
                // Editor polygon selection uses the current UCS. Let AutoCAD's
                // spatial selection engine restrict the expensive inspection to
                // objects inside or touching the crop polygon.
                var worldToUcs = document.Editor.CurrentUserCoordinateSystem.Inverse();
                var points = new Point3dCollection();
                foreach (var point in cropBoundary)
                    points.Add(new Point3d(point.X, point.Y, 0.0).TransformBy(worldToUcs));
                var selection = document.Editor.SelectCrossingPolygon(points);
                if (selection.Status != PromptStatus.OK || selection.Value == null) return result;
                result.AddRange(selection.Value.GetObjectIds()
                    .Where(id => !id.IsNull && id.IsValid)
                    .Distinct());
            }
            catch
            {
                // Some AutoCAD display states reject polygon selection when a
                // vertex is outside the current view. The caller retains the
                // safe whole-space fallback for that exceptional case.
            }
            return result;
        }

        private static string EnsureTemporaryTrimLayer(
            Database database,
            Transaction transaction)
        {
            if (database == null || transaction == null) return "0";
            var table = transaction.GetObject(database.LayerTableId,
                OpenMode.ForRead, false) as LayerTable;
            if (table == null) return "0";
            if (!table.Has(TemporaryTrimLayer))
            {
                table.UpgradeOpen();
                var record = new LayerTableRecord
                {
                    Name = TemporaryTrimLayer,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, 1),
                    IsPlottable = false
                };
                table.Add(record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            else
            {
                var record = transaction.GetObject(table[TemporaryTrimLayer],
                    OpenMode.ForWrite, false) as LayerTableRecord;
                if (record != null)
                {
                    record.IsPlottable = false;
                    record.IsLocked = false;
                }
            }
            return TemporaryTrimLayer;
        }

        private static void SetTemporaryTrimLayerLocked(
            Database database,
            Transaction transaction,
            bool locked)
        {
            if (database == null || transaction == null) return;
            var table = transaction.GetObject(database.LayerTableId,
                OpenMode.ForRead, false) as LayerTable;
            if (table == null || !table.Has(TemporaryTrimLayer)) return;
            var record = transaction.GetObject(table[TemporaryTrimLayer],
                OpenMode.ForWrite, false) as LayerTableRecord;
            if (record != null) record.IsLocked = locked;
        }

        private static void RestoreDisplayCropBoundary(
            Document document,
            StairPlanSourceDefinition definition,
            Vector3d displacement)
        {
            if (document == null || definition == null
                || definition.CropBoundaryPoints == null
                || definition.CropBoundaryPoints.Count < 3) return;
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var space = transaction.GetObject(document.Database.CurrentSpaceId,
                    OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                SetTemporaryTrimLayerLocked(document.Database, transaction, false);

                // TRIM may replace/split the temporary cutting polyline. Remove
                // every fragment on the dedicated non-plot layer, not only the
                // original ObjectId.
                foreach (var id in space.Cast<ObjectId>().ToList())
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity.IsErased
                        || !string.Equals(entity.Layer, TemporaryTrimLayer,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    entity.UpgradeOpen();
                    entity.Erase();
                }

                // The visible crop frame is created only after TRIM. Therefore
                // it cannot be damaged by the command and always remains a
                // complete, closed, red dashed polyline on top of the copy.
                var boundary = new Autodesk.AutoCAD.DatabaseServices.Polyline(
                    definition.CropBoundaryPoints.Count)
                {
                    Closed = true,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, 1),
                    LinetypeScale = Math.Max(1, definition.TargetScale)
                };
                boundary.SetDatabaseDefaults(document.Database);
                TryAssignDashedLinetype(document.Database, transaction, boundary);
                foreach (var point in definition.CropBoundaryPoints)
                    boundary.AddVertexAt(boundary.NumberOfVertices,
                        new Point2d(point.X + displacement.X, point.Y + displacement.Y),
                        0.0, 0.0, 0.0);
                space.AppendEntity(boundary);
                transaction.AddNewlyCreatedDBObject(boundary, true);
                transaction.Commit();
            }
            document.Editor.Regen();
        }

        private static void CollectWallTrimPickPoints(
            Curve wall,
            IList<StairPlanPointDefinition> cropBoundary,
            Matrix3d transform,
            IList<Point3d> result)
        {
            if (wall == null || cropBoundary == null || cropBoundary.Count < 3 || result == null) return;
            try
            {
                var polygon = cropBoundary
                    .Select(point => new WL.Stair.Core.Geometry.Point2D(point.X, point.Y))
                    .ToList();
                var start = wall.StartPoint;
                var end = wall.EndPoint;
                var clipped = PlanPolygonClipper.ClipSegment(
                    new WL.Stair.Core.Geometry.Point2D(start.X, start.Y),
                    new WL.Stair.Core.Geometry.Point2D(end.X, end.Y),
                    polygon);
                if (clipped == null || clipped.Count == 0) return;

                AddOutsideWallPickPoint(start, end, clipped, polygon, transform, result);
                AddOutsideWallPickPoint(end, start, clipped, polygon, transform, result);
            }
            catch
            {
                // The wall stays complete when no reliable outside pick point
                // can be derived. Never fall back to touching the source wall.
            }
        }

        private static void AddOutsideWallPickPoint(
            Point3d endpoint,
            Point3d otherEndpoint,
            IList<PlanClipSegment> clipped,
            IList<WL.Stair.Core.Geometry.Point2D> polygon,
            Matrix3d transform,
            IList<Point3d> result)
        {
            var sourcePoint = new WL.Stair.Core.Geometry.Point2D(endpoint.X, endpoint.Y);
            if (PlanPolygonClipper.Contains(sourcePoint, polygon)) return;
            var candidates = clipped
                .SelectMany(segment => new[] { segment.Start, segment.End })
                .OrderBy(point => DistanceSquared(point.X, point.Y, endpoint.X, endpoint.Y))
                .ToList();
            if (candidates.Count == 0) return;
            var boundaryPoint = candidates[0];
            var pick = new Point3d(
                (endpoint.X + boundaryPoint.X) * 0.5,
                (endpoint.Y + boundaryPoint.Y) * 0.5,
                endpoint.Z);
            // Degenerate very short outside tails are still selectable a little
            // away from the crop edge.
            if (pick.DistanceTo(endpoint) < 0.01)
                pick = endpoint + (otherEndpoint - endpoint) * 0.1;
            pick = pick.TransformBy(transform);
            if (result.All(existing => existing.DistanceTo(pick) > 0.1)) result.Add(pick);
        }

        private static double DistanceSquared(double x1, double y1, double x2, double y2)
        {
            var dx = x1 - x2;
            var dy = y1 - y2;
            return dx * dx + dy * dy;
        }

        private static int TrimCopiedTianzhengWalls(
            Document document,
            ObjectId cropBoundaryId,
            IList<Point3d> pickPoints,
            IList<ObjectId> copiedWallIds,
            IList<StairPlanPointDefinition> sourceBoundary,
            Matrix3d transform,
            WorkingCopyAssemblyResult result)
        {
            if (document == null || copiedWallIds == null || copiedWallIds.Count == 0
                || sourceBoundary == null || sourceBoundary.Count < 3) return 0;
            var targetPolygon = sourceBoundary
                .Select(point => new Point3d(point.X, point.Y, 0.0).TransformBy(transform))
                .Select(point => new WL.Stair.Core.Geometry.Point2D(point.X, point.Y))
                .ToList();
            var before = CountOutsideWallEndpoints(document.Database, copiedWallIds, targetPolygon);
            try
            {
                // Never invoke the interactive TRIM command while building a
                // background cache. A Tianzheng wall can refresh between pick
                // points; one stale point makes TRIM enter window selection and
                // leaves CAD waiting for the user. Work only on the generated
                // copies and split their curve wrappers at crop intersections.
                // Fully outside remnants are erased first; inside pieces retain
                // their native/proxy identity whenever GetSplitCurves supports it.
                EraseCopiedWallRemnantsOutsideCrop(
                    document.Database, copiedWallIds, targetPolygon);
                SplitRemainingCopiedWallsToCrop(
                    document.Database, copiedWallIds, targetPolygon, result);
                EraseCopiedWallRemnantsOutsideCrop(
                    document.Database, copiedWallIds, targetPolygon);
                var after = CountOutsideWallEndpoints(document.Database, copiedWallIds, targetPolygon);
                var trimmed = Math.Max(0, before - after);
                if (after > 0 && result != null && result.Errors.Count < 12)
                    result.Errors.Add("天正墙数据库切段后仍有 " + after + " 个墙端点位于裁切线外");
                document.Editor.Regen();
                return trimmed;
            }
            catch (System.Exception exception)
            {
                if (result != null && result.Errors.Count < 12)
                    result.Errors.Add("天正墙 TRIM 未完成：" + exception.Message);
                try
                {
                    document.Editor.WriteMessage(
                        "\n小平面已生成，但天正墙自动 TRIM 未完成：{0}。源图未受影响。\n",
                        exception.Message);
                }
                catch { }
                return 0;
            }
            finally
            {
                try
                {
                    using (var transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        SetTemporaryTrimLayerLocked(document.Database, transaction, false);
                        transaction.Commit();
                    }
                }
                catch { }
            }
        }

        private static List<Point3d> DistinctValidPoints(IEnumerable<Point3d> points)
        {
            var result = new List<Point3d>();
            if (points == null) return result;
            foreach (var point in points)
            {
                if (double.IsNaN(point.X) || double.IsNaN(point.Y)
                    || double.IsInfinity(point.X) || double.IsInfinity(point.Y)) continue;
                if (result.All(existing => existing.DistanceTo(point) > 0.1)) result.Add(point);
            }
            return result;
        }

        private static List<Point3d> CollectCurrentWallTrimPickPoints(
            Database database,
            IEnumerable<ObjectId> wallIds,
            IList<WL.Stair.Core.Geometry.Point2D> polygon)
        {
            var result = new List<Point3d>();
            if (database == null || wallIds == null || polygon == null || polygon.Count < 3)
                return result;
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in wallIds.Where(value => !value.IsNull && value.IsValid).Distinct())
                {
                    try
                    {
                        var curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve;
                        if (curve == null || curve.IsErased) continue;
                        var start = curve.StartPoint;
                        var end = curve.EndPoint;
                        var clipped = PlanPolygonClipper.ClipSegment(
                            new WL.Stair.Core.Geometry.Point2D(start.X, start.Y),
                            new WL.Stair.Core.Geometry.Point2D(end.X, end.Y),
                            polygon);
                        if (clipped == null || clipped.Count == 0) continue;
                        AddOutsideWallPickPoint(start, end, clipped, polygon,
                            Matrix3d.Identity, result);
                        AddOutsideWallPickPoint(end, start, clipped, polygon,
                            Matrix3d.Identity, result);
                    }
                    catch
                    {
                        // A successfully trimmed Tianzheng wall may replace its
                        // database object. It simply contributes no next-pass
                        // pick point.
                    }
                }
            }
            return DistinctValidPoints(result);
        }

        private static int EraseCopiedWallRemnantsOutsideCrop(
            Database database,
            IEnumerable<ObjectId> wallIds,
            IList<WL.Stair.Core.Geometry.Point2D> polygon)
        {
            if (database == null || wallIds == null || polygon == null || polygon.Count < 3)
                return 0;
            var cadPolygon = polygon.Select(point => new Point2d(point.X, point.Y)).ToList();
            var erased = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var id in wallIds.Where(value => !value.IsNull && value.IsValid).Distinct())
                {
                    try
                    {
                        var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null || entity.IsErased) continue;
                        Extents3d extents;
                        var remove = false;
                        var curve = entity as Curve;
                        if (curve != null)
                        {
                            var clipped = PlanPolygonClipper.ClipSegment(
                                new WL.Stair.Core.Geometry.Point2D(
                                    curve.StartPoint.X, curve.StartPoint.Y),
                                new WL.Stair.Core.Geometry.Point2D(
                                    curve.EndPoint.X, curve.EndPoint.Y),
                                polygon);
                            // A tail can touch the crop line at one endpoint but
                            // still have no positive-length part inside. It is an
                            // outside object and must be deleted, not TRIMmed
                            // again at the same endpoint.
                            var retainedLength = clipped == null ? 0.0 : clipped.Sum(segment =>
                            {
                                var dx = segment.End.X - segment.Start.X;
                                var dy = segment.End.Y - segment.Start.Y;
                                return Math.Sqrt(dx * dx + dy * dy);
                            });
                            remove = retainedLength <= 0.1;
                        }
                        else if (TryGetExtents(entity, out extents))
                        {
                            remove = ClassifyExtents(extents, cadPolygon)
                                == BoundaryClassification.Outside;
                        }
                        if (!remove) continue;
                        entity.UpgradeOpen();
                        entity.Erase();
                        erased++;
                    }
                    catch
                    {
                        // A successful Tianzheng refresh may already have
                        // replaced this generated clone. Never inspect or erase
                        // objects outside the clone mapping of this operation.
                    }
                }
                transaction.Commit();
            }
            return erased;
        }

        private static int SplitRemainingCopiedWallsToCrop(
            Database database,
            IEnumerable<ObjectId> wallIds,
            IList<WL.Stair.Core.Geometry.Point2D> polygon,
            WorkingCopyAssemblyResult result)
        {
            if (database == null || wallIds == null || polygon == null || polygon.Count < 3)
                return 0;
            var replaced = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var space = transaction.GetObject(database.CurrentSpaceId,
                    OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return 0;
                foreach (var id in wallIds.Where(value => !value.IsNull && value.IsValid).Distinct())
                {
                    DBObjectCollection pieces = null;
                    try
                    {
                        var curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve;
                        if (curve == null || curve.IsErased) continue;
                        var start = curve.StartPoint;
                        var end = curve.EndPoint;
                        var start2d = new WL.Stair.Core.Geometry.Point2D(start.X, start.Y);
                        var end2d = new WL.Stair.Core.Geometry.Point2D(end.X, end.Y);
                        if (PlanPolygonClipper.Contains(start2d, polygon)
                            && PlanPolygonClipper.Contains(end2d, polygon)) continue;
                        var clipped = PlanPolygonClipper.ClipSegment(start2d, end2d, polygon);
                        if (clipped == null || clipped.Count == 0) continue;

                        var splitPoints = new Point3dCollection();
                        foreach (var segment in clipped)
                        {
                            AddSplitPoint(curve, start, end, segment.Start, splitPoints);
                            AddSplitPoint(curve, start, end, segment.End, splitPoints);
                        }
                        if (splitPoints.Count == 0) continue;
                        pieces = curve.GetSplitCurves(splitPoints);
                        if (pieces == null || pieces.Count < 2) continue;

                        var insidePieces = new List<Entity>();
                        foreach (DBObject value in pieces)
                        {
                            var piece = value as Curve;
                            if (piece == null) continue;
                            var middle = piece.GetPointAtDist(piece.GetDistanceAtParameter(
                                (piece.StartParam + piece.EndParam) * 0.5));
                            if (PlanPolygonClipper.Contains(
                                new WL.Stair.Core.Geometry.Point2D(middle.X, middle.Y), polygon))
                                insidePieces.Add(piece);
                        }
                        if (insidePieces.Count == 0) continue;

                        foreach (var piece in insidePieces)
                        {
                            space.AppendEntity(piece);
                            transaction.AddNewlyCreatedDBObject(piece, true);
                        }
                        curve.UpgradeOpen();
                        curve.Erase();
                        replaced++;
                    }
                    catch (System.Exception exception)
                    {
                        if (result != null && result.Errors.Count < 12)
                            result.Errors.Add("天正墙红框交点切段未完成：" + exception.Message);
                    }
                    finally
                    {
                        if (pieces != null)
                        {
                            foreach (DBObject piece in pieces)
                            {
                                if (piece != null && piece.ObjectId.IsNull) piece.Dispose();
                            }
                            pieces.Dispose();
                        }
                    }
                }
                transaction.Commit();
            }
            return replaced;
        }

        private static int CountOutsideWallEndpoints(
            Database database,
            IEnumerable<ObjectId> wallIds,
            IList<WL.Stair.Core.Geometry.Point2D> polygon)
        {
            if (database == null || wallIds == null || polygon == null || polygon.Count < 3) return 0;
            var count = 0;
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in wallIds.Where(value => !value.IsNull && value.IsValid).Distinct())
                {
                    try
                    {
                        var curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve;
                        if (curve == null || curve.IsErased) continue;
                        var start = new WL.Stair.Core.Geometry.Point2D(
                            curve.StartPoint.X, curve.StartPoint.Y);
                        var end = new WL.Stair.Core.Geometry.Point2D(
                            curve.EndPoint.X, curve.EndPoint.Y);
                        if (!PlanPolygonClipper.Contains(start, polygon)) count++;
                        if (!PlanPolygonClipper.Contains(end, polygon)) count++;
                    }
                    catch
                    {
                        // TRIM may replace the original custom entity. An erased
                        // source id is not an untrimmed endpoint.
                    }
                }
            }
            return count;
        }

        private static void ProbeTianzhengWallSplit(
            Entity wall,
            IList<StairPlanPointDefinition> cropBoundary,
            TianzhengWallSplitProbeResult result)
        {
            result.Attempted++;
            DBObjectCollection pieces = null;
            try
            {
                var curve = wall as Curve;
                if (curve == null)
                    throw new InvalidOperationException("天正墙包装对象不是可切分曲线");
                var polygon = cropBoundary
                    .Select(point => new WL.Stair.Core.Geometry.Point2D(point.X, point.Y))
                    .ToList();
                var start = curve.StartPoint;
                var end = curve.EndPoint;
                var clipped = PlanPolygonClipper.ClipSegment(
                    new WL.Stair.Core.Geometry.Point2D(start.X, start.Y),
                    new WL.Stair.Core.Geometry.Point2D(end.X, end.Y),
                    polygon);
                if (clipped.Count == 0)
                    throw new InvalidOperationException("墙基线与裁剪框没有有效保留段");

                var splitPoints = new Point3dCollection();
                foreach (var segment in clipped)
                {
                    AddSplitPoint(curve, start, end, segment.Start, splitPoints);
                    AddSplitPoint(curve, start, end, segment.End, splitPoints);
                }
                if (splitPoints.Count == 0)
                {
                    result.NotRequired++;
                    return;
                }

                pieces = curve.GetSplitCurves(splitPoints);
                if (pieces == null || pieces.Count < 2)
                    throw new InvalidOperationException("GetSplitCurves 未返回有效墙段");
                var sourceIdentity = CopyIdentity(wall);
                var preserved = true;
                var hasInsidePiece = false;
                foreach (DBObject value in pieces)
                {
                    var entity = value as Entity;
                    if (entity == null)
                    {
                        preserved = false;
                        continue;
                    }
                    var identity = CopyIdentity(entity);
                    if (!string.Equals(sourceIdentity, identity, StringComparison.OrdinalIgnoreCase))
                    {
                        preserved = false;
                        result.ChangedTypes.Add(sourceIdentity + " -> " + identity);
                    }
                    var pieceCurve = entity as Curve;
                    if (pieceCurve == null) continue;
                    var pieceStart = pieceCurve.StartPoint;
                    var pieceEnd = pieceCurve.EndPoint;
                    var middle = new WL.Stair.Core.Geometry.Point2D(
                        (pieceStart.X + pieceEnd.X) * 0.5,
                        (pieceStart.Y + pieceEnd.Y) * 0.5);
                    if (PlanPolygonClipper.Contains(middle, polygon)) hasInsidePiece = true;
                }
                if (!hasInsidePiece)
                    throw new InvalidOperationException("切段结果中没有可判定的框内墙段");
                if (preserved) result.Preserved++;
                else result.TypeChanged++;
            }
            catch (System.Exception exception)
            {
                result.Failed++;
                if (result.Errors.Count < 8)
                    result.Errors.Add(SafeDxfName(wall) + "/" + wall.Handle + ": " + exception.Message);
            }
            finally
            {
                if (pieces != null)
                {
                    foreach (DBObject piece in pieces)
                        piece.Dispose();
                    pieces.Dispose();
                }
            }
        }

        private static void AddSplitPoint(
            Curve curve,
            Point3d curveStart,
            Point3d curveEnd,
            WL.Stair.Core.Geometry.Point2D segmentPoint,
            Point3dCollection splitPoints)
        {
            var candidate = curve.GetClosestPointTo(
                new Point3d(segmentPoint.X, segmentPoint.Y, curveStart.Z),
                false);
            if (candidate.DistanceTo(curveStart) <= 0.1
                || candidate.DistanceTo(curveEnd) <= 0.1) return;
            foreach (Point3d existing in splitPoints)
            {
                if (candidate.DistanceTo(existing) <= 0.1) return;
            }
            splitPoints.Add(candidate);
        }

        private static IList<PlanClipSegment> PreviewClipOrdinaryEntity(
            Entity entity,
            IList<StairPlanPointDefinition> cropBoundary)
        {
            if (entity == null || cropBoundary == null || cropBoundary.Count < 3) return null;
            var polygon = cropBoundary
                .Select(point => new WL.Stair.Core.Geometry.Point2D(point.X, point.Y))
                .ToList();
            var line = entity as Line;
            if (line != null)
            {
                return PlanPolygonClipper.ClipSegment(
                    new WL.Stair.Core.Geometry.Point2D(line.StartPoint.X, line.StartPoint.Y),
                    new WL.Stair.Core.Geometry.Point2D(line.EndPoint.X, line.EndPoint.Y),
                    polygon);
            }

            var polyline = entity as Autodesk.AutoCAD.DatabaseServices.Polyline;
            if (polyline == null || polyline.NumberOfVertices < 2) return null;
            var segmentCount = polyline.Closed
                ? polyline.NumberOfVertices
                : polyline.NumberOfVertices - 1;
            var result = new List<PlanClipSegment>();
            for (var index = 0; index < segmentCount; index++)
            {
                if (Math.Abs(polyline.GetBulgeAt(index)) > 0.000001) return null;
                var next = (index + 1) % polyline.NumberOfVertices;
                var start = polyline.GetPoint2dAt(index);
                var end = polyline.GetPoint2dAt(next);
                result.AddRange(PlanPolygonClipper.ClipSegment(
                    new WL.Stair.Core.Geometry.Point2D(start.X, start.Y),
                    new WL.Stair.Core.Geometry.Point2D(end.X, end.Y),
                    polygon));
            }
            return result;
        }

        private static WorkingCopyAssemblyResult ValidateWorkingCopyAssembly(
            Document document,
            StairPlanSourceDefinition definition,
            IEnumerable<ObjectId> insideIds,
            IEnumerable<ObjectId> crossingIds)
        {
            var result = new WorkingCopyAssemblyResult();
            try
            {
                using (document.LockDocument())
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    var space = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (space == null) throw new InvalidOperationException("无法打开工作副本目标空间");

                    var inside = insideIds == null
                        ? new List<ObjectId>()
                        : insideIds.Where(id => !id.IsNull && id.IsValid).Distinct().ToList();
                    var insideToClone = new List<ObjectId>();
                    foreach (var id in inside)
                    {
                        var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity != null && IsTianzhengOpening(entity))
                        {
                            result.TianzhengOpeningPreserved++;
                            continue;
                        }
                        if (entity != null && IsTianzhengWall(entity))
                        {
                            result.TianzhengWallPreserved++;
                            continue;
                        }
                        if (entity != null && IsTianzhengObject(entity))
                        {
                            result.TianzhengInsidePreserved++;
                            continue;
                        }
                        insideToClone.Add(id);
                    }
                    if (insideToClone.Count > 0)
                    {
                        var mapping = new IdMapping();
                        document.Database.DeepCloneObjects(
                            new ObjectIdCollection(insideToClone.ToArray()),
                            document.Database.CurrentSpaceId,
                            mapping,
                            false);
                        var sourceSet = new HashSet<ObjectId>(insideToClone);
                        foreach (IdPair pair in mapping)
                        {
                            if (pair.IsCloned && sourceSet.Contains(pair.Key)) result.InsideClones++;
                        }
                    }

                    foreach (var id in crossingIds == null
                        ? Enumerable.Empty<ObjectId>()
                        : crossingIds.Where(value => !value.IsNull && value.IsValid).Distinct())
                    {
                        var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null) continue;
                        try
                        {
                            if (IsTianzhengWall(entity))
                            {
                                // Preview must not split or clone Tianzheng
                                // walls. Wall operations can invalidate the
                                // associated door/window graphics cache even
                                // when the transaction is rolled back.
                                result.TianzhengWallPreserved++;
                                continue;
                            }
                            else if (IsTianzhengOpening(entity))
                            {
                                // Strict no-touch preview rule: Tianzheng doors
                                // and windows that are inside or touch the crop
                                // boundary are retained by policy, but are not
                                // even temporarily cloned. DeepClone can disturb
                                // the wall-opening graphics cache after rollback.
                                result.TianzhengOpeningPreserved++;
                                continue;
                            }
                            else if (entity is BlockReference && !IsTianzhengObject(entity))
                            {
                                var blockPieces = AppendExplodedBlockPieces(
                                    (BlockReference)entity,
                                    definition.CropBoundaryPoints,
                                    space,
                                    transaction,
                                    result,
                                    0);
                                if (blockPieces >= 0)
                                {
                                    result.OrdinaryBlockPieces += blockPieces;
                                    continue;
                                }
                            }
                            else if (!IsTianzhengObject(entity))
                            {
                                var segments = PreviewClipOrdinaryEntity(entity, definition.CropBoundaryPoints);
                                if (segments != null)
                                {
                                    result.OrdinaryPieces += AppendOrdinaryClipPieces(
                                        entity,
                                        segments,
                                        space,
                                        transaction);
                                    continue;
                                }
                            }
                            result.AddBlocker(entity);
                        }
                        catch (System.Exception exception)
                        {
                            result.AddBlocker(entity);
                            if (result.Errors.Count < 12)
                                result.Errors.Add(SafeDxfName(entity) + "/" + entity.Handle + ": " + exception.Message);
                        }
                    }
                    // Intentionally no Commit(): this validates the exact candidate
                    // subset without adding anything to the source drawing.
                }
            }
            catch (System.Exception exception)
            {
                result.FatalError = exception.Message;
            }
            return result;
        }

        private static int AppendOrdinaryClipPieces(
            Entity source,
            IEnumerable<PlanClipSegment> segments,
            BlockTableRecord target,
            Transaction transaction,
            Matrix3d transform = default(Matrix3d),
            bool applyTransform = false)
        {
            var count = 0;
            foreach (var segment in segments ?? Enumerable.Empty<PlanClipSegment>())
            {
                Entity piece;
                var sourceLine = source as Line;
                if (sourceLine != null)
                {
                    var clone = (Line)sourceLine.Clone();
                    clone.StartPoint = new Point3d(segment.Start.X, segment.Start.Y, sourceLine.StartPoint.Z);
                    clone.EndPoint = new Point3d(segment.End.X, segment.End.Y, sourceLine.EndPoint.Z);
                    piece = clone;
                }
                else
                {
                    var sourcePolyline = source as Autodesk.AutoCAD.DatabaseServices.Polyline;
                    if (sourcePolyline == null) continue;
                    var polyline = new Autodesk.AutoCAD.DatabaseServices.Polyline(2);
                    polyline.SetDatabaseDefaults(target.Database);
                    polyline.SetPropertiesFrom(sourcePolyline);
                    polyline.Elevation = sourcePolyline.Elevation;
                    polyline.AddVertexAt(0, new Point2d(segment.Start.X, segment.Start.Y), 0.0, 0.0, 0.0);
                    polyline.AddVertexAt(1, new Point2d(segment.End.X, segment.End.Y), 0.0, 0.0, 0.0);
                    piece = polyline;
                }
                if (applyTransform) piece.TransformBy(transform);
                target.AppendEntity(piece);
                transaction.AddNewlyCreatedDBObject(piece, true);
                count++;
            }
            return count;
        }

        private static int AppendExplodedBlockPieces(
            BlockReference block,
            IList<StairPlanPointDefinition> cropBoundary,
            BlockTableRecord target,
            Transaction transaction,
            WorkingCopyAssemblyResult result,
            int depth,
            Matrix3d transform = default(Matrix3d),
            bool applyTransform = false)
        {
            if (block == null || depth > 8) return -1;
            var polygon = cropBoundary
                .Select(point => new Point2d(point.X, point.Y))
                .ToList();
            var exploded = new DBObjectCollection();
            var appended = 0;
            try
            {
                block.Explode(exploded);
                foreach (DBObject value in exploded)
                {
                    var entity = value as Entity;
                    if (entity == null)
                    {
                        if (value != null) value.Dispose();
                        continue;
                    }
                    var nested = entity as BlockReference;
                    if (nested != null)
                    {
                        var nestedCount = AppendExplodedBlockPieces(
                            nested,
                            cropBoundary,
                            target,
                            transaction,
                            result,
                            depth + 1,
                            transform,
                            applyTransform);
                        if (nestedCount < 0) result.AddBlocker(entity);
                        else appended += nestedCount;
                        nested.Dispose();
                        continue;
                    }

                    Extents3d extents;
                    if (!TryGetExtents(entity, out extents))
                    {
                        result.AddBlocker(entity);
                        entity.Dispose();
                        continue;
                    }
                    var classification = ClassifyExtents(extents, polygon);
                    if (classification == BoundaryClassification.Outside)
                    {
                        entity.Dispose();
                        continue;
                    }
                    if (classification == BoundaryClassification.Inside)
                    {
                        if (applyTransform) entity.TransformBy(transform);
                        target.AppendEntity(entity);
                        transaction.AddNewlyCreatedDBObject(entity, true);
                        appended++;
                        continue;
                    }

                    var segments = PreviewClipOrdinaryEntity(entity, cropBoundary);
                    if (segments != null)
                    {
                        appended += AppendOrdinaryClipPieces(entity, segments, target, transaction,
                            transform, applyTransform);
                        entity.Dispose();
                        continue;
                    }
                    result.AddBlocker(entity);
                    entity.Dispose();
                }
            }
            finally
            {
                exploded.Dispose();
            }
            return appended;
        }

        private static int AppendClippedTianzhengWallPieces(
            Entity wall,
            IList<StairPlanPointDefinition> cropBoundary,
            BlockTableRecord target,
            Transaction transaction)
        {
            var curve = wall as Curve;
            if (curve == null) return 0;
            var polygon = cropBoundary
                .Select(point => new WL.Stair.Core.Geometry.Point2D(point.X, point.Y))
                .ToList();
            var start = curve.StartPoint;
            var end = curve.EndPoint;
            var clipped = PlanPolygonClipper.ClipSegment(
                new WL.Stair.Core.Geometry.Point2D(start.X, start.Y),
                new WL.Stair.Core.Geometry.Point2D(end.X, end.Y),
                polygon);
            if (clipped.Count == 0) return 0;
            var splitPoints = new Point3dCollection();
            foreach (var segment in clipped)
            {
                AddSplitPoint(curve, start, end, segment.Start, splitPoints);
                AddSplitPoint(curve, start, end, segment.End, splitPoints);
            }
            if (splitPoints.Count == 0) return 0;

            var pieces = curve.GetSplitCurves(splitPoints);
            var appended = 0;
            try
            {
                foreach (DBObject value in pieces)
                {
                    var entity = value as Entity;
                    var pieceCurve = entity as Curve;
                    if (entity == null || pieceCurve == null)
                    {
                        if (value != null) value.Dispose();
                        continue;
                    }
                    var pieceStart = pieceCurve.StartPoint;
                    var pieceEnd = pieceCurve.EndPoint;
                    var middle = new WL.Stair.Core.Geometry.Point2D(
                        (pieceStart.X + pieceEnd.X) * 0.5,
                        (pieceStart.Y + pieceEnd.Y) * 0.5);
                    if (!PlanPolygonClipper.Contains(middle, polygon))
                    {
                        entity.Dispose();
                        continue;
                    }
                    if (!string.Equals(CopyIdentity(wall), CopyIdentity(entity), StringComparison.OrdinalIgnoreCase))
                    {
                        entity.Dispose();
                        throw new InvalidOperationException("天正墙切段后对象类型发生变化");
                    }
                    target.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    appended++;
                }
            }
            finally
            {
                pieces.Dispose();
            }
            return appended;
        }

        private static CopyProbeResult ProbeCopyCompatibility(
            Document document,
            IEnumerable<ObjectId> sourceIds)
        {
            var result = new CopyProbeResult();
            var candidates = sourceIds == null
                ? new List<ObjectId>()
                : sourceIds.Where(id => !id.IsNull && id.IsValid).Distinct().ToList();
            if (candidates.Count == 0)
            {
                result.Summary = "没有可验证对象";
                return result;
            }

            // DeepCloneObjects is deliberately executed inside an uncommitted
            // transaction. AutoCAD/Tianzheng can fully instantiate the working
            // copies so their runtime types can be checked, while disposing the
            // transaction rolls every clone back and leaves the source drawing
            // byte-for-byte untouched.
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var sourceTypes = new Dictionary<ObjectId, string>();
                foreach (var id in candidates)
                {
                    var source = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (source != null) sourceTypes[id] = CopyIdentity(source);
                }

                var mapping = new IdMapping();
                try
                {
                    document.Database.DeepCloneObjects(
                        new ObjectIdCollection(sourceTypes.Keys.ToArray()),
                        document.Database.CurrentSpaceId,
                        mapping,
                        false);
                    foreach (IdPair pair in mapping)
                    {
                        if (!pair.IsCloned || !sourceTypes.ContainsKey(pair.Key)) continue;
                        result.Attempted++;
                        var clone = transaction.GetObject(pair.Value, OpenMode.ForRead, false) as Entity;
                        if (clone == null)
                        {
                            result.Failed++;
                            continue;
                        }
                        var cloneIdentity = CopyIdentity(clone);
                        if (string.Equals(sourceTypes[pair.Key], cloneIdentity, StringComparison.OrdinalIgnoreCase))
                            result.Preserved++;
                        else
                        {
                            result.TypeChanged++;
                            result.ChangedTypes.Add(sourceTypes[pair.Key] + " -> " + cloneIdentity);
                        }
                    }
                }
                catch (System.Exception exception)
                {
                    result.Failed += Math.Max(1, sourceTypes.Count - result.Attempted);
                    result.Error = exception.Message;
                }
                // Intentionally no Commit().
            }

            result.Summary = result.Failed == 0 && result.TypeChanged == 0
                ? string.Format(CultureInfo.CurrentCulture, "{0} 类对象复制后类型保持", result.Preserved)
                : string.Format(CultureInfo.CurrentCulture,
                    "保持 {0} 类，类型变化 {1} 类，失败 {2} 类{3}",
                    result.Preserved,
                    result.TypeChanged,
                    result.Failed,
                    string.IsNullOrWhiteSpace(result.Error) ? string.Empty : "（" + result.Error + "）");
            return result;
        }

        private static string CopyIdentity(Entity entity)
        {
            if (entity == null) return string.Empty;
            return SafeDxfName(entity) + "|" + entity.GetType().FullName;
        }

        private static BoundaryClassification ClassifyExtents(
            Extents3d extents,
            IList<Point2d> polygon)
        {
            var corners = new[]
            {
                new Point2d(extents.MinPoint.X, extents.MinPoint.Y),
                new Point2d(extents.MaxPoint.X, extents.MinPoint.Y),
                new Point2d(extents.MaxPoint.X, extents.MaxPoint.Y),
                new Point2d(extents.MinPoint.X, extents.MaxPoint.Y)
            };
            var insideCount = corners.Count(point => IsPointInPolygon(point, polygon));
            if (insideCount == corners.Length) return BoundaryClassification.Inside;
            if (insideCount > 0) return BoundaryClassification.Crossing;
            if (polygon.Any(point => point.X >= extents.MinPoint.X && point.X <= extents.MaxPoint.X
                && point.Y >= extents.MinPoint.Y && point.Y <= extents.MaxPoint.Y))
                return BoundaryClassification.Crossing;
            for (var polygonIndex = 0; polygonIndex < polygon.Count; polygonIndex++)
            {
                var first = polygon[polygonIndex];
                var second = polygon[(polygonIndex + 1) % polygon.Count];
                for (var boxIndex = 0; boxIndex < corners.Length; boxIndex++)
                {
                    if (SegmentsIntersect(
                        first,
                        second,
                        corners[boxIndex],
                        corners[(boxIndex + 1) % corners.Length]))
                        return BoundaryClassification.Crossing;
                }
            }
            return BoundaryClassification.Outside;
        }

        private static bool IsPointInPolygon(Point2d point, IList<Point2d> polygon)
        {
            var inside = false;
            for (var index = 0; index < polygon.Count; index++)
            {
                var previous = (index + polygon.Count - 1) % polygon.Count;
                var current = polygon[index];
                var prior = polygon[previous];
                if (Math.Abs(Cross(prior, current, point)) < 0.001
                    && point.X >= Math.Min(prior.X, current.X) - 0.001
                    && point.X <= Math.Max(prior.X, current.X) + 0.001
                    && point.Y >= Math.Min(prior.Y, current.Y) - 0.001
                    && point.Y <= Math.Max(prior.Y, current.Y) + 0.001)
                    return true;
                if ((current.Y > point.Y) != (prior.Y > point.Y)
                    && point.X < (prior.X - current.X) * (point.Y - current.Y)
                        / (prior.Y - current.Y) + current.X)
                    inside = !inside;
            }
            return inside;
        }

        private static bool SegmentsIntersect(Point2d a, Point2d b, Point2d c, Point2d d)
        {
            var abC = Cross(a, b, c);
            var abD = Cross(a, b, d);
            var cdA = Cross(c, d, a);
            var cdB = Cross(c, d, b);
            return ((abC <= 0.0 && abD >= 0.0) || (abC >= 0.0 && abD <= 0.0))
                && ((cdA <= 0.0 && cdB >= 0.0) || (cdA >= 0.0 && cdB <= 0.0));
        }

        private static double Cross(Point2d a, Point2d b, Point2d c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        private static bool IsTianzhengObject(Entity entity)
        {
            var identity = (SafeDxfName(entity) + " " + SafeComProperty(entity, "ObjectName"))
                .ToUpperInvariant();
            return identity.Contains("TCH_") || identity.Contains("TDB");
        }

        private static StairPlanSourceDefinition BuildManualDefinition(
            Document document,
            Entity stair,
            Autodesk.AutoCAD.DatabaseServices.Polyline boundary,
            string storeyId,
            string displayName,
            double cropOffset)
        {
            IList<Point2d> points;
            if (!TryReadPolylineBoundary(boundary, out points))
                throw new InvalidOperationException("手动边界必须是至少3个顶点、仅含直线段的闭合二维多段线。");
            return BuildManualDefinition(
                document,
                stair,
                points,
                boundary.Handle.ToString(),
                storeyId,
                displayName,
                cropOffset);
        }

        private static StairPlanSourceDefinition BuildManualDefinition(
            Document document,
            Entity stair,
            IList<Point2d> points,
            string boundaryHandle,
            string storeyId,
            string displayName,
            double cropOffset)
        {
            if (points == null || points.Count < 3)
                throw new InvalidOperationException("手动边界至少需要3个有效顶点。");
            var offset = cropOffset > 0.0 ? cropOffset : 300.0;
            var cropPoints = OffsetClosedPolygon(points, offset);
            if (cropPoints == null || cropPoints.Count != points.Count)
                throw new InvalidOperationException("闭合多段线无法可靠向外偏移，请检查是否存在自交或重复顶点。");

            return new StairPlanSourceDefinition
            {
                StoreyId = storeyId ?? string.Empty,
                DisplayName = displayName ?? string.Empty,
                Mode = stair == null
                    ? StairPlanSourceMode.ManualPolyline
                    : StairPlanSourceMode.TianzhengStairWithManualBoundary,
                SourceDrawing = SafeDocumentName(document),
                SourceDrawingFingerprint = SafeFingerprint(document.Database),
                SourceHandle = stair == null ? boundaryHandle ?? string.Empty : stair.Handle.ToString(),
                BoundarySourceHandle = boundaryHandle ?? string.Empty,
                SourceDxfName = stair == null ? "MANUAL_BOUNDARY" : SafeDxfName(stair),
                SourceComType = stair == null
                    ? "框选/闭合多段线"
                    : SafeComProperty(stair, "ObjectName"),
                SourceScale = stair == null ? 0 : ToInt(SafeComValue(stair, "Scale"), 0),
                StairWidth = stair == null ? 0.0 : ToDouble(SafeComValue(stair, "StairWidth"), 0.0),
                CropOffset = offset,
                RecognitionSummary = stair == null
                    ? "使用用户闭合多段线作为内边界。"
                    : "已识别天正楼梯；墙轴线未可靠闭合，改用用户闭合多段线作为内边界。",
                BoundaryPoints = points.Select(point =>
                    new StairPlanPointDefinition(point.X, point.Y)).ToList(),
                CropBoundaryPoints = cropPoints.Select(point =>
                    new StairPlanPointDefinition(point.X, point.Y)).ToList(),
                WallAxes = new List<StairPlanWallAxisDefinition>()
            };
        }

        private static bool TryPromptManualBoundary(
            Editor editor,
            Transaction transaction,
            out IList<Point2d> points,
            out string boundaryHandle)
        {
            points = null;
            boundaryHandle = string.Empty;
            var modeOptions = new PromptKeywordOptions(
                "\n选择内边界取得方式 [框选(F)/闭合多段线(P)/取消(C)] <框选>: ");
            modeOptions.AllowNone = true;
            modeOptions.Keywords.Add("Frame", "F", "框选");
            modeOptions.Keywords.Add("Polyline", "P", "闭合多段线");
            modeOptions.Keywords.Add("Cancel", "C", "取消");
            var mode = editor.GetKeywords(modeOptions);
            if (mode.Status == PromptStatus.Cancel
                || (mode.Status == PromptStatus.OK
                    && string.Equals(mode.StringResult, "Cancel", StringComparison.OrdinalIgnoreCase)))
                return false;

            if (mode.Status == PromptStatus.OK
                && string.Equals(mode.StringResult, "Polyline", StringComparison.OrdinalIgnoreCase))
            {
                var entityOptions = new PromptEntityOptions("\n请选择闭合多段线内边界：");
                entityOptions.SetRejectMessage("\n请选择闭合的二维多段线。");
                entityOptions.AddAllowedClass(
                    typeof(Autodesk.AutoCAD.DatabaseServices.Polyline),
                    true);
                var picked = editor.GetEntity(entityOptions);
                if (picked.Status != PromptStatus.OK) return false;
                var boundary = transaction.GetObject(
                    picked.ObjectId,
                    OpenMode.ForRead,
                    false) as Autodesk.AutoCAD.DatabaseServices.Polyline;
                if (!TryReadPolylineBoundary(boundary, out points))
                    throw new InvalidOperationException(
                        "所选边界必须闭合、至少3个顶点，并且暂不包含圆弧段。");
                boundaryHandle = boundary.Handle.ToString();
                return true;
            }

            var first = editor.GetPoint("\n指定内边界第一个角点：");
            if (first.Status != PromptStatus.OK) return false;
            var second = editor.GetCorner(new PromptCornerOptions(
                "\n指定内边界另一个角点：",
                first.Value));
            if (second.Status != PromptStatus.OK) return false;
            var minX = Math.Min(first.Value.X, second.Value.X);
            var minY = Math.Min(first.Value.Y, second.Value.Y);
            var maxX = Math.Max(first.Value.X, second.Value.X);
            var maxY = Math.Max(first.Value.Y, second.Value.Y);
            if (maxX - minX < 1.0 || maxY - minY < 1.0)
                throw new InvalidOperationException("框选范围过小，请重新指定两个角点。");
            points = new List<Point2d>
            {
                new Point2d(minX, minY),
                new Point2d(maxX, minY),
                new Point2d(maxX, maxY),
                new Point2d(minX, maxY)
            };
            return true;
        }

        private static bool TryReadPolylineBoundary(
            Autodesk.AutoCAD.DatabaseServices.Polyline boundary,
            out IList<Point2d> points)
        {
            points = null;
            if (boundary == null || !boundary.Closed || boundary.NumberOfVertices < 3)
                return false;
            var result = new List<Point2d>();
            for (var index = 0; index < boundary.NumberOfVertices; index++)
            {
                if (Math.Abs(boundary.GetBulgeAt(index)) > 0.000001) return false;
                result.Add(boundary.GetPoint2dAt(index));
            }
            points = result;
            return true;
        }

        private static IList<Point2d> OffsetClosedPolygon(IList<Point2d> points, double distance)
        {
            if (points == null || points.Count < 3) return null;
            var signedArea = 0.0;
            for (var index = 0; index < points.Count; index++)
            {
                var current = points[index];
                var next = points[(index + 1) % points.Count];
                signedArea += current.X * next.Y - next.X * current.Y;
            }
            if (Math.Abs(signedArea) < 0.001) return null;

            var offsetStarts = new Point2d[points.Count];
            var directions = new Vector2d[points.Count];
            for (var index = 0; index < points.Count; index++)
            {
                var start = points[index];
                var end = points[(index + 1) % points.Count];
                var direction = end - start;
                if (direction.Length < 0.001) return null;
                direction = direction.GetNormal();
                var outward = signedArea > 0.0
                    ? new Vector2d(direction.Y, -direction.X)
                    : new Vector2d(-direction.Y, direction.X);
                offsetStarts[index] = start + outward * distance;
                directions[index] = direction;
            }

            var result = new List<Point2d>();
            for (var index = 0; index < points.Count; index++)
            {
                var previous = (index + points.Count - 1) % points.Count;
                Point2d intersection;
                if (!TryIntersectLines(
                    offsetStarts[previous], directions[previous],
                    offsetStarts[index], directions[index],
                    out intersection)) return null;
                result.Add(intersection);
            }
            return result;
        }

        private static bool TryIntersectLines(
            Point2d first,
            Vector2d firstDirection,
            Point2d second,
            Vector2d secondDirection,
            out Point2d intersection)
        {
            var cross = firstDirection.X * secondDirection.Y
                - firstDirection.Y * secondDirection.X;
            if (Math.Abs(cross) < 0.0000001)
            {
                intersection = default(Point2d);
                return false;
            }
            var delta = second - first;
            var factor = (delta.X * secondDirection.Y - delta.Y * secondDirection.X) / cross;
            intersection = first + firstDirection * factor;
            return true;
        }

        private static StairPlanSourceDefinition BuildDefinition(
            Document document,
            Entity stair,
            string storeyId,
            string displayName,
            double cropOffset,
            BoundarySolution solution)
        {
            var definition = new StairPlanSourceDefinition
            {
                StoreyId = storeyId ?? string.Empty,
                DisplayName = displayName ?? string.Empty,
                Mode = StairPlanSourceMode.TianzhengStair,
                SourceDrawing = SafeDocumentName(document),
                SourceDrawingFingerprint = SafeFingerprint(document.Database),
                SourceHandle = stair.Handle.ToString(),
                SourceDxfName = SafeDxfName(stair),
                SourceComType = SafeComProperty(stair, "ObjectName"),
                SourceScale = ToInt(SafeComValue(stair, "Scale"), 0),
                StairWidth = ToDouble(SafeComValue(stair, "StairWidth"), 0.0),
                CropOffset = cropOffset > 0.0 ? cropOffset : 300.0,
                RecognitionSummary = "已识别天正楼梯及四面墙轴线；请以CAD中的临时边界预览为准。"
            };

            definition.BoundaryPoints = solution.Points
                .Select(point => new StairPlanPointDefinition(point.X, point.Y))
                .ToList();
            definition.CropBoundaryPoints = ExpandRectangle(solution, definition.CropOffset)
                .Select(point => new StairPlanPointDefinition(point.X, point.Y))
                .ToList();
            definition.WallAxes = solution.Walls.Select(wall => new StairPlanWallAxisDefinition
            {
                Handle = wall.Handle,
                StartX = wall.Start.X,
                StartY = wall.Start.Y,
                EndX = wall.End.X,
                EndY = wall.End.Y,
                LeftWidth = wall.LeftWidth,
                RightWidth = wall.RightWidth,
                Thickness = wall.LeftWidth + wall.RightWidth
            }).ToList();
            return definition;
        }

        private static bool ConfirmPreview(Editor editor, StairPlanSourceDefinition definition)
        {
            var transients = new List<Entity>();
            try
            {
                foreach (var wall in definition.WallAxes)
                {
                    var line = new Line(
                        new Point3d(wall.StartX, wall.StartY, 0.0),
                        new Point3d(wall.EndX, wall.EndY, 0.0))
                    {
                        Color = Color.FromColorIndex(ColorMethod.ByAci, 4)
                    };
                    AddTransient(line, transients);
                }

                var inner = CreatePolyline(definition.BoundaryPoints, 3);
                AddTransient(inner, transients);
                AddDashedBoundary(definition.CropBoundaryPoints, transients);

                editor.WriteMessage(definition.WallAxes.Count > 0
                    ? "\n预览颜色：青色=采用的天正墙基线，绿色=墙轴线计算内边界，红色虚线=外偏裁剪边界。\n"
                    : "\n预览颜色：绿色=用户闭合多段线内边界，红色虚线=外偏裁剪边界。\n");
                var keyword = new PromptKeywordOptions(
                    "\n确认采用当前墙轴线边界？ [接受(A)/取消(C)] <接受>: ");
                keyword.AllowNone = true;
                keyword.Keywords.Add("Accept", "A", "接受");
                keyword.Keywords.Add("Cancel", "C", "取消");
                var result = editor.GetKeywords(keyword);
                return result.Status == PromptStatus.None
                    || (result.Status == PromptStatus.OK
                        && string.Equals(result.StringResult, "Accept", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                var manager = TransientManager.CurrentTransientManager;
                foreach (var entity in transients)
                {
                    try { manager.EraseTransient(entity, new IntegerCollection()); }
                    catch { }
                    entity.Dispose();
                }
                editor.Regen();
            }
        }

        private static void AddTransient(Entity entity, ICollection<Entity> transients)
        {
            TransientManager.CurrentTransientManager.AddTransient(
                entity,
                TransientDrawingMode.DirectShortTerm,
                128,
                new IntegerCollection());
            transients.Add(entity);
        }

        private static Autodesk.AutoCAD.DatabaseServices.Polyline CreatePolyline(
            IList<StairPlanPointDefinition> points,
            short colorIndex)
        {
            var polyline = new Autodesk.AutoCAD.DatabaseServices.Polyline(
                points == null ? 0 : points.Count)
            {
                Closed = true,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex)
            };
            if (points != null)
            {
                for (var index = 0; index < points.Count; index++)
                    polyline.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), 0.0, 0.0, 0.0);
            }
            return polyline;
        }

        private static void AddDashedBoundary(
            IList<StairPlanPointDefinition> points,
            ICollection<Entity> transients)
        {
            if (points == null || points.Count < 2) return;
            for (var index = 0; index < points.Count; index++)
            {
                var start = new Point2d(points[index].X, points[index].Y);
                var endDefinition = points[(index + 1) % points.Count];
                var end = new Point2d(endDefinition.X, endDefinition.Y);
                var vector = end - start;
                var length = vector.Length;
                if (length <= 0.001) continue;
                var direction = vector.GetNormal();
                const double dash = 180.0;
                const double gap = 100.0;
                for (var distance = 0.0; distance < length; distance += dash + gap)
                {
                    var dashEnd = Math.Min(length, distance + dash);
                    var line = new Line(
                        new Point3d((start + direction * distance).X, (start + direction * distance).Y, 0.0),
                        new Point3d((start + direction * dashEnd).X, (start + direction * dashEnd).Y, 0.0))
                    {
                        Color = Color.FromColorIndex(ColorMethod.ByAci, 1)
                    };
                    AddTransient(line, transients);
                }
            }
        }

        private static IList<WallAxis> ReadNearbyWalls(
            Database database,
            Transaction transaction,
            Extents3d stairExtents)
        {
            var result = new List<WallAxis>();
            var currentSpace = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
            if (currentSpace == null) return result;
            var search = Expand(stairExtents, 5000.0);
            foreach (ObjectId objectId in currentSpace)
            {
                var entity = transaction.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                if (entity == null || !IsTianzhengWall(entity)) continue;
                Extents3d extents;
                if (!TryGetExtents(entity, out extents) || !Intersects(search, extents)) continue;

                Point3d start;
                Point3d end;
                if (!TryReadCurveAxis(entity, extents, out start, out end)) continue;
                if (start.DistanceTo(end) < 200.0) continue;
                result.Add(new WallAxis
                {
                    Handle = entity.Handle.ToString(),
                    Start = new Point2d(start.X, start.Y),
                    End = new Point2d(end.X, end.Y),
                    LeftWidth = ToDouble(SafeComValue(entity, "LeftWidth"), 0.0),
                    RightWidth = ToDouble(SafeComValue(entity, "RightWidth"), 0.0)
                });
            }
            return result;
        }

        private static bool TryReadCurveAxis(Entity entity, Extents3d extents, out Point3d start, out Point3d end)
        {
            try
            {
                var curve = entity as Curve;
                if (curve != null)
                {
                    start = curve.StartPoint;
                    end = curve.EndPoint;
                    if (start.DistanceTo(end) > 0.001) return true;
                }
            }
            catch
            {
                // Some proxy wrappers expose extents but not curve endpoints.
            }

            var width = extents.MaxPoint.X - extents.MinPoint.X;
            var height = extents.MaxPoint.Y - extents.MinPoint.Y;
            if (width >= height)
            {
                var y = (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5;
                start = new Point3d(extents.MinPoint.X, y, 0.0);
                end = new Point3d(extents.MaxPoint.X, y, 0.0);
            }
            else
            {
                var x = (extents.MinPoint.X + extents.MaxPoint.X) * 0.5;
                start = new Point3d(x, extents.MinPoint.Y, 0.0);
                end = new Point3d(x, extents.MaxPoint.Y, 0.0);
            }
            return start.DistanceTo(end) > 0.001;
        }

        private static bool TrySolveBoundary(
            Extents3d stairExtents,
            IList<WallAxis> walls,
            out BoundarySolution solution,
            out string failure)
        {
            solution = null;
            failure = string.Empty;
            if (walls == null || walls.Count < 4)
            {
                failure = "周边有效天正墙不足4段";
                return false;
            }

            var center = new Point2d(
                (stairExtents.MinPoint.X + stairExtents.MaxPoint.X) * 0.5,
                (stairExtents.MinPoint.Y + stairExtents.MaxPoint.Y) * 0.5);
            var extentsCorners = new[]
            {
                new Point2d(stairExtents.MinPoint.X, stairExtents.MinPoint.Y),
                new Point2d(stairExtents.MaxPoint.X, stairExtents.MinPoint.Y),
                new Point2d(stairExtents.MaxPoint.X, stairExtents.MaxPoint.Y),
                new Point2d(stairExtents.MinPoint.X, stairExtents.MaxPoint.Y)
            };

            BoundarySolution best = null;
            foreach (var seed in walls)
            {
                var u = Canonical((seed.End - seed.Start).GetNormal());
                var v = new Vector2d(-u.Y, u.X);
                var alongU = walls.Where(wall => IsParallel(wall.Direction, u)).ToList();
                var alongV = walls.Where(wall => IsParallel(wall.Direction, v)).ToList();
                WallAxis vNegative;
                WallAxis vPositive;
                WallAxis uNegative;
                WallAxis uPositive;
                double scoreU;
                double scoreV;
                if (!TryPickOppositeWalls(alongU, center, v, u, extentsCorners,
                    out vNegative, out vPositive, out scoreU)) continue;
                if (!TryPickOppositeWalls(alongV, center, u, v, extentsCorners,
                    out uNegative, out uPositive, out scoreV)) continue;

                var minU = Dot(uNegative.Start, u);
                var maxU = Dot(uPositive.Start, u);
                var minV = Dot(vNegative.Start, v);
                var maxV = Dot(vPositive.Start, v);
                if (minU > maxU) Swap(ref minU, ref maxU);
                if (minV > maxV) Swap(ref minV, ref maxV);
                var projectedU = ProjectionSpan(extentsCorners, u);
                var projectedV = ProjectionSpan(extentsCorners, v);
                if (maxU - minU < projectedU * 0.75 || maxV - minV < projectedV * 0.75) continue;

                var candidate = new BoundarySolution
                {
                    U = u,
                    V = v,
                    MinU = minU,
                    MaxU = maxU,
                    MinV = minV,
                    MaxV = maxV,
                    Score = scoreU + scoreV,
                    Walls = new List<WallAxis> { vNegative, uPositive, vPositive, uNegative }
                };
                candidate.Points = RectanglePoints(candidate);
                if (best == null || candidate.Score < best.Score) best = candidate;
            }

            if (best == null)
            {
                failure = "没有找到能包围楼梯且方向成组的四面墙基线";
                return false;
            }
            solution = best;
            return true;
        }

        private static bool TryPickOppositeWalls(
            IList<WallAxis> walls,
            Point2d center,
            Vector2d sideAxis,
            Vector2d runAxis,
            IList<Point2d> stairCorners,
            out WallAxis negative,
            out WallAxis positive,
            out double score)
        {
            negative = null;
            positive = null;
            score = double.MaxValue;
            var centerOffset = Dot(center, sideAxis);
            var stairProjection = ProjectionRange(stairCorners, runAxis);
            var bestNegative = double.MaxValue;
            var bestPositive = double.MaxValue;
            foreach (var wall in walls)
            {
                var offset = Dot(wall.Start, sideAxis) - centerOffset;
                if (Math.Abs(offset) < 1.0) continue;
                var wallRange = ProjectionRange(new[] { wall.Start, wall.End }, runAxis);
                var overlap = Math.Max(0.0,
                    Math.Min(stairProjection.Item2, wallRange.Item2)
                    - Math.Max(stairProjection.Item1, wallRange.Item1));
                var span = Math.Max(1.0, stairProjection.Item2 - stairProjection.Item1);
                var coveragePenalty = overlap <= 0.0 ? 100000.0 : (1.0 - Math.Min(1.0, overlap / span)) * 2000.0;
                var candidateScore = Math.Abs(offset) + coveragePenalty;
                if (offset < 0.0 && candidateScore < bestNegative)
                {
                    negative = wall;
                    bestNegative = candidateScore;
                }
                else if (offset > 0.0 && candidateScore < bestPositive)
                {
                    positive = wall;
                    bestPositive = candidateScore;
                }
            }
            if (negative == null || positive == null) return false;
            score = bestNegative + bestPositive;
            return true;
        }

        private static IList<Point2d> RectanglePoints(BoundarySolution solution)
        {
            return new List<Point2d>
            {
                FromCoordinates(solution.U, solution.V, solution.MinU, solution.MinV),
                FromCoordinates(solution.U, solution.V, solution.MaxU, solution.MinV),
                FromCoordinates(solution.U, solution.V, solution.MaxU, solution.MaxV),
                FromCoordinates(solution.U, solution.V, solution.MinU, solution.MaxV)
            };
        }

        private static IList<Point2d> ExpandRectangle(BoundarySolution solution, double offset)
        {
            return new List<Point2d>
            {
                FromCoordinates(solution.U, solution.V, solution.MinU - offset, solution.MinV - offset),
                FromCoordinates(solution.U, solution.V, solution.MaxU + offset, solution.MinV - offset),
                FromCoordinates(solution.U, solution.V, solution.MaxU + offset, solution.MaxV + offset),
                FromCoordinates(solution.U, solution.V, solution.MinU - offset, solution.MaxV + offset)
            };
        }

        private static Point2d FromCoordinates(Vector2d u, Vector2d v, double uValue, double vValue)
        {
            return new Point2d(u.X * uValue + v.X * vValue, u.Y * uValue + v.Y * vValue);
        }

        private static bool IsTianzhengStair(Entity entity)
        {
            var identity = (SafeDxfName(entity) + " " + SafeComProperty(entity, "ObjectName")).ToUpperInvariant();
            return identity.Contains("TCH_RECTSTAIR")
                || identity.Contains("TDBRECTSTAIR")
                || (identity.Contains("TCH_") && identity.Contains("STAIR"));
        }

        private static bool IsTianzhengWall(Entity entity)
        {
            var identity = (SafeDxfName(entity) + " " + SafeComProperty(entity, "ObjectName")).ToUpperInvariant();
            return identity.Contains("TCH_WALL") || identity.Contains("TDBWALL");
        }

        private static bool IsTianzhengOpening(Entity entity)
        {
            var identity = (SafeDxfName(entity) + " " + SafeComProperty(entity, "ObjectName")).ToUpperInvariant();
            return identity.Contains("TCH_OPENING") || identity.Contains("TDBOPENING");
        }

        private static bool IsTianzhengRoomSpace(Entity entity)
        {
            var identity = (SafeDxfName(entity) + " " + SafeComProperty(entity, "ObjectName")).ToUpperInvariant();
            return identity.Contains("TCH_SPACE") || identity.Contains("TDBSPACE");
        }

        private static bool IsParallel(Vector2d left, Vector2d right)
        {
            var cross = Math.Abs(left.GetNormal().X * right.GetNormal().Y
                - left.GetNormal().Y * right.GetNormal().X);
            return cross <= AlignmentTolerance;
        }

        private static Vector2d Canonical(Vector2d direction)
        {
            var normalized = direction.GetNormal();
            return normalized.X < -0.000001
                || (Math.Abs(normalized.X) <= 0.000001 && normalized.Y < 0.0)
                ? -normalized
                : normalized;
        }

        private static Tuple<double, double> ProjectionRange(IEnumerable<Point2d> points, Vector2d axis)
        {
            var values = points.Select(point => Dot(point, axis)).ToList();
            return Tuple.Create(values.Min(), values.Max());
        }

        private static double ProjectionSpan(IEnumerable<Point2d> points, Vector2d axis)
        {
            var range = ProjectionRange(points, axis);
            return range.Item2 - range.Item1;
        }

        private static double Dot(Point2d point, Vector2d vector)
        {
            return point.X * vector.X + point.Y * vector.Y;
        }

        private static void Swap(ref double left, ref double right)
        {
            var value = left;
            left = right;
            right = value;
        }

        private static object SafeComValue(Entity entity, string propertyName)
        {
            try
            {
                var acadObject = entity.AcadObject;
                return acadObject == null
                    ? null
                    : acadObject.GetType().InvokeMember(
                        propertyName,
                        BindingFlags.GetProperty,
                        null,
                        acadObject,
                        null,
                        CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static string SafeComProperty(Entity entity, string propertyName)
        {
            var value = SafeComValue(entity, propertyName);
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static double ToDouble(object value, double fallback)
        {
            try { return value == null ? fallback : Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static int ToInt(object value, int fallback)
        {
            try { return value == null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static string SafeDxfName(Entity entity)
        {
            try { return entity.GetRXClass() == null ? string.Empty : entity.GetRXClass().DxfName; }
            catch { return string.Empty; }
        }

        private static bool TryGetExtents(Entity entity, out Extents3d extents)
        {
            try { extents = entity.GeometricExtents; return true; }
            catch { extents = default(Extents3d); return false; }
        }

        private static Extents3d Expand(Extents3d extents, double amount)
        {
            return new Extents3d(
                new Point3d(extents.MinPoint.X - amount, extents.MinPoint.Y - amount, extents.MinPoint.Z),
                new Point3d(extents.MaxPoint.X + amount, extents.MaxPoint.Y + amount, extents.MaxPoint.Z));
        }

        private static bool Intersects(Extents3d left, Extents3d right)
        {
            return left.MinPoint.X <= right.MaxPoint.X
                && left.MaxPoint.X >= right.MinPoint.X
                && left.MinPoint.Y <= right.MaxPoint.Y
                && left.MaxPoint.Y >= right.MinPoint.Y;
        }

        private static string SafeDocumentName(Document document)
        {
            try { return document.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeFingerprint(Database database)
        {
            try { return Convert.ToString(database.FingerprintGuid, CultureInfo.InvariantCulture); }
            catch { return string.Empty; }
        }

        private static void TryAssignDashedLinetype(
            Database database,
            Transaction transaction,
            Entity entity)
        {
            if (database == null || transaction == null || entity == null) return;
            try
            {
                var table = transaction.GetObject(database.LinetypeTableId,
                    OpenMode.ForRead, false) as LinetypeTable;
                if (table != null && !table.Has("DASHED"))
                {
                    database.LoadLineTypeFile("DASHED", "acad.lin");
                    table = transaction.GetObject(database.LinetypeTableId,
                        OpenMode.ForRead, false) as LinetypeTable;
                }
                if (table != null && table.Has("DASHED")) entity.LinetypeId = table["DASHED"];
            }
            catch
            {
                // A visible red continuous crop boundary is preferable to
                // aborting a complete work-copy transaction.
            }
        }

        private static void AppendWorkingCopyLog(
            StairPlanSourceDefinition definition,
            string summary,
            WorkingCopyAssemblyResult result)
        {
            try
            {
                var root = Environment.GetEnvironmentVariable("WANLUO_ARCHITECTURE_TOOLS_ROOT");
                if (string.IsNullOrWhiteSpace(root)) return;
                var path = Path.Combine(root, "用户配置文件", "日志", "stair-plan-capture.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var text = new StringBuilder()
                    .AppendLine("[正式小平面工作副本]")
                    .AppendLine("时间=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .AppendLine("楼层=" + (definition == null ? string.Empty : definition.DisplayName + " / " + definition.StoreyId))
                    .AppendLine("结果=" + (summary ?? string.Empty))
                    .AppendLine("待适配对象=" + string.Join("; ", result.Blockers.Select(item => item.Key + "×" + item.Value)))
                    .AppendLine("错误=" + string.Join("; ", result.Errors))
                    .AppendLine()
                    .ToString();
                File.AppendAllText(path, text, new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must never roll back a successfully generated copy.
            }
        }

        private static void AppendLog(StairPlanSourceDefinition definition)
        {
            try
            {
                var root = Environment.GetEnvironmentVariable("WANLUO_ARCHITECTURE_TOOLS_ROOT");
                if (string.IsNullOrWhiteSpace(root)) return;
                var path = Path.Combine(root, "用户配置文件", "日志", "stair-plan-capture.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var text = new StringBuilder()
                    .AppendLine("[正式平面来源识别]")
                    .AppendLine("时间=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .AppendLine("楼层=" + definition.DisplayName + " / " + definition.StoreyId)
                    .AppendLine("对象=" + definition.SourceDxfName + " / " + definition.SourceComType + " / " + definition.SourceHandle)
                    .AppendLine("比例=" + definition.SourceScale + "; 梯宽=" + definition.StairWidth.ToString("0.###", CultureInfo.InvariantCulture))
                    .AppendLine("墙轴线=" + definition.WallAxes.Count + "; 裁剪外偏=" + definition.CropOffset.ToString("0.###", CultureInfo.InvariantCulture))
                    .AppendLine()
                    .ToString();
                File.AppendAllText(path, text, new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must never prevent plan registration.
            }
        }

        private static void AppendCopyProbeLog(
            StairPlanSourceDefinition definition,
            CopyProbeResult probe,
            TianzhengWallSplitProbeResult wallSplitProbe,
            WorkingCopyAssemblyResult assemblyProbe,
            int inside,
            int crossing,
            int outside)
        {
            try
            {
                var root = Environment.GetEnvironmentVariable("WANLUO_ARCHITECTURE_TOOLS_ROOT");
                if (string.IsNullOrWhiteSpace(root)) return;
                var path = Path.Combine(root, "用户配置文件", "日志", "stair-plan-capture.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var text = new StringBuilder()
                    .AppendLine("[平面复制兼容性检查]")
                    .AppendLine("时间=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .AppendLine("楼层=" + (definition == null ? string.Empty : definition.DisplayName + " / " + definition.StoreyId))
                    .AppendLine("分类=框内" + inside + "; 穿框" + crossing + "; 框外" + outside)
                    .AppendLine("复制=" + probe.Summary)
                    .AppendLine("类型变化=" + string.Join("; ", probe.ChangedTypes))
                    .AppendLine("天正墙切段=" + wallSplitProbe.Summary)
                    .AppendLine("墙类型变化=" + string.Join("; ", wallSplitProbe.ChangedTypes.Distinct()))
                    .AppendLine("墙切段错误=" + string.Join("; ", wallSplitProbe.Errors))
                    .AppendLine("工作副本组装=" + assemblyProbe.Summary)
                    .AppendLine("待适配对象=" + string.Join("; ", assemblyProbe.Blockers.Select(item => item.Key + "×" + item.Value)))
                    .AppendLine("组装错误=" + string.Join("; ", assemblyProbe.Errors))
                    .AppendLine()
                    .ToString();
                File.AppendAllText(path, text, new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must never prevent source inspection.
            }
        }

        private sealed class WallAxis
        {
            public string Handle { get; set; }
            public Point2d Start { get; set; }
            public Point2d End { get; set; }
            public double LeftWidth { get; set; }
            public double RightWidth { get; set; }
            public Vector2d Direction { get { return (End - Start).GetNormal(); } }
        }

        private sealed class BoundarySolution
        {
            public Vector2d U { get; set; }
            public Vector2d V { get; set; }
            public double MinU { get; set; }
            public double MaxU { get; set; }
            public double MinV { get; set; }
            public double MaxV { get; set; }
            public double Score { get; set; }
            public IList<Point2d> Points { get; set; }
            public IList<WallAxis> Walls { get; set; }
        }

        private sealed class CopyProbeResult
        {
            public CopyProbeResult()
            {
                ChangedTypes = new List<string>();
                Summary = string.Empty;
                Error = string.Empty;
            }

            public int Attempted { get; set; }
            public int Preserved { get; set; }
            public int TypeChanged { get; set; }
            public int Failed { get; set; }
            public string Summary { get; set; }
            public string Error { get; set; }
            public IList<string> ChangedTypes { get; private set; }
        }

        private sealed class TianzhengWallSplitProbeResult
        {
            public TianzhengWallSplitProbeResult()
            {
                ChangedTypes = new List<string>();
                Errors = new List<string>();
            }

            public int Attempted { get; set; }
            public int Preserved { get; set; }
            public int NotRequired { get; set; }
            public int TypeChanged { get; set; }
            public int Failed { get; set; }
            public IList<string> ChangedTypes { get; private set; }
            public IList<string> Errors { get; private set; }
            public bool NoTouchPreview { get; set; }

            public string Summary
            {
                get
                {
                    if (NoTouchPreview) return "预览免触碰（墙和门窗仅分类显示，不复制、不切段）";
                    if (Attempted == 0) return "未发现穿框天正墙";
                    return string.Format(
                        CultureInfo.CurrentCulture,
                        "检查 {0} 段，切段后保持天正类型 {1} 段，无需切段 {2} 段，类型变化 {3} 段，失败 {4} 段",
                        Attempted,
                        Preserved,
                        NotRequired,
                        TypeChanged,
                        Failed);
                }
            }
        }

        private sealed class WorkingCopyAssemblyResult
        {
            public WorkingCopyAssemblyResult()
            {
                Blockers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Errors = new List<string>();
                FatalError = string.Empty;
            }

            public int InsideClones { get; set; }
            public int OrdinaryPieces { get; set; }
            public int OrdinaryBlockPieces { get; set; }
            public int TianzhengWallPieces { get; set; }
            public int TianzhengWallPreserved { get; set; }
            public int TianzhengOpeningPreserved { get; set; }
            public int TianzhengInsidePreserved { get; set; }
            public IDictionary<string, int> Blockers { get; private set; }
            public IList<string> Errors { get; private set; }
            public string FatalError { get; set; }

            public void AddBlocker(Entity entity)
            {
                var key = SafeDxfName(entity);
                var comType = SafeComProperty(entity, "ObjectName");
                if (!string.IsNullOrWhiteSpace(comType)) key += "/" + comType;
                int count;
                Blockers.TryGetValue(key, out count);
                Blockers[key] = count + 1;
            }

            public string Summary
            {
                get
                {
                    if (!string.IsNullOrWhiteSpace(FatalError)) return "失败（" + FatalError + "）";
                    var blockerCount = Blockers.Sum(item => item.Value);
                    return string.Format(
                        CultureInfo.CurrentCulture,
                        "框内普通对象副本 {0} 个、普通裁剪段 {1} 个、普通块展开保留 {2} 个、天正墙免触碰保留 {3} 个、天正门窗免触碰保留 {4} 个、其它框内天正对象免触碰保留 {5} 个；待适配 {6} 个{7}",
                        InsideClones,
                        OrdinaryPieces,
                        OrdinaryBlockPieces,
                        TianzhengWallPreserved,
                        TianzhengOpeningPreserved,
                        TianzhengInsidePreserved,
                        blockerCount,
                        blockerCount == 0
                            ? "，已具备正式落图条件"
                            : "（" + string.Join("、", Blockers.Select(item => item.Key + "×" + item.Value)) + "）");
                }
            }
        }

        private enum BoundaryClassification
        {
            Outside = 0,
            Inside = 1,
            Crossing = 2
        }
    }
}
