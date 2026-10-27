using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace BatchPdfPublisher.Services
{
    internal sealed class LineCorrectionPlan
    {
        internal ObjectId Id;
        internal List<CorrectionPoint> Original;
        internal LineCorrectionResult Result;
        internal bool Closed, HasArcs;
        public string Handle { get; set; }
        public string Type { get; set; }
        public string Status { get; set; }
        public int Segments { get { return Result == null ? 0 : Result.CorrectedSegments; } }
        public int VerticesZ { get { return Result == null ? 0 : Result.FlattenedVertices; } }
        public string Movement { get { return Result == null ? "—" : Result.MaximumMovement.ToString("0.###"); } }
    }

    internal static class LineCorrectionService
    {
        internal static List<LineCorrectionPlan> Analyze(Database database, IEnumerable<ObjectId> ids, LineCorrectionOptions options)
        {
            var plans = new List<LineCorrectionPlan>();
            using (var tx = database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids.Distinct())
                {
                    var plan = new LineCorrectionPlan { Id = id, Handle = id.Handle.ToString() }; plans.Add(plan);
                    try
                    {
                        var entity = tx.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null) { plan.Status = "对象已删除"; continue; }
                        plan.Type = entity.GetRXClass().DxfName;
                        if (((LayerTableRecord)tx.GetObject(entity.LayerId, OpenMode.ForRead)).IsLocked)
                        { plan.Status = "锁定图层，跳过"; continue; }
                        bool closed, arcs; string error;
                        if (!Read(entity, tx, out plan.Original, out closed, out arcs, out error)) { plan.Status = error; continue; }
                        plan.Closed = closed; plan.HasArcs = arcs;
                        plan.Result = LineCorrectionGeometry.Calculate(plan.Original, closed, arcs, options);
                        plan.Status = plan.Result.Changed ? "待校正" : "无需修改";
                        if (!string.IsNullOrEmpty(plan.Result.Warning)) plan.Status += " · " + plan.Result.Warning;
                    }
                    catch (Exception ex) { plan.Status = "跳过：" + ex.Message; }
                }
            }
            return plans;
        }

        internal static int Apply(Document document, IEnumerable<ObjectId> ids, LineCorrectionOptions options, out int skipped)
        {
            var plans = Analyze(document.Database, ids, options); skipped = plans.Count(p => p.Result == null || !string.IsNullOrEmpty(p.Result.Warning));
            var applied = 0;
            using (var tx = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var plan in plans.Where(p => p.Result != null && p.Result.Changed))
                {
                    using (var edit = document.Database.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var entity = (Entity)edit.GetObject(plan.Id, OpenMode.ForWrite);
                            Write(entity, edit, plan.Result.Points, options.FlattenZ);
                            edit.Commit(); applied++;
                        }
                        catch (Exception ex) { skipped++; document.Editor.WriteMessage("\n对象 " + plan.Handle + " 未修改：" + ex.Message); }
                    }
                }
                tx.Commit();
            }
            document.Editor.Regen(); return applied;
        }

        internal static bool Read(Entity entity, Transaction tx, out List<CorrectionPoint> points,
            out bool closed, out bool arcs, out string error)
        {
            var vertices = new List<CorrectionPoint>(); points = vertices; closed = false; arcs = false; error = null;
            Action<Point3d> add = p => vertices.Add(new CorrectionPoint(p.X, p.Y, p.Z));
            var line = entity as Line;
            if (line != null) { add(line.StartPoint); add(line.EndPoint); return true; }
            var poly = entity as Polyline;
            if (poly != null)
            {
                if (Math.Abs(poly.Normal.Z) < 1 - 1e-10) { error = "倾斜平面多段线，跳过"; return false; }
                closed = poly.Closed;
                for (var i = 0; i < poly.NumberOfVertices; i++) { add(poly.GetPoint3dAt(i)); arcs |= poly.GetBulgeAt(i) != 0; }
                return true;
            }
            var poly3 = entity as Polyline3d;
            if (poly3 != null && poly3.PolyType == Poly3dType.SimplePoly)
            {
                closed = poly3.Closed;
                foreach (ObjectId id in poly3) add(((PolylineVertex3d)tx.GetObject(id, OpenMode.ForRead)).Position);
                return true;
            }
            var poly2 = entity as Polyline2d;
            if (poly2 != null && poly2.PolyType == Poly2dType.SimplePoly)
            {
                if (Math.Abs(poly2.Normal.Z) < 1 - 1e-10) { error = "倾斜平面多段线，跳过"; return false; }
                closed = poly2.Closed;
                foreach (ObjectId id in poly2)
                {
                    var vertex = (Vertex2d)tx.GetObject(id, OpenMode.ForRead);
                    var p = vertex.Position;
                    add(new Point3d(p.X, p.Y, poly2.Elevation).TransformBy(Matrix3d.PlaneToWorld(poly2.Normal)));
                    arcs |= vertex.Bulge != 0;
                }
                return true;
            }
            error = "非普通直线／多段线，跳过"; return false;
        }

        private static void Write(Entity entity, Transaction tx, IList<CorrectionPoint> points, bool flatten)
        {
            Func<CorrectionPoint, Point3d> point = p => new Point3d(p.X, p.Y, p.Z);
            var line = entity as Line;
            if (line != null) { line.StartPoint = point(points[0]); line.EndPoint = point(points[1]); return; }
            var poly = entity as Polyline;
            if (poly != null)
            {
                var transform = Matrix3d.WorldToPlane(poly.Normal);
                for (var i = 0; i < points.Count; i++) { var p = point(points[i]).TransformBy(transform); poly.SetPointAt(i, new Point2d(p.X, p.Y)); }
                if (flatten) poly.Elevation = 0;
                return;
            }
            var poly3 = entity as Polyline3d;
            if (poly3 != null)
            {
                var i = 0;
                foreach (ObjectId id in poly3) ((PolylineVertex3d)tx.GetObject(id, OpenMode.ForWrite)).Position = point(points[i++]);
                return;
            }
            var poly2 = (Polyline2d)entity;
            var ecs = Matrix3d.WorldToPlane(poly2.Normal); var index = 0;
            foreach (ObjectId id in poly2)
            {
                var vertex = (Vertex2d)tx.GetObject(id, OpenMode.ForWrite);
                var p = point(points[index++]).TransformBy(ecs);
                vertex.Position = new Point3d(p.X, p.Y, vertex.Position.Z);
            }
            if (flatten) poly2.Elevation = 0;
        }
    }
}
