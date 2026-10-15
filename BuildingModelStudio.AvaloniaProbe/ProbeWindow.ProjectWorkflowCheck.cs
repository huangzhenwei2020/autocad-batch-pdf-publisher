using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using BatchPdfPublisher.BuildingModel;
using System.IO;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunProjectWorkflowCheckAsync()
    {
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
        var oldModel=BuildingModelJson.ToJson(_session.Model);
        var folder=Path.GetFullPath(".artifacts/studio-project-workflow/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var dialog=new NewBuildingModelWindow(folder);var pending=dialog.ShowDialog<string?>(this);await Task.Delay(150);
        Check(dialog.Draft.Walls.Count==0&&dialog.Draft.Storeys.Count==1,"New dialog must be empty.");
        dialog.ModelName.Text="UI 新模型";
        using(var bitmap=await ElementComposition.GetElementVisual(dialog)!.Compositor.CreateCompositionVisualSnapshot(ElementComposition.GetElementVisual(dialog)!,1))
            bitmap.Save(Path.Combine(folder,"studio-new.png"),PngBitmapEncoderOptions.Default);
        dialog.Create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var path=await pending;Check(path!=null&&File.Exists(path),"Create button did not create model.");
        Check(BuildingModelJson.ToJson(_session.Model)==oldModel,"New dialog changes old session before confirming.");
        var previousFile=_filePath;
        Check(await LoadModelAsync(path!),"Created empty model did not load.");
        Check(_session.Model.Walls.Count==0&&!HasChanges,"New model loaded sample geometry.");
        var referenceFloor=CadFloorRegistrationContext.Create(path!,_session.Model,"1F",new PointModel(),1);
        referenceFloor.SetSourceDatum(new PointModel(),new PointModel(10,0));
        referenceFloor.RegionMin=new PointModel(0,0);referenceFloor.RegionMax=new PointModel(6000,4000);
        referenceFloor.RegionPolygon=new(){new(0,0),new(6000,0),new(6000,4000),new(0,4000)};
        var referenceCapture=new CadFloorPlanCapture {Floor=referenceFloor,Probe=new() {DrawingFingerprint="reference-only-ui",
            Entities=new(){new() {Handle="M1",BoundsMin=new(){X=1000,Y=1000},BoundsMax=new(){X=1900,Y=1200}}}},
            Openings=new(){new() {SourceHandle="M1",Code="M0921",Kind="门",Width=900,Height=2100}},
            Underlay=new(){Primitives=new(){new() {Kind="Line",SourceLayer="CAD-普通线",X1=0,Y1=0,X2=6000,Y2=0}}}};
        var referenceRegistry=new CadFloorPlanRegistry {ModelPath=path!};referenceRegistry.SaveFloor(referenceCapture);
        var referenceRequest=CadModelGenerationRequest.Queue(referenceRegistry);await ReceiveCadGenerationAsync();
        Check(CadModelGenerationRequest.LoadResult(path!).Succeeded&&!File.Exists(CadModelGenerationRequest.FilePath(path!)),
            "Reference-only generation did not acknowledge success.");
        Check(_session.Model.CadImport?.RequestId==referenceRequest.Id&&_session.Model.Walls.Count==0
            &&_session.Model.CadImport.PendingOpenings.Count==1&&!HasChanges
            &&BuildingModelJson.LoadModel(path!).CadImport.RequestId==referenceRequest.Id,
            "Reference-only generation failed to save the empty model and opening marker.");
        _planCanvas.SetStorey("1F");
        Check(_planCanvas.Underlay?.Primitives.Count==1&&_planCanvas.SnapUnderlay(0,0,10,new SnapResult()).Snapped,
            "Reference-only generation lost the snappable CAD underlay.");
        Check(_session.Undo()&&_session.Model.CadImport==null,"Reference-only generation did not undo in one step.");
        await ReceiveCadGenerationAsync();Check(_session.Model.CadImport==null,"Reference-only request replayed after undo.");
        Console.WriteLine("CAD_REFERENCE_ONLY_STUDIO_OK empty pending underlay snap save acknowledge undo noReplay");
        referenceCapture.Floor.WallCandidates=new(){
            new(){Handle="valid-wall",DxfName="TCH_WALL",StraightHorizontalLineVerified=true,CurveStart=new(){X=0,Y=0},CurveEnd=new(){X=6000,Y=0},
                CandidateThickness=200,CandidateAxisOffset=0,Fields=new(){new(){Name="Elevation",Number=0}}},
            new(){Handle="unverified-wall",StraightHorizontalLineVerified=false,CurveStart=new(){X=0,Y=0},CurveEnd=new(){X=0,Y=4000}}};
        referenceRegistry.SaveFloor(referenceCapture);
        var mixedRequest=CadModelGenerationRequest.Queue(referenceRegistry);await ReceiveCadGenerationAsync();
        Check(CadModelGenerationRequest.LoadResult(path!).Succeeded&&_session.Model.Walls.Count==1
            &&_session.Model.CadImport!.RequestId==mixedRequest.Id&&_session.Model.CadImport.StructureMessages.Any(m=>m.Contains("定位尚未核验"))
            &&!HasChanges&&_planCanvas.Underlay?.Primitives.Count==1,
            "An unverified wall blocked the valid wall, save or underlay.");
        var mixedModel=BuildingModelJson.ToJson(_session.Model);
        referenceRegistry.SlabThickness=-1;
        var warningRequest=CadModelGenerationRequest.Queue(referenceRegistry);await ReceiveCadGenerationAsync();
        Check(CadModelGenerationRequest.LoadResult(path!).Succeeded&&_session.Model.Walls.Count==1
            &&_session.Model.CadImport!.RequestId==warningRequest.Id&&_session.Model.CadImport.StructureMessages.Any(m=>m.Contains("自动更新已跳过"))
            &&!HasChanges,"Recognition fallback failed to acknowledge, preserve and save the model.");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==mixedModel,"Warning-only update failed to undo.");
        await ReceiveCadGenerationAsync();Check(BuildingModelJson.ToJson(_session.Model)==mixedModel,"Warning-only request replayed after undo.");
        Console.WriteLine("CAD_NONBLOCKING_STUDIO_OK unverifiedWall validWall warningFallback underlay save acknowledge undo noReplay");
        referenceRegistry.SlabThickness=100;referenceRegistry.ReplaceRegisteredConstruction=true;
        referenceCapture.Openings[0].Placement=CadOpeningPlacement.Create(referenceFloor,"valid-wall",new PointModel(),new PointModel(6000,0),new PointModel(1550,0),new PointModel(2450,0),900);
        referenceRegistry.SaveFloor(referenceCapture);
        CadModelGenerationRequest.Queue(referenceRegistry);await ReceiveCadGenerationAsync();
        var replacementOpening=_session.Model.Openings.Single();
        var replacementType=OpeningConstruction.Copy(OpeningConstruction.Resolve(_session.Model,replacementOpening));replacementType.PanelThickness=55;
        Check(_session.TrySetOpeningConstruction(replacementOpening.Id,replacementType,false,out var constructionError),constructionError);
        Check(_session.TrySetOpeningPresentation(replacementOpening.Id,replacementOpening.Code,45,true,out var presentationError),presentationError);
        Check(_session.TrySetOpeningPlacement(replacementOpening.Id,_session.Model.Walls[0].Id,3000,true,true,out var placementError),placementError);
        Check(_session.TrySetWallGeometry(_session.Model.Walls[0].Id,4000,200,0,out var wallError),wallError);
        Check(_session.TryAddWall(new WallModel {StoreyId="1F",X1=0,Y1=1000,X2=1000,Y2=1000,Thickness=200},out _,out var addError),addError);
        var replacementBefore=BuildingModelJson.ToJson(_session.Model);
        var replacementRequest=CadModelGenerationRequest.Queue(referenceRegistry);await ReceiveCadGenerationAsync();
        var replaced=_session.Model.Openings.Single();
        Check(CadModelGenerationRequest.LoadResult(path!).Succeeded && _session.Model.Walls.Count==1 && _session.Model.Walls[0].X2==6000
            && replaced.Offset==2000 && replaced.PlanOpenAngle==45 && replaced.OpenIn3D==true
            && OpeningConstruction.Resolve(_session.Model,replaced).PanelThickness==55 && !HasChanges,
            "Live replacement lost matching construction or retained edited geometry.");
        Check(BuildingModelJson.LoadModel(path!).CadImport.RequestId==replacementRequest.Id,"Replacement was not saved.");
        Check(_session.Undo() && BuildingModelJson.ToJson(_session.Model)==replacementBefore,"Live replacement did not undo all geometry and definitions together.");
        await ReceiveCadGenerationAsync();Check(BuildingModelJson.ToJson(_session.Model)==replacementBefore,"Replacement replayed after undo.");
        Check(_session.Redo(),"Replacement redo failed.");
        Console.WriteLine("CAD_REPLACEMENT_STUDIO_OK liveUnsaved manualWallRemoval matchingDefinition placement angle save acknowledge oneUndo redo noReplay");
        _session=new BuildingModelEditSession(SampleModelFactory.CreateTwoStoreyHouse());
        BuildingModelJson.SaveModel(path!,_session.Model);StudioLaunch.RegisterSession(path!);_savedJson=BuildingModelJson.ToJson(_session.Model);
        var movingWall=_session.Model.Walls[0];
        Check(_session.TryMoveWallGripOnly(movingWall.Id,0,movingWall.X1+25,movingWall.Y1,out var moveError),moveError);
        var movedX=_session.Model.Walls[0].X1;
        var original=BuildingModelJson.LoadModel(path!);var floors=original.Storeys.Select(s=>new StoreyModel {Id=s.Id,Name=s.Name,Height=s.Height,Elevation=s.Elevation}).ToList();
        floors.Add(new StoreyModel {Id="RF",Name="屋顶层",Elevation=6900,Height=3000,Kind=StoreyKind.Roof});
        var request=BuildingModelProjectWorkflow.Queue(path!,BuildingModelProjectWorkflow.StoreysHash(original),floors);await ReceiveCadStoreysAsync();
        Check(BuildingModelProjectWorkflow.Result(path!).Succeeded&&_session.Model.FindStorey("RF")!=null,"Live CAD floor request failed.");
        Check(_session.Model.Walls[0].X1==movedX&&!HasChanges,"Live floor request loses unsaved model geometry.");
        Check(_session.Undo()&&_session.Model.FindStorey("RF")==null&&_session.Model.Walls[0].X1==movedX,"Floor undo affects unrelated wall edit.");
        await ReceiveCadStoreysAsync();Check(_session.Model.FindStorey("RF")==null,"Applied request replayed after undo.");
        _session=new BuildingModelEditSession(original);_session.TryReplaceStoreys(floors,out _,true);
        _session.Model.Storeys.Single(s=>s.Id=="RF").Height=3100;
        request=BuildingModelProjectWorkflow.Queue(path!,BuildingModelProjectWorkflow.StoreysHash(BuildingModelJson.LoadModel(path!)),original.Storeys);
        var before=BuildingModelJson.ToJson(_session.Model);await ReceiveCadStoreysAsync();
        Check(!BuildingModelProjectWorkflow.Result(path!).Succeeded&&BuildingModelJson.ToJson(_session.Model)==before,"Conflicting live floor request overwrites current data.");
        StudioLaunch.RemoveSession(path!);_filePath=previousFile;_session=new BuildingModelEditSession(BuildingModelJson.FromJson(oldModel));
        _savedJson=oldModel;var frame=_viewport.RenderedFrameCount;await RefreshModelAsync("项目流程测试完成");
        _workspaces.SelectedIndex=0;
        _viewport.SetDisplayMode(ModelViewport.DisplayMode.Shaded);
        _viewport.ResetView();await WaitForFrameAsync(_viewport.CurrentScene.VertexCount,frame);
        Console.WriteLine("MODEL_PROJECT_STUDIO_UI_OK create empty cancelSafety liveFloor unsavedGeometry save undo noReplay conflict "+folder);
    }
}
