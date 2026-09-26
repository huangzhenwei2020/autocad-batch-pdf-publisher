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

        public BuildingModelEditSession(BuildingModelDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Model = Clone(source);
            _history.Reset(Model);
        }

        public bool TrySetWallLength(string id, double length, out string error)
        {
            error = null;
            if (!Finite(length) || length < 10d)
            {
                error = "墙长必须是至少 10 mm 的有限数值。";
                return false;
            }
            var candidate = Clone(Model);
            var wall = candidate.Walls.FirstOrDefault(x => x != null && Same(x.Id, id));
            if (wall == null) { error = "未找到墙：" + id; return false; }
            var dx = wall.X2 - wall.X1;
            var dy = wall.Y2 - wall.Y1;
            var previousLength = Math.Sqrt(dx * dx + dy * dy);
            if (previousLength < 1e-9d) { error = "墙轴线长度为零。"; return false; }
            wall.X2 = wall.X1 + dx / previousLength * length;
            wall.Y2 = wall.Y1 + dy / previousLength * length;
            error = ValidateWallAndOpenings(candidate, wall);
            if (error != null) return false;
            Commit(candidate);
            return true;
        }

        public bool TrySetOpeningOffset(string id, double offset, out string error)
        {
            error = null;
            if (!Finite(offset)) { error = "洞口定位必须是有限数值。"; return false; }
            var candidate = Clone(Model);
            var opening = candidate.Openings.FirstOrDefault(x => x != null && Same(x.Id, id));
            if (opening == null) { error = "未找到洞口：" + id; return false; }
            var wall = candidate.Walls.FirstOrDefault(x => x != null && Same(x.Id, opening.HostWallId));
            if (wall == null) { error = "洞口宿主墙不存在：" + opening.HostWallId; return false; }
            opening.Offset = offset;
            error = PlanEditing.ValidateOpening(candidate, wall, opening);
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
                error = PlanEditing.ValidateOpening(candidate, wall, opening);
                if (error != null) return error;
            }
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
