using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunComponentPackageCheckAsync()
    {
        var before=BuildingModelJson.ToJson(_session.Model);var root=Path.GetFullPath(".artifacts/component-packages");
        var run=Path.Combine(root,"ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(run);
        var library=new ComponentAssetLibrary(Path.Combine(run,"library"));
        var modelPath=Path.Combine(run,"model.json");BuildingModelJson.SaveModel(modelPath,_session.Model);var modelBytes=File.ReadAllBytes(modelPath);
        var planPath=Path.Combine(root,"window.wlplan.json");var meshPath=Path.Combine(root,"window.glb");
        var planBytes=File.ReadAllBytes(planPath);var meshBytes=File.ReadAllBytes(meshPath);
        var catalog=new ComponentCatalog(library.Root);
        var managerBefore=new ComponentLibraryWindow(_session.Model,modelPath,library) {Width=Width,Height=Height};managerBefore.Show(this);
        await managerBefore.ReloadAsync();
        var record=catalog.SavePlan(ComponentPlanSymbols.Load(planPath));
        for(var i=0;i<100&&managerBefore.CatalogCount!=1;i++)await Task.Delay(25);
        if(managerBefore.CatalogCount!=1)throw new Exception("CAD 入库未自动同步到已打开的三维图库。");
        managerBefore.SelectCatalog(record.AssetId);await Task.Delay(150);
        await SnapshotComponentWindow(managerBefore,Path.Combine(root,$"shared-cad-only-{(int)Width}x{(int)Height}.png"));managerBefore.Close();
        var editor=new ComponentPairingWindow(library,record) {Width=Width,Height=Height};editor.Show(this);
        ComponentAsset asset;
        try {
            if(editor.Plan?.Code!="C1216")throw new Exception("共享 CAD 平面未预载。");
            try{await editor.LoadPlanAsync(planPath);throw new Exception("共享模式仍可重新导入 CAD 平面。");}catch(InvalidOperationException){}
            await editor.LoadMeshAsync(meshPath);
            for(var i=0;i<200&&(!editor.Volume.FrameRendered||editor.Isolated.FaceCount==0);i++)await Task.Delay(25);
            if(editor.Parts.ItemCount!=5||!editor.Volume.FrameRendered||editor.Isolated.FaceCount==0)throw new Exception("实际部件或 GPU 预览为空。");
            var old=editor.Mesh;
            try{await editor.LoadMeshAsync(Path.Combine(root,"window.wlopkg"));throw new Exception("坏 GLB 被接受。");}catch(InvalidDataException){}
            if(!ReferenceEquals(old,editor.Mesh))throw new Exception("失败载入丢失有效模型。");
            editor.BindPlanAt(new PointModel(500,0),2);
            editor.Parts.SelectedIndex=1;editor.BindPlanAt(new PointModel(500,0),2);
            if(((ComponentPartDefinition)editor.Parts.SelectedItem!).PlanPrimitives.Count!=0)throw new Exception("二维图元重复绑定。");
            editor.Parts.SelectedIndex=0;editor.BindPlanAt(new PointModel(500,0),2);
            if(((ComponentPartDefinition)editor.Parts.SelectedItem!).PlanPrimitives.Count!=0)throw new Exception("再次点选未取消绑定。");
            editor.BindPlanAt(new PointModel(500,0),2);
            editor.OriginX.Text="bad";
            var draftPath=Path.Combine(run,"saved.wlodraft");await editor.SaveDraftAsync(draftPath);await editor.LoadDraftAsync(draftPath);
            if(editor.OriginX.Text!="bad"||editor.Parts.ItemCount!=5)throw new Exception("草稿未保留未完成输入或几何。");
            try{await editor.PublishAsync();throw new Exception("无效基点被发布。");}catch(InvalidDataException){}
            editor.OriginX.Text="0";editor.PartName.Text="左框";await editor.RefreshAlignedAsync();
            asset=await editor.PublishAsync();
            if(!asset.IsExternal||asset.Manifest.External.Parts[0].Name!="左框"||!asset.Manifest.External.Parts[0].PlanPrimitives.SequenceEqual(new[]{0}))throw new Exception("发布未保留部件名称或二维绑定。");
            if(!ReferenceEquals(await editor.PublishAsync(),asset))throw new Exception("无修改再次发布产生修订。");
            editor.PartName.Text="左框第二版";var second=await editor.PublishAsync();
            if(second.Manifest.AssetId!=asset.Manifest.AssetId||second.Manifest.Revision!=2)throw new Exception("新修订不是原资源身份。");
            await editor.SaveDraftAsync(draftPath);await editor.LoadDraftAsync(draftPath);
            editor.PartName.Text="左框第三版";var third=await editor.PublishAsync();
            if(third.Manifest.AssetId!=asset.Manifest.AssetId||third.Manifest.Revision!=3||third.Manifest.External.Parts[0].PartId!=asset.Manifest.External.Parts[0].PartId)throw new Exception("恢复草稿丢失资源或部件身份。");
            if(catalog.Load().Records.Count!=1||catalog.Get(record.AssetId).ModelRevision!=3||third.Manifest.AssetId!=record.AssetId)throw new Exception("补充三维产生了另一条资源记录。");
            asset=third;
            await Task.Delay(150);
            foreach(var button in new[]{editor.Publish})if(button.TranslatePoint(new Point(),editor) is not Point p||p.Y+button.Bounds.Height>editor.Bounds.Height||button.Bounds.Height<32)throw new Exception("小窗口底栏裁切。");
            await SnapshotComponentWindow(editor,Path.Combine(root,$"pairing-{(int)Width}x{(int)Height}.png"));
        }finally{editor.CloseForCheck();}
        library.Import(Path.Combine(root,"chair.wlopkg"));
        library.Save("旧参数窗",new OpeningTypeModel {Code="C0915",Kind="窗",Width=900,Height=1500});
        var manager=new ComponentLibraryWindow(_session.Model,modelPath,library) {Width=Width,Height=Height};manager.Show(this);
        try {
            await manager.ReloadAsync();
            for(var i=0;i<100&&(manager.ExternalCount!=2||manager.CatalogCount!=2);i++)await Task.Delay(25);
            if(manager.ExternalCount!=2||manager.CatalogCount!=2)throw new Exception($"同一资源修订重复显示或外部椅兼容失败：模型 {manager.ExternalCount} / 共享 {manager.CatalogCount}。");
            var deleteHead=catalog.Get(record.AssetId);var deleted=catalog.SetDeleted(record.AssetId,deleteHead.Version,true);
            for(var i=0;i<100&&manager.CatalogCount!=1;i++)await Task.Delay(25);
            await manager.ReloadAsync();if(manager.CatalogCount!=1||manager.ExternalCount!=1||catalog.Load().Records.Any(r=>r.AssetId==record.AssetId))throw new Exception("CAD 删除未同步到三维图库，或旧模型包复活了已删除资源。");
            catalog.SetDeleted(record.AssetId,deleted.Version,false);await manager.ReloadAsync();if(manager.CatalogCount!=2||manager.ExternalCount!=2)throw new Exception("恢复共享资源后模型配对或稳定身份丢失。");
            manager.SelectExternal("C1216");var copy=await manager.RetainSelectedAsync();
            if(copy.Sha256!=asset.Sha256||!File.ReadAllBytes(modelPath).SequenceEqual(modelBytes))throw new Exception("项目副本修改语义模型。");
            manager.Search.Text="不存在的资源";await Task.Delay(100);if(manager.Assets.ItemCount!=0)throw new Exception("空搜索未清空。");
            manager.Search.Text="";await Task.Delay(100);manager.SelectExternal("C1216");await Task.Delay(250);
            foreach(var preview in manager.GetVisualDescendants().OfType<ComponentShapePreview>())if(preview.Bounds.Height>10&&preview.FaceCount==0)throw new Exception("图库实际缩略图未渲染。");
            await SnapshotComponentWindow(manager,Path.Combine(root,$"library-{(int)Width}x{(int)Height}.png"));
        }finally{manager.Close();}
        var reopened=new ComponentPairingWindow(library,catalog.Get(record.AssetId));reopened.Show(this);
        try{await reopened.LoadExistingAsync(asset);if(reopened.Parts.ItemCount!=5||reopened.Plan?.Code!="C1216")throw new Exception("已配对资源不能直接继续编辑。");}
        finally{reopened.CloseForCheck();}
        var doorPlan=ComponentPlanSymbols.Load(Path.Combine(root,"door.wlplan.json"));
        var doorRecord=catalog.SavePlan(doorPlan);
        var doorEditor=new ComponentPairingWindow(library,doorRecord) {Width=Width,Height=Height};doorEditor.Show(this);
        ComponentAsset doorAsset;
        try {
            await doorEditor.LoadMeshAsync(Path.Combine(root,"door.glb"));
            if(doorEditor.CadPart.ItemCount!=3||doorEditor.Plan?.Name!=doorPlan.Name||doorEditor.Plan.Code!="")throw new Exception("CAD 部件或未定编号资源未同步。");
            var frameParts=doorEditor.Parts.ItemsSource!.Cast<ComponentPartDefinition>().Where(p=>p.Name.StartsWith("frame_")).ToArray();
            doorEditor.Parts.SelectedItems!.Clear();foreach(var part in frameParts)doorEditor.Parts.SelectedItems.Add(part);doorEditor.MergeParts();
            if(doorEditor.Parts.ItemCount!=3||((ComponentPartDefinition)doorEditor.Parts.SelectedItem!).MeshNodes.Count!=3)throw new Exception("门套的多个实际节点不能合并。");
            doorEditor.BindCadPart(doorPlan.Parts[0].PartId);
            doorEditor.BindCadPart(null);
            var unlinkedFrame=(ComponentPartDefinition)doorEditor.Parts.SelectedItem!;
            if(unlinkedFrame.PlanPartId!=null||unlinkedFrame.PlanPrimitives.Count!=0)throw new Exception("取消整组关联后未释放二维图元。");
            doorEditor.BindCadPart(doorPlan.Parts[0].PartId);
            doorEditor.Parts.SelectedItem=doorEditor.Parts.ItemsSource!.Cast<ComponentPartDefinition>().Single(p=>p.Name=="leaf");doorEditor.BindCadPart(doorPlan.Parts[1].PartId);
            doorEditor.Parts.SelectedItem=doorEditor.Parts.ItemsSource!.Cast<ComponentPartDefinition>().Single(p=>p.Name=="handle");
            try{doorEditor.BindCadPart(doorPlan.Parts[0].PartId);throw new Exception("已关联 CAD 部件被重复绑定。");}catch(InvalidDataException){}
            try{doorEditor.BindCadPart(doorPlan.Parts[2].PartId);throw new Exception("开启弧被关联成三维实体。");}catch(InvalidDataException){}
            doorAsset=await doorEditor.PublishAsync();
            if(doorAsset.Manifest.External.Code!=""||doorAsset.Manifest.OpeningType.Code!="M0921"||doorAsset.Manifest.External.Parts.Count(p=>p.PlanPartId!=null)!=2)throw new Exception("发布丢失部件关系或提前改变资源编号。");
            var doorDraft=Path.Combine(run,"door.wlodraft");await doorEditor.SaveDraftAsync(doorDraft);await doorEditor.LoadDraftAsync(doorDraft);
            if(doorEditor.Parts.ItemsSource!.Cast<ComponentPartDefinition>().Count(p=>p.PlanPartId!=null)!=2)throw new Exception("恢复草稿丢失 CAD 部件关联。");
            doorEditor.Parts.SelectedItem=doorEditor.Parts.ItemsSource!.Cast<ComponentPartDefinition>().Single(p=>p.PlanPartId==doorPlan.Parts[1].PartId);
            await Task.Delay(150);await SnapshotComponentWindow(doorEditor,Path.Combine(root,$"decoration-pairing-{(int)Width}x{(int)Height}.png"));
        }finally{doorEditor.CloseForCheck();}
        var doorReopen=new ComponentPairingWindow(library,catalog.Get(doorRecord.AssetId));doorReopen.Show(this);
        try{await doorReopen.LoadExistingAsync(doorAsset);if(doorReopen.Parts.ItemsSource!.Cast<ComponentPartDefinition>().Count(p=>p.PlanPartId!=null)!=2)throw new Exception("重开资源丢失 CAD 部件关联。");}
        finally{doorReopen.CloseForCheck();}
        if(BuildingModelJson.ToJson(_session.Model)!=before||!File.ReadAllBytes(planPath).SequenceEqual(planBytes)||!File.ReadAllBytes(meshPath).SequenceEqual(meshBytes))throw new Exception("图库改了工程或来源文件。");
        Console.WriteLine("COMPONENT_PACKAGE_UI_OK sharedDelete liveDeleteSync noResurrection restorePair sharedCatalog liveCadSync CADOnlyPreview preloadedPlan modelOnlyImport stableIdentity oneRecordPerResource reopenExisting realParts nativeGLB GPU nativePlanBinding drafts invalidFrame publish resumedRevisions mixedLibrary isolatedPreview projectCopy emptySearch smallBounds decorationParts mergeFrame semanticBinding optionalCode unchangedModelAndSources");
    }
    private static async Task SnapshotComponentWindow(Window window,string path)
    {
        var visual=ElementComposition.GetElementVisual(window)!;var bitmap=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
        bitmap.Save(path,PngBitmapEncoderOptions.Default);
    }
}
