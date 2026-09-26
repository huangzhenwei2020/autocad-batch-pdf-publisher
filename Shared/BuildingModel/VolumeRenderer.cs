using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>投影后面（已经算好屏幕坐标、深度与颜色）。</summary>
    public sealed class VolumeFace2D
    {
        /// <summary>屏幕坐标（视图平面，mm；由调用方再乘比例）。</summary>
        public List<PointModel> Points { get; set; } = new List<PointModel>();
        /// <summary>离视点的距离（越大越远）——按它从远到近画，就是最简单的消隐。</summary>
        public double Depth { get; set; }
        /// <summary>明暗（0.25~1，用来乘基色）。</summary>
        public double Shade { get; set; }
        /// <summary>构件种类与楼层，交给画布决定具体颜色。</summary>
        public string Kind { get; set; }
        public string StoreyId { get; set; }
        public bool IsUp { get; set; }
        /// <summary>
        /// 每条边画不画（长度与 Points 相同）。与别的**共面**面贴在一起的那条边不画，
        /// 这样一排墙段之间就不会出现"分格线"，看上去是一整片墙。
        /// </summary>
        /// <summary>
        /// 该画的边（每条两个点，屏幕坐标）。与别的**共面**面贴在一起的那一段不画，
        /// 这样一排墙段之间就不会出现"分格线"，看上去是一整片墙；被部分盖住的边只画剩下的一段。
        /// </summary>
        public List<List<PointModel>> Edges { get; set; } = new List<List<PointModel>>();
        /// <summary>来源面所在的平面（法线 + 平面到原点的距离）——用来判断"是不是共面"。</summary>
        public string PlaneKey { get; set; }
        /// <summary>同一个平面，**不分正反**（用来找"贴在一起"的内部面）。</summary>
        public string PlaneId { get; set; }
        /// <summary>只按法线（带正反）分的组 —— 找"平行的、在它前面的平面"用。</summary>
        public string NormalKey { get; set; }
        /// <summary>平面沿法线到原点的距离（带正负）。</summary>
        public double PlaneOffset { get; set; }
        /// <summary>法线在屏幕右／上方向的投影（把面沿法线平移时，投影里就平移这个量）。</summary>
        public double NormalU { get; set; }
        public double NormalV { get; set; }
        /// <summary>法线是不是朝着相机（false = 背面）。</summary>
        public bool Visible { get; set; }
    }

    /// <summary>
    /// 相机：绕建筑转的相机。
    /// 默认**平行投影（轴测）**——与建筑制图里的轴测图观感一致，也没有透视除零；
    /// 打开 <see cref="Perspective"/> 后按视场角做透视（更像"看模型"）。
    /// <see cref="ClipZ"/> &gt; 0 时做**水平剖切**：只保留该标高以下的部分，用来看内部（剖切轴测）。
    /// </summary>
    public sealed class VolumeCamera
    {
        /// <summary>方位角（度）：0 = 从南往北看，逆时针为正。</summary>
        public double AzimuthDegrees { get; set; } = 35d;
        /// <summary>仰角（度）：0 = 平视，90 = 俯视。</summary>
        public double ElevationDegrees { get; set; } = 28d;
        /// <summary>相对建筑对角线的缩放（1 = 正好铺满）。</summary>
        public double Zoom { get; set; } = 1d;
        /// <summary>透视投影（默认关 = 轴测）。</summary>
        public bool Perspective { get; set; }
        /// <summary>透视视场角（度）。</summary>
        public double FieldOfViewDegrees { get; set; } = 32d;
        /// <summary>水平剖切标高（mm）；0 = 不剖。</summary>
        public double ClipZ { get; set; }
        /// <summary>剖切时保留标高**以上**的部分（true）还是以下（false，默认：像看平面一样往下看）。</summary>
        public bool ClipKeepAbove { get; set; }

        public VolumeCamera Clone()
        {
            return new VolumeCamera
            {
                AzimuthDegrees = AzimuthDegrees, ElevationDegrees = ElevationDegrees, Zoom = Zoom,
                Perspective = Perspective, FieldOfViewDegrees = FieldOfViewDegrees,
                ClipZ = ClipZ, ClipKeepAbove = ClipKeepAbove
            };
        }
    }

    /// <summary>
    /// 体量 → 二维轴测图：**纯几何**（不碰 GDI+，可以单元测试）。
    ///
    /// 三步：
    /// 1. 背面剔除：法线背对相机的面直接不画（凸的长方体靠这一步就去掉了所有被挡住的面）；
    /// 2. 平行投影：把三维点投到视图平面（U 向右、V 向上），屏幕 Y 方向由调用方翻转；
    /// 3. 按深度从远到近排序 —— 画布按这个顺序填充，就是画家算法消隐。
    ///
    /// 明暗用一个固定的"太阳方向"，按面法线与阳光的夹角给 0.25~1 的系数，
    /// 这样不用光照引擎也能一眼看出体块关系。
    /// </summary>
    public static class VolumeRenderer
    {
        private static readonly double[] Sun = Normalize(-0.45d, -0.55d, 0.70d);

        /// <summary>投影整个体量；结果已按"从远到近"排好序。</summary>
        public static List<VolumeFace2D> Project(BuildingVolume volume, VolumeCamera camera)
        {
            var result = new List<VolumeFace2D>();
            if (volume == null || volume.Faces.Count == 0) return result;
            var view = new VolumeCamera
            {
                AzimuthDegrees = camera == null ? 35d : camera.AzimuthDegrees,
                ElevationDegrees = camera == null ? 28d : camera.ElevationDegrees,
                Zoom = camera == null ? 1d : Math.Max(0.05d, camera.Zoom),
                Perspective = camera != null && camera.Perspective,
                FieldOfViewDegrees = camera == null ? 32d : camera.FieldOfViewDegrees,
                ClipZ = camera == null ? 0d : camera.ClipZ,
                ClipKeepAbove = camera != null && camera.ClipKeepAbove
            };

            // 剖切：先在三维里把面裁到剖切面一侧（这样"剖开看内部"是几何上真的切掉）
            var faces = view.ClipZ > 0.5d ? ClipFaces(volume.Faces, view.ClipZ, view.ClipKeepAbove) : volume.Faces;

            var center = volume.Center;
            var radius = Math.Max(1d, volume.Diagonal);
            var eye = EyePosition(center, radius, view);
            var forward = Normalize(center.X - eye.X, center.Y - eye.Y, center.Z - eye.Z);
            // 屏幕右方向 = forward × up(0,0,1) = (fy, -fx, 0)；上方向 = right × forward
            var right = Normalize(forward[1], -forward[0], 0d);
            if (Math.Abs(right[0]) < 1e-9d && Math.Abs(right[1]) < 1e-9d) right = new[] { 1d, 0d, 0d };
            var up = new[]
            {
                right[1] * forward[2] - right[2] * forward[1],
                right[2] * forward[0] - right[0] * forward[2],
                right[0] * forward[1] - right[1] * forward[0]
            };
            var focal = 1d / Math.Max(0.05d, Math.Tan((view.FieldOfViewDegrees * Math.PI / 180d) / 2d));

            foreach (var face in faces)
            {
                if (face == null || face.Points == null || face.Points.Count < 3) continue;
                // 背面剔除：法线与视线同向（点积 < 0 表示背着相机）
                var towards = face.NormalX * forward[0] + face.NormalY * forward[1] + face.NormalZ * forward[2];
                var facingCamera = towards <= -1e-6d;

                var projected = new List<PointModel>(face.Points.Count);
                double depth = 0d;
                var projectedCount = 0;
                var behind = false;
                foreach (var point in face.Points)
                {
                    if (point == null) continue;
                    var dx = point.X - eye.X; var dy = point.Y - eye.Y; var dz = point.Z - eye.Z;
                    var u = dx * right[0] + dy * right[1] + dz * right[2];
                    var v = dx * up[0] + dy * up[1] + dz * up[2];
                    var d = dx * forward[0] + dy * forward[1] + dz * forward[2];
                    if (view.Perspective)
                    {
                        if (d < 1d) { behind = true; break; }          // 在相机后面/贴着相机：这个面不画
                        u = u * focal / d;
                        v = v * focal / d;
                    }
                    depth += d;
                    projectedCount++;
                    projected.Add(new PointModel(u, v));
                }
                if (behind || projected.Count < 3) continue;

                result.Add(new VolumeFace2D
                {
                    Points = projected,
                    Depth = projectedCount == 0 ? 0d : depth / projectedCount,
                    Shade = Shade(face),
                    Kind = face.Kind,
                    StoreyId = face.StoreyId,
                    IsUp = face.IsUp,
                    Visible = facingCamera,
                    PlaneKey = PlaneKeyOf(face),
                    PlaneId = PlaneIdOf(face),
                    NormalKey = NormalKeyOf(face.NormalX, face.NormalY, face.NormalZ),
                    PlaneOffset = PlaneOffsetOf(face),
                    NormalU = face.NormalX * right[0] + face.NormalY * right[1] + face.NormalZ * right[2],
                    NormalV = face.NormalX * up[0] + face.NormalY * up[1] + face.NormalZ * up[2]
                });
            }

            // 注意：判"内部贴合面"要用**全部**面（含背面）—— 贴在一起的两块，朝外的那面正好背着相机时，
            // 用它对面那块来盖住它才对；先用背面剔除会把判据本身剔掉。
            var kept = DropFacesBehindParallelPlanes(DropHiddenInterfaces(result))
                .Where(item => item.Visible).ToList();
            var ordered = kept.OrderByDescending(item => item.Depth).ToList();      // 远的先画
            SuppressCoplanarEdges(ordered);
            return ordered;
        }

        /// <summary>
        /// 丢掉"藏在墙/板里"的面：找**同一朝向、平行、且在它前面 ≤ <paramref name="maxGap"/> mm** 的那些面，
        /// 如果这个面的像素几乎全被那一层盖住 —— 说明它整片都埋在实体里（例：楼板侧边落在墙厚中间、
        /// 柱身贴在墙里），从外面根本看不见。
        ///
        /// 判据是"投影像素是否落在前面那层的投影里"：平行投影下，过同一个像素的视线与两层平面的交点
        /// 投到屏幕上就是同一个点，所以**不需要平移**，直接拿像素比就行。
        /// 允许 5% 的采样点在边缘露出去：切出来的墙块总会有几十毫米的边角露在外面，
        /// 为了这点边角留着整片"藏起来的面"，画板算法反而会把它错画到墙面上（三维里最难看的横带就是这么来的）。
        /// </summary>
        public static List<VolumeFace2D> DropFacesBehindParallelPlanes(List<VolumeFace2D> faces, double maxGap = 600d)
        {
            var kept = new List<VolumeFace2D>();
            if (faces == null) return kept;
            foreach (var line in faces.GroupBy(face => face.NormalKey ?? string.Empty, StringComparer.Ordinal))
            {
                var layers = line.GroupBy(face => Math.Round(face.PlaneOffset / 0.5d, MidpointRounding.AwayFromZero))
                    .OrderBy(layer => layer.Key)
                    .ToList();
                foreach (var layer in layers) kept.AddRange(layer);
                for (var index = 0; index < layers.Count; index++)
                {
                    foreach (var face in layers[index])
                    {
                        if (!face.Visible) continue;                       // 背面的本来就不画
                        for (var ahead = index + 1; ahead < layers.Count; ahead++)
                        {
                            var gap = (layers[ahead].Key - layers[index].Key) * 0.5d;
                            if (gap > maxGap + 0.5d) break;
                            if (gap <= 0.5d) continue;
                            var cover = layers[ahead].Where(item => item.Visible).ToList();
                            if (cover.Count == 0) continue;
                            if (!MostlyCovered(face, cover)) continue;
                            kept.Remove(face);
                            break;
                        }
                    }
                }
            }
            return kept;
        }

        /// <summary>采样点里有多大比例落在 cover 里（只采样内部点，顶点与边中点不参与）。</summary>
        private static bool MostlyCovered(VolumeFace2D face, List<VolumeFace2D> cover, double required = 1d)
        {
            var samples = InteriorSamples(face).ToList();
            if (samples.Count == 0) return false;
            var inside = 0;
            foreach (var sample in samples)
                foreach (var other in cover)
                    if (Inside(other.Points, sample.X, sample.Y)) { inside++; break; }
            return inside >= samples.Count * required;
        }

        /// <summary>内部采样点：形状中心 + 每个顶点朝中心 10%…90% 的点（避开边缘本身）。</summary>
        private static IEnumerable<PointModel> InteriorSamples(VolumeFace2D face)
        {
            var centerX = face.Points.Average(point => point.X);
            var centerY = face.Points.Average(point => point.Y);
            yield return new PointModel(centerX, centerY);
            for (var index = 0; index < face.Points.Count; index++)
            {
                var vertex = face.Points[index];
                for (var step = 1; step <= 9; step++)
                {
                    var ratio = step / 10d;
                    yield return new PointModel(vertex.X + (centerX - vertex.X) * ratio,
                        vertex.Y + (centerY - vertex.Y) * ratio);
                }
            }
        }

        /// <summary>
        /// 丢掉"内部贴合面"：体量是由一堆小方块拼出来的（墙被洞口切成几块、楼板压在墙下……），
        /// 块与块贴在一起的那片面谁也看不见 —— 它必然被**同一平面上别的面**整个盖住。
        /// 不丢的话，画面上会出现一堆莫名的分格线／朝内的面（三维里最影响观感的就是它）。
        ///
        /// 注意区分两种"盖住"：
        /// - 洞口把墙面切成几块：几块是**不重叠**地拼满一块区域 → 每块都有别人盖不到的地方 → 全部保留；
        /// - 内部贴合面：几块**互相重叠**（朝外的墙块与朝内的框料贴在一个平面上）→ 每一块都被别人盖满 → 全部丢掉。
        /// 所以判据必须是"被**其余的合起来**盖满"，而不是"被某一个更大的面盖满"（后者会把内部面留下来）。
        /// </summary>
        public static List<VolumeFace2D> DropHiddenInterfaces(List<VolumeFace2D> faces)
        {
            var kept = new List<VolumeFace2D>();
            if (faces == null) return kept;
            foreach (var group in faces.GroupBy(face => face.PlaneId ?? string.Empty, StringComparer.Ordinal))
            {
                var unique = ResolveCoincident(group.ToList());
                if (unique.Count < 2) { kept.AddRange(unique); continue; }
                foreach (var face in unique)
                {
                    var covered = true;
                    foreach (var sample in SamplePoints(face))
                    {
                        var inside = false;
                        foreach (var other in unique)
                        {
                            if (ReferenceEquals(other, face)) continue;
                            if (!Inside(other.Points, sample.X, sample.Y)) continue;
                            inside = true;
                            break;
                        }
                        if (!inside) { covered = false; break; }
                    }
                    if (!covered) kept.Add(face);
                }
            }
            return kept;
        }

        /// <summary>
        /// 处理**完全重合**的面（投影后一模一样）：
        /// - 一个有正有反 → 这是两块贴死的接触面（例：一层墙顶与二层墙底）→ 全丢，谁也看不见；
        /// - 同向重复（同一个面被生成两遍）→ 留一个就够。
        /// 不做这一步，"贴死"的两块会互相盖住，反而把两个都留下（还画出接缝线）。
        /// </summary>
        private static List<VolumeFace2D> ResolveCoincident(List<VolumeFace2D> faces)
        {
            var result = new List<VolumeFace2D>();
            var clusters = new List<List<VolumeFace2D>>();
            foreach (var face in faces)
            {
                var found = false;
                foreach (var cluster in clusters)
                    if (SamePolygon(cluster[0].Points, face.Points)) { cluster.Add(face); found = true; break; }
                if (found) continue;
                clusters.Add(new List<VolumeFace2D> { face });
            }
            foreach (var cluster in clusters)
            {
                var front = cluster.Any(item => item.Visible);
                var back = cluster.Any(item => !item.Visible);
                if (front && back) continue;                  // 正反贴死：内部接触面
                result.Add(cluster[0]);
            }
            return result;
        }

        private static bool SamePolygon(List<PointModel> left, List<PointModel> right)
        {
            const double tolerance = 0.05d;
            if (left == null || right == null || left.Count != right.Count) return false;
            foreach (var point in left)
            {
                var matched = false;
                foreach (var other in right)
                    if (Math.Abs(point.X - other.X) <= tolerance && Math.Abs(point.Y - other.Y) <= tolerance)
                    { matched = true; break; }
                if (!matched) return false;
            }
            return true;
        }

        /// <summary>判定覆盖用的采样点：顶点、边中点、以及每条边朝中心 1/4 与 3/4 处的点、形状中心。</summary>
        private static IEnumerable<PointModel> SamplePoints(VolumeFace2D face)
        {
            var centerX = face.Points.Average(point => point.X);
            var centerY = face.Points.Average(point => point.Y);
            yield return new PointModel(centerX, centerY);
            for (var index = 0; index < face.Points.Count; index++)
            {
                var a = face.Points[index];
                var b = face.Points[(index + 1) % face.Points.Count];
                yield return a;
                yield return new PointModel((a.X + b.X) / 2d, (a.Y + b.Y) / 2d);
                yield return new PointModel(a.X + (centerX - a.X) * 0.25d, a.Y + (centerY - a.Y) * 0.25d);
                yield return new PointModel(a.X + (centerX - a.X) * 0.75d, a.Y + (centerY - a.Y) * 0.75d);
            }
        }

        /// <summary>点在不在这块（凸/凹都行）多边形里；落在边上也算在里（容差 0.05mm）。</summary>
        private static bool Inside(List<PointModel> polygon, double x, double y)
        {
            const double tolerance = 0.05d;
            var inside = false;
            for (var index = 0; index < polygon.Count; index++)
            {
                var a = polygon[index];
                var b = polygon[(index + 1) % polygon.Count];
                if (a == null || b == null) continue;
                if (DistanceToSegment(a.X, a.Y, b.X, b.Y, x, y) <= tolerance) return true;
                if (a.Y > y != b.Y > y)
                {
                    var cross = a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    if (x < cross) inside = !inside;
                }
            }
            return inside;
        }

        private static double DistanceToSegment(double ax, double ay, double bx, double by, double x, double y)
        {
            var dx = bx - ax;
            var dy = by - ay;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1e-12d) return Math.Sqrt((x - ax) * (x - ax) + (y - ay) * (y - ay));
            var t = Math.Max(0d, Math.Min(1d, ((x - ax) * dx + (y - ay) * dy) / lengthSquared));
            var px = ax + dx * t;
            var py = ay + dy * t;
            return Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
        }

        /// <summary>多边形面积（有符号，逆时针为正）。</summary>
        public static double PolygonArea(List<PointModel> points)
        {
            if (points == null || points.Count < 3) return 0d;
            double sum = 0d;
            for (var index = 0; index < points.Count; index++)
            {
                var next = points[(index + 1) % points.Count];
                sum += points[index].X * next.Y - next.X * points[index].Y;
            }
            return sum / 2d;
        }

        /// <summary>
        /// 把体量的面裁到"某标高以下（或以上）"：Sutherland–Hodgman 单平面裁剪。
        /// 保留哪一侧由 <paramref name="keepAbove"/> 决定；裁剪后仍是凸多边形，法线不变。
        /// </summary>
        public static List<VolumeFace> ClipFaces(IEnumerable<VolumeFace> faces, double clipZ, bool keepAbove = false)
        {
            var result = new List<VolumeFace>();
            if (faces == null) return result;
            foreach (var face in faces)
            {
                if (face == null || face.Points == null || face.Points.Count < 3) continue;
                var clipped = new List<Point3DModel>();
                for (var index = 0; index < face.Points.Count; index++)
                {
                    var current = face.Points[index];
                    var next = face.Points[(index + 1) % face.Points.Count];
                    if (current == null || next == null) continue;
                    var currentInside = keepAbove ? current.Z >= clipZ : current.Z <= clipZ;
                    var nextInside = keepAbove ? next.Z >= clipZ : next.Z <= clipZ;
                    if (currentInside) clipped.Add(current);
                    if (currentInside == nextInside) continue;
                    // 与剖切面相交：按 Z 插值求交点（只裁水平面，所以只动 Z）
                    var span = next.Z - current.Z;
                    var ratio = Math.Abs(span) < 1e-9d ? 0d : (clipZ - current.Z) / span;
                    clipped.Add(new Point3DModel(
                        current.X + (next.X - current.X) * ratio,
                        current.Y + (next.Y - current.Y) * ratio,
                        clipZ));
                }
                if (clipped.Count < 3) continue;
                result.Add(new VolumeFace
                {
                    Kind = face.Kind, StoreyId = face.StoreyId,
                    NormalX = face.NormalX, NormalY = face.NormalY, NormalZ = face.NormalZ,
                    Points = clipped
                });
            }
            return result;
        }

        /// <summary>
        /// 与别的**共面**面贴在一起的那段边不画：一排墙段之间就不会出现"分格线"，
        /// 看上去是一整片墙。判据：同平面（法线 + 到原点距离一致），把对面落在同一条直线上的
        /// 边投影成参数区间求差，只画没被盖住的那几段（所以"被盖住一半"的边也能正确处理）。
        /// </summary>
        private static void SuppressCoplanarEdges(List<VolumeFace2D> faces)
        {
            var byPlane = new Dictionary<string, List<VolumeFace2D>>(StringComparer.Ordinal);
            foreach (var face in faces)
            {
                if (string.IsNullOrEmpty(face.PlaneKey)) continue;
                if (!byPlane.TryGetValue(face.PlaneKey, out var list))
                    byPlane[face.PlaneKey] = list = new List<VolumeFace2D>();
                list.Add(face);
            }

            foreach (var face in faces)
            {
                face.Edges = new List<List<PointModel>>();
                var neighbours = face.PlaneKey != null && byPlane.TryGetValue(face.PlaneKey, out var same)
                    ? same.Where(other => !ReferenceEquals(other, face)).ToList()
                    : new List<VolumeFace2D>();
                for (var index = 0; index < face.Points.Count; index++)
                {
                    var a = face.Points[index];
                    var b = face.Points[(index + 1) % face.Points.Count];
                    if (a == null || b == null) continue;
                    var covered = new List<double[]>();
                    foreach (var other in neighbours) CollectCoverage(other, a, b, covered);
                    EmitUncovered(face.Edges, a, b, covered);
                }
            }
        }

        /// <summary>收集 <paramref name="other"/> 里与 a→b 共线且重叠的那些边（参数区间 0~1）。</summary>
        private static void CollectCoverage(VolumeFace2D other, PointModel a, PointModel b, List<double[]> covered)
        {
            const double tolerance = 0.35d;
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1e-9d) return;
            var length = Math.Sqrt(lengthSquared);
            for (var index = 0; index < other.Points.Count; index++)
            {
                var p = other.Points[index];
                var q = other.Points[(index + 1) % other.Points.Count];
                if (p == null || q == null) continue;
                // 两个端点都要落在这条直线上（点到直线的距离在容差内），否则不算共线
                if (Math.Abs((p.X - a.X) * dy - (p.Y - a.Y) * dx) / length > tolerance) continue;
                if (Math.Abs((q.X - a.X) * dy - (q.Y - a.Y) * dx) / length > tolerance) continue;
                var t0 = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared;
                var t1 = ((q.X - a.X) * dx + (q.Y - a.Y) * dy) / lengthSquared;
                var low = Math.Max(0d, Math.Min(t0, t1));
                var high = Math.Min(1d, Math.Max(t0, t1));
                if (high - low > 1e-9d) covered.Add(new[] { low, high });
            }
        }

        /// <summary>边 a→b 减去被盖住的区间，剩下的逐段放进 <paramref name="edges"/>。</summary>
        private static void EmitUncovered(List<List<PointModel>> edges, PointModel a, PointModel b, List<double[]> covered)
        {
            covered.Sort((left, right) => left[0].CompareTo(right[0]));
            var cursor = 0d;
            foreach (var span in covered)
            {
                if (span[0] > cursor + 1e-9d) AddSegment(edges, a, b, cursor, span[0]);
                cursor = Math.Max(cursor, span[1]);
            }
            if (cursor < 1d - 1e-9d) AddSegment(edges, a, b, cursor, 1d);
        }

        private static void AddSegment(List<List<PointModel>> edges, PointModel a, PointModel b, double from, double to)
        {
            edges.Add(new List<PointModel>
            {
                new PointModel(a.X + (b.X - a.X) * from, a.Y + (b.Y - a.Y) * from),
                new PointModel(a.X + (b.X - a.X) * to, a.Y + (b.Y - a.Y) * to)
            });
        }

        /// <summary>面的平面标识：法线（三位小数）+ 平面到原点的距离（0.5mm 一档）。带正反。</summary>
        private static string PlaneKeyOf(VolumeFace face)
        {
            return NormalKeyOf(face.NormalX, face.NormalY, face.NormalZ) + "@" + BucketOf(PlaneOffsetOf(face));
        }

        /// <summary>平面沿法线到原点的距离。</summary>
        private static double PlaneOffsetOf(VolumeFace face)
        {
            var point = face.Points == null || face.Points.Count == 0 ? null : face.Points[0];
            return point == null ? 0d : face.NormalX * point.X + face.NormalY * point.Y + face.NormalZ * point.Z;
        }

        /// <summary>
        /// 平面标识：**不分正反** —— 法线统一取"第一个非零分量为正"的那一侧，
        /// 这样面对面贴着的两块（法线正好相反）会得到同一个号。
        /// </summary>
        private static string PlaneIdOf(VolumeFace face)
        {
            var point = face.Points == null || face.Points.Count == 0 ? null : face.Points[0];
            if (point == null) return string.Empty;
            var sign = 1d;
            if (face.NormalX < -1e-9d) sign = -1d;
            else if (Math.Abs(face.NormalX) <= 1e-9d)
            {
                if (face.NormalY < -1e-9d) sign = -1d;
                else if (Math.Abs(face.NormalY) <= 1e-9d && face.NormalZ < -1e-9d) sign = -1d;
            }
            var offset = (face.NormalX * point.X + face.NormalY * point.Y + face.NormalZ * point.Z) * sign;
            return NormalKeyOf(face.NormalX * sign, face.NormalY * sign, face.NormalZ * sign) + "@" + BucketOf(offset);
        }

        private static string NormalKeyOf(double normalX, double normalY, double normalZ)
        {
            return Round(normalX) + "," + Round(normalY) + "," + Round(normalZ);
        }

        private static string BucketOf(double offset)
        {
            return Math.Round(offset / 0.5d, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        }

        private static double Round(double value)
        {
            return Math.Round(value, 3) + 0d;      // +0d 是为了把 -0 归一成 0（否则 "-0" 与 "0" 会分成两个平面）
        }

        /// <summary>相机位置（平行投影里只影响投影方向，但保留距离便于以后换透视）。</summary>
        public static Point3DModel EyePosition(Point3DModel center, double radius, VolumeCamera camera)
        {
            var azimuth = (camera == null ? 35d : camera.AzimuthDegrees) * Math.PI / 180d;
            var elevation = (camera == null ? 28d : camera.ElevationDegrees) * Math.PI / 180d;
            var distance = Math.Max(1d, radius) * 2d;
            return new Point3DModel(
                center.X + distance * Math.Cos(elevation) * Math.Sin(azimuth),
                center.Y - distance * Math.Cos(elevation) * Math.Cos(azimuth),
                center.Z + distance * Math.Sin(elevation));
        }

        /// <summary>面的明暗系数（0.25~1）。</summary>
        public static double Shade(VolumeFace face)
        {
            if (face == null) return 1d;
            var dot = face.NormalX * Sun[0] + face.NormalY * Sun[1] + face.NormalZ * Sun[2];
            return 0.25d + 0.75d * Math.Max(0d, dot);
        }

        /// <summary>把投影结果整体平移/缩放到目标尺寸（返回：缩放比例与偏移，画布照此换算屏幕）。</summary>
        public static void FitToView(List<VolumeFace2D> faces, double viewWidth, double viewHeight,
            out double scale, out double offsetX, out double offsetY)
        {
            scale = 1d; offsetX = 0d; offsetY = 0d;
            if (faces == null || faces.Count == 0 || viewWidth <= 0d || viewHeight <= 0d) return;
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var face in faces)
                foreach (var point in face.Points)
                {
                    if (point == null) continue;
                    minU = Math.Min(minU, point.X); maxU = Math.Max(maxU, point.X);
                    minV = Math.Min(minV, point.Y); maxV = Math.Max(maxV, point.Y);
                }
            if (minU > maxU || minV > maxV) return;
            var width = Math.Max(1d, maxU - minU);
            var height = Math.Max(1d, maxV - minV);
            var margin = 24d;
            scale = Math.Min((viewWidth - margin * 2d) / width, (viewHeight - margin * 2d) / height);
            if (!IsFinite(scale) || scale <= 0d) scale = 1d;
            offsetX = (viewWidth - width * scale) / 2d - minU * scale;
            offsetY = (viewHeight - height * scale) / 2d + maxV * scale;
        }

        private static double[] Normalize(double x, double y, double z)
        {
            var length = Math.Sqrt(x * x + y * y + z * z);
            if (length < 1e-12d) return new[] { 0d, 0d, 1d };
            return new[] { x / length, y / length, z / length };
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
