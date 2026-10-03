using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed partial class BuildingModelEditSession
    {
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
            var candidate=Clone(Model);candidate.OpeningTemplates=candidate.OpeningTemplates ?? new List<OpeningTypeModel>();
            var copy=OpeningConstruction.Copy(type);copy.Code=name.Trim();
            candidate.OpeningTemplates.RemoveAll(t=>Same(t.Code,copy.Code));candidate.OpeningTemplates.Add(copy);Commit(candidate);return true;
        }
    }
}
