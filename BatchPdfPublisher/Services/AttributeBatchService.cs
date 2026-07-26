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
            if (!increment) return seed ?? string.Empty;
            seed = seed ?? string.Empty;
            var offset = (long)Math.Max(0, index) * Math.Max(1, step);
            if (reverse) offset = -offset;
            var match = Regex.Match(seed, alphabetic ? "[A-Za-z]+$" : "\\d+$");
            if (!match.Success)
            {
                if (reverse) return seed;
                return alphabetic ? seed + IncrementLetters("A", offset) : seed + (offset + 1).ToString(CultureInfo.InvariantCulture);
            }
            var prefix = seed.Substring(0, match.Index);
            var token = match.Value;
            if (alphabetic) return prefix + IncrementLetters(token, offset);
            return prefix + IncrementNumber(token, offset);
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
    }
}
