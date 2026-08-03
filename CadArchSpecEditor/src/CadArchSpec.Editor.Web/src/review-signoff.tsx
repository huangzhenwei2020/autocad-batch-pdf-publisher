import { useState } from "react";
import type { ProjectReviewSignoff } from "./editor-model";

type ReviewSignoffProps = {
  value: ProjectReviewSignoff;
  onSave(value: ProjectReviewSignoff): void;
  onClose(): void;
};

export function ReviewSignoffSettings({
  value,
  onSave,
  onClose,
}: ReviewSignoffProps) {
  const [draft, setDraft] = useState<ProjectReviewSignoff>(value);
  const update = (key: keyof ProjectReviewSignoff, next: string) =>
    setDraft((current) => ({ ...current, [key]: next }));

  return (
    <div className="dialog-backdrop" onMouseDown={onClose}>
      <section className="settings-dialog signoff-dialog" onMouseDown={(event) => event.stopPropagation()}>
        <header>
          <div>
            <span className="eyebrow">项目审查档案</span>
            <h2>报告签发设置</h2>
          </div>
          <button className="dialog-close" onClick={onClose}>×</button>
        </header>
        <div className="signoff-notice">
          这里设置的是新预审报告的默认签发信息。已经归档的旧报告不会被改写。
        </div>
        <div className="signoff-form">
          <label className="wide">
            <span>设计单位</span>
            <input
              value={draft.organization}
              onChange={(event) => update("organization", event.target.value)}
              placeholder="例如：××建筑设计有限公司"
            />
          </label>
          <label>
            <span>项目负责人</span>
            <input value={draft.projectManager} onChange={(event) => update("projectManager", event.target.value)} />
          </label>
          <label>
            <span>编制人</span>
            <input value={draft.preparedBy} onChange={(event) => update("preparedBy", event.target.value)} />
          </label>
          <label>
            <span>校对人</span>
            <input value={draft.checkedBy} onChange={(event) => update("checkedBy", event.target.value)} />
          </label>
          <label>
            <span>审核人</span>
            <input value={draft.approvedBy} onChange={(event) => update("approvedBy", event.target.value)} />
          </label>
          <label className="wide">
            <span>报告编号</span>
            <input
              value={draft.reportNumber}
              onChange={(event) => update("reportNumber", event.target.value)}
              placeholder="留空时每次预审自动生成"
            />
            <small>填写后作为项目默认编号；留空会生成 BPP-JZYS-日期-短编号。</small>
          </label>
        </div>
        <footer>
          <button className="button" onClick={onClose}>取消</button>
          <button className="button primary" onClick={() => onSave(draft)}>保存设置</button>
        </footer>
      </section>
    </div>
  );
}
