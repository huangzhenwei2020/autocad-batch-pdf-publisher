using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;

internal static class CadOpeningRegistrationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [STAThread]
    private static void Main(string[] args)
    {
        if(args.Length==2 && args[0]=="--queue-registration") {
            var request=CadModelGenerationRequest.Queue(CadFloorPlanRegistry.Load(args[1]));
            Console.WriteLine("QUEUED_NATIVE_REGISTRATION "+request.Id);
            return;
        }
        if(args.Length==2 && args[0]=="--inspect-registration") {
            var actualModel=BuildingModelJson.LoadModel(args[1]); var actualRegistry=CadFloorPlanRegistry.Load(args[1]);
            foreach(var floor in actualRegistry.Floors) {
                var missing=floor.Openings.Where(o=>o.Include && o.Placement==null && CadOpeningJambPlacement.Find(floor.Floor,
                    floor.Probe.Entities.FirstOrDefault(e=>e.Handle==o.SourceHandle),o.Width.GetValueOrDefault())==null).Count();
                Console.WriteLine("REGISTRATION floor="+floor.Floor.Storey.Name+" defaults="+floor.Openings.Count+" missing-placement="+missing);
            }
            try { var generated=CadFloorModelGeneration.Build(actualModel,actualRegistry);Console.WriteLine("READ_ONLY_BUILD_OK walls="+generated.Walls.Count+" openings="+generated.Openings.Count); }
            catch(InvalidDataException ex) { Console.WriteLine("READ_ONLY_BUILD_BLOCKED "+ex.Message); }
            return;
        }
        var probe = new CadBuildingProbeDocument { DrawingFingerprint = "test-drawing", DrawingPath = "test.dwg" };
        probe.Entities.Add(new CadBuildingProbeEntity { Handle = "A1", DxfName = "TCH_OPENING", Layer = "A_WINDOW",
            Fields = new List<CadProbeField> { CadBuildingProbeRules.Field("Width", true, 800d, null), CadBuildingProbeRules.Field("Height", true, 2200d, null) } });
        probe.Entities.Add(new CadBuildingProbeEntity { Handle = "A2", DxfName = "LINE" });
        var library = new OpeningTypeLibraryDocument { ProjectName = "测试项目", Types = new List<OpeningTypeModel> {
            new OpeningTypeModel { Code = "C01", Kind = "窗", Width = 900, Height = 2100, Sill = 900,
                ElevationType = "普通窗", DivisionPreset = "双扇", OuterFrameWidth = 65 },
            new OpeningTypeModel { Code = "c01", Kind = "窗", Width = 1000, Height = 2300, Sill = 1000,
                ElevationType = "普通窗", OuterFrameWidth = 80 } } };
        var rows = CadOpeningRegistration.FromProbe(probe); var row = rows.Single();
        Check(row.Code == null && row.Sill == null && !row.Include, "Missing native code/sill must stay unknown; no layer guess.");
        row.Include = true; row.Code = " C01 ";
        Check(CadOpeningRegistration.Matches(library, row.Code).Count == 2, "Keep duplicate code choices.");
        Check(CadOpeningRegistration.Validate(row, library).Contains("多条"), "Ambiguous types must block.");
        row.TypeIndex = 1; row.Sill = 0; row.Confirmed = true;
        var result = CadOpeningRegistration.Build(probe, library, rows, true);
        Check(result.Openings[0].Width == 800 && result.Openings[0].Height == 2200 && result.Openings[0].Sill == 0, "Keep CAD sizes and allow explicit zero sill.");
        Check(!result.Openings[0].PlacementVerified, "Parameter review must not imply placement.");
        var item = OpeningElevationAdapter.ToScheduleItem(new OpeningModel { Code = row.Code, Kind = "窗", Width = 800, Height = 2200 },
            result.Openings[0].ElevationParameters, 800, 2200);
        Check(item.Width == 800 && item.Height == 2200 && item.OuterFrameWidth == 80, "Instance dimensions and existing elevation styles must combine.");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1, 0 }) {
            row.Width = invalid; Check(CadOpeningRegistration.Validate(row, library) != null, "Invalid dimensions block."); }
        row.Width = 800; row.Sill = null; Check(CadOpeningRegistration.Validate(row, library) != null, "Unknown sill blocks.");
        row.Sill = 900; row.Confirmed = false; Check(CadOpeningRegistration.Validate(row, library) != null, "Unconfirmed row blocks.");
        row.Confirmed = true;
        var sillEntries = new List<CadOpeningSillEntry> {
            new CadOpeningSillEntry { Code = "C01", Height = 1000, TypeIndex = 1, Source = "立面已保存参数" },
            new CadOpeningSillEntry { Code = "C01", Height = 850, FromSchedule = true, Source = "门窗表" } };
        string sillSource;
        Check(CadOpeningRegistration.ResolveSill(sillEntries, " c01 ", 1, null, out sillSource) == 850 && sillSource == "门窗表", "Schedule overrides saved type sill.");
        sillEntries[1].Height = null;
        Check(CadOpeningRegistration.ResolveSill(sillEntries, "C01", 1, null, out sillSource) == null, "Suppressed schedule must not use type fallback.");
        sillEntries[1].Height = 0;
        Check(CadOpeningRegistration.ResolveSill(sillEntries, "C01", 1, null, out sillSource) == 0, "Explicit schedule zero is valid.");
        sillEntries.Add(new CadOpeningSillEntry { Code = "C01", Height = 900, FromSchedule = true, Source = "另一行" });
        Check(CadOpeningRegistration.ResolveSill(sillEntries, "C01", 1, null, out sillSource) == null, "Conflicting same-code sill values block.");
        sillEntries[2].FloorName = "2F";
        Check(CadOpeningRegistration.ResolveSill(sillEntries, "C01", 1, null, out sillSource) == null, "Floor schedule requires floor context.");
        Check(CadOpeningRegistration.ResolveSill(sillEntries, "C01", 1, "2F", out sillSource) == 900, "Exact floor takes precedence.");
        Check(CadOpeningRegistration.ResolveSill(sillEntries, "C01", 1, "1F", out sillSource) == 0, "Another floor is not used.");
        Check(CadOpeningRegistration.ResolveSill(null, "C01", 1, null, out sillSource) == null, "Missing values must never use default 900.");
        try { CadOpeningRegistration.Build(probe, library, rows, false); throw new Exception("Units were not checked."); } catch (InvalidDataException) { }
        var output = Path.GetFullPath(".artifacts/cad-opening-register-20260930"); Directory.CreateDirectory(output);
        var firstFloor = new CadFloorRegistrationAlignment { StoreyId = "1F", CadBase = new PointModel(700000, 40000), ModelBase = new PointModel(0, 0) };
        var secondFloor = new CadFloorRegistrationAlignment { StoreyId = "2F", CadBase = new PointModel(800000, 90000), ModelBase = new PointModel(0, 0), RotationRadians = Math.PI / 2 };
        var firstPoint = firstFloor.ToModel(new PointModel(702000, 43000));
        var secondPoint = secondFloor.ToModel(new PointModel(803000, 88000));
        Check(Math.Abs(firstPoint.X - secondPoint.X) < 1e-7 && Math.Abs(firstPoint.Y - secondPoint.Y) < 1e-7, "Different floor source datums/directions must share model coordinates.");
        var inverse = secondFloor.ToCad(secondPoint);
        Check(Math.Abs(inverse.X - 803000) < 1e-7 && Math.Abs(inverse.Y - 88000) < 1e-7, "CAD inverse transform must preserve source position.");
        secondFloor.MillimetresPerCadUnit = 1000;
        Check(Math.Abs(secondFloor.ToModel(new PointModel(800003, 89998)).X - 2000) < 1e-7, "Explicit unit scale must apply after subtracting datum.");
        Check(secondFloor.OpeningBottomElevation(new StoreyModel { Id = "2F", Elevation = 3600 }, 900) == 4500, "Opening bottom is relative to its slab datum.");
        firstFloor.StoreyId = "B1";
        Check(firstFloor.OpeningBottomElevation(new StoreyModel { Id = "B1", Elevation = -4200 }, 900) == -3300, "Basement sill must preserve negative elevation.");
        try { firstFloor.OpeningBottomElevation(new StoreyModel { Id = "2F", Elevation = 3600 }, 900); throw new Exception("Wrong floor accepted."); } catch (ArgumentException) { }
        firstFloor.CadBase = null;
        try { firstFloor.ToModel(new PointModel(0, 0)); throw new Exception("Missing datum accepted."); } catch (ArgumentException) { }
        var floorModel = new BuildingModelDocument { Name = "登记测试", Storeys = new List<StoreyModel> {
            new StoreyModel { Id = "B1", Name = "地下一层", Height = 4200, Elevation = -4200 },
            new StoreyModel { Id = "1F", Name = "一层", Height = 3600, Elevation = 0 },
            new StoreyModel { Id = "2F", Name = "二层", Height = 3300, Elevation = 3600, TemplateStoreyId = "1F" } } };
        var modelPath = Path.Combine(output, "registration-model.json"); BuildingModelJson.SaveModel(modelPath, floorModel);
        var floorContext = CadFloorRegistrationContext.Create(modelPath, floorModel, "1F", new PointModel(0, 0), 1);
        Check(floorContext.ReferenceStoreys.Single().Id == "2F", "Standard floor references must retain real floor identities.");
        floorModel.Storeys[1].Elevation = 100;
        Check(floorContext.Storey.Elevation == 0, "Registration must snapshot floor settings.");
        try { CadFloorRegistrationContext.Create(modelPath, floorModel, "2F", new PointModel(), 1); throw new Exception("Referenced floor accepted."); } catch (InvalidDataException) { }
        try { floorContext.SetSourceDatum(new PointModel(1, 1), new PointModel(1, 1)); throw new Exception("Coincident datum accepted."); } catch (InvalidDataException) { }
        floorContext.SetSourceDatum(new PointModel(700000, 40000), new PointModel(700000, 42000));
        Check(Math.Abs(floorContext.Alignment.ToModel(new PointModel(700000, 42000)).X - 2000) < 1e-7, "Direction point defines model positive X.");
        floorContext.RegionMin = new PointModel(699000, 39000); floorContext.RegionMax = new PointModel(703000, 45000);
        floorContext.RegionPolygon = new List<PointModel> { new PointModel(699000,39000), new PointModel(703000,39000), new PointModel(703000,45000), new PointModel(699000,45000) };
        floorContext.WallCandidates.Add(new CadBuildingProbeEntity { Handle = "W1", DxfName = "TCH_WALL" });
        floorContext.ValidateCapture();
        var placement = CadOpeningPlacement.Create(floorContext, "W1", new PointModel(700000,40000), new PointModel(700000,44000),
            new PointModel(700000,41000), new PointModel(700000,41800), 800);
        Check(Math.Abs(placement.ModelCenter.X-1400) < 1e-7 && Math.Abs(placement.ModelCenter.Y) < 1e-7
            && placement.DistanceFromWallStart == 1400, "Manual opening placement must share floor rotation and datum.");
        var reversed = CadOpeningPlacement.Create(floorContext, "W1", new PointModel(700000,44000), new PointModel(700000,40000),
            new PointModel(700000,41800), new PointModel(700000,41000), 800);
        Check(Math.Abs(reversed.ModelCenter.X-placement.ModelCenter.X) < 1e-7 && reversed.DistanceFromWallStart == 2600,
            "Reversing picks preserves world position while distance follows wall direction.");
        Action<Action> rejectsPlacement = action => { try { action(); throw new Exception("Invalid placement accepted."); } catch (InvalidDataException) { } };
        rejectsPlacement(() => placement.Revalidate(floorContext, 900));
        rejectsPlacement(() => CadOpeningPlacement.Create(floorContext,"OTHER",placement.CadWallStart,placement.CadWallEnd,placement.CadOpeningStart,placement.CadOpeningEnd,800));
        rejectsPlacement(() => CadOpeningPlacement.Create(floorContext,"W1",placement.CadWallStart,placement.CadWallEnd,new PointModel(700010,41000),placement.CadOpeningEnd,800));
        rejectsPlacement(() => CadOpeningPlacement.Create(floorContext,"W1",placement.CadWallStart,placement.CadWallEnd,new PointModel(700000,43500),new PointModel(700000,44300),800));
        var placedRow = CadOpeningRegistration.FromProbe(probe).Single();
        placedRow.Include = true; placedRow.Code = "C01"; placedRow.TypeIndex = 0; placedRow.Sill = 900; placedRow.Confirmed = true; placedRow.Placement = placement;
        var located = CadOpeningRegistration.Build(probe,library,new[] { placedRow },true,floorContext);
        Check(located.Openings.Single().PlacementVerified && located.Openings.Single().SourceGeometry.Handle == "A1", "Confirmed placement and source geometry must survive export.");
        CadOpeningRegistration.Save(Path.Combine(output,"located.openings-register.json"),located);
        using (var stream = File.OpenRead(Path.Combine(output,"located.openings-register.json"))) {
            var saved = (CadOpeningRegistrationDocument)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(CadOpeningRegistrationDocument)).ReadObject(stream);
            Check(saved.Openings.Single().Placement.Revalidate(saved.Floor,800).HostSourceHandle == "W1", "Location must round-trip with host provenance."); }
        placedRow.Width = 900;
        rejectsPlacement(() => CadOpeningRegistration.Build(probe,library,new[] { placedRow },true,floorContext));
        var wallOnly = CadOpeningRegistration.Build(probe, library, new List<CadOpeningRegistrationRow>(), true, floorContext);
        Check(wallOnly.Floor.Storey.Id == "1F" && wallOnly.Openings.Count == 0 && wallOnly.Floor.WallCandidates.Count == 1, "Wall-only floor captures remain valid review queues.");
        CadOpeningRegistration.Save(Path.Combine(output, "floor-capture.json"), wallOnly);
        using (var stream = File.OpenRead(Path.Combine(output, "floor-capture.json"))) {
            var saved = (CadOpeningRegistrationDocument)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(CadOpeningRegistrationDocument)).ReadObject(stream);
            Check(saved.Floor.ReferenceStoreys.Single().Id == "2F" && saved.Floor.RegionPolygon.Count == 4 && saved.Floor.WallCandidates.Single().Handle == "W1", "Floor capture must round-trip provenance, standard references and region.");
            saved.Floor.ValidateCapture();
            Check(Math.Abs(saved.Floor.Alignment.ToModel(new PointModel(700000,42000)).X - 2000) < 1e-7, "Saved alignment must round-trip without re-centering.");
        }
        var workflowOutput = Path.GetFullPath(".artifacts/cad-floor-workflow-20261001/tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workflowOutput);
        var workflowModelPath = Path.Combine(workflowOutput, "model.json");
        var workflowModel = BuildingModelJson.LoadModel(modelPath); BuildingModelJson.SaveModel(workflowModelPath,workflowModel);
        Func<string,CadFloorRegistrationContext> workflowFloor = id => {
            var f = CadFloorRegistrationContext.Create(workflowModelPath,workflowModel,id,new PointModel(0,0),1);
            f.SetSourceDatum(new PointModel(100,200),new PointModel(110,200));
            f.RegionMin = new PointModel(0,0); f.RegionMax = new PointModel(1000,1000);
            f.RegionPolygon = new List<PointModel> { new PointModel(0,0),new PointModel(1000,0),new PointModel(1000,1000),new PointModel(0,1000) };
            return f;
        };
        var registry = CadFloorPlanRegistry.Load(workflowModelPath);
        registry.SaveDatum(workflowFloor("1F"));
        Check(CadFloorPlanRegistry.Load(workflowModelPath).FindDatum("1F").Alignment.CadBase.X == 100, "Base picks persist before framing.");
        var firstCapture = CadFloorPlanCapture.FromProbe(workflowFloor("1F"),probe);
        Check(firstCapture.Openings.Single().Code == null && firstCapture.Openings.Single().Sill == null, "Unknown code and sill must remain missing.");
        Check(firstCapture.Openings.Single().ModelCode=="C0822" && firstCapture.Openings.Single().ModelKind=="窗", "Missing labels and types must have usable defaults.");
        var openingProbe=probe.Entities.First(e=>e.Handle=="A1");
        openingProbe.Fields.Add(CadBuildingProbeRules.Field("OpeningCode",false,null,"Missing own label"));
        Check(CadFloorPlanCapture.FromProbe(workflowFloor("1F"),probe).Openings.Single().Code==null,"Diagnostic dashes must not be treated as CAD codes.");
        openingProbe.Fields.RemoveAll(f=>f.Name=="OpeningCode");
        openingProbe.Fields.Add(CadBuildingProbeRules.Field("OpeningCode",true,"M01",null));
        Check(CadFloorPlanCapture.FromProbe(workflowFloor("1F"),probe).Openings.Single().ModelKind=="门","Successfully read numbered openings must infer a default without confirmation.");
        openingProbe.Fields.RemoveAll(f=>f.Name=="OpeningCode");
        firstCapture.Openings.Single().Code = "C01"; firstCapture.Openings.Single().Kind = "窗";
        registry.SaveFloor(firstCapture);
        Check(registry.BuildSchedule(workflowModel).Single().Quantity == 2, "An independent floor and its real standard reference each count once.");
        Check(registry.BuildSchedule(workflowModel).Single().SillHeightSuppressed, "Unknown sill must not become an asserted zero or 900.");
        var basementProbe = new CadBuildingProbeDocument { DrawingFingerprint = probe.DrawingFingerprint, DrawingPath = probe.DrawingPath,
            Entities = new List<CadBuildingProbeEntity> { new CadBuildingProbeEntity { Handle = "B9", DxfName = "TCH_OPENING",
                Fields = new List<CadProbeField> { CadBuildingProbeRules.Field("Width",true,800d,null),CadBuildingProbeRules.Field("Height",true,2200d,null),CadBuildingProbeRules.Field("OpeningCode",true,"C01",null) } } } };
        var basementCapture = CadFloorPlanCapture.FromProbe(workflowFloor("B1"),basementProbe);
        basementCapture.Openings.Single().Kind = "窗"; basementCapture.Openings.Single().Sill = 0;
        registry.SaveFloor(basementCapture);
        var mergedSchedule = CadFloorPlanRegistry.Load(workflowModelPath).BuildSchedule(workflowModel);
        Check(mergedSchedule.Sum(x=>x.Quantity) == 3 && mergedSchedule.Count == 2, "Multiple floors accumulate while zero and unknown sill remain distinct.");
        Check(mergedSchedule.Single(x=>x.SillHeightFromCadRegistration).SillHeight == 0, "Explicit zero must survive schedule creation.");
        Check(registry.Floors.Count == 2, "Saving another floor keeps the first floor.");
        firstCapture.Openings.Add(new CadFloorOpeningItem { SourceHandle = "A3",Code = "C01",Kind = "窗",Width = 800,Height = 2200 });
        registry.SaveFloor(firstCapture);
        Check(registry.Floors.Count == 2 && registry.BuildSchedule(workflowModel).Sum(x=>x.Quantity) == 5, "Recapturing replaces a floor without double counting prior instances.");
        var duplicateCapture = CadFloorPlanCapture.FromProbe(workflowFloor("B1"),probe);
        duplicateCapture.Openings.Single().Code = "C01";
        rejectsPlacement(() => registry.SaveFloor(duplicateCapture));
        Check(CadFloorPlanRegistry.Load(workflowModelPath).BuildSchedule(workflowModel).Sum(x=>x.Quantity) == 5, "Rejected cross-floor duplicates preserve the saved table.");
        var sillEdits = registry.BuildSchedule(workflowModel);
        var unknownSill = sillEdits.Single(x=>x.SillHeightSuppressed);
        BatchPdfPublisher.Services.DoorWindowElevationSuggestionService.Apply(unknownSill);
        Check(unknownSill.SillHeightSuppressed, "Elevation suggestions must preserve unknown registered sill.");
        var zeroSill = sillEdits.Single(x=>x.SillHeightFromCadRegistration);
        BatchPdfPublisher.Services.DoorWindowElevationSuggestionService.Apply(zeroSill);
        Check(zeroSill.SillHeight == 0, "Elevation suggestions must preserve explicit registered zero.");
        unknownSill.Code = "立面显示编号"; unknownSill.SillHeight = 875; unknownSill.SillHeightSuppressed = false;
        registry.UpdateScheduleSills(workflowModel,sillEdits);
        var reloadedSills = CadFloorPlanRegistry.Load(workflowModelPath).BuildSchedule(workflowModel);
        Check(reloadedSills.Single(x=>x.Quantity==4).SillHeight == 875 && reloadedSills.Single(x=>x.Quantity==4).Code == "C01", "MCLM sill edits return to source floor records without replacing CAD codes.");
        unknownSill.SillHeight = -1;
        rejectsPlacement(()=>registry.UpdateScheduleSills(workflowModel,sillEdits));
        Check(CadFloorPlanRegistry.Load(workflowModelPath).BuildSchedule(workflowModel).Single(x=>x.Quantity==4).SillHeight==875, "Invalid sill edits preserve the entire previous registration.");
        workflowModel.Storeys.RemoveAll(s=>s.Id=="2F");
        Check(registry.BuildSchedule(workflowModel).Sum(x=>x.Quantity) == 3, "Changed standard references use current model floors, not stale snapshot counts.");
        StudioLaunch.RememberActiveModel(workflowOutput,workflowModelPath);
        Check(StudioLaunch.ActiveModelPath(workflowOutput,"unrelated-name") == workflowModelPath, "CAD reuses the actual open model, regardless of project name.");
        var switchedModelPath = Path.Combine(workflowOutput,"另存模型.json"); BuildingModelJson.SaveModel(switchedModelPath,workflowModel);
        StudioLaunch.RememberActiveModel(workflowOutput,switchedModelPath);
        Check(StudioLaunch.ActiveModelPath(workflowOutput,"unrelated-name") == switchedModelPath, "Open/save-as updates CAD's model path.");
        Check(CadFloorPlanRegistry.Load(switchedModelPath).Floors.Count == 0, "A different model must not inherit another model's registration.");
        var reviewWindow = new CadFloorOpeningReviewWindow(firstCapture);
        var reviewContent = (FrameworkElement)reviewWindow.Content;
        reviewContent.Measure(new Size(960,590)); reviewContent.Arrange(new Rect(new Size(960,590))); reviewContent.UpdateLayout();
        Check(!Descendants(reviewContent).OfType<DataGrid>().Single().Columns.OfType<DataGridComboBoxColumn>().Any()
            && !Descendants(reviewContent).OfType<ComboBox>().Any(), "Registration must not ask users to choose or confirm door/window types.");
        reviewWindow.Close();
        var actionModelPath = Path.Combine(workflowOutput,"逐层按钮模型.json"); BuildingModelJson.SaveModel(actionModelPath,BuildingModelJson.LoadModel(modelPath));
        var pickedFloors = new List<string>(); var capturedFloors = new List<string>(); var cancelCapture = false;
        var actionWindow = new CadFloorRegistrationWindow(actionModelPath,
            (owner,f) => { pickedFloors.Add(f.Storey.Id); f.SetSourceDatum(new PointModel(100,200),new PointModel(110,200)); return true; },
            (owner,f) => { capturedFloors.Add(f.Storey.Id); f.RegionMin = new PointModel(0,0); f.RegionMax = new PointModel(1000,1000);
                f.RegionPolygon = new List<PointModel> { new PointModel(0,0),new PointModel(1000,0),new PointModel(1000,1000),new PointModel(0,1000) };
                if (cancelCapture) { f.RegionMin.X = -999; return null; }
                var p = new CadBuildingProbeDocument { DrawingFingerprint = f.Storey.Id,Entities = probe.Entities };
                var c = CadFloorPlanCapture.FromProbe(f,p); c.Openings.Single().Code = "C01"; return c; });
        var actionContent = (FrameworkElement)actionWindow.Content;
        actionContent.Measure(new Size(1060,560)); actionContent.Arrange(new Rect(new Size(1060,560))); actionContent.UpdateLayout();
        var actionTable = Descendants(actionContent).OfType<DataGrid>().Single();
        Func<int,string,Button> rowAction = (index,label) => Descendants(actionTable.Columns.Single(c=>(c.Header as string)==label).GetCellContent(actionTable.Items[index])).OfType<Button>().Single();
        foreach (var index in new[] { 0,1 }) {
            rowAction(index,"拾取基点").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            rowAction(index,"框选登记平面").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Check(pickedFloors.SequenceEqual(new[] { "B1","1F" }) && capturedFloors.SequenceEqual(pickedFloors), "Each row button must dispatch its own model floor.");
        Check(CadFloorPlanRegistry.Load(actionModelPath).Floors.Count == 2, "Two row captures save two independent floors.");
        Check(!rowAction(2,"拾取基点").IsEnabled && ((CadFloorRegistrationRow)actionTable.Items[2]).Status.Contains("已登记"), "Standard references reuse source capture without another countable registration.");
        cancelCapture = true; rowAction(0,"框选登记平面").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(CadFloorPlanRegistry.Load(actionModelPath).Find("B1").Floor.RegionMin.X == 0 && ((CadFloorRegistrationRow)actionTable.Items[0]).Status.Contains("已登记"), "Cancelling a row recapture keeps saved geometry and registration state.");
        actionWindow.Close();
        foreach (var size in new[] { new Size(1060, 560), new Size(820, 460) }) {
            var floorWindow = new CadFloorRegistrationWindow(); floorWindow.LoadModel(modelPath);
            var content = (FrameworkElement)floorWindow.Content; ((Grid)content).Background = floorWindow.Background;
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            Check(Descendants(content).OfType<DataGrid>().Single().Items.Count == 3, "Floor chooser shows basement and standard references.");
            Check(Descendants(content).OfType<DataGrid>().Single().ActualHeight >= 130, "Floor chooser retains a usable table at its minimum size.");
            Check(Descendants(content).OfType<DataGrid>().Single().Columns.Take(5).All(c => c.ActualWidth >= 90), "Floor values must retain readable column widths.");
            Check(!Descendants(content).OfType<Button>().Any(b => (b.Content as string)?.Contains("选择已有建筑模型") == true), "Registration must use the active model without another file picker.");
            foreach (var button in Descendants(content).OfType<Button>().Where(b => b.Content is string)) {
                var origin = button.TranslatePoint(new Point(), content);
                Check(origin.X >= -1 && origin.Y >= -1 && origin.X + button.ActualWidth <= size.Width + 1 && origin.Y + button.ActualHeight <= size.Height + 1, "Floor action clipped: " + button.Content); }
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96,96,PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output,"floor-" + size.Width + ".png"))) encoder.Save(stream);
            floorWindow.Close();
        }
        var path = Path.Combine(output, "test.openings-register.json"); CadOpeningRegistration.Save(path, result); CadOpeningRegistration.Save(path, result);
        Check(File.Exists(path + ".bak") && !Directory.GetFiles(output, "*.tmp").Any(), "Atomic export and backup.");
        foreach (var size in new[] { new Size(1080, 630), new Size(720, 400) })
        {
            var window = new TianzhengOpeningRegistrationWindow(probe, library,
                new[] { new CadOpeningSillEntry { Code = "C01", Height = 1000, TypeIndex = 1, Source = "立面已保存参数" } });
            var content = (FrameworkElement)window.Content; ((Grid)content).Background = window.Background;
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            var table = Descendants(content).OfType<DataGrid>().Single();
            var combo = Descendants(content).OfType<ComboBox>().Single(); combo.SelectedIndex = 1;
            var uiRow = (CadOpeningRegistrationRow)table.SelectedItem;
            Check(uiRow.Code == "c01" && uiRow.TypeIndex == 1 && uiRow.Sill == 1000 && !uiRow.Confirmed,
                "Choosing a style must prefill only, requiring explicit confirmation.");
            var confirmation = Descendants(content).OfType<CheckBox>().Single(c => c.Content is string && ((string)c.Content).StartsWith("已核对本"));
            confirmation.IsChecked = true; Check(uiRow.Confirmed, "Confirmation must reach the reviewed record.");
            Descendants(content).OfType<TextBox>().First(b => b.Text == "800").Text = "850";
            Check(uiRow.Width == 850 && !uiRow.Confirmed && confirmation.IsChecked == false, "Editing invalidates confirmation.");
            Check(ReferenceEquals(table.SelectedItem, uiRow), "Edits must preserve selection.");
            content.UpdateLayout();
            Check(table.ActualHeight > 100, "Data table must retain room at compact sizes.");
            foreach (var button in Descendants(content).OfType<Button>().Where(b => b.Content is string)) {
                var origin = button.TranslatePoint(new Point(), content);
                Check(origin.X >= -1 && origin.Y >= -1 && origin.X + button.ActualWidth <= size.Width + 1
                    && origin.Y + button.ActualHeight <= size.Height + 1, "Action outside window: " + button.Content); }
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, "review-" + size.Width + ".png"))) encoder.Save(stream);
            window.Close();
        }
        var pickCount = 0;
        var placementWindow = new TianzhengOpeningRegistrationWindow(probe,library,null,floorContext,
            (owner, selected) => ++pickCount == 1 ? placement : null);
        var placementContent = (FrameworkElement)placementWindow.Content;
        placementContent.Measure(new Size(720,400)); placementContent.Arrange(new Rect(new Size(720,400))); placementContent.UpdateLayout();
        var placementTable = Descendants(placementContent).OfType<DataGrid>().Single();
        var selectedPlacementRow = (CadOpeningRegistrationRow)placementTable.SelectedItem;
        var pickButton = Descendants(placementContent).OfType<Button>().Single(b => (b.Content as string) == "在 CAD 拾取宿主墙与洞口定位");
        selectedPlacementRow.Confirmed = true;
        pickButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(selectedPlacementRow.Placement != null && !selectedPlacementRow.Confirmed, "Picking location requires renewed confirmation.");
        selectedPlacementRow.Confirmed = true;
        pickButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ReferenceEquals(selectedPlacementRow.Placement,placement) && selectedPlacementRow.Confirmed,
            "Cancelling a pick must preserve prior location, confirmation and row selection.");
        placementWindow.Close();
        CadFloorModelGenerationTests.Run();
        CadOpeningScheduleRulesTests.Run();
        Console.WriteLine("PASS: active model path, multi-floor registration, model generation/undo/reimport, safe jamb recognition, standard references, schedule/sill rollback and WPF actions.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested; }
    }
}
