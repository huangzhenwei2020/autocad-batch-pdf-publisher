using System;
using System.IO;
using System.Linq;
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
                    using (var template = new Database(true, true))
                    {
                        template.Insbase = record.Origin;
                        var ids = new ObjectIdCollection();
                        foreach (ObjectId id in record) if (id.IsValid && !id.IsErased) ids.Add(id);
                        var mapping = new IdMapping();
                        document.Database.WblockCloneObjects(ids, template.CurrentSpaceId, mapping, DuplicateRecordCloning.Replace, false);
                        if (File.Exists(path)) File.Delete(path);
                        template.SaveAs(path, DwgVersion.Current);
                    }
                }
                frame.TemplateRelativePath = UserDataPaths.RelativeToRoot(path);
                try { if (!string.IsNullOrWhiteSpace(oldPath) && !string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath)) File.Delete(oldPath); } catch { }
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
                    throw new InvalidOperationException("当前图纸缺少图框块“" + frame.BlockName + "”，而该旧登记尚无便携图框模板。请在含该图框的图纸中重新登记一次；以后复制整个插件文件夹即可在其他电脑自动使用。");
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                    database.Insert(frame.BlockName, source, false);
                }
            }
        }
    }
}
