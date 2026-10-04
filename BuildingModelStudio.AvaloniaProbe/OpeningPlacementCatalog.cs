using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed record OpeningPlacementEntry(OpeningTypeModel Type,string Name,string Source)
{
    internal bool IsDoor=>(Type.Kind??"").Contains("门");
    internal string SearchText=>$"{Type.Code} {Name} {Type.Kind} {Source}";
}

internal static class OpeningPlacementCatalog
{
    internal static List<OpeningPlacementEntry> Create(BuildingModelDocument model)
    {
        var entries=new List<OpeningPlacementEntry>();
        var codes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddProject(OpeningTypeModel type,string source){
            if(string.IsNullOrWhiteSpace(type.Code)||!codes.Add(type.Code.Trim()))return;
            var copy=OpeningConstruction.Copy(type);copy.Code=copy.Code.Trim();
            entries.Add(new(copy,string.IsNullOrWhiteSpace(copy.Remarks)?copy.ElevationType??copy.Kind:copy.Remarks,source));
        }
        foreach(var type in model.OpeningTypes)AddProject(type,"项目");
        foreach(var opening in model.Openings)AddProject(OpeningConstruction.Resolve(model,opening),"项目");
        var templateCodes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var type in model.OpeningTemplates.Where(t=>t!=null&&!string.IsNullOrWhiteSpace(t.Code))) {
            if(!templateCodes.Add(type.Code.Trim()))continue;
            var copy=OpeningConstruction.Copy(type);copy.Code=copy.Code.Trim();codes.Add(copy.Code);
            entries.Add(new(copy,string.IsNullOrWhiteSpace(copy.Remarks)?copy.ElevationType??copy.Kind:copy.Remarks,"模板"));
        }
        foreach(var entry in BuiltIns())if(codes.Add(entry.Type.Code))entries.Add(entry);
        return entries;
    }

    internal static List<OpeningPlacementEntry> BuiltIns()
    {
        var entries=new List<OpeningPlacementEntry>();
        Add("M0921","单扇平开门",900,2100,"普通门",new[]{900d},new[]{"左平开"});
        Add("M1825","双扇平开门",1800,2500,"普通门",new[]{900d,900},new[]{"左平开","右平开"});
        Add("M1221","子母门",1200,2100,"普通门",new[]{300d,900},new[]{"左平开","右平开"});
        Add("TLM0921","单扇推拉门",900,2100,"推拉门",new[]{900d},new[]{"右推拉"});
        Add("TLM1525","双扇推拉门",1500,2500,"推拉门",new[]{750d,750},new[]{"左推拉","右推拉"});
        Add("TLM3025","四扇推拉门",3000,2500,"推拉门",new[]{750d,750,750,750},new[]{"左推拉","右推拉","左推拉","右推拉"});
        Add("M2424","折叠门",2400,2400,"普通门",new[]{600d,600,600,600},new[]{"固定","固定","固定","固定"},"折叠门");
        Add("M3024","卷帘门",3000,2400,"普通门",new[]{3000d},new[]{"固定"},"卷帘门");
        Add("M1825A","旋转门",1800,2500,"普通门",new[]{1800d},new[]{"固定"},"旋转门");
        Add("MLC2424","门联窗",2400,2400,"门联窗",new[]{900d,1500},new[]{"左平开","固定"});
        Add("FM0921","防火门",900,2100,"防火门",new[]{900d},new[]{"左平开"});
        Add("RFM0921","人防门",900,2100,"人防门",new[]{900d},new[]{"左平开"});
        Add("C1218","固定窗",1200,1800,"普通窗",new[]{1200d},new[]{"固定"});
        Add("C0915","平开窗",900,1500,"普通窗",new[]{900d},new[]{"左平开"});
        Add("C1518","双扇推拉窗",1500,1800,"普通窗",new[]{750d,750},new[]{"左推拉","右推拉"});
        Add("C3018","四扇推拉窗",3000,1800,"普通窗",new[]{750d,750,750,750},new[]{"左推拉","右推拉","左推拉","右推拉"});
        Add("C0606","上悬窗",600,600,"普通窗",new[]{600d},new[]{"上悬"});
        Add("BYC0610","百叶窗",600,1000,"百叶窗",new[]{600d},new[]{"百叶"});
        Add("ZJC1818","转角窗",1800,1800,"转角窗",new[]{1800d},new[]{"固定"},"转角窗");
        Add("TC1818","矩形凸窗",1800,1800,"凸窗",new[]{1800d},new[]{"固定"},"矩形凸窗");
        Add("TC2418","梯形凸窗",2400,1800,"凸窗",new[]{2400d},new[]{"固定"},"梯形凸窗");
        Add("DXC6018","带形窗",6000,1800,"带形窗",new[]{1500d,1500,1500,1500},new[]{"固定","固定","固定","固定"});
        Add("GC1210","高窗",1200,1000,"高窗",new[]{1200d},new[]{"固定"});
        Add("GXC1218","拱形窗",1200,1800,"拱形窗",new[]{1200d},new[]{"固定"});
        return entries;
        void Add(string code,string name,double width,double height,string kind,double[] widths,string[] modes,string style="按立面"){
            var door=kind.Contains("门");
            var type=new OpeningTypeModel {Code=code,Kind=kind,Width=width,Height=height,Sill=door?0:kind=="高窗"?2000:900,
                ElevationType=kind,PlanStyle=style,PlanReturnInset=style=="梯形凸窗"?400:0,DivisionPreset="自定义",
                HasInstallationGap=false,OuterFrameWidth=50,MullionWidth=50,FrameDepth=100,SashWidth=40,
                BayLeftDepth=600,BayRightDepth=600,BayLeftSide="窗",BayRightSide="窗",Remarks=name,Source="内置"};
            var cells=new List<DoorWindowLayoutCell>();var left=0d;
            for(var i=0;i<widths.Length;i++){
                var leaf=door&&(kind!="门联窗"||i==0);
                cells.Add(new(){Left=left,Right=left+widths[i],Top=height,IsDoor=leaf,Material=leaf?"实板":"玻璃",Opening=modes[i]});left+=widths[i];
            }
            type.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(cells);
            entries.Add(new(type,name,"内置"));
        }
    }
}
