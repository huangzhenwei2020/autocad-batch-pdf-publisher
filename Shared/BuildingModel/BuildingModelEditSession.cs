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
        private static bool Same(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
