using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Numerics;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace BatchPdfPublisher.Services
{
    public sealed class AttributeTarget
    {
        public ObjectId BlockId { get; set; }
        public string BlockName { get; set; }
        public string Tag { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public Point3d Center { get; set; }
        public ObjectId AttributeId { get; set; }
        public string BlockHandle { get; set; }
        public Point3d MinPoint { get; set; }
        public Point3d MaxPoint { get; set; }
        public Point3d AttributePosition { get; set; }
    }

    public enum AttributeSortOrder { LeftThenTop, TopThenLeft }

    public enum AttributeNumberingStyle
    {
        Arabic = 0,
        LatinUpper = 1,
        LatinLower = 2,
        RomanUpper = 3,
        RomanLower = 4,
        ChineseLower = 5,
        ChineseFinancial = 6,
        CircledNumber = 7,
        ParenthesizedNumber = 8,
        BlackCircledNumber = 9,
        DoubleCircledNumber = 10,
        DingbatCircledNumber = 11,
        AsciiParenthesizedNumber = 12,
        FullWidthParenthesizedNumber = 13,
        SquareBracketNumber = 14,
        FullWidthChineseNumber = 15,
        CircledLatinUpper = 16,
        CircledLatinLower = 17,
        ParenthesizedLatinLower = 18,
        AsciiParenthesizedLatinUpper = 19,
        HeavenlyStems = 20,
        EarthlyBranches = 21,
        ChineseOrdinal = 22
    }

    [DataContract]
    public sealed class AttributeBatchSettings
    {
        [DataMember] public int Scope { get; set; }
        [DataMember] public int Sort { get; set; }
        [DataMember] public bool Increment { get; set; }
        [DataMember] public bool Letters { get; set; }
        [DataMember] public bool Reverse { get; set; }
        [DataMember] public int Step { get; set; } = 1;
        [DataMember] public string Tolerance { get; set; }
        [DataMember] public string LastPreset { get; set; }
        [DataMember] public bool PrefixIncrement { get; set; }
        [DataMember] public bool SuffixIncrement { get; set; }
        [DataMember] public int? NumberingStyle { get; set; }

        public static AttributeBatchSettings Load()
        {
            try
            {
                var path = SettingsPath();
                if (!File.Exists(path)) return new AttributeBatchSettings();
                using (var stream = File.OpenRead(path)) return (AttributeBatchSettings)new DataContractJsonSerializer(typeof(AttributeBatchSettings)).ReadObject(stream);
            }
            catch { return new AttributeBatchSettings(); }
        }

        public void Save()
        {
            try { using (var stream = File.Create(SettingsPath())) new DataContractJsonSerializer(typeof(AttributeBatchSettings)).WriteObject(stream, this); } catch { }
        }

        private static string SettingsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatchPdfPublisher.attribute-settings.json");
    }

    [DataContract]
    public sealed class AttributeBatchPreset
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string Seed { get; set; }
        [DataMember] public string Prefix { get; set; }
        [DataMember] public string Suffix { get; set; }
        [DataMember] public bool Increment { get; set; }
        [DataMember] public bool Letters { get; set; }
        [DataMember] public bool Reverse { get; set; }
        [DataMember] public int Step { get; set; } = 1;
        [DataMember] public int Sort { get; set; }
        [DataMember] public string Tolerance { get; set; }
        [DataMember] public bool PrefixIncrement { get; set; }
        [DataMember] public bool SuffixIncrement { get; set; }
        [DataMember] public int? NumberingStyle { get; set; }
    }

    public static class AttributePresetStore
    {
        public static List<AttributeBatchPreset> Load()
        {
            try { var path = PresetsPath(); if (!File.Exists(path)) return new List<AttributeBatchPreset>(); using (var stream = File.OpenRead(path)) return (List<AttributeBatchPreset>)new DataContractJsonSerializer(typeof(List<AttributeBatchPreset>)).ReadObject(stream) ?? new List<AttributeBatchPreset>(); }
            catch { return new List<AttributeBatchPreset>(); }
        }
        public static void Save(List<AttributeBatchPreset> presets)
        {
            try { using (var stream = File.Create(PresetsPath())) new DataContractJsonSerializer(typeof(List<AttributeBatchPreset>)).WriteObject(stream, presets ?? new List<AttributeBatchPreset>()); } catch { }
        }
        private static string PresetsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatchPdfPublisher.attribute-presets.json");
    }

    public sealed class AttributeApplyResult
    {
        public int Changed { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public List<string> Errors { get; } = new List<string>();
        public HashSet<ObjectId> ChangedAttributeIds { get; } = new HashSet<ObjectId>();
        public List<AttributeApplyDetail> Details { get; } = new List<AttributeApplyDetail>();
    }

    public sealed class AttributeApplyDetail
    {
        public AttributeTarget Target { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
    }

    public static class AttributeBatchService
    {
        public static List<AttributeTarget> SelectTargets(Document document, AttributeSortOrder order)
        {
            var editor = document.Editor;
            var options = new PromptSelectionOptions { MessageForAdding = "\n请选择要批量修改的图块：" };
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            var result = editor.GetSelection(options, filter);
            if (result.Status != PromptStatus.OK) return new List<AttributeTarget>();
            return ReadTargets(document, result.Value.GetObjectIds(), order);
        }

        public static List<AttributeTarget> ReadTargets(Document document, IEnumerable<ObjectId> ids, AttributeSortOrder order)
        {
            var rows = new List<AttributeTarget>();
            using (document.LockDocument())
            using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids ?? Enumerable.Empty<ObjectId>())
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var reference = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (reference == null || reference.AttributeCollection.Count == 0) continue;
                    var name = GetBlockName(reference, tr);
                    // Sorting is based on the block reference insertion point,
                    // not the block definition name or the attribute geometry.
                    // This keeps mixed block types in one common coordinate order.
                    var position = reference.Position;
                    var minPoint = position;
                    var maxPoint = position;
                    try { var extents = reference.GeometricExtents; minPoint = extents.MinPoint; maxPoint = extents.MaxPoint; }
                    catch
                    {
                        try { var bounds = reference.Bounds; if (bounds.HasValue) { minPoint = bounds.Value.MinPoint; maxPoint = bounds.Value.MaxPoint; } }
                        catch { }
                    }
                    foreach (ObjectId attId in reference.AttributeCollection)
                    {
                        var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                        if (att == null) continue;
                        rows.Add(new AttributeTarget { BlockId = id, AttributeId = attId, BlockHandle = id.Handle.ToString(), BlockName = name, Tag = att.Tag ?? string.Empty, OldValue = att.TextString ?? string.Empty, Center = position, MinPoint = minPoint, MaxPoint = maxPoint, AttributePosition = att.Position });
                    }
                }
                tr.Commit();
            }
            return Sort(rows, order);
        }

        public static List<AttributeTarget> Sort(IEnumerable<AttributeTarget> rows, AttributeSortOrder order, double? manualTolerance = null)
        {
            var source = (rows ?? Enumerable.Empty<AttributeTarget>()).ToList();
            if (source.Count < 2) return source;
            // LeftThenTop is row-major: top row left-to-right, then the next
            // row. TopThenLeft is column-major: left column top-to-bottom,
            // then the next column.
            return order == AttributeSortOrder.TopThenLeft
                ? SortByBands(source, false, manualTolerance ?? BandTolerance(source.Select(x => x.Center.X)))
                : SortByBands(source, true, manualTolerance ?? BandTolerance(source.Select(x => x.Center.Y)));
        }

        private static List<AttributeTarget> SortByBands(List<AttributeTarget> source, bool rows, double tolerance)
        {
            var ordered = rows
                ? source.OrderByDescending(x => x.Center.Y).ThenBy(x => x.Center.X)
                : source.OrderBy(x => x.Center.X).ThenByDescending(x => x.Center.Y);
            var bands = new List<SpatialBand>();
            foreach (var target in ordered)
            {
                var coordinate = rows ? target.Center.Y : target.Center.X;
                var band = bands.FirstOrDefault(x => Math.Abs(x.Anchor - coordinate) <= tolerance);
                if (band == null) { band = new SpatialBand { Anchor = coordinate }; bands.Add(band); }
                band.Items.Add(target);
                band.Anchor = band.Items.Average(x => rows ? x.Center.Y : x.Center.X);
            }
            var result = new List<AttributeTarget>(source.Count);
            foreach (var band in rows ? bands.OrderByDescending(x => x.Anchor) : bands.OrderBy(x => x.Anchor))
            {
                result.AddRange(rows
                    ? band.Items.OrderBy(x => x.Center.X).ThenByDescending(x => x.Center.Y).ThenBy(x => x.BlockHandle, StringComparer.OrdinalIgnoreCase)
                    : band.Items.OrderByDescending(x => x.Center.Y).ThenBy(x => x.Center.X).ThenBy(x => x.BlockHandle, StringComparer.OrdinalIgnoreCase));
            }
            return result;
        }

        private static double BandTolerance(IEnumerable<double> coordinates)
        {
            var values = coordinates.OrderBy(x => x).Distinct().ToList();
            var gaps = ConsecutiveGaps(values).Where(x => x > 1e-6).OrderBy(x => x).ToList();
            if (gaps.Count == 0) return 1e-6;
            if (gaps.Count == 1) return Math.Max(1e-6, gaps[0] * 0.25d);
            for (var i = 1; i < gaps.Count; i++)
                if (gaps[i] > gaps[i - 1] * 3d)
                    return Math.Max(1e-6, (gaps[i] + gaps[i - 1]) * 0.5d);
            return Math.Max(1e-6, gaps[0] * 0.25d);
        }

        private static IEnumerable<double> ConsecutiveGaps(IList<double> values)
        {
            for (var i = 1; i < values.Count; i++) yield return Math.Abs(values[i] - values[i - 1]);
        }

        private sealed class SpatialBand
        {
            public double Anchor { get; set; }
            public List<AttributeTarget> Items { get; } = new List<AttributeTarget>();
        }

        public static string BuildValue(string seed, int index, bool increment, bool alphabetic, int step = 1, bool reverse = false)
        {
            return BuildValue(seed, index, increment,
                alphabetic ? AttributeNumberingStyle.LatinUpper : AttributeNumberingStyle.Arabic, step, reverse);
        }

        public static string BuildValue(string seed, int index, bool increment, AttributeNumberingStyle style, int step = 1, bool reverse = false)
        {
            if (!increment) return seed ?? string.Empty;
            seed = seed ?? string.Empty;
            var offset = (long)Math.Max(0, index) * Math.Max(1, step);
            if (reverse) offset = -offset;
            if (style == AttributeNumberingStyle.Arabic)
            {
                var numericMatch = Regex.Match(seed, "\\d+$");
                if (!numericMatch.Success) return reverse ? seed : seed + (offset + 1).ToString(CultureInfo.InvariantCulture);
                return seed.Substring(0, numericMatch.Index) + IncrementNumber(numericMatch.Value, offset);
            }
            string prefix;
            long initial;
            int padding;
            if (!TryExtractNumberingToken(seed, style, out prefix, out initial, out padding))
            {
                if (reverse) return seed;
                prefix = seed;
                initial = 1;
                padding = 0;
            }
            var value = initial + offset;
            if (value < 1) value = 1;
            return prefix + FormatNumberingValue(value, style, padding);
        }

        public static string BuildComposedValue(string fixedContent, string prefix, string suffix, int index,
            bool increment, bool prefixIncrement, bool suffixIncrement, AttributeNumberingStyle style, int step = 1, bool reverse = false)
        {
            var prefixValue = BuildValue(prefix, index, increment && prefixIncrement, style, step, reverse);
            var suffixValue = BuildValue(suffix, index, increment && suffixIncrement, style, step, reverse);
            return prefixValue + (fixedContent ?? string.Empty) + suffixValue;
        }

        public static IList<string> RunRegressionChecks()
        {
            var failures = new List<string>();
            Check(failures, "数字递增", BuildValue("建施01", 1, true, false) == "建施02");
            Check(failures, "数字步长", BuildValue("建施01", 1, true, false, 2) == "建施03");
            Check(failures, "数字反向", BuildValue("建施10", 1, true, false, 2, true) == "建施08");
            Check(failures, "无数字起始值", BuildValue("建施", 1, true, false) == "建施2");
            Check(failures, "字母进位", BuildValue("Z", 1, true, true) == "AA");
            Check(failures, "字母反向", BuildValue("D", 3, true, true, 1, true) == "A");
            Check(failures, "大整数", BuildValue("999999999999999999", 1, true, false) == "1000000000000000000");
            Check(failures, "罗马数字", BuildValue("III", 1, true, AttributeNumberingStyle.RomanUpper) == "IV");
            Check(failures, "中文数字", BuildValue("十", 1, true, AttributeNumberingStyle.ChineseLower) == "十一");
            Check(failures, "中文大写", BuildValue("贰", 1, true, AttributeNumberingStyle.ChineseFinancial) == "叁");
            Check(failures, "带圈数字", BuildValue("⑳", 1, true, AttributeNumberingStyle.CircledNumber) == "㉑");
            Check(failures, "带圈字母进位", BuildValue("Ⓩ", 1, true, AttributeNumberingStyle.CircledLatinUpper) == "ⒶⒶ");
            Check(failures, "括号数字", BuildValue("(9)", 1, true, AttributeNumberingStyle.AsciiParenthesizedNumber) == "(10)");
            Check(failures, "默认后缀递增", BuildComposedValue("图纸", "", "", 1, true, false, true, AttributeNumberingStyle.Arabic) == "图纸2");
            Check(failures, "前缀递增", BuildComposedValue("图纸", "", "", 1, true, true, false, AttributeNumberingStyle.Arabic) == "2图纸");
            Check(failures, "前后缀递增", BuildComposedValue("图纸", "1", "1", 1, true, true, true, AttributeNumberingStyle.Arabic) == "2图纸2");
            Check(failures, "关闭递增", BuildComposedValue("图纸", "前", "后", 1, false, false, true, AttributeNumberingStyle.Arabic) == "前图纸后");

            var points = new[]
            {
                RegressionTarget("左上", 0, 10), RegressionTarget("右下", 10, 0),
                RegressionTarget("右上", 10, 10), RegressionTarget("左下", 0, 0)
            };
            Check(failures, "先左右后上下", string.Join(",", Sort(points, AttributeSortOrder.LeftThenTop, 1).Select(x => x.BlockHandle)) == "左上,右上,左下,右下");
            Check(failures, "先上下后左右", string.Join(",", Sort(points, AttributeSortOrder.TopThenLeft, 1).Select(x => x.BlockHandle)) == "左上,左下,右上,右下");
            return failures;
        }

        private static AttributeTarget RegressionTarget(string name, double x, double y) =>
            new AttributeTarget { BlockHandle = name, Center = new Point3d(x, y, 0) };

        private static void Check(ICollection<string> failures, string name, bool passed)
        {
            if (!passed) failures.Add(name);
        }

        public static AttributeApplyResult Apply(Document document, IEnumerable<AttributeTarget> targets)
        {
            var result = new AttributeApplyResult();
            var list = targets?.Where(x => x != null && IsUsable(x.BlockId) && IsUsable(x.AttributeId)).ToList() ?? new List<AttributeTarget>();
            if (list.Count == 0) return result;
            using (document.LockDocument())
            {
                using (var tr = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (var target in list)
                    {
                        var oldValue = target.OldValue ?? string.Empty;
                        var newValue = target.NewValue ?? string.Empty;
                        try
                        {
                            var att = tr.GetObject(target.AttributeId, OpenMode.ForRead) as AttributeReference;
                            if (att == null) { result.Failed++; result.Details.Add(new AttributeApplyDetail { Target = target, Status = "失败", Message = "属性对象不存在或已失效", OldValue = oldValue, NewValue = newValue }); continue; }
                            oldValue = att.TextString ?? string.Empty;
                            if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) { result.Skipped++; result.Details.Add(new AttributeApplyDetail { Target = target, Status = "跳过", Message = "新旧值相同", OldValue = oldValue, NewValue = newValue }); continue; }
                            att.UpgradeOpen();
                            att.TextString = newValue;
                            result.Changed++;
                            result.ChangedAttributeIds.Add(target.AttributeId);
                            result.Details.Add(new AttributeApplyDetail { Target = target, Status = "成功", Message = string.Empty, OldValue = oldValue, NewValue = newValue });
                        }
                        catch (Exception ex)
                        {
                            result.Failed++;
                            result.Errors.Add(target.BlockHandle + " / " + target.Tag + "：" + ex.Message);
                            result.Details.Add(new AttributeApplyDetail { Target = target, Status = "失败", Message = ex.Message, OldValue = oldValue, NewValue = newValue });
                            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BatchPdfPublisher.attribute.log"), DateTime.Now.ToString("s") + " | " + target.BlockHandle + " | " + target.Tag + " | " + ex.Message + Environment.NewLine); } catch { }
                        }
                    }
                    tr.Commit();
                }
            }
            return result;
        }

        public static bool IsUsable(ObjectId id)
        {
            try { return id.IsValid && !id.IsNull && !id.IsErased; }
            catch { return false; }
        }

        private static string IncrementNumber(string token, long offset)
        {
            if (!BigInteger.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return token;
            value += offset;
            if (value < BigInteger.Zero) value = BigInteger.Zero;
            return value.ToString(new string('0', token.Length), CultureInfo.InvariantCulture);
        }

        private static string GetBlockName(BlockReference reference, Transaction tr)
        {
            var id = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
            return (tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord)?.Name ?? string.Empty;
        }

        private static string IncrementLetters(string value, long offset)
        {
            var upper = value.All(char.IsUpper);
            var number = BigInteger.Zero;
            foreach (var ch in value) number = number * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            number += offset;
            if (number < BigInteger.One) number = BigInteger.One;
            var result = new StringBuilder();
            while (number > 0) { number--; result.Insert(0, (char)((upper ? 'A' : 'a') + (int)(number % 26))); number /= 26; }
            return result.ToString();
        }

        private static bool TryExtractNumberingToken(string seed, AttributeNumberingStyle style, out string prefix, out long value, out int padding)
        {
            prefix = seed ?? string.Empty;
            value = 1;
            padding = 0;
            Match match;
            switch (style)
            {
                case AttributeNumberingStyle.Arabic:
                    match = Regex.Match(prefix, "\\d+$");
                    if (!match.Success || !long.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return false;
                    padding = match.Value.Length;
                    prefix = prefix.Substring(0, match.Index);
                    return true;
                case AttributeNumberingStyle.LatinUpper:
                case AttributeNumberingStyle.LatinLower:
                    match = Regex.Match(prefix, "[A-Za-z]+$");
                    if (!match.Success) return false;
                    value = BijectiveToNumber(match.Value, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
                    prefix = prefix.Substring(0, match.Index);
                    return value > 0;
                case AttributeNumberingStyle.RomanUpper:
                case AttributeNumberingStyle.RomanLower:
                    match = Regex.Match(prefix, "[IVXLCDMivxlcdm]+$");
                    if (!match.Success || !TryParseRoman(match.Value, out value)) return false;
                    prefix = prefix.Substring(0, match.Index);
                    return true;
                case AttributeNumberingStyle.ChineseLower:
                case AttributeNumberingStyle.ChineseFinancial:
                    match = Regex.Match(prefix, "[零〇一二三四五六七八九十百千万亿壹贰叁肆伍陆柒捌玖拾佰仟萬億]+$");
                    if (!match.Success || !TryParseChinese(match.Value, out value)) return false;
                    prefix = prefix.Substring(0, match.Index);
                    return true;
                case AttributeNumberingStyle.CircledNumber:
                case AttributeNumberingStyle.ParenthesizedNumber:
                case AttributeNumberingStyle.BlackCircledNumber:
                case AttributeNumberingStyle.DoubleCircledNumber:
                case AttributeNumberingStyle.DingbatCircledNumber:
                    if (!TryParseEnclosedNumberSuffix(prefix, style, out match, out value)) return false;
                    prefix = prefix.Substring(0, match.Index);
                    return true;
                case AttributeNumberingStyle.AsciiParenthesizedNumber:
                    return TryParseWrappedNumber(seed, "\\((\\d+)\\)$", out prefix, out value);
                case AttributeNumberingStyle.FullWidthParenthesizedNumber:
                    return TryParseWrappedNumber(seed, "（(\\d+)）$", out prefix, out value);
                case AttributeNumberingStyle.SquareBracketNumber:
                    return TryParseWrappedNumber(seed, "\\[(\\d+)\\]$", out prefix, out value);
                case AttributeNumberingStyle.FullWidthChineseNumber:
                    match = Regex.Match(prefix, "（([零〇一二三四五六七八九十百千万亿]+)）$");
                    if (!match.Success || !TryParseChinese(match.Groups[1].Value, out value)) return false;
                    prefix = prefix.Substring(0, match.Index);
                    return true;
                case AttributeNumberingStyle.CircledLatinUpper:
                    return TryParseEnclosedLetters(seed, 'Ⓐ', 'Ⓩ', out prefix, out value);
                case AttributeNumberingStyle.CircledLatinLower:
                    return TryParseEnclosedLetters(seed, 'ⓐ', 'ⓩ', out prefix, out value);
                case AttributeNumberingStyle.ParenthesizedLatinLower:
                    return TryParseEnclosedLetters(seed, '⒜', '⒵', out prefix, out value);
                case AttributeNumberingStyle.AsciiParenthesizedLatinUpper:
                    match = Regex.Match(prefix, "\\(([A-Za-z]+)\\)$");
                    if (!match.Success) return false;
                    value = BijectiveToNumber(match.Groups[1].Value, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
                    prefix = prefix.Substring(0, match.Index);
                    return value > 0;
                case AttributeNumberingStyle.HeavenlyStems:
                    return TryParseAlphabetSuffix(seed, "甲乙丙丁戊己庚辛壬癸", out prefix, out value);
                case AttributeNumberingStyle.EarthlyBranches:
                    return TryParseAlphabetSuffix(seed, "子丑寅卯辰巳午未申酉戌亥", out prefix, out value);
                case AttributeNumberingStyle.ChineseOrdinal:
                    match = Regex.Match(prefix, "第([零〇一二三四五六七八九十百千万亿]+)$");
                    if (!match.Success || !TryParseChinese(match.Groups[1].Value, out value)) return false;
                    prefix = prefix.Substring(0, match.Index);
                    return true;
                default:
                    return false;
            }
        }

        private static string FormatNumberingValue(long value, AttributeNumberingStyle style, int padding)
        {
            switch (style)
            {
                case AttributeNumberingStyle.Arabic:
                    return value.ToString(padding > 1 ? new string('0', padding) : "0", CultureInfo.InvariantCulture);
                case AttributeNumberingStyle.LatinUpper:
                    return NumberToBijective(value, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
                case AttributeNumberingStyle.LatinLower:
                    return NumberToBijective(value, "abcdefghijklmnopqrstuvwxyz");
                case AttributeNumberingStyle.RomanUpper:
                    return ToRoman(value);
                case AttributeNumberingStyle.RomanLower:
                    return ToRoman(value).ToLowerInvariant();
                case AttributeNumberingStyle.ChineseLower:
                    return ToChinese(value, false);
                case AttributeNumberingStyle.ChineseFinancial:
                    return ToChinese(value, true);
                case AttributeNumberingStyle.CircledNumber:
                    return FormatCircledNumber(value);
                case AttributeNumberingStyle.ParenthesizedNumber:
                    return value <= 20 ? char.ConvertFromUtf32(0x2473 + (int)value) : "(" + value.ToString(CultureInfo.InvariantCulture) + ")";
                case AttributeNumberingStyle.BlackCircledNumber:
                    return value <= 10 ? char.ConvertFromUtf32(0x2775 + (int)value) : "●" + value.ToString(CultureInfo.InvariantCulture);
                case AttributeNumberingStyle.DoubleCircledNumber:
                    return value <= 10 ? char.ConvertFromUtf32(0x24F4 + (int)value) : "◎" + value.ToString(CultureInfo.InvariantCulture);
                case AttributeNumberingStyle.DingbatCircledNumber:
                    return value <= 10 ? char.ConvertFromUtf32(0x2789 + (int)value) : "➊" + value.ToString(CultureInfo.InvariantCulture);
                case AttributeNumberingStyle.AsciiParenthesizedNumber:
                    return "(" + value.ToString(CultureInfo.InvariantCulture) + ")";
                case AttributeNumberingStyle.FullWidthParenthesizedNumber:
                    return "（" + value.ToString(CultureInfo.InvariantCulture) + "）";
                case AttributeNumberingStyle.SquareBracketNumber:
                    return "[" + value.ToString(CultureInfo.InvariantCulture) + "]";
                case AttributeNumberingStyle.FullWidthChineseNumber:
                    return "（" + ToChinese(value, false) + "）";
                case AttributeNumberingStyle.CircledLatinUpper:
                    return ConvertBijectiveToEnclosed(value, 'Ⓐ');
                case AttributeNumberingStyle.CircledLatinLower:
                    return ConvertBijectiveToEnclosed(value, 'ⓐ');
                case AttributeNumberingStyle.ParenthesizedLatinLower:
                    return ConvertBijectiveToEnclosed(value, '⒜');
                case AttributeNumberingStyle.AsciiParenthesizedLatinUpper:
                    return "(" + NumberToBijective(value, "ABCDEFGHIJKLMNOPQRSTUVWXYZ") + ")";
                case AttributeNumberingStyle.HeavenlyStems:
                    return NumberToBijective(value, "甲乙丙丁戊己庚辛壬癸");
                case AttributeNumberingStyle.EarthlyBranches:
                    return NumberToBijective(value, "子丑寅卯辰巳午未申酉戌亥");
                case AttributeNumberingStyle.ChineseOrdinal:
                    return "第" + ToChinese(value, false);
                default:
                    return value.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string FormatCircledNumber(long value)
        {
            if (value >= 1 && value <= 20) return char.ConvertFromUtf32(0x245F + (int)value);
            if (value >= 21 && value <= 35) return char.ConvertFromUtf32(0x323C + (int)value);
            if (value >= 36 && value <= 50) return char.ConvertFromUtf32(0x328D + (int)value);
            return "○" + value.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryParseEnclosedNumberSuffix(string seed, AttributeNumberingStyle style, out Match match, out long value)
        {
            var fallbackPattern = style == AttributeNumberingStyle.CircledNumber ? "○(\\d+)$"
                : style == AttributeNumberingStyle.ParenthesizedNumber ? "\\((\\d+)\\)$"
                : style == AttributeNumberingStyle.BlackCircledNumber ? "●(\\d+)$"
                : style == AttributeNumberingStyle.DoubleCircledNumber ? "◎(\\d+)$"
                : "➊(\\d+)$";
            match = Regex.Match(seed ?? string.Empty, fallbackPattern);
            if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0)
                return true;
            match = Regex.Match(seed ?? string.Empty, ".$");
            value = 0;
            if (!match.Success) return false;
            var code = char.ConvertToUtf32(match.Value, 0);
            if (style == AttributeNumberingStyle.CircledNumber)
            {
                if (code >= 0x2460 && code <= 0x2473) value = code - 0x245F;
                else if (code >= 0x3251 && code <= 0x325F) value = code - 0x323C;
                else if (code >= 0x32B1 && code <= 0x32BF) value = code - 0x328D;
            }
            else if (style == AttributeNumberingStyle.ParenthesizedNumber && code >= 0x2474 && code <= 0x2487) value = code - 0x2473;
            else if (style == AttributeNumberingStyle.BlackCircledNumber && code >= 0x2776 && code <= 0x277F) value = code - 0x2775;
            else if (style == AttributeNumberingStyle.DoubleCircledNumber && code >= 0x24F5 && code <= 0x24FE) value = code - 0x24F4;
            else if (style == AttributeNumberingStyle.DingbatCircledNumber && code >= 0x278A && code <= 0x2793) value = code - 0x2789;
            return value > 0;
        }

        private static bool TryParseWrappedNumber(string seed, string pattern, out string prefix, out long value)
        {
            prefix = seed ?? string.Empty;
            value = 0;
            var match = Regex.Match(prefix, pattern);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, out value)) return false;
            prefix = prefix.Substring(0, match.Index);
            return value > 0;
        }

        private static bool TryParseEnclosedLetters(string seed, char first, char last, out string prefix, out long value)
        {
            prefix = seed ?? string.Empty;
            value = 0;
            var match = Regex.Match(prefix, "[" + first + "-" + last + "]+$");
            if (!match.Success) return false;
            foreach (var ch in match.Value) value = value * 26 + ch - first + 1;
            prefix = prefix.Substring(0, match.Index);
            return value > 0;
        }

        private static string ConvertBijectiveToEnclosed(long value, char first)
        {
            var plain = NumberToBijective(value, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
            return new string(plain.Select(ch => (char)(first + ch - 'A')).ToArray());
        }

        private static bool TryParseAlphabetSuffix(string seed, string alphabet, out string prefix, out long value)
        {
            prefix = seed ?? string.Empty;
            value = 0;
            var match = Regex.Match(prefix, "[" + alphabet + "]+$");
            if (!match.Success) return false;
            value = BijectiveToNumber(match.Value, alphabet);
            prefix = prefix.Substring(0, match.Index);
            return value > 0;
        }

        private static long BijectiveToNumber(string text, string alphabet)
        {
            long value = 0;
            foreach (var raw in text)
            {
                var ch = alphabet.Length == 26 ? char.ToUpperInvariant(raw) : raw;
                var index = alphabet.IndexOf(ch);
                if (index < 0) return 0;
                checked { value = value * alphabet.Length + index + 1; }
            }
            return value;
        }

        private static string NumberToBijective(long value, string alphabet)
        {
            if (value < 1) value = 1;
            var result = new StringBuilder();
            while (value > 0)
            {
                value--;
                result.Insert(0, alphabet[(int)(value % alphabet.Length)]);
                value /= alphabet.Length;
            }
            return result.ToString();
        }

        private static string ToRoman(long value)
        {
            if (value < 1) value = 1;
            var values = new[] { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            var symbols = new[] { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            var result = new StringBuilder();
            for (var i = 0; i < values.Length; i++)
                while (value >= values[i]) { result.Append(symbols[i]); value -= values[i]; }
            return result.ToString();
        }

        private static bool TryParseRoman(string text, out long value)
        {
            value = 0;
            var previous = 0;
            for (var i = text.Length - 1; i >= 0; i--)
            {
                var current = RomanDigit(char.ToUpperInvariant(text[i]));
                if (current == 0) return false;
                value += current < previous ? -current : current;
                if (current > previous) previous = current;
            }
            return value > 0;
        }

        private static int RomanDigit(char ch)
        {
            switch (ch)
            {
                case 'I': return 1; case 'V': return 5; case 'X': return 10; case 'L': return 50;
                case 'C': return 100; case 'D': return 500; case 'M': return 1000; default: return 0;
            }
        }

        private static string ToChinese(long value, bool financial)
        {
            if (value <= 0) return financial ? "零" : "零";
            var digits = financial ? "零壹贰叁肆伍陆柒捌玖" : "零一二三四五六七八九";
            var smallUnits = financial ? new[] { "", "拾", "佰", "仟" } : new[] { "", "十", "百", "千" };
            var groups = new[] { "", "万", "亿", "万亿" };
            var parts = new List<int>();
            while (value > 0) { parts.Add((int)(value % 10000)); value /= 10000; }
            var result = new StringBuilder();
            var pendingZero = false;
            for (var group = parts.Count - 1; group >= 0; group--)
            {
                var part = parts[group];
                if (part == 0) { pendingZero = result.Length > 0; continue; }
                if (result.Length > 0 && (pendingZero || part < 1000)) result.Append(digits[0]);
                result.Append(FormatChineseGroup(part, digits, smallUnits));
                if (group < groups.Length) result.Append(groups[group]);
                pendingZero = false;
            }
            var text = result.ToString();
            if (!financial && text.StartsWith("一十", StringComparison.Ordinal)) text = text.Substring(1);
            return text;
        }

        private static string FormatChineseGroup(int value, string digits, string[] units)
        {
            var result = new StringBuilder();
            var zeroPending = false;
            for (var position = 3; position >= 0; position--)
            {
                var divisor = (int)Math.Pow(10, position);
                var digit = value / divisor % 10;
                if (digit == 0) { if (result.Length > 0) zeroPending = true; continue; }
                if (zeroPending) { result.Append(digits[0]); zeroPending = false; }
                result.Append(digits[digit]).Append(units[position]);
            }
            return result.ToString();
        }

        private static bool TryParseChinese(string text, out long value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            long section = 0;
            long number = 0;
            foreach (var ch in text)
            {
                var digit = ChineseDigit(ch);
                if (digit >= 0) { number = digit; continue; }
                var unit = ChineseUnit(ch);
                if (unit == 0) return false;
                if (unit < 10000)
                {
                    if (number == 0) number = 1;
                    section += number * unit;
                }
                else
                {
                    section += number;
                    value += section * unit;
                    section = 0;
                }
                number = 0;
            }
            value += section + number;
            return value > 0;
        }

        private static int ChineseDigit(char ch)
        {
            const string lower = "零一二三四五六七八九";
            const string financial = "零壹贰叁肆伍陆柒捌玖";
            if (ch == '〇') return 0;
            var index = lower.IndexOf(ch);
            if (index >= 0) return index;
            return financial.IndexOf(ch);
        }

        private static long ChineseUnit(char ch)
        {
            switch (ch)
            {
                case '十': case '拾': return 10;
                case '百': case '佰': return 100;
                case '千': case '仟': return 1000;
                case '万': case '萬': return 10000;
                case '亿': case '億': return 100000000;
                default: return 0;
            }
        }
    }
}
