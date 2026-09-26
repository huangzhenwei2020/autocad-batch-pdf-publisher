using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>平面编辑的命中结果。</summary>
    public sealed class PlanHit
    {
        /// <summary>wall / opening / column / slab。</summary>
        public string Kind { get; set; }
        public string Id { get; set; }
        /// <summary>夹点序号：0 = 起点/中心，1 = 终点，-1 = 整体。</summary>
        public int Grip { get; set; } = -1;
        /// <summary>洞口专用：沿墙距离（拖动洞口时用）。</summary>
        public double Offset { get; set; }
    }

    /// <summary>捕捉结果。</summary>
    public sealed class SnapResult
    {
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>捕捉类型（端点/中点/正交/轴网/无）。</summary>
        public string Kind { get; set; } = "无";
        public bool Snapped { get { return Kind != "无"; } }
    }

    /// <summary>
    /// 平面草图的纯逻辑：命中测试、捕捉、沿墙定位、参数校验。
    ///
    /// 放在 Shared 里是为了**可单元测试**（不依赖 WinForms），
    /// 将来 CAD 侧的"构件化绘制"（P1 精简版）也能复用同一套规则，两边行为一致。
    /// 单位：毫米。
    /// </summary>
    public static class PlanEditing
    {
        public const string SnapEndpoint = "端点";
        public const string SnapMidpoint = "中点";
        public const string SnapOrthogonal = "正交";
        public const string SnapAxis = "轴网";
        public const string SnapNone = "无";

        /// <summary>点到线段的距离。</summary>
        public static double DistanceToSegment(double px, double py, double x1, double y1, double x2, double y2)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1e-9d) return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
            var t = ((px - x1) * dx + (py - y1) * dy) / lengthSquared;
            t = Math.Max(0d, Math.Min(1d, t));
            var cx = x1 + t * dx;
            var cy = y1 + t * dy;
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }

        /// <summary>点在线段上的参数位置（0..1，超出范围会被夹住）。</summary>
        public static double ParameterOnSegment(double px, double py, double x1, double y1, double x2, double y2)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1e-9d) return 0d;
            var t = ((px - x1) * dx + (py - y1) * dy) / lengthSquared;
            return Math.Max(0d, Math.Min(1d, t));
        }

        /// <summary>点到墙轴线的垂足距离（沿墙轴线到起点的距离，mm）。</summary>
        public static double ProjectOnWall(WallModel wall, double x, double y)
        {
            if (wall == null) return 0d;
            var dx = wall.X2 - wall.X1;
            var dy = wall.Y2 - wall.Y1;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9d) return 0d;
            return ((x - wall.X1) * dx + (y - wall.Y1) * dy) / length;
        }

        /// <summary>墙的水平方向（单位向量）。</summary>
        public static void WallDirection(WallModel wall, out double ux, out double uy)
        {
            var dx = wall.X2 - wall.X1;
            var dy = wall.Y2 - wall.Y1;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9d) { ux = 1d; uy = 0d; return; }
            ux = dx / length;
            uy = dy / length;
        }

        /// <summary>洞口在墙上的两端点（世界坐标）。</summary>
        public static void OpeningSpan(WallModel wall, OpeningModel opening, out double ax, out double ay, out double bx, out double by)
        {
            WallDirection(wall, out var ux, out var uy);
            var half = (opening == null ? 0d : opening.Width) / 2d;
            var centre = opening == null ? 0d : opening.Offset;
            ax = wall.X1 + ux * (centre - half);
            ay = wall.Y1 + uy * (centre - half);
            bx = wall.X1 + ux * (centre + half);
            by = wall.Y1 + uy * (centre + half);
        }

        /// <summary>洞口中心的世界坐标。</summary>
        public static void OpeningCentre(WallModel wall, OpeningModel opening, out double x, out double y)
        {
            WallDirection(wall, out var ux, out var uy);
            var centre = opening == null ? 0d : opening.Offset;
            x = wall.X1 + ux * centre;
            y = wall.Y1 + uy * centre;
        }

        /// <summary>
        /// 命中测试：洞口 → 墙夹点 → 墙身 → 柱，按"越小越优先"的顺序。
        /// <paramref name="tolerance"/> 是屏幕像素换算到模型尺寸的容差。
        /// </summary>
        public static PlanHit HitTest(BuildingModelDocument model, string storeyId, double x, double y, double tolerance)
        {
            if (model == null) return null;
            var walls = (model.Walls ?? new List<WallModel>())
                .Where(w => w != null && Same(w.StoreyId, storeyId)).ToList();
            var openings = (model.Openings ?? new List<OpeningModel>()).Where(o => o != null).ToList();

            // 1) 洞口（在墙身上，优先级最高，否则永远选不到）
            foreach (var opening in openings)
            {
                var wall = walls.FirstOrDefault(w => Same(w.Id, opening.HostWallId));
                if (wall == null) continue;
                double ax, ay, bx, by;
                OpeningSpan(wall, opening, out ax, out ay, out bx, out by);
                if (DistanceToSegment(x, y, ax, ay, bx, by) <= tolerance)
                    return new PlanHit { Kind = "opening", Id = opening.Id, Offset = opening.Offset };
            }

            // 2) 墙的夹点（端点/中点）
            foreach (var wall in walls)
            {
                var midpointX = (wall.X1 + wall.X2) / 2d;
                var midpointY = (wall.Y1 + wall.Y2) / 2d;
                if (Near(x, y, wall.X1, wall.Y1, tolerance)) return new PlanHit { Kind = "wall", Id = wall.Id, Grip = 0 };
                if (Near(x, y, wall.X2, wall.Y2, tolerance)) return new PlanHit { Kind = "wall", Id = wall.Id, Grip = 1 };
                if (Near(x, y, midpointX, midpointY, tolerance)) return new PlanHit { Kind = "wall", Id = wall.Id, Grip = 2 };
            }

            // 3) 柱
            foreach (var column in (model.Columns ?? new List<ColumnModel>()).Where(c => c != null && Same(c.StoreyId, storeyId)))
            {
                var halfW = Math.Max(1d, column.Width) / 2d;
                var halfD = Math.Max(1d, column.Depth) / 2d;
                if (Math.Abs(x - column.X) <= halfW + tolerance && Math.Abs(y - column.Y) <= halfD + tolerance)
                    return new PlanHit { Kind = "column", Id = column.Id, Grip = 0 };
            }

            // 4) 墙身（按厚度的一半 + 容差判定）
            foreach (var wall in walls)
            {
                var half = Math.Max(1d, wall.Thickness) / 2d;
                if (DistanceToSegment(x, y, wall.X1, wall.Y1, wall.X2, wall.Y2) <= half + tolerance)
                    return new PlanHit { Kind = "wall", Id = wall.Id, Grip = -1 };
            }
            return null;
        }

        /// <summary>
        /// 捕捉：先端点/中点，再"从上一点正交"，最后轴网；都没命中就返回原始点。
        /// </summary>
        public static SnapResult Snap(BuildingModelDocument model, string storeyId, double x, double y, double tolerance,
            bool hasFrom, double fromX, double fromY, double axisStep)
        {
            var result = new SnapResult { X = x, Y = y, Kind = SnapNone };
            if (model != null)
            {
                var best = tolerance;
                foreach (var wall in (model.Walls ?? new List<WallModel>())
                    .Where(w => w != null && Same(w.StoreyId, storeyId)))
                {
                    Consider(x, y, wall.X1, wall.Y1, SnapEndpoint, tolerance, ref best, result);
                    Consider(x, y, wall.X2, wall.Y2, SnapEndpoint, tolerance, ref best, result);
                    Consider(x, y, (wall.X1 + wall.X2) / 2d, (wall.Y1 + wall.Y2) / 2d, SnapMidpoint, tolerance, ref best, result);
                }
                if (result.Snapped) return result;
            }

            if (hasFrom)
            {
                var dx = x - fromX;
                var dy = y - fromY;
                if (Math.Abs(dx) <= tolerance || Math.Abs(dy) <= tolerance)
                {
                    // 已经接近水平/竖向：贴齐
                    result.X = Math.Abs(dx) <= Math.Abs(dy) ? fromX : x;
                    result.Y = Math.Abs(dx) <= Math.Abs(dy) ? y : fromY;
                    result.Kind = SnapOrthogonal;
                    return result;
                }
                // 与上一点构成近似正交时，吸附到正交方向
                var angle = Math.Atan2(dy, dx) * 180d / Math.PI;
                var snappedAngle = Math.Round(angle / 90d) * 90d;
                if (Math.Abs(angle - snappedAngle) <= 8d)
                {
                    var radians = snappedAngle * Math.PI / 180d;
                    var length = Math.Sqrt(dx * dx + dy * dy);
                    result.X = fromX + Math.Cos(radians) * length;
                    result.Y = fromY + Math.Sin(radians) * length;
                    result.Kind = SnapOrthogonal;
                    return result;
                }
            }

            if (axisStep > 0.5d)
            {
                var gx = Math.Round(x / axisStep) * axisStep;
                var gy = Math.Round(y / axisStep) * axisStep;
                if (Near(x, y, gx, gy, tolerance))
                {
                    result.X = gx;
                    result.Y = gy;
                    result.Kind = SnapAxis;
                }
            }
            return result;
        }

        private static void Consider(double x, double y, double tx, double ty, string kind, double tolerance,
            ref double best, SnapResult result)
        {
            var distance = Math.Sqrt((x - tx) * (x - tx) + (y - ty) * (y - ty));
            if (distance > tolerance || distance >= best) return;
            best = distance;
            result.X = tx;
            result.Y = ty;
            result.Kind = kind;
        }

        /// <summary>新墙的合法性检查；返回错误文本，null 表示通过。</summary>
        public static string ValidateWall(WallModel wall)
        {
            if (wall == null) return "墙为空。";
            var length = Math.Sqrt((wall.X2 - wall.X1) * (wall.X2 - wall.X1) + (wall.Y2 - wall.Y1) * (wall.Y2 - wall.Y1));
            if (length < 10d) return "墙太短（不足 10mm），已忽略。";
            if (wall.Thickness <= 0d) return "墙厚必须大于 0。";
            return null;
        }

        /// <summary>洞口的合法性检查：必须完全落在墙上，且不与其他洞口重叠。</summary>
        public static string ValidateOpening(BuildingModelDocument model, WallModel wall, OpeningModel opening)
        {
            if (model == null || wall == null || opening == null) return "洞口或宿主墙为空。";
            var length = Math.Sqrt((wall.X2 - wall.X1) * (wall.X2 - wall.X1) + (wall.Y2 - wall.Y1) * (wall.Y2 - wall.Y1));
            var half = opening.Width / 2d;
            if (opening.Width <= 0d || opening.Height <= 0d) return "洞口宽高必须大于 0。";
            if (opening.Offset - half < -0.5d || opening.Offset + half > length + 0.5d)
                return "洞口超出了墙的范围（墙长 " + Math.Round(length) + "mm）。";
            if (opening.Sill < -0.5d) return "窗台高不能为负。";
            foreach (var other in (model.Openings ?? new List<OpeningModel>())
                .Where(o => o != null && Same(o.HostWallId, wall.Id) && !IsSameOpening(o, opening)))
            {
                if (Math.Abs(other.Offset - opening.Offset) < (other.Width + opening.Width) / 2d - 0.5d)
                    return "与同一道墙上的洞口“" + (other.Code ?? other.Id ?? "未命名") + "”重叠。";
            }
            return null;
        }

        /// <summary>
        /// 判断两个洞口是不是同一个。
        /// 注意：**不能只比 Id**——新建的洞口在落库前 Id 为空，只比 Id 会把所有无 Id 的洞口
        /// 都当成"自己"而漏掉重叠检查（这个坑在测试里被抓住过一次）。
        /// </summary>
        private static bool IsSameOpening(OpeningModel left, OpeningModel right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (string.IsNullOrWhiteSpace(left.Id) || string.IsNullOrWhiteSpace(right.Id)) return false;
            return Same(left.Id, right.Id);
        }

        /// <summary>给新洞口一个默认尺寸（窗/门）。</summary>
        public static OpeningModel CreateOpening(string kind, string wallId, double offset)
        {
            var isDoor = string.Equals(kind, "门", StringComparison.OrdinalIgnoreCase);
            return new OpeningModel
            {
                HostWallId = wallId,
                Kind = isDoor ? "门" : "窗",
                Code = isDoor ? "M0921" : "C1518",
                Offset = offset,
                Width = isDoor ? 900d : 1500d,
                Height = isDoor ? 2100d : 1800d,
                Sill = isDoor ? 0d : 900d
            };
        }

        /// <summary>墙长（mm）。</summary>
        public static double WallLength(WallModel wall)
        {
            if (wall == null) return 0d;
            return Math.Sqrt((wall.X2 - wall.X1) * (wall.X2 - wall.X1) + (wall.Y2 - wall.Y1) * (wall.Y2 - wall.Y1));
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Near(double x, double y, double tx, double ty, double tolerance)
        {
            return (x - tx) * (x - tx) + (y - ty) * (y - ty) <= tolerance * tolerance;
        }
    }
}
