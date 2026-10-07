using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;

internal static class OpeningPlanGeometryTests
{
    public static void Run()
    {
        var doors=Atlas(true);var windows=Atlas(false);
        foreach(var model in new[]{doors,windows})foreach(var opening in model.Openings) {
            var type=model.OpeningTypes.Single(t=>t.Code==opening.Code);
            var lines=OpeningPlanGeometry.Build(opening,type,200);
            Check(lines.Count>0&&lines.All(l=>new[]{l.X1,l.Y1,l.X2,l.Y2}.All(Finite)),"空图例或非有限坐标 "+opening.Code);
            Check(OpeningConstruction.Validate(opening,type)==null,"图例类型参数无效 "+opening.Code);
            var flipped=new OpeningModel {Width=opening.Width,Height=opening.Height,Sill=opening.Sill,Kind=opening.Kind,Code=opening.Code,PlanFlipAlong=true,PlanFlipNormal=true};
            var mirror=OpeningPlanGeometry.Build(flipped,type,200);
            Check(lines.Count==mirror.Count&&lines.Zip(mirror,(a,b)=>Math.Abs(a.X1+b.X1-opening.Width)<.001&&Math.Abs(a.Y1+b.Y1)<.001).All(x=>x),"实例镜像不一致 "+opening.Code);
            var wall=model.Walls.Single(w=>w.Id==opening.HostWallId);
            var label=OrthographicProjector.CreatePlanOpeningLabel(wall,opening,250,.7);
            Check(label.Text==opening.Code&&label.Height==250&&label.WidthFactor==.7,"原编号或文字参数丢失");
            Check(BuildingModelJson.FromJson(BuildingModelJson.ToJson(model)).OpeningTypes.Single(t=>t.Code==opening.Code).PlanStyle==type.PlanStyle,"平面形式未持久化");
        }
        var single=doors.Openings[0];var singleType=doors.OpeningTypes[0];
        var handle=OpeningPlanGeometry.DirectionHandle(single,singleType,200);
        Check(handle.X==52&&handle.Y==873,"圆点不是净扇宽门扇自由端");
        var doubleLines=OpeningPlanGeometry.Build(doors.Openings[1],doors.OpeningTypes[1],200);
        Check(doubleLines.Max(l=>Math.Max(l.Y1,l.Y2))==923,"双扇门错误使用整宽弧");
        var uneven=OpeningPlanGeometry.Build(doors.Openings[2],doors.OpeningTypes[2],200);
        Check(uneven.Any(l=>Math.Abs(l.Y2-323)<.001)&&uneven.Any(l=>Math.Abs(l.Y2-923)<.001),"子母扇半径未扣除对应边框和边缝");
        Check(OpeningPlanGeometry.DirectionHandle(windows.Openings[0],windows.OpeningTypes[0],200)==null,"固定窗不应有方向点");
        Check(OpeningPlanGeometry.DirectionHandle(windows.Openings[2],windows.OpeningTypes[2],200)!=null,"推拉窗缺方向点");
        Check(OpeningPlanGeometry.DirectionHandle(windows.Openings[4],windows.OpeningTypes[4],200)==null,"上悬窗不应套用平开夹点");
        var rectangular=OpeningPlanGeometry.Build(windows.Openings[7],windows.OpeningTypes[7],200);
        Check(rectangular.All(l=>Math.Abs(l.X1-l.X2)<.001||Math.Abs(l.Y1-l.Y2)<.001),"矩形凸窗出现斜侧边");
        var trapezoid=OpeningPlanGeometry.Build(windows.Openings[8],windows.OpeningTypes[8],200);
        Check(trapezoid.Any(l=>Math.Abs(l.X1-l.X2)>1&&Math.Abs(l.Y1-l.Y2)>1),"梯形凸窗没有斜侧边");
        var high=windows.Openings[10];
        Check(!OpeningPlanGeometry.CutsWall(high),"高窗断墙");
        var rotated=BuildingModelJson.FromJson(BuildingModelJson.ToJson(doors));
        rotated.Walls[0].X2=rotated.Walls[0].X1;rotated.Walls[0].Y2=rotated.Walls[0].Y1+7000;
        var world=OrthographicProjector.CreatePlanDetailSymbols(new BuildingModelDocument {Walls=new(){rotated.Walls[0]},Openings=new(){single},OpeningTypes=doors.OpeningTypes},"1F");
        var local=OpeningPlanGeometry.Build(single,singleType,200);var wall0=rotated.Walls[0];var startY=wall0.Y1+single.Offset-single.Width/2;
        Check(world.Zip(local,(a,b)=>Math.Abs(a.X1-(wall0.X1-b.Y1))<.001&&Math.Abs(a.Y1-(startY+b.X1))<.001).All(v=>v),"斜墙局部符号变换错误");
        LabelAndClearanceChecks(doors);
        AngleAndCodeChecks(doors);
        CenterChecks(doors);
        PlacementTypeChecks(doors,windows);
        ThresholdChecks(doors,windows);
        FrameClearanceChecks(doors,windows);
        ElevationHingeTipChecks();
        PairedHingeChecks();
        HeightEditChecks();
        ScopedSizeChecks();
        WindowHeightLimitChecks();
        WindowDirectionAndArcChecks(doors);
        WindowPlanSwingChecks();
        Directory.CreateDirectory(".artifacts/opening-symbols");
        File.WriteAllText(".artifacts/opening-symbols/door-atlas.json",BuildingModelJson.ToJson(doors));
        File.WriteAllText(".artifacts/opening-symbols/window-atlas.json",BuildingModelJson.ToJson(windows));
        foreach(var index in new[]{0,4}) {
            var closeup=BuildingModelJson.FromJson(BuildingModelJson.ToJson(doors));
            closeup.Openings=closeup.Openings.Where(o=>o.Id=="o"+index).ToList();
            var instance=closeup.Openings.Single();closeup.Walls=closeup.Walls.Where(w=>w.Id==instance.HostWallId).ToList();
            var host=closeup.Walls.Single();host.X1=0;host.Y1=0;host.X2=instance.Width+400;host.Y2=0;instance.Offset=host.X2/2;
            closeup.Axes=BuildingAxisLayout.Resolve(closeup);foreach(var axis in closeup.Axes)axis.Hidden=true;
            File.WriteAllText($".artifacts/opening-symbols/frame-leaf-{index}.json",BuildingModelJson.ToJson(closeup));
        }
        Console.WriteLine("PASS opening plan atlas 24 types originalCodes leafRadii frames tracks mirror highWindow rotatedWall handles serialization");
    }
    private static void WindowDirectionAndArcChecks(BuildingModelDocument doors)
    {
        var model=SampleModelFactory.CreateEmptyModel("Window directions");model.Storeys[0].Height=4000;
        model.Walls.Add(new WallModel {Id="casement-host",StoreyId="1F",X2=7000,Thickness=200});
        var opening=new OpeningModel {Id="casement",HostWallId="casement-host",Code="C1824",Kind="窗",Width=1800,Height=2400,Sill=400,Offset=2000,OpenIn3D=false};
        model.Openings.Add(opening);
        var type=OpeningConstruction.Default(opening);type.DivisionPreset="自定义";type.OpenAngle=80;
        type.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
            new DoorWindowLayoutCell {Right=900,Top=2000,Opening="左平开",Material="玻璃"},
            new DoorWindowLayoutCell {Left=900,Right=1800,Top=2000,Opening="右平开",Material="玻璃"},
            new DoorWindowLayoutCell {Right=1800,Bottom=2000,Top=2400,Opening="固定",Material="玻璃"}});
        model.OpeningTypes.Add(type);
        var baseline=OpeningPlanGeometry.DirectionHandle(opening,type,200);
        Check(baseline!=null,"带固定亮子的平开窗缺方向夹点");
        Check(!OpeningPlanGeometry.HasSwingDoor(opening,type)&&OpeningPlanGeometry.HasPlanSwing(opening,type),"平开窗的平面角度和门三维开关未区分");
        var lines=OpeningPlanGeometry.Build(opening,type,200);
        Check(lines.Count(l=>l.OpeningArcId?.StartsWith("swing-")==true)==2*OpeningPlanGeometry.SwingArcSegments,"双平开窗没有按两扇分别画弧");
        var session=new BuildingModelEditSession(model);var before=BuildingModelJson.ToJson(session.Model);
        foreach(var along in new[]{false,true})foreach(var normal in new[]{false,true}) {
            Check(session.TrySetOpeningPlacement(opening.Id,opening.HostWallId,opening.Offset,along,normal,out var error),"窗方向提交失败 "+error);
            var changed=session.Model.Openings[0];var handle=OpeningPlanGeometry.DirectionHandle(changed,type,200);
            Check(Math.Abs(handle.X-(along?opening.Width-baseline.X:baseline.X))<.001&&Math.Abs(handle.Y-(normal?-baseline.Y:baseline.Y))<.001,"窗方向夹点镜像错误");
            Check(BuildingModelJson.ToJson(new BuildingModelDocument {OpeningTypes=session.Model.OpeningTypes})==BuildingModelJson.ToJson(new BuildingModelDocument {OpeningTypes=model.OpeningTypes}),"实例翻转改变类型");
            var reopened=BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
            Check(reopened.Openings[0].PlanFlipAlong==along&&reopened.Openings[0].PlanFlipNormal==normal,"窗方向保存丢失");
            if(along||normal){Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before,"窗方向不是一次撤销");}
        }
        opening.Sill=2000;Check(OpeningPlanGeometry.DirectionHandle(opening,type,200)!=null,"高平开窗缺方向控件");
        var fixedType=OpeningConstruction.Copy(type);fixedType.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{new DoorWindowLayoutCell {Right=1800,Top=2400,Opening="固定",Material="玻璃"}});
        Check(OpeningPlanGeometry.DirectionHandle(opening,fixedType,200)==null,"固定窗产生无效方向控件");
        var door=doors.Openings[0];var doorType=doors.OpeningTypes[0];
        var local=OpeningPlanGeometry.Build(door,doorType,200);
        Check(local.Count(l=>l.OpeningArcId!=null)==OpeningPlanGeometry.SwingArcSegments&&local.Where(l=>l.OpeningArcId!=null).All(l=>l.LineType=="DASHED"&&l.StrokeAreaId==null),"主开启弧未使用独立连续虚线");
        Check(local.Where(l=>l.OpeningArcId==null).All(l=>l.LineType==null),"门框、门扇或箭头被改成虚线");
        var world=OrthographicProjector.CreatePlanDetailSymbols(doors,"1F");
        Check(world.Count(l=>l.OpeningArcId!=null)==doors.Openings.Sum(o=>OpeningPlanGeometry.Build(o,OpeningConstruction.Resolve(doors,o),200).Count(l=>l.OpeningArcId!=null)),"投影丢失开启弧标识");
        Check(world.Where(l=>l.OpeningArcId!=null).All(l=>l.LineType=="DASHED"),"投影丢失虚线");
        Directory.CreateDirectory(".artifacts/window-directions");opening.Sill=400;
        File.WriteAllText(".artifacts/window-directions/c1824.json",BuildingModelJson.ToJson(model));
        Console.WriteLine("PASS window direction transom highWindow fixedDisabled mirrors instanceTypeIsolation undo persistence swingArcContinuousDashed solidFrames");
    }
    private static void WindowPlanSwingChecks()
    {
        var model=SampleModelFactory.CreateEmptyModel("C1216 plan casement");model.Storeys[0].Height=3500;
        var wall=new WallModel {Id="window-host",StoreyId="1F",X2=4000,Thickness=200};model.Walls.Add(wall);
        var opening=new OpeningModel {Id="window",HostWallId=wall.Id,Code="C1216",Kind="窗",Width=1200,Height=1600,Sill=900,Offset=2000,OpenIn3D=false};
        var type=OpeningConstruction.Default(opening);type.DivisionPreset="自定义";type.OpenAngle=80;
        type.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
            new DoorWindowLayoutCell {Right=600,Top=1600,Opening="左平开",Material="玻璃"},
            new DoorWindowLayoutCell {Left=600,Right=1200,Top=1600,Opening="右平开",Material="玻璃"}});
        model.Openings.Add(opening);model.OpeningTypes.Add(type);
        var volumeBefore=BuildingVolumeBuilder.BuildOpeningParts(model).Faces.SelectMany(f=>f.Points).Select(p=>$"{p.X:R}|{p.Y:R}|{p.Z:R}").ToArray();
        foreach(var angle in new[]{0d,15,30,45,90,37.5,120,180})foreach(var along in new[]{false,true})foreach(var normal in new[]{false,true}) {
            opening.PlanOpenAngle=angle;opening.PlanFlipAlong=along;opening.PlanFlipNormal=normal;
            var areas=new List<ViewStrokeArea>();var lines=OpeningPlanGeometry.Build(opening,type,200,areas);
            var arcs=lines.Where(l=>l.OpeningArcId?.StartsWith("swing-")==true).GroupBy(l=>l.OpeningArcId).ToArray();
            Check(arcs.Length==(angle==0?0:2)&&arcs.All(g=>g.Count()==OpeningPlanGeometry.SwingArcSegments&&g.All(l=>l.LineType=="DASHED")),"C1216 两扇平开弧数量或线型错误");
            var handle=OpeningPlanGeometry.DirectionHandle(opening,type,200);
            var regions=OpeningPlanGeometry.SelectionRegions(opening,type,200);
            Check(regions.Count==3&&Math.Abs(regions[1].Last().X-handle.X)<.00001&&Math.Abs(regions[1].Last().Y-handle.Y)<.00001,"窗的预选扇形没有跟随自由端");
            if(angle>0)Check(Math.Abs(arcs[0].First().X1-handle.X)<.00001&&Math.Abs(arcs[0].First().Y1-handle.Y)<.00001,"窗的圆夹点不在实际扇线及弧的自由端");
            Check(lines.Where(l=>l.StrokeAreaId!=null).All(l=>l.LineType==null),"窗框、扇线变成虚线");
            var indications=lines.Where(l=>l.OpeningArcId!=null).ToArray();
            Check(indications.Length==(angle==0?0:2*(OpeningPlanGeometry.SwingArcSegments+27))&&indications.All(l=>l.LineType=="DASHED"&&l.StrokeAreaId==null),"窗开启线、弧或箭头不是无厚度虚线");
            var leaves=indications.Where(l=>l.OpeningArcId.StartsWith("window-leaf-")).ToArray();
            Check(leaves.Length==(angle==0?0:2),"每个开启窗扇应只有一条虚线，不能画扇厚轮廓");
            if(angle>0)Check(Math.Abs(leaves[0].X2-handle.X)<.00001&&Math.Abs(leaves[0].Y2-handle.Y)<.00001,"窗的单线开启扇端点不在夹点");
            opening.PlanOpenAngle=0;var closed=OpeningPlanGeometry.Build(opening,type,200);opening.PlanOpenAngle=angle;
            string Key(ViewLine line)=>$"{line.X1:R}|{line.Y1:R}|{line.X2:R}|{line.Y2:R}";
            Check(lines.Where(l=>l.OpeningArcId==null).Select(Key).SequenceEqual(closed.Select(Key)),"开启时原闭合窗框及玻璃线消失或改变");
        }
        opening.PlanFlipAlong=false;opening.PlanFlipNormal=false;opening.PlanOpenAngle=90;
        var session=new BuildingModelEditSession(model);var before=BuildingModelJson.ToJson(session.Model);
        Check(session.TrySetOpeningPresentation(opening.Id,opening.Code,30,false,out var error),"窗平面角度不能保存 "+error);
        Check(session.Model.Openings[0].PlanOpenAngle==30&&session.Model.Openings[0].OpenIn3D==false&&session.Model.OpeningTypes[0].OpenAngle==80,"平面角度改动了窗的三维构造参数");
        Check(BuildingVolumeBuilder.BuildOpeningParts(session.Model).Faces.SelectMany(f=>f.Points).Select(p=>$"{p.X:R}|{p.Y:R}|{p.Z:R}").SequenceEqual(volumeBefore),"窗平面角度改变三维实际几何");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before,"窗角度不是一次撤销");
        var mixed=OpeningConstruction.Copy(type);mixed.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
            new DoorWindowLayoutCell {Right=600,Top=1600,Opening="左平开",Material="玻璃"},
            new DoorWindowLayoutCell {Left=600,Right=1200,Top=1600,Opening="固定",Material="玻璃"}});
        Check(OpeningPlanGeometry.Build(opening,mixed,200).Count(l=>l.OpeningArcId?.StartsWith("swing-")==true)==OpeningPlanGeometry.SwingArcSegments,"固定格也生成了平开弧");
        var sliding=OpeningConstruction.Copy(type);sliding.CustomCellLayout=type.CustomCellLayout.Replace("左平开","左推拉").Replace("右平开","右推拉");
        var slidingLines=OpeningPlanGeometry.Build(opening,sliding,200);
        Check(!OpeningPlanGeometry.HasPlanSwing(opening,sliding)&&slidingLines.All(l=>l.OpeningArcId?.StartsWith("swing-")!=true),"推拉窗错误画平开弧");
        Check(slidingLines.Count(l=>l.OpeningArcId?.StartsWith("window-slide-")==true)==6&&slidingLines.Where(l=>l.OpeningArcId!=null).All(l=>l.LineType=="DASHED"&&l.StrokeAreaId==null),"推拉窗开启箭头不是单线虚线");
        wall.X2=2400;wall.Y2=3200;var world=OrthographicProjector.CreatePlanDetailSymbols(model,"1F");
        var local=OpeningPlanGeometry.Build(opening,type,200);var origin=WallReferenceGeometry.BodyPoint(wall,840,1120);
        Check(world.Zip(local,(a,b)=>Math.Abs(a.X1-(origin.X+.6*b.X1-.8*b.Y1))<.001&&Math.Abs(a.Y1-(origin.Y+.8*b.X1+.6*b.Y1))<.001&&a.LineType==b.LineType).All(b=>b),"斜墙窗开启符号投影不一致");
        wall.X2=4000;wall.Y2=0;opening.PlanOpenAngle=90;
        Directory.CreateDirectory(".artifacts/window-directions");File.WriteAllText(".artifacts/window-directions/c1216.json",BuildingModelJson.ToJson(model));
        Console.WriteLine("PASS C1216 plan casement leftRightLeaves lightDashedArcs angle15-30-45-90-custom mirrors freeEndGrip selection fixedAndSlidingDistinct obliqueProjection 3Dunchanged undo");
    }
    private static void ScopedSizeChecks()
    {
        var model=SampleModelFactory.CreateEmptyModel("Scoped opening sizes");
        model.Walls.Add(new WallModel {Id="size-host",StoreyId="1F",X2=6000,Thickness=200});
        model.Storeys.RemoveAll(s=>s.Id=="2F");
        model.Storeys.Add(new StoreyModel {Id="2F",Name="二层",Height=2800,Elevation=3000,TemplateStoreyId="1F"});
        model.Openings.Add(new OpeningModel {Id="size-a",HostWallId="size-host",Code="M1525",Kind="门",Width=1500,Height=2500,Offset=1700});
        model.Openings.Add(new OpeningModel {Id="size-b",HostWallId="size-host",Code="M1525",Kind="门",Width=1500,Height=2500,Offset=4500});
        var type=OpeningConstruction.Default(model.Openings[0]);type.DivisionPreset="自定义";
        type.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
            new DoorWindowLayoutCell {Right=750,Top=2200,IsDoor=true,Opening="左平开",Material="玻璃"},
            new DoorWindowLayoutCell {Left=750,Right=1500,Top=2200,IsDoor=true,Opening="右平开",Material="玻璃"},
            new DoorWindowLayoutCell {Bottom=2200,Right=1500,Top=2500,Opening="固定",Material="玻璃"}});
        model.OpeningTypes.Add(type);
        var target=OpeningConstruction.Default(new OpeningModel {Code="M1621",Kind="门",Width=1600,Height=2100});target.GlassThickness=16;
        model.OpeningTypes.Add(target);
        var session=new BuildingModelEditSession(model);var original=BuildingModelJson.ToJson(session.Model);
        var plan=session.PlanOpeningSizeCode("size-a",1600,2100,out var planError);
        Check(plan!=null&&plan.HasConflict&&plan.CanMerge&&plan.Code=="M1621"&&plan.NewCode=="M1621A",planError??"未返回可合并编号决策");
        Check(BuildingModelJson.ToJson(session.Model)==original,"编号决策改写模型");
        Check(session.TrySetOpeningParameters("size-a",1700,1600,2100,0,0,true,out var error),error);
        var a=session.Model.Openings.Single(o=>o.Id=="size-a");var b=session.Model.Openings.Single(o=>o.Id=="size-b");
        Check(a.Code=="M1621A"&&a.Width==1600&&a.Height==2100,"单樘尺寸变更未创建无冲突尺寸编号");
        Check(b.Code=="M1525"&&b.Width==1500&&b.Height==2500,"单樘尺寸变更影响其他位置");
        var physical=StandardStoreyLayout.Materialize(session.Model);
        Check(physical.Openings.Single(o=>o.Id=="size-a@STD@2F").Code==a.Code&&physical.Openings.Single(o=>o.Id=="size-a@STD@2F").Height==2100,"共用平面洞口不同步");
        Check(session.Model.OpeningTypes.Single(t=>t.Code==a.Code).Height==2100&&OpeningConstruction.Validate(a,OpeningConstruction.Resolve(session.Model,a))==null,"新编号分格未随洞口尺寸更新");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==original,"单樘尺寸、编号和类型不能一起撤销");
        Check(session.TrySetOpeningParameters("size-a",1700,1600,2100,0,0,false,out error),error);
        Check(StandardStoreyLayout.Materialize(session.Model).Openings.All(o=>o.Code=="M1621A"&&o.Width==1600&&o.Height==2100),"同编号尺寸批改或新尺寸编号不完整");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==original,"批量尺寸不能一次撤销");
        Check(session.TrySetOpeningParameters("size-a",1700,1600,2100,0,0,true,out error,OpeningSizeConflictChoice.MergeExisting),error);
        a=session.Model.Openings.Single(o=>o.Id=="size-a");
        Check(a.Code=="M1621"&&OpeningConstruction.Resolve(session.Model,a).GlassThickness==16
            &&session.Model.OpeningTypes.All(t=>t.Code!="M1621A")&&session.Model.Openings.Single(o=>o.Id=="size-b").Code=="M1525","合并未采用已有构造或影响其他位置");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==original,"合并不能一次撤销");
        Check(session.TrySetOpeningParameters("size-a",1700,1600,2100,0,0,false,out error,OpeningSizeConflictChoice.MergeExisting),error);
        Check(StandardStoreyLayout.Materialize(session.Model).Openings.All(o=>o.Code=="M1621"&&o.Height==2100),"整组未合并已有类型");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==original,"整组合并不能一次撤销");
        plan=session.PlanOpeningSizeCode("size-a",1600,2105,out planError);
        Check(plan!=null&&plan.HasConflict&&!plan.CanMerge,"同尺寸式编号但实际尺寸不同仍允许合并");
        Check(!session.TrySetOpeningParameters("size-a",1700,1600,2105,0,0,true,out error,OpeningSizeConflictChoice.MergeExisting)
            &&BuildingModelJson.ToJson(session.Model)==original,"不匹配类型合并未整笔回滚");
        foreach(var size in new[]{(width:3500d,height:2100d),(width:1600d,height:2900d),(width:1600d,height:2d)}) {
            Check(!session.TrySetOpeningParameters("size-a",1700,size.width,size.height,0,0,false,out error)&&!string.IsNullOrWhiteSpace(error),"批量洞口冲突或层高无效仍提交："+size.width+"x"+size.height+" / "+error);
            Check(BuildingModelJson.ToJson(session.Model)==original,"批量尺寸失败未整批回滚");
        }
        var draft=OpeningConstruction.Copy(type);OpeningConstruction.ResizeType(draft,1600,2100);draft.GlassThickness=12;
        Check(session.TrySetOpeningDefinition("size-a",draft,true,out error),error);
        var reopened=BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        Check(reopened.Openings.Single(o=>o.Id=="size-a").Height==2100&&OpeningConstruction.Resolve(reopened,reopened.Openings.Single(o=>o.Id=="size-a")).GlassThickness==12,"分格编辑尺寸未保存");
        Console.WriteLine("PASS opening scoped sizes single group automaticCode conflictPlan mergeExisting adoptConstruction suffix mismatchRollback standardPlan layout persistence undo");
    }
    private static void WindowHeightLimitChecks()
    {
        var model=SampleModelFactory.CreateEmptyModel("C0722 height limit");model.FindStorey("1F").Height=3000;
        model.Walls.Add(new WallModel {Id="height-limit-host",StoreyId="1F",X2=2000,Thickness=200});
        var opening=new OpeningModel {Id="height-limit",HostWallId="height-limit-host",Code="C0722",Kind="窗",Width=700,Height=2200,Sill=800,Offset=747.89530825003};
        model.Openings.Add(opening);model.OpeningTypes.Add(OpeningConstruction.Default(opening));
        var session=new BuildingModelEditSession(model);var original=BuildingModelJson.ToJson(session.Model);
        Check(!session.TrySetOpeningParameters(opening.Id,opening.Offset,900,2600,800,0,true,out var error)
            &&new[]{"800","2600","3400","3000","2200"}.All(error.Contains),"截图洞口超高缺少完整数值提示："+error);
        Check(BuildingModelJson.ToJson(session.Model)==original&&!session.CanUndo,"超高洞口改写了模型或撤销历史");
        Check(session.TrySetOpeningParameters(opening.Id,opening.Offset,900,2200,800,0,true,out error),error);
        Check(session.Model.Openings.Single().Code=="C0922"&&session.Model.Openings.Single().Width==900,"有效窗尺寸没有生成 C0922");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==original,"洞口尺寸与编号没有一次撤销");
        Console.WriteLine("PASS screenshot C0722 heightLimit numericError atomicReject validC0922 oneUndo");
    }
    private static void HeightEditChecks()
    {
        var model=SampleModelFactory.CreateEmptyModel("Height edit regression");
        model.Walls.Add(new WallModel {Id="host",StoreyId="1F",X2=4000,Thickness=200});
        var opening=new OpeningModel {Id="height-door",HostWallId="host",Code="M1525",Kind="门",Width=1500,Height=2500,Offset=2000};
        var type=OpeningConstruction.Default(opening);
        type.DivisionPreset="自定义";type.HasInstallationGap=true;type.InstallationGap=20;
        type.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
            new DoorWindowLayoutCell {Right=730,Top=2200,IsDoor=true,Opening="左平开",Material="玻璃"},
            new DoorWindowLayoutCell {Left=730,Right=1460,Top=2200,IsDoor=true,Opening="右平开",Material="玻璃"},
            new DoorWindowLayoutCell {Bottom=2200,Right=1460,Top=2460,Opening="固定",Material="玻璃"}});
        model.Openings.Add(opening);model.OpeningTypes.Add(type);
        var session=new BuildingModelEditSession(model);
        var original=BuildingModelJson.ToJson(session.Model);var revision=session.Revision;
        foreach(var height in new[]{0d,2,21,40,double.NaN,double.PositiveInfinity,4000}) {
            Check(!session.TrySetOpeningGeometry(opening.Id,2000,1500,height,0,out var error)&&!string.IsNullOrWhiteSpace(error),
                "过小或无效门高进入预览模型："+height);
            Check(session.Revision==revision&&BuildingModelJson.ToJson(session.Model)==original,"拒绝门高修改后模型或撤销历史变化");
        }
        Check(session.TrySetOpeningGeometry(opening.Id,2000,1500,2100,0,out var validError),"有效门高被拒绝："+validError);
        var resized=session.Model.Openings.Single();var resizedType=session.Model.OpeningTypes.Single();
        Check(resizedType.Height==2500&&resizedType.CustomCellLayout==type.CustomCellLayout,"实例尺寸修改覆盖共享分格定义");
        Check(OpeningConstruction.Validate(resized,resizedType)==null,"门高修改后分格无效");
        Check(OpeningPlanGeometry.DirectionHandle(resized,resizedType,200)!=null,"门高修改后方向夹点丢失");
        Check(OpeningPlanGeometry.Build(resized,resizedType,200).Count>0&&OpeningPlanGeometry.SelectionRegions(resized,resizedType,200).Count==3,
            "门高修改后平面或预选区域无效");
        Check(BuildingVolumeBuilder.BuildOpeningParts(session.Model).Faces.Count>0,"门高修改后三维构造为空");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==original,"门高修改不能一次撤销");
        Check(session.Redo()&&session.Model.Openings.Single().Height==2100,"门高修改不能重做");
        Console.WriteLine("PASS opening height custom-grid gap invalid-input render geometry undo redo");
    }
    private static void ElevationHingeTipChecks()
    {
        foreach(var kind in new[]{"门","窗"})foreach(var mode in new[]{"左平开","右平开","上悬","下悬","中悬"}) {
            var opening=new OpeningModel {Code="TIP",Kind=kind,Width=1000,Height=2000};
            var type=OpeningConstruction.Default(opening);type.DivisionPreset="自定义";
            type.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
                new DoorWindowLayoutCell {Right=1000,Top=2000,IsDoor=kind=="门",Opening=mode,Material="无"}});
            var geometry=DoorWindowElevationGeometryBuilder.Build(OpeningElevationAdapter.ToScheduleItem(opening,type,1000,2000));
            var lines=geometry.Lines.Where(l=>l.Role==DoorWindowLineRole.Opening).ToArray();
            if(mode=="中悬") {
                Check(lines.Length==5&&lines.Skip(1).All(l=>Math.Abs(l.Y2-(lines[0].Y1+lines[0].Y2)/2)<.001),
                    kind+"中悬开启线尖端未指向中轴");
            } else {
                Check(lines.Length==2&&Math.Abs(lines[0].X2-lines[1].X1)<.001&&Math.Abs(lines[0].Y2-lines[1].Y1)<.001,
                    kind+mode+"开启线尖端不连续");
                var tip=lines[0];
                Check(mode=="左平开"?tip.X2<tip.X1:mode=="右平开"?tip.X2>tip.X1:
                    mode=="上悬"?tip.Y2>tip.Y1:tip.Y2<tip.Y1,kind+mode+"开启线尖端未指向合页");
            }
        }
        Console.WriteLine("PASS elevation hinge tips doors/windows left/right/top/bottom/pivot");
    }
    private static void PairedHingeChecks()
    {
        var opening=new OpeningModel {Id="pair",HostWallId="host",Code="M1525",Kind="门",Width=1500,Height=2500,Offset=1500,OpenIn3D=true};
        var generic=OpeningConstruction.Default(opening);generic.DivisionPreset="双扇等分";
        generic.CustomCellLayout=null;generic.CellOpeningModes=null;generic.OpeningMode="双扇平开";
        var geometry=DoorWindowElevationGeometryBuilder.Build(OpeningElevationAdapter.ToScheduleItem(opening,generic,1500,2500));
        Check(geometry.Cells[0].Opening=="左平开"&&geometry.Cells[1].Opening=="右平开","通用双扇合页未放在两侧外框");
        var openingLines=geometry.Lines.Where(l=>l.Role==DoorWindowLineRole.Opening).ToArray();
        Check(openingLines.Length==4&&openingLines[0].X2<100&&openingLines[2].X2>1400,
            "双扇门立面折线尖端未指向外侧合页");
        var explicitType=OpeningConstruction.Copy(generic);explicitType.DivisionPreset="自定义";
        explicitType.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
            new DoorWindowLayoutCell {Right=750,Top=2500,IsDoor=true,Opening="左平开",Material=geometry.Cells[0].Material},
            new DoorWindowLayoutCell {Left=750,Right=1500,Top=2500,IsDoor=true,Opening="右平开",Material=geometry.Cells[1].Material}});
        bool SameLines(List<ViewLine> a,List<ViewLine> b)=>a.Count==b.Count&&a.Zip(b,(x,y)=>
            Math.Abs(x.X1-y.X1)<.001&&Math.Abs(x.Y1-y.Y1)<.001&&Math.Abs(x.X2-y.X2)<.001&&Math.Abs(x.Y2-y.Y2)<.001).All(v=>v);
        foreach(var angle in new[]{15d,30,45,90})foreach(var along in new[]{false,true})foreach(var normal in new[]{false,true}) {
            opening.PlanOpenAngle=angle;opening.PlanFlipAlong=along;opening.PlanFlipNormal=normal;
            Check(SameLines(OpeningPlanGeometry.Build(opening,generic,200),OpeningPlanGeometry.Build(opening,explicitType,200)),
                "通用双扇与显式立面分格的平面不一致");
            var a=OpeningPlanGeometry.DirectionHandle(opening,generic,200);var b=OpeningPlanGeometry.DirectionHandle(opening,explicitType,200);
            Check(Math.Abs(a.X-b.X)<.001&&Math.Abs(a.Y-b.Y)<.001,"双扇夹点未采用立面合页");
            var regions=OpeningPlanGeometry.SelectionRegions(opening,generic,200);
            Check(regions.Count==3&&Math.Abs(regions[1].Last().X-a.X)<.001&&Math.Abs(regions[1].Last().Y-a.Y)<.001,
                "双扇预选区域与开启弧不一致");
            var model=SampleModelFactory.CreateEmptyModel("Paired hinge check");
            model.Walls.Add(new WallModel {Id="host",StoreyId="1F",X2=3000,Thickness=200});opening.HostWallId="host";
            model.Openings.Add(opening);model.OpeningTypes.Add(generic);
            var genericPoints=BuildingVolumeBuilder.BuildOpeningParts(model).Faces.SelectMany(f=>f.Points).ToArray();
            model.OpeningTypes[0]=explicitType;
            var explicitPoints=BuildingVolumeBuilder.BuildOpeningParts(model).Faces.SelectMany(f=>f.Points).ToArray();
            Check(genericPoints.Length==explicitPoints.Length&&genericPoints.Zip(explicitPoints,(x,y)=>
                Math.Abs(x.X-y.X)<.001&&Math.Abs(x.Y-y.Y)<.001&&Math.Abs(x.Z-y.Z)<.001).All(v=>v),
                "通用双扇三维合页未采用同一分格");
        }
        var modesType=OpeningConstruction.Copy(explicitType);
        modesType.CustomCellLayout="0,0,750,2500,,1,0,"+geometry.Cells[0].Material+"|750,0,1500,2500,,1,0,"+geometry.Cells[1].Material;
        modesType.CellOpeningModes="左平开|右平开";modesType.OpeningMode="固定";
        Check(SameLines(OpeningPlanGeometry.Build(opening,modesType,200),OpeningPlanGeometry.Build(opening,explicitType,200)),
            "旧逐格开启参数被整体开启方式覆盖");
        var fixture=SampleModelFactory.CreateEmptyModel("M1525 transom paired leaves");
        fixture.Walls.Add(new WallModel {Id="host",StoreyId="1F",X2=3500,Thickness=200});
        opening.PlanOpenAngle=90;opening.PlanFlipAlong=false;opening.PlanFlipNormal=false;opening.OpenIn3D=false;opening.Offset=1750;
        fixture.Openings.Add(opening);
        explicitType.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
            new DoorWindowLayoutCell {Right=750,Top=2235.77,IsDoor=true,Opening="左平开",Material="玻璃"},
            new DoorWindowLayoutCell {Left=750,Right=1500,Top=2235.77,IsDoor=true,Opening="右平开",Material="玻璃"},
            new DoorWindowLayoutCell {Right=1500,Bottom=2235.77,Top=2500,Opening="固定",Material="玻璃"}});
        fixture.OpeningTypes.Add(explicitType);
        var sectors=OpeningPlanGeometry.SelectionRegions(opening,explicitType,200);
        Check(sectors.Count==3&&sectors[1][0].X<100&&sectors[2][0].X>1400,"显式外合页双扇被改成中间合页或固定亮子成为门扇");
        Directory.CreateDirectory(".artifacts/opening-symbols");
        BuildingModelJson.SaveModel(".artifacts/opening-symbols/m1525-paired-transom.json",fixture);
        Console.WriteLine("PASS paired opening hinges generic/per-cell elevation-plan-3D grip selection angles mirrors fixedTransom");
    }
    private static void CenterChecks(BuildingModelDocument atlas)
    {
        var model=BuildingModelJson.FromJson(BuildingModelJson.ToJson(atlas));var opening=model.Openings[0];
        opening.Offset=1800;opening.PlanFlipAlong=true;opening.PlanFlipNormal=true;opening.PlanOpenAngle=30;opening.OpenIn3D=true;
        opening.PlanLabelAlong=100;opening.PlanLabelNormal=-200;
        var wall=model.Walls[0];wall.X2=wall.X1+4200;wall.Y2=wall.Y1+5600;
        var session=new BuildingModelEditSession(model);var before=BuildingModelJson.ToJson(session.Model);
        Check(session.TryCenterOpening(opening.Id,out var error),"斜墙居中失败 "+error);
        var centered=session.Model.Openings[0];var after=BuildingModelJson.ToJson(session.Model);
        Check(centered.Offset==3500,"居中不是洞口中心对齐墙段中点");
        Check(centered.Code==opening.Code&&centered.PlanOpenAngle==30&&centered.OpenIn3D==true&&centered.PlanFlipAlong&&centered.PlanFlipNormal
            &&centered.PlanLabelAlong==100&&centered.PlanLabelNormal==-200,"居中改变编号、方向、角度或编号位置");
        var revision=session.Revision;
        Check(session.TryCenterOpening(opening.Id,out error)&&session.Revision==revision,"重复居中产生多余历史");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before&&session.Redo()&&BuildingModelJson.ToJson(session.Model)==after,"居中撤销重做不原子");
        model.Openings.Add(new OpeningModel {Id="blocking",HostWallId=wall.Id,Code=opening.Code,Kind="门",Offset=3500,Width=900,Height=2100});
        var blocked=new BuildingModelEditSession(model);var blockedJson=BuildingModelJson.ToJson(blocked.Model);
        Check(!blocked.TryCenterOpening(opening.Id,out error)&&BuildingModelJson.ToJson(blocked.Model)==blockedJson,"居中覆盖其他洞口");
        Check(!blocked.TryCenterOpening("missing",out error)&&BuildingModelJson.ToJson(blocked.Model)==blockedJson,"失效宿主被提交");
        model.Openings.RemoveAll(o=>o.Id=="blocking");wall.X2=wall.X1-4200;wall.Y2=wall.Y1-5600;
        var reverse=new BuildingModelEditSession(model);
        Check(reverse.TryCenterOpening(opening.Id,out error)&&reverse.Model.Openings[0].Offset==3500,"反向墙居中错误");
        Console.WriteLine("PASS opening center oblique reversedWall centerOffset presentationPreserved oneStepUndo redo noOp collisionRollback missingHost");
    }
    private static void AngleAndCodeChecks(BuildingModelDocument atlas)
    {
        var model=BuildingModelJson.FromJson(BuildingModelJson.ToJson(atlas));
        var oldJson=BuildingModelJson.FromJson("{\"Openings\":[{\"Code\":\"M0921\",\"Kind\":\"门\",\"Width\":900,\"Height\":2100}]}");
        Check(oldJson.Openings[0].PlanOpenAngle==90&&oldJson.Openings[0].OpenIn3D==null,"旧 JSON 的默认角度或兼容标志错误");
        var opening=model.Openings[0];var type=model.OpeningTypes[0];
        foreach(var angle in new[]{0d,15,30,45,90,37.5,120,180})foreach(var flip in new[]{false,true}) {
            opening.PlanOpenAngle=angle;opening.PlanFlipAlong=flip;opening.PlanFlipNormal=flip;
            var handle=OpeningPlanGeometry.DirectionHandle(opening,type,200);var a=angle*Math.PI/180;
            var x=52+796*Math.Cos(a);var y=77+796*Math.Sin(a);
            Check(Math.Abs(handle.X-(flip?900-x:x))<.00001&&Math.Abs(handle.Y-(flip?-y:y))<.00001,"开启角度夹点错误 "+angle);
            var lines=OpeningPlanGeometry.Build(opening,type,200);
            Check(lines.All(l=>new[]{l.X1,l.Y1,l.X2,l.Y2}.All(Finite)),"开启角度非有限坐标");
            var regions=OpeningPlanGeometry.SelectionRegions(opening,type,200);
            Check(Math.Abs(regions[1].Last().X-handle.X)<.00001&&Math.Abs(regions[1].Last().Y-handle.Y)<.00001,"预选扇形未跟随开启角度");
            if(angle>0)Check(Math.Abs(lines[lines.Count-OpeningPlanGeometry.SwingArcSegments].X1-handle.X)<.00001&&Math.Abs(lines[lines.Count-OpeningPlanGeometry.SwingArcSegments].Y1-handle.Y)<.00001,"开启弧端点不在门扇夹点");
        }
        opening.PlanFlipAlong=false;opening.PlanFlipNormal=false;opening.PlanOpenAngle=30;
        var closed=BuildingVolumeBuilder.BuildOpeningParts(model).Faces.Where(f=>f.ElementId==opening.Id&&f.Kind=="door").SelectMany(f=>f.Points).ToList();
        Check(closed.Count>0&&closed.Max(p=>p.Y)-closed.Min(p=>p.Y)<100,"三维默认不应开启");
        opening.OpenIn3D=true;
        var opened=BuildingVolumeBuilder.BuildOpeningParts(model).Faces.Where(f=>f.ElementId==opening.Id&&f.Kind=="door").SelectMany(f=>f.Points).ToList();
        Check(opened.Max(p=>p.Y)-opened.Min(p=>p.Y)>300,"三维开启开关没有旋转门扇");
        type.OpenAngle=45;opening.OpenIn3D=false;
        var explicitClosed=BuildingVolumeBuilder.BuildOpeningParts(model).Faces.Where(f=>f.ElementId==opening.Id&&f.Kind=="door").SelectMany(f=>f.Points).ToList();
        Check(explicitClosed.Max(p=>p.Y)-explicitClosed.Min(p=>p.Y)<100,"三维关闭开关未覆盖旧类型角度");
        opening.OpenIn3D=null;
        var legacy=BuildingVolumeBuilder.BuildOpeningParts(model).Faces.Where(f=>f.ElementId==opening.Id&&f.Kind=="door").SelectMany(f=>f.Points).ToList();
        Check(legacy.Max(p=>p.Y)-legacy.Min(p=>p.Y)>300,"旧模型显式三维角度丢失");
        var sibling=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model)).Openings[0];sibling.Id="sibling";sibling.Offset=5200;model.Openings.Add(sibling);
        var session=new BuildingModelEditSession(model);var before=BuildingModelJson.ToJson(session.Model);
        var newSession=new BuildingModelEditSession(model);
        Check(newSession.TryAddOpening(new OpeningModel {HostWallId=opening.HostWallId,Code=opening.Code,Kind=opening.Kind,Width=900,Height=2100,Offset=1100},out var newId,out var addError),"新增默认闭合测试失败 "+addError);
        Check(newSession.Model.Openings.Single(o=>o.Id==newId).OpenIn3D==false,"新增门错误继承旧类型打开角度");
        Check(session.TrySetOpeningPresentation(opening.Id,"自定义-M0921B",37.5,true,out var error),"修改编号与开启参数失败 "+error);
        var changed=session.Model.Openings[0];var json=BuildingModelJson.ToJson(session.Model);
        Check(changed.CodeManuallyEdited&&changed.PlanOpenAngle==37.5&&changed.OpenIn3D==true,"实例参数丢失");
        Check(session.Model.Openings.Last().Code==opening.Code&&session.Model.Openings.Last().PlanOpenAngle==30,"影响同编号实例");
        Check(OpeningConstruction.Resolve(session.Model,changed).CustomCellLayout==type.CustomCellLayout,"改编号丢失立面分格");
        Check(OpeningConstruction.Resolve(session.Model,sibling).Code==type.Code,"旧编号立面丢失");
        foreach(var invalid in new[]{double.NaN,double.PositiveInfinity,-1,181})
            Check(!session.TrySetOpeningPresentation(opening.Id,"X",invalid,true,out _)&&BuildingModelJson.ToJson(session.Model)==json,"非法角度污染历史");
        Check(!session.TrySetOpeningPresentation(opening.Id,"  ",45,true,out _)&&BuildingModelJson.ToJson(session.Model)==json,"空编号被提交");
        Check(!session.TrySetOpeningPresentation(opening.Id,"M1825",45,true,out _)&&BuildingModelJson.ToJson(session.Model)==json,"不同尺寸同编号混用");
        var saved=BuildingModelJson.FromJson(json).Openings[0];
        Check(saved.Code==changed.Code&&saved.PlanOpenAngle==37.5&&saved.OpenIn3D==true,"保存重开丢失参数");
        var copySession=new BuildingModelEditSession(session.Model);
        Check(copySession.TryTransformWall(model.Walls[0].Id,0,20000,0,true,out var copyId,out error),"复制开启角度失败 "+error);
        Check(copySession.Model.Openings.Where(o=>o.HostWallId==copyId).Any(o=>o.PlanOpenAngle==37.5&&o.OpenIn3D==true&&o.CodeManuallyEdited),"复制丢失开启设置");
        var standard=BuildingModelJson.FromJson(json);standard.Storeys.Add(new StoreyModel {Id="2F",TemplateStoreyId="1F",Elevation=4000,Height=4000});
        Check(StandardStoreyLayout.Materialize(standard).Openings.Any(o=>o.Id!=opening.Id&&o.Code==changed.Code&&o.PlanOpenAngle==37.5&&o.OpenIn3D==true),"标准层丢失开启设置");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before&&session.Redo()&&BuildingModelJson.ToJson(session.Model)==json,"编号与角度不能整笔撤销重做");
        var overrides=BuildingModelJson.FromJson(json);var overrideType=OpeningConstruction.Copy(type);overrideType.Code="instance-type";overrideType.DoorFrameWidth=88;
        overrides.OpeningTypes.Add(overrideType);overrides.OpeningOverrides.Add(new OpeningInstanceOverride {OpeningId=opening.Id,TypeCode=overrideType.Code});
        var overrideSession=new BuildingModelEditSession(overrides);
        Check(overrideSession.TrySetOpeningPresentation(opening.Id,"M-保留做法",30,false,out error)&&OpeningConstruction.Resolve(overrideSession.Model,overrideSession.Model.Openings[0]).DoorFrameWidth==88,"改编号没有保留实例立面覆盖");
        var windowSession=new BuildingModelEditSession(Atlas(false));var window=windowSession.Model.Openings[0];
        Check(windowSession.TrySetOpeningPresentation(window.Id,"C-新编号",90,false,out error),"固定窗无法改编号 "+error);
        var windowJson=BuildingModelJson.ToJson(windowSession.Model);
        Check(!windowSession.TrySetOpeningPresentation(window.Id,"C-新编号",30,true,out _)&&BuildingModelJson.ToJson(windowSession.Model)==windowJson,"固定窗误套平开角度");
        Console.WriteLine("PASS opening angles 0/15/30/45/90/custom/180 mirror arc grip sector 3Dclosed/open legacy rename instanceIsolation copy standardFloor persistence undo invalidRollback");
    }
    private static void LabelAndClearanceChecks(BuildingModelDocument atlas)
    {
        var model=BuildingModelJson.FromJson(BuildingModelJson.ToJson(atlas));var opening=model.Openings[0];var wall=model.Walls[0];
        var label=OrthographicProjector.CreatePlanOpeningLabel(wall,opening,250,.7);
        Check(Math.Abs(label.Y-wall.Y1-130)<.001,"编号未贴近洞口外沿");
        foreach(var sign in new[]{1d,-1d}) {
            wall.X2=wall.X1;wall.Y2=wall.Y1+7000*sign;
            label=OrthographicProjector.CreatePlanOpeningLabel(wall,opening,250,.7);
            Check(Math.Abs(Math.Abs(label.X-wall.X1)-130)<.001,"反向/竖墙编号间距错误");
        }
        wall.X2=wall.X1+7000;wall.Y2=wall.Y1;
        var session=new BuildingModelEditSession(model);var before=BuildingModelJson.ToJson(session.Model);
        Check(session.TrySetOpeningLabel(opening.Id,125,-550,out var error),"编号位移不能保存 "+error);
        var adjusted=session.Model.Openings[0];label=OrthographicProjector.CreatePlanOpeningLabel(wall,adjusted,250,.7);
        Check(Math.Abs(label.Y-wall.Y1+420)<.001,"编号法向位移未参与输出");
        Check(adjusted.Offset==opening.Offset&&adjusted.PlanFlipAlong==opening.PlanFlipAlong,"编号位移修改洞口");
        var json=BuildingModelJson.ToJson(session.Model);
        Check(BuildingModelJson.FromJson(json).Openings[0].PlanLabelAlong==125,"编号位移序列化丢失");
        Check(!session.TrySetOpeningLabel(opening.Id,double.NaN,0,out _)&&BuildingModelJson.ToJson(session.Model)==json,"非法编号坐标污染历史");
        var copySession=new BuildingModelEditSession(session.Model);
        Check(copySession.TryTransformWall(wall.Id,0,10000,0,true,out var copiedWall,out error),"复制编号测试失败 "+error);
        Check(copySession.Model.Openings.Single(o=>o.HostWallId==copiedWall).PlanLabelNormal==-550,"复制墙丢失编号位置");
        var standard=BuildingModelJson.FromJson(json);standard.Storeys.Add(new StoreyModel {Id="2F",TemplateStoreyId="1F",Elevation=4000,Height=4000});
        Check(StandardStoreyLayout.Materialize(standard).Openings.Any(o=>o.Id!=opening.Id&&o.Code==opening.Code&&o.PlanLabelAlong==125),"标准层丢失编号位置");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before,"编号不能一次撤销");
        Check(OpeningPlanGeometry.WallEndClearance(7000,opening,out var first)==3050&&first,"墙端净距错误地使用中心距离");
        Check(OpeningPlanGeometry.OffsetFromWallEnd(7000,900,300,false)==6250,"终点墙端净距换算错误");
        var projected=OrthographicProjector.Project(BuildingModelJson.FromJson(json),new ViewDefinitionModel {Kind=ViewKind.Plan,StoreyIds=new List<string>{"1F"},Scale=100,
            Annotations=new DrawingAnnotationSettings {TextHeight=3,WidthFactor=.8,AxisDiameter=8}});
        Check(projected.Texts.Any(t=>t.Layer==ViewLayers.Opening&&t.Text==opening.Code&&t.Height==300&&t.WidthFactor==.8),"图纸编号没有采用当前文字设置");
        Console.WriteLine("PASS opening labels closeEdge reversedWall persistence copy standardFloor undo outputSettings wallEndClearance");
    }
    private static BuildingModelDocument Atlas(bool door)
    {
        var model=new BuildingModelDocument {Name=door?"门平面图例校对":"窗平面图例校对",Storeys=new(){new StoreyModel {Id="1F",Name="一层",Height=4000}},
            Annotations=new DrawingAnnotationSettings {TextHeight=2.5,WidthFactor=.7}};
        if(door) {
            Add("M0921",900,2100,"普通门",new[]{900d},new[]{"左平开"});
            Add("M1825",1800,2500,"普通门",new[]{900d,900},new[]{"左平开","右平开"});
            Add("M1221",1200,2100,"普通门",new[]{300d,900},new[]{"左平开","右平开"});
            Add("TLM0921",900,2100,"推拉门",new[]{900d},new[]{"右推拉"});
            Add("TLM1525",1500,2500,"推拉门",new[]{750d,750},new[]{"左推拉","右推拉"});
            Add("TLM3025",3000,2500,"推拉门",new[]{750d,750,750,750},new[]{"左推拉","右推拉","左推拉","右推拉"});
            Add("M2424",2400,2400,"普通门",new[]{600d,600,600,600},new[]{"固定","固定","固定","固定"},"折叠门");
            Add("M3024",3000,2400,"普通门",new[]{3000d},new[]{"固定"},"卷帘门");
            Add("M1825A",1800,2500,"普通门",new[]{1800d},new[]{"固定"},"旋转门");
            Add("MLC2424",2400,2400,"门联窗",new[]{900d,1500},new[]{"左平开","固定"});
            Add("FM0921",900,2100,"防火门",new[]{900d},new[]{"左平开"});
            Add("RFM0921",900,2100,"人防门",new[]{900d},new[]{"左平开"});
        } else {
            Add("C1218",1200,1800,"普通窗",new[]{1200d},new[]{"固定"});
            Add("C0915",900,1500,"普通窗",new[]{900d},new[]{"左平开"});
            Add("C1518",1500,1800,"普通窗",new[]{750d,750},new[]{"左推拉","右推拉"});
            Add("C3018",3000,1800,"普通窗",new[]{750d,750,750,750},new[]{"左推拉","右推拉","左推拉","右推拉"});
            Add("C0606",600,600,"普通窗",new[]{600d},new[]{"上悬"});
            Add("BYC0610",600,1000,"百叶窗",new[]{600d},new[]{"百叶"});
            Add("ZJC1818",1800,1800,"转角窗",new[]{1800d},new[]{"固定"});
            Add("TC1818",1800,1800,"凸窗",new[]{1800d},new[]{"固定"},"矩形凸窗");
            Add("TC2418",2400,1800,"凸窗",new[]{2400d},new[]{"固定"},"梯形凸窗");
            Add("DXC6018",6000,1800,"带形窗",new[]{1500d,1500,1500,1500},new[]{"固定","固定","固定","固定"});
            Add("GC1210",1200,1000,"高窗",new[]{1200d},new[]{"固定"});
            Add("GXC1218",1200,1800,"拱形窗",new[]{1200d},new[]{"固定"});
        }
        return model;
        void Add(string code,double width,double height,string kind,double[] widths,string[] modes,string style="按立面") {
            var index=model.Openings.Count;var x=index%4*7500d;var y=index/4*4000d;var id="w"+index;
            model.Walls.Add(new WallModel {Id=id,StoreyId="1F",X1=x,Y1=y,X2=x+7000,Y2=y,Thickness=200});
            var opening=new OpeningModel {Id="o"+index,Code=code,Kind=kind,HostWallId=id,Offset=3500,Width=width,Height=height,Sill=door?0:kind=="高窗"?2000:900};
            var type=new OpeningTypeModel {Code=code,Kind=kind,ElevationType=kind,Width=width,Height=height,PlanStyle=style,PlanReturnInset=style=="梯形凸窗"?400:0,
                DivisionPreset="自定义",HasInstallationGap=false,HasOuterFrame=true,OuterFrameWidth=50,HasMullion=true,MullionWidth=50,FrameDepth=100,SashWidth=40,
                BayLeftDepth=600,BayRightDepth=600,BayLeftSide="窗",BayRightSide="窗"};
            var left=0d;var cells=new List<DoorWindowLayoutCell>();
            for(var i=0;i<widths.Length;i++){cells.Add(new DoorWindowLayoutCell {Left=left,Right=left+widths[i],Bottom=0,Top=height,
                IsDoor=door&&(kind!="门联窗"||i==0),Material=door&&(kind!="门联窗"||i==0)?"实板":"玻璃",Opening=modes[i]});left+=widths[i];}
            type.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(cells);model.Openings.Add(opening);model.OpeningTypes.Add(type);
        }
    }
    private static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n);
    private static void FrameClearanceChecks(params BuildingModelDocument[] atlases)
    {
        foreach(var atlas in atlases)foreach(var source in atlas.Openings) {
            var originalType=atlas.OpeningTypes.Single(t=>t.Code==source.Code);
            if(originalType.PlanStyle!="按立面"||!OpeningPlanGeometry.CutsWall(source))continue;
            foreach(var position in new[]{"居中","靠内","靠外"})foreach(var angle in new[]{0d,15,30,45,90,120,180})
            foreach(var flipAlong in new[]{false,true})foreach(var flipNormal in new[]{false,true}) {
                var type=OpeningConstruction.Copy(originalType);type.InstallationPosition=position;type.InstallationOffset=17;
                var model=BuildingModelJson.FromJson(BuildingModelJson.ToJson(atlas));
                var opening=model.Openings.Single(o=>o.Id==source.Id);
                opening.PlanOpenAngle=angle;opening.PlanFlipAlong=flipAlong;opening.PlanFlipNormal=flipNormal;
                var before=BuildingModelJson.ToJson(model);var construction=OpeningConstruction.Build(opening,type,200);
                string PartKey(OpeningPart p)=>$"{p.Left:R}|{p.Right:R}|{p.Bottom:R}|{p.Top:R}|{p.NormalOffset:R}|{p.Depth:R}|{p.Kind}";
                var geometryBefore=construction.Select(PartKey).ToArray();
                var section=Math.Max(0,Math.Min(opening.Height-.01,1200-opening.Sill));
                var frames=construction.Where(p=>p.Face==0&&p.Kind=="frame"&&p.Bottom<=section&&p.Top>section).ToArray();
                var lines=OpeningPlanGeometry.Build(opening,type,200).Skip(frames.Length*4).ToArray();
                foreach(var line in lines)foreach(var frame in frames) {
                    // Undo instance mirroring to compare with construction coordinates.
                    for(var step=0;step<=40;step++) {
                        var x=line.X1+(line.X2-line.X1)*step/40;var y=line.Y1+(line.Y2-line.Y1)*step/40;
                        if(flipAlong)x=opening.Width-x;if(flipNormal)y=-y;
                        Check(!(x>frame.Left+.001&&x<frame.Right-.001
                            &&y>frame.NormalOffset-frame.Depth/2+.001&&y<frame.NormalOffset+frame.Depth/2-.001),
                            "门窗扇线穿过固定框 "+opening.Code+" "+position+" "+angle);
                    }
                }
                OpeningPlanGeometry.DirectionHandle(opening,type,200);OpeningPlanGeometry.SelectionRegions(opening,type,200);
                Check(BuildingModelJson.ToJson(model)==before,"平面框扇分离改动了模型数据");
                Check(OpeningConstruction.Build(opening,type,200).Select(PartKey).SequenceEqual(geometryBefore),"平面调整改动了三维构造");
            }
        }
        // With no fixed meeting post, two tracks retain their intentional leaf-to-leaf lap exactly once.
        var sliding=atlases[0].Openings.Single(o=>o.Code=="TLM1525");
        var slidingType=OpeningConstruction.Copy(atlases[0].OpeningTypes.Single(t=>t.Code==sliding.Code));slidingType.HasMullion=false;
        var parts=OpeningConstruction.Build(sliding,slidingType,200).Where(p=>p.Face==0&&p.Bottom<=1200&&p.Top>1200).ToArray();
        var slidingLines=OpeningPlanGeometry.Build(sliding,slidingType,200).Skip(parts.Count(p=>p.Kind=="frame")*4).ToArray();
        var cells=parts.Where(p=>p.Cell!=null).GroupBy(p=>p.Cell.Left).OrderBy(g=>g.Key).ToArray();
        Check(slidingLines[0].X1==cells[0].Min(p=>p.Left)&&slidingLines[0].X2==cells[0].Max(p=>p.Right)
            &&slidingLines[8].X1==cells[1].Min(p=>p.Left)&&slidingLines[8].X2==cells[1].Max(p=>p.Right),"无中梃推拉扇重复计算搭接");
        Check(slidingLines[0].X2>slidingLines[8].X1,"推拉扇之间的合法搭接被删除");
        var transomType=OpeningConstruction.Copy(atlases[0].OpeningTypes[0]);
        transomType.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
            new DoorWindowLayoutCell {Right=900,Top=1200,IsDoor=true,Opening="左平开",Material="实板"},
            new DoorWindowLayoutCell {Right=900,Bottom=1200,Top=2100,IsDoor=true,Opening="左平开",Material="实板"}});
        var transom=atlases[0].Openings[0];
        Check(OpeningPlanGeometry.DirectionHandle(transom,transomType,200)==null
            &&OpeningPlanGeometry.SelectionRegions(transom,transomType,200).Count==1,"剖切穿横框时产生负半径方向点或预选扇形");
        Console.WriteLine("PASS opening plan clearFrames netLeafWidth swingAngles independentMirrors threeInstallationPositions slidingLapOnce constructionUnchanged");
    }
    private static void ThresholdChecks(BuildingModelDocument doors,BuildingModelDocument windows)
    {
        var model=BuildingModelJson.FromJson(BuildingModelJson.ToJson(doors));var opening=model.Openings[0];var type=model.OpeningTypes[0];
        var plain=OpeningPlanGeometry.Build(opening,type,200);opening.ThresholdHeight=80;
        var lines=OpeningPlanGeometry.Build(opening,type,200);
        Check(lines.Count==plain.Count+2&&lines.Take(2).All(l=>l.X1==0&&l.X2==900&&Math.Abs(l.Y1)==100&&l.Y1==l.Y2),"门槛不是对齐墙边的两根线");
        var parts=OpeningConstruction.Build(opening,type,200).Where(p=>p.Kind=="threshold").ToArray();
        Check(parts.Length==1&&parts[0].Depth==200&&parts[0].Left==0&&parts[0].Right==900&&parts[0].Bottom==0&&parts[0].Top==80,"门槛三维尺寸错误");
        opening.PlanFlipAlong=true;opening.PlanFlipNormal=true;
        var mirrored=OpeningPlanGeometry.Build(opening,type,200);
        Check(mirrored[0].X1==900&&mirrored[0].X2==0&&mirrored[0].Y1==100,"门槛翻转错误");
        var combined=model.Openings.Single(o=>o.Code=="MLC2424");combined.ThresholdHeight=50;
        var spans=OpeningConstruction.ThresholdSpans(combined,OpeningConstruction.Resolve(model,combined));
        Check(spans.Count==1&&spans[0][0]==0&&spans[0][1]==900,"门联窗的门槛延伸到窗区");
        var legacySliding=new OpeningModel {Code="TLM1525",Kind="门",Width=1500,Height=2500,ThresholdHeight=50};
        var legacySpans=OpeningConstruction.ThresholdSpans(legacySliding,OpeningConstruction.Default(legacySliding));
        Check(legacySpans.Count==1&&legacySpans[0][0]==0&&legacySpans[0][1]==1500,"旧推拉门的窗式分格丢失门槛");
        var session=new BuildingModelEditSession(model);var before=BuildingModelJson.ToJson(session.Model);
        Check(session.TrySetOpeningGeometry(opening.Id,opening.Offset,900,2100,0,120,out var error),"属性门槛修改失败 "+error);
        var changed=BuildingModelJson.ToJson(session.Model);
        Check(session.Model.Openings[0].ThresholdHeight==120&&session.Model.Openings[1].ThresholdHeight==0,"门槛修改影响同型号其他实例");
        foreach(var invalid in new[]{double.NaN,double.PositiveInfinity,-1,2100,3000})
            Check(!session.TrySetOpeningGeometry(opening.Id,opening.Offset,900,2100,0,invalid,out _)&&BuildingModelJson.ToJson(session.Model)==changed,"非法门槛修改污染历史");
        Check(!session.TrySetOpeningGeometry(opening.Id,opening.Offset,900,100,0,out _)&&BuildingModelJson.ToJson(session.Model)==changed,"洞口缩短后门槛超高未拦截");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before&&session.Redo()&&BuildingModelJson.ToJson(session.Model)==changed,"门槛不能原子撤销重做");
        Check(BuildingModelJson.FromJson(changed).Openings[0].ThresholdHeight==120,"门槛保存重开丢失");
        var old=BuildingModelJson.FromJson("{\"Openings\":[{\"Kind\":\"门\",\"Width\":900,\"Height\":2100}]}");
        Check(old.Openings[0].ThresholdHeight==0,"旧模型不是默认无门槛");
        Check(session.TryTransformWall(opening.HostWallId,0,20000,0,true,out var copiedWall,out error)&&session.Model.Openings.Single(o=>o.HostWallId==copiedWall).ThresholdHeight==120,"复制墙丢失门槛");
        var standard=BuildingModelJson.FromJson(changed);standard.Storeys.Add(new StoreyModel {Id="2F",TemplateStoreyId="1F",Elevation=4000,Height=4000});
        var expanded=StandardStoreyLayout.Materialize(standard);var inherited=expanded.Openings.Single(o=>o.Id==opening.Id+"@STD@2F");
        Check(inherited.ThresholdHeight==120,"标准层丢失门槛");
        var faces=BuildingVolumeBuilder.BuildOpeningParts(standard).Faces.Where(f=>f.ElementId==inherited.Id&&f.Kind=="threshold").ToArray();
        Check(faces.Length==6&&faces.SelectMany(f=>f.Points).Min(p=>p.Z)==4000&&faces.SelectMany(f=>f.Points).Max(p=>p.Z)==4120,"标准层门槛三维标高或闭合实体错误");
        var allParts=BuildingVolumeBuilder.BuildOpeningParts(standard);
        var localParts=BuildingVolumeBuilder.BuildOpeningParts(standard,opening.Id);
        var expected=allParts.Faces.Where(f=>StandardStoreyLayout.SourceElementId(standard,f.ElementId)==opening.Id).ToArray();
        string FaceKey(VolumeFace f)=>f.ElementId+"|"+f.Kind+"|"+f.StoreyId+"|"+string.Join(";",f.Points.Select(p=>$"{p.X:R},{p.Y:R},{p.Z:R}"));
        Check(localParts.Faces.Count==expected.Length&&localParts.Faces.Select(FaceKey).SequenceEqual(expected.Select(FaceKey)),"门槛局部重建漏掉标准层或影响其他门窗");
        Check(BuildingVolumeBuilder.BuildOpeningParts(standard,"missing").Faces.Count==0,"未找到门窗却重建了全模型");
        var wndSession=new BuildingModelEditSession(windows);var wnd=wndSession.Model.Openings[0];
        Check(!wndSession.TrySetOpeningGeometry(wnd.Id,wnd.Offset,wnd.Width,wnd.Height,wnd.Sill,50,out _),"窗接受了门槛");
        var template=OpeningConstruction.Copy(type);template.ThresholdHeight=60;template.PlanOpenAngle=30;template.DefaultOpenIn3D=true;
        Check(session.TrySaveOpeningTemplate(template.Code,template,out error),"模板保存门槛失败 "+error);
        var savedType=BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model)).OpeningTemplates.Single(t=>t.Code==template.Code);
        var inserted=PlanEditing.CreateOpening("门",opening.HostWallId,1000);PlanEditing.ApplyType(inserted,savedType);
        Check(inserted.ThresholdHeight==60&&inserted.PlanOpenAngle==30&&inserted.OpenIn3D==true,"模板参数没有应用到插入实例");
        Check(OpeningConstruction.SameConstruction(type,template),"实例默认参数误判为类型做法冲突");
        template.OuterFrameWidth+=5;Check(!OpeningConstruction.SameConstruction(type,template),"真正框料变化误判为相同做法");
        Console.WriteLine("PASS opening threshold twoWallEdges doorZoneOnly closed3D standardFloor instanceIsolation copy persistence undo invalidRollback templateDefaults");
    }
    private static void PlacementTypeChecks(params BuildingModelDocument[] atlases)
    {
        foreach(var atlas in atlases)foreach(var sourceType in atlas.OpeningTypes) {
            var type=OpeningConstruction.Copy(sourceType);type.Sill=atlas.Openings.Single(o=>o.Code==type.Code).Sill;
            var model=new BuildingModelDocument {Storeys=new(){new StoreyModel {Id="1F",Height=6000}},
                Walls=new(){new WallModel {Id="host",StoreyId="1F",X2=10000,Thickness=200}}};
            var session=new BuildingModelEditSession(model);var before=BuildingModelJson.ToJson(session.Model);
            var opening=PlanEditing.CreateOpening(type.Kind,"host",5000);PlanEditing.ApplyType(opening,type);
            Check(session.TryAddOpening(opening,type,out var id,out var error),"选型放置失败 "+type.Code+" "+error);
            var json=BuildingModelJson.ToJson(session.Model);
            Check(session.Model.Openings.Single().Code==type.Code&&session.Model.Openings.Single().OpenIn3D==false,"选型编号/三维默认丢失");
            Check(OpeningConstruction.Resolve(session.Model,session.Model.Openings.Single()).CustomCellLayout==type.CustomCellLayout,"选型分格丢失");
            Check(!session.TryAddOpening(opening,type,out _,out _)&&BuildingModelJson.ToJson(session.Model)==json,"重叠放置污染类型或历史");
            Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before,"类型和实例不是一次撤销");
            Check(session.Redo()&&BuildingModelJson.ToJson(session.Model)==json,"选型放置不能重做");
            var conflict=OpeningConstruction.Copy(type);conflict.OuterFrameWidth+=1;
            opening.Offset=750;
            Check(!session.TryAddOpening(opening,conflict,out _,out _)&&BuildingModelJson.ToJson(session.Model)==json,"同编号做法冲突覆盖了项目");
            Check(BuildingModelJson.FromJson(json).OpeningTypes.Single().CustomCellLayout==type.CustomCellLayout,"选型保存丢失做法");
        }
        Console.WriteLine("PASS opening placement 24 types code dimensions construction atomicUndo redo save collisionRollback existingTypeProtection");
    }
    private static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
}
