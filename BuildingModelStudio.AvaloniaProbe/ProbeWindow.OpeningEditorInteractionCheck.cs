using Avalonia.Controls;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunOpeningEditorInteractionCheckAsync()
    {
        Directory.CreateDirectory(".artifacts/opening-editor");
        await CheckCombinedPanelMaterialsAsync();
        await CheckLegacyGlazingAsync();
        var shortcutOpening=new OpeningModel {Id="shortcuts",Code="MLC3030",Kind="门联窗",Width=3000,Height=3000};
        var shortcutType=OpeningConstruction.Default(shortcutOpening);shortcutType.DivisionPreset="单格";shortcutType.CustomCellLayout=null;
        var shortcutWindow=new OpeningEditorWindow(shortcutOpening,shortcutType,1,null);
        if(Width<1300){shortcutWindow.Width=1120;shortcutWindow.Height=720;}
        Exception? shortcutFailure=null;
        shortcutWindow.Opened+=async(_,_)=>{try{await shortcutWindow.RunShortcutCheckAsync();}catch(Exception ex){shortcutFailure=ex;shortcutWindow.Close();}};
        await shortcutWindow.ShowDialog(this);if(shortcutFailure!=null)throw shortcutFailure;
        foreach(var combined in new[]{false,true})
        {
            var sample=new OpeningModel {Id="default",Code=combined?"MLC7630":"C1121",Kind=combined?"门联窗":"窗",Width=combined?7600:1100,Height=combined?3000:2100};
            var defaults=new OpeningEditorWindow(sample,OpeningConstruction.Default(sample),1,null);
            if(Width<1300){defaults.Width=1120;defaults.Height=720;}
            Exception? defaultFailure=null;
            defaults.Opened+=async(_,_)=>{try{await defaults.RunDefaultConstructionCheckAsync(combined);}catch(Exception ex){defaultFailure=ex;defaults.Close();}};
            await defaults.ShowDialog(this);if(defaultFailure!=null)throw defaultFailure;
        }
        var opening=new OpeningModel {Id="precision",Code="MLC01B",Kind="门联窗",Width=7599.672341,Height=3000};
        var type=OpeningConstruction.Default(opening);type.DivisionPreset="单格";
        var window=new OpeningEditorWindow(opening,type,6,null);
        if(Width<1300){window.Width=1120;window.Height=720;}
        Exception? failure=null;
        window.Opened+=async(_,_)=>{try{await window.RunInteractionCheckAsync();}catch(Exception ex){failure=ex;window.Close();}};
        await window.ShowDialog(this);if(failure!=null)throw failure;
        await CheckEditorCollectionsAsync();
        await CheckRoundedFitApplyAsync();
        await CheckPendingOpeningSizesAsync();
    }

    private async Task CheckCombinedPanelMaterialsAsync()
    {
        var type=OpeningPlacementCatalog.BuiltIns().Single(e=>e.Type.Code=="MLC2424").Type;
        var opening=new OpeningModel {Id="mlc2424-panels",Code=type.Code,Kind=type.Kind,Width=type.Width,Height=type.Height};
        var editor=new OpeningEditorWindow(opening,type,1,null);
        if(Width<1300){editor.Width=1120;editor.Height=720;}
        Exception? failure=null;
        editor.Opened+=async(_,_)=>{try{await editor.RunCombinedPanelMaterialsCheckAsync();}catch(Exception ex){failure=ex;editor.Close();}};
        var result=await editor.ShowDialog<OpeningEditResult?>(this);
        if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        if(result==null)throw new InvalidOperationException("门联窗编辑器未返回应用的类型。");

        var model=SampleModelFactory.CreateEmptyModel("MLC2424 分格");
        model.OpeningTypes.Add(result.Type);
        var saved=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model)).OpeningTypes.Single();
        var parts=OpeningConstruction.Build(opening,saved,200);
        if(parts.Count(p=>p.Kind=="glass")!=3 || parts.Count(p=>p.Kind=="door" && p.Cell.IsDoor)!=1)
            throw new InvalidOperationException("门联窗保存回读后分格材料错误。");
        Console.WriteLine("OPENING_COMBINED_PANEL_MATERIALS_OK MLC2424 fourPanels split undo clear intentionally batchGlass preservesOpening preview apply JSON");
    }

    private async Task CheckLegacyGlazingAsync()
    {
        var originalSession=_session;var originalSelection=_selectedId;
        var model=new BuildingModelDocument {Storeys=new(){new StoreyModel {Id="1F",Height=3400}},
            Walls=new(){new WallModel {Id="host",StoreyId="1F",X2=5400,Thickness=200}},
            Openings=new(){new OpeningModel {Id="legacy-mlc",Code="MLC2730",Kind="门联窗",HostWallId="host",Offset=2700,Width=2700,Height=3000}}};
        var type=new OpeningTypeModel {Code="MLC2730",Kind="门联窗",ElevationType="门联窗",Width=2700,Height=3000,HasInstallationGap=false,
            DivisionPreset="自定义",CustomCellLayout="0,0,600,2400,固定,0,0,无|0,2400,600,3000,固定,0,0,无|600,0,1350,2400,左平开,1,0,玻璃|600,2400,1350,3000,固定,0,0,玻璃|1350,0,2100,2400,右平开,1,0,玻璃|1350,2400,2100,3000,固定,0,0,玻璃|2100,0,2700,2400,固定,0,0,无|2100,2400,2700,3000,固定,0,0,无"};
        model.OpeningTypes.Add(type);
        try
        {
            _session=new(model);await RefreshModelAsync("旧 MLC2730 补玻璃测试");
            var before=BuildingModelJson.ToJson(_session.Model);
            _openingEditorCheckDriver=w=>w.RunLegacyGlazingCheckAsync();
            await OpenOpeningEditorAsync("legacy-mlc");
            var after=BuildingModelJson.ToJson(_session.Model);
            var loaded=BuildingModelJson.FromJson(after);
            if(loaded.OpeningTypes.Single().CustomCellLayout!=type.CustomCellLayout.Replace(",无",",玻璃")
                ||!_session.Undo()||BuildingModelJson.ToJson(_session.Model)!=before
                ||!_session.Redo()||BuildingModelJson.ToJson(_session.Model)!=after)
                throw new InvalidOperationException("补玻璃写入了其他分格属性，或无法在模型中一步撤销重做。");
            Console.WriteLine("OPENING_LEGACY_GLAZING_OK MLC2730 fourEmpty toEightGlass preservesLayout andHinges preview apply reload undo redo");
        }
        finally
        {
            _openingEditorCheckDriver=null;_session=originalSession;_selectedId=originalSelection;
            await RefreshModelAsync("恢复模型");
        }
    }

    private async Task CheckRoundedFitApplyAsync()
    {
        var saved=_session;var selected=_selectedId;var conflictCheck=_openingSizeConflictCheck;
        var model=SampleModelFactory.CreateEmptyModel("MLC04B rounding");model.Storeys.RemoveAll(s=>s.Id!="1F");model.Storeys[0].Height=3000;
        model.Walls.Add(new(){Id="host",StoreyId="1F",X2=3768,Thickness=200});
        var opening=new OpeningModel {Id="rounding",HostWallId="host",Code="MLC04B",Kind="门联窗",Offset=1884,Width=3768,Height=3000};model.Openings.Add(opening);
        var type=OpeningConstruction.Default(opening);type.DivisionPreset="自定义";
        type.CustomCellLayout="0,0,1134,2200,固定,0,0,玻璃|1134,0,1884,2200,左平开,1,0,玻璃|1884,0,2634,2200,右平开,1,0,玻璃|2634,0,3768,2200,固定,0,0,玻璃|0,2200,1134,3000,固定,0,0,玻璃|1134,2200,2634,3000,固定,0,0,玻璃|2634,2200,3768,3000,固定,0,0,玻璃";
        model.OpeningTypes.Add(type);
        try
        {
            _session=new(model);await RefreshModelAsync("MLC04B 取整适配测试");_workspaces.SelectedIndex=1;SelectById(opening.Id);
            var before=BuildingModelJson.ToJson(_session.Model);
            _openingEditorCheckDriver=w=>w.RunRoundedFitApplyCheckAsync(false,Width<1300);await OpenOpeningEditorAsync(opening.Id);
            var applied=_session.Model.Openings.Single();var definition=OpeningConstruction.Resolve(_session.Model,applied);
            if(applied.Width!=3768||applied.Code!="MLC04B"||_session.Model.Walls.Single().X2!=3768
                ||BatchPdfPublisher.Models.DoorWindowElevationGeometryBuilder.ParseCellLayout(definition.CustomCellLayout).Count(c=>c.Bottom==2200&&c.Opening=="上悬")!=3)
                throw new InvalidOperationException("MLC04B 关闭后没有提交全部上悬格，或误改洞口／墙／编号。");
            var after=BuildingModelJson.ToJson(_session.Model);
            var folder=Path.GetFullPath(".artifacts/opening-rounded-fit");Directory.CreateDirectory(folder);var path=Path.Combine(folder,"model.json");
            BuildingModelJson.SaveModel(path,_session.Model);
            if(BuildingModelJson.ToJson(BuildingModelJson.LoadModel(path))!=after)throw new InvalidOperationException("MLC04B 保存重开丢失适配和分格。");
            if(!_session.Undo()||BuildingModelJson.ToJson(_session.Model)!=before||!_session.Redo()||BuildingModelJson.ToJson(_session.Model)!=after)
                throw new InvalidOperationException("MLC04B 适配应用未一步撤销重做。");
            _session.Undo();await RefreshModelAsync("真实超限测试");
            _openingEditorCheckDriver=w=>w.RunRoundedFitApplyCheckAsync(true,Width<1300);await OpenOpeningEditorAsync(opening.Id);
            if(BuildingModelJson.ToJson(_session.Model)!=before)throw new InvalidOperationException("真实超限拒绝后仍修改模型。");
            var prompted=false;_openingSizeConflictCheck=dialog=>{prompted=true;dialog.Close();return Task.CompletedTask;};
            var fields=new[]{"1884","3770","3000","0","0"}.Select(text=>new TextBox {Text=text}).ToArray();
            await ApplyOpeningGeometryAsync(opening.Id,fields,false,values=>values);
            if(prompted||_session.Model.Openings.Single().Width!=3768||_session.Model.Openings.Single().Code!="MLC04B")
                throw new InvalidOperationException("属性面板适配后的实际尺寸没有用于编号判断。");
            Console.WriteLine("OPENING_ROUNDED_FIT_APPLY_UI_OK MLC04B 3770to3768 dynamicExactWidth threeHungCells closes committed saveReload codeUnchanged wallUnchanged oneUndoRedo trueOversizeRetainsEditor explicitNotApplied");
        }
        finally{_openingEditorCheckDriver=null;_openingSizeConflictCheck=conflictCheck;_session=saved;await RefreshModelAsync("取整适配测试完成");SelectById(selected);}
    }

    private async Task CheckEditorCollectionsAsync()
    {
        var saved=_session;var conflict=_openingSizeConflictCheck;var selected=_selectedId;
        var model=SampleModelFactory.CreateEmptyModel("MLC01B collections");model.Storeys[0].Height=4000;
        model.Storeys.Add(new(){Id="2F",Height=4000,Elevation=4000,TemplateStoreyId="1F"});
        for(var i=0;i<3;i++){
            model.Walls.Add(new(){Id="host"+i,StoreyId="1F",X2=10000,Y1=i*5000,Y2=i*5000,Height=i==2?3500:4000});
            model.Openings.Add(new(){Id="opening"+i,HostWallId="host"+i,Code="MLC01B",Kind="门联窗",Width=7599.672341,Height=3000,Offset=5000});
        }
        model.OpeningTypes.Add(OpeningConstruction.Default(model.Openings[0]));
        try {
            _session=new(model);await RefreshModelAsync("集合分格检查");_workspaces.SelectedIndex=1;SelectById("opening0");
            var before=BuildingModelJson.ToJson(_session.Model);
            async Task Apply(string action,double height,bool single=false,bool reject=false){
                _openingSizeConflictCheck=dialog=>{
                    var button=dialog.GetVisualDescendants().OfType<Button>().Single(b=>b.Name==action);
                    if(!button.IsEnabled)throw new InvalidOperationException("分格集合并入按钮不可用。");
                    button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));return Task.CompletedTask;};
                _openingEditorCheckDriver=w=>w.RunCollectionApplyCheckAsync(height,single,reject);
                await OpenOpeningEditorAsync("opening0");
            }
            await Apply("MergeOpeningType",2900);
            var physical=StandardStoreyLayout.Materialize(_session.Model);
            if(physical.Openings.Count!=6||physical.Openings.Any(o=>o.Code!="MLC01B"||o.Height!=2900
                ||OpeningConstruction.Resolve(physical,o).GlassThickness!=12))throw new InvalidOperationException("MLC01B 并入集合没有应用全部草稿。");
            if(!_session.Undo()||BuildingModelJson.ToJson(_session.Model)!=before)throw new InvalidOperationException("MLC01B 并入未一步撤销。");
            await RefreshModelAsync("后缀集合检查");await Apply("CreateOpeningType",2900);
            physical=StandardStoreyLayout.Materialize(_session.Model);
            if(physical.Openings.Any(o=>o.Code!="MLC01BA"||o.Height!=2900)
                ||_session.Model.OpeningTypes.Single(t=>t.Code=="MLC01B").GlassThickness==12)throw new InvalidOperationException("后缀集合只修改一樘或影响旧类型。");
            if(!_session.Undo()||BuildingModelJson.ToJson(_session.Model)!=before)throw new InvalidOperationException("后缀集合未一步撤销。");
            await RefreshModelAsync("单樘后缀检查");await Apply("CreateOpeningType",2900,true);
            physical=StandardStoreyLayout.Materialize(_session.Model);
            if(physical.Openings.Count(o=>o.Code=="MLC01BA")!=2||physical.Openings.Count(o=>o.Code=="MLC01B")!=4)
                throw new InvalidOperationException("显式单樘范围未保留共用层或修改其他樘。");
            _session.Undo();await RefreshModelAsync("非法集合检查");await Apply("MergeOpeningType",3900,reject:true);
            if(BuildingModelJson.ToJson(_session.Model)!=before)throw new InvalidOperationException("拒绝合并后仍部分写入模型。");
            Console.WriteLine("OPENING_EDITOR_COLLECTION_UI_OK MLC01B sixInstances exactWidth editedMerge groupSuffix explicitSingle standardFloors preflightRetainsEditor invalidAtomic oneUndo");
        }finally{_openingEditorCheckDriver=null;_openingSizeConflictCheck=conflict;_session=saved;await RefreshModelAsync("集合分格检查完成");SelectById(selected);}
    }
}
