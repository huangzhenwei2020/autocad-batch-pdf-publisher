import { useEffect, useMemo, useRef, useState } from "react";
import {
  applyBuildingTemplate,
  createReviewArchiveRecord,
  createInitialWorkspace,
  getReviewFingerprint,
  getWorkspaceIssues,
  normalizeWorkspace,
  recordFieldChange,
  updateReviewIssueAction,
  type EditorWorkspace,
  type FieldChangeEntry,
  type FieldSourceType,
  type ProjectField,
  type ProjectReviewSignoff,
  type ReviewArchiveRecord,
  type ReviewIssueAction,
} from "./editor-model";
import {
  editorCommands,
  ProseMirrorEditor,
  type ProseMirrorEditorHandle,
} from "./prosemirror-editor";
import { ProjectWizard } from "./project-wizard";
import {
  ProjectHistory,
  type ProjectSnapshotInfo,
} from "./project-history";
import {
  RuleReviewReport,
  type RuleReviewIssue,
  type RuleReviewResult,
} from "./rule-review";
import { ReviewHistory } from "./review-history";
import {
  compareReviewRecords,
  ReviewComparison,
  type ReviewComparisonResult,
} from "./review-comparison";
import { ReviewSignoffSettings } from "./review-signoff";
import { ProfessionalTableEditor } from "./professional-table-editor";

export const protocolVersion = 1;
const storageKey = "cad-arch-spec-editor.workspace.v1";

type HostReadyPayload = {
  productName: string;
  productVersion: string;
  runtimeVersion: string;
  webView2Version: string;
  currentProjectPath?: string;
  recentProjects?: string[];
};

type HostMessage = {
  protocolVersion: number;
  type: string;
  payload: Record<string, unknown>;
};

type WebViewBridge = {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: MessageEvent<HostMessage>) => void): void;
  removeEventListener(type: "message", listener: (event: MessageEvent<HostMessage>) => void): void;
};

declare global {
  interface Window {
    chrome?: {
      webview?: WebViewBridge;
    };
  }
}

function createMessageId() {
  return globalThis.crypto?.randomUUID?.() ?? `editor-${Date.now()}`;
}

export function createReadyMessage(messageId: string) {
  return {
    protocolVersion,
    messageId,
    type: "editor.ready",
    payload: { phase: 2 },
  };
}

export function createProjectMessage(
  type:
    | "project.new"
    | "project.open"
    | "project.openRecent"
    | "project.save"
    | "project.saveAs"
    | "project.historyList"
    | "project.historyLoad"
    | "project.historyRestore"
    | "review.run",
  payload: Record<string, unknown> = {},
) {
  return { protocolVersion, messageId: createMessageId(), type, payload };
}

export function loadStoredWorkspace(storage: Pick<Storage, "getItem">): EditorWorkspace {
  const stored = storage.getItem(storageKey);
  if (!stored) {
    return createInitialWorkspace();
  }
  try {
    const parsed = JSON.parse(stored) as EditorWorkspace;
    if (parsed.schemaVersion === 1 && Array.isArray(parsed.sections) && parsed.sections.length > 0) {
      return normalizeWorkspace(parsed);
    }
  } catch {
    // 损坏的本地草稿不阻止编辑器启动。
  }
  return createInitialWorkspace();
}

function updateFieldNodesInJson(
  value: unknown,
  fieldPath: string,
  fieldValue: string,
): unknown {
  if (Array.isArray(value)) {
    return value.map((item) => updateFieldNodesInJson(item, fieldPath, fieldValue));
  }
  if (!value || typeof value !== "object") {
    return value;
  }

  const node = value as Record<string, unknown>;
  if (node.type === "projectField") {
    const attrs = node.attrs as Record<string, unknown> | undefined;
    if (attrs?.path === fieldPath) {
      return {
        ...node,
        attrs: {
          ...attrs,
          value: fieldValue,
        },
      };
    }
  }

  return Object.fromEntries(
    Object.entries(node).map(([key, child]) => [
      key,
      updateFieldNodesInJson(child, fieldPath, fieldValue),
    ]),
  );
}

function saveWorkspace(workspace: EditorWorkspace): EditorWorkspace {
  const saved = {
    ...workspace,
    lastSavedAt: new Date().toISOString(),
  };
  localStorage.setItem(storageKey, JSON.stringify(saved));
  return saved;
}

function downloadWorkspace(workspace: EditorWorkspace) {
  const blob = new Blob([JSON.stringify(workspace, null, 2)], {
    type: "application/json;charset=utf-8",
  });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = `${workspace.projectName || "建筑设计说明项目"}.json`;
  anchor.click();
  URL.revokeObjectURL(url);
}

function stateLabel(field: ProjectField) {
  switch (field.state) {
    case "confirmed":
      return "已确认";
    case "pending":
      return "待复核";
    case "providedByOtherDiscipline":
      return "其他专业提供";
    case "providedBySpecialist":
      return "专项单位提供";
    case "notApplicable":
      return "不适用";
    case "overridden":
      return "人工覆盖";
    default:
      return "待确认";
  }
}

function changeKindLabel(change: FieldChangeEntry) {
  switch (change.kind) {
    case "value": return "字段值";
    case "state": return "确认状态";
    case "lock": return "锁定状态";
    case "source": return "数据来源";
  }
}

function metadataChangeLabel(key: string) {
  const labels: Record<string, string> = {
    state: "字段状态",
    locked: "锁定状态",
    sourceType: "来源类型",
    source: "来源说明",
    sourceDocumentId: "来源文件/编号",
    enteredBy: "录入/确认人",
    isManuallyOverridden: "人工覆盖",
    overrideReason: "覆盖原因",
  };
  return labels[key] ?? key;
}

function metadataChangeValue(key: string, value: unknown) {
  if (key === "locked") return value ? "已锁定" : "未锁定";
  if (key === "isManuallyOverridden") return value ? "是" : "否";
  if (key === "state") {
    const labels: Record<string, string> = {
      confirmed: "已确认",
      pending: "待复核",
      unknown: "待确认",
      notApplicable: "不适用",
      providedByOtherDiscipline: "其他专业提供",
      providedBySpecialist: "专项单位提供",
      overridden: "人工覆盖",
    };
    return labels[String(value)] ?? String(value ?? "");
  }
  return value == null ? "" : String(value);
}

export function ArchitectureSpecEditor() {
  const [workspace, setWorkspace] = useState(() => loadStoredWorkspace(localStorage));
  const [selectedSectionId, setSelectedSectionId] = useState(workspace.sections[0].id);
  const [host, setHost] = useState<HostReadyPayload | null>(null);
  const [currentProjectPath, setCurrentProjectPath] = useState("");
  const [recentProjects, setRecentProjects] = useState<string[]>([]);
  const [projectNotice, setProjectNotice] = useState("");
  const [editorHandle, setEditorHandle] = useState<ProseMirrorEditorHandle | null>(null);
  const [fieldToInsert, setFieldToInsert] = useState(workspace.fields[0].path);
  const [searchText, setSearchText] = useState("");
  const [replacementText, setReplacementText] = useState("");
  const [saveState, setSaveState] = useState<"saved" | "dirty">("saved");
  const [editorRevision, setEditorRevision] = useState(0);
  const [leftOpen, setLeftOpen] = useState(false);
  const [rightOpen, setRightOpen] = useState(false);
  const [conditionsOpen, setConditionsOpen] = useState(false);
  const [wizardOpen, setWizardOpen] = useState(false);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [versionHistoryOpen, setVersionHistoryOpen] = useState(false);
  const [snapshots, setSnapshots] = useState<ProjectSnapshotInfo[]>([]);
  const [selectedSnapshotPath, setSelectedSnapshotPath] = useState("");
  const [snapshotWorkspace, setSnapshotWorkspace] = useState<EditorWorkspace | null>(null);
  const [snapshotLoading, setSnapshotLoading] = useState(false);
  const [ruleReview, setRuleReview] = useState<RuleReviewResult | null>(null);
  const [ruleReportOpen, setRuleReportOpen] = useState(false);
  const [reviewHistoryOpen, setReviewHistoryOpen] = useState(false);
  const [selectedReviewRecord, setSelectedReviewRecord] = useState<ReviewArchiveRecord | null>(null);
  const [reviewComparison, setReviewComparison] = useState<ReviewComparisonResult | null>(null);
  const [signoffOpen, setSignoffOpen] = useState(false);
  const [tablesOpen, setTablesOpen] = useState(false);
  const [reviewRunning, setReviewRunning] = useState(false);
  const [editingFieldPath, setEditingFieldPath] = useState<string | null>(null);
  const autoSaveTimer = useRef<number | null>(null);
  const workspaceRef = useRef(workspace);
  const pendingReviewWorkspace = useRef<EditorWorkspace | null>(null);

  useEffect(() => {
    workspaceRef.current = workspace;
  }, [workspace]);

  const selectedSection =
    workspace.sections.find((section) => section.id === selectedSectionId) ??
    workspace.sections[0];
  const issues = useMemo(() => {
    const local = getWorkspaceIssues(workspace);
    const fromRules = (ruleReview?.issues ?? []).map((issue) => ({
      id: `rule:${issue.ruleId}`,
      level: issue.severity === "blocker" || issue.severity === "error" ? "warning" as const : "info" as const,
      title: issue.title,
      detail: issue.message,
      fieldPath: issue.targetFieldPath || undefined,
      sectionId: issue.targetNodeId || undefined,
    }));
    return [...local.map((issue) => ({ ...issue, sectionId: undefined as string | undefined })), ...fromRules];
  }, [workspace, ruleReview]);
  const confirmedCount = workspace.fields.filter((field) => field.state === "confirmed").length;
  const completeness = Math.round((confirmedCount / workspace.fields.length) * 100);

  useEffect(() => {
    const bridge = window.chrome?.webview;
    if (!bridge) {
      return;
    }
    const onMessage = (event: MessageEvent<HostMessage>) => {
      if (event.data?.protocolVersion !== protocolVersion) {
        return;
      }
      const payload = event.data.payload;
      if (event.data.type === "host.ready") {
        setHost(payload as HostReadyPayload);
        setCurrentProjectPath(String(payload.currentProjectPath ?? ""));
        setRecentProjects((payload.recentProjects as string[] | undefined) ?? []);
      } else if (event.data.type === "project.loaded") {
        const loaded = normalizeWorkspace(payload.workspace as EditorWorkspace);
        setWorkspace(loaded);
        setSelectedSectionId(loaded.sections[0].id);
        setFieldToInsert(loaded.fields[0].path);
        setEditorRevision((revision) => revision + 1);
        setCurrentProjectPath(String(payload.filePath ?? ""));
        setRecentProjects((payload.recentProjects as string[] | undefined) ?? []);
        setSaveState("saved");
        setProjectNotice("项目已打开");
        setRuleReview(null);
      } else if (event.data.type === "project.saved") {
        const savedAt = String(payload.savedAt ?? new Date().toISOString());
        setWorkspace((current) => ({ ...current, lastSavedAt: savedAt }));
        setCurrentProjectPath(String(payload.filePath ?? ""));
        setRecentProjects((payload.recentProjects as string[] | undefined) ?? []);
        setSaveState("saved");
        setProjectNotice(payload.snapshotPath ? "项目已保存，并创建版本快照" : "项目已保存");
      } else if (event.data.type === "project.newed") {
        setCurrentProjectPath("");
        setRecentProjects((payload.recentProjects as string[] | undefined) ?? []);
        setRuleReview(null);
      } else if (event.data.type === "project.historyListed") {
        setSnapshots((payload.snapshots as ProjectSnapshotInfo[] | undefined) ?? []);
        setSelectedSnapshotPath("");
        setSnapshotWorkspace(null);
        setSnapshotLoading(false);
        setVersionHistoryOpen(true);
      } else if (event.data.type === "project.historyLoaded") {
        setSnapshotWorkspace(normalizeWorkspace(payload.workspace as EditorWorkspace));
        setSelectedSnapshotPath(String(payload.snapshotPath ?? ""));
        setSnapshotLoading(false);
      } else if (event.data.type === "project.historyRestored") {
        const restored = normalizeWorkspace(payload.workspace as EditorWorkspace);
        setWorkspace(restored);
        setSelectedSectionId(restored.sections[0].id);
        setFieldToInsert(restored.fields[0].path);
        setEditorRevision((revision) => revision + 1);
        setSaveState("saved");
        setSnapshots((payload.snapshots as ProjectSnapshotInfo[] | undefined) ?? []);
        setSnapshotWorkspace(null);
        setSelectedSnapshotPath("");
        setSnapshotLoading(false);
        setProjectNotice("历史版本已恢复；恢复前版本已自动创建安全快照");
      } else if (event.data.type === "review.result") {
        const result = payload as unknown as RuleReviewResult;
        const reviewedWorkspace = pendingReviewWorkspace.current ?? workspaceRef.current;
        const record = createReviewArchiveRecord(reviewedWorkspace, result);
        setRuleReview(result);
        setWorkspace((current) => ({
          ...current,
          reviewRecords: [...(current.reviewRecords ?? []), record].slice(-100),
        }));
        setSaveState("dirty");
        setSelectedReviewRecord(record);
        pendingReviewWorkspace.current = null;
        setReviewRunning(false);
        setRuleReportOpen(true);
        setProjectNotice("预审完成，记录已加入项目；请保存项目文件");
      } else if (event.data.type === "project.error") {
        setSnapshotLoading(false);
        setReviewRunning(false);
        window.alert(`项目操作失败：${String(payload.message ?? "未知错误")}`);
      }
    };
    bridge.addEventListener("message", onMessage);
    bridge.postMessage(createReadyMessage(createMessageId()));
    return () => bridge.removeEventListener("message", onMessage);
  }, []);

  useEffect(() => {
    if (saveState !== "dirty") {
      return;
    }
    if (autoSaveTimer.current) {
      window.clearTimeout(autoSaveTimer.current);
    }
    autoSaveTimer.current = window.setTimeout(() => {
      setWorkspace((current) => saveWorkspace(current));
      setSaveState("saved");
    }, 1200);
    return () => {
      if (autoSaveTimer.current) {
        window.clearTimeout(autoSaveTimer.current);
      }
    };
  }, [workspace, saveState]);

  const changeWorkspace = (update: (current: EditorWorkspace) => EditorWorkspace) => {
    setWorkspace(update);
    setSaveState("dirty");
    setRuleReview(null);
    setRuleReportOpen(false);
  };

  const updateField = (path: string, value: string) => {
    if (workspace.fields.find((field) => field.path === path)?.locked) {
      return;
    }
    changeWorkspace((current) => {
      const field = current.fields.find((item) => item.path === path);
      if (!field || field.locked) return current;
      const updated = {
        ...current,
        projectName: path === "project.projectName" ? value : current.projectName,
        location: path === "project.location" ? value : current.location,
        fields: current.fields.map((item) =>
          item.path === path ? { ...item, value, state: "pending" as const } : item,
        ),
        sections: current.sections.map((section) => ({
          ...section,
          content: updateFieldNodesInJson(section.content, path, value) as Record<string, unknown>,
        })),
      };
      return recordFieldChange(updated, path, "value", field.value, value, "用户修改字段值");
    });
    setEditorRevision((revision) => revision + 1);
  };

  const updateFieldMetadata = (path: string, patch: Partial<ProjectField>) => {
    changeWorkspace((current) => {
      const field = current.fields.find((item) => item.path === path);
      if (!field) return current;
      let updated: EditorWorkspace = {
        ...current,
        fields: current.fields.map((item) =>
          item.path === path ? { ...item, ...patch } : item,
        ),
      };
      for (const [key, next] of Object.entries(patch)) {
        if (key === "confirmedAt") continue;
        const previous = field[key as keyof ProjectField];
        const oldValue = metadataChangeValue(key, previous);
        const newValue = metadataChangeValue(key, next);
        const kind = key === "state" ? "state" : key === "locked" ? "lock" : "source";
        updated = recordFieldChange(updated, path, kind, oldValue, newValue, `修改${metadataChangeLabel(key)}`);
      }
      return updated;
    });
  };

  const postProjectMessage = (
    type:
      | "project.new"
      | "project.open"
      | "project.openRecent"
      | "project.save"
      | "project.saveAs"
      | "project.historyList"
      | "project.historyLoad"
      | "project.historyRestore"
      | "review.run",
    payload: Record<string, unknown> = {},
  ) => window.chrome?.webview?.postMessage(createProjectMessage(type, payload));

  const replaceWorkspace = (next: EditorWorkspace) => {
    const normalized = normalizeWorkspace(next);
    setWorkspace(normalized);
    setSelectedSectionId(normalized.sections[0].id);
    setFieldToInsert(normalized.fields[0].path);
    setEditorRevision((revision) => revision + 1);
    setSaveState("saved");
  };

  const openVersionHistory = () => {
    if (!window.chrome?.webview) {
      window.alert("版本历史需要在 AutoCAD 宿主中使用。");
      return;
    }
    if (!currentProjectPath) {
      window.alert("请先保存项目文件，再创建或查看快照。");
      return;
    }
    setSnapshotLoading(true);
    postProjectMessage("project.historyList");
  };

  const loadSnapshotForComparison = (snapshotPath: string) => {
    setSelectedSnapshotPath(snapshotPath);
    setSnapshotWorkspace(null);
    setSnapshotLoading(true);
    postProjectMessage("project.historyLoad", { snapshotPath });
  };

  const restoreSelectedSnapshot = (snapshotPath: string) => {
    if (!window.confirm("确定恢复所选历史版本吗？当前项目会先自动创建安全快照。")) return;
    setSnapshotLoading(true);
    postProjectMessage("project.historyRestore", { snapshotPath });
  };

  const runFoundationReview = () => {
    if (!window.chrome?.webview) {
      window.alert("国家基础规则预审需要在 AutoCAD 宿主中运行。");
      return;
    }
    pendingReviewWorkspace.current = workspace;
    setReviewRunning(true);
    postProjectMessage("review.run", { workspace });
  };

  const openArchivedReview = (record: ReviewArchiveRecord) => {
    setRuleReview(record.result);
    setSelectedReviewRecord(record);
    setReviewHistoryOpen(false);
    setRuleReportOpen(true);
  };

  const openReviewComparison = (
    first: ReviewArchiveRecord,
    second: ReviewArchiveRecord,
  ) => {
    setReviewComparison(compareReviewRecords(first, second));
    setReviewHistoryOpen(false);
  };

  const updateSelectedReviewAction = (
    issueId: string,
    patch: Partial<Omit<ReviewIssueAction, "issueId">>,
  ) => {
    if (!selectedReviewRecord) return;
    const updatedRecord = updateReviewIssueAction(selectedReviewRecord, issueId, patch);
    setSelectedReviewRecord(updatedRecord);
    setWorkspace((current) => ({
      ...current,
      reviewRecords: (current.reviewRecords ?? []).map((record) =>
        record.recordId === updatedRecord.recordId ? updatedRecord : record),
    }));
    setSaveState("dirty");
    setProjectNotice("问题处理记录已修改，请保存项目文件");
  };

  const saveReviewSignoff = (value: ProjectReviewSignoff) => {
    setWorkspace((current) => ({ ...current, reviewSignoff: value }));
    setSaveState("dirty");
    setProjectNotice("报告签发设置已修改，请保存项目文件");
    setSignoffOpen(false);
  };

  const locateRuleIssue = (issue: RuleReviewIssue) => {
    if (issue.targetFieldPath) {
      setRuleReportOpen(false);
      setRightOpen(true);
      window.setTimeout(() => {
        document.getElementById(`field-${issue.targetFieldPath}`)?.scrollIntoView({
          behavior: "smooth",
          block: "center",
        });
      }, 50);
    } else if (issue.targetNodeId) {
      setSelectedSectionId(issue.targetNodeId);
      setRuleReportOpen(false);
    }
  };

  const handleNewProject = () => {
    if (!window.confirm("新建项目将清空当前未保存的编辑内容，是否继续？")) {
      return;
    }
    setWizardOpen(true);
  };

  const completeNewProject = (next: EditorWorkspace) => {
    replaceWorkspace(next);
    saveWorkspace(next);
    setCurrentProjectPath("");
    postProjectMessage("project.new");
    setProjectNotice("项目向导已完成，请保存为项目文件");
    setWizardOpen(false);
  };

  const handleSaveProject = (saveAs = false, createSnapshot = false) => {
    if (window.chrome?.webview) {
      postProjectMessage(saveAs ? "project.saveAs" : "project.save", {
        workspace,
        createSnapshot,
      });
      setProjectNotice("正在保存项目…");
      return;
    }
    setWorkspace((current) => saveWorkspace(current));
    downloadWorkspace(workspace);
    setSaveState("saved");
  };

  const updateProjectMetadata = (
    property: "projectName" | "location" | "buildingType" | "designStage",
    value: string,
  ) => {
    const fieldPath =
      property === "projectName"
        ? "project.projectName"
        : property === "location"
          ? "project.location"
          : null;
    if (fieldPath) {
      updateField(fieldPath, value);
      return;
    }
    changeWorkspace((current) =>
      applyBuildingTemplate({ ...current, [property]: value }),
    );
  };

  const updateProjectCondition = (
    name: keyof NonNullable<EditorWorkspace["features"]>,
    checked: boolean,
  ) => {
    changeWorkspace((current) =>
      applyBuildingTemplate({
        ...current,
        features: {
          ...current.features!,
          [name]: checked,
        },
      }),
    );
  };

  const handleSectionChange = (sectionId: string) => {
    setSelectedSectionId(sectionId);
    setLeftOpen(false);
  };

  const insertSelectedField = () => {
    const field = workspace.fields.find((item) => item.path === fieldToInsert);
    if (field) {
      editorHandle?.insertField(field);
    }
  };

  const handleReplaceAll = () => {
    const count = editorHandle?.replaceAll(searchText, replacementText) ?? 0;
    if (count === 0 && searchText) {
      window.alert("当前章节未找到匹配文字。");
    }
  };

  const handleReset = () => {
    if (!window.confirm("确定恢复阶段1示例文档吗？当前本地草稿将被替换。")) {
      return;
    }
    const sample = createInitialWorkspace();
    setWorkspace(saveWorkspace(sample));
    setSelectedSectionId(sample.sections[0].id);
    setEditorRevision((revision) => revision + 1);
    setSaveState("saved");
  };

  return (
    <main className="app-shell">
      <header className="app-header">
        <div className="brand-block">
          <div className="brand-mark">建</div>
          <div>
            <h1>建筑设计说明助手</h1>
            <p>建筑专业说明编制与审图前自检</p>
          </div>
        </div>
        <div className="header-actions">
          <span className={`connection-state ${host ? "connected" : ""}`}>
            <span className="state-dot" />
            {host ? "CAD 已连接" : "独立编辑模式"}
          </span>
          <button className="button" onClick={handleNewProject}>新建</button>
          <button className="button" onClick={() => window.chrome?.webview ? postProjectMessage("project.open") : handleReset()}>打开</button>
          <button className="button" onClick={() => setConditionsOpen(true)}>项目条件</button>
          <button className="button" onClick={() => setTablesOpen(true)}>
            专业表格{workspace.tables?.length ? ` (${workspace.tables.length})` : ""}
          </button>
          <button className="button" onClick={() => setHistoryOpen(true)}>变更记录</button>
          <button className="button" onClick={openVersionHistory}>版本历史</button>
          <button className="button" disabled={reviewRunning} onClick={runFoundationReview}>
            {reviewRunning ? "正在预审…" : "运行预审"}
          </button>
          <button className="button" onClick={() => setReviewHistoryOpen(true)}>
            预审记录{workspace.reviewRecords?.length ? ` (${workspace.reviewRecords.length})` : ""}
          </button>
          <button className="button" onClick={() => setSignoffOpen(true)}>签发设置</button>
          <select
            className="recent-projects"
            aria-label="最近项目"
            value=""
            onChange={(event) => event.target.value && postProjectMessage("project.openRecent", { filePath: event.target.value })}
          >
            <option value="">最近项目</option>
            {recentProjects.map((path) => <option key={path} value={path}>{path.split(/[\\/]/).pop()}</option>)}
          </select>
          <button className="button" onClick={() => handleSaveProject(true)}>另存为</button>
          <button className="button" onClick={() => handleSaveProject(false, true)}>创建快照</button>
          <button
            className="button primary"
            onClick={() => handleSaveProject()}
          >
            保存
          </button>
        </div>
      </header>

      <section className="project-strip">
        <label>
          <span>项目名称</span>
          <input
            value={workspace.projectName}
            onChange={(event) => updateProjectMetadata("projectName", event.target.value)}
          />
        </label>
        <label>
          <span>建设地点</span>
          <input
            value={workspace.location}
            onChange={(event) => updateProjectMetadata("location", event.target.value)}
          />
        </label>
        <label>
          <span>建筑类型</span>
          <select
            value={workspace.buildingType}
            onChange={(event) => updateProjectMetadata("buildingType", event.target.value)}
          >
            <option>通用建筑</option>
            <option>住宅建筑</option>
            <option>办公建筑</option>
            <option>商业建筑</option>
            <option>教育建筑</option>
            <option>医疗建筑</option>
            <option>交通建筑</option>
            <option>文体建筑</option>
            <option>工业建筑</option>
          </select>
        </label>
        <label>
          <span>设计阶段</span>
          <select
            value={workspace.designStage}
            onChange={(event) => updateProjectMetadata("designStage", event.target.value)}
          >
            <option>方案设计</option>
            <option>初步设计</option>
            <option>施工图设计</option>
          </select>
        </label>
      </section>

      <nav className="mobile-workspace-nav">
        <button className="button" onClick={() => setLeftOpen((open) => !open)}>章节</button>
        <strong>{selectedSection.number} {selectedSection.title}</strong>
        <button className="button" onClick={() => setRightOpen((open) => !open)}>
          自检 {issues.length}
        </button>
      </nav>

      <section className="workspace-grid">
        <aside className={`left-panel ${leftOpen ? "mobile-open" : ""}`}>
          <div className="panel-heading">
            <div>
              <span className="eyebrow">文档结构</span>
              <h2>建筑设计说明</h2>
            </div>
            <span className="count-badge">{workspace.sections.length}</span>
          </div>
          <div className="section-list">
            {workspace.sections.map((section) => (
              <button
                className={`section-item ${section.id === selectedSection.id ? "selected" : ""} ${section.enabled === false ? "disabled-section" : ""}`}
                key={section.id}
                onClick={() => handleSectionChange(section.id)}
              >
                <span className="section-number">{section.number}</span>
                <span className="section-copy">
                  <strong>{section.title}</strong>
                  <small>
                    {section.enabled === false
                      ? "未启用"
                      : section.requirement === "required"
                      ? "必填"
                      : section.requirement === "conditional"
                        ? "条件启用"
                        : "可选"}
                  </small>
                </span>
                <span className={`review-dot ${section.reviewState}`} />
              </button>
            ))}
          </div>
          <div className="panel-summary">
            <div className="summary-row">
              <span>项目参数完整度</span>
              <strong>{completeness}%</strong>
            </div>
            <div className="progress-track"><span style={{ width: `${completeness}%` }} /></div>
            <p>这里只表示字段确认状态，不代表施工图审查结论。</p>
          </div>
        </aside>

        <section className="editor-panel">
          <div className="editor-toolbar">
            <div className="toolbar-group">
              <button title="撤销 Ctrl+Z" onClick={() => editorHandle?.command(editorCommands.undo)}>↶</button>
              <button title="重做 Ctrl+Y" onClick={() => editorHandle?.command(editorCommands.redo)}>↷</button>
            </div>
            <div className="toolbar-group">
              <button onClick={() => editorHandle?.command(editorCommands.paragraph)}>正文</button>
              <button onClick={() => editorHandle?.command(editorCommands.heading2)}>标题</button>
              <button className="bold-button" onClick={() => editorHandle?.command(editorCommands.bold)}>B</button>
              <button onClick={() => editorHandle?.command(editorCommands.orderedList)}>1.</button>
              <button onClick={() => editorHandle?.command(editorCommands.bulletList)}>•</button>
              <button onClick={() => editorHandle?.command(editorCommands.blockquote)}>引用</button>
            </div>
            <div className="toolbar-group insert-group">
              <select value={fieldToInsert} onChange={(event) => setFieldToInsert(event.target.value)}>
                {workspace.fields.map((field) => (
                  <option key={field.path} value={field.path}>{field.label}</option>
                ))}
              </select>
              <button onClick={insertSelectedField}>插入字段</button>
              <button
                onClick={() =>
                  editorHandle?.insertStandard("GB 55031-2022", "民用建筑通用规范")
                }
              >
                插入规范
              </button>
            </div>
          </div>

          <div className="find-bar">
            <input
              aria-label="查找文字"
              placeholder="查找"
              value={searchText}
              onChange={(event) => setSearchText(event.target.value)}
            />
            <input
              aria-label="替换文字"
              placeholder="替换为"
              value={replacementText}
              onChange={(event) => setReplacementText(event.target.value)}
            />
            <button className="button compact" onClick={handleReplaceAll}>全部替换</button>
            <span className={`save-indicator ${saveState}`} title={currentProjectPath}>
              {saveState === "dirty"
                ? "正在保存草稿…"
                : projectNotice || (currentProjectPath ? `项目：${currentProjectPath.split(/[\\/]/).pop()}` : "草稿已保存")}
            </span>
          </div>

          <div className="document-scroll">
            <article className="paper">
              <div className="paper-heading">
                <span>第 {selectedSection.number} 章</span>
                <strong>{selectedSection.title}</strong>
              </div>
              <ProseMirrorEditor
                key={`${selectedSection.id}:${editorRevision}`}
                sectionId={`${selectedSection.id}:${editorRevision}`}
                content={selectedSection.content}
                onReady={setEditorHandle}
                onChange={(content) =>
                  changeWorkspace((current) => ({
                    ...current,
                    sections: current.sections.map((section) =>
                      section.id === selectedSection.id
                        ? { ...section, content, reviewState: "ready" }
                        : section,
                    ),
                  }))
                }
              />
            </article>
          </div>
        </section>

        <aside className={`right-panel ${rightOpen ? "mobile-open" : ""}`}>
          <div className="panel-heading">
            <div>
              <span className="eyebrow">审图前自检</span>
              <h2>待处理事项</h2>
            </div>
            <span className="count-badge warning">{issues.length}</span>
          </div>
          <div className="review-notice">
            当前仅检查字段和章节完整性，不判定是否满足强制性条文。
          </div>
          <div className="issue-list">
            {issues.length === 0 ? (
              <div className="empty-state">当前没有完整性问题。</div>
            ) : (
              issues.map((issue) => (
                <button
                  className={`issue-card ${issue.level}`}
                  key={issue.id}
                  onClick={() => {
                    if (issue.fieldPath) {
                      document.getElementById(`field-${issue.fieldPath}`)?.scrollIntoView({
                        behavior: "smooth",
                        block: "center",
                      });
                    } else if (issue.sectionId) {
                      setSelectedSectionId(issue.sectionId);
                    }
                  }}
                >
                  <strong>{issue.title}</strong>
                  <span>{issue.detail}</span>
                </button>
              ))
            )}
          </div>
          <div className="field-panel">
            <div className="subheading">
              <h3>项目字段</h3>
              <span>{confirmedCount}/{workspace.fields.length} 已确认</span>
            </div>
            {workspace.fields.map((field) => (
              <label className={`field-card ${field.locked ? "locked" : ""}`} id={`field-${field.path}`} key={field.path}>
                <span className="field-label">
                  <strong>{field.label}{field.required === false ? "（可选）" : ""}</strong>
                  <em className={`field-state ${field.state}`}>{field.locked ? "已锁定" : stateLabel(field)}</em>
                </span>
                <span className="field-input-row">
                  <input disabled={field.locked} value={field.value} onChange={(event) => updateField(field.path, event.target.value)} />
                  {field.unit && <small>{field.unit}</small>}
                </span>
                <small className="field-source">
                  {field.source ? `来源：${field.source}` : "来源：未登记"}
                </small>
                <span className="field-actions">
                  <button
                    type="button"
                    onClick={(event) => {
                      event.preventDefault();
                      setEditingFieldPath(field.path);
                    }}
                  >
                    编辑来源
                  </button>
                  <button
                    type="button"
                    onClick={(event) => {
                      event.preventDefault();
                      updateFieldMetadata(field.path, {
                        state: "confirmed",
                        locked: true,
                        confirmedAt: new Date().toISOString(),
                      });
                    }}
                    disabled={field.locked}
                  >
                    确认并锁定
                  </button>
                  {field.locked && (
                    <button
                      type="button"
                      onClick={(event) => {
                        event.preventDefault();
                        updateFieldMetadata(field.path, { locked: false });
                      }}
                    >
                      解锁
                    </button>
                  )}
                </span>
              </label>
            ))}
          </div>
        </aside>
      </section>

      {wizardOpen && (
        <ProjectWizard
          onCancel={() => setWizardOpen(false)}
          onComplete={completeNewProject}
        />
      )}

      {historyOpen && (
        <div className="dialog-backdrop" onMouseDown={() => setHistoryOpen(false)}>
          <section className="settings-dialog change-history-dialog" onMouseDown={(event) => event.stopPropagation()}>
            <header>
              <div>
                <span className="eyebrow">项目数据中心</span>
                <h2>字段变更记录</h2>
              </div>
              <button className="dialog-close" onClick={() => setHistoryOpen(false)}>×</button>
            </header>
            <div className="history-summary">
              共记录 {workspace.fieldChanges?.length ?? 0} 次有效变更；连续输入会自动合并。
            </div>
            <div className="change-history-list">
              {(workspace.fieldChanges ?? []).length === 0 ? (
                <div className="empty-state">当前项目还没有字段变更记录。</div>
              ) : (
                [...(workspace.fieldChanges ?? [])].reverse().map((change) => (
                  <article className="change-entry" key={change.id}>
                    <header>
                      <strong>{change.fieldLabel}</strong>
                      <span>{changeKindLabel(change)}</span>
                      <time>{new Date(change.changedAt).toLocaleString("zh-CN")}</time>
                    </header>
                    <div className="change-values">
                      <del>{change.oldValue || "（空）"}</del>
                      <span>→</span>
                      <ins>{change.newValue || "（空）"}</ins>
                    </div>
                    {change.note && <small>{change.note}</small>}
                  </article>
                ))
              )}
            </div>
            <footer><button className="button primary" onClick={() => setHistoryOpen(false)}>关闭</button></footer>
          </section>
        </div>
      )}

      {versionHistoryOpen && (
        <ProjectHistory
          currentWorkspace={workspace}
          snapshots={snapshots}
          selectedSnapshotPath={selectedSnapshotPath}
          snapshotWorkspace={snapshotWorkspace}
          loading={snapshotLoading}
          onSelect={loadSnapshotForComparison}
          onRestore={restoreSelectedSnapshot}
          onClose={() => setVersionHistoryOpen(false)}
        />
      )}

      {ruleReportOpen && ruleReview && (
        <RuleReviewReport
          projectName={selectedReviewRecord?.projectName ?? workspace.projectName}
          projectInfo={selectedReviewRecord?.projectInfo ?? {
            location: workspace.location,
            buildingType: workspace.buildingType,
            designStage: workspace.designStage,
            submissionDate: workspace.submissionDate ?? "",
          }}
          result={ruleReview}
          archiveInfo={selectedReviewRecord ? {
            recordId: selectedReviewRecord.recordId,
            projectFingerprint: selectedReviewRecord.projectFingerprint,
            archivedAt: selectedReviewRecord.archivedAt,
            isCurrent: selectedReviewRecord.projectFingerprint === getReviewFingerprint(workspace),
          } : {
            recordId: "",
            projectFingerprint: getReviewFingerprint(workspace),
            archivedAt: ruleReview.executedAt,
            isCurrent: true,
          }}
          record={selectedReviewRecord ?? undefined}
          onUpdateAction={updateSelectedReviewAction}
          onLocate={locateRuleIssue}
          onClose={() => {
            setRuleReportOpen(false);
            setSelectedReviewRecord(null);
          }}
        />
      )}

      {reviewHistoryOpen && (
        <ReviewHistory
          records={workspace.reviewRecords ?? []}
          currentFingerprint={getReviewFingerprint(workspace)}
          onView={openArchivedReview}
          onCompare={openReviewComparison}
          onClose={() => setReviewHistoryOpen(false)}
        />
      )}

      {reviewComparison && (
        <ReviewComparison
          comparison={reviewComparison}
          onClose={() => setReviewComparison(null)}
        />
      )}

      {signoffOpen && (
        <ReviewSignoffSettings
          value={workspace.reviewSignoff ?? {
            organization: "",
            projectManager: "",
            preparedBy: "",
            checkedBy: "",
            approvedBy: "",
            reportNumber: "",
          }}
          onSave={saveReviewSignoff}
          onClose={() => setSignoffOpen(false)}
        />
      )}

      {tablesOpen && (
        <ProfessionalTableEditor
          value={workspace.tables ?? []}
          fields={workspace.fields}
          onSave={(tables) => {
            changeWorkspace((current) => ({ ...current, tables }));
            setProjectNotice(`已保存 ${tables.length} 张专业表格，请保存项目文件`);
            setTablesOpen(false);
          }}
          onClose={() => setTablesOpen(false)}
        />
      )}

      {conditionsOpen && (
        <div className="dialog-backdrop" onMouseDown={() => setConditionsOpen(false)}>
          <section className="settings-dialog" onMouseDown={(event) => event.stopPropagation()}>
            <header>
              <div>
                <span className="eyebrow">章节模板</span>
                <h2>项目条件</h2>
              </div>
              <button className="dialog-close" onClick={() => setConditionsOpen(false)}>×</button>
            </header>
            <div className="settings-row">
              <label>
                <span>建筑类型</span>
                <select
                  value={workspace.buildingType}
                  onChange={(event) => updateProjectMetadata("buildingType", event.target.value)}
                >
                  {["通用建筑", "住宅建筑", "办公建筑", "商业建筑", "教育建筑", "医疗建筑", "交通建筑", "文体建筑", "工业建筑", "其他建筑"].map((type) => (
                    <option key={type}>{type}</option>
                  ))}
                </select>
              </label>
              <label>
                <span>项目性质</span>
                <select
                  value={workspace.projectNature}
                  onChange={(event) =>
                    changeWorkspace((current) =>
                      applyBuildingTemplate({
                        ...current,
                        projectNature: event.target.value as EditorWorkspace["projectNature"],
                      }),
                    )
                  }
                >
                  <option>新建</option>
                  <option>改建</option>
                  <option>扩建</option>
                </select>
              </label>
            </div>
            <div className="feature-grid">
              {([
                ["hasBasement", "设有地下室"],
                ["hasCurtainWall", "涉及幕墙"],
                ["hasElevator", "设有电梯"],
                ["hasCivilDefense", "涉及人防"],
                ["isGreenBuilding", "绿色建筑"],
                ["isPrefabricated", "装配式建筑"],
                ["hasSpecialistDesign", "涉及专项深化设计"],
              ] as const).map(([name, label]) => (
                <label key={name}>
                  <input
                    type="checkbox"
                    checked={workspace.features?.[name] ?? false}
                    onChange={(event) => updateProjectCondition(name, event.target.checked)}
                  />
                  <span>{label}</span>
                </label>
              ))}
            </div>
            <div className="template-summary">
              当前模板启用 {workspace.sections.filter((section) => section.enabled !== false).length} 个章节，
              {workspace.fields.filter((field) => field.required !== false).length} 个必填字段。
            </div>
            <footer>
              <button className="button primary" onClick={() => setConditionsOpen(false)}>确定</button>
            </footer>
          </section>
        </div>
      )}

      {editingFieldPath && (() => {
        const field = workspace.fields.find((item) => item.path === editingFieldPath);
        if (!field) return null;
        return (
          <div className="dialog-backdrop" onMouseDown={() => setEditingFieldPath(null)}>
            <section className="settings-dialog source-dialog" onMouseDown={(event) => event.stopPropagation()}>
              <header>
                <div>
                  <span className="eyebrow">项目数据中心</span>
                  <h2>{field.label} · 数据来源</h2>
                </div>
                <button className="dialog-close" onClick={() => setEditingFieldPath(null)}>×</button>
              </header>
              <div className="form-grid">
                <label>
                  <span>字段状态</span>
                  <select
                    value={field.state}
                    onChange={(event) => updateFieldMetadata(field.path, { state: event.target.value as ProjectField["state"] })}
                  >
                    <option value="unknown">待确认</option>
                    <option value="pending">待复核</option>
                    <option value="confirmed">已确认</option>
                    <option value="notApplicable">不适用</option>
                    <option value="providedByOtherDiscipline">其他专业提供</option>
                    <option value="providedBySpecialist">专项单位提供</option>
                    <option value="overridden">人工覆盖</option>
                  </select>
                </label>
                <label>
                  <span>来源类型</span>
                  <select
                    value={field.sourceType}
                    onChange={(event) => updateFieldMetadata(field.path, { sourceType: event.target.value as FieldSourceType })}
                  >
                    <option value="projectApproval">项目批复</option>
                    <option value="designBrief">设计任务书</option>
                    <option value="drawing">设计图纸</option>
                    <option value="calculation">计算书</option>
                    <option value="otherDiscipline">其他专业</option>
                    <option value="specialist">专项单位</option>
                    <option value="manual">人工录入</option>
                  </select>
                </label>
                <label className="wide">
                  <span>来源说明</span>
                  <input value={field.source} onChange={(event) => updateFieldMetadata(field.path, { source: event.target.value })} />
                </label>
                <label>
                  <span>来源文件/编号</span>
                  <input value={field.sourceDocumentId} onChange={(event) => updateFieldMetadata(field.path, { sourceDocumentId: event.target.value })} />
                </label>
                <label>
                  <span>录入/确认人</span>
                  <input value={field.enteredBy} onChange={(event) => updateFieldMetadata(field.path, { enteredBy: event.target.value })} />
                </label>
                <label className="wide checkbox-line">
                  <input
                    type="checkbox"
                    checked={field.isManuallyOverridden}
                    onChange={(event) => updateFieldMetadata(field.path, {
                      isManuallyOverridden: event.target.checked,
                      state: event.target.checked ? "overridden" : field.state,
                    })}
                  />
                  <span>这是对原始数据的人工覆盖</span>
                </label>
                {field.isManuallyOverridden && (
                  <label className="wide">
                    <span>覆盖原因</span>
                    <input value={field.overrideReason} onChange={(event) => updateFieldMetadata(field.path, { overrideReason: event.target.value })} />
                  </label>
                )}
              </div>
              <footer>
                <button className="button" onClick={() => setEditingFieldPath(null)}>关闭</button>
                <button
                  className="button primary"
                  onClick={() => {
                    updateFieldMetadata(field.path, {
                      state: "confirmed",
                      locked: true,
                      confirmedAt: new Date().toISOString(),
                    });
                    setEditingFieldPath(null);
                  }}
                >
                  确认并锁定
                </button>
              </footer>
            </section>
          </div>
        );
      })()}
    </main>
  );
}
