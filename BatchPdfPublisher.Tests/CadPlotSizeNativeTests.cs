using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
[assembly: CommandClass(typeof(CadPlotSizeQA))]
public class CadPlotSizeQA
{
    [CommandMethod("WLPRINTSIZEQA")]
    public void Run()
    {
        try { RunCore(); }
        catch(System.Exception ex) { Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nPLOT_SIZE_NATIVE_FAILED "+ex+"\n"); }
    }
    private void RunCore()
    {
        CheckMediaPreferences();
        var doc=Application.DocumentManager.MdiActiveDocument;
        if(!string.Equals(System.Diagnostics.Process.GetCurrentProcess().ProcessName,"accoreconsole",StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(doc.Database.Filename),".dwt",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fresh disposable core-console drawing required");
        var output=Environment.GetEnvironmentVariable("WANLUO_PLOT_QA_OUTPUT");
        if(string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("QA output required");
        Directory.CreateDirectory(output);
        var raw=new List<string>(); var sheets=new List<SheetItem>();
        foreach(var frame in new[]{"A3","A2"})
        {
            double w=frame=="A3"?420:594, h=frame=="A3"?297:420;
            using(var tx=doc.Database.TransactionManager.StartTransaction())
            {
                var table=(BlockTable)tx.GetObject(doc.Database.BlockTableId,OpenMode.ForRead);
                var model=(BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                var ids=new List<ObjectId>(); foreach(ObjectId id in model)ids.Add(id);
                foreach(var id in ids)((Entity)tx.GetObject(id,OpenMode.ForWrite)).Erase();
                var border=new Polyline();
                border.AddVertexAt(0,new Point2d(5,5),0,0,0);border.AddVertexAt(1,new Point2d(w-5,5),0,0,0);
                border.AddVertexAt(2,new Point2d(w-5,h-5),0,0,0);border.AddVertexAt(3,new Point2d(5,h-5),0,0,0);border.Closed=true;
                model.AppendEntity(border);tx.AddNewlyCreatedDBObject(border,true);
                var ruler=new Line(new Point3d(50,50,0),new Point3d(150,50,0));
                ruler.ColorIndex=1;
                model.AppendEntity(ruler);tx.AddNewlyCreatedDBObject(ruler,true);tx.Commit();
            }
            using(var tx=doc.Database.TransactionManager.StartTransaction())
            {
                var table=(BlockTable)tx.GetObject(doc.Database.BlockTableId,OpenMode.ForRead);
                var model=(BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                for(int i=0;i<40;i++) {
                    var line=new Line(new Point3d(20,70+i*3,0),new Point3d(w-20,70+i*3,0));
                    model.AppendEntity(line);tx.AddNewlyCreatedDBObject(line,true);
                    var text=new DBText{Position=new Point3d(30,72+i*3,0),Height=1.5,TextString="Print size QA row "+i};
                    model.AppendEntity(text);tx.AddNewlyCreatedDBObject(text,true);
                }
                tx.Commit();
            }
            doc.Editor.Regen();
            var sheet=new SheetItem{Frame=frame,PaperOrientation="横向",PrintScale="1:1",MinX=0,MinY=0,MaxX=w,MaxY=h,SourceLayout="Model",SourceFile=doc.Name,SheetName="100mm ruler",SheetNumber=frame};
            var file=Path.Combine(output,frame+".pdf");
            typeof(PdfPublisherService).GetMethod("PlotSinglePage",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{doc,sheet,file,null,"自动适配",0});
            raw.Add(file);sheets.Add(sheet);
            using(var tx=doc.Database.TransactionManager.StartTransaction())
            {
                var layout=(Layout)tx.GetObject(LayoutManager.Current.GetLayoutId("Model"),OpenMode.ForRead);
                var args=new object[]{layout,sheet,null,"自动适配",true,false,PlotRotation.Degrees000};
                using(var ps=(PlotSettings)typeof(PdfPublisherService).GetMethod("CreateSettings",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args))
                {
                    doc.Editor.WriteMessage("\nPLOT_SIZE_SETTINGS "+frame+" DEVICE="+ps.PlotConfigurationName+" MEDIA="+ps.CanonicalMediaName+" MARGINS="+ps.PlotPaperMargins+" PAPER="+ps.PlotPaperSize);
                    if(frame=="A3")foreach(string media in PlotSettingsValidator.Current.GetCanonicalMediaNameList(ps))
                        if(media.Contains("A3")||media.Contains("420.00"))doc.Editor.WriteMessage("\nPLOT_SIZE_MEDIA "+media);
                }
            }
        }
        var merger=typeof(PdfPublisherService).GetNestedType("PdfMerger",BindingFlags.NonPublic);
        merger.GetMethod("Merge",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{raw,sheets,Path.Combine(output,"mixed-auto.pdf"),"自动适配",null});
        merger.GetMethod("Merge",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{raw,sheets,Path.Combine(output,"mixed-3mm.pdf"),"保留 3 mm 白边",null});
        doc.Editor.WriteMessage("\nPLOT_SIZE_NATIVE_OK\n");
    }
    private static void CheckMediaPreferences()
    {
        var pick=typeof(PdfPublisherService).GetMethod("PickBestMedia",BindingFlags.NonPublic|BindingFlags.Static);
        var plain="ISO_A3_(420.00_x_297.00_MM)";
        var expanded="ISO_expand_A3_(420.00_x_297.00_MM)";
        var bleed="ISO_full_bleed_A3_(420.00_x_297.00_MM)";
        foreach(var mode in new[]{"自动适配","无白边（满幅）","保留 3 mm 白边"}) {
            var result=(string)pick.Invoke(null,new object[]{new[]{plain,expanded,bleed},420d,297d,mode,true,false});
            if(result!=bleed) throw new InvalidOperationException("Margin mode did not prefer full bleed: "+mode);
            result=(string)pick.Invoke(null,new object[]{new[]{plain},420d,297d,mode,true,false});
            if(result!=plain) throw new InvalidOperationException("Exact non-bleed fallback lost");
        }
    }
}
