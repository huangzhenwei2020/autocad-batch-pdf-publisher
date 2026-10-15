using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>Analytic CAD bulge geometry. Tessellation is only a rendering/solid boundary.</summary>
    public sealed class CurveGeometry
    {
        public PointModel Start { get; private set; }
        public PointModel End { get; private set; }
        public PointModel Center { get; private set; }
        public double Radius { get; private set; }
        public double Sweep { get; private set; }
        public double Length { get; private set; }
        public bool IsArc { get { return Math.Abs(Sweep) > 1e-10; } }
        private double _angle, _ux, _uy;

        public CurveGeometry(PointModel start, PointModel end, double bulge)
        {
            if (start == null || end == null || !Finite(start.X) || !Finite(start.Y)
                || !Finite(end.X) || !Finite(end.Y) || !Finite(bulge))
                throw new ArgumentException("圆弧坐标无效。");
            Start = start; End = end;
            var dx = end.X - start.X; var dy = end.Y - start.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-6) {
                if(Math.Abs(bulge)>1e-10)throw new ArgumentException("圆弧起点和终点不能相同。");
                _ux=1;return;
            }
            _ux = dx / chord; _uy = dy / chord;
            if (Math.Abs(bulge) < 1e-10) { Length = chord; return; }
            Sweep = 4 * Math.Atan(bulge);
            var offset = chord * (1 / bulge - bulge) / 4;
            Center = new PointModel(start.X + dx / 2 - _uy * offset, start.Y + dy / 2 + _ux * offset);
            Radius = chord * (Math.Abs(bulge) + 1 / Math.Abs(bulge)) / 4;
            _angle = Math.Atan2(start.Y - Center.Y, start.X - Center.X);
            Length = Radius * Math.Abs(Sweep);
            if (!Finite(Radius) || !Finite(Length)) throw new ArgumentException("圆弧半径超出范围。");
        }

        public static CurveGeometry ForWall(WallModel w)
            => new CurveGeometry(new PointModel(w.X1, w.Y1), new PointModel(w.X2, w.Y2), w.Bulge);
        public static CurveGeometry ForLine(ViewLine l)
            => new CurveGeometry(new PointModel(l.X1, l.Y1), new PointModel(l.X2, l.Y2), l.Bulge);
        public static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        public PointModel At(double t, double leftOffset = 0)
        {
            if (!IsArc) return new PointModel(Start.X + (End.X - Start.X) * t - _uy * leftOffset,
                Start.Y + (End.Y - Start.Y) * t + _ux * leftOffset);
            var a = _angle + Sweep * t;
            var r = Radius - Math.Sign(Sweep) * leftOffset;
            return new PointModel(Center.X + Math.Cos(a) * r, Center.Y + Math.Sin(a) * r);
        }
        public PointModel Tangent(double t)
        {
            if (!IsArc) return new PointModel(_ux, _uy);
            var a = _angle + Sweep * t; var sign = Math.Sign(Sweep);
            return new PointModel(-Math.Sin(a) * sign, Math.Cos(a) * sign);
        }
        public double Parameter(double x, double y)
        {
            if(Length<1e-6)return 0;
            if (!IsArc) return Math.Max(0, Math.Min(1, ((x - Start.X) * _ux + (y - Start.Y) * _uy) / Length));
            var delta = Math.Atan2(y - Center.Y, x - Center.X) - _angle;
            delta = Sweep > 0 ? Positive(delta) : -Positive(-delta);
            if (Math.Abs(delta) <= Math.Abs(Sweep)) return delta / Sweep;
            return Distance(x, y, Start) <= Distance(x, y, End) ? 0 : 1;
        }
        public double DistanceTo(double x, double y, double leftOffset = 0)
            => Distance(x, y, At(Parameter(x, y), leftOffset));
        private static double Distance(double x, double y, PointModel p)
            => Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y));
        private static double Positive(double angle) => (angle % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);

        public List<PointModel> Sample(double leftOffset = 0, double tolerance = .5)
        {
            var steps = 1;
            if (IsArc)
            {
                var r = Math.Abs(Radius - Math.Sign(Sweep) * leftOffset);
                var step = 2 * Math.Acos(Math.Max(-1, Math.Min(1, 1 - tolerance / Math.Max(tolerance, r))));
                steps = Math.Min(2048, Math.Max(2, (int)Math.Ceiling(Math.Abs(Sweep) / Math.Min(Math.PI / 36, Math.Max(.0001, step)))));
            }
            return Enumerable.Range(0, steps + 1).Select(i => At((double)i / steps, leftOffset)).ToList();
        }
        public List<PointModel> BoundsPoints(double leftOffset = 0)
        {
            var points = new List<PointModel> { At(0, leftOffset), At(1, leftOffset) };
            if (IsArc) for (var i = 0; i < 4; i++)
            {
                var a = i * Math.PI / 2;
                var x = Center.X + Math.Cos(a) * Radius; var y = Center.Y + Math.Sin(a) * Radius;
                var t = Parameter(x, y);
                if (t > 0 && t < 1) points.Add(At(t, leftOffset));
            }
            return points;
        }
        public static List<PointModel> Expand(IList<PointModel> ring)
        {
            if (ring == null || ring.Count < 2) throw new ArgumentException("轮廓至少需要两个点。");
            var result = new List<PointModel>();
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                var samples = new CurveGeometry(a, b, a.Bulge).Sample();
                result.AddRange(samples.Take(samples.Count - 1));
                if (result.Count > 8192) throw new ArgumentException("圆弧轮廓过于复杂。");
            }
            return result;
        }
        public static List<PointModel> WallOutline(WallModel wall)
        {
            var c = ForWall(wall); var offset = WallReferenceGeometry.BodyOffset(wall);
            var half = wall.Thickness / 2;
            return c.Sample(offset + half).Concat(c.Sample(offset - half).AsEnumerable().Reverse()).ToList();
        }
        public static List<PointModel> WallBoundary(WallModel wall)
        {
            var c=ForWall(wall);var offset=WallReferenceGeometry.BodyOffset(wall);var half=wall.Thickness/2;
            var a=c.At(0,offset+half);a.Bulge=wall.Bulge;
            var b=c.At(1,offset+half);var d=c.At(1,offset-half);d.Bulge=-wall.Bulge;
            return new List<PointModel> {a,b,d,c.At(0,offset-half)};
        }
        public static double ThroughBulge(PointModel start, PointModel end, PointModel through)
        {
            var bx = end.X - start.X; var by = end.Y - start.Y;
            var cx = through.X - start.X; var cy = through.Y - start.Y;
            var cross = bx * cy - by * cx;
            if (Math.Abs(cross) < 1e-6 * Math.Sqrt(bx * bx + by * by))
                throw new ArgumentException("三点不能共线，请指定弧线经过的点。");
            var b2 = bx * bx + by * by; var c2 = cx * cx + cy * cy;
            var ux = (b2 * cy - c2 * by) / (2 * cross);
            var uy = (bx * c2 - cx * b2) / (2 * cross);
            var a = Math.Atan2(-uy, -ux);
            var e = Positive(Math.Atan2(by - uy, bx - ux) - a);
            var mid = Positive(Math.Atan2(cy - uy, cx - ux) - a);
            var sweep = mid < e ? e : e - 2 * Math.PI;
            return Math.Tan(sweep / 4);
        }
        public static bool TryCorner(PointModel before, PointModel corner, PointModel after, double radius,
            out PointModel first, out PointModel second, out double bulge, out string error)
        {
            first = second = null; bulge = 0; error = null;
            if (!Finite(radius) || radius < 0) { error = "圆角半径必须是非负毫米数。"; return false; }
            var ax = before.X - corner.X; var ay = before.Y - corner.Y;
            var bx = after.X - corner.X; var by = after.Y - corner.Y;
            var la = Math.Sqrt(ax * ax + ay * ay); var lb = Math.Sqrt(bx * bx + by * by);
            if (la < 10 || lb < 10) { error = "圆角两侧边太短。"; return false; }
            ax /= la; ay /= la; bx /= lb; by /= lb;
            var theta = Math.Acos(Math.Max(-1, Math.Min(1, ax * bx + ay * by)));
            if (theta < 1e-5 || Math.PI - theta < 1e-5) { error = "平行或折返边不能圆角。"; return false; }
            var trim = radius / Math.Tan(theta / 2);
            if (trim > la - 10 || trim > lb - 10) { error = "圆角半径过大，两侧须各保留至少 10 mm。"; return false; }
            first = new PointModel(corner.X + ax * trim, corner.Y + ay * trim);
            second = new PointModel(corner.X + bx * trim, corner.Y + by * trim);
            bulge = Math.Sign(bx * ay - by * ax) * Math.Tan((Math.PI - theta) / 4);
            return true;
        }

        public static List<PointModel> Intersections(CurveGeometry a,CurveGeometry b)
        {
            var candidates=new List<PointModel>();
            if(!a.IsArc&&!b.IsArc) {
                var ax=a.End.X-a.Start.X;var ay=a.End.Y-a.Start.Y;var bx=b.End.X-b.Start.X;var by=b.End.Y-b.Start.Y;
                var cross=ax*by-ay*bx;
                if(Math.Abs(cross)>1e-8) {
                    var t=((b.Start.X-a.Start.X)*by-(b.Start.Y-a.Start.Y)*bx)/cross;
                    candidates.Add(a.At(t));
                }
            } else if(a.IsArc&&b.IsArc) {
                var dx=b.Center.X-a.Center.X;var dy=b.Center.Y-a.Center.Y;var d=Math.Sqrt(dx*dx+dy*dy);
                if(d>1e-8&&d<=a.Radius+b.Radius+1e-6&&d>=Math.Abs(a.Radius-b.Radius)-1e-6) {
                    var along=(a.Radius*a.Radius-b.Radius*b.Radius+d*d)/(2*d);
                    var height=Math.Sqrt(Math.Max(0,a.Radius*a.Radius-along*along));
                    foreach(var sign in new[] {-1,1})candidates.Add(new PointModel(a.Center.X+dx*along/d-sign*dy*height/d,
                        a.Center.Y+dy*along/d+sign*dx*height/d));
                }
            } else {
                var line=a.IsArc?b:a;var arc=a.IsArc?a:b;
                var dx=line.End.X-line.Start.X;var dy=line.End.Y-line.Start.Y;
                var x=line.Start.X-arc.Center.X;var y=line.Start.Y-arc.Center.Y;
                var aa=dx*dx+dy*dy;var bb=2*(x*dx+y*dy);var cc=x*x+y*y-arc.Radius*arc.Radius;
                var discriminant=bb*bb-4*aa*cc;
                if(aa>1e-8&&discriminant>=-1e-6)foreach(var sign in new[]{-1,1})
                    candidates.Add(line.At((-bb+sign*Math.Sqrt(Math.Max(0,discriminant)))/(2*aa)));
            }
            return candidates.Where(p=>a.DistanceTo(p.X,p.Y)<.001&&b.DistanceTo(p.X,p.Y)<.001).ToList();
        }
    }
}
