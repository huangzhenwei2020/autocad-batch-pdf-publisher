using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    // Independent, read-only source geometry; never converted into model elements.
    public sealed class CadPlanUnderlay
    {
        public string CapturedAt { get; set; }
        public int UnsupportedEntities { get; set; }
        public List<ComponentPlanPrimitive> Primitives { get; set; } = new List<ComponentPlanPrimitive>();
        public List<CadUnderlayText> Texts { get; set; } = new List<CadUnderlayText>();
        public List<int> NonSnapPrimitiveIndices { get; set; } = new List<int>();
        public void Validate()
        {
            if (Primitives == null || Texts == null || Primitives.Count > 50000 || Texts.Count > 5000)
                throw new InvalidDataException("参照底图数量超限，请缩小登记范围。");
            if(NonSnapPrimitiveIndices!=null && (NonSnapPrimitiveIndices.Count>Primitives.Count || NonSnapPrimitiveIndices.Any(i=>i<0||i>=Primitives.Count)))
                throw new InvalidDataException("参照底图标注索引无效。");
            foreach (var p in Primitives)
                if (p == null || !new[] { p.X1, p.Y1, p.X2, p.Y2, p.Radius, p.StartDegrees, p.SweepDegrees }.All(Finite)
                    || (p.Kind != "Line" && p.Kind != "Arc" && p.Kind != "Circle")
                    || (p.Kind != "Line" && (p.Radius <= 0 || Math.Abs(p.SweepDegrees) < .000001 || Math.Abs(p.SweepDegrees) > 360.00001)))
                    throw new InvalidDataException("参照底图包含无效几何。");
            foreach (var t in Texts)
                if (t == null || t.Text == null || t.Text.Length > 2048
                    || !new[] { t.X, t.Y, t.Height, t.WidthFactor, t.RotationDegrees }.All(Finite)
                    || t.Height <= 0 || t.WidthFactor <= 0)
                    throw new InvalidDataException("参照底图包含无效文字。");
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= 1e12;
    }
    public sealed class CadUnderlayText
    {
        public string Text { get; set; }
        public string Layer { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Height { get; set; }
        public double WidthFactor { get; set; } = 1;
        public double RotationDegrees { get; set; }
    }
    public sealed class CadUnderlaySnap
    {
        public double X { get; set; }
        public double Y { get; set; }
        public string Kind { get; set; } = "无";
        public bool Snapped => Kind != "无";
    }

    public sealed class CadUnderlayIndex
    {
        public List<ComponentPlanPrimitive> Primitives { get; } = new List<ComponentPlanPrimitive>();
        public List<CadUnderlayText> Texts { get; } = new List<CadUnderlayText>();
        public List<PointModel> Region { get; }
        private readonly Node root;
        private readonly HashSet<ComponentPlanPrimitive> annotations = new HashSet<ComponentPlanPrimitive>();
        private sealed class Node
        {
            public double X0, Y0, X1, Y1;
            public Node Left, Right;
            public ComponentPlanPrimitive[] Items;
        }
        public CadUnderlayIndex(CadFloorPlanCapture capture)
        {
            var data = capture?.Underlay;
            if (data == null) { Region = new List<PointModel>(); return; }
            data.Validate();
            var frame = capture.Floor.Alignment;
            Region = capture.Floor.RegionPolygon.Select(frame.ToModel).ToList();
            foreach (var p in data.Primitives)
            {
                var a = frame.ToModel(new PointModel(p.X1, p.Y1));
                var b = frame.ToModel(new PointModel(p.X2, p.Y2));
                Primitives.Add(new ComponentPlanPrimitive { Kind = p.Kind, SourceLayer = p.SourceLayer,
                    SourcePath = p.SourcePath, SourceLineType = p.SourceLineType,
                    X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                    Radius = p.Radius * frame.MillimetresPerCadUnit,
                    StartDegrees = p.StartDegrees + frame.RotationRadians * 180 / Math.PI, SweepDegrees = p.SweepDegrees });
            }
            foreach (var t in data.Texts)
            {
                var p = frame.ToModel(new PointModel(t.X, t.Y));
                Texts.Add(new CadUnderlayText { Text = t.Text, Layer = t.Layer, X = p.X, Y = p.Y,
                    Height = t.Height * frame.MillimetresPerCadUnit, WidthFactor = t.WidthFactor,
                    RotationDegrees = t.RotationDegrees + frame.RotationRadians * 180 / Math.PI });
            }
            foreach(var i in data.NonSnapPrimitiveIndices??new List<int>())annotations.Add(Primitives[i]);
            root = Build(Primitives.ToArray());
        }
        private static Node Build(ComponentPlanPrimitive[] items)
        {
            if (items.Length == 0) return null;
            var n = new Node { X0 = double.MaxValue, Y0 = double.MaxValue, X1 = double.MinValue, Y1 = double.MinValue };
            foreach (var p in items)
            {
                var r = p.Kind == "Line" ? 0 : p.Radius;
                n.X0 = Math.Min(n.X0, p.Kind == "Line" ? Math.Min(p.X1, p.X2) : p.X1 - r);
                n.X1 = Math.Max(n.X1, p.Kind == "Line" ? Math.Max(p.X1, p.X2) : p.X1 + r);
                n.Y0 = Math.Min(n.Y0, p.Kind == "Line" ? Math.Min(p.Y1, p.Y2) : p.Y1 - r);
                n.Y1 = Math.Max(n.Y1, p.Kind == "Line" ? Math.Max(p.Y1, p.Y2) : p.Y1 + r);
            }
            if (items.Length <= 8) n.Items = items;
            else
            {
                var x = n.X1 - n.X0 >= n.Y1 - n.Y0;
                Array.Sort(items, (a, b) => (x ? a.X1 + a.X2 : a.Y1 + a.Y2).CompareTo(x ? b.X1 + b.X2 : b.Y1 + b.Y2));
                n.Left = Build(items.Take(items.Length / 2).ToArray());
                n.Right = Build(items.Skip(items.Length / 2).ToArray());
            }
            return n;
        }
        public IEnumerable<ComponentPlanPrimitive> Query(double x0, double y0, double x1, double y1)
        {
            if (root == null) yield break;
            var stack = new Stack<Node>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.X1 < x0 || n.Y1 < y0 || n.X0 > x1 || n.Y0 > y1) continue;
                if (n.Items != null) { foreach (var p in n.Items) yield return p; }
                else { stack.Push(n.Left); stack.Push(n.Right); }
            }
        }
        public bool Contains(double x, double y)
        {
            if (Region.Count < 3) return false;
            var inside = false;
            for (var i = 0; i < Region.Count; i++)
            {
                var a = Region[i]; var b = Region[(i + 1) % Region.Count];
                var line = new ComponentPlanPrimitive {Kind="Line",X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y};
                if (Distance(Nearest(line,x,y),x,y) < .000001) return true;
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }
        public static PointModel PointAt(ComponentPlanPrimitive p, double fraction)
        {
            if (p.Kind == "Line") return new PointModel(p.X1 + (p.X2 - p.X1) * fraction, p.Y1 + (p.Y2 - p.Y1) * fraction);
            var radians = (p.StartDegrees + p.SweepDegrees * fraction) * Math.PI / 180;
            return new PointModel(p.X1 + p.Radius * Math.Cos(radians), p.Y1 + p.Radius * Math.Sin(radians));
        }
        private static bool OnArc(ComponentPlanPrimitive p, double x, double y)
        {
            if (p.Kind == "Circle") return true;
            var angle = Math.Atan2(y - p.Y1, x - p.X1) * 180 / Math.PI;
            var delta = p.SweepDegrees >= 0 ? angle - p.StartDegrees : p.StartDegrees - angle;
            delta = (delta % 360 + 360) % 360;
            return delta <= Math.Abs(p.SweepDegrees) + .000001 || delta > 359.999999;
        }
        private static PointModel Nearest(ComponentPlanPrimitive p, double x, double y)
        {
            if (p.Kind == "Line")
            {
                var dx = p.X2 - p.X1; var dy = p.Y2 - p.Y1; var l = dx * dx + dy * dy;
                return PointAt(p, l < 1e-20 ? 0 : Math.Max(0, Math.Min(1, ((x - p.X1) * dx + (y - p.Y1) * dy) / l)));
            }
            var length = Math.Sqrt((x - p.X1) * (x - p.X1) + (y - p.Y1) * (y - p.Y1));
            if (length < 1e-12) return PointAt(p, 0);
            var q = new PointModel(p.X1 + (x - p.X1) * p.Radius / length, p.Y1 + (y - p.Y1) * p.Radius / length);
            if (OnArc(p, q.X, q.Y)) return q;
            var a = PointAt(p, 0); var b = PointAt(p, 1);
            return Distance(a, x, y) < Distance(b, x, y) ? a : b;
        }
        private static double Distance(PointModel p, double x, double y) => Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
        public CadUnderlaySnap Snap(double x, double y, double tolerance, ISet<string> hiddenLayers, PointModel from = null)
        {
            var result = new CadUnderlaySnap { X = x, Y = y };
            var rank = int.MaxValue; var distance = tolerance;
            void Consider(PointModel p, int priority, string kind)
            {
                var d = Distance(p, x, y);
                if (d > tolerance || !Contains(p.X, p.Y) || priority > rank || (priority == rank && d >= distance)) return;
                rank = priority; distance = d; result.X = p.X; result.Y = p.Y; result.Kind = "CAD · " + kind;
            }
            var nearby = Query(x - tolerance, y - tolerance, x + tolerance, y + tolerance)
                .Where(p => !annotations.Contains(p) && (hiddenLayers == null || !hiddenLayers.Contains(p.SourceLayer ?? "0"))).ToList();
            var curves = new List<ComponentPlanPrimitive>();
            foreach (var p in nearby)
            {
                if (p.Kind != "Circle") { Consider(PointAt(p, 0), 0, "端点"); Consider(PointAt(p, 1), 0, "端点"); }
                if (p.Kind != "Line") Consider(new PointModel(p.X1, p.Y1), 2, "圆心");
                if (p.Kind != "Circle") Consider(PointAt(p, .5), 2, "中点");
                var nearest = Nearest(p, x, y);
                Consider(nearest, 4, "最近点");
                if (Distance(nearest, x, y) <= tolerance) curves.Add(p);
                if (from != null && p.Kind == "Line")
                {
                    var foot = Nearest(p, from.X, from.Y);
                    var a = PointAt(p, 0); var b = PointAt(p, 1);
                    if (Distance(foot, a.X, a.Y) > .000001 && Distance(foot, b.X, b.Y) > .000001) Consider(foot, 3, "垂足");
                }
            }
            for (var i = 0; i < curves.Count; i++)
                for (var j = i + 1; j < curves.Count; j++)
                    foreach (var p in Intersections(curves[i], curves[j])) Consider(p, 1, "交点");
            return result;
        }
        public static IEnumerable<PointModel> Intersections(ComponentPlanPrimitive a, ComponentPlanPrimitive b)
        {
            if (a.Kind != "Line" && b.Kind == "Line") { foreach (var p in Intersections(b, a)) yield return p; yield break; }
            if (a.Kind == "Line" && b.Kind == "Line")
            {
                var dx = a.X2 - a.X1; var dy = a.Y2 - a.Y1; var ex = b.X2 - b.X1; var ey = b.Y2 - b.Y1;
                var cross = dx * ey - dy * ex;
                if (Math.Abs(cross) <= 1e-12 * Math.Sqrt((dx * dx + dy * dy) * (ex * ex + ey * ey))) yield break;
                var t = ((b.X1 - a.X1) * ey - (b.Y1 - a.Y1) * ex) / cross;
                var u = ((b.X1 - a.X1) * dy - (b.Y1 - a.Y1) * dx) / cross;
                if (t >= 0 && t <= 1 && u >= 0 && u <= 1) yield return PointAt(a, t);
            }
            else if (a.Kind == "Line")
            {
                var dx = a.X2 - a.X1; var dy = a.Y2 - a.Y1; var ox = a.X1 - b.X1; var oy = a.Y1 - b.Y1;
                var aa = dx * dx + dy * dy; var bb = 2 * (ox * dx + oy * dy); var cc = ox * ox + oy * oy - b.Radius * b.Radius;
                var discriminant = bb * bb - 4 * aa * cc;
                if (aa < 1e-20 || discriminant < 0) yield break;
                foreach (var t in new[] { (-bb - Math.Sqrt(discriminant)) / (2 * aa), (-bb + Math.Sqrt(discriminant)) / (2 * aa) })
                    if (t >= 0 && t <= 1) { var p = PointAt(a, t); if (OnArc(b, p.X, p.Y)) yield return p; }
            }
            else
            {
                var dx = b.X1 - a.X1; var dy = b.Y1 - a.Y1; var d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 1e-12 || d > a.Radius + b.Radius || d < Math.Abs(a.Radius - b.Radius)) yield break;
                var along = (a.Radius * a.Radius - b.Radius * b.Radius + d * d) / (2 * d);
                var height = Math.Sqrt(Math.Max(0, a.Radius * a.Radius - along * along));
                foreach (var side in new[] { -1, 1 })
                {
                    var p = new PointModel(a.X1 + dx * along / d - side * dy * height / d, a.Y1 + dy * along / d + side * dx * height / d);
                    if (OnArc(a, p.X, p.Y) && OnArc(b, p.X, p.Y)) yield return p;
                }
            }
        }
    }
}
