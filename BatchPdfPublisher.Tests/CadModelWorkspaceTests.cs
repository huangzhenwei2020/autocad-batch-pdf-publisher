using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        var cadDispatches=0;var inCad=false;var switchBeforeCad=false;
        CadFloorRegistrationContext picked=null;CadFloorPlanRegistry generatedRegistry=null;
        var draftAnswer=MessageBoxResult.No;
        var host=new CadModelWorkspaceHost{ProjectFolder=folder,ProjectName="测试项目",DrawingName="方案.dwg",DrawingFingerprint="dwg-A",CurrentModel=()=>StudioLaunch.ActiveModelPath(folder,"test","dwg-A"),Theme=CadComponentPlanWindow.CreateTheme,
            EnsureDocument=()=>{guards++;if(!allow)throw new InvalidOperationException("wrong drawing");},Open=p=>{opens++;return Task.CompletedTask;},Save=p=>{saves++;return Task.CompletedTask;},
            SaveFloors=(path,hash,floors)=>{var model=BuildingModelJson.LoadModel(path);Check(BuildingModelProjectWorkflow.StoreysHash(model)==hash,"stale floor hash");model.Storeys=floors;BuildingModelJson.SaveModel(path,model);return Task.CompletedTask;},
            Generate=r=>{generated++;generatedRegistry=r;return Task.CompletedTask;},Confirm=(title,text,buttons)=>buttons==MessageBoxButton.OKCancel?MessageBoxResult.OK:draftAnswer};
        host.RunCad=action=>{cadDispatches++;if(switchBeforeCad)allow=false;host.EnsureDocument();inCad=true;try{action();}finally{inCad=false;}return Task.CompletedTask;};
        var window=new CadFloorRegistrationWindow(pa,pickDatum:(owner,floor)=>{Check(inCad,"pick escaped CAD command context");picked=floor;if(switchDuringPick)allow=false;return pickAccepted;},
            capture:(owner,floor)=>{Check(inCad,"capture escaped CAD command context");return null;},
            readPlan:r=>Check(inCad,"generation read escaped CAD command context"),workspace:host);
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
        window.SetCadContextState("登记已暂停：源图纸未激活。");
        Check(root.IsEnabled&&Field<TextBox>(window,"_floorHeightText").Text=="4600","paused document disabled workspace or lost draft");
        window.SetCadContextState(null);
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
        switchBeforeCad=true;var previousPick=picked;
        try{((Task)Call(window,"CaptureWorkspace",true)).GetAwaiter().GetResult();throw new Exception("queued document switch accepted");}catch(InvalidOperationException){}
        Check(ReferenceEquals(previousPick,picked)&&File.ReadAllBytes(CadFloorPlanRegistry.FilePath(pa)).SequenceEqual(registryBefore),"queued context failure changed registration");
        allow=true;switchBeforeCad=false;
        ((Task)Call(window,"CaptureWorkspace",false)).GetAwaiter().GetResult();
        Field<TextBox>(window,"_slabThickness").Text="bad";var bytes=File.ReadAllBytes(pa);
        try{window.SaveWorkspace().GetAwaiter().GetResult();throw new Exception("invalid thickness accepted");}catch(InvalidDataException){}
        Check(File.ReadAllBytes(pa).SequenceEqual(bytes),"invalid setting changes model");
        window.LoadModel(pa);allow=false;
        try{window.SaveWorkspace().GetAwaiter().GetResult();throw new Exception("wrong drawing accepted");}catch(InvalidOperationException){}
        allow=true;((Task)Call(window,"GenerateWorkspace")).GetAwaiter().GetResult();Check(generated==1&&guards>0,"generate target/guard missing");
        Field<TextBox>(window,"_slabThickness").Text="invalid";
        ((Task)Call(window,"GenerateWorkspace")).GetAwaiter().GetResult();
        Check(generated==2&&generatedRegistry.GenerationWarning!=null,"invalid auto setting blocks generation");
        switchBeforeCad=true;
        try{((Task)Call(window,"GenerateWorkspace")).GetAwaiter().GetResult();throw new Exception("wrong drawing generation accepted");}catch(InvalidOperationException){}
        Check(generated==2,"context guard was swallowed as nonblocking recognition warning");allow=true;switchBeforeCad=false;
        Check(cadDispatches>=6,"CAD operations bypassed dispatcher");
        ((Task)Call(window,"BeginNewModel")).GetAwaiter().GetResult();Call(window,"ShowWorkspacePage",0);
        Check(Field<CadModelProjectEditor>(window,"_floorEditor").ModelName=="新建模型","inline new model editor missing");
        Field<TextBox>(window,"_slabThickness").Text="180";Field<ComboBox>(window,"_generationMode").SelectedIndex=1;
        Check(window.SaveWorkspace().GetAwaiter().GetResult(),"inline creation failed");
        var createdRegistry=CadFloorPlanRegistry.Load(window.TargetModelPath);
        Check(createdRegistry.ResolvedSlabThickness==180&&!createdRegistry.ReplaceRegisteredConstruction,"inline new loses generation settings");
        Check(BuildingModelJson.LoadModel(window.TargetModelPath).Walls.Count==0&&StudioLaunch.ActiveModelPath(folder,"test","dwg-A")==pa,"new model contains sample or changes active model");
        window.LoadModel(pa);
        ((Task)Call(window,"WorkspaceRun",(Func<Task>)(()=>Task.CompletedTask))).GetAwaiter().GetResult();
        Check(!window.WorkspaceBusy&&root.IsEnabled&&Field<TextBlock>(window,"_workspaceStatus").Text!="正在处理…",
            "completed workspace action leaves misleading processing status");
        CheckAsyncCompletion(window);
        CheckGenerationUi(window,host,folder);
        window.Close();
        var missing=new CadFloorRegistrationWindow(Path.Combine(folder,"missing.json"),workspace:host);
        Check(missing.TargetModelPath==null,"invalid remembered path prevents workspace recovery");
        ((Task)Call(missing,"BeginNewModel")).GetAwaiter().GetResult();
        Field<CadModelProjectEditor>(missing,"_floorEditor").Cancelled();missing.Close();
        CheckCreateButtons(folder,host);
        Console.WriteLine("CAD_MODEL_WORKSPACE_OK tabs nativeTheme sizes realUnderlay standardGroups targets sameHash draftSave cancel invalid rollback guard captureUnits inlineNew modelessPause cadDispatch queuedDocumentGuard "+folder);
    }
    private sealed class DormantHostContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback,object state){Posts++;}
    }
    private static void CheckGenerationUi(CadFloorRegistrationWindow window,CadModelWorkspaceHost host,string folder)
    {
        Action<CadGenerationProgress> late=null;
        var bar=Field<ProgressBar>(window,"_generationProgress");var stage=Field<TextBlock>(window,"_generationStage");
        var panel=Field<StackPanel>(window,"_generationPanel");var elapsed=Field<TextBlock>(window,"_generationElapsed");
        host.GenerateWithProgress=async(registry,report)=>
        {
            late=report;report(new CadGenerationProgress {Stage="等待模型窗口接收"});
            Check(panel.Visibility==Visibility.Visible&&bar.IsIndeterminate,"Waiting stage is not visible/indeterminate.");
            await Task.Delay(300);
            Check(!string.IsNullOrEmpty(elapsed.Text),"Elapsed time froze while awaiting generation.");
            report(new CadGenerationProgress {Stage="生成楼层：二层",Completed=1,Total=3});
            Check(!bar.IsIndeterminate&&Math.Abs(bar.Value-100d/3)<.01&&stage.Text.Contains("1/3"),"Floor count is not actual stage progress.");
            var root=(FrameworkElement)window.Content;Arrange(root,820,650);
            Render(root,Path.Combine(folder,"generation-progress-small.png"),820,650);
            Arrange(root,1280,850);Render(root,Path.Combine(folder,"generation-progress-large.png"),1280,850);
            await Task.Delay(30);
            Check(bar.Value<100,"Awaiting generation displayed completed early.");
        };
        Task Run()=>(Task)Call(window,"WorkspaceRun",(Func<Task>)(()=> (Task)Call(window,"GenerateWorkspace")));
        try
        {
            Pump(Run(),window.Dispatcher);
            Check(!window.WorkspaceBusy&&bar.Value==100&&stage.Text.Contains("已生成并保存"),"Saved generation does not complete progress.");
            var terminal=stage.Text;late(new CadGenerationProgress {Stage="late callback"});
            Check(stage.Text==terminal,"Late progress replaced completion.");
            host.GenerateWithProgress=async(registry,report)=>{report(new CadGenerationProgress {Stage="保存模型"});await Task.Delay(30);throw new IOException("generation-save-failed");};
            Pump(Run(),window.Dispatcher);
            Check(!window.WorkspaceBusy&&panel.Visibility==Visibility.Collapsed&&Field<TextBlock>(window,"_workspaceStatus").Text=="generation-save-failed",
                "Failed generation displays completion or leaves the workspace busy.");
            late(new CadGenerationProgress {Stage="stale prior job"});
            Check(stage.Text!="stale prior job","Previous job callback contaminated the next attempt.");
        }
        finally{host.GenerateWithProgress=null;}
        Console.WriteLine("CAD_GENERATION_PROGRESS_UI_OK waiting counts elapsed awaitReceipt failure lateCallback small large");
    }
    private static void CheckAsyncCompletion(CadFloorRegistrationWindow window)
    {
        var blocked=new DormantHostContext();var previous=SynchronizationContext.Current;
        var finished=false;var entered=0;Task task;
        try
        {
            SynchronizationContext.SetSynchronizationContext(blocked);
            task=(Task)Call(window,"WorkspaceRun",(Func<Task>)(async()=>{entered++;await Task.Delay(30);finished=true;}));
            Check(ReferenceEquals(SynchronizationContext.Current,blocked),"workspace leaks WPF context back to host");
            Check(window.WorkspaceBusy&&!((FrameworkElement)window.Content).IsEnabled,"async operation not protected from double submit");
            ((Task)Call(window,"WorkspaceRun",(Func<Task>)(()=>{entered++;return Task.CompletedTask;}))).GetAwaiter().GetResult();
        }
        finally{SynchronizationContext.SetSynchronizationContext(previous);}
        Pump(task,window.Dispatcher);
        Check(finished&&entered==1&&blocked.Posts==0&&!window.WorkspaceBusy&&((FrameworkElement)window.Content).IsEnabled,
            "modeless host context strands completion or permits duplicate work");
        var failure=(Task)Call(window,"WorkspaceRun",(Func<Task>)(async()=>{await Task.Delay(20);throw new IOException("async-create-failed");}));
        Pump(failure,window.Dispatcher);
        Check(!window.WorkspaceBusy&&((FrameworkElement)window.Content).IsEnabled&&Field<TextBlock>(window,"_workspaceStatus").Text=="async-create-failed",
            "async failure leaves workspace locked or hides failure reason");
    }
    private static void Pump(Task task,Dispatcher dispatcher)
    {
        var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(3);
        var timer=new DispatcherTimer(DispatcherPriority.Background,dispatcher){Interval=TimeSpan.FromMilliseconds(10)};
        timer.Tick+=(s,e)=>{if(task.IsCompleted||DateTime.UtcNow>deadline)frame.Continue=false;};
        timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
        Check(task.IsCompleted,"workspace operation did not return to WPF dispatcher");task.GetAwaiter().GetResult();
    }
    private static void CheckCreateButtons(string folder,CadModelWorkspaceHost host)
    {
        var existing=BuildingModelJson.ModelFilePath(folder,"6#");
        BuildingModelProjectWorkflow.CreateNew(existing,BuildingModelProjectWorkflow.Empty("6#"));
        var before=File.ReadAllBytes(existing);
        var oldConfirm=host.Confirm;var answer=MessageBoxResult.Cancel;var prompts=0;
        host.Confirm=(title,text,buttons)=>{if(title=="替换已有模型"){prompts++;return answer;}return oldConfirm(title,text,buttons);};
        var window=new CadFloorRegistrationWindow(workspace:host);
        ((Task)Call(window,"BeginNewModel")).GetAwaiter().GetResult();
        var editor=Field<CadModelProjectEditor>(window,"_floorEditor");
        Field<TextBox>(editor,"_name").Text="6#";
        Call(editor,"Append","F","层",StoreyKind.Normal,false,false);
        Call(editor,"Append","F","层",StoreyKind.Normal,false,false);
        editor.SetFloorProperties("1F","一层","5900");editor.SetFloorProperties("2F","2层","4500");editor.SetFloorProperties("3F","3层","3600");
        var root=(FrameworkElement)window.Content;Arrange(root,1050,730);
        var create=Children(editor).OfType<Button>().Single(b=>b.Content as string=="创建模型");
        create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!window.WorkspaceBusy&&root.IsEnabled&&Field<TextBlock>(window,"_workspaceStatus").Text.Contains("已取消替换")
            &&File.ReadAllBytes(existing).SequenceEqual(before)&&window.TargetModelPath==null,"duplicate creation hangs or overwrites old model");
        answer=MessageBoxResult.Yes;
        create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!window.WorkspaceBusy&&root.IsEnabled&&Field<TextBlock>(window,"_workspaceStatus").Text.Contains("已保存"),"create button does not finish");
        var model=BuildingModelJson.LoadModel(window.TargetModelPath);
        Check(model.Storeys.Select(f=>f.Height).SequenceEqual(new[]{5900d,4500d,3600d})
            &&model.Storeys.Select(f=>f.Elevation).SequenceEqual(new[]{0d,5900d,10400d}),"create loses screenshot floor heights/elevations");
        Check(Field<ComboBox>(window,"_modelPicker").SelectedItem is BuildingModelCatalogEntry entry&&entry.ModelPath==window.TargetModelPath,
            "created model is not selected in target picker");
        var backup=Directory.GetDirectories(Path.GetDirectoryName(Path.GetDirectoryName(existing)),".model-replaced-6#-*").Single();
        Check(File.ReadAllBytes(Path.Combine(backup,"model.json")).SequenceEqual(before)&&prompts==2,"confirmed replacement did not preserve old model");
        window.Close();
        host.Confirm=oldConfirm;
        Console.WriteLine("WORKSPACE_CREATE_COMPLETION_OK actualButtons duplicateChoice backup cancelDraft threeFloors selectedModel asyncSuccessFailure WpfContinuation noDuplicateSubmit");
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Children(child))yield return nested;}
    }
    private static void Arrange(FrameworkElement root,double width,double height){root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();}
    private static void Render(FrameworkElement root,string path,int width,int height,double dpi=96){var bitmap=new RenderTargetBitmap((int)(width*dpi/96),(int)(height*dpi/96),dpi,dpi,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);}
}
