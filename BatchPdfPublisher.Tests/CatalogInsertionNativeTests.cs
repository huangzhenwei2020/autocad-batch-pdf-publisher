using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.Views;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(CatalogInsertionNativeTests))]
public sealed class CatalogInsertionNativeTests
{
    private static readonly string[] Inputs = { "1:1", "1:20", "1:50", "1:75", "1:100", "1:200", "1:500", "1：75", "1/50", "2:1", "2:150", "100", "0.5", "1:2.5", " 1 : 75 " };
    private static readonly double[] Factors = { 1, 20, 50, 75, 100, 200, 500, 75, 50, 0.5, 75, 100, 0.5, 2.5, 75 };
    private static int _case;
    private static HashSet<ObjectId> _before;

    [CommandMethod("WLCATALOGSCALEQA")]
    public void Insert()
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        try
        {
            Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT")), "Private test configuration required");
            if (_case == 0)
                File.WriteAllLines(UserDataPaths.SettingsFile("catalog.settings", "BatchPdfPublisher.catalog.settings"), new[] { "30", "7", "20,30,70,24,24", "3.5", "1:1", "Standard", "ByAci", "7", "11111" });
            var sheets = new[] { new SheetItem { Building = "目录测试", SheetNumber = "QA-01", SheetName = "测试图纸", Frame = "A3", PrintScale = "1:75" } };
            CatalogSettings settings;
            var window = new BppCatalogWindow(document, sheets, null);
            window.Show();
            Pump();
            try
            {
                var scale = (ComboBox)window.FindName("Scale");
                if (_case == 0)
                {
                    foreach (var invalid in new[] { "", "0", "-1", "1:0", "0:1", "1:2:3", "NaN", "Infinity", "1:Infinity", "1/2/3" })
                    {
                        scale.Text = invalid;
                        var rejected = false;
                        try { window.ReadSettings(); }
                        catch (InvalidOperationException) { rejected = true; }
                        Check(rejected, "Invalid catalog scale accepted: " + invalid);
                    }
                }
                scale.Text = Inputs[_case];
                settings = window.ReadSettings();
                Check(Math.Abs(settings.Scale - Factors[_case]) < 1e-10, "WPF scale conversion failed: " + Inputs[_case]);
                typeof(BppCatalogWindow).GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
            }
            finally { window.Close(); }

            var reopened = new BppCatalogWindow(document, sheets, null);
            try
            {
                Check(((ComboBox)reopened.FindName("Scale")).Text == Inputs[_case], "Saved scale text changed on reopening");
                Check(Math.Abs(reopened.ReadSettings().Scale - Factors[_case]) < 1e-10, "Saved scale factor changed on reopening");
            }
            finally { reopened.Close(); }

            _before = EntityIds(document);
            Check(CatalogInsertionService.Insert(document, sheets, settings), "Insertion was canceled unexpectedly");
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var created = EntityIds(document).Except(_before).Select(id => transaction.GetObject(id, OpenMode.ForRead)).ToList();
                var lines = created.OfType<Line>().ToList();
                var texts = created.OfType<DBText>().ToList();
                var factor = Factors[_case];
                var minX = lines.Min(line => Math.Min(line.StartPoint.X, line.EndPoint.X));
                var maxX = lines.Max(line => Math.Max(line.StartPoint.X, line.EndPoint.X));
                var minY = lines.Min(line => Math.Min(line.StartPoint.Y, line.EndPoint.Y));
                var maxY = lines.Max(line => Math.Max(line.StartPoint.Y, line.EndPoint.Y));
                Check(Math.Abs(maxX - minX - 168 * factor) < 1e-6, "Catalog width has the wrong scale: " + (maxX - minX) + ", factor=" + factor);
                Check(Math.Abs(maxY - minY - 14 * factor) < 1e-6, "Catalog row heights have the wrong scale: " + (maxY - minY));
                Check(texts.Count == 11 && texts.All(text => Math.Abs(text.Height - 3.5 * factor) < 1e-6), "Catalog text height has the wrong scale");
            }
            document.Editor.WriteMessage("\nCATALOG_SCALE_CASE_OK " + Inputs[_case] + " factor=" + Factors[_case] + "\n");
            _case++;
        }
        catch (System.Exception error) { Fail(document, error); }
    }

    [CommandMethod("WLCATALOGUNDOQA")]
    public void CheckUndo()
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        try
        {
            Check(_before != null && EntityIds(document).SetEquals(_before), "One undo did not remove the inserted catalog");
            document.Editor.WriteMessage("\nCATALOG_UNDO_CASE_OK " + _case + "\n");
            if (_case == Inputs.Length)
                document.Editor.WriteMessage("\nCATALOG_SCALE_QA_OK\n");
        }
        catch (System.Exception error) { Fail(document, error); }
    }

    private static HashSet<ObjectId> EntityIds(Document document)
    {
        using (var transaction = document.Database.TransactionManager.StartTransaction())
            return new HashSet<ObjectId>(((BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead)).Cast<ObjectId>());
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Fail(Document document, System.Exception error)
    {
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT"), "failure.txt"), error.ToString());
        document.Editor.WriteMessage("\nCATALOG_SCALE_QA_FAILED " + error + "\n");
    }
}
