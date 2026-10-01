using System;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// UI-independent editing boundary for the first cross-platform wall/opening probe.
    /// Changes are validated on a clone and committed only when the whole model is valid.
    /// </summary>
    public sealed partial class BuildingModelEditSession
    {
        private readonly ModelEditHistory _history = new ModelEditHistory();

        public BuildingModelDocument Model { get; private set; }
        public long Revision { get; private set; }
        public bool CanUndo { get { return _history.CanUndo; } }
        public bool CanRedo { get { return _history.CanRedo; } }

        /// <summary>Replace building-wide explicit axes as one undoable edit. Derived wall axes stay automatic.</summary>
        public bool TryReplaceAxes(System.Collections.Generic.IEnumerable<AxisModel> axes, out string error)
        {
            error = null;
            if (axes == null) { error = "轴网为空。"; return false; }
            var replacement = axes.ToList();
            if (replacement.Any(a => a == null || string.IsNullOrWhiteSpace(a.Id)
                || !Finite(a.Position) || !Finite(a.ExtentStart) || !Finite(a.ExtentEnd)))
            { error = "轴线 ID、位置和范围必须有效。"; return false; }
            if (replacement.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            { error = "轴线 ID 重复。"; return false; }
            if (replacement.SelectMany(a => new[] { a.Name, a.StartName, a.EndName })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Any(s => s.Length > 24 || s.Any(c => !((c >= 'A' && c <= 'Z')
                    || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '/' || c == '\''))))
            { error = "轴号最多 24 个字符，仅支持英文字母、数字、-、/ 和撇号。"; return false; }
            var candidate = Clone(Model);
            candidate.Axes = replacement.Select(a => new AxisModel
            {
                Id = a.Id, Name = a.Name?.Trim(), StartName = a.StartName?.Trim(),
                EndName = a.EndName?.Trim(), Vertical = a.Vertical, Position = a.Position,
                ExtentStart = a.ExtentStart, ExtentEnd = a.ExtentEnd
            }).ToList();
            Commit(candidate);
            return true;
        }

        public bool TryReplaceStoreys(System.Collections.Generic.IEnumerable<StoreyModel> storeys, out string error)
        {
            error = null;
            if (storeys == null) { error = "楼层列表为空。"; return false; }
            var replacement = storeys.ToList();
            if (replacement.Count == 0 || replacement.Any(s => s == null
                || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Name)
                || !Finite(s.Elevation) || !Finite(s.Height) || s.Height <= 0d
                || !Enum.IsDefined(typeof(StoreyKind), s.Kind)))
            { error = "楼层名称、标高和层高必须有效，层高应大于 0。"; return false; }
            if (replacement.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            { error = "楼层 ID 重复。"; return false; }
            var ids = new System.Collections.Generic.HashSet<string>(replacement.Select(s => s.Id),
                StringComparer.OrdinalIgnoreCase);
            foreach (var storey in replacement.Where(s => !string.IsNullOrWhiteSpace(s.TemplateStoreyId)))
            {
                var source = replacement.FirstOrDefault(s => Same(s.Id, storey.TemplateStoreyId));
                if (storey.Kind != StoreyKind.Normal || (source != null && source.Kind != StoreyKind.Normal))
                { error = "屋顶层和机房层应使用独立平面，不能作为标准层引用或来源。"; return false; }
                if (source == null || Same(source.Id, storey.Id)
                    || !string.IsNullOrWhiteSpace(source.TemplateStoreyId))
                { error = "标准层来源必须是另一独立楼层，不能形成引用链。"; return false; }
                if (Model.Walls.Any(w => Same(w.StoreyId, storey.Id))
                    || Model.Columns.Any(c => Same(c.StoreyId, storey.Id))
                    || Model.Slabs.Any(s => Same(s.StoreyId, storey.Id))
                    || Model.Stairs.Any(s => Same(s.StoreyId, storey.Id))
                    || Model.Roofs.Any(r => Same(r.StoreyId, storey.Id))
                    || Model.Rooms.Any(r => Same(r.StoreyId, storey.Id)))
                { error = "该楼层已有独立构件，请先移走再设为标准层引用。"; return false; }
            }
            if (Model.Walls.Any(w => !ids.Contains(w.StoreyId))
                || Model.Columns.Any(c => !ids.Contains(c.StoreyId))
                || Model.Slabs.Any(s => !ids.Contains(s.StoreyId))
                || Model.Stairs.Any(s => !ids.Contains(s.StoreyId))
                || Model.Roofs.Any(r => !ids.Contains(r.StoreyId))
                || Model.Rooms.Any(r => !ids.Contains(r.StoreyId)))
            { error = "已有构件所在的楼层不能删除。"; return false; }
            var candidate = Clone(Model);
            candidate.Storeys = replacement.Select(s => new StoreyModel
            { Id = s.Id.Trim(), Name = s.Name.Trim(), Kind = s.Kind, TemplateStoreyId = s.TemplateStoreyId?.Trim(),
                Elevation = s.Elevation, Height = s.Height }).ToList();
            if (candidate.Slabs.Any(s => s.TopOffset.HasValue && !Finite(s.TopOffset.Value))
                || candidate.Columns.Any(c => !Finite(c.BaseOffset) || !Finite(c.TopOffset)
                    || candidate.HeightOf(c) <= 0.5d))
            { error = "构件标高偏移无效，或修改层高后柱顶低于柱底。"; return false; }
            foreach (var slab in candidate.Slabs)
            {
                if (slab.TopOffset.HasValue) continue;
                var before = Model.FindStorey(slab.StoreyId);
                var after = candidate.FindStorey(slab.StoreyId);
                if (before == null || after == null) continue;
                var followsTop = Math.Abs(slab.TopElevation - (before.Elevation + before.Height)) < 1d;
                slab.TopElevation += followsTop
                    ? after.Elevation + after.Height - before.Elevation - before.Height
                    : after.Elevation - before.Elevation;
            }
            foreach (var roof in candidate.Roofs)
            {
                if (roof.EaveElevation <= 0.5d) continue;
                var before = Model.FindStorey(roof.StoreyId);
                var after = candidate.FindStorey(roof.StoreyId);
                if (before == null || after == null) continue;
                var followsTop = Math.Abs(roof.EaveElevation - (before.Elevation + before.Height)) < 1d;
                roof.EaveElevation += followsTop
                    ? after.Elevation + after.Height - before.Elevation - before.Height
                    : after.Elevation - before.Elevation;
            }
            foreach (var wall in candidate.Walls)
            {
                var height = wall.Height > 0d ? wall.Height : candidate.FindStorey(wall.StoreyId)?.Height ?? 0d;
                if (candidate.Openings.Where(o => Same(o.HostWallId, wall.Id))
                    .Any(o => o.Sill + o.Height > height + 0.5d))
                { error = "楼层层高修改后有门窗超出墙高：" + wall.Id; return false; }
            }
            foreach (var target in candidate.Storeys.Where(s => !string.IsNullOrWhiteSpace(s.TemplateStoreyId)))
            {
                foreach (var wall in candidate.Walls.Where(w => Same(w.StoreyId, target.TemplateStoreyId)))
                {
                    var height = wall.Height > 0d ? wall.Height : target.Height;
                    if (candidate.Openings.Where(o => Same(o.HostWallId, wall.Id))
                        .Any(o => o.Sill + o.Height > height + 0.5d))
                    { error = "标准层层高不足，门窗超出墙高：" + target.Id; return false; }
                }
            }
            Commit(candidate);
            return true;
        }

        public bool TryUpsertSlab(SlabModel source, out string id, out string error)
        {
            id = null;
            error = SlabGeometry.Validate(source);
            if (error != null) return false;
            var storey = Model.FindStorey(source.StoreyId);
            if (storey == null || !string.IsNullOrWhiteSpace(storey.TemplateStoreyId))
            { error = "请在独立楼层或标准层来源编辑楼板。"; return false; }
            var candidate = Clone(Model);
            var replacement = Clone(new BuildingModelDocument
            { Slabs = new System.Collections.Generic.List<SlabModel> { source } }).Slabs[0];
            if (replacement.Openings == null) replacement.Openings = new System.Collections.Generic.List<SlabOpeningModel>();
            replacement.Id = string.IsNullOrWhiteSpace(source.Id) ? "S-" + Guid.NewGuid().ToString("N") : source.Id;
            var index = candidate.Slabs.FindIndex(s => Same(s.Id, replacement.Id));
            if (string.IsNullOrWhiteSpace(replacement.Code))
            {
                if (index >= 0) replacement.Code = candidate.Slabs[index].Code;
                if (string.IsNullOrWhiteSpace(replacement.Code))
                {
                    var number = 1;
                    while (candidate.Slabs.Any(s => Same(s.Code, "S-" + number))) number++;
                    replacement.Code = "S-" + number;
                }
            }
            if (candidate.Slabs.Any(s => !Same(s.Id, replacement.Id) && Same(s.Code, replacement.Code)))
            { error = "楼板编号重复。"; return false; }
            if (index < 0)
            {
                if (candidate.Walls.Any(w => Same(w.Id, replacement.Id))
                    || candidate.Openings.Any(o => Same(o.Id, replacement.Id))
                    || candidate.Columns.Any(c => Same(c.Id, replacement.Id))
                    || candidate.Stairs.Any(s => Same(s.Id, replacement.Id))
                    || candidate.Roofs.Any(r => Same(r.Id, replacement.Id))
                    || candidate.Rooms.Any(r => Same(r.Id, replacement.Id)))
                { error = "楼板 ID 已被其他构件使用。"; return false; }
                // New slabs default to this floor's top datum, not an absolute zero elevation.
                if (!replacement.TopOffset.HasValue) replacement.TopOffset = 0d;
                candidate.Slabs.Add(replacement);
            }
            else candidate.Slabs[index] = replacement;
            Commit(candidate);
            id = replacement.Id;
            return true;
        }

        public bool TryTransformSlab(string sourceId, double dx, double dy, bool copy,
            out string id, out string error)
        {
            id = null;
            var slab = Model.Slabs.FirstOrDefault(s => Same(s.Id, sourceId));
            if (slab == null || !Finite(dx) || !Finite(dy))
            { error = "楼板或位移无效。"; return false; }
            var draft = Clone(new BuildingModelDocument
            { Slabs = new System.Collections.Generic.List<SlabModel> { slab } }).Slabs[0];
            if (draft.Openings == null) draft.Openings = new System.Collections.Generic.List<SlabOpeningModel>();
            foreach (var point in draft.Outline.Concat(draft.Openings.SelectMany(o => o.Outline)))
            { point.X += dx; point.Y += dy; }
            if (copy)
            {
                draft.Id = null; draft.Code = null;
                foreach (var opening in draft.Openings) opening.Id = Guid.NewGuid().ToString("N");
            }
            return TryUpsertSlab(draft, out id, out error);
        }

        public bool TryAddWall(WallModel source, out string id, out string error)
        {
            id = null;
            error = null;
            if (source == null) { error = "墙为空。"; return false; }
            var storey = Model.FindStorey(source.StoreyId);
            if (storey == null) { error = "墙所属楼层不存在。"; return false; }
            if (!Finite(source.X1) || !Finite(source.Y1) || !Finite(source.X2) || !Finite(source.Y2)
                || !Finite(source.Thickness) || !Finite(source.Height) || source.Height < 0d)
            { error = "墙的坐标、厚度和高度必须是有限且有效的数值。"; return false; }
            error = PlanEditing.ValidateWall(source);
            if (error != null) return false;
            var candidate = Clone(Model);
            var wall = new WallModel
            {
                Id = "W-" + Guid.NewGuid().ToString("N"),
                StoreyId = string.IsNullOrWhiteSpace(storey.TemplateStoreyId)
                    ? source.StoreyId : storey.TemplateStoreyId,
                X1 = source.X1, Y1 = source.Y1, X2 = source.X2, Y2 = source.Y2,
                Thickness = source.Thickness, Height = source.Height, Material = source.Material,
                AxisPlacement = source.AxisPlacement, AxisOffset = source.AxisOffset
            };
            candidate.Walls.Add(wall);
            if (!SplitOrthogonalTConnections(candidate, true, out error)) return false;
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
                if (opening != null) candidate.Openings.Remove(opening);
                else if (candidate.Columns.RemoveAll(x => x != null && Same(x.Id, id)) == 0
                    && candidate.Slabs.RemoveAll(x => x != null && Same(x.Id, id)) == 0
                    && candidate.Stairs.RemoveAll(x => x != null && Same(x.Id, id)) == 0
                    && candidate.Roofs.RemoveAll(x => x != null && Same(x.Id, id)) == 0
                    && candidate.Rooms.RemoveAll(x => x != null && Same(x.Id, id)) == 0)
                { error = "未找到可删除的构件：" + id; return false; }
            }
            Commit(candidate);
            return true;
        }

        public BuildingModelEditSession(BuildingModelDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Model = Clone(source);
            // Older files may contain an unsplit through-wall at a T junction.
            // Give each arm its own persistent wall ID before editing starts.
            SplitOrthogonalTConnections(Model, false, out _);
            BuildingElementNames.EnsureWallCodes(Model);
            _history.Reset(Model);
        }

        private static bool SplitOrthogonalTConnections(BuildingModelDocument model,
            bool rejectBlockedSplit, out string error)
        {
            error = null;
            // Keep iterating because a single through-wall may meet several branch walls.
            for (var pass = 0; pass < 256; pass++)
            {
                var split = false;
                foreach (var host in model.Walls.ToArray())
                {
                    var hostHorizontal = Math.Abs(host.Y2 - host.Y1) <= 0.001d;
                    var hostVertical = Math.Abs(host.X2 - host.X1) <= 0.001d;
                    if (hostHorizontal == hostVertical) continue;
                    var length = hostHorizontal ? Math.Abs(host.X2 - host.X1) : Math.Abs(host.Y2 - host.Y1);
                    foreach (var branch in model.Walls.ToArray())
                    {
                        if (ReferenceEquals(host, branch) || !Same(host.StoreyId, branch.StoreyId)
                            || Math.Min(model.BaseElevationOf(host) + model.HeightOf(host),
                                model.BaseElevationOf(branch) + model.HeightOf(branch))
                                - Math.Max(model.BaseElevationOf(host), model.BaseElevationOf(branch)) <= 0.5d) continue;
                        var branchHorizontal = Math.Abs(branch.Y2 - branch.Y1) <= 0.001d;
                        var branchVertical = Math.Abs(branch.X2 - branch.X1) <= 0.001d;
                        if (!(hostHorizontal && branchVertical || hostVertical && branchHorizontal)) continue;
                        foreach (var endpoint in new[] { new PointModel(branch.X1, branch.Y1),
                            new PointModel(branch.X2, branch.Y2) })
                        {
                            var onAxis = hostHorizontal ? Math.Abs(endpoint.Y - host.Y1) <= 0.5d
                                : Math.Abs(endpoint.X - host.X1) <= 0.5d;
                            var signed = hostHorizontal ? (endpoint.X - host.X1) * Math.Sign(host.X2 - host.X1)
                                : (endpoint.Y - host.Y1) * Math.Sign(host.Y2 - host.Y1);
                            if (!onAxis || signed <= 0.5d || signed >= length - 0.5d) continue;
                            var openings = model.Openings.Where(o => Same(o.HostWallId, host.Id)).ToArray();
                            if (openings.Any(o => o.Offset - o.Width / 2d < signed + 0.5d
                                && o.Offset + o.Width / 2d > signed - 0.5d))
                            {
                                if (rejectBlockedSplit)
                                { error = "T 形连接位于门窗洞口内；请先移动洞口或接点。"; return false; }
                                continue;
                            }
                            var right = new WallModel
                            {
                                Id = "W-" + Guid.NewGuid().ToString("N"), StoreyId = host.StoreyId,
                                X1 = endpoint.X, Y1 = endpoint.Y, X2 = host.X2, Y2 = host.Y2,
                                Thickness = host.Thickness, Height = host.Height,
                                Material = host.Material, AxisPlacement = host.AxisPlacement,
                                AxisOffset = host.AxisOffset
                            };
                            host.X2 = endpoint.X; host.Y2 = endpoint.Y;
                            model.Walls.Add(right);
                            foreach (var opening in openings.Where(o => o.Offset > signed))
                            { opening.HostWallId = right.Id; opening.Offset -= signed; }
                            split = true;
                            break;
                        }
                        if (split) break;
                    }
                    if (split) break;
                }
                if (!split) return true;
            }
            error = "T 形墙体连接过多，无法自动拆分。";
            return false;
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

        /// <summary>Moves only the selected wall endpoint; the ordinary editor does not infer a persistent junction constraint.</summary>
        public bool TryMoveWallGripOnly(string id, int endpointIndex, double x, double y, out string error)
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
            var target = candidate.Walls.First(w => w != null && Same(w.Id, id));
            if (endpointIndex == 0) { target.X1 = x; target.Y1 = y; }
            else { target.X2 = x; target.Y2 = y; }
            error = ValidateWallAndOpenings(candidate, target);
            if (error != null) return false;
            Commit(candidate);
            return true;
        }

        /// <summary>Explicit optional constraint operation: moves coincident endpoints and T-junction dependencies.</summary>
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
            if (!WallGripPropagation.TryCreate(Model, source.StoreyId, out var graph, out error)
                || !graph.TryMove(id, endpointIndex, x, y, out var changes, out error)) return false;
            var candidate = Clone(Model);
            var byId = candidate.Walls.Where(w => w != null && Same(w.StoreyId, source.StoreyId))
                .ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
            var changed = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var move in changes)
            {
                var target = byId[move.WallId];
                if (move.Index == 0) { target.X1 = move.X; target.Y1 = move.Y; }
                else { target.X2 = move.X; target.Y2 = move.Y; }
                changed.Add(move.WallId);
            }
            foreach (var current in changed.Select(wallId => byId[wallId]))
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
                    Thickness = source.Thickness, Height = source.Height, Material = source.Material,
                    AxisPlacement = source.AxisPlacement, AxisOffset = source.AxisOffset
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
            var wall = Model.Walls.FirstOrDefault(x => x != null && Same(x.Id, id));
            if (wall == null) { error = "未找到墙：" + id; return false; }
            return TrySetWallGeometryCore(id, length, thickness, height,
                wall.AxisPlacement, wall.AxisOffset, out error);
        }

        public bool TrySetWallGeometry(string id, double length, double thickness, double height,
            WallAxisPlacement placement, out string error)
            => TrySetWallGeometryCore(id, length, thickness, height, placement, null, out error);

        public bool TrySetWallGeometryBySides(string id, double length, double left, double right,
            double height, out string error)
        {
            if (!Finite(left) || !Finite(right) || left < 0d || right < 0d || left + right <= 0d)
            { error = "轴线左右两侧墙厚必须是非负有限数值，且总墙厚大于 0。"; return false; }
            return TrySetWallGeometryCore(id, length, left + right, height,
                WallAxisPlacement.Center, (left - right) / 2d, out error);
        }

        private bool TrySetWallGeometryCore(string id, double length, double thickness, double height,
            WallAxisPlacement placement, double? axisOffset, out string error)
        {
            error = null;
            if (!Enum.IsDefined(typeof(WallAxisPlacement), placement))
            { error = "墙定位轴线只能位于墙中、左面或右面。"; return false; }
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
            wall.AxisPlacement = placement;
            wall.AxisOffset = axisOffset;
            error = ValidateWallAndOpenings(candidate, wall);
            if (error != null) return false;
            Commit(candidate);
            return true;
        }

        public bool TrySetWallAxisPlacement(string id, WallAxisPlacement placement, out string error)
        {
            error = null;
            if (!Enum.IsDefined(typeof(WallAxisPlacement), placement))
            { error = "墙定位轴线只能位于墙中、左面或右面。"; return false; }
            var candidate = Clone(Model);
            var wall = candidate.Walls.FirstOrDefault(x => x != null && Same(x.Id, id));
            if (wall == null) { error = "未找到墙：" + id; return false; }
            if (wall.AxisPlacement == placement && wall.AxisOffset == null) return true;
            wall.AxisPlacement = placement;
            wall.AxisOffset = null;
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
            BuildingElementNames.EnsureWallCodes(candidate);
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
