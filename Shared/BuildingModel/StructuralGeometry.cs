using System;
using System.Collections.Generic;
using System.Linq;
using LibTessDotNet.Double;

namespace BatchPdfPublisher.BuildingModel
{
    public static class StructuralGeometry
    {
        public static List<PointModel> ColumnOutline(ColumnModel c)
        {
            var angle=c.RotationDegrees*Math.PI/180d;var cos=Math.Cos(angle);var sin=Math.Sin(angle);
            return new[] {new PointModel(-c.Width/2,-c.Depth/2),new PointModel(c.Width/2,-c.Depth/2),
                new PointModel(c.Width/2,c.Depth/2),new PointModel(-c.Width/2,c.Depth/2)}
                .Select(p=>new PointModel(c.X+p.X*cos-p.Y*sin,c.Y+p.X*sin+p.Y*cos)).ToList();
        }
        public static List<PointModel> BeamOutline(BeamModel b)
        {
            var dx=b.X2-b.X1;var dy=b.Y2-b.Y1;var length=Math.Sqrt(dx*dx+dy*dy);
            if(length<.5)return new List<PointModel>();
            var nx=-dy/length*b.Width/2;var ny=dx/length*b.Width/2;
            return new List<PointModel> {new PointModel(b.X1-nx,b.Y1-ny),new PointModel(b.X2-nx,b.Y2-ny),
                new PointModel(b.X2+nx,b.Y2+ny),new PointModel(b.X1+nx,b.Y1+ny)};
        }
    }

    public sealed class ColumnDrawingBand
    {
        public double Bottom,Top;
        public bool HasOverlap;
        public List<List<PointModel>> Contours=new List<List<PointModel>>();
        public List<List<PointModel>> Triangles=new List<List<PointModel>>();
        public List<List<PointModel>> WallMasks=new List<List<PointModel>>();
    }

    /// <summary>Drawing-only column minus actual wall solids; model instances stay intact.</summary>
    public static class ColumnDrawingGeometry
    {
        private const double Tolerance=.001;
        public static List<ColumnDrawingBand> Build(BuildingModelDocument model,ColumnModel column)
        {
            var bottom=model.BaseElevationOf(column);var top=bottom+model.HeightOf(column);
            var outline=StructuralGeometry.ColumnOutline(column);
            var walls=model.Walls.Where(w=>string.Equals(w.StoreyId,column.StoreyId,StringComparison.OrdinalIgnoreCase)
                && model.BaseElevationOf(w)<top-Tolerance && model.BaseElevationOf(w)+model.HeightOf(w)>bottom+Tolerance
                && BoundsOverlap(outline,CurveGeometry.WallOutline(w))).ToList();
            var heights=new List<double> {bottom,top};
            foreach(var wall in walls) {
                var z=model.BaseElevationOf(wall);heights.Add(z);heights.Add(z+model.HeightOf(wall));
                foreach(var opening in model.Openings.Where(o=>o.HostWallId==wall.Id)) {
                    heights.Add(z+opening.Sill);heights.Add(z+opening.Sill+opening.Height);
                }
            }
            heights=heights.Where(z=>z>=bottom && z<=top).Distinct().OrderBy(z=>z).ToList();
            var result=new List<ColumnDrawingBand>();
            for(var i=0;i<heights.Count-1;i++) {
                if(heights[i+1]-heights[i]<Tolerance)continue;
                var band=At(model,column,(heights[i]+heights[i+1])/2,walls);
                band.Bottom=heights[i];band.Top=heights[i+1];result.Add(band);
            }
            return result;
        }
        public static ColumnDrawingBand At(BuildingModelDocument model,ColumnModel column,double elevation,IEnumerable<WallModel> candidates=null)
        {
            var outline=StructuralGeometry.ColumnOutline(column);
            var masks=new List<List<PointModel>>();
            foreach(var wall in candidates ?? model.Walls.Where(w=>string.Equals(w.StoreyId,column.StoreyId,StringComparison.OrdinalIgnoreCase))) {
                var bottom=model.BaseElevationOf(wall);
                if(elevation<bottom-Tolerance || elevation>bottom+model.HeightOf(wall)+Tolerance)continue;
                if(Math.Abs(wall.Bulge)>1e-10) {
                    var mask=CurveGeometry.WallOutline(wall);if(BoundsOverlap(outline,mask))masks.Add(mask);continue;
                }
                var curve=CurveGeometry.ForWall(wall);if(curve.Length<Tolerance)continue;
                var holes=model.Openings.Where(o=>o.HostWallId==wall.Id && elevation>bottom+o.Sill+Tolerance
                    && elevation<bottom+o.Sill+o.Height-Tolerance).OrderBy(o=>o.Offset-o.Width/2).ToList();
                var cursor=0d;
                foreach(var hole in holes) {
                    Add(cursor,Math.Max(0,Math.Min(curve.Length,hole.Offset-hole.Width/2)));
                    cursor=Math.Max(cursor,Math.Min(curve.Length,hole.Offset+hole.Width/2));
                }
                Add(cursor,curve.Length);
                void Add(double first,double last) {
                    if(last-first<Tolerance)return;
                    var offset=WallReferenceGeometry.BodyOffset(wall);var half=wall.Thickness/2;
                    var mask=new List<PointModel> {curve.At(first/curve.Length,offset+half),curve.At(last/curve.Length,offset+half),
                        curve.At(last/curve.Length,offset-half),curve.At(first/curve.Length,offset-half)};
                    if(BoundsOverlap(outline,mask))masks.Add(mask);
                }
            }
            var result=new ColumnDrawingBand {WallMasks=masks};
            if(masks.Count==0) {
                result.Contours.Add(outline);
                result.Triangles.Add(new List<PointModel> {outline[0],outline[1],outline[2]});
                result.Triangles.Add(new List<PointModel> {outline[0],outline[2],outline[3]});return result;
            }
            var origin=outline[0];
            Tess Tessellate(ElementType mode) {
                var tess=new Tess();
                void Add(List<PointModel> ring,bool positive) {
                    var vertices=ring.Select(p=>new ContourVertex {Position=new Vec3 {X=p.X-origin.X,Y=p.Y-origin.Y,Z=0}}).ToArray();
                    var area=Enumerable.Range(0,vertices.Length).Sum(i=>vertices[i].Position.X*vertices[(i+1)%vertices.Length].Position.Y
                        -vertices[(i+1)%vertices.Length].Position.X*vertices[i].Position.Y);
                    if((area>0)!=positive)Array.Reverse(vertices);
                    tess.AddContour(vertices);
                }
                Add(outline,true);
                foreach(var mask in masks)Add(mask,false);
                // Positive winding is a union of subtractors, not an even/odd toggle at wall crossings.
                tess.Tessellate(WindingRule.Positive,mode,3,normal:new Vec3 {Z=1});return tess;
            }
            var boundaries=Tessellate(ElementType.BoundaryContours);
            for(var i=0;i<boundaries.ElementCount;i++) {
                var start=boundaries.Elements[i*2];var count=boundaries.Elements[i*2+1];var ring=new List<PointModel>();
                for(var j=0;j<count;j++){var p=boundaries.Vertices[start+j].Position;ring.Add(new PointModel(p.X+origin.X,p.Y+origin.Y));}
                if(ring.Count>=3)result.Contours.Add(ring);
            }
            var triangles=Tessellate(ElementType.Polygons);
            for(var i=0;i<triangles.ElementCount;i++) {
                var ring=new List<PointModel>();
                for(var j=0;j<3;j++){var index=triangles.Elements[i*3+j];if(index==Tess.Undef)break;
                    var p=triangles.Vertices[index].Position;ring.Add(new PointModel(p.X+origin.X,p.Y+origin.Y));}
                if(ring.Count==3)result.Triangles.Add(ring);
            }
            var visibleArea=result.Triangles.Sum(t=>Math.Abs((t[1].X-t[0].X)*(t[2].Y-t[0].Y)-(t[1].Y-t[0].Y)*(t[2].X-t[0].X))/2);
            result.HasOverlap=column.Width*column.Depth-visibleArea>.001;
            return result;
        }
        public static List<ViewLine> VisibleOutline(ColumnModel column,ColumnDrawingBand band)
        {
            var outline=StructuralGeometry.ColumnOutline(column);var result=new List<ViewLine>();
            for(var i=0;i<outline.Count;i++) {
                var a=outline[i];var b=outline[(i+1)%outline.Count];var dx=b.X-a.X;var dy=b.Y-a.Y;
                var stops=new List<double>{0,1};
                foreach(var mask in band.WallMasks)for(var j=0;j<mask.Count;j++) {
                    var p=mask[j];var q=mask[(j+1)%mask.Count];var ex=q.X-p.X;var ey=q.Y-p.Y;var cross=dx*ey-dy*ex;
                    if(Math.Abs(cross)<1e-10) {
                        var length=dx*dx+dy*dy;
                        if(length>Tolerance && Math.Abs((p.X-a.X)*dy-(p.Y-a.Y)*dx)<Tolerance*Math.Sqrt(length)) {
                            stops.Add(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/length);stops.Add(((q.X-a.X)*dx+(q.Y-a.Y)*dy)/length);
                        }
                    } else {
                        var t=((p.X-a.X)*ey-(p.Y-a.Y)*ex)/cross;var u=((p.X-a.X)*dy-(p.Y-a.Y)*dx)/cross;
                        if(u>=-Tolerance && u<=1+Tolerance)stops.Add(t);
                    }
                }
                stops=stops.Where(t=>t>=0 && t<=1).Distinct().OrderBy(t=>t).ToList();
                for(var j=0;j<stops.Count-1;j++) {
                    var t=(stops[j]+stops[j+1])/2;
                    if(stops[j+1]-stops[j]<1e-9 || Covered(new PointModel(a.X+dx*t,a.Y+dy*t),band.WallMasks))continue;
                    result.Add(new ViewLine {X1=a.X+dx*stops[j],Y1=a.Y+dy*stops[j],X2=a.X+dx*stops[j+1],Y2=a.Y+dy*stops[j+1]});
                }
            }
            return result;
        }
        public static bool Covered(PointModel point,IEnumerable<List<PointModel>> masks)
        {
            foreach(var mask in masks) {
                if(SlabGeometry.Contains(point,mask))return true;
                for(var i=0;i<mask.Count;i++) {
                    var a=mask[i];var b=mask[(i+1)%mask.Count];
                    if(new CurveGeometry(a,b,0).DistanceTo(point.X,point.Y)<Tolerance)return true;
                }
            }
            return false;
        }
        private static bool BoundsOverlap(List<PointModel> a,List<PointModel> b)=>a.Count>0 && b.Count>0
            && a.Max(p=>p.X)>=b.Min(p=>p.X)-Tolerance && b.Max(p=>p.X)>=a.Min(p=>p.X)-Tolerance
            && a.Max(p=>p.Y)>=b.Min(p=>p.Y)-Tolerance && b.Max(p=>p.Y)>=a.Min(p=>p.Y)-Tolerance;
    }
}
