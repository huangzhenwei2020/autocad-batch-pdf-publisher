import { useState } from "react";
import type { ReviewArchiveRecord } from "./editor-model";

type ReviewHistoryProps = {
  records: ReviewArchiveRecord[];
  currentFingerprint: string;
  onView(record: ReviewArchiveRecord): void;
  onCompare(first: ReviewArchiveRecord, second: ReviewArchiveRecord): void;
  onClose(): void;
};

function issueSummary(record: ReviewArchiveRecord) {
  const issues = record.result.issues;
  const blockers = issues.filter((issue) => issue.severity === "blocker").length;
  const errors = issues.filter((issue) => issue.severity === "error").length;
  const closed = record.issueActions.filter((action) =>
    action.status === "resolved" || action.status === "notApplicable").length;
  return `${issues.length} 个问题 · ${blockers} 阻断 · ${errors} 错误 · ${closed} 已关闭`;
}

export function ReviewHistory({
  records,
  currentFingerprint,
  onView,
  onCompare,
  onClose,
}: ReviewHistoryProps) {
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const ordered = [...records].sort((left, right) =>
    right.archivedAt.localeCompare(left.archivedAt));
  const toggleSelection = (recordId: string) => {
    setSelectedIds((current) =>
      current.includes(recordId)
        ? current.filter((id) => id !== recordId)
        : [...current, recordId].slice(-2));
  };
  const compareSelected = () => {
    const selected = selectedIds
      .map((id) => records.find((record) => record.recordId === id))
      .filter((record): record is ReviewArchiveRecord => Boolean(record));
    if (selected.length === 2) onCompare(selected[0], selected[1]);
  };
  return (
    <div className="dialog-backdrop" onMouseDown={onClose}>
      <section className="settings-dialog review-history-dialog" onMouseDown={(event) => event.stopPropagation()}>
        <header>
          <div>
            <span className="eyebrow">项目审查档案</span>
            <h2>预审记录</h2>
          </div>
          <button className="dialog-close" onClick={onClose}>×</button>
        </header>
        <div className="review-history-notice">
          记录随项目文件保存。项目数据发生变化后，旧记录仍保留，但不再代表当前版本。
        </div>
        <div className="review-history-list">
          {ordered.length === 0 ? (
            <div className="empty-state">尚无预审记录，请先运行一次国家基础预审。</div>
          ) : ordered.map((record, index) => {
            const isCurrent = record.projectFingerprint === currentFingerprint;
            const selected = selectedIds.includes(record.recordId);
            return (
              <article className={selected ? "selected" : ""} key={record.recordId}>
                <label className="review-compare-check">
                  <input
                    type="checkbox"
                    checked={selected}
                    onChange={() => toggleSelection(record.recordId)}
                  />
                  <span>对比</span>
                </label>
                <div className="review-history-index">{ordered.length - index}</div>
                <div>
                  <header>
                    <strong>{new Date(record.archivedAt).toLocaleString("zh-CN")}</strong>
                    <span className={isCurrent ? "current" : "historical"}>
                      {isCurrent ? "匹配当前项目" : "基于旧版本数据"}
                    </span>
                  </header>
                  <p>{record.result.packageDisplayName} v{record.result.packageVersion}</p>
                  <small>{issueSummary(record)} · 数据指纹 {record.projectFingerprint}</small>
                </div>
                <button className="button" onClick={() => onView(record)}>查看报告</button>
              </article>
            );
          })}
        </div>
        <footer>
          <span>已选择 {selectedIds.length}/2 · 最多保留最近 100 次记录</span>
          <div>
            <button className="button" onClick={onClose}>关闭</button>
            <button className="button primary" disabled={selectedIds.length !== 2} onClick={compareSelected}>
              对比所选
            </button>
          </div>
        </footer>
      </section>
    </div>
  );
}
