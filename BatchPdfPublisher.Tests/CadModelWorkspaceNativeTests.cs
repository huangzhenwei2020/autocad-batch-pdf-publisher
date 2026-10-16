using System;
using System.IO;
using Autodesk.AutoCAD.Runtime;
using Application=Autodesk.AutoCAD.ApplicationServices.Application;

[assembly:CommandClass(typeof(CadModelWorkspaceNativeTests))]
public sealed class CadModelWorkspaceNativeTests
{
    [CommandMethod("WLMODELWORKSPACEQA",CommandFlags.NoUndoMarker)]
    public void Run()
    {
        var root=Environment.GetEnvironmentVariable("WANLUO_WORKSPACE_QA_ROOT");
        var document=Application.DocumentManager.MdiActiveDocument;
        if(string.IsNullOrEmpty(root)||!string.Equals(System.Diagnostics.Process.GetCurrentProcess().ProcessName,"accoreconsole",StringComparison.OrdinalIgnoreCase)
            ||Path.GetExtension(document.Database.Filename)!=".dwt")throw new InvalidOperationException("Disposable CAD QA process required");
        var original=Directory.GetCurrentDirectory();var dbmod=Application.GetSystemVariable("DBMOD");
        Directory.CreateDirectory(root);
        try
        {
            Directory.SetCurrentDirectory(root);CadModelWorkspaceTests.Run();
            if(!Equals(dbmod,Application.GetSystemVariable("DBMOD")))throw new InvalidDataException("Workspace changes CAD drawing");
            File.WriteAllText(Path.Combine(root,"result.txt"),"CAD_MODEL_WORKSPACE_NATIVE_OK drawingUnchanged realPlugin resources nativeWpf");
            document.Editor.WriteMessage("\nCAD_MODEL_WORKSPACE_NATIVE_OK\n");
        }
        catch(System.Exception ex){File.WriteAllText(Path.Combine(root,"result.txt"),ex.ToString());throw;}
        finally{Directory.SetCurrentDirectory(original);}
    }
}
