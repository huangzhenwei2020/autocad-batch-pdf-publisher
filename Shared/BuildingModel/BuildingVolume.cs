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

    /// <summary>
    /// 从建筑模型生成**三维体量**（P4 的第一块，纯几何、不依赖任何图形库）：
    ///
    /// - 墙 → 沿轴线拉出的长方体（厚 × 长 × 高，高取层高）；
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
            var onlyOne = !string.IsNullOrWhiteSpace(storeyId);
            var first = true;

            foreach (var wall in model.Walls ?? new List<WallModel>())
            {
                if (wall == null) continue;
                if (onlyOne && !Same(wall.StoreyId, storeyId)) continue;
                var z0 = model.BaseElevationOf(wall);
                var z1 = z0 + model.HeightOf(wall);
                AddWallWithOpenings(volume, wall, model, z0, z1, ref first);
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
            return volume;
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
        }

        /// <summary>墙轴线上 [from, to] 这一段、标高 za~zb 的体块。</summary>
        private static void AddWallSegment(BuildingVolume volume, WallModel wall, double from, double to,
            double za, double zb, ref bool first)
        {
            var length = Math.Sqrt((wall.X2 - wall.X1) * (wall.X2 - wall.X1) + (wall.Y2 - wall.Y1) * (wall.Y2 - wall.Y1));
            if (length < 1d || to - from < 1d || zb - za < 1d) return;
            var ux = (wall.X2 - wall.X1) / length;
            var uy = (wall.Y2 - wall.Y1) / length;
            var half = (wall.Thickness > 0.5d ? wall.Thickness : 200d) / 2d;
            var nx = -uy * half;
            var ny = ux * half;
            var ax = wall.X1 + ux * from;
            var ay = wall.Y1 + uy * from;
            var bx = wall.X1 + ux * to;
            var by = wall.Y1 + uy * to;
            var corners = new List<Point3DModel>
            {
                new Point3DModel(ax + nx, ay + ny, za),
                new Point3DModel(bx + nx, by + ny, za),
                new Point3DModel(bx - nx, by - ny, za),
                new Point3DModel(ax - nx, ay - ny, za)
            };
            AddPrism(volume, corners, za, zb, "wall", wall.StoreyId, ref first);
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
            AddPrism(volume, corners, z0, z1, "column", column.StoreyId, ref first);
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
            AddPrism(volume, corners, z0, z1, "slab", slab.StoreyId, ref first);
        }

        /// <summary>把一个平面轮廓沿 Z 拉成棱柱：顶面 + 底面 + 每个侧面。</summary>
        private static void AddPrism(BuildingVolume volume, List<Point3DModel> baseCorners, double z0, double z1,
            string kind, string storeyId, ref bool first)
        {
            if (baseCorners == null || baseCorners.Count < 3) return;
            // 轮廓按"逆时针"归一（面积正 = 逆时针，保证法线朝外）
            var corners = SignedArea(baseCorners) < 0d ? Enumerable.Reverse(baseCorners).ToList() : baseCorners;

            volume.Faces.Add(new VolumeFace
            {
                Kind = kind, StoreyId = storeyId, NormalZ = 1d,
                Points = corners.Select(p => new Point3DModel(p.X, p.Y, z1)).ToList()
            });
            volume.Faces.Add(new VolumeFace
            {
                Kind = kind, StoreyId = storeyId, NormalZ = -1d,
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
                    Kind = kind, StoreyId = storeyId, NormalX = nx, NormalY = ny,
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
