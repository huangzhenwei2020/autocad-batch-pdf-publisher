using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace BatchPdfPublisher.Services
{
    public static class DraftingStandardService
    {
        public const string FrameLayer = "WL-图框", CatalogLayer = "WL-目录", ArchitectureOutlineLayer = "WL-建筑-轮廓", ArchitectureFineLayer = "WL-建筑-细线", ArchitectureStructureLayer = "WL-建筑-结构", ArchitectureHiddenLayer = "WL-建筑-隐藏", ArchitectureHatchLayer = "WL-建筑-填充", AnnotationTextLayer = "WL-注释-文字", AnnotationDimensionLayer = "WL-注释-标注";
        public const string BodyTextStyle = "WL-文字-正文", TitleTextStyle = "WL-文字-标题", AnnotationTextStyle = "WL-文字-标注", DimensionStyle50 = "WL-标注-1_50";

        public static string SettingsPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WanluoArchitectureTools", "drafting-standard.ini"); } }
        public static string GetLayerName(string key) { return LoadProfile().Layer(key).Name; }
        public static string GetTextStyleName(string key) { return LoadProfile().Text(key).Name; }

        public static DraftingStandardProfile LoadProfile()
        {
            var profile = DraftingStandardProfile.CreateDefault();
            if (!File.Exists(SettingsPath)) return profile;
            try
            {
                var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(SettingsPath)) { var i = line.IndexOf('='); if (i > 0 && !line.TrimStart().StartsWith("#")) data[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim(); }
                foreach (var x in profile.Layers) { x.Name = Read(data, "Layer." + x.Key + ".Name", x.Name); x.ColorIndex = ReadShort(data, "Layer." + x.Key + ".Color", x.ColorIndex); x.LineWeight = ReadInt(data, "Layer." + x.Key + ".LineWeight", x.LineWeight); x.LineType = Read(data, "Layer." + x.Key + ".LineType", x.LineType); }
                foreach (var x in profile.TextStyles) { x.Name = Read(data, "Text." + x.Key + ".Name", x.Name); x.FontFile = Read(data, "Text." + x.Key + ".Font", x.FontFile); x.WidthFactor = ReadDouble(data, "Text." + x.Key + ".Width", x.WidthFactor); }
                profile.DimensionScales = ParseScales(Read(data, "Dimension.Scales", "1,20,50,100,200"));
                profile.DimensionTextHeight = ReadDouble(data, "Dimension.TextHeight", profile.DimensionTextHeight); profile.DimensionArrowSize = ReadDouble(data, "Dimension.ArrowSize", profile.DimensionArrowSize);
                profile.UpdateExisting = Read(data, "General.UpdateExisting", "0") == "1";
            }
            catch { return DraftingStandardProfile.CreateDefault(); }
            return profile;
        }

        public static void SaveProfile(DraftingStandardProfile profile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            var lines = new List<string> { "# 万落建筑工具制图标准 v1", "General.UpdateExisting=" + (profile.UpdateExisting ? "1" : "0") };
            foreach (var x in profile.Layers) { lines.Add("Layer." + x.Key + ".Name=" + x.Name); lines.Add("Layer." + x.Key + ".Color=" + x.ColorIndex); lines.Add("Layer." + x.Key + ".LineWeight=" + x.LineWeight); lines.Add("Layer." + x.Key + ".LineType=" + x.LineType); }
            foreach (var x in profile.TextStyles) { lines.Add("Text." + x.Key + ".Name=" + x.Name); lines.Add("Text." + x.Key + ".Font=" + x.FontFile); lines.Add("Text." + x.Key + ".Width=" + x.WidthFactor.ToString(CultureInfo.InvariantCulture)); }
            lines.Add("Dimension.Scales=" + string.Join(",", profile.DimensionScales.Select(x => x.ToString(CultureInfo.InvariantCulture)))); lines.Add("Dimension.TextHeight=" + profile.DimensionTextHeight.ToString(CultureInfo.InvariantCulture)); lines.Add("Dimension.ArrowSize=" + profile.DimensionArrowSize.ToString(CultureInfo.InvariantCulture));
            File.WriteAllLines(SettingsPath, lines.ToArray(), System.Text.Encoding.UTF8);
        }

        public static DraftingStandardResources EnsureAll(Database db, Transaction tr) { var p = LoadProfile(); return EnsureAll(db, tr, p, p.UpdateExisting); }
        public static DraftingStandardResources EnsureAll(Database db, Transaction tr, DraftingStandardProfile profile, bool updateExisting)
        {
            if (db == null) throw new ArgumentNullException("db"); if (tr == null) throw new ArgumentNullException("tr"); profile = profile ?? DraftingStandardProfile.CreateDefault();
            var result = new DraftingStandardResources { Profile = profile };
            foreach (var x in profile.Layers) { var lt = string.Equals(x.LineType, "Continuous", StringComparison.OrdinalIgnoreCase) ? ObjectId.Null : EnsureLineType(db, tr, x.LineType); result.LayerIds[x.Key] = EnsureLayer(db, tr, x.Name, x.ColorIndex, (LineWeight)x.LineWeight, lt, updateExisting); }
            foreach (var x in profile.TextStyles) result.TextStyleIds[x.Key] = EnsureTextStyle(db, tr, x.Name, x.FontFile, x.WidthFactor, updateExisting);
            foreach (var scale in profile.DimensionScales) result.DimensionStyleIds[scale] = EnsureDimStyle(db, tr, profile.DimensionStyleName(scale), scale, result.TextStyleIds[DraftingStandardProfile.AnnotationTextKey], profile.DimensionTextHeight, profile.DimensionArrowSize, updateExisting);
            return result;
        }

        public static ObjectId ResolveTextStyle(Database db, Transaction tr, string requestedName, bool title)
        {
            var r = EnsureAll(db, tr); if (string.IsNullOrWhiteSpace(requestedName)) return title ? r.TitleTextStyleId : r.BodyTextStyleId;
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead); if (table.Has(requestedName)) return table[requestedName];
            var file = string.Equals(requestedName, "黑体", StringComparison.OrdinalIgnoreCase) ? "simhei.ttf" : string.Equals(requestedName, "宋体", StringComparison.OrdinalIgnoreCase) ? "simsun.ttc" : string.Equals(requestedName, "微软雅黑", StringComparison.OrdinalIgnoreCase) ? "msyh.ttc" : string.Equals(requestedName, "Arial", StringComparison.OrdinalIgnoreCase) ? "arial.ttf" : null;
            return file == null ? (title ? r.TitleTextStyleId : r.BodyTextStyleId) : EnsureTextStyle(db, tr, "WL-文字-" + requestedName, file, 1, false);
        }

        private static ObjectId EnsureLayer(Database db, Transaction tr, string name, short color, LineWeight weight, ObjectId lt, bool update)
        {
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead); if (table.Has(name)) { if (update) { var x = (LayerTableRecord)tr.GetObject(table[name], OpenMode.ForWrite); x.Color = Color.FromColorIndex(ColorMethod.ByAci, color); x.LineWeight = weight; if (!lt.IsNull) x.LinetypeObjectId = lt; } return table[name]; }
            table.UpgradeOpen(); var record = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, color), LineWeight = weight }; if (!lt.IsNull) record.LinetypeObjectId = lt; var id = table.Add(record); tr.AddNewlyCreatedDBObject(record, true); return id;
        }
        private static ObjectId EnsureLineType(Database db, Transaction tr, string name) { var t = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead); if (!t.Has(name)) { try { db.LoadLineTypeFile(name, "acadiso.lin"); } catch { return ObjectId.Null; } t = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead); } return t.Has(name) ? t[name] : ObjectId.Null; }
        private static ObjectId EnsureTextStyle(Database db, Transaction tr, string name, string font, double width, bool update)
        {
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead); if (table.Has(name)) { if (update) { var x = (TextStyleTableRecord)tr.GetObject(table[name], OpenMode.ForWrite); x.FileName = font; x.XScale = width; x.TextSize = 0; } return table[name]; }
            table.UpgradeOpen(); var record = new TextStyleTableRecord { Name = name, FileName = font, TextSize = 0, XScale = width }; var id = table.Add(record); tr.AddNewlyCreatedDBObject(record, true); return id;
        }
        private static ObjectId EnsureDimStyle(Database db, Transaction tr, string name, int scale, ObjectId textStyle, double textHeight, double arrow, bool update)
        {
            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead); DimStyleTableRecord x; if (table.Has(name)) { if (!update) return table[name]; x = (DimStyleTableRecord)tr.GetObject(table[name], OpenMode.ForWrite); } else { table.UpgradeOpen(); x = new DimStyleTableRecord { Name = name }; var id = table.Add(x); tr.AddNewlyCreatedDBObject(x, true); }
            x.Dimscale = scale; x.Dimtxt = textHeight; x.Dimasz = arrow; x.Dimexe = textHeight / 2; x.Dimexo = textHeight / 4; x.Dimgap = textHeight / 4; x.Dimdec = 0; x.Dimtxsty = textStyle; return x.ObjectId;
        }
        private static string Read(IDictionary<string, string> d, string k, string v) { string x; return d.TryGetValue(k, out x) && !string.IsNullOrWhiteSpace(x) ? x : v; }
        private static int ReadInt(IDictionary<string, string> d, string k, int v) { int x; return int.TryParse(Read(d, k, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ? x : v; }
        private static short ReadShort(IDictionary<string, string> d, string k, short v) { short x; return short.TryParse(Read(d, k, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ? x : v; }
        private static double ReadDouble(IDictionary<string, string> d, string k, double v) { double x; return double.TryParse(Read(d, k, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : v; }
        public static List<int> ParseScales(string value) { var r = (value ?? "").Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => { int n; return int.TryParse(x.Trim().Replace("1:", "").Replace("1：", ""), out n) ? n : 0; }).Where(x => x > 0).Distinct().OrderBy(x => x).ToList(); return r.Count == 0 ? new List<int> { 50 } : r; }
    }

    public sealed class DraftingStandardProfile
    {
        public const string FrameKey = "Frame", CatalogKey = "Catalog", OutlineKey = "Outline", FineKey = "Fine", StructureKey = "Structure", HiddenKey = "Hidden", HatchKey = "Hatch", AnnotationTextLayerKey = "AnnotationTextLayer", AnnotationDimensionLayerKey = "AnnotationDimensionLayer", BodyTextKey = "Body", TitleTextKey = "Title", AnnotationTextKey = "Annotation";
        public List<DraftingLayerSetting> Layers = new List<DraftingLayerSetting>(); public List<DraftingTextStyleSetting> TextStyles = new List<DraftingTextStyleSetting>(); public List<int> DimensionScales = new List<int>(); public double DimensionTextHeight = 2.5, DimensionArrowSize = 2.5; public bool UpdateExisting;
        public static DraftingStandardProfile CreateDefault() { var p = new DraftingStandardProfile(); p.Layers.AddRange(new[] { L(FrameKey,"图框",DraftingStandardService.FrameLayer,7,30,"Continuous"), L(CatalogKey,"图纸目录",DraftingStandardService.CatalogLayer,7,18,"Continuous"), L(OutlineKey,"建筑轮廓",DraftingStandardService.ArchitectureOutlineLayer,7,30,"Continuous"), L(FineKey,"建筑细线",DraftingStandardService.ArchitectureFineLayer,2,13,"Continuous"), L(StructureKey,"建筑结构",DraftingStandardService.ArchitectureStructureLayer,1,35,"Continuous"), L(HiddenKey,"建筑隐藏",DraftingStandardService.ArchitectureHiddenLayer,8,13,"HIDDEN"), L(HatchKey,"建筑填充",DraftingStandardService.ArchitectureHatchLayer,8,9,"Continuous"), L(AnnotationTextLayerKey,"注释文字",DraftingStandardService.AnnotationTextLayer,7,18,"Continuous"), L(AnnotationDimensionLayerKey,"注释标注",DraftingStandardService.AnnotationDimensionLayer,3,13,"Continuous") }); p.TextStyles.AddRange(new[] { T(BodyTextKey,"正文",DraftingStandardService.BodyTextStyle,"simsun.ttc",.7), T(TitleTextKey,"标题",DraftingStandardService.TitleTextStyle,"simhei.ttf",1), T(AnnotationTextKey,"标注",DraftingStandardService.AnnotationTextStyle,"simsun.ttc",.7) }); p.DimensionScales.AddRange(new[] { 1,20,50,100,200 }); return p; }
        private static DraftingLayerSetting L(string k,string purpose,string name,short color,int weight,string lt) { return new DraftingLayerSetting { Key=k,Purpose=purpose,Name=name,ColorIndex=color,LineWeight=weight,LineType=lt }; } private static DraftingTextStyleSetting T(string k,string purpose,string name,string font,double width) { return new DraftingTextStyleSetting { Key=k,Purpose=purpose,Name=name,FontFile=font,WidthFactor=width }; }
        public DraftingLayerSetting Layer(string key) { return Layers.First(x => x.Key == key); } public DraftingTextStyleSetting Text(string key) { return TextStyles.First(x => x.Key == key); } public string DimensionStyleName(int scale) { return "WL-标注-1_" + scale; }
    }
    public sealed class DraftingLayerSetting { public string Key, Purpose, Name, LineType; public short ColorIndex; public int LineWeight; }
    public sealed class DraftingTextStyleSetting { public string Key, Purpose, Name, FontFile; public double WidthFactor; }
    public sealed class DraftingStandardResources
    {
        public DraftingStandardProfile Profile; public readonly Dictionary<string,ObjectId> LayerIds = new Dictionary<string,ObjectId>(); public readonly Dictionary<string,ObjectId> TextStyleIds = new Dictionary<string,ObjectId>(); public readonly Dictionary<int,ObjectId> DimensionStyleIds = new Dictionary<int,ObjectId>();
        public ObjectId FrameLayerId { get { return LayerIds[DraftingStandardProfile.FrameKey]; } } public ObjectId CatalogLayerId { get { return LayerIds[DraftingStandardProfile.CatalogKey]; } } public ObjectId ArchitectureOutlineLayerId { get { return LayerIds[DraftingStandardProfile.OutlineKey]; } } public ObjectId ArchitectureFineLayerId { get { return LayerIds[DraftingStandardProfile.FineKey]; } } public ObjectId ArchitectureStructureLayerId { get { return LayerIds[DraftingStandardProfile.StructureKey]; } } public ObjectId ArchitectureHiddenLayerId { get { return LayerIds[DraftingStandardProfile.HiddenKey]; } } public ObjectId ArchitectureHatchLayerId { get { return LayerIds[DraftingStandardProfile.HatchKey]; } } public ObjectId AnnotationTextLayerId { get { return LayerIds[DraftingStandardProfile.AnnotationTextLayerKey]; } } public ObjectId AnnotationDimensionLayerId { get { return LayerIds[DraftingStandardProfile.AnnotationDimensionLayerKey]; } }
        public ObjectId BodyTextStyleId { get { return TextStyleIds[DraftingStandardProfile.BodyTextKey]; } } public ObjectId TitleTextStyleId { get { return TextStyleIds[DraftingStandardProfile.TitleTextKey]; } } public ObjectId AnnotationTextStyleId { get { return TextStyleIds[DraftingStandardProfile.AnnotationTextKey]; } } public ObjectId DimensionStyle50Id { get { ObjectId x; return DimensionStyleIds.TryGetValue(50,out x) ? x : ObjectId.Null; } }
    }
}
