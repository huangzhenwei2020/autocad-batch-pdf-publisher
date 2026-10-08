using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatchPdfPublisher.BuildingModel;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;

internal static class ComponentPackageTests
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Reject(Action action,string message){try{action();}catch(Exception ex) when(ex is InvalidDataException or JsonException or IOException or ArgumentException){return;}throw new Exception(message);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static int Main()
    {
        var root=Path.GetFullPath(".artifacts/component-packages");Directory.CreateDirectory(root);
        var run=Path.Combine(root,"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(run);
        try {
            var library=new ComponentAssetLibrary(Path.Combine(run,"library"));
            var windowPath=Path.Combine(root,"window.glb");MakeWindow(windowPath);
            var plan=Plan("C1216","Window",1200,1600,100);var planPath=Path.Combine(root,"window.wlplan.json");ComponentPlanSymbols.Save(planPath,plan);
            CheckCatalog(Path.Combine(run,"shared-catalog"),plan);
            CheckDecorationDoor(root,run,library);
            Check(ComponentPlanSymbols.Pick(plan,new PointModel(500,1),2)==0,"Nearest native line was not picked");
            Check(ComponentPlanSymbols.Pick(plan,new PointModel(500,500),2)==-1,"Distant line was picked");
            var curvePlan=Plan("C0915","Window",900,1500,100);
            curvePlan.Primitives=new(){new ComponentPlanPrimitive {Kind="Arc",X1=0,Y1=0,Radius=100,StartDegrees=0,SweepDegrees=-90}};
            Check(ComponentPlanSymbols.Pick(curvePlan,new PointModel(70.710678,-70.710678),1)==0,"Clockwise native arc was not picked");
            Check(ComponentPlanSymbols.Pick(curvePlan,new PointModel(70.710678,70.710678),1)==-1,"Point outside arc span was picked");
            curvePlan.Primitives[0].StartDegrees=350;curvePlan.Primitives[0].SweepDegrees=30;
            Check(ComponentPlanSymbols.Pick(curvePlan,new PointModel(100,0),1)==0,"Arc crossing zero degrees was not picked");
            curvePlan.Primitives[0].Kind="Circle";
            Check(ComponentPlanSymbols.Pick(curvePlan,new PointModel(-100,0),1)==0,"Native circle was not picked");
            Reject(()=>ComponentPlanSymbols.Pick(plan,new PointModel(double.NaN,0),1),"Invalid pick point was accepted");
            var original=Hash(windowPath);var model=File.ReadAllBytes(windowPath);var mesh=ComponentGlbInspector.Inspect(model);
            Check(mesh.Nodes.Count==5&&mesh.Triangles==60,"Real frame/glass fixture nodes missing");
            var parts=mesh.Nodes.Select(n=>new ComponentPartDefinition {Name=n.Name,Role=n.Name=="glass"?"Panel":"Frame",MeshNodes=new(){n.Index}}).ToArray();
            parts[0].PlanPrimitives=Enumerable.Range(0,plan.Primitives.Count).ToList();
            var fallback=new OpeningTypeModel {Code="C1216",Kind="窗",Width=1200,Height=1600,HasInstallationGap=false};
            var asset=library.SaveExternal("固定窗",plan,model,new ComponentSourceFrame(),parts,fallback);
            var draftPath=Path.Combine(run,"author.wlodraft");
            ComponentAssetLibrary.SaveDraft(draftPath,new ComponentAuthorDraft {Name="未完成",OriginX="bad",Parts=parts.ToList(),Plan=plan,ModelBytes=model});
            var draft=ComponentAssetLibrary.LoadDraft(draftPath);Check(draft.OriginX=="bad"&&draft.Parts.Count==5&&draft.ModelBytes.SequenceEqual(model),"Self-contained draft lost incomplete input or geometry");
            Reject(()=>library.Import(draftPath),"Draft published as resource");
            Reject(()=>ComponentAssetLibrary.SaveDraft(windowPath,draft),"Draft overwrote GLB source");
            var emptyDraft=Path.Combine(run,"partial.wlodraft");ComponentAssetLibrary.SaveDraft(emptyDraft,new ComponentAuthorDraft {Name="partial"});
            Check(ComponentAssetLibrary.LoadDraft(emptyDraft).Plan==null,"Partial author draft requires complete sources");
            Check(asset.IsExternal&&asset.ExternalContent.Mesh.Nodes.Count==5&&asset.ExternalContent.Plan.Primitives.Count==4,"External roundtrip missing");
            Check(asset.ExternalContent.PartVolume(asset.Manifest.External.Parts[0]).Faces.Count==12,"Isolated part geometry missing");
            Check(asset.ExternalContent.PartPlan(asset.Manifest.External.Parts[0]).Lines.Count==4,"Part plan binding lost");
            Check(library.Import(asset.PackagePath).Sha256==asset.Sha256,"Duplicate import changed identity");
            var export=Path.Combine(run,"portable.wlopkg");library.Export(asset,export);Check(Hash(export)==asset.Sha256,"External export changed bytes");
            var id=asset.Manifest.AssetId;
            var revision2=library.SaveExternal("固定窗修订",plan,model,new ComponentSourceFrame(),parts,fallback,assetId:id,revision:2);
            Check(revision2.Manifest.Revision==2&&library.Load().Assets.Count==2,"Immutable new revision lost");
            Reject(()=>library.SaveExternal("conflict",plan,model,new ComponentSourceFrame(),parts,fallback,assetId:id,revision:1),"Conflicting revision overwritten");
            var legacy=library.Save("旧参数窗",fallback);Check(!legacy.IsExternal&&legacy.ExternalContent==null,"Legacy resource changed backend");
            Check(library.Load().Assets.Count==3,"Mixed legacy/external library failed");
            Check(library.Load(includeExternal:false).Assets.Single().Sha256==legacy.Sha256,"MM parameter-only read included external mesh resources");
            var modelFolder=Path.Combine(run,"project");Directory.CreateDirectory(modelFolder);var projectPath=Path.Combine(modelFolder,"model.json");
            BuildingModelJson.SaveModel(projectPath,SampleModelFactory.CreateTwoStoreyHouse());var before=Hash(projectPath);
            var copy=ComponentAssetLibrary.RetainInProject(asset,projectPath);Check(copy.Sha256==asset.Sha256&&Hash(projectPath)==before,"Project copy changed model or hash");
            Check(ComponentAssetLibrary.RetainInProject(asset,projectPath).PackagePath==copy.PackagePath,"Project copy not idempotent");
            var moved=Path.Combine(run,"moved-project");Directory.Move(modelFolder,moved);
            // Delete only the test-owned public source; the moved project must still be self-contained.
            File.Delete(asset.PackagePath);
            var offline=new ComponentAssetLibrary(Path.Combine(moved,"components")).Load();
            Check(offline.Assets.Single().ExternalContent.Mesh.Nodes.Count==5&&offline.Assets[0].Sha256==asset.Sha256,"Offline project relies on author path");
            Reject(()=>ComponentAssetLibrary.RetainInProject(revision2,Path.Combine(run,"missing","model.json")),"Unsaved project accepted");

            string Rewrite(Action<Dictionary<string,byte[]>> edit) {
                var entries=new Dictionary<string,byte[]>();using(var zip=ZipFile.OpenRead(export))foreach(var entry in zip.Entries){using var stream=entry.Open();using var memory=new MemoryStream();stream.CopyTo(memory);entries[entry.FullName]=memory.ToArray();}
                edit(entries);var path=Path.Combine(run,Guid.NewGuid()+".wlopkg");using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))foreach(var entry in entries){using var stream=zip.CreateEntry(entry.Key).Open();stream.Write(entry.Value);}return path;
            }
            Reject(()=>library.Import(Rewrite(e=>e["../outside.py"]=Encoding.UTF8.GetBytes("script"))),"Traversal/script accepted");
            Reject(()=>library.Import(Rewrite(e=>e["MODEL.GLB"]=e["model.glb"])),"Duplicate case path accepted");
            Reject(()=>library.Import(Rewrite(e=>e.Remove("model.glb"))),"Missing model accepted");
            Reject(()=>library.Import(Rewrite(e=>e["model.glb"][0]^=1)),"Corrupt hash accepted");
            var badPlan=Rewrite(e=>{
                e["plan.wlplan.json"]=Encoding.UTF8.GetBytes("not json");var m=JsonSerializer.Deserialize<ComponentAssetManifest>(e["manifest.json"]);
                m.External.PlanSha256=Convert.ToHexString(SHA256.HashData(e["plan.wlplan.json"]));e["manifest.json"]=JsonSerializer.SerializeToUtf8Bytes(m);
            });
            Reject(()=>library.Import(badPlan),"Invalid CAD JSON with valid hash accepted");
            File.Copy(badPlan,Path.Combine(library.Root,"broken-plan.wlopkg"));
            Check(library.Load().Errors.Count==1&&library.Load().Assets.Count==2,"Bad CAD JSON poisoned valid library entries");
            File.Delete(Path.Combine(library.Root,"broken-plan.wlopkg"));
            Reject(()=>library.Import(Rewrite(e=>e["manifest.json"]=Encoding.UTF8.GetBytes("{\"SchemaVersion\":2,\"schemaversion\":1}"))),"Duplicate JSON key accepted");
            Reject(()=>library.Import(Rewrite(e=>{
                var m=JsonSerializer.Deserialize<ComponentAssetManifest>(e["manifest.json"]);m.SchemaVersion=99;e["manifest.json"]=JsonSerializer.SerializeToUtf8Bytes(m);
            })),"Future version silently loaded");
            Reject(()=>library.Import(Rewrite(e=>{
                var m=JsonSerializer.Deserialize<ComponentAssetManifest>(e["manifest.json"]);m.External.Parts[0].MotionKind="Rotate";e["manifest.json"]=JsonSerializer.SerializeToUtf8Bytes(m);
            })),"Unimplemented motion silently published");
            Reject(()=>library.Import(Rewrite(e=>{
                var m=JsonSerializer.Deserialize<ComponentAssetManifest>(e["manifest.json"]);m.External.Parts[0].MeshNodes.Add(m.External.Parts[1].MeshNodes[0]);e["manifest.json"]=JsonSerializer.SerializeToUtf8Bytes(m);
            })),"Duplicate part node accepted");
            Reject(()=>library.Import(Rewrite(e=>{
                var m=JsonSerializer.Deserialize<ComponentAssetManifest>(e["manifest.json"]);m.External.Parts.RemoveAt(0);e["manifest.json"]=JsonSerializer.SerializeToUtf8Bytes(m);
            })),"Unbound node accepted");
            Reject(()=>library.Import(Rewrite(e=>{
                var m=JsonSerializer.Deserialize<ComponentAssetManifest>(e["manifest.json"]);m.External.Width+=100;e["manifest.json"]=JsonSerializer.SerializeToUtf8Bytes(m);
            })),"Dimension mismatch silently scaled");
            Reject(()=>library.Import(Rewrite(e=>e["plan.wlplan.json"]=new byte[ComponentPlanSymbols.MaxBytes+1])),"Decompression quota ignored");
            // A valid hash does not make an invalid GLB safe.
            var bad=Rewrite(e=>{
                e["model.glb"]=Encoding.UTF8.GetBytes("not glb");var m=JsonSerializer.Deserialize<ComponentAssetManifest>(e["manifest.json"]);
                m.External.ModelSha256=Convert.ToHexString(SHA256.HashData(e["model.glb"]));e["manifest.json"]=JsonSerializer.SerializeToUtf8Bytes(m);
            });Reject(()=>library.Import(bad),"Invalid GLB with matching hash accepted");
            File.Copy(bad,Path.Combine(library.Root,"broken.wlopkg"));Check(library.Load().Errors.Count==1,"Corrupt package prevented good library entries loading");
            var count=Directory.GetFiles(library.Root,"*.wlopkg").Length;
            using(var cancellation=new CancellationTokenSource()) {
                cancellation.Cancel();try{library.Import(export,cancellation.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
            }
            Check(Directory.GetFiles(library.Root,"*.wlopkg").Length==count&&!Directory.GetFiles(library.Root,"*.tmp").Any(),"Failed/cancelled writes left files");
            var chairPath=Path.Combine(root,"chair.glb");MakeChair(chairPath);var chairPlan=Plan("F-C01","Furniture",600,850,600);
            ComponentPlanSymbols.Save(Path.Combine(root,"chair.wlplan.json"),chairPlan);
            var chairMesh=ComponentGlbInspector.Inspect(chairPath);
            var chair=library.SaveExternal("固定椅",chairPlan,File.ReadAllBytes(chairPath),new ComponentSourceFrame(),chairMesh.Nodes.Select(n=>new ComponentPartDefinition {Name=n.Name,Role="Body",MeshNodes=new(){n.Index}}));
            Check(chair.Manifest.OpeningType==null&&chair.Manifest.Category=="Furniture"&&chair.ExternalContent.Mesh.Nodes.Count==6,"Furniture gained door/window semantics");
            var yVolume=ComponentAssetLibrary.Align(mesh.Volume,new ComponentSourceFrame {AlongAxis="Y"});
            Check(Math.Abs(yVolume.Width-100)<.1&&Math.Abs(yVolume.Depth-1200)<.1&&yVolume.Faces.All(f=>Math.Abs(f.NormalX*f.NormalX+f.NormalY*f.NormalY+f.NormalZ*f.NormalZ-1)<.001),"X/Y alignment not rigid");
            Reject(()=>library.SaveExternal("bad axis",plan,model,new ComponentSourceFrame {AlongAxis="Z"},parts,fallback),"Z direction accepted");
            Reject(()=>library.SaveExternal("bad dimension",plan,model,new ComponentSourceFrame {AlongAxis="Y"},parts,fallback),"Mismatched orientation scaled silently");
            Check(Hash(windowPath)==original,"Author source changed");
            // Persist portable samples for native UI tests; only tests own these files.
            File.Copy(export,Path.Combine(root,"window.wlopkg"),true);File.Copy(chair.PackagePath,Path.Combine(root,"chair.wlopkg"),true);
            Console.WriteLine("COMPONENT_PACKAGES_OK v1Mixed v2Portable realParts fixedChair immutable revisions projectCopy offline hashes quotas invalidGLB traversal duplicateJson unavailableMotion mismatch cancel unchangedSources");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private static void CheckCatalog(string root,ComponentPlanSymbol original)
    {
        var cad=new ComponentCatalog(root);var studio=new ComponentCatalog(root);
        var record=cad.SavePlan(original);
        Check(studio.Load().Records.Single().AssetId==record.AssetId&&!record.IsModelCurrent,"CAD-only record missing in Studio");
        Check(cad.SavePlan(original,record.AssetId,record.Version).Version==record.Version,"Unchanged plan created revision");
        Reject(()=>cad.SavePlan(original),"Duplicate category/code created unrelated resource");
        var linked=studio.AttachModel(record,1,new string('a',64));
        Check(cad.Get(record.AssetId).IsModelCurrent&&cad.Load().Records.Count==1,"3D created separate CAD record");
        Reject(()=>studio.AttachModel(record,2,new string('b',64)),"Stale edit overwrote model head");
        var edited=ComponentPlanSymbols.Load(ComponentPlanSymbols.Bytes(original));edited.Primitives[0].Y1=2;
        var changed=cad.SavePlan(edited,record.AssetId,linked.Version);
        Check(!studio.Get(record.AssetId).IsModelCurrent&&changed.ModelHash==linked.ModelHash,"CAD update lost model or silently kept current status");
        Reject(()=>studio.AttachModel(linked,2,new string('b',64)),"Model paired to outdated CAD plan");
        var recovered=studio.AttachModel(changed,2,new string('b',64));
        Check(cad.Get(record.AssetId).IsModelCurrent&&recovered.AssetId==record.AssetId,"Re-pair changed resource identity");
        var deleted=cad.SetDeleted(record.AssetId,recovered.Version,true);
        Check(studio.Load().Records.Count==0&&studio.Load(true).Records.Single().IsDeleted,"共享资源删除未同步或不支持恢复");
        Reject(()=>studio.Get(record.AssetId),"删除后仍能插入资源");Reject(()=>studio.AttachModel(recovered,3,new string('b',64)),"旧作者窗口复活已删除资源");Reject(()=>cad.SetDeleted(record.AssetId,recovered.Version,false),"旧版本恢复覆盖删除状态");
        var restored=cad.SetDeleted(record.AssetId,deleted.Version,false);Check(studio.Load().Records.Single().AssetId==record.AssetId&&restored.ModelHash==recovered.ModelHash&&restored.PlanHash==recovered.PlanHash,"恢复资源丢失身份或配对模型");
        var sibling=ComponentPlanSymbols.Load(ComponentPlanSymbols.Bytes(original));sibling.Code="C0915";sibling.Name=sibling.Code;
        cad.SavePlan(sibling);
        var corrupt=Path.Combine(cad.RecordsPath,Guid.NewGuid()+".json");File.WriteAllText(corrupt,"broken");
        Check(studio.Load().Records.Count==2&&studio.Load().Errors.Count==1,"Bad catalog record concealed valid resources");
        Reject(()=>cad.SavePlan(sibling),"Catalog corruption was ignored during duplicate validation");
        Console.WriteLine("COMPONENT_CATALOG_OK sharedIdentity CADOnly visibleInStudio duplicateCode noOp optimisticConcurrency invalidation preservedModel recover badRecordIsolation");
    }
    private static void CheckDecorationDoor(string root,string run,ComponentAssetLibrary ignored)
    {
        var library=new ComponentAssetLibrary(Path.Combine(run,"decoration-library"));
        var catalog=new ComponentCatalog(library.Root);
        var plan=Plan("","Door",900,2100,100);plan.SchemaVersion=3;plan.Name="平板单开装修门";plan.DoorAssembly="SingleSwing";
        plan.Parts=new(){new ComponentPlanPart {Name="门套",Role="Frame",Primitives=new(){0,1}},new ComponentPlanPart {Name="主门扇",Role="PrimaryLeaf",Primitives=new(){2}},new ComponentPlanPart {Name="开启示意",Role="OpeningSymbol",Primitives=new(){3}}};
        var record=catalog.SavePlan(plan);var second=catalog.SavePlan(plan);
        Check(record.AssetId!=second.AssetId&&catalog.Load().Records.Count==2,"未定编号的资源不能独立入库");
        var roundtrip=catalog.Get(record.AssetId).Plan;
        var original=ComponentPlanSymbols.Bytes(roundtrip);
        var basePoint=ComponentPlanSymbols.SuggestedInsertionBase(roundtrip);Check(basePoint.X==0&&basePoint.Y==0,"正常资源的基点被改动");
        var offset=ComponentPlanSymbols.Load(original);foreach(var p in offset.Primitives){p.X1+=2300;p.X2+=2300;p.Y1+=4200;p.Y2+=4200;}
        basePoint=ComponentPlanSymbols.SuggestedInsertionBase(offset);Check(basePoint.X==2300&&basePoint.Y==4200,"历史远离原点的资源未选择局部基点");
        Check(ComponentPlanSymbols.Bytes(roundtrip).SequenceEqual(original),"计算插入基点改写了共享记录");
        Check(ComponentPlanSymbols.RoleColor("Frame")!=ComponentPlanSymbols.RoleColor("Casing")&&ComponentPlanSymbols.RoleColor("Frame")!=ComponentPlanSymbols.RoleColor("PrimaryLeaf")&&ComponentPlanSymbols.RoleLineType("OpeningSymbol")=="DASHED","门的部件样式未区分");
        offset.Primitives.Add(new ComponentPlanPrimitive {Kind="Arc",X1=2300,Y1=4200,Radius=900,SweepDegrees=90});offset.Parts[1].Primitives.Add(4);
        Check(ComponentPlanSymbols.PrimitiveRole(offset,4)=="OpeningSymbol","旧门扇里的开启弧未使用开启样式");
        Check(roundtrip.Code==""&&roundtrip.Parts[0].PartId==plan.Parts[0].PartId&&ComponentPlanSymbols.ReferenceCode(roundtrip)=="M0921","资源名称、部件身份或参考编号丢失");
        var changed=ComponentPlanSymbols.Load(ComponentPlanSymbols.Bytes(plan));changed.Parts[1].Primitives.Add(0);
        Reject(()=>ComponentPlanSymbols.Validate(changed),"重复部件归属未拒绝");
        changed.Parts[1].Primitives=new(){99};Reject(()=>ComponentPlanSymbols.Validate(changed),"越界图元未拒绝");
        changed.Parts[1].Primitives=new(){2};changed.DoorAssembly="Guess";Reject(()=>ComponentPlanSymbols.Validate(changed),"未知门型未拒绝");
        var glbPath=Path.Combine(root,"door.glb");var model=ModelRoot.CreateModel();
        Box(model,"frame_left",0,-50,0,50,50,2100);Box(model,"frame_right",850,-50,0,900,50,2100);Box(model,"frame_top",50,-50,2050,850,50,2100);
        Box(model,"leaf",52,-20,2,848,20,2048);Box(model,"handle",740,-35,950,820,-20,970);model.SaveGLB(glbPath);
        var bytes=File.ReadAllBytes(glbPath);var mesh=ComponentGlbInspector.Inspect(bytes);
        var parts=new[]{new ComponentPartDefinition {Name="门套",Role="Frame",MeshNodes=mesh.Nodes.Where(n=>n.Name.StartsWith("frame_")).Select(n=>n.Index).ToList(),PlanPartId=plan.Parts[0].PartId,PlanPrimitives=new(){0,1}},
            new ComponentPartDefinition {Name="主门扇",Role="Panel",MeshNodes=mesh.Nodes.Where(n=>n.Name=="leaf").Select(n=>n.Index).ToList(),PlanPartId=plan.Parts[1].PartId,PlanPrimitives=new(){2}},
            new ComponentPartDefinition {Name="把手",Role="Hardware",MeshNodes=mesh.Nodes.Where(n=>n.Name=="handle").Select(n=>n.Index).ToList()}};
        var fallback=new OpeningTypeModel {Code="M0921",Kind="门",Width=900,Height=2100,HasInstallationGap=false};
        var asset=library.SaveExternal(plan.Name,plan,bytes,new ComponentSourceFrame(),parts,fallback,assetId:record.AssetId);
        catalog.AttachModel(record,asset.Manifest.Revision,asset.Sha256);
        Check(catalog.Get(record.AssetId).IsModelCurrent&&asset.ExternalContent.Plan.Code==""&&asset.Manifest.External.Parts[0].MeshNodes.Count==3,"名称入库不能配对三维或合并门套");
        parts[0].PlanPrimitives.Add(2);Reject(()=>library.SaveExternal(plan.Name,plan,bytes,new ComponentSourceFrame(),parts,fallback),"CAD 部件与三维图元关联失配未拒绝");parts[0].PlanPrimitives.Remove(2);
        parts[2].PlanPartId=plan.Parts[2].PartId;parts[2].PlanPrimitives=new(){3};Reject(()=>library.SaveExternal(plan.Name,plan,bytes,new ComponentSourceFrame(),parts,fallback),"开启示意被绑定成三维实体");
        ComponentPlanSymbols.Save(Path.Combine(root,"door.wlplan.json"),plan);File.Copy(asset.PackagePath,Path.Combine(root,"door.wlopkg"),true);
        Console.WriteLine("DECORATION_DOOR_CONTRACT_OK optionalCode stableParts referenceCode sharedRecord groupedFrame semanticBinding invalidPart rejectedSymbol");
    }
    private static ComponentPlanSymbol Plan(string code,string category,double width,double height,double depth)
    {
        var plan=new ComponentPlanSymbol {SchemaVersion=category=="Furniture"?2:1,Code=code,Name=code,Category=category,Width=width,Height=height,Depth=depth};
        var points=new[]{new PointModel(0,0),new PointModel(width,0),new PointModel(width,depth),new PointModel(0,depth)};
        for(var i=0;i<4;i++)plan.Primitives.Add(new ComponentPlanPrimitive {Kind="Line",X1=points[i].X,Y1=points[i].Y,X2=points[(i+1)%4].X,Y2=points[(i+1)%4].Y});return plan;
    }
    private static void MakeWindow(string path)
    {
        var root=ModelRoot.CreateModel();Box(root,"frame_left",0,-50,0,50,50,1600);Box(root,"frame_right",1150,-50,0,1200,50,1600);
        Box(root,"frame_top",50,-50,1550,1150,50,1600);Box(root,"frame_bottom",50,-50,0,1150,50,50);Box(root,"glass",50,-3,50,1150,3,1550);root.SaveGLB(path);
    }
    private static void MakeChair(string path)
    {
        var root=ModelRoot.CreateModel();Box(root,"seat",0,0,400,600,600,450);Box(root,"back",0,550,450,600,600,850);
        foreach(var (x,y) in new[]{(0d,0d),(550d,0d),(0d,550d),(550d,550d)})Box(root,"leg_"+x+"_"+y,x,y,0,x+50,y+50,400);root.SaveGLB(path);
    }
    private static void Box(ModelRoot root,string name,double x0,double y0,double z0,double x1,double y1,double z1)
    {
        Vector3 P(double x,double y,double z)=>new((float)x/1000,(float)z/1000,-(float)y/1000);
        var points=new[]{P(x0,y0,z0),P(x1,y0,z0),P(x1,y1,z0),P(x0,y1,z0),P(x0,y0,z1),P(x1,y0,z1),P(x1,y1,z1),P(x0,y1,z1)};
        var mesh=new MeshBuilder<VertexPositionNormal,VertexEmpty,VertexEmpty>(name);var primitive=mesh.UsePrimitive(new MaterialBuilder("neutral").WithMetallicRoughnessShader());
        foreach(var face in new[]{new[]{0,3,2,1},new[]{4,5,6,7},new[]{0,1,5,4},new[]{3,7,6,2},new[]{0,4,7,3},new[]{1,2,6,5}}) {
            var normal=Vector3.Normalize(Vector3.Cross(points[face[1]]-points[face[0]],points[face[2]]-points[face[0]]));
            primitive.AddTriangle(new VertexPositionNormal(points[face[0]],normal),new VertexPositionNormal(points[face[1]],normal),new VertexPositionNormal(points[face[2]],normal));
            primitive.AddTriangle(new VertexPositionNormal(points[face[0]],normal),new VertexPositionNormal(points[face[2]],normal),new VertexPositionNormal(points[face[3]],normal));
        }
        var index=root.LogicalMeshes.Count;root.CreateMeshes(mesh);root.UseScene(0).CreateNode(name).Mesh=root.LogicalMeshes[index];
    }
}
