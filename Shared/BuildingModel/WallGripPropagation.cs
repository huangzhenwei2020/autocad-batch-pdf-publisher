using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>A predicted wall endpoint after a grip move, in model millimetres.</summary>
    public sealed class WallEndpointMove
    {
        public string WallId { get; set; }
        public int Index { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>
    /// Read-only junction graph built once at pointer-down. Preview and commit use the same
    /// propagation rules without cloning the model on every pointer move.
    /// </summary>
    public sealed class WallGripPropagation
    {
        private sealed class Endpoint
        {
            public WallModel Wall;
            public int Index;
            public double X => Index == 0 ? Wall.X1 : Wall.X2;
            public double Y => Index == 0 ? Wall.Y1 : Wall.Y2;
            public string Key => Wall.Id + "|" + Index;
        }

        private sealed class Attachment
        {
            public Endpoint Endpoint;
            public double Fraction;
        }

        private const double Tolerance = 0.5d;
        private readonly Dictionary<string, WallModel> _walls;
        private readonly Dictionary<string, List<Endpoint>> _coincident;
        private readonly Dictionary<string, List<Attachment>> _attached;

        private WallGripPropagation(Dictionary<string, WallModel> walls,
            Dictionary<string, List<Endpoint>> coincident,
            Dictionary<string, List<Attachment>> attached)
        {
            _walls = walls;
            _coincident = coincident;
            _attached = attached;
        }

        public static bool TryCreate(BuildingModelDocument model, string storeyId,
            out WallGripPropagation graph, out string error)
        {
            graph = null;
            error = null;
            if (model == null) { error = "模型为空。"; return false; }
            var walls = model.Walls.Where(w => w != null
                && string.Equals(w.StoreyId, storeyId, StringComparison.OrdinalIgnoreCase)).ToArray();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (walls.Any(w => string.IsNullOrWhiteSpace(w.Id) || !ids.Add(w.Id)))
            { error = "当前楼层存在空白或重复的墙 ID，不能联动编辑。"; return false; }
            var byId = walls.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
            var endpoints = walls.SelectMany(w => new[]
            {
                new Endpoint { Wall = w, Index = 0 },
                new Endpoint { Wall = w, Index = 1 }
            }).ToArray();
            var coincident = endpoints.ToDictionary(e => e.Key, e => new List<Endpoint>(),
                StringComparer.OrdinalIgnoreCase);
            var buckets = new Dictionary<(long x, long y), List<Endpoint>>();
            foreach (var endpoint in endpoints)
            {
                if (!Finite(endpoint.X) || !Finite(endpoint.Y)
                    || Math.Abs(endpoint.X) > 1e15d || Math.Abs(endpoint.Y) > 1e15d)
                { error = "墙端点坐标超出交接计算范围。"; return false; }
                var bx = (long)Math.Floor(endpoint.X / Tolerance);
                var by = (long)Math.Floor(endpoint.Y / Tolerance);
                for (var ix = bx - 1; ix <= bx + 1; ix++)
                for (var iy = by - 1; iy <= by + 1; iy++)
                {
                    if (!buckets.TryGetValue((ix, iy), out var neighbors)) continue;
                    foreach (var other in neighbors)
                    {
                        if (other.Wall.Id == endpoint.Wall.Id || !Near(endpoint.X, endpoint.Y, other.X, other.Y)) continue;
                        coincident[endpoint.Key].Add(other);
                        coincident[other.Key].Add(endpoint);
                    }
                }
                if (!buckets.TryGetValue((bx, by), out var bucket))
                    buckets[(bx, by)] = bucket = new List<Endpoint>();
                bucket.Add(endpoint);
            }
            var attached = walls.ToDictionary(w => w.Id, w => new List<Attachment>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var host in walls)
            foreach (var endpoint in endpoints)
            {
                if (host.Id == endpoint.Wall.Id) continue;
                if (endpoint.X < Math.Min(host.X1, host.X2) - Tolerance
                    || endpoint.X > Math.Max(host.X1, host.X2) + Tolerance
                    || endpoint.Y < Math.Min(host.Y1, host.Y2) - Tolerance
                    || endpoint.Y > Math.Max(host.Y1, host.Y2) + Tolerance) continue;
                if (PlanEditing.TryProjectWallInterior(host, endpoint.X, endpoint.Y,
                    Tolerance, out var fraction))
                    attached[host.Id].Add(new Attachment { Endpoint = endpoint, Fraction = fraction });
            }
            graph = new WallGripPropagation(byId, coincident, attached);
            return true;
        }

        public bool TryMove(string wallId, int endpointIndex, double x, double y,
            out List<WallEndpointMove> changes, out string error)
        {
            changes = new List<WallEndpointMove>();
            error = null;
            if (endpointIndex != 0 && endpointIndex != 1)
            { error = "墙端点序号只能是 0 或 1。"; return false; }
            if (!Finite(x) || !Finite(y))
            { error = "墙端点必须是有限坐标。"; return false; }
            if (!_walls.TryGetValue(wallId ?? "", out var selected))
            { error = "未找到墙：" + wallId; return false; }
            var pending = new Queue<(Endpoint endpoint, PointModel target)>();
            var assigned = new Dictionary<string, PointModel>(StringComparer.OrdinalIgnoreCase);
            var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Enqueue((new Endpoint { Wall = selected, Index = endpointIndex }, new PointModel(x, y)));
            while (pending.Count > 0 || dirty.Count > 0)
            {
                while (pending.Count > 0)
                {
                    var move = pending.Dequeue();
                    var key = move.endpoint.Key;
                    if (assigned.TryGetValue(key, out var previous))
                    {
                        if (!Near(previous.X, previous.Y, move.target.X, move.target.Y))
                        { error = "墙交接约束冲突，无法确定端点新位置。"; return false; }
                        continue;
                    }
                    if (!Finite(move.target.X) || !Finite(move.target.Y))
                    { error = "联动后的墙端点超出有效坐标范围。"; return false; }
                    assigned.Add(key, move.target);
                    changes.Add(new WallEndpointMove
                    {
                        WallId = move.endpoint.Wall.Id, Index = move.endpoint.Index,
                        X = move.target.X, Y = move.target.Y
                    });
                    dirty.Add(move.endpoint.Wall.Id);
                    foreach (var neighbor in _coincident[key])
                        pending.Enqueue((neighbor, move.target));
                }
                var hosts = dirty.ToArray();
                dirty.Clear();
                foreach (var hostId in hosts)
                {
                    var host = _walls[hostId];
                    var start = assigned.TryGetValue(hostId + "|0", out var movedStart)
                        ? movedStart : new PointModel(host.X1, host.Y1);
                    var end = assigned.TryGetValue(hostId + "|1", out var movedEnd)
                        ? movedEnd : new PointModel(host.X2, host.Y2);
                    foreach (var attachment in _attached[hostId])
                        pending.Enqueue((attachment.Endpoint, new PointModel(
                            start.X + (end.X - start.X) * attachment.Fraction,
                            start.Y + (end.Y - start.Y) * attachment.Fraction)));
                }
            }
            return true;
        }

        private static bool Near(double x, double y, double otherX, double otherY)
        {
            var dx = x - otherX; var dy = y - otherY;
            return Math.Abs(dx) <= Tolerance && Math.Abs(dy) <= Tolerance
                && Math.Sqrt(dx * dx + dy * dy) <= Tolerance;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
