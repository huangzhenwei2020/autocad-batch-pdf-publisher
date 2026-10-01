using System;
using System.IO;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    // Explicit user picks on the wall datum. Never inferred from an opening's bounds.
    public sealed class CadOpeningPlacement
    {
        public string HostSourceHandle { get; set; }
        public PointModel CadWallStart { get; set; }
        public PointModel CadWallEnd { get; set; }
        public PointModel CadOpeningStart { get; set; }
        public PointModel CadOpeningEnd { get; set; }
        public PointModel ModelWallStart { get; set; }
        public PointModel ModelWallEnd { get; set; }
        public PointModel ModelCenter { get; set; }
        public double DistanceFromWallStart { get; set; }
        public double PickedWidth { get; set; }
        public string Source { get; set; } = "用户选择宿主墙，读取原生定位线端点并拾取洞口两端，核对后确认";

        public static CadOpeningPlacement Create(CadFloorRegistrationContext floor, string hostHandle,
            PointModel wallStart, PointModel wallEnd, PointModel openingStart, PointModel openingEnd, double width)
        {
            if (floor == null) throw new InvalidDataException("请先登记楼层与基点。");
            floor.ValidateCapture();
            var hosts = floor.WallCandidates.Where(w => w != null && string.Equals(w.Handle, hostHandle, StringComparison.OrdinalIgnoreCase)).ToList();
            if (string.IsNullOrWhiteSpace(hostHandle) || hosts.Count != 1 || !CadBuildingProbeRules.IsWall(hosts[0].DxfName))
                throw new InvalidDataException("宿主必须是本次框选中的唯一一面天正墙。");
            if (!Finite(width) || width <= 0.5) throw new InvalidDataException("请先补充洞口宽度。");
            var a = floor.Alignment.ToModel(wallStart); var b = floor.Alignment.ToModel(wallEnd);
            var p = floor.Alignment.ToModel(openingStart); var q = floor.Alignment.ToModel(openingEnd);
            var dx = b.X - a.X; var dy = b.Y - a.Y; var length = Math.Sqrt(dx * dx + dy * dy);
            if (!Finite(length) || length < 1) throw new InvalidDataException("墙定位线长度至少为 1 mm。");
            dx /= length; dy /= length;
            var tp = (p.X-a.X)*dx + (p.Y-a.Y)*dy; var tq = (q.X-a.X)*dx + (q.Y-a.Y)*dy;
            var dp = Math.Abs((p.X-a.X)*dy - (p.Y-a.Y)*dx); var dq = Math.Abs((q.X-a.X)*dy - (q.Y-a.Y)*dx);
            if (dp > 2 || dq > 2) throw new InvalidDataException("洞口两端须拾取在宿主墙定位线上（误差不超过 2 mm）。");
            var low = Math.Min(tp,tq); var high = Math.Max(tp,tq); var span = high-low;
            if (low < -0.5 || high > length+0.5) throw new InvalidDataException("洞口超出宿主墙端部，请核对宿主及定位线。");
            if (!Finite(span) || span <= 0.5 || Math.Abs(span-width) > 2)
                throw new InvalidDataException("拾取跨度与洞口宽度不一致（允许 2 mm 误差），请重新拾取或核对尺寸。");
            var distance = (low+high)/2;
            return new CadOpeningPlacement { HostSourceHandle = hosts[0].Handle,
                CadWallStart = Copy(wallStart), CadWallEnd = Copy(wallEnd), CadOpeningStart = Copy(openingStart), CadOpeningEnd = Copy(openingEnd),
                ModelWallStart = a, ModelWallEnd = b, ModelCenter = new PointModel(a.X+distance*dx,a.Y+distance*dy),
                DistanceFromWallStart = distance, PickedWidth = span };
        }

        public CadOpeningPlacement Revalidate(CadFloorRegistrationContext floor, double width)
        {
            var result = Create(floor, HostSourceHandle, CadWallStart, CadWallEnd, CadOpeningStart, CadOpeningEnd, width);
            result.Source = Source;
            return result;
        }
        private static PointModel Copy(PointModel p) => new PointModel(p.X,p.Y);
        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
