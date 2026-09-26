using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>一跑梯段：平面矩形 + 每条踏步线 + 起步/到达标高。</summary>
    public sealed class StairFlight
    {
        /// <summary>0 = 第一跑（从本层楼面起步），1 = 第二跑（从休息平台起步）。</summary>
        public int Index { get; set; }
        /// <summary>梯段平面矩形（沿梯段方向是 Length 边）。</summary>
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        /// <summary>这一跑的起步标高与到达标高。</summary>
        public double BaseElevation { get; set; }
        public double TopElevation { get; set; }
        /// <summary>踏步线（每条两个端点，画在平面上）。</summary>
        public List<PointModel[]> Treads { get; set; } = new List<PointModel[]>();
        /// <summary>起步点 → 到达点（梯段中线的两端），画上行/下行箭头用。</summary>
        public PointModel Start { get; set; }
        public PointModel End { get; set; }
        /// <summary>每级踏步的平面范围与顶标高：依次是第 1…N 级。</summary>
        public List<StairStep> Steps { get; set; } = new List<StairStep>();
    }

    /// <summary>一级踏步：平面矩形 + 顶标高（体量按它拉成一个个小方块）。</summary>
    public sealed class StairStep
    {
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        /// <summary>这一级踏面的标高。</summary>
        public double TopElevation { get; set; }
        /// <summary>第几级（从 1 开始，含两跑的连续编号）。</summary>
        public int Number { get; set; }
    }

    /// <summary>
    /// 楼梯的几何（纯计算、可单元测试）：把 <see cref="StairModel"/> 的参数展开成
    /// 两跑梯段、休息平台、踏步线与上下行路径的坐标。
    ///
    /// 平面约定（<see cref="StairModel.AlongX"/> = true，楼梯间是 X..X+Length × Y..Y+Width）：
    /// - 第一跑在南侧条带（Y..Y+梯段宽），从 X 起步朝 +X 上行；
    /// - 休息平台在远端（X+踏步总长 .. X+Length），占满整个楼梯间宽度；
    /// - 第二跑在北侧条带（Y+Width-梯段宽 .. Y+Width），从平台起步朝 -X 上行；
    /// - 两跑之间是**梯井**（宽 <see cref="StairModel.WellWidth"/>）。
    /// </summary>
    public sealed class StairGeometry
    {
        /// <summary>楼梯间平面矩形。</summary>
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        /// <summary>两跑（0 = 第一跑，1 = 第二跑）。</summary>
        public List<StairFlight> Flights { get; set; } = new List<StairFlight>();
        /// <summary>休息平台平面矩形与标高（板顶）。</summary>
        public double LandingX0 { get; set; }
        public double LandingY0 { get; set; }
        public double LandingX1 { get; set; }
        public double LandingY1 { get; set; }
        public double LandingElevation { get; set; }
        /// <summary>本层楼面标高与"上一层楼面"标高（第二跑到达处）。</summary>
        public double BaseElevation { get; set; }
        public double TopElevation { get; set; }
        /// <summary>踏步高（现算或显式给）。</summary>
        public double Riser { get; set; }
        /// <summary>每跑踏步数 / 总踏步数。</summary>
        public int StepsPerFlight { get; set; }
        public int TotalSteps { get { return StepsPerFlight * 2; } }
        /// <summary>上行路径（第一跑起步 → 平台 → 第二跑到达），画"上"箭头用；下行是它的反向。</summary>
        public List<PointModel> UpPath { get; set; } = new List<PointModel>();
        /// <summary>梯井平面矩形。</summary>
        public double WellX0 { get; set; }
        public double WellY0 { get; set; }
        public double WellX1 { get; set; }
        public double WellY1 { get; set; }
        /// <summary>扶手/栏板线（平面，沿梯井两侧 + 平台外沿）。</summary>
        public List<PointModel[]> Handrails { get; set; } = new List<PointModel[]>();
        /// <summary>平台板厚（mm）。</summary>
        public double LandingThickness { get; set; } = 120d;

        public bool AlongX { get; set; }

        /// <summary>参数是否够画（长度够放踏步与平台、宽度够放两跑）。</summary>
        public bool IsValid
        {
            get
            {
                return StepsPerFlight >= 1
                    && Going > 50d
                    && Riser > 20d
                    && FlightWidth > 200d
                    && TreadRun > 0d
                    && LandingDepth > 0d
                    && WellWidth >= 0d;
            }
        }

        /// <summary>踏步总长（一跑的踏步区长度）。</summary>
        public double TreadRun { get { return StepsPerFlight * Going; } }
        /// <summary>休息平台深（现算或显式给）。</summary>
        public double LandingDepth { get; set; }
        /// <summary>踏步宽（mm）。</summary>
        public double Going { get; set; }
        /// <summary>单跑梯段宽（mm）。</summary>
        public double FlightWidth { get; set; }
        /// <summary>梯井宽（mm）。</summary>
        public double WellWidth { get; set; }

        /// <summary>按模型与楼梯参数展开几何；参数不足时返回 null（调用方只提示、不抛）。</summary>
        public static StairGeometry Build(BuildingModelDocument model, StairModel stair)
        {
            if (stair == null) return null;
            var storey = model == null ? null : model.FindStorey(stair.StoreyId);
            var storeyHeight = storey != null && storey.Height > 0.5d ? storey.Height : 3000d;
            var baseElevation = storey == null ? 0d : storey.Elevation;

            var steps = Math.Max(1, stair.StepsPerFlight);
            var going = stair.Going > 50d ? stair.Going : 260d;
            var riser = stair.Riser > 20d ? stair.Riser : storeyHeight / (2d * steps);
            var flightWidth = stair.FlightWidth > 200d ? stair.FlightWidth : 1200d;
            var length = Math.Abs(stair.Length) > 1d ? Math.Abs(stair.Length) : 5400d;
            var width = Math.Abs(stair.Width) > 1d ? Math.Abs(stair.Width) : 2700d;
            var treadRun = steps * going;
            var landing = stair.LandingDepth > 1d ? stair.LandingDepth : length - treadRun;
            var well = Math.Max(0d, stair.WellWidth);

            var geometry = new StairGeometry
            {
                AlongX = stair.AlongX,
                // (X, Y) 是楼梯间左下角；Length 永远沿**梯段方向**，Width 永远沿**梯段宽度方向**
                X0 = stair.X, Y0 = stair.Y,
                X1 = stair.X + (stair.AlongX ? length : width),
                Y1 = stair.Y + (stair.AlongX ? width : length),
                BaseElevation = baseElevation,
                TopElevation = baseElevation + 2d * steps * riser,
                Riser = riser,
                StepsPerFlight = steps,
                Going = going,
                FlightWidth = flightWidth,
                WellWidth = well,
                LandingDepth = landing,
                LandingElevation = baseElevation + steps * riser
            };

            // 沿梯段方向（s 轴）与沿宽度方向（t 轴）的换算：AlongX 时 s→X、t→Y，否则反过来
            Func<double, double, PointModel> point = (s, t) => geometry.AlongX
                ? new PointModel(geometry.X0 + s, geometry.Y0 + t)
                : new PointModel(geometry.X0 + t, geometry.Y0 + s);
            var spanT = geometry.AlongX ? geometry.Y1 - geometry.Y0 : geometry.X1 - geometry.X0;
            var spanS = geometry.AlongX ? geometry.X1 - geometry.X0 : geometry.Y1 - geometry.Y0;

            if (!(treadRun > 0d) || !(landing > 0d) || spanT < flightWidth * 2d - 1d
                || spanS < treadRun + landing - 1d)
            {
                // 尺寸不够（踏步放不下 / 两跑放不下）：只给出楼梯间的范围，不上报梯段。
                // 调用方看 IsValid（或 Flights.Count == 0）就知道该提示"尺寸不够"。
                return geometry;
            }

            // 两条梯段条带的 t 范围：第一跑在南/西侧，第二跑在北/东侧，中间是梯井
            var firstT0 = 0d;
            var firstT1 = flightWidth;
            var secondT1 = spanT;
            var secondT0 = spanT - flightWidth;
            // 梯井取"中间剩下多少就多少"（保证两条梯段各占 flightWidth）
            var wellT0 = Math.Min(firstT1, (firstT1 + secondT0) / 2d - well / 2d);
            var wellT1 = Math.Max(secondT0, (firstT1 + secondT0) / 2d + well / 2d);

            // 第一跑：s 从 0 到 treadRun，第 k 级踏面 = k×踏步高，踏步线画在 k×踏步宽处
            var first = new StairFlight
            {
                Index = 0,
                BaseElevation = geometry.BaseElevation,
                TopElevation = geometry.BaseElevation + steps * riser
            };
            var second = new StairFlight
            {
                Index = 1,
                BaseElevation = geometry.LandingElevation,
                TopElevation = geometry.TopElevation
            };

            for (var step = 1; step <= steps; step++)
            {
                // 第一跑：第 step 级占 s ∈ [(step-1)×going, step×going]
                var s0 = (step - 1) * going;
                var s1 = step * going;
                first.Steps.Add(Step(point, s0, s1, firstT0, firstT1, geometry.BaseElevation + step * riser, step));
                if (step < steps)
                {
                    var line = new[] { point(s1, firstT0), point(s1, firstT1) };
                    first.Treads.Add(line);
                }

                // 第二跑：从平台往回跑，第 step 级占 s ∈ [treadRun - step×going, treadRun - (step-1)×going]
                var r0 = treadRun - step * going;
                var r1 = treadRun - (step - 1) * going;
                second.Steps.Add(Step(point, r0, r1, secondT0, secondT1,
                    geometry.LandingElevation + step * riser, steps + step));
                if (step < steps)
                    second.Treads.Add(new[] { point(r0, secondT0), point(r0, secondT1) });
            }

            first.Start = point(going / 2d, (firstT0 + firstT1) / 2d);
            first.End = point(treadRun - going / 2d, (firstT0 + firstT1) / 2d);
            second.Start = point(treadRun - going / 2d, (secondT0 + secondT1) / 2d);
            second.End = point(going / 2d, (secondT0 + secondT1) / 2d);

            // 梯段的平面矩形（沿方向是踏步区，宽度方向是条带）
            SetFlightRect(first, point, 0d, treadRun, firstT0, firstT1);
            SetFlightRect(second, point, 0d, treadRun, secondT0, secondT1);

            geometry.Flights.Add(first);
            geometry.Flights.Add(second);

            // 休息平台：远端整宽
            var landingA = point(treadRun, 0d);
            var landingB = point(spanS, spanT);
            geometry.LandingX0 = Math.Min(landingA.X, landingB.X);
            geometry.LandingY0 = Math.Min(landingA.Y, landingB.Y);
            geometry.LandingX1 = Math.Max(landingA.X, landingB.X);
            geometry.LandingY1 = Math.Max(landingA.Y, landingB.Y);

            // 梯井
            var wellA = point(0d, wellT0);
            var wellB = point(treadRun, wellT1);
            geometry.WellX0 = Math.Min(wellA.X, wellB.X);
            geometry.WellY0 = Math.Min(wellA.Y, wellB.Y);
            geometry.WellX1 = Math.Max(wellA.X, wellB.X);
            geometry.WellY1 = Math.Max(wellA.Y, wellB.Y);

            // 上行路径：第一跑起步 → 平台内 → 第二跑到达
            geometry.UpPath.Add(point(going / 2d, (firstT0 + firstT1) / 2d));
            geometry.UpPath.Add(point(treadRun, (firstT0 + firstT1) / 2d));
            geometry.UpPath.Add(point(treadRun, (secondT0 + secondT1) / 2d));
            geometry.UpPath.Add(point(going / 2d, (secondT0 + secondT1) / 2d));

            // 扶手线：梯井两侧各一条（沿踏步区），外加平台内沿
            geometry.Handrails.Add(new[] { point(0d, firstT1), point(treadRun, firstT1) });
            geometry.Handrails.Add(new[] { point(0d, secondT0), point(treadRun, secondT0) });
            geometry.Handrails.Add(new[] { point(treadRun, firstT1), point(treadRun, secondT0) });
            return geometry;
        }

        private static StairStep Step(Func<double, double, PointModel> point, double s0, double s1,
            double t0, double t1, double topElevation, int number)
        {
            var a = point(s0, t0);
            var b = point(s1, t1);
            return new StairStep
            {
                X0 = Math.Min(a.X, b.X), Y0 = Math.Min(a.Y, b.Y),
                X1 = Math.Max(a.X, b.X), Y1 = Math.Max(a.Y, b.Y),
                TopElevation = topElevation, Number = number
            };
        }

        private static void SetFlightRect(StairFlight flight, Func<double, double, PointModel> point,
            double s0, double s1, double t0, double t1)
        {
            var a = point(s0, t0);
            var b = point(s1, t1);
            flight.X0 = Math.Min(a.X, b.X);
            flight.Y0 = Math.Min(a.Y, b.Y);
            flight.X1 = Math.Max(a.X, b.X);
            flight.Y1 = Math.Max(a.Y, b.Y);
        }
    }
}
