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
        public static BuildingModelDocument Build(BuildingModelDocument current, CadFloorPlanRegistry registry, string requestId = null)
        {
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
            foreach(var c in previousColumns)if(!SameStructure(c,model.Columns.FirstOrDefault(x=>x.Id==c.Id)))
                throw new InvalidDataException("已编辑或删除登记柱，请先保留修改再重新登记。");
            foreach(var b in previousBeams)if(!SameStructure(b,model.Beams.FirstOrDefault(x=>x.Id==b.Id)))
                throw new InvalidDataException("已编辑或删除登记梁，请先保留修改再重新登记。");
            var columns=new List<ColumnModel>();var beams=new List<BeamModel>();var structureMessages=new List<string>();
            if(!Finite(registry.ResolvedSlabThickness) || registry.ResolvedSlabThickness<=0.5)
                throw new InvalidDataException("楼板厚度必须大于 0.5 mm。");
            var codes=registry.ResolveCodes();
            foreach (var wall in previous.Walls)
                if (!SameWall(wall, model.Walls.FirstOrDefault(w=>w.Id==wall.Id)))
                    throw new InvalidDataException("模型中已编辑或删除登记墙 " + wall.Code + "，请先保留该修改并核对重新登记范围。");
            foreach (var opening in previous.Openings)
                if (!SameOpening(opening, model.Openings.FirstOrDefault(o=>o.Id==opening.Id)))
                    throw new InvalidDataException("模型中已编辑或删除登记洞口 " + opening.Code + "，请先核对后再更新登记模型。");
            foreach(var slab in previousSlabs)
                if(!SameSlab(slab,model.Slabs.FirstOrDefault(s=>s.Id==slab.Id)))
                    throw new InvalidDataException("模型中已编辑或删除登记楼板 " + slab.Code + "，请先保留该修改并核对重新登记范围。");
            var walls = new List<WallModel>(); var openings = new List<OpeningModel>();
            var pending = new List<CadPendingOpening>();
            var slabs=new List<SlabModel>();var slabMessages=new List<string>();
            foreach (var capture in registry.Floors)
            {
                capture.ValidateSchedule();
                CadStructuralRegistration.Add(capture,(kind,handle)=>Identity(kind,capture.Probe.DrawingFingerprint,capture.Floor.Storey.Id,handle),
                    columns,beams,structureMessages);
                var floor = model.FindStorey(capture.Floor.Storey.Id);
                if (floor == null || !string.IsNullOrWhiteSpace(floor.TemplateStoreyId))
                    throw new InvalidDataException("登记楼层已删除或改为标准层引用，请重新核对：" + capture.Floor.Storey.Name);
                var scale = capture.Floor.Alignment.MillimetresPerCadUnit;
                var hostMap = new Dictionary<string,WallModel>(StringComparer.OrdinalIgnoreCase);
                var wallNumber=0;
                foreach (var source in capture.Floor.WallCandidates.Where(w=>!(capture.ExcludedWallHandles ?? new List<string>()).Contains(w.Handle)))
                {
                    wallNumber++;
                    if (!source.StraightHorizontalLineVerified || source.CurveStart == null || source.CurveEnd == null)
                        throw new InvalidDataException(floor.Name + " · 第 " + wallNumber + " 面墙的直线定位尚未核验，请重新框选或在核对中排除不支持的墙。");
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
                        X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y,Thickness=source.CandidateThickness.HasValue ? source.CandidateThickness.Value*scale : 200d,
                        AxisOffset=source.CandidateAxisOffset.GetValueOrDefault()*scale,Height=0 };
                    if (!Finite(wall.Thickness) || wall.Thickness<=0 || !Finite(wall.AxisOffset.Value) || !Finite(wall.Height) || wall.Height<0
                        || Math.Sqrt((b.X-a.X)*(b.X-a.X)+(b.Y-a.Y)*(b.Y-a.Y))<1)
                        throw new InvalidDataException(floor.Name + " · 第 " + wallNumber + " 面墙几何参数无效。");
                    wall.Code = previous.Walls.FirstOrDefault(w=>w.Id==wall.Id)?.Code;
                    hostMap.Add(source.Handle,wall); walls.Add(wall);
                }
                var floorMessages=new List<string>();
                var roomLabels=(capture.Probe.RoomLabels ?? new List<CadRoomLabel>()).Where(l=>l?.Position!=null)
                    .Select(l=>new CadRoomLabel { SourceHandle=l.SourceHandle,Name=l.Name,Position=capture.Floor.Alignment.ToModel(l.Position),AreaSquareMetres=l.AreaSquareMetres });
                var floorSlabs=CadFloorSlabGeneration.Build(hostMap.Values.ToList(),roomLabels,registry.ResolvedSlabThickness,floor.Id,floorMessages);
                for(var i=0;i<floorSlabs.Count;i++) {
                    var slab=floorSlabs[i];
                    slab.Id=Identity("slab",capture.Probe.DrawingFingerprint,floor.Id,"part-"+i);
                    slab.Code=previousSlabs.FirstOrDefault(s=>s.Id==slab.Id)?.Code;
                    slab.TopElevation=floor.Elevation+floor.Height;
                    foreach(var hole in slab.Openings)hole.Id=slab.Id+"-"+hole.Id;
                    slabs.Add(slab);
                }
                slabMessages.AddRange(floorMessages.Select(m=>floor.Name+" · "+m));
                foreach (var source in capture.Openings.Where(o=>o.Include))
                {
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
                    if (Distance(location.ModelWallStart,new PointModel(host.X1,host.Y1))>0.01
                        || Distance(location.ModelWallEnd,new PointModel(host.X2,host.Y2))>0.01)
                    {
                        pending.Add(Pending(capture, source, codes[source], probe, "宿主定位线已变化，未开洞"));
                        continue;
                    }
                    var targetFloors = model.Storeys.Where(f=>f.Id==floor.Id || string.Equals(f.TemplateStoreyId,floor.Id,StringComparison.OrdinalIgnoreCase));
                    var availableHeight=targetFloors.Min(f=>host.Height>0.5 ? host.Height : f.Height);
                    var sill=CadOpeningDefaults.Sill(source,availableHeight);
                    if (sill + source.Height.Value > availableHeight+0.5)
                        throw new InvalidDataException(source.Code + " 的洞口顶部超出宿主墙，请核对墙高和离地高度。");
                    openings.Add(new OpeningModel { Id=Identity("opening",capture.Probe.DrawingFingerprint,floor.Id,source.SourceHandle),
                        HostWallId=host.Id,Code=codes[source],Kind=source.ModelKind,Offset=location.DistanceFromWallStart,
                        Width=source.Width.Value,Height=source.Height.Value,Sill=sill });
                }
            }
            if (walls.Count==0 && columns.Count==0 && beams.Count==0) throw new InvalidDataException("没有可生成的墙、梁或柱，请重新登记平面。");
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
                Walls=walls.Select(CopyWall).ToList(),Openings=openings.Select(CopyOpening).ToList(),PendingOpenings=pending,
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
        private static WallModel CopyWall(WallModel w) => new WallModel { Id=w.Id,Code=w.Code,StoreyId=w.StoreyId,X1=w.X1,Y1=w.Y1,X2=w.X2,Y2=w.Y2,Thickness=w.Thickness,AxisOffset=w.AxisOffset,AxisPlacement=w.AxisPlacement,Height=w.Height,Material=w.Material };
        private static OpeningModel CopyOpening(OpeningModel o) => new OpeningModel { Id=o.Id,HostWallId=o.HostWallId,Code=o.Code,Kind=o.Kind,Offset=o.Offset,Width=o.Width,Height=o.Height,Sill=o.Sill };
        private static SlabModel CopySlab(SlabModel s)=>new SlabModel { Id=s.Id,Code=s.Code,StoreyId=s.StoreyId,Thickness=s.Thickness,TopElevation=s.TopElevation,TopOffset=s.TopOffset,FollowsStoreyTop=s.FollowsStoreyTop,
            Outline=s.Outline.Select(p=>new PointModel(p.X,p.Y)).ToList(),Openings=s.Openings.Select(o=>new SlabOpeningModel {
                Id=o.Id,Name=o.Name,Outline=o.Outline.Select(p=>new PointModel(p.X,p.Y)).ToList() }).ToList() };
        private static bool SameRing(IList<PointModel> a,IList<PointModel> b)=>a!=null && b!=null && a.Count==b.Count
            && !a.Where((p,i)=>p.X!=b[i].X || p.Y!=b[i].Y).Any();
        private static bool SameSlab(SlabModel a,SlabModel b)=>b!=null && a.Id==b.Id && a.Code==b.Code && a.StoreyId==b.StoreyId
            && a.Thickness==b.Thickness && a.TopElevation==b.TopElevation && a.TopOffset==b.TopOffset && a.FollowsStoreyTop==b.FollowsStoreyTop && SameRing(a.Outline,b.Outline)
            && a.Openings.Count==(b.Openings?.Count ?? 0) && !a.Openings.Where((o,i)=>o.Id!=b.Openings[i].Id || o.Name!=b.Openings[i].Name || !SameRing(o.Outline,b.Openings[i].Outline)).Any();
        private static bool SameWall(WallModel a,WallModel b) => b!=null && a.Id==b.Id && a.Code==b.Code && a.StoreyId==b.StoreyId && a.X1==b.X1 && a.Y1==b.Y1 && a.X2==b.X2 && a.Y2==b.Y2 && a.Thickness==b.Thickness && a.AxisOffset==b.AxisOffset && a.AxisPlacement==b.AxisPlacement && a.Height==b.Height && a.Material==b.Material;
        private static bool SameOpening(OpeningModel a,OpeningModel b) => b!=null && a.Id==b.Id && a.HostWallId==b.HostWallId && a.Code==b.Code && a.Kind==b.Kind && a.Offset==b.Offset && a.Width==b.Width && a.Height==b.Height && a.Sill==b.Sill;
    }

    public sealed class CadModelGenerationRequest
    {
        public string Id { get; set; }
        public CadFloorPlanRegistry Registry { get; set; }
        public static string FilePath(string modelPath) => modelPath + ".cad-generate.json";
        public static CadModelGenerationRequest Queue(CadFloorPlanRegistry registry)
        {
            var request = new CadModelGenerationRequest { Id=Guid.NewGuid().ToString("N"),Registry=registry };
            CadFloorModelGeneration.Build(BuildingModelJson.LoadModel(registry.ModelPath),registry,request.Id);
            WithGate(registry.ModelPath,()=>Write(FilePath(registry.ModelPath),request,typeof(CadModelGenerationRequest))); return request;
        }
        public static CadModelGenerationRequest Load(string modelPath)
        {
            using (var stream=File.OpenRead(FilePath(modelPath))) return (CadModelGenerationRequest)new DataContractJsonSerializer(typeof(CadModelGenerationRequest)).ReadObject(stream);
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
                if(File.Exists(FilePath(modelPath)) && Load(modelPath).Id==id) File.Delete(FilePath(modelPath));
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
