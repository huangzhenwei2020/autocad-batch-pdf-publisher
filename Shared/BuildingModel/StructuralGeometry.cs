using System;
using System.Collections.Generic;
using System.Linq;

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
}
