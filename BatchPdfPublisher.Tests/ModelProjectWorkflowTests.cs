using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;

internal static class ModelProjectWorkflowTests
{
    private static void Check(bool value,string why) { if(!value)throw new Exception(why); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var item in Descendants(VisualTreeHelper.GetChild(node,i)))yield return item;
    }
    public static void Run()
    {
        var folder=Path.GetFullPath(".artifacts/model-project-workflow/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var model=BuildingModelProjectWorkflow.Empty("新建测试");
        Check(model.Storeys.Count==1 && model.Walls.Count==0 && model.Openings.Count==0,"New model must not carry sample geometry.");
        var path=BuildingModelJson.ModelFilePath(folder,model.Name);BuildingModelProjectWorkflow.CreateNew(path,model);var bytes=File.ReadAllBytes(path);
        try {BuildingModelProjectWorkflow.CreateNew(path,model);throw new Exception("Duplicate replaced existing model.");}catch(IOException) { }
        Check(File.ReadAllBytes(path).SequenceEqual(bytes),"Duplicate create changes file.");
        try {BuildingModelProjectWorkflow.Empty("..");throw new Exception("Unsafe model name accepted.");}catch(ArgumentException) { }
        StudioLaunch.RememberActiveModel(folder,path);Check(StudioLaunch.ActiveModelPath(folder,"other")==path,"CAD and Studio do not use the same active path.");
        var floors=StoreyElevationLayout.Resolve(StandardStoreyLayout.ExpandRanges(new[] {
            new StoreyModel {Id="B1",Name="地下一层",Height=3300},new StoreyModel {Id="1F",Name="一层",Height=4500},
            new StoreyModel {Id="2F",Name="2～4层",Height=3300},new StoreyModel {Id="RF",Name="屋顶层",Height=3000,Kind=StoreyKind.Roof},
            new StoreyModel {Id="MR",Name="机房层",Height=3000,Kind=StoreyKind.MachineRoom} }),"1F",0);
        BuildingModelProjectWorkflow.ValidateStoreys(floors);Check(floors.Count==7&&floors.Single(s=>s.Id=="4F").TemplateStoreyId=="2F","Standard ranges not shared.");
        var hash=BuildingModelProjectWorkflow.StoreysHash(model);var request=BuildingModelProjectWorkflow.Queue(path,hash,floors);
        var live=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model));live.Walls.Add(new WallModel {Id="unsaved",StoreyId="1F",X2=3000,Thickness=200});
        BuildingModelProjectWorkflow.ValidateRequest(request,live);var session=new BuildingModelEditSession(live);
        Check(session.TryReplaceStoreys(request.Storeys,out var error,true),error);Check(session.Model.Walls.Any(w=>w.Id=="unsaved"),"Floor merge loses unsaved wall.");
        Check(session.Undo()&&session.Model.Storeys.Count==1&&session.Model.Walls.Count==1,"Floor merge undo affects unrelated edits.");
        var stale=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model));stale.Storeys[0].Height=3600;
        try {BuildingModelProjectWorkflow.ValidateRequest(request,stale);throw new Exception("Stale floor data accepted.");}catch(InvalidDataException) { }
        BuildingModelProjectWorkflow.Complete(path,request.Id,null);Check(BuildingModelProjectWorkflow.Result(path).Succeeded&&!File.Exists(BuildingModelProjectWorkflow.RequestPath(path)),"Completed request can replay.");
        request=BuildingModelProjectWorkflow.Queue(path,hash,floors);BuildingModelProjectWorkflow.Cancel(path,request.Id);
        Check(!File.Exists(BuildingModelProjectWorkflow.RequestPath(path))&&File.ReadAllBytes(path).SequenceEqual(bytes),"Cancel alters saved model.");
        foreach(var width in new[] {780d,940d})
        {
            var dialog=new CadModelProjectWindow(model,true);var content=(FrameworkElement)dialog.Content;((Grid)content).Background=dialog.Background;
            content.Measure(new Size(width-36,500));content.Arrange(new Rect(new Size(width-36,500)));content.UpdateLayout();
            var buttons=Descendants(content).OfType<Button>().Where(b=>b.Content is string).ToArray();
            buttons.Single(b=>(string)b.Content=="新增楼层").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            buttons.Single(b=>(string)b.Content=="标准层").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            buttons.Single(b=>(string)b.Content=="屋顶层").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var rows=Descendants(content).OfType<DataGrid>().Single().Items.Cast<CadModelFloorEditRow>().ToArray();
            Check(rows.Length==4 && rows.Any(r=>r.Name.Contains("～"))&&rows.Any(r=>r.Kind=="屋顶层"),"CAD floor add controls unavailable.");
            content.UpdateLayout();Render(content,Path.Combine(folder,"cad-new-"+width+".png"),width-36,500);dialog.Close();
        }
        var registration=new CadFloorRegistrationWindow(createModel:owner=>path,openModel:owner=>null,editStoreys:(owner,file)=>System.Threading.Tasks.Task.CompletedTask);
        var root=(FrameworkElement)registration.Content;((Grid)root).Background=registration.Background;root.Measure(new Size(1000,610));root.Arrange(new Rect(new Size(1000,610)));root.UpdateLayout();
        Descendants(root).OfType<Button>().Single(b=>(b.Content as string)=="新建模型").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Descendants(root).OfType<DataGrid>().Single().Items.Count==1,"CAD registration cannot create without pre-existing active model.");
        Descendants(root).OfType<Button>().Single(b=>(b.Content as string)=="打开模型").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Descendants(root).OfType<DataGrid>().Single().Items.Count==1,"Cancelling model chooser clears current registration.");
        root.UpdateLayout();Render(root,Path.Combine(folder,"cad-registration.png"),1000,610);registration.Close();
        Console.WriteLine("MODEL_PROJECT_WORKFLOW_OK empty duplicate names activePath range basement roof machineRoom liveMerge undo conflict cancellation cadUi "+folder);
    }
    private static void Render(FrameworkElement content,string path,double width,double height)
    {
        var image=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);image.Render(content);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create(path))png.Save(stream);
    }
}
