using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadArchSpec.CadTable;

namespace CadArchSpec.Host.Shared.CadTable
{
    internal static class CadTableEntityReader
    {
        private const int MaximumExplodeDepth = 4;
        private const int MaximumExplodedObjects = 20000;
        private static readonly string[] TianzhengTextProperties =
        {
            "TextString", "Text", "Contents", "Content", "Caption", "Value"
        };

        public static CadTableEntityReadResult Read(Transaction transaction, IEnumerable<ObjectId> objectIds,
            bool includeHiddenLayers = false)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (objectIds == null) throw new ArgumentNullException(nameof(objectIds));
            var result = new CadTableEntityReadResult();
            foreach (var objectId in objectIds.Where(id => !id.IsNull && id.IsValid).Distinct())
            {
                var entity = transaction.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                if (entity == null) continue;
                result.SourceEntityCount++;
                if (!includeHiddenLayers && !IsVisible(transaction, entity))
                {
                    result.SkippedHiddenEntityCount++;
                    continue;
                }
                var sourceHandle = SafeHandle(entity);
                Collect(transaction, entity, sourceHandle, result, 0, false, includeHiddenLayers);
            }
            return result;
        }

        private static void Collect(Transaction transaction, Entity entity, string sourceHandle,
            CadTableEntityReadResult result, int depth, bool explodedClone, bool includeHiddenLayers)
        {
            if (entity == null) return;
            if (!includeHiddenLayers && !IsVisible(transaction, entity))
            {
                result.SkippedHiddenEntityCount++;
                return;
            }
            var sourceKind = explodedClone ? CadTextSourceKind.ExplodedClone : CadTextSourceKind.Standard;
            var dbText = entity as DBText;
            if (dbText != null)
            {
                AddText(result, dbText.TextString, dbText.TextString, Center(dbText, dbText.Position), dbText.Height, dbText.Rotation, sourceKind, sourceHandle, DxfName(dbText), dbText);
                return;
            }

            var mText = entity as MText;
            if (mText != null)
            {
                AddText(result, mText.Contents, MTextContentNormalizer.Normalize(mText.Contents, mText.Text),
                    Center(mText, mText.Location), mText.TextHeight, mText.Rotation, sourceKind, sourceHandle, DxfName(mText), mText);
                return;
            }

            var attribute = entity as AttributeReference;
            if (attribute != null)
            {
                AddText(result, attribute.TextString, attribute.TextString, Center(attribute, attribute.Position), attribute.Height, attribute.Rotation, sourceKind, sourceHandle, DxfName(attribute), attribute);
                return;
            }

            var line = entity as Line;
            if (line != null)
            {
                AddSegment(result, line.StartPoint, line.EndPoint, sourceHandle, line.Layer);
                return;
            }

            var polyline = entity as Polyline;
            if (polyline != null)
            {
                for (var index = 0; index < polyline.NumberOfVertices - 1; index++)
                    AddSegment(result, polyline.GetPoint3dAt(index), polyline.GetPoint3dAt(index + 1), sourceHandle, polyline.Layer);
                if (polyline.Closed && polyline.NumberOfVertices > 2)
                    AddSegment(result, polyline.GetPoint3dAt(polyline.NumberOfVertices - 1), polyline.GetPoint3dAt(0), sourceHandle, polyline.Layer);
                return;
            }

            var polyline2d = entity as Polyline2d;
            if (polyline2d != null && !explodedClone)
            {
                var points = new List<Point3d>();
                foreach (ObjectId vertexId in polyline2d)
                {
                    var vertex = transaction.GetObject(vertexId, OpenMode.ForRead, false) as Vertex2d;
                    if (vertex != null) points.Add(vertex.Position);
                }
                for (var index = 0; index < points.Count - 1; index++)
                    AddSegment(result, points[index], points[index + 1], sourceHandle, polyline2d.Layer);
                if (polyline2d.Closed && points.Count > 2)
                    AddSegment(result, points[points.Count - 1], points[0], sourceHandle, polyline2d.Layer);
                return;
            }

            var blockReference = entity as BlockReference;
            if (blockReference != null && !explodedClone)
            {
                // AttributeReference positions are already expressed in the inserted
                // block's world coordinates and are not reliably returned by Explode.
                foreach (ObjectId attributeId in blockReference.AttributeCollection)
                {
                    if (attributeId.IsNull || !attributeId.IsValid) continue;
                    var blockAttribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                    if (blockAttribute != null)
                        Collect(transaction, blockAttribute, sourceHandle, result, depth + 1, false, includeHiddenLayers);
                }
            }

            if (!explodedClone && IsTianzhengText(entity))
            {
                string text;
                if (TryReadTianzhengText(entity, out text))
                {
                    var fallback = Center(entity, Point3d.Origin);
                    AddText(result, text, MTextContentNormalizer.Normalize(text, text), fallback,
                        Height(entity), Rotation(entity), CadTextSourceKind.TianzhengProperty, sourceHandle, DxfName(entity), entity);
                    return;
                }
            }

            if (depth >= MaximumExplodeDepth)
            {
                AddWarning(result, sourceHandle, "对象分解深度超过限制", entity);
                return;
            }
            if (result.ExplodedObjectCount >= MaximumExplodedObjects)
            {
                AddWarning(result, sourceHandle, "对象分解数量超过安全限制", entity);
                return;
            }

            var objects = new DBObjectCollection();
            try
            {
                entity.Explode(objects);
                if (objects.Count == 0)
                {
                    if (IsTianzhengCandidate(entity)) AddWarning(result, sourceHandle, "天正对象未返回可读取的文字或图形副本", entity);
                    return;
                }
                result.ExplodedObjectCount += objects.Count;
                foreach (DBObject item in objects)
                {
                    if (result.ExplodedObjectCount > MaximumExplodedObjects) break;
                    var child = item as Entity;
                    if (child != null) Collect(transaction, child, sourceHandle, result, depth + 1, true, includeHiddenLayers);
                }
            }
            catch (Exception exception)
            {
                if (IsTianzhengCandidate(entity)) AddWarning(result, sourceHandle, "天正对象只读分解失败：" + exception.GetBaseException().Message, entity);
            }
            finally
            {
                foreach (DBObject item in objects) item.Dispose();
            }
        }

        private static void AddText(CadTableEntityReadResult result, string text, string plainText,
            Point3d center, double height, double rotationRadians, CadTextSourceKind sourceKind,
            string sourceHandle, string dxfName, Entity boundsSource)
        {
            var normalized = (plainText ?? text ?? string.Empty).Trim();
            if (normalized.Length == 0) return;
            Extents3d bounds;
            var hasBounds = TryGetBounds(boundsSource, out bounds);
            result.Input.TextFragments.Add(new CadTextFragment
            {
                Text = (text ?? normalized).Trim(),
                PlainText = normalized,
                Center = new CadTablePoint(center.X, center.Y),
                HasBounds = hasBounds,
                Left = hasBounds ? bounds.MinPoint.X : 0d,
                Bottom = hasBounds ? bounds.MinPoint.Y : 0d,
                Right = hasBounds ? bounds.MaxPoint.X : 0d,
                Top = hasBounds ? bounds.MaxPoint.Y : 0d,
                Width = hasBounds ? Math.Abs(bounds.MaxPoint.X - bounds.MinPoint.X) : 0d,
                Height = Math.Abs(height),
                RotationDegrees = rotationRadians * 180d / Math.PI,
                SourceKind = sourceKind,
                SourceHandle = sourceHandle,
                SourceDxfName = dxfName
            });
        }

        private static bool TryGetBounds(Entity entity, out Extents3d bounds)
        {
            try
            {
                bounds = entity.GeometricExtents;
                return bounds.MaxPoint.X > bounds.MinPoint.X && bounds.MaxPoint.Y > bounds.MinPoint.Y;
            }
            catch
            {
                bounds = new Extents3d();
                return false;
            }
        }

        private static void AddSegment(CadTableEntityReadResult result, Point3d start, Point3d end, string sourceHandle, string layer)
        {
            result.Input.Segments.Add(new CadTableSegment
            {
                Start = new CadTablePoint(start.X, start.Y),
                End = new CadTablePoint(end.X, end.Y),
                SourceHandle = sourceHandle,
                Layer = layer ?? string.Empty
            });
        }

        private static bool TryReadTianzhengText(Entity entity, out string text)
        {
            text = string.Empty;
            object acadObject;
            try { acadObject = entity.AcadObject; }
            catch { return false; }
            if (acadObject == null) return false;
            foreach (var property in TianzhengTextProperties)
            {
                try
                {
                    var value = acadObject.GetType().InvokeMember(property, BindingFlags.GetProperty, null, acadObject, null, CultureInfo.CurrentCulture);
                    var candidate = Convert.ToString(value, CultureInfo.CurrentCulture);
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    text = candidate.Trim();
                    return true;
                }
                catch
                {
                }
            }
            return false;
        }

        private static bool IsTianzhengText(DBObject value)
        {
            var dxf = DxfName(value);
            return dxf.StartsWith("TCH_", StringComparison.OrdinalIgnoreCase) &&
                (dxf.IndexOf("TEXT", StringComparison.OrdinalIgnoreCase) >= 0 || dxf.IndexOf("WORD", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsTianzhengCandidate(DBObject value)
        {
            var dxf = DxfName(value);
            var className = string.Empty;
            try { className = value.GetRXClass().Name ?? string.Empty; }
            catch { }
            return dxf.StartsWith("TCH_", StringComparison.OrdinalIgnoreCase) ||
                className.IndexOf("Tianzheng", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value is ProxyEntity;
        }

        private static Point3d Center(Entity entity, Point3d fallback)
        {
            try
            {
                var extents = entity.GeometricExtents;
                return new Point3d((extents.MinPoint.X + extents.MaxPoint.X) * 0.5d, (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5d, 0d);
            }
            catch { return fallback; }
        }

        private static double Height(Entity entity)
        {
            try { var extents = entity.GeometricExtents; return Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y); }
            catch { return 0d; }
        }

        private static double Rotation(Entity entity)
        {
            object acadObject;
            try { acadObject = entity.AcadObject; }
            catch { return 0d; }
            if (acadObject == null) return 0d;
            try
            {
                var value = acadObject.GetType().InvokeMember("Rotation", BindingFlags.GetProperty, null, acadObject, null, CultureInfo.CurrentCulture);
                return Convert.ToDouble(value, CultureInfo.CurrentCulture);
            }
            catch { return 0d; }
        }

        private static string SafeHandle(DBObject value)
        {
            try { return value.Handle.ToString(); }
            catch { return string.Empty; }
        }

        private static string DxfName(DBObject value)
        {
            try { return value.GetRXClass().DxfName ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static void AddWarning(CadTableEntityReadResult result, string sourceHandle, string message, DBObject value)
        {
            result.Warnings.Add("Handle " + (string.IsNullOrWhiteSpace(sourceHandle) ? "未知" : sourceHandle) + "（" + DxfName(value) + "）：" + message);
        }

        private static bool IsVisible(Transaction transaction, Entity entity)
        {
            try
            {
                if (!entity.Visible) return false;
                if (entity.LayerId.IsNull || !entity.LayerId.IsValid) return true;
                var layer = transaction.GetObject(entity.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer == null || !layer.IsOff && !layer.IsFrozen;
            }
            catch
            {
                // A proxy clone may not expose a database-backed layer. Do not drop
                // visible geometry merely because its layer metadata is unavailable.
                return entity.Visible;
            }
        }
    }
}
