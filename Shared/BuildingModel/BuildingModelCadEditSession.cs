using System;
using System.Threading.Tasks;

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
        public async Task ImportCadFloorsAsync(CadFloorPlanRegistry registry,string requestId,Action<CadGenerationProgress> progress=null)
        {
            var revision=Revision;
            var snapshot=Clone(Model);var input=registry.Clone();
            var prepared=await Task.Run(()=>CadFloorModelGeneration.Build(snapshot,input,requestId,progress));
            if(Revision!=revision)throw new InvalidOperationException("生成期间模型已被修改，未覆盖新编辑，请重新生成。");
            Commit(prepared);
        }
    }
}
