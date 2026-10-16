using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;

internal static class CadModelWorkspaceTests
{
    private static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    private static T Field<T>(object o,string name)=>(T)o.GetType().GetField(name,Private).GetValue(o);
    private static object Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,Private).Invoke(o,args);
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    public static void Run()
    {
        var folder=Path.GetFullPath(".artifacts/model-workspace/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var a=BuildingModelProjectWorkflow.Empty("A模型");
        a.Storeys=StoreyElevationLayout.Resolve(StandardStoreyLayout.ExpandRanges(new[]{new StoreyModel{Id="1F",Name="1层",Height=4500},new StoreyModel{Id="2F",Name="2层",Height=3900},new StoreyModel{Id="3F",Name="3~5层",Height=3900}}),"1F",0);
        var b=BuildingModelJson.FromJson(BuildingModelJson.ToJson(a));b.Name="B模型";
        var pa=BuildingModelJson.ModelFilePath(folder,a.Name);var pb=BuildingModelJson.ModelFilePath(folder,b.Name);
        BuildingModelProjectWorkflow.CreateNew(pa,a);BuildingModelProjectWorkflow.CreateNew(pb,b);
        BuildingModelCatalog.Register(folder,pa,"dwg-A","方案.dwg");BuildingModelCatalog.Register(folder,pb,"dwg-A","方案.dwg");StudioLaunch.RememberActiveModel(folder,pa,"dwg-A");
        var context=CadFloorRegistrationContext.Create(pa,a,"1F",new PointModel(0,0),1);context.SetSourceDatum(new PointModel(0,0),new PointModel(100,0));
        context.RegionMin=new PointModel(-200,-200);context.RegionMax=new PointModel(8200,6200);context.RegionPolygon=new List<PointModel>{new PointModel(-200,-200),new PointModel(8200,-200),new PointModel(8200,6200),new PointModel(-200,6200)};
        var capture=new CadFloorPlanCapture{Floor=context,Probe=new CadBuildingProbeDocument{DrawingFingerprint="dwg-A",DrawingPath="方案.dwg"},Underlay=new CadPlanUnderlay()};
        void Line(double x,double y,double u,double v)=>capture.Underlay.Primitives.Add(new ComponentPlanPrimitive{Kind="Line",X1=x,Y1=y,X2=u,Y2=v});
        foreach(var d in new[]{0d,180d}){Line(d,d,8000-d,d);Line(8000-d,d,8000-d,6000-d);Line(8000-d,6000-d,d,6000-d);Line(d,6000-d,d,d);}
        foreach(var x in new[]{2500d,2680d,5000d,5180d}){Line(x,180,x,2500);Line(x,3400,x,5820);}
        foreach(var y in new[]{2500d,2680d})Line(180,y,7820,y);
        capture.Underlay.Primitives.Add(new ComponentPlanPrimitive{Kind="Arc",X1=2500,Y1=2500,Radius=900,StartDegrees=0,SweepDegrees=90});
        var registry=CadFloorPlanRegistry.Load(pa);registry.ReplaceRegisteredConstruction=true;registry.SaveFloor(capture);
        Check(CadFloorPlanRegistry.Load(pa).ReplaceRegisteredConstruction,"capture loses replacement policy");
        var opens=0;var saves=0;var guards=0;var generated=0;var allow=true;var pickAccepted=false;var switchDuringPick=false;
        CadFloorRegistrationContext picked=null;CadFloorPlanRegistry generatedRegistry=null;
        var draftAnswer=MessageBoxResult.No;
        var host=new CadModelWorkspaceHost{ProjectFolder=folder,ProjectName="测试项目",DrawingName="方案.dwg",DrawingFingerprint="dwg-A",CurrentModel=()=>StudioLaunch.ActiveModelPath(folder,"test","dwg-A"),Theme=CadComponentPlanWindow.CreateTheme,
            EnsureDocument=()=>{guards++;if(!allow)throw new InvalidOperationException("wrong drawing");},Open=p=>{opens++;return Task.CompletedTask;},Save=p=>{saves++;return Task.CompletedTask;},
            SaveFloors=(path,hash,floors)=>{var model=BuildingModelJson.LoadModel(path);Check(BuildingModelProjectWorkflow.StoreysHash(model)==hash,"stale floor hash");model.Storeys=floors;BuildingModelJson.SaveModel(path,model);return Task.CompletedTask;},
            Generate=r=>{generated++;generatedRegistry=r;return Task.CompletedTask;},Confirm=(title,text,buttons)=>buttons==MessageBoxButton.OKCancel?MessageBoxResult.OK:draftAnswer};
        var window=new CadFloorRegistrationWindow(pa,pickDatum:(owner,floor)=>{picked=floor;if(switchDuringPick)allow=false;return pickAccepted;},capture:(owner,floor)=>null,workspace:host);
        var root=(FrameworkElement)window.Content;
        var draftEditor=Field<CadModelProjectEditor>(window,"_floorEditor");
        draftEditor.SetFloorProperties("1F","首层","4700");draftEditor.UndoDraft();
        Check(draftEditor.FindFloor("1F").Height=="4500","floor draft undo missing");
        draftEditor.RedoDraft();Check(draftEditor.FindFloor("1F").Height=="4700","floor draft redo missing");draftEditor.UndoDraft();
        var rows=Field<ListBox>(window,"_workFloors");Check(rows.Items.Count==3,"shared storeys must appear once");
        Check(Field<CadRegisteredPlanPreview>(window,"_preview").PrimitiveCount==19,"preview does not use registered geometry");
        for(var repeat=0;repeat<3;repeat++)for(var page=0;page<3;page++){Call(window,"ShowWorkspacePage",page);Arrange(root,1244,760);}
        foreach(var width in new[]{1244,960,844})for(var page=0;page<3;page++){Call(window,"ShowWorkspacePage",page);var height=width==844?560:720;Arrange(root,width,height);Render(root,Path.Combine(folder,"workspace-"+width+"-"+page+".png"),width,height);}
        Call(window,"ShowWorkspacePage",1);Arrange(root,960,720);Render(root,Path.Combine(folder,"workspace-144dpi.png"),960,720,144);
        Call(window,"ShowWorkspacePage",1);
        Field<TextBox>(window,"_floorHeightText").Text="4600";
        rows.SelectedIndex=1;rows.SelectedIndex=0;
        Check(Field<TextBox>(window,"_floorHeightText").Text=="4600","switch floor loses draft input");
        var picker=Field<ComboBox>(window,"_modelPicker");draftAnswer=MessageBoxResult.Cancel;
        picker.SelectedItem=picker.Items.Cast<BuildingModelCatalogEntry>().Single(e=>e.ModelPath==pb);
        Check(window.TargetModelPath==pa&&Field<TextBox>(window,"_floorHeightText").Text=="4600","cancel model switch loses draft");
        draftAnswer=MessageBoxResult.No;
        Check(window.SaveWorkspace().GetAwaiter().GetResult()&&BuildingModelJson.LoadModel(pa).FindStorey("1F").Height==4600,"floor edits not saved to target");
        b=BuildingModelJson.LoadModel(pa);b.Name="B模型";BuildingModelJson.SaveModel(pb,b);
        window.LoadModel(pa);
        picker.SelectedItem=picker.Items.Cast<BuildingModelCatalogEntry>().Single(e=>e.ModelPath==pb);
        Check(window.TargetModelPath==pb&&opens==0&&StudioLaunch.ActiveModelPath(folder,"test","dwg-A")==pa,"selection activates Studio or targets wrong model");
        Check(Field<CadModelProjectEditor>(window,"_floorEditor").ModelName=="B模型","same floor hash reuses wrong model editor");
        window.LoadModel(pa);Call(window,"ShowWorkspacePage",1);
        var registryBefore=File.ReadAllBytes(CadFloorPlanRegistry.FilePath(pa));
        ((Task)Call(window,"CaptureWorkspace",true)).GetAwaiter().GetResult();
        Check(File.ReadAllBytes(CadFloorPlanRegistry.FilePath(pa)).SequenceEqual(registryBefore),"cancel pick changes prior registration");
        Field<TextBox>(window,"_x").Text="1234";Field<TextBox>(window,"_unitScale").Text="10";
        ((Task)Call(window,"CaptureWorkspace",true)).GetAwaiter().GetResult();
        Check(picked.Alignment.ModelBase.X==1234&&picked.Alignment.MillimetresPerCadUnit==10,"save reload discards capture coordinates");
        pickAccepted=true;switchDuringPick=true;
        try{((Task)Call(window,"CaptureWorkspace",true)).GetAwaiter().GetResult();throw new Exception("changed document accepted");}catch(InvalidOperationException){}
        Check(File.ReadAllBytes(CadFloorPlanRegistry.FilePath(pa)).SequenceEqual(registryBefore),"document switch during pick changes registration");
        allow=true;switchDuringPick=false;pickAccepted=false;
        Field<TextBox>(window,"_slabThickness").Text="bad";var bytes=File.ReadAllBytes(pa);
        try{window.SaveWorkspace().GetAwaiter().GetResult();throw new Exception("invalid thickness accepted");}catch(InvalidDataException){}
        Check(File.ReadAllBytes(pa).SequenceEqual(bytes),"invalid setting changes model");
        window.LoadModel(pa);allow=false;
        try{window.SaveWorkspace().GetAwaiter().GetResult();throw new Exception("wrong drawing accepted");}catch(InvalidOperationException){}
        allow=true;((Task)Call(window,"GenerateWorkspace")).GetAwaiter().GetResult();Check(generated==1&&guards>0,"generate target/guard missing");
        Field<TextBox>(window,"_slabThickness").Text="invalid";
        ((Task)Call(window,"GenerateWorkspace")).GetAwaiter().GetResult();
        Check(generated==2&&generatedRegistry.GenerationWarning!=null,"invalid auto setting blocks generation");
        ((Task)Call(window,"BeginNewModel")).GetAwaiter().GetResult();Call(window,"ShowWorkspacePage",0);
        Check(Field<CadModelProjectEditor>(window,"_floorEditor").ModelName=="新建模型","inline new model editor missing");
        Field<TextBox>(window,"_slabThickness").Text="180";Field<ComboBox>(window,"_generationMode").SelectedIndex=1;
        Check(window.SaveWorkspace().GetAwaiter().GetResult(),"inline creation failed");
        var createdRegistry=CadFloorPlanRegistry.Load(window.TargetModelPath);
        Check(createdRegistry.ResolvedSlabThickness==180&&!createdRegistry.ReplaceRegisteredConstruction,"inline new loses generation settings");
        Check(BuildingModelJson.LoadModel(window.TargetModelPath).Walls.Count==0&&StudioLaunch.ActiveModelPath(folder,"test","dwg-A")==pa,"new model contains sample or changes active model");
        window.LoadModel(pa);window.Close();
        var missing=new CadFloorRegistrationWindow(Path.Combine(folder,"missing.json"),workspace:host);
        Check(missing.TargetModelPath==null,"invalid remembered path prevents workspace recovery");
        ((Task)Call(missing,"BeginNewModel")).GetAwaiter().GetResult();
        Field<CadModelProjectEditor>(missing,"_floorEditor").Cancelled();missing.Close();
        Console.WriteLine("CAD_MODEL_WORKSPACE_OK tabs nativeTheme sizes realUnderlay standardGroups targets sameHash draftSave cancel invalid rollback guard captureUnits inlineNew "+folder);
    }
    private static void Arrange(FrameworkElement root,double width,double height){root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();}
    private static void Render(FrameworkElement root,string path,int width,int height,double dpi=96){var bitmap=new RenderTargetBitmap((int)(width*dpi/96),(int)(height*dpi/96),dpi,dpi,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);}
}
