using System;
using System.Collections.Generic;
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
    }

    /// <summary>
    /// 相机：绕建筑转的**平行投影（轴测）**相机。方位角/仰角/距离三个参数就够用，
    /// 与建筑制图里的"轴测图"观感一致，也不需要处理透视除零。
    /// </summary>
    public sealed class VolumeCamera
    {
        /// <summary>方位角（度）：0 = 从南往北看，逆时针为正。</summary>
        public double AzimuthDegrees { get; set; } = 35d;
        /// <summary>仰角（度）：0 = 平视，90 = 俯视。</summary>
        public double ElevationDegrees { get; set; } = 28d;
        /// <summary>相对建筑对角线的缩放（1 = 正好铺满）。</summary>
        public double Zoom { get; set; } = 1d;

        public VolumeCamera Clone()
        {
            return new VolumeCamera { AzimuthDegrees = AzimuthDegrees, ElevationDegrees = ElevationDegrees, Zoom = Zoom };
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
                Zoom = camera == null ? 1d : Math.Max(0.05d, camera.Zoom)
            };

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

            foreach (var face in volume.Faces)
            {
                if (face == null || face.Points == null || face.Points.Count < 3) continue;
                // 背面剔除：法线与视线同向（点积 < 0 表示背着相机）
                var towards = face.NormalX * forward[0] + face.NormalY * forward[1] + face.NormalZ * forward[2];
                if (towards > -1e-6d) continue;

                var projected = new List<PointModel>(face.Points.Count);
                double depth = 0d;
                foreach (var point in face.Points)
                {
                    if (point == null) continue;
                    var dx = point.X - eye.X; var dy = point.Y - eye.Y; var dz = point.Z - eye.Z;
                    var u = dx * right[0] + dy * right[1] + dz * right[2];
                    var v = dx * up[0] + dy * up[1] + dz * up[2];
                    depth += dx * forward[0] + dy * forward[1] + dz * forward[2];
                    projected.Add(new PointModel(u, v));
                }
                if (projected.Count < 3) continue;

                result.Add(new VolumeFace2D
                {
                    Points = projected,
                    Depth = depth / projected.Count,
                    Shade = Shade(face),
                    Kind = face.Kind,
                    StoreyId = face.StoreyId,
                    IsUp = face.IsUp
                });
            }

            return result.OrderByDescending(item => item.Depth).ToList();      // 远的先画
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
