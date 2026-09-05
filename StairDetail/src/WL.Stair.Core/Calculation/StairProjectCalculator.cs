using System;
using System.Collections.Generic;
using System.Linq;
using WL.Stair.Core.Domain;
using WL.Stair.Core.Validation;

namespace WL.Stair.Core.Calculation
{
    public sealed class StairProjectCalculator
    {
        private readonly StairRuleSet _rules;
        private readonly StairProjectConstraintService _constraints = new StairProjectConstraintService();

        public StairProjectCalculator(StairRuleSet rules = null)
        {
            _rules = rules ?? new StairRuleSet();
        }

        public StairProjectCalculationOutcome Calculate(StairProjectDefinition project)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            _constraints.Normalize(project);

            var issues = new List<ValidationIssue>();
            ValidateConstruction(project.Construction, issues);
            ValidateIds(project, issues);

            var floorLookup = project.Floors
                .Where(floor => floor != null && !string.IsNullOrWhiteSpace(floor.Id))
                .GroupBy(floor => floor.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var storeyResults = new List<StairStoreyResult>();
            var elevations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var elevation = project.BaseElevation;

            for (var storeyIndex = 0; storeyIndex < project.Storeys.Count; storeyIndex++)
            {
                var storey = project.Storeys[storeyIndex];
                if (storey == null)
                {
                    issues.Add(Error("WL-PR-001", "Storeys", "楼层段不能为空。"));
                    continue;
                }

                if (storeyIndex > 0)
                {
                    var previousStorey = project.Storeys[storeyIndex - 1];
                    if (previousStorey != null
                        && !string.Equals(
                            previousStorey.UpperFloorId,
                            storey.LowerFloorId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(Error("WL-PR-033", storey.Id,
                            "相邻楼层段必须共享同一块上下楼板。"));
                    }
                }

                if (!floorLookup.ContainsKey(storey.LowerFloorId ?? string.Empty)
                    || !floorLookup.ContainsKey(storey.UpperFloorId ?? string.Empty))
                {
                    issues.Add(Error("WL-PR-002", storey.Id, "楼层段引用的楼板编号不存在。"));
                }

                RequirePositive(storey.Height, storey.Id + ".Height", "WL-PR-003", issues);
                ValidateIndependentStairwell(storey, issues);
                if (storey.Flights == null || storey.Flights.Count == 0)
                {
                    issues.Add(Error("WL-PR-004", storey.Id, "每个楼层段至少需要一跑梯段。"));
                    continue;
                }

                if (storey.Landings == null || storey.Landings.Count != storey.Flights.Count - 1)
                {
                    issues.Add(Error("WL-PR-005", storey.Id, "N 跑梯段必须配置 N-1 个休息平台。"));
                }

                var totalRisers = 0;
                foreach (var flight in storey.Flights)
                {
                    if (flight == null)
                    {
                        issues.Add(Error("WL-PR-006", storey.Id, "梯段不能为空。"));
                        continue;
                    }

                    if (flight.RiserCount < _rules.MinimumRisersPerFlight)
                    {
                        issues.Add(Error("WL-PR-007", flight.Id, "梯段踏步级数过少。"));
                    }
                    else if (flight.RiserCount < 3 || flight.RiserCount > 18)
                    {
                        issues.Add(Warning("WL-PR-103", flight.Id,
                            "按《民用建筑设计统一标准》GB 50352-2019 第6.8.5条校核，每个梯段应为3～18级。"));
                    }
                    RequirePositive(flight.TreadDepth, flight.Id + ".TreadDepth", "WL-PR-008", issues);
                    RequirePositive(flight.Width, flight.Id + ".Width", "WL-PR-009", issues);
                    totalRisers += Math.Max(0, flight.RiserCount);
                }

                if (totalRisers == 0 || storey.Height <= 0.0)
                {
                    continue;
                }

                var riserHeight = storey.Height / totalRisers;
                ValidateRiserHeight(storey, riserHeight, issues);
                var flightResults = storey.Flights
                    .Where(flight => flight != null)
                    .Select(flight => new StairProjectFlightResult(
                        flight.Id,
                        flight.RiserCount,
                        riserHeight,
                        flight.TreadDepth,
                        flight.Width))
                    .ToArray();

                if (!string.IsNullOrWhiteSpace(storey.LowerFloorId))
                {
                    elevations[storey.LowerFloorId] = elevation;
                }
                var upperElevation = elevation + storey.Height;
                if (!string.IsNullOrWhiteSpace(storey.UpperFloorId))
                {
                    elevations[storey.UpperFloorId] = upperElevation;
                }
                storeyResults.Add(new StairStoreyResult(
                    storey.Id,
                    elevation,
                    upperElevation,
                    riserHeight,
                    flightResults));
                elevation = upperElevation;
            }

            if (issues.Any(issue => issue.Severity == ValidationSeverity.Error))
            {
                return new StairProjectCalculationOutcome(null, issues);
            }

            return new StairProjectCalculationOutcome(
                new StairProjectCalculationResult(storeyResults, elevations),
                issues);
        }

        private void ValidateRiserHeight(
            StairStoreyDefinition storey,
            double riserHeight,
            ICollection<ValidationIssue> issues)
        {
            if (riserHeight < _rules.MinimumRiserHeight || riserHeight > _rules.MaximumRiserHeight)
            {
                issues.Add(Error("WL-PR-010", storey.Id, "该层按各梯段级数计算出的踏步高度超出生成范围。"));
            }
            else if (riserHeight > _rules.RecommendedMaximumRiserHeight)
            {
                issues.Add(Warning("WL-PR-101", storey.Id,
                    "按《民用建筑设计统一标准》GB 50352-2019 表6.8.10“其他建筑楼梯”校核，踏步高度应不大于175mm。"));
            }

            foreach (var flight in storey.Flights.Where(item => item != null))
            {
                if (flight.TreadDepth < _rules.RecommendedMinimumTreadDepth)
                {
                    issues.Add(Warning("WL-PR-102", flight.Id,
                        "按《民用建筑设计统一标准》GB 50352-2019 表6.8.10“其他建筑楼梯”校核，踏步宽度应不小于260mm。"));
                }
            }
        }

        private static void ValidateConstruction(
            StairConstructionDefaults construction,
            ICollection<ValidationIssue> issues)
        {
            if (construction == null)
            {
                issues.Add(Error("WL-PR-011", "Construction", "必须配置统一构造参数。"));
                return;
            }

            RequirePositive(construction.FlightSlabThickness, "FlightSlabThickness", "WL-PR-012", issues);
            RequirePositive(construction.StairwellWidth, "StairwellWidth", "WL-PR-022", issues);
            RequirePositive(construction.StairwellDepth, "StairwellDepth", "WL-PR-023", issues);
            RequirePositive(construction.LandingSlabThickness, "LandingSlabThickness", "WL-PR-013", issues);
            RequirePositive(construction.FloorSlabThickness, "FloorSlabThickness", "WL-PR-014", issues);
            ValidateBeam(construction.FloorBeam, "FloorBeam", issues);
            ValidateBeam(construction.LandingBeam, "LandingBeam", issues);
            RequirePositive(construction.SlabOverhang, "SlabOverhang", "WL-PR-020", issues);
            if (construction.Railing != null && construction.Railing.Enabled)
            {
                RequirePositive(construction.Railing.Height, "Railing.Height", "WL-PR-015", issues);
            }
            if (construction.Wall != null && construction.Wall.Enabled)
            {
                RequirePositive(construction.Wall.Thickness, "Wall.Thickness", "WL-PR-016", issues);
            }
            ValidateOpening(construction.Door, "Door", issues);
            ValidateOpening(construction.Window, "Window", issues);
        }

        private static void ValidateIndependentStairwell(
            StairStoreyDefinition storey,
            ICollection<ValidationIssue> issues)
        {
            if (storey == null || !storey.IndependentStairwellEnabled) return;
            RequirePositive(
                storey.StairwellDepthOverride,
                storey.Id + ".StairwellDepthOverride",
                "WL-PR-036",
                issues);
            if (storey.StairwellAlignment < StairwellAlignment.Left
                || storey.StairwellAlignment > StairwellAlignment.Right)
            {
                issues.Add(Error("WL-PR-037", storey.Id + ".StairwellAlignment",
                    "独立楼梯井只能选择左对齐、居中或右对齐。"));
            }
            if (double.IsNaN(storey.StairwellAxisOffset)
                || double.IsInfinity(storey.StairwellAxisOffset))
            {
                issues.Add(Error("WL-PR-038", storey.Id + ".StairwellAxisOffset",
                    "独立楼梯井轴线偏移必须是有限值。"));
            }
        }

        private static void ValidateBeam(BeamDefaults beam, string parameter, ICollection<ValidationIssue> issues)
        {
            if (beam == null)
            {
                issues.Add(Error("WL-PR-017", parameter, "梁统一参数不能为空。"));
                return;
            }
            RequirePositive(beam.Width, parameter + ".Width", "WL-PR-018", issues);
            RequirePositive(beam.Depth, parameter + ".Depth", "WL-PR-019", issues);
        }

        private static void ValidateOpening(
            OpeningDefaults opening,
            string parameter,
            ICollection<ValidationIssue> issues)
        {
            if (opening == null || !opening.Enabled)
            {
                return;
            }
            RequirePositive(opening.Width, parameter + ".Width", "WL-PR-020", issues);
            RequirePositive(opening.Height, parameter + ".Height", "WL-PR-021", issues);
        }

        private static void ValidateIds(StairProjectDefinition project, ICollection<ValidationIssue> issues)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var floor in project.Floors ?? new List<StairFloorDefinition>())
            {
                AddId(ids, floor == null ? null : floor.Id, "楼板", issues);
                if (floor != null)
                {
                    AddId(ids, floor.BeamId, "楼板梁", issues);
                    ValidateFloor(floor, issues);
                }
            }
            foreach (var storey in project.Storeys ?? new List<StairStoreyDefinition>())
            {
                AddId(ids, storey == null ? null : storey.Id, "楼层段", issues);
                if (storey == null) continue;
                foreach (var flight in storey.Flights ?? new List<StairFlightDefinition>())
                {
                    AddId(ids, flight == null ? null : flight.Id, "梯段", issues);
                }
                foreach (var landing in storey.Landings ?? new List<StairLandingDefinition>())
                {
                    AddId(ids, landing == null ? null : landing.Id, "休息平台", issues);
                    if (landing != null)
                    {
                        AddId(ids, landing.BeamId, "休息平台梁", issues);
                        ValidateLanding(landing, storey, issues);
                    }
                }
                if (storey.Flights != null && storey.Landings != null)
                {
                    for (var index = 0; index < storey.Landings.Count
                        && index + 1 < storey.Flights.Count; index++)
                    {
                        var landing = storey.Landings[index];
                        var incoming = storey.Flights[index];
                        var outgoing = storey.Flights[index + 1];
                        if (landing != null && incoming != null && outgoing != null
                            && (!string.Equals(landing.IncomingFlightId, incoming.Id, StringComparison.OrdinalIgnoreCase)
                                || !string.Equals(landing.OutgoingFlightId, outgoing.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            issues.Add(Error("WL-PR-034", landing.Id,
                                "休息平台必须按顺序连接前一跑和后一跑梯段。"));
                        }
                    }
                }
            }
        }

        private static void ValidateFloor(StairFloorDefinition floor, ICollection<ValidationIssue> issues)
        {
            RequirePositive(floor.PlatformWidth, floor.Id + ".PlatformWidth", "WL-PR-022", issues);
            ValidatePlatformType(floor.PlatformType, floor.Id, issues);
            if (floor.ProjectionDirection != -1 && floor.ProjectionDirection != 1)
            {
                issues.Add(Error("WL-PR-031", floor.Id, "楼板投影方向只能为左或右。"));
            }
            ValidateOptionalPositive(floor.SlabThicknessOverride, floor.Id + ".SlabThicknessOverride", issues);
            ValidateOptionalPositive(floor.BeamWidthOverride, floor.Id + ".BeamWidthOverride", issues);
            ValidateOptionalPositive(floor.BeamDepthOverride, floor.Id + ".BeamDepthOverride", issues);
            ValidateOppositeSupport(floor.OppositeSupportType, floor.Id, issues);
            ValidateOptionalPositive(floor.OppositeSlabThicknessOverride, floor.Id + ".OppositeSlabThicknessOverride", issues);
            ValidateOptionalPositive(floor.OppositeBeamWidthOverride, floor.Id + ".OppositeBeamWidthOverride", issues);
            ValidateOptionalPositive(floor.OppositeBeamDepthOverride, floor.Id + ".OppositeBeamDepthOverride", issues);
            ValidateOptionalPositive(floor.SlabOverhangOverride, floor.Id + ".SlabOverhangOverride", issues);
        }

        private static void ValidateLanding(
            StairLandingDefinition landing,
            StairStoreyDefinition storey,
            ICollection<ValidationIssue> issues)
        {
            RequirePositive(landing.PlatformWidth, landing.Id + ".PlatformWidth", "WL-PR-024", issues);
            ValidatePlatformType(landing.PlatformType, landing.Id, issues);
            if (landing.ProjectionDirection != -1 && landing.ProjectionDirection != 1)
            {
                issues.Add(Error("WL-PR-026", landing.Id, "休息平台投影方向只能为左或右。"));
            }
            var flightIds = new HashSet<string>(
                storey.Flights.Where(flight => flight != null).Select(flight => flight.Id),
                StringComparer.OrdinalIgnoreCase);
            if (!flightIds.Contains(landing.IncomingFlightId ?? string.Empty)
                || !flightIds.Contains(landing.OutgoingFlightId ?? string.Empty))
            {
                issues.Add(Error("WL-PR-027", landing.Id, "休息平台连接的梯段编号不存在。"));
            }
            ValidateOptionalPositive(landing.SlabThicknessOverride, landing.Id + ".SlabThicknessOverride", issues);
            ValidateOptionalPositive(landing.BeamWidthOverride, landing.Id + ".BeamWidthOverride", issues);
            ValidateOptionalPositive(landing.BeamDepthOverride, landing.Id + ".BeamDepthOverride", issues);
            ValidateOptionalPositive(landing.SlabOverhangOverride, landing.Id + ".SlabOverhangOverride", issues);
        }

        private static void ValidateOppositeSupport(
            OppositeSupportType supportType,
            string parameter,
            ICollection<ValidationIssue> issues)
        {
            if (supportType < OppositeSupportType.None
                || supportType > OppositeSupportType.BeamWithSlab)
                issues.Add(Error("WL-PR-035", parameter,
                    "对面支承只能为无、仅梁或梁加楼板。"));
        }

        private static void ValidatePlatformType(
            PlatformLayoutType platformType,
            string parameter,
            ICollection<ValidationIssue> issues)
        {
            if (platformType < PlatformLayoutType.Platform1
                || platformType > PlatformLayoutType.Platform3)
            {
                issues.Add(Error("WL-PR-032", parameter, "平台类型只能为平台1、平台2或平台3。"));
            }
        }

        private static void AddId(
            ISet<string> ids,
            string id,
            string componentName,
            ICollection<ValidationIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                issues.Add(Error("WL-PR-028", componentName, componentName + "必须有编号。"));
            }
            else if (!ids.Add(id))
            {
                issues.Add(Error("WL-PR-029", id, "构件编号必须在项目内唯一。"));
            }
        }

        private static void ValidateOptionalPositive(double? value, string parameter, ICollection<ValidationIssue> issues)
        {
            if (value.HasValue)
            {
                RequirePositive(value.Value, parameter, "WL-PR-030", issues);
            }
        }

        private static void RequirePositive(
            double value,
            string parameter,
            string code,
            ICollection<ValidationIssue> issues)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            {
                issues.Add(Error(code, parameter, "数值必须是大于零的有限值。"));
            }
        }

        private static ValidationIssue Error(string code, string parameter, string message)
        {
            return new ValidationIssue(code, ValidationSeverity.Error, parameter, message);
        }

        private static ValidationIssue Warning(string code, string parameter, string message)
        {
            return new ValidationIssue(code, ValidationSeverity.Warning, parameter, message);
        }
    }

    public sealed class StairProjectCalculationOutcome
    {
        public StairProjectCalculationOutcome(
            StairProjectCalculationResult result,
            IEnumerable<ValidationIssue> issues)
        {
            Result = result;
            Issues = issues.ToArray();
        }

        public StairProjectCalculationResult Result { get; }

        public IReadOnlyList<ValidationIssue> Issues { get; }

        public bool IsSuccess { get { return Result != null; } }
    }

    public sealed class StairProjectCalculationResult
    {
        public StairProjectCalculationResult(
            IEnumerable<StairStoreyResult> storeys,
            IDictionary<string, double> floorElevations)
        {
            Storeys = storeys.ToArray();
            FloorElevations = new Dictionary<string, double>(floorElevations, StringComparer.OrdinalIgnoreCase);
            TotalHeight = Storeys.Count == 0
                ? 0.0
                : Storeys[Storeys.Count - 1].UpperElevation - Storeys[0].LowerElevation;
        }

        public IReadOnlyList<StairStoreyResult> Storeys { get; }

        public IReadOnlyDictionary<string, double> FloorElevations { get; }

        public double TotalHeight { get; }
    }

    public sealed class StairStoreyResult
    {
        public StairStoreyResult(
            string id,
            double lowerElevation,
            double upperElevation,
            double riserHeight,
            IEnumerable<StairProjectFlightResult> flights)
        {
            Id = id;
            LowerElevation = lowerElevation;
            UpperElevation = upperElevation;
            RiserHeight = riserHeight;
            Flights = flights.ToArray();
            TotalRiserCount = Flights.Sum(flight => flight.RiserCount);
        }

        public string Id { get; }

        public double LowerElevation { get; }

        public double UpperElevation { get; }

        public double RiserHeight { get; }

        public int TotalRiserCount { get; }

        public IReadOnlyList<StairProjectFlightResult> Flights { get; }
    }

    public sealed class StairProjectFlightResult
    {
        public StairProjectFlightResult(
            string id,
            int riserCount,
            double riserHeight,
            double treadDepth,
            double width)
        {
            Id = id;
            RiserCount = riserCount;
            TreadCount = Math.Max(0, riserCount - 1);
            RiserHeight = riserHeight;
            TreadDepth = treadDepth;
            Width = width;
            HorizontalRun = TreadCount * treadDepth;
            VerticalRise = riserCount * riserHeight;
        }

        public string Id { get; }

        public int RiserCount { get; }

        public int TreadCount { get; }

        public double RiserHeight { get; }

        public double TreadDepth { get; }

        public double Width { get; }

        public double HorizontalRun { get; }

        public double VerticalRise { get; }
    }
}
