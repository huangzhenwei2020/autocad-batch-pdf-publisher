using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace BatchPdfPublisher.Services
{
    public static class DraftingStandardService
    {
        public const string ArrowLibraryFileName = "WanLuoArrowSymbols.dwg";
        public static readonly string ArrowLibraryRelativePath = Path.Combine("Resources", "Blocks", ArrowLibraryFileName);
        public const string FrameLayer = "WL-图框", CatalogLayer = "WL-目录", ArchitectureOutlineLayer = "WL-建筑-轮廓", ArchitectureFineLayer = "WL-建筑-细线", ArchitectureStructureLayer = "WL-建筑-结构", ArchitectureHiddenLayer = "WL-建筑-隐藏", ArchitectureHatchLayer = "WL-建筑-填充", AnnotationTextLayer = "WL-注释-文字", AnnotationDimensionLayer = "WL-注释-标注";
        public const string BodyTextStyle = "WL-文字-正文", TitleTextStyle = "WL-文字-标题", AnnotationTextStyle = "WL-文字-标注", DimensionStyle50 = "WL-标注-1_50";

        public static string SettingsPath { get { return UserDataPaths.SettingsFile("drafting-standard.ini", Path.Combine("WanluoArchitectureTools", "drafting-standard.ini")); } }
        public static string GetLayerName(string key) { return LoadProfile().Layer(key).Name; }
        public static string GetTextStyleName(string key) { return LoadProfile().Text(key).Name; }
        public static string ArrowLibraryPath
        {
            get
            {
                var assemblyDirectory = Path.GetDirectoryName(typeof(DraftingStandardService).Assembly.Location);
                var organizedPath = Path.Combine(assemblyDirectory, ArrowLibraryRelativePath);
                // Portable packages keep program assemblies under CadApi\Rxx
                // and shared resources at the launcher root.
                var portablePath = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", ArrowLibraryRelativePath));
                var legacyPath = Path.Combine(assemblyDirectory, ArrowLibraryFileName);
                if (File.Exists(portablePath)) return portablePath;
                if (File.Exists(organizedPath)) return organizedPath;
                return File.Exists(legacyPath) ? legacyPath : organizedPath;
            }
        }

        public static List<string> GetArrowStyleChoices()
        {
            var result = new List<string> { "实心闭合", "空心闭合", "建筑斜线", "点" }; var path = ArrowLibraryPath; if (!File.Exists(path)) return result;
            try { using (var source = new Database(false, true)) { source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, ""); using (var tr = source.TransactionManager.StartOpenCloseTransaction()) { var table = (BlockTable)tr.GetObject(source.BlockTableId, OpenMode.ForRead); foreach (ObjectId id in table) { var block = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead); if (!block.IsAnonymous && !block.IsLayout && !block.Name.StartsWith("*", StringComparison.Ordinal)) result.Add("图块：" + block.Name); } } } } catch { }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static DraftingStandardProfile LoadProfile()
        {
            var profile = DraftingStandardProfile.CreateDefault();
            if (!File.Exists(SettingsPath)) return profile;
            try
            {
                var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(SettingsPath)) { var i = line.IndexOf('='); if (i > 0 && !line.TrimStart().StartsWith("#")) data[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim(); }
                foreach (var x in profile.Layers) { x.Name = Read(data, "Layer." + x.Key + ".Name", x.Name); x.ColorIndex = ReadShort(data, "Layer." + x.Key + ".Color", x.ColorIndex); x.TrueColorRgb = ReadInt(data, "Layer." + x.Key + ".ColorRgb", x.TrueColorRgb); x.LineWeight = ReadInt(data, "Layer." + x.Key + ".LineWeight", x.LineWeight); x.LineType = Read(data, "Layer." + x.Key + ".LineType", x.LineType); x.IsPlottable = Read(data, "Layer." + x.Key + ".Plottable", x.IsPlottable ? "1" : "0") == "1"; x.CreateOnApply = Read(data, "Layer." + x.Key + ".Create", "1") == "1"; }
                int layerCount; if (int.TryParse(Read(data, "Layer.Count", "0"), out layerCount) && layerCount > 0)
                {
                    profile.Layers.Clear();
                    for (var i = 0; i < layerCount; i++) { var prefix = "LayerItem." + i + "."; profile.Layers.Add(new DraftingLayerSetting { Key = Read(data, prefix + "Key", "CustomLayer_" + i), Purpose = Read(data, prefix + "Purpose", "自定义图层"), Name = Read(data, prefix + "Name", "WL-自定义-" + (i + 1)), ColorIndex = ReadShort(data, prefix + "Color", 7), TrueColorRgb = ReadInt(data, prefix + "ColorRgb", -1), LineWeight = ReadInt(data, prefix + "LineWeight", 18), LineType = Read(data, prefix + "LineType", "Continuous"), IsPlottable = Read(data, prefix + "Plottable", "1") == "1", CreateOnApply = Read(data, prefix + "Create", "1") == "1" }); }
                }
                int textCount; if (int.TryParse(Read(data, "Text.Count", "0"), out textCount) && textCount > 0)
                {
                    profile.TextStyles.Clear();
                    for (var i = 0; i < textCount; i++)
                    {
                        var prefix = "Text." + i + ".";
                        var font = Read(data, prefix + "Font", "simsun.ttc");
                        profile.TextStyles.Add(new DraftingTextStyleSetting { Key = Read(data, prefix + "Key", "Custom_" + i), Purpose = Read(data, prefix + "Purpose", "自定义文字"), Name = Read(data, prefix + "Name", "WL-文字-" + (i + 1)), FontType = Read(data, prefix + "FontType", InferFontType(font)), FontFile = font, BigFontFile = Read(data, prefix + "BigFont", ""), TextHeight = ReadDouble(data, prefix + "Height", 0), WidthFactor = ReadDouble(data, prefix + "Width", 1), CreateOnApply = Read(data, prefix + "Create", "1") == "1" });
                    }
                }
                else foreach (var x in profile.TextStyles) { x.Purpose = Read(data, "Text." + x.Key + ".Purpose", x.Purpose); x.Name = Read(data, "Text." + x.Key + ".Name", x.Name); x.FontFile = Read(data, "Text." + x.Key + ".Font", x.FontFile); x.FontType = Read(data, "Text." + x.Key + ".FontType", InferFontType(x.FontFile)); x.BigFontFile = Read(data, "Text." + x.Key + ".BigFont", x.BigFontFile); x.TextHeight = ReadDouble(data, "Text." + x.Key + ".Height", x.TextHeight); x.WidthFactor = ReadDouble(data, "Text." + x.Key + ".Width", x.WidthFactor); x.CreateOnApply = Read(data, "Text." + x.Key + ".Create", "1") == "1"; }
                // Drawing resources are always authored at 1:1.  Actual display
                // scale is controlled by the separate scale manager.
                profile.DimensionScales = new List<int> { 1 };
                profile.DimensionCreateOnApply = Read(data, "Dimension.Create", "1") == "1"; profile.DimensionStylePrefix = Read(data, "Dimension.StylePrefix", profile.DimensionStylePrefix);
                profile.DimensionTextHeight = ReadDouble(data, "Dimension.TextHeight", profile.DimensionTextHeight); profile.DimensionArrowSize = ReadDouble(data, "Dimension.ArrowSize", profile.DimensionArrowSize);
                profile.DimensionLineColor = ReadShort(data, "Dimension.LineColor", profile.DimensionLineColor); profile.ExtensionLineColor = ReadShort(data, "Dimension.ExtensionColor", profile.ExtensionLineColor); profile.DimensionTextColor = ReadShort(data, "Dimension.TextColor", profile.DimensionTextColor);
                profile.DimensionLineExtension = ReadDouble(data, "Dimension.LineExtension", profile.DimensionLineExtension); profile.BaselineSpacing = ReadDouble(data, "Dimension.BaselineSpacing", profile.BaselineSpacing); profile.ExtensionBeyond = ReadDouble(data, "Dimension.ExtensionBeyond", profile.ExtensionBeyond); profile.ExtensionOriginOffset = ReadDouble(data, "Dimension.ExtensionOriginOffset", profile.ExtensionOriginOffset); profile.FixedExtensionLength = ReadDouble(data, "Dimension.FixedExtensionLength", profile.FixedExtensionLength); profile.UseFixedExtensionLength = Read(data, "Dimension.UseFixedExtensionLength", "0") == "1"; profile.DimensionTextGap = ReadDouble(data, "Dimension.TextGap", profile.DimensionTextGap); profile.DimensionPrecision = ReadInt(data, "Dimension.Precision", profile.DimensionPrecision); profile.DimensionRounding = ReadDouble(data, "Dimension.Rounding", profile.DimensionRounding);
                profile.DimensionArrowStyle = Read(data, "Dimension.ArrowStyle", profile.DimensionArrowStyle); profile.CenterMarkStyle = Read(data, "Dimension.CenterMarkStyle", profile.CenterMarkStyle); profile.CenterMarkSize = ReadDouble(data, "Dimension.CenterMarkSize", profile.CenterMarkSize); profile.ArcLengthSymbol = Read(data, "Dimension.ArcLengthSymbol", profile.ArcLengthSymbol); profile.JogAngle = ReadDouble(data, "Dimension.JogAngle", profile.JogAngle);
                profile.LeaderCreateOnApply = Read(data, "Leader.Create", "1") == "1"; profile.LeaderStyleName = Read(data, "Leader.Name", profile.LeaderStyleName); profile.LeaderLineType = Read(data, "Leader.LineType", profile.LeaderLineType); profile.LeaderLineColor = ReadShort(data, "Leader.LineColor", profile.LeaderLineColor); profile.LeaderTextColor = ReadShort(data, "Leader.TextColor", profile.LeaderTextColor); profile.LeaderLineWeight = ReadInt(data, "Leader.LineWeight", profile.LeaderLineWeight); profile.LeaderArrowStyle = Read(data, "Leader.ArrowStyle", profile.LeaderArrowStyle); profile.LeaderArrowSize = ReadDouble(data, "Leader.ArrowSize", profile.LeaderArrowSize); profile.LeaderTextHeight = ReadDouble(data, "Leader.TextHeight", profile.LeaderTextHeight); profile.LeaderLandingGap = ReadDouble(data, "Leader.LandingGap", profile.LeaderLandingGap); profile.LeaderDoglegLength = ReadDouble(data, "Leader.DoglegLength", profile.LeaderDoglegLength); profile.LeaderEnableLanding = Read(data, "Leader.EnableLanding", "1") == "1"; profile.LeaderEnableDogleg = Read(data, "Leader.EnableDogleg", "1") == "1"; profile.LeaderFrameText = Read(data, "Leader.FrameText", "0") == "1";
                profile.UpdateExisting = Read(data, "General.UpdateExisting", "0") == "1";
                UpgradeLegacyTextDefaults(profile);
            }
            catch { return DraftingStandardProfile.CreateDefault(); }
            return profile;
        }

        public static void SaveProfile(DraftingStandardProfile profile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            var lines = new List<string> { "# 万落建筑工具制图标准 v1", "General.UpdateExisting=" + (profile.UpdateExisting ? "1" : "0") };
            lines.Add("Layer.Count=" + profile.Layers.Count); for (var i = 0; i < profile.Layers.Count; i++) { var x = profile.Layers[i]; var prefix = "LayerItem." + i + "."; lines.Add(prefix + "Key=" + x.Key); lines.Add(prefix + "Purpose=" + x.Purpose); lines.Add(prefix + "Name=" + x.Name); lines.Add(prefix + "Color=" + x.ColorIndex); lines.Add(prefix + "ColorRgb=" + x.TrueColorRgb); lines.Add(prefix + "LineWeight=" + x.LineWeight); lines.Add(prefix + "LineType=" + x.LineType); lines.Add(prefix + "Plottable=" + (x.IsPlottable ? "1" : "0")); lines.Add(prefix + "Create=" + (x.CreateOnApply ? "1" : "0")); }
            lines.Add("Text.Count=" + profile.TextStyles.Count);
            for (var i = 0; i < profile.TextStyles.Count; i++) { var x = profile.TextStyles[i]; var prefix = "Text." + i + "."; lines.Add(prefix + "Key=" + x.Key); lines.Add(prefix + "Purpose=" + x.Purpose); lines.Add(prefix + "Name=" + x.Name); lines.Add(prefix + "FontType=" + x.FontType); lines.Add(prefix + "Font=" + x.FontFile); lines.Add(prefix + "BigFont=" + (x.BigFontFile ?? "")); lines.Add(prefix + "Height=" + x.TextHeight.ToString(CultureInfo.InvariantCulture)); lines.Add(prefix + "Width=" + x.WidthFactor.ToString(CultureInfo.InvariantCulture)); lines.Add(prefix + "Create=" + (x.CreateOnApply ? "1" : "0")); }
            lines.Add("Dimension.Scales=" + string.Join(",", profile.DimensionScales.Select(x => x.ToString(CultureInfo.InvariantCulture)))); lines.Add("Dimension.Create=" + (profile.DimensionCreateOnApply ? "1" : "0")); lines.Add("Dimension.StylePrefix=" + profile.DimensionStylePrefix); lines.Add("Dimension.TextHeight=" + profile.DimensionTextHeight.ToString(CultureInfo.InvariantCulture)); lines.Add("Dimension.ArrowSize=" + profile.DimensionArrowSize.ToString(CultureInfo.InvariantCulture)); lines.Add("Dimension.LineColor=" + profile.DimensionLineColor); lines.Add("Dimension.ExtensionColor=" + profile.ExtensionLineColor); lines.Add("Dimension.TextColor=" + profile.DimensionTextColor); lines.Add("Dimension.LineExtension=" + profile.DimensionLineExtension.ToString(CultureInfo.InvariantCulture)); lines.Add("Dimension.BaselineSpacing=" + profile.BaselineSpacing.ToString(CultureInfo.InvariantCulture)); lines.Add("Dimension.ExtensionBeyond=" + profile.ExtensionBeyond.ToString(CultureInfo.InvariantCulture)); lines.Add("Dimension.ExtensionOriginOffset=" + profile.ExtensionOriginOffset.ToString(CultureInfo.InvariantCulture)); lines.Add("Dimension.UseFixedExtensionLength=" + (profile.UseFixedExtensionLength ? "1" : "0")); lines.Add("Dimension.FixedExtensionLength=" + profile.FixedExtensionLength.ToString(CultureInfo.InvariantCulture)); lines.Add("Dimension.TextGap=" + profile.DimensionTextGap.ToString(CultureInfo.InvariantCulture)); lines.Add("Dimension.Precision=" + profile.DimensionPrecision); lines.Add("Dimension.Rounding=" + profile.DimensionRounding.ToString(CultureInfo.InvariantCulture));
            lines.Add("Dimension.ArrowStyle=" + profile.DimensionArrowStyle); lines.Add("Dimension.CenterMarkStyle=" + profile.CenterMarkStyle); lines.Add("Dimension.CenterMarkSize=" + profile.CenterMarkSize.ToString(CultureInfo.InvariantCulture)); lines.Add("Dimension.ArcLengthSymbol=" + profile.ArcLengthSymbol); lines.Add("Dimension.JogAngle=" + profile.JogAngle.ToString(CultureInfo.InvariantCulture));
            lines.Add("Leader.Create=" + (profile.LeaderCreateOnApply ? "1" : "0")); lines.Add("Leader.Name=" + profile.LeaderStyleName); lines.Add("Leader.LineType=" + profile.LeaderLineType); lines.Add("Leader.LineColor=" + profile.LeaderLineColor); lines.Add("Leader.TextColor=" + profile.LeaderTextColor); lines.Add("Leader.LineWeight=" + profile.LeaderLineWeight); lines.Add("Leader.ArrowStyle=" + profile.LeaderArrowStyle); lines.Add("Leader.ArrowSize=" + profile.LeaderArrowSize.ToString(CultureInfo.InvariantCulture)); lines.Add("Leader.TextHeight=" + profile.LeaderTextHeight.ToString(CultureInfo.InvariantCulture)); lines.Add("Leader.LandingGap=" + profile.LeaderLandingGap.ToString(CultureInfo.InvariantCulture)); lines.Add("Leader.DoglegLength=" + profile.LeaderDoglegLength.ToString(CultureInfo.InvariantCulture)); lines.Add("Leader.EnableLanding=" + (profile.LeaderEnableLanding ? "1" : "0")); lines.Add("Leader.EnableDogleg=" + (profile.LeaderEnableDogleg ? "1" : "0")); lines.Add("Leader.FrameText=" + (profile.LeaderFrameText ? "1" : "0"));
            File.WriteAllLines(SettingsPath, lines.ToArray(), System.Text.Encoding.UTF8);
        }

        public static DraftingStandardResources EnsureAll(Database db, Transaction tr) { var p = LoadProfile(); return EnsureAll(db, tr, p, p.UpdateExisting); }
        public static DraftingStandardResources EnsureAll(Database db, Transaction tr, DraftingStandardProfile profile, bool updateExisting)
        {
            if (db == null) throw new ArgumentNullException("db"); if (tr == null) throw new ArgumentNullException("tr"); profile = profile ?? DraftingStandardProfile.CreateDefault();
            var result = new DraftingStandardResources { Profile = profile };
            foreach (var x in profile.Layers) { var lt = string.Equals(x.LineType, "Continuous", StringComparison.OrdinalIgnoreCase) ? ObjectId.Null : EnsureLineType(db, tr, x.LineType); result.LayerIds[x.Key] = EnsureLayer(db, tr, x.Name, LayerColor(x), (LineWeight)x.LineWeight, lt, x.IsPlottable, updateExisting); }
            foreach (var x in profile.TextStyles.Where(x => x.CreateOnApply || x.Key == DraftingStandardProfile.BodyTextKey || x.Key == DraftingStandardProfile.TitleTextKey || x.Key == DraftingStandardProfile.AnnotationTextKey)) result.TextStyleIds[x.Key] = EnsureTextStyle(db, tr, x.Name, x.FontFile, x.BigFontFile, x.TextHeight, x.WidthFactor, updateExisting);
            var dimensionStyle = EnsureDimStyle(db, tr, profile.DimensionStyleName(1), 1, result.TextStyleIds[DraftingStandardProfile.AnnotationTextKey], profile, updateExisting);
            result.DimensionStyleIds[1] = dimensionStyle;
            // Compatibility aliases for older callers; every alias points to
            // the same 1:1 style instead of creating duplicate CAD resources.
            foreach (var scale in new[] { 20, 50, 100, 200 }) result.DimensionStyleIds[scale] = dimensionStyle;
            return result;
        }

        public static ObjectId ResolveTextStyle(Database db, Transaction tr, string requestedName, bool title)
        {
            var r = EnsureAll(db, tr); if (string.IsNullOrWhiteSpace(requestedName)) return title ? r.TitleTextStyleId : r.BodyTextStyleId;
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead); if (table.Has(requestedName)) return table[requestedName];
            var file = string.Equals(requestedName, "黑体", StringComparison.OrdinalIgnoreCase) ? "simhei.ttf" : string.Equals(requestedName, "宋体", StringComparison.OrdinalIgnoreCase) ? "simsun.ttc" : string.Equals(requestedName, "微软雅黑", StringComparison.OrdinalIgnoreCase) ? "msyh.ttc" : string.Equals(requestedName, "Arial", StringComparison.OrdinalIgnoreCase) ? "arial.ttf" : null;
            return file == null ? (title ? r.TitleTextStyleId : r.BodyTextStyleId) : EnsureTextStyle(db, tr, "WL-文字-" + requestedName, file, "", 0, 1, false);
        }

        public static ObjectId EnsureDimensionStyleForScale(Database db, Transaction tr, int scale)
        {
            var profile = LoadProfile();
            var resources = EnsureAll(db, tr, profile, profile.UpdateExisting);
            return EnsureDimensionStyleForScale(db, tr, scale, profile, resources);
        }

        public static ObjectId EnsureDimensionStyleForScale(Database db, Transaction tr, int scale, DraftingStandardProfile profile, DraftingStandardResources resources)
        {
            return EnsureDimensionStyleForScale(db, tr, scale, profile, resources, true);
        }

        public static ObjectId EnsureDimensionStyleForScale(Database db, Transaction tr, int scale, DraftingStandardProfile profile, DraftingStandardResources resources, bool updateExisting)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            if (resources == null) throw new ArgumentNullException("resources");
            return EnsureDimStyle(db, tr, profile.DimensionStyleName(Math.Max(1, scale)), Math.Max(1, scale), resources.AnnotationTextStyleId, profile, updateExisting);
        }

        public static void ApplyConfiguredResources(Database db, Transaction tr, DraftingStandardProfile profile, bool updateExisting)
        {
            ApplyConfiguredLayers(db, tr, profile, updateExisting);
            ApplyConfiguredTextStyles(db, tr, profile, updateExisting);
            ApplyConfiguredDimensionStyle(db, tr, profile, updateExisting);
            ApplyConfiguredLeaderStyle(db, tr, profile, updateExisting);
        }

        public static void ApplyConfiguredLayers(Database db, Transaction tr, DraftingStandardProfile profile, bool updateExisting)
        {
            foreach (var x in profile.Layers.Where(x => x.CreateOnApply))
            {
                var lt = string.Equals(x.LineType, "Continuous", StringComparison.OrdinalIgnoreCase) ? ObjectId.Null : EnsureLineType(db, tr, x.LineType);
                EnsureLayer(db, tr, x.Name, LayerColor(x), (LineWeight)x.LineWeight, lt, x.IsPlottable, updateExisting);
            }
        }

        public static void ApplyAllConfiguredLayersToCurrentDrawing(Database db, Transaction tr, DraftingStandardProfile profile)
        {
            foreach (var x in profile.Layers)
            {
                // Unlike “create checked layers”, this command deliberately
                // updates every configured row and also resolves Continuous to
                // a real linetype id so an old dashed layer can become solid.
                var lineType = EnsureLineType(db, tr, string.IsNullOrWhiteSpace(x.LineType) ? "Continuous" : x.LineType);
                EnsureLayer(db, tr, x.Name, LayerColor(x), (LineWeight)x.LineWeight, lineType, x.IsPlottable, true);
            }
        }

        public static void ApplyConfiguredTextStyles(Database db, Transaction tr, DraftingStandardProfile profile, bool updateExisting)
        {
            foreach (var x in profile.TextStyles.Where(x => x.CreateOnApply)) EnsureTextStyle(db, tr, x.Name, x.FontFile, x.BigFontFile, x.TextHeight, x.WidthFactor, updateExisting);
        }

        public static void ApplyConfiguredDimensionStyle(Database db, Transaction tr, DraftingStandardProfile profile, bool updateExisting)
        {
            if (!profile.DimensionCreateOnApply) return;
            var annotation = profile.Text(DraftingStandardProfile.AnnotationTextKey);
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            var textStyle = table.Has(annotation.Name) ? table[annotation.Name] : annotation.CreateOnApply ? EnsureTextStyle(db, tr, annotation.Name, annotation.FontFile, annotation.BigFontFile, annotation.TextHeight, annotation.WidthFactor, updateExisting) : db.Textstyle;
            EnsureDimStyle(db, tr, profile.DimensionStyleName(1), 1, textStyle, profile, updateExisting);
        }

        public static void ApplyConfiguredLeaderStyle(Database db, Transaction tr, DraftingStandardProfile profile, bool updateExisting)
        {
            if (!profile.LeaderCreateOnApply) return;
            SymbolUtilityServices.ValidateSymbolName(profile.LeaderStyleName, false);
            var annotation = profile.Text(DraftingStandardProfile.AnnotationTextKey); var textTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            var textStyle = textTable.Has(annotation.Name) ? textTable[annotation.Name] : EnsureTextStyle(db, tr, annotation.Name, annotation.FontFile, annotation.BigFontFile, annotation.TextHeight, annotation.WidthFactor, updateExisting);
            var dictionary = (DBDictionary)tr.GetObject(db.MLeaderStyleDictionaryId, OpenMode.ForRead); MLeaderStyle style;
            if (dictionary.Contains(profile.LeaderStyleName)) { if (!updateExisting) return; style = (MLeaderStyle)tr.GetObject(dictionary.GetAt(profile.LeaderStyleName), OpenMode.ForWrite); }
            else { style = new MLeaderStyle(); style.PostMLeaderStyleToDb(db, profile.LeaderStyleName); tr.AddNewlyCreatedDBObject(style, true); }
            style.ContentType = ContentType.MTextContent; style.LeaderLineType = profile.LeaderLineType == "样条曲线" ? LeaderType.SplineLeader : LeaderType.StraightLeader; style.LeaderLineColor = Color.FromColorIndex(ColorMethod.ByAci, profile.LeaderLineColor); style.LeaderLineWeight = (LineWeight)profile.LeaderLineWeight; style.ArrowSymbolId = EnsureArrowBlock(db, tr, profile.LeaderArrowStyle); style.ArrowSize = profile.LeaderArrowSize; style.TextStyleId = textStyle; style.TextColor = Color.FromColorIndex(ColorMethod.ByAci, profile.LeaderTextColor); style.TextHeight = profile.LeaderTextHeight; style.EnableLanding = profile.LeaderEnableLanding; style.LandingGap = profile.LeaderLandingGap; style.EnableDogleg = profile.LeaderEnableDogleg; style.DoglegLength = profile.LeaderDoglegLength; style.EnableFrameText = profile.LeaderFrameText; style.Scale = 1;
        }

        private static Autodesk.AutoCAD.Colors.Color LayerColor(DraftingLayerSetting setting)
        {
            if (setting.TrueColorRgb >= 0)
                return Autodesk.AutoCAD.Colors.Color.FromRgb((byte)((setting.TrueColorRgb >> 16) & 255), (byte)((setting.TrueColorRgb >> 8) & 255), (byte)(setting.TrueColorRgb & 255));
            return Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, setting.ColorIndex);
        }
        private static ObjectId EnsureLayer(Database db, Transaction tr, string name, Autodesk.AutoCAD.Colors.Color color, LineWeight weight, ObjectId lt, bool plottable, bool update)
        {
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead); if (table.Has(name)) { if (update) { var x = (LayerTableRecord)tr.GetObject(table[name], OpenMode.ForWrite); x.Color = color; x.LineWeight = weight; x.IsPlottable = plottable; if (!lt.IsNull) x.LinetypeObjectId = lt; } return table[name]; }
            table.UpgradeOpen(); var record = new LayerTableRecord { Name = name, Color = color, LineWeight = weight, IsPlottable = plottable }; if (!lt.IsNull) record.LinetypeObjectId = lt; var id = table.Add(record); tr.AddNewlyCreatedDBObject(record, true); return id;
        }
        private static ObjectId EnsureLineType(Database db, Transaction tr, string name) { var t = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead); if (!t.Has(name)) { try { db.LoadLineTypeFile(name, "acadiso.lin"); } catch { return ObjectId.Null; } t = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead); } return t.Has(name) ? t[name] : ObjectId.Null; }
        private static ObjectId EnsureTextStyle(Database db, Transaction tr, string name, string font, string bigFont, double height, double width, bool update)
        {
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead); if (table.Has(name)) { if (update) { var x = (TextStyleTableRecord)tr.GetObject(table[name], OpenMode.ForWrite); ApplyFont(x, font, bigFont); x.XScale = width; x.TextSize = Math.Max(0, height); } return table[name]; }
            table.UpgradeOpen(); var record = new TextStyleTableRecord { Name = name, TextSize = Math.Max(0, height), XScale = width }; ApplyFont(record, font, bigFont); var id = table.Add(record); tr.AddNewlyCreatedDBObject(record, true); return id;
        }
        private static void ApplyFont(TextStyleTableRecord record, string font, string bigFont)
        {
            var value = (font ?? string.Empty).Trim();
            if (string.Equals(Path.GetExtension(value), ".shx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(value), ".ttf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(value), ".ttc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(value), ".otf", StringComparison.OrdinalIgnoreCase)) record.FileName = value;
            else record.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(value, false, false, 134, 0);
            record.BigFontFileName = string.IsNullOrEmpty(Path.GetExtension(value)) ? string.Empty : bigFont ?? string.Empty;
        }
        private static ObjectId EnsureDimStyle(Database db, Transaction tr, string name, int scale, ObjectId textStyle, DraftingStandardProfile profile, bool update)
        {
            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead); DimStyleTableRecord x; if (table.Has(name)) { if (!update) return table[name]; x = (DimStyleTableRecord)tr.GetObject(table[name], OpenMode.ForWrite); } else { table.UpgradeOpen(); x = new DimStyleTableRecord { Name = name }; var id = table.Add(x); tr.AddNewlyCreatedDBObject(x, true); }
            x.Dimscale = scale; x.Dimtxt = profile.DimensionTextHeight; x.Dimasz = profile.DimensionArrowSize; x.Dimtsz = 0; x.Dimblk = EnsureArrowBlock(db, tr, profile.DimensionArrowStyle); x.Dimdle = profile.DimensionLineExtension; x.Dimdli = profile.BaselineSpacing; x.Dimexe = profile.ExtensionBeyond; x.Dimexo = profile.ExtensionOriginOffset; x.DimfxlenOn = profile.UseFixedExtensionLength; x.Dimfxlen = profile.FixedExtensionLength; x.Dimgap = profile.DimensionTextGap; x.Dimdec = Math.Max(0, Math.Min(8, profile.DimensionPrecision)); x.Dimrnd = Math.Max(0, profile.DimensionRounding); x.Dimclrd = Color.FromColorIndex(ColorMethod.ByAci, profile.DimensionLineColor); x.Dimclre = Color.FromColorIndex(ColorMethod.ByAci, profile.ExtensionLineColor); x.Dimclrt = Color.FromColorIndex(ColorMethod.ByAci, profile.DimensionTextColor); x.Dimcen = profile.CenterMarkStyle == "无" ? 0 : profile.CenterMarkStyle == "中心线" ? -profile.CenterMarkSize : profile.CenterMarkSize; x.Dimarcsym = profile.ArcLengthSymbol == "前置" ? 0 : profile.ArcLengthSymbol == "上方" ? 1 : 2; x.Dimjogang = profile.JogAngle * Math.PI / 180d; x.Dimtad = 1; x.Dimtxsty = textStyle; return x.ObjectId;
        }

        private static ObjectId EnsureArrowBlock(Database db, Transaction tr, string style)
        {
            if (string.IsNullOrWhiteSpace(style) || style == "实心闭合") return ObjectId.Null;
            if (style.StartsWith("图块：", StringComparison.OrdinalIgnoreCase) || style.StartsWith("图块:", StringComparison.OrdinalIgnoreCase)) return ImportArrowBlock(db, tr, style.Substring(3).Trim());
            var safe = style == "建筑斜线" ? "ARCHTICK" : style == "空心闭合" ? "OPEN" : "DOT"; var name = "WL-箭头-" + safe;
            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead); if (table.Has(name)) return table[name]; table.UpgradeOpen(); var block = new BlockTableRecord { Name = name }; var id = table.Add(block); tr.AddNewlyCreatedDBObject(block, true);
            if (safe == "ARCHTICK") Append(block, tr, new Line(new Point3d(-.55, -.55, 0), new Point3d(.55, .55, 0)));
            else if (safe == "OPEN") { Append(block, tr, new Line(Point3d.Origin, new Point3d(-1, .35, 0))); Append(block, tr, new Line(Point3d.Origin, new Point3d(-1, -.35, 0))); }
            else Append(block, tr, new Circle(Point3d.Origin, Vector3d.ZAxis, .5));
            return id;
        }
        private static ObjectId ImportArrowBlock(Database db, Transaction tr, string blockName)
        {
            var target = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead); if (target.Has(blockName)) return target[blockName]; var path = ArrowLibraryPath; if (!File.Exists(path)) throw new FileNotFoundException("未找到箭头图块库：" + path);
            using (var source = new Database(false, true))
            {
                source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, ""); ObjectId sourceId;
                using (var sourceTr = source.TransactionManager.StartOpenCloseTransaction()) { var sourceTable = (BlockTable)sourceTr.GetObject(source.BlockTableId, OpenMode.ForRead); if (!sourceTable.Has(blockName)) throw new InvalidOperationException("箭头图块库中不存在图块：“" + blockName + "”。"); sourceId = sourceTable[blockName]; }
                var ids = new ObjectIdCollection { sourceId }; var mapping = new IdMapping(); source.WblockCloneObjects(ids, db.BlockTableId, mapping, DuplicateRecordCloning.Ignore, false);
            }
            target = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead); if (!target.Has(blockName)) throw new InvalidOperationException("无法从箭头图块库导入：“" + blockName + "”。"); return target[blockName];
        }

        public static void CreateDefaultArrowLibrary(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            using (var db = new Database(true, true)) using (var tr = db.TransactionManager.StartTransaction()) { EnsureArrowBlock(db, tr, "空心闭合"); EnsureArrowBlock(db, tr, "建筑斜线"); EnsureArrowBlock(db, tr, "点"); tr.Commit(); db.SaveAs(path, DwgVersion.AC1027); }
        }
        private static void Append(BlockTableRecord block, Transaction tr, Entity entity) { block.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true); }
        private static string Read(IDictionary<string, string> d, string k, string v) { string x; return d.TryGetValue(k, out x) && !string.IsNullOrWhiteSpace(x) ? x : v; }
        private static int ReadInt(IDictionary<string, string> d, string k, int v) { int x; return int.TryParse(Read(d, k, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ? x : v; }
        private static short ReadShort(IDictionary<string, string> d, string k, short v) { short x; return short.TryParse(Read(d, k, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ? x : v; }
        private static double ReadDouble(IDictionary<string, string> d, string k, double v) { double x; return double.TryParse(Read(d, k, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : v; }
        private static void UpgradeLegacyTextDefaults(DraftingStandardProfile profile)
        {
            UpgradeLegacyTextDefault(profile, DraftingStandardProfile.BodyTextKey, BodyTextStyle, "simsun.ttc", 2.5, .7);
            UpgradeLegacyTextDefault(profile, DraftingStandardProfile.TitleTextKey, TitleTextStyle, "simhei.ttf", 7, 1);
            UpgradeLegacyTextDefault(profile, DraftingStandardProfile.AnnotationTextKey, AnnotationTextStyle, "simsun.ttc", 3.5, .7);
        }
        private static void UpgradeLegacyTextDefault(DraftingStandardProfile profile, string key, string name, string font, double height, double width)
        {
            var setting = profile.TextStyles.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (setting == null || !string.Equals(setting.Name, name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(setting.FontFile, font, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(setting.TextHeight) > .000001d || Math.Abs(setting.WidthFactor - width) > .000001d) return;
            setting.TextHeight = height;
        }
        private static string InferFontType(string font) { return string.Equals(Path.GetExtension(font ?? ""), ".shx", StringComparison.OrdinalIgnoreCase) ? "CAD 字体（SHX）" : "Windows 字体"; }
        public static List<int> ParseScales(string value) { var r = (value ?? "").Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => { int n; return int.TryParse(x.Trim().Replace("1:", "").Replace("1：", ""), out n) ? n : 0; }).Where(x => x > 0).Distinct().OrderBy(x => x).ToList(); return r.Count == 0 ? new List<int> { 50 } : r; }
    }

    public sealed class DraftingStandardProfile
    {
        public const string FrameKey = "Frame", CatalogKey = "Catalog", OutlineKey = "Outline", FineKey = "Fine", StructureKey = "Structure", HiddenKey = "Hidden", HatchKey = "Hatch", AnnotationTextLayerKey = "AnnotationTextLayer", AnnotationDimensionLayerKey = "AnnotationDimensionLayer", BodyTextKey = "Body", TitleTextKey = "Title", AnnotationTextKey = "Annotation";
        public List<DraftingLayerSetting> Layers = new List<DraftingLayerSetting>(); public List<DraftingTextStyleSetting> TextStyles = new List<DraftingTextStyleSetting>(); public List<int> DimensionScales = new List<int>(); public double DimensionTextHeight = 2.5, DimensionArrowSize = 2.5, DimensionLineExtension = 0, BaselineSpacing = 3.75, ExtensionBeyond = 1.25, ExtensionOriginOffset = .625, FixedExtensionLength = 5, DimensionTextGap = .625, DimensionRounding = 0, CenterMarkSize = 2.5, JogAngle = 45; public short DimensionLineColor = 0, ExtensionLineColor = 0, DimensionTextColor = 2; public int DimensionPrecision = 0; public bool UseFixedExtensionLength, DimensionCreateOnApply = true, UpdateExisting; public string DimensionStylePrefix = "WL-标注-1_", DimensionArrowStyle = "实心闭合", CenterMarkStyle = "中心标记", ArcLengthSymbol = "前置";
        public bool LeaderCreateOnApply = true, LeaderEnableLanding = true, LeaderEnableDogleg = true, LeaderFrameText; public string LeaderStyleName = "WL-引线-1_1", LeaderLineType = "直线", LeaderArrowStyle = "实心闭合"; public short LeaderLineColor = 0, LeaderTextColor = 2; public int LeaderLineWeight = (int)LineWeight.ByLineWeightDefault; public double LeaderArrowSize = 2.5, LeaderTextHeight = 2.5, LeaderLandingGap = .625, LeaderDoglegLength = 3.75;
        public static DraftingStandardProfile CreateDefault() { var p = new DraftingStandardProfile(); p.Layers.AddRange(new[] { L(FrameKey,"图框",DraftingStandardService.FrameLayer,7,30,"Continuous"), L(CatalogKey,"图纸目录",DraftingStandardService.CatalogLayer,7,18,"Continuous"), L(OutlineKey,"建筑轮廓",DraftingStandardService.ArchitectureOutlineLayer,7,30,"Continuous"), L(FineKey,"建筑细线",DraftingStandardService.ArchitectureFineLayer,2,13,"Continuous"), L(StructureKey,"建筑结构",DraftingStandardService.ArchitectureStructureLayer,1,35,"Continuous"), L(HiddenKey,"建筑隐藏",DraftingStandardService.ArchitectureHiddenLayer,8,13,"HIDDEN"), L(HatchKey,"建筑填充",DraftingStandardService.ArchitectureHatchLayer,8,9,"Continuous"), L(AnnotationTextLayerKey,"注释文字",DraftingStandardService.AnnotationTextLayer,7,18,"Continuous"), L(AnnotationDimensionLayerKey,"注释标注",DraftingStandardService.AnnotationDimensionLayer,3,13,"Continuous") }); p.TextStyles.AddRange(new[] { T(BodyTextKey,"正文",DraftingStandardService.BodyTextStyle,"simsun.ttc",2.5,.7), T(TitleTextKey,"标题",DraftingStandardService.TitleTextStyle,"simhei.ttf",7,1), T(AnnotationTextKey,"标注",DraftingStandardService.AnnotationTextStyle,"simsun.ttc",3.5,.7) }); p.DimensionScales.Add(1); return p; }
        private static DraftingLayerSetting L(string k,string purpose,string name,short color,int weight,string lt) { return new DraftingLayerSetting { Key=k,Purpose=purpose,Name=name,ColorIndex=color,TrueColorRgb=-1,LineWeight=weight,LineType=lt,IsPlottable=true,CreateOnApply=true }; } private static DraftingTextStyleSetting T(string k,string purpose,string name,string font,double height,double width) { return new DraftingTextStyleSetting { Key=k,Purpose=purpose,Name=name,FontType=string.Equals(Path.GetExtension(font),".shx",StringComparison.OrdinalIgnoreCase)?"CAD 字体（SHX）":"Windows 字体",FontFile=font,BigFontFile="",TextHeight=height,WidthFactor=width,CreateOnApply=true }; }
        public DraftingLayerSetting Layer(string key) { return Layers.First(x => x.Key == key); } public DraftingTextStyleSetting Text(string key) { return TextStyles.First(x => x.Key == key); } public string DimensionStyleName(int scale) { return (string.IsNullOrWhiteSpace(DimensionStylePrefix) ? "WL-标注-1_" : DimensionStylePrefix) + Math.Max(1, scale); }
    }
    public sealed class DraftingLayerSetting { public string Key, Purpose, Name, LineType; public short ColorIndex; public int TrueColorRgb = -1; public int LineWeight; public bool IsPlottable = true, CreateOnApply = true; }
    public sealed class DraftingTextStyleSetting { public string Key, Purpose, Name, FontType, FontFile, BigFontFile; public double TextHeight, WidthFactor; public bool CreateOnApply = true; }
    public sealed class DraftingStandardResources
    {
        public DraftingStandardProfile Profile; public readonly Dictionary<string,ObjectId> LayerIds = new Dictionary<string,ObjectId>(); public readonly Dictionary<string,ObjectId> TextStyleIds = new Dictionary<string,ObjectId>(); public readonly Dictionary<int,ObjectId> DimensionStyleIds = new Dictionary<int,ObjectId>();
        public ObjectId FrameLayerId { get { return LayerIds[DraftingStandardProfile.FrameKey]; } } public ObjectId CatalogLayerId { get { return LayerIds[DraftingStandardProfile.CatalogKey]; } } public ObjectId ArchitectureOutlineLayerId { get { return LayerIds[DraftingStandardProfile.OutlineKey]; } } public ObjectId ArchitectureFineLayerId { get { return LayerIds[DraftingStandardProfile.FineKey]; } } public ObjectId ArchitectureStructureLayerId { get { return LayerIds[DraftingStandardProfile.StructureKey]; } } public ObjectId ArchitectureHiddenLayerId { get { return LayerIds[DraftingStandardProfile.HiddenKey]; } } public ObjectId ArchitectureHatchLayerId { get { return LayerIds[DraftingStandardProfile.HatchKey]; } } public ObjectId AnnotationTextLayerId { get { return LayerIds[DraftingStandardProfile.AnnotationTextLayerKey]; } } public ObjectId AnnotationDimensionLayerId { get { return LayerIds[DraftingStandardProfile.AnnotationDimensionLayerKey]; } }
        public ObjectId BodyTextStyleId { get { return TextStyleIds[DraftingStandardProfile.BodyTextKey]; } } public ObjectId TitleTextStyleId { get { return TextStyleIds[DraftingStandardProfile.TitleTextKey]; } } public ObjectId AnnotationTextStyleId { get { return TextStyleIds[DraftingStandardProfile.AnnotationTextKey]; } } public ObjectId DimensionStyle50Id { get { ObjectId x; return DimensionStyleIds.TryGetValue(50,out x) ? x : ObjectId.Null; } }
    }
}
