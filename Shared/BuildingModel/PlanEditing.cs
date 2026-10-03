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
        /// <summary>捕捉类型（墙端点/交点/中点/垂足/墙身/无）。</summary>
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
        public const string SnapIntersection = "交点";
        public const string SnapPerpendicular = "垂足";
        public const string SnapNearest = "墙身";
        public const string SnapAxis = "轴线";
        public const string SnapAxisIntersection = "轴线交点";
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

        /// <summary>Finds an endpoint attached to the interior of a wall axis, excluding its two ends.</summary>
        public static bool TryProjectWallInterior(WallModel wall, double x, double y,
            double tolerance, out double fraction)
        {
            fraction = 0d;
            if (wall == null || tolerance < 0d || double.IsNaN(tolerance) || double.IsInfinity(tolerance)) return false;
            var dx = wall.X2 - wall.X1;
            var dy = wall.Y2 - wall.Y1;
            var squared = dx * dx + dy * dy;
            if (squared < 100d) return false;
            var raw = ((x - wall.X1) * dx + (y - wall.Y1) * dy) / squared;
            if (double.IsNaN(raw) || double.IsInfinity(raw)) return false;
            var margin = tolerance / Math.Sqrt(squared);
            if (raw <= margin || raw >= 1d - margin) return false;
            var px = wall.X1 + raw * dx;
            var py = wall.Y1 + raw * dy;
            var distance = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            if (distance > tolerance) return false;
            fraction = raw;
            return true;
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

            // 4) 楼梯：点在楼梯间矩形里就选中它（楼梯内部没有别的可点构件）
            foreach (var stair in (model.Stairs ?? new List<StairModel>()).Where(s => s != null && Same(s.StoreyId, storeyId)))
            {
                var length = Math.Abs(stair.Length) > 1d ? Math.Abs(stair.Length) : 5400d;
                var width = Math.Abs(stair.Width) > 1d ? Math.Abs(stair.Width) : 2700d;
                if (x >= stair.X - tolerance && x <= stair.X + length + tolerance
                    && y >= stair.Y - tolerance && y <= stair.Y + width + tolerance)
                    return new PlanHit { Kind = "stair", Id = stair.Id, Grip = -1 };
            }

            // 5) 屋面：点在檐口矩形里就选中它（屋面在平面图里是"上层投影"，只用来选中与编辑）
            foreach (var roof in (model.Roofs ?? new List<RoofModel>()).Where(r => r != null && Same(r.StoreyId, storeyId)))
            {
                var width = Math.Abs(roof.Width) > 1d ? Math.Abs(roof.Width) : 7440d;
                var depth = Math.Abs(roof.Depth) > 1d ? Math.Abs(roof.Depth) : 5640d;
                if (x >= roof.X - tolerance && x <= roof.X + width + tolerance
                    && y >= roof.Y - tolerance && y <= roof.Y + depth + tolerance)
                    return new PlanHit { Kind = "roof", Id = roof.Id, Grip = -1 };
            }

            // 4) 墙身（按厚度的一半 + 容差判定）
            foreach (var wall in walls)
            {
                var half = Math.Max(1d, wall.Thickness) / 2d;
                if (DistanceToSegment(x, y, wall.X1, wall.Y1, wall.X2, wall.Y2) <= half + tolerance)
                    return new PlanHit { Kind = "wall", Id = wall.Id, Grip = -1 };
            }

            // 5) 轴线（整栋通用；排在墙后面，墙上的轴线仍然优先选中墙）
            foreach (var axis in BuildingAxisLayout.Resolve(model,storeyId).Where(a => a != null && !a.Hidden && !a.Deleted))
            {
                var distance = axis.Vertical ? Math.Abs(x - axis.Position) : Math.Abs(y - axis.Position);
                if (distance <= tolerance) return new PlanHit { Kind = "axis", Id = axis.Id, Grip = -1 };
            }

            // 6) 房间：点房间名（轮廓形心附近）就选中它 —— 不按整个房间面积判定，
            //    否则房间会把墙、门窗的点击全吃掉。
            foreach (var room in (model.Rooms ?? new List<RoomModel>()).Where(r => r != null && Same(r.StoreyId, storeyId)))
            {
                var points = (room.Outline ?? new List<PointModel>()).Where(p => p != null).ToList();
                if (points.Count < 3) continue;
                var centerX = points.Average(p => p.X);
                var centerY = points.Average(p => p.Y);
                var radius = Math.Max(tolerance, 600d);
                if (Math.Abs(x - centerX) <= radius && Math.Abs(y - centerY) <= radius)
                    return new PlanHit { Kind = "room", Id = room.Id, Grip = -1 };
            }
            return null;
        }

        /// <summary>
        /// 轴号重排：竖轴按 X 从小到大编 1、2、3…，横轴按 Y 从小到大编 A、B、C…（超过 Z 后 AA、AB…）。
        /// 移动/新增/删除轴线后都该调一次，保证轴号永远与位置一致。
        /// </summary>
        public static void RenumberAxes(BuildingModelDocument model)
        {
            if (model == null || model.Axes == null) return;
            var vertical = model.Axes.Where(a => a != null && a.Vertical).OrderBy(a => a.Position).ToList();
            for (var index = 0; index < vertical.Count; index++) vertical[index].Name = (index + 1).ToString();
            var horizontal = model.Axes.Where(a => a != null && !a.Vertical).OrderBy(a => a.Position).ToList();
            for (var index = 0; index < horizontal.Count; index++) horizontal[index].Name = LetterName(index);
        }

        /// <summary>0 → A、1 → B…25 → Z、26 → AA。</summary>
        public static string LetterName(int index)
        {
            var name = string.Empty;
            var value = Math.Max(0, index);
            do
            {
                name = (char)('A' + value % 26) + name;
                value = value / 26 - 1;
            } while (value >= 0);
            return name;
        }

        /// <summary>房间/轴线之类的简单校验：房间至少 3 个点、轴号不能为空。</summary>
        public static string ValidateRoom(RoomModel room)
        {
            if (room == null) return "房间为空。";
            var points = (room.Outline ?? new List<PointModel>()).Where(p => p != null).ToList();
            if (points.Count < 3) return "房间轮廓至少要 3 个点。";
            if (room.AreaSquareMetres < 0.01d) return "房间面积太小（轮廓可能重合了）。";
            return null;
        }

        /// <summary>屋面校验：尺寸够大、坡度在 1°~60° 之间。</summary>
        public static string ValidateRoof(RoofModel roof)
        {
            if (roof == null) return "屋面为空。";
            var width = Math.Abs(roof.Width);
            var depth = Math.Abs(roof.Depth);
            if (width < 1000d || depth < 1000d)
                return "屋面尺寸太小（" + Math.Round(width) + "×" + Math.Round(depth) + "）：檐口矩形至少要 1m 见方。";
            if (roof.PitchDegrees <= 1d || roof.PitchDegrees >= 60d)
                return "坡度角 " + Math.Round(roof.PitchDegrees, 1) + "° 不合理（应在 1°~60° 之间）。";
            return null;
        }

        /// <summary>
        /// 楼梯校验：楼梯间要放得下两跑梯段（梯段宽 ×2 + 梯井 ≤ 净宽；踏步总长 + 平台 ≤ 净长），
        /// 踏步高要在 100~200mm 的常用区间里。
        /// </summary>
        public static string ValidateStair(BuildingModelDocument model, StairModel stair)
        {
            if (stair == null) return "楼梯为空。";
            var length = Math.Abs(stair.Length);
            var width = Math.Abs(stair.Width);
            if (length < 1000d) return "楼梯间净长太小（至少要放得下踏步区与休息平台）。";
            if (width < 1000d) return "楼梯间净宽太小（至少要放得下两跑梯段）。";
            var flightWidth = stair.FlightWidth > 200d ? stair.FlightWidth : 1200d;
            var well = Math.Max(0d, stair.WellWidth);
            if (flightWidth * 2d + well > width + 1d)
                return "楼梯间净宽 " + Math.Round(width) + " 放不下两跑 " + Math.Round(flightWidth)
                    + "（两跑 + 梯井 = " + Math.Round(flightWidth * 2d + well) + "）。";
            var steps = Math.Max(1, stair.StepsPerFlight);
            var going = stair.Going > 50d ? stair.Going : 260d;
            var treadRun = steps * going;
            var landing = stair.LandingDepth > 1d ? stair.LandingDepth : length - treadRun;
            if (landing < 600d)
                return "楼梯间净长不够：踏步总长 " + Math.Round(treadRun) + " + 平台 600 已超过净长 "
                    + Math.Round(length) + "（可减小踏步宽或减少踏步数）。";
            var riser = stair.Riser > 20d ? stair.Riser : (model == null ? 3000d : model.HeightOf(stair)) / (2d * steps);
            if (riser < 100d || riser > 200d)
                return "踏步高 " + Math.Round(riser) + " mm 超出常用范围（100~200）：请调踏步数或直接给踏步高。";
            return null;
        }

        /// <summary>
        /// 捕捉墙对象及轴网：端点、墙交点、中点、轴线交点、垂足、最近墙身、单轴线。
        /// 空白处返回原始坐标，不自动吸附到正交方向或虚拟网格。
        /// </summary>
        public static SnapResult Snap(BuildingModelDocument model, string storeyId, double x, double y, double tolerance,
            bool hasFrom, double fromX, double fromY, string excludedWallId = null,
            IReadOnlyList<AxisModel> resolvedAxes = null)
        {
            var result = new SnapResult { X = x, Y = y, Kind = SnapNone };
            var walls = (model?.Walls ?? new List<WallModel>())
                .Where(w => w != null && Same(w.StoreyId, storeyId)
                    && (excludedWallId == null || !Same(w.Id, excludedWallId)))
                .ToList();
            if (model != null)
            {
                var best = tolerance;
                foreach (var wall in walls)
                {
                    Consider(x, y, wall.X1, wall.Y1, SnapEndpoint, tolerance, ref best, result);
                    Consider(x, y, wall.X2, wall.Y2, SnapEndpoint, tolerance, ref best, result);
                }
                foreach(var column in model.Columns.Where(c=>Same(c.StoreyId,storeyId)&&c.Id!=excludedWallId))
                {
                    Consider(x,y,column.X,column.Y,"柱中心",tolerance,ref best,result);
                    foreach(var corner in StructuralGeometry.ColumnOutline(column))Consider(x,y,corner.X,corner.Y,SnapEndpoint,tolerance,ref best,result);
                }
                foreach(var beam in model.Beams.Where(b=>Same(b.StoreyId,storeyId)&&b.Id!=excludedWallId))
                {
                    Consider(x,y,beam.X1,beam.Y1,SnapEndpoint,tolerance,ref best,result);
                    Consider(x,y,beam.X2,beam.Y2,SnapEndpoint,tolerance,ref best,result);
                    Consider(x,y,(beam.X1+beam.X2)/2,(beam.Y1+beam.Y2)/2,SnapMidpoint,tolerance,ref best,result);
                }
                if (result.Snapped) return result;

                var near = walls.Where(w => DistanceToSegment(x, y, w.X1, w.Y1, w.X2, w.Y2) <= tolerance)
                    .ToList();
                best = tolerance;
                for (var i = 0; i < near.Count; i++)
                for (var j = i + 1; j < near.Count; j++)
                {
                    if (!TrySegmentIntersection(near[i], near[j], out var ix, out var iy)) continue;
                    Consider(x, y, ix, iy, SnapIntersection, tolerance, ref best, result);
                }
                if (result.Snapped) return result;

                best = tolerance;
                foreach (var wall in near)
                    Consider(x, y, (wall.X1 + wall.X2) / 2d, (wall.Y1 + wall.Y2) / 2d,
                        SnapMidpoint, tolerance, ref best, result);
                if (result.Snapped) return result;

                // A discrete axis intersection must not lose to a nearby continuous wall projection.
                var axes = (resolvedAxes ?? BuildingAxisLayout.Resolve(model,storeyId)).Where(a=>!a.Hidden&&!a.Deleted).ToList();
                var nearbyVertical = axes.Where(a => a.Vertical && Math.Abs(a.Position - x) <= tolerance).ToArray();
                var nearbyHorizontal = axes.Where(a => !a.Vertical && Math.Abs(a.Position - y) <= tolerance).ToArray();
                best = tolerance;
                foreach (var vertical in nearbyVertical)
                foreach (var horizontal in nearbyHorizontal)
                {
                    if (!OnAxis(vertical, horizontal.Position) || !OnAxis(horizontal, vertical.Position)) continue;
                    Consider(x, y, vertical.Position, horizontal.Position,
                        SnapAxisIntersection, tolerance, ref best, result);
                }
                if (result.Snapped) return result;

                if (hasFrom)
                {
                    best = tolerance;
                    foreach (var wall in near)
                    {
                        var dx = wall.X2 - wall.X1;
                        var dy = wall.Y2 - wall.Y1;
                        var lengthSquared = dx * dx + dy * dy;
                        if (lengthSquared < 1e-9d) continue;
                        var t = ((fromX - wall.X1) * dx + (fromY - wall.Y1) * dy) / lengthSquared;
                        if (t <= 0d || t >= 1d) continue;
                        Consider(x, y, wall.X1 + t * dx, wall.Y1 + t * dy,
                            SnapPerpendicular, tolerance, ref best, result);
                    }
                    if (result.Snapped) return result;
                }
                best = tolerance;
                foreach (var wall in near)
                {
                    var t = ParameterOnSegment(x, y, wall.X1, wall.Y1, wall.X2, wall.Y2);
                    Consider(x, y, wall.X1 + t * (wall.X2 - wall.X1),
                        wall.Y1 + t * (wall.Y2 - wall.Y1), SnapNearest, tolerance, ref best, result);
                }
                if (result.Snapped) return result;
                best = tolerance;
                foreach (var axis in nearbyVertical)
                    if (OnAxis(axis, y)) Consider(x, y, axis.Position, y,
                        SnapAxis, tolerance, ref best, result);
                foreach (var axis in nearbyHorizontal)
                    if (OnAxis(axis, x)) Consider(x, y, x, axis.Position,
                        SnapAxis, tolerance, ref best, result);
            }
            return result;
        }

        private static bool OnAxis(AxisModel axis, double along)
        {
            if (axis.ExtentStart == 0d && axis.ExtentEnd == 0d) return true;
            return along >= Math.Min(axis.ExtentStart, axis.ExtentEnd) - 0.5d
                && along <= Math.Max(axis.ExtentStart, axis.ExtentEnd) + 0.5d;
        }

        private static bool TrySegmentIntersection(WallModel a, WallModel b, out double x, out double y)
        {
            x = y = 0d;
            var ax = a.X2 - a.X1; var ay = a.Y2 - a.Y1;
            var bx = b.X2 - b.X1; var by = b.Y2 - b.Y1;
            var cross = ax * by - ay * bx;
            if (Math.Abs(cross) < 1e-9d) return false;
            var cx = b.X1 - a.X1; var cy = b.Y1 - a.Y1;
            var t = (cx * by - cy * bx) / cross;
            var u = (cx * ay - cy * ax) / cross;
            if (t < 0d || t > 1d || u < 0d || u > 1d) return false;
            x = a.X1 + t * ax;
            y = a.Y1 + t * ay;
            return true;
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
            if (!Enum.IsDefined(typeof(WallAxisPlacement), wall.AxisPlacement))
                return "墙定位轴线只能位于墙中、左面或右面。";
            if (wall.AxisOffset.HasValue && ((double.IsNaN(wall.AxisOffset.Value)
                || double.IsInfinity(wall.AxisOffset.Value))
                || Math.Abs(wall.AxisOffset.Value) > wall.Thickness / 2d + 0.001d))
                return "轴线左右侧墙厚不能为负。";
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

        /// <summary>
        /// 按门窗类型库里的一个类型设置洞口（编号/类型/宽/高/窗台）。
        /// 门一律落地（窗台 0）；其余按类型给的窗台高。
        /// </summary>
        public static void ApplyType(OpeningModel opening, OpeningTypeModel type)
        {
            if (opening == null || type == null) return;
            if (!string.IsNullOrWhiteSpace(type.Code)) opening.Code = type.Code.Trim();
            if (!string.IsNullOrWhiteSpace(type.Kind)) opening.Kind = type.Kind.Trim();
            if (type.Width > 0.5d) opening.Width = type.Width;
            if (type.Height > 0.5d) opening.Height = type.Height;
            var isDoor = (opening.Kind ?? string.Empty).IndexOf("门", StringComparison.Ordinal) >= 0;
            opening.Sill = isDoor ? 0d : Math.Max(0d, type.Sill);
        }

        /// <summary>在类型库里按编号找类型（忽略大小写与首尾空格）。</summary>
        public static OpeningTypeModel FindType(OpeningTypeLibraryDocument library, string code)
        {
            if (library == null || library.Types == null || string.IsNullOrWhiteSpace(code)) return null;
            var clean = code.Trim();
            foreach (var type in library.Types)
                if (type != null && string.Equals((type.Code ?? string.Empty).Trim(), clean, StringComparison.OrdinalIgnoreCase))
                    return type;
            return null;
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
