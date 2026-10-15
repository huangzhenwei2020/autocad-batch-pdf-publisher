using BatchPdfPublisher.BuildingModel;
using System.Diagnostics;

internal static class ModelManagementTests
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Reject(Action action,string message)
    {try{action();}catch(IOException){return;}catch(InvalidDataException){return;}catch(ArgumentException){return;}throw new Exception(message);}
    public static void Main()
    {
        var project=Path.GetFullPath(".artifacts/model-management/"+Guid.NewGuid().ToString("N"));
        var model=SampleModelFactory.CreateTwoStoreyHouse();model.Name="主楼";
        var a=BuildingModelJson.ModelFilePath(project,model.Name);BuildingModelProjectWorkflow.CreateNew(a,model);
        var b=BuildingModelJson.ModelFilePath(project,"副楼");BuildingModelProjectWorkflow.CreateNew(b,BuildingModelProjectWorkflow.Empty("副楼"));
        var original=File.ReadAllBytes(a);
        Check(BuildingModelCatalog.List(project,a).Count==2,"Legacy models were not discovered.");
        BuildingModelCatalog.Register(project,a,"dwg-A","A.dwg");BuildingModelCatalog.Register(project,b,"dwg-B","B.dwg");
        BuildingModelCatalog.Register(project,a,"dwg-B","B.dwg");
        StudioLaunch.RememberActiveModel(project,a,"dwg-A");StudioLaunch.RememberActiveModel(project,b,"dwg-B");
        Check(StudioLaunch.ActiveModelPath(project,"other","dwg-A")==a&&StudioLaunch.ActiveModelPath(project,"other","dwg-B")==b,"Two CAD drawings share an active model pointer.");
        var rows=BuildingModelCatalog.List(project,a);Check(rows.Count==2&&rows.Count(e=>BuildingModelCatalog.LinkedTo(e,"dwg-A"))==1
            &&rows.Count(e=>BuildingModelCatalog.LinkedTo(e,"dwg-B"))==2&&rows.Single(e=>e.ModelPath==a).IsCurrent,"Catalog links/current state incorrect.");
        var external=Path.Combine(project,"external.json");BuildingModelJson.SaveModel(external,model);BuildingModelCatalog.Register(project,external,"dwg-A","A.dwg");
        Check(BuildingModelCatalog.List(project).Count==3,"External model not catalogued.");
        var floor=CadFloorRegistrationContext.Create(a,model,"1F",new PointModel(),1);floor.SetSourceDatum(new PointModel(),new PointModel(10,0));
        floor.RegionMin=new PointModel(0,0);floor.RegionMax=new PointModel(12000,12000);
        floor.RegionPolygon=new(){new(0,0),new(12000,0),new(12000,12000),new(0,12000)};
        var registry=new CadFloorPlanRegistry {ModelPath=a};registry.SaveDatum(floor);
        var capture=new CadFloorPlanCapture {Floor=floor,Probe=new() {DrawingFingerprint="dwg-C",DrawingPath="C.dwg"}};
        registry.SaveFloor(capture);
        Check(BuildingModelCatalog.LinkedTo(BuildingModelCatalog.List(project).Single(e=>e.ModelPath==a),"dwg-C"),"Legacy registration drawing link was not recognized.");
        StudioLaunch.RegisterSession(a);
        try
        {
            Check(BuildingModelCatalog.List(project,a).Single(e=>e.ModelPath==a).IsOpen,"Live window status incorrect.");
            var request=BuildingModelFileRequests.Queue(a,"Save",project,"dwg-A","A.dwg");BuildingModelFileRequests.Validate(a,request);
            Reject(()=>BuildingModelFileRequests.Queue(a,"Save",project),"Concurrent request replaced pending save.");
            BuildingModelFileRequests.Complete(a,request.Id,a,null);
            Check(BuildingModelFileRequests.Result(a).Succeeded&&!File.Exists(BuildingModelFileRequests.RequestPath(a)),"Ack can replay.");
            request=BuildingModelFileRequests.Queue(a,"Activate",project);BuildingModelFileRequests.Cancel(a,request.Id);
            Reject(()=>BuildingModelFileRequests.Validate(a,request),"Canceled activation was accepted.");
            request=BuildingModelFileRequests.Queue(a,"Save",project);request.SessionToken="stale";
            Reject(()=>BuildingModelFileRequests.Validate(a,request),"Old window request accepted.");BuildingModelFileRequests.Cancel(a,request.Id);
            request=BuildingModelFileRequests.Queue(a,"Save",project);request.CreatedUtc=DateTime.UtcNow.AddMinutes(-3);
            Reject(()=>BuildingModelFileRequests.Validate(a,request),"Expired request accepted.");BuildingModelFileRequests.Cancel(a,request.Id);
            var live=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model));live.Walls[0].X1+=123;
            var copy=BuildingModelJson.ModelFilePath(project,"主楼方案二");
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(a)!,"views"));File.WriteAllText(Path.Combine(Path.GetDirectoryName(a)!,"views","old.json"),"stale");
            BuildingModelProjectWorkflow.CreateCopy(a,copy,live,"主楼方案二");
            Check(BuildingModelJson.LoadModel(copy).Walls[0].X1==live.Walls[0].X1&&BuildingModelJson.LoadModel(copy).Name=="主楼方案二","SaveAs did not use live draft.");
            Check(File.ReadAllBytes(a).SequenceEqual(original),"SaveAs modified source file.");
            var copiedRegistry=CadFloorPlanRegistry.Load(copy);
            Check(copiedRegistry.ModelPath==copy&&copiedRegistry.Floors.Single().Floor.ModelPath==copy&&copiedRegistry.Datums.Single().ModelPath==copy,"Copied registration still points to source.");
            Check(!Directory.Exists(Path.Combine(Path.GetDirectoryName(copy)!,"views"))&&!File.Exists(copy+".studio-session.txt")
                &&!File.Exists(BuildingModelFileRequests.RequestPath(copy)),"Copy contains session/requests/stale views.");
            Reject(()=>BuildingModelProjectWorkflow.CreateCopy(a,copy,model,"主楼方案二"),"Copy replaced existing model.");
            Reject(()=>BuildingModelProjectWorkflow.CreateCopy(a,BuildingModelJson.ModelFilePath(project,"wrong"),model,"name"),"Inconsistent copy path accepted.");
        }
        finally{StudioLaunch.RemoveSession(a);}
        Reject(()=>BuildingModelFileRequests.Queue(a,"Save",project),"Disk version substituted for live save.");
        var moved=project+"-moved";Directory.Move(project,moved);
        var movedA=BuildingModelJson.ModelFilePath(moved,"主楼");
        Check(StudioLaunch.ActiveModelPath(moved,"other","dwg-A")==movedA,"Project move lost per-DWG selection.");
        Check(BuildingModelCatalog.List(moved).Any(e=>e.ModelPath==movedA&&e.Available),"Relative catalog path not portable.");
        var key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("DWG-A")));
        var marker=Path.Combine(moved,StudioLaunch.ModelFolderName,".drawing-models",key+".txt");File.WriteAllText(marker,"../escape.json");
        Reject(()=>StudioLaunch.ActiveModelPath(moved,"other","dwg-A"),"Unsafe marker accepted.");
        Console.WriteLine("MODEL_MANAGEMENT_CORE_OK legacyCatalog links multipleDwg liveSave token expiry cancel copyDraft registration collision portable "+moved);
    }
}
