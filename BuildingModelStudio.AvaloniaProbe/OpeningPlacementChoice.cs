using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed record OpeningPlacementChoice(OpeningTypeModel Type,double Sill,double ThresholdHeight,double PlanOpenAngle,bool OpenIn3D)
{
    internal static OpeningPlacementChoice FromType(OpeningTypeModel type)=>new(OpeningConstruction.Copy(type),
        (type.Kind??"").Contains("门")?0:type.Sill,type.ThresholdHeight,type.PlanOpenAngle,type.DefaultOpenIn3D);
    internal OpeningModel CreateOpening(string wallId,double offset)
    {
        var opening=PlanEditing.CreateOpening(Type.Kind,wallId,offset);PlanEditing.ApplyType(opening,Type);
        opening.Sill=Sill;opening.ThresholdHeight=ThresholdHeight;opening.PlanOpenAngle=PlanOpenAngle;opening.OpenIn3D=OpenIn3D;
        return opening;
    }
}
