using System;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed partial class BuildingModelEditSession
    {
        public bool TryImportCadFloors(CadFloorPlanRegistry registry, string requestId, out string error)
        {
            error = null;
            try { Commit(CadFloorModelGeneration.Build(Model, registry, requestId)); return true; }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
