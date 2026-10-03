using System;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;
using WanLuo.CadInterop;

namespace BatchPdfPublisher.Features.BuildingModel.Services
{
    internal static class TianzhengBuildingProbeService
    {
        // Candidates, not a semantic map. Preserve failed/empty reads individually.
        private static readonly string[] Properties =
        {
            "ObjectName", "Name", "Style", "Type", "Code", "Number", "Scale",
            "LeftWidth", "RightWidth", "Width", "Thickness", "Height", "WallHeight",
            "FloorHeight", "StoreyHeight", "Elevation", "BaseElevation", "BottomElevation",
            "Sill", "SillHeight", "WindowSillHeight", "Location", "Position", "InsertionPoint",
            "StartPoint", "EndPoint", "Rotation", "Direction", "OpeningDirection",
            "IsMirrored", "Mirror", "HostWall", "HostWallId", "WallHandle"
        };

        public static void Execute(Document document, bool registration = false)
        {
            if (document == null) return;
            try
            {
                if (registration)
                {
                    var project = new BatchPdfPublisher.Services.PublishPlanStore().GetActiveProject();
                    var path = StudioLaunch.ActiveModelPath(project?.ProjectFolder, project?.Name);
                    if (path == null) throw new InvalidOperationException("请先打开当前项目的建筑模型，设置并保存楼层，再进行 CAD 楼层登记。");
                    var window = new CadFloorRegistrationWindow(path,
                        (owner, floor) => PickFloorDatum(document, owner, floor),
                        (owner, floor) => CaptureFloor(document, owner, floor),
                        registry => {
                            // Validate before starting anything; the live editor merges and validates again.
                            var preview=CadFloorModelGeneration.Build(BuildingModelJson.LoadModel(path), registry);
                            if (!BuildingModelStudioLauncher.OpenCurrentModel(document, path))
                                throw new InvalidOperationException("建筑模型程序未启动，尚未提交生成请求。");
                            CadModelGenerationRequest.Queue(registry);
                            document.Editor.WriteMessage("\n已提交生成建筑模型：墙、门窗洞口和楼板将一并更新，可撤销本次生成。");
                            foreach(var message in preview.CadImport.SlabMessages)document.Editor.WriteMessage("\n"+message);
                            foreach(var message in preview.CadImport.StructureMessages)document.Editor.WriteMessage("\n"+message);
                        },
                        (owner,registry,model,locations) => Application.ShowModalWindow(new CadRegisteredOpeningTableWindow(registry,model,
                            (table,capture,item)=>PickOpeningPlacement(document,table,new CadOpeningRegistrationRow { SourceHandle=item.SourceHandle,Code=item.ModelCode,Width=item.Width },capture.Floor),
                            draft=>ReadRegisteredPlan(document,draft,false),locations) { Owner=owner })==true,
                        registry=>ReadRegisteredPlan(document,registry,true));
                    Application.ShowModalWindow(window);
                    return;
                }
                var selection = document.Editor.GetSelection(new PromptSelectionOptions
                    { MessageForAdding = "\n选择要核验的天正墙、门、窗（最多 100 个；只读，不修改图纸）：" });
                if (selection.Status != PromptStatus.OK) return;
                var report = ReadSelection(document, selection, false);
                Application.ShowModalWindow(new TianzhengBuildingProbeWindow(report));
            }
            catch (Exception ex) { Application.ShowAlertDialog("楼层登记/核验未完成：\n" + ex.Message); }
        }

        private static bool PickFloorDatum(Document document, System.Windows.Window owner, CadFloorRegistrationContext floor)
        {
            var editor = document.Editor;
            using (editor.StartUserInteraction(owner))
            {
                var ucs = editor.CurrentUserCoordinateSystem;
                if (!ucs.CoordinateSystem3d.Zaxis.IsParallelTo(Vector3d.ZAxis))
                    throw new InvalidOperationException("请使用与世界 XY 平行的 CAD 坐标系。");
                var point = editor.GetPoint("\n拾取 " + floor.Storey.Name + " 的对准基点（各层选择同一轴线交点）：");
                if (point.Status != PromptStatus.OK) return false;
                var origin = point.Value.TransformBy(ucs);
                var direction = origin + ucs.CoordinateSystem3d.Xaxis.GetNormal() * (10 / floor.Alignment.MillimetresPerCadUnit);
                floor.SetSourceDatum(new PointModel(origin.X, origin.Y), new PointModel(direction.X, direction.Y));
                return true;
            }
        }

        private static CadFloorPlanCapture CaptureFloor(Document document, System.Windows.Window owner, CadFloorRegistrationContext floor)
        {
            var editor = document.Editor; CadBuildingProbeDocument report;
            using (editor.StartUserInteraction(owner))
            {
                var ucs = editor.CurrentUserCoordinateSystem;
                if (!ucs.CoordinateSystem3d.Zaxis.IsParallelTo(Vector3d.ZAxis))
                    throw new InvalidOperationException("请使用与世界 XY 平行的 CAD 坐标系。");
                var first = editor.GetPoint("\n框选 " + floor.Storey.Name + " 登记平面第一个角点：");
                if (first.Status != PromptStatus.OK) return null;
                var last = editor.GetCorner(new PromptCornerOptions("\n框选登记平面另一个角点：", first.Value));
                if (last.Status != PromptStatus.OK) return null;
                var minX = Math.Min(first.Value.X, last.Value.X); var maxX = Math.Max(first.Value.X, last.Value.X);
                var minY = Math.Min(first.Value.Y, last.Value.Y); var maxY = Math.Max(first.Value.Y, last.Value.Y);
                if ((maxX-minX) * floor.Alignment.MillimetresPerCadUnit < 1 || (maxY-minY) * floor.Alignment.MillimetresPerCadUnit < 1)
                    throw new InvalidOperationException("框选范围过小。");
                var polygon = new Point3dCollection(new[] { new Point3d(minX,minY,first.Value.Z).TransformBy(ucs),
                    new Point3d(maxX,minY,first.Value.Z).TransformBy(ucs), new Point3d(maxX,maxY,first.Value.Z).TransformBy(ucs),
                    new Point3d(minX,maxY,first.Value.Z).TransformBy(ucs) });
                floor.RegionMin = new PointModel(polygon.Cast<Point3d>().Min(p=>p.X),polygon.Cast<Point3d>().Min(p=>p.Y));
                floor.RegionMax = new PointModel(polygon.Cast<Point3d>().Max(p=>p.X),polygon.Cast<Point3d>().Max(p=>p.Y));
                floor.RegionPolygon = polygon.Cast<Point3d>().Select(p=>new PointModel(p.X,p.Y)).ToList();
                var selection = editor.SelectCrossingPolygon(polygon, new SelectionFilter(new[] { new TypedValue(0,"TCH_WALL,TCH_CURTAIN_WALL,TCH_OPENING,TCH_COLUMN,TCH_BEAM") }));
                if (selection.Status != PromptStatus.OK) throw new InvalidOperationException("框选范围内未找到天正墙或门窗洞口，请重新框选。");
                report = ReadSelection(document, selection, true);
            }
            ReadRegionWalls(document,floor,report);
            var selectedWalls=RegionWallHandles(floor,report);
            ReadRoomLabels(document,floor,report);
            var capture = CadFloorPlanCapture.FromProbe(floor, report);
            RetainUsedWalls(capture,selectedWalls);
            var review = new CadFloorOpeningReviewWindow(capture, (window, item) =>
                PickOpeningPlacement(document, window, new CadOpeningRegistrationRow { SourceHandle = item.SourceHandle, Width = item.Width }, floor)) { Owner = owner };
            return Application.ShowModalWindow(review) == true ? capture : null;
        }

        private static CadBuildingProbeDocument ReadSelection(Document document, PromptSelectionResult selection, bool registration)
        {
            var ids = selection.Value.GetObjectIds().Distinct().ToArray();
            if (ids.Length > (registration ? 5000 : 100)) throw new InvalidOperationException("对象数量超过上限，请缩小框选范围。");
            var report = new CadBuildingProbeDocument { CapturedAt = DateTimeOffset.Now.ToString("O"), DrawingPath = document.Name,
                DrawingFingerprint = document.Database.FingerprintGuid.ToString(), CadVersion = SystemVariable("ACADVER"),
                DbmodBefore = Dbmod(), TianzhengEnvironment = EnvironmentDescription() };
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in ids)
                {
                    var record = new CadBuildingProbeEntity { Handle = id.Handle.ToString() }; report.Entities.Add(record);
                    try
                    {
                        var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null) { record.Category = "未适配对象"; continue; }
                        ReadEntity(entity, record, registration);
                        if (registration && string.Equals(record.DxfName,"TCH_OPENING",StringComparison.OrdinalIgnoreCase))
                            ReadOpeningLabel(entity, record);
                    }
                    catch (Exception ex) { record.Category = "读取失败"; record.Note = ex.Message; }
                }
            }
            report.DbmodAfter = Dbmod(); return report;
        }

        internal static void ReadRegisteredPlan(Document document,CadFloorPlanRegistry registry,bool persist)
        {
            var fingerprint=document.Database.FingerprintGuid.ToString();
            if(registry.Floors.Any(c=>!SameDrawing(c.Probe.DrawingFingerprint,fingerprint)))
                throw new InvalidDataException("请打开登记时的源 CAD 图纸，再自动读取门窗位置。");
            var file=CadFloorPlanRegistry.FilePath(registry.ModelPath);
            var stamp=File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0;
            var draft=registry.Clone();
            foreach(var capture in draft.Floors) {
                var previousWalls=capture.Floor.WallCandidates.Select(w=>w.Handle).ToList();
                var handles=previousWalls.Concat(capture.Openings.Select(o=>o.SourceHandle))
                    .Concat(capture.Probe.Entities.Where(e=>CadStructuralRegistration.IsStructure(e.DxfName)).Select(e=>e.Handle)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var report=new CadBuildingProbeDocument { DrawingPath=capture.Probe.DrawingPath,DrawingFingerprint=capture.Probe.DrawingFingerprint,CapturedAt=DateTimeOffset.Now.ToString("O"),
                    CadVersion=SystemVariable("ACADVER"),TianzhengEnvironment=EnvironmentDescription(),DbmodBefore=Dbmod() };
                using(var tx=document.Database.TransactionManager.StartOpenCloseTransaction()) {
                    foreach(var handle in handles) {
                        Entity entity;
                        try { var id=document.Database.GetObjectId(false,new Handle(Convert.ToInt64(handle,16)),0);entity=tx.GetObject(id,OpenMode.ForRead,false) as Entity; }
                        catch(Exception) { throw new InvalidDataException(capture.Floor.Storey.Name+" 的源平面对象已删除或无法读取，请重新框选登记这一层。"); }
                        if(entity==null)throw new InvalidDataException("源平面对象无法读取，请重新登记。");
                        var record=new CadBuildingProbeEntity { Handle=handle };ReadEntity(entity,record,true);
                        if(string.Equals(record.DxfName,"TCH_OPENING",StringComparison.OrdinalIgnoreCase))ReadOpeningLabel(entity,record);
                        else if(!IsWall(record)&&!CadStructuralRegistration.IsStructure(record.DxfName))throw new InvalidDataException("源平面对象类型已改变，请重新登记。");
                        report.Entities.Add(record);
                    }
                }
                ReadRegionWalls(document,capture.Floor,report);
                previousWalls=RegionWallHandles(capture.Floor,report);
                ReadRoomLabels(document,capture.Floor,report);
                foreach(var item in capture.Openings) {
                    var source=report.Entities.Single(e=>string.Equals(e.Handle,item.SourceHandle,StringComparison.OrdinalIgnoreCase));
                    if(!string.Equals(source.DxfName,"TCH_OPENING",StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("源门窗对象已改变，请重新登记。");
                    var nativeCode=source.Fields.FirstOrDefault(f=>f.Name=="OpeningCode" && f.Status.StartsWith("已读取",StringComparison.Ordinal) && CadOpeningDefaults.HasCode(f.Text))?.Text;
                    if(item.CodeSource!="门窗表编号与尺寸") {
                        item.Width=source.Fields.FirstOrDefault(f=>f.Name=="Width")?.Number*capture.Floor.Alignment.MillimetresPerCadUnit;
                        item.Height=source.Fields.FirstOrDefault(f=>f.Name=="Height")?.Number*capture.Floor.Alignment.MillimetresPerCadUnit;
                        if(!CadOpeningDefaults.HasCode(item.Code) && nativeCode!=null) { item.Code=nativeCode;item.Kind=null;item.CodeSource="本洞口自身标注"; }
                        if(!CadOpeningDefaults.HasCode(item.Code) && item.Kind!="门" && item.Kind!="窗")item.Kind=CadOpeningDefaults.KindFromLayer(source.Layer);
                    }
                    var old=item.Placement;
                    if(old!=null && old.Source!=null && (old.Source.StartsWith("人工",StringComparison.Ordinal) || old.Source.StartsWith("用户选择",StringComparison.Ordinal))) {
                        try { item.Placement=old.Revalidate(capture.Floor,item.Width.GetValueOrDefault()); }
                        catch(InvalidDataException) { item.Placement=null; }
                    } else item.Placement=CadOpeningJambPlacement.Find(capture.Floor,source,item.Width.GetValueOrDefault());
                    if(item.CodeSource!="门窗表编号与尺寸" && !CadOpeningDefaults.HasCode(item.Code)
                        && item.Placement?.Source?.StartsWith("本洞口原生门扇",StringComparison.Ordinal)==true)item.Kind="门";
                }
                report.DbmodAfter=Dbmod();capture.Probe=report;RetainUsedWalls(capture,previousWalls);
            }
            if(persist)registry.ReplaceFrom(draft,stamp);
            else { registry.Floors=draft.Floors;registry.Datums=draft.Datums; }
        }
        private static bool IsWall(CadBuildingProbeEntity e)=>CadBuildingProbeRules.IsWall(e.DxfName);
        private static void ReadRegionWalls(Document document,CadFloorRegistrationContext floor,CadBuildingProbeDocument report)
        {
            if(floor.RegionPolygon==null || floor.RegionPolygon.Count<3)return;
            var known=new HashSet<string>(report.Entities.Select(e=>e.Handle),StringComparer.OrdinalIgnoreCase);
            var openings=report.Entities.Where(e=>e.DxfName=="TCH_OPENING" && e.BoundsMin!=null && e.BoundsMax!=null).ToList();
            var padding=500/floor.Alignment.MillimetresPerCadUnit;
            using(var tx=document.Database.TransactionManager.StartOpenCloseTransaction()) {
                var space=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database),OpenMode.ForRead);
                foreach(ObjectId id in space) {
                    if(known.Contains(id.Handle.ToString()) || !CadBuildingProbeRules.IsWall(id.ObjectClass.DxfName))continue;
                    var entity=tx.GetObject(id,OpenMode.ForRead,false) as Entity;if(entity==null)continue;
                    Extents3d bounds;try { bounds=entity.GeometricExtents; } catch { continue; }
                    if(!RegionIntersects(floor,bounds) && !openings.Any(o=>bounds.MaxPoint.X>=o.BoundsMin.X-padding && bounds.MinPoint.X<=o.BoundsMax.X+padding
                        && bounds.MaxPoint.Y>=o.BoundsMin.Y-padding && bounds.MinPoint.Y<=o.BoundsMax.Y+padding))continue;
                    if(report.Entities.Count>=25000)throw new InvalidDataException("登记平面墙体数量过多，请缩小登记范围。");
                    var record=new CadBuildingProbeEntity { Handle=id.Handle.ToString() };ReadEntity(entity,record,true,bounds);report.Entities.Add(record);
                }
            }
            floor.WallCandidates=report.Entities.Where(IsWall).ToList();
            foreach(var wall in floor.WallCandidates.Where(w=>w.DxfName=="TCH_CURTAIN_WALL"))
                CadBuildingProbeRules.DeriveCurtainWall(wall,floor.Alignment.MillimetresPerCadUnit);
        }
        private static List<string> RegionWallHandles(CadFloorRegistrationContext floor,CadBuildingProbeDocument report)
        {
            // Nearby walls are read for opening placement, but retained only if they
            // intersect the registered region or actually host a captured opening.
            return report.Entities.Where(e=>IsWall(e) && (e.BoundsMin==null || e.BoundsMax==null ||
                RegionIntersects(floor,new Extents3d(new Point3d(e.BoundsMin.X,e.BoundsMin.Y,e.BoundsMin.Z),
                    new Point3d(e.BoundsMax.X,e.BoundsMax.Y,e.BoundsMax.Z)))))
                .Select(e=>e.Handle).ToList();
        }
        private static bool RegionIntersects(CadFloorRegistrationContext floor,Extents3d bounds)
        {
            if(bounds.MaxPoint.X<floor.RegionMin.X || bounds.MinPoint.X>floor.RegionMax.X
                || bounds.MaxPoint.Y<floor.RegionMin.Y || bounds.MinPoint.Y>floor.RegionMax.Y)return false;
            var box=new[] { new PointModel(bounds.MinPoint.X,bounds.MinPoint.Y),new PointModel(bounds.MaxPoint.X,bounds.MinPoint.Y),
                new PointModel(bounds.MaxPoint.X,bounds.MaxPoint.Y),new PointModel(bounds.MinPoint.X,bounds.MaxPoint.Y) };
            if(box.Any(p=>CadFloorSlabGeneration.Inside(p,floor.RegionPolygon))
                || floor.RegionPolygon.Any(p=>p.X>=bounds.MinPoint.X && p.X<=bounds.MaxPoint.X && p.Y>=bounds.MinPoint.Y && p.Y<=bounds.MaxPoint.Y))return true;
            for(var i=0;i<4;i++)for(var j=0;j<floor.RegionPolygon.Count;j++) {
                var a=box[i];var b=box[(i+1)%4];var c=floor.RegionPolygon[j];var d=floor.RegionPolygon[(j+1)%floor.RegionPolygon.Count];
                if(Side(a,b,c)*Side(a,b,d)<=0 && Side(c,d,a)*Side(c,d,b)<=0)return true;
            }
            return false;
        }
        private static double Side(PointModel a,PointModel b,PointModel p)=>(b.X-a.X)*(p.Y-a.Y)-(b.Y-a.Y)*(p.X-a.X);
        private static void ReadRoomLabels(Document document,CadFloorRegistrationContext floor,CadBuildingProbeDocument report)
        {
            report.RoomLabels=new List<CadRoomLabel>();
            var region=floor.RegionPolygon;
            if(region==null || region.Count<3)return;
            using(var tx=document.Database.TransactionManager.StartOpenCloseTransaction()) {
                var space=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database),OpenMode.ForRead);
                foreach(ObjectId id in space) {
                    var dxf=id.ObjectClass.DxfName;
                    if(!new[] { "TEXT","MTEXT","TCH_TEXT","TCH_MTEXT","TCH_ROOM","TCH_SPACE","TCH_ROOM_NAME" }.Contains(dxf))continue;
                    var entity=tx.GetObject(id,OpenMode.ForRead,false) as Entity;if(entity==null)continue;
                    Extents3d bounds;try { bounds=entity.GeometricExtents; } catch { continue; }
                    if(bounds.MaxPoint.X<floor.RegionMin.X || bounds.MinPoint.X>floor.RegionMax.X
                        || bounds.MaxPoint.Y<floor.RegionMin.Y || bounds.MinPoint.Y>floor.RegionMax.Y)continue;
                    var count=0;
                    var texts=new List<string>();var first=report.RoomLabels.Count;
                    ReadRoomText(entity,id.Handle.ToString(),region,report.RoomLabels,texts,0,ref count);
                    if(dxf=="TCH_SPACE") {
                        var areas=texts.Select(t=>System.Text.RegularExpressions.Regex.Match(t.Trim(),@"^(\d+(?:\.\d+)?)\s*(?:m(?:2|²)?|㎡|平方米)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            .Where(m=>m.Success).Select(m=>double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)).Distinct().ToList();
                        if(areas.Count==1)for(var i=first;i<report.RoomLabels.Count;i++)report.RoomLabels[i].AreaSquareMetres=areas[0];
                    }
                }
            }
        }
        private static void ReadRoomText(Entity entity,string handle,IList<PointModel> region,IList<CadRoomLabel> labels,IList<string> texts,int depth,ref int count)
        {
            if(depth>=6 || ++count>2048)return;
            var text=entity is DBText ? ((DBText)entity).TextString : entity is MText ? ((MText)entity).Text : null;
            if(text!=null) {
                texts.Add(text);
                if(!CadFloorSlabGeneration.IsVoidName(text))return;
                var point=entity is DBText ? ((DBText)entity).Position : ((MText)entity).Location;
                try { var bounds=entity.GeometricExtents;point=new Point3d(bounds.MinPoint.X/2+bounds.MaxPoint.X/2,bounds.MinPoint.Y/2+bounds.MaxPoint.Y/2,point.Z); } catch { }
                var position=new PointModel(point.X,point.Y);
                if(CadFloorSlabGeneration.Inside(position,region))labels.Add(new CadRoomLabel { SourceHandle=handle,Name=text.Trim(),Position=position });
                return;
            }
            var parts=new DBObjectCollection();
            try {
                entity.Explode(parts);
                foreach(DBObject part in parts)if(part is Entity)
                    ReadRoomText((Entity)part,handle,region,labels,texts,depth+1,ref count);
            } catch(Autodesk.AutoCAD.Runtime.Exception) { }
            finally { foreach(DBObject part in parts)part.Dispose();parts.Dispose(); }
        }
        private static bool SameDrawing(string left,string right)
        {
            Guid a,b;return Guid.TryParse(left,out a) && Guid.TryParse(right,out b) && a==b;
        }
        private static void RetainUsedWalls(CadFloorPlanCapture capture,IEnumerable<string> selected)
        {
            var keep=new HashSet<string>(selected,StringComparer.OrdinalIgnoreCase);
            foreach(var item in capture.Openings.Where(o=>o.Include && o.Placement!=null))keep.Add(item.Placement.HostSourceHandle);
            capture.Floor.WallCandidates=capture.Floor.WallCandidates.Where(w=>keep.Contains(w.Handle)).ToList();
        }
        private static void ExpandOpeningHosts(Document document,CadFloorRegistrationContext floor,CadBuildingProbeDocument report)
        {
            var openings=report.Entities.Where(e=>string.Equals(e.DxfName,"TCH_OPENING",StringComparison.OrdinalIgnoreCase) && e.BoundsMin!=null && e.BoundsMax!=null).ToList();
            var known=new HashSet<string>(report.Entities.Select(e=>e.Handle),StringComparer.OrdinalIgnoreCase);
            var padding=500/floor.Alignment.MillimetresPerCadUnit;
            // Database iteration works even if part of the registered floor is off screen.
            // Nearby native wall getters are read only after spatial filtering.
            using(var tx=document.Database.TransactionManager.StartOpenCloseTransaction()) {
                var space=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database),OpenMode.ForRead);
                foreach(ObjectId id in space) {
                    if(known.Contains(id.Handle.ToString()) || !CadBuildingProbeRules.IsWall(id.ObjectClass.DxfName))continue;
                    var entity=tx.GetObject(id,OpenMode.ForRead,false) as Entity;if(entity==null)continue;
                    Extents3d bounds;try { bounds=entity.GeometricExtents; } catch { continue; }
                    if(!openings.Any(o=>bounds.MaxPoint.X>=o.BoundsMin.X-padding && bounds.MinPoint.X<=o.BoundsMax.X+padding
                        && bounds.MaxPoint.Y>=o.BoundsMin.Y-padding && bounds.MinPoint.Y<=o.BoundsMax.Y+padding))continue;
                    if(report.Entities.Count>=25000)throw new InvalidDataException("登记平面附近墙体数量过多，请缩小登记范围。");
                    var record=new CadBuildingProbeEntity { Handle=id.Handle.ToString() };ReadEntity(entity,record,true);report.Entities.Add(record);known.Add(record.Handle);
                }
            }
            floor.WallCandidates=report.Entities.Where(IsWall).ToList();
            foreach(var wall in floor.WallCandidates.Where(w=>w.DxfName=="TCH_CURTAIN_WALL"))
                CadBuildingProbeRules.DeriveCurtainWall(wall,floor.Alignment.MillimetresPerCadUnit);
        }

        private static void ReadOpeningLabel(Entity entity, CadBuildingProbeEntity record)
        {
            // Only this opening's own display parts; never choose a nearby drawing label.
            var labels=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var count=0;
            try
            {
                ReadDisplayParts(entity,record,labels,0,ref count);
                record.Fields.Add(CadBuildingProbeRules.Field("OpeningCode", labels.Count == 1,
                    labels.Count == 1 ? labels.First() : null, labels.Count == 1 ? null : "本洞口没有唯一编号，自动按尺寸编号"));
            }
            catch (Exception ex) { record.Fields.Add(CadBuildingProbeRules.Field("OpeningCode",false,null,ex.Message)); }
        }
        private static void ReadDisplayParts(Entity entity,CadBuildingProbeEntity record,HashSet<string> labels,int depth,ref int count)
        {
            if(depth>=6 || count>=2048)return;
            var parts=new DBObjectCollection();
            try {
                entity.Explode(parts);
                foreach(DBObject part in parts) {
                    if(++count>2048)break;
                    var text=part is DBText ? ((DBText)part).TextString : part is MText ? ((MText)part).Text : null;
                    if(!string.IsNullOrWhiteSpace(text) && System.Text.RegularExpressions.Regex.IsMatch(text.Trim(),@"^[A-Za-z][A-Za-z0-9_\-\.]*\d[A-Za-z0-9_\-\.]*$"))labels.Add(text.Trim());
                    var line=part as Line;
                    if(line!=null) { record.DisplaySegments.Add(new CadProbeSegment { Start=Point(line.StartPoint),End=Point(line.EndPoint) });continue; }
                    var arc=part as Arc;
                    if(arc!=null) { record.DisplayArcs.Add(new CadProbeArc { Center=Point(arc.Center),Start=Point(arc.StartPoint),End=Point(arc.EndPoint),Radius=arc.Radius,Sweep=arc.TotalAngle });continue; }
                    var poly=part as Polyline;
                    if(poly!=null) {
                        for(var i=0;i<(poly.Closed ? poly.NumberOfVertices : poly.NumberOfVertices-1);i++)
                            if(poly.GetSegmentType(i)==SegmentType.Line)record.DisplaySegments.Add(new CadProbeSegment { Start=Point(poly.GetPoint3dAt(i)),End=Point(poly.GetPoint3dAt((i+1)%poly.NumberOfVertices)) });
                            else if(poly.GetSegmentType(i)==SegmentType.Arc)using(var curved=poly.GetArcSegmentAt(i)) {
                                record.DisplayArcs.Add(new CadProbeArc { Center=Point(new Point3d(curved.Center.X,curved.Center.Y,poly.Elevation).TransformBy(Matrix3d.PlaneToWorld(poly.Normal))),
                                    Start=Point(poly.GetPoint3dAt(i)),End=Point(poly.GetPoint3dAt((i+1)%poly.NumberOfVertices)),Radius=curved.Radius,Sweep=Math.Abs(curved.EndAngle-curved.StartAngle) });
                            }
                        continue;
                    }
                    var nested=part as Entity;if(nested==null || part is DBText || part is MText)continue;
                    // Group openings and block references can contain the native frame.
                    if(nested is BlockReference || new[] { "TCH_OPENING","TCH_TEXT","TCH_MTEXT","TCH_BLOCK_INSERT" }.Contains(nested.GetRXClass()?.DxfName))
                        ReadDisplayParts(nested,record,labels,depth+1,ref count);
                }
            } catch(Autodesk.AutoCAD.Runtime.Exception) { /* Some display primitives cannot be exploded. */ }
            finally { foreach(DBObject part in parts)part.Dispose();parts.Dispose(); }
        }
        private static void ReadEntity(Entity entity, CadBuildingProbeEntity record, bool registration, Extents3d? knownBounds = null)
        {
            record.DxfName = entity.GetRXClass() == null ? "" : entity.GetRXClass().DxfName;
            record.ManagedType = entity.GetType().FullName;
            record.Layer = entity.Layer;
            if(CadStructuralRegistration.IsStructure(record.DxfName))
            {
                // Use read-only display geometry; do not probe unverified Tianzheng COM getters.
                record.Category=CadStructuralRegistration.IsColumn(record.DxfName)?"天正柱":"天正梁";
                var count=0;ReadDisplayParts(entity,record,new HashSet<string>(),0,ref count);
                record.StructuralOutline=CadStructuralRegistration.FindRectangle(record.DisplaySegments);
                record.CurveStatus=record.StructuralOutline.Count==4?"原生矩形轮廓已确认":"未找到可确认的矩形轮廓";
                return;
            }
            var curtain=string.Equals(record.DxfName,"TCH_CURTAIN_WALL",StringComparison.OrdinalIgnoreCase);
            object com = null;
            string comError = null;
            try { if(!curtain)com = entity.AcadObject; }
            catch (Exception exception) { comError = exception.GetType().Name + ": " + exception.Message; }
            // Production registration only reads the three opening getters already
            // verified in CAD 2022/T20 V9. Never enumerate native getters: even some
            // publicly advertised getters can raise native access violations.
            var safeProperties = string.Equals(record.DxfName, "TCH_WALL", StringComparison.OrdinalIgnoreCase)
                ? new[] { "ObjectName", "LeftWidth", "RightWidth", "Height", "Elevation" }
                : new[] { "ObjectName", "Width", "Height" };
            foreach (var name in curtain ? new string[0] : registration ? safeProperties : Properties)
            {
                object value;
                string error;
                var readable = TianzhengReadOnlyAccess.TryReadProperty(com, name, out value, out error);
                record.Fields.Add(CadBuildingProbeRules.Field(name, readable, value, comError ?? error));
            }
            record.ComType = record.Fields.FirstOrDefault(f => f.Name == "ObjectName")?.Text;
            record.Category = CadBuildingProbeRules.Classify(record.DxfName, record.ComType);
            try
            {
                var bounds = knownBounds ?? entity.GeometricExtents;
                record.BoundsMin = Point(bounds.MinPoint); record.BoundsMax = Point(bounds.MaxPoint);
            }
            catch { record.Note = "无法读取包围范围；不使用包围框推断定位线。"; }
            var curve = entity as Curve;
            record.CurveStatus = "未提供曲线接口；定位线待适配，不用包围框代替";
            if (curve != null)
            {
                try
                {
                    // Retain the actual curve sample, including Z; do not label an
                    // arbitrary curved wall as a straight wall based on endpoints.
                    var start = Point(curve.StartPoint); var end = Point(curve.EndPoint);
                    var middle = Point(curve.GetPointAtParameter((curve.StartParam + curve.EndParam) / 2));
                    record.CurveStart = start; record.CurveEnd = end; record.CurveMidpoint = middle;
                    var length=Math.Abs(curve.GetDistanceAtParameter(curve.EndParam)-curve.GetDistanceAtParameter(curve.StartParam));
                    var chord=curve.StartPoint.DistanceTo(curve.EndPoint);
                    record.CurveLength=length;
                    record.StraightHorizontalLineVerified=!double.IsNaN(length) && !double.IsInfinity(length) && chord>0
                        && Math.Abs(length-chord)<0.000001 && Math.Abs(start.Z-end.Z)<0.000001
                        && curve.GetPointAtParameter((curve.StartParam + curve.EndParam)/2).DistanceTo((curve.StartPoint + (curve.EndPoint-curve.StartPoint)*0.5))<0.000001;
                    record.CurveStatus = "原生曲线采样已读取；是否为墙定位线及直线须核验";
                }
                catch (Exception exception) { record.CurveStatus = "曲线不可读：" + exception.Message; }
            }
            CadBuildingProbeRules.DeriveWall(record);
            if(curtain) { ReadOpeningLabel(entity,record);CadBuildingProbeRules.DeriveCurtainWall(record,1); }
        }

        private static CadOpeningPlacement PickOpeningPlacement(Document document, System.Windows.Window window,
            CadOpeningRegistrationRow row, CadFloorRegistrationContext floor)
        {
            if (!row.Width.HasValue) throw new InvalidOperationException("请先核对洞口宽度。");
            var editor = document.Editor;
            using (editor.StartUserInteraction(window))
            {
                var ucs = editor.CurrentUserCoordinateSystem;
                if (!ucs.CoordinateSystem3d.Zaxis.IsParallelTo(Vector3d.ZAxis))
                    throw new InvalidOperationException("请使用与世界 XY 平行的 UCS。");
                var picked = editor.GetEntity("\n选择洞口 " + (row.Code??"") + " 所在的天正直墙：");
                if (picked.Status != PromptStatus.OK) return null;
                var handle = picked.ObjectId.Handle.ToString();
                if (!floor.WallCandidates.Any(w => string.Equals(w.Handle, handle, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("请选择本次框选范围内的天正墙。");
                Point3d nativeStart, nativeEnd;
                using (var tx = document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    var host = tx.GetObject(picked.ObjectId, OpenMode.ForRead) as Curve;
                    if (host == null) throw new InvalidOperationException("宿主墙没有可核验的曲线接口。");
                    nativeStart = host.StartPoint; nativeEnd = host.EndPoint;
                    // T20 V9 implements distance/parameter queries but GetGeCurve
                    // returns eNotImplementedYet. Compare actual arc length with
                    // chord, rather than inferring a line from sparse samples.
                    var length = Math.Abs(host.GetDistanceAtParameter(host.EndParam) - host.GetDistanceAtParameter(host.StartParam));
                    var chord = nativeStart.DistanceTo(nativeEnd);
                    var scale = floor.Alignment.MillimetresPerCadUnit;
                    if (double.IsNaN(length) || double.IsInfinity(length) || chord * scale < 1
                        || Math.Abs(length-chord) * scale > 0.01 || Math.Abs(nativeStart.Z-nativeEnd.Z) * scale > 0.01)
                        throw new InvalidOperationException("曲墙、倾斜墙或无效定位线尚未适配，请选择水平直墙。");
                }
                var p = editor.GetPoint("\n在这条定位线上拾取洞口第一端：");
                if (p.Status != PromptStatus.OK) return null;
                var q = editor.GetPoint(new PromptPointOptions("\n在定位线上拾取洞口另一端：") { BasePoint = p.Value, UseBasePoint = true });
                if (q.Status != PromptStatus.OK) return null;
                Func<Point3d, PointModel> world = point => { var value = point.TransformBy(ucs); return new PointModel(value.X, value.Y); };
                return CadOpeningPlacement.Create(floor, handle, new PointModel(nativeStart.X,nativeStart.Y),
                    new PointModel(nativeEnd.X,nativeEnd.Y), world(p.Value), world(q.Value), row.Width.Value);
            }
        }

        private static CadProbePoint Point(Point3d point)
        {
            if (double.IsNaN(point.X) || double.IsInfinity(point.X)
                || double.IsNaN(point.Y) || double.IsInfinity(point.Y)
                || double.IsNaN(point.Z) || double.IsInfinity(point.Z))
                throw new InvalidOperationException("曲线/范围含非有限坐标");
            return new CadProbePoint { X = point.X, Y = point.Y, Z = point.Z };
        }

        private static string SystemVariable(string name)
        {
            try { return Convert.ToString(Application.GetSystemVariable(name), CultureInfo.InvariantCulture); }
            catch { return "未知"; }
        }

        private static int? Dbmod()
        {
            int result;
            return int.TryParse(SystemVariable("DBMOD"), out result) ? (int?)result : null;
        }

        private static string EnvironmentDescription()
        {
            // Tianzheng often uses native modules: absence of managed assemblies
            // cannot prove the host is missing. This is evidence, not detection.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName())
                // Match product prefixes; "BatchPdfPublisher" contains "tch"
                // and must not appear as evidence of a Tianzheng module.
                .Where(a => a.Name.StartsWith("TARCH", StringComparison.OrdinalIgnoreCase)
                    || a.Name.StartsWith("TCH", StringComparison.OrdinalIgnoreCase)
                    || a.Name.StartsWith("TIANZHENG", StringComparison.OrdinalIgnoreCase)
                    || a.Name.StartsWith("T20", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Name + " " + a.Version).ToArray();
            return assemblies.Length == 0 ? "未找到托管天正模块；不代表未加载。请在核验文件旁注明实际天正版本。"
                : "托管模块线索（实际天正版本仍需确认）：" + string.Join("; ", assemblies);
        }
    }
}
