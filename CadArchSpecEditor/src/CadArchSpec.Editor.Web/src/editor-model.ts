import type { Node as ProseMirrorNode } from "prosemirror-model";
import type { RuleReviewResult } from "./rule-review";

export type FieldState =
  | "confirmed"
  | "pending"
  | "unknown"
  | "notApplicable"
  | "providedByOtherDiscipline"
  | "providedBySpecialist"
  | "overridden";

export type FieldSourceType =
  | "projectApproval"
  | "designBrief"
  | "drawing"
  | "calculation"
  | "otherDiscipline"
  | "specialist"
  | "manual";

export type ProjectField = {
  path: string;
  label: string;
  value: string;
  unit?: string;
  state: FieldState;
  source: string;
  locked?: boolean;
  required?: boolean;
  sourceType?: FieldSourceType;
  sourceDocumentId?: string;
  enteredBy?: string;
  confirmedAt?: string | null;
  isManuallyOverridden?: boolean;
  overrideReason?: string;
};

export type EditorSection = {
  id: string;
  number: string;
  title: string;
  requirement: "required" | "conditional" | "optional";
  enabled?: boolean;
  activationReason?: string;
  reviewState: "notReviewed" | "needsAttention" | "ready";
  content: Record<string, unknown>;
};

export type EditorWorkspace = {
  schemaVersion: 1;
  projectName: string;
  location: string;
  buildingType: string;
  designStage: string;
  projectNature?: "新建" | "改建" | "扩建";
  jurisdiction?: ProjectJurisdiction;
  submissionDate?: string;
  reviewProfile?: ProjectReviewProfile;
  reviewSignoff?: ProjectReviewSignoff;
  features?: ProjectFeatures;
  lastSavedAt: string | null;
  fieldChanges?: FieldChangeEntry[];
  reviewRecords?: ReviewArchiveRecord[];
  tables?: ArchitectureTable[];
  cadLayout?: CadLayoutProfile;
  standards?: DesignStandard[];
  fields: ProjectField[];
  sections: EditorSection[];
};

export type DesignStandard = {
  id: string;
  code: string;
  name: string;
  level: "国家" | "地方" | "自定义";
  region: string;
  buildingTypes: string[];
  enabled: boolean;
  isPreset: boolean;
  note: string;
  sourceUrl: string;
};

const residentialNationalPreset = (id: string, code: string, name: string, enabled = true, note = "住宅项目常用国家/行业预设，使用前核对现行性"): DesignStandard => ({
  id, code, name, level: "国家", region: "全国", buildingTypes: ["住宅建筑"], enabled, isPreset: true, note, sourceUrl: "",
});

const nationalPreset = (
  id: string,
  code: string,
  name: string,
  buildingTypes: string[],
  enabled = true,
  note = "国家/行业预设，使用前应按项目报审日期复核现行性",
  sourceUrl = "https://www.mohurd.gov.cn/cms_files/filemanager/1150240553/attach/202411/d843924393711cc7abd4e4a2f030504a.pdf",
): DesignStandard => ({
  id, code, name, level: "国家", region: "全国", buildingTypes,
  enabled, isPreset: true, note, sourceUrl,
});

export const defaultDesignStandards = (): DesignStandard[] => [
  { id: "gb-55031-2022", code: "GB 55031-2022", name: "民用建筑通用规范", level: "国家", region: "全国", buildingTypes: ["通用建筑", "住宅建筑", "办公建筑", "商业建筑", "教育建筑", "医疗建筑", "交通建筑", "文体建筑"], enabled: true, isPreset: true, note: "民用建筑基础预设", sourceUrl: "https://zjj.sm.gov.cn/xxgk/fgwj/jsbz/202209/t20220909_1827378.htm" },
  { id: "gb-55037-2022", code: "GB 55037-2022", name: "建筑防火通用规范", level: "国家", region: "全国", buildingTypes: ["通用建筑", "住宅建筑", "办公建筑", "商业建筑", "教育建筑", "医疗建筑", "交通建筑", "文体建筑", "工业建筑"], enabled: true, isPreset: true, note: "建筑防火基础预设", sourceUrl: "https://zjj.sm.gov.cn/ztzl/xfzt/202401/t20240111_1993450.htm" },
  { id: "gb-55019-2021", code: "GB 55019-2021", name: "建筑与市政工程无障碍通用规范", level: "国家", region: "全国", buildingTypes: ["通用建筑", "住宅建筑", "办公建筑", "商业建筑", "教育建筑", "医疗建筑", "交通建筑", "文体建筑"], enabled: true, isPreset: true, note: "无障碍设计预设", sourceUrl: "https://www.beijing.gov.cn/gate/big5/www.beijing.gov.cn/zhengce/zhengcefagui/qtwj/202204/t20220412_2674451.html" },
  { id: "gb-55030-2022", code: "GB 55030-2022", name: "建筑与市政工程防水通用规范", level: "国家", region: "全国", buildingTypes: ["通用建筑", "住宅建筑", "办公建筑", "商业建筑", "教育建筑", "医疗建筑", "交通建筑", "文体建筑", "工业建筑"], enabled: true, isPreset: true, note: "防水设计预设", sourceUrl: "https://www.gsjn.gov.cn/zfxxgk/zfbmxxgk/zjj/fdzdgknr/lzyj/art/2022/art_0f0b3ad7fa304c0b961a009b0d8319ca.html" },
  { id: "db11-1950-2021", code: "DB11/1950-2021", name: "公共建筑无障碍设计标准", level: "地方", region: "北京", buildingTypes: ["办公建筑", "商业建筑", "教育建筑", "医疗建筑", "交通建筑", "文体建筑"], enabled: false, isPreset: true, note: "北京市地方预设", sourceUrl: "https://ghzrzyw.beijing.gov.cn/biaozhunguanli/bz/jzsj/202203/t20220316_2632078.html" },
  { id: "dbj-t45-020-2016", code: "DBJ/T45-020-2016", name: "绿色建筑评价标准", level: "地方", region: "广西", buildingTypes: ["通用建筑", "住宅建筑", "办公建筑", "商业建筑", "教育建筑", "医疗建筑"], enabled: false, isPreset: true, note: "广西地方预设，使用前应按项目报审日期复核现行性", sourceUrl: "https://zjt.gxzf.gov.cn/zfxxgk/fdzdgknr/wjtz/t6187953.shtml" },
  residentialNationalPreset("gb-55038-2025", "GB 55038-2025", "住宅项目规范", true, "2025年5月1日起实施的住宅项目强制性工程建设规范"),
  residentialNationalPreset("gb-50096-2011", "GB 50096-2011", "住宅设计规范"),
  residentialNationalPreset("gb-50180-2018", "GB 50180-2018", "城市居住区规划设计标准"),
  residentialNationalPreset("gb-50352-2019", "GB 50352-2019", "民用建筑设计统一标准"),
  residentialNationalPreset("gb-50368-2005", "GB 50368-2005", "住宅建筑规范", false, "已由GB 55038-2025公告废止，仅保留作历史项目识别"),
  residentialNationalPreset("gb-50118-2010", "GB 50118-2010", "民用建筑隔声设计规范"),
  residentialNationalPreset("gb-50325-2020", "GB 50325-2020", "民用建筑工程室内环境污染控制标准"),
  residentialNationalPreset("gbt-50353-2013", "GB/T 50353-2013", "建筑工程建筑面积计算规范"),
  residentialNationalPreset("design-depth-2016", "2016年版", "建筑工程设计文件编制深度规定"),
  residentialNationalPreset("gb-50016-2014", "GB 50016-2014（2018年版）", "建筑设计防火规范"),
  residentialNationalPreset("gb-50222-2017", "GB 50222-2017", "建筑内部装修设计防火规范"),
  residentialNationalPreset("gb-50037-2013", "GB 50037-2013", "建筑地面设计规范"),
  residentialNationalPreset("gb-50345-2012", "GB 50345-2012", "屋面工程技术规范"),
  residentialNationalPreset("gb-55008-2021", "GB 55008-2021", "混凝土结构通用规范"),
  residentialNationalPreset("jgj-230-2010", "JGJ 230-2010", "倒置式屋面工程技术规程"),
  residentialNationalPreset("jgj-214-2010", "JGJ 214-2010", "铝合金门窗工程技术规范"),
  residentialNationalPreset("gbt-8478-2020", "GB/T 8478-2020", "铝合金门窗"),
  residentialNationalPreset("gbt-11976-2002", "GB/T 11976-2002", "建筑外窗采光性能分级及检测方法", false),
  residentialNationalPreset("gbt-8484-2008", "GB/T 8484-2008", "建筑外门窗保温性能分级及检测方法", false),
  residentialNationalPreset("gbt-8485-2008", "GB/T 8485-2008", "建筑门窗空气声隔声性能分级及检测方法", false),
  residentialNationalPreset("gb-51251-2017", "GB 51251-2017", "建筑防烟排烟系统技术标准"),
  residentialNationalPreset("gbt-7106-2008", "GB/T 7106-2008", "建筑外门窗气密、水密、抗风压性能分级及检测方法", false),
  residentialNationalPreset("gb-55015-2021", "GB 55015-2021", "建筑节能与可再生能源利用通用规范"),
  residentialNationalPreset("jgj-t342-2014", "JGJ/T 342-2014", "建筑玻璃与金属护栏"),
  residentialNationalPreset("gb-55016-2021", "GB 55016-2021", "建筑环境通用规范"),
  residentialNationalPreset("jgj-t235-2011", "JGJ/T 235-2011", "建筑外墙防水工程技术规程"),
  residentialNationalPreset("gbt-50378-2019", "GB/T 50378-2019", "绿色建筑评价标准"),
  residentialNationalPreset("jgj-t261-2011", "JGJ/T 261-2011", "外墙内保温工程技术规程"),
  residentialNationalPreset("gb-55007-2021", "GB 55007-2021", "砌体结构通用规范"),
  residentialNationalPreset("gbt-50104-2010", "GB/T 50104-2010", "建筑制图标准"),
  residentialNationalPreset("gbt-50001-2017", "GB/T 50001-2017", "房屋建筑制图统一标准"),
  residentialNationalPreset("gbt-50103-2010", "GB/T 50103-2010", "总图制图标准"),
  residentialNationalPreset("gb-50176-2016", "GB 50176-2016", "民用建筑热工设计规范"),
  residentialNationalPreset("jgj-t331-2014", "JGJ/T 331-2014", "建筑地面工程防滑技术规程"),
  nationalPreset("gb-55036-2022", "GB 55036-2022", "消防设施通用规范", ["通用建筑"], true, "各类建筑消防设施基础规范"),
  residentialNationalPreset("gb-50763-2012", "GB 50763-2012", "无障碍设计规范"),
  { id: "dbj-t45-095-2019", code: "DBJ/T45-095-2019", name: "居住建筑节能65%设计标准", level: "地方", region: "广西", buildingTypes: ["住宅建筑"], enabled: true, isPreset: true, note: "广西住宅项目地方预设，使用前按报审日期复核", sourceUrl: "" },
  nationalPreset("gb-55002-2021", "GB 55002-2021", "建筑与市政工程抗震通用规范", ["通用建筑"], true, "各类建筑常用强制性工程建设规范"),
  nationalPreset("gb-55020-2021", "GB 55020-2021", "建筑给水排水与节水通用规范", ["通用建筑"], true, "各类建筑给排水与节水基础规范"),
  nationalPreset("gb-55024-2022", "GB 55024-2022", "建筑电气与智能化通用规范", ["通用建筑"], true, "各类建筑电气与智能化基础规范"),
  nationalPreset("gb-55029-2022", "GB 55029-2022", "安全防范工程通用规范", ["通用建筑"], true, "各类公共与居住建筑安全防范基础规范"),
  nationalPreset("gb-55021-2021", "GB 55021-2021", "既有建筑鉴定与加固通用规范", ["既有建筑改造"], true),
  nationalPreset("gb-55022-2021", "GB 55022-2021", "既有建筑维护与改造通用规范", ["既有建筑改造"], true, "2022年4月1日起实施的既有建筑维护改造通用规范", "https://www.beijing.gov.cn/zhengce/zhengcefagui/qtwj/202204/t20220408_2669617.html"),
  nationalPreset("jgj-t67-2019", "JGJ/T 67-2019", "办公建筑设计标准", ["办公建筑"]),
  nationalPreset("jgj-48-2014", "JGJ 48-2014", "商店建筑设计规范", ["商业建筑"]),
  nationalPreset("gb-50099-2011", "GB 50099-2011", "中小学校设计规范", ["教育建筑"]),
  nationalPreset("jgj-39-2016", "JGJ 39-2016", "托儿所、幼儿园建筑设计规范", ["教育建筑"]),
  nationalPreset("gb-51039-2014", "GB 51039-2014", "综合医院建筑设计规范", ["医疗建筑"]),
  nationalPreset("gbt-51457-2024", "GB/T 51457-2024", "医院洁净护理与隔离单元建筑技术标准", ["医疗建筑"], true, "2024年9月1日起实施", "https://www.mohurd.gov.cn/file/2024/20240822/a015dc08-eaf2-474a-81c1-c4454e3b220c.pdf"),
  nationalPreset("gb-55025-2022", "GB 55025-2022", "宿舍、旅馆建筑项目规范", ["宿舍建筑", "旅馆建筑"], true, "2022年10月1日起实施的强制性工程建设规范", "https://www.mohurd.gov.cn/api-gateway/jpaas-web-server/front/document/download?fileName=2022%E5%B9%B4.pdf&fileUrl=YW5UzzlvCwcM%2FNHHX%2FtT6BJ6yM%2FAU1LVtmOVIvNjcuodKrYMbtEVykYUOoISn7rd1liJ3Xbku8CKj9rcuDHUCZSLTa80GxurpQoI49HVPRGjNZiCY1eAVyAKG8ipZL1CRnt22UAfsm96KZLwQjNf0g%3D%3D"),
  nationalPreset("jgj-62-2014", "JGJ 62-2014", "旅馆建筑设计规范", ["旅馆建筑"]),
  nationalPreset("gb-50867-2013", "GB 50867-2013", "养老设施建筑设计规范", ["养老建筑"]),
  nationalPreset("jgj-450-2018", "JGJ 450-2018", "老年人照料设施建筑设计标准", ["养老建筑"]),
  nationalPreset("jgj-100-2015", "JGJ 100-2015", "车库建筑设计规范", ["停车建筑"], true, "住房城乡建设部公告第788号发布"),
  nationalPreset("gb-50067-2014", "GB 50067-2014", "汽车库、修车库、停车场设计防火规范", ["停车建筑"]),
  nationalPreset("jgj-31-2003", "JGJ 31-2003", "体育建筑设计规范", ["文体建筑"], false, "文体项目参考预设，使用前重点核对现行性"),
  nationalPreset("jgj-66-2015", "JGJ 66-2015", "博物馆建筑设计规范", ["文体建筑"]),
  nationalPreset("jgj-57-2016", "JGJ 57-2016", "剧场建筑设计规范", ["文体建筑"]),
  nationalPreset("gb-50187-2012", "GB 50187-2012", "工业企业总平面设计规范", ["工业建筑"]),
  nationalPreset("gb-50073-2013", "GB 50073-2013", "洁净厂房设计规范", ["工业建筑"], false, "仅洁净厂房项目启用"),
  nationalPreset("gb-50072-2021", "GB 50072-2021", "冷库设计标准", ["工业建筑"], false, "仅冷库项目启用"),
];

export const mergeDesignStandards = (saved?: DesignStandard[]): DesignStandard[] => {
  const presets = defaultDesignStandards();
  if (!saved?.length) return presets;
  const savedById = new Map(saved.map((item) => [item.id, item]));
  const merged = presets.map((preset) => {
    const existing = savedById.get(preset.id);
    return existing ? { ...preset, ...existing, isPreset: true } : preset;
  });
  const presetIds = new Set(presets.map((item) => item.id));
  return [...merged, ...saved.filter((item) => !presetIds.has(item.id))];
};

export type CadLayoutProfile = {
  paperName: string;
  landscape: boolean;
  paperWidthMillimeters: number;
  paperHeightMillimeters: number;
  drawingScale: number;
  bodyTextHeightMillimeters: number;
  columnCount: number;
  columnGapMillimeters: number;
  frameBlockName: string;
  frameHandle: string;
  drawingPath: string;
  frameArea: CadRectangle;
  textArea: CadRectangle;
  textMarginsMillimeters: CadMargins;
};

export type CadRectangle = {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
};

export type CadMargins = {
  left: number;
  top: number;
  right: number;
  bottom: number;
};

export const defaultCadLayout = (): CadLayoutProfile => ({
  paperName: "A1",
  landscape: true,
  paperWidthMillimeters: 841,
  paperHeightMillimeters: 594,
  drawingScale: 100,
  bodyTextHeightMillimeters: 3.5,
  columnCount: 2,
  columnGapMillimeters: 12,
  frameBlockName: "",
  frameHandle: "",
  drawingPath: "",
  frameArea: { minX: 0, minY: 0, maxX: 84100, maxY: 59400 },
  textArea: { minX: 2500, minY: 2000, maxX: 65100, maxY: 57400 },
  textMarginsMillimeters: { left: 25, top: 20, right: 190, bottom: 20 },
});

export type ProfessionalTableType =
  | "technicalEconomicIndicators"
  | "waterproofDesign"
  | "interiorFinish"
  | "buildingSafetyMeasures"
  | "accessibilityFacilities";

export type ArchitectureTableColumn = {
  key: string;
  title: string;
  unit: string;
  widthMillimeters: number;
  decimalPlaces: number;
  required: boolean;
};

export type ArchitectureTableCell = {
  cellId: string;
  columnKey: string;
  displayValue: string;
  numericValue: number | null;
  unit: string;
  fieldPath: string;
  formula: string;
  state: FieldState;
  source: string;
  rowSpan: number;
  columnSpan: number;
};

export type ArchitectureTableRow = {
  rowId: string;
  rowType: "Data" | "Subtotal" | "Note";
  keepTogether: boolean;
  cells: ArchitectureTableCell[];
};

export type ArchitectureTable = {
  tableId: string;
  schemaVersion: 1;
  tableType: ProfessionalTableType;
  tableNumber: string;
  title: string;
  repeatHeader: boolean;
  allowSplitAcrossPages: boolean;
  columns: ArchitectureTableColumn[];
  rows: ArchitectureTableRow[];
  formulaAudits: TableFormulaAudit[];
};

export type TableFormulaAudit = {
  auditId: string;
  cellId: string;
  formula: string;
  formulaVersion: "1";
  inputs: Record<string, number>;
  result: number;
  calculatedAt: string;
  isManuallyOverridden: boolean;
  overrideReason: string;
};

export type ReviewArchiveRecord = {
  recordId: string;
  projectName: string;
  projectFingerprint: string;
  archivedAt: string;
  projectInfo: ReviewProjectSnapshot;
  reviewSignoff: ProjectReviewSignoff;
  result: RuleReviewResult;
  issueActions: ReviewIssueAction[];
};

export type ReviewProjectSnapshot = {
  location: string;
  buildingType: string;
  designStage: string;
  submissionDate: string;
};

export type ProjectReviewSignoff = {
  organization: string;
  projectManager: string;
  preparedBy: string;
  checkedBy: string;
  approvedBy: string;
  reportNumber: string;
};

export type ReviewIssueStatus =
  | "open"
  | "inProgress"
  | "resolved"
  | "acceptedRisk"
  | "notApplicable";

export type ReviewIssueAction = {
  issueId: string;
  status: ReviewIssueStatus;
  owner: string;
  comment: string;
  reviewer: string;
  updatedAt: string;
};

export type FieldChangeKind = "value" | "state" | "lock" | "source";

export type FieldChangeEntry = {
  id: string;
  fieldPath: string;
  fieldLabel: string;
  kind: FieldChangeKind;
  oldValue: string;
  newValue: string;
  changedAt: string;
  note: string;
};

export type ProjectJurisdiction = {
  country: string;
  province: string;
  city: string;
  district: string;
};

export type ProjectReviewProfile = {
  isSpecialConstruction: boolean;
  requiresFireReview: boolean;
  isHighRiseOrSpecial: boolean;
};

export type ProjectFeatures = {
  hasBasement: boolean;
  hasCurtainWall: boolean;
  hasElevator: boolean;
  hasCivilDefense: boolean;
  isGreenBuilding: boolean;
  isPrefabricated: boolean;
  hasSpecialistDesign: boolean;
};

export type ReviewItem = {
  id: string;
  level: "warning" | "info";
  title: string;
  detail: string;
  fieldPath?: string;
};

const paragraph = (text: string) => ({
  type: "paragraph",
  content: text ? [{ type: "text", text }] : undefined,
});

const fieldNode = (path: string, label: string, value: string, unit = "") => ({
  type: "projectField",
  attrs: { path, label, value, unit },
});

const standardNode = (code: string, name: string) => ({
  type: "standardCitation",
  attrs: { code, name },
});

function sectionDoc(
  title: string,
  content: Array<Record<string, unknown>>,
): Record<string, unknown> {
  return {
    type: "doc",
    content: [
      {
        type: "heading",
        attrs: { level: 2 },
        content: [{ type: "text", text: title }],
      },
      ...content,
    ],
  };
}

export function plainTextToSectionDocument(title: string, value: string): Record<string, unknown> {
  const paragraphs = value
    .replace(/\r\n/g, "\n")
    .split(/\n+/)
    .map((line) => line.trim())
    .filter(Boolean)
    .map(paragraph);
  return sectionDoc(title, paragraphs.length > 0 ? paragraphs : [paragraph("")]);
}

export function sectionDocumentToPlainText(value: unknown): string {
  const lines: string[] = [];
  const visit = (node: unknown) => {
    if (!node || typeof node !== "object") return;
    const item = node as Record<string, unknown>;
    if (item.type === "text" && typeof item.text === "string") {
      lines.push(item.text);
      return;
    }
    if (item.type === "projectField") {
      const attrs = item.attrs as Record<string, unknown> | undefined;
      lines.push(String(attrs?.value ?? attrs?.label ?? ""));
      return;
    }
    if (item.type === "standardCitation") {
      const attrs = item.attrs as Record<string, unknown> | undefined;
      lines.push(`${String(attrs?.name ?? "")}（${String(attrs?.code ?? "")}）`);
      return;
    }
    if (item.type === "professionalTable") {
      const table = (item.attrs as Record<string, unknown> | undefined)?.table as ArchitectureTable | undefined;
      if (table) {
        lines.push(`${table.tableNumber || ""} ${table.title}`.trim(), "\n");
        lines.push(table.columns.map((column) => column.title).join("\t"), "\n");
        table.rows.forEach((row) => {
          lines.push(table.columns.map((column) => row.cells.find((cell) => cell.columnKey === column.key)?.displayValue ?? "").join("\t"), "\n");
        });
      }
      return;
    }
    const content = Array.isArray(item.content) ? item.content : [];
    const start = lines.length;
    content.forEach(visit);
    if (["paragraph", "heading", "blockquote", "listItem", "list_item"].includes(String(item.type)) && lines.length > start) {
      lines.push("\n");
    }
  };
  visit(value);
  return lines.join("").replace(/\n{3,}/g, "\n\n").trim();
}

export function createInitialWorkspace(): EditorWorkspace {
  const fields: ProjectField[] = [
    {
      path: "project.projectName",
      label: "项目名称",
      value: "建筑设计说明助手示例办公项目",
      state: "confirmed",
      source: "项目立项信息",
    },
    {
      path: "project.location",
      label: "建设地点",
      value: "广西壮族自治区南宁市示例区",
      state: "confirmed",
      source: "示例项目任务书",
    },
    {
      path: "building.totalFloorArea",
      label: "总建筑面积",
      value: "36000.00",
      unit: "m²",
      state: "confirmed",
      source: "示例面积统计",
    },
    {
      path: "building.height",
      label: "建筑高度",
      value: "49.80",
      unit: "m",
      state: "confirmed",
      source: "示例立面图",
    },
    {
      path: "building.aboveGroundFloors",
      label: "地上层数",
      value: "12",
      unit: "层",
      state: "confirmed",
      source: "示例剖面图",
    },
    {
      path: "building.undergroundFloors",
      label: "地下层数",
      value: "1",
      unit: "层",
      state: "confirmed",
      source: "示例剖面图",
    },
    {
      path: "fire.classification",
      label: "建筑防火分类",
      value: "一类高层公共建筑（示例待复核）",
      state: "pending",
      source: "示例消防专篇",
    },
    {
      path: "fire.resistanceRating",
      label: "耐火等级",
      value: "一级（示例待复核）",
      state: "pending",
      source: "示例消防专篇",
    },
    {
      path: "waterproof.roofGrade",
      label: "屋面防水等级",
      value: "一级（示例待复核）",
      state: "pending",
      source: "示例防水表",
    },
    {
      path: "green.targetRating",
      label: "绿色建筑目标",
      value: "待建设单位确认",
      state: "unknown",
      source: "",
    },
  ];

  const byPath = (path: string) => fields.find((field) => field.path === path)!;
  const projectName = byPath("project.projectName");
  const location = byPath("project.location");
  const area = byPath("building.totalFloorArea");
  const height = byPath("building.height");

  const definitions: Array<[string, string, string, EditorSection["requirement"], Record<string, unknown>]> = [
    [
      "design-basis",
      "1",
      "设计依据",
      "required",
      sectionDoc("设计依据", [
        paragraph("本工程设计文件依据现行有效的法律法规、工程建设规范、项目批复文件及设计任务书编制。"),
        {
          type: "paragraph",
          content: [
            { type: "text", text: "国家基础规范索引：" },
            standardNode("GB 55031-2022", "民用建筑通用规范"),
            { type: "text", text: "。具体适用条文须由建筑专业人员复核。" },
          ],
        },
      ]),
    ],
    [
      "project-overview",
      "2",
      "项目概况",
      "required",
      sectionDoc("项目概况", [
        {
          type: "paragraph",
          content: [
            { type: "text", text: "项目名称：" },
            fieldNode(projectName.path, projectName.label, projectName.value),
            { type: "text", text: "；建设地点：" },
            fieldNode(location.path, location.label, location.value),
            { type: "text", text: "。" },
          ],
        },
        {
          type: "paragraph",
          content: [
            { type: "text", text: "项目总建筑面积为 " },
            fieldNode(area.path, area.label, area.value, area.unit),
            { type: "text", text: "，建筑高度为 " },
            fieldNode(height.path, height.label, height.value, height.unit),
            { type: "text", text: "。项目参数应以最终审批文件和各专业确认资料为准。" },
          ],
        },
      ]),
    ],
    ["technical-indicators", "3", "主要技术经济指标", "required", sectionDoc("主要技术经济指标", [paragraph("本节将在阶段2接入专业指标表。")])],
    ["elevation", "4", "设计标高", "required", sectionDoc("设计标高", [paragraph("请填写 ±0.000 对应绝对标高、高程系统及室内外高差。")])],
    ["general-layout", "5", "总平面建筑说明", "required", sectionDoc("总平面建筑说明", [paragraph("请说明基地概况、规划布局、竖向设计、交通组织及分期建设。")])],
    ["materials", "6", "建筑用料和装修构造", "required", sectionDoc("建筑用料和装修构造", [paragraph("请说明主要围护、装修材料及构造做法。")])],
    ["doors-curtain-wall", "7", "门窗与幕墙", "conditional", sectionDoc("门窗与幕墙", [paragraph("本项目涉及幕墙，需明确主体设计控制条件和专项设计责任边界。")])],
    ["waterproof", "8", "防水设计", "required", sectionDoc("防水设计", [paragraph("请分别说明屋面、地下室和涉水房间的防水等级及构造要求。")])],
    ["elevators", "9", "电梯和自动扶梯", "conditional", sectionDoc("电梯和自动扶梯", [paragraph("请填写电梯类型、服务楼层及建筑接口条件。")])],
    ["accessibility", "10", "无障碍设计", "required", sectionDoc("无障碍设计", [paragraph("请按设施清单和连续无障碍流线进行说明。")])],
    ["safety", "11", "建筑安全设计", "required", sectionDoc("建筑安全设计", [paragraph("请逐项核对临空防护、栏杆、玻璃、防坠落及检修安全措施。")])],
    ["fire", "12", "建筑防火设计", "required", sectionDoc("建筑防火设计", [paragraph("请填写建筑分类、耐火等级、防火分区、疏散及消防救援设施。")])],
    ["energy-green", "13", "建筑节能与绿色建筑", "required", sectionDoc("建筑节能与绿色建筑", [paragraph("节能计算结果必须来源于正式计算书，不由本工具推测。")])],
    ["specialist", "14", "专项深化设计责任边界", "conditional", sectionDoc("专项深化设计责任边界", [paragraph("请明确幕墙等专项设计的输入条件、输出文件、审核责任与提交节点。")])],
  ];

  return applyBuildingTemplate({
    schemaVersion: 1,
    projectName: projectName.value,
    location: location.value,
    buildingType: "办公建筑",
    designStage: "施工图设计",
    projectNature: "新建",
    features: {
      hasBasement: true,
      hasCurtainWall: true,
      hasElevator: true,
      hasCivilDefense: false,
      isGreenBuilding: true,
      isPrefabricated: false,
      hasSpecialistDesign: true,
    },
    lastSavedAt: null,
    fieldChanges: [],
    reviewRecords: [],
    tables: [],
    standards: defaultDesignStandards(),
    cadLayout: defaultCadLayout(),
    reviewSignoff: {
      organization: "",
      projectManager: "",
      preparedBy: "",
      checkedBy: "",
      approvedBy: "",
      reportNumber: "",
    },
    fields: fields.map((field) => ({
      ...field,
      locked: false,
      required: true,
      sourceType: "manual" as FieldSourceType,
      sourceDocumentId: "",
      enteredBy: "",
      confirmedAt: field.state === "confirmed" ? new Date().toISOString() : null,
      isManuallyOverridden: false,
      overrideReason: "",
    })),
    sections: definitions.map(([id, number, title, requirement, content]) => ({
      id,
      number,
      title,
      requirement,
      reviewState: id === "project-overview" || id === "design-basis" ? "ready" : "notReviewed",
      content,
    })),
  });
}

function replaceProjectFieldValues(value: unknown, values: ReadonlyMap<string, string>): unknown {
  if (Array.isArray(value)) {
    return value.map((item) => replaceProjectFieldValues(item, values));
  }
  if (!value || typeof value !== "object") {
    return value;
  }
  const node = value as Record<string, unknown>;
  const attrs = node.attrs as Record<string, unknown> | undefined;
  const path = attrs?.path;
  if (node.type === "projectField" && typeof path === "string" && values.has(path)) {
    return {
      ...node,
      attrs: {
        ...attrs,
        value: values.get(path) ?? "",
      },
    };
  }
  return Object.fromEntries(
    Object.entries(node).map(([key, child]) => [key, replaceProjectFieldValues(child, values)]),
  );
}

export function createBlankWorkspace(): EditorWorkspace {
  const sample = createInitialWorkspace();
  const values = new Map(sample.fields.map((field) => [field.path, ""]));
  return applyBuildingTemplate({
    ...sample,
    projectName: "",
    location: "",
    buildingType: "通用建筑",
    designStage: "施工图设计",
    projectNature: "新建",
    jurisdiction: {
      country: "中国",
      province: "",
      city: "",
      district: "",
    },
    submissionDate: "",
    features: {
      hasBasement: false,
      hasCurtainWall: false,
      hasElevator: false,
      hasCivilDefense: false,
      isGreenBuilding: false,
      isPrefabricated: false,
      hasSpecialistDesign: false,
    },
    lastSavedAt: null,
    fieldChanges: [],
    reviewRecords: [],
    tables: [],
    standards: defaultDesignStandards(),
    reviewSignoff: {
      organization: "",
      projectManager: "",
      preparedBy: "",
      checkedBy: "",
      approvedBy: "",
      reportNumber: "",
    },
    fields: sample.fields.map((field) => ({
      ...field,
      value: "",
      state: "unknown",
      source: "",
      locked: false,
      confirmedAt: null,
    })),
    sections: sample.sections.map((section) => ({
      ...section,
      reviewState: "notReviewed",
      content: replaceProjectFieldValues(section.content, values) as Record<string, unknown>,
    })),
  });
}

export function synchronizeWorkspaceFieldNodes(workspace: EditorWorkspace): EditorWorkspace {
  const values = new Map(workspace.fields.map((field) => [field.path, field.value]));
  return {
    ...workspace,
    sections: workspace.sections.map((section) => ({
      ...section,
      content: replaceProjectFieldValues(section.content, values) as Record<string, unknown>,
    })),
  };
}

export function normalizeWorkspace(workspace: EditorWorkspace): EditorWorkspace {
  const fallbackLayout = defaultCadLayout();
  const savedLayout = workspace.cadLayout;
  return applyBuildingTemplate({
    ...workspace,
    projectNature: workspace.projectNature ?? "新建",
    jurisdiction: workspace.jurisdiction ?? {
      country: "中国",
      province: "",
      city: "",
      district: "",
    },
    submissionDate: workspace.submissionDate ?? "",
    fieldChanges: workspace.fieldChanges ?? [],
    reviewRecords: (workspace.reviewRecords ?? []).map((record) => ({
      ...record,
      projectInfo: record.projectInfo ?? {
        location: workspace.location,
        buildingType: workspace.buildingType,
        designStage: workspace.designStage,
        submissionDate: workspace.submissionDate ?? "",
      },
      reviewSignoff: record.reviewSignoff ?? workspace.reviewSignoff ?? {
        organization: "",
        projectManager: "",
        preparedBy: "",
        checkedBy: "",
        approvedBy: "",
        reportNumber: "",
      },
      issueActions: record.issueActions ?? record.result.issues.map((issue) => ({
        issueId: issue.issueId,
        status: "open" as const,
        owner: "",
        comment: "",
        reviewer: "",
        updatedAt: record.archivedAt,
      })),
    })),
    tables: workspace.tables ?? [],
    standards: mergeDesignStandards(workspace.standards),
    cadLayout: {
      ...fallbackLayout,
      ...(savedLayout ?? {}),
      frameArea: { ...fallbackLayout.frameArea, ...(savedLayout?.frameArea ?? {}) },
      textArea: { ...fallbackLayout.textArea, ...(savedLayout?.textArea ?? {}) },
      textMarginsMillimeters: {
        ...fallbackLayout.textMarginsMillimeters,
        ...(savedLayout?.textMarginsMillimeters ?? {}),
      },
    },
    reviewSignoff: workspace.reviewSignoff ?? {
      organization: "",
      projectManager: "",
      preparedBy: "",
      checkedBy: "",
      approvedBy: "",
      reportNumber: "",
    },
    reviewProfile: workspace.reviewProfile ?? {
      isSpecialConstruction: false,
      requiresFireReview: false,
      isHighRiseOrSpecial: false,
    },
    features: {
      hasBasement: workspace.features?.hasBasement ?? true,
      hasCurtainWall: workspace.features?.hasCurtainWall ?? false,
      hasElevator: workspace.features?.hasElevator ?? false,
      hasCivilDefense: workspace.features?.hasCivilDefense ?? false,
      isGreenBuilding: workspace.features?.isGreenBuilding ?? false,
      isPrefabricated: workspace.features?.isPrefabricated ?? false,
      hasSpecialistDesign: workspace.features?.hasSpecialistDesign ?? false,
    },
    fields: workspace.fields.map((field) => ({
      ...field,
      locked: field.locked === true,
      required: field.required !== false,
      sourceType: field.sourceType ?? "manual",
      sourceDocumentId: field.sourceDocumentId ?? "",
      enteredBy: field.enteredBy ?? "",
      confirmedAt: field.confirmedAt ?? null,
      isManuallyOverridden: field.isManuallyOverridden === true,
      overrideReason: field.overrideReason ?? "",
    })),
  });
}

function stableSerialize(value: unknown): string {
  if (Array.isArray(value)) {
    return `[${value.map(stableSerialize).join(",")}]`;
  }
  if (value && typeof value === "object") {
    return `{${Object.entries(value as Record<string, unknown>)
      .filter(([key]) =>
        key !== "reviewRecords" &&
        key !== "reviewSignoff" &&
        key !== "lastSavedAt")
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([key, child]) => `${JSON.stringify(key)}:${stableSerialize(child)}`)
      .join(",")}}`;
  }
  return JSON.stringify(value) ?? "null";
}

export function getReviewFingerprint(workspace: EditorWorkspace): string {
  const serialized = stableSerialize(workspace);
  let hash = 0x811c9dc5;
  for (let index = 0; index < serialized.length; index += 1) {
    hash ^= serialized.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193);
  }
  return `fnv1a32-${(hash >>> 0).toString(16).padStart(8, "0")}`;
}

export function createReviewArchiveRecord(
  workspace: EditorWorkspace,
  result: RuleReviewResult,
): ReviewArchiveRecord {
  const generatedReportNumber = `BPP-JZYS-${(result.executedAt || new Date().toISOString())
    .slice(0, 10)
    .replace(/-/g, "")}-${(globalThis.crypto?.randomUUID?.() ?? `${Date.now()}`)
    .replace(/-/g, "")
    .slice(0, 8)
    .toUpperCase()}`;
  return {
    recordId: globalThis.crypto?.randomUUID?.() ?? `review-${Date.now()}`,
    projectName: workspace.projectName,
    projectFingerprint: getReviewFingerprint(workspace),
    archivedAt: result.executedAt || new Date().toISOString(),
    projectInfo: {
      location: workspace.location,
      buildingType: workspace.buildingType,
      designStage: workspace.designStage,
      submissionDate: workspace.submissionDate ?? "",
    },
    reviewSignoff: {
      organization: workspace.reviewSignoff?.organization ?? "",
      projectManager: workspace.reviewSignoff?.projectManager ?? "",
      preparedBy: workspace.reviewSignoff?.preparedBy ?? "",
      checkedBy: workspace.reviewSignoff?.checkedBy ?? "",
      approvedBy: workspace.reviewSignoff?.approvedBy ?? "",
      reportNumber: workspace.reviewSignoff?.reportNumber.trim() || generatedReportNumber,
    },
    result,
    issueActions: result.issues.map((issue) => ({
      issueId: issue.issueId,
      status: "open",
      owner: "",
      comment: "",
      reviewer: "",
      updatedAt: result.executedAt || new Date().toISOString(),
    })),
  };
}

export function archiveReviewResult(
  workspace: EditorWorkspace,
  result: RuleReviewResult,
): EditorWorkspace {
  const record = createReviewArchiveRecord(workspace, result);
  return {
    ...workspace,
    reviewRecords: [...(workspace.reviewRecords ?? []), record].slice(-100),
  };
}

export function updateReviewIssueAction(
  record: ReviewArchiveRecord,
  issueId: string,
  patch: Partial<Omit<ReviewIssueAction, "issueId">>,
): ReviewArchiveRecord {
  const existing = record.issueActions.find((action) => action.issueId === issueId) ?? {
    issueId,
    status: "open" as const,
    owner: "",
    comment: "",
    reviewer: "",
    updatedAt: record.archivedAt,
  };
  const updated: ReviewIssueAction = {
    ...existing,
    ...patch,
    issueId,
    updatedAt: new Date().toISOString(),
  };
  return {
    ...record,
    issueActions: [
      ...record.issueActions.filter((action) => action.issueId !== issueId),
      updated,
    ],
  };
}

const alwaysRequiredSections = new Set([
  "design-basis",
  "project-overview",
  "technical-indicators",
  "elevation",
  "general-layout",
  "materials",
  "waterproof",
  "accessibility",
  "safety",
  "fire",
  "energy-green",
]);
const constructionDetailSections = new Set(["materials", "waterproof", "safety"]);

export function applyBuildingTemplate(workspace: EditorWorkspace): EditorWorkspace {
  const features = workspace.features ?? {
    hasBasement: false,
    hasCurtainWall: false,
    hasElevator: false,
    hasCivilDefense: false,
    isGreenBuilding: false,
    isPrefabricated: false,
    hasSpecialistDesign: false,
  };
  const elevatorTypes = new Set(["住宅建筑", "办公建筑", "商业建筑", "教育建筑", "医疗建筑", "交通建筑"]);
  const curtainWallByType = new Set(["办公建筑", "商业建筑", "交通建筑", "文体建筑"]);

  const sections = workspace.sections.map((section) => {
    if (alwaysRequiredSections.has(section.id)) {
      if (workspace.designStage === "方案设计" && constructionDetailSections.has(section.id)) {
        return {
          ...section,
          requirement: "optional" as const,
          enabled: true,
          activationReason: "方案设计阶段可选，后续阶段转为必填",
        };
      }
      return { ...section, requirement: "required" as const, enabled: true, activationReason: "基础必填章节" };
    }
    if (section.id === "doors-curtain-wall") {
      const enabled = features.hasCurtainWall || curtainWallByType.has(workspace.buildingType);
      return {
        ...section,
        requirement: "conditional" as const,
        enabled,
        activationReason: enabled ? "建筑类型或项目条件涉及幕墙" : "项目未登记幕墙条件",
      };
    }
    if (section.id === "elevators") {
      const enabled = features.hasElevator || elevatorTypes.has(workspace.buildingType);
      return {
        ...section,
        requirement: "conditional" as const,
        enabled,
        activationReason: enabled ? "建筑类型或项目条件涉及电梯" : "项目未登记电梯条件",
      };
    }
    if (section.id === "specialist") {
      const enabled = features.hasSpecialistDesign || features.hasCurtainWall;
      return {
        ...section,
        requirement: "conditional" as const,
        enabled,
        activationReason: enabled ? "项目存在专项深化设计" : "项目未登记专项深化设计",
      };
    }
    return { ...section, enabled: true, activationReason: "用户可选章节" };
  });

  const fields = workspace.fields.map((field) => {
    let required = true;
    if (field.path === "green.targetRating") {
      required = features.isGreenBuilding;
    } else if (field.path === "waterproof.roofGrade") {
      required = workspace.buildingType !== "工业建筑" || features.hasBasement;
    }
    return { ...field, required };
  });

  return { ...workspace, sections, fields };
}

export function recordFieldChange(
  workspace: EditorWorkspace,
  fieldPath: string,
  kind: FieldChangeKind,
  oldValue: string,
  newValue: string,
  note = "",
): EditorWorkspace {
  if (oldValue === newValue) return workspace;
  const field = workspace.fields.find((item) => item.path === fieldPath);
  if (!field) return workspace;
  const now = new Date();
  const changes = [...(workspace.fieldChanges ?? [])];
  const last = changes[changes.length - 1];
  const canCoalesce = last?.fieldPath === fieldPath &&
    last.kind === kind &&
    last.note === note &&
    now.getTime() - new Date(last.changedAt).getTime() < 5 * 60 * 1000;
  if (canCoalesce) {
    changes[changes.length - 1] = {
      ...last,
      newValue,
      changedAt: now.toISOString(),
      note: note || last.note,
    };
  } else {
    changes.push({
      id: `${now.getTime()}-${changes.length}-${fieldPath}`,
      fieldPath,
      fieldLabel: field.label,
      kind,
      oldValue,
      newValue,
      changedAt: now.toISOString(),
      note,
    });
  }
  return { ...workspace, fieldChanges: changes.slice(-1000) };
}

export function getWorkspaceIssues(workspace: EditorWorkspace): ReviewItem[] {
  const issues: ReviewItem[] = workspace.fields
    .filter((field) =>
      field.required !== false &&
      field.state !== "confirmed" &&
      field.state !== "notApplicable")
    .map((field) => ({
      id: `field:${field.path}`,
      level: field.state === "unknown" ? "warning" : "info",
      title: `${field.label}${field.state === "unknown" ? "尚未确认" : "待复核"}`,
      detail: field.source ? `当前来源：${field.source}` : "尚未登记数据来源。",
      fieldPath: field.path,
    }));

  const emptySections = workspace.sections.filter((section) => {
    const content = section.content.content;
    return section.enabled !== false &&
      section.requirement === "required" &&
      (!Array.isArray(content) || content.length < 2);
  });
  for (const section of emptySections) {
    issues.push({
      id: `section:${section.id}`,
      level: "warning",
      title: `${section.number} ${section.title} 内容不完整`,
      detail: "必填章节尚未形成有效正文。",
    });
  }

  const valueOf = (path: string) =>
    workspace.fields.find((field) => field.path === path)?.value.trim() ?? "";
  const underground = Number(valueOf("building.undergroundFloors"));
  if (workspace.features?.hasBasement === false && Number.isFinite(underground) && underground > 0) {
    issues.push({
      id: "conflict:basement-disabled",
      level: "warning",
      title: "地下室条件与地下层数矛盾",
      detail: `项目条件为“无地下室”，但地下层数填写为 ${underground}。`,
      fieldPath: "building.undergroundFloors",
    });
  }
  if (workspace.features?.hasBasement === true && underground === 0) {
    issues.push({
      id: "conflict:basement-zero-floor",
      level: "warning",
      title: "地下室条件与地下层数矛盾",
      detail: "项目条件为“设有地下室”，但地下层数填写为 0。",
      fieldPath: "building.undergroundFloors",
    });
  }
  if (workspace.projectName.trim() !== valueOf("project.projectName")) {
    issues.push({
      id: "conflict:project-name",
      level: "warning",
      title: "项目名称存在两个不同值",
      detail: "项目元数据与项目字段中的名称不一致。",
      fieldPath: "project.projectName",
    });
  }
  if (workspace.location.trim() !== valueOf("project.location")) {
    issues.push({
      id: "conflict:project-location",
      level: "warning",
      title: "建设地点存在两个不同值",
      detail: "项目元数据与项目字段中的建设地点不一致。",
      fieldPath: "project.location",
    });
  }
  const greenField = workspace.fields.find((field) => field.path === "green.targetRating");
  if (workspace.features?.isGreenBuilding === false &&
      greenField?.state === "confirmed" &&
      greenField.value.trim()) {
    issues.push({
      id: "conflict:green-target",
      level: "warning",
      title: "绿色建筑条件与目标等级矛盾",
      detail: "项目条件未启用绿色建筑，但绿色建筑目标已确认。",
      fieldPath: "green.targetRating",
    });
  }
  for (const field of workspace.fields.filter((item) =>
    item.locked && item.state !== "confirmed" && item.state !== "notApplicable")) {
    issues.push({
      id: `conflict:locked-state:${field.path}`,
      level: "warning",
      title: `${field.label}的锁定状态矛盾`,
      detail: `字段已锁定，但当前状态为“${field.state}”。`,
      fieldPath: field.path,
    });
  }

  return issues;
}

export function updateFieldNodes(
  document: ProseMirrorNode,
  path: string,
  value: string,
): Array<{ position: number; node: ProseMirrorNode }> {
  const matches: Array<{ position: number; node: ProseMirrorNode }> = [];
  document.descendants((node, position) => {
    if (node.type.name === "projectField" && node.attrs.path === path && node.attrs.value !== value) {
      matches.push({ position, node });
    }
  });
  return matches;
}
