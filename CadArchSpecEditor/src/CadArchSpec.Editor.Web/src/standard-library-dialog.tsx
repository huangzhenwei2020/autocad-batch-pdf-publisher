import { useMemo, useState } from "react";
import { defaultDesignStandards, type DesignStandard } from "./editor-model";

type Props = {
  value: DesignStandard[];
  buildingType: string;
  location: string;
  onSave(value: DesignStandard[]): void;
  onInsert(value: DesignStandard[], library: DesignStandard[]): void;
  onClose(): void;
};

const clone = (items: DesignStandard[]) => items.map((item) => ({ ...item, buildingTypes: [...item.buildingTypes] }));
const isApplicable = (item: DesignStandard, buildingType: string, location: string) => {
  const buildingMatches = item.buildingTypes.length === 0 || item.buildingTypes.includes(buildingType) || item.buildingTypes.includes("通用建筑");
  const regionMatches = item.level === "国家" || !item.region || location.includes(item.region);
  return buildingMatches && regionMatches;
};

export function StandardLibraryDialog({ value, buildingType, location, onSave, onInsert, onClose }: Props) {
  const [items, setItems] = useState(() => clone(value));
  const [selectedId, setSelectedId] = useState(items[0]?.id ?? "");
  const [checkedIds, setCheckedIds] = useState<Set<string>>(() => new Set(items.filter((item) => item.enabled && isApplicable(item, buildingType, location)).map((item) => item.id)));
  const [level, setLevel] = useState<"全部" | DesignStandard["level"]>("全部");
  const [onlyApplicable, setOnlyApplicable] = useState(true);
  const selected = items.find((item) => item.id === selectedId) ?? null;
  const filtered = useMemo(() => items.filter((item) => {
    if (level !== "全部" && item.level !== level) return false;
    if (!onlyApplicable) return true;
    return isApplicable(item, buildingType, location);
  }), [items, level, onlyApplicable, buildingType, location]);

  const update = (patch: Partial<DesignStandard>) => {
    if (!selected) return;
    setItems((current) => current.map((item) => item.id === selected.id ? { ...item, ...patch } : item));
  };

  const add = () => {
    const id = `custom-${Date.now()}`;
    setItems((current) => [...current, {
      id, code: "", name: "自定义规范", level: "自定义", region: "", buildingTypes: [buildingType],
      enabled: true, isPreset: false, note: "", sourceUrl: "",
    }]);
    setSelectedId(id);
    setOnlyApplicable(false);
  };

  return (
    <div className="dialog-backdrop" onMouseDown={onClose}>
      <section className="settings-dialog standard-library-dialog" onMouseDown={(event) => event.stopPropagation()}>
        <header>
          <div><span className="eyebrow">国家 / 地方 / 用户自定义</span><h2>设计规范库</h2></div>
          <button className="dialog-close" onClick={onClose}>×</button>
        </header>
        <div className="standard-library-toolbar">
          <label>分类
            <select value={level} onChange={(event) => setLevel(event.target.value as typeof level)}>
              <option>全部</option><option>国家</option><option>地方</option><option>自定义</option>
            </select>
          </label>
          <label><input type="checkbox" checked={onlyApplicable} onChange={(event) => setOnlyApplicable(event.target.checked)} />仅显示适用于“{buildingType} / {location || "未设置地区"}”</label>
          <button className="button compact" onClick={add}>新增自定义</button>
          <button className="button compact" onClick={() => setCheckedIds((current) => new Set([...current, ...filtered.filter((item) => item.enabled).map((item) => item.id)]))}>全选当前筛选</button>
          <button className="button compact" onClick={() => setCheckedIds(new Set())}>取消全选</button>
          <button className="button compact" onClick={() => {
            const presets = defaultDesignStandards();
            setItems(presets);
            setSelectedId(presets[0].id);
            setCheckedIds(new Set(presets.filter((item) => item.enabled && isApplicable(item, buildingType, location)).map((item) => item.id)));
          }}>恢复预设</button>
        </div>
        <div className="standard-library-body">
          <aside className="standard-library-list">
            {filtered.map((item) => (
              <div key={item.id} className={`standard-library-row ${item.id === selectedId ? "selected" : ""}`} onClick={() => setSelectedId(item.id)}>
                <input
                  type="checkbox"
                  checked={checkedIds.has(item.id)}
                  disabled={!item.enabled}
                  onClick={(event) => event.stopPropagation()}
                  onChange={(event) => setCheckedIds((current) => {
                    const next = new Set(current);
                    if (event.target.checked) next.add(item.id); else next.delete(item.id);
                    return next;
                  })}
                />
                <span className="standard-library-row-copy"><strong>{item.code || "无编号"}</strong><span>{item.name}</span><small>{item.level} · {item.region || "不限地区"}{item.isPreset ? " · 预设" : " · 自定义"}</small></span>
              </div>
            ))}
            {filtered.length === 0 && <p className="empty-state">没有符合当前筛选条件的规范。</p>}
          </aside>
          {selected ? <div className="standard-editor-form">
            <label><span>启用</span><input type="checkbox" checked={selected.enabled} onChange={(event) => update({ enabled: event.target.checked })} /></label>
            <label><span>规范编号</span><input value={selected.code} onChange={(event) => update({ code: event.target.value })} /></label>
            <label className="wide"><span>规范名称</span><input value={selected.name} onChange={(event) => update({ name: event.target.value })} /></label>
            <label><span>级别</span><select value={selected.level} onChange={(event) => update({ level: event.target.value as DesignStandard["level"] })}><option>国家</option><option>地方</option><option>自定义</option></select></label>
            <label><span>地区</span><input value={selected.region} placeholder="全国/广西/南宁…" onChange={(event) => update({ region: event.target.value })} /></label>
            <label className="wide"><span>适用建筑类型</span><input value={selected.buildingTypes.join("、")} onChange={(event) => update({ buildingTypes: event.target.value.split(/[、,，]/).map((x) => x.trim()).filter(Boolean) })} /></label>
            <label className="wide"><span>来源网址</span><input value={selected.sourceUrl} onChange={(event) => update({ sourceUrl: event.target.value })} /></label>
            <label className="wide"><span>备注</span><textarea value={selected.note} onChange={(event) => update({ note: event.target.value })} /></label>
            <div className="standard-editor-actions">
              {!selected.isPreset && <button className="button danger" onClick={() => { setItems((current) => current.filter((item) => item.id !== selected.id)); setSelectedId(""); }}>删除自定义</button>}
            </div>
          </div> : <div className="empty-state">请选择规范。</div>}
        </div>
        <footer><span>已勾选 {checkedIds.size} 条；预设仍需按报审日期核对现行版本。</span><button className="button primary" disabled={checkedIds.size === 0} onClick={() => onInsert(items.filter((item) => checkedIds.has(item.id) && item.enabled), items)}>批量插入所选规范</button><button className="button" onClick={onClose}>取消</button><button className="button primary" onClick={() => onSave(items)}>保存规范库</button></footer>
      </section>
    </div>
  );
}
