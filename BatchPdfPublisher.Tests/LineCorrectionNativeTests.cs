using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.Services;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

public sealed class LineCorrectionNativeTests
{
    private static readonly List<ObjectId> Ids = new List<ObjectId>();
    private static List<CorrectionPoint> _originalLine;
    private static string _folder;
    [CommandMethod("WLLINECORRECTIONSETUP")]
    public void Setup()
    {
        try
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            _folder = Environment.GetEnvironmentVariable("WANLUO_LINE_QA"); Ids.Clear();
            using (var tx = doc.Database.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tx.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                Action<Entity> add = e => { Ids.Add(space.AppendEntity(e)); tx.AddNewlyCreatedDBObject(e, true); };
                add(new Line(new Point3d(100, 300, 10), new Point3d(1100, 301, -3)) { ColorIndex = 3, LineWeight = LineWeight.LineWeight030 });
                var poly = new Polyline();
                foreach (var p in new[] { new Point2d(0, 0), new Point2d(1000, 1), new Point2d(1001, 1000), new Point2d(1, 1001) })
                    poly.AddVertexAt(poly.NumberOfVertices, p, 0, 20, 25);
                poly.Closed = true; poly.Elevation = 300; add(poly);
                var arc = new Polyline(); arc.AddVertexAt(0, new Point2d(0, 0), .5, 10, 20); arc.AddVertexAt(1, new Point2d(1000, 1), 0, 20, 10);
                arc.Elevation = -100; add(arc);
                add(new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(new[] { new Point3d(0, 0, 10), new Point3d(1000, 1, 20), new Point3d(1001, 1000, 30) }), false));
                foreach (var normal in new[] { Vector3d.ZAxis, -Vector3d.ZAxis })
                {
                    var legacy = new Polyline2d(Poly2dType.SimplePoly, new Point3dCollection(new[] { new Point3d(0, 0, 0), new Point3d(1000, 1, 0), new Point3d(1001, 1000, 0) }), 80, false, 10, 20, null);
                    legacy.Normal = normal; add(legacy);
                }
                add(new Circle(Point3d.Origin, Vector3d.ZAxis, 200));
                var layers = (LayerTable)tx.GetObject(doc.Database.LayerTableId, OpenMode.ForWrite);
                var locked = new LayerTableRecord { Name = "LineCorrectionLocked" };
                layers.Add(locked); tx.AddNewlyCreatedDBObject(locked, true);
                add(new Line(new Point3d(0, 0, 50), new Point3d(1000, 1, 50)) { LayerId = locked.ObjectId });
                locked.IsLocked = true;
                tx.Commit();
            }
            using (var tx = doc.Database.TransactionManager.StartTransaction())
            {
                bool closed, arcs; string error;
                LineCorrectionService.Read((Entity)tx.GetObject(Ids[0], OpenMode.ForRead), tx, out _originalLine, out closed, out arcs, out error);
                foreach (var id in Ids.Skip(4).Take(2))
                {
                    var legacy = (Polyline2d)tx.GetObject(id, OpenMode.ForRead); List<CorrectionPoint> points;
                    Check(LineCorrectionService.Read(legacy, tx, out points, out closed, out arcs, out error), "Legacy read rejected.");
                    for (var i = 0; i < points.Count; i++)
                        Check(new Point3d(points[i].X, points[i].Y, points[i].Z).DistanceTo(legacy.GetPointAtParameter(i)) < 1e-7, "Legacy OCS/elevation mismatch.");
                }
            }
            doc.Editor.WriteMessage("\nLINE_CORRECTION_SETUP_OK");
        }
        catch (System.Exception ex) { CadApplication.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nLINE_CORRECTION_NATIVE_FAILED " + ex); }
    }

    [CommandMethod("WLLINECORRECTIONQA", CommandFlags.UsePickSet)]
    public void Run()
    {
        var doc = CadApplication.DocumentManager.MdiActiveDocument;
        try
        {
            var options = new LineCorrectionOptions();
            var preview = LineCorrectionService.Analyze(doc.Database, Ids, options);
            Check(preview.Count == 8 && preview.Count(p => p.Result != null && p.Result.Changed) == 6, "Preview counts/locked/unsupported.");
            var formType = typeof(BatchPdfPublisher.Commands).Assembly.GetType("BatchPdfPublisher.Views.LineCorrectionForm", true);
            doc.Editor.SetImpliedSelection(Ids.ToArray());
            using (var form = (Form)Activator.CreateInstance(formType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { doc }, null))
            {
                foreach (var size in new[] { new Size(960, 660), new Size(800, 480), new Size(620, 400) })
                {
                    form.ClientSize = size; form.Show(); System.Windows.Forms.Application.DoEvents(); form.PerformLayout(); form.Refresh();
                    var grid = (DataGridView)formType.GetField("_grid", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    using (var diagnostic = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(diagnostic, new Rectangle(Point.Empty, diagnostic.Size)); diagnostic.Save(System.IO.Path.Combine(_folder, "layout-" + size.Width + ".png")); }
                    Check(grid.Rows.Count == 8 && grid.Width > 500 && grid.Height > 80, "Window preview/layout is blank: rows=" + grid.Rows.Count
                        + " grid=" + grid.Size + " client=" + form.ClientSize);
                    Check(Controls(form).Any(c => c is Button && c.Text == "选择对象" && c.Visible && c.Height >= 25), "Selection button is hidden.");
                    Check(form.BackColor.GetBrightness() < .25 && grid.BackgroundColor.GetBrightness() < .25, "Dialog theme is inconsistent.");
                    foreach (var control in Controls(form).Where(c => c.Visible && (c is Button || c is CheckBox || c is NumericUpDown)))
                        Check(control.Left >= 0 && control.Right <= control.Parent.ClientSize.Width + 1 && control.Top >= 0
                            && control.Bottom <= control.Parent.ClientSize.Height + 1, "Clipped control: " + control.Text + " " + control.Bounds + " parent=" + control.Parent.ClientSize);
                    var previewControl = (Control)formType.GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    var compact = !previewControl.Visible;
                    if (compact) { Controls(form).OfType<Button>().Single(c => c.Text == "预览").PerformClick(); System.Windows.Forms.Application.DoEvents(); }
                    Check(previewControl.Width > 500 && previewControl.Height > 30, "Graphic preview is hidden.");
                    var projectionField = previewControl.GetType().GetField("Projection", BindingFlags.Instance | BindingFlags.NonPublic);
                    foreach (var mode in Controls(form).OfType<RadioButton>())
                    {
                        mode.Checked = true; System.Windows.Forms.Application.DoEvents();
                        Check((int)projectionField.GetValue(previewControl) == (mode.Text == "XY" ? 0 : mode.Text == "XZ" ? 1 : 2), "Projection toggle did not update.");
                    }
                    Controls(form).OfType<RadioButton>().Single(c => c.Text == "XY").Checked = true;
                    var angle = (NumericUpDown)formType.GetField("_angle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    angle.Value = 0; System.Windows.Forms.Application.DoEvents();
                    Check(grid.Rows.Cast<DataGridViewRow>().Sum(r => Convert.ToInt32(r.Cells[2].Value)) == 0, "Angle input did not refresh preview.");
                    angle.Value = 1;
                    var flatten = (CheckBox)formType.GetField("_flatten", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    flatten.Checked = false; System.Windows.Forms.Application.DoEvents();
                    Check(grid.Rows.Cast<DataGridViewRow>().Sum(r => Convert.ToInt32(r.Cells[3].Value)) == 0, "Z toggle did not refresh preview.");
                    flatten.Checked = true;
                    var horizontal = (CheckBox)formType.GetField("_horizontal", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    var vertical = (CheckBox)formType.GetField("_vertical", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    horizontal.Checked = false; vertical.Checked = false;
                    Check(!angle.Enabled && grid.Rows.Cast<DataGridViewRow>().Sum(r => Convert.ToInt32(r.Cells[2].Value)) == 0, "Z-only mode changed directions.");
                    flatten.Checked = false;
                    var apply = (Button)formType.GetField("_apply", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    Check(!apply.Enabled, "No-op application remained enabled.");
                    Controls(form).OfType<Button>().Single(c => c.Text == "恢复默认").PerformClick();
                    Check(angle.Value == 1 && angle.Enabled && horizontal.Checked && vertical.Checked && flatten.Checked && apply.Enabled, "Default options did not reset.");
                    Check(!string.IsNullOrWhiteSpace(angle.Text) && angle.Controls.OfType<TextBox>().All(c => !string.IsNullOrWhiteSpace(c.Text)), "Angle input text disappeared after re-enabling.");
                    grid.CurrentCell = grid.Rows[1].Cells[0]; System.Windows.Forms.Application.DoEvents();
                    Check(ReferenceEquals(previewControl.GetType().GetField("Highlight", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(previewControl), grid.Rows[1].DataBoundItem), "Row selection did not highlight the preview.");
                    if (compact)
                    {
                        form.Refresh(); System.Windows.Forms.Application.DoEvents();
                        using (var image = new Bitmap(form.Width, form.Height))
                        { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(System.IO.Path.Combine(_folder, "line-ui-preview-" + size.Width + ".png")); }
                        Controls(form).OfType<Button>().Single(c => c.Text == "明细").PerformClick(); System.Windows.Forms.Application.DoEvents();
                        Check(grid.Visible && grid.Height > 80, "Compact details did not restore.");
                    }
                    form.Refresh(); System.Windows.Forms.Application.DoEvents();
                    using (var image = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(System.IO.Path.Combine(_folder, "line-ui-" + size.Width + ".png")); }
                }
                form.Hide();
            }
            doc.Editor.SetImpliedSelection(new ObjectId[0]);
            using (var empty = (Form)Activator.CreateInstance(formType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { doc }, null))
            {
                empty.Show(); System.Windows.Forms.Application.DoEvents();
                Check(!((Button)formType.GetField("_apply", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(empty)).Enabled, "Empty selection may apply.");
                using (var image = new Bitmap(empty.Width, empty.Height))
                { empty.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(System.IO.Path.Combine(_folder, "line-ui-empty.png")); }
                empty.Hide();
            }
            using (var tx = doc.Database.TransactionManager.StartTransaction())
                Check(((Line)tx.GetObject(Ids[0], OpenMode.ForRead)).StartPoint == new Point3d(100, 300, 10), "Preview/cancel wrote to CAD.");
            var assembly = typeof(BatchPdfPublisher.Commands).Assembly;
            var productionOptions = Activator.CreateInstance(assembly.GetType("BatchPdfPublisher.Services.LineCorrectionOptions", true), true);
            var applyArguments = new object[] { doc, Ids, productionOptions, 0 };
            var applied = (int)assembly.GetType("BatchPdfPublisher.Services.LineCorrectionService", true)
                .GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, applyArguments);
            var skipped = (int)applyArguments[3];
            Check(applied == 6 && skipped >= 2, "Apply count.");
            using (var tx = doc.Database.TransactionManager.StartTransaction())
            {
                var line = (Line)tx.GetObject(Ids[0], OpenMode.ForRead);
                Check(line.StartPoint.Y == line.EndPoint.Y && line.StartPoint.Z == 0 && line.EndPoint.Z == 0 && line.ColorIndex == 3 && line.LineWeight == LineWeight.LineWeight030, "Line/attributes.");
                Check(Math.Abs((line.StartPoint.X + line.EndPoint.X) / 2 - 600) < 1e-7 && line.StartPoint.Y == 300.5, "Midpoint rotation.");
                var poly = (Polyline)tx.GetObject(Ids[1], OpenMode.ForRead);
                Check(poly.Closed && poly.NumberOfVertices == 4 && poly.GetPoint2dAt(0).Y == poly.GetPoint2dAt(1).Y
                    && poly.GetPoint2dAt(1).X == poly.GetPoint2dAt(2).X && poly.Elevation == 0 && poly.GetStartWidthAt(0) == 20 && poly.GetEndWidthAt(0) == 25, "Connected polyline/width/closure.");
                var arc = (Polyline)tx.GetObject(Ids[2], OpenMode.ForRead);
                Check(arc.Elevation == 0 && arc.GetBulgeAt(0) == .5 && arc.GetPoint2dAt(1) == new Point2d(1000, 1), "Arc changed beyond elevation.");
                foreach (var id in Ids.Skip(3).Take(3))
                {
                    List<CorrectionPoint> points; bool closed, arcs; string error;
                    Check(LineCorrectionService.Read((Entity)tx.GetObject(id, OpenMode.ForRead), tx, out points, out closed, out arcs, out error)
                        && points.All(p => p.Z == 0) && points[0].Y == points[1].Y && points[1].X == points[2].X, "Legacy/3d vertex connection or elevation.");
                }
                Check(((Line)tx.GetObject(Ids[7], OpenMode.ForRead)).StartPoint.Z == 50, "Locked entity changed.");
            }
            doc.Editor.WriteMessage("\nLINE_CORRECTION_NATIVE_OK line midpoint polyline2d polyline3d sharedVertices arcs widths properties lockedLayer preview smallLargeUI");
        }
        catch (System.Exception ex) { doc.Editor.WriteMessage("\nLINE_CORRECTION_NATIVE_FAILED " + ex); }
    }

    [CommandMethod("WLLINECORRECTIONUNDOQA")]
    public void CheckUndo()
    {
        try
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            using (var tx = doc.Database.TransactionManager.StartTransaction())
            {
                var line = (Line)tx.GetObject(Ids[0], OpenMode.ForRead);
                Check(line.StartPoint == new Point3d(100, 300, 10) && line.EndPoint == new Point3d(1100, 301, -3), "One undo did not restore endpoints.");
                Check(((Polyline)tx.GetObject(Ids[1], OpenMode.ForRead)).Elevation == 300, "One undo did not restore polyline.");
            }
            doc.Editor.WriteMessage("\nLINE_CORRECTION_UNDO_OK");
        }
        catch (System.Exception ex) { CadApplication.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nLINE_CORRECTION_NATIVE_FAILED " + ex); }
    }
    private static void Check(bool value, string reason) { if (!value) throw new System.Exception(reason); }
    private static IEnumerable<Control> Controls(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var descendant in Controls(child)) yield return descendant; } }
}
