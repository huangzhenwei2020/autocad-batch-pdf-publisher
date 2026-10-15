using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;

internal static class OpeningConstructionTests
{
    internal static void Run()
    {
        CheckRoundedOpeningFit();
        CheckPendingSizeAdaptation();
        CheckDefaultCombinedAndOpeningPaths();
        CheckEditedMergeCollections();
        var model=SampleModelFactory.CreateEmptyModel("门窗构造回归");
        model.Walls.Add(new WallModel {Id="wall",StoreyId="1F",X2=6000,Y2=1000,Thickness=200});
        model.Openings.Add(new OpeningModel {Id="a",HostWallId="wall",Code="C1518",Width=1500,Height=1800,Sill=900,Offset=1800});
        model.Openings.Add(new OpeningModel {Id="b",HostWallId="wall",Code="C1518",Width=1500,Height=1800,Sill=900,Offset=4200});
        model.Storeys.Add(new StoreyModel {Id="2F",Name="二层",TemplateStoreyId="1F",Height=3000,Elevation=3000});
        var draft=OpeningConstruction.Resolve(model,model.Openings[0]);draft.DivisionPreset="三扇等分";draft.OpeningMode="固定";draft.FrameDepth=90;draft.GlassThickness=12;
        var session=new BuildingModelEditSession(model);
        Check(session.TrySetOpeningConstruction("a",draft,false,out var error),error);
        var physical=StandardStoreyLayout.Materialize(session.Model);
        Check(physical.Openings.Count==4,"标准层应保留实际实例数。");
        foreach(var o in physical.Openings) {
            var type=OpeningConstruction.Resolve(physical,o);
            var parts=OpeningConstruction.Build(o,type,200);
            Check(parts.Count(p=>p.Kind=="glass")==3 && parts.Where(p=>p.Kind=="glass").All(p=>p.Depth==12),"同编号的分格和玻璃厚度应同步。");
            var item=OpeningElevationAdapter.ToScheduleItem(o,type,o.Width,o.Height);
            var g=DoorWindowElevationGeometryBuilder.Build(item);
            foreach(var cell in g.Cells) {
                var net=DoorWindowElevationGeometryBuilder.PanelBounds(cell,g.Cells,item);
                Check(parts.Any(p=>p.Kind=="glass"&&Math.Abs(p.Left-net[0])<.001&&Math.Abs(p.Right-net[2])<.001),"二维与三维净面板边界必须相同。");
            }
        }
        var volume=BuildingVolumeBuilder.BuildOpeningParts(session.Model);
        var angle=Math.Atan2(1000,6000);double Normal(Point3DModel p)=>-Math.Sin(angle)*p.X+Math.Cos(angle)*p.Y;
        var glass=volume.Faces.Where(f=>f.ElementId=="a"&&f.Kind=="glass").SelectMany(f=>f.Points).ToList();
        Check(Math.Abs(glass.Max(Normal)-glass.Min(Normal)-12)<.001,"斜墙上的玻璃实际厚度必须为 12 mm。");
        var original=BuildingModelJson.ToJson(session.Model);
        draft.GlassThickness=18;
        Check(session.TrySetOpeningConstruction("a@STD@2F",draft,true,out error),error);
        physical=StandardStoreyLayout.Materialize(session.Model);
        Check(physical.Openings.Single(o=>o.Id=="a@STD@2F").Code=="C1518A","单层单樘修改应自动添加编号后缀。");
        Check(physical.Openings.Where(o=>o.Id!="a@STD@2F").All(o=>o.Code=="C1518"),"单樘修改不得改变其他标准层。");
        Check(OpeningConstruction.Resolve(physical,physical.Openings.Single(o=>o.Id=="a@STD@2F")).GlassThickness==18,"覆盖应落到实际实例。");
        var changed=BuildingModelJson.ToJson(session.Model);
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==original,"一次撤销必须恢复类型、编号及实例覆盖。");
        Check(session.Redo()&&BuildingModelJson.ToJson(session.Model)==changed,"重做应恢复所有数据。");
        draft.GlassThickness=-1;Check(!session.TrySetOpeningConstruction("a",draft,false,out error)&&BuildingModelJson.ToJson(session.Model)==changed,"无效构造必须回滚。");
        var reopened=BuildingModelJson.FromJson(changed);Check(StandardStoreyLayout.Materialize(reopened).Openings.Any(o=>o.Code=="C1518A"),"保存重开必须保留标准层特殊门窗。");
        var external=new OpeningTypeLibraryDocument();external.Types.Add(new OpeningTypeModel {Code="C1518",GlassThickness=3});
        Check(OpeningConstruction.Library(session.Model,external).FindType("C1518").GlassThickness==12,"模型内已编辑做法优先于旧外部库。");
        var batchBefore=BuildingModelJson.ToJson(session.Model);
        var batchType=OpeningConstruction.Copy(OpeningConstruction.Library(session.Model).FindType("C1518"));batchType.FrameDepth=110;
        Check(session.TrySetOpeningConstructions(new Dictionary<string,OpeningTypeModel>{{"C1518",batchType}},out error),error);
        Check(OpeningConstruction.Library(session.Model).FindType("C1518").FrameDepth==110,"批量构造应保存所选厚度。");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==batchBefore,"批量构造应一次撤销。");
        draft.GlassThickness=12;
        Check(session.TrySetOpeningConstruction("a",draft,false,out error,new Dictionary<string,OpeningTypeModel>{{"三格窗",draft}},20),error);
        Check(session.Model.OpeningEditorSnapStep==20&&StandardStoreyLayout.Materialize(session.Model).OpeningEditorSnapStep==20,"移动步长须随模型保存并保留到实际楼层。");
        Check(session.Model.OpeningTemplates.Count==1,"模板应随做法一起保存。");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==batchBefore,"取消或撤销不得留下草稿模板。");
        Check(OpeningConstruction.AlphabeticSuffix(0)=="A"&&OpeningConstruction.AlphabeticSuffix(26)=="AA","单樘与 CAD 尺寸变体应使用相同字母后缀。");
        var state=new OpeningLayoutEditing(1000,1200,new[]{new DoorWindowLayoutCell {Right=1000,Top=1200,Material="玻璃",Opening="固定"}});
        Check(state.Split(new[]{0},true,2,out error),error);Check(state.MoveDivider(true,500,600,out error),error);
        Check(state.Cells.Any(c=>c.Right==600)&&state.Cells.Any(c=>c.Left==600),"拖动分隔应同步两侧。");
        var cells=DoorWindowElevationGeometryBuilder.SerializeCellLayout(state.Cells);
        Check(state.MoveDivider(true,600,1200,out error)&&state.Cells.Any(c=>c.Right==950),"越界拖动应按原编辑器规则钳制并为邻格保留 50 mm。");
        Check(state.Undo()&&cells==DoorWindowElevationGeometryBuilder.SerializeCellLayout(state.Cells),"钳制拖动应可撤销。");
        Check(!state.MoveDivider(true,1000,1100,out error)&&cells==DoorWindowElevationGeometryBuilder.SerializeCellLayout(state.Cells),"外边框不能被拖动。");
        Check(!state.SetCellSize(1,300,1200,out error)&&cells==DoorWindowElevationGeometryBuilder.SerializeCellLayout(state.Cells),"最右格不能通过格宽改变外边框。");
        Check(state.SetCellSize(0,550,1200,out error)&&state.Cells[0].Right==550&&state.Cells[1].Right==1000,"实际格宽优先，邻格随动而框口固定。");
        Check(state.Undo(),"尺寸修改可一次撤销。");
        Check(state.Merge(new[]{0,1},out error)&&state.Cells.Count==1,error);Check(state.Undo()&&state.Cells.Count==2,"编辑窗口内部撤销应可用。");
        var reference=new DoorWindowScheduleItem {Code="C1524",Width=1500,Height=2400,ElevationType="普通窗",SillHeightFromCadRegistration=true};
        BatchPdfPublisher.Services.DoorWindowElevationSuggestionService.Apply(reference);
        var defaults=OpeningConstruction.Default(new OpeningModel {Code="C1524",Kind="窗",Width=1500,Height=2400});
        Check(!defaults.HasInstallationGap,"模型默认应关闭洞口安装缝，扇边缝须单独处理。");
        Check(defaults.FrameDepth==100&&defaults.MullionDepth==100,"模型外框和分隔框默认进深须为 100 mm。");
        var referenceCells=DoorWindowElevationGeometryBuilder.ParseCellLayout(reference.CustomCellLayout);
        foreach(var c in referenceCells){c.Left*=1500d/(1500-2*reference.InstallationGap);c.Right*=1500d/(1500-2*reference.InstallationGap);c.Bottom*=2400d/(2400-2*reference.InstallationGap);c.Top*=2400d/(2400-2*reference.InstallationGap);}
        Check(defaults.CustomCellLayout==DoorWindowElevationGeometryBuilder.SerializeCellLayout(referenceCells)&&defaults.DoorFrameWidth==reference.DoorFrameWidth&&defaults.DoorFrameType==reference.DoorFrameType,"默认分格和框料来自原立面，关闭安装缝后须填满洞口。");
        var fixedFrame=OpeningConstruction.Build(new OpeningModel {Width=1500,Height=2400,Kind="窗",Code="C1524"},defaults,200).Where(p=>p.Kind=="frame").ToList();
        Check(fixedFrame.Min(p=>p.Left)==0&&fixedFrame.Max(p=>p.Right)==1500&&fixedFrame.Min(p=>p.Bottom)==0&&fixedFrame.Max(p=>p.Top)==2400,"关闭安装缝的默认外框须精确覆盖整个洞口。");
        var legacy=OpeningConstruction.Copy(defaults);legacy.CustomCellLayout=reference.CustomCellLayout;
        Check(OpeningConstruction.ScaledLayout(legacy,1500,2400)==defaults.CustomCellLayout,"旧模型关闭开关却保留内缩坐标的做法须自动修复。");
        var bayOpening=new OpeningModel {Id="bay",Kind="窗",Code="TC1818",Width=1800,Height=1800,HostWallId="host",Offset=1500,Sill=700};
        var bayType=OpeningConstruction.Default(bayOpening);
        var bayParts=OpeningConstruction.Build(bayOpening,bayType,200);
        Check(bayParts.Count(p=>p.Kind=="glass"&&p.Face==0)==6&&bayParts.Count(p=>p.Kind=="glass"&&p.Face==-1)==2&&bayParts.Count(p=>p.Kind=="glass"&&p.Face==1)==2,"飘窗应只有 6 正面、左右各 2 面板，不能重复展开侧格。");
        Check(bayParts.Where(p=>p.Face==0&&p.Kind=="glass").All(p=>p.Left>=0&&p.Right<=1800),"飘窗正面不能包含展开侧面的坐标。");
        Check(bayParts.Count(p=>p.Kind=="bay-cap")==2,"飘窗上下须有封板。");
        Check(bayParts.Where(p=>p.Kind=="bay-cap").All(p=>p.Left<=-35&&p.Right>=1835&&p.Depth>=635),"飘窗上下板必须完整覆盖侧框厚度、前框及转角外缘。");
        var bayModel=SampleModelFactory.CreateEmptyModel("飘窗朝向");bayModel.Walls.Add(new WallModel {Id="host",StoreyId="1F",X2=3000,Thickness=200});bayModel.Openings.Add(bayOpening);
        bayModel.Slabs.Add(new SlabModel {Id="slab",StoreyId="1F",Outline=new List<PointModel>{new PointModel(0,100),new PointModel(3000,100),new PointModel(3000,3000),new PointModel(0,3000)}});
        var bayVolume=BuildingVolumeBuilder.BuildOpeningParts(bayModel);
        Check(bayVolume.MinY<-550&&bayVolume.MaxY<=50&&bayVolume.MinX>=-50&&bayVolume.MaxX<=3050,"飘窗应朝楼板轮廓的外侧伸出，不能反向进入室内或放大正面。");
        var joinedModel=SampleModelFactory.CreateEmptyModel("窗框并集");
        joinedModel.Walls.Add(new WallModel {Id="host",StoreyId="1F",X2=3000,Thickness=200});
        var joinedOpening=new OpeningModel {Id="joined",HostWallId="host",Width=1500,Height=1800,Offset=1500,Sill=0,Code="C1518",Kind="窗"};joinedModel.Openings.Add(joinedOpening);
        var rawFrames=OpeningConstruction.Build(joinedOpening,OpeningConstruction.Default(joinedOpening),200).Where(p=>p.Kind=="frame").ToList();
        bool InFrame(double x,double y,double z)=>rawFrames.Any(p=>x>=750+p.Left&&x<=750+p.Right&&y>=p.NormalOffset-p.Depth/2&&y<=p.NormalOffset+p.Depth/2&&z>=p.Bottom&&z<=p.Top);
        foreach(var f in BuildingVolumeBuilder.BuildOpeningParts(joinedModel).Faces.Where(f=>f.Kind=="frame")){
            var x=f.Points.Average(p=>p.X);var y=f.Points.Average(p=>p.Y);var z=f.Points.Average(p=>p.Z);
            Check(InFrame(x-f.NormalX*.1,y-f.NormalY*.1,z-f.NormalZ*.1)&&!InFrame(x+f.NormalX*.1,y+f.NormalY*.1,z+f.NormalZ*.1),"窗框仅应输出实体并集的外表面，不能输出相接框条的内部接缝面。");
        }
        var equal=new OpeningLayoutEditing(1000,1200,new[]{new DoorWindowLayoutCell {Right=200,Top=1200},new DoorWindowLayoutCell {Left=200,Right=500,Top=1200},new DoorWindowLayoutCell {Left=500,Right=1000,Top=1200}});
        Check(equal.Center(new[]{1},out error)&&equal.Cells[1].Left==350&&equal.Cells[1].Right==650,"居中应保留所选格宽并调整左右邻格。");
        CheckMultiRowCenter();
        Check(equal.Equalize(new[]{0,1,2},true,out error),error);
        Check(equal.Cells.All(c=>Math.Abs(c.Right-c.Left-1000d/3)<.01),"同行等宽须保持整体框口。");
        Check(equal.MoveDivider(true,equal.Cells[0].Right,351,out error,600,20)&&equal.LastDividerCoordinate==360,"移动步长与最终中梃坐标一致。");
        var doorOpening=new OpeningModel {Id="double",HostWallId="host",Kind="门",Code="M1825",Width=1800,Height=2500,Offset=1500};
        var doubleDoor=OpeningConstruction.Default(doorOpening);doubleDoor.SashWidth=50;doubleDoor.SashClearance=2;
        doubleDoor.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
            new DoorWindowLayoutCell {Right=900,Top=2500,IsDoor=true,Opening="右平开",Material="玻璃"},
            new DoorWindowLayoutCell {Left=900,Right=1800,Top=2500,IsDoor=true,Opening="左平开",Material="玻璃"}});
        var leafParts=OpeningConstruction.Build(doorOpening,doubleDoor,200).Where(p=>p.Cell!=null).ToList();
        Check(leafParts.Where(p=>p.Cell.Left==0).Max(p=>p.Right)==898&&leafParts.Where(p=>p.Cell.Left==900).Min(p=>p.Left)==902,"双扇闭合时必须有真实 4 mm 门缝，独立扇框不能相接。");
        doubleDoor.SashClearance=5;leafParts=OpeningConstruction.Build(doorOpening,doubleDoor,200).Where(p=>p.Cell!=null).ToList();
        Check(leafParts.Where(p=>p.Cell.Left==0).Max(p=>p.Right)==895&&leafParts.Where(p=>p.Cell.Left==900).Min(p=>p.Left)==905&&doorOpening.Width==1800,"每边扇缝可调整，洞口宽度必须保持不变。");
        var sliding=DoorWindowElevationGeometryBuilder.ParseCellLayout(doubleDoor.CustomCellLayout);foreach(var c in sliding)c.Opening="双向推拉";doubleDoor.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(sliding);doubleDoor.SashClearance=2;
        leafParts=OpeningConstruction.Build(doorOpening,doubleDoor,200).Where(p=>p.Cell!=null).ToList();
        var track0=leafParts.Where(p=>p.Cell.Left==0).ToList();var track1=leafParts.Where(p=>p.Cell.Left==900).ToList();
        Check(track0.All(p=>p.NormalOffset==-26)&&track1.All(p=>p.NormalOffset==26),"推拉扇的框和面板须同步处于独立前后轨道。");
        Check(track0.Max(p=>p.Right)>track1.Min(p=>p.Left)&&track0.Min(p=>p.Left)>=0&&track1.Max(p=>p.Right)<=1800,"推拉扇交接处须搭接，外围不能超出洞口。");
        var planModel=SampleModelFactory.CreateEmptyModel("推拉平面");
        planModel.Walls.Add(new WallModel {Id="host",StoreyId="1F",X2=3000,Thickness=200});
        doorOpening.Code="TLM1825";planModel.Openings.Add(doorOpening);
        doubleDoor.Code=doorOpening.Code;planModel.OpeningTypes.Add(doubleDoor);
        var planLines=OrthographicProjector.CreatePlanDetailSymbols(planModel,"1F");
        Check(!doorOpening.HasSwingLeaf()&&planLines.Count>8&&planLines.All(l=>Math.Max(Math.Abs(l.Y1),Math.Abs(l.Y2))<300),"推拉门平面须绘制双轨、搭接及箭头，不得画大开启弧。");
        doorOpening.Code="M1825";doubleDoor.Code="M1825";
        planLines=OrthographicProjector.CreatePlanDetailSymbols(planModel,"1F");
        Check(planLines.All(l=>Math.Max(Math.Abs(l.Y1),Math.Abs(l.Y2))<300),
            "普通门编号也必须遵循保存的推拉做法，不能仅靠 TLM 前缀判断。");
        foreach(var c in sliding)c.Opening=c.Left==0?"左平开":"右平开";
        doubleDoor.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(sliding);
        planLines=OrthographicProjector.CreatePlanDetailSymbols(planModel,"1F");
        var swingParts=OpeningConstruction.Build(doorOpening,doubleDoor,200).Where(p=>p.Bottom<=1200&&p.Top>1200).ToArray();
        var swingLeaves=swingParts.Where(p=>p.Cell!=null).GroupBy(p=>p.Cell.Left).ToArray();
        var leafSpan=swingLeaves.Max(g=>g.Max(p=>p.Right)-g.Min(p=>p.Left));
        var frameFront=swingParts.Where(p=>p.Kind=="frame").Select(p=>p.NormalOffset+p.Depth/2).DefaultIfEmpty(0).Max();
        var hingeNormal=swingParts.Any(p=>p.Kind=="frame")?frameFront+swingLeaves.SelectMany(g=>g).Max(p=>p.Depth)/2+2:0;
        Check(planLines.Count>=34&&planLines.Max(l=>Math.Max(l.Y1,l.Y2))==leafSpan+hingeNormal&&leafSpan<900,
            "普通双扇门必须按立面分扇，不能生成整宽开启弧。");
        var beforeFlip=planLines;
        var gripSession=new BuildingModelEditSession(planModel);
        Check(gripSession.TrySetOpeningPlacement("double","host",1600,true,true,out error),error);
        var flipped=OrthographicProjector.CreatePlanDetailSymbols(gripSession.Model,"1F");
        Check(flipped.Count==beforeFlip.Count&&flipped.Zip(beforeFlip,(a,b)=>
            Math.Abs(a.X1-(3100-b.X1))<.001&&Math.Abs(a.Y1+b.Y1)<.001).All(x=>x),
            "门轴与开启侧必须按实例镜像，并保持真实毫米坐标。");
        Check(gripSession.Model.OpeningTypes[0].CustomCellLayout==doubleDoor.CustomCellLayout,"方向夹点不能改共享立面类型。");
        var savedGrip=BuildingModelJson.ToJson(gripSession.Model);
        Check(BuildingModelJson.FromJson(savedGrip).Openings[0].PlanFlipNormal,"实例方向未持久化。");
        Check(!gripSession.TrySetOpeningPlacement("double","host",100,true,true,out error)
            &&BuildingModelJson.ToJson(gripSession.Model)==savedGrip,"越墙端拖动必须完整回滚。");
        var moveModel=BuildingModelJson.FromJson(savedGrip);
        moveModel.Walls.Add(new WallModel {Id="other",StoreyId="1F",X1=4000,X2=4000,Y2=3000,Thickness=200});
        moveModel.Storeys.Add(new StoreyModel {Id="2F",Height=3000,Elevation=3000,TemplateStoreyId="1F"});
        moveModel.Walls.Add(new WallModel {Id="up",StoreyId="2F",X2=6000,Thickness=200});
        var moveSession=new BuildingModelEditSession(moveModel);
        Check(moveSession.TrySetOpeningPlacement("double","other",1500,true,true,out error),error);
        Check(moveSession.Model.Openings[0].HostWallId=="other"&&StandardStoreyLayout.Materialize(moveSession.Model)
            .Openings.All(o=>o.PlanFlipAlong&&o.PlanFlipNormal),"换宿主和标准层必须保留实例方向。");
        var movedJson=BuildingModelJson.ToJson(moveSession.Model);
        Check(!moveSession.TrySetOpeningPlacement("double","up",1500,false,false,out error)
            &&BuildingModelJson.ToJson(moveSession.Model)==movedJson,"夹点不能跨楼层换宿主。");
        Check(!moveSession.TrySetOpeningPlacement("double","other",double.NaN,false,false,out error),"夹点不能接受非有限坐标。");
        Check(gripSession.Undo()&&gripSession.Model.Openings[0].Offset==1500&&!gripSession.Model.Openings[0].PlanFlipAlong,
            "移动和方向调整必须一次撤销。");
        Console.WriteLine("PASS 门窗三维编辑：共享默认、固定外框、双扇门缝、推拉轨道、普通门分扇及实例方向夹点");
        WindowHingeChecks();
    }
    private static void CheckPendingSizeAdaptation()
    {
        var model=SampleModelFactory.CreateEmptyModel("Pending aperture sizes");model.Storeys.RemoveAll(s=>s.Id!="1F");model.Storeys[0].Height=3000;
        model.Storeys.Add(new(){Id="2F",TemplateStoreyId="1F",Elevation=3000,Height=2800});
        model.Walls.Add(new(){Id="host",StoreyId="1F",X1=6000,X2=0,Thickness=200});
        model.CadImport=new(){PendingOpenings=new(){new(){StoreyId="1F",SourceHandle="pending",Code="MLC8035",Kind="门联窗",Width=8000,Height=3500}}};
        var session=new BuildingModelEditSession(model);var original=BuildingModelJson.ToJson(session.Model);
        var plan=session.PlanPendingOpeningPlacement("1F","pending","host",3000);
        Check(plan.Error!=null&&plan.MaximumWidth==6000&&plan.MaximumHeight==2800&&plan.SuggestedWidth==6000&&plan.SuggestedHeight==2800,"待放置尺寸未按墙端和最短共用层适配。");
        Check(!session.TryPlacePendingOpening("1F","pending","host",3000,8000,3500,0,OpeningSizeConflictChoice.CreateNew,out _,out _)&&BuildingModelJson.ToJson(session.Model)==original,"非法待放置尺寸提交或丢失参考。");
        Check(session.TryPlacePendingOpening("1F","pending","host",3000,6000,2800,0,OpeningSizeConflictChoice.CreateNew,out var id,out var error),error);
        var placed=session.Model.Openings.Single();Check(placed.Code=="MLC6028"&&placed.Offset==3000&&placed.Width==6000&&placed.Height==2800,"尺寸适配没有同步编号或误改位置。");
        var cells=BatchPdfPublisher.Models.DoorWindowElevationGeometryBuilder.ParseCellLayout(OpeningConstruction.Resolve(session.Model,placed).CustomCellLayout);
        Check(cells.Count(c=>c.IsDoor&&c.Right-c.Left==750&&c.Top-c.Bottom==2200)==2,"新适配 MLC 的默认居中门被按旧尺寸压缩。");
        Check(session.Model.CadImport.PendingOpenings.Count==0&&session.Model.CadImport.ResolvedOpenings.Single().OpeningId==id,"待放置关联记录未原子提交。");
        var applied=BuildingModelJson.ToJson(session.Model);Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==original&&session.Redo()&&BuildingModelJson.ToJson(session.Model)==applied,"适配放置不能一步撤销重做。");
        Check(BuildingModelJson.ToJson(BuildingModelJson.FromJson(applied))==applied,"适配放置持久化丢失。");
        session.Undo();var collision=session.Model;collision.Openings.Add(new(){Id="occupied",HostWallId="host",Code="C1015",Kind="窗",Width=1000,Height=1500,Offset=3000});
        session=new(collision);plan=session.PlanPendingOpeningPlacement("1F","pending","host",1000);
        Check(plan.SuggestedWidth==2000,"适配未考虑相邻洞口。");plan=session.PlanPendingOpeningPlacement("1F","pending","host",2900);
        Check(plan.SuggestedWidth==0&&plan.Error!=null,"已占用中心仍给出自动适配。");
        plan=session.PlanPendingOpeningPlacement("1F","pending","host",1000,1200,1500,800);Check(plan.Error==null&&plan.MaximumHeight==2000,"手动窗台高未参与共用层限值。");
        plan=session.PlanPendingOpeningPlacement("1F","pending","host",double.NaN);Check(plan.Error!=null,"非法定位中心未拒绝。");
        Console.WriteLine("PASS pending aperture manual/fit limits reversedHost neighbours standardHeight automaticCode defaultMLC1500 sourcePreserved atomicUndo redo persistence");
    }
    private static void WindowHingeChecks()
    {
        var model=SampleModelFactory.CreateEmptyModel("C1215 casement hinges");
        model.Walls.Add(new WallModel {Id="hinge-host",StoreyId="1F",X2=4000,Thickness=200});
        var opening=new OpeningModel {Id="hinge-window",HostWallId="hinge-host",Code="C1215",Kind="窗",Width=1200,Height=1500,Sill=900,Offset=2000,OpenIn3D=false,PlanOpenAngle=35};
        var type=OpeningConstruction.Default(opening);type.HasOuterFrame=true;type.OuterFrameWidth=50;type.HasMullion=true;type.MullionWidth=50;
        type.SashWidth=40;type.SashDepth=50;type.FrameDepth=100;type.MullionDepth=70;type.SashClearance=2;type.DivisionPreset="自定义";
        type.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
            new DoorWindowLayoutCell {Right=600,Top=1500,Opening="左平开",Material="玻璃"},
            new DoorWindowLayoutCell {Left=600,Right=1200,Top=1500,Opening="右平开",Material="玻璃"}});
        model.Openings.Add(opening);model.OpeningTypes.Add(type);
        foreach(var oblique in new[]{false,true})foreach(var along in new[]{false,true})foreach(var normal in new[]{false,true}) {
            var wall=model.Walls[0];wall.Y2=oblique?1500:0;opening.PlanFlipAlong=along;opening.PlanFlipNormal=normal;
            var length=Math.Sqrt(wall.X2*wall.X2+wall.Y2*wall.Y2);var ux=wall.X2/length;var uy=wall.Y2/length;
            var origin=WallReferenceGeometry.BodyPoint(wall,ux*(opening.Offset-opening.Width/2),uy*(opening.Offset-opening.Width/2));
            type.OpenAngle=0;var closed=BuildingVolumeBuilder.BuildOpeningParts(model);
            var closedGlass=closed.Faces.Where(f=>f.Kind=="glass").ToArray();
            Check(closedGlass.Length==12,"双扇窗应有两块完整玻璃实体");
            foreach(var angle in new[]{15d,30,45,80,90,120,180})foreach(var flag in new bool?[]{null,false,true}) {
                opening.OpenIn3D=flag;type.OpenAngle=angle;var opened=BuildingVolumeBuilder.BuildOpeningParts(model);
                var openGlass=opened.Faces.Where(f=>f.Kind=="glass").ToArray();
                Check(openGlass.Length==closedGlass.Length,"窗开启后丢面");
                for(var leaf=0;leaf<2;leaf++) {
                    var pivot=leaf==0?52d:1148d;var pivotNormal=52d;
                    var a=(leaf==0?angle:-angle)*Math.PI/180;
                    var expected=closedGlass.Skip(leaf*6).Take(6).SelectMany(f=>f.Points).Select(p=> {
                        var dx=p.X-origin.X;var dy=p.Y-origin.Y;var x=dx*ux+dy*uy;var y=-dx*uy+dy*ux;
                        if(along)x=1200-x;if(normal)y=-y;
                        dx=x-pivot;dy=y-pivotNormal;
                        x=pivot+dx*Math.Cos(a)-dy*Math.Sin(a);y=pivotNormal+dx*Math.Sin(a)+dy*Math.Cos(a);
                        if(along)x=1200-x;if(normal)y=-y;
                        return new Point3DModel(origin.X+ux*x-uy*y,origin.Y+uy*x+ux*y,p.Z);
                    }).ToArray();
                    var actual=openGlass.Skip(leaf*6).Take(6).SelectMany(f=>f.Points).ToArray();
                    Check(expected.Length==actual.Length&&expected.Zip(actual,(p,q)=>Math.Abs(p.X-q.X)<.000001&&Math.Abs(p.Y-q.Y)<.000001&&Math.Abs(p.Z-q.Z)<.000001).All(v=>v),"窗未按净扇与固定框接合轴刚性旋转："+angle+" / "+flag);
                    var x=along?1200-pivot:pivot;var y=normal?-pivotNormal:pivotNormal;
                    var axis=new Point3DModel(origin.X+ux*x-uy*y,origin.Y+uy*x+ux*y,opening.Sill+1448);
                    Check(opened.Faces.Where(f=>f.Kind=="sash").SelectMany(f=>f.Points).Any(p=>Math.Abs(p.X-axis.X)<.000001&&Math.Abs(p.Y-axis.Y)<.000001&&Math.Abs(p.Z-axis.Z)<.000001),"合页上的净扇角点随开启角度漂移");
                }
                string FrameKey(VolumeFace f)=>string.Join(";",f.Points.Select(p=>$"{p.X:R},{p.Y:R},{p.Z:R}"));
                Check(closed.Faces.Where(f=>f.Kind=="frame").Select(FrameKey).SequenceEqual(opened.Faces.Where(f=>f.Kind=="frame").Select(FrameKey)),"窗开启带动了固定框");
            }
        }
        model.Walls[0].Y2=0;opening.PlanFlipAlong=false;opening.OpenIn3D=false;
        foreach(var mode in new[]{"上悬","下悬"})foreach(var flip in new[]{false,true}) {
            var hung=OpeningConstruction.Copy(type);hung.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{new DoorWindowLayoutCell {Right=1200,Top=1500,Opening=mode,Material="玻璃"}});
            model.OpeningTypes[0]=hung;opening.PlanFlipNormal=flip;hung.OpenAngle=0;
            var closed=BuildingVolumeBuilder.BuildOpeningParts(model).Faces.Where(f=>f.Kind=="glass").SelectMany(f=>f.Points).ToArray();
            hung.OpenAngle=80;var actual=BuildingVolumeBuilder.BuildOpeningParts(model).Faces.Where(f=>f.Kind=="glass").SelectMany(f=>f.Points).ToArray();
            var pivotZ=opening.Sill+(mode=="上悬"?1448:52);var a=80*Math.PI/180*(mode=="上悬"?1:-1);
            var expected=closed.Select(p=> {
                var y=(flip?-p.Y:p.Y)-52;var z=p.Z-pivotZ;
                var normal=52+y*Math.Cos(a)-z*Math.Sin(a);
                return new Point3DModel(p.X,flip?-normal:normal,pivotZ+y*Math.Sin(a)+z*Math.Cos(a));
            }).ToArray();
            Check(expected.Zip(actual,(p,q)=>Math.Abs(p.X-q.X)<.000001&&Math.Abs(p.Y-q.Y)<.000001&&Math.Abs(p.Z-q.Z)<.000001).All(v=>v),"悬窗扇框与玻璃未采用同一净扇轴或镜像后轴翻转错误");
        }
        model.OpeningTypes[0]=type;opening.PlanFlipNormal=false;type.OpenAngle=0;
        model.Storeys.RemoveAll(s=>s.Id=="2F");model.Storeys.Add(new StoreyModel {Id="2F",Height=3000,Elevation=3000,TemplateStoreyId="1F"});
        var session=new BuildingModelEditSession(model);var original=BuildingModelJson.ToJson(session.Model);
        var draft=OpeningConstruction.Copy(type);draft.OpenAngle=80;
        Check(session.TrySetOpeningConstruction(opening.Id+"@STD@2F",draft,true,out var error),error);
        var volume=BuildingVolumeBuilder.BuildOpeningParts(session.Model);
        double Span(string id){var points=volume.Faces.Where(f=>f.ElementId==id&&f.Kind=="glass").SelectMany(f=>f.Points).ToArray();return points.Max(p=>p.Y)-points.Min(p=>p.Y);}
        Check(Span(opening.Id)<10&&Span(opening.Id+"@STD@2F")>400,"标准层特殊窗角度未进入场景或影响了来源层");
        var restored=BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        Check(BuildingVolumeBuilder.BuildOpeningParts(restored).Faces.Count==volume.Faces.Count,"保存重开丢失窗开启构造");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==original,"窗开启及编号覆盖不能一次撤销");
        type.OpenAngle=80;
        System.IO.Directory.CreateDirectory(".artifacts/opening-hinge");
        System.IO.File.WriteAllText(".artifacts/opening-hinge/c1215.json",BuildingModelJson.ToJson(model));
        var doorModel=SampleModelFactory.CreateEmptyModel("Explicit 3D door angle");
        doorModel.Walls.Add(new WallModel {Id="door-host",StoreyId="1F",X2=4000,Thickness=200});
        var door=new OpeningModel {Id="angle-door",HostWallId="door-host",Kind="门",Code="M1215",Width=1200,Height=1500,Offset=2000,OpenIn3D=true,PlanOpenAngle=20};
        var doorType=OpeningConstruction.Copy(type);doorType.Code=door.Code;doorType.Kind="门";
        doorType.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
            new DoorWindowLayoutCell {Right=600,Top=1500,Opening="左平开",IsDoor=true,Material="玻璃"},
            new DoorWindowLayoutCell {Left=600,Right=1200,Top=1500,Opening="右平开",IsDoor=true,Material="玻璃"}});
        doorModel.Openings.Add(door);doorModel.OpeningTypes.Add(doorType);
        var span=OpeningConstruction.Build(door,doorType,200).Where(p=>p.Kind=="sash").GroupBy(p=>p.Cell).Max(g=>g.Max(p=>p.Right)-g.Min(p=>p.Left));
        double DoorNormal()=>BuildingVolumeBuilder.BuildOpeningParts(doorModel).Faces.Where(f=>f.Kind=="sash").SelectMany(f=>f.Points).Max(p=>p.Y);
        Check(Math.Abs(DoorNormal()-(52+span*Math.Sin(80*Math.PI/180)))<.000001,"门的显式三维角度被平面角度覆盖");
        door.OpenIn3D=false;Check(DoorNormal()<100,"门的显式关闭开关被类型角度覆盖");
        door.OpenIn3D=true;doorType.OpenAngle=0;
        Check(Math.Abs(DoorNormal()-(52+span*Math.Sin(20*Math.PI/180)))<.000001,"无类型三维角度时门不再沿用实例平面角度");
        Console.WriteLine("PASS C1215 window scene angle80 importedFalse fixedFrames commonNetHinge rigidGlassSash mirrors oblique angles standardInstance persistence undo");
    }
    private static void CheckMultiRowCenter()
    {
        var cells=new List<DoorWindowLayoutCell>{new DoorWindowLayoutCell {Right=1915,Top=3000,Material="玻璃",Opening="固定"}};
        for(var column=0;column<4;column++)foreach(var row in new[]{0,1})cells.Add(new DoorWindowLayoutCell {
            Left=1915+column*750,Right=1915+(column+1)*750,Bottom=row==0?0:2200,Top=row==0?2200:3000,
            IsDoor=row==0,Material=row==0?"实板":"玻璃",Opening=row==0?"左平开":"固定"});
        cells.Add(new DoorWindowLayoutCell {Left=4915,Right=7660,Top=3000,Material="玻璃",Opening="固定"});
        var state=new OpeningLayoutEditing(7660,3000,cells);var selected=Enumerable.Range(1,8).ToArray();
        var before=DoorWindowElevationGeometryBuilder.SerializeCellLayout(state.Cells);
        var original=DoorWindowElevationGeometryBuilder.ParseCellLayout(before);
        Check(state.Center(selected,out var error),error);
        Check(state.Cells[0].Right==2330&&state.Cells[9].Left==5330,"MLC01C 八格居中后两侧不等宽。");
        foreach(var i in selected){var a=original[i];var b=state.Cells[i];
            Check(b.Left-a.Left==415&&b.Right-a.Right==415&&b.Bottom==a.Bottom&&b.Top==a.Top,
                "跨排居中改变内部尺寸或上下分格。");
            Check(a.Material==b.Material&&a.Opening==b.Opening&&a.IsDoor==b.IsDoor,"居中改变面板构造。");}
        var after=DoorWindowElevationGeometryBuilder.SerializeCellLayout(state.Cells);
        Check(state.Undo()&&DoorWindowElevationGeometryBuilder.SerializeCellLayout(state.Cells)==before&&state.Undo(true)
            &&DoorWindowElevationGeometryBuilder.SerializeCellLayout(state.Cells)==after,"跨排居中撤销／重做不完整。");
        Check(state.Center(selected,out error),error);
        Check(state.Undo()&&DoorWindowElevationGeometryBuilder.SerializeCellLayout(state.Cells)==before,"重复居中增加了撤销步骤。");
        Check(!state.Center(selected.Where(i=>i!=4),out error)&&DoorWindowElevationGeometryBuilder.SerializeCellLayout(state.Cells)==before,"漏选一格居中仍修改几何。");
        Check(!state.Center(new[]{0,1},out error)&&DoorWindowElevationGeometryBuilder.SerializeCellLayout(state.Cells)==before,"非矩形居中修改了几何。");
        var splitSides=new List<DoorWindowLayoutCell>{new DoorWindowLayoutCell {Right=1915,Top=2200},new DoorWindowLayoutCell {Right=1915,Bottom=2200,Top=3000}};
        splitSides.AddRange(original.Skip(1).Take(8));splitSides.Add(new DoorWindowLayoutCell {Left=4915,Right=7660,Top=2200});splitSides.Add(new DoorWindowLayoutCell {Left=4915,Right=7660,Bottom=2200,Top=3000});
        var partial=new OpeningLayoutEditing(7660,3000,splitSides);var bottomIds=new[]{2,4,6,8};
        Check(partial.Center(bottomIds,out error),error);
        Check(partial.Cells[0].Right==2330&&partial.Cells[1].Right==1915&&partial.Cells[10].Left==5330&&partial.Cells[11].Left==4915,"部分行居中影响了其他行。");
        Console.WriteLine("PASS layout multiRowCenter MLC01C eightCells rigidDimensions equalSides partialBand neighbourAlignment rejectIncomplete atomicUndo noOp");
    }
    private static void CheckEditedMergeCollections()
    {
        var model=SampleModelFactory.CreateEmptyModel("Edited merge collections");
        foreach(var floor in new[]{"1F","2F","3F"}) {
            if(floor!="1F")model.Storeys.Add(new StoreyModel {Id=floor,Height=4000,Elevation=floor=="2F"?4000:8000});
            model.Walls.Add(new WallModel {Id="wall-"+floor,StoreyId=floor,X2=12000,Height=4000});
            model.Openings.Add(new OpeningModel {Id="source-"+floor,HostWallId="wall-"+floor,Kind="门联窗",Code="MLC01B",Width=7599.672341,Height=3000,Offset=6000});
        }
        model.Storeys.Add(new StoreyModel {Id="4F",TemplateStoreyId="1F",Height=4000,Elevation=12000});
        model.Walls.Add(new WallModel {Id="target-wall",StoreyId="1F",Y1=15000,Y2=15000,X2=12000,Height=4000});
        model.Openings.Add(new OpeningModel {Id="target",HostWallId="target-wall",Kind="门联窗",Code="MLC01B",Width=7599.672341,Height=3000,Offset=6000});
        model.OpeningTypes.Add(OpeningConstruction.Default(model.Openings[0]));
        var session=new BuildingModelEditSession(model);var draft=OpeningConstruction.Copy(model.OpeningTypes[0]);
        draft.DivisionPreset="双扇等分";draft.GlassThickness=12;
        var before=BuildingModelJson.ToJson(session.Model);
        Check(session.TrySetOpeningDefinition("source-1F",draft,false,out var error),error);
        var physical=StandardStoreyLayout.Materialize(session.Model);
        Check(physical.Openings.All(o=>OpeningConstruction.Resolve(physical,o).GlassThickness==12),"同编号分格未应用到集合或标准层。");
        Check(session.Model.Openings.All(o=>o.Width==7599.672341),"分格编辑改变了原始精确洞口宽。");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before,"同编号分格没有一步撤销。");
        // A size conflict targets a different existing definition; both collections share the edited result.
        session.Model.Openings.Single(o=>o.Id=="target").Height=2800;
        session.Model.Openings.Single(o=>o.Id=="target").Code="MLC02";
        var target=OpeningConstruction.Default(session.Model.Openings.Single(o=>o.Id=="target"));target.GlassThickness=6;session.Model.OpeningTypes.Add(target);
        foreach(var o in session.Model.Openings.Where(o=>o.Id.StartsWith("source-")))o.Code="MLC02";
        session.Model.OpeningTypes[0].Code="MLC02-old";
        // Give sources a numeric size code so their resized code resolves to the existing target.
        target.Code="MLC7628";session.Model.Openings.Single(o=>o.Id=="target").Code=target.Code;
        foreach(var o in session.Model.Openings.Where(o=>o.Id.StartsWith("source-")))o.Code="MLC7630";
        session.Model.OpeningTypes[0].Code="MLC7630";
        draft.Code="MLC7630";OpeningConstruction.ResizeType(draft,7599.672341,2800);
        session=new BuildingModelEditSession(session.Model);
        before=BuildingModelJson.ToJson(session.Model);
        Check(session.TrySetOpeningDefinition("source-1F",draft,false,out error,conflictChoice:OpeningSizeConflictChoice.MergeEditedDefinition),error);
        physical=StandardStoreyLayout.Materialize(session.Model);
        Check(physical.Openings.All(o=>o.Code=="MLC7628"&&OpeningConstruction.Resolve(physical,o).GlassThickness==12),"并入类型丢弃了草稿或未同步集合。");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before,"并入更新没有原子撤销。");
        Check(session.TrySetOpeningDefinition("source-1F",draft,false,out error),error);
        Check(session.Model.Openings.Where(o=>o.Id.StartsWith("source-")).All(o=>o.Code=="MLC7628A")
            &&session.Model.Openings.Single(o=>o.Id=="target").Code=="MLC7628","后缀新建范围丢失或修改已有类型。");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before,"后缀集合未一步撤销。");
        var invalid=OpeningConstruction.Copy(draft);invalid.GlassThickness=-1;
        Check(!session.TrySetOpeningDefinition("source-1F",invalid,false,out error,conflictChoice:OpeningSizeConflictChoice.MergeEditedDefinition)
            &&BuildingModelJson.ToJson(session.Model)==before,"非法并入部分提交。");
        Console.WriteLine("PASS edited definition collections mergeKeepsDraft groupSuffix targetUnchanged exactWidth standardFloors atomicUndo invalidRollback");
    }
    private static void CheckDefaultCombinedAndOpeningPaths()
    {
        foreach(var width in new[]{2000d,2400d,7600d})foreach(var height in new[]{2100d,2200d,3000d})
        {
            var opening=new OpeningModel {Id="combined",Kind="门联窗",Code="MLC",Width=width,Height=height};
            var type=OpeningConstruction.Default(opening);
            var cells=DoorWindowElevationGeometryBuilder.ParseCellLayout(type.CustomCellLayout);
            var doors=cells.Where(c=>c.IsDoor).OrderBy(c=>c.Left).ToArray();
            Check(doors.Length==2&&doors.All(c=>c.Bottom==0&&c.Top==Math.Min(height,2200)&&c.Right-c.Left==750),"MLC >=2000 应有居中 1500 宽、2200 高双开扇，低洞口不得出界。");
            Check(doors[0].Left==(width-1500)/2&&doors[1].Right==(width+1500)/2&&doors[0].Opening=="左平开"&&doors[1].Opening=="右平开","MLC 双开扇应居中且合页在两外侧。");
            Check(cells.Where(c=>!c.IsDoor).All(c=>c.Opening=="固定")&&Math.Abs(cells.Sum(c=>(c.Right-c.Left)*(c.Top-c.Bottom))-width*height)<.01,"MLC 固定窗／亮子应完整填满其余洞口。");
            Check(type.SashWidth==50&&type.DoorFrameWidth==50&&type.DoorPlacement=="居中"&&OpeningConstruction.Validate(opening,type)==null,"MLC 默认扇框和有效构造未同步。");
        }
        foreach(var code in new[]{"C1121","FC1518","TC1818","DXC3018","GC0912","BYC1518","MLC1824"})
        {
            var opening=new OpeningModel {Code=code,Kind=code.StartsWith("MLC")?"门联窗":"窗",Width=1800,Height=2400};
            var type=OpeningConstruction.Default(opening);
            Check(type.SashWidth==50&&type.DoorFrameWidth==50,"窗和 MLC 默认必须打开扇框／门套："+code);
        }
        var item=new DoorWindowScheduleItem {Width=1100,Height=2100,ElevationType="普通窗",DivisionPreset="自定义",HasInstallationGap=false,
            CustomCellLayout="0,0,550,2100,右平开,0,0,玻璃|550,0,1100,2100,左平开,0,0,玻璃"};
        var saved=item.CustomCellLayout;
        var geometry=DoorWindowElevationGeometryBuilder.Build(item);
        Check(item.CustomCellLayout==saved&&geometry.Cells[0].Opening=="右平开"&&geometry.Cells[1].Opening=="左平开","用户手动设置的相对合页必须保留，不能强制套用默认方向。");
        var paths=OpeningElevationAdapter.CleanGeometryLines(geometry.Lines).Where(l=>l.OpeningPathId!=null).GroupBy(l=>l.OpeningPathId).ToArray();
        Check(paths.Length==2&&paths.All(g=>g.Count()==2),"每扇开启线应保留独立双段路径身份。");
        foreach(var path in paths)
        {
            var lines=path.ToArray();
            Check(Math.Abs(lines[0].X2-lines[1].X1)<.01&&Math.Abs(lines[0].Y2-lines[1].Y1)<.01,"每扇开启线必须连续经过合页尖头。");
            var tip=lines[0].X2;var free=lines[0].X1;
            Check(free<550?tip>free:tip<free,"用户设置的相对尖头必须仍指向实际合页。");
        }
        var model=SampleModelFactory.CreateEmptyModel("默认开启路径");
        model.Walls.Add(new WallModel {Id="host",StoreyId="1F",X2=8000,Thickness=200});
        foreach(var offset in new[]{1500d,4500d})model.Openings.Add(new OpeningModel {Id="w"+offset,HostWallId="host",Code="C1121",Kind="窗",Width=1100,Height=2100,Sill=800,Offset=offset});
        foreach(var direction in new[]{ElevationDirection.South,ElevationDirection.North})
        {
            var view=OrthographicProjector.Project(model,new ViewDefinitionModel {Id=direction.ToString(),Kind=ViewKind.Elevation,Direction=direction,Scale=100},null);
            var grouped=view.Lines.Where(l=>l.OpeningRole=="Opening").GroupBy(l=>l.OpeningArcId).ToArray();
            Check(grouped.Length==4&&grouped.All(g=>g.Key!=null&&g.Count()==2&&g.All(l=>l.LineType=="DASHED")),"前后立面必须保留每樘每扇的独立虚线路径。");
        }
        var custom=OpeningConstruction.Default(model.Openings[0]);custom.SashWidth=0;custom.DoorFrameWidth=0;custom.CustomCellLayout="0,0,1100,2100,固定,0,0,玻璃";
        model.OpeningTypes.Add(custom);var before=BuildingModelJson.ToJson(model);
        Check(OpeningConstruction.Resolve(model,model.Openings[0]).SashWidth==0&&BuildingModelJson.ToJson(model)==before,"新默认不能重置已有用户关闭的扇框及分格。");
        Console.WriteLine("PASS opening defaults MLC2000 centered1500 height2200 shortHole fixedSides sashEnabled outwardDefaults manualInwardHingesAllowed continuousIndependentPaths mirroredViews customPreserved");
    }
    private static void CheckRoundedOpeningFit()
    {
        Check(OpeningConstruction.FitRoundedOpeningSize(3770,3768)==3768,"3770 的小幅取整误差应向内适配为 3768，不能拉长墙。");
        Check(OpeningConstruction.FitRoundedOpeningSize(3770,3768.672)==3768,"自动适配必须向内取整毫米，不能再次越界。");
        Check(OpeningConstruction.FitRoundedOpeningSize(3770,3764)==3770&&OpeningConstruction.FitRoundedOpeningSize(3800,3768)==3800,"真实超限不能被自动适配掩盖。");
        Check(OpeningConstruction.FitRoundedOpeningSize(3760,3768)==3760&&OpeningConstruction.FitRoundedOpeningSize(3745,3744)==3745,"不跨越不同 10 mm 档位，不改已合法尺寸。");
        var model=SampleModelFactory.CreateEmptyModel("MLC04B 取整适配");model.Storeys.RemoveAll(s=>s.Id!="1F");model.Storeys[0].Height=3000;
        model.Storeys.Add(new StoreyModel {Id="2F",Name="标准层",Elevation=3000,Height=3000,TemplateStoreyId="1F"});
        model.Walls.Add(new WallModel {Id="short",StoreyId="1F",X2=3768,Thickness=200});
        model.Walls.Add(new WallModel {Id="long",StoreyId="1F",X2=4000,Y1=5000,Y2=5000,Thickness=200});
        model.Openings.Add(new OpeningModel {Id="a",HostWallId="short",Code="MLC04B",Kind="门联窗",Offset=1884,Width=3768,Height=3000});
        model.Openings.Add(new OpeningModel {Id="b",HostWallId="long",Code="MLC04B",Kind="门联窗",Offset=2000,Width=3768,Height=3000});
        model.OpeningTypes.Add(OpeningConstruction.Default(model.Openings[0]));
        var session=new BuildingModelEditSession(model);var before=BuildingModelJson.ToJson(session.Model);
        var draft=OpeningConstruction.Copy(model.OpeningTypes[0]);OpeningConstruction.ResizeType(draft,3770,3000);draft.GlassThickness=12;
        Check(session.TrySetOpeningDefinition("a",draft,false,out var error),error);
        var physical=StandardStoreyLayout.Materialize(session.Model);
        Check(physical.Openings.Count==4&&physical.Openings.All(o=>o.Width==3768&&o.Code=="MLC04B"),"同编号及标准层必须采用统一可用尺寸，不误改编号。");
        Check(physical.Openings.All(o=>OpeningConstruction.Resolve(physical,o).GlassThickness==12),"自动适配不得丢弃编辑的分格／构造。");
        Check(session.Model.Walls.Single(w=>w.Id=="short").X2==3768&&PlanEditing.ValidateOpening(physical,physical.Walls.First(w=>w.Id=="short"),physical.Openings.First(o=>o.Id=="a"))==null,"不能延长墙或放宽几何校验。");
        var changed=BuildingModelJson.ToJson(session.Model);
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before&&session.Redo()&&BuildingModelJson.ToJson(session.Model)==changed,"自动适配和构造必须原子撤销重做。");
        var saved=BuildingModelJson.FromJson(changed);
        Check(saved.Openings.All(o=>o.Width==3768)&&OpeningConstruction.Resolve(saved,saved.Openings[0]).GlassThickness==12,"保存重开不得恢复超限草稿。");
        OpeningConstruction.ResizeType(draft,3800,3000);
        Check(!session.TrySetOpeningDefinition("a",draft,false,out error)&&BuildingModelJson.ToJson(session.Model)==changed,"真实超限必须拒绝且保留模型。");
        Check(session.TrySetOpeningGeometry("a",1884,3770,3000,0,out error)&&session.Model.Openings.First(o=>o.Id=="a").Width==3768,"属性参数路径也应适配取整误差。");
        session.Model.Storeys.First(s=>s.Id=="2F").Height=2998;
        var limits=session.GetOpeningSizeLimits("a",true);
        Check(limits[0]==3768&&limits[1]==2998,"仅当前樘也必须核对洞口共用标准层的可用高度。");
        Console.WriteLine("PASS opening roundedFit MLC04B 3770to3768 inwardWholeMillimetre sharedStandardFloors preserveCode construction atomicUndo save strictReject propertyPath unchangedWall");
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
