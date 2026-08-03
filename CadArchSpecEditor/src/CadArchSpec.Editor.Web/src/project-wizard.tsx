import { useMemo, useState } from "react";
import {
  applyBuildingTemplate,
  createBlankWorkspace,
  synchronizeWorkspaceFieldNodes,
  type EditorWorkspace,
  type ProjectFeatures,
} from "./editor-model";

export type ProjectWizardDraft = {
  projectName: string;
  country: string;
  province: string;
  city: string;
  district: string;
  submissionDate: string;
  designStage: string;
  projectNature: NonNullable<EditorWorkspace["projectNature"]>;
  buildingType: string;
  totalFloorArea: string;
  buildingHeight: string;
  aboveGroundFloors: string;
  undergroundFloors: string;
  features: ProjectFeatures;
  isSpecialConstruction: boolean;
  requiresFireReview: boolean;
  isHighRiseOrSpecial: boolean;
};

export function createProjectWizardDraft(): ProjectWizardDraft {
  return {
    projectName: "",
    country: "中国",
    province: "",
    city: "",
    district: "",
    submissionDate: new Date().toISOString().slice(0, 10),
    designStage: "施工图设计",
    projectNature: "新建",
    buildingType: "通用建筑",
    totalFloorArea: "",
    buildingHeight: "",
    aboveGroundFloors: "",
    undergroundFloors: "0",
    features: {
      hasBasement: false,
      hasCurtainWall: false,
      hasElevator: false,
      hasCivilDefense: false,
      isGreenBuilding: false,
      isPrefabricated: false,
      hasSpecialistDesign: false,
    },
    isSpecialConstruction: false,
    requiresFireReview: false,
    isHighRiseOrSpecial: false,
  };
}

export function validateWizardStep(step: number, draft: ProjectWizardDraft): string[] {
  const issues: string[] = [];
  if (step === 0) {
    if (!draft.projectName.trim()) issues.push("请填写项目名称");
    if (!draft.province.trim()) issues.push("请填写省级地区");
    if (!draft.city.trim()) issues.push("请填写城市");
    if (!draft.submissionDate) issues.push("请选择报审日期");
  }
  if (step === 1) {
    const positive = (value: string) => Number.isFinite(Number(value)) && Number(value) > 0;
    const nonNegativeInteger = (value: string) =>
      Number.isInteger(Number(value)) && Number(value) >= 0;
    if (!positive(draft.totalFloorArea)) issues.push("总建筑面积应为大于 0 的数字");
    if (!positive(draft.buildingHeight)) issues.push("建筑高度应为大于 0 的数字");
    if (!nonNegativeInteger(draft.aboveGroundFloors) || Number(draft.aboveGroundFloors) < 1) {
      issues.push("地上层数应为大于 0 的整数");
    }
    if (!nonNegativeInteger(draft.undergroundFloors)) {
      issues.push("地下层数应为大于或等于 0 的整数");
    }
  }
  return issues;
}

export function buildWorkspaceFromWizard(draft: ProjectWizardDraft): EditorWorkspace {
  const blank = createBlankWorkspace();
  const location = [draft.province, draft.city, draft.district]
    .map((part) => part.trim())
    .filter(Boolean)
    .join("");
  const values: Record<string, string> = {
    "project.projectName": draft.projectName.trim(),
    "project.location": location,
    "building.totalFloorArea": draft.totalFloorArea.trim(),
    "building.height": draft.buildingHeight.trim(),
    "building.aboveGroundFloors": draft.aboveGroundFloors.trim(),
    "building.undergroundFloors": draft.undergroundFloors.trim(),
  };
  const enteredAt = new Date().toISOString();
  return synchronizeWorkspaceFieldNodes(applyBuildingTemplate({
    ...blank,
    projectName: values["project.projectName"],
    location,
    buildingType: draft.buildingType,
    designStage: draft.designStage,
    projectNature: draft.projectNature,
    jurisdiction: {
      country: draft.country.trim() || "中国",
      province: draft.province.trim(),
      city: draft.city.trim(),
      district: draft.district.trim(),
    },
    submissionDate: draft.submissionDate,
    reviewProfile: {
      isSpecialConstruction: draft.isSpecialConstruction,
      requiresFireReview: draft.requiresFireReview,
      isHighRiseOrSpecial: draft.isHighRiseOrSpecial,
    },
    features: {
      ...draft.features,
      hasBasement: draft.features.hasBasement || Number(draft.undergroundFloors) > 0,
    },
    fields: blank.fields.map((field) => {
      const value = values[field.path] ?? "";
      return value
        ? {
            ...field,
            value,
            state: "pending" as const,
            source: "项目向导录入，待专业人员确认",
            sourceType: "manual" as const,
            enteredBy: "",
            confirmedAt: null,
            locked: false,
          }
        : field;
    }),
    lastSavedAt: enteredAt,
  }));
}

type ProjectWizardProps = {
  onCancel(): void;
  onComplete(workspace: EditorWorkspace): void;
};

const buildingTypes = [
  "通用建筑",
  "住宅建筑",
  "办公建筑",
  "商业建筑",
  "教育建筑",
  "医疗建筑",
  "交通建筑",
  "文体建筑",
  "工业建筑",
  "其他建筑",
];

export function ProjectWizard({ onCancel, onComplete }: ProjectWizardProps) {
  const [step, setStep] = useState(0);
  const [draft, setDraft] = useState(createProjectWizardDraft);
  const [issues, setIssues] = useState<string[]>([]);
  const steps = ["项目与地区", "分类与规模", "专项条件", "确认"];
  const location = useMemo(
    () => [draft.province, draft.city, draft.district].filter(Boolean).join(""),
    [draft.province, draft.city, draft.district],
  );
  const patch = <K extends keyof ProjectWizardDraft>(key: K, value: ProjectWizardDraft[K]) =>
    setDraft((current) => ({ ...current, [key]: value }));
  const patchFeature = (key: keyof ProjectFeatures, value: boolean) =>
    setDraft((current) => ({
      ...current,
      features: { ...current.features, [key]: value },
    }));
  const moveNext = () => {
    const validation = validateWizardStep(step, draft);
    setIssues(validation);
    if (validation.length === 0) setStep((current) => Math.min(3, current + 1));
  };

  return (
    <div className="dialog-backdrop wizard-backdrop">
      <section className="settings-dialog project-wizard">
        <header>
          <div>
            <span className="eyebrow">新建建筑专业项目</span>
            <h2>项目向导</h2>
          </div>
          <button className="dialog-close" onClick={onCancel}>×</button>
        </header>

        <ol className="wizard-steps">
          {steps.map((label, index) => (
            <li className={index === step ? "active" : index < step ? "done" : ""} key={label}>
              <span>{index + 1}</span>{label}
            </li>
          ))}
        </ol>

        <div className="wizard-content">
          {step === 0 && (
            <div className="form-grid wizard-grid">
              <label className="wide">
                <span>项目名称 *</span>
                <input autoFocus value={draft.projectName} onChange={(event) => patch("projectName", event.target.value)} />
              </label>
              <label><span>国家/地区</span><input value={draft.country} onChange={(event) => patch("country", event.target.value)} /></label>
              <label><span>省/自治区/直辖市 *</span><input value={draft.province} onChange={(event) => patch("province", event.target.value)} /></label>
              <label><span>城市 *</span><input value={draft.city} onChange={(event) => patch("city", event.target.value)} /></label>
              <label><span>区县（可选）</span><input value={draft.district} onChange={(event) => patch("district", event.target.value)} /></label>
              <label><span>报审日期 *</span><input type="date" value={draft.submissionDate} onChange={(event) => patch("submissionDate", event.target.value)} /></label>
              <div className="wizard-note wide">所在地用于选择规则包；当前版本未加载地方规则时，只进行国家基础完整性检查。</div>
            </div>
          )}

          {step === 1 && (
            <div className="form-grid wizard-grid">
              <label><span>建筑类型</span><select value={draft.buildingType} onChange={(event) => patch("buildingType", event.target.value)}>{buildingTypes.map((item) => <option key={item}>{item}</option>)}</select></label>
              <label><span>设计阶段</span><select value={draft.designStage} onChange={(event) => patch("designStage", event.target.value)}><option>方案设计</option><option>初步设计</option><option>施工图设计</option></select></label>
              <label><span>项目性质</span><select value={draft.projectNature} onChange={(event) => patch("projectNature", event.target.value as ProjectWizardDraft["projectNature"])}><option>新建</option><option>改建</option><option>扩建</option></select></label>
              <label><span>总建筑面积（m²）*</span><input inputMode="decimal" value={draft.totalFloorArea} onChange={(event) => patch("totalFloorArea", event.target.value)} /></label>
              <label><span>建筑高度（m）*</span><input inputMode="decimal" value={draft.buildingHeight} onChange={(event) => patch("buildingHeight", event.target.value)} /></label>
              <label><span>地上层数 *</span><input inputMode="numeric" value={draft.aboveGroundFloors} onChange={(event) => patch("aboveGroundFloors", event.target.value)} /></label>
              <label><span>地下层数 *</span><input inputMode="numeric" value={draft.undergroundFloors} onChange={(event) => patch("undergroundFloors", event.target.value)} /></label>
              <div className="wizard-note wide">向导录入的数据统一标记为“待复核”，不会自动确认为有效设计参数。</div>
            </div>
          )}

          {step === 2 && (
            <>
              <div className="feature-grid wizard-features">
                {([
                  ["hasBasement", "设有地下室"],
                  ["hasCurtainWall", "涉及幕墙"],
                  ["hasElevator", "设有电梯"],
                  ["hasCivilDefense", "涉及人防"],
                  ["isGreenBuilding", "绿色建筑"],
                  ["isPrefabricated", "装配式建筑"],
                  ["hasSpecialistDesign", "涉及专项深化设计"],
                ] as const).map(([key, label]) => (
                  <label key={key}><input type="checkbox" checked={draft.features[key]} onChange={(event) => patchFeature(key, event.target.checked)} /><span>{label}</span></label>
                ))}
                <label><input type="checkbox" checked={draft.isSpecialConstruction} onChange={(event) => patch("isSpecialConstruction", event.target.checked)} /><span>特殊建设工程</span></label>
                <label><input type="checkbox" checked={draft.requiresFireReview} onChange={(event) => patch("requiresFireReview", event.target.checked)} /><span>需要消防设计审查</span></label>
                <label><input type="checkbox" checked={draft.isHighRiseOrSpecial} onChange={(event) => patch("isHighRiseOrSpecial", event.target.checked)} /><span>涉及超限、高层或其他专项</span></label>
              </div>
              <div className="wizard-note">条件仅用于启用相关章节，不代表项目已经满足对应规范要求。</div>
            </>
          )}

          {step === 3 && (
            <div className="wizard-summary">
              <h3>{draft.projectName}</h3>
              <dl>
                <div><dt>建设地点</dt><dd>{location}</dd></div>
                <div><dt>分类</dt><dd>{draft.buildingType} · {draft.projectNature} · {draft.designStage}</dd></div>
                <div><dt>规模</dt><dd>{draft.totalFloorArea} m² · {draft.buildingHeight} m · 地上 {draft.aboveGroundFloors} 层 / 地下 {draft.undergroundFloors} 层</dd></div>
                <div><dt>报审日期</dt><dd>{draft.submissionDate}</dd></div>
              </dl>
              <div className="review-notice">完成后将生成结构化建筑专业章节。所有向导数据仍为“待复核”，需要设计人员逐项确认并锁定。</div>
            </div>
          )}

          {issues.length > 0 && (
            <ul className="wizard-errors">{issues.map((issue) => <li key={issue}>{issue}</li>)}</ul>
          )}
        </div>

        <footer>
          <button className="button" onClick={onCancel}>取消</button>
          {step > 0 && <button className="button" onClick={() => { setIssues([]); setStep((current) => current - 1); }}>上一步</button>}
          {step < 3
            ? <button className="button primary" onClick={moveNext}>下一步</button>
            : <button className="button primary" onClick={() => onComplete(buildWorkspaceFromWizard(draft))}>创建项目</button>}
        </footer>
      </section>
    </div>
  );
}
