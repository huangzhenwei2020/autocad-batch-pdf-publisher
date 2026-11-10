using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace BatchPdfPublisher.BuildingModel
{
    public static class ComponentElevationProjection
    {
        public static List<ViewDocument> Four(BuildingVolume volume,string name,double frontDegrees=0,CancellationToken cancellation=default(CancellationToken),Action<string> progress=null)
        {
            var result=new List<ViewDocument>();var titles=new[]{"正立面","背立面","左立面","右立面"};var angles=new[]{0d,180d,-90d,90d};
            for(var i=0;i<4;i++){cancellation.ThrowIfCancellationRequested();progress?.Invoke("生成"+titles[i]);result.Add(Project(volume,name+" · "+titles[i],frontDegrees+angles[i],0,cancellation));}
            return result;
        }
        public static ViewDocument Project(BuildingVolume volume,string title,double azimuth,double elevation=0,CancellationToken cancellation=default(CancellationToken))
        {
            if(volume==null||volume.Faces.Count==0)throw new ArgumentException("请先载入模型。");
            var faces=VolumeRenderer.Project(volume,new VolumeCamera {AzimuthDegrees=azimuth,ElevationDegrees=elevation,Perspective=false},_=>cancellation.ThrowIfCancellationRequested(),edgesOnly:true,includeHiddenEdges:false);
            var document=new ViewDocument {Id=Guid.NewGuid().ToString("N"),Title=title,Kind=ViewKind.Elevation,Scale=1};
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var face in faces)foreach(var edge in face.Edges??new List<List<PointModel>>())
            {
                cancellation.ThrowIfCancellationRequested();if(edge.Count<2)continue;var a=edge[0];var b=edge[1];
                if(Math.Abs(a.X-b.X)+Math.Abs(a.Y-b.Y)<.001)continue;
                string Key(PointModel p)=>Math.Round(p.X,4).ToString("R",CultureInfo.InvariantCulture)+","+Math.Round(p.Y,4).ToString("R",CultureInfo.InvariantCulture);
                var k1=Key(a);var k2=Key(b);if(!keys.Add(string.CompareOrdinal(k1,k2)<0?k1+"|"+k2:k2+"|"+k1))continue;
                document.Lines.Add(new ViewLine {Layer="WL-图库-立面轮廓",X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y});
            }
            if(document.Lines.Count==0)throw new ArgumentException("该方向没有可见轮廓，请核对模型方向。");
            var minX=document.Lines.Min(l=>Math.Min(l.X1,l.X2));var minY=document.Lines.Min(l=>Math.Min(l.Y1,l.Y2));
            foreach(var l in document.Lines){l.X1-=minX;l.X2-=minX;l.Y1-=minY;l.Y2-=minY;}
            return document;
        }
    }
}
