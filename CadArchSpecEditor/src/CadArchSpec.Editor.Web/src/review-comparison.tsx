import type {
  ReviewArchiveRecord,
  ReviewIssueAction,
  ReviewIssueStatus,
} from "./editor-model";
import type { RuleReviewIssue } from "./rule-review";

export type ComparedIssue = {
  key: string;
  issue: RuleReviewIssue;
  oldAction?: ReviewIssueAction;
  newAction?: ReviewIssueAction;
};

export type ReviewComparisonResult = {
  older: ReviewArchiveRecord;
  newer: ReviewArchiveRecord;
  added: ComparedIssue[];
  persistent: ComparedIssue[];
  noLongerDetected: ComparedIssue[];
};

const statusLabels: Record<ReviewIssueStatus, string> = {
  open: "待处理",
  inProgress: "处理中",
  resolved: "已解决",
  acceptedRisk: "接受风险",
  notApplicable: "不适用",
};

function issueKey(issue: RuleReviewIssue) {
  return [
    issue.ruleId,
    issue.targetFieldPath || "",
    issue.targetNodeId || "",
  ].join("|").toLocaleLowerCase();
}

function actionsByIssue(record: ReviewArchiveRecord) {
  return new Map(record.issueActions.map((action) => [action.issueId, action]));
}

export function compareReviewRecords(
  first: ReviewArchiveRecord,
  second: ReviewArchiveRecord,
): ReviewComparisonResult {
  const [older, newer] = first.archivedAt <= second.archivedAt
    ? [first, second]
    : [second, first];
  const oldIssues = new Map(older.result.issues.map((issue) => [issueKey(issue), issue]));
  const newIssues = new Map(newer.result.issues.map((issue) => [issueKey(issue), issue]));
  const oldActions = actionsByIssue(older);
  const newActions = actionsByIssue(newer);

  const added = [...newIssues.entries()]
    .filter(([key]) => !oldIssues.has(key))
    .map(([key, issue]) => ({
      key,
      issue,
      newAction: newActions.get(issue.issueId),
    }));
  const persistent = [...newIssues.entries()]
    .filter(([key]) => oldIssues.has(key))
    .map(([key, issue]) => {
      const oldIssue = oldIssues.get(key)!;
      return {
        key,
        issue,
        oldAction: oldActions.get(oldIssue.issueId),
        newAction: newActions.get(issue.issueId),
      };
    });
  const noLongerDetected = [...oldIssues.entries()]
    .filter(([key]) => !newIssues.has(key))
    .map(([key, issue]) => ({
      key,
      issue,
      oldAction: oldActions.get(issue.issueId),
    }));

  return { older, newer, added, persistent, noLongerDetected };
}

export function downloadReviewComparison(comparison: ReviewComparisonResult) {
  const payload = {
    reportType: "CadArchSpecReviewComparison",
    generatedAt: new Date().toISOString(),
    olderRecordId: comparison.older.recordId,
    newerRecordId: comparison.newer.recordId,
    olderExecutedAt: comparison.older.result.executedAt,
    newerExecutedAt: comparison.newer.result.executedAt,
    added: comparison.added,
    persistent: comparison.persistent,
    noLongerDetected: comparison.noLongerDetected,
  };
  const blob = new Blob([JSON.stringify(payload, null, 2)], {
    type: "application/json;charset=utf-8",
  });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = `${comparison.newer.projectName || "建筑设计说明项目"}-预审差异.json`;
  anchor.click();
  URL.revokeObjectURL(url);
}

function statusText(action?: ReviewIssueAction) {
  return action ? statusLabels[action.status] : "未记录";
}

function ComparisonGroup({
  title,
  description,
  kind,
  items,
}: {
  title: string;
  description: string;
  kind: "added" | "persistent" | "removed";
  items: ComparedIssue[];
}) {
  return (
    <section className={`comparison-group ${kind}`}>
      <header>
        <h3>{title}</h3>
        <strong>{items.length}</strong>
      </header>
      <p>{description}</p>
      {items.length === 0 ? (
        <div className="empty-state">没有此类问题。</div>
      ) : items.map((item) => (
        <article key={item.key}>
          <div>
            <strong>{item.issue.title}</strong>
            <code>{item.issue.ruleId}</code>
          </div>
          <p>{item.issue.message}</p>
          {kind === "persistent" ? (
            <small>
              处理状态：{statusText(item.oldAction)} → {statusText(item.newAction)}
            </small>
          ) : (
            <small>
              {kind === "added"
                ? `当前状态：${statusText(item.newAction)}`
                : `原处理状态：${statusText(item.oldAction)}`}
            </small>
          )}
        </article>
      ))}
    </section>
  );
}

export function ReviewComparison({
  comparison,
  onClose,
}: {
  comparison: ReviewComparisonResult;
  onClose(): void;
}) {
  return (
    <div className="dialog-backdrop" onMouseDown={onClose}>
      <section className="settings-dialog review-comparison-dialog" onMouseDown={(event) => event.stopPropagation()}>
        <header>
          <div>
            <span className="eyebrow">项目审查档案</span>
            <h2>两次预审差异</h2>
          </div>
          <button className="dialog-close" onClick={onClose}>×</button>
        </header>
        <div className="comparison-versions">
          <div>
            <span>较早版本</span>
            <strong>{new Date(comparison.older.archivedAt).toLocaleString("zh-CN")}</strong>
            <small>{comparison.older.result.packageDisplayName} v{comparison.older.result.packageVersion}</small>
          </div>
          <b>→</b>
          <div>
            <span>较新版本</span>
            <strong>{new Date(comparison.newer.archivedAt).toLocaleString("zh-CN")}</strong>
            <small>{comparison.newer.result.packageDisplayName} v{comparison.newer.result.packageVersion}</small>
          </div>
        </div>
        <div className="comparison-notice">
          “本次不再检出”只表示新预审结果未包含该问题，不自动等同于人工已解决或专业审查通过。
        </div>
        <div className="comparison-body">
          <ComparisonGroup
            title="新增问题"
            description="较新预审中首次出现，需要确认是否由项目变化或规则包变化引起。"
            kind="added"
            items={comparison.added}
          />
          <ComparisonGroup
            title="持续问题"
            description="两次预审均检出，可核对处理状态是否发生变化。"
            kind="persistent"
            items={comparison.persistent}
          />
          <ComparisonGroup
            title="本次不再检出"
            description="较早预审存在、较新预审未检出，仍应结合处理记录人工确认关闭原因。"
            kind="removed"
            items={comparison.noLongerDetected}
          />
        </div>
        <footer>
          <span>
            新增 {comparison.added.length} · 持续 {comparison.persistent.length} ·
            不再检出 {comparison.noLongerDetected.length}
          </span>
          <div>
            <button className="button" onClick={onClose}>关闭</button>
            <button className="button primary" onClick={() => downloadReviewComparison(comparison)}>导出差异 JSON</button>
          </div>
        </footer>
      </section>
    </div>
  );
}
