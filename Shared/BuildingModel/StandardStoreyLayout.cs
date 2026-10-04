using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>Read-only instances of one source plan at each standard floor's own elevation.</summary>
    public static class StandardStoreyLayout
    {
        private const string Suffix = "@STD@";

        public static bool TryParseRange(string label, out int first, out int last)
        {
            first = last = 0;
            var match = System.Text.RegularExpressions.Regex.Match((label ?? "").Replace(" ",""),
                @"^(\d+)(?:层|F)?[~～至到—-](\d+)(?:层|F)?$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value,out first)
                && int.TryParse(match.Groups[2].Value,out last) && first>=1 && last>=first && last<=500;
        }

        /// <summary>范围行展开成实际楼层，平面自动归组到范围首层；随后由标高布局逐层推算。</summary>
        public static List<StoreyModel> ExpandRanges(IEnumerable<StoreyModel> rows)
        {
            var result = new List<StoreyModel>();
            foreach (var row in rows)
            {
                var label = string.IsNullOrWhiteSpace(row.StandardFloorRange) ? row.Name : row.StandardFloorRange;
                int first,last;
                if (!TryParseRange(label,out first,out last))
                {
                    if ((label ?? "").IndexOfAny(new[] {'~','～','至','到','—'})>=0
                        || System.Text.RegularExpressions.Regex.IsMatch(label ?? "",@"^\d+.*-.*\d+"))
                        throw new ArgumentException("楼层范围无效，请填写如 4～15层，起止层应递增。");
                    result.Add(new StoreyModel { Id=row.Id,Name=row.Name,Kind=row.Kind,Height=row.Height,Elevation=row.Elevation,
                        TemplateStoreyId=row.TemplateStoreyId });
                    continue;
                }
                if (row.Kind!=StoreyKind.Normal) throw new ArgumentException("屋顶层和机房层不能设置标准层范围。");
                if (!string.Equals(row.Id,first+"F",StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("此行从 "+row.Id+" 开始，范围首层应与本行楼层一致。");
                for (var floor=first;floor<=last;floor++)
                    result.Add(new StoreyModel { Id=floor+"F",Name=floor+"层",Kind=row.Kind,Height=row.Height,
                        Elevation=row.Elevation+(floor-first)*row.Height,
                        TemplateStoreyId=floor==first ? null : row.Id,StandardGroupId=row.Id,
                        StandardFloorRange=floor==first ? first+"～"+last+"层" : null });
            }
            if (result.GroupBy(s=>s.Id,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))
                throw new ArgumentException("楼层范围与已有楼层重叠，请调整范围或删除重复楼层行。");
            return result;
        }

        public static string SourceElementId(BuildingModelDocument model, string id)
        {
            if (model == null || string.IsNullOrEmpty(id)) return id;
            foreach (var storey in model.Storeys.Where(s => !string.IsNullOrWhiteSpace(s.TemplateStoreyId)))
            {
                var suffix = Suffix + storey.Id;
                if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return id.Substring(0, id.Length - suffix.Length);
            }
            return id;
        }

        public static BuildingModelDocument Materialize(BuildingModelDocument model)
        {
            if (model == null || !model.Storeys.Any(s => !string.IsNullOrWhiteSpace(s.TemplateStoreyId)))
                return OpeningConstruction.ApplyOverrides(model);
            var expanded = new BuildingModelDocument
            {
                SchemaVersion = model.SchemaVersion,
                Name = model.Name,
                Storeys = model.Storeys.Select(s => new StoreyModel
                { Id = s.Id, Name = s.Name, Kind = s.Kind, Elevation = s.Elevation, Height = s.Height }).ToList(),
                Walls = new List<WallModel>(model.Walls),
                Openings = new List<OpeningModel>(model.Openings),
                Slabs = new List<SlabModel>(model.Slabs),
                Columns = new List<ColumnModel>(model.Columns),
                Beams = new List<BeamModel>(model.Beams),
                Stairs = new List<StairModel>(model.Stairs),
                Roofs = new List<RoofModel>(model.Roofs),
                Rooms = new List<RoomModel>(model.Rooms),
                Axes = model.Axes, StoreyAxes=model.StoreyAxes,
                Annotations=model.Annotations,DrawingScales=model.DrawingScales,DrawingViews = model.DrawingViews, OpeningTypes=model.OpeningTypes, OpeningTemplates=model.OpeningTemplates, OpeningOverrides=model.OpeningOverrides,OpeningEditorSnapStep=model.OpeningEditorSnapStep
            };
            foreach (var target in model.Storeys.Where(s => !string.IsNullOrWhiteSpace(s.TemplateStoreyId)))
            {
                var source = model.FindStorey(target.TemplateStoreyId);
                if (source == null || target.Kind != StoreyKind.Normal || source.Kind != StoreyKind.Normal
                    || !string.IsNullOrWhiteSpace(source.TemplateStoreyId)
                    || string.Equals(source.Id, target.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("标准层来源无效：" + target.Id);
                var baseShift = target.Elevation - source.Elevation;
                var topShift = target.Elevation + target.Height - source.Elevation - source.Height;
                string InstanceId(string id) { return id + Suffix + target.Id; }
                var sourceWalls = model.Walls.Where(w => Same(w.StoreyId, source.Id)).ToList();
                var sourceWallIds = new HashSet<string>(sourceWalls.Select(w => w.Id),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var wall in sourceWalls)
                    expanded.Walls.Add(new WallModel
                    {
                        Id = InstanceId(wall.Id), Code = wall.Code, StoreyId = target.Id, X1 = wall.X1, Y1 = wall.Y1,
                        X2 = wall.X2, Y2 = wall.Y2, Thickness = wall.Thickness,
                        AxisPlacement = wall.AxisPlacement, AxisOffset = wall.AxisOffset,
                        Height = wall.Height, Material = wall.Material
                    });
                foreach (var opening in model.Openings.Where(o => sourceWallIds.Contains(o.HostWallId)))
                    expanded.Openings.Add(new OpeningModel
                    {
                        Id = InstanceId(opening.Id), HostWallId = InstanceId(opening.HostWallId),
                        Code = opening.Code, Kind = opening.Kind, Offset = opening.Offset,
                        Width = opening.Width, Height = opening.Height, Sill = opening.Sill, ThresholdHeight=opening.ThresholdHeight,
                        PlanFlipAlong=opening.PlanFlipAlong,PlanFlipNormal=opening.PlanFlipNormal,
                        PlanLabelAlong=opening.PlanLabelAlong,PlanLabelNormal=opening.PlanLabelNormal,
                        PlanOpenAngle=opening.PlanOpenAngle,OpenIn3D=opening.OpenIn3D,CodeManuallyEdited=opening.CodeManuallyEdited
                    });
                foreach (var slab in model.Slabs.Where(s => Same(s.StoreyId, source.Id)))
                {
                    var atTop = Math.Abs(slab.TopElevation - source.Elevation - source.Height) < 1d;
                    expanded.Slabs.Add(new SlabModel
                    {
                        Id = InstanceId(slab.Id), Code = slab.Code, StoreyId = target.Id, Outline = slab.Outline,
                        Openings = (slab.Openings ?? new List<SlabOpeningModel>()).Select(o => new SlabOpeningModel
                        { Id = InstanceId(o.Id), Name = o.Name, Outline = o.Outline }).ToList(),
                        Thickness = slab.Thickness, TopOffset = slab.TopOffset,FollowsStoreyTop=slab.FollowsStoreyTop,
                        TopElevation = slab.TopElevation + (atTop ? topShift : baseShift)
                    });
                }
                foreach (var column in model.Columns.Where(c => Same(c.StoreyId, source.Id)))
                    expanded.Columns.Add(new ColumnModel
                    {
                        Id = InstanceId(column.Id), StoreyId = target.Id,
                        X = column.X, Y = column.Y, Width = column.Width,
                        Depth = column.Depth, Height = column.Height,
                        BaseOffset = column.BaseOffset, TopOffset = column.TopOffset,
                        Code=column.Code,RotationDegrees=column.RotationDegrees
                    });
                foreach(var beam in model.Beams.Where(b=>Same(b.StoreyId,source.Id)))
                    expanded.Beams.Add(new BeamModel {Id=InstanceId(beam.Id),StoreyId=target.Id,Code=beam.Code,
                        X1=beam.X1,Y1=beam.Y1,X2=beam.X2,Y2=beam.Y2,Width=beam.Width,Depth=beam.Depth,TopOffset=beam.TopOffset});
                foreach (var stair in model.Stairs.Where(s => Same(s.StoreyId, source.Id)))
                    expanded.Stairs.Add(new StairModel
                    {
                        Id = InstanceId(stair.Id), StoreyId = target.Id,
                        X = stair.X, Y = stair.Y, Length = stair.Length, Width = stair.Width,
                        AlongX = stair.AlongX, FlightWidth = stair.FlightWidth, Going = stair.Going,
                        StepsPerFlight = stair.StepsPerFlight, Riser = stair.Riser,
                        LandingDepth = stair.LandingDepth, WellWidth = stair.WellWidth
                    });
                foreach (var roof in model.Roofs.Where(r => Same(r.StoreyId, source.Id)))
                {
                    var atTop = Math.Abs(roof.EaveElevation - source.Elevation - source.Height) < 1d;
                    expanded.Roofs.Add(new RoofModel
                    {
                        Id = InstanceId(roof.Id), StoreyId = target.Id,
                        X = roof.X, Y = roof.Y, Width = roof.Width, Depth = roof.Depth,
                        AlongX = roof.AlongX, PitchDegrees = roof.PitchDegrees,
                        EaveElevation = roof.EaveElevation <= 0.5d ? roof.EaveElevation
                            : roof.EaveElevation + (atTop ? topShift : baseShift),
                        Thickness = roof.Thickness
                    });
                }
                foreach (var room in model.Rooms.Where(r => Same(r.StoreyId, source.Id)))
                    expanded.Rooms.Add(new RoomModel
                    {
                        Id = InstanceId(room.Id), StoreyId = target.Id,
                        Name = room.Name, Outline = room.Outline
                    });
            }
            return OpeningConstruction.ApplyOverrides(expanded);
        }

        private static bool Same(string left, string right)
            => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
