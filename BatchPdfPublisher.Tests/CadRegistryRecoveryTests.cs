using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;

internal static class CadRegistryRecoveryTests
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T typed)yield return typed;foreach(var nested in Children<T>(child))yield return nested;}}
    private static void Write(string path,CadFloorPlanRegistry registry){using(var stream=File.Create(CadFloorPlanRegistry.FilePath(path)))new DataContractJsonSerializer(typeof(CadFloorPlanRegistry)).WriteObject(stream,registry);}
    private static void Throws(Action action){try{action();}catch(Exception ex) when(ex is IOException||ex is InvalidDataException){return;}throw new Exception("Expected guarded rejection.");}
    public static void Run()
    {
        var folder=Path.GetFullPath(".artifacts/registry-recovery/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"model.json");var model=BuildingModelProjectWorkflow.Empty("移动后的测试模型");
        model.Storeys=new List<StoreyModel> {new StoreyModel {Id="A",Name="一层",Height=4500},new StoreyModel {Id="B",Name="二层",Elevation=4500,Height=3300},new StoreyModel {Id="S",Name="三层",Elevation=7800,Height=3300,TemplateStoreyId="B"}};
        model.Walls.Add(new WallModel {Id="manual",StoreyId="A",X2=4000,Thickness=200});BuildingModelJson.SaveModel(path,model);
        var old=new CadFloorPlanRegistry {ModelPath=@"C:\旧电脑\项目\model.json",SlabThickness=160,ReplaceRegisteredConstruction=true};
        foreach(var id in new[]{"A","B"})
        {
            var context=CadFloorRegistrationContext.Create(path,model,id,new PointModel(0,0),1);context.ModelPath=old.ModelPath;
            context.SetSourceDatum(new PointModel(1000,2000),new PointModel(1100,2000));context.RegionMin=new PointModel(1000,2000);context.RegionMax=new PointModel(5000,6000);
            context.RegionPolygon=new List<PointModel>{new PointModel(1000,2000),new PointModel(5000,2000),new PointModel(5000,6000),new PointModel(1000,6000)};
            var capture=new CadFloorPlanCapture {Floor=context,Probe=new CadBuildingProbeDocument {DrawingFingerprint="source-"+id,DrawingPath="原平面.dwg"},Underlay=new CadPlanUnderlay()};
            foreach(var line in new[]{new[]{1000d,2000d,5000d,2000d},new[]{5000d,2000d,5000d,6000d},new[]{5000d,6000d,1000d,6000d},new[]{1000d,6000d,1000d,2000d}})
                capture.Underlay.Primitives.Add(new ComponentPlanPrimitive {Kind="Line",X1=line[0],Y1=line[1],X2=line[2],Y2=line[3]});old.Floors.Add(capture);old.Datums.Add(context);
        }
        Write(path,old);var original=File.ReadAllBytes(CadFloorPlanRegistry.FilePath(path));var modelBytes=File.ReadAllBytes(path);
        Throws(()=>CadFloorPlanRegistry.Load(path));
        var recovery=CadRegistryRecovery.Read(path);var rows=recovery.Rows(model);
        rows[1].TargetStoreyId="A";Throws(()=>recovery.Apply(rows,false));
        rows[1].TargetStoreyId="S";Throws(()=>recovery.Apply(rows,false));
        rows[1].TargetStoreyId=null;Throws(()=>recovery.Apply(rows,false));
        Check(File.ReadAllBytes(CadFloorPlanRegistry.FilePath(path)).SequenceEqual(original),"Invalid mapping changed registry.");
        rows[0].TargetStoreyId="B";rows[1].TargetStoreyId="A";
        var backup=recovery.Apply(rows,false);var associated=CadFloorPlanRegistry.Load(path);
        Check(File.ReadAllBytes(backup).SequenceEqual(original)&&File.ReadAllBytes(path).SequenceEqual(modelBytes),"Association lost backup or edited model.");
        Check(associated.Floors[0].Floor.Storey.Id=="B"&&associated.Floors[0].Floor.Storey.Elevation==4500&&associated.Floors[0].Floor.ReferenceStoreys.Single().Id=="S","Mapping lost current elevations/standard references.");
        Check(associated.Floors.All(f=>f.Floor.ModelPath==path&&f.Floor.Alignment.StoreyId==f.Floor.Storey.Id&&f.Floor.Alignment.CadBase.X==1000&&f.Underlay.Primitives.Count==4)&&associated.Datums.All(d=>d.ModelPath==path),"Nested paths, alignment or underlay changed.");
        Throws(()=>recovery.Apply(rows,false));
        Write(path,old);var stale=CadRegistryRecovery.Read(path);File.AppendAllText(CadFloorPlanRegistry.FilePath(path)," ");Throws(()=>stale.Apply(rows,false));
        Write(path,old);stale=CadRegistryRecovery.Read(path);File.AppendAllText(path," ");Throws(()=>stale.Apply(rows,false));File.WriteAllBytes(path,modelBytes);
        var host=new CadModelWorkspaceHost {ProjectFolder=folder,ProjectName="关联测试项目",DrawingName="方案.dwg",DrawingFingerprint="source-A",Theme=CadComponentPlanWindow.CreateTheme};
        var window=new CadFloorRegistrationWindow(path,workspace:host);var root=(FrameworkElement)window.Content;
        Check(window.TargetModelPath==path&&Field<CadRegistryRecovery>(window,"_registryRecovery")!=null,"Mismatch prevented workspace opening.");
        Check(!window.SaveWorkspace().GetAwaiter().GetResult()&&File.ReadAllBytes(CadFloorPlanRegistry.FilePath(path)).SequenceEqual(original),"Pending association silently overwrote registry.");
        foreach(var size in new[]{new Size(1244,760),new Size(844,560)})
        {
            root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();
            var buttons=Children<Button>(root).Where(b=>b.Name=="CadRegistryAssociate"||b.Name=="CadRegistryStartFresh").ToList();
            Check(buttons.Count==2&&buttons.All(b=>b.ActualHeight>0&&b.TranslatePoint(new Point(0,b.ActualHeight),root).Y<=size.Height+1),"Recovery actions clipped.");
            var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(folder,"recovery-"+size.Width+".png")))png.Save(stream);
        }
        var combos=Children<ComboBox>(Field<DataGrid>(window,"_recoveryTable")).Where(c=>c.SelectedValuePath=="Id").ToList();
        Check(combos.Count>=2,"Editable floor mappings missing.");combos[0].SelectedValue="B";combos[1].SelectedValue="A";
        Children<Button>(root).Single(b=>b.Name=="CadRegistryAssociate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Field<CadRegistryRecovery>(window,"_registryRecovery")==null&&CadFloorPlanRegistry.Load(path).Floors[0].Floor.Storey.Id=="B"&&!window.WorkspaceBusy,"Actual association action failed.");window.Close();
        Write(path,old);window=new CadFloorRegistrationWindow(path,workspace:host);root=(FrameworkElement)window.Content;root.Measure(new Size(1000,700));root.Arrange(new Rect(0,0,1000,700));root.UpdateLayout();
        Children<Button>(root).Single(b=>b.Name=="CadRegistryStartFresh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var fresh=CadFloorPlanRegistry.Load(path);Check(fresh.Floors.Count==0&&fresh.Datums.Count==0&&File.ReadAllBytes(path).SequenceEqual(modelBytes),"Restart changed model or retained old datums.");window.Close();
        Write(path,old);window=new CadFloorRegistrationWindow(path,workspace:host);window.Close();
        Check(File.ReadAllBytes(CadFloorPlanRegistry.FilePath(path)).SequenceEqual(original),"Closing recovery writes registry.");
        window=new CadFloorRegistrationWindow(path,workspace:host);root=(FrameworkElement)window.Content;root.Measure(new Size(1000,700));root.Arrange(new Rect(0,0,1000,700));root.UpdateLayout();
        Check(!Field<Button>(window,"_workspaceGenerateButton").IsEnabled,"Unresolved registration can generate into a mismatched model.");
        Children<Button>(root).Single(b=>b.Name=="CadRegistryCreateNew").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Field<bool>(window,"_creatingModel")&&Field<CadRegistryRecovery>(window,"_registryRecovery")==null,"New model action does not enter separate creation flow.");
        Check(window.SaveWorkspace().GetAwaiter().GetResult()&&window.TargetModelPath!=path,"New action overwrites existing target.");
        var created=BuildingModelJson.LoadModel(window.TargetModelPath);var createdRegistry=CadFloorPlanRegistry.Load(window.TargetModelPath);
        Check(created.Walls.Count==0&&created.Openings.Count==0&&created.CadImport==null&&createdRegistry.Floors.Count==0&&createdRegistry.Datums.Count==0,"New model inherited old construction or registration.");
        Check(File.ReadAllBytes(path).SequenceEqual(modelBytes)&&File.ReadAllBytes(CadFloorPlanRegistry.FilePath(path)).SequenceEqual(original),"Separate creation changed original model/registration.");window.Close();
        Check(Children<TextBlock>(Field<Button>(window,"_workspaceGenerateButton")).All(t=>!t.Text.Contains("更新")),"New empty model is labelled as an update.");
        Console.WriteLine("CAD_REGISTRY_RECOVERY_OK openNoAlert editableMapping actualButtons sizes cancelNoWrite completeBackup currentHeights standardReferences preserveXYUnderlay manualModelUnchanged staleGuards reset separateNew "+folder);
    }
    public static void VerifyCopy(string source)
    {
        var modelBytes=File.ReadAllBytes(source);var registryBytes=File.ReadAllBytes(CadFloorPlanRegistry.FilePath(source));
        var folder=Path.GetFullPath(".artifacts/registry-recovery-project/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"model.json");File.WriteAllBytes(path,modelBytes);File.WriteAllBytes(CadFloorPlanRegistry.FilePath(path),registryBytes);
        var model=BuildingModelJson.LoadModel(path);var recovery=CadRegistryRecovery.Read(path);var rows=recovery.Rows(model);var unmapped=rows.Count(r=>r.TargetStoreyId==null);
        if(unmapped>0)Throws(()=>recovery.Apply(rows,false));
        // Explicit skip is tested only in this private copy, never in a business project.
        foreach(var row in rows.Where(r=>r.TargetStoreyId==null))row.TargetStoreyId="";
        var backup=recovery.Apply(rows,false);var registry=CadFloorPlanRegistry.Load(path);
        Check(registry.Floors.All(f=>f.Floor.ModelPath==path)&&registry.Datums.All(d=>d.ModelPath==path),"Real copied registry has stale nested paths.");
        Check(File.ReadAllBytes(path).SequenceEqual(modelBytes)&&File.ReadAllBytes(backup).SequenceEqual(registryBytes)&&File.ReadAllBytes(source).SequenceEqual(modelBytes)&&File.ReadAllBytes(CadFloorPlanRegistry.FilePath(source)).SequenceEqual(registryBytes),"Private recovery changed source/model or lost backup.");
        Console.WriteLine("PRIVATE_REGISTRY_RECOVERY_OK floors="+registry.Floors.Count+" explicitUnmapped="+unmapped+" modelUnchanged sourceUnchanged backupExact "+folder);
    }
}
