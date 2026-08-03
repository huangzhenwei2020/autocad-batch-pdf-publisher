import type {
  ReviewArchiveRecord,
  ReviewIssueAction,
  ReviewIssueStatus,
} from "./editor-model";
import type { ReactNode } from "react";

export type RuleReviewIssue = {
  issueId: string;
  ruleId: string;
  severity: "blocker" | "error" | "warning" | "info";
  title: string;
  message: string;
  standardCode: string;
  clauseReference: string;
  targetNodeId: string;
  targetFieldPath: string;
  evidence: string;
  suggestedAction: string;
  requiresProfessionalConfirmation: boolean;
};

export type RuleReviewResult = {
  packageId: string;
  packageVersion: string;
  packageDisplayName: string;
  packageStatus: string;
  packageVerifiedAt: string;
  executedAt: string;
  localRulesLoaded: boolean;
  scopeNotice: string;
  issues: RuleReviewIssue[];
};

export type ReviewReportArchiveInfo = {
  recordId: string;
  projectFingerprint: string;
  archivedAt: string;
  isCurrent: boolean;
};

export type ReviewReportProjectInfo = {
  location: string;
  buildingType: string;
  designStage: string;
  submissionDate: string;
};

const statusLabels: Record<ReviewIssueStatus, string> = {
  open: "待处理",
  inProgress: "处理中",
  resolved: "已解决",
  acceptedRisk: "接受风险",
  notApplicable: "不适用",
};

function defaultAction(issueId: string, executedAt: string): ReviewIssueAction {
  return {
    issueId,
    status: "open",
    owner: "",
    comment: "",
    reviewer: "",
    updatedAt: executedAt,
  };
}

function splitIssues(issues: RuleReviewIssue[], pageSize = 4) {
  if (issues.length === 0) return [[]];
  const pages: RuleReviewIssue[][] = [];
  for (let index = 0; index < issues.length; index += pageSize) {
    pages.push(issues.slice(index, index + pageSize));
  }
  return pages;
}

export function getReviewReportPageTitles(issueCount: number, pageSize = 4) {
  const detailPageCount = Math.max(1, Math.ceil(issueCount / pageSize));
  return [
    "封面与预审结论",
    "项目与审查范围",
    ...Array.from({ length: detailPageCount }, (_, index) =>
      `问题明细 ${index + 1}/${detailPageCount}`),
    "问题处理记录",
    "规则索引与使用声明",
  ];
}

export function downloadReviewReport(
  result: RuleReviewResult,
  projectName: string,
  archiveInfo?: ReviewReportArchiveInfo,
  record?: ReviewArchiveRecord,
) {
  const blob = new Blob([JSON.stringify({
    reportType: "CadArchSpecFoundationReview",
    projectName,
    archiveInfo,
    issueActions: record?.issueActions ?? [],
    ...result,
  }, null, 2)], { type: "application/json;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = `${projectName || "建筑设计说明项目"}-国家基础预审报告.json`;
  anchor.click();
  URL.revokeObjectURL(url);
}

type ReportPageProps = {
  number: number;
  total: number;
  title?: string;
  children: ReactNode;
  cover?: boolean;
};

function ReportPage({ number, total, title, children, cover = false }: ReportPageProps) {
  return (
    <section className={`report-page ${cover ? "cover" : ""}`}>
      {!cover && (
        <header className="report-page-header">
          <strong>建筑专业国家基础预审报告</strong>
          <span>{title}</span>
        </header>
      )}
      <div className="report-page-body">{children}</div>
      <footer className="report-page-footer">
        <span>建筑设计说明助手 · 试运行报告</span>
        <span>第 {number} 页 / 共 {total} 页</span>
      </footer>
    </section>
  );
}

type RuleReviewReportProps = {
  projectName: string;
  projectInfo: ReviewReportProjectInfo;
  result: RuleReviewResult;
  archiveInfo?: ReviewReportArchiveInfo;
  record?: ReviewArchiveRecord;
  onUpdateAction(issueId: string, patch: Partial<Omit<ReviewIssueAction, "issueId">>): void;
  onLocate(issue: RuleReviewIssue): void;
  onClose(): void;
};

export function RuleReviewReport({
  projectName,
  projectInfo,
  result,
  archiveInfo,
  record,
  onUpdateAction,
  onLocate,
  onClose,
}: RuleReviewReportProps) {
  const counts = {
    blocker: result.issues.filter((item) => item.severity === "blocker").length,
    error: result.issues.filter((item) => item.severity === "error").length,
    warning: result.issues.filter((item) => item.severity === "warning").length,
    info: result.issues.filter((item) => item.severity === "info").length,
  };
  const issuePages = splitIssues(result.issues);
  const totalPages = getReviewReportPageTitles(result.issues.length).length;
  const actions = new Map((record?.issueActions ?? []).map((action) => [action.issueId, action]));
  const resolvedCount = [...actions.values()].filter((action) =>
    action.status === "resolved" || action.status === "notApplicable").length;
  const standardCodes = [...new Set(result.issues
    .map((issue) => issue.standardCode)
    .filter(Boolean))];
  const signoff = record?.reviewSignoff;
  const conclusion = counts.blocker > 0
    ? `发现 ${counts.blocker} 项阻断问题，建议完成处理并由建筑专业人员复核后再提交。`
    : result.issues.length > 0
      ? `发现 ${result.issues.length} 项完整性问题，尚不能形成“符合”结论。`
      : "本次试运行规则未发现问题，但不代表项目符合全部规范。";

  return (
    <div className="dialog-backdrop report-backdrop" onMouseDown={onClose}>
      <section className="settings-dialog rule-report-dialog" onMouseDown={(event) => event.stopPropagation()}>
        <header className="report-window-header">
          <div>
            <span className="eyebrow">项目审查档案</span>
            <h2>国家基础预审报告 · 多页预览</h2>
          </div>
          <button className="dialog-close" onClick={onClose}>×</button>
        </header>

        <div className="report-pages">
          <ReportPage number={1} total={totalPages} cover>
            <div className="report-cover-mark">建筑</div>
            <p className="report-cover-kicker">ARCHITECTURAL DESIGN REVIEW</p>
            <h1>建筑专业国家基础预审报告</h1>
            <h2>{projectName || "未命名项目"}</h2>
            <p className="report-cover-organization">{signoff?.organization || "设计单位未填写"}</p>
            <dl className="report-cover-meta">
              <div><dt>报告性质</dt><dd>设计资料完整性自检（试运行）</dd></div>
              <div><dt>规则包</dt><dd>{result.packageDisplayName} v{result.packageVersion}</dd></div>
              <div><dt>执行日期</dt><dd>{new Date(result.executedAt).toLocaleDateString("zh-CN")}</dd></div>
              <div><dt>数据版本</dt><dd>{archiveInfo?.isCurrent ? "匹配当前项目" : "基于旧版本数据"}</dd></div>
              <div><dt>报告编号</dt><dd>{signoff?.reportNumber || archiveInfo?.recordId || "当前预审"}</dd></div>
              <div><dt>项目负责人</dt><dd>{signoff?.projectManager || "未填写"}</dd></div>
            </dl>
            <div className="report-cover-conclusion">
              <strong>预审摘要</strong>
              <p>{conclusion}</p>
            </div>
            <p className="report-cover-disclaimer">
              本报告仅用于建筑专业设计资料完整性自检，不代替设计、校审、注册建筑师判断或施工图审查机构结论。
            </p>
          </ReportPage>

          <ReportPage number={2} total={totalPages} title="项目与审查范围">
            <h2 className="report-section-title">一、项目基本信息</h2>
            <table className="report-info-table">
              <tbody>
                <tr><th>项目名称</th><td>{projectName || "未填写"}</td><th>建设地点</th><td>{projectInfo.location || "未填写"}</td></tr>
                <tr><th>建筑类型</th><td>{projectInfo.buildingType || "未填写"}</td><th>设计阶段</th><td>{projectInfo.designStage || "未填写"}</td></tr>
                <tr><th>报审日期</th><td>{projectInfo.submissionDate || "未填写"}</td><th>数据指纹</th><td>{archiveInfo?.projectFingerprint || "未生成"}</td></tr>
              </tbody>
            </table>
            <h2 className="report-section-title">二、规则包与适用范围</h2>
            <table className="report-info-table vertical">
              <tbody>
                <tr><th>规则包</th><td>{result.packageId} / v{result.packageVersion}</td></tr>
                <tr><th>规则状态</th><td>{result.packageStatus === "Draft" ? "试运行 / 待专业复核" : result.packageStatus}</td></tr>
                <tr><th>核实时间</th><td>{result.packageVerifiedAt || "未登记"}</td></tr>
                <tr><th>地方规则</th><td>{result.localRulesLoaded ? "已加载" : "未加载"}</td></tr>
              </tbody>
            </table>
            <div className="report-scope-box">
              <strong>审查范围声明</strong>
              <p>{result.scopeNotice}</p>
            </div>
            <h2 className="report-section-title">三、问题汇总</h2>
            <div className="report-count-grid">
              <div><strong>{counts.blocker}</strong><span>阻断</span></div>
              <div><strong>{counts.error}</strong><span>错误</span></div>
              <div><strong>{counts.warning}</strong><span>警告</span></div>
              <div><strong>{counts.info}</strong><span>提示</span></div>
            </div>
            <p className="report-conclusion">{conclusion}</p>
          </ReportPage>

          {issuePages.map((pageIssues, pageIndex) => (
            <ReportPage
              key={`issues-${pageIndex}`}
              number={pageIndex + 3}
              total={totalPages}
              title={`问题明细 ${pageIndex + 1}/${issuePages.length}`}
            >
              <h2 className="report-section-title">四、问题明细</h2>
              {pageIssues.length === 0 ? (
                <div className="report-empty">本次试运行规则未发现问题。</div>
              ) : pageIssues.map((issue, issueIndex) => {
                const action = actions.get(issue.issueId) ?? defaultAction(issue.issueId, result.executedAt);
                const sequence = pageIndex * 4 + issueIndex + 1;
                return (
                  <article className={`report-issue-card ${issue.severity}`} key={issue.issueId}>
                    <header>
                      <span>{sequence}</span>
                      <strong>{issue.title}</strong>
                      <em>{issue.severity}</em>
                      <code>{issue.ruleId}</code>
                    </header>
                    <p>{issue.message}</p>
                    <dl>
                      <div><dt>检查证据</dt><dd>{issue.evidence || "无"}</dd></div>
                      <div><dt>建议处理</dt><dd>{issue.suggestedAction || "请由建筑专业人员核实。"}</dd></div>
                      <div><dt>依据索引</dt><dd>{issue.standardCode || "无"} {issue.clauseReference}</dd></div>
                    </dl>
                    <div className="issue-action-editor">
                      <label>
                        <span>状态</span>
                        <select
                          value={action.status}
                          onChange={(event) => onUpdateAction(issue.issueId, {
                            status: event.target.value as ReviewIssueStatus,
                          })}
                        >
                          {Object.entries(statusLabels).map(([value, label]) =>
                            <option key={value} value={value}>{label}</option>)}
                        </select>
                      </label>
                      <label><span>责任人</span><input value={action.owner} onChange={(event) => onUpdateAction(issue.issueId, { owner: event.target.value })} /></label>
                      <label><span>复核人</span><input value={action.reviewer} onChange={(event) => onUpdateAction(issue.issueId, { reviewer: event.target.value })} /></label>
                      <label className="wide"><span>处理意见</span><textarea value={action.comment} onChange={(event) => onUpdateAction(issue.issueId, { comment: event.target.value })} /></label>
                      <div className="print-action-value">
                        状态：{statusLabels[action.status]}　责任人：{action.owner || "未填写"}　复核人：{action.reviewer || "未填写"}<br />
                        处理意见：{action.comment || "未填写"}
                      </div>
                    </div>
                    <button className="report-locate" onClick={() => onLocate(issue)}>定位到项目</button>
                  </article>
                );
              })}
            </ReportPage>
          ))}

          <ReportPage number={issuePages.length + 3} total={totalPages} title="问题处理记录">
            <h2 className="report-section-title">五、问题处理记录</h2>
            <p className="report-intro">已关闭或判定不适用 {resolvedCount} 项，共 {result.issues.length} 项。接受风险不计入已解决数量。</p>
            <table className="report-action-table">
              <thead>
                <tr><th>序号</th><th>问题</th><th>状态</th><th>责任人</th><th>处理意见</th><th>复核人</th></tr>
              </thead>
              <tbody>
                {result.issues.length === 0 ? (
                  <tr><td colSpan={6}>本次没有问题处理记录。</td></tr>
                ) : result.issues.map((issue, index) => {
                  const action = actions.get(issue.issueId) ?? defaultAction(issue.issueId, result.executedAt);
                  return (
                    <tr key={issue.issueId}>
                      <td>{index + 1}</td>
                      <td>{issue.title}</td>
                      <td>{statusLabels[action.status]}</td>
                      <td>{action.owner || "—"}</td>
                      <td>{action.comment || "—"}</td>
                      <td>{action.reviewer || "—"}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            <div className="report-signatures">
              <div>编制人：<span>{signoff?.preparedBy}</span></div>
              <div>校对人：<span>{signoff?.checkedBy}</span></div>
              <div>审核人：<span>{signoff?.approvedBy}</span></div>
              <div>项目负责人：<span>{signoff?.projectManager}</span></div>
              <div>设计单位：<span>{signoff?.organization}</span></div>
              <div>签发日期：<span>{new Date(result.executedAt).toLocaleDateString("zh-CN")}</span></div>
            </div>
          </ReportPage>

          <ReportPage number={totalPages} total={totalPages} title="规则索引与声明">
            <h2 className="report-section-title">六、规则与规范索引</h2>
            <table className="report-info-table vertical">
              <tbody>
                <tr><th>规则包名称</th><td>{result.packageDisplayName}</td></tr>
                <tr><th>规则包编号</th><td>{result.packageId}</td></tr>
                <tr><th>规则包版本</th><td>{result.packageVersion}</td></tr>
                <tr><th>规则包状态</th><td>{result.packageStatus}</td></tr>
                <tr><th>规范索引</th><td>{standardCodes.length ? standardCodes.join("、") : "本次问题未引用规范索引"}</td></tr>
              </tbody>
            </table>
            <h2 className="report-section-title">七、结果解释</h2>
            <ol className="report-notes">
              <li>本报告检查的是规则包已覆盖范围内的数据与章节完整性。</li>
              <li>“未发现问题”不等同于“符合全部现行规范”。</li>
              <li>规则包状态为 Draft 时，所有结果均须由建筑专业人员复核。</li>
              <li>未加载项目所在地地方规则时，不得据此判断地方报审要求。</li>
              <li>数据指纹用于识别项目内容是否变化，不是数字签名或防篡改证明。</li>
              <li>接受风险和不适用结论必须填写原因并由有权限人员复核。</li>
            </ol>
            <div className="report-final-statement">
              <strong>最终声明</strong>
              <p>本报告是建筑设计说明助手生成的辅助校审资料，不能替代注册建筑师、设计单位校审人员及施工图审查机构的专业判断。</p>
            </div>
          </ReportPage>
        </div>

        <footer className="report-window-footer">
          <span>共 {totalPages} 页 · 问题 {result.issues.length} 项 · 已关闭 {resolvedCount} 项</span>
          <div>
            <button className="button" onClick={onClose}>关闭</button>
            <button className="button" onClick={() => window.print()}>打印 / 保存 PDF</button>
            <button className="button primary" onClick={() => downloadReviewReport(result, projectName, archiveInfo, record)}>导出 JSON</button>
          </div>
        </footer>
      </section>
    </div>
  );
}
