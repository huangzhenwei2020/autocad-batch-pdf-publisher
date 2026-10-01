using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class CadFloorRegistrationContext
    {
        public string ModelPath { get; set; }
        public StoreyModel Storey { get; set; }
        public List<StoreyModel> ReferenceStoreys { get; set; } = new List<StoreyModel>();
        public CadFloorRegistrationAlignment Alignment { get; set; }
        public PointModel DirectionPoint { get; set; }
        public PointModel RegionMin { get; set; }
        public PointModel RegionMax { get; set; }
        public List<PointModel> RegionPolygon { get; set; } = new List<PointModel>();
        public List<CadBuildingProbeEntity> WallCandidates { get; set; } = new List<CadBuildingProbeEntity>();

        public static CadFloorRegistrationContext Create(string modelPath, BuildingModelDocument model, string storeyId,
            PointModel modelBase, double unitScale)
        {
            var floors = model?.Storeys;
            if (floors == null || floors.Any(f => f == null || string.IsNullOrWhiteSpace(f.Id))
                || floors.Select(f => f.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != floors.Count)
                throw new InvalidDataException("楼层身份缺失或重复，请先在建筑模型的楼层设置中修正。");
            var floor = floors.FirstOrDefault(f => string.Equals(f.Id, storeyId, StringComparison.OrdinalIgnoreCase));
            if (floor == null || !Finite(floor.Elevation) || !Finite(floor.Height) || floor.Height <= 0)
                throw new InvalidDataException("请选择已设置标高和层高的楼层。");
            if (!string.IsNullOrWhiteSpace(floor.TemplateStoreyId))
                throw new InvalidDataException("该层引用标准层，请登记其来源楼层；需要独立登记时先在模型中转为独立层。");
            var references = floors.Where(f => string.Equals(f.TemplateStoreyId, floor.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (references.Any(f => !Finite(f.Elevation) || !Finite(f.Height) || f.Height <= 0))
                throw new InvalidDataException("标准层引用楼层的标高或层高无效。");
            if (string.IsNullOrWhiteSpace(modelPath) || !Valid(modelBase) || !Finite(unitScale) || unitScale <= 0)
                throw new InvalidDataException("模型路径、统一模型基点或源图单位无效。");
            return new CadFloorRegistrationContext { ModelPath = Path.GetFullPath(modelPath), Storey = Copy(floor),
                ReferenceStoreys = references.Select(Copy).ToList(),
                Alignment = new CadFloorRegistrationAlignment { StoreyId = floor.Id,
                    ModelBase = new PointModel(modelBase.X, modelBase.Y), MillimetresPerCadUnit = unitScale } };
        }

        public void SetSourceDatum(PointModel basePoint, PointModel directionPoint)
        {
            if (!Valid(basePoint) || !Valid(directionPoint) || Alignment == null)
                throw new InvalidDataException("CAD 对准基点或方向点无效。");
            var x = directionPoint.X - basePoint.X; var y = directionPoint.Y - basePoint.Y;
            var length = Math.Sqrt(x * x + y * y) * Alignment.MillimetresPerCadUnit;
            if (!Finite(length) || length < 1) throw new InvalidDataException("方向点必须与基点分开至少 1 mm。");
            Alignment.CadBase = new PointModel(basePoint.X, basePoint.Y);
            Alignment.RotationRadians = -Math.Atan2(y, x);
            DirectionPoint = new PointModel(directionPoint.X, directionPoint.Y);
            Alignment.ToModel(basePoint);
        }

        public void ValidateCapture()
        {
            if (Storey == null || Alignment == null || string.IsNullOrWhiteSpace(ModelPath)
                || !string.Equals(Storey.Id, Alignment.StoreyId, StringComparison.OrdinalIgnoreCase)
                || !Valid(RegionMin) || !Valid(RegionMax) || RegionMax.X <= RegionMin.X || RegionMax.Y <= RegionMin.Y
                || RegionPolygon == null || RegionPolygon.Count != 4 || RegionPolygon.Any(p => !Valid(p)))
                throw new InvalidDataException("楼层、定位或框选范围尚未完成。");
            SetSourceDatum(Alignment.CadBase, DirectionPoint);
        }
        private static StoreyModel Copy(StoreyModel f) => new StoreyModel { Id = f.Id, Name = f.Name,
            Height = f.Height, Elevation = f.Elevation, TemplateStoreyId = f.TemplateStoreyId };
        private static bool Valid(PointModel p) => p != null && Finite(p.X) && Finite(p.Y);
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    // Source points must already be converted from the selected UCS into WCS.
    // Each source floor has its own CAD datum but shares the model datum.
    public sealed class CadFloorRegistrationAlignment
    {
        public string StoreyId { get; set; }
        public PointModel CadBase { get; set; }
        public PointModel ModelBase { get; set; }
        public double RotationRadians { get; set; }
        public double MillimetresPerCadUnit { get; set; } = 1d;

        public PointModel ToModel(PointModel source)
        {
            Validate(source);
            var x = (source.X - CadBase.X) * MillimetresPerCadUnit;
            var y = (source.Y - CadBase.Y) * MillimetresPerCadUnit;
            var c = Math.Cos(RotationRadians); var s = Math.Sin(RotationRadians);
            return CheckedPoint(ModelBase.X + c * x - s * y, ModelBase.Y + s * x + c * y);
        }

        public PointModel ToCad(PointModel model)
        {
            Validate(model);
            var x = model.X - ModelBase.X; var y = model.Y - ModelBase.Y;
            var c = Math.Cos(RotationRadians); var s = Math.Sin(RotationRadians);
            return CheckedPoint(CadBase.X + (c * x + s * y) / MillimetresPerCadUnit,
                CadBase.Y + (-s * x + c * y) / MillimetresPerCadUnit);
        }

        public double OpeningBottomElevation(StoreyModel storey, double heightAboveSlab)
        {
            Validate(CadBase);
            if (storey == null || !string.Equals(StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase)
                || !Finite(storey.Elevation) || !Finite(heightAboveSlab) || heightAboveSlab < 0)
                throw new ArgumentException("登记楼层或窗底离地高度无效。");
            var elevation = storey.Elevation + heightAboveSlab;
            if (!Finite(elevation)) throw new ArgumentException("窗底标高溢出。");
            return elevation;
        }

        private void Validate(PointModel point)
        {
            if (string.IsNullOrWhiteSpace(StoreyId) || !Valid(CadBase) || !Valid(ModelBase) || !Valid(point)
                || !Finite(RotationRadians) || !Finite(MillimetresPerCadUnit) || MillimetresPerCadUnit <= 0)
                throw new ArgumentException("须确定登记楼层、CAD 对准基点、模型基点、方向和单位。");
        }
        private static PointModel CheckedPoint(double x, double y)
        {
            var point = new PointModel(x, y);
            if (!Valid(point)) throw new ArgumentException("登记坐标溢出。");
            return point;
        }
        private static bool Valid(PointModel point) { return point != null && Finite(point.X) && Finite(point.Y); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
