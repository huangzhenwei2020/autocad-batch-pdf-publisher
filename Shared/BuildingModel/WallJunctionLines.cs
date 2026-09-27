using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>Plan-view ownership seams at orthogonal wall endpoints.</summary>
    public static class WallJunctionLines
    {
        public static IReadOnlyList<Tuple<PointModel, PointModel>> Resolve(
            BuildingModelDocument model, IEnumerable<WallModel> walls, double? cutElevation = null)
        {
            var result = new List<Tuple<PointModel, PointModel>>();
            var ends = walls.Where(w => w != null &&
                    (Math.Abs(w.Y2 - w.Y1) < 0.001d) != (Math.Abs(w.X2 - w.X1) < 0.001d))
                .SelectMany(w => new[] {
                    Tuple.Create(w, new PointModel(w.X1, w.Y1), new PointModel(w.X2, w.Y2)),
                    Tuple.Create(w, new PointModel(w.X2, w.Y2), new PointModel(w.X1, w.Y1)) })
                .ToArray();
            foreach (var group in ends.GroupBy(e => Tuple.Create(e.Item1.StoreyId,
                Math.Round(e.Item2.X, 1), Math.Round(e.Item2.Y, 1))))
            {
                var nodes = group.ToArray();
                if (nodes.Length < 2 || nodes.Length > 3) continue;
                if (cutElevation.HasValue)
                {
                    if (nodes.Any(e => cutElevation.Value < model.BaseElevationOf(e.Item1) + 0.5d
                        || cutElevation.Value > model.BaseElevationOf(e.Item1)
                            + model.HeightOf(e.Item1) - 0.5d)) continue;
                }
                else if (nodes.Any(e => Math.Abs(model.BaseElevationOf(e.Item1)
                    - model.BaseElevationOf(nodes[0].Item1)) > 0.5d
                    || Math.Abs(model.HeightOf(e.Item1) - model.HeightOf(nodes[0].Item1)) > 0.5d)) continue;
                var h = nodes.Where(e => Math.Abs(e.Item1.Y2 - e.Item1.Y1) < 0.001d).ToArray();
                var v = nodes.Where(e => Math.Abs(e.Item1.X2 - e.Item1.X1) < 0.001d).ToArray();
                var x = nodes[0].Item2.X; var y = nodes[0].Item2.Y;
                if (h.Length == 1 && v.Length == 1)
                {
                    var hx = Math.Sign(h[0].Item3.X - x);
                    var vy = Math.Sign(v[0].Item3.Y - y);
                    var xBounds = BodyBounds(v[0].Item1, x);
                    var yBounds = BodyBounds(h[0].Item1, y);
                    result.Add(Tuple.Create(
                        new PointModel(hx > 0 ? xBounds.Item2 : xBounds.Item1,
                            vy > 0 ? yBounds.Item2 : yBounds.Item1),
                        new PointModel(hx > 0 ? xBounds.Item1 : xBounds.Item2,
                            vy > 0 ? yBounds.Item1 : yBounds.Item2)));
                }
                else if (h.Length == 2 && v.Length == 1
                    && Math.Sign(h[0].Item3.X - x) != Math.Sign(h[1].Item3.X - x))
                {
                    var vy = Math.Sign(v[0].Item3.Y - y);
                    var xBounds = BodyBounds(v[0].Item1, x);
                    var yBounds = BodyBounds(h[0].Item1, y);
                    var farY = vy > 0 ? yBounds.Item2 : yBounds.Item1;
                    var nearY = vy > 0 ? yBounds.Item1 : yBounds.Item2;
                    var tip = new PointModel(x, nearY);
                    result.Add(Tuple.Create(new PointModel(xBounds.Item1, farY), tip));
                    result.Add(Tuple.Create(tip, new PointModel(xBounds.Item2, farY)));
                }
                else if (v.Length == 2 && h.Length == 1
                    && Math.Sign(v[0].Item3.Y - y) != Math.Sign(v[1].Item3.Y - y))
                {
                    var hx = Math.Sign(h[0].Item3.X - x);
                    var xBounds = BodyBounds(v[0].Item1, x);
                    var yBounds = BodyBounds(h[0].Item1, y);
                    var farX = hx > 0 ? xBounds.Item2 : xBounds.Item1;
                    var nearX = hx > 0 ? xBounds.Item1 : xBounds.Item2;
                    var tip = new PointModel(nearX, y);
                    result.Add(Tuple.Create(new PointModel(farX, yBounds.Item1), tip));
                    result.Add(Tuple.Create(tip, new PointModel(farX, yBounds.Item2)));
                }
            }
            return result;
        }

        private static Tuple<double, double> BodyBounds(WallModel wall, double axis)
        {
            var half = wall.Thickness / 2d;
            var offset = WallReferenceGeometry.BodyOffset(wall);
            var direction = Math.Abs(wall.X2 - wall.X1) < 0.001d
                ? -Math.Sign(wall.Y2 - wall.Y1) : Math.Sign(wall.X2 - wall.X1);
            var center = axis + direction * offset;
            return Tuple.Create(center - half, center + half);
        }
    }
}
