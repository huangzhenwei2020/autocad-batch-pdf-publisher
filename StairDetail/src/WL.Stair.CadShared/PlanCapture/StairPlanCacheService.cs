using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using WL.Stair.Core.Domain;
using WL.Stair.Core.Layout;

namespace WL.Stair.CadShared.PlanCapture
{
    internal sealed class StairPlanCacheService
    {
        private const string CacheFolderName = "楼梯平面缓存";

        public string Build(
            Document document,
            StairProjectDefinition project,
            StairPlanSourceDefinition definition,
            string title,
            Action<string, int> reportProgress = null)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (project == null) throw new ArgumentNullException("project");
            if (definition == null) throw new ArgumentNullException("definition");

            var oldRelativePath = definition.CacheRelativePath;
            var generated = new List<ObjectId>();
            string cachePath = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                Report(reportProgress, "正在准备裁切范围", 5);
                WriteLog("开始", definition, "标题=" + (title ?? string.Empty));
                var temporaryPoint = GetTemporaryInsertionPoint(document.Database, definition);
                Report(reportProgress, "正在生成裁切工作副本", 15);
                new StairPlanCaptureService().CreateWorkingCopy(
                    document, definition, temporaryPoint, title);
                WriteLog("工作副本完成", definition,
                    "累计耗时=" + stopwatch.ElapsedMilliseconds + "ms");
                Report(reportProgress, "正在核对裁切对象", 65);
                generated = SelectGeneratedRegion(document, definition,
                    temporaryPoint);
                if (generated.Count == 0)
                    throw new InvalidOperationException("裁剪完成后没有生成可缓存的平面对象。");

                // A Tianzheng deep clone can bring associated display objects
                // whose complete extents are far outside the crop.  Remove only
                // those fully detached objects; touching doors/windows remain
                // complete as required.  The title reserve below the crop is
                // deliberately retained.
                RemoveDetachedGeneratedObjects(document, definition, generated,
                    temporaryPoint);
                // TRIM and Tianzheng associative updates may erase some of the
                // original ObjectIds. Re-enumerate current space instead of
                // querying stale ids (ObjectId.IsErased itself can throw
                // eWasErased for proxy objects).
                generated = FilterLiveIds(document.Database, generated);
                WriteLog("清理后", definition, "存活对象=" + generated.Count
                    + "; 累计耗时=" + stopwatch.ElapsedMilliseconds + "ms");

                Extents3d extents;
                Report(reportProgress, "正在计算缓存范围", 75);
                if (!TryGetCombinedExtents(document.Database, generated, out extents))
                    throw new InvalidOperationException("无法取得裁剪平面的实际外包范围。");

                // Do not rely on Database.Wblock(ids, basePoint) to normalize a
                // Tianzheng plan.  A number of Tianzheng custom entities ignore
                // the Wblock base point and keep their large source coordinates
                // (for example X=1,180,000), while the layout code expects every
                // cache to start at the origin.  MOVE is the native operation
                // supported by those objects, so normalize the complete working
                // copy first and then Wblock it with an origin base point.
                Report(reportProgress, "正在归零缓存坐标", 80);
                string normalizationWarning;
                var normalized = TryMoveObjects(document, generated,
                    extents.MinPoint, Point3d.Origin, out normalizationWarning);
                WriteLog(normalized ? "缓存归零" : "缓存归零降级", definition,
                    string.Format(CultureInfo.InvariantCulture,
                        "源最小点=({0:R},{1:R},{2:R}){3}",
                        extents.MinPoint.X, extents.MinPoint.Y, extents.MinPoint.Z,
                        string.IsNullOrWhiteSpace(normalizationWarning)
                            ? string.Empty : "; " + normalizationWarning));

                cachePath = GetCachePath(project, definition);
                Report(reportProgress, "正在保存楼梯平面缓存", 85);
                using (document.LockDocument())
                    SaveObjects(document.Database, generated, Point3d.Origin, cachePath);
                definition.CacheRelativePath = MakeRelativeToUserConfig(cachePath);
                definition.CacheWidth = Math.Max(1.0,
                    extents.MaxPoint.X - extents.MinPoint.X);
                definition.CacheHeight = Math.Max(1.0,
                    extents.MaxPoint.Y - extents.MinPoint.Y);
                var layoutMargin = 25.0 * Math.Max(1, definition.TargetScale);
                var cropMaxX = definition.CropBoundaryPoints.Max(point => point.X);
                var cropMaxY = definition.CropBoundaryPoints.Max(point => point.Y);
                var cropMinX = definition.CropBoundaryPoints.Min(point => point.X);
                var cropMinY = definition.CropBoundaryPoints.Min(point => point.Y);
                // CreateWorkingCopy maps the source crop minimum to
                // temporaryPoint. Extents are therefore in temporary-copy
                // coordinates, not in the original drawing coordinates.
                definition.CacheLayoutOffsetX = temporaryPoint.X - layoutMargin
                    - extents.MinPoint.X;
                definition.CacheLayoutOffsetY = temporaryPoint.Y - layoutMargin
                    - extents.MinPoint.Y;
                definition.CacheLayoutWidth = Math.Max(1.0,
                    cropMaxX - cropMinX + 2.0 * layoutMargin);
                definition.CacheLayoutHeight = Math.Max(1.0,
                    cropMaxY - cropMinY + 2.0 * layoutMargin);
                definition.CacheObjectCount = generated.Count;
                definition.CacheFingerprint = ComputeFingerprint(definition, title);
                definition.CachedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                if (!string.IsNullOrWhiteSpace(oldRelativePath)
                    && !string.Equals(oldRelativePath, definition.CacheRelativePath,
                        StringComparison.OrdinalIgnoreCase))
                    DeleteRelative(oldRelativePath);
                Report(reportProgress, "本层缓存已完成", 100);
                WriteLog("完成", definition, "对象=" + generated.Count + "; 文件=" + cachePath
                    + "; 总耗时=" + stopwatch.ElapsedMilliseconds + "ms");
                return string.Format(CultureInfo.CurrentCulture,
                    "已缓存裁剪成果 {0} 个对象，实际范围 {1:0.#}×{2:0.#}。",
                    definition.CacheObjectCount, definition.CacheWidth,
                    definition.CacheHeight);
            }
            catch (System.Exception exception)
            {
                WriteLog("失败", definition, exception.ToString());
                throw;
            }
            finally
            {
                try { EraseGeneratedObjects(document, generated); }
                catch (System.Exception exception)
                {
                    // Cleanup must never replace the real cache result/error.
                    WriteLog("临时对象清理警告", definition, exception.ToString());
                }
            }
        }

        public bool IsValid(StairPlanSourceDefinition definition, string title)
        {
            if (!IsAvailable(definition)
                || string.IsNullOrWhiteSpace(definition.CacheFingerprint)) return false;
            var current = ComputeFingerprint(definition, title);
            if (string.Equals(definition.CacheFingerprint, current,
                StringComparison.OrdinalIgnoreCase)) return true;
            // Builds before schema 21 included the editable floor title in the
            // cache fingerprint. Accept that legacy value so renaming a floor
            // never forces the user to capture the same geometry again.
            return string.Equals(definition.CacheFingerprint,
                ComputeLegacyFingerprint(definition, title),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether a previously captured plan is physically available for insertion.
        /// Unlike IsValid, this ignores freshness so harmless edits such as renaming
        /// a floor never block whole-set insertion.
        /// </summary>
        public bool IsAvailable(StairPlanSourceDefinition definition)
        {
            if (definition == null
                || string.IsNullOrWhiteSpace(definition.CacheRelativePath)) return false;
            return File.Exists(ResolveRelative(definition.CacheRelativePath));
        }

        public static void GetLayoutRange(StairPlanSourceDefinition definition,
            out double offsetX, out double offsetY, out double width, out double height)
        {
            if (definition == null) throw new ArgumentNullException("definition");
            var margin = 25.0 * Math.Max(1, definition.TargetScale);
            var cropWidth = definition.CropBoundaryPoints == null
                || definition.CropBoundaryPoints.Count < 3 ? definition.CacheWidth
                : definition.CropBoundaryPoints.Max(point => point.X)
                    - definition.CropBoundaryPoints.Min(point => point.X);
            var cropHeight = definition.CropBoundaryPoints == null
                || definition.CropBoundaryPoints.Count < 3 ? definition.CacheHeight
                : definition.CropBoundaryPoints.Max(point => point.Y)
                    - definition.CropBoundaryPoints.Min(point => point.Y);
            width = definition.CacheLayoutWidth > 0.01
                ? definition.CacheLayoutWidth : Math.Max(1.0, cropWidth + 2.0 * margin);
            height = definition.CacheLayoutHeight > 0.01
                ? definition.CacheLayoutHeight : Math.Max(1.0, cropHeight + 2.0 * margin);
            offsetX = definition.CacheLayoutWidth > 0.01
                ? definition.CacheLayoutOffsetX
                : Math.Min(0.0, (definition.CacheWidth - width) / 2.0);
            offsetY = definition.CacheLayoutHeight > 0.01
                ? definition.CacheLayoutOffsetY
                : Math.Min(0.0, (definition.CacheHeight - height) / 2.0);
        }

        public IList<StairLayoutPreviewLine> ReadPreviewLines(
            StairPlanSourceDefinition definition, int maximumLines)
        {
            var result = new List<StairLayoutPreviewLine>();
            if (definition == null || maximumLines <= 0) return result;
            var path = ResolveRelative(definition.CacheRelativePath);
            if (!File.Exists(path)) return result;
            double offsetX, offsetY, width, height;
            GetLayoutRange(definition, out offsetX, out offsetY, out width, out height);
            try
            {
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare,
                        true, string.Empty);
                    source.CloseInput(true);
                    var ids = new List<ObjectId>();
                    using (var transaction = source.TransactionManager.StartOpenCloseTransaction())
                    {
                        var model = transaction.GetObject(
                            SymbolUtilityServices.GetBlockModelSpaceId(source),
                            OpenMode.ForRead, false) as BlockTableRecord;
                        if (model != null) ids.AddRange(model.Cast<ObjectId>());
                    }
                    Extents3d all;
                    if (!TryGetCombinedExtents(source, ids, out all)) return result;
                    using (var transaction = source.TransactionManager.StartOpenCloseTransaction())
                    {
                        foreach (var id in ids)
                        {
                            if (result.Count >= maximumLines) break;
                            Entity entity;
                            try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                            catch { continue; }
                            if (entity == null) continue;
                            var color = PreviewColor(entity.ColorIndex);
                            var line = entity as Line;
                            if (line != null)
                            {
                                AddPreviewLine(result, line.StartPoint, line.EndPoint,
                                    all.MinPoint, offsetX, offsetY, width, height, color, false);
                                continue;
                            }
                            var polyline = entity as Polyline;
                            if (polyline != null && polyline.NumberOfVertices > 1)
                            {
                                for (var index = 1; index < polyline.NumberOfVertices
                                    && result.Count < maximumLines; index++)
                                {
                                    var a = polyline.GetPoint3dAt(index - 1);
                                    var b = polyline.GetPoint3dAt(index);
                                    AddPreviewLine(result, a, b, all.MinPoint, offsetX,
                                        offsetY, width, height, color, false);
                                }
                                if (polyline.Closed && result.Count < maximumLines)
                                    AddPreviewLine(result, polyline.GetPoint3dAt(polyline.NumberOfVertices - 1),
                                        polyline.GetPoint3dAt(0), all.MinPoint, offsetX, offsetY,
                                        width, height, color, false);
                                continue;
                            }
                            var circle = entity as Circle;
                            var arc = entity as Arc;
                            if (circle != null || arc != null)
                            {
                                var center = circle != null ? circle.Center : arc.Center;
                                var radius = circle != null ? circle.Radius : arc.Radius;
                                var start = circle != null ? 0.0 : arc.StartAngle;
                                var sweep = circle != null ? Math.PI * 2.0 : arc.EndAngle - arc.StartAngle;
                                if (sweep <= 0.0) sweep += Math.PI * 2.0;
                                var count = Math.Max(8, Math.Min(32, (int)Math.Ceiling(sweep / (Math.PI / 12.0))));
                                var previous = new Point3d(center.X + radius * Math.Cos(start),
                                    center.Y + radius * Math.Sin(start), center.Z);
                                for (var index = 1; index <= count && result.Count < maximumLines; index++)
                                {
                                    var angle = start + sweep * index / count;
                                    var current = new Point3d(center.X + radius * Math.Cos(angle),
                                        center.Y + radius * Math.Sin(angle), center.Z);
                                    AddPreviewLine(result, previous, current, all.MinPoint,
                                        offsetX, offsetY, width, height, color, false);
                                    previous = current;
                                }
                                continue;
                            }
                            Extents3d extents;
                            try { extents = entity.GeometricExtents; }
                            catch { continue; }
                            var p1 = new Point3d(extents.MinPoint.X, extents.MinPoint.Y, 0);
                            var p2 = new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0);
                            var p3 = new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, 0);
                            var p4 = new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0);
                            AddPreviewLine(result, p1, p2, all.MinPoint, offsetX, offsetY, width, height, color, false);
                            AddPreviewLine(result, p2, p3, all.MinPoint, offsetX, offsetY, width, height, color, false);
                            AddPreviewLine(result, p3, p4, all.MinPoint, offsetX, offsetY, width, height, color, false);
                            AddPreviewLine(result, p4, p1, all.MinPoint, offsetX, offsetY, width, height, color, false);
                        }
                    }
                }
            }
            catch (System.Exception exception)
            {
                WriteLog("预览线读取警告", definition, exception.Message);
            }
            return result;
        }

        private static void AddPreviewLine(ICollection<StairLayoutPreviewLine> lines,
            Point3d sourceA, Point3d sourceB, Point3d cacheMin,
            double offsetX, double offsetY, double width, double height,
            string color, bool dashed)
        {
            var x1 = sourceA.X - cacheMin.X - offsetX;
            var y1 = sourceA.Y - cacheMin.Y - offsetY;
            var x2 = sourceB.X - cacheMin.X - offsetX;
            var y2 = sourceB.Y - cacheMin.Y - offsetY;
            if (!ClipLine(ref x1, ref y1, ref x2, ref y2, width, height)) return;
            lines.Add(new StairLayoutPreviewLine
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Color = color, Dashed = dashed
            });
        }

        private static bool ClipLine(ref double x1, ref double y1,
            ref double x2, ref double y2, double width, double height)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var t0 = 0.0;
            var t1 = 1.0;
            var p = new[] { -dx, dx, -dy, dy };
            var q = new[] { x1, width - x1, y1, height - y1 };
            for (var index = 0; index < 4; index++)
            {
                if (Math.Abs(p[index]) < 0.0000001)
                { if (q[index] < 0.0) return false; continue; }
                var ratio = q[index] / p[index];
                if (p[index] < 0.0) t0 = Math.Max(t0, ratio);
                else t1 = Math.Min(t1, ratio);
                if (t0 > t1) return false;
            }
            var originalX = x1;
            var originalY = y1;
            x1 = originalX + t0 * dx;
            y1 = originalY + t0 * dy;
            x2 = originalX + t1 * dx;
            y2 = originalY + t1 * dy;
            return true;
        }

        private static string PreviewColor(int colorIndex)
        {
            switch (colorIndex)
            {
                case 1: return "#ff5b5b";
                case 2: return "#f4e74f";
                case 3: return "#58ef70";
                case 4: return "#26cbd0";
                case 5: return "#6e8cff";
                case 6: return "#e477ff";
                case 8: return "#91999f";
                default: return "#d8e0e6";
            }
        }

        public int Insert(Document document, StairPlanSourceDefinition definition,
            Point3d insertionPoint)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (definition == null) throw new ArgumentNullException("definition");
            var path = ResolveRelative(definition.CacheRelativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException("楼梯平面缓存不存在，请重新拾取该层平面。", path);

            using (var source = new Database(false, true))
            {
                source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare,
                    true, string.Empty);
                source.CloseInput(true);
                var sourceIds = new ObjectIdCollection();
                using (var sourceTransaction = source.TransactionManager.StartOpenCloseTransaction())
                {
                    var model = sourceTransaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(source),
                        OpenMode.ForRead, false) as BlockTableRecord;
                    if (model != null)
                        foreach (var id in model.Cast<ObjectId>()) sourceIds.Add(id);
                }
                if (sourceIds.Count == 0) return 0;

                Extents3d sourceExtents;
                if (!TryGetCombinedExtents(source, sourceIds.Cast<ObjectId>().ToList(),
                    out sourceExtents))
                    throw new InvalidOperationException("无法取得楼梯平面缓存的实际范围。");
                var sourceBasePoint = sourceExtents.MinPoint;

                var clonedEntityIds = new List<ObjectId>();
                try
                {
                    using (document.LockDocument())
                    {
                        var mapping = new IdMapping();
                        // WblockCloneObjects owns its destination write operation.  Running it
                        // inside a destination transaction is not supported for a number of
                        // Tianzheng/proxy objects and intermittently raises eNotApplicable.
                        source.WblockCloneObjects(sourceIds,
                            document.Database.CurrentSpaceId, mapping,
                            DuplicateRecordCloning.Ignore, false);
                        clonedEntityIds = GetMappedCurrentSpaceEntities(
                            document.Database, mapping);
                    }

                    // IdMapping also contains entities cloned into block definitions and
                    // Tianzheng associative/display records.  Those objects are not valid
                    // members of an Editor selection set and make MOVE fail with
                    // SelectionSet.GetAdsName/eInvalidInput.  Only move entities that were
                    // actually appended to the active current space.
                    if (clonedEntityIds.Count == 0) return 0;

                    // Do not call Entity.TransformBy here. Tianzheng custom entities may be
                    // cloned successfully but deliberately reject the managed TransformBy API
                    // with eNotApplicable. AutoCAD's native MOVE command is the supported path
                    // for the same objects and moves the entire cached floor as one selection
                    // without exposing any prompt to the user.
                    if (insertionPoint.DistanceTo(sourceBasePoint)
                        > Tolerance.Global.EqualPoint)
                    {
                        string moveWarning;
                        if (!TryMoveObjects(document, clonedEntityIds,
                            sourceBasePoint, insertionPoint, out moveWarning))
                            throw new InvalidOperationException(
                                "楼梯平面缓存已插入，但无法移动到目标位置：" + moveWarning);
                    }
                    WriteLog("插入缓存", definition, string.Format(
                        CultureInfo.InvariantCulture,
                        "缓存基点=({0:R},{1:R},{2:R}); 目标=({3:R},{4:R},{5:R}); 对象={6}",
                        sourceBasePoint.X, sourceBasePoint.Y, sourceBasePoint.Z,
                        insertionPoint.X, insertionPoint.Y, insertionPoint.Z,
                        clonedEntityIds.Count));
                    return clonedEntityIds.Count;
                }
                catch
                {
                    // A failed combined insert must not leave a half-cloned floor plan.
                    using (document.LockDocument())
                        EraseObjects(document.Database, clonedEntityIds);
                    throw;
                }
            }
        }

        public void Delete(StairPlanSourceDefinition definition)
        {
            if (definition == null) return;
            DeleteRelative(definition.CacheRelativePath);
            definition.CacheRelativePath = null;
            definition.CacheFingerprint = null;
            definition.CacheWidth = 0.0;
            definition.CacheHeight = 0.0;
            definition.CacheLayoutOffsetX = 0.0;
            definition.CacheLayoutOffsetY = 0.0;
            definition.CacheLayoutWidth = 0.0;
            definition.CacheLayoutHeight = 0.0;
            definition.CacheObjectCount = 0;
            definition.CachedUtc = null;
        }

        public static string ComputeFingerprint(
            StairPlanSourceDefinition definition, string title)
        {
            if (definition == null) return string.Empty;
            var builder = new StringBuilder();
            builder.Append(definition.SourceDrawingFingerprint).Append('|')
                .Append(definition.SourceHandle).Append('|')
                .Append(definition.BoundarySourceHandle).Append('|')
                .Append(definition.TargetScale).Append('|')
                .Append(definition.CropOffset.ToString("R", CultureInfo.InvariantCulture));
            foreach (var point in definition.CropBoundaryPoints
                ?? new List<StairPlanPointDefinition>())
                builder.Append('|').Append(point.X.ToString("R", CultureInfo.InvariantCulture))
                    .Append(',').Append(point.Y.ToString("R", CultureInfo.InvariantCulture));
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(builder.ToString()))).Replace("-", string.Empty);
        }

        private static string ComputeLegacyFingerprint(
            StairPlanSourceDefinition definition, string title)
        {
            if (definition == null) return string.Empty;
            var builder = new StringBuilder();
            builder.Append(definition.SourceDrawingFingerprint).Append('|')
                .Append(definition.SourceHandle).Append('|')
                .Append(definition.BoundarySourceHandle).Append('|')
                .Append(definition.TargetScale).Append('|')
                .Append(definition.CropOffset.ToString("R", CultureInfo.InvariantCulture))
                .Append('|').Append(title ?? string.Empty);
            foreach (var point in definition.CropBoundaryPoints
                ?? new List<StairPlanPointDefinition>())
                builder.Append('|').Append(point.X.ToString("R", CultureInfo.InvariantCulture))
                    .Append(',').Append(point.Y.ToString("R", CultureInfo.InvariantCulture));
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(builder.ToString()))).Replace("-", string.Empty);
        }

        private static List<ObjectId> SelectGeneratedRegion(Document document,
            StairPlanSourceDefinition definition, Point3d insertionPoint)
        {
            var result = new List<ObjectId>();
            if (document == null || definition == null
                || definition.CropBoundaryPoints == null
                || definition.CropBoundaryPoints.Count < 3) return result;
            var width = definition.CropBoundaryPoints.Max(point => point.X)
                - definition.CropBoundaryPoints.Min(point => point.X);
            var height = definition.CropBoundaryPoints.Max(point => point.Y)
                - definition.CropBoundaryPoints.Min(point => point.Y);
            var titleReserve = Math.Max(500.0, definition.TargetScale * 30.0);
            try
            {
                var worldToUcs = document.Editor.CurrentUserCoordinateSystem.Inverse();
                var points = new Point3dCollection
                {
                    new Point3d(insertionPoint.X, insertionPoint.Y - titleReserve, 0.0)
                        .TransformBy(worldToUcs),
                    new Point3d(insertionPoint.X + width,
                        insertionPoint.Y - titleReserve, 0.0).TransformBy(worldToUcs),
                    new Point3d(insertionPoint.X + width,
                        insertionPoint.Y + height, 0.0).TransformBy(worldToUcs),
                    new Point3d(insertionPoint.X, insertionPoint.Y + height, 0.0)
                        .TransformBy(worldToUcs)
                };
                var selection = document.Editor.SelectCrossingPolygon(points);
                if (selection.Status == PromptStatus.OK && selection.Value != null)
                    result.AddRange(selection.Value.GetObjectIds()
                        .Where(id => !id.IsNull && id.IsValid).Distinct());
            }
            catch (System.Exception exception)
            {
                WriteLog("区域对象选择失败", definition, exception.Message);
            }
            return result;
        }

        private static List<ObjectId> GetMappedCurrentSpaceEntities(
            Database database, IdMapping mapping)
        {
            var result = new List<ObjectId>();
            if (database == null || mapping == null) return result;
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (IdPair pair in mapping)
                {
                    if (!pair.IsCloned || pair.Value.IsNull || !pair.Value.IsValid) continue;
                    try
                    {
                        var entity = transaction.GetObject(pair.Value,
                            OpenMode.ForRead, false) as Entity;
                        if (entity != null && !entity.IsErased
                            && entity.OwnerId == database.CurrentSpaceId)
                            result.Add(pair.Value);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
                }
            }
            return result.Distinct().ToList();
        }

        private static List<ObjectId> FilterLiveIds(Database database,
            IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            if (database == null || ids == null) return result;
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in ids.Distinct())
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

        private static Point3d GetTemporaryInsertionPoint(Database database,
            StairPlanSourceDefinition definition)
        {
            var width = definition.CropBoundaryPoints.Max(p => p.X)
                - definition.CropBoundaryPoints.Min(p => p.X);
            var height = definition.CropBoundaryPoints.Max(p => p.Y)
                - definition.CropBoundaryPoints.Min(p => p.Y);
            Point3d max;
            try { max = database.Extmax; }
            catch { max = Point3d.Origin; }
            if (double.IsNaN(max.X) || double.IsInfinity(max.X)) max = Point3d.Origin;
            return new Point3d(max.X + Math.Max(10000.0, width * 3.0),
                max.Y + Math.Max(10000.0, height * 3.0), 0.0);
        }

        private static void RemoveDetachedGeneratedObjects(Document document,
            StairPlanSourceDefinition definition, IList<ObjectId> generated,
            Point3d insertionPoint)
        {
            var sourceMinX = definition.CropBoundaryPoints.Min(p => p.X);
            var sourceMinY = definition.CropBoundaryPoints.Min(p => p.Y);
            var width = definition.CropBoundaryPoints.Max(p => p.X) - sourceMinX;
            var height = definition.CropBoundaryPoints.Max(p => p.Y) - sourceMinY;
            var titleReserve = Math.Max(500.0, definition.TargetScale * 30.0);
            var keep = new Extents3d(
                new Point3d(insertionPoint.X, insertionPoint.Y - titleReserve, 0.0),
                new Point3d(insertionPoint.X + width, insertionPoint.Y + height, 0.0));
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in generated.ToList())
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                    if (entity == null) continue;
                    Extents3d extents;
                    try { extents = entity.GeometricExtents; }
                    catch { continue; }
                    if (extents.MaxPoint.X < keep.MinPoint.X
                        || extents.MinPoint.X > keep.MaxPoint.X
                        || extents.MaxPoint.Y < keep.MinPoint.Y
                        || extents.MinPoint.Y > keep.MaxPoint.Y)
                    {
                        entity.UpgradeOpen();
                        entity.Erase();
                    }
                }
                transaction.Commit();
            }
        }

        private static bool TryGetCombinedExtents(Database database,
            IList<ObjectId> ids, out Extents3d result)
        {
            result = new Extents3d();
            var found = false;
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in ids)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                    if (entity == null) continue;
                    try
                    {
                        var extents = entity.GeometricExtents;
                        if (!found) { result = extents; found = true; }
                        else result.AddExtents(extents);
                    }
                    catch { }
                }
            }
            return found;
        }

        private static bool TryMoveObjects(Document document,
            IList<ObjectId> ids, Point3d sourceBasePoint, Point3d targetPoint,
            out string warning)
        {
            warning = string.Empty;
            if (document == null || ids == null || ids.Count == 0) return true;
            if (sourceBasePoint.DistanceTo(targetPoint)
                <= Tolerance.Global.EqualPoint) return true;
            var liveIds = new List<ObjectId>();
            using (var transaction = document.Database.TransactionManager
                .StartOpenCloseTransaction())
            {
                foreach (var id in ids)
                {
                    try
                    {
                        var entity = transaction.GetObject(id, OpenMode.ForRead, false)
                            as Entity;
                        if (entity != null && !entity.IsErased) liveIds.Add(id);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
                }
            }
            if (liveIds.Count == 0) return true;

            try
            {
                var selection = SelectionSet.FromObjectIds(liveIds.ToArray());
                document.Editor.Command("_.MOVE", selection, string.Empty,
                    sourceBasePoint, targetPoint);
                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception commandException)
            {
                var commandError = commandException.ErrorStatus + ": "
                    + commandException.Message;
                try
                {
                    var displacement = Matrix3d.Displacement(targetPoint - sourceBasePoint);
                    using (document.LockDocument())
                    using (var transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        foreach (var id in liveIds)
                        {
                            var entity = transaction.GetObject(id, OpenMode.ForWrite, false)
                                as Entity;
                            if (entity != null && !entity.IsErased)
                                entity.TransformBy(displacement);
                        }
                        transaction.Commit();
                    }
                    warning = "原生 MOVE 失败后已改用事务移动（" + commandError + "）";
                    return true;
                }
                catch (System.Exception fallbackException)
                {
                    warning = "原生 MOVE 与事务移动均失败；对象类型="
                        + DescribeObjectTypes(document.Database, liveIds)
                        + "；MOVE=" + commandError
                        + "；事务=" + fallbackException.Message;
                    return false;
                }
            }
        }

        private static string DescribeObjectTypes(Database database,
            IEnumerable<ObjectId> ids)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in ids)
                {
                    string name;
                    try
                    {
                        var value = transaction.GetObject(id, OpenMode.ForRead, false);
                        name = value == null || value.GetRXClass() == null
                            ? "<unknown>" : value.GetRXClass().DxfName;
                    }
                    catch { name = "<unreadable>"; }
                    int count;
                    counts[name] = counts.TryGetValue(name, out count) ? count + 1 : 1;
                }
            }
            return string.Join(",", counts.OrderBy(item => item.Key)
                .Select(item => item.Key + "×" + item.Value));
        }

        private static void SaveObjects(Database database, IList<ObjectId> ids,
            Point3d basePoint, string finalPath)
        {
            var directory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = finalPath + ".new.dwg";
            if (File.Exists(temporary)) File.Delete(temporary);
            using (var cache = database.Wblock(
                new ObjectIdCollection(ids.ToArray()), basePoint))
                cache.SaveAs(temporary, DwgVersion.Current);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(temporary, finalPath);
        }

        private static void EraseGeneratedObjects(Document document, IList<ObjectId> ids)
        {
            if (document == null || ids == null || ids.Count == 0) return;
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids.ToList())
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForWrite, true); }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                    if (value != null && !value.IsErased) value.Erase();
                }
                transaction.Commit();
            }
        }

        private static void EraseObjects(Database database, IEnumerable<ObjectId> ids)
        {
            if (database == null || ids == null) return;
            var values = ids.ToList();
            if (values.Count == 0) return;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var id in values)
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForWrite, true); }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                    if (value != null && !value.IsErased) value.Erase();
                }
                transaction.Commit();
            }
        }

        private static string GetCachePath(StairProjectDefinition project,
            StairPlanSourceDefinition definition)
        {
            var projectName = SafeName(project.ProjectName, "未命名项目");
            var stairNumber = SafeName(project.StairNumber, "楼梯");
            var floor = SafeName(!string.IsNullOrWhiteSpace(definition.FloorLabel)
                ? definition.FloorLabel : definition.DisplayName, "未命名楼层");
            var directory = Path.Combine(GetUserConfigRoot(), CacheFolderName, projectName);
            return Path.Combine(directory,
                projectName + "_" + stairNumber + "_" + floor + "楼梯平面.dwg");
        }

        private static string SafeName(string value, string fallback)
        {
            var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            foreach (var character in Path.GetInvalidFileNameChars())
                result = result.Replace(character, '_');
            return result.Length > 60 ? result.Substring(0, 60) : result;
        }

        private static string GetUserConfigRoot()
        {
            return WanluoDataPaths.Root;
        }

        private static string MakeRelativeToUserConfig(string path)
        {
            var root = GetUserConfigRoot().TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("缓存文件必须位于插件的用户配置文件目录内。");
            return full.Substring(root.Length);
        }

        private static string ResolveRelative(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return string.Empty;
            var root = GetUserConfigRoot();
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("楼梯平面缓存路径越出了用户配置文件目录。");
            return full;
        }

        private static void DeleteRelative(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return;
            try
            {
                var path = ResolveRelative(relative);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static void Report(Action<string, int> callback, string text, int percent)
        {
            if (callback != null) callback(text, Math.Max(0, Math.Min(100, percent)));
        }

        private static void WriteLog(string stage,
            StairPlanSourceDefinition definition, string message)
        {
            try
            {
                var directory = Path.Combine(GetUserConfigRoot(), "日志");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "stair-plan-cache.log");
                File.AppendAllText(path,
                    string.Format(CultureInfo.InvariantCulture,
                        "[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n楼层={2}; Handle={3}\r\n{4}\r\n\r\n",
                        DateTime.Now, stage,
                        definition == null ? string.Empty : definition.FloorLabel,
                        definition == null ? string.Empty : definition.SourceHandle,
                        message ?? string.Empty), Encoding.UTF8);
            }
            catch { }
        }
    }
}
