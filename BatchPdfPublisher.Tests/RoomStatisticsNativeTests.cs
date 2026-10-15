using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Drawing;
using System.Windows.Forms;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.Services;

[assembly: CommandClass(typeof(RoomStatisticsNativeTests))]
public sealed class RoomStatisticsNativeTests
{
    private static ObjectId _undoTable;

    [CommandMethod("WLROOMUNDOINSERT")]
    public void InsertUndoTest()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (!string.Equals(System.Diagnostics.Process.GetCurrentProcess().ProcessName, "accoreconsole", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WANLUO_ROOM_QA_OUTPUT"))) throw new InvalidOperationException("QA process required");
        using (var tx = doc.Database.TransactionManager.StartTransaction())
        {
            _undoTable = RoomStatisticsTableService.Insert(doc.Database, tx, new Point3d(400, 0, 0),
                new List<RoomStatisticsItem> { RoomStatistics.Room("undo", "卧室", 12, "") }, false, 1, 2.5);
            tx.Commit();
        }
    }

    [CommandMethod("WLROOMUNDOCHECK", CommandFlags.NoUndoMarker)]
    public void CheckUndo()
    {
        Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(_undoTable.IsNull || !_undoTable.IsErased
            ? "\nROOM_STATISTICS_UNDO_FAILED\n" : "\nROOM_STATISTICS_UNDO_OK\n");
    }

    [CommandMethod("WLROOMREDOCHECK", CommandFlags.NoUndoMarker)]
    public void CheckRedo()
    {
        Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(_undoTable.IsNull || _undoTable.IsErased
            ? "\nROOM_STATISTICS_REDO_FAILED\n" : "\nROOM_STATISTICS_REDO_OK\n");
    }
    [CommandMethod("WLROOMSTATQA")]
    public void Run()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        try
        {
            if (!string.Equals(System.Diagnostics.Process.GetCurrentProcess().ProcessName, "accoreconsole", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(document.Database.Filename) != ".dwt") throw new InvalidOperationException("Disposable core console required");
            var folder = Environment.GetEnvironmentVariable("WANLUO_ROOM_QA_OUTPUT");
            if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("QA output required");
            Directory.CreateDirectory(folder);
            var items = new List<RoomStatisticsItem>();
            for (var i = 0; i < 300; i++) items.Add(RoomStatistics.Room("room:" + i, i < 250 ? "卧室" : "客厅", i < 200 ? 12 : 30, ""));
            ObjectId first, summary, legacy, quantity, floorTable, floorAreaTable;
            var db = document.Database;
            using (var tx = db.TransactionManager.StartTransaction())
            {
                first = RoomStatisticsTableService.Insert(db, tx, Point3d.Origin, items, false, 1, 2.5);
                summary = RoomStatisticsTableService.Insert(db, tx, new Point3d(170, 0, 0), items.Take(200).ToList(), true, 1, 2.5);
                quantity = RoomStatisticsTableService.Insert(db, tx, new Point3d(170, -100, 0), items, true, 1, 2.5);
                var floorItems = items.Take(10).Select(x=>RoomStatistics.WithFloor(x,"3~10层",8)).Concat(items.Skip(250).Take(10).Select(x=>RoomStatistics.WithFloor(x,"1层",1))).ToList();
                floorTable = RoomStatisticsTableService.Insert(db, tx, new Point3d(300,-100,0),floorItems,true,1,2.5);
                floorAreaTable = RoomStatisticsTableService.Insert(db, tx, new Point3d(300,-200,0),floorItems,false,1,2.5);
                var table = new Table(); table.SetSize(4, 5); table.SetRowHeight(7); table.SetColumnWidth(30);
                table.Cells[0, 0].TextString = "房间面积统计表";
                var headers = new[] { "序号", "房间名称", "数量", "面积明细", "合计面积" };
                for (int c = 0; c < 5; c++) table.Cells[1, c].TextString = headers[c];
                table.Cells[2, 0].TextString = "1"; table.Cells[2, 1].TextString = "卧室";
                table.Cells[2, 2].TextString = "2"; table.Cells[2, 3].TextString = "12×2"; table.Cells[2, 4].TextString = "24";
                var space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                legacy = space.AppendEntity(table); tx.AddNewlyCreatedDBObject(table, true); table.GenerateLayout();
                tx.Commit();
            }
            using (var tx = db.TransactionManager.StartTransaction())
            {
                List<RoomStatisticsItem> a, b, c; string warning;
                Check(RoomStatisticsTableService.TryRead((Table)tx.GetObject(first, OpenMode.ForRead), tx, out a, out warning) && warning == null && a.Count == 300, "native snapshot");
                Check(RoomStatisticsTableService.TryRead((Table)tx.GetObject(summary, OpenMode.ForRead), tx, out b, out warning) && warning == null && b.Count == 200, "summary snapshot");
                var q = (Table)tx.GetObject(quantity, OpenMode.ForRead);
                Check(q.Columns.Count == 4 && q.Cells[0, 0].TextString == "房间数量汇总表" && q.Cells[1,3].TextString == "数量占比", "quantity table no area");
                Check(q.Cells[q.Rows.Count-1, 3].TextString == "100.00%", "quantity total percent");
                Check(RoomStatisticsTableService.TryRead(q, tx, out c, out warning, true) && warning == null && c.Count == 300, "quantity metadata");
                var countRows = RoomStatistics.Rows(c, true);
                Check(countRows.Single(x => x.RoomName == "卧室").CountShare == "83.33%", "quantity share");
                var f = (Table)tx.GetObject(floorTable,OpenMode.ForRead);
                Check(f.Columns.Count==5 && f.Cells[1,2].TextString=="分层数量","floor table headers");
                Check(RoomStatisticsTableService.TryRead(f,tx,out c,out warning,true) && warning==null && c.Sum(RoomStatistics.Quantity)==90,"floor metadata weighted read");
                Check(RoomStatistics.Rows(c,true).Single(x=>x.RoomName=="卧室").CountShare=="88.89%","floor share");
                var fa = (Table)tx.GetObject(floorAreaTable,OpenMode.ForRead);
                Check(fa.Columns.Count==6 && fa.Cells[1,2].TextString=="分层数量","floor area headers");
                Check(RoomStatisticsTableService.TryRead(fa,tx,out c,out warning) && warning==null && RoomStatistics.Total(c)=="1260","floor area metadata");
                int duplicates;
                Check(RoomStatistics.Combine(a.Concat(b), out duplicates).Count == 300 && duplicates == 200, "overlap");
                Check(RoomStatisticsTableService.TryRead((Table)tx.GetObject(legacy, OpenMode.ForRead), tx, out c, out warning) && warning != null && c.Sum(x => x.Count) == 2, "legacy native");
                var table = (Table)tx.GetObject(first, OpenMode.ForWrite);
                var row = Enumerable.Range(2, table.Rows.Count - 3).Single(r => table.Cells[r, 1].TextString == "客厅");
                table.Cells[row, 2].TextString = "51"; table.Cells[row, 3].TextString = "30×51"; table.Cells[row, 4].TextString = "1530";
                Check(RoomStatisticsTableService.TryRead(table, tx, out a, out warning) && warning != null && a.Sum(x => x.Count) == 301, "visible edits respected");
                tx.Abort();
            }
            var file = Path.Combine(folder, "room-statistics-private.dwg");
            db.SaveAs(file, DwgVersion.Current);
            using (var side = new Database(false, true))
            {
                side.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, false, null); side.CloseInput(true);
                using (var tx = side.TransactionManager.StartTransaction())
                {
                    var table = (Table)tx.GetObject(side.GetObjectId(false, first.Handle, 0), OpenMode.ForRead);
                    List<RoomStatisticsItem> saved; string warning;
                    Check(RoomStatisticsTableService.TryRead(table, tx, out saved, out warning) && warning == null && saved.Count == 300, "save/reopen snapshot");
                    Check(RoomStatistics.Total(saved) == "5400", "native total");
                    var q = (Table)tx.GetObject(side.GetObjectId(false, quantity.Handle, 0), OpenMode.ForRead);
                    Check(RoomStatisticsTableService.TryRead(q, tx, out saved, out warning, true) && warning == null && saved.Count == 300, "quantity save/reopen");
                    var f = (Table)tx.GetObject(side.GetObjectId(false,floorTable.Handle,0),OpenMode.ForRead);
                    Check(RoomStatisticsTableService.TryRead(f,tx,out saved,out warning,true) && warning==null && saved.Sum(RoomStatistics.Quantity)==90,"floor save no double multiply");
                    var fa = (Table)tx.GetObject(side.GetObjectId(false,floorAreaTable.Handle,0),OpenMode.ForRead);
                    Check(RoomStatisticsTableService.TryRead(fa,tx,out saved,out warning) && warning==null && RoomStatistics.Total(saved)=="1260","floor area save/reopen");
                }
            }
            CheckWindow(document, folder);
            document.Editor.WriteMessage("\nROOM_STATISTICS_NATIVE_OK insert legacy overlap manualEdit abort saveReopen 300rooms ui\n");
        }
        catch (System.Exception ex) { document.Editor.WriteMessage("\nROOM_STATISTICS_NATIVE_FAILED " + ex + "\n"); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void CheckWindow(Document document, string folder)
    {
        var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(RoomStatisticsNativeTests).Assembly.Location), "Main", "BatchPdfPublisher.dll"));
        var registry = assembly.GetType("BatchPdfPublisher.Services.FeatureRegistry", true);
        var features = (System.Collections.IEnumerable)registry.GetProperty("Items", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null, null);
        var ribbon = assembly.GetType("BatchPdfPublisher.Services.RibbonService", true);
        var create = ribbon.GetMethod("CreateFeatureButton", BindingFlags.Static | BindingFlags.NonPublic);
        var count = 0;
        foreach (var feature in features)
        {
            var button = create.Invoke(null, new object[] { feature, new Dictionary<string, string>() });
            Check(button.GetType().GetProperty("Image").GetValue(button, null) != null, "Ribbon small image");
            Check(button.GetType().GetProperty("LargeImage").GetValue(button, null) != null, "Ribbon large image");
            var command = (string)feature.GetType().GetProperty("Command").GetValue(feature, null);
            Check((string)button.GetType().GetProperty("CommandParameter").GetValue(button, null) == command + " ", "Ribbon command");
            count++;
        }
        document.Editor.WriteMessage("\nRIBBON_BUTTONS_NATIVE_OK " + count + "\n");
        var type = assembly.GetType("BatchPdfPublisher.Views.TianzhengRoomStatisticsForm", true);
        using (var form = (Form)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.Instance, null, new object[] { document, false }, null))
        {
            type.GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, true);
            form.Show(); System.Windows.Forms.Application.DoEvents();
            Check(((ComboBox)type.GetField("_scale", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form)).Text == "1:100", "default scale");
            Check(((ComboBox)type.GetField("_textHeight", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form)).Text == "2.5", "default text height");
            var dataType = assembly.GetType("BatchPdfPublisher.Services.RoomStatisticsItem", true);
            var listType = typeof(List<>).MakeGenericType(dataType);
            var allItems = (System.Collections.IList)Activator.CreateInstance(listType);
            for (int i = 0; i < 15; i++)
            {
                var item = Activator.CreateInstance(dataType);
                dataType.GetProperty("Source").SetValue(item, "ui:" + i, null);
                dataType.GetProperty("Name").SetValue(item, i == 0 ? "卧室" : i == 1 ? "楼梯间" : "房间统计测试名称" + i, null);
                dataType.GetProperty("Count").SetValue(item, 16, null);
                dataType.GetProperty("Area").SetValue(item, 12m, null);
                dataType.GetProperty("AreaLabel").SetValue(item, "12", null);
                allItems.Add(item);
            }
            type.GetField("_allItems", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, allItems);
            var refresh = type.GetMethod("RefreshStatistics", BindingFlags.NonPublic | BindingFlags.Instance);
            refresh.Invoke(form, null);
            var grid = (DataGridView)type.GetField("_grid", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
            Check(grid.Rows.Count == 14, "UI default exclusion");
            type.GetField("_summary", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, true); refresh.Invoke(form, null);
            Check(grid.Columns.GetColumnCount(DataGridViewElementStates.Visible) == 5 && !grid.Columns["AreaDetails"].Visible && !grid.Columns["TotalArea"].Visible, "summary columns hidden");
            grid.Rows[0].Cells[0].Value = false; System.Windows.Forms.Application.DoEvents();
            var selected = (System.Collections.IEnumerable)type.GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
            Check(selected.Cast<object>().Count() == 13, "manual checkbox exclusion");
            var selectedRows = (System.Collections.IEnumerable)type.GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
            var shares = selectedRows.Cast<object>().Where(x => (bool)x.GetType().GetProperty("Included").GetValue(x, null)).ToList();
            Check(shares.All(x => (string)x.GetType().GetProperty("CountShare").GetValue(x, null) == "7.69%"), "manual share recalculation");
            var mode = (ComboBox)type.GetField("_filterMode", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
            var keywords = (TextBox)type.GetField("_keywords", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
            mode.SelectedIndex = 1; keywords.Text = "卧室"; System.Windows.Forms.Application.DoEvents();
            Check(grid.Rows.Count == 1, "UI inclusion keyword");
            mode.SelectedIndex = 0; keywords.Text = RoomStatistics.DefaultKeywords;
            foreach (var size in new[] { new Size(860, 560), new Size(660, 440) })
            {
                form.Size = size; form.CreateControl(); form.PerformLayout(); System.Windows.Forms.Application.DoEvents();
                grid.CreateControl(); grid.PerformLayout();
                Check(grid.Columns.Count == 8 && grid.Bounds.Height > 150 && grid.Bounds.Width > 600, "window grid bounds");
                Check(mode.Height >= 20 && keywords.Width >= 150 && keywords.Height >= 20 && keywords.Text == RoomStatistics.DefaultKeywords, "filter input layout/text");
                var filterPanel = keywords.Parent;
                foreach (Control control in filterPanel.Controls)
                    Check(control.Width > 0 && control.Height >= 18 && control.Right <= filterPanel.ClientSize.Width, "filter controls bounded");
                using (var bitmap = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(folder, "room-window-" + size.Width + ".png")); }
            }
            foreach (Control child in form.Controls.Cast<Control>().SelectMany(x => x.Controls.Cast<Control>()).SelectMany(x => x.Controls.Cast<Control>()))
                if (child is Button && child.Text == "恢复默认") ((Button)child).PerformClick();
            Check(((System.Collections.IEnumerable)type.GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form)).Cast<object>().Count() == 14, "restore defaults clears manual exclusions");
            var nameBox=(TextBox)type.GetField("_floorName",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
            var countBox=(NumericUpDown)type.GetField("_floorCount",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
            type.GetField("_busy",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(form,false);
            nameBox.Text="3~10层"; Check(countBox.Value==8,"room shared range auto count");
            var withFloor=assembly.GetType("BatchPdfPublisher.Services.RoomStatistics",true).GetMethod("WithFloor",BindingFlags.Public|BindingFlags.Static);
            var floorItems=(System.Collections.IList)Activator.CreateInstance(listType);
            foreach(var item in allItems)floorItems.Add(withFloor.Invoke(null,new object[]{item,"3~10层",8}));
            type.GetField("_allItems",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(form,floorItems);refresh.Invoke(form,null);
            Check(grid.Columns["FloorQuantitySummary"].Visible,"floor preview shown");
            var floorSettings=assembly.GetType("BatchPdfPublisher.Views.RoomFloorSettingsForm",true);
            using(var dialog=(Form)Activator.CreateInstance(floorSettings,BindingFlags.Public|BindingFlags.Instance,null,new object[]{floorItems},null))
            {
                dialog.Show();System.Windows.Forms.Application.DoEvents();
                floorSettings.GetMethod("Apply",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(dialog,null);
                var floorResult=(System.Collections.IEnumerable)floorSettings.GetProperty("Result").GetValue(dialog,null);
                Check(floorResult.Cast<object>().Count()==15,"floor management apply");
            }
            foreach(var item in allItems)dataType.GetProperty("FloorSource").SetValue(item,"legacy-table",null);
            using(var dialog=(Form)Activator.CreateInstance(floorSettings,BindingFlags.Public|BindingFlags.Instance,null,new object[]{allItems},null))
            {
                dialog.Show();System.Windows.Forms.Application.DoEvents();
                var floorGrid=(DataGridView)floorSettings.GetField("_grid",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(dialog);
                Check(floorGrid.Rows.Count==1,"legacy items grouped by source table");
                floorGrid.Rows[0].Cells[0].Value="3~10层";
                floorSettings.GetMethod("Apply",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(dialog,null);
                var floorResult=((System.Collections.IEnumerable)floorSettings.GetProperty("Result").GetValue(dialog,null)).Cast<object>().ToList();
                Check(floorResult.All(x=>(int)dataType.GetProperty("FloorCount").GetValue(x,null)==8),"legacy floor range applied");
                Check(allItems.Cast<object>().All(x=>(int)dataType.GetProperty("FloorCount").GetValue(x,null)==0),"floor draft does not mutate originals");
            }
            form.Close();
        }
    }
}
