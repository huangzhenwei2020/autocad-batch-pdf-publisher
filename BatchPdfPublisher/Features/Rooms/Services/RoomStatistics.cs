using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace BatchPdfPublisher.Services
{
    [DataContract]
    internal sealed class RoomStatisticsItem
    {
        [DataMember] public string Source { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public decimal? Area { get; set; }
        [DataMember] public string AreaLabel { get; set; }
        [DataMember] public int Count { get; set; }
        [DataMember] public string FloorName { get; set; }
        [DataMember] public int FloorCount { get; set; }
        [DataMember] public string FloorSource { get; set; }
    }

    internal sealed class RoomStatisticsRow
    {
        public bool Included { get; set; } = true;
        public int Sequence { get; set; }
        public string RoomName { get; set; }
        public int Count { get; set; }
        public string AreaDetails { get; set; }
        public string TotalArea { get; set; }
        public string CountShare { get; set; }
        public string FloorQuantitySummary { get; set; }
    }

    internal static class RoomStatistics
    {
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        public const string DefaultKeywords = "楼梯、水井、电井、前室、电梯";
        public static int Layers(RoomStatisticsItem item) { return Math.Max(1, item.FloorCount); }
        public static int Quantity(RoomStatisticsItem item) { return checked(item.Count * Layers(item)); }
        public static string Floor(RoomStatisticsItem item) { return string.IsNullOrWhiteSpace(item.FloorName) ? "未标楼层" : item.FloorName.Trim(); }

        public static RoomStatisticsItem WithFloor(RoomStatisticsItem item, string name, int layers)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '；', ';', '\r', '\n' }) >= 0 || layers < 1 || layers > 999)
                throw new InvalidOperationException("请填写楼层名称，层数必须为 1～999；楼层名称不能含分号或换行。");
            int parsed; if (FloorStatisticsRules.TryParseFloorCount(name, out parsed)) layers = parsed;
            return new RoomStatisticsItem { Source = item.Source, Name = item.Name, Area = item.Area, AreaLabel = item.AreaLabel,
                Count = item.Count, FloorName = name.Trim(), FloorCount = layers, FloorSource = item.FloorSource };
        }

        public static List<RoomStatisticsItem> Filter(IEnumerable<RoomStatisticsItem> items, int mode, string keywords)
        {
            var terms = Regex.Split(keywords ?? "", @"[,，、;；\r\n]+").Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return items.Where(item => mode == 2 || (mode == 1
                ? terms.Any(term => Name(item.Name).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                : !terms.Any(term => Name(item.Name).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))).ToList();
        }

        public static string Share(int count, int total)
        {
            return total <= 0 ? "0.00%" : (count * 100m / total).ToString("0.00", Culture) + "%";
        }

        public static RoomStatisticsItem Room(string source, string name, double? area, string label)
        {
            decimal? number = null;
            if (area.HasValue && !double.IsNaN(area.Value) && !double.IsInfinity(area.Value) && area.Value >= 0 && area.Value <= (double)decimal.MaxValue)
            {
                try { number = Math.Round((decimal)area.Value, 2, MidpointRounding.AwayFromZero); }
                catch (OverflowException) { }
            }
            return new RoomStatisticsItem { Source = source, Name = Name(name), Area = number,
                AreaLabel = number.HasValue ? Format(number.Value) : Label(label), Count = 1 };
        }

        public static List<RoomStatisticsItem> Combine(IEnumerable<RoomStatisticsItem> items, out int duplicates, bool countsOnly = false)
        {
            duplicates = 0;
            var sources = new Dictionary<string, RoomStatisticsItem>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.Source) || item.Count <= 0 || item.Area < 0 || item.FloorCount < 0 || item.FloorCount > 999)
                    throw new InvalidOperationException("统计来源或数量无效，请重新框选统计。");
                RoomStatisticsItem previous;
                var key = item.Source.Length + ":" + item.Source + "|" + Floor(item).ToUpperInvariant();
                if (!sources.TryGetValue(key, out previous)) sources.Add(key, item);
                else
                {
                    if (previous.Name != item.Name || previous.Count != item.Count || Layers(previous) != Layers(item) || (!countsOnly && (previous.Area != item.Area || previous.AreaLabel != item.AreaLabel)))
                        throw new InvalidOperationException("同一来源存在不同版本的统计数据，请只选择最新统计表。");
                    duplicates = checked(duplicates + Quantity(item));
                }
            }
            var result = sources.Values.ToList();
            if (result.Where(x => !string.IsNullOrWhiteSpace(x.FloorName)).GroupBy(x => Floor(x), StringComparer.CurrentCultureIgnoreCase).Any(g => g.Select(Layers).Distinct().Count() > 1))
                throw new InvalidOperationException("同一楼层的代表层数不同，请核对楼层设置或只选择最新表。");
            return result;
        }

        public static List<RoomStatisticsRow> Rows(IEnumerable<RoomStatisticsItem> items, bool countsOnly = false)
        {
            var list = items.ToList(); var count = list.Sum(Quantity);
            return list.GroupBy(x => Name(x.Name), StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.CurrentCulture)
                .Select((group, index) => new RoomStatisticsRow
                {
                    Sequence = index + 1, RoomName = group.Key, Count = group.Sum(Quantity),
                    AreaDetails = countsOnly ? "" : string.Join("；", group.GroupBy(x => x.Area.HasValue ? Format(x.Area.Value) : Label(x.AreaLabel))
                        .OrderBy(x => x.First().Area ?? decimal.MaxValue).ThenBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => x.Key + "×" + x.Sum(Quantity).ToString(Culture))),
                    TotalArea = countsOnly ? "" : Total(group), CountShare = Share(group.Sum(Quantity), count),
                    FloorQuantitySummary = FloorSummary(group)
                }).ToList();
        }

        public static string Total(IEnumerable<RoomStatisticsItem> items)
        {
            var list = items.ToList();
            return list.Any(x => !x.Area.HasValue) ? "未知" : Format(list.Sum(x => x.Area.Value * Quantity(x)));
        }

        private static string FloorSummary(IEnumerable<RoomStatisticsItem> items)
        {
            return string.Join("；", items.GroupBy(x => new { Name = Floor(x), Count = Layers(x) }).OrderBy(x => x.Key.Name, StringComparer.Ordinal)
                .Select(g => g.Key.Name + "（" + g.Key.Count + "层）:" + g.Sum(x => x.Count) + "×" + g.Key.Count + "=" + g.Sum(Quantity)));
        }

        public static bool TryReadTable(string[,] cells, string source, out List<RoomStatisticsItem> items, out string error, bool countsOnly = false)
        {
            items = new List<RoomStatisticsItem>(); error = null;
            if (cells != null && cells.GetLength(0) >= 3 && cells.GetLength(1) >= 5 && cells[1, 2] == "分层数量")
                return TryReadFloorTable(cells, source, out items, out error);
            var quantityTable = cells != null && cells.GetLength(0) > 0 && cells.GetLength(1) == 4 && cells[0, 0] == "房间数量汇总表";
            if (cells == null || cells.GetLength(0) < 3 || (!quantityTable && (cells.GetLength(1) != 5 ||
                (cells[0, 0] != "房间面积统计表" && cells[0, 0] != "房间面积总统计表"))))
            { error = "不是房间统计表"; return false; }
            var headers = quantityTable ? new[] { "序号", "房间名称", "数量", "数量占比" } : new[] { "序号", "房间名称", "数量", "面积明细", "合计面积" };
            for (var c = 0; c < headers.Length; c++)
                if ((cells[1, c] ?? "").Replace("（㎡）", "").Trim() != headers[c])
                { error = "表头格式不匹配"; return false; }
            try
            {
                for (var r = 2; r < cells.GetLength(0); r++)
                {
                    var sequence = (cells[r, 0] ?? "").Trim();
                    var name = (cells[r, 1] ?? "").Trim();
                    if (sequence == "合计" || sequence == "总计") continue;
                    if (Enumerable.Range(0, cells.GetLength(1)).All(c => string.IsNullOrWhiteSpace(cells[r, c]))) continue;
                    int count;
                    if (name.Length == 0 || !int.TryParse(cells[r, 2], NumberStyles.None, Culture, out count) || count <= 0)
                        throw new FormatException("房间名称或数量无效");
                    if (quantityTable || countsOnly)
                    {
                        items.Add(new RoomStatisticsItem { Name = name, Count = count, AreaLabel = "未知" });
                        continue;
                    }
                    var rowItems = new List<RoomStatisticsItem>();
                    foreach (var detail in (cells[r, 3] ?? "").Split(new[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var match = Regex.Match(detail.Trim(), @"^(.+)[×xX*]\s*(\d+)\s*$");
                        int quantity;
                        if (!match.Success || !int.TryParse(match.Groups[2].Value, NumberStyles.None, Culture, out quantity) || quantity <= 0)
                            throw new FormatException("面积明细需为“面积×数量”");
                        var label = match.Groups[1].Value.Trim();
                        decimal area;
                        var numericLabel = Regex.Replace(label, @"\s*(㎡|m²|m2|平方米)\s*$", "", RegexOptions.IgnoreCase);
                        var numeric = decimal.TryParse(numericLabel, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, Culture, out area);
                        if (numeric && area < 0) throw new FormatException("面积不能为负数");
                        var item = new RoomStatisticsItem { Name = name, Count = quantity,
                            Area = numeric ? Math.Round(area, 2, MidpointRounding.AwayFromZero) : (decimal?)null,
                            AreaLabel = numeric ? Format(Math.Round(area, 2, MidpointRounding.AwayFromZero)) : Label(label) };
                        rowItems.Add(item);
                    }
                    if (rowItems.Sum(x => x.Count) != count) throw new FormatException("数量与面积明细不一致");
                    items.AddRange(rowItems);
                }
                if (items.Count == 0) throw new FormatException("没有房间统计数据");
                // Legacy tables have no room identities: each table/name/area bucket is one source.
                items = items.GroupBy(x => new { x.Name, x.Area, x.AreaLabel }).Select((g, index) => new RoomStatisticsItem
                { Source = source + ":" + index, Name = g.Key.Name, Area = g.Key.Area, AreaLabel = g.Key.AreaLabel, Count = g.Sum(x => x.Count) }).ToList();
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException)
            { items.Clear(); error = ex.Message; return false; }
        }

        private static bool TryReadFloorTable(string[,] cells, string source, out List<RoomStatisticsItem> items, out string error)
        {
            items = new List<RoomStatisticsItem>(); error = null;
            var summary = cells.GetLength(1) == 5 && cells[0,0] == "房间数量汇总表";
            var headers = summary ? new[] { "序号", "房间名称", "分层数量", "数量", "数量占比" }
                : new[] { "序号", "房间名称", "分层数量", "数量", "面积明细（㎡）", "合计面积（㎡）" };
            if ((!summary && (cells.GetLength(1) != 6 || cells[0,0] != "房间面积统计表")) ||
                Enumerable.Range(0, headers.Length).Any(c => cells[1,c] != headers[c]))
            { error = "分层统计表格式不匹配"; return false; }
            try
            {
                for (var r = 2; r < cells.GetLength(0); r++)
                {
                    if (cells[r,0] == "合计" || cells[r,0] == "总计") continue;
                    if (Enumerable.Range(0, cells.GetLength(1)).All(c => string.IsNullOrWhiteSpace(cells[r,c]))) continue;
                    int total;
                    if (string.IsNullOrWhiteSpace(cells[r,1]) || !int.TryParse(cells[r,3], out total) || total <= 0) throw new FormatException("房间名称或数量无效");
                    var row = new List<RoomStatisticsItem>();
                    foreach (var detail in (cells[r,2] ?? "").Split('；'))
                    {
                        var match = Regex.Match(detail, @"^(.+)（(\d+)层）:(\d+)×(\d+)=(\d+)$");
                        if (!match.Success) throw new FormatException("分层明细格式无效");
                        var layers = int.Parse(match.Groups[2].Value); var perFloor = int.Parse(match.Groups[3].Value);
                        if (layers != int.Parse(match.Groups[4].Value) || checked(layers * perFloor) != int.Parse(match.Groups[5].Value) || perFloor <= 0)
                            throw new FormatException("分层数量与层数不一致");
                        row.Add(WithFloor(new RoomStatisticsItem { Source = source + ":" + r + ":" + row.Count, Name = cells[r,1].Trim(), Count = perFloor, AreaLabel = "未知" }, match.Groups[1].Value, layers));
                    }
                    if (row.Sum(Quantity) != total) throw new FormatException("分层明细与总数量不一致");
                    items.AddRange(row);
                }
                if (items.Count == 0) throw new FormatException("没有房间统计数据");
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is InvalidOperationException)
            { error = ex.Message; items.Clear(); return false; }
        }

        public static string Format(decimal area) { return area.ToString("0.##", Culture); }
        private static string Name(string value) { return string.IsNullOrWhiteSpace(value) ? "未命名" : value.Trim(); }
        private static string Label(string value) { return string.IsNullOrWhiteSpace(value) ? "未知" : value.Trim(); }
    }
}
