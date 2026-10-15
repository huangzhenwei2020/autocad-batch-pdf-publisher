using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace BatchPdfPublisher.Services
{
    [DataContract]
    internal sealed class RoomStatisticsSnapshot
    {
        [DataMember] public int Version { get; set; }
        [DataMember] public string Cells { get; set; }
        [DataMember] public List<RoomStatisticsItem> Items { get; set; }
    }

    internal static class RoomStatisticsTableService
    {
        private const string Key = "WANLUO_ROOM_STATISTICS_V1";

        public static bool TryRead(Table table, Transaction transaction, out List<RoomStatisticsItem> items, out string warning, bool countsOnly = false)
        {
            var cells = ReadCells(table);
            var source = table.Database.FingerprintGuid + ":table:" + table.Handle;
            if (!RoomStatistics.TryReadTable(cells, source, out items, out warning, countsOnly)) return false;
            try
            {
                if (!table.ExtensionDictionary.IsNull)
                {
                    var dictionary = (DBDictionary)transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead);
                    if (dictionary.Contains(Key))
                    {
                        var record = (Xrecord)transaction.GetObject(dictionary.GetAt(Key), OpenMode.ForRead);
                        string json;
                        using (var data = record.Data)
                            json = string.Concat(data.AsArray().Select(x => Convert.ToString(x.Value, CultureInfo.InvariantCulture)));
                        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                        {
                            var snapshot = (RoomStatisticsSnapshot)new DataContractJsonSerializer(typeof(RoomStatisticsSnapshot)).ReadObject(stream);
                            if (snapshot.Version == 1 && snapshot.Cells == CellSignature(cells) && snapshot.Items != null)
                            {
                                int duplicates;
                                var validated = RoomStatistics.Combine(snapshot.Items, out duplicates);
                                if (SameRows(validated, items, countsOnly || cells.GetLength(1) == 4 || cells[1,2] == "分层数量")) { items = validated; warning = null; return true; }
                            }
                        }
                    }
                }
            }
            catch { }
            warning = "旧表或已手工编辑的表按当前明细汇总，无法核对与其他表是否包含相同房间。";
            return true;
        }

        public static ObjectId Insert(Database database, Transaction transaction, Point3d position,
            IList<RoomStatisticsItem> items, bool summary, double scale, double textHeight)
        {
            if (items == null || items.Count == 0 || double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 ||
                double.IsNaN(textHeight) || double.IsInfinity(textHeight) || textHeight <= 0)
                throw new ArgumentException("统计数据、比例或字高无效。");
            var rows = RoomStatistics.Rows(items, summary);
            var floors = items.Any(x => !string.IsNullOrWhiteSpace(x.FloorName));
            var columns = (summary ? 4 : 5) + (floors ? 1 : 0);
            var table = new Table { TableStyle = database.Tablestyle, Position = position };
            table.SetSize(rows.Count + 3, columns);
            table.SetRowHeight(Math.Max(7d, textHeight * 2.8) * scale);
            var widths = new List<double>(summary ? new[] { 12d, 50d, 20d, 30d } : new[] { 12d, 35d, 15d, 70d, 25d });
            if (floors) widths.Insert(2, 80d);
            for (var column = 0; column < columns; column++) table.Columns[column].Width = widths[column] * scale;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));
            table.Cells[0, 0].TextString = summary ? "房间数量汇总表" : "房间面积统计表";
            var headers = new List<string>(summary ? new[] { "序号", "房间名称", "数量", "数量占比" } : new[] { "序号", "房间名称", "数量", "面积明细（㎡）", "合计面积（㎡）" });
            if (floors) headers.Insert(2, "分层数量");
            for (var column = 0; column < columns; column++) table.Cells[1, column].TextString = headers[column];
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index]; var r = index + 2;
                table.Cells[r, 0].TextString = row.Sequence.ToString(CultureInfo.InvariantCulture);
                table.Cells[r, 1].TextString = row.RoomName;
                var c = 2;
                if (floors) table.Cells[r, c++].TextString = row.FloorQuantitySummary;
                table.Cells[r, c++].TextString = row.Count.ToString(CultureInfo.InvariantCulture);
                table.Cells[r, c++].TextString = summary ? row.CountShare : row.AreaDetails;
                if (!summary) table.Cells[r, c].TextString = row.TotalArea;
                var lines = Math.Max(summary ? 1 : Math.Ceiling(row.AreaDetails.Length * textHeight / 66d), Math.Ceiling(row.RoomName.Length * textHeight / (widths[1] - 4)));
                if (floors) lines = Math.Max(lines, Math.Ceiling(row.FloorQuantitySummary.Length * textHeight / 76d));
                table.Rows[r].Height = Math.Max(7d, (lines + 1) * textHeight * 1.5) * scale;
            }
            var total = rows.Count + 2;
            table.Cells[total, 0].TextString = "合计";
            table.Cells[total, floors ? 3 : 2].TextString = items.Sum(RoomStatistics.Quantity).ToString(CultureInfo.InvariantCulture);
            if (summary) table.Cells[total, columns - 1].TextString = "100.00%";
            else table.Cells[total, columns - 1].TextString = RoomStatistics.Total(items);
            for (var row = 0; row < rows.Count + 3; row++)
                for (var column = 0; column < columns; column++)
                { table.Cells[row, column].TextHeight = textHeight * scale; table.Cells[row, column].Alignment = CellAlignment.MiddleCenter; }
            var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
            space.AppendEntity(table); transaction.AddNewlyCreatedDBObject(table, true);
            table.GenerateLayout();
            table.CreateExtensionDictionary();
            var dictionary = (DBDictionary)transaction.GetObject(table.ExtensionDictionary, OpenMode.ForWrite);
            var snapshot = new RoomStatisticsSnapshot { Version = 1, Cells = CellSignature(ReadCells(table)), Items = items.ToList() };
            string json;
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(RoomStatisticsSnapshot)).WriteObject(stream, snapshot);
                json = Encoding.UTF8.GetString(stream.ToArray());
            }
            var chunks = new List<TypedValue>();
            for (var start = 0; start < json.Length; start += 200)
                chunks.Add(new TypedValue((int)DxfCode.Text, json.Substring(start, Math.Min(200, json.Length - start))));
            var record = new Xrecord();
            using (var buffer = new ResultBuffer(chunks.ToArray())) record.Data = buffer;
            dictionary.SetAt(Key, record); transaction.AddNewlyCreatedDBObject(record, true);
            return table.ObjectId;
        }

        private static bool SameRows(IEnumerable<RoomStatisticsItem> left, IEnumerable<RoomStatisticsItem> right, bool countsOnly)
        {
            Func<IEnumerable<RoomStatisticsItem>, string> signature = items => string.Join("\n", RoomStatistics.Rows(items, countsOnly)
                .Select(x => x.RoomName + "\t" + x.Count + "\t" + x.AreaDetails + "\t" + x.TotalArea + "\t" + x.FloorQuantitySummary));
            return signature(left) == signature(right);
        }

        private static string[,] ReadCells(Table table)
        {
            // Reject unrelated/oversized tables before walking their cells.
            if ((table.Columns.Count < 4 || table.Columns.Count > 6) || table.Rows.Count > 100000) return null;
            var cells = new string[table.Rows.Count, table.Columns.Count];
            for (var r = 0; r < table.Rows.Count; r++)
                for (var c = 0; c < table.Columns.Count; c++) cells[r, c] = table.Cells[r, c].TextString;
            return cells;
        }

        private static string CellSignature(string[,] cells)
        {
            var result = new StringBuilder();
            for (var r = 0; r < cells.GetLength(0); r++)
                for (var c = 0; c < cells.GetLength(1); c++)
                { var value = cells[r, c] ?? ""; result.Append(value.Length).Append(':').Append(value); }
            return result.ToString();
        }
    }
}
