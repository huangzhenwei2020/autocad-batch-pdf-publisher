using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;
using System.IO;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunModelManagementCheckAsync()
    {
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
        Window[] Windows()=>((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows.ToArray();
        var oldJson=BuildingModelJson.ToJson(_session.Model);var oldPath=_filePath;
        var oldProject=_modelProjectFolder;var oldFingerprint=_modelDrawingFingerprint;var oldDrawing=_modelDrawingPath;
        var folder=Path.GetFullPath(".artifacts/studio-model-management/"+Guid.NewGuid().ToString("N"));
        var model=BuildingModelJson.FromJson(oldJson);model.Name="主楼方案一";
        var a=BuildingModelJson.ModelFilePath(folder,model.Name);BuildingModelProjectWorkflow.CreateNew(a,model);
        var b=BuildingModelJson.ModelFilePath(folder,"主楼方案二");model.Name="主楼方案二";BuildingModelProjectWorkflow.CreateNew(b,model);
        _modelProjectFolder=folder;_modelDrawingFingerprint="ui-drawing-A";_modelDrawingPath="建筑平面.dwg";
        Check(await LoadModelAsync(a),"First model failed to load.");
        BuildingModelCatalog.Register(folder,b,"ui-drawing-B","另一图纸.dwg");
        var management=ManageModelsAsync();await Task.Delay(180);var manager=_modelManagerWindow!;
        Check(manager.Entries.Count==2,"Manager did not list legacy and current models.");
        manager.Scope.SelectedIndex=1;await Task.Delay(80);Check(manager.Models.ItemCount==1,"Current CAD drawing filter is incorrect.");
        manager.Scope.SelectedIndex=0;manager.Search.Text="方案二";await Task.Delay(80);Check(manager.Models.ItemCount==1,"Search did not filter.");manager.Search.Text="";await Task.Delay(80);
        for(var i=0;i<2;i++)
        {
            manager.Width=i==0?660:1000;manager.Height=i==0?460:640;await Task.Delay(150);
            var visual=ElementComposition.GetElementVisual(manager)!;
            using var bitmap=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
            bitmap.Save(Path.Combine(folder,"studio-manager-"+manager.Width+".png"),PngBitmapEncoderOptions.Default);
            Check(manager.Models.Bounds.Height>100&&manager.Search.Bounds.Width>150,"Manager layout collapsed.");
        }
        var wall=_session.Model.Walls[0];Check(_session.TryMoveWallGripOnly(wall.Id,0,wall.X1+50,wall.Y1,out var error),error);
        manager.Models.SelectedItem=manager.Entries.Single(e=>e.ModelPath==b);
        var switching=manager.Run("Open");await Task.Delay(150);
        var confirm=Windows().Single(w=>w.Title=="未保存的修改");
        confirm.GetVisualDescendants().OfType<Button>().Single(b=>(b.Content as string)=="取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await switching;Check(_filePath==a&&HasChanges,"Canceled switch discarded live draft.");
        await manager.Run("Save");Check(!HasChanges&&BuildingModelJson.LoadModel(a).Walls[0].X1==_session.Model.Walls[0].X1,"Manager save ignored live edit.");
        var sourceBytes=File.ReadAllBytes(a);wall=_session.Model.Walls[0];Check(_session.TryMoveWallGripOnly(wall.Id,0,wall.X1+20,wall.Y1,out error),error);
        var unsavedX=_session.Model.Walls[0].X1;
        var copying=manager.Run("SaveAs");await Task.Delay(150);
        var naming=Windows().OfType<ModelNameWindow>().Single();naming.NameInput.Text="主楼方案三";
        naming.Commit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await copying;
        var c=BuildingModelJson.ModelFilePath(folder,"主楼方案三");
        Check(_filePath==c&&!HasChanges&&_session.Model.Walls[0].X1==unsavedX&&File.ReadAllBytes(a).SequenceEqual(sourceBytes),"Managed copy changed source or lost unsaved edit.");
        var creating=manager.Run("New");await Task.Delay(150);
        var newDialog=Windows().OfType<NewBuildingModelWindow>().Single();newDialog.ModelName.Text="附楼";
        newDialog.Create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await creating;
        var d=BuildingModelJson.ModelFilePath(folder,"附楼");
        Check(_filePath==d&&_session.Model.Walls.Count==0&&!HasChanges,"New manager model contains sample geometry.");
        manager.Models.SelectedItem=manager.Entries.Single(e=>e.ModelPath==c);await manager.Run("Open");
        Check(_filePath==c&&StudioLaunch.ActiveModelPath(folder,"other","ui-drawing-A")==c,"Switch did not update CAD active model.");
        manager.Close();await management;
        wall=_session.Model.Walls[0];Check(_session.TryMoveWallGripOnly(wall.Id,0,wall.X1+30,wall.Y1,out error),error);
        var request=BuildingModelFileRequests.Queue(c,"Save",folder,"ui-drawing-A","建筑平面.dwg");await ReceiveModelFileRequestAsync();
        Check(BuildingModelFileRequests.Result(c).Id==request.Id&&BuildingModelFileRequests.Result(c).Succeeded&&!HasChanges
            &&BuildingModelJson.LoadModel(c).Walls[0].X1==_session.Model.Walls[0].X1,"CAD live save failed.");
        wall=_session.Model.Walls[0];Check(_session.TryMoveWallGripOnly(wall.Id,0,wall.X1+40,wall.Y1,out error),error);
        var beforeBytes=File.ReadAllBytes(c);var next=BuildingModelJson.ModelFilePath(folder,"CAD另存方案");
        request=BuildingModelFileRequests.Queue(c,"SaveAs",folder,"ui-drawing-A","建筑平面.dwg",next,"CAD另存方案");
        var expectedX=_session.Model.Walls[0].X1;await ReceiveModelFileRequestAsync();
        Check(_filePath==next&&BuildingModelFileRequests.Result(c).Id==request.Id&&BuildingModelFileRequests.Result(c).Succeeded
            &&BuildingModelJson.LoadModel(next).Walls[0].X1==expectedX&&File.ReadAllBytes(c).SequenceEqual(beforeBytes)
            &&!File.Exists(BuildingModelFileRequests.RequestPath(c)),"CAD live SaveAs lost data or left replayable request.");
        var savedJson=BuildingModelJson.ToJson(_session.Model);
        CancelActiveCommand();Check(BuildingModelJson.ToJson(_session.Model)==savedJson,"Cancel undid saved model edits.");
        request=BuildingModelFileRequests.Queue(next,"Activate",folder,"ui-drawing-B","另一图纸.dwg");await ReceiveModelFileRequestAsync();
        Check(BuildingModelFileRequests.Result(next).Succeeded&&StudioLaunch.ActiveModelPath(folder,"other","ui-drawing-B")==next
            &&StudioLaunch.ActiveModelPath(folder,"other","ui-drawing-A")==next,"Activate lost per-DWG state.");
        StudioLaunch.RemoveSession(next);_filePath=oldPath;_session=new BuildingModelEditSession(BuildingModelJson.FromJson(oldJson));_savedJson=oldJson;
        _modelProjectFolder=oldProject;_modelDrawingFingerprint=oldFingerprint;_modelDrawingPath=oldDrawing;
        if(oldPath!=null)StudioLaunch.RegisterSession(oldPath);
        var frame=_viewport.RenderedFrameCount;await RefreshModelAsync("模型管理测试完成");
        _workspaces.SelectedIndex=0;_viewport.SetDisplayMode(ModelViewport.DisplayMode.Shaded);_viewport.ResetView();
        await WaitForFrameAsync(_viewport.CurrentScene.VertexCount,frame);
        Console.WriteLine("MODEL_MANAGEMENT_STUDIO_OK list filter search small large cancelSwitch liveSave new switch copyDraft sourceUnchanged cadSave cadSaveAs activate noReplay "+folder);
    }
}
