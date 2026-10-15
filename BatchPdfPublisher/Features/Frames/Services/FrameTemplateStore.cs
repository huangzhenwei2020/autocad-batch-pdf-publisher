using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    /// <summary>把登记图框保存为随插件目录迁移的独立 DWG，并在目标图纸缺少块定义时自动导入。</summary>
    internal static class FrameTemplateStore
    {
        public static bool TrySave(Document document, ObjectId referenceId, FrameDefinition frame, out string error)
        {
            error = null;
            if (document == null || frame == null || referenceId.IsNull) { error = "没有可保存的图框实例。"; return false; }
            try
            {
                if (string.IsNullOrWhiteSpace(frame.RegistrationId)) frame.RegistrationId = Guid.NewGuid().ToString("N");
                var oldPath = UserDataPaths.ResolveFromRoot(frame.TemplateRelativePath);
                var project = new PublishPlanStore().GetActiveProject();
                var path = FrameTemplatePaths.ReadablePath(project == null ? "默认项目" : project.Name, frame, oldPath);
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    var reference = transaction.GetObject(referenceId, OpenMode.ForRead, false) as BlockReference;
                    if (reference == null) throw new InvalidOperationException("所选对象不是图框块参照。");
                    var recordId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
                    var record = (BlockTableRecord)transaction.GetObject(recordId, OpenMode.ForRead);
                    WriteTemplate(document.Database, record, path);
                }
                frame.TemplateRelativePath = UserDataPaths.RelativeToRoot(path);
                frame.TemplateSourceDrawingPath = document.Database.Filename;
                try { if (OwnedTemplate(oldPath) && !string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath)) File.Delete(oldPath); } catch { }
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        /// <summary>
        /// 纯文件操作部分已抽到 <see cref="FrameTemplatePaths"/>（启动器要共用同一份实现，
        /// 而本文件依赖 AutoCAD 无法被启动器编译），这里保留同名入口以免调用方改动。
        /// </summary>
        public static bool MakePathsReadable(System.Collections.Generic.IEnumerable<ProjectProfile> projects)
        {
            return FrameTemplatePaths.MakeReadable(projects);
        }

        public static bool DeleteIfUnused(FrameDefinition removed, System.Collections.Generic.IEnumerable<FrameDefinition> remaining, out string error)
        {
            error = null;
            if (removed == null || string.IsNullOrWhiteSpace(removed.TemplateRelativePath)) return true;
            if ((remaining ?? new FrameDefinition[0]).Any(x => x != null && string.Equals(x.TemplateRelativePath, removed.TemplateRelativePath, StringComparison.OrdinalIgnoreCase))) return true;
            try
            {
                var path = UserDataPaths.ResolveFromRoot(removed.TemplateRelativePath);
                var root = Path.GetFullPath(UserDataPaths.FrameTemplatesDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("图框模板路径不在插件的用户配置目录内，已停止删除。");
                if (File.Exists(full)) File.Delete(full);
                var folder = Path.GetDirectoryName(full);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        public static void EnsureAvailable(Document document, FrameDefinition frame)
        {
            if (document == null) throw new ArgumentNullException("document");
            var database = document.Database;
            if (database == null || frame == null || string.IsNullOrWhiteSpace(frame.BlockName)) throw new InvalidOperationException("请选择有效的登记图框。");
            var mode = document.LockMode();
            var alreadyLocked = mode == DocumentLockMode.Write || mode == DocumentLockMode.ExclusiveWrite
                || mode == DocumentLockMode.ProtectedAutoWrite || mode == DocumentLockMode.AutoWrite;
            using (var writeLock = alreadyLocked ? null : document.LockDocument())
            {
                using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                    if (blocks.Has(frame.BlockName)) return;
                }
                var path = UserDataPaths.ResolveFromRoot(frame.TemplateRelativePath);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    var project = new PublishPlanStore().GetActiveProject();
                    var paths = (project.CadFiles ?? new List<string>()).Concat(new[] { frame.TemplateSourceDrawingPath });
                    foreach (Document open in Application.DocumentManager) paths = paths.Concat(new[] { DrawingPath(open) });
                    string error;
                    if (!TryRecover(frame, paths, out error)) throw new InvalidOperationException(error);
                }
                ImportTemplate(database, frame);
            }
        }
        public static bool TryPrepare(Document document, FrameDefinition frame, out string error)
        {
            try { EnsureAvailable(document, frame); error = null; return true; }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        public static bool TryRecover(FrameDefinition frame, IEnumerable<string> drawingPaths, out string error)
        {
            error = null;
            try
            {
                if (frame == null || string.IsNullOrWhiteSpace(frame.BlockName)) throw new InvalidOperationException("请选择已登记的图框。");
                var paths = (drawingPaths ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (paths.Length > 64) throw new InvalidOperationException("项目图纸较多，请直接选择含原图框的 DWG 补齐模板。");
                var candidates = new List<KeyValuePair<string, string>>();
                foreach (var sourcePath in paths)
                {
                    try { WithDrawing(sourcePath, db => {
                        using (var tr = db.TransactionManager.StartOpenCloseTransaction()) {
                            var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                            if (!blocks.Has(frame.BlockName)) return;
                            var record = (BlockTableRecord)tr.GetObject(blocks[frame.BlockName], OpenMode.ForRead);
                            var signature = MatchingSignature(record, tr, frame);
                            if (signature != null) candidates.Add(new KeyValuePair<string, string>(sourcePath, signature));
                        }
                    }); } catch { }
                }
                if (candidates.Count == 0) throw new InvalidOperationException("未找到与登记“" + frame.DisplayName + "”一致的图框模板。已检查原来源、打开图纸及当前项目 CAD 文件；登记参数已保留。");
                if (candidates.Select(c => c.Value).Distinct().Count() != 1) throw new InvalidOperationException("找到多个同名图框版本，不能自动确定原版本。请选择原图纸补齐模板，避免插入错误图框。");
                var store = new PublishPlanStore();var project = store.GetActiveProject();
                var registrations = (project.Frames ?? new List<FrameDefinition>()).Where(f => f != null &&
                    (!string.IsNullOrWhiteSpace(frame.RegistrationId)
                        ? string.Equals(f.RegistrationId, frame.RegistrationId, StringComparison.OrdinalIgnoreCase)
                        : typeof(FrameDefinition).GetProperties().Where(p => p.CanRead && p.CanWrite).All(p => Equals(p.GetValue(f, null), p.GetValue(frame, null))))).ToArray();
                var stored = registrations.Length == 1 ? registrations[0] : null;
                if (stored == null) throw new InvalidOperationException("当前项目的图框登记已变化，请重新打开图框窗口。");
                var oldPath = UserDataPaths.ResolveFromRoot(stored.TemplateRelativePath);
                var destination = FrameTemplatePaths.ReadablePath(project.Name, frame, oldPath);
                var candidate = candidates[0];
                WithDrawing(candidate.Key, db => {
                    using (var tr = db.TransactionManager.StartOpenCloseTransaction()) {
                        var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var record = (BlockTableRecord)tr.GetObject(blocks[frame.BlockName], OpenMode.ForRead);
                        if (MatchingSignature(record, tr, frame) != candidate.Value) throw new InvalidOperationException("原图框已变化，请重新选择图纸。");
                        WriteTemplate(db, record, destination);
                    }
                });
                var relative = UserDataPaths.RelativeToRoot(destination);
                if (string.IsNullOrWhiteSpace(stored.RegistrationId)) stored.RegistrationId = Guid.NewGuid().ToString("N");
                stored.TemplateRelativePath = relative;stored.TemplateSourceDrawingPath = candidate.Key;
                store.SaveProject(project);
                frame.RegistrationId = stored.RegistrationId;frame.TemplateRelativePath = relative;frame.TemplateSourceDrawingPath = candidate.Key;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        private static void ImportTemplate(Database database, FrameDefinition frame)
        {
            var path = UserDataPaths.ResolveFromRoot(frame.TemplateRelativePath);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new InvalidOperationException("图框模板文件不存在，请补齐原图框模板。");
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                    database.Insert(frame.BlockName, source, false);
                }
        }
        private static void WriteTemplate(Database database, BlockTableRecord record, string path)
        {
            if (!OwnedTemplate(path)) throw new InvalidOperationException("图框模板只能保存在插件的图框模板目录。");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp.dwg";
            try
            {
                using (var template = new Database(true, true)) {
                    template.Insbase = record.Origin;
                    var ids = new ObjectIdCollection();foreach (ObjectId id in record) if (id.IsValid && !id.IsErased) ids.Add(id);
                    database.WblockCloneObjects(ids, template.CurrentSpaceId, new IdMapping(), DuplicateRecordCloning.Replace, false);
                    template.SaveAs(temporary, DwgVersion.Current);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static bool OwnedTemplate(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(UserDataPaths.FrameTemplatesDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        private static string MatchingSignature(BlockTableRecord record, Transaction transaction, FrameDefinition frame)
        {
            if (record.IsLayout || record.IsFromExternalReference) return null;
            var tags = new List<string>();var bounds = new Extents3d();var hasBounds = false;
            foreach (ObjectId id in record) {
                var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;if (entity == null) continue;
                var attribute = entity as AttributeDefinition;if (attribute != null && !string.IsNullOrWhiteSpace(attribute.Tag)) tags.Add(attribute.Tag.Trim().ToUpperInvariant());
                try { if (hasBounds) bounds.AddExtents(entity.GeometricExtents);else {bounds = entity.GeometricExtents;hasBounds = true;} } catch { }
            }
            if (!hasBounds || tags.Distinct().Count() != tags.Count) return null;
            var signature = FrameIdentityService.DefinitionSignature(record, transaction);
            if (string.IsNullOrWhiteSpace(signature) || !FrameIdentityService.IsSameVariant(frame, FrameIdentityService.AttributeSignature(tags), signature, FrameIdentityService.AspectRatio(bounds))) return null;
            return signature;
        }
        private static string DrawingPath(Document document)
        {
            try { return string.IsNullOrWhiteSpace(document.Database.Filename) ? document.Name : document.Database.Filename; }catch { return null; }
        }
        private static void WithDrawing(string path, Action<Database> action)
        {
            var full = Path.GetFullPath(path);
            foreach (Document open in Application.DocumentManager) {
                var openPath = DrawingPath(open);
                if (string.IsNullOrWhiteSpace(openPath) || !string.Equals(Path.GetFullPath(openPath), full, StringComparison.OrdinalIgnoreCase)) continue;
                var mode = open.LockMode();var locked = mode == DocumentLockMode.Write || mode == DocumentLockMode.ExclusiveWrite || mode == DocumentLockMode.ProtectedAutoWrite || mode == DocumentLockMode.AutoWrite;
                using (var gate = locked ? null : open.LockDocument()) action(open.Database);return;
            }
            using (var source = new Database(false, true)) {
                source.ReadDwgFile(full, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);action(source);
            }
        }
        internal static void RunNativeCheck(Document document)
        {
            var before = Convert.ToInt32(Application.GetSystemVariable("DBMOD"));
            var previousRoot = Environment.GetEnvironmentVariable(UserDataPaths.UserDataRootEnvironmentVariable);
            var root = Path.Combine(Path.GetTempPath(), "wanluo-frame-check-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(UserDataPaths.UserDataRootEnvironmentVariable, Path.Combine(root, "settings"));
            try
            {
                Directory.CreateDirectory(root);var first = Path.Combine(root, "source.dwg");var other = Path.Combine(root, "variant.dwg");
                void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
                string Create(string path, bool extra)
                {
                    using (var db = new Database(true, true)) {
                        string signature;
                        using (var tr = db.TransactionManager.StartTransaction()) {
                            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                            var record = new BlockTableRecord { Name = "LegacyFrame", Origin = new Autodesk.AutoCAD.Geometry.Point3d(100, 200, 0) };
                            table.Add(record);tr.AddNewlyCreatedDBObject(record, true);
                            var border = new Polyline();border.AddVertexAt(0, new Autodesk.AutoCAD.Geometry.Point2d(120, 230), 0, 0, 0);
                            border.AddVertexAt(1, new Autodesk.AutoCAD.Geometry.Point2d(961, 230), 0, 0, 0);border.AddVertexAt(2, new Autodesk.AutoCAD.Geometry.Point2d(961, 824), 0, 0, 0);
                            border.AddVertexAt(3, new Autodesk.AutoCAD.Geometry.Point2d(120, 824), 0, 0, 0);border.Closed = true;record.AppendEntity(border);tr.AddNewlyCreatedDBObject(border, true);
                            var attribute = new AttributeDefinition { Tag = "SCALE", TextString = "1:100", Height = 2.5, Position = new Autodesk.AutoCAD.Geometry.Point3d(200, 250, 0) };
                            record.AppendEntity(attribute);tr.AddNewlyCreatedDBObject(attribute, true);
                            if (extra) { var line = new Line(new Autodesk.AutoCAD.Geometry.Point3d(300, 300, 0), new Autodesk.AutoCAD.Geometry.Point3d(400, 300, 0));record.AppendEntity(line);tr.AddNewlyCreatedDBObject(line, true); }
                            signature = FrameIdentityService.DefinitionSignature(record, tr);tr.Commit();
                        }
                        db.SaveAs(path, DwgVersion.Current);return signature;
                    }
                }
                var originalSignature = Create(first, false);Create(other, true);var sourceBytes = File.ReadAllBytes(first);
                var frame = new FrameDefinition { BlockName = "LegacyFrame", PaperSize = "A1", PaperOrientation = "横向", PrintScaleAttributeTag = "SCALE",
                    Note = "保留备注", DefaultPrintScale = "1:100", DefaultSheetNumber = "原图号", HasLayoutRange = true, LayoutLeftMargin = 20, LayoutRightMargin = 10, LayoutTopMargin = 10, LayoutBottomMargin = 20 };
                var store = new PublishPlanStore();var project = store.CreateProject("Native frame recovery");project.Frames.Add(frame);project.CadFiles.Add(first);store.SaveProject(project);
                Require(frame.RegistrationId == null && frame.TemplateRelativePath == null, "Legacy fixture is not legacy.");
                string error;Require(!TryRecover(frame, new[] { Path.Combine(root, "missing.dwg") }, out error) && frame.TemplateRelativePath == null, "Missing source changed registration.");
                Require(TryRecover(frame, new[] { first }, out error), error);
                Require(!string.IsNullOrWhiteSpace(frame.RegistrationId) && File.Exists(UserDataPaths.ResolveFromRoot(frame.TemplateRelativePath)), "Legacy ID or portable cache not created.");
                Require(frame.Note == "保留备注" && frame.DefaultSheetNumber == "原图号" && frame.PrintScaleAttributeTag == "SCALE"
                    && frame.HasLayoutRange && frame.LayoutLeftMargin == 20 && frame.LayoutRightMargin == 10, "Recovery reset registration fields.");
                var loaded = store.LoadFrames().Single();Require(loaded.RegistrationId == frame.RegistrationId && loaded.TemplateRelativePath == frame.TemplateRelativePath
                    && loaded.TemplateSourceDrawingPath == first, "Recovery did not persist metadata.");
                using (var target = new Database(true, true)) {
                    ImportTemplate(target, frame);
                    using (var tr = target.TransactionManager.StartTransaction()) {
                        var table = (BlockTable)tr.GetObject(target.BlockTableId, OpenMode.ForRead);Require(table.Has(frame.BlockName), "Template not imported.");
                        var definition = (BlockTableRecord)tr.GetObject(table[frame.BlockName], OpenMode.ForRead);
                        Require(definition.Cast<ObjectId>().Any(id => tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition), "Attribute definitions were lost.");
                        var placement = FrameBlockPlacementService.Measure(definition, tr, frame, 100);
                        Require(Math.Abs(placement.DefinitionWidth * placement.Factor - 84100) < .01 && Math.Abs(placement.DefinitionHeight * placement.Factor - 59400) < .01, "Wrong paper dimensions after recovery.");
                        using (var reference = new BlockReference(placement.PositionForLowerLeft(new Autodesk.AutoCAD.Geometry.Point3d(3000, 4000, 0)), definition.ObjectId) { ScaleFactors = new Autodesk.AutoCAD.Geometry.Scale3d(placement.Factor) }) {
                            var minimum = placement.DefinitionMinimum.TransformBy(reference.BlockTransform);
                            Require(minimum.DistanceTo(new Autodesk.AutoCAD.Geometry.Point3d(3000, 4000, 0)) < .001, "Frame basepoint changed.");
                        }
                    }
                }
                var ambiguous = new FrameDefinition { RegistrationId = Guid.NewGuid().ToString("N"), BlockName = frame.BlockName, PaperSize = "A1", PaperOrientation = "横向", PrintScaleAttributeTag = "SCALE", Note = "另一个登记" };
                project = store.GetActiveProject();project.Frames.Add(ambiguous);store.SaveProject(project);
                Require(!TryRecover(ambiguous, new[] { first, other }, out error) && error.Contains("多个") && ambiguous.TemplateRelativePath == null, "Ambiguous variants were silently chosen.");
                ambiguous.DefinitionSignature = originalSignature;project = store.GetActiveProject();project.Frames.Single(f => f.RegistrationId == ambiguous.RegistrationId).DefinitionSignature = originalSignature;store.SaveProject(project);
                Require(TryRecover(ambiguous, new[] { first, other }, out error), error);
                Require(TryRecover(ambiguous, new[] { first }, out error), error);
                Require(File.ReadAllBytes(first).SequenceEqual(sourceBytes), "Original DWG was modified.");
                using (var form = new BatchPdfPublisher.Views.FrameCreationForm(document, null)) {
                    var guard = typeof(BatchPdfPublisher.Views.FrameCreationForm).GetMethod("ExecuteCadAction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    guard.Invoke(form, new object[] { new Action(() => { throw new InvalidOperationException("Expected missing-frame UI error"); }) });
                }
                Require(Convert.ToInt32(Application.GetSystemVariable("DBMOD")) == before, "Current drawing was modified.");
                document.Editor.WriteMessage("\nFRAME_TEMPLATE_NATIVE_OK legacyNullId recovered cached metadataPreserved sourceReadOnly importAttributes paperScale lowerLeft variants rejected strongMatch atomicRefresh managedUiGuard unchangedDrawing\n");
            }
            finally { Environment.SetEnvironmentVariable(UserDataPaths.UserDataRootEnvironmentVariable, previousRoot); }
        }
    }
}
