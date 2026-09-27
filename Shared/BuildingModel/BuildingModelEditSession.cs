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

        /// <summary>Moves one wall grip and all wall endpoints joined to its previous position.</summary>
        public bool TryMoveWallGrip(string id, int endpointIndex, double x, double y, out string error)
        {
            error = null;
            if (endpointIndex != 0 && endpointIndex != 1)
            { error = "墙端点序号只能是 0 或 1。"; return false; }
            if (!Finite(x) || !Finite(y))
            { error = "墙端点必须是有限坐标。"; return false; }
            var candidate = Clone(Model);
            var wall = candidate.Walls.FirstOrDefault(w => w != null && Same(w.Id, id));
            if (wall == null) { error = "未找到墙：" + id; return false; }
            var oldX = endpointIndex == 0 ? wall.X1 : wall.X2;
            var oldY = endpointIndex == 0 ? wall.Y1 : wall.Y2;
            const double joinTolerance = 0.5d;
            var changed = candidate.Walls.Where(w => w != null && Same(w.StoreyId, wall.StoreyId)
                && (Same(w.Id, id)
                    || EndpointNear(w.X1, w.Y1, oldX, oldY, joinTolerance)
                    || EndpointNear(w.X2, w.Y2, oldX, oldY, joinTolerance))).ToList();
            foreach (var current in changed)
            {
                if (Same(current.Id, id))
                {
                    if (endpointIndex == 0) { current.X1 = x; current.Y1 = y; }
                    else { current.X2 = x; current.Y2 = y; }
                    continue;
                }
                if (EndpointNear(current.X1, current.Y1, oldX, oldY, joinTolerance))
                { current.X1 = x; current.Y1 = y; }
                if (EndpointNear(current.X2, current.Y2, oldX, oldY, joinTolerance))
                { current.X2 = x; current.Y2 = y; }
            }
            // An endpoint on a moving wall's interior keeps its fractional position on that wall.
            // Collect first so a branch attached to two moving walls cannot be silently pulled apart.
            var assignments = new System.Collections.Generic.Dictionary<string, PointModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var host in changed.ToArray())
            {
                var previous = Model.Walls.First(w => w != null && Same(w.Id, host.Id));
                foreach (var other in Model.Walls.Where(w => w != null && Same(w.StoreyId, host.StoreyId)
                    && !Same(w.Id, host.Id)))
                {
                    for (var index = 0; index < 2; index++)
                    {
                        var ox = index == 0 ? other.X1 : other.X2;
                        var oy = index == 0 ? other.Y1 : other.Y2;
                        if (!PlanEditing.TryProjectWallInterior(previous, ox, oy,
                            joinTolerance, out var fraction)) continue;
                        var branch = candidate.Walls.First(w => w != null && Same(w.Id, other.Id));
                        var currentX = index == 0 ? branch.X1 : branch.X2;
                        var currentY = index == 0 ? branch.Y1 : branch.Y2;
                        if (!EndpointNear(currentX, currentY, ox, oy, joinTolerance)) continue;
                        var destination = new PointModel(host.X1 + (host.X2 - host.X1) * fraction,
                            host.Y1 + (host.Y2 - host.Y1) * fraction);
                        var key = other.Id + "|" + index;
                        if (assignments.TryGetValue(key, out var existing)
                            && !EndpointNear(existing.X, existing.Y, destination.X, destination.Y, joinTolerance))
                        { error = "T 形交接点同时依附多道移动墙，无法确定新位置。"; return false; }
                        assignments[key] = destination;
                    }
                }
            }
            foreach (var assignment in assignments)
            {
                var separator = assignment.Key.LastIndexOf('|');
                var branchId = assignment.Key.Substring(0, separator);
                var index = assignment.Key[separator + 1] - '0';
                var branch = candidate.Walls.First(w => w != null && Same(w.Id, branchId));
                if (index == 0) { branch.X1 = assignment.Value.X; branch.Y1 = assignment.Value.Y; }
                else { branch.X2 = assignment.Value.X; branch.Y2 = assignment.Value.Y; }
                if (!changed.Any(w => Same(w.Id, branch.Id))) changed.Add(branch);
            }
            foreach (var current in changed)
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
