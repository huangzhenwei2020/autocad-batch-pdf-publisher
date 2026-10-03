using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    public static class CadStructuralRegistration
    {
        public static bool IsColumn(string dxf)=>string.Equals(dxf,"TCH_COLUMN",StringComparison.OrdinalIgnoreCase);
        public static bool IsBeam(string dxf)=>string.Equals(dxf,"TCH_BEAM",StringComparison.OrdinalIgnoreCase);
        public static bool IsStructure(string dxf)=>IsColumn(dxf)||IsBeam(dxf);
        public static List<PointModel> FindRectangle(IEnumerable<CadProbeSegment> display)
        {
            var segments=(display??new List<CadProbeSegment>()).Where(s=>s?.Start!=null&&s.End!=null
                &&Math.Abs(s.Start.Z-s.End.Z)<.001).Take(512).ToList();
            var found=new List<List<PointModel>>();
            foreach(var segment in segments)
            {
                var points=new List<PointModel>{new PointModel(segment.Start.X,segment.Start.Y),new PointModel(segment.End.X,segment.End.Y)};
                var used=new HashSet<CadProbeSegment>{segment};
                while(points.Count<=4)
                {
                    var current=points.Last();
                    var previous=points[points.Count-2];var dx=current.X-previous.X;var dy=current.Y-previous.Y;
                    var next=segments.Where(s=>!used.Contains(s)&&Math.Abs(s.Start.Z-segment.Start.Z)<.001)
                        .Where(s=>Near(current,s.Start)||Near(current,s.End))
                        .Where(s=>{var a=s.End.X-s.Start.X;var b=s.End.Y-s.Start.Y;
                            return Math.Abs(a*dx+b*dy)<Math.Sqrt((a*a+b*b)*(dx*dx+dy*dy))*.00001;}).ToList();
                    if(next.Count!=1)break;
                    var line=next[0];used.Add(line);var end=Near(current,line.Start)?line.End:line.Start;
                    if(Near(points[0],end))
                    {double w,d;if(TryRectangle(points,out w,out d))found.Add(points);break;}
                    points.Add(new PointModel(end.X,end.Y));
                }
            }
            return found.OrderByDescending(p=>{double w,d;TryRectangle(p,out w,out d);return w*d;}).FirstOrDefault()??new List<PointModel>();
        }
        private static bool Near(PointModel p,CadProbePoint q)=>Math.Abs(p.X-q.X)<.001&&Math.Abs(p.Y-q.Y)<.001;
        public static bool TryRectangle(IList<PointModel> points,out double width,out double depth)
        {
            width=depth=0;
            if(points==null||points.Count!=4||points.Any(p=>p==null||!Finite(p.X)||!Finite(p.Y)))return false;
            var dx=new double[4];var dy=new double[4];var len=new double[4];
            for(var i=0;i<4;i++){var j=(i+1)%4;dx[i]=points[j].X-points[i].X;dy[i]=points[j].Y-points[i].Y;len[i]=Math.Sqrt(dx[i]*dx[i]+dy[i]*dy[i]);if(len[i]<.5)return false;}
            for(var i=0;i<4;i++)if(Math.Abs(dx[i]*dx[(i+1)%4]+dy[i]*dy[(i+1)%4])>len[i]*len[(i+1)%4]*.00001)return false;
            if(Math.Abs(len[0]-len[2])>.01||Math.Abs(len[1]-len[3])>.01)return false;
            width=len[0];depth=len[1];return true;
        }
        public static void Add(CadFloorPlanCapture capture,Func<string,string,string> identity,
            List<ColumnModel> columns,List<BeamModel> beams,List<string> messages)
        {
            foreach(var source in capture.Probe.Entities.Where(e=>IsStructure(e.DxfName)))
            {
                var outline=(source.StructuralOutline??new List<PointModel>()).Select(capture.Floor.Alignment.ToModel).ToList();
                double width,depth;
                if(!TryRectangle(outline,out width,out depth))
                {messages.Add(capture.Floor.Storey.Name+" · "+(IsColumn(source.DxfName)?"柱":"梁")+"未找到可确认的矩形轮廓，未生成；请检查 CAD 对象或在模型中绘制。");continue;}
                var floor=capture.Floor.Storey.Id;
                if(IsColumn(source.DxfName))
                    columns.Add(new ColumnModel {Id=identity("column",source.Handle),StoreyId=floor,
                        X=outline.Average(p=>p.X),Y=outline.Average(p=>p.Y),Width=width,Depth=depth,
                        RotationDegrees=Math.Atan2(outline[1].Y-outline[0].Y,outline[1].X-outline[0].X)*180/Math.PI,Height=0});
                else
                {
                    // Use the long rectangle axis; CAD plan supplies width, not section depth.
                    var i=width>=depth?0:1;var j=(i+1)%4;var k=(i+2)%4;var l=(i+3)%4;
                    beams.Add(new BeamModel {Id=identity("beam",source.Handle),StoreyId=floor,
                        X1=(outline[i].X+outline[l].X)/2,Y1=(outline[i].Y+outline[l].Y)/2,
                        X2=(outline[j].X+outline[k].X)/2,Y2=(outline[j].Y+outline[k].Y)/2,
                        Width=Math.Min(width,depth),Depth=500});
                    messages.Add(capture.Floor.Storey.Name+" · 梁宽采用 CAD 轮廓，截面高暂用 500 mm，可在模型属性中修改。");
                }
            }
        }
        private static bool Finite(double v)=>!double.IsNaN(v)&&!double.IsInfinity(v);
    }
}
