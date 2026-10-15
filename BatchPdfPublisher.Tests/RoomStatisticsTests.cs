using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.Services;

internal static class RoomStatisticsTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Main()
    {
        var rooms = new[] {
            RoomStatistics.Room("a", " 卧室 ", 12.001, ""), RoomStatistics.Room("b", "卧室", 12.004, ""),
            RoomStatistics.Room("c", "卧室", 15, ""), RoomStatistics.Room("d", "客厅", 30, "") };
        var rows = RoomStatistics.Rows(rooms);
        var bedroom = rows.Single(x => x.RoomName == "卧室");
        Check(bedroom.Count == 3 && bedroom.AreaDetails == "12×2；15×1" && bedroom.TotalArea == "39", "name/area grouping");
        Check(RoomStatistics.Total(rooms) == "69", "total");
        Check(RoomStatistics.Room("tie", "", 1.005, "").Area == 1.01m, "rounding");
        Check(RoomStatistics.Room("nan", "未知", double.NaN, "").Area == null, "NaN");
        Check(RoomStatistics.Total(new[] { RoomStatistics.Room("u", "未知", null, "") }) == "未知", "unknown is not zero");
        int duplicates;
        Check(RoomStatistics.Combine(rooms.Concat(rooms.Take(2)), out duplicates).Count == 4 && duplicates == 2, "overlapping summary");
        try { RoomStatistics.Combine(rooms.Concat(new[] { RoomStatistics.Room("a", "卧室", 13, "") }), out duplicates); throw new Exception("conflict accepted"); }
        catch (InvalidOperationException) { }
        var old = new string[,] {
            { "房间面积统计表", "", "", "", "" }, { "序号", "房间名称", "数量", "面积明细", "合计面积" },
            { "1", "卧室", "3", "12×2；15×1", "39" }, { "2", "客厅", "1", "30×1", "30" },
            { "合计", "", "4", "", "69" } };
        List<RoomStatisticsItem> parsed; string error;
        Check(RoomStatistics.TryReadTable(old, "old", out parsed, out error) && parsed.Sum(x => x.Count) == 4, "legacy table");
        Check(RoomStatistics.Total(parsed) == "69", "legacy area");
        Check(RoomStatistics.Combine(parsed.Concat(parsed), out duplicates).Sum(x => x.Count) == 4 && duplicates == 4, "legacy summary provenance");
        old[0, 0] = "房间面积总统计表"; old[1, 3] += "（㎡）"; old[1, 4] += "（㎡）";
        Check(RoomStatistics.TryReadTable(old, "summary", out parsed, out error), "new table");
        old[2, 2] = "4";
        Check(!RoomStatistics.TryReadTable(old, "bad", out parsed, out error) && parsed.Count == 0, "inconsistent counts");
        old[2, 2] = "3"; old[2, 3] = "-12×2；15×1";
        Check(!RoomStatistics.TryReadTable(old, "bad", out parsed, out error), "negative area");
        old[2, 3] = "未知×2；15×1";
        Check(RoomStatistics.TryReadTable(old, "unknown", out parsed, out error) && RoomStatistics.Total(parsed) == "未知", "unknown legacy");
        old[2, 3] = "12㎡×2;15 m2×1";
        Check(RoomStatistics.TryReadTable(old, "units", out parsed, out error) && RoomStatistics.Total(parsed) == "69", "units");
        old[0, 0] = "门窗统计表";
        Check(!RoomStatistics.TryReadTable(old, "unrelated", out parsed, out error), "unrelated table");
        var mixed = rooms.Concat(new[] {
            RoomStatistics.Room("stair", "1号楼梯间", 20, ""), RoomStatistics.Room("water", "水井", 2, ""),
            RoomStatistics.Room("power", "强电井", 2, ""), RoomStatistics.Room("lobby", "消防前室", 15, ""),
            RoomStatistics.Room("lift", "电梯厅", 15, "") }).ToList();
        Check(RoomStatistics.Filter(mixed, 0, RoomStatistics.DefaultKeywords).Count == 4, "default exclusion substring");
        Check(RoomStatistics.Filter(mixed, 1, "卧室， 客厅;").Count == 4, "include keywords");
        Check(RoomStatistics.Filter(mixed, 1, "").Count == 0, "empty include");
        Check(RoomStatistics.Filter(mixed, 0, "").Count == 9, "empty exclusion");
        Check(RoomStatistics.Filter(mixed, 2, RoomStatistics.DefaultKeywords).Count == 9, "all rooms");
        var counts = RoomStatistics.Rows(rooms, true);
        Check(counts.Single(x => x.RoomName == "卧室").CountShare == "75.00%", "count share");
        Check(counts.All(x => x.AreaDetails == "" && x.TotalArea == ""), "summary has no area");
        Check(RoomStatistics.Share(1, 3) == "33.33%" && RoomStatistics.Share(0, 0) == "0.00%", "share rounding/empty");
        var quantity = new string[,] { { "房间数量汇总表", "", "", "" }, { "序号", "房间名称", "数量", "数量占比" },
            { "1", "卧室", "3", "75.00%" }, { "2", "客厅", "1", "25.00%" }, { "合计", "", "4", "100.00%" } };
        Check(RoomStatistics.TryReadTable(quantity, "quantity", out parsed, out error) && parsed.Sum(x => x.Count) == 4, "quantity summary reread");
        quantity[2, 3] = "99.00%";
        Check(RoomStatistics.TryReadTable(quantity, "quantity", out parsed, out error) && RoomStatistics.Rows(parsed, true).Single(x => x.RoomName == "卧室").CountShare == "75.00%", "recompute old percentages");
        old[0, 0] = "房间面积统计表"; old[2, 3] = "无效面积明细";
        Check(RoomStatistics.TryReadTable(old, "countOld", out parsed, out error, true) && parsed.Sum(x => x.Count) == 4, "legacy counts ignore areas");
        Check(!RoomStatistics.TryReadTable(new string[0,4], "empty", out parsed, out error), "empty table");
        Check(RoomStatistics.Combine(rooms.Concat(new[] { RoomStatistics.Room("a", "卧室", 13, "") }), out duplicates, true).Count == 4, "count only area conflict ignored");
        int layers;
        Check(FloorStatisticsRules.TryParseFloorCount("3~10层", out layers) && layers == 8, "shared standard floor range");
        Check(FloorStatisticsRules.TryParseFloorCount("10至3层", out layers) && layers == 8, "reverse range");
        Check(!FloorStatisticsRules.TryParseFloorCount("1~1001层", out layers), "range bounded");
        var floors = new[] { RoomStatistics.WithFloor(rooms[0], "1层", 1), RoomStatistics.WithFloor(rooms[1], "3~10层", 8), RoomStatistics.WithFloor(rooms[3], "1层", 1) };
        var floorRows = RoomStatistics.Rows(floors, true);
        Check(floorRows.Single(x=>x.RoomName=="卧室").Count == 9 && floorRows.Single(x=>x.RoomName=="卧室").CountShare=="90.00%", "weighted floor share");
        Check(floorRows.Single(x=>x.RoomName=="卧室").FloorQuantitySummary.Contains("3~10层（8层）:1×8=8"), "floor details");
        Check(RoomStatistics.Total(floors) == "138", "weighted area");
        Check(RoomStatistics.Combine(floors.Concat(floors), out duplicates, true).Sum(RoomStatistics.Quantity)==10 && duplicates==10, "weighted dedup");
        var sameRoomOtherFloor = RoomStatistics.WithFloor(rooms[0], "2层", 1);
        Check(RoomStatistics.Combine(new[]{floors[0],sameRoomOtherFloor},out duplicates,true).Count==2, "template on separate floors");
        var floorTable = new string[,] { { "房间数量汇总表","","","","" }, { "序号","房间名称","分层数量","数量","数量占比" },
            { "1","卧室","1层（1层）:1×1=1；3~10层（8层）:1×8=8","9","90.00%" },
            { "2","客厅","1层（1层）:1×1=1","1","10.00%" }, { "合计","","","10","100.00%" } };
        Check(RoomStatistics.TryReadTable(floorTable,"floors",out parsed,out error,true) && parsed.Sum(RoomStatistics.Quantity)==10, "floor table no double multiply");
        floorTable[2,3]="72";
        Check(!RoomStatistics.TryReadTable(floorTable,"badfloor",out parsed,out error,true), "floor totals validated");
        Console.WriteLine("ROOM_STATISTICS_TESTS_OK grouping legacy filter countShare floors sharedRange weighted dedup");
    }
}
