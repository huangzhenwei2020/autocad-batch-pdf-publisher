using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using WL.Stair.CadShared.PlanCapture;

namespace WL.Stair.PlanProbe.Cad2022
{
    public sealed class ProbeCommands
    {
        [CommandMethod("WLSTAIRPLANPROBE", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void ProbeTianzhengStairPlanObject()
        {
            StairPlanObjectProbe.Execute(Application.DocumentManager.MdiActiveDocument);
        }
    }
}
