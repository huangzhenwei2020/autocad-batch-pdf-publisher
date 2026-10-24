using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Features.BuildingModel.Services
{
    // Diagnostic samples are not semantic balcony contours or railing paths.
    internal static class CadBuildingDiagnosticGeometry
    {
        internal const int MaximumPaths = 2048;
        private const int MaximumVertices = 16384;
        private const int MaximumDepth = 6;

        public static void Read(Entity source, CadBuildingProbeEntity record)
        {
            record.GeometryPaths = new List<CadProbeGeometryPath>();
            record.UnsupportedGeometry = 0; record.GeometryTruncated = false;
            var visited = 0; var vertices = 0; var stack = new HashSet<ObjectId>();
            void Visit(Entity entity, string path, int depth, Matrix3d transform)
            {
                if (++visited > MaximumPaths * 2 || record.GeometryPaths.Count >= MaximumPaths
                    || vertices >= MaximumVertices || depth > MaximumDepth)
                { record.GeometryTruncated = true; return; }
                if (entity is BlockReference block)
                {
                    if (depth >= MaximumDepth) { record.GeometryTruncated = true; return; }
                    var id = block.BlockTableRecord;
                    if (id.IsNull || !stack.Add(id)) { record.UnsupportedGeometry++; return; }
                    try
                    {
                        using (var tx = id.Database.TransactionManager.StartOpenCloseTransaction())
                        {
                            var definition = (BlockTableRecord)tx.GetObject(id, OpenMode.ForRead);
                            if (definition.IsFromExternalReference || definition.IsFromOverlayReference)
                            { record.UnsupportedGeometry++; return; }
                            foreach (ObjectId childId in definition)
                            {
                                if (record.GeometryTruncated) break;
                                if (tx.GetObject(childId, OpenMode.ForRead) is Entity child)
                                    Visit(child, path + "/" + childId.Handle, depth + 1, transform * block.BlockTransform);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception || ex is InvalidOperationException || ex is ArgumentException)
                    { record.UnsupportedGeometry++; }
                    finally { stack.Remove(id); }
                    return;
                }
                var item = new CadProbeGeometryPath { SourcePath = path, SourceTransform = new List<double>(transform.ToArray()) };
                if (entity is Line line)
                {
                    item.Kind = "Line"; item.Vertices.Add(Point(line.StartPoint)); item.Vertices.Add(Point(line.EndPoint));
                    item.PreviewPoints.AddRange(item.Vertices);
                }
                else if (entity is Arc arc)
                {
                    item.Kind = "Arc"; item.Normal = Point(arc.Normal);
                    item.Vertices.Add(Point(arc.Center)); item.Vertices.Add(Point(arc.StartPoint)); item.Vertices.Add(Point(arc.EndPoint));
                    Sample(arc, item, arc.TotalAngle);
                }
                else if (entity is Circle circle)
                {
                    item.Kind = "Circle"; item.Closed = true; item.Normal = Point(circle.Normal);
                    item.Vertices.Add(Point(circle.Center)); item.Vertices.Add(Point(circle.GetPointAtParameter(circle.StartParam)));
                    Sample(circle, item, Math.PI * 2);
                }
                else if (entity is Polyline poly)
                {
                    if (poly.NumberOfVertices * 2L + vertices > MaximumVertices)
                    { record.GeometryTruncated = true; return; }
                    item.Kind = "Polyline"; item.Closed = poly.Closed; item.Normal = Point(poly.Normal);
                    for (var i = 0; i < poly.NumberOfVertices; i++)
                    {
                        item.Vertices.Add(Point(poly.GetPoint3dAt(i))); item.Bulges.Add(poly.GetBulgeAt(i));
                        item.PreviewPoints.Add(Point(poly.GetPoint3dAt(i)));
                        if (i == poly.NumberOfVertices - 1 && !poly.Closed) continue;
                        var bulge = poly.GetBulgeAt(i);
                        if (double.IsNaN(bulge) || double.IsInfinity(bulge)) throw new InvalidOperationException("Non-finite bulge.");
                        if (Math.Abs(bulge) < 1e-12) continue;
                        var samples = Math.Min(96, Math.Max(2, (int)Math.Ceiling(Math.Abs(4 * Math.Atan(bulge)) / (Math.PI / 24))));
                        if (vertices + item.Vertices.Count + item.PreviewPoints.Count + samples > MaximumVertices)
                        { record.GeometryTruncated = true; return; }
                        for (var j = 1; j < samples; j++) item.PreviewPoints.Add(Point(poly.GetPointAtParameter(i + (double)j / samples)));
                    }
                }
                else if (entity is DBText || entity is MText || entity is AttributeDefinition || entity is AttributeReference)
                    return;
                else
                {
                    if (entity is ProxyEntity || depth >= MaximumDepth)
                    {
                        record.UnsupportedGeometry++;
                        if (depth >= MaximumDepth) record.GeometryTruncated = true;
                        return;
                    }
                    if (!(entity is Polyline2d || entity is Polyline3d
                        || (entity.GetRXClass()?.DxfName ?? "").StartsWith("TCH_", StringComparison.OrdinalIgnoreCase)))
                    { record.UnsupportedGeometry++; return; }
                    var parts = new DBObjectCollection();
                    try
                    {
                        using (var copy = (Entity)entity.Clone()) copy.Explode(parts);
                        if (parts.Count == 0) record.UnsupportedGeometry++;
                        for (var i = 0; i < parts.Count; i++)
                        {
                            if (record.GeometryTruncated) break;
                            if (parts[i] is Entity child) Visit(child, path + "/" + i, depth + 1, transform);
                        }
                    }
                    catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception || ex is InvalidOperationException || ex is ArgumentException)
                    { record.UnsupportedGeometry++; }
                    finally { foreach (DBObject part in parts) part.Dispose(); parts.Dispose(); }
                    return;
                }
                if (vertices + item.Vertices.Count + item.PreviewPoints.Count > MaximumVertices)
                { record.GeometryTruncated = true; return; }
                vertices += item.Vertices.Count + item.PreviewPoints.Count;
                for (var i = 0; i < item.PreviewPoints.Count; i++)
                {
                    var p = item.PreviewPoints[i];
                    item.PreviewPoints[i] = Point(new Point3d(p.X, p.Y, p.Z).TransformBy(transform));
                }
                record.GeometryPaths.Add(item);
            }
            try { Visit(source, record.Handle ?? "source", 0, Matrix3d.Identity); }
            catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception || ex is InvalidOperationException || ex is ArgumentException)
            { record.UnsupportedGeometry++; }
            record.GeometryStatus = record.GeometryTruncated ? "几何采集超限（局部预览）"
                : record.UnsupportedGeometry > 0 ? "几何部分可读（含未支持图元）"
                : record.GeometryPaths.Count == 0 ? "未取得线/弧几何" : "原始几何已读取（用途与轮廓待核验）";
        }

        private static void Sample(Curve curve, CadProbeGeometryPath item, double sweep)
        {
            var count = Math.Min(96, Math.Max(2, (int)Math.Ceiling(sweep / (Math.PI / 24))));
            for (var i = 0; i <= count; i++)
                item.PreviewPoints.Add(Point(curve.GetPointAtParameter(curve.StartParam + (curve.EndParam - curve.StartParam) * i / count)));
        }
        private static CadProbePoint Point(Vector3d p) => Point(new Point3d(p.X, p.Y, p.Z));
        private static CadProbePoint Point(Point3d p)
        {
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsNaN(p.Z)
                || double.IsInfinity(p.X) || double.IsInfinity(p.Y) || double.IsInfinity(p.Z))
                throw new InvalidOperationException("Non-finite diagnostic geometry.");
            return new CadProbePoint { X = p.X, Y = p.Y, Z = p.Z };
        }
    }
}
