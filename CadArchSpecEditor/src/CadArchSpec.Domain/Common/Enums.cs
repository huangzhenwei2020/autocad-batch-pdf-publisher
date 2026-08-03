namespace CadArchSpec.Domain.Common
{
    public enum ValueState
    {
        Unknown,
        Pending,
        Confirmed,
        NotApplicable,
        ProvidedByOtherDiscipline,
        ProvidedBySpecialist,
        Overridden
    }

    public enum RequirementState
    {
        Required,
        Conditional,
        Optional,
        NotApplicable,
        ExternalDesign,
        Pending
    }

    public enum ReviewState
    {
        NotReviewed,
        InReview,
        Accepted,
        NeedsRevision
    }

    public enum ReviewSeverity
    {
        Blocker,
        Error,
        Warning,
        Info
    }

    public enum StandardStatus
    {
        Draft,
        Active,
        PartiallySuperseded,
        Superseded,
        Repealed,
        Unknown
    }

    public enum ProjectNature
    {
        NewConstruction,
        Renovation,
        Extension
    }

    public enum DesignStage
    {
        Scheme,
        PreliminaryDesign,
        ConstructionDocuments,
        RecordDrawing,
        Other
    }

    public enum ArchitectureBuildingType
    {
        Common,
        Residential,
        Office,
        Transportation,
        Education,
        Commercial,
        CultureAndSports,
        Medical,
        Industrial,
        Other
    }

    public enum DocumentNodeType
    {
        Heading,
        Paragraph,
        NumberedParagraph,
        BulletList,
        ProjectField,
        StandardCitation,
        TableReference,
        DrawingReference,
        Warning,
        Note,
        PageBreak,
        KeepTogether
    }

    public enum ProfessionalTableType
    {
        TechnicalEconomicIndicators,
        BuildingUnitIndicators,
        PhaseIndicators,
        BuildingAreaComposition,
        InteriorFinish,
        BuildingAssembly,
        WaterproofDesign,
        DoorWindowPerformance,
        ElevatorParameters,
        AccessibilityFacilities,
        BuildingSafetyMeasures,
        FireCompartmentSummary,
        EvacuationCalculation,
        EnergyEnvelope,
        GreenBuildingMeasures,
        SpecialistInterface,
        StandardReferences
    }
}
