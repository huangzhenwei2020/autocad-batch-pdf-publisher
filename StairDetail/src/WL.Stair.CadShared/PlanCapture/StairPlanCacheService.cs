using System;
using System.Collections.Generic;
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
            var before = SnapshotCurrentSpace(document.Database);
            var generated = new List<ObjectId>();
            string cachePath = null;
            try
            {
                Report(reportProgress, "正在准备裁切范围", 5);
                WriteLog("开始", definition, "标题=" + (title ?? string.Empty));
                var temporaryPoint = GetTemporaryInsertionPoint(document.Database, definition);
                Report(reportProgress, "正在生成裁切工作副本", 15);
                new StairPlanCaptureService().CreateWorkingCopy(
                    document, definition, temporaryPoint, title);
                Report(reportProgress, "正在核对裁切对象", 65);
                generated = SnapshotCurrentSpace(document.Database)
                    .Where(id => !before.Contains(id)).ToList();
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
                generated = SnapshotCurrentSpace(document.Database)
                    .Where(id => !before.Contains(id)).ToList();
                WriteLog("清理后", definition, "存活对象=" + generated.Count);

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
                NormalizeGeneratedObjects(document, generated, extents.MinPoint);
                WriteLog("缓存归零", definition, string.Format(
                    CultureInfo.InvariantCulture,
                    "源最小点=({0:R},{1:R},{2:R})",
                    extents.MinPoint.X, extents.MinPoint.Y, extents.MinPoint.Z));

                cachePath = GetCachePath(project, definition);
                Report(reportProgress, "正在保存楼梯平面缓存", 85);
                using (document.LockDocument())
                    SaveObjects(document.Database, generated, Point3d.Origin, cachePath);
                definition.CacheRelativePath = MakeRelativeToUserConfig(cachePath);
                definition.CacheWidth = Math.Max(1.0,
                    extents.MaxPoint.X - extents.MinPoint.X);
                definition.CacheHeight = Math.Max(1.0,
                    extents.MaxPoint.Y - extents.MinPoint.Y);
                definition.CacheObjectCount = generated.Count;
                definition.CacheFingerprint = ComputeFingerprint(definition, title);
                definition.CachedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                if (!string.IsNullOrWhiteSpace(oldRelativePath)
                    && !string.Equals(oldRelativePath, definition.CacheRelativePath,
                        StringComparison.OrdinalIgnoreCase))
                    DeleteRelative(oldRelativePath);
                Report(reportProgress, "本层缓存已完成", 100);
                WriteLog("完成", definition, "对象=" + generated.Count + "; 文件=" + cachePath);
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
                try { document.Editor.Regen(); } catch { }
            }
        }

        public bool IsValid(StairPlanSourceDefinition definition, string title)
        {
            if (definition == null || definition.CacheWidth <= 0.0
                || definition.CacheHeight <= 0.0
                || string.IsNullOrWhiteSpace(definition.CacheRelativePath)
                || string.IsNullOrWhiteSpace(definition.CacheFingerprint)) return false;
            var path = ResolveRelative(definition.CacheRelativePath);
            return File.Exists(path) && string.Equals(definition.CacheFingerprint,
                ComputeFingerprint(definition, title), StringComparison.OrdinalIgnoreCase);
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

                var before = SnapshotCurrentSpace(document.Database);
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

                    }

                    // IdMapping also contains entities cloned into block definitions and
                    // Tianzheng associative/display records.  Those objects are not valid
                    // members of an Editor selection set and make MOVE fail with
                    // SelectionSet.GetAdsName/eInvalidInput.  Only move entities that were
                    // actually appended to the active current space.
                    var clonedEntityIds = SnapshotCurrentSpace(document.Database)
                        .Where(id => !before.Contains(id)).ToList();

                    if (clonedEntityIds.Count == 0) return 0;

                    // Do not call Entity.TransformBy here. Tianzheng custom entities may be
                    // cloned successfully but deliberately reject the managed TransformBy API
                    // with eNotApplicable. AutoCAD's native MOVE command is the supported path
                    // for the same objects and moves the entire cached floor as one selection
                    // without exposing any prompt to the user.
                    if (insertionPoint.DistanceTo(sourceBasePoint)
                        > Tolerance.Global.EqualPoint)
                    {
                        var selection = SelectionSet.FromObjectIds(clonedEntityIds.ToArray());
                        document.Editor.Command("_.MOVE", selection, string.Empty,
                            sourceBasePoint, insertionPoint);
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
                        EraseObjects(document.Database, SnapshotCurrentSpace(document.Database)
                            .Where(id => !before.Contains(id)));
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

        private static void NormalizeGeneratedObjects(Document document,
            IList<ObjectId> ids, Point3d sourceBasePoint)
        {
            if (document == null || ids == null || ids.Count == 0) return;
            if (sourceBasePoint.DistanceTo(Point3d.Origin)
                <= Tolerance.Global.EqualPoint) return;
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
            if (liveIds.Count == 0) return;
            var selection = SelectionSet.FromObjectIds(liveIds.ToArray());
            document.Editor.Command("_.MOVE", selection, string.Empty,
                sourceBasePoint, Point3d.Origin);
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
            var packageRoot = Environment.GetEnvironmentVariable(
                "WANLUO_ARCHITECTURE_TOOLS_ROOT");
            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                var assembly = typeof(StairPlanCacheService).Assembly.Location;
                var directory = Path.GetDirectoryName(assembly);
                // ...\CadApi\R24\WL.Stair.Cad2022.dll -> plug-in root
                packageRoot = directory;
                for (var index = 0; index < 2 && !string.IsNullOrWhiteSpace(packageRoot); index++)
                    packageRoot = Path.GetDirectoryName(packageRoot);
            }
            if (string.IsNullOrWhiteSpace(packageRoot))
                throw new InvalidOperationException("无法确定插件目录，不能保存楼梯平面缓存。");
            return Path.Combine(Path.GetFullPath(packageRoot), "用户配置文件");
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
