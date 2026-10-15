using System;
using System.Collections.Generic;
using System.Linq;
using LibTessDotNet.Double;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>Find closed wall footprints, trim slabs to perimeter inner faces and cut named room voids.</summary>
    public static class CadFloorSlabGeneration
    {
        public static bool IsVoidName(string name) => !string.IsNullOrWhiteSpace(name)
            && new[] { "楼梯", "电梯", "井" }.Any(name.Contains);

        public static List<SlabModel> Build(IList<WallModel> walls, IEnumerable<CadRoomLabel> labels,
            double thickness, string floor, IList<string> messages)
        {
            var result = new List<SlabModel>();
            if (walls.Count == 0) return result;
            if(walls.Any(w=>Math.Abs(w.Bulge)>1e-10))return BuildCurved(walls,labels,thickness,floor,messages);
            // Work near the origin, including the signed native wall-axis offset.
            var origin = new PointModel(walls[0].X1, walls[0].Y1);
            var tess = new Tess();
            foreach (var wall in walls)
            {
                var a = WallReferenceGeometry.BodyPoint(wall, wall.X1, wall.Y1);
                var b = WallReferenceGeometry.BodyPoint(wall, wall.X2, wall.Y2);
                var length = Distance(a, b); if (length < 1) continue;
                var nx = -(b.Y-a.Y)/length*wall.Thickness/2;
                var ny = (b.X-a.X)/length*wall.Thickness/2;
                Add(tess, new[] { new PointModel(a.X-nx,a.Y-ny),new PointModel(b.X-nx,b.Y-ny),
                    new PointModel(b.X+nx,b.Y+ny),new PointModel(a.X+nx,a.Y+ny) }, origin);
            }
            // Native axes may lie on a wall face. Join offset end caps at an actual shared
            // axis endpoint; never bridge an arbitrary opening or a gap in the drawing.
            var ends=walls.SelectMany(w=>new[] { new End(w,w.X1,w.Y1),new End(w,w.X2,w.Y2) }).OrderBy(e=>e.Point.X).ToList();
            for(var i=0;i<ends.Count;i++)for(var j=i+1;j<ends.Count && ends[j].Point.X-ends[i].Point.X<=.01;j++)
                if(ends[i].Wall!=ends[j].Wall && Distance(ends[i].Point,ends[j].Point)<=.01)
                    Join(tess,ends[i],ends[j],origin);
            tess.Tessellate(WindingRule.NonZero, ElementType.BoundaryContours, normal: new Vec3 { Z=1 });
            var rings=Contours(tess,origin);
            var outer=rings.Where(r=>!rings.Any(other=>other!=r && Inside(r[0],other))).ToList();
            var rooms=rings.Where(r=>rings.Count(other=>other!=r && Inside(r[0],other))%2==1).ToList();
            var voids=new Dictionary<List<PointModel>,CadRoomLabel>();
            foreach(var label in labels ?? Enumerable.Empty<CadRoomLabel>()) {
                if(label?.Position==null || !IsVoidName(label.Name))continue;
                var room=rooms.Where(r=>Inside(label.Position,r)).OrderBy(r=>Math.Abs(Area(r))).FirstOrDefault();
                if(room==null) {
                    messages.Add(outer.Any(r=>Inside(label.Position,r))
                        ? label.Name+"：未找到闭合房间边界，未自动开洞。"
                        : label.Name+"：该处在楼板外轮廓的空缺区域，楼板已留空。");continue;
                }
                if(label.AreaSquareMetres.HasValue && label.AreaSquareMetres.Value>0) {
                    var ratio=Math.Abs(Area(room))/1e6/label.AreaSquareMetres.Value;
                    if(ratio>2 || ratio<.5) { messages.Add(label.Name+"：围合区域与天正房间面积不符，未自动开洞。");continue; }
                }
                if(!voids.ContainsKey(room))voids.Add(room,label);
            }
            foreach(var boundary in outer.OrderBy(r=>r.Min(p=>p.X)).ThenBy(r=>r.Min(p=>p.Y))) {
                if(!rooms.Any(room=>Inside(room[0],boundary)))continue;
                var cut=new Tess();Add(cut,boundary,origin);
                var floorVoids=voids.Keys.Where(r=>Inside(r[0],boundary)).Select(r=>ExpandVoid(r,walls)).ToList();
                var perimeterEnds=new Dictionary<WallModel,int>();
                foreach(var wall in walls) {
                    var a=WallReferenceGeometry.BodyPoint(wall,wall.X1,wall.Y1);
                    var b=WallReferenceGeometry.BodyPoint(wall,wall.X2,wall.Y2);
                    var length=Distance(a,b);var dx=(b.X-a.X)/length;var dy=(b.Y-a.Y)/length;
                    var half=wall.Thickness/2;
                    // Split where the exterior silhouette changes, so a partly exposed wall
                    // removes only its perimeter section, rather than an interior partition.
                    var stations=new List<double> { 0,length };
                    stations.AddRange(boundary.Concat(floorVoids.SelectMany(r=>r))
                        .Select(v=>(v.X-a.X)*dx+(v.Y-a.Y)*dy).Where(t=>t>.01 && t<length-.01));
                    stations=stations.OrderBy(t=>t).ToList();
                    for(var k=0;k<stations.Count-1;k++) {
                        var lo=stations[k];var hi=stations[k+1];if(hi-lo<.01)continue;
                        var mid=(lo+hi)/2;var x=a.X+dx*mid;var y=a.Y+dy*mid;
                        var left=new PointModel(x-dy*(half+.1),y+dx*(half+.1));
                        var right=new PointModel(x+dy*(half+.1),y-dx*(half+.1));
                        if(Inside(left,boundary) && Inside(right,boundary))continue;
                        var q=new PointModel(a.X+dx*lo,a.Y+dy*lo);var r=new PointModel(a.X+dx*hi,a.Y+dy*hi);
                        Add(cut,new[] { new PointModel(q.X+dy*half,q.Y-dx*half),new PointModel(r.X+dy*half,r.Y-dx*half),
                            new PointModel(r.X-dy*half,r.Y+dx*half),new PointModel(q.X-dy*half,q.Y+dx*half) },origin,true);
                        int mask;perimeterEnds.TryGetValue(wall,out mask);
                        if(lo<.01)mask|=1;if(hi>length-.01)mask|=2;perimeterEnds[wall]=mask;
                    }
                }
                bool Exposed(End e) {
                    int mask;if(!perimeterEnds.TryGetValue(e.Wall,out mask))return false;
                    return (mask & (Distance(e.Point,new PointModel(e.Wall.X1,e.Wall.Y1))<.01 ? 1 : 2))!=0;
                }
                for(var i=0;i<ends.Count;i++)for(var j=i+1;j<ends.Count && ends[j].Point.X-ends[i].Point.X<=.01;j++)
                    if(ends[i].Wall!=ends[j].Wall && Distance(ends[i].Point,ends[j].Point)<=.01 && Exposed(ends[i]) && Exposed(ends[j]))
                        Join(cut,ends[i],ends[j],origin,true);
                foreach(var room in floorVoids)Add(cut,room,origin,true);
                // Positive winding implements filled boundary minus overlapping wall strips/voids.
                cut.Tessellate(WindingRule.Positive,ElementType.BoundaryContours,normal:new Vec3 { Z=1 });
                var clipped=Contours(cut,origin);
                var shells=clipped.Where(r=>!clipped.Any(other=>other!=r && Inside(r[0],other))).ToList();
                foreach(var shell in shells.OrderBy(r=>r.Min(p=>p.X)).ThenBy(r=>r.Min(p=>p.Y))) {
                    if(IsMicroSliver(shell,100,.5)) {messages.Add(floor+"：已清理裁剪产生的微小边角（面积 ≤100 mm²、宽度 ≤0.5 mm），未作为独立楼板。");continue;}
                    var slab=new SlabModel { StoreyId=floor,Thickness=thickness,TopOffset=0,FollowsStoreyTop=true,Outline=shell };
                    foreach(var hole in clipped.Except(shells).Where(r=>Inside(r[0],shell))) {
                        var label=voids.Values.FirstOrDefault(l=>Inside(l.Position,hole));
                        if(label==null&&IsMicroSliver(hole)) {
                            messages.Add(floor+"：已清理裁剪产生的微小细缝（面积 ≤10 mm²、宽度 ≤0.05 mm），未作为楼板洞口。");continue;
                        }
                        slab.Openings.Add(new SlabOpeningModel { Id="room-"+slab.Openings.Count,Name=label?.Name ?? "板洞",Outline=hole });
                    }
                    var error=SlabGeometry.Validate(slab);
                    if(error!=null) {
                        messages.Add(floor+" 自动楼板轮廓无效，已跳过本块楼板，其他构件继续生成："+error);continue;
                    }
                    result.Add(slab);
                }
            }
            if(result.Count==0 && voids.Count==0)messages.Add("墙体未形成闭合范围，未自动生成楼板。");
            return result;
        }
        private static bool IsMicroSliver(IList<PointModel> ring,double maximumArea=10,double maximumWidth=.05)
        {
            if(Math.Abs(Area(ring))>maximumArea)return false;
            var longest=Enumerable.Range(0,ring.Count).OrderByDescending(i=>Distance(ring[i],ring[(i+1)%ring.Count])).First();
            var a=ring[longest];var b=ring[(longest+1)%ring.Count];var length=Distance(a,b);if(length<=0)return false;
            var offsets=ring.Select(p=>((p.X-a.X)*(b.Y-a.Y)-(p.Y-a.Y)*(b.X-a.X))/length).ToArray();
            return offsets.Max()-offsets.Min()<=maximumWidth;
        }

        private static List<SlabModel> BuildCurved(IList<WallModel> walls,IEnumerable<CadRoomLabel> labels,
            double thickness,string floor,IList<string> messages)
        {
            var segments=new List<WallModel>();var borders=new List<CurveGeometry>();
            foreach(var wall in walls)
            {
                var curve=CurveGeometry.ForWall(wall);
                if(!curve.IsArc) { segments.Add(wall);continue; }
                var offset=WallReferenceGeometry.BodyOffset(wall);
                var points=curve.Sample(offset,.1);
                for(var i=0;i<points.Count-1;i++)segments.Add(new WallModel {
                    Id=wall.Id+"-arc-"+i,StoreyId=wall.StoreyId,Thickness=wall.Thickness,AxisOffset=0,
                    X1=points[i].X,Y1=points[i].Y,X2=points[i+1].X,Y2=points[i+1].Y });
                foreach(var side in new[]{-1,1})
                {
                    var borderOffset=offset+side*wall.Thickness/2;
                    borders.Add(new CurveGeometry(curve.At(0,borderOffset),curve.At(1,borderOffset),wall.Bulge));
                }
            }
            if(segments.Count>5000)
            {
                messages.Add("圆弧围合范围过于复杂，已生成墙体；楼板请在模型中绘制。");
                return new List<SlabModel>();
            }
            // Keep the established footprint/void solver; only its working geometry is tessellated.
            var slabs=Build(segments,labels,thickness,floor,messages);
            foreach(var slab in slabs)
            {
                var outline=slab.Outline;var holes=slab.Openings.Select(o=>o.Outline).ToList();
                slab.Outline=RestoreArcEdges(outline,borders);
                for(var i=0;i<holes.Count;i++)slab.Openings[i].Outline=RestoreArcEdges(holes[i],borders);
                if(SlabGeometry.Validate(slab)!=null)
                {
                    slab.Outline=outline;
                    for(var i=0;i<holes.Count;i++)slab.Openings[i].Outline=holes[i];
                    messages.Add("圆弧楼板交接处保留精细采样轮廓，墙体仍为真实圆弧。");
                }
            }
            return slabs;
        }

        private static List<PointModel> RestoreArcEdges(IList<PointModel> ring,IList<CurveGeometry> borders)
        {
            var matches=Enumerable.Repeat(-1,ring.Count).ToArray();var directions=new int[ring.Count];
            for(var i=0;i<ring.Count;i++)
            {
                var a=ring[i];var b=ring[(i+1)%ring.Count];
                for(var j=0;j<borders.Count;j++)
                {
                    var arc=borders[j];var ta=arc.Parameter(a.X,a.Y);var tb=arc.Parameter(b.X,b.Y);
                    if(Math.Abs(tb-ta)<1e-8 || Math.Abs(tb-ta)>.2 || arc.DistanceTo(a.X,a.Y)>.25
                        ||arc.DistanceTo(b.X,b.Y)>.25 ||arc.DistanceTo((a.X+b.X)/2,(a.Y+b.Y)/2)>.25)continue;
                    matches[i]=j;directions[i]=Math.Sign(tb-ta);break;
                }
            }
            var start=Enumerable.Range(0,ring.Count).FirstOrDefault(i=>matches[i]!=matches[(i+ring.Count-1)%ring.Count]
                || directions[i]!=directions[(i+ring.Count-1)%ring.Count]);
            var result=new List<PointModel>();
            for(var count=0;count<ring.Count;)
            {
                var index=(start+count)%ring.Count;var match=matches[index];var run=1;
                while(match>=0 && count+run<ring.Count && matches[(index+run)%ring.Count]==match
                    && directions[(index+run)%ring.Count]==directions[index])run++;
                var point=new PointModel(ring[index].X,ring[index].Y);
                if(match>=0 && run>=2)
                {
                    var arc=borders[match];var end=ring[(index+run)%ring.Count];
                    var ta=arc.Parameter(point.X,point.Y);var tb=arc.Parameter(end.X,end.Y);
                    // Boundary stations already lie within 0.25 mm of the native circle.
                    point.Bulge=Math.Tan(arc.Sweep*(tb-ta)/4);
                }
                else run=1;
                result.Add(point);count+=run;
            }
            return result;
        }
        private static List<List<PointModel>> Contours(Tess tess,PointModel origin)
        {
            // Rebuild the planar arrangement after snapping, rather than rounding a
            // completed walk. This resolves coincident edges and intersections together
            // and retains clockwise hole contours as negative winding.
            var normalized=new Tess();
            for(var i=0;i<tess.ElementCount;i++)
                normalized.AddContour(Enumerable.Range(tess.Elements[i*2],tess.Elements[i*2+1]).Select(v=>new ContourVertex {
                    Position=new Vec3 { X=Math.Round(tess.Vertices[v].Position.X,2),Y=Math.Round(tess.Vertices[v].Position.Y,2) }
                }).ToArray());
            normalized.Tessellate(WindingRule.NonZero,ElementType.BoundaryContours,normal:new Vec3 { Z=1 });
            tess=normalized;
            var rings=new List<List<PointModel>>();
            for(var i=0;i<tess.ElementCount;i++) {
                var ring=Clean(Enumerable.Range(tess.Elements[i*2],tess.Elements[i*2+1]).Select(v=>new PointModel(
                    tess.Vertices[v].Position.X+origin.X,tess.Vertices[v].Position.Y+origin.Y)).ToList());
                foreach(var simple in SplitTouches(ring))if(simple.Count>=3 && Math.Abs(Area(simple))>1)rings.Add(simple);
            }
            return rings;
        }

        // A named room's net outline is the inside face of its surrounding walls.
        // Offset each edge through that wall's actual thickness to its opposite face.
        // Intersect neighbouring offset edges so the corners are cleared as well.
        private static List<PointModel> ExpandVoid(IList<PointModel> room,IList<WallModel> walls)
        {
            var starts=new List<PointModel>();var directions=new List<PointModel>();
            var sign=Area(room)>0 ? 1d : -1d;
            double maximum=0;
            for(var i=0;i<room.Count;i++) {
                var a=room[i];var b=room[(i+1)%room.Count];var length=Distance(a,b);
                var dx=(b.X-a.X)/length;var dy=(b.Y-a.Y)/length;
                var mid=new PointModel((a.X+b.X)/2,(a.Y+b.Y)/2);
                var thickness=walls.Where(w=> {
                    var p=WallReferenceGeometry.BodyPoint(w,w.X1,w.Y1);var q=WallReferenceGeometry.BodyPoint(w,w.X2,w.Y2);
                    var wl=Distance(p,q);if(wl<1)return false;
                    var wx=(q.X-p.X)/wl;var wy=(q.Y-p.Y)/wl;
                    var along=(mid.X-p.X)*wx+(mid.Y-p.Y)*wy;
                    var across=Math.Abs((mid.X-p.X)*wy-(mid.Y-p.Y)*wx);
                    return Math.Abs(dx*wy-dy*wx)<1e-5 && along>=-.1 && along<=wl+.1
                        && Math.Abs(across-w.Thickness/2)<.1;
                }).Select(w=>w.Thickness).DefaultIfEmpty(0).Max();
                maximum=Math.Max(maximum,thickness);
                starts.Add(new PointModel(a.X+dy*thickness*sign,a.Y-dx*thickness*sign));
                directions.Add(new PointModel(dx,dy));
            }
            var result=new List<PointModel>();
            for(var i=0;i<room.Count;i++) {
                var previous=(i+room.Count-1)%room.Count;var p=starts[previous];var q=starts[i];
                var a=directions[previous];var b=directions[i];var cross=a.X*b.Y-a.Y*b.X;
                if(Math.Abs(cross)<1e-8) { result.Add(q);continue; }
                var t=((q.X-p.X)*b.Y-(q.Y-p.Y)*b.X)/cross;
                var intersection=new PointModel(p.X+a.X*t,p.Y+a.Y*t);
                if(Distance(intersection,room[i])<=Math.Max(1,maximum*4))result.Add(intersection);
                else { var end=room[i];result.Add(new PointModel(end.X+(p.X-room[previous].X),end.Y+(p.Y-room[previous].Y)));result.Add(q); }
            }
            return Clean(result);
        }
        private static void Add(Tess tess,IEnumerable<PointModel> ring,PointModel origin,bool subtract=false)
        {
            var points=ring.ToList();if((Area(points)>0)==subtract)points.Reverse();
            tess.AddContour(points.Select(p=>new ContourVertex { Position=new Vec3 { X=Math.Round(p.X-origin.X,3),Y=Math.Round(p.Y-origin.Y,3) } }).ToArray());
        }
        private sealed class End
        {
            public WallModel Wall;public PointModel Point;public double Dx,Dy;
            public End(WallModel wall,double x,double y) {
                Wall=wall;Point=new PointModel(x,y);var length=Distance(new PointModel(wall.X1,wall.Y1),new PointModel(wall.X2,wall.Y2));
                Dx=(wall.X2-wall.X1)/length;Dy=(wall.Y2-wall.Y1)/length;
            }
            public PointModel Edge(int side) {
                var offset=WallReferenceGeometry.BodyOffset(Wall)+side*Wall.Thickness/2;
                return new PointModel(Point.X-Dy*offset,Point.Y+Dx*offset);
            }
        }
        private static void Join(Tess tess,End a,End b,PointModel origin,bool subtract=false)
        {
            var cross=a.Dx*b.Dy-a.Dy*b.Dx;if(Math.Abs(cross)<1e-6)return;
            var points=new List<PointModel> { a.Edge(-1),a.Edge(1),b.Edge(-1),b.Edge(1) };
            var limit=4*Math.Max(a.Wall.Thickness+Math.Abs(WallReferenceGeometry.BodyOffset(a.Wall)),
                b.Wall.Thickness+Math.Abs(WallReferenceGeometry.BodyOffset(b.Wall)));
            foreach(var sa in new[] { -1,1 })foreach(var sb in new[] { -1,1 }) {
                var p=a.Edge(sa);var q=b.Edge(sb);
                var t=((q.X-p.X)*b.Dy-(q.Y-p.Y)*b.Dx)/cross;
                var intersection=new PointModel(p.X+a.Dx*t,p.Y+a.Dy*t);
                if(Distance(intersection,a.Point)<=limit)points.Add(intersection);
            }
            points=points.OrderBy(p=>p.X).ThenBy(p=>p.Y).ToList();
            var hull=new List<PointModel>();
            foreach(var p in points) { while(hull.Count>=2 && Cross(hull[hull.Count-2],hull[hull.Count-1],p)<=1e-8)hull.RemoveAt(hull.Count-1);hull.Add(p); }
            var lower=hull.Count;
            for(var i=points.Count-2;i>=0;i--) { var p=points[i];while(hull.Count>lower && Cross(hull[hull.Count-2],hull[hull.Count-1],p)<=1e-8)hull.RemoveAt(hull.Count-1);hull.Add(p); }
            if(hull.Count>1)hull.RemoveAt(hull.Count-1);
            if(hull.Count>=3 && Area(hull)>1e-6)Add(tess,hull,origin,subtract);
        }
        private static double Cross(PointModel a,PointModel b,PointModel c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        private static List<PointModel> Clean(List<PointModel> points)
        {
            for(var i=points.Count-1;i>=0 && points.Count>2;i--)
                if(Distance(points[i],points[(i+1)%points.Count])<1e-6)points.RemoveAt(i);
            bool changed;
            do {
                changed=false;
                for(var i=0;i<points.Count && points.Count>3;i++) {
                    var a=points[(i+points.Count-1)%points.Count];var b=points[i];var c=points[(i+1)%points.Count];
                    var cross=(b.X-a.X)*(c.Y-b.Y)-(b.Y-a.Y)*(c.X-b.X);
                    if(Math.Abs(cross)<1e-6*(Distance(a,b)+Distance(b,c))
                        && (b.X-a.X)*(c.X-b.X)+(b.Y-a.Y)*(c.Y-b.Y)>=0) {
                        points.RemoveAt(i);changed=true;break;
                    }
                }
            } while(changed);
            return points;
        }
        // Tessellation contours may revisit a point where two wall bodies only touch.
        // Split those walks into simple rings so they cannot create a self-touching slab.
        private static IEnumerable<List<PointModel>> SplitTouches(List<PointModel> ring)
        {
            for(var i=0;i<ring.Count;i++)for(var j=i+2;j<ring.Count;j++) {
                if(i==0 && j==ring.Count-1)continue;
                if(Distance(ring[i],ring[j])>1e-5)continue;
                foreach(var part in SplitTouches(Clean(ring.Skip(i).Take(j-i).ToList())))yield return part;
                foreach(var part in SplitTouches(Clean(ring.Skip(j).Concat(ring.Take(i)).ToList())))yield return part;
                yield break;
            }
            yield return ring;
        }
        public static bool Inside(PointModel p,IList<PointModel> ring)
        {
            var inside=false;
            for(int i=0,j=ring.Count-1;i<ring.Count;j=i++) {
                var a=ring[j];var b=ring[i];
                if((a.Y>p.Y)!=(b.Y>p.Y) && p.X<(b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
            }
            return inside;
        }
        public static double Area(IList<PointModel> ring)
        {
            var origin=ring[0];double area=0;
            for(var i=0;i<ring.Count;i++) { var a=ring[i];var b=ring[(i+1)%ring.Count];
                area+=(a.X-origin.X)*(b.Y-origin.Y)-(b.X-origin.X)*(a.Y-origin.Y); }
            return area/2;
        }
        private static double Distance(PointModel a,PointModel b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    }
}
