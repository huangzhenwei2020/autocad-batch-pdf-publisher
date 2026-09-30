using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>体量里的一个面（平面多边形，顶点按逆时针，法线朝外）。</summary>
    public sealed class VolumeFace
    {
        public List<Point3DModel> Points { get; set; } = new List<Point3DModel>();
        /// <summary>构件种类：wall / column / slab。</summary>
        public string Kind { get; set; }
        /// <summary>生成此面的语义构件 ID；拾取和选中均使用它，不使用临时面序号。</summary>
        public string ElementId { get; set; }
        /// <summary>所属楼层（用于"只显示某层"与着色）。</summary>
        public string StoreyId { get; set; }
        /// <summary>面法线（单位向量，朝外）。</summary>
        public double NormalX { get; set; }
        public double NormalY { get; set; }
        public double NormalZ { get; set; }
        /// <summary>是不是顶面（屋面/楼板顶）——顶面单独给个浅色，看着更像轴测图。</summary>
        public bool IsUp { get { return NormalZ > 0.5d; } }
    }

    /// <summary>整栋（或某层）的体量：一堆面 + 包围盒。</summary>
    public sealed class BuildingVolume
    {
        public List<VolumeFace> Faces { get; set; } = new List<VolumeFace>();
        /// <summary>定位参考线，仅供编辑视口显示；不是实体面，也不参与拾取或 CAD 网格。</summary>
        public List<VolumeGuideLine> GuideLines { get; set; } = new List<VolumeGuideLine>();
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public double Width { get { return MaxX - MinX; } }
        public double Depth { get { return MaxY - MinY; } }
        public double Height { get { return MaxZ - MinZ; } }

        public Point3DModel Center
        {
            get { return new Point3DModel((MinX + MaxX) / 2d, (MinY + MaxY) / 2d, (MinZ + MaxZ) / 2d); }
        }

        public double Diagonal
        {
            get
            {
                var dx = Width; var dy = Depth; var dz = Height;
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }
    }

    public sealed class VolumeGuideLine
    {
        public Point3DModel Start { get; set; }
        public Point3DModel End { get; set; }
        public string ElementId { get; set; }
        public string Label { get; set; }
        public string StartLabel { get; set; }
        public string EndLabel { get; set; }
        public bool IsBuildingAxis { get; set; }
        public bool IsWallJoint { get; set; }
    }

    /// <summary>
    /// 从建筑模型生成**三维体量**（P4 的第一块，纯几何、不依赖任何图形库）：
    ///
    /// - 正交、等高且无洞口的相接墙 → 融合后的实体；其他墙逐墙生成；
    /// - 柱 → 长方体（宽 × 深 × 高）；
    /// - 楼板 → 按轮廓拉出的棱柱（顶标高 - 板厚 ~ 顶标高）。
    ///
    /// 已知简化（P4 后续）：**门窗洞口还没在体量上开洞**（洞口位置在立面投影里已经处理），
    /// 斜墙按轴线方向的矩形处理，楼梯/坡屋面还没做。
    /// </summary>
    public static class BuildingVolumeBuilder
    {
        public static BuildingVolume Build(BuildingModelDocument model, string storeyId = null)
        {
            var volume = new BuildingVolume();
            if (model == null) return volume;
            model = StandardStoreyLayout.Materialize(model);
            var onlyOne = !string.IsNullOrWhiteSpace(storeyId);
            var first = true;

            var walls = (model.Walls ?? new List<WallModel>())
                .Where(wall => wall != null && (!onlyOne || Same(wall.StoreyId, storeyId))).ToList();
            var merged = OrthogonalWallUnion.AddJoinedWalls(volume, model, walls, ref first);
            foreach (var floorWalls in walls.GroupBy(w => w.StoreyId))
            {
                var top = model.BaseElevationOf(floorWalls.First()) + model.HeightOf(floorWalls.First()) + 2d;
                foreach (var seam in WallJunctionLines.Resolve(model, floorWalls))
                    volume.GuideLines.Add(new VolumeGuideLine
                    {
                        Start = new Point3DModel(seam.Item1.X, seam.Item1.Y, top),
                        End = new Point3DModel(seam.Item2.X, seam.Item2.Y, top),
                        IsWallJoint = true
                    });
            }
            foreach (var wall in walls)
            {
                var z0 = model.BaseElevationOf(wall);
                var z1 = z0 + model.HeightOf(wall);
                if (merged.Contains(wall.Id)) AddMergedWallOpeningParts(volume, wall, model, z0, z1, ref first);
                else AddWallWithOpenings(volume, wall, model, z0, z1, ref first);
            }
            foreach (var column in model.Columns ?? new List<ColumnModel>())
            {
                if (column == null) continue;
                if (onlyOne && !Same(column.StoreyId, storeyId)) continue;
                var storey = model.FindStorey(column.StoreyId);
                var z0 = storey == null ? 0d : storey.Elevation;
                var z1 = z0 + model.HeightOf(column);
                AddColumnBox(volume, column, z0, z1, ref first);
            }
            foreach (var slab in model.Slabs ?? new List<SlabModel>())
            {
                if (slab == null) continue;
                if (onlyOne && !Same(slab.StoreyId, storeyId)) continue;
                AddSlabPrism(volume, slab, ref first);
            }
            foreach (var stair in model.Stairs ?? new List<StairModel>())
            {
                if (stair == null) continue;
                if (onlyOne && !Same(stair.StoreyId, storeyId)) continue;
                AddStair(volume, model, stair, ref first);
            }
            foreach (var roof in model.Roofs ?? new List<RoofModel>())
            {
                if (roof == null) continue;
                if (onlyOne && !Same(roof.StoreyId, storeyId)) continue;
                AddRoof(volume, model, roof, ref first);
            }
            if (!first)
            {
                var guideElevation = walls.Count == 0 ? volume.MinZ
                    : walls.Min(wall => model.BaseElevationOf(wall));
                foreach (var wall in walls)
                {
                    if (!onlyOne && Math.Abs(model.BaseElevationOf(wall) - guideElevation) > 0.001d)
                        continue;
                    var z = model.BaseElevationOf(wall) + 2d;
                    var dx = wall.X2 - wall.X1;
                    var dy = wall.Y2 - wall.Y1;
                    var length = Math.Sqrt(dx * dx + dy * dy);
                    if (length < 1d) continue;
                    var ux = dx / length; var uy = dy / length;
                    volume.GuideLines.Add(new VolumeGuideLine
                    {
                        Start = new Point3DModel(wall.X1 - ux * 1200d, wall.Y1 - uy * 1200d, z),
                        End = new Point3DModel(wall.X2 + ux * 1200d, wall.Y2 + uy * 1200d, z), ElementId = wall.Id
                    });
                }
                foreach (var axis in BuildingAxisLayout.Resolve(model))
                {
                    if (axis == null || !IsFinite(axis.Position)) continue;
                    var from = axis.ExtentStart == 0 && axis.ExtentEnd == 0
                        ? (axis.Vertical ? volume.MinY - 4500d : volume.MinX - 4500d)
                        : Math.Min(axis.ExtentStart, axis.ExtentEnd);
                    var to = axis.ExtentStart == 0 && axis.ExtentEnd == 0
                        ? (axis.Vertical ? volume.MaxY + 4500d : volume.MaxX + 4500d)
                        : Math.Max(axis.ExtentStart, axis.ExtentEnd);
                    var z = volume.MinZ + 2d;
                    volume.GuideLines.Add(new VolumeGuideLine
                    {
                        Start = axis.Vertical ? new Point3DModel(axis.Position, from, z)
                            : new Point3DModel(from, axis.Position, z),
                        End = axis.Vertical ? new Point3DModel(axis.Position, to, z)
                            : new Point3DModel(to, axis.Position, z),
                        ElementId = axis.Id, Label = axis.Name,
                        StartLabel = string.IsNullOrWhiteSpace(axis.StartName) ? axis.Name : axis.StartName,
                        EndLabel = string.IsNullOrWhiteSpace(axis.EndName) ? axis.Name : axis.EndName,
                        IsBuildingAxis = true
                    });
                }
            }
            return volume;
        }

        /// <summary>
        /// 坡屋面 → 5 个面（底面 + 两坡 + 两端山墙三角），几何全部由 <see cref="RoofGeometry"/> 算。
        /// </summary>
        private static void AddRoof(BuildingVolume volume, BuildingModelDocument model, RoofModel roof, ref bool first)
        {
            var geometry = RoofGeometry.Build(model, roof);
            if (geometry == null || !geometry.IsValid) return;
            var faces = geometry.ToFaces("roof", roof.StoreyId);
            foreach (var face in faces)
            {
                face.ElementId = roof.Id;
                volume.Faces.Add(face);
                foreach (var point in face.Points)
                {
                    if (first)
                    {
                        volume.MinX = volume.MaxX = point.X; volume.MinY = volume.MaxY = point.Y;
                        volume.MinZ = volume.MaxZ = point.Z; first = false;
                    }
                    else
                    {
                        volume.MinX = Math.Min(volume.MinX, point.X); volume.MaxX = Math.Max(volume.MaxX, point.X);
                        volume.MinY = Math.Min(volume.MinY, point.Y); volume.MaxY = Math.Max(volume.MaxY, point.Y);
                        volume.MinZ = Math.Min(volume.MinZ, point.Z); volume.MaxZ = Math.Max(volume.MaxZ, point.Z);
                    }
                }
            }
        }

        /// <summary>
        /// 楼梯 → 一级踏步一个小方块（底面按 踏面 - 踏步高 - 板厚 取，下面就是斜板的样子）+
        /// 休息平台板 + 两侧**栏板**（靠梯井那侧，每 3 级一段）。
        /// </summary>
        private static void AddStair(BuildingVolume volume, BuildingModelDocument model, StairModel stair, ref bool first)
        {
            var geometry = StairGeometry.Build(model, stair);
            if (geometry == null || geometry.Flights.Count == 0) return;
            const double treadThickness = 120d;      // 梯段板厚（近似）
            const double railHeight = 1000d;         // 栏板高（从踏面算）
            const double railThickness = 40d;        // 栏板厚
            const int railGroup = 3;                 // 每 3 级一段栏板（跟着踏步台阶式上升）

            var spanT = geometry.AlongX ? geometry.Y1 - geometry.Y0 : geometry.X1 - geometry.X0;
            foreach (var flight in geometry.Flights)
            {
                foreach (var step in flight.Steps)
                {
                    var z1 = step.TopElevation;
                    var z0 = Math.Max(geometry.BaseElevation, z1 - geometry.Riser - treadThickness);
                    AddBox(volume, step.X0, step.Y0, step.X1, step.Y1, z0, z1, "stair", stair.StoreyId, stair.Id, ref first);
                }

                if (flight.Steps.Count == 0) continue;
                // 靠梯井那一侧的栏板：第一跑在南/西侧条带（梯井在 t 大的一侧），第二跑相反
                var wellT = flight.Index == 0
                    ? (geometry.AlongX ? geometry.Y0 + stair.FlightWidth : geometry.X0 + stair.FlightWidth)
                    : (geometry.AlongX ? geometry.Y1 - stair.FlightWidth : geometry.X1 - stair.FlightWidth);
                for (var index = 0; index < flight.Steps.Count; index += railGroup)
                {
                    var last = Math.Min(index + railGroup - 1, flight.Steps.Count - 1);
                    var from = flight.Steps[index];
                    var to = flight.Steps[last];
                    var z0 = from.TopElevation;
                    var z1 = to.TopElevation + railHeight;
                    if (geometry.AlongX)
                    {
                        var x0 = Math.Min(from.X0, to.X0);
                        var x1 = Math.Max(from.X1, to.X1);
                        var inner = wellT - railThickness;
                        AddBox(volume, x0, Math.Min(inner, wellT), x1, Math.Max(inner, wellT), z0, z1,
                            "stair", stair.StoreyId, stair.Id, ref first);
                    }
                    else
                    {
                        var y0 = Math.Min(from.Y0, to.Y0);
                        var y1 = Math.Max(from.Y1, to.Y1);
                        var inner = wellT - railThickness;
                        AddBox(volume, Math.Min(inner, wellT), y0, Math.Max(inner, wellT), y1, z0, z1,
                            "stair", stair.StoreyId, stair.Id, ref first);
                    }
                }
                _ = spanT;
            }

            // 休息平台板
            if (geometry.LandingX1 - geometry.LandingX0 > 1d && geometry.LandingY1 - geometry.LandingY0 > 1d)
                AddBox(volume, geometry.LandingX0, geometry.LandingY0, geometry.LandingX1, geometry.LandingY1,
                    geometry.LandingElevation - geometry.LandingThickness, geometry.LandingElevation,
                    "stair", stair.StoreyId, stair.Id, ref first);
        }

        /// <summary>轴对齐的长方体（平面矩形 + 底顶标高）。</summary>
        private static void AddBox(BuildingVolume volume, double x0, double y0, double x1, double y1,
            double z0, double z1, string kind, string storeyId, string elementId, ref bool first)
        {
            if (x1 - x0 < 0.5d || y1 - y0 < 0.5d || z1 - z0 < 0.5d) return;
            var corners = new List<Point3DModel>
            {
                new Point3DModel(x0, y0, z0), new Point3DModel(x1, y0, z0),
                new Point3DModel(x1, y1, z0), new Point3DModel(x0, y1, z0)
            };
            AddPrism(volume, corners, z0, z1, kind, storeyId, elementId, ref first);
        }

        /// <summary>
        /// 墙 + 洞口 → 一组体块：**把洞口从墙上挖掉**。
        /// 沿墙轴线按洞口分段：洞口之间的墙拉整高盒子；洞口那段只在"窗台以下 / 窗顶以上"拉盒子，
        /// 于是窗和门在三维里就是真的洞（这是体量生成最关键的一步 —— 不然建筑永远是个实心方块）。
        /// </summary>
        private static void AddWallWithOpenings(BuildingVolume volume, WallModel wall, BuildingModelDocument model,
            double z0, double z1, ref bool first)
        {
            var length = Math.Sqrt((wall.X2 - wall.X1) * (wall.X2 - wall.X1) + (wall.Y2 - wall.Y1) * (wall.Y2 - wall.Y1));
            if (length < 1d || z1 - z0 < 1d) return;
            var height = z1 - z0;

            var openings = (model.Openings ?? new List<OpeningModel>())
                .Where(opening => opening != null && Same(opening.HostWallId, wall.Id))
                .Select(opening =>
                {
                    var half = Math.Max(0d, opening.Width) / 2d;
                    var sill = Math.Max(0d, opening.Sill);
                    var head = Math.Min(sill + Math.Max(0d, opening.Height), height);
                    return new
                    {
                        Source = opening,
                        Start = Math.Max(0d, opening.Offset - half),
                        End = Math.Min(length, opening.Offset + half),
                        Sill = sill,
                        Head = head
                    };
                })
                .Where(opening => opening.End - opening.Start > 1d)
                .OrderBy(opening => opening.Start)
                .ToList();

            var cursor = 0d;
            foreach (var opening in openings)
            {
                if (opening.Start - cursor > 1d) AddWallSegment(volume, wall, cursor, opening.Start, z0, z1, ref first);
                if (opening.Sill > 1d) AddWallSegment(volume, wall, opening.Start, opening.End, z0, z0 + opening.Sill, ref first);
                if (height - opening.Head > 1d)
                    AddWallSegment(volume, wall, opening.Start, opening.End, z0 + opening.Head, z1, ref first);
                cursor = Math.Max(cursor, opening.End);
            }
            if (length - cursor > 1d) AddWallSegment(volume, wall, cursor, length, z0, z1, ref first);

            // 门窗构件：窗 = 四条边框 + 玻璃；门 = 一扇打开的门扇（这样三维里一眼能看出是窗还是门）
            foreach (var opening in openings)
            {
                var kind = opening.Source.Kind ?? "窗";
                if (kind.IndexOf("门", StringComparison.Ordinal) >= 0)
                    AddDoorLeaf(volume, wall, opening.Start, opening.End, z0 + opening.Sill, z0 + opening.Head,
                        wall.StoreyId, opening.Source.Id, ref first);
                else
                    AddWindowParts(volume, wall, opening.Start, opening.End, z0 + opening.Sill, z0 + opening.Head,
                        wall.StoreyId, opening.Source.Id, ref first);
            }
        }

        private static void AddMergedWallOpeningParts(BuildingVolume volume, WallModel wall,
            BuildingModelDocument model, double z0, double z1, ref bool first)
        {
            var length = Math.Sqrt(Math.Pow(wall.X2 - wall.X1, 2) + Math.Pow(wall.Y2 - wall.Y1, 2));
            foreach (var opening in model.Openings ?? new List<OpeningModel>())
            {
                if (opening == null || !Same(opening.HostWallId, wall.Id)) continue;
                var start = Math.Max(0d, opening.Offset - opening.Width / 2d);
                var end = Math.Min(length, opening.Offset + opening.Width / 2d);
                var sill = z0 + Math.Max(0d, opening.Sill);
                var head = Math.Min(z1, sill + Math.Max(0d, opening.Height));
                if (end - start < 1d || head - sill < 1d) continue;
                if ((opening.Kind ?? "窗").IndexOf("门", StringComparison.Ordinal) >= 0)
                    AddDoorLeaf(volume, wall, start, end, sill, head,
                        wall.StoreyId, opening.Id, ref first);
                else AddWindowParts(volume, wall, start, end, sill, head,
                    wall.StoreyId, opening.Id, ref first);
            }
        }

        /// <summary>窗：外框（四条边各一块）+ 玻璃（薄板，居中在墙厚里）。</summary>
        private static void AddWindowParts(BuildingVolume volume, WallModel wall, double start, double end,
            double sill, double head, string storeyId, string elementId, ref bool first)
        {
            var width = end - start;
            var height = head - sill;
            if (width < 40d || height < 40d) return;
            var frame = Math.Min(60d, Math.Min(width, height) / 4d);
            AddWallSegment(volume, wall, start, end, sill, sill + frame, storeyId, "frame", ref first, 0d, elementId); // 下框
            AddWallSegment(volume, wall, start, end, head - frame, head, storeyId, "frame", ref first, 0d, elementId); // 上框
            // Side rails meet the horizontal rails, rather than overlapping them at all four corners.
            AddWallSegment(volume, wall, start, start + frame, sill + frame, head - frame, storeyId, "frame", ref first, 0d, elementId); // 左框
            AddWallSegment(volume, wall, end - frame, end, sill + frame, head - frame, storeyId, "frame", ref first, 0d, elementId);   // 右框
            AddWallSegment(volume, wall, start + frame, end - frame, sill + frame, head - frame, storeyId, "glass",
                ref first, 20d, elementId);                                                                             // 玻璃
        }

        /// <summary>
        /// 门：一扇**打开 35°** 的门扇（合页在洞口起点一侧、朝墙法线方向开）。
        /// 模型里还没存开启方向，这里按制图习惯统一取"起点侧合页、向法线正方向开"。
        /// </summary>
        private static void AddDoorLeaf(BuildingVolume volume, WallModel wall, double start, double end,
            double sill, double head, string storeyId, string elementId, ref bool first)
        {
            var width = end - start;
            var height = head - sill;
            var length = Math.Sqrt((wall.X2 - wall.X1) * (wall.X2 - wall.X1) + (wall.Y2 - wall.Y1) * (wall.Y2 - wall.Y1));
            if (length < 1d || width < 40d || height < 40d) return;
            var ux = (wall.X2 - wall.X1) / length;
            var uy = (wall.Y2 - wall.Y1) / length;
            var nx = -uy;
            var ny = ux;
            const double angle = 35d * Math.PI / 180d;
            var dx = ux * Math.Cos(angle) + nx * Math.Sin(angle);
            var dy = uy * Math.Cos(angle) + ny * Math.Sin(angle);
            var thickness = 40d;
            var hingeX = wall.X1 + ux * start;
            var hingeY = wall.Y1 + uy * start;
            var hinge = WallReferenceGeometry.BodyPoint(wall, hingeX, hingeY);
            hingeX = hinge.X; hingeY = hinge.Y;

            var corners = new List<Point3DModel>
            {
                new Point3DModel(hingeX - nx * thickness / 2d, hingeY - ny * thickness / 2d, sill),
                new Point3DModel(hingeX + dx * width - nx * thickness / 2d, hingeY + dy * width - ny * thickness / 2d, sill),
                new Point3DModel(hingeX + dx * width + nx * thickness / 2d, hingeY + dy * width + ny * thickness / 2d, sill),
                new Point3DModel(hingeX + nx * thickness / 2d, hingeY + ny * thickness / 2d, sill)
            };
            AddPrism(volume, corners, sill, head, "door", storeyId, elementId, ref first);
        }

        /// <summary>墙轴线上 [from, to] 这一段、标高 za~zb 的体块。</summary>
        private static void AddWallSegment(BuildingVolume volume, WallModel wall, double from, double to,
            double za, double zb, ref bool first)
        {
            AddWallSegment(volume, wall, from, to, za, zb, wall.StoreyId, "wall", ref first, 0d, wall.Id);
        }

        /// <summary>
        /// 同上，但可以指定构件种类与厚度（门窗构件用）：
        /// <paramref name="thickness"/> = 0 表示用墙厚；&gt; 0 表示以墙轴线为中心的这个厚度（玻璃就是这样一块薄板）。
        /// </summary>
        private static void AddWallSegment(BuildingVolume volume, WallModel wall, double from, double to,
            double za, double zb, string storeyId, string kind, ref bool first, double thickness = 0d,
            string elementId = null)
        {
            var length = Math.Sqrt((wall.X2 - wall.X1) * (wall.X2 - wall.X1) + (wall.Y2 - wall.Y1) * (wall.Y2 - wall.Y1));
            if (length < 1d || to - from < 1d || zb - za < 1d) return;
            var ux = (wall.X2 - wall.X1) / length;
            var uy = (wall.Y2 - wall.Y1) / length;
            var half = (thickness > 0.5d ? thickness : (wall.Thickness > 0.5d ? wall.Thickness : 200d)) / 2d;
            var nx = -uy * half;
            var ny = ux * half;
            var ax = wall.X1 + ux * from;
            var ay = wall.Y1 + uy * from;
            var bx = wall.X1 + ux * to;
            var by = wall.Y1 + uy * to;
            var bodyStart = WallReferenceGeometry.BodyPoint(wall, ax, ay);
            var bodyEnd = WallReferenceGeometry.BodyPoint(wall, bx, by);
            ax = bodyStart.X; ay = bodyStart.Y;
            bx = bodyEnd.X; by = bodyEnd.Y;
            var corners = new List<Point3DModel>
            {
                new Point3DModel(ax + nx, ay + ny, za),
                new Point3DModel(bx + nx, by + ny, za),
                new Point3DModel(bx - nx, by - ny, za),
                new Point3DModel(ax - nx, ay - ny, za)
            };
            AddPrism(volume, corners, za, zb, kind, storeyId, elementId ?? wall.Id, ref first);
        }

        private static void AddColumnBox(BuildingVolume volume, ColumnModel column, double z0, double z1, ref bool first)
        {
            if (z1 - z0 < 1d) return;
            var halfWidth = Math.Max(1d, column.Width) / 2d;
            var halfDepth = Math.Max(1d, column.Depth) / 2d;
            var corners = new List<Point3DModel>
            {
                new Point3DModel(column.X - halfWidth, column.Y - halfDepth, z0),
                new Point3DModel(column.X + halfWidth, column.Y - halfDepth, z0),
                new Point3DModel(column.X + halfWidth, column.Y + halfDepth, z0),
                new Point3DModel(column.X - halfWidth, column.Y + halfDepth, z0)
            };
            AddPrism(volume, corners, z0, z1, "column", column.StoreyId, column.Id, ref first);
        }

        private static void AddSlabPrism(BuildingVolume volume, SlabModel slab, ref bool first)
        {
            var outline = (slab.Outline ?? new List<PointModel>())
                .Where(p => p != null && IsFinite(p.X) && IsFinite(p.Y)).ToList();
            if (outline.Count < 3) return;
            var thickness = slab.Thickness > 0.5d ? slab.Thickness : 120d;
            var z1 = slab.TopElevation;
            var z0 = z1 - thickness;
            var corners = outline.Select(p => new Point3DModel(p.X, p.Y, z0)).ToList();
            AddPrism(volume, corners, z0, z1, "slab", slab.StoreyId, slab.Id, ref first);
        }

        /// <summary>把一个平面轮廓沿 Z 拉成棱柱：顶面 + 底面 + 每个侧面。</summary>
        private static void AddPrism(BuildingVolume volume, List<Point3DModel> baseCorners, double z0, double z1,
            string kind, string storeyId, string elementId, ref bool first)
        {
            if (baseCorners == null || baseCorners.Count < 3) return;
            // 轮廓按"逆时针"归一（面积正 = 逆时针，保证法线朝外）
            var corners = SignedArea(baseCorners) < 0d ? Enumerable.Reverse(baseCorners).ToList() : baseCorners;

            volume.Faces.Add(new VolumeFace
            {
                Kind = kind, StoreyId = storeyId, ElementId = elementId, NormalZ = 1d,
                Points = corners.Select(p => new Point3DModel(p.X, p.Y, z1)).ToList()
            });
            volume.Faces.Add(new VolumeFace
            {
                Kind = kind, StoreyId = storeyId, ElementId = elementId, NormalZ = -1d,
                Points = corners.Select(p => new Point3DModel(p.X, p.Y, z0)).Reverse().ToList()
            });
            for (var index = 0; index < corners.Count; index++)
            {
                var a = corners[index];
                var b = corners[(index + 1) % corners.Count];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-6d) continue;
                var nx = dy / length;
                var ny = -dx / length;
                volume.Faces.Add(new VolumeFace
                {
                    Kind = kind, StoreyId = storeyId, ElementId = elementId, NormalX = nx, NormalY = ny,
                    Points = new List<Point3DModel>
                    {
                        new Point3DModel(a.X, a.Y, z0), new Point3DModel(b.X, b.Y, z0),
                        new Point3DModel(b.X, b.Y, z1), new Point3DModel(a.X, a.Y, z1)
                    }
                });
            }

            foreach (var corner in corners)
            {
                var x = corner.X; var y = corner.Y;
                if (first) { volume.MinX = volume.MaxX = x; volume.MinY = volume.MaxY = y; volume.MinZ = z0; volume.MaxZ = z1; first = false; }
                else
                {
                    volume.MinX = Math.Min(volume.MinX, x); volume.MaxX = Math.Max(volume.MaxX, x);
                    volume.MinY = Math.Min(volume.MinY, y); volume.MaxY = Math.Max(volume.MaxY, y);
                    volume.MinZ = Math.Min(volume.MinZ, z0); volume.MaxZ = Math.Max(volume.MaxZ, z1);
                }
            }
        }

        /// <summary>轮廓的有向面积（>0 = 逆时针）。</summary>
        private static double SignedArea(List<Point3DModel> points)
        {
            double sum = 0d;
            for (var index = 0; index < points.Count; index++)
            {
                var current = points[index];
                var next = points[(index + 1) % points.Count];
                sum += current.X * next.Y - next.X * current.Y;
            }
            return sum / 2d;
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
