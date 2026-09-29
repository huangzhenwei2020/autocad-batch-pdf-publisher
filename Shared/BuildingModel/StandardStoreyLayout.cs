using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>Read-only instances of one source plan at each standard floor's own elevation.</summary>
    public static class StandardStoreyLayout
    {
        private const string Suffix = "@STD@";

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
                return model;
            var expanded = new BuildingModelDocument
            {
                SchemaVersion = model.SchemaVersion,
                Name = model.Name,
                Storeys = model.Storeys.Select(s => new StoreyModel
                { Id = s.Id, Name = s.Name, Elevation = s.Elevation, Height = s.Height }).ToList(),
                Walls = new List<WallModel>(model.Walls),
                Openings = new List<OpeningModel>(model.Openings),
                Slabs = new List<SlabModel>(model.Slabs),
                Columns = new List<ColumnModel>(model.Columns),
                Stairs = new List<StairModel>(model.Stairs),
                Roofs = new List<RoofModel>(model.Roofs),
                Rooms = new List<RoomModel>(model.Rooms),
                Axes = model.Axes
            };
            foreach (var target in model.Storeys.Where(s => !string.IsNullOrWhiteSpace(s.TemplateStoreyId)))
            {
                var source = model.FindStorey(target.TemplateStoreyId);
                if (source == null || !string.IsNullOrWhiteSpace(source.TemplateStoreyId)
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
                        Id = InstanceId(wall.Id), StoreyId = target.Id, X1 = wall.X1, Y1 = wall.Y1,
                        X2 = wall.X2, Y2 = wall.Y2, Thickness = wall.Thickness,
                        AxisPlacement = wall.AxisPlacement, AxisOffset = wall.AxisOffset,
                        Height = wall.Height, Material = wall.Material
                    });
                foreach (var opening in model.Openings.Where(o => sourceWallIds.Contains(o.HostWallId)))
                    expanded.Openings.Add(new OpeningModel
                    {
                        Id = InstanceId(opening.Id), HostWallId = InstanceId(opening.HostWallId),
                        Code = opening.Code, Kind = opening.Kind, Offset = opening.Offset,
                        Width = opening.Width, Height = opening.Height, Sill = opening.Sill
                    });
                foreach (var slab in model.Slabs.Where(s => Same(s.StoreyId, source.Id)))
                {
                    var atTop = Math.Abs(slab.TopElevation - source.Elevation - source.Height) < 1d;
                    expanded.Slabs.Add(new SlabModel
                    {
                        Id = InstanceId(slab.Id), StoreyId = target.Id, Outline = slab.Outline,
                        Thickness = slab.Thickness,
                        TopElevation = slab.TopElevation + (atTop ? topShift : baseShift)
                    });
                }
                foreach (var column in model.Columns.Where(c => Same(c.StoreyId, source.Id)))
                    expanded.Columns.Add(new ColumnModel
                    {
                        Id = InstanceId(column.Id), StoreyId = target.Id,
                        X = column.X, Y = column.Y, Width = column.Width,
                        Depth = column.Depth, Height = column.Height
                    });
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
            return expanded;
        }

        private static bool Same(string left, string right)
            => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
