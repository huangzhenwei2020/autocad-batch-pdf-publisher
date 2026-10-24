using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BatchPdfPublisher.BuildingModel
{
    public static class CadFloorModelGeneration
    {
        public static BuildingModelDocument Build(BuildingModelDocument current, CadFloorPlanRegistry registry, string requestId = null,
            Action<CadGenerationProgress> progress = null)
        {
            progress?.Invoke(new CadGenerationProgress {Stage="准备登记数据"});
            try { return registry?.ReplaceRegisteredConstruction==true
                    ? BuildReplacement(current,registry,requestId,progress) : BuildVerified(current,registry,requestId,progress); }
            catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException || ex is InvalidOperationException)
            {
                // Recognition or merge failure must not prevent opening a safe, editable model.
                var retained=BuildingModelJson.FromJson(BuildingModelJson.ToJson(current));
                retained.CadImport=retained.CadImport ?? new CadModelImportState();
                retained.CadImport.RequestId=requestId;
                retained.CadImport.StructureMessages=new List<string> {
                    "本次自动更新已跳过，已有模型及 CAD 参照底图保留，可继续建模。原因："+ex.Message };
                return retained;
            }
        }
        private static BuildingModelDocument BuildReplacement(BuildingModelDocument current,CadFloorPlanRegistry registry,string requestId,Action<CadGenerationProgress> progress)
        {
            var prepared=BuildingModelJson.FromJson(BuildingModelJson.ToJson(current));
            registry=registry.Clone();registry.ReconcileStoreys(prepared);
            var floors=new HashSet<string>(registry.Floors.Select(c=>c.Floor.Storey.Id),StringComparer.OrdinalIgnoreCase);
            var wallIds=new HashSet<string>(prepared.Walls.Where(w=>floors.Contains(w.StoreyId)).Select(w=>w.Id),StringComparer.OrdinalIgnoreCase);
            var oldOpenings=OpeningConstruction.ApplyOverrides(current).Openings.Where(o=>wallIds.Contains(o.HostWallId)).ToList();
            var oldIds=new HashSet<string>(oldOpenings.Select(o=>o.Id),StringComparer.OrdinalIgnoreCase);
            var previous=prepared.CadImport ?? new CadModelImportState();
            var baselineWallIds=new HashSet<string>(previous.Walls.Where(w=>floors.Contains(w.StoreyId)).Select(w=>w.Id),StringComparer.OrdinalIgnoreCase);
            oldIds.UnionWith(previous.Openings.Where(o=>baselineWallIds.Contains(o.HostWallId)).Select(o=>o.Id));
            oldIds.UnionWith((previous.ResolvedOpenings ?? new List<CadResolvedOpening>()).Where(r=>floors.Contains(r.StoreyId)).Select(r=>r.OpeningId));
            prepared.Openings.RemoveAll(o=>wallIds.Contains(o.HostWallId) || oldIds.Contains(o.Id));
            prepared.Walls.RemoveAll(w=>floors.Contains(w.StoreyId));
            prepared.Slabs.RemoveAll(s=>floors.Contains(s.StoreyId));
            prepared.Columns.RemoveAll(c=>floors.Contains(c.StoreyId));
            prepared.Beams.RemoveAll(b=>floors.Contains(b.StoreyId));
            prepared.Stairs.RemoveAll(s=>floors.Contains(s.StoreyId));
            prepared.Roofs.RemoveAll(r=>floors.Contains(r.StoreyId));
            prepared.Rooms.RemoveAll(r=>floors.Contains(r.StoreyId));
            prepared.OpeningOverrides=prepared.OpeningOverrides ?? new List<OpeningInstanceOverride>();
            prepared.OpeningOverrides.RemoveAll(o=>oldIds.Contains(o.OpeningId));
            // Build against an empty import baseline, then merge only unaffected floor metadata.
            // The outer transaction returns the original model if the fresh build is unsafe.
            prepared.CadImport=new CadModelImportState();
            var result=BuildVerified(prepared,registry,requestId,progress);
            progress?.Invoke(new CadGenerationProgress {Stage="保留匹配的门窗做法"});
            var fresh=result.CadImport;
            var retained=0;
            foreach(var opening in fresh.Openings)
            {
                var target=result.Openings.First(o=>o.Id==opening.Id);
                var floor=result.Walls.First(w=>w.Id==target.HostWallId).StoreyId;
                var candidates=oldOpenings.Where(o=>current.Walls.Any(w=>w.Id==o.HostWallId && string.Equals(w.StoreyId,floor,StringComparison.OrdinalIgnoreCase))
                    && SameOpeningSpecification(o,target)).ToList();
                var old=candidates.FirstOrDefault(o=>o.Id==target.Id);
                if(old==null)
                {
                    var source=registry.Floors.First(c=>c.Floor.Storey.Id==floor).Openings.FirstOrDefault(o=>
                        Identity("opening",registry.Floors.First(c=>c.Floor.Storey.Id==floor).Probe.DrawingFingerprint,floor,o.SourceHandle)==target.Id);
                    var mapped=(previous.ResolvedOpenings ?? new List<CadResolvedOpening>()).FirstOrDefault(r=>r.StoreyId==floor && r.SourceHandle==source?.SourceHandle);
                    old=candidates.FirstOrDefault(o=>o.Id==mapped?.OpeningId);
                }
                if(old==null && candidates.Count==1)old=candidates[0];
                OpeningTypeModel type=null;
                if(old!=null)
                {
                    type=OpeningConstruction.Copy(OpeningConstruction.Resolve(current,old));
                    target.PlanOpenAngle=old.PlanOpenAngle;target.OpenIn3D=old.OpenIn3D;
                    target.ThresholdHeight=old.ThresholdHeight;
                    target.CodeManuallyEdited=old.CodeManuallyEdited;
                    var oldWall=current.Walls.First(w=>w.Id==old.HostWallId);
                    var newWall=result.Walls.First(w=>w.Id==target.HostWallId);
                    var reversed=(oldWall.X2-oldWall.X1)*(newWall.X2-newWall.X1)+(oldWall.Y2-oldWall.Y1)*(newWall.Y2-newWall.Y1)<0;
                    target.PlanFlipAlong=old.PlanFlipAlong ^ reversed;target.PlanFlipNormal=old.PlanFlipNormal ^ reversed;
                    retained++;
                }
                else if(candidates.Count>1)
                {
                    var types=candidates.Select(o=>OpeningConstruction.Resolve(current,o)).ToList();
                    if(types.All(t=>OpeningConstruction.SameConstruction(types[0],t)))
                    {type=OpeningConstruction.Copy(types[0]);retained++;}
                }
                // A same-code definition of a different size must not leak into this new hole,
                // nor may resetting it alter an unregistered floor that still uses the type.
                var preserving=type!=null;
                type=type ?? OpeningConstruction.Default(target);
                type.Code=target.Code;type.Kind=target.Kind;OpeningConstruction.ResizeType(type,target.Width,target.Height);
                var validation=OpeningConstruction.Validate(target,type);
                if(validation!=null)
                {
                    if(preserving)retained--;
                    target.ThresholdHeight=0;target.OpenIn3D=false;target.PlanOpenAngle=90;
                    type=OpeningConstruction.Default(target);
                    fresh.StructureMessages.Add(target.Code+" 原门窗做法无法用于当前 CAD 洞口，已恢复默认做法："+validation);
                }
                var shared=OpeningConstruction.Copy(OpeningConstruction.Resolve(result,target));
                OpeningConstruction.ResizeType(shared,target.Width,target.Height);
                if(!OpeningConstruction.SameConstruction(type,shared))
                    result.OpeningOverrides.Add(new OpeningInstanceOverride {OpeningId=target.Id,Definition=type});
            }
            fresh.Walls=previous.Walls.Where(w=>!floors.Contains(w.StoreyId)).Concat(fresh.Walls).ToList();
            fresh.Openings=previous.Openings.Where(o=>!baselineWallIds.Contains(o.HostWallId) && !oldIds.Contains(o.Id)).Concat(fresh.Openings).ToList();
            fresh.Slabs=(previous.Slabs ?? new List<SlabModel>()).Where(s=>!floors.Contains(s.StoreyId)).Concat(fresh.Slabs).ToList();
            fresh.Columns=(previous.Columns ?? new List<ColumnModel>()).Where(c=>!floors.Contains(c.StoreyId)).Concat(fresh.Columns).ToList();
            fresh.Beams=(previous.Beams ?? new List<BeamModel>()).Where(b=>!floors.Contains(b.StoreyId)).Concat(fresh.Beams).ToList();
            fresh.PendingOpenings=(previous.PendingOpenings ?? new List<CadPendingOpening>()).Where(p=>!floors.Contains(p.StoreyId)).Concat(fresh.PendingOpenings).ToList();
            fresh.ResolvedOpenings=(previous.ResolvedOpenings ?? new List<CadResolvedOpening>()).Where(r=>!floors.Contains(r.StoreyId)).ToList();
            fresh.StructureMessages.RemoveAll(m=>m.Contains("已有构件"));
            fresh.StructureMessages.Add("已按 CAD 替换登记楼层的构件；保留 "+retained+" 樘编号和洞口尺寸相同的门窗做法，位置与宿主重新计算。未登记楼层未修改。");
            return result;
        }
        private static bool SameOpeningSpecification(OpeningModel a,OpeningModel b)
            => string.Equals(OpeningConstruction.EffectiveCode(a).Trim(),OpeningConstruction.EffectiveCode(b).Trim(),StringComparison.OrdinalIgnoreCase)
                && Math.Round(a.Width/10,MidpointRounding.AwayFromZero)==Math.Round(b.Width/10,MidpointRounding.AwayFromZero)
                && Math.Round(a.Height/10,MidpointRounding.AwayFromZero)==Math.Round(b.Height/10,MidpointRounding.AwayFromZero);
        private static BuildingModelDocument BuildVerified(BuildingModelDocument current, CadFloorPlanRegistry registry, string requestId,Action<CadGenerationProgress> progress)
        {
            if (!string.IsNullOrWhiteSpace(registry?.GenerationWarning))
                throw new InvalidDataException(registry.GenerationWarning);
            if (registry == null || registry.Floors == null || registry.Floors.Count == 0) throw new InvalidDataException("请先登记楼层平面。");
            var model = BuildingModelJson.FromJson(BuildingModelJson.ToJson(current));
            registry = registry.Clone();
            registry.ReconcileStoreys(model);
            if (registry.Floors.Count == 0) throw new InvalidDataException("请先登记当前独立楼层的平面。");
            if (model.Storeys.Any(f=>f==null || !Finite(f.Height) || f.Height<=0 || !Finite(f.Elevation))
                || model.Storeys.GroupBy(f=>f.Id,StringComparer.OrdinalIgnoreCase).Any(g=>string.IsNullOrWhiteSpace(g.Key) || g.Count()!=1)
                || model.Storeys.Any(f=>!string.IsNullOrWhiteSpace(f.TemplateStoreyId) &&
                    (model.FindStorey(f.TemplateStoreyId)==null || !string.IsNullOrWhiteSpace(model.FindStorey(f.TemplateStoreyId).TemplateStoreyId))))
                throw new InvalidDataException("当前模型楼层或标准层引用无效，请先修正并保存。");
            if (registry.Floors.GroupBy(f=>f.Floor.Storey.Id,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()!=1))
                throw new InvalidDataException("楼层登记重复。");
            var previous = model.CadImport ?? new CadModelImportState();
            var previousSlabs=previous.Slabs ?? new List<SlabModel>();
            var previousColumns=previous.Columns ?? new List<ColumnModel>();
            var previousBeams=previous.Beams ?? new List<BeamModel>();
            var protectedOpeningIds=new HashSet<string>(previous.Openings
                .Where(o=>!SameOpening(o,model.Openings.FirstOrDefault(currentOpening=>currentOpening.Id==o.Id))).Select(o=>o.Id));
            var resolvedIds=new HashSet<string>((previous.ResolvedOpenings ?? new List<CadResolvedOpening>()).Select(r=>r.OpeningId));
            var protectedHostIds=new HashSet<string>(model.Openings.Where(o=>protectedOpeningIds.Contains(o.Id)
                ||resolvedIds.Contains(o.Id)).Select(o=>o.HostWallId));
            var columns=new List<ColumnModel>();var beams=new List<BeamModel>();var structureMessages=new List<string>();
            if(!Finite(registry.ResolvedSlabThickness) || registry.ResolvedSlabThickness<=0.5)
                throw new InvalidDataException("楼板厚度必须大于 0.5 mm。");
            var codes=registry.ResolveCodes();
            var walls = new List<WallModel>(); var openings = new List<OpeningModel>();
            var pending = new List<CadPendingOpening>();
            var slabs=new List<SlabModel>();var slabMessages=new List<string>();
            var hostMaps = new Dictionary<string,Dictionary<string,WallModel>>(StringComparer.OrdinalIgnoreCase);
            var completedFloors=0;
            foreach (var capture in registry.Floors)
            {
                progress?.Invoke(new CadGenerationProgress {Stage="生成楼层："+capture.Floor.Storey.Name,Completed=completedFloors,Total=registry.Floors.Count});
                capture.ValidateSchedule();
                CadStructuralRegistration.Add(capture,(kind,handle)=>Identity(kind,capture.Probe.DrawingFingerprint,capture.Floor.Storey.Id,handle),
                    columns,beams,structureMessages);
                var floor = model.FindStorey(capture.Floor.Storey.Id);
                if (floor == null || !string.IsNullOrWhiteSpace(floor.TemplateStoreyId))
                    throw new InvalidDataException("登记楼层已删除或改为标准层引用，请重新核对：" + capture.Floor.Storey.Name);
                var scale = capture.Floor.Alignment.MillimetresPerCadUnit;
                var hostMap = new Dictionary<string,WallModel>(StringComparer.OrdinalIgnoreCase);
                hostMaps.Add(floor.Id,hostMap);
                var wallNumber=0;
                foreach (var source in capture.Floor.WallCandidates.Where(w=>w==null || !(capture.ExcludedWallHandles ?? new List<string>()).Contains(w.Handle)))
                {
                    wallNumber++;
                    try
                    {
                    if (source==null || string.IsNullOrWhiteSpace(source.Handle))
                        throw new InvalidDataException(floor.Name + " · 第 " + wallNumber + " 面墙缺少来源身份。");
                    var circular=CadBuildingProbeRules.VerifyCircularWall(source);
                    if ((!source.StraightHorizontalLineVerified && !circular) || source.CurveStart == null || source.CurveEnd == null)
                        throw new InvalidDataException(floor.Name + " · 第 " + wallNumber + " 面墙的直线或圆弧定位尚未核验。");
                    var a = capture.Floor.Alignment.ToModel(new PointModel(source.CurveStart.X,source.CurveStart.Y));
                    var b = capture.Floor.Alignment.ToModel(new PointModel(source.CurveEnd.X,source.CurveEnd.Y));
                    // Verified native thickness takes priority; missing thickness uses the modelling default.
                    if(source.CandidateThickness.HasValue && !source.CandidateAxisOffset.HasValue)
                        throw new InvalidDataException(floor.Name + " · 第 " + wallNumber + " 面墙缺少左右墙厚定位。");
                    var elevation = Number(source,"Elevation");
                    if (!elevation.HasValue || !Finite(elevation.Value) || Math.Abs(elevation.Value*scale)>0.1)
                        throw new InvalidDataException(floor.Name + " · 第 " + wallNumber + " 面墙的非零/未知墙底偏移尚未支持，请核对或排除。");
                    // CAD 平面提供位置与墙厚；模型的层高由楼层设置统一决定。
                    // Height=0 使用已有的随层高机制，引用层也采用各自的实际层高。
                    var wall = new WallModel { Id = Identity("wall",capture.Probe.DrawingFingerprint,floor.Id,source.Handle), StoreyId = floor.Id,
                        X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y,Bulge=circular?source.CurveBulge:0,Thickness=source.CandidateThickness.HasValue ? source.CandidateThickness.Value*scale : 200d,
                        AxisOffset=source.CandidateAxisOffset.GetValueOrDefault()*scale,Height=0 };
                    if (!Finite(wall.X1) || !Finite(wall.Y1) || !Finite(wall.X2) || !Finite(wall.Y2)
                        || !Finite(wall.Thickness) || wall.Thickness<=0 || !Finite(wall.AxisOffset.Value) || !Finite(wall.Height) || wall.Height<0
                        || Math.Sqrt((b.X-a.X)*(b.X-a.X)+(b.Y-a.Y)*(b.Y-a.Y))<1)
                        throw new InvalidDataException(floor.Name + " · 第 " + wallNumber + " 面墙几何参数无效。");
                    if(circular)
                    {
                        var curve=CurveGeometry.ForWall(wall);
                        if(curve.Radius-Math.Sign(curve.Sweep)*WallReferenceGeometry.BodyOffset(wall)-wall.Thickness/2<=.5
                            ||Math.Abs(wall.AxisOffset.Value)>wall.Thickness/2+.001)
                            throw new InvalidDataException(floor.Name+" · 第 "+wallNumber+" 面弧墙内侧半径或左右墙厚无效。");
                    }
                    wall.Code = previous.Walls.FirstOrDefault(w=>w.Id==wall.Id)?.Code;
                    var localHost=model.Walls.FirstOrDefault(w=>w.Id==wall.Id);
                    if(protectedHostIds.Contains(wall.Id) && localHost!=null && !SameWall(wall,localHost))
                    {
                        wall=CopyWall(localHost);
                        structureMessages.Add(floor.Name+" · 已保留模型中修改过的门窗及其宿主墙，未覆盖该墙的 CAD 定位变化。");
                    }
                    hostMap.Add(source.Handle,wall); walls.Add(wall);
                    }
                    catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException)
                    {
                        structureMessages.Add(ex.Message+" 本墙自动识别已跳过，可参照 CAD 底图继续建模。");
                        if (source!=null && !string.IsNullOrWhiteSpace(source.Handle) && !string.IsNullOrWhiteSpace(capture.Probe.DrawingFingerprint))
                        {
                            var id=Identity("wall",capture.Probe.DrawingFingerprint,floor.Id,source.Handle);
                            var old=previous.Walls.FirstOrDefault(w=>w.Id==id);
                            var kept=model.Walls.FirstOrDefault(w=>w.Id==id);
                            if (old!=null && kept!=null && !hostMap.ContainsKey(source.Handle))
                            { var retained=CopyWall(kept);hostMap.Add(source.Handle,retained);walls.Add(retained); }
                        }
                    }
                }
                foreach(var localHost in model.Walls.Where(w=>w.StoreyId==floor.Id && protectedHostIds.Contains(w.Id)
                    && previous.Walls.Any(old=>old.Id==w.Id) && !hostMap.Values.Any(existing=>existing.Id==w.Id)))
                {
                    var retained=CopyWall(localHost);
                    var source=capture.Floor.WallCandidates.FirstOrDefault(w=>w!=null && !string.IsNullOrWhiteSpace(w.Handle)
                        && Identity("wall",capture.Probe.DrawingFingerprint,floor.Id,w.Handle)==localHost.Id);
                    hostMap.Add(source?.Handle ?? "retained-"+localHost.Id,retained);walls.Add(retained);
                    structureMessages.Add(floor.Name+" · 已保留模型中修改过的门窗宿主墙，未随本次登记范围删除。");
                }
                var floorMessages=new List<string>();
                var roomLabels=(capture.Probe.RoomLabels ?? new List<CadRoomLabel>()).Where(l=>l?.Position!=null)
                    .Select(l=>new CadRoomLabel { SourceHandle=l.SourceHandle,Name=l.Name,Position=capture.Floor.Alignment.ToModel(l.Position),AreaSquareMetres=l.AreaSquareMetres });
                List<SlabModel> floorSlabs;
                try {floorSlabs=CadFloorSlabGeneration.Build(hostMap.Values.ToList(),roomLabels,registry.ResolvedSlabThickness,floor.Id,floorMessages);}
                catch(Exception ex) when(ex is ArgumentException || ex is InvalidOperationException) {
                    floorMessages.Add("自动楼板裁剪未完成，已保留墙、门窗及梁柱；楼板可在模型中补画："+ex.Message);floorSlabs=new List<SlabModel>();
                }
                for(var i=0;i<floorSlabs.Count;i++) {
                    var slab=floorSlabs[i];
                    slab.Id=Identity("slab",capture.Probe.DrawingFingerprint,floor.Id,"part-"+i);
                    slab.Code=previousSlabs.FirstOrDefault(s=>s.Id==slab.Id)?.Code;
                    slab.TopElevation=floor.Elevation+floor.Height;
                    foreach(var hole in slab.Openings)hole.Id=slab.Id+"-"+hole.Id;
                    slabs.Add(slab);
                }
                slabMessages.AddRange(floorMessages.Select(m=>floor.Name+" · "+m));
                progress?.Invoke(new CadGenerationProgress {Stage="生成楼层："+floor.Name,Completed=++completedFloors,Total=registry.Floors.Count});
            }
            progress?.Invoke(new CadGenerationProgress {Stage="匹配门窗与合并模型"});
            if (walls.Count==0 && columns.Count==0 && beams.Count==0)
            {
                // A reference-only capture is not an instruction to delete existing construction.
                foreach (var capture in registry.Floors)
                foreach (var source in capture.Openings.Where(o=>o.Include))
                {
                    var floorId=capture.Floor.Storey.Id;
                    if ((previous.ResolvedOpenings ?? new List<CadResolvedOpening>()).Any(r=>r.StoreyId==floorId
                        && r.SourceHandle==source.SourceHandle && model.Openings.Any(o=>o.Id==r.OpeningId))) continue;
                    if (!string.IsNullOrWhiteSpace(capture.Probe.DrawingFingerprint) && model.Openings.Any(o=>o.Id==
                        Identity("opening",capture.Probe.DrawingFingerprint,floorId,source.SourceHandle))) continue;
                    var probe=capture.Probe.Entities.FirstOrDefault(e=>string.Equals(e.Handle,source.SourceHandle,StringComparison.OrdinalIgnoreCase));
                    pending.Add(Pending(capture,source,codes[source],probe,"未识别到宿主墙，可在模型中绘墙后放置"));
                }
                var registeredFloors=new HashSet<string>(registry.Floors.Select(c=>c.Floor.Storey.Id),StringComparer.OrdinalIgnoreCase);
                previous.PendingOpenings=(previous.PendingOpenings ?? new List<CadPendingOpening>())
                    .Where(p=>!registeredFloors.Contains(p.StoreyId)).Concat(pending).ToList();
                previous.RequestId=requestId;
                previous.StructureMessages=structureMessages.Concat(new[] {
                    "未识别到可生成的墙、梁或柱，已保留楼层、CAD 参照底图和已有构件，可继续建模。" }).Distinct().ToList();
                model.CadImport=previous;
                return model;
            }
            foreach(var c in previousColumns)if(!SameStructure(c,model.Columns.FirstOrDefault(x=>x.Id==c.Id)))
                throw new InvalidDataException("已编辑或删除登记柱，请先保留修改再重新登记。");
            foreach(var b in previousBeams)if(!SameStructure(b,model.Beams.FirstOrDefault(x=>x.Id==b.Id)))
                throw new InvalidDataException("已编辑或删除登记梁，请先保留修改再重新登记。");
            foreach (var wall in previous.Walls)
                if (!SameWall(wall, model.Walls.FirstOrDefault(w=>w.Id==wall.Id)))
                    throw new InvalidDataException("模型中已编辑或删除登记墙 " + wall.Code + "，请先保留该修改并核对重新登记范围。");
            foreach (var baseline in previous.Openings.Where(o=>protectedOpeningIds.Contains(o.Id)))
            {
                var edited=model.Openings.FirstOrDefault(o=>o.Id==baseline.Id);
                if(edited!=null)openings.Add(CopyOpening(edited));
                structureMessages.Add(edited==null
                    ? "已保留模型中删除的登记门窗 "+baseline.Code+"，本次不重新插入；其他构件继续生成。"
                    : "已保留模型中修改的门窗 "+edited.Code+"（尺寸、编号、位置及开启设置），其他构件继续生成。");
            }
            foreach(var slab in previousSlabs)
                if(!SameSlab(slab,model.Slabs.FirstOrDefault(s=>s.Id==slab.Id)))
                    throw new InvalidDataException("模型中已编辑或删除登记楼板 " + slab.Code + "，请先保留该修改并核对重新登记范围。");
            foreach (var capture in registry.Floors)
            {
                var floor=model.FindStorey(capture.Floor.Storey.Id);
                var hostMap=hostMaps[floor.Id];
                foreach (var source in capture.Openings.Where(o=>o.Include))
                {
                    var manual=model.CadImport?.ResolvedOpenings?.FirstOrDefault(p=>p.StoreyId==floor.Id&&p.SourceHandle==source.SourceHandle);
                    var sourceId=manual?.OpeningId ?? Identity("opening",capture.Probe.DrawingFingerprint,floor.Id,source.SourceHandle);
                    if(protectedOpeningIds.Contains(sourceId))continue;
                    var placed=manual==null?null:model.Openings.FirstOrDefault(o=>o.Id==manual.OpeningId);
                    if(manual!=null && placed==null)
                    {
                        structureMessages.Add("已保留模型中删除的已定位门窗 "+source.Code+"，其他构件继续生成。");
                        continue;
                    }
                    if(placed!=null) {
                        if(!hostMap.Values.Any(w=>w.Id==placed.HostWallId) && !model.Walls.Any(w=>w.Id==placed.HostWallId))
                            throw new InvalidDataException(source.Code+" 在模型中已定位，但宿主墙不存在。");
                        if(Math.Abs(placed.Width-source.Width.Value)>.5||Math.Abs(placed.Height-source.Height.Value)>.5)
                            structureMessages.Add("已保留模型中定位的门窗 "+placed.Code+" 及尺寸，未覆盖本次 CAD 尺寸变化；其他构件继续生成。");
                        openings.Add(CopyOpening(placed));continue;
                    }
                    var probe = capture.Probe.Entities.FirstOrDefault(e=>string.Equals(e.Handle,source.SourceHandle,StringComparison.OrdinalIgnoreCase));
                    var placement=source.Placement ?? CadOpeningJambPlacement.Find(capture.Floor,
                        probe,source.Width.Value);
                    if (placement == null)
                    {
                        pending.Add(Pending(capture, source, codes[source], probe, "未确认洞口位置，未开洞"));
                        continue;
                    }
                    CadOpeningPlacement location;
                    try { location = placement.Revalidate(capture.Floor,source.Width.Value); }
                    catch (InvalidDataException exception)
                    {
                        pending.Add(Pending(capture, source, codes[source], probe, exception.Message));
                        continue;
                    }
                    WallModel host;
                    if (!hostMap.TryGetValue(location.HostSourceHandle,out host))
                    {
                        pending.Add(Pending(capture, source, codes[source], probe, "宿主墙未纳入模型，未开洞"));
                        continue;
                    }
                    if(Math.Abs(host.Bulge)>1e-10)
                    {
                        pending.Add(Pending(capture,source,codes[source],probe,"弧墙门窗暂保留参考位置，未按直墙开洞"));
                        continue;
                    }
                    if (Distance(location.ModelWallStart,new PointModel(host.X1,host.Y1))>0.01
                        || Distance(location.ModelWallEnd,new PointModel(host.X2,host.Y2))>0.01)
                    {
                        pending.Add(Pending(capture, source, codes[source], probe, "宿主定位线已变化，未开洞"));
                        continue;
                    }
                    var targetFloors = model.Storeys.Where(f=>f.Id==floor.Id || string.Equals(f.TemplateStoreyId,floor.Id,StringComparison.OrdinalIgnoreCase));
                    var availableHeight=targetFloors.Min(f=>host.Height>0.5 ? host.Height : f.Height);
                    var sill=CadOpeningDefaults.Sill(source,availableHeight);
                    var length=Distance(new PointModel(host.X1,host.Y1),new PointModel(host.X2,host.Y2));
                    var width=OpeningConstruction.FitRoundedOpeningSize(source.Width.Value,
                        2*Math.Min(location.DistanceFromWallStart,length-location.DistanceFromWallStart));
                    var height=OpeningConstruction.FitRoundedOpeningSize(source.Height.Value,availableHeight-sill);
                    if (width<=0 || location.DistanceFromWallStart-width/2 < -0.5 || location.DistanceFromWallStart+width/2 > length+0.5)
                    {
                        pending.Add(Pending(capture,source,codes[source],probe,"洞口超出宿主墙端部，未开洞，可在模型中调整"));
                        continue;
                    }
                    if (height<=0 || sill + height > availableHeight+0.5)
                    {
                        pending.Add(Pending(capture,source,codes[source],probe,"洞口顶部超出宿主墙，未开洞，可在模型中调整"));
                        continue;
                    }
                    if(width!=source.Width.Value || height!=source.Height.Value)
                        structureMessages.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0} · {1} 洞口归整尺寸已适配实际宿主：{2:0.###} × {3:0.###} → {4:0.###} × {5:0.###} mm。",
                            floor.Name,codes[source],source.Width.Value,source.Height.Value,width,height));
                    openings.Add(new OpeningModel { Id=Identity("opening",capture.Probe.DrawingFingerprint,floor.Id,source.SourceHandle),
                        HostWallId=host.Id,Code=codes[source],Kind=source.ModelKind,Offset=location.DistanceFromWallStart,
                        Width=width,Height=height,Sill=sill,OpenIn3D=false });
                }
            }
            var oldColumnIds=new HashSet<string>(previousColumns.Select(c=>c.Id));
            var oldBeamIds=new HashSet<string>(previousBeams.Select(b=>b.Id));
            if(columns.Any(c=>model.Columns.Any(x=>x.Id==c.Id&&!oldColumnIds.Contains(c.Id)))
                ||beams.Any(b=>model.Beams.Any(x=>x.Id==b.Id&&!oldBeamIds.Contains(b.Id))))throw new InvalidDataException("登记梁柱与已有构件身份冲突。");
            for(var i=0;i<columns.Count;i++)columns[i].Code=model.Columns.FirstOrDefault(c=>c.Id==columns[i].Id)?.Code ?? "KZ-"+(i+1);
            for(var i=0;i<beams.Count;i++)beams[i].Code=model.Beams.FirstOrDefault(b=>b.Id==beams[i].Id)?.Code ?? "L-"+(i+1);
            model.Columns.RemoveAll(c=>oldColumnIds.Contains(c.Id));model.Columns.AddRange(columns);
            model.Beams.RemoveAll(b=>oldBeamIds.Contains(b.Id));model.Beams.AddRange(beams);
            var oldWallIds = new HashSet<string>(previous.Walls.Select(w=>w.Id));
            var oldOpeningIds = new HashSet<string>(previous.Openings.Select(o=>o.Id));
            oldOpeningIds.UnionWith((model.CadImport?.ResolvedOpenings??new List<CadResolvedOpening>()).Select(r=>r.OpeningId));
            var oldSlabIds=new HashSet<string>(previousSlabs.Select(s=>s.Id));
            var slabCodes=new HashSet<string>(model.Slabs.Where(s=>!oldSlabIds.Contains(s.Id) && !string.IsNullOrWhiteSpace(s.Code)).Select(s=>s.Code),StringComparer.OrdinalIgnoreCase);
            foreach(var slab in slabs.Where(s=>!string.IsNullOrWhiteSpace(s.Code)))
                if(!slabCodes.Add(slab.Code))slab.Code=null;
            var slabNumber=0;
            foreach(var slab in slabs.Where(s=>string.IsNullOrWhiteSpace(s.Code))) {
                do { slab.Code="S-"+(++slabNumber); } while(!slabCodes.Add(slab.Code));
            }
            if (walls.Any(w=>model.Walls.Any(x=>x.Id==w.Id && !oldWallIds.Contains(x.Id)))
                || openings.Any(o=>model.Openings.Any(x=>x.Id==o.Id && !oldOpeningIds.Contains(x.Id)))
                || slabs.Any(s=>model.Slabs.Any(x=>x.Id==s.Id && !oldSlabIds.Contains(x.Id))))
                throw new InvalidDataException("登记构件 ID 与现有模型冲突，未修改模型。");
            foreach(var fresh in openings) {
                var old=model.Openings.FirstOrDefault(o=>o.Id==fresh.Id);
                if(old!=null){fresh.PlanFlipAlong=old.PlanFlipAlong;fresh.PlanFlipNormal=old.PlanFlipNormal;
                    fresh.PlanLabelAlong=old.PlanLabelAlong;fresh.PlanLabelNormal=old.PlanLabelNormal;
                    fresh.PlanOpenAngle=old.PlanOpenAngle;fresh.OpenIn3D=old.OpenIn3D;fresh.CodeManuallyEdited=old.CodeManuallyEdited;fresh.ThresholdHeight=old.ThresholdHeight;
                    if(old.CodeManuallyEdited)fresh.Code=old.Code;}
                if(old!=null && (model.OpeningTypes??new List<OpeningTypeModel>()).Any(t=>t.Code==old.Code))fresh.Code=old.Code;
            }
            model.Walls.RemoveAll(w=>oldWallIds.Contains(w.Id)); model.Openings.RemoveAll(o=>oldOpeningIds.Contains(o.Id));
            model.Walls.AddRange(walls); model.Openings.AddRange(openings);
            model.Slabs.RemoveAll(s=>oldSlabIds.Contains(s.Id));model.Slabs.AddRange(slabs);
            if (model.Openings.Any(o=>!model.Walls.Any(w=>w.Id==o.HostWallId)))
                throw new InvalidDataException("更新将使手动添加的洞口失去宿主墙，请先核对。");
            foreach (var opening in model.Openings.Where(o=>walls.Any(w=>w.Id==o.HostWallId)))
            {
                var host = model.Walls.First(w=>w.Id==opening.HostWallId);
                var length = Distance(new PointModel(host.X1,host.Y1),new PointModel(host.X2,host.Y2));
                if (opening.Offset-opening.Width/2 < -0.5 || opening.Offset+opening.Width/2 > length+0.5
                    || model.Storeys.Where(f=>f.Id==host.StoreyId || f.TemplateStoreyId==host.StoreyId)
                        .Any(f=>opening.Sill+opening.Height>(host.Height>0.5 ? host.Height : f.Height)+0.5))
                    throw new InvalidDataException(opening.Code+" 超出更新后的宿主墙，请核对。");
            }
            BuildingElementNames.EnsureWallCodes(model);
            var structureBaseline=BuildingModelJson.FromJson(BuildingModelJson.ToJson(new BuildingModelDocument {Columns=columns,Beams=beams}));
            model.CadImport = new CadModelImportState { RequestId=requestId,
                Walls=walls.Select(CopyWall).ToList(),
                // Keep the CAD baseline for local overrides/deletions so the next import cannot undo them.
                Openings=openings.Where(o=>!protectedOpeningIds.Contains(o.Id)).Select(CopyOpening)
                    .Concat(previous.Openings.Where(o=>protectedOpeningIds.Contains(o.Id)).Select(CopyOpening)).ToList(),PendingOpenings=pending,
                ResolvedOpenings=(model.CadImport?.ResolvedOpenings??new List<CadResolvedOpening>()).Where(r=>openings.Any(o=>o.Id==r.OpeningId)
                    || protectedOpeningIds.Contains(r.OpeningId) || !model.Openings.Any(o=>o.Id==r.OpeningId)).ToList(),
                Slabs=slabs.Select(CopySlab).ToList(),SlabMessages=slabMessages.Distinct().ToList(),
                Columns=structureBaseline.Columns,Beams=structureBaseline.Beams,StructureMessages=structureMessages.Distinct().ToList() };
            return model;
        }
        private static CadPendingOpening Pending(CadFloorPlanCapture capture, CadFloorOpeningItem source,
            string code, CadBuildingProbeEntity probe, string reason)
        {
            PointModel position = null;
            if (probe != null && probe.BoundsMin != null && probe.BoundsMax != null
                && Finite(probe.BoundsMin.X) && Finite(probe.BoundsMin.Y)
                && Finite(probe.BoundsMax.X) && Finite(probe.BoundsMax.Y))
                position = capture.Floor.Alignment.ToModel(new PointModel(
                    probe.BoundsMin.X / 2 + probe.BoundsMax.X / 2,
                    probe.BoundsMin.Y / 2 + probe.BoundsMax.Y / 2));
            return new CadPendingOpening { StoreyId=capture.Floor.Storey.Id,SourceHandle=source.SourceHandle,
                Code=code,Kind=source.ModelKind,Width=source.Width.Value,Height=source.Height.Value,
                Reason=reason,ReferencePosition=position };
        }
        public static double? Number(CadBuildingProbeEntity source,string name) => source.Fields.FirstOrDefault(f=>f.Name==name)?.Number;
        private static bool SameStructure<T>(T before,T after) where T:class
        {
            if(before==null||after==null)return false;
            return typeof(T).GetProperties().Where(p=>p.Name!="Code").All(p=>Equals(p.GetValue(before,null),p.GetValue(after,null)));
        }
        private static double Distance(PointModel a,PointModel b) => Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static string Identity(string kind,string drawing,string floor,string handle)
        {
            if (string.IsNullOrWhiteSpace(drawing)) throw new InvalidDataException("来源图纸缺少指纹。");
            using (var hash=SHA256.Create()) return "CAD-"+kind+"-"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(drawing.ToUpperInvariant()+"|"+floor.ToUpperInvariant()+"|"+handle.ToUpperInvariant()))).Replace("-","").Substring(0,24);
        }
        private static WallModel CopyWall(WallModel w) => new WallModel { Id=w.Id,Code=w.Code,StoreyId=w.StoreyId,X1=w.X1,Y1=w.Y1,X2=w.X2,Y2=w.Y2,Bulge=w.Bulge,Thickness=w.Thickness,AxisOffset=w.AxisOffset,AxisPlacement=w.AxisPlacement,Height=w.Height,Material=w.Material,Purpose=w.Purpose,BaseOffset=w.BaseOffset };
        private static OpeningModel CopyOpening(OpeningModel o) => new OpeningModel { Id=o.Id,HostWallId=o.HostWallId,Code=o.Code,Kind=o.Kind,Offset=o.Offset,Width=o.Width,Height=o.Height,Sill=o.Sill,ThresholdHeight=o.ThresholdHeight,
            PlanFlipAlong=o.PlanFlipAlong,PlanFlipNormal=o.PlanFlipNormal,
            PlanLabelAlong=o.PlanLabelAlong,PlanLabelNormal=o.PlanLabelNormal,
            PlanOpenAngle=o.PlanOpenAngle,OpenIn3D=o.OpenIn3D,CodeManuallyEdited=o.CodeManuallyEdited };
        private static SlabModel CopySlab(SlabModel s)=>new SlabModel { Id=s.Id,Code=s.Code,StoreyId=s.StoreyId,Thickness=s.Thickness,TopElevation=s.TopElevation,TopOffset=s.TopOffset,FollowsStoreyTop=s.FollowsStoreyTop,Purpose=s.Purpose,
            Outline=s.Outline.Select(p=>new PointModel(p.X,p.Y) {Bulge=p.Bulge}).ToList(),Openings=s.Openings.Select(o=>new SlabOpeningModel {
                Id=o.Id,Name=o.Name,Outline=o.Outline.Select(p=>new PointModel(p.X,p.Y) {Bulge=p.Bulge}).ToList() }).ToList() };
        private static bool SameRing(IList<PointModel> a,IList<PointModel> b)=>a!=null && b!=null && a.Count==b.Count
            && !a.Where((p,i)=>p.X!=b[i].X || p.Y!=b[i].Y || p.Bulge!=b[i].Bulge).Any();
        private static bool SameSlab(SlabModel a,SlabModel b)=>b!=null && a.Id==b.Id && a.Code==b.Code && a.StoreyId==b.StoreyId
            && a.Purpose==b.Purpose && a.Thickness==b.Thickness && a.TopElevation==b.TopElevation && a.TopOffset==b.TopOffset && a.FollowsStoreyTop==b.FollowsStoreyTop && SameRing(a.Outline,b.Outline)
            && a.Openings.Count==(b.Openings?.Count ?? 0) && !a.Openings.Where((o,i)=>o.Id!=b.Openings[i].Id || o.Name!=b.Openings[i].Name || !SameRing(o.Outline,b.Openings[i].Outline)).Any();
        private static bool SameWall(WallModel a,WallModel b) => b!=null && a.Id==b.Id && a.Code==b.Code && a.StoreyId==b.StoreyId && a.X1==b.X1 && a.Y1==b.Y1 && a.X2==b.X2 && a.Y2==b.Y2 && a.Bulge==b.Bulge && a.Thickness==b.Thickness && a.AxisOffset==b.AxisOffset && a.AxisPlacement==b.AxisPlacement && a.Height==b.Height && a.Material==b.Material && a.Purpose==b.Purpose && a.BaseOffset==b.BaseOffset;
        private static bool SameOpening(OpeningModel a,OpeningModel b) => b!=null && a.Id==b.Id && a.HostWallId==b.HostWallId && (a.Code==b.Code||b.CodeManuallyEdited) && a.Kind==b.Kind && a.Offset==b.Offset && a.Width==b.Width && a.Height==b.Height && a.Sill==b.Sill;
    }

    public sealed partial class CadModelGenerationRequest
    {
        public string Id { get; set; }
        public CadFloorPlanRegistry Registry { get; set; }
        public static string FilePath(string modelPath) => modelPath + ".cad-generate.json";
        public static CadModelGenerationRequest Queue(CadFloorPlanRegistry registry,Action<CadGenerationProgress> progress=null)
        {
            var request = new CadModelGenerationRequest { Id=Guid.NewGuid().ToString("N"),Registry=registry };
            CadFloorModelGeneration.Build(BuildingModelJson.LoadModel(registry.ModelPath),registry,request.Id,progress);
            WithGate(registry.ModelPath,()=>
            {
                if(File.Exists(FilePath(registry.ModelPath)))throw new IOException("该模型已有生成请求，请等待完成后重试。");
                Write(FilePath(registry.ModelPath),request,typeof(CadModelGenerationRequest));
                WriteProgressOptional(registry.ModelPath,new CadGenerationProgress {RequestId=request.Id,Stage="等待模型窗口接收",State="waiting",UpdatedUtc=DateTime.UtcNow.ToString("O")});
            });return request;
        }
        public static CadModelGenerationRequest Load(string modelPath)
        {
            using (var stream=File.OpenRead(FilePath(modelPath))) return (CadModelGenerationRequest)new DataContractJsonSerializer(typeof(CadModelGenerationRequest)).ReadObject(stream);
        }
        public static void Cancel(string modelPath,string id)
        {
            WithGate(modelPath,()=>{var path=FilePath(modelPath);if(File.Exists(path)&&Load(modelPath).Id==id)File.Delete(path);});
        }
        public static CadModelGenerationResult LoadResult(string modelPath)
        {
            using(var stream=File.OpenRead(modelPath+".cad-generate-result.json"))
                return (CadModelGenerationResult)new DataContractJsonSerializer(typeof(CadModelGenerationResult)).ReadObject(stream);
        }
        public static void Acknowledge(string modelPath,string id,string error)
        {
            WithGate(modelPath,()=> {
                Write(modelPath+".cad-generate-result.json",new CadModelGenerationResult { RequestId=id,Error=error,Succeeded=error==null },typeof(CadModelGenerationResult));
                // Never replay a completed request after undo/save/reopen, and never remove a newer request.
                if(File.Exists(FilePath(modelPath)) && Load(modelPath).Id==id)
                {
                    WriteProgressOptional(modelPath,new CadGenerationProgress {RequestId=id,Stage=error==null?"模型已生成并保存":"生成未完成",State=error==null?"completed":"failed",Completed=error==null?1:0,Total=1,UpdatedUtc=DateTime.UtcNow.ToString("O")});
                    File.Delete(FilePath(modelPath));
                }
            });
        }
        private static void WithGate(string modelPath,Action action)
        {
            string name;
            using(var hash=SHA256.Create()) name="WanLuoCadGenerate-"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(modelPath).ToUpperInvariant()))).Replace("-","").Substring(0,32);
            using(var gate=new Mutex(false,name)) {
                try { if(!gate.WaitOne(TimeSpan.FromSeconds(5)))throw new IOException("另一生成请求正在提交，请稍后重试。"); }
                catch(AbandonedMutexException) { }
                try { action(); } finally { gate.ReleaseMutex(); }
            }
        }
        private static void Write(string path,object value,Type type)
        {
            var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try { using(var stream=File.Create(temp)) new DataContractJsonSerializer(type).WriteObject(stream,value);
                if(File.Exists(path)) File.Replace(temp,path,path+".bak"); else File.Move(temp,path); }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
    }
    public sealed class CadModelGenerationResult
    {
        public string RequestId { get; set; }
        public bool Succeeded { get; set; }
        public string Error { get; set; }
    }
}
