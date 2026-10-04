using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed partial class BuildingModelEditSession
    {
        public bool TryCenterOpening(string openingId,out string error)
        {
            error=null;var opening=Model.Openings.FirstOrDefault(o=>Same(o.Id,openingId));
            var wall=opening==null?null:Model.Walls.FirstOrDefault(w=>Same(w.Id,opening.HostWallId));
            if(wall==null){error="未找到门窗的宿主墙。";return false;}
            var length=Math.Sqrt(Math.Pow(wall.X2-wall.X1,2)+Math.Pow(wall.Y2-wall.Y1,2));
            if(!Finite(length)||length<.001){error="宿主墙长度无效。";return false;}
            if(opening.Offset==length/2)return true;
            return TrySetOpeningPlacement(opening.Id,wall.Id,length/2,opening.PlanFlipAlong,opening.PlanFlipNormal,out error);
        }
        public bool TrySetOpeningPresentation(string openingId,string code,double angle,bool openIn3D,out string error)
        {
            error=null;code=(code??"").Trim();
            if(code.Length==0||code.Length>64||code.Any(char.IsControl)){error="门窗编号须为 1～64 个字符，不能包含换行。";return false;}
            if(!Finite(angle)||angle<0||angle>180){error="平面开启角度应为 0～180°。";return false;}
            var candidate=Clone(Model);var opening=candidate.Openings.FirstOrDefault(o=>Same(o.Id,openingId));
            if(opening==null){error="未找到门窗。";return false;}
            var type=OpeningConstruction.Resolve(candidate,opening);
            var swing=OpeningPlanGeometry.HasSwingDoor(opening,type);
            if(!swing&&(angle!=opening.PlanOpenAngle||openIn3D!=(opening.OpenIn3D??((type.OpenAngle??0)>0)))) {
                error="开启角度用于平开门扇，请先在立面中设置平开形式。";return false;
            }
            if(!string.Equals(code,opening.Code,StringComparison.Ordinal)) {
                if(candidate.Openings.Any(o=>o.Id!=opening.Id&&Same(o.Code,code)&&(Math.Abs(o.Width-opening.Width)>.5||Math.Abs(o.Height-opening.Height)>.5||o.Kind!=opening.Kind))) {
                    error="该编号已有不同尺寸或类别的门窗，请使用其他编号。";return false;
                }
                var target=candidate.OpeningTypes.FirstOrDefault(t=>Same(t.Code,code));
                if(target!=null) {
                    if(Math.Abs(target.Width-opening.Width)>.5||Math.Abs(target.Height-opening.Height)>.5||target.Kind!=opening.Kind){error="该编号已有不同尺寸或类别的门窗，请使用其他编号。";return false;}
                    error=OpeningConstruction.Validate(opening,target);if(error!=null)return false;
                } else {
                    target=OpeningConstruction.Copy(type);OpeningConstruction.ResizeType(target,opening.Width,opening.Height);
                    target.Code=code;target.Kind=opening.Kind;candidate.OpeningTypes.Add(target);
                }
                candidate.OpeningOverrides?.RemoveAll(o=>Same(o.OpeningId,openingId));
                opening.Code=code;opening.CodeManuallyEdited=true;
            }
            if(swing){opening.PlanOpenAngle=angle;opening.OpenIn3D=openIn3D;}
            if(BuildingModelJson.ToJson(candidate)==BuildingModelJson.ToJson(Model))return true;
            Commit(candidate);return true;
        }
        public bool TrySetOpeningLabel(string openingId,double along,double normal,out string error)
        {
            error=null;
            if(!Finite(along)||!Finite(normal)){error="编号位置必须是有效数值。";return false;}
            var candidate=Clone(Model);var opening=candidate.Openings.FirstOrDefault(o=>Same(o.Id,openingId));
            if(opening==null){error="未找到门窗。";return false;}
            if(opening.PlanLabelAlong==along&&opening.PlanLabelNormal==normal)return true;
            opening.PlanLabelAlong=along;opening.PlanLabelNormal=normal;Commit(candidate);return true;
        }
        public bool TrySetOpeningPlacement(string openingId,string wallId,double offset,bool flipAlong,bool flipNormal,out string error)
        {
            error=null;
            var candidate=Clone(Model);var opening=candidate.Openings.FirstOrDefault(o=>Same(o.Id,openingId));
            var oldWall=opening==null?null:candidate.Walls.FirstOrDefault(w=>Same(w.Id,opening.HostWallId));
            var wall=candidate.Walls.FirstOrDefault(w=>Same(w.Id,wallId));
            if(oldWall==null||wall==null||wall.StoreyId!=oldWall.StoreyId){error="请选择同一楼层的墙。";return false;}
            if(!Finite(offset)){error="门窗位置必须是有效数值。";return false;}
            opening.HostWallId=wallId;opening.Offset=offset;opening.PlanFlipAlong=flipAlong;opening.PlanFlipNormal=flipNormal;
            error=ValidateOpeningGeometry(candidate,wall,opening);if(error!=null)return false;
            foreach(var reference in candidate.Storeys.Where(s=>s.TemplateStoreyId==wall.StoreyId))
                if(opening.Sill+opening.Height>(wall.Height>0?wall.Height:reference.Height)+.5){error="门窗顶部超出引用层墙高。";return false;}
            Commit(candidate);return true;
        }
        public bool TryPlacePendingOpening(string storeyId,string sourceHandle,string wallId,double offset,out string id,out string error)
        {
            id=null;error=null;
            var candidate=Clone(Model);
            var pending=candidate.CadImport?.PendingOpenings?.SingleOrDefault(p=>p.StoreyId==storeyId&&p.SourceHandle==sourceHandle);
            var wall=candidate.Walls.FirstOrDefault(w=>w.Id==wallId&&w.StoreyId==storeyId);
            if(pending==null||wall==null){error="请选择该门窗所在楼层的墙。";return false;}
            var floor=candidate.FindStorey(storeyId);var height=wall.Height>0?wall.Height:floor.Height;
            var opening=new OpeningModel {Id="O-"+Guid.NewGuid().ToString("N"),HostWallId=wall.Id,Code=pending.Code,
                Kind=pending.Kind,Offset=offset,Width=pending.Width,Height=pending.Height,OpenIn3D=false,
                Sill=Math.Max(0,Math.Min(pending.Kind=="窗"?900:0,height-pending.Height))};
            candidate.Openings.Add(opening);
            error=ValidateOpeningGeometry(candidate,wall,opening);
            if(error!=null)return false;
            foreach(var reference in candidate.Storeys.Where(s=>s.TemplateStoreyId==storeyId))
                if(opening.Sill+opening.Height>(wall.Height>0?wall.Height:reference.Height)+.5){error="门窗顶部超出引用层墙高。";return false;}
            candidate.CadImport.PendingOpenings.Remove(pending);
            candidate.CadImport.ResolvedOpenings=candidate.CadImport.ResolvedOpenings??new List<CadResolvedOpening>();
            candidate.CadImport.ResolvedOpenings.Add(new CadResolvedOpening {StoreyId=storeyId,SourceHandle=sourceHandle,OpeningId=opening.Id});
            Commit(candidate);id=opening.Id;return true;
        }
        public bool TrySetOpeningConstruction(string openingId,OpeningTypeModel draft,bool onlyInstance,out string error,IDictionary<string,OpeningTypeModel> templates=null,double? snapStep=null)
        {
            error=null;
            if(snapStep.HasValue && (double.IsNaN(snapStep.Value)||double.IsInfinity(snapStep.Value)||snapStep.Value<0||snapStep.Value>1000)){error="移动步长须在 0～1000 mm 之间。";return false;}
            var physical=StandardStoreyLayout.Materialize(Model);
            var selected=physical.Openings.FirstOrDefault(o=>Same(o.Id,openingId));
            if(selected==null){error="未找到门窗。";return false;}
            var candidate=Clone(Model);
            candidate.OpeningTypes=candidate.OpeningTypes ?? new List<OpeningTypeModel>();
            candidate.OpeningOverrides=candidate.OpeningOverrides ?? new List<OpeningInstanceOverride>();
            var type=OpeningConstruction.Copy(draft);
            OpeningConstruction.ResizeType(type,selected.Width,selected.Height);
            var code=OpeningConstruction.EffectiveCode(selected);
            var originalCode=code;
            if(onlyInstance) {
                var used=new HashSet<string>(physical.Openings.Select(o=>o.Code).Concat(candidate.OpeningTypes.Select(t=>t.Code)),StringComparer.OrdinalIgnoreCase);
                var index=0;var prefix=code;do{code=prefix+OpeningConstruction.AlphabeticSuffix(index++);}while(used.Contains(code));
                candidate.OpeningOverrides.RemoveAll(o=>Same(o.OpeningId,openingId));
                candidate.OpeningOverrides.Add(new OpeningInstanceOverride { OpeningId=openingId,TypeCode=code });
            }
            var affected=onlyInstance ? new[]{selected} : physical.Openings.Where(o=>Same(OpeningConstruction.EffectiveCode(o),originalCode)).ToArray();
            foreach(var o in affected) {error=OpeningConstruction.Validate(o,type);if(error!=null)return false;}
            type.Code=code;type.Width=selected.Width;type.Height=selected.Height;type.Kind=selected.Kind;
            candidate.OpeningTypes.RemoveAll(t=>Same(t.Code,code));candidate.OpeningTypes.Add(type);
            if(string.IsNullOrWhiteSpace(selected.Code) && !onlyInstance)
                foreach(var o in candidate.Openings.Where(o=>string.IsNullOrWhiteSpace(o.Code)&&Same(OpeningConstruction.EffectiveCode(o),originalCode)))o.Code=code;
            if(templates!=null)candidate.OpeningTemplates=templates.Select(t=>{var copy=OpeningConstruction.Copy(t.Value);copy.Code=t.Key;return copy;}).ToList();
            if(snapStep.HasValue)candidate.OpeningEditorSnapStep=snapStep;
            Commit(candidate);return true;
        }
        public bool TrySetOpeningConstructions(IDictionary<string,OpeningTypeModel> changes,out string error)
        {
            error=null;if(changes==null || changes.Count==0){error="请选择门窗类型。";return false;}
            var candidate=Clone(Model);candidate.OpeningTypes=candidate.OpeningTypes??new List<OpeningTypeModel>();
            var physical=StandardStoreyLayout.Materialize(Model);
            foreach(var entry in changes) {
                var affected=physical.Openings.Where(o=>Same(OpeningConstruction.EffectiveCode(o),entry.Key)).ToArray();
                if(affected.Length==0){error="门窗类型已变化，请重新打开门窗表。";return false;}
                var type=OpeningConstruction.Copy(entry.Value);type.Code=entry.Key;
                foreach(var opening in affected){error=OpeningConstruction.Validate(opening,type);if(error!=null)return false;}
                candidate.OpeningTypes.RemoveAll(t=>Same(t.Code??"",entry.Key));candidate.OpeningTypes.Add(type);
                foreach(var o in candidate.Openings.Where(o=>string.IsNullOrWhiteSpace(o.Code)&&Same(OpeningConstruction.EffectiveCode(o),entry.Key)))o.Code=entry.Key;
            }
            Commit(candidate);return true;
        }
        public bool TrySaveOpeningTemplate(string name,OpeningTypeModel type,out string error)
        {
            error=null;
            if(string.IsNullOrWhiteSpace(name)){error="请输入模板名称。";return false;}
            if(type==null){error="请选择门窗做法。";return false;}
            var example=new OpeningModel {Code=type.Code,Kind=type.Kind,Width=type.Width,Height=type.Height,Sill=type.Sill,ThresholdHeight=type.ThresholdHeight};
            error=OpeningConstruction.Validate(example,type);if(error!=null)return false;
            if(!Finite(type.PlanOpenAngle)||type.PlanOpenAngle<0||type.PlanOpenAngle>180){error="平面开启角度应为 0～180°。";return false;}
            var candidate=Clone(Model);candidate.OpeningTemplates=candidate.OpeningTemplates ?? new List<OpeningTypeModel>();
            var copy=OpeningConstruction.Copy(type);copy.Code=name.Trim();
            candidate.OpeningTemplates.RemoveAll(t=>Same(t.Code,copy.Code));candidate.OpeningTemplates.Add(copy);Commit(candidate);return true;
        }
    }
}
