using System;
using System.IO;
using System.Linq;
using System.Reflection;
using SynchronizationContext=System.Threading.SynchronizationContext;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;
using Application=Autodesk.AutoCAD.ApplicationServices.Application;

[assembly:CommandClass(typeof(CadModelWorkspaceModelessNativeTests))]
public sealed class CadModelWorkspaceModelessNativeTests
{
    [DllImport("user32.dll")]private static extern bool IsWindowEnabled(IntPtr window);
    private static readonly Type Service=typeof(CadFloorRegistrationWindow).Assembly.GetType("BatchPdfPublisher.Features.BuildingModel.Services.CadModelWorkspaceService",true);
    private static readonly BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static void Check(bool value,string error){if(!value)throw new InvalidDataException(error);}
    private static T Field<T>(object instance,string name)=>(T)instance.GetType().GetField(name,Private).GetValue(instance);
    private static Task Cad(Document doc,Action ensure,Action action)=>(Task)Service.GetMethod("RunCadAsync",Static).Invoke(null,new object[]{doc,ensure,action});

    [CommandMethod("WLMODELESSWORKSPACEQA",CommandFlags.Session)]
    public void Start()
    {
        var root=Environment.GetEnvironmentVariable("WANLUO_MODELESS_QA_ROOT");
        var doc=Application.DocumentManager.MdiActiveDocument;
        void Trace(string phase)=>File.AppendAllText(Path.Combine(root,"phases.txt"),DateTime.Now.ToString("O")+" "+phase+Environment.NewLine);
        Trace("command entered "+doc?.Database.Filename);
        if(string.IsNullOrWhiteSpace(root)||doc==null||!Path.GetFullPath(doc.Database.Filename).StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Disposable CAD template required");
        EventHandler idle=null;
        idle=async(s,e)=>
        {
            if(!doc.Editor.IsQuiescent)return;
            Application.Idle-=idle;
            Trace("idle accepted");
            var synchronization=SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            CadFloorRegistrationWindow window=null;Document other=null;
            try
            {
                var path=Path.Combine(root,"models","model.json");
                BuildingModelProjectWorkflow.CreateNew(path,BuildingModelProjectWorkflow.Empty("非模态验证"));
                var initial=File.ReadAllBytes(path);var picks=0;
                void Ensure()
                {
                    if(!Application.DocumentManager.Cast<Document>().Any(d=>ReferenceEquals(d,doc))||!ReferenceEquals(Application.DocumentManager.MdiActiveDocument,doc))
                        throw new InvalidOperationException("source document unavailable");
                }
                async Task Ready()
                {
                    var stable=0;
                    for(var i=0;i<300;i++)
                    {
                        if(doc.Editor.IsQuiescent&&string.IsNullOrEmpty(doc.CommandInProgress)){if(++stable==5)return;}
                        else stable=0;
                        await Task.Delay(100);
                    }
                    throw new InvalidOperationException("Startup CAD commands did not become idle");
                }
                var host=new CadModelWorkspaceHost {ProjectFolder=Path.GetDirectoryName(path),ProjectName="临时验证",DrawingName=doc.Name,
                    DrawingFingerprint=doc.Database.FingerprintGuid.ToString(),CurrentModel=()=>path,EnsureDocument=Ensure,
                    RunCad=async action=>
                    {
                        Trace("CAD dispatch begin appContext="+Application.DocumentManager.IsApplicationContext);
                        await Cad(doc,Ensure,()=>{Trace("CAD callback enter");action();Trace("CAD callback leave");});
                        Trace("CAD dispatch returned");
                    },
                    Save=async p=>{Trace("save await begin "+SynchronizationContext.Current?.GetType().FullName);await Task.Delay(40);Trace("save await returned");},Confirm=(a,b,c)=>MessageBoxResult.No};
                window=new CadFloorRegistrationWindow(path,pickDatum:(owner,floor)=>
                {
                    Trace("fake pick entered");
                    Check(!Application.DocumentManager.IsApplicationContext,"pick outside command context");
                    using((IDisposable)Service.GetMethod("BeginInteraction",Static).Invoke(null,new object[]{doc,owner}))
                    {Check(!owner.IsVisible,"workspace obstructs CAD picking");picks++;return false;}
                },workspace:host);
                window.IsVisibleChanged+=(a,b)=>Trace("visibility "+window.IsVisible+" appContext="+Application.DocumentManager.IsApplicationContext);
                Trace("workspace constructed");
                Service.GetMethod("Show",Static).Invoke(null,new object[]{doc,window});Trace("modeless show returned");await Task.Delay(250);
                Trace("workspace shown");
                Check(window.IsVisible&&IsWindowEnabled(Application.MainWindow.Handle),"workspace locks CAD main window");
                Check((bool)Service.GetMethod("Activate",Static).Invoke(null,new object[]{doc}),"workspace not reused");
                await Ready();
                await Cad(doc,Ensure,()=>
                {
                    Check(!Application.DocumentManager.IsApplicationContext,"CAD editing action not in command context");
                    doc.Editor.Command("_.LINE",new Point3d(10,10,0),new Point3d(100,100,0),"");
                });
                Trace("CAD edit completed");
                using(var tx=doc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    var space=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database),OpenMode.ForRead);
                    Check(space.Cast<ObjectId>().Any(id=>tx.GetObject(id,OpenMode.ForRead) is Line),"CAD edit did not execute with workspace open");
                }
                await Ready();
                // Exercise the same WPF entry as a real button, including its
                // explicit context capture; calling the private body skips it.
                await (Task)window.GetType().GetMethod("WorkspaceRun",Private).Invoke(window,new object[]{
                    (Func<Task>)(()=> (Task)window.GetType().GetMethod("CaptureWorkspace",Private).Invoke(window,new object[]{true}))});
                Check(picks==1&&window.IsVisible&&IsWindowEnabled(Application.MainWindow.Handle),"cancelled picking failed to restore modeless workspace");
                Trace("cancelled pick completed");
                var legacy=new CadFloorPlanRegistry {ModelPath=@"C:\old-computer\model.json"};
                var legacyDatum=CadFloorRegistrationContext.Create(path,BuildingModelJson.LoadModel(path),"1F",new PointModel(),1);
                legacyDatum.ModelPath=legacy.ModelPath;legacy.Datums.Add(legacyDatum);
                void WriteLegacy(){using(var stream=File.Create(CadFloorPlanRegistry.FilePath(path)))new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(CadFloorPlanRegistry)).WriteObject(stream,legacy);}
                WriteLegacy();var legacyBytes=File.ReadAllBytes(CadFloorPlanRegistry.FilePath(path));window.LoadModel(path);await Task.Delay(100);
                Check(Field<CadRegistryRecovery>(window,"_registryRecovery")!=null&&window.IsVisible&&IsWindowEnabled(Application.MainWindow.Handle),"mismatched registration blocks modeless CAD workspace");
                var associate=Descendants(window).OfType<Button>().Single(b=>b.Name=="CadRegistryAssociate");associate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);
                Check(Field<CadRegistryRecovery>(window,"_registryRecovery")==null&&CadFloorPlanRegistry.Load(path).Datums.Single().ModelPath==path,"native association action failed");
                Check(Directory.GetFiles(Path.GetDirectoryName(path),"*.before-association-*.bak").Any(p=>File.ReadAllBytes(p).SequenceEqual(legacyBytes))&&File.ReadAllBytes(path).SequenceEqual(initial),"native association backup/model preservation failed");
                WriteLegacy();window.LoadModel(path);await Task.Delay(100);
                Descendants(window).OfType<Button>().Single(b=>b.Name=="CadRegistryStartFresh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);
                Check(CadFloorPlanRegistry.Load(path).Datums.Count==0&&File.ReadAllBytes(path).SequenceEqual(initial),"native reset registration changed existing model");
                Trace("registration recovery association backup reset modelUnchanged completed");
                var existing=BuildingModelJson.ModelFilePath(host.ProjectFolder,"6#");
                BuildingModelProjectWorkflow.CreateNew(existing,BuildingModelProjectWorkflow.Empty("6#"));
                var oldModel=File.ReadAllBytes(existing);var replace=false;
                host.Confirm=(a,b,c)=>a=="替换已有模型"?(replace?MessageBoxResult.Yes:MessageBoxResult.Cancel):MessageBoxResult.No;
                await (Task)window.GetType().GetMethod("BeginNewModel",Private).Invoke(window,null);
                var editor=Field<CadModelProjectEditor>(window,"_floorEditor");
                Field<TextBox>(editor,"_name").Text="6#";
                for(var i=0;i<2;i++)editor.GetType().GetMethod("Append",Private).Invoke(editor,new object[]{"F","层",StoreyKind.Normal,false,false});
                editor.SetFloorProperties("1F","一层","5900");editor.SetFloorProperties("2F","2层","4500");
                window.UpdateLayout();var grid=Field<DataGrid>(editor,"_grid");var row=grid.Items[2];grid.SelectedItem=row;grid.ScrollIntoView(row);grid.CurrentCell=new DataGridCellInfo(row,grid.Columns[2]);
                grid.Focus();Check(grid.BeginEdit(),"cannot enter live storey cell edit");window.UpdateLayout();
                var input=grid.Columns[2].GetCellContent(row) as TextBox;Check(input!=null,"live height editor missing");input.Text="3600";input.Focus();
                var create=Descendants(editor).OfType<Button>().Single(b=>b.Content as string=="创建模型");
                create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);
                Check(!window.WorkspaceBusy&&Field<TextBlock>(window,"_workspaceStatus").Text.Contains("已取消替换")&&File.ReadAllBytes(existing).SequenceEqual(oldModel),"cancel replacement hangs or changes old model");
                replace=true;create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);
                Check(!window.WorkspaceBusy&&window.TargetModelPath==existing&&Field<TextBlock>(window,"_workspaceStatus").Text.Contains("已保存"),"create/replace never completes");
                Check(BuildingModelJson.LoadModel(existing).Storeys.Select(f=>f.Height).SequenceEqual(new[]{5900d,4500d,3600d}),"active cell height lost on create");
                var priorContext=SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(synchronization);
                    var save=Descendants(window).OfType<Button>().Single(b=>b.ToolTip as string=="保存");
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                finally{SynchronizationContext.SetSynchronizationContext(priorContext);}
                for(var i=0;i<30&&window.WorkspaceBusy;i++)await Task.Delay(50);
                Check(!window.WorkspaceBusy&&Field<TextBlock>(window,"_workspaceStatus").Text.Contains("已保存"),"native host context strands async save");
                window.LoadModel(path);Trace("creation replacement cancel activeCell asyncHost completed");
                Action<CadGenerationProgress> oldProgress=null;
                host.GenerateWithProgress=async(registry,report)=>
                {
                    oldProgress=report;report(new CadGenerationProgress {Stage="等待模型窗口接收"});
                    await Task.Delay(350);
                    Check(window.IsVisible&&Field<ProgressBar>(window,"_generationProgress").IsIndeterminate
                        &&Field<TextBlock>(window,"_generationElapsed").Text.Contains("已用时"),"native waiting progress/elapsed not visible");
                    Check(IsWindowEnabled(Application.MainWindow.Handle),"generation progress locks native CAD");
                    report(new CadGenerationProgress {Stage="生成楼层：一层",Completed=1,Total=3});
                    await Task.Delay(100);
                    Check(Math.Abs(Field<ProgressBar>(window,"_generationProgress").Value-100d/3)<.01,"native stage progress not updated");
                };
                await (Task)window.GetType().GetMethod("WorkspaceRun",Private).Invoke(window,new object[]{
                    (Func<Task>)(()=> (Task)window.GetType().GetMethod("GenerateWorkspace",Private).Invoke(window,null))});
                Check(!window.WorkspaceBusy&&Field<ProgressBar>(window,"_generationProgress").Value==100,"native generation progress never completes");
                oldProgress(new CadGenerationProgress {Stage="late"});
                Check(Field<TextBlock>(window,"_generationStage").Text.Contains("已生成并保存"),"native late progress overwrote completion");
                Trace("generation stage progress elapsed receipt lateCallback completed");
                Field<TextBox>(window,"_floorHeightText").Text="3456";
                other=Application.DocumentManager.Add(Path.Combine(root,"fixture.dwt"));
                await Task.Delay(100);
                Check(Field<TextBlock>(window,"_cadContextStatus").Visibility==System.Windows.Visibility.Visible,"document switch not indicated");
                window.Activate();Check(ReferenceEquals(Application.DocumentManager.MdiActiveDocument,other),"workspace activation stole CAD document");
                var rejected=false;var ran=false;try{await Cad(doc,Ensure,()=>ran=true);}catch(System.Exception){rejected=true;}
                Check(rejected&&!ran&&Field<TextBox>(window,"_floorHeightText").Text=="3456"&&File.ReadAllBytes(path).SequenceEqual(initial),"document switch changed model/draft");
                doc.CloseAndDiscard();await Task.Delay(100);
                Check(window.IsVisible&&Field<TextBlock>(window,"_cadContextStatus").Text.Contains("关闭")&&Field<TextBox>(window,"_floorHeightText").Text=="3456","source close discarded draft or left stale binding");
                Field<CadModelProjectEditor>(window,"_floorEditor").UndoDraft();
                window.LoadModel(path);window.Close();await Task.Delay(100);
                Trace("document lifecycle completed");
                File.WriteAllText(Path.Combine(root,"result.txt"),"CAD_MODELESS_WORKSPACE_NATIVE_OK modelessMainEnabled actualCadLine singleWindow commandContext cancelledPick registrationRecovery associateBackup resetExistingModel createThreeFloors activeCell duplicateChoice backup asyncHostCompletion generationProgress elapsed receipt lateCallback switchGuard noFocusSteal closedSourceDraft modelUnchanged");
            }
            catch(System.Exception ex)
            {
                File.WriteAllText(Path.Combine(root,"result.txt"),ex.ToString());
                if(window!=null){window.LoadModel(window.TargetModelPath);window.Close();}
            }
            finally
            {
                foreach(var item in new[]{doc,other}.Where(d=>d!=null&&Application.DocumentManager.Cast<Document>().Any(open=>ReferenceEquals(open,d))))item.CloseAndDiscard();
                SynchronizationContext.SetSynchronizationContext(synchronization);
                Application.Quit();
            }
        };
        Application.Idle+=idle;
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
}
