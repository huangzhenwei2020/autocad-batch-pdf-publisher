using System;
using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace WL.Stair.CadShared
{
    internal static class StairTitleService
    {
        private const string DrawingNameDxfName = "TCH_DRAWINGNAME";
        private const string DrawingNameComName = "TDbDrawingName";
        private static readonly string[] TitleTextCandidates =
        {
            "TitleText", "NameText", "TextString", "FigName", "FigNameText",
            "Title", "Name", "Text", "DrawingName"
        };

        public static void Insert(Database database, BlockTableRecord space, Transaction transaction,
            Point3d center, string title, int drawingScale, double targetWidth, string layerName)
        {
            if (TryInsertNative(database, space, transaction, center, title, drawingScale, targetWidth)) return;
            InsertFallback(database, space, transaction, center, title, drawingScale, layerName);
        }

        private static bool TryInsertNative(Database database, BlockTableRecord space,
            Transaction transaction, Point3d center, string title, int drawingScale, double targetWidth)
        {
            try
            {
                var template = FindTemplate(database, transaction);
                if (template == null)
                {
                    WriteTitleTrace(database, "native template not found");
                    return false;
                }
                var clone = template.Clone() as Entity;
                if (clone == null) return false;
                Point3d templateCenter;
                double templateWidth;
                try
                {
                    var extents = template.GeometricExtents;
                    templateCenter = new Point3d((extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
                        (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0, extents.MinPoint.Z);
                    templateWidth = Math.Max(1.0, extents.MaxPoint.X - extents.MinPoint.X);
                }
                catch
                {
                    templateCenter = center;
                    templateWidth = 1.0;
                }
                var width = Math.Max(10.0, targetWidth * 0.8);
                var factor = templateWidth > width ? width / templateWidth : 1.0;
                if (factor < 1.0 && factor > 0.01)
                    clone.TransformBy(Matrix3d.Scaling(factor, templateCenter));
                clone.TransformBy(Matrix3d.Displacement(center - templateCenter));
                clone.SetDatabaseDefaults(database);
                space.AppendEntity(clone);
                transaction.AddNewlyCreatedDBObject(clone, true);
                object com = null;
                try { com = clone.AcadObject; } catch { }
                if (com != null)
                {
                    TrySetCom(com, "Scale", Convert.ToDouble(drawingScale, CultureInfo.InvariantCulture));
                    TrySetCom(com, "DrawScale", true);
                    TrySetCom(com, "ScaleText", "1:" + drawingScale.ToString(CultureInfo.InvariantCulture));
                    foreach (var property in TitleTextCandidates)
                        if (TrySetCom(com, property, title)) break;
                    try { com.GetType().InvokeMember("Update", BindingFlags.InvokeMethod,
                        null, com, null, CultureInfo.CurrentCulture); } catch { }
                }
                WriteTitleTrace(database, "native title inserted text=" + (title ?? string.Empty)
                    + " scale=1:" + drawingScale);
                return true;
            }
            catch (Exception exception)
            {
                WriteTitleTrace(database, "native title failed error=" + exception);
                return false;
            }
        }

        private static void InsertFallback(Database database, BlockTableRecord space,
            Transaction transaction, Point3d center, string title, int drawingScale, string layerName)
        {
            var scale = Math.Max(1, drawingScale);
            var titleHeight = Math.Max(3.5 * scale, 70.0);
            var noteHeight = Math.Max(2.5 * scale, 50.0);
            var titleText = string.IsNullOrWhiteSpace(title) ? "楼梯大样" : title;
            var text = new MText
            {
                Contents = titleText,
                Location = center,
                Attachment = AttachmentPoint.MiddleCenter,
                TextHeight = titleHeight,
                TextStyleId = FindTextStyle(database, transaction, "WL-文字-标题"),
                Layer = layerName
            };
            Append(space, transaction, text);
            var width = EstimateTextWidth(titleText, titleHeight) + titleHeight * 0.8;
            var underlineY = center.Y - titleHeight * 0.72;
            var underline = new Polyline(2)
            {
                Layer = layerName,
                ConstantWidth = Math.Max(0.5 * scale, 10.0)
            };
            underline.AddVertexAt(0, new Point2d(center.X - width / 2.0, underlineY), 0.0, 0.0, 0.0);
            underline.AddVertexAt(1, new Point2d(center.X + width / 2.0, underlineY), 0.0, 0.0, 0.0);
            Append(space, transaction, underline);
            var ratio = new MText
            {
                Contents = "1:" + scale.ToString(CultureInfo.InvariantCulture),
                Location = new Point3d(center.X, underlineY - titleHeight * 0.62, center.Z),
                Attachment = AttachmentPoint.MiddleCenter,
                TextHeight = noteHeight,
                TextStyleId = FindTextStyle(database, transaction, "WL-文字-标注"),
                Layer = layerName
            };
            Append(space, transaction, ratio);
            WriteTitleTrace(database, "fallback title inserted text=" + titleText
                + " scale=1:" + scale);
        }

        private static void WriteTitleTrace(Database database, string message)
        {
            try
            {
                var root = Environment.GetEnvironmentVariable("WANLUO_ARCHITECTURE_TOOLS_ROOT");
                if (string.IsNullOrWhiteSpace(root)) return;
                var logs = System.IO.Path.Combine(root, "用户配置文件", "Logs");
                System.IO.Directory.CreateDirectory(logs);
                System.IO.File.AppendAllText(System.IO.Path.Combine(logs, "stair-title.log"),
                    DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        private static ObjectId FindTextStyle(Database database, Transaction transaction, string name)
        {
            try
            {
                var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
                if (table.Has(name)) return table[name];
            }
            catch { }
            return database.Textstyle;
        }

        private static Entity FindTemplate(Database database, Transaction transaction)
        {
            var space = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead, false)
                as BlockTableRecord;
            if (space == null) return null;
            foreach (ObjectId id in space)
            {
                var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (IsDrawingName(entity)) return entity;
            }
            return null;
        }

        private static bool IsDrawingName(Entity entity)
        {
            if (entity == null) return false;
            try
            {
                if (string.Equals(entity.GetRXClass().DxfName, DrawingNameDxfName,
                    StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            try
            {
                var com = entity.AcadObject;
                var value = com == null ? null : com.GetType().InvokeMember("ObjectName",
                    BindingFlags.GetProperty, null, com, null, CultureInfo.InvariantCulture);
                return string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture),
                    DrawingNameComName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool TrySetCom(object instance, string name, object value)
        {
            try
            {
                instance.GetType().InvokeMember(name, BindingFlags.SetProperty, null, instance,
                    new[] { value }, CultureInfo.CurrentCulture);
                return true;
            }
            catch { return false; }
        }

        private static double EstimateTextWidth(string text, double height)
        {
            if (string.IsNullOrEmpty(text)) return height;
            var width = 0.0;
            foreach (var character in text)
                width += character > 0x2E7F || character == '　' ? height : height * 0.55;
            return width;
        }

        private static void Append(BlockTableRecord space, Transaction transaction, Entity entity)
        {
            space.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
        }
    }
}
