using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    public static class CadOpeningJambPlacement
    {
        // Two actual jamb segments belonging to this opening must span the wall body,
        // be perpendicular to its verified datum and be separated by native opening width.
        // A full-width native envelope can recover axis-aligned openings only after
        // its span equals native width and its wall body intersects a unique host.
        public static CadOpeningPlacement Find(CadFloorRegistrationContext floor,CadBuildingProbeEntity opening,double width)
        {
            if(opening==null)return null;
            var candidates = new List<CadOpeningPlacement>(); var scale=floor.Alignment.MillimetresPerCadUnit;
            foreach(var wall in floor.WallCandidates.Where(w=>w.StraightHorizontalLineVerified && w.CandidateThickness.HasValue && w.CandidateAxisOffset.HasValue))
            {
                var a=wall.CurveStart; var b=wall.CurveEnd;
                if(a==null || b==null) continue;
                var dx=b.X-a.X; var dy=b.Y-a.Y; var length=Math.Sqrt(dx*dx+dy*dy);
                if(length*scale<1) continue;
                dx/=length; dy/=length;
                var offset=wall.CandidateAxisOffset.Value; var half=wall.CandidateThickness.Value/2;
                var jambs=new List<double>();
                foreach(var line in opening.DisplaySegments ?? new List<CadProbeSegment>())
                {
                    if(line.Start==null || line.End==null) continue;
                    var p=line.Start; var q=line.End;
                    var tp=(p.X-a.X)*dx+(p.Y-a.Y)*dy; var tq=(q.X-a.X)*dx+(q.Y-a.Y)*dy;
                    var np=-(p.X-a.X)*dy+(p.Y-a.Y)*dx; var nq=-(q.X-a.X)*dy+(q.Y-a.Y)*dx;
                    if(Math.Abs(tp-tq)*scale>0.5 || Math.Abs(p.Z-q.Z)*scale>0.5 || Math.Abs(p.Z-a.Z)*scale>0.5) continue;
                    // Jamb must actually cross both wall faces; a door leaf is insufficient.
                    if(Math.Min(np,nq)>offset-half+0.5/scale || Math.Max(np,nq)<offset+half-0.5/scale) continue;
                    var t=(tp+tq)/2;
                    if(t < -0.5/scale || t>length+0.5/scale) continue;
                    if(!jambs.Any(x=>Math.Abs(x-t)*scale<0.5)) jambs.Add(t);
                }
                for(var i=0;i<jambs.Count;i++) for(var j=i+1;j<jambs.Count;j++)
                {
                    if(Math.Abs(Math.Abs(jambs[j]-jambs[i])*scale-width)>0.5) continue;
                    try
                    {
                        var placement=CadOpeningPlacement.Create(floor,wall.Handle,new PointModel(a.X,a.Y),new PointModel(b.X,b.Y),
                            new PointModel(a.X+jambs[i]*dx,a.Y+jambs[i]*dy),new PointModel(a.X+jambs[j]*dx,a.Y+jambs[j]*dy),width);
                        placement.Source="本洞口自身两条穿过墙厚的门窗侧边线，按原生宽度与唯一宿主定位线核验";
                        if(!candidates.Any(x=>x.HostSourceHandle==placement.HostSourceHandle && Math.Abs(x.DistanceFromWallStart-placement.DistanceFromWallStart)<0.5)) candidates.Add(placement);
                    }
                    catch(System.IO.InvalidDataException) { }
                }
                // Tianzheng grouped openings may expose a full-width frame edge
                // instead of two transverse jambs. Its actual endpoints must agree
                // with native width, wall direction, wall body and a unique host.
                foreach(var line in opening.DisplaySegments ?? new List<CadProbeSegment>())
                {
                    if(line.Start==null || line.End==null)continue;
                    var p=line.Start; var q=line.End;
                    var tp=(p.X-a.X)*dx+(p.Y-a.Y)*dy; var tq=(q.X-a.X)*dx+(q.Y-a.Y)*dy;
                    var np=-(p.X-a.X)*dy+(p.Y-a.Y)*dx; var nq=-(q.X-a.X)*dy+(q.Y-a.Y)*dx;
                    if(Math.Abs(np-nq)*scale>0.5 || Math.Abs(Math.Abs(tq-tp)*scale-width)>0.5
                        || Math.Abs(np-offset)>half+0.5/scale || Math.Abs(nq-offset)>half+0.5/scale
                        || Math.Abs(p.Z-a.Z)*scale>0.5 || Math.Abs(q.Z-a.Z)*scale>0.5)continue;
                    try {
                        var placement=CadOpeningPlacement.Create(floor,wall.Handle,new PointModel(a.X,a.Y),new PointModel(b.X,b.Y),
                            new PointModel(a.X+tp*dx,a.Y+tp*dy),new PointModel(a.X+tq*dx,a.Y+tq*dy),width);
                        placement.Source="本洞口自身整宽框边线，按原生宽度、墙厚范围与唯一宿主定位线核验";
                        if(!candidates.Any(x=>x.HostSourceHandle==placement.HostSourceHandle && Math.Abs(x.DistanceFromWallStart-placement.DistanceFromWallStart)<0.5))candidates.Add(placement);
                    } catch(System.IO.InvalidDataException) { }
                }
                FindDoorSwings(floor,opening,wall,width,candidates);
            }
            if(candidates.Count>0)return candidates.Count==1 ? candidates[0] : null;
            if(opening.BoundsMin==null || opening.BoundsMax==null)return null;
            var min=opening.BoundsMin;var max=opening.BoundsMax;
            if(new[] { min.X,min.Y,min.Z,max.X,max.Y,max.Z,width,scale }.Any(v=>double.IsNaN(v) || double.IsInfinity(v))
                || width<=0 || scale<=0 || max.X<min.X || max.Y<min.Y)return null;
            foreach(var wall in floor.WallCandidates.Where(w=>w.StraightHorizontalLineVerified && w.CandidateThickness.HasValue && w.CandidateAxisOffset.HasValue)) {
                var a=wall.CurveStart;var b=wall.CurveEnd;if(a==null || b==null)continue;
                var dx=b.X-a.X;var dy=b.Y-a.Y;var length=Math.Sqrt(dx*dx+dy*dy);if(length*scale<1)continue;
                dx/=length;dy/=length;
                // A rotated AABB has an enlarged span: never infer its endpoints.
                if(Math.Min(Math.Abs(dx),Math.Abs(dy))>1e-8 || Math.Abs(min.Z-a.Z)*scale>0.5)continue;
                var points=new[] { new PointModel(min.X,min.Y),new PointModel(max.X,min.Y),new PointModel(max.X,max.Y),new PointModel(min.X,max.Y) };
                var ts=points.Select(p=>(p.X-a.X)*dx+(p.Y-a.Y)*dy).ToArray();
                var ns=points.Select(p=>-(p.X-a.X)*dy+(p.Y-a.Y)*dx).ToArray();
                var low=ts.Min();var high=ts.Max();var offset=wall.CandidateAxisOffset.Value;var half=wall.CandidateThickness.Value/2;
                if(Math.Abs((high-low)*scale-width)>0.5 || low < -0.5/scale || high>length+0.5/scale
                    || ns.Min()>offset+half+0.5/scale || ns.Max()<offset-half-0.5/scale)continue;
                try {
                    var placement=CadOpeningPlacement.Create(floor,wall.Handle,new PointModel(a.X,a.Y),new PointModel(b.X,b.Y),
                        new PointModel(a.X+low*dx,a.Y+low*dy),new PointModel(a.X+high*dx,a.Y+high*dy),width);
                    placement.Source="本洞口原生范围整宽投影，与原生宽度和唯一水平/竖直宿主墙交叠核验";
                    candidates.Add(placement);
                } catch(System.IO.InvalidDataException) { }
            }
            return candidates.Count==1 ? candidates[0] : null;
        }
        private sealed class Hinge { public double Outer,Closed,LeafWidth; }
        private static void FindDoorSwings(CadFloorRegistrationContext floor,CadBuildingProbeEntity opening,CadBuildingProbeEntity wall,
            double width,List<CadOpeningPlacement> candidates)
        {
            var a=wall.CurveStart;var b=wall.CurveEnd;var scale=floor.Alignment.MillimetresPerCadUnit;
            var dx=b.X-a.X;var dy=b.Y-a.Y;var length=Math.Sqrt(dx*dx+dy*dy);dx/=length;dy/=length;
            Func<CadProbePoint,double> t=p=>(p.X-a.X)*dx+(p.Y-a.Y)*dy;
            Func<CadProbePoint,double> n=p=>-(p.X-a.X)*dy+(p.Y-a.Y)*dx;
            var half=wall.CandidateThickness.Value/2;var offset=wall.CandidateAxisOffset.Value;
            var hinges=new List<Hinge>();
            foreach(var arc in opening.DisplayArcs ?? new List<CadProbeArc>()) {
                if(arc.Center==null || arc.Start==null || arc.End==null
                    || new[] { arc.Radius,arc.Sweep,arc.Center.X,arc.Center.Y,arc.Center.Z,arc.Start.X,arc.Start.Y,arc.Start.Z,arc.End.X,arc.End.Y,arc.End.Z }.Any(v=>double.IsNaN(v) || double.IsInfinity(v))
                    || arc.Radius*scale<1 || arc.Sweep<Math.PI/6 || arc.Sweep>Math.PI/2+.01
                    || Math.Abs(n(arc.Center)-offset)>half+.5/scale || Math.Abs(arc.Center.Z-a.Z)*scale>.5)continue;
                var closed=new[] { arc.Start,arc.End }.Where(p=>Math.Abs(n(p)-n(arc.Center))*scale<.5 && Math.Abs(p.Z-a.Z)*scale<.5).ToList();
                if(closed.Count!=1)continue;
                var closedT=t(closed[0]);var centerT=t(arc.Center);
                if(Math.Abs(Math.Abs(closedT-centerT)-arc.Radius)*scale>.5)continue;
                foreach(var line in opening.DisplaySegments ?? new List<CadProbeSegment>()) {
                    if(line.Start==null || line.End==null)continue;
                    var p=line.Start;var q=line.End;var tp=t(p);var tq=t(q);
                    var leafThickness=Math.Abs(tp-tq);
                    // This short end of the actual leaf rectangle straddles its
                    // arc centre. Include half its measured thickness to recover
                    // the aperture, rather than treating leaf radius as door width.
                    if(leafThickness*scale<1 || leafThickness*scale>width*.2
                        || Math.Abs(n(p)-n(arc.Center))*scale>.5 || Math.Abs(n(q)-n(arc.Center))*scale>.5
                        || Math.Abs((tp+tq)/2-centerT)*scale>.5 || Math.Abs(p.Z-a.Z)*scale>.5 || Math.Abs(q.Z-a.Z)*scale>.5)continue;
                    var outer=Math.Abs(tp-closedT)>Math.Abs(tq-closedT) ? tp : tq;
                    var leafWidth=arc.Radius+leafThickness/2;
                    if(Math.Abs(Math.Abs(outer-closedT)-leafWidth)*scale>.5)continue;
                    if(!hinges.Any(h=>Math.Abs(h.Outer-outer)*scale<.5 && Math.Abs(h.Closed-closedT)*scale<.5))
                        hinges.Add(new Hinge { Outer=outer,Closed=closedT,LeafWidth=leafWidth });
                }
            }
            Action<double,double> add=(p,q)=> {
                try { var placement=CadOpeningPlacement.Create(floor,wall.Handle,new PointModel(a.X,a.Y),new PointModel(b.X,b.Y),
                    new PointModel(a.X+p*dx,a.Y+p*dy),new PointModel(a.X+q*dx,a.Y+q*dy),width);
                    placement.Source="本洞口原生门扇开启弧、铰点与实际门扇厚度，按原生洞口宽度和唯一宿主墙核验";
                    if(!candidates.Any(c=>c.HostSourceHandle==placement.HostSourceHandle && Math.Abs(c.DistanceFromWallStart-placement.DistanceFromWallStart)<.5))candidates.Add(placement);
                } catch(System.IO.InvalidDataException) { }
            };
            foreach(var hinge in hinges)if(Math.Abs(hinge.LeafWidth*scale-width)<.5)add(hinge.Outer,hinge.Closed);
            for(var i=0;i<hinges.Count;i++)for(var j=i+1;j<hinges.Count;j++)
                if(Math.Abs(hinges[i].Closed-hinges[j].Closed)*scale<.5
                    && (hinges[i].Outer-hinges[i].Closed)*(hinges[j].Outer-hinges[j].Closed)<0
                    && Math.Abs((hinges[i].LeafWidth+hinges[j].LeafWidth)*scale-width)<.5
                    && Math.Abs(Math.Abs(hinges[i].Outer-hinges[j].Outer)*scale-width)<.5)add(hinges[i].Outer,hinges[j].Outer);
        }
    }
}
