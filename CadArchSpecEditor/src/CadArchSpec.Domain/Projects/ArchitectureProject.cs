using System;
using System.Collections.Generic;
using CadArchSpec.Domain.Common;
using CadArchSpec.Domain.Standards;

namespace CadArchSpec.Domain.Projects
{
    public sealed class ArchitectureProject
    {
        public Guid ProjectId { get; set; }
        public int SchemaVersion { get; set; } = 1;
        public string ProjectName { get; set; } = string.Empty;
        public ProjectLocation Location { get; set; } = new ProjectLocation();
        public ProjectLifecycle Lifecycle { get; set; } = new ProjectLifecycle();
        public ProjectClassification Classification { get; set; } = new ProjectClassification();
        public PlanningData Planning { get; set; } = new PlanningData();
        public BuildingData Building { get; set; } = new BuildingData();
        public FireData Fire { get; set; } = new FireData();
        public AccessibilityData Accessibility { get; set; } = new AccessibilityData();
        public WaterproofData Waterproof { get; set; } = new WaterproofData();
        public EnergyData Energy { get; set; } = new EnergyData();
        public GreenBuildingData GreenBuilding { get; set; } = new GreenBuildingData();
        public List<BuildingUnit> BuildingUnits { get; set; } = new List<BuildingUnit>();
        public List<ApprovalDocument> ApprovalDocuments { get; set; } = new List<ApprovalDocument>();
        public List<StandardReference> Standards { get; set; } = new List<StandardReference>();
    }

    public sealed class ProjectLocation
    {
        public string CountryCode { get; set; } = "CN";
        public string ProvinceCode { get; set; } = string.Empty;
        public string ProvinceName { get; set; } = string.Empty;
        public string CityCode { get; set; } = string.Empty;
        public string CityName { get; set; } = string.Empty;
        public string DistrictName { get; set; } = string.Empty;
        public ProjectValue<string> Address { get; set; } = new ProjectValue<string>();
    }

    public sealed class ProjectLifecycle
    {
        public ProjectNature Nature { get; set; }
        public DesignStage DesignStage { get; set; }
        public ProjectValue<DateTime> SubmissionDate { get; set; } = new ProjectValue<DateTime>();
        public ProjectValue<string> PlanningApprovalNumber { get; set; } = new ProjectValue<string>();
        public ProjectValue<string> FireSubmissionCategory { get; set; } = new ProjectValue<string>();
    }

    public sealed class ProjectClassification
    {
        public ArchitectureBuildingType BuildingType { get; set; }
        public bool IsSpecialConstructionProject { get; set; }
        public bool RequiresFireDesignReview { get; set; }
        public bool HasCivilDefense { get; set; }
        public bool HasBasement { get; set; }
        public bool HasCurtainWall { get; set; }
        public bool HasElevator { get; set; }
        public bool RequiresGreenBuilding { get; set; }
        public bool RequiresPrefabrication { get; set; }
        public bool HasSpecialistDesign { get; set; }
        public bool IsHighRiseOrSpecial { get; set; }
    }

    public sealed class PlanningData
    {
        public ProjectValue<decimal> SiteAreaSquareMeters { get; set; } = new ProjectValue<decimal>();
        public ProjectValue<decimal> PlotRatio { get; set; } = new ProjectValue<decimal>();
        public ProjectValue<decimal> BuildingDensityPercent { get; set; } = new ProjectValue<decimal>();
        public ProjectValue<decimal> GreenRatePercent { get; set; } = new ProjectValue<decimal>();
        public ProjectValue<int> MotorParkingSpaces { get; set; } = new ProjectValue<int>();
        public ProjectValue<int> NonMotorParkingSpaces { get; set; } = new ProjectValue<int>();
    }

    public sealed class BuildingData
    {
        public ProjectValue<string> OwnerName { get; set; } = new ProjectValue<string>();
        public ProjectValue<string> DesignOrganization { get; set; } = new ProjectValue<string>();
        public ProjectValue<decimal> TotalFloorAreaSquareMeters { get; set; } = new ProjectValue<decimal>();
        public ProjectValue<decimal> AboveGroundAreaSquareMeters { get; set; } = new ProjectValue<decimal>();
        public ProjectValue<decimal> UndergroundAreaSquareMeters { get; set; } = new ProjectValue<decimal>();
        public ProjectValue<decimal> BuildingHeightMeters { get; set; } = new ProjectValue<decimal>();
        public ProjectValue<int> AboveGroundFloors { get; set; } = new ProjectValue<int>();
        public ProjectValue<int> UndergroundFloors { get; set; } = new ProjectValue<int>();
        public ProjectValue<int> DesignServiceLifeYears { get; set; } = new ProjectValue<int>();
        public ProjectValue<string> MainStructuralType { get; set; } = new ProjectValue<string>();
        public ProjectValue<string> SeismicFortificationIntensity { get; set; } = new ProjectValue<string>();
        public ProjectValue<decimal> RelativeZeroElevation { get; set; } = new ProjectValue<decimal>();
        public ProjectValue<decimal> AbsoluteZeroElevation { get; set; } = new ProjectValue<decimal>();
    }

    public sealed class FireData
    {
        public ProjectValue<string> FireClassification { get; set; } = new ProjectValue<string>();
        public ProjectValue<string> FireResistanceRating { get; set; } = new ProjectValue<string>();
        public ProjectValue<string> FireHazardCategory { get; set; } = new ProjectValue<string>();
    }

    public sealed class AccessibilityData
    {
        public ProjectValue<string> ApplicableStandard { get; set; } = new ProjectValue<string>();
        public ProjectValue<string> AccessibleRouteSummary { get; set; } = new ProjectValue<string>();
    }

    public sealed class WaterproofData
    {
        public ProjectValue<string> RoofGrade { get; set; } = new ProjectValue<string>();
        public ProjectValue<string> BasementGrade { get; set; } = new ProjectValue<string>();
    }

    public sealed class EnergyData
    {
        public ProjectValue<string> ClimateZone { get; set; } = new ProjectValue<string>();
        public ProjectValue<string> CalculationReportVersion { get; set; } = new ProjectValue<string>();
    }

    public sealed class GreenBuildingData
    {
        public ProjectValue<string> TargetRating { get; set; } = new ProjectValue<string>();
        public ProjectValue<string> ApplicableLocalStandard { get; set; } = new ProjectValue<string>();
    }

    public sealed class BuildingUnit
    {
        public Guid BuildingUnitId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Function { get; set; } = string.Empty;
        public ProjectValue<decimal> FloorAreaSquareMeters { get; set; } = new ProjectValue<decimal>();
        public ProjectValue<decimal> HeightMeters { get; set; } = new ProjectValue<decimal>();
        public ProjectValue<int> AboveGroundFloors { get; set; } = new ProjectValue<int>();
        public ProjectValue<int> UndergroundFloors { get; set; } = new ProjectValue<int>();
    }

    public sealed class ApprovalDocument
    {
        public Guid ApprovalDocumentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
        public DateTime? DocumentDate { get; set; }
        public string IssuingAuthority { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public bool IsApplicable { get; set; }
        public string Notes { get; set; } = string.Empty;
    }
}
