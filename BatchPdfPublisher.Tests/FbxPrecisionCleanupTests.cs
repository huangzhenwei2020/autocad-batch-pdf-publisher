using System.Reflection;
using Assimp;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.BuildingModel.Export;

internal static class FbxPrecisionCleanupTests
{
    internal static void Run()
    {
        void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);}
        BuildingExportScene Fixture(double size=1000)
        {
            var mesh=new BuildingExportMesh {Id="precision-test",Vertices=new(){new[]{0d,0,0},new[]{size,0d,0},new[]{size,size,0},new[]{0d,size,0}},
                Triangles=new(){new(){A=0,B=1,C=2},new(){A=0,B=2,C=3}}};
            return new(){Name="fixture",Storeys=new(){new(){Id="F",Name="Test floor"}},Materials=new(){new(){Name="wall",Red=.5,Green=.5,Blue=.5}},
                Meshes=new(){mesh},Instances=new(){new(){Id="W",Name="Wall",StoreyId="F",MeshIndex=0}}};
        }
        void Sliver(BuildingExportScene scene,double[][] points,int material=0)
        {
            var m=scene.Meshes[0];var n=m.Vertices.Count;m.Vertices.AddRange(points);
            m.Triangles.Add(new(){A=n,B=n+1,C=n+2,MaterialIndex=material});
        }
        Scene Convert(BuildingExportScene scene,List<string> warnings,CancellationToken token=default)
        {
            try{return (Scene)typeof(BuildingFbxWriter).GetMethod("CreateScene",BindingFlags.NonPublic|BindingFlags.Static)!
                .Invoke(null,new object[]{scene,token,warnings})!;}
            catch(TargetInvocationException ex){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();throw;}
        }
        void Reject(BuildingExportScene scene)
        {
            try{Convert(scene,new());throw new Exception("Unsafe precision cleanup was accepted.");}catch(ArgumentException){}
        }
        foreach(var collinear in new[]{false,true})
        {
            var scene=Fixture();
            Sliver(scene,collinear?new[]{new[]{1000d,1000,0},new[]{1100d,1100,0},new[]{1050d,1050.00001,0}}
                :new[]{new[]{1000d,1000,0},new[]{1000.00001,1000,0},new[]{1000d,1100,0}});
            var before=System.Text.Json.JsonSerializer.Serialize(scene);var warnings=new List<string>();var output=Convert(scene,warnings);
            Check(warnings.Count==1&&warnings[0].Contains("Wall")&&warnings[0].Contains("1 个退化面"),"Cleanup warning lacks count/element identity.");
            Check(output.MeshCount==1&&output.Meshes[0].FaceCount==1&&output.Meshes[0].VertexCount==4,"Zero-area cleanup lost surface or retained loose vertices.");
            Check(before==System.Text.Json.JsonSerializer.Serialize(scene),"FBX cleanup mutated the neutral/source geometry.");
        }
        var tiny=Fixture(1);Sliver(tiny,new[]{new[]{1000d,1000,0},new[]{1000.00001,1000,0},new[]{1000d,1100,0}});Reject(tiny);
        var detail=Fixture();Sliver(detail,new[]{new[]{1000d,1000,0},new[]{1000.001,1000,0},new[]{1000d,1100,0}});
        var detailWarnings=new List<string>();Convert(detail,detailWarnings);Check(detailWarnings.Count==0,"A representable real detail was treated as a collapsed face.");
        var all=Fixture();all.Meshes[0].Triangles.Clear();Sliver(all,new[]{new[]{1000d,1000,0},new[]{1000.00001,1000,0},new[]{1000d,1100,0}});Reject(all);
        var material=Fixture();material.Materials.Add(new(){Name="glass"});
        Sliver(material,new[]{new[]{1000d,1000,0},new[]{1000.00001,1000,0},new[]{1000d,1100,0}},1);Reject(material);
        var large=Fixture(5000);Sliver(large,new[]{new[]{1e8,0d,0},new[]{1e8+.001,0d,0},new[]{1e8,1000d,0}});Reject(large);
        var tall=Fixture(5000);Sliver(tall,new[]{new[]{1e8,0d,0},new[]{1e8+.1,0d,0},new[]{1e8,.1,0}});Reject(tall);
        var cumulative=Fixture(5000);
        for(var i=0;i<21;i++)Sliver(cumulative,new[]{new[]{1000d,1000,0},new[]{1000.00001,1000,0},new[]{1000d,11000,0}});
        Reject(cumulative);
        var invalid=Fixture();invalid.Meshes[0].Triangles[0].C=999;Reject(invalid);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{Convert(Fixture(),new(),cancel.Token);throw new Exception("Cleanup ignored cancellation.");}catch(OperationCanceledException){}
        Console.WriteLine("FBX_PRECISION_CLEANUP_OK zeroArea coincident collinear warning counts sourceUnchanged materialProtection wholeMesh relative absolute cumulative height invalid cancellation");
    }
}
