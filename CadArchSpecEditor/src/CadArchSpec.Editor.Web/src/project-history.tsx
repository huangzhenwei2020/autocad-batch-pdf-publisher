import type { EditorWorkspace } from "./editor-model";

export type ProjectSnapshotInfo = {
  filePath: string;
  fileName: string;
  createdAt: string;
  savedAt: string;
  projectName: string;
  fieldChangeCount: number;
};

export type WorkspaceDifference = {
  metadata: Array<{ label: string; current: string; snapshot: string }>;
  fields: Array<{ label: string; current: string; snapshot: string }>;
  sections: string[];
};

export function compareWorkspaces(
  current: EditorWorkspace,
  snapshot: EditorWorkspace,
): WorkspaceDifference {
  const metadataDefinitions: Array<[keyof EditorWorkspace, string]> = [
    ["projectName", "项目名称"],
    ["location", "建设地点"],
    ["buildingType", "建筑类型"],
    ["designStage", "设计阶段"],
    ["projectNature", "项目性质"],
    ["submissionDate", "报审日期"],
  ];
  const metadata = metadataDefinitions
    .map(([key, label]) => ({
      label,
      current: String(current[key] ?? ""),
      snapshot: String(snapshot[key] ?? ""),
    }))
    .filter((item) => item.current !== item.snapshot);

  const snapshotFields = new Map(snapshot.fields.map((field) => [field.path, field]));
  const fields = current.fields.flatMap((field) => {
    const other = snapshotFields.get(field.path);
    if (!other) {
      return [{ label: field.label, current: field.value, snapshot: "（快照中不存在）" }];
    }
    const currentValue = `${field.value}｜${field.state}${field.locked ? "｜已锁定" : ""}`;
    const snapshotValue = `${other.value}｜${other.state}${other.locked ? "｜已锁定" : ""}`;
    return currentValue === snapshotValue
      ? []
      : [{ label: field.label, current: currentValue, snapshot: snapshotValue }];
  });

  const snapshotSections = new Map(snapshot.sections.map((section) => [section.id, section]));
  const sections = current.sections.flatMap((section) => {
    const other = snapshotSections.get(section.id);
    const same = other &&
      section.enabled === other.enabled &&
      section.requirement === other.requirement &&
      JSON.stringify(section.content) === JSON.stringify(other.content);
    return same ? [] : [`${section.number} ${section.title}`];
  });
  return { metadata, fields, sections };
}

type ProjectHistoryProps = {
  currentWorkspace: EditorWorkspace;
  snapshots: ProjectSnapshotInfo[];
  selectedSnapshotPath: string;
  snapshotWorkspace: EditorWorkspace | null;
  loading: boolean;
  onSelect(snapshotPath: string): void;
  onRestore(snapshotPath: string): void;
  onClose(): void;
};

export function ProjectHistory({
  currentWorkspace,
  snapshots,
  selectedSnapshotPath,
  snapshotWorkspace,
  loading,
  onSelect,
  onRestore,
  onClose,
}: ProjectHistoryProps) {
  const difference = snapshotWorkspace
    ? compareWorkspaces(currentWorkspace, snapshotWorkspace)
    : null;
  const selected = snapshots.find((item) => item.filePath === selectedSnapshotPath);

  return (
    <div className="dialog-backdrop" onMouseDown={onClose}>
      <section className="settings-dialog version-history-dialog" onMouseDown={(event) => event.stopPropagation()}>
        <header>
          <div>
            <span className="eyebrow">项目版本管理</span>
            <h2>快照历史与版本差异</h2>
          </div>
          <button className="dialog-close" onClick={onClose}>×</button>
        </header>
        <div className="version-history-body">
          <aside className="snapshot-list">
            {snapshots.length === 0 ? (
              <div className="empty-state">当前项目还没有快照。请先点击顶部“创建快照”。</div>
            ) : snapshots.map((snapshot) => (
              <button
                className={snapshot.filePath === selectedSnapshotPath ? "active" : ""}
                key={snapshot.filePath}
                onClick={() => onSelect(snapshot.filePath)}
              >
                <strong>{new Date(snapshot.createdAt).toLocaleString("zh-CN")}</strong>
                <span>{snapshot.projectName || "未命名项目"}</span>
                <small>{snapshot.fieldChangeCount} 条字段变更</small>
              </button>
            ))}
          </aside>
          <section className="snapshot-difference">
            {loading ? (
              <div className="empty-state">正在读取快照…</div>
            ) : !selected || !difference ? (
              <div className="empty-state">选择左侧快照后查看与当前项目的差异。</div>
            ) : (
              <>
                <div className="difference-summary">
                  <strong>{difference.metadata.length + difference.fields.length + difference.sections.length}</strong>
                  项差异
                  <span>比较基准：当前编辑器内容</span>
                </div>
                {difference.metadata.length > 0 && (
                  <div className="difference-group">
                    <h3>项目元数据</h3>
                    {difference.metadata.map((item) => (
                      <article key={item.label}>
                        <strong>{item.label}</strong>
                        <del>{item.snapshot || "（空）"}</del>
                        <span>→</span>
                        <ins>{item.current || "（空）"}</ins>
                      </article>
                    ))}
                  </div>
                )}
                {difference.fields.length > 0 && (
                  <div className="difference-group">
                    <h3>结构化字段</h3>
                    {difference.fields.map((item) => (
                      <article key={item.label}>
                        <strong>{item.label}</strong>
                        <del>{item.snapshot || "（空）"}</del>
                        <span>→</span>
                        <ins>{item.current || "（空）"}</ins>
                      </article>
                    ))}
                  </div>
                )}
                {difference.sections.length > 0 && (
                  <div className="difference-group section-differences">
                    <h3>正文或章节状态有变化</h3>
                    <p>{difference.sections.join("、")}</p>
                  </div>
                )}
                {difference.metadata.length === 0 && difference.fields.length === 0 && difference.sections.length === 0 && (
                  <div className="empty-state">该快照与当前编辑器内容一致。</div>
                )}
              </>
            )}
          </section>
        </div>
        <footer>
          <span className="restore-warning">恢复前会自动保存当前版本，误操作仍可撤回。</span>
          <button className="button" onClick={onClose}>关闭</button>
          <button
            className="button primary"
            disabled={!selectedSnapshotPath || loading}
            onClick={() => onRestore(selectedSnapshotPath)}
          >
            恢复此版本
          </button>
        </footer>
      </section>
    </div>
  );
}
