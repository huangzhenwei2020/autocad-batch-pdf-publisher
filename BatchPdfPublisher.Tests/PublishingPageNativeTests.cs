using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.Views;
using BatchPdfPublisher.ViewModels;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(PublishingPageNativeTests))]
public sealed class PublishingPageNativeTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }

    [CommandMethod("WLPAGEQA")]
    public void Run()
    {
        try { RunCore(); }
        catch (System.Exception exception)
        {
            File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT"), "failure.txt"), exception.ToString());
            Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nPUBLISH_PAGE_QA_FAILED " + exception + "\n");
        }
    }

    private void RunCore()
    {
        using (var db = new Database(true, true))
        {
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var blocks = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForWrite);
                var frame = new BlockTableRecord { Name = "PAGE_QA_FRAME" };
                var frameId = blocks.Add(frame); tx.AddNewlyCreatedDBObject(frame, true);
                var outline = new Polyline();
                outline.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
                outline.AddVertexAt(1, new Point2d(420, 0), 0, 0, 0);
                outline.AddVertexAt(2, new Point2d(420, 297), 0, 0, 0);
                outline.AddVertexAt(3, new Point2d(0, 297), 0, 0, 0); outline.Closed = true;
                frame.AppendEntity(outline); tx.AddNewlyCreatedDBObject(outline, true);
                var constantPage = new AttributeDefinition { Tag = "PAGE_NO", TextString = "C004", Constant = true, Position = new Point3d(20, 20, 0), Height = 3 };
                frame.AppendEntity(constantPage); tx.AddNewlyCreatedDBObject(constantPage, true);
                var spaces = new List<BlockTableRecord>();
                foreach (ObjectId id in blocks)
                {
                    var space = (BlockTableRecord)tx.GetObject(id, OpenMode.ForRead);
                    if (space.IsLayout) spaces.Add(space);
                }
                Check(spaces.Count >= 2, "Side database has no paper layout");
                for (var i = 0; i < spaces.Count; i++)
                {
                    var space = spaces[i]; space.UpgradeOpen();
                    var reference = new BlockReference(Point3d.Origin, frameId);
                    space.AppendEntity(reference); tx.AddNewlyCreatedDBObject(reference, true);
                    var attribute = new AttributeReference { Tag = "页码", TextString = (i + 1).ToString("D3"), Position = new Point3d(10, 10, 0), Height = 3 };
                    reference.AttributeCollection.AppendAttribute(attribute); tx.AddNewlyCreatedDBObject(attribute, true);
                }
                tx.Commit();
            }
            var registration = new FrameDefinition { BlockName = "PAGE_QA_FRAME", PaperSize = "A3", PaperOrientation = "横向" };
            var scanner = new DrawingScanner();
            var sheets = scanner.Scan(db, "synthetic.dwg", new[] { registration });
            Check(sheets.Count >= 2 && sheets.All(s => !string.IsNullOrEmpty(s.PageNumber)), "Legacy registration did not read page tag");
            Check(sheets.Last().IsModelSpace && sheets.Take(sheets.Count - 1).All(s => !s.IsModelSpace), "Model is not last");
            registration.PageNumberAttributeTag = ""; registration.DefaultPageNumber = "manual";
            Check(scanner.Scan(db, "synthetic.dwg", new[] { registration }).All(s => s.PageNumber == "manual"), "Explicit disabled mapping ignored");
            registration.PageNumberAttributeTag = "页码";
            Check(scanner.Scan(db, "synthetic.dwg", new[] { registration }).All(s => s.PageNumber != "manual"), "Mapped tag did not override default");
            registration.PageNumberAttributeTag = "PAGE_NO";
            Check(scanner.Scan(db, "synthetic.dwg", new[] { registration }).All(s => s.PageNumber == "C004"), "Constant definition tag was not read");
            var root = Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT");
            Check(!string.IsNullOrEmpty(root), "Private test root required");
            db.SaveAs(Path.Combine(root, "synthetic.dwg"), DwgVersion.Current);
            using (var reopened = new Database(false, true))
            {
                reopened.ReadDwgFile(Path.Combine(root, "synthetic.dwg"), FileOpenMode.OpenForReadAndAllShare, false, null);
                reopened.CloseInput(true);
                Check(scanner.Scan(reopened, "synthetic.dwg", new[] { registration }).Count == sheets.Count, "Save/reopen scan changed");
            }
        }
        using (var viewModel = new PublisherViewModel())
        {
            viewModel.Sheets.Clear();
            viewModel.Sheets.Add(new SheetItem { Building = "QA", SourceFile = "test.dwg", SourceLayout = "Model", Order = 1, PageNumber = "10" });
            viewModel.Sheets.Add(new SheetItem { Building = "QA", SourceFile = "test.dwg", SourceLayout = "布局10", Order = 2, PageNumber = "2" });
            viewModel.Sheets.Add(new SheetItem { Building = "QA", SourceFile = "test.dwg", SourceLayout = "布局2", Order = 3, PageNumber = "1" });
            viewModel.SelectedBuilding = "QA";
            viewModel.SortVisibleSheets(SheetSortMode.LayoutsBeforeModel);
            Check(viewModel.Sheets.Select(s => s.SourceLayout).SequenceEqual(new[] { "布局2", "布局10", "Model" }), "Legacy layout natural ordering failed");
            foreach (var sheet in viewModel.Sheets) sheet.SourceLayoutOrder = sheet.IsModelSpace ? 0 : sheet.SourceLayout == "布局10" ? 1 : 2;
            viewModel.SortVisibleSheets(SheetSortMode.LayoutsBeforeModel);
            Check(viewModel.Sheets.Select(s => s.SourceLayout).SequenceEqual(new[] { "布局10", "布局2", "Model" }), "CAD layout tab order was ignored");
            viewModel.SortVisibleSheets(SheetSortMode.PageNumberAscending);
            Check(viewModel.Sheets.Select(s => s.PageNumber).SequenceEqual(new[] { "1", "2", "10" }), "Page natural ordering failed");
            viewModel.Frames.Add(new FrameDefinition { BlockName = "QA", PaperSize = "A3", PageNumberAttributeTag = "PAGE_NO", DefaultPageNumber = "007" });
            using (var manager = new FrameRegistrationManagerForm(viewModel, () => { }))
            {
                manager.Show(); System.Windows.Forms.Application.DoEvents();
                var root = Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT");
                using (var bitmap = new System.Drawing.Bitmap(manager.Width, manager.Height)) { manager.DrawToBitmap(bitmap, manager.ClientRectangle); bitmap.Save(Path.Combine(root, "page-manager.png")); }
                manager.Width = 1040; manager.Height = 710; System.Windows.Forms.Application.DoEvents();
                using (var bitmap = new System.Drawing.Bitmap(manager.Width, manager.Height)) { manager.DrawToBitmap(bitmap, manager.ClientRectangle); bitmap.Save(Path.Combine(root, "page-manager-small.png")); }
                manager.Close();
            }
        }
        var attributes = new Dictionary<string, string> { { "PAGE_NUMBER", "007" } };
        using (var editor = new FrameRegistrationEditorForm("QA", new FrameSizeGuess { PaperSize = "A3", PrintScale = "1:100" }, attributes))
        {
            editor.Show(); System.Windows.Forms.Application.DoEvents();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var tags = (Array)typeof(FrameRegistrationEditorForm).GetField("_tags", flags).GetValue(editor);
            var defaults = (TextBox[])typeof(FrameRegistrationEditorForm).GetField("_defaults", flags).GetValue(editor);
            Check(tags.Length == 5 && ((Control)tags.GetValue(4)).Text == "PAGE_NUMBER" && defaults[4].Text == "007", "Editor page field detection failed");
            var root = Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT");
            using (var bitmap = new System.Drawing.Bitmap(editor.Width, editor.Height)) { editor.DrawToBitmap(bitmap, editor.ClientRectangle); bitmap.Save(Path.Combine(root, "page-editor.png")); }
            editor.Close();
        }
        CheckPublisherOrderingAndLayout();
        CheckMultipleSheetMoves();
        Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nPUBLISH_PAGE_QA_OK nativeTags legacy disabled defaults layoutFirst naturalPages saveReopen editor dragOrder responsiveButtons\n");
    }

    private static void CheckPublisherOrderingAndLayout()
    {
        var root = Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT");
        var store = new PublishPlanStore();
        var project = store.CreateProject("排序窗口测试", Path.Combine(root, "order-project"));
        project.CadFiles = new List<string> { "first.dwg", "second.dwg" };
        project.SelectedCadFiles = new List<string> { "second.dwg" };
        project.SelectedPublishBuildings = new List<string> { "四层" };
        foreach (var building in new[] { "四层", "八层" })
            foreach (var page in new[] { "10", "2", "1", "" })
                project.SavedSheets.Add(new SheetCatalogItem { Building = building, PageNumber = page,
                    SourceFile = page == "1" || page == "10" ? "first.dwg" : "second.dwg", Order = project.SavedSheets.Count + 1 });
        store.SaveProject(project);
        store.SetActiveProject(project.Name);
        using (var vm = new PublisherViewModel())
        {
            Check(vm.CurrentSheetSort == SheetSortMode.PageNumberAscending, "Page order is not default");
            Check(vm.Sheets.Where(s => s.Building == "四层").Select(s => s.PageNumber).SequenceEqual(new[] { "1", "2", "10", "" }), "Default natural page order or blank fallback failed");
            vm.MoveBuilding("八层", "四层", false);
            Check(vm.Buildings.SequenceEqual(new[] { "八层", "四层" }) && vm.Sheets.First().Building == "八层", "Building move did not change publication order");
            vm.MoveCadFile("second.dwg", "first.dwg", false);
            Check(vm.CadFiles.First().Path == "second.dwg" && vm.CadFiles.First().IsSelected && !vm.CadFiles.Last().IsSelected, "CAD drag changed switches");
            Check(vm.Sheets.Where(s => s.Building == "四层").Select(s => s.PageNumber).SequenceEqual(new[] { "2", "", "1", "10" }), "File move lost per-file relative page ordering");
            Check(vm.PublishBuildings.Single(b => b.Name == "四层").IsSelected && !vm.PublishBuildings.Single(b => b.Name == "八层").IsSelected, "Building drag changed publish selection");
        }
        using (var vm = new PublisherViewModel())
        {
            Check(vm.CurrentSheetSort == SheetSortMode.Custom && vm.Buildings.First() == "八层" && vm.CadFiles.First().Path == "second.dwg", "Drag order did not survive reopening");
            Check(vm.Sheets.First().PageNumber == "2", "Reopening overwrote manually ordered pages");
            var prepared = new List<PreparedPdfPage>();
            foreach (var building in new[] { "四层", "八层" })
            {
                var path = Path.Combine(root, building + ".pdf");
                using (var pdf = new PdfSharp.Pdf.PdfDocument())
                {
                    var page = pdf.AddPage();
                    page.Width = PdfSharp.Drawing.XUnit.FromMillimeter(420);
                    page.Height = PdfSharp.Drawing.XUnit.FromMillimeter(297);
                    pdf.Save(path);
                }
                prepared.Add(new PreparedPdfPage { TemporaryPath = path, Sheet = new SheetItem { Building = building, Order = 1, Frame = "A3", PaperOrientation = "横向" } });
            }
            var completedBuildings = new List<string>();
            var result = new PdfPublisherService().FinalizePreparedPages(prepared, vm.SelectedProject, Path.Combine(root, "publish-output"),
                (completed, total, sheet) => completedBuildings.Add(sheet.Building));
            Check(result.Failures.Count == 0 && result.SheetCount == 2 && completedBuildings.SequenceEqual(new[] { "八层", "四层" }),
                "PDF output did not respect building order " + string.Join(" | ", result.Failures));
            vm.SortVisibleSheets(SheetSortMode.PageNumberAscending);
        }
        foreach (var scale in new[] { 1F, 1.25F, 1.5F })
        {
            File.AppendAllText(Path.Combine(root, "progress.txt"), "Create scale " + scale + Environment.NewLine);
            using (var form = new PublisherForm())
            {
                form.Show(); System.Windows.Forms.Application.DoEvents();
                File.AppendAllText(Path.Combine(root, "progress.txt"), "Show scale " + scale + Environment.NewLine);
                if (scale != 1F) form.Scale(new System.Drawing.SizeF(scale, scale));
                File.AppendAllText(Path.Combine(root, "progress.txt"), "Scaled " + scale + Environment.NewLine);
                foreach (var width in new[] { 1690, 1200 })
                {
                    form.Size = new System.Drawing.Size(width, 900);
                    System.Windows.Forms.Application.DoEvents();
                    foreach (var input in Descendants(form).Where(c => c.Visible && c.GetType().Name == "ThemedComboBox"))
                        Check(input.Width >= 40 && input.Height >= input.Font.Height, "Scaled dropdown disappeared " + input.Text);
                    var rootPanel = (TableLayoutPanel)form.Controls[0];
                    Check(rootPanel.GetRowHeights()[1] < 240, "Header consumed the drawing list space");
                    using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, form.ClientRectangle);
                        bitmap.Save(Path.Combine(root, "publisher-" + width + "-" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png"));
                    }
                    CheckButtons(form);
                    var settings = Descendants(form).OfType<Button>().First(b => b.Text == "输出设置");
                    settings.PerformClick(); System.Windows.Forms.Application.DoEvents();
                    CheckButtons(form);
                    using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, form.ClientRectangle);
                        bitmap.Save(Path.Combine(root, "publisher-settings-" + width + "-" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png"));
                    }
                    Descendants(form).OfType<Button>().First(b => b.Text == "图纸预览").PerformClick();
                }
                if (scale == 1F)
                {
                    File.AppendAllText(Path.Combine(root, "progress.txt"), "Drop" + Environment.NewLine);
                    var fields = BindingFlags.Instance | BindingFlags.NonPublic;
                    var list = (ListBox)typeof(PublisherForm).GetField("_cadFiles", fields).GetValue(form);
                    var data = new DataObject(); data.SetData("WanLuo.CadFileOrder", list.Items[0]);
                    var bounds = list.GetItemRectangle(1);
                    var point = list.PointToScreen(new System.Drawing.Point(20, bounds.Bottom - 2));
                    typeof(Control).GetMethod("OnDragDrop", fields).Invoke(list,
                        new object[] { new DragEventArgs(data, 0, point.X, point.Y, DragDropEffects.Move, DragDropEffects.Move) });
                    var vm = (PublisherViewModel)typeof(PublisherForm).GetField("_viewModel", fields).GetValue(form);
                    Check(vm.CadFiles.First().Path == "first.dwg", "CAD list drop event did not call reorder");
                    Check(((CheckedListBox)list).GetItemChecked(1), "Drop event changed checked file");
                    var buildings = (ListBox)typeof(PublisherForm).GetField("_buildings", fields).GetValue(form);
                    var buildingData = new DataObject(); buildingData.SetData("WanLuo.BuildingOrder", buildings.Items[0]);
                    var lastBuilding = buildings.GetItemRectangle(1);
                    var buildingPoint = buildings.PointToScreen(new System.Drawing.Point(20, lastBuilding.Bottom - 2));
                    typeof(Control).GetMethod("OnDragDrop", fields).Invoke(buildings,
                        new object[] { new DragEventArgs(buildingData, 0, buildingPoint.X, buildingPoint.Y, DragDropEffects.Move, DragDropEffects.Move) });
                    Check(vm.Buildings.First() == "四层" && vm.PublishBuildings.First().IsSelected, "Building list drop or publish selection failed");
                }
                form.Close();
                File.AppendAllText(Path.Combine(root, "progress.txt"), "Closed " + scale + Environment.NewLine);
            }
        }
    }

    private static void CheckButtons(Control root)
    {
        foreach (Control control in root.Controls)
        {
            var button = control as Button;
            if (button != null && button.Visible && button.GetType().Name == "RoundedButton")
            {
                var text = TextRenderer.MeasureText(button.Text, button.Font, new System.Drawing.Size(10000, 1000),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                var required = text.Width + (button.Image == null ? 0 : button.Image.Width + 5) + 13;
                Check(button.Width >= required, "Caption is clipped " + button.Text + " actual=" + button.Width + " required=" + required);
                Check(button.Right <= button.Parent.ClientSize.Width && button.Bottom <= button.Parent.ClientSize.Height,
                    "Toolbar button is outside its container " + button.Text);
                var bounds = button.RectangleToScreen(button.ClientRectangle);
                for (var parent = button.Parent; parent != null; parent = parent.Parent)
                {
                    var viewport = parent.RectangleToScreen(parent.ClientRectangle);
                    Check(bounds.Left >= viewport.Left && bounds.Right <= viewport.Right, "Ancestor clips caption " + button.Text + " parent=" + parent.GetType().Name + " button=" + bounds + " viewport=" + viewport);
                    if (parent.GetType().Name == "DarkScrollHost") break;
                    Check(bounds.Top >= viewport.Top && bounds.Bottom <= viewport.Bottom, "Ancestor clips toolbar " + button.Text);
                }
            }
            CheckButtons(control);
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }

    private static void CheckMultipleSheetMoves()
    {
        var root = Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT");
        var store = new PublishPlanStore();
        var project = store.CreateProject("多张移动测试", Path.Combine(root, "multi-project"));
        project.SheetSort = "Custom";
        project.BuildingOrder = new List<string> { "多选", "其他" };
        foreach (var name in new[] { "A", "B", "C", "D", "E", "F" })
            project.SavedSheets.Add(new SheetCatalogItem { Building = "多选", SheetNumber = name, SheetName = name,
                Order = project.SavedSheets.Count + 1, Frame = "A3", PaperOrientation = "横向", PrintScale = "1:1", MinX = 0, MinY = 0, MaxX = 420, MaxY = 297 });
        project.SavedSheets.Add(new SheetCatalogItem { Building = "其他", SheetNumber = "Z", Order = 1 });
        store.SaveProject(project); store.SetActiveProject(project.Name);
        using (var vm = new PublisherViewModel())
        {
            vm.SelectedBuilding = "多选";
            var sheets = vm.Sheets.ToDictionary(s => s.SheetNumber);
            vm.MoveSheets(new[] { sheets["C"], sheets["B"] }, sheets["F"], true);
            CheckOrder(vm, "ADEFBC");
            vm.MoveSheets(new[] { sheets["B"], sheets["C"] }, sheets["A"], false);
            CheckOrder(vm, "BCADEF");
            vm.MoveSheetsToEdge(new[] { sheets["D"], sheets["F"] }, true);
            CheckOrder(vm, "BCAEDF");
            vm.MoveSheetsToEdge(new[] { sheets["D"], sheets["F"] }, false);
            CheckOrder(vm, "DFBCAE");
            vm.MoveSheetsByOffset(new[] { sheets["D"], sheets["F"] }, 1);
            CheckOrder(vm, "BDFCAE");
            vm.MoveSheetsByOffset(new[] { sheets["F"], sheets["A"] }, -1);
            CheckOrder(vm, "BFDACE");
            vm.MoveSheetsByOffset(new[] { sheets["D"], sheets["A"] }, 1, new[] { sheets["B"], sheets["D"], sheets["A"], sheets["E"] });
            CheckOrder(vm, "BFEDCA");
            vm.MoveSheets(new[] { sheets["C"] }, sheets["Z"], false);
            vm.MoveSheets(new[] { sheets["F"], sheets["E"] }, sheets["F"], true);
            vm.MoveSheetsByOffset(new[] { sheets["B"] }, -1);
            vm.MoveSheetsByOffset(new[] { sheets["A"] }, 1);
            CheckOrder(vm, "BFEDCA");
            Check(vm.Sheets.Single(s => s.SheetNumber == "Z").Building == "其他", "Other building was changed");
        }
        using (var vm = new PublisherViewModel())
        {
            CheckOrder(vm, "BFEDCA");
            Check(vm.CurrentSheetSort == SheetSortMode.Custom, "Reopen changed manual multi-row order");
        }
        using (var form = new PublisherForm())
        {
            form.Show(); System.Windows.Forms.Application.DoEvents();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var grid = (DataGridView)typeof(PublisherForm).GetField("_sheets", flags).GetValue(form);
            var vm = (PublisherViewModel)typeof(PublisherForm).GetField("_viewModel", flags).GetValue(form);
            vm.SelectedBuilding = "多选";
            Check(grid.MultiSelect, "Grid is still single-select");
            ClickSheetRow(grid, 1, Keys.None);
            ClickSheetRow(grid, 3, Keys.Shift);
            Check(SelectedNames(grid) == "FED", "Shift click lost contiguous selection " + SelectedNames(grid));
            ClickSheetRow(grid, 5, Keys.Control);
            Check(SelectedNames(grid) == "FEDA", "Ctrl click failed to add a row " + SelectedNames(grid));
            ClickSheetRow(grid, 2, Keys.Control);
            Check(SelectedNames(grid) == "FDA", "Ctrl click failed to remove a row " + SelectedNames(grid));
            ClickSheetRow(grid, 3, Keys.None);
            Check(SelectedNames(grid) == "FDA", "Mouse-down collapsed multi-row drag selection " + SelectedNames(grid));
            Descendants(form).OfType<Button>().First(b => b.Text == "上移").PerformClick();
            CheckOrder(vm, "FBDEAC");
            Check(SelectedNames(grid) == "FDA", "Up button lost selected rows");
            Descendants(form).OfType<Button>().First(b => b.Text == "下移").PerformClick();
            CheckOrder(vm, "BFEDCA");
            Check(SelectedNames(grid) == "FDA", "Down button lost selected rows");
            Descendants(form).OfType<Button>().First(b => b.Text == "移到顶部").PerformClick();
            CheckOrder(vm, "FDABEC");
            Check(SelectedNames(grid) == "FDA", "Top button lost selected rows");
            Descendants(form).OfType<Button>().First(b => b.Text == "移到尾部").PerformClick();
            CheckOrder(vm, "BECFDA");
            Check(SelectedNames(grid) == "FDA", "Bottom button lost selected rows");
            var selected = grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => (SheetItem)r.DataBoundItem).ToArray();
            var data = new DataObject(typeof(SheetItem[]).FullName, selected);
            var rowBounds = grid.GetRowDisplayRectangle(0, false);
            var point = grid.PointToScreen(new System.Drawing.Point(20, rowBounds.Top + 2));
            var drag = new DragEventArgs(data, 0, point.X, point.Y, DragDropEffects.Move, DragDropEffects.Move);
            typeof(Control).GetMethod("OnDragOver", flags).Invoke(grid, new object[] { drag });
            typeof(Control).GetMethod("OnDragDrop", flags).Invoke(grid, new object[] { drag });
            CheckOrder(vm, "FDABEC");
            Check(SelectedNames(grid) == "FDA", "Group drag lost selected rows");
            System.Windows.Forms.Application.DoEvents();
            using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, form.ClientRectangle);
                bitmap.Save(Path.Combine(root, "publisher-multiselect.png"));
            }
            form.Close();
        }
        using (var vm = new PublisherViewModel()) CheckOrder(vm, "FDABEC");
    }

    private static void CheckOrder(PublisherViewModel vm, string expected)
    {
        var actual = string.Concat(vm.Sheets.Where(s => s.Building == "多选").Select(s => s.SheetNumber));
        Check(actual == expected, "Sheet order expected " + expected + " got " + actual);
    }

    private static string SelectedNames(DataGridView grid)
    {
        return string.Concat(grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => ((SheetItem)r.DataBoundItem).SheetNumber));
    }

    private static void ClickSheetRow(DataGridView grid, int row, Keys modifiers)
    {
        var before = new byte[256];
        Check(GetKeyboardState(before), "Cannot read test keyboard state");
        var state = (byte[])before.Clone();
        state[0x10] = (byte)((modifiers & Keys.Shift) != Keys.None ? 0x80 : 0);
        state[0x11] = (byte)((modifiers & Keys.Control) != Keys.None ? 0x80 : 0);
        Check(SetKeyboardState(state), "Cannot set test thread modifiers");
        try
        {
            var bounds = grid.GetCellDisplayRectangle(0, row, true);
            var mouse = new MouseEventArgs(MouseButtons.Left, 1, bounds.Left + 8, bounds.Top + bounds.Height / 2, 0);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            grid.GetType().GetMethod("OnMouseDown", flags).Invoke(grid, new object[] { mouse });
            grid.GetType().GetMethod("OnMouseUp", flags).Invoke(grid, new object[] { mouse });
        }
        finally { SetKeyboardState(before); }
        System.Windows.Forms.Application.DoEvents();
    }

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] state);
}
