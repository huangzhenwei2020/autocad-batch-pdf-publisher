using BatchPdfPublisher.Services;

internal static class LineCorrectionGeometryTests
{
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    private static void Near(double a, double b) { Check(Math.Abs(a - b) < 1e-7, $"{a} != {b}"); }
    private static int Main()
    {
        try
        {
            var options = new LineCorrectionOptions();
            foreach (var reverse in new[] { false, true })
            foreach (var vertical in new[] { false, true })
            {
                var p = new List<CorrectionPoint> { new(100, 300, 10), vertical ? new(101, 1300, -3) : new(1100, 301, -3) };
                if (reverse) p.Reverse();
                var r = LineCorrectionGeometry.Calculate(p, false, false, options);
                Check(r.CorrectedSegments == 1 && r.FlattenedVertices == 2, "Line correction count.");
                Near((r.Points[0].X + r.Points[1].X) / 2, (p[0].X + p[1].X) / 2);
                Near((r.Points[0].Y + r.Points[1].Y) / 2, (p[0].Y + p[1].Y) / 2);
                Near(Math.Sqrt(Math.Pow(r.Points[1].X - r.Points[0].X, 2) + Math.Pow(r.Points[1].Y - r.Points[0].Y, 2)), Math.Sqrt(1000001));
                Near(vertical ? r.Points[0].X : r.Points[0].Y, vertical ? r.Points[1].X : r.Points[1].Y);
                Check(r.Points.All(v => v.Z == 0) && p[0].Z != 0, "Flatten/source mutation.");
            }
            var rectangle = new List<CorrectionPoint> { new(0, 0, 30), new(1000, 1, 30), new(1001, 1000, 30), new(1, 1001, 30) };
            var closed = LineCorrectionGeometry.Calculate(rectangle, true, false, options);
            Check(closed.CorrectedSegments == 4 && closed.Points.Count == 4 && closed.Warning == null, "Closed polyline.");
            Near(closed.Points[0].Y, closed.Points[1].Y); Near(closed.Points[1].X, closed.Points[2].X);
            Near(closed.Points[2].Y, closed.Points[3].Y); Near(closed.Points[3].X, closed.Points[0].X);
            var open = LineCorrectionGeometry.Calculate(rectangle.Take(3).ToList(), false, false, options);
            Near(open.Points[0].Y, open.Points[1].Y); Near(open.Points[1].X, open.Points[2].X);
            options.FlattenZ = false;
            Check(LineCorrectionGeometry.Calculate(rectangle, true, false, options).Points.All(p => p.Z == 30), "Z changed with option off.");
            var diagonal = LineCorrectionGeometry.Calculate(new[] { new CorrectionPoint(0, 0, 3), new CorrectionPoint(100, 100, 3) }, false, false, options);
            Check(!diagonal.Changed, "Outside tolerance changed.");
            options.Horizontal = false;
            Check(!LineCorrectionGeometry.Calculate(new[] { new CorrectionPoint(0, 0), new CorrectionPoint(1000, 1) }, false, false, options).Changed, "Disabled horizontal changed.");
            options.FlattenZ = true; options.Horizontal = true;
            var arc = LineCorrectionGeometry.Calculate(rectangle, true, true, options);
            Check(arc.CorrectedSegments == 0 && arc.FlattenedVertices == 4 && arc.Warning != null, "Arc geometry changed.");
            var parallel = LineCorrectionGeometry.Calculate(new[] { new CorrectionPoint(0, 0, 5), new CorrectionPoint(1000, 1, 5), new CorrectionPoint(2000, 2, 5) }, false, false, options);
            Check(parallel.Warning != null && parallel.CorrectedSegments == 0 && parallel.FlattenedVertices == 3, "Inconsistent parallel axes distorted.");
            var zero = LineCorrectionGeometry.Calculate(new[] { new CorrectionPoint(1, 1, 3), new CorrectionPoint(1, 1, 5) }, false, false, options);
            Check(zero.Warning != null && zero.FlattenedVertices == 2 && zero.CorrectedSegments == 0, "Z-only zero-length path.");
            options.ToleranceDegrees = 45;
            try { LineCorrectionGeometry.Calculate(rectangle, true, false, options); throw new Exception("Ambiguous tolerance accepted."); } catch (ArgumentException) { }
            Console.WriteLine("LINE_CORRECTION_GEOMETRY_OK midpoint length reversed horizontal vertical tolerance closedSharedVertices zToggle arcs parallel zeroLength sourceUnchanged");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
