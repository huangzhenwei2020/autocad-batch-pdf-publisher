using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    public enum OpeningSizeConflictChoice { CreateNew, MergeExisting, MergeEditedDefinition }
    public sealed class OpeningSizeCodePlan
    {
        public string Code { get; internal set; }
        public string NewCode { get; internal set; }
        public bool HasConflict { get; internal set; }
        public bool CanMerge { get; internal set; }
        public string MergeError { get; internal set; }
        public bool CanUpdateDefinition { get; internal set; }
        public string UpdateDefinitionError { get; internal set; }
    }
    public sealed class PendingOpeningPlacementPlan
    {
        public double Width { get; internal set; }
        public double Height { get; internal set; }
        public double Sill { get; internal set; }
        public double MaximumWidth { get; internal set; }
        public double MaximumHeight { get; internal set; }
        public double SuggestedWidth { get; internal set; }
        public double SuggestedHeight { get; internal set; }
        public OpeningSizeCodePlan CodePlan { get; internal set; }
        public string Error { get; internal set; }
    }
    public sealed partial class BuildingModelEditSession
    {
        public double[] GetOpeningSizeLimits(string id,bool onlyInstance,double? offset=null,double? sill=null)
        {
            var physical=StandardStoreyLayout.Materialize(Model);
            var sourceId=StandardStoreyLayout.SourceElementId(Model,id);
            var selected=physical.Openings.FirstOrDefault(o=>Same(o.Id,id))??physical.Openings.FirstOrDefault(o=>Same(o.Id,sourceId));
            if(selected==null)return new[]{double.PositiveInfinity,double.PositiveInfinity};
            var code=OpeningConstruction.EffectiveCode(selected);
            var affected=physical.Openings.Where(o=>onlyInstance
                ?Same(StandardStoreyLayout.SourceElementId(Model,o.Id),sourceId)
                :Same(OpeningConstruction.EffectiveCode(o),code));
            var width=double.PositiveInfinity;var height=double.PositiveInfinity;
            foreach(var opening in affected)
            {
                var wall=physical.Walls.FirstOrDefault(w=>Same(w.Id,opening.HostWallId));
                if(wall==null||Math.Abs(wall.Bulge)>1e-10)continue;
                var isSelected=Same(StandardStoreyLayout.SourceElementId(Model,opening.Id),sourceId);
                var center=isSelected&&offset.HasValue?offset.Value:opening.Offset;
                var bottom=isSelected&&sill.HasValue?sill.Value:opening.Sill;
                var length=Math.Sqrt(Math.Pow(wall.X2-wall.X1,2)+Math.Pow(wall.Y2-wall.Y1,2));
                width=Math.Min(width,Math.Max(0,2*Math.Min(center,length-center)));
                var wallHeight=wall.Height>0?wall.Height:physical.FindStorey(wall.StoreyId)?.Height??0;
                height=Math.Min(height,Math.Max(0,wallHeight-bottom));
            }
            return new[]{width,height};
        }

        public OpeningSizeCodePlan PlanOpeningSizeCode(string id,double width,double height,out string error)
        {
            error=null;
            var physical=StandardStoreyLayout.Materialize(Model);
            var sourceId=StandardStoreyLayout.SourceElementId(Model,id);
            var opening=physical.Openings.FirstOrDefault(o=>Same(o.Id,id))??physical.Openings.FirstOrDefault(o=>Same(o.Id,sourceId));
            if(opening==null){error="未找到门窗。";return null;}
            return PlanOpeningSizeCode(physical,opening,width,height,out error);
        }
        private OpeningSizeCodePlan PlanOpeningSizeCode(BuildingModelDocument physical,OpeningModel opening,double width,double height,out string error,bool preserveCode=false)
        {
            error=null;
            if(!Finite(width)||!Finite(height)||width<=0||height<=0){error="洞口宽高必须是有效正数。";return null;}
            var originalCode=OpeningConstruction.EffectiveCode(opening);
            var match=System.Text.RegularExpressions.Regex.Match(originalCode,@"^([A-Za-z]+)\d{4}[A-Za-z]*$");
            var code=!preserveCode&&match.Success?OpeningConstruction.SizeCode(match.Groups[1].Value,width,height):originalCode;
            if(code.Length>56)code=code.Substring(0,56);
            var used=new HashSet<string>(physical.Openings.Select(OpeningConstruction.EffectiveCode)
                .Concat((Model.OpeningTypes??new List<OpeningTypeModel>()).Select(t=>t.Code)),StringComparer.OrdinalIgnoreCase);
            var fresh=code;var index=0;
            while(used.Contains(fresh))fresh=code+OpeningConstruction.AlphabeticSuffix(index++);
            var plan=new OpeningSizeCodePlan {Code=code,NewCode=fresh,HasConflict=used.Contains(code)};
            if(plan.HasConflict) {
                var target=FindOpeningMergeType(physical,code);
                plan.CanUpdateDefinition=target!=null&&target.Kind==opening.Kind
                    &&physical.Openings.Where(o=>Same(OpeningConstruction.EffectiveCode(o),code)).All(o=>o.Kind==opening.Kind);
                if(!plan.CanUpdateDefinition)plan.UpdateDefinitionError="已有编号的门窗类别不同，不能同步当前分格。";
                if(target==null||target.Kind!=opening.Kind||Math.Abs(target.Width-width)>.001||Math.Abs(target.Height-height)>.001
                    ||physical.Openings.Where(o=>Same(OpeningConstruction.EffectiveCode(o),code))
                        .Any(o=>o.Kind!=opening.Kind||Math.Abs(o.Width-width)>.001||Math.Abs(o.Height-height)>.001))
                    plan.MergeError="已有编号的尺寸或类别不同，不能合并。";
                else {
                    var example=new OpeningModel {Kind=opening.Kind,Width=width,Height=height,Code=code};
                    plan.MergeError=OpeningConstruction.Validate(example,target);
                    plan.CanMerge=plan.MergeError==null;
                }
            }
            return plan;
        }
        private static OpeningTypeModel FindOpeningMergeType(BuildingModelDocument model,string code)
        {
            var type=model.OpeningTypes?.FirstOrDefault(t=>Same(t.Code,code));
            if(type!=null)return type;
            var opening=model.Openings.FirstOrDefault(o=>Same(OpeningConstruction.EffectiveCode(o),code));
            return opening==null?null:OpeningConstruction.Resolve(model,opening);
        }
        public bool TrySetOpeningParameters(string id,double offset,double width,double height,double sill,double threshold,bool onlyInstance,out string error,
            OpeningSizeConflictChoice conflictChoice=OpeningSizeConflictChoice.CreateNew)
        {
            var sourceId=StandardStoreyLayout.SourceElementId(Model,id);
            var opening=Model.Openings.FirstOrDefault(o=>Same(o.Id,sourceId));
            if(opening==null){error="未找到门窗。";return false;}
            if(width==opening.Width&&height==opening.Height)
                return TrySetOpeningGeometry(sourceId,offset,width,height,sill,threshold,out error);
            var type=OpeningConstruction.Copy(OpeningConstruction.Resolve(Model,opening));
            OpeningConstruction.ResizeType(type,width,height);
            return TrySetOpeningDefinition(id,type,onlyInstance,out error,offset,sill,threshold,conflictChoice:conflictChoice);
        }

        public bool TrySetOpeningDefinition(string id,OpeningTypeModel draft,bool onlyInstance,out string error,
            double? offset=null,double? sill=null,double? threshold=null,IDictionary<string,OpeningTypeModel> templates=null,double? snapStep=null,
            OpeningSizeConflictChoice conflictChoice=OpeningSizeConflictChoice.CreateNew)
        {
            error=null;
            if(draft==null||!Finite(draft.Width)||!Finite(draft.Height)||draft.Width<=0||draft.Height<=0)
            {error="洞口宽高必须是有效正数。";return false;}
            if((offset.HasValue&&!Finite(offset.Value))||(sill.HasValue&&!Finite(sill.Value))||(threshold.HasValue&&!Finite(threshold.Value)))
            {error="洞口定位、窗台和门槛必须是有限数值。";return false;}
            if(snapStep.HasValue&&(!Finite(snapStep.Value)||snapStep<0||snapStep>1000))
            {error="移动步长须在 0～1000 mm 之间。";return false;}
            var sourceId=StandardStoreyLayout.SourceElementId(Model,id);
            var source=Model.Openings.FirstOrDefault(o=>Same(o.Id,sourceId));
            if(source==null){error="未找到门窗。";return false;}
            var limits=GetOpeningSizeLimits(id,onlyInstance,offset,sill);
            var fittedWidth=OpeningConstruction.FitRoundedOpeningSize(draft.Width,limits[0]);
            var fittedHeight=OpeningConstruction.FitRoundedOpeningSize(draft.Height,limits[1]);
            if(fittedWidth!=draft.Width||fittedHeight!=draft.Height)
            {draft=OpeningConstruction.Copy(draft);OpeningConstruction.ResizeType(draft,fittedWidth,fittedHeight);}
            if(draft.Width==source.Width&&draft.Height==source.Height&&!offset.HasValue&&!sill.HasValue&&!threshold.HasValue)
                return TrySetOpeningConstruction(id,draft,onlyInstance,out error,templates,snapStep);
            var candidate=Clone(Model);
            candidate.OpeningOverrides=candidate.OpeningOverrides??new List<OpeningInstanceOverride>();
            candidate.OpeningTypes=candidate.OpeningTypes??new List<OpeningTypeModel>();
            var displayed=OpeningConstruction.ApplyOverrides(candidate);
            var selected=displayed.Openings.First(o=>Same(o.Id,sourceId));
            var originalCode=OpeningConstruction.EffectiveCode(selected);
            var affected=onlyInstance?new[]{sourceId}:displayed.Openings
                .Where(o=>Same(OpeningConstruction.EffectiveCode(o),originalCode)).Select(o=>o.Id).ToArray();
            var plan=PlanOpeningSizeCode(id,draft.Width,draft.Height,out error);
            if(plan==null)return false;
            var merge=plan.HasConflict&&(conflictChoice==OpeningSizeConflictChoice.MergeExisting
                ||conflictChoice==OpeningSizeConflictChoice.MergeEditedDefinition);
            if(merge&&(conflictChoice==OpeningSizeConflictChoice.MergeEditedDefinition?!plan.CanUpdateDefinition:!plan.CanMerge)){
                error=conflictChoice==OpeningSizeConflictChoice.MergeEditedDefinition?plan.UpdateDefinitionError:plan.MergeError;return false;}
            var code=merge?plan.Code:plan.NewCode;
            var type=OpeningConstruction.Copy(merge&&conflictChoice==OpeningSizeConflictChoice.MergeExisting
                ?FindOpeningMergeType(StandardStoreyLayout.Materialize(candidate),code):draft);
            type.Code=code;type.Kind=source.Kind;
            candidate.OpeningTypes.RemoveAll(t=>Same(t.Code,code));candidate.OpeningTypes.Add(type);
            var ids=new HashSet<string>(affected,StringComparer.OrdinalIgnoreCase);
            if(merge&&conflictChoice==OpeningSizeConflictChoice.MergeEditedDefinition)
                ids.UnionWith(displayed.Openings.Where(o=>Same(OpeningConstruction.EffectiveCode(o),code)).Select(o=>o.Id));
            foreach(var opening in candidate.Openings.Where(o=>ids.Contains(o.Id))) {
                opening.Width=type.Width;opening.Height=type.Height;opening.Code=code;
                opening.CodeManuallyEdited=true;
                if(Same(opening.Id,sourceId)) {
                    if(offset.HasValue)opening.Offset=offset.Value;
                    if(sill.HasValue)opening.Sill=sill.Value;
                    if(threshold.HasValue)opening.ThresholdHeight=threshold.Value;
                }
            }
            candidate.OpeningOverrides.RemoveAll(o=>ids.Contains(o.OpeningId));
            var physical=StandardStoreyLayout.Materialize(candidate);
            foreach(var opening in physical.Openings.Where(o=>ids.Contains(StandardStoreyLayout.SourceElementId(candidate,o.Id)))) {
                var wall=physical.Walls.FirstOrDefault(w=>Same(w.Id,opening.HostWallId));
                error=wall==null?"门窗的宿主墙不存在。":ValidateOpeningGeometry(physical,wall,opening);
                if(error==null)error=OpeningConstruction.Validate(opening,OpeningConstruction.Resolve(physical,opening));
                if(error!=null){error=physical.FindStorey(wall?.StoreyId)?.Name+" · "+opening.Code+"："+error;return false;}
            }
            if(templates!=null)candidate.OpeningTemplates=templates.Select(t=>{var copy=OpeningConstruction.Copy(t.Value);copy.Code=t.Key;return copy;}).ToList();
            if(snapStep.HasValue)candidate.OpeningEditorSnapStep=snapStep;
            Commit(candidate);return true;
        }

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
            var planSwing=OpeningPlanGeometry.HasPlanSwing(opening,type);
            if((!planSwing&&angle!=opening.PlanOpenAngle)||(!swing&&openIn3D!=(opening.OpenIn3D??((type.OpenAngle??0)>0)))) {
                error="平面开启角度用于平开扇，请先在立面中设置平开形式；窗的三维开启在构造中设置。";return false;
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
            if(planSwing)opening.PlanOpenAngle=angle;
            if(swing)opening.OpenIn3D=openIn3D;
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
        public PendingOpeningPlacementPlan PlanPendingOpeningPlacement(string storeyId,string sourceHandle,string wallId,double offset,
            double? width=null,double? height=null,double? sill=null)
        {
            var plan=new PendingOpeningPlacementPlan();
            var pending=Model.CadImport?.PendingOpenings?.SingleOrDefault(p=>Same(p.StoreyId,storeyId)&&Same(p.SourceHandle,sourceHandle));
            var wall=Model.Walls.FirstOrDefault(w=>Same(w.Id,wallId)&&Same(w.StoreyId,storeyId));
            if(pending==null||wall==null){plan.Error="请选择该门窗所在楼层的墙。";return plan;}
            if(!Finite(offset)||Math.Abs(wall.Bulge)>1e-10){plan.Error="请选择直墙上的有效定位中心。";return plan;}
            var wallHeight=wall.Height>0?wall.Height:Model.FindStorey(storeyId)?.Height??0;
            foreach(var reference in Model.Storeys.Where(s=>Same(s.TemplateStoreyId,storeyId)))
                wallHeight=Math.Min(wallHeight,wall.Height>0?wall.Height:reference.Height);
            var length=Math.Sqrt(Math.Pow(wall.X2-wall.X1,2)+Math.Pow(wall.Y2-wall.Y1,2));
            if(!Finite(length)||length<=0){plan.Error="宿主墙没有有效长度。";return plan;}
            plan.Sill=sill??Math.Max(0,Math.Min(pending.Kind=="窗"?900:0,wallHeight-pending.Height));
            plan.MaximumWidth=Math.Max(0,2*Math.Min(offset,length-offset));
            foreach(var other in Model.Openings.Where(o=>Same(o.HostWallId,wallId)))
                plan.MaximumWidth=Math.Min(plan.MaximumWidth,Math.Max(0,2*(Math.Abs(other.Offset-offset)-other.Width/2)));
            plan.MaximumHeight=Math.Max(0,wallHeight-plan.Sill);
            plan.Width=width??pending.Width;plan.Height=height??pending.Height;
            if(!Finite(plan.Width)||!Finite(plan.Height)||!Finite(plan.Sill)||plan.Width<=0||plan.Height<=0||plan.Sill<0
                ||!Finite(wallHeight)||wallHeight<=0){plan.Error="洞口宽高须为有效正数，窗台高须为非负毫米数。";return plan;}
            plan.Width=OpeningConstruction.FitRoundedOpeningSize(plan.Width,plan.MaximumWidth);
            plan.Height=OpeningConstruction.FitRoundedOpeningSize(plan.Height,plan.MaximumHeight);
            plan.SuggestedWidth=ClosestInside(plan.Width,plan.MaximumWidth);
            plan.SuggestedHeight=ClosestInside(plan.Height,plan.MaximumHeight);
            var opening=new OpeningModel {Id="pending-preview",HostWallId=wallId,Code=pending.Code,Kind=pending.Kind,
                Offset=offset,Width=plan.Width,Height=plan.Height,Sill=plan.Sill};
            plan.Error=ValidateOpeningGeometry(Model,wall,opening);
            if(plan.Error==null&&plan.Height>plan.MaximumHeight+.5)plan.Error="洞口顶部超出共用标准层可用高度。";
            var changed=plan.Width!=pending.Width||plan.Height!=pending.Height;
            plan.CodePlan=PlanOpeningSizeCode(StandardStoreyLayout.Materialize(Model),opening,plan.Width,plan.Height,out var codeError,!changed);
            if(plan.Error==null)plan.Error=codeError;
            return plan;
        }
        private static double ClosestInside(double requested,double maximum)
        {
            if(requested<=maximum)return Math.Round(requested,0,MidpointRounding.AwayFromZero)<=maximum
                ?Math.Round(requested,0,MidpointRounding.AwayFromZero):Math.Floor(maximum);
            var fitted=OpeningConstruction.FitRoundedOpeningSize(requested,maximum);
            return fitted!=requested?fitted:Math.Floor(maximum/10)*10;
        }
        public bool TryPlacePendingOpening(string storeyId,string sourceHandle,string wallId,double offset,double width,double height,double sill,
            OpeningSizeConflictChoice choice,out string id,out string error)
        {
            id=null;var plan=PlanPendingOpeningPlacement(storeyId,sourceHandle,wallId,offset,width,height,sill);error=plan.Error;
            if(error!=null)return false;
            if(choice==OpeningSizeConflictChoice.MergeEditedDefinition){error="待放置门窗不能覆盖已有类型的做法。";return false;}
            var candidate=Clone(Model);var pending=candidate.CadImport.PendingOpenings.Single(p=>Same(p.StoreyId,storeyId)&&Same(p.SourceHandle,sourceHandle));
            var merge=plan.CodePlan.HasConflict&&choice==OpeningSizeConflictChoice.MergeExisting;
            if(merge&&!plan.CodePlan.CanMerge){error=plan.CodePlan.MergeError??"已有编号的尺寸或类别不同，不能合并。";return false;}
            var code=merge?plan.CodePlan.Code:plan.CodePlan.NewCode;
            var opening=new OpeningModel {Id="O-"+Guid.NewGuid().ToString("N"),HostWallId=wallId,Code=code,Kind=pending.Kind,
                Offset=offset,Width=plan.Width,Height=plan.Height,Sill=plan.Sill,OpenIn3D=false};
            var type=merge?FindOpeningMergeType(StandardStoreyLayout.Materialize(candidate),code):null;
            if(type==null){var sourceType=OpeningConstruction.Library(candidate).FindType(pending.Code);
                type=sourceType==null?OpeningConstruction.Default(opening):OpeningConstruction.Copy(sourceType);OpeningConstruction.ResizeType(type,plan.Width,plan.Height);
                type.Code=code;type.Kind=pending.Kind;candidate.OpeningTypes.Add(type);}
            error=OpeningConstruction.Validate(opening,type);if(error!=null)return false;
            candidate.Openings.Add(opening);candidate.CadImport.PendingOpenings.Remove(pending);
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
            var sourceIds=new HashSet<string>(affected.Select(o=>StandardStoreyLayout.SourceElementId(Model,o.Id)),StringComparer.OrdinalIgnoreCase);
            candidate.OpeningOverrides.RemoveAll(o=>o.Definition!=null && sourceIds.Contains(StandardStoreyLayout.SourceElementId(Model,o.OpeningId)));
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
                var sourceIds=new HashSet<string>(affected.Select(o=>StandardStoreyLayout.SourceElementId(Model,o.Id)),StringComparer.OrdinalIgnoreCase);
                candidate.OpeningOverrides.RemoveAll(o=>o.Definition!=null && sourceIds.Contains(StandardStoreyLayout.SourceElementId(Model,o.OpeningId)));
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
