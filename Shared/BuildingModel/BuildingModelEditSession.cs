using System;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// UI-independent editing boundary for the first cross-platform wall/opening probe.
    /// Changes are validated on a clone and committed only when the whole model is valid.
    /// </summary>
    public sealed class BuildingModelEditSession
    {
        private readonly ModelEditHistory _history = new ModelEditHistory();

        public BuildingModelDocument Model { get; private set; }
        public long Revision { get; private set; }
        public bool CanUndo { get { return _history.CanUndo; } }
        public bool CanRedo { get { return _history.CanRedo; } }

        public bool TryAddWall(WallModel source, out string id, out string error)
        {
            id = null;
            error = null;
            if (source == null) { error = "墙为空。"; return false; }
            if (Model.FindStorey(source.StoreyId) == null) { error = "墙所属楼层不存在。"; return false; }
            if (!Finite(source.X1) || !Finite(source.Y1) || !Finite(source.X2) || !Finite(source.Y2)
                || !Finite(source.Thickness) || !Finite(source.Height) || source.Height < 0d)
            { error = "墙的坐标、厚度和高度必须是有限且有效的数值。"; return false; }
            error = PlanEditing.ValidateWall(source);
            if (error != null) return false;
            var candidate = Clone(Model);
            var wall = new WallModel
            {
                Id = "W-" + Guid.NewGuid().ToString("N"), StoreyId = source.StoreyId,
                X1 = source.X1, Y1 = source.Y1, X2 = source.X2, Y2 = source.Y2,
                Thickness = source.Thickness, Height = source.Height, Material = source.Material
            };
            candidate.Walls.Add(wall);
            Commit(candidate);
            id = wall.Id;
            return true;
        }

        public bool TryAddOpening(OpeningModel source, out string id, out string error)
        {
            id = null;
            error = null;
            if (source == null) { error = "洞口为空。"; return false; }
            var wall = Model.Walls.FirstOrDefault(x => x != null && Same(x.Id, source.HostWallId));
            if (wall == null) { error = "门窗的宿主墙不存在。"; return false; }
            var candidate = Clone(Model);
            var opening = new OpeningModel
            {
                Id = "O-" + Guid.NewGuid().ToString("N"), HostWallId = wall.Id,
                Kind = source.Kind, Code = source.Code, Offset = source.Offset,
                Width = source.Width, Height = source.Height, Sill = source.Sill
            };
            candidate.Openings.Add(opening);
            error = ValidateOpeningGeometry(candidate,
                candidate.Walls.First(x => Same(x.Id, wall.Id)), opening);
            if (error != null) return false;
            Commit(candidate);
            id = opening.Id;
            return true;
        }

        public bool TryDeleteElement(string id, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(id)) { error = "尚未选择构件。"; return false; }
            var candidate = Clone(Model);
            var wall = candidate.Walls.FirstOrDefault(x => x != null && Same(x.Id, id));
            if (wall != null)
            {
                candidate.Openings.RemoveAll(x => x != null && Same(x.HostWallId, wall.Id));
                candidate.Walls.Remove(wall);
            }
            else
            {
                var opening = candidate.Openings.FirstOrDefault(x => x != null && Same(x.Id, id));
                if (opening == null) { error = "当前只能删除墙或门窗；未找到构件：" + id; return false; }
                candidate.Openings.Remove(opening);
            }
            Commit(candidate);
            return true;
        }

        public BuildingModelEditSession(BuildingModelDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Model = Clone(source);
            _history.Reset(Model);
        }

        public bool TrySetWallLength(string id, double length, out string error)
        {
            var source = Model.Walls.FirstOrDefault(x => x != null && Same(x.Id, id));
            if (source == null) { error = "未找到墙：" + id; return false; }
            return TrySetWallGeometry(id, length, source.Thickness, source.Height, out error);
        }

        public bool TrySetWallEndpoints(string id, double x1, double y1, double x2, double y2,
            out string error)
        {
            error = null;
            if (!Finite(x1) || !Finite(y1) || !Finite(x2) || !Finite(y2))
            { error = "墙端点必须是有限坐标。"; return false; }
            var candidate = Clone(Model);
            var wall = candidate.Walls.FirstOrDefault(x => x != null && Same(x.Id, id));
            if (wall == null) { error = "未找到墙：" + id; return false; }
            wall.X1 = x1; wall.Y1 = y1; wall.X2 = x2; wall.Y2 = y2;
            error = ValidateWallAndOpenings(candidate, wall);
            if (error != null) return false;
            Commit(candidate);
            return true;
        }

        /// <summary>Moves one wall grip through coincident endpoints and T-junction dependencies.</summary>
        public bool TryMoveWallGrip(string id, int endpointIndex, double x, double y, out string error)
        {
            error = null;
            if (endpointIndex != 0 && endpointIndex != 1)
            { error = "墙端点序号只能是 0 或 1。"; return false; }
            if (!Finite(x) || !Finite(y))
            { error = "墙端点必须是有限坐标。"; return false; }
            var source = Model.Walls.FirstOrDefault(w => w != null && Same(w.Id, id));
            if (source == null) { error = "未找到墙：" + id; return false; }
            if (x == (endpointIndex == 0 ? source.X1 : source.X2)
                && y == (endpointIndex == 0 ? source.Y1 : source.Y2)) return true;
            var candidate = Clone(Model);
            var wall = candidate.Walls.First(w => w != null && Same(w.Id, id));
            const double joinTolerance = 0.5d;
            var originals = Model.Walls.Where(w => w != null && Same(w.StoreyId, wall.StoreyId)).ToArray();
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (originals.Any(w => string.IsNullOrWhiteSpace(w.Id) || !ids.Add(w.Id)))
            { error = "当前楼层存在空白或重复的墙 ID，不能联动编辑。"; return false; }
            var originalById = originals.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
            var candidateById = candidate.Walls.Where(w => w != null && Same(w.StoreyId, wall.StoreyId))
                .ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
            var pending = new System.Collections.Generic.Queue<(WallModel wall, int index, PointModel position)>();
            var assigned = new System.Collections.Generic.Dictionary<string, PointModel>(StringComparer.OrdinalIgnoreCase);
            var changed = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirtyHosts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Enqueue((originalById[id], endpointIndex,
                new PointModel(x, y)));
            while (pending.Count > 0 || dirtyHosts.Count > 0)
            {
                while (pending.Count > 0)
                {
                    var move = pending.Dequeue();
                    var key = move.wall.Id + "|" + move.index;
                    if (assigned.TryGetValue(key, out var previousTarget))
                    {
                        if (!EndpointNear(previousTarget.X, previousTarget.Y,
                            move.position.X, move.position.Y, joinTolerance))
                        { error = "墙交接约束冲突，无法确定端点新位置。"; return false; }
                        continue;
                    }
                    if (!Finite(move.position.X) || !Finite(move.position.Y))
                    { error = "联动后的墙端点超出有效坐标范围。"; return false; }
                    assigned.Add(key, move.position);
                    var target = candidateById[move.wall.Id];
                    if (move.index == 0) { target.X1 = move.position.X; target.Y1 = move.position.Y; }
                    else { target.X2 = move.position.X; target.Y2 = move.position.Y; }
                    changed.Add(target.Id);
                    dirtyHosts.Add(target.Id);
                    var oldX = move.index == 0 ? move.wall.X1 : move.wall.X2;
                    var oldY = move.index == 0 ? move.wall.Y1 : move.wall.Y2;
                    foreach (var other in originals)
                    {
                        if (!Same(other.Id, move.wall.Id)
                            && EndpointNear(other.X1, other.Y1, oldX, oldY, joinTolerance))
                            pending.Enqueue((other, 0, move.position));
                        if (!Same(other.Id, move.wall.Id)
                            && EndpointNear(other.X2, other.Y2, oldX, oldY, joinTolerance))
                            pending.Enqueue((other, 1, move.position));
                    }
                }
                var hosts = dirtyHosts.ToArray();
                dirtyHosts.Clear();
                foreach (var hostId in hosts)
                {
                    var previous = originalById[hostId];
                    var host = candidateById[hostId];
                    foreach (var other in originals)
                    {
                        if (Same(other.Id, hostId)) continue;
                        for (var index = 0; index < 2; index++)
                        {
                            var ox = index == 0 ? other.X1 : other.X2;
                            var oy = index == 0 ? other.Y1 : other.Y2;
                            if (!PlanEditing.TryProjectWallInterior(previous, ox, oy,
                                joinTolerance, out var fraction)) continue;
                            pending.Enqueue((other, index, new PointModel(
                                host.X1 + (host.X2 - host.X1) * fraction,
                                host.Y1 + (host.Y2 - host.Y1) * fraction)));
                        }
                    }
                }
            }
            foreach (var current in changed.Select(wallId => candidateById[wallId]))
            {
                error = ValidateWallAndOpenings(candidate, current);
                if (error != null) return false;
            }
            Commit(candidate);
            return true;
        }

        /// <summary>Moves or rotates a whole wall about its midpoint. Copies keep their hosted openings.</summary>
        public bool TryTransformWall(string id, double deltaX, double deltaY, double angleDegrees,
            bool copy, out string affectedId, out string error)
        {
            affectedId = null;
            error = null;
            if (!Finite(deltaX) || !Finite(deltaY) || !Finite(angleDegrees))
            { error = "墙体位移和旋转角度必须是有限数值。"; return false; }
            if (deltaX == 0d && deltaY == 0d && angleDegrees == 0d)
            { error = "请输入位移或旋转角度。"; return false; }
            var candidate = Clone(Model);
            var source = candidate.Walls.FirstOrDefault(x => x != null && Same(x.Id, id));
            if (source == null) { error = "未找到墙：" + id; return false; }
            var angle = angleDegrees * Math.PI / 180d;
            if (!Finite(angle)) { error = "旋转角度超出有效范围。"; return false; }
            var cosine = Math.Cos(angle);
            var sine = Math.Sin(angle);
            var midX = source.X1 / 2d + source.X2 / 2d;
            var midY = source.Y1 / 2d + source.Y2 / 2d;
            var halfX = (source.X2 - source.X1) / 2d;
            var halfY = (source.Y2 - source.Y1) / 2d;
            var rotatedX = halfX * cosine - halfY * sine;
            var rotatedY = halfX * sine + halfY * cosine;
            var centerX = midX + deltaX;
            var centerY = midY + deltaY;
            var x1 = centerX - rotatedX;
            var y1 = centerY - rotatedY;
            var x2 = centerX + rotatedX;
            var y2 = centerY + rotatedY;
            if (!Finite(x1) || !Finite(y1) || !Finite(x2) || !Finite(y2))
            { error = "变换后的墙端点超出有效范围。"; return false; }
            WallModel target;
            if (copy)
            {
                target = new WallModel
                {
                    Id = "W-" + Guid.NewGuid().ToString("N"), StoreyId = source.StoreyId,
                    Thickness = source.Thickness, Height = source.Height, Material = source.Material
                };
                candidate.Walls.Add(target);
                foreach (var opening in candidate.Openings.Where(x => x != null && Same(x.HostWallId, id)).ToArray())
                {
                    candidate.Openings.Add(new OpeningModel
                    {
                        Id = "O-" + Guid.NewGuid().ToString("N"), HostWallId = target.Id,
                        Kind = opening.Kind, Code = opening.Code, Offset = opening.Offset,
                        Width = opening.Width, Height = opening.Height, Sill = opening.Sill
                    });
                }
            }
            else target = source;
            target.X1 = x1; target.Y1 = y1; target.X2 = x2; target.Y2 = y2;
            error = ValidateWallAndOpenings(candidate, target);
            if (error != null) return false;
            Commit(candidate);
            affectedId = target.Id;
            return true;
        }

        public bool TrySetWallGeometry(string id, double length, double thickness, double height, out string error)
        {
            error = null;
            if (!Finite(length) || length < 10d)
            {
                error = "墙长必须是至少 10 mm 的有限数值。";
                return false;
            }
            if (!Finite(thickness) || thickness <= 0d)
            { error = "墙厚必须是大于 0 mm 的有限数值。"; return false; }
            if (!Finite(height) || (height != 0d && height < 10d))
            { error = "墙高必须是 0（随楼层）或至少 10 mm 的有限数值。"; return false; }
            var candidate = Clone(Model);
            var wall = candidate.Walls.FirstOrDefault(x => x != null && Same(x.Id, id));
            if (wall == null) { error = "未找到墙：" + id; return false; }
            var dx = wall.X2 - wall.X1;
            var dy = wall.Y2 - wall.Y1;
            var previousLength = Math.Sqrt(dx * dx + dy * dy);
            if (previousLength < 1e-9d) { error = "墙轴线长度为零。"; return false; }
            wall.X2 = wall.X1 + dx / previousLength * length;
            wall.Y2 = wall.Y1 + dy / previousLength * length;
            wall.Thickness = thickness;
            wall.Height = height;
            error = ValidateWallAndOpenings(candidate, wall);
            if (error != null) return false;
            Commit(candidate);
            return true;
        }

        public bool TrySetOpeningOffset(string id, double offset, out string error)
        {
            var source = Model.Openings.FirstOrDefault(x => x != null && Same(x.Id, id));
            if (source == null) { error = "未找到洞口：" + id; return false; }
            return TrySetOpeningGeometry(id, offset, source.Width, source.Height, source.Sill, out error);
        }

        public bool TrySetOpeningGeometry(string id, double offset, double width, double height,
            double sill, out string error)
        {
            error = null;
            if (!Finite(offset) || !Finite(width) || !Finite(height) || !Finite(sill))
            { error = "洞口参数必须是有限数值。"; return false; }
            if (width <= 0d || height <= 0d || sill < 0d)
            { error = "洞口宽高必须大于 0，窗台高不能为负。"; return false; }
            var candidate = Clone(Model);
            var opening = candidate.Openings.FirstOrDefault(x => x != null && Same(x.Id, id));
            if (opening == null) { error = "未找到洞口：" + id; return false; }
            var wall = candidate.Walls.FirstOrDefault(x => x != null && Same(x.Id, opening.HostWallId));
            if (wall == null) { error = "洞口宿主墙不存在：" + opening.HostWallId; return false; }
            opening.Offset = offset;
            opening.Width = width;
            opening.Height = height;
            opening.Sill = sill;
            error = ValidateOpeningGeometry(candidate, wall, opening);
            if (error != null) return false;
            Commit(candidate);
            return true;
        }

        public bool Undo()
        {
            if (!CanUndo) return false;
            Model = _history.Undo(Model);
            Revision++;
            return true;
        }

        public bool Redo()
        {
            if (!CanRedo) return false;
            Model = _history.Redo(Model);
            Revision++;
            return true;
        }

        private void Commit(BuildingModelDocument candidate)
        {
            if (string.Equals(BuildingModelJson.ToJson(candidate), BuildingModelJson.ToJson(Model), StringComparison.Ordinal))
                return;
            Model = candidate;
            _history.Push(Model);
            Revision++;
        }

        private static string ValidateWallAndOpenings(BuildingModelDocument candidate, WallModel wall)
        {
            var error = PlanEditing.ValidateWall(wall);
            if (error != null) return error;
            foreach (var opening in candidate.Openings.Where(x => x != null && Same(x.HostWallId, wall.Id)))
            {
                error = ValidateOpeningGeometry(candidate, wall, opening);
                if (error != null) return error;
            }
            return null;
        }

        private static string ValidateOpeningGeometry(BuildingModelDocument model, WallModel wall, OpeningModel opening)
        {
            var error = PlanEditing.ValidateOpening(model, wall, opening);
            if (error != null) return error;
            var storey = model.FindStorey(wall.StoreyId);
            var wallHeight = wall.Height > 0d ? wall.Height : (storey == null ? 0d : storey.Height);
            if (!Finite(wallHeight) || wallHeight <= 0d)
                return "宿主墙没有有效高度或楼层。";
            if (opening.Sill + opening.Height > wallHeight + 0.5d)
                return "洞口顶部超出宿主墙高度（" + Math.Round(wallHeight) + " mm）。";
            return null;
        }

        private static BuildingModelDocument Clone(BuildingModelDocument source)
        {
            return BuildingModelJson.FromJson(BuildingModelJson.ToJson(source));
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool EndpointNear(double x, double y, double otherX, double otherY, double tolerance)
        {
            return Math.Abs(x - otherX) <= tolerance && Math.Abs(y - otherY) <= tolerance
                && Math.Sqrt(Math.Pow(x - otherX, 2) + Math.Pow(y - otherY, 2)) <= tolerance;
        }
        private static bool Same(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
