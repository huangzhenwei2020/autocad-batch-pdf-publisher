using System;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Services
{
    // Transient native preview only. The caller writes one transaction after a successful drop.
    public sealed class CadDoorDirectionJig : DrawJig
    {
        private readonly Database _db;private readonly ComponentPlanSymbol _source;private readonly PointModel _base;
        private readonly double _units,_width,_height;private readonly string _code;private readonly double? _opening;
        private readonly CadLibraryDoorPlacement.PlanPreview _original;private Point3d? _last;
        public bool FlipAlong {get;private set;}public bool FlipAcross {get;private set;}
        public CadLibraryDoorPlacement.PlanPreview Preview {get;private set;}
        public CadDoorDirectionJig(Database db,ComponentPlanSymbol source,PointModel localBase,double units,double width,double height,string code,double? opening,CadLibraryDoorPlacement.PlanPreview original)
        {_db=db;_source=source;_base=localBase;_units=units;_width=width;_height=height;_code=code;_opening=opening;_original=Preview=original;}
        public void SetDirection(Point3d point)
        {
            var along=new Vector3d(Math.Cos(_original.Angle),Math.Sin(_original.Angle),0);var normal=new Vector3d(-along.Y,along.X,0);var center=_original.Start+along*(_width/_units/2);var handle=ComponentPlanSymbols.DirectionHandle(_original.Plan);var reference=handle==null?normal: new Point3d(handle.X,handle.Y,center.Z)-center;var target=point-center;
            var a=target.DotProduct(along);var b=target.DotProduct(normal);if(Math.Abs(a)>2/_units)FlipAlong=(a<0)!=(reference.DotProduct(along)<0);if(Math.Abs(b)>2/_units)FlipAcross=(b<0)!=(reference.DotProduct(normal)<0);
            using(var tx=_db.TransactionManager.StartTransaction()){var walls=_original.Cuts.Select(c=>_db.GetObjectId(false,new Handle(long.Parse(c.Wall,System.Globalization.NumberStyles.HexNumber)),0));Preview=CadLibraryDoorPlacement.Preview(_db,tx,_source,_base,_original.Start,_original.Angle,_units,_width,_height,FlipAlong,FlipAcross,_code,_opening,walls);}
        }
        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options=new JigPromptPointOptions("\n移动鼠标确定合页侧和开启侧，单击确认；Esc 取消：") {BasePoint=_original.Start,UseBasePoint=true,Cursor=CursorType.RubberBand,UserInputControls=UserInputControls.Accept3dCoordinates};
            var result=prompts.AcquirePoint(options);if(result.Status!=PromptStatus.OK)return SamplerStatus.Cancel;if(_last.HasValue&&_last.Value.DistanceTo(result.Value)<1e-8)return SamplerStatus.NoChange;
            _last=result.Value;SetDirection(result.Value);return SamplerStatus.OK;
        }
        protected override bool WorldDraw(WorldDraw draw)
        {
            var plan=Preview.Plan;for(var i=0;i<plan.Primitives.Count;i++){var primitive=plan.Primitives[i];var role=ComponentPlanSymbols.PrimitiveRole(plan,i);draw.SubEntityTraits.Color=ComponentPlanSymbols.RoleColor(role);
                if(primitive.Kind=="Line")draw.Geometry.WorldLine(new Point3d(primitive.X1,primitive.Y1,Preview.Start.Z),new Point3d(primitive.X2,primitive.Y2,Preview.Start.Z));
                else {var sweep=primitive.Kind=="Circle"?360:primitive.SweepDegrees;var count=Math.Max(8,(int)Math.Ceiling(Math.Abs(sweep)/3));Point3d? previous=null;for(var step=0;step<=count;step++){var angle=(primitive.StartDegrees+sweep*step/count)*Math.PI/180;var next=new Point3d(primitive.X1+primitive.Radius*Math.Cos(angle),primitive.Y1+primitive.Radius*Math.Sin(angle),Preview.Start.Z);if(previous.HasValue&&(role!="OpeningSymbol"||step%4<2))draw.Geometry.WorldLine(previous.Value,next);previous=next;}}
            }return true;
        }
    }
}
