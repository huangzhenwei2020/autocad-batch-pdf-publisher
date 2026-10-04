using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// One building-wide orthogonal axis catalogue. Walls on different storeys at the same
    /// coordinate resolve to the same axis ID and label; explicit axes take precedence.
    /// The returned objects are copies, so view generation never renumbers the saved model.
    /// </summary>
    public static class BuildingAxisLayout
    {
        private const double Tolerance = 0.5d;

        public sealed class Placement
        {
            public AxisModel Axis { get; set; }
            public PointModel Start { get; set; }
            public PointModel End { get; set; }
            public PointModel StartLabel { get; set; }
            public PointModel EndLabel { get; set; }
        }

        // Keep axis coordinates exact. Resolve crowded bubbles by extending outwards,
        // never by moving an axis or placing its label inside the building.
        public static List<Placement> Layout(BuildingModelDocument model, string storeyId,
            double margin, double radius, bool includeGhosts = false)
        {
            var axes=Resolve(model,storeyId);
            var walls=model.Walls.Where(w=>storeyId==null||w.StoreyId==storeyId).ToList();
            var xs=walls.SelectMany(w=>new[] {Math.Min(w.X1,w.X2)-w.Thickness/2,Math.Max(w.X1,w.X2)+w.Thickness/2})
                .Concat(axes.Where(a=>a.Vertical).Select(a=>a.Position)).ToList();
            var ys=walls.SelectMany(w=>new[] {Math.Min(w.Y1,w.Y2)-w.Thickness/2,Math.Max(w.Y1,w.Y2)+w.Thickness/2})
                .Concat(axes.Where(a=>!a.Vertical).Select(a=>a.Position)).ToList();
            var structure=model.Slabs.Where(s=>storeyId==null||s.StoreyId==storeyId).SelectMany(s=>s.Outline??new List<PointModel>())
                .Concat(model.Columns.Where(c=>storeyId==null||c.StoreyId==storeyId).SelectMany(StructuralGeometry.ColumnOutline))
                .Concat(model.Beams.Where(b=>storeyId==null||b.StoreyId==storeyId).SelectMany(StructuralGeometry.BeamOutline))
                .Concat(model.Roofs.Where(r=>storeyId==null||r.StoreyId==storeyId).SelectMany(r=>new[] {new PointModel(r.X,r.Y),new PointModel(r.X+r.Width,r.Y+r.Depth)}));
            foreach(var p in structure){xs.Add(p.X);ys.Add(p.Y);}
            foreach(var wall in walls)foreach(var p in new[] {WallReferenceGeometry.BodyPoint(wall,wall.X1,wall.Y1),WallReferenceGeometry.BodyPoint(wall,wall.X2,wall.Y2)}) {
                xs.Add(p.X-wall.Thickness/2);xs.Add(p.X+wall.Thickness/2);
                ys.Add(p.Y-wall.Thickness/2);ys.Add(p.Y+wall.Thickness/2);
            }
            var minX=xs.Count==0?-1500:xs.Min();var maxX=xs.Count==0?1500:xs.Max();
            var minY=ys.Count==0?-1500:ys.Min();var maxY=ys.Count==0?1500:ys.Max();
            var result=new List<Placement>();
            foreach(var vertical in new[] {true,false}) {
                var group=axes.Where(a=>a.Vertical==vertical).OrderBy(a=>a.Position).ToList();
                var lo=(vertical?minY:minX)-Math.Max(margin,radius*3);
                var hi=(vertical?maxY:maxX)+Math.Max(margin,radius*3);
                foreach(var axis in group.Where(a=>(includeGhosts||(!a.Hidden&&!a.Deleted))&&(a.ExtentStart!=0||a.ExtentEnd!=0))) {
                    lo=Math.Min(lo,Math.Min(axis.ExtentStart,axis.ExtentEnd));
                    hi=Math.Max(hi,Math.Max(axis.ExtentStart,axis.ExtentEnd));
                }
                var starts=new List<PointModel>();var ends=new List<PointModel>();var separation=radius*2.02;
                foreach(var axis in group) {
                    var startVisible=includeGhosts||(!axis.Hidden&&!axis.Deleted&&!axis.StartHidden&&!axis.StartRemoved);
                    var endVisible=includeGhosts||(!axis.Hidden&&!axis.Deleted&&!axis.EndHidden&&!axis.EndRemoved);
                    var startOffset=startVisible?MinimumBubbleOffset(starts,axis.Position,separation):0;
                    var endOffset=endVisible?MinimumBubbleOffset(ends,axis.Position,separation):0;
                    if(startVisible)starts.Add(new PointModel(axis.Position,startOffset));
                    if(endVisible)ends.Add(new PointModel(axis.Position,endOffset));
                    var start=lo-startOffset;var end=hi+endOffset;
                    var a=vertical?new PointModel(axis.Position,start):new PointModel(start,axis.Position);
                    var b=vertical?new PointModel(axis.Position,end):new PointModel(end,axis.Position);
                    var shortened=Extents(model,axis,storeyId,margin);
                    result.Add(new Placement {Axis=axis,StartLabel=a,EndLabel=b,
                        Start=axis.StartRemoved?(vertical?new PointModel(axis.Position,shortened[0]):new PointModel(shortened[0],axis.Position)):a,
                        End=axis.EndRemoved?(vertical?new PointModel(axis.Position,shortened[1]):new PointModel(shortened[1],axis.Position)):b});
                }
            }
            return result;
        }

        private static double MinimumBubbleOffset(List<PointModel> occupied, double position, double separation)
        {
            // Circle distance defines forbidden intervals, not fixed staggered rows.
            var intervals=occupied.Where(p=>Math.Abs(p.X-position)<separation).Select(p=> {
                var distance=Math.Sqrt(separation*separation-Math.Pow(p.X-position,2));
                return new PointModel(p.Y-distance,p.Y+distance);
            }).OrderBy(p=>p.X);
            var offset=0d;
            foreach(var interval in intervals) {
                if(interval.X>offset)break;
                if(interval.Y>offset)offset=interval.Y;
            }
            return offset;
        }

        public static List<AxisModel> Resolve(BuildingModelDocument model, string storeyId = null)
        {
            var result = new List<AxisModel>();
            if (model == null) return result;
            List<AxisModel> independent = null;
            var isolated = storeyId != null && model.StoreyAxes != null && model.StoreyAxes.TryGetValue(storeyId, out independent);
            foreach (var axis in ((isolated ? independent : model.Axes) ?? new List<AxisModel>())
                .Where(a => a != null && Finite(a.Position))
                .OrderBy(a => a.Vertical ? 0 : 1).ThenBy(a => a.Position))
            {
                var existing = Find(result, axis.Vertical, axis.Position);
                if (existing != null)
                {
                    if (string.IsNullOrWhiteSpace(existing.Name)) existing.Name = axis.Name;
                    if (string.IsNullOrWhiteSpace(existing.StartName)) existing.StartName = axis.StartName;
                    if (string.IsNullOrWhiteSpace(existing.EndName)) existing.EndName = axis.EndName;
                    continue;
                }
                result.Add(new AxisModel
                {
                    Id = string.IsNullOrWhiteSpace(axis.Id) ? AutoId(axis.Vertical, axis.Position) : axis.Id,
                    Name = axis.Name, StartName = axis.StartName, EndName = axis.EndName,
                    AutomaticNumber = axis.AutomaticNumber, Hidden = axis.Hidden, Deleted = axis.Deleted, StartHidden = axis.StartHidden, EndHidden = axis.EndHidden, StartRemoved = axis.StartRemoved, EndRemoved = axis.EndRemoved,
                    Vertical = axis.Vertical, Position = axis.Position,
                    ExtentStart = axis.ExtentStart, ExtentEnd = axis.ExtentEnd
                });
            }
            foreach (var wall in (model.Walls ?? new List<WallModel>())
                .Where(w => w != null && !isolated).OrderBy(w => w.X1).ThenBy(w => w.Y1).ThenBy(w => w.Id))
            {
                var vertical = Math.Abs(wall.X2 - wall.X1) <= Tolerance;
                var horizontal = Math.Abs(wall.Y2 - wall.Y1) <= Tolerance;
                if (vertical == horizontal) continue;
                var position = vertical ? (wall.X1 + wall.X2) / 2d : (wall.Y1 + wall.Y2) / 2d;
                if (!Finite(position) || Find(result, vertical, position) != null) continue;
                result.Add(new AxisModel
                {
                    Id = AutoId(vertical, position), Vertical = vertical, Position = position
                });
            }
            foreach (var vertical in new[] { true, false })
            {
                var ordered = result.Where(a => !a.Deleted && a.Vertical == vertical).OrderBy(a => a.Position).ToList();
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var axis in ordered)
                {
                    if(axis.AutomaticNumber == true) axis.Name=null;
                    if (string.IsNullOrWhiteSpace(axis.Name)) continue;
                    axis.Name = axis.Name.Trim();
                    if (!used.Add(axis.Name)) axis.Name = null;
                }
                for (var index = 0; index < ordered.Count; index++)
                {
                    var axis = ordered[index];
                    if (!string.IsNullOrWhiteSpace(axis.Name)) continue;
                    var candidate = vertical ? (index + 1).ToString() : LetterName(index);
                    if (used.Contains(candidate))
                    {
                        var next = ordered.Count + 1;
                        do { candidate = vertical ? next.ToString() : LetterName(next - 1); next++; }
                        while (used.Contains(candidate));
                    }
                    axis.Name = candidate;
                    used.Add(candidate);
                }
            }
            return result;
        }

        public static double[] Extents(BuildingModelDocument model, AxisModel axis, string storeyId, double margin, double stub = 500d)
        {
            var walls = model.Walls.Where(w => storeyId == null || w.StoreyId == storeyId).ToList();
            var crossing = walls.Where(w => axis.Position >= Math.Min(axis.Vertical ? w.X1 : w.Y1, axis.Vertical ? w.X2 : w.Y2)-w.Thickness/2-.5
                && axis.Position <= Math.Max(axis.Vertical ? w.X1 : w.Y1, axis.Vertical ? w.X2 : w.Y2)+w.Thickness/2+.5).ToList();
            var local = crossing.Count > 0 ? crossing : walls;
            var values = local.SelectMany(w => axis.Vertical ? new[] {w.Y1-w.Thickness/2,w.Y2+w.Thickness/2}
                : new[] {w.X1-w.Thickness/2,w.X2+w.Thickness/2}).ToList();
            var lo = values.Count > 0 ? values.Min() : -1500d;
            var hi = values.Count > 0 ? values.Max() : 1500d;
            var automatic = axis.ExtentStart == 0 && axis.ExtentEnd == 0;
            var start = automatic ? lo-margin : Math.Min(axis.ExtentStart,axis.ExtentEnd);
            var end = automatic ? hi+margin : Math.Max(axis.ExtentStart,axis.ExtentEnd);
            if(axis.StartRemoved) start = lo-stub;
            if(axis.EndRemoved) end = hi+stub;
            return new[] {start,end};
        }

        private static AxisModel Find(List<AxisModel> axes, bool vertical, double position)
        {
            return axes.FirstOrDefault(a => a.Vertical == vertical
                && Math.Abs(a.Position - position) <= Tolerance);
        }

        private static string AutoId(bool vertical, double position)
        {
            return "AX-AUTO-" + (vertical ? "V-" : "H-")
                + Math.Round(position * 2d, MidpointRounding.AwayFromZero).ToString("0",
                    System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string LetterName(int index)
        {
            var name = string.Empty;
            var value = Math.Max(0, index);
            do
            {
                name = (char)('A' + value % 26) + name;
                value = value / 26 - 1;
            } while (value >= 0);
            return name;
        }
    }
}
