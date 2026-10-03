using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Runtime.Serialization.Json;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class CadFloorOpeningItem : INotifyPropertyChanged
    {
        public string SourceHandle { get; set; }
        private string _code, _kind;
        private double? _width, _height;
        public string Code { get => _code; set { _code=value; NotifyDefaults("Code"); } }
        public string Kind { get => _kind; set { _kind=value; NotifyDefaults("Kind"); } }
        public double? Width { get => _width; set { _width=value; NotifyDefaults("Width"); } }
        public double? Height { get => _height; set { _height=value; NotifyDefaults("Height"); } }
        public double? Sill { get; set; }
        public bool Include { get; set; } = true;
        public string CodeSource { get; set; }
        public CadOpeningPlacement Placement { get; set; }
        public string ModelKind => CadOpeningDefaults.Kind(Code,Kind);
        public string ModelCode => CadOpeningDefaults.Code(this);
        public string DefaultDescription => "默认" + ModelKind + (CadOpeningDefaults.HasCode(Code) ? "" : " · 自动编号 " + ModelCode);
        public string PlacementStatus => Placement == null ? "待补充位置" : "位置已确定";
        public event PropertyChangedEventHandler PropertyChanged;
        private void NotifyDefaults(string name)
        {
            foreach(var property in new[] { name,"ModelKind","ModelCode","DefaultDescription" })
                PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(property));
        }
    }

    public static class CadOpeningDefaults
    {
        public static bool HasCode(string code)
        {
            var value=(code??"").Trim();
            return value.Length>0 && value!="—" && value!="-" && value!="待确认" && value!="未读到";
        }
        public static string Kind(string code,string kind)
        {
            if(kind=="门连窗")return "门联窗";
            if(kind=="门" || kind=="窗" || kind=="门联窗" || kind=="洞口") return kind;
            var value=Regex.Replace((code??"").Trim().ToUpperInvariant(),@"\s+","");
            if(value.StartsWith("MLC") || value.StartsWith("门联窗") || value.StartsWith("门连窗"))return "门联窗";
            if(new[] { "M","FM","FHM","RFM","TLM","BM","门" }.Any(prefix=>value.StartsWith(prefix,StringComparison.Ordinal)))return "门";
            // A generic window is a usable default; detailed types belong to MCLM.
            return "窗";
        }
        public static string KindFromLayer(string layer)
        {
            var value=(layer??"").ToUpperInvariant();
            if(value.Contains("WINDOW") || value.Contains("窗")) return "窗";
            return value.Contains("DOOR") || value.Contains("门") ? "门" : null;
        }
        public static string Code(CadFloorOpeningItem item)
        {
            if(HasCode(item.Code))return item.Code.Trim();
            var prefix=item.ModelKind=="门" ? "M" : item.ModelKind=="门联窗" ? "MLC" : item.ModelKind=="洞口" ? "DK" : "C";
            return SizeCode(prefix,item.Width??0,item.Height??0);
        }
        public static string SizeCode(string prefix,double width,double height) => OpeningConstruction.SizeCode(prefix,width,height);
        public static string AlphabeticSuffix(int index) => OpeningConstruction.AlphabeticSuffix(index);
        public static double Sill(CadFloorOpeningItem item,double availableHeight)
        {
            if(item.Sill.HasValue)return item.Sill.Value;
            var nominal=item.ModelKind=="窗" ? 900d : 0d;
            return Math.Max(0,Math.Min(nominal,availableHeight-item.Height.GetValueOrDefault()));
        }
    }

    public sealed class CadFloorPlanCapture
    {
        public CadFloorRegistrationContext Floor { get; set; }
        public CadBuildingProbeDocument Probe { get; set; }
        public List<CadFloorOpeningItem> Openings { get; set; } = new List<CadFloorOpeningItem>();
        public List<string> ExcludedWallHandles { get; set; } = new List<string>();
        public bool UseStoreyHeightForMissingWalls { get; set; }

        public static CadFloorPlanCapture FromProbe(CadFloorRegistrationContext floor, CadBuildingProbeDocument probe)
        {
            floor.ValidateCapture();
            var capture = new CadFloorPlanCapture { Floor = floor, Probe = probe };
            foreach (var row in CadOpeningRegistration.FromProbe(probe))
            {
                var source = probe.Entities.Single(e => e.Handle == row.SourceHandle);
                var code = source.Fields.FirstOrDefault(f => f.Name == "OpeningCode" && (f.Status==null || f.Status.StartsWith("已读取",StringComparison.Ordinal)) && CadOpeningDefaults.HasCode(f.Text))?.Text;
                var item = new CadFloorOpeningItem { SourceHandle = row.SourceHandle, Code = code,
                    Kind = CadOpeningDefaults.HasCode(code) ? null : CadOpeningDefaults.KindFromLayer(source.Layer),
                    Width = row.Width * floor.Alignment.MillimetresPerCadUnit,
                    Height = row.Height * floor.Alignment.MillimetresPerCadUnit,
                    CodeSource = code == null ? "自动按洞口尺寸编号，可在门窗立面修改" : "本洞口自身标注" };
                if (item.Width.HasValue) item.Placement = CadOpeningJambPlacement.Find(floor,source,item.Width.Value);
                if(!CadOpeningDefaults.HasCode(item.Code) && item.Placement?.Source?.StartsWith("本洞口原生门扇",StringComparison.Ordinal)==true)item.Kind="门";
                capture.Openings.Add(item);
            }
            return capture;
        }

        public void ValidateSchedule()
        {
            Floor.ValidateCapture();
            if (Probe == null || Openings == null || Openings.Any(x=>x == null || string.IsNullOrWhiteSpace(x.SourceHandle))
                || Openings.Select(x=>x.SourceHandle).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Openings.Count)
                throw new InvalidDataException("楼层登记缺少来源记录或洞口身份重复。");
            foreach (var item in Openings.Where(x => x.Include))
            {
                if (!Positive(item.Width) || !Positive(item.Height)) throw new InvalidDataException(item.Code + " 洞口尺寸无效。");
                if (item.Sill.HasValue && (double.IsNaN(item.Sill.Value) || double.IsInfinity(item.Sill.Value) || item.Sill.Value < 0))
                    throw new InvalidDataException(item.Code + " 离地高度无效，未知时可暂存登记，生成模型前须核对填写。");
            }
        }
        private static bool Positive(double? value) => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value > 0;
    }

    public sealed class CadRegisteredOpeningRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public CadFloorPlanCapture Capture { get; set; }
        public List<CadFloorOpeningItem> Items { get; set; }
        public string FloorName { get; set; }
        private string _code, _baseCode;
        public string Code { get => _code; set { _baseCode=value;SetResolvedCode(value); } }
        public string BaseCode => _baseCode;
        public string Kind { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double? Sill { get; set; }
        public int Quantity { get; set; }
        private readonly Dictionary<string,CadOpeningPlacement> _placements=new Dictionary<string,CadOpeningPlacement>();
        public string PlacementStatus => Items.All(o=>GetPlacement(o.SourceHandle)!=null) ? "位置已确定" : "待补充位置 "+Items.Count(o=>GetPlacement(o.SourceHandle)==null)+" 樘";
        public CadOpeningPlacement GetPlacement(string handle) { CadOpeningPlacement value;return _placements.TryGetValue(handle,out value) ? value : null; }
        public void SetPlacement(string handle,CadOpeningPlacement value) { _placements[handle]=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs("PlacementStatus")); }
        public void SetResolvedCode(string value) { _code=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs("Code")); }
        public static void RefreshCodes(IList<CadRegisteredOpeningRow> rows)
        {
            var codes=CadFloorPlanRegistry.ResolveCodes(rows,r=>CadOpeningDefaults.HasCode(r.BaseCode) ? r.BaseCode.Trim()
                : CadOpeningDefaults.Code(new CadFloorOpeningItem { Kind=r.Kind,Width=r.Width,Height=r.Height }),r=>r.Width,r=>r.Height);
            foreach(var row in rows)row.SetResolvedCode(codes[row]);
        }
    }

    public sealed class CadFloorPlanRegistry
    {
        public int SchemaVersion { get; set; } = 1;
        public string ModelPath { get; set; }
        // Nullable preserves the 100 mm default when reading older registration files.
        public double? SlabThickness { get; set; }
        public double ResolvedSlabThickness => SlabThickness ?? 100d;
        public List<CadFloorPlanCapture> Floors { get; set; } = new List<CadFloorPlanCapture>();
        public List<CadFloorRegistrationContext> Datums { get; set; } = new List<CadFloorRegistrationContext>();
        public void ReconcileStoreys(BuildingModelDocument model)
        {
            var floors = model.Storeys.Where(s => string.IsNullOrWhiteSpace(s.TemplateStoreyId))
                .ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            Floors.RemoveAll(c => !floors.ContainsKey(c.Floor.Storey.Id));
            Datums?.RemoveAll(c => !floors.ContainsKey(c.Storey.Id));
            foreach (var context in Floors.Select(c => c.Floor).Concat(Datums ?? new List<CadFloorRegistrationContext>()))
            {
                var floor = floors[context.Storey.Id];
                context.Storey = new StoreyModel { Id = floor.Id, Name = floor.Name, Kind = floor.Kind,
                    Height = floor.Height, Elevation = floor.Elevation };
            }
        }
        public Dictionary<CadFloorOpeningItem,string> ResolveCodes()
        {
            var items=Floors.SelectMany(f=>f.Openings).Where(o=>o.Include).ToList();
            return ResolveCodes(items,o=>o.ModelCode,o=>o.Width.GetValueOrDefault(),o=>o.Height.GetValueOrDefault());
        }
        public static Dictionary<T,string> ResolveCodes<T>(IEnumerable<T> items,Func<T,string> baseCode,Func<T,double> width,Func<T,double> height)
        {
            var all=items.ToList(); var result=new Dictionary<T,string>();
            var reserved=new HashSet<string>(all.Select(baseCode),StringComparer.OrdinalIgnoreCase);
            foreach(var group in all.GroupBy(baseCode,StringComparer.OrdinalIgnoreCase)) {
                var variants=group.GroupBy(o=>new { Width=Math.Round(width(o),2),Height=Math.Round(height(o),2) })
                    .OrderBy(g=>g.Key.Width).ThenBy(g=>g.Key.Height).ToList(); var index=0;
                foreach(var variant in variants) {
                    var code=group.Key;
                    if(variants.Count>1) { do { code=group.Key+CadOpeningDefaults.AlphabeticSuffix(index++); } while(reserved.Contains(code)); reserved.Add(code); }
                    foreach(var item in variant)result[item]=code;
                }
            }
            return result;
        }
        public List<CadRegisteredOpeningRow> OpeningRows(BuildingModelDocument model)
        {
            var codes=ResolveCodes(); var rows=new List<CadRegisteredOpeningRow>();
            foreach(var capture in Floors) {
                var actualFloors=model.Storeys.Where(f=>f.Id==capture.Floor.Storey.Id || f.TemplateStoreyId==capture.Floor.Storey.Id).ToList();
                if(actualFloors.Count==0)continue;
                foreach(var group in capture.Openings.Where(o=>o.Include).GroupBy(o=>new { Code=codes[o],o.Width,o.Height,o.Sill,Kind=o.ModelKind })) {
                    var first=group.First(); var row=new CadRegisteredOpeningRow { Capture=capture,Items=group.ToList(),
                        FloorName=string.Join("、",actualFloors.Select(f=>f.Name)),Width=first.Width.GetValueOrDefault(),Height=first.Height.GetValueOrDefault(),
                        Sill=first.Sill,Kind=first.Kind,Quantity=group.Count()*actualFloors.Count };
                    row.Code=first.ModelCode; row.SetResolvedCode(codes[first]);
                    foreach(var item in row.Items) {
                        var placement=item.Placement ?? CadOpeningJambPlacement.Find(capture.Floor,capture.Probe.Entities.FirstOrDefault(e=>e.Handle==item.SourceHandle),item.Width.GetValueOrDefault());
                        if(placement!=null)row.SetPlacement(item.SourceHandle,placement);
                    }
                    rows.Add(row);
                }
            }
            return rows;
        }
        public CadFloorPlanRegistry Clone()
        {
            using(var memory=new MemoryStream()) {
                var serializer=new DataContractJsonSerializer(typeof(CadFloorPlanRegistry));serializer.WriteObject(memory,this);memory.Position=0;
                return (CadFloorPlanRegistry)serializer.ReadObject(memory);
            }
        }
        public void ReplaceFrom(CadFloorPlanRegistry snapshot,long expectedFileStamp)
        {
            var file=FilePath(ModelPath);
            if(!string.Equals(ModelPath,snapshot.ModelPath,StringComparison.OrdinalIgnoreCase)
                || (File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0)!=expectedFileStamp)
                throw new InvalidDataException("楼层登记已更新，请重新打开后保存。");
            foreach(var capture in snapshot.Floors)capture.ValidateSchedule();
            Write(snapshot);Floors=snapshot.Floors;Datums=snapshot.Datums;SlabThickness=snapshot.SlabThickness;
        }
        public void SaveOpeningRows(IList<CadRegisteredOpeningRow> rows,long expectedFileStamp,bool persist=true)
        {
            var file=FilePath(ModelPath);
            if((File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0)!=expectedFileStamp)
                throw new InvalidDataException("楼层登记已更新，请重新打开门窗表后保存。");
            var snapshot=Clone();
            foreach(var row in rows) {
                var capture=snapshot.Find(row.Capture.Floor.Storey.Id);
                if(capture==null || row.Items.Count==0)throw new InvalidDataException("门窗来源已改变，请重新打开门窗表。");
                foreach(var item in row.Items) {
                    var target=capture.Openings.Single(o=>o.SourceHandle==item.SourceHandle);
                    var widthChanged=Math.Abs(target.Width.GetValueOrDefault()-row.Width)>.01;
                    target.Code=CadOpeningDefaults.HasCode(row.BaseCode) ? row.BaseCode.Trim() : null;
                    target.Kind=row.Kind;target.Width=row.Width;target.Height=row.Height;target.Sill=row.Sill;
                    target.Placement=row.GetPlacement(item.SourceHandle);
                    if(widthChanged && target.Placement!=null)
                        try { target.Placement=target.Placement.Revalidate(capture.Floor,row.Width); }
                        catch(InvalidDataException) { target.Placement=null; }
                    target.CodeSource="门窗表编号与尺寸";
                }
                capture.ValidateSchedule();
            }
            if(persist)ReplaceFrom(snapshot,expectedFileStamp);
            else { Floors=snapshot.Floors;Datums=snapshot.Datums; }
        }
        public static string FilePath(string modelPath) => modelPath + ".cad-floors.json";
        public static CadFloorPlanRegistry Load(string modelPath)
        {
            var path = FilePath(modelPath);
            if (!File.Exists(path)) return new CadFloorPlanRegistry { ModelPath = Path.GetFullPath(modelPath) };
            using (var stream = File.OpenRead(path))
            {
                var result = (CadFloorPlanRegistry)new DataContractJsonSerializer(typeof(CadFloorPlanRegistry)).ReadObject(stream);
                if (result == null || result.Floors == null || !string.Equals(result.ModelPath, Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("楼层登记文件与当前建筑模型不匹配。");
                return result;
            }
        }
        public CadFloorPlanCapture Find(string storeyId) => Floors.FirstOrDefault(f => string.Equals(f.Floor.Storey.Id, storeyId, StringComparison.OrdinalIgnoreCase));
        public CadFloorRegistrationContext FindDatum(string storeyId) => (Datums ?? new List<CadFloorRegistrationContext>())
            .FirstOrDefault(f => string.Equals(f.Storey.Id, storeyId, StringComparison.OrdinalIgnoreCase)) ?? Find(storeyId)?.Floor;
        public void SaveDatum(CadFloorRegistrationContext context)
        {
            context.SetSourceDatum(context.Alignment.CadBase, context.DirectionPoint);
            ValidateModelDatum(context);
            var next = (Datums ?? new List<CadFloorRegistrationContext>()).Where(f => !string.Equals(f.Storey.Id, context.Storey.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            next.Add(context);
            Write(new CadFloorPlanRegistry { ModelPath = ModelPath, Floors = Floors, Datums = next, SlabThickness=SlabThickness });
            Datums = next;
        }
        private void ValidateModelDatum(CadFloorRegistrationContext context)
        {
            if (!string.Equals(ModelPath, context.ModelPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("登记目标模型已改变。");
            if (Floors.Any(f => !string.Equals(f.Floor.Storey.Id, context.Storey.Id, StringComparison.OrdinalIgnoreCase)
                && (f.Floor.Alignment.ModelBase.X != context.Alignment.ModelBase.X || f.Floor.Alignment.ModelBase.Y != context.Alignment.ModelBase.Y)))
                throw new InvalidDataException("各层必须使用同一模型基点坐标，请沿用已登记楼层的模型基点。");
        }
        public List<DoorWindowScheduleItem> BuildSchedule(BuildingModelDocument model)
        {
            var codes=ResolveCodes();
            var instances = model.Storeys.SelectMany(storey =>
            {
                var sourceId = string.IsNullOrWhiteSpace(storey.TemplateStoreyId) ? storey.Id : storey.TemplateStoreyId;
                var capture = Find(sourceId);
                return (capture?.Openings ?? new List<CadFloorOpeningItem>()).Where(x => x.Include)
                    .Select(x => new { Storey = storey, Opening = x });
            });
            var rows = new List<DoorWindowScheduleItem>();
            foreach (var group in instances.GroupBy(x => new { Code = codes[x.Opening].ToUpperInvariant(),
                x.Opening.Width, x.Opening.Height, Kind=x.Opening.ModelKind, x.Opening.Sill }))
            {
                var opening = group.First().Opening;
                var row = new DoorWindowScheduleItem { Code = codes[opening], CadRegistrationCode = opening.ModelCode, CadRegistrationSill = opening.Sill, Width = opening.Width.Value,
                    Height = opening.Height.Value, SourceCategory = opening.ModelKind,
                    ElevationType = opening.ModelKind == "门" ? "普通门" : opening.ModelKind == "窗" ? "普通窗" : opening.ModelKind,
                    Quantity = group.Count(), SillHeight = opening.Sill ?? 0, SillHeightSuppressed = !opening.Sill.HasValue,
                    SillHeightFromCadRegistration = opening.Sill.HasValue,
                    DivisionPreset = "未设置", OpeningMode = "未设置", SourceNote = "CAD 楼层登记 · 天正洞口"+(CadOpeningDefaults.HasCode(opening.Code) ? "" : " · 自动编号") };
                foreach (var floor in group.GroupBy(x => x.Storey.Id))
                    row.FloorQuantities.Add(new DoorWindowFloorQuantity { FloorName = floor.First().Storey.Name, PerFloorQuantity = floor.Count(), FloorCount = 1 });
                rows.Add(row);
            }
            return rows;
        }
        public void UpdateScheduleSills(BuildingModelDocument model, IEnumerable<DoorWindowScheduleItem> items)
        {
            var rows = items.Where(i=>!string.IsNullOrWhiteSpace(i.CadRegistrationCode)).ToList();
            var updated = new HashSet<DoorWindowScheduleItem>();
            var snapshot = Load(ModelPath);
            foreach (var capture in snapshot.Floors)
            {
                var name = model.FindStorey(capture.Floor.Storey.Id)?.Name;
                foreach (var opening in capture.Openings.Where(x=>x.Include))
                {
                    var matches = rows.Where(r=>string.Equals(r.CadRegistrationCode,opening.ModelCode,StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(r.Width-opening.Width.Value)<0.01 && Math.Abs(r.Height-opening.Height.Value)<0.01
                        && r.CadRegistrationSill == opening.Sill && r.SourceCategory == opening.ModelKind
                        && r.FloorQuantities.Any(q=>q.FloorName == name && q.PerFloorQuantity > 0)).ToList();
                    if (matches.Count != 1) continue;
                    var row = matches[0]; var sill = row.SillHeightSuppressed ? (double?)null : row.SillHeight;
                    if (sill == opening.Sill) continue;
                    opening.Sill = sill; updated.Add(row);
                }
                capture.ValidateSchedule();
            }
            if (updated.Count == 0) return;
            Write(snapshot); Floors = snapshot.Floors; Datums = snapshot.Datums;
            foreach (var row in updated) row.CadRegistrationSill = row.SillHeightSuppressed ? (double?)null : row.SillHeight;
        }
        public void SaveFloor(CadFloorPlanCapture capture)
        {
            capture.ValidateSchedule();
            ValidateModelDatum(capture.Floor);
            var next = Floors.Where(f => !string.Equals(f.Floor.Storey.Id, capture.Floor.Storey.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Any(f => string.Equals(f.Probe.DrawingFingerprint, capture.Probe.DrawingFingerprint, StringComparison.OrdinalIgnoreCase)
                && f.Openings.Where(x => x.Include).Any(x => capture.Openings.Any(y => y.Include && string.Equals(x.SourceHandle,y.SourceHandle,StringComparison.OrdinalIgnoreCase)))))
                throw new InvalidDataException("框选洞口已登记到其他独立楼层，请核对范围；重复平面请在建筑模型设置标准层引用。");
            next.Add(capture);
            var datums = (Datums ?? new List<CadFloorRegistrationContext>()).Where(f => !string.Equals(f.Storey.Id,capture.Floor.Storey.Id,StringComparison.OrdinalIgnoreCase)).ToList();
            datums.Add(capture.Floor);
            Write(new CadFloorPlanRegistry { ModelPath = ModelPath, Floors = next, Datums = datums, SlabThickness=SlabThickness });
            Floors = next; Datums = datums;
        }
        private void Write(CadFloorPlanRegistry snapshot)
        {
            var file = FilePath(ModelPath); var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary)) new DataContractJsonSerializer(typeof(CadFloorPlanRegistry)).WriteObject(stream, snapshot);
                if (File.Exists(file)) File.Replace(temporary, file, file + ".bak"); else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
