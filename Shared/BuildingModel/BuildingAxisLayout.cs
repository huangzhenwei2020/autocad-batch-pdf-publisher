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
