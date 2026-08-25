using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace WL.Stair.CadShared.PlanCapture
{
    /// <summary>
    /// Read-only compatibility probe used before enabling stair-plan capture in the
    /// production LTDY path. It never opens a database object for write.
    /// </summary>
    internal static class StairPlanObjectProbe
    {
        private static readonly string[] CandidateProperties =
        {
            "ObjectName", "Name", "Type", "Style", "Width", "Thickness",
            "WallWidth", "LeftWidth", "RightWidth", "BaseLine", "CenterLine",
            "Location", "Rotation", "Scale", "DrawScale", "FloorHeight",
            "StoreyHeight", "StepCount", "TreadCount", "RiserCount",
            "TreadWidth", "RiserHeight", "StairWidth"
        };

        public static void Execute(Document document)
        {
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var options = new PromptEntityOptions(
                "\n请选择一个原生天正楼梯对象进行只读识别（不会修改图纸）：");
            options.SetRejectMessage("\n请选择一个有效对象。");
            var result = editor.GetEntity(options);
            if (result.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\n已取消天正楼梯兼容性探针。\n");
                return;
            }

            var report = new StringBuilder();
            report.AppendLine("万落建筑楼梯平面对象兼容性探针");
            report.AppendLine("时间=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            report.AppendLine("图纸=" + SafeDocumentName(document));
            report.AppendLine("注意=本探针只读，不会修改所选对象或周边对象。");

            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var selected = transaction.GetObject(result.ObjectId, OpenMode.ForRead, false) as Entity;
                if (selected == null)
                {
                    editor.WriteMessage("\n所选对象无法读取。\n");
                    return;
                }

                report.AppendLine();
                report.AppendLine("[所选对象]");
                AppendEntityIdentity(report, selected);
                AppendCandidateProperties(report, selected);

                Extents3d selectedExtents;
                if (TryGetExtents(selected, out selectedExtents))
                {
                    report.AppendLine("范围=" + FormatExtents(selectedExtents));
                    AppendNearbyCandidates(
                        report,
                        document.Database,
                        transaction,
                        selected.ObjectId,
                        Expand(selectedExtents, 3000.0));
                }
                else
                {
                    report.AppendLine("范围=<无法取得，未扫描周边墙体>");
                }
            }

            var logPath = ResolveLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            File.AppendAllText(logPath, report.ToString() + Environment.NewLine, new UTF8Encoding(false));
            editor.WriteMessage(
                "\n只读探针完成。请检查所选对象与周边墙候选是否正确。\n日志：{0}\n",
                logPath);
        }

        private static void AppendNearbyCandidates(
            StringBuilder report,
            Database database,
            Transaction transaction,
            ObjectId selectedId,
            Extents3d searchExtents)
        {
            report.AppendLine();
            report.AppendLine("[周边天正/墙体候选，搜索外扩=3000mm]");

            var currentSpace = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
            if (currentSpace == null)
            {
                report.AppendLine("<当前空间无法读取>");
                return;
            }

            var found = 0;
            foreach (ObjectId objectId in currentSpace)
            {
                if (objectId == selectedId)
                {
                    continue;
                }

                var entity = transaction.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                if (entity == null)
                {
                    continue;
                }

                Extents3d extents;
                if (!TryGetExtents(entity, out extents) || !Intersects(searchExtents, extents))
                {
                    continue;
                }

                var dxfName = SafeDxfName(entity);
                var managedName = entity.GetType().FullName ?? string.Empty;
                var objectName = SafeComProperty(entity, "ObjectName");
                var identity = (dxfName + " " + managedName + " " + objectName).ToUpperInvariant();
                if (!identity.Contains("TCH")
                    && !identity.Contains("TARCH")
                    && !identity.Contains("WALL")
                    && !identity.Contains("STAIR"))
                {
                    continue;
                }

                found++;
                report.AppendLine();
                report.AppendLine("候选#" + found.ToString(CultureInfo.InvariantCulture));
                AppendEntityIdentity(report, entity);
                report.AppendLine("范围=" + FormatExtents(extents));
                AppendCandidateProperties(report, entity);
            }

            if (found == 0)
            {
                report.AppendLine("<未找到候选；后续应回退到用户闭合多段线或扩大人工搜索范围>");
            }
        }

        private static void AppendEntityIdentity(StringBuilder report, Entity entity)
        {
            report.AppendLine("句柄=" + entity.Handle);
            report.AppendLine("DXF=" + SafeDxfName(entity));
            report.AppendLine("托管类型=" + (entity.GetType().FullName ?? "<未知>"));
            report.AppendLine("COM类型=" + SafeComProperty(entity, "ObjectName"));
            report.AppendLine("图层=" + entity.Layer);
        }

        private static void AppendCandidateProperties(StringBuilder report, Entity entity)
        {
            var values = new List<string>();
            foreach (var propertyName in CandidateProperties)
            {
                var value = SafeComProperty(entity, propertyName);
                if (!string.IsNullOrWhiteSpace(value) && value != "<不可读>")
                {
                    values.Add(propertyName + "=" + value);
                }
            }

            report.AppendLine(values.Count == 0
                ? "候选属性=<当前 COM 接口未暴露>"
                : "候选属性=" + string.Join("; ", values));
        }

        private static string SafeComProperty(Entity entity, string propertyName)
        {
            try
            {
                var acadObject = entity.AcadObject;
                if (acadObject == null)
                {
                    return "<不可读>";
                }

                var value = acadObject.GetType().InvokeMember(
                    propertyName,
                    BindingFlags.GetProperty,
                    null,
                    acadObject,
                    null,
                    CultureInfo.InvariantCulture);
                return value == null
                    ? string.Empty
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return "<不可读>";
            }
        }

        private static string SafeDxfName(Entity entity)
        {
            try
            {
                return entity.GetRXClass() == null ? "<未知>" : entity.GetRXClass().DxfName;
            }
            catch
            {
                return "<未知>";
            }
        }

        private static bool TryGetExtents(Entity entity, out Extents3d extents)
        {
            try
            {
                extents = entity.GeometricExtents;
                return true;
            }
            catch
            {
                extents = default(Extents3d);
                return false;
            }
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

        private static string FormatExtents(Extents3d extents)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:0.###},{1:0.###})-({2:0.###},{3:0.###})",
                extents.MinPoint.X,
                extents.MinPoint.Y,
                extents.MaxPoint.X,
                extents.MaxPoint.Y);
        }

        private static string SafeDocumentName(Document document)
        {
            try
            {
                return document.Name ?? "<未命名图纸>";
            }
            catch
            {
                return "<未命名图纸>";
            }
        }

        private static string ResolveLogPath()
        {
            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var cursor = new DirectoryInfo(assemblyDirectory ?? AppDomain.CurrentDomain.BaseDirectory);
            for (var index = 0; cursor != null && index < 6; index++, cursor = cursor.Parent)
            {
                var configured = Path.Combine(cursor.FullName, "用户配置文件");
                if (Directory.Exists(configured))
                {
                    return Path.Combine(configured, "日志", "stair-plan-capture.log");
                }
            }

            return Path.Combine(
                assemblyDirectory ?? AppDomain.CurrentDomain.BaseDirectory,
                "用户配置文件",
                "日志",
                "stair-plan-capture.log");
        }
    }
}
