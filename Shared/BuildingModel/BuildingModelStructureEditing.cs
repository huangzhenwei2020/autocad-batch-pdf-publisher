using System;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed partial class BuildingModelEditSession
    {
        public bool TryTransformStructure(string id,double dx,double dy,bool copy,out string affectedId,out string error)
        {
            affectedId=null;error=null;
            if(!Finite(dx)||!Finite(dy)){error="位移必须是有效毫米数。";return false;}
            var model=Clone(Model);var c=model.Columns.FirstOrDefault(x=>x.Id==id);
            if(c!=null){c.X+=dx;c.Y+=dy;if(copy){c.Id=null;c.Code=null;}return TryUpsertColumn(c,out affectedId,out error);}
            var b=model.Beams.FirstOrDefault(x=>x.Id==id);
            if(b!=null){b.X1+=dx;b.Y1+=dy;b.X2+=dx;b.Y2+=dy;if(copy){b.Id=null;b.Code=null;}return TryUpsertBeam(b,out affectedId,out error);}
            error="未找到梁柱。";return false;
        }
        public bool TryUpsertColumn(ColumnModel column,out string id,out string error)
        {
            id=null;error=null;
            if(column==null || Model.FindStorey(column.StoreyId)==null || !Finite(column.X)||!Finite(column.Y)
                || !Finite(column.Width)||!Finite(column.Depth)||column.Width<=.5||column.Depth<=.5
                || !Finite(column.Height)||column.Height<0||!Finite(column.RotationDegrees)
                || !Finite(column.BaseOffset)||!Finite(column.TopOffset)||Model.HeightOf(column)<=.5)
            {error="柱的位置、截面或高度无效。";return false;}
            if(!string.IsNullOrWhiteSpace(Model.FindStorey(column.StoreyId).TemplateStoreyId))
            {error="请在标准层共用平面所属楼层绘制柱。";return false;}
            var candidate=Clone(Model);id=string.IsNullOrWhiteSpace(column.Id)?"column-"+Guid.NewGuid().ToString("N"):column.Id;
            if(OtherStructureId(candidate,id,"column")){error="构件身份重复。";return false;}
            var copy=new ColumnModel {Id=id,Code=column.Code,StoreyId=column.StoreyId,X=column.X,Y=column.Y,
                Width=column.Width,Depth=column.Depth,Height=column.Height,RotationDegrees=column.RotationDegrees,
                BaseOffset=column.BaseOffset,TopOffset=column.TopOffset};
            if(string.IsNullOrWhiteSpace(copy.Code))copy.Code="KZ-"+(candidate.Columns.Count+1);
            candidate.Columns.RemoveAll(c=>c.Id==copy.Id);candidate.Columns.Add(copy);Commit(candidate);return true;
        }
        public bool TryUpsertBeam(BeamModel beam,out string id,out string error)
        {
            id=null;error=null;
            if(beam==null||Model.FindStorey(beam.StoreyId)==null||!Finite(beam.X1)||!Finite(beam.Y1)
                ||!Finite(beam.X2)||!Finite(beam.Y2)||!Finite(beam.Width)||!Finite(beam.Depth)||!Finite(beam.TopOffset)
                ||beam.Width<=.5||beam.Depth<=.5||StructuralGeometry.BeamOutline(beam).Count!=4)
            {error="梁的端点、截面或顶面偏移无效。";return false;}
            if(!string.IsNullOrWhiteSpace(Model.FindStorey(beam.StoreyId).TemplateStoreyId))
            {error="请在标准层共用平面所属楼层绘制梁。";return false;}
            var candidate=Clone(Model);id=string.IsNullOrWhiteSpace(beam.Id)?"beam-"+Guid.NewGuid().ToString("N"):beam.Id;
            if(OtherStructureId(candidate,id,"beam")){error="构件身份重复。";return false;}
            var copy=new BeamModel {Id=id,Code=beam.Code,StoreyId=beam.StoreyId,X1=beam.X1,Y1=beam.Y1,X2=beam.X2,Y2=beam.Y2,
                Width=beam.Width,Depth=beam.Depth,TopOffset=beam.TopOffset};
            if(string.IsNullOrWhiteSpace(copy.Code))copy.Code="L-"+(candidate.Beams.Count+1);
            candidate.Beams.RemoveAll(b=>b.Id==copy.Id);candidate.Beams.Add(copy);Commit(candidate);return true;
        }
        private static bool OtherStructureId(BuildingModelDocument m,string id,string kind)=>
            m.Walls.Any(x=>x.Id==id)||m.Openings.Any(x=>x.Id==id)||m.Slabs.Any(x=>x.Id==id)||m.Stairs.Any(x=>x.Id==id)||m.Roofs.Any(x=>x.Id==id)
            ||(kind!="column"&&m.Columns.Any(x=>x.Id==id))||(kind!="beam"&&m.Beams.Any(x=>x.Id==id));
    }
}
