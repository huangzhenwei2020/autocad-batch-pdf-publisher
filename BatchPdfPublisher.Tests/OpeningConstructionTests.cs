using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;

internal static class OpeningConstructionTests
{
    internal static void Run()
    {
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
        Console.WriteLine("PASS 门窗三维编辑：共享默认、固定外框、双扇门缝、推拉轨道错位与搭接、100 mm 默认进深");
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
