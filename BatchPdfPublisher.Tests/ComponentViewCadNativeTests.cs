using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.BuildingModel;

[assembly:CommandClass(typeof(ComponentViewCadNativeTests))]
public sealed class ComponentViewCadNativeTests
{
    private static int _before;
    private static string _file;
    private static readonly Type Service=typeof(BatchPdfPublisher.Commands).Assembly.GetType("BatchPdfPublisher.Features.BuildingModel.Services.ComponentViewCadService",true);
    private static void Check(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
    private static int Count(Database db){using(var tx=db.TransactionManager.StartOpenCloseTransaction())return ((BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForRead)).Cast<ObjectId>().Count();}
    [CommandMethod("WLCOMPONENTVIEWQAPREP",CommandFlags.NoUndoMarker)]
    public void Prepare()
    {
        var doc=Application.DocumentManager.MdiActiveDocument;_before=Count(doc.Database);
        var root=Environment.GetEnvironmentVariable("WANLUO_COMPONENT_QA");Check(!string.IsNullOrEmpty(root),"Disposable QA directory required.");
        var job=new ComponentCadViews {CadProcessId=System.Diagnostics.Process.GetCurrentProcess().Id,DrawingFingerprint=doc.Database.FingerprintGuid.ToString(),DrawingPath=doc.Name,Name="合成柜体",Gap=500};
        for(var i=0;i<4;i++){var view=new ViewDocument {Title="合成立面 "+i,Scale=1};var w=i<2?1200:600;foreach(var xy in new[]{new[]{0d,0d,(double)w,0d},new[]{(double)w,0d,(double)w,900d},new[]{(double)w,900d,0d,900d},new[]{0d,900d,0d,0d}})view.Lines.Add(new ViewLine {X1=xy[0],Y1=xy[1],X2=xy[2],Y2=xy[3]});job.Views.Add(view);}
        _file=job.Queue();Service.GetField("_pending",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,_file);
    }
    [CommandMethod("WLCOMPONENTVIEWQACHECK",CommandFlags.NoUndoMarker)]
    public void Verify()
    {
        var doc=Application.DocumentManager.MdiActiveDocument;Check(Count(doc.Database)==_before+4,"Expected four block references.");
        using(var tx=doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var space=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database),OpenMode.ForRead);
            foreach(var reference in space.Cast<ObjectId>().Select(id=>tx.GetObject(id,OpenMode.ForRead)).OfType<BlockReference>())
            {
                var block=(BlockTableRecord)tx.GetObject(reference.BlockTableRecord,OpenMode.ForRead);
                var entities=block.Cast<ObjectId>().Select(id=>tx.GetObject(id,OpenMode.ForRead)).ToList();
                Check(entities.OfType<Line>().Count()==4&&entities.OfType<RotatedDimension>().Count()==2&&entities.OfType<DBText>().Count()==1,"Editable contours/dimensions/title missing.");
            }
        }
        Check(File.ReadAllText(_file+".result.txt").Contains("已插入 4"),"Native receipt missing.");
        var invalid=new ComponentCadViews();var bad=new ViewDocument();bad.Lines.Add(new ViewLine {X1=double.NaN});invalid.Views.Add(bad);
        try{Service.GetMethod("Insert",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{doc.Database,invalid,Point3d.Origin});throw new InvalidDataException("Invalid geometry accepted.");}catch(TargetInvocationException){}
        Check(Count(doc.Database)==_before+4,"Failed insertion partially committed.");
        doc.Editor.WriteMessage("\nCOMPONENT_CAD_INSERT_OK fourBlocks editableLines trueDimensions titles receipt invalidRollback\n");
    }
    [CommandMethod("WLCOMPONENTVIEWQAUNDO",CommandFlags.NoUndoMarker)]
    public void UndoCheck(){var doc=Application.DocumentManager.MdiActiveDocument;Check(Count(doc.Database)==_before,"One undo did not remove all views.");doc.Editor.WriteMessage("\nCOMPONENT_CAD_UNDO_OK\n");}
    [CommandMethod("WLCOMPONENTVIEWQAREJECT",CommandFlags.NoUndoMarker)]
    public void RejectCheck()
    {
        var doc=Application.DocumentManager.MdiActiveDocument;
        foreach(var cancel in new[]{false,true})
        {
            var job=new ComponentCadViews {CadProcessId=System.Diagnostics.Process.GetCurrentProcess().Id,DrawingFingerprint=cancel?doc.Database.FingerprintGuid.ToString():Guid.NewGuid().ToString(),DrawingPath=doc.Name};
            job.Views.Add(new ViewDocument());var file=job.Queue();if(cancel)File.WriteAllText(file+".cancel","cancel");
            Service.GetField("_pending",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,file);
            Service.GetMethod("Execute").Invoke(null,new object[]{doc});
            Check(File.Exists(file+".done")&&File.ReadAllText(file+".result.txt").Contains(cancel?"已取消":"目标图纸"),"Cancellation/target guard failed.");
            Check(Count(doc.Database)==_before,"Rejected request changed drawing.");
        }
        doc.Editor.WriteMessage("\nCOMPONENT_CAD_REJECT_OK targetMismatch preCancelled noChanges\n");
    }
}
