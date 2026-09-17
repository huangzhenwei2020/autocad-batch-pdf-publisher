import { useEffect, useState } from "react";
import type { ArchitectureTable } from "./editor-model";
import { createMessageId, createProjectMessage, createReadyMessage, protocolVersion } from "./host-protocol";
import { ProfessionalTableEditor, type CadTableInsertOptions } from "./professional-table-editor";

type HostMessage = { protocolVersion: number; type: string; payload: Record<string, unknown> };

export function revealCadTableEditor(bridge: Pick<NonNullable<NonNullable<Window["chrome"]>["webview"]>, "postMessage">) {
  bridge.postMessage(createProjectMessage("cad.table.visible"));
}

export function CadTableApp() {
  const [table, setTable] = useState<ArchitectureTable | null>(null);
  const [payload, setPayload] = useState<Record<string, unknown> | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const post = (type: Parameters<typeof createProjectMessage>[0], messagePayload: Record<string, unknown> = {}) =>
    window.chrome?.webview?.postMessage(createProjectMessage(type, messagePayload));

  useEffect(() => {
    const bridge = window.chrome?.webview;
    if (!bridge) return;
    const onMessage = (event: MessageEvent<HostMessage>) => {
      if (event.data?.protocolVersion !== protocolVersion) return;
      const nextPayload = event.data.payload ?? {};
      if (event.data.type === "cad.tableRead" || event.data.type === "image.tableRead") {
        setBusy(false);
        setError(null);
        if (nextPayload.cancelled === true) return;
        if (nextPayload.deferredStandalonePayload === true) {
          setBusy(true);
          return;
        }
        const imported = nextPayload.table as ArchitectureTable | undefined;
        if (!imported?.tableId || !Array.isArray(imported.columns) || !Array.isArray(imported.rows)) {
          window.alert("表格识别结果不完整，未打开 CE。");
          return;
        }
        setTable(imported);
        setPayload(nextPayload);
        // Keep this acknowledgement synchronous so CE startup never waits on a
        // browser timer that WebView2 may throttle.
        revealCadTableEditor(bridge);
      } else if (event.data.type === "cad.table.templatesChanged") {
        setBusy(false);
        setPayload((current) => current ? { ...current, cadTableTemplates: nextPayload.cadTableTemplates } : current);
      } else if (["cad.tableInserted", "cad.tableLocated", "table.xlsxExported"].includes(event.data.type)) {
        setBusy(false);
      } else if (event.data.type === "project.error") {
        setBusy(false);
        setError(String(nextPayload.message ?? "未知错误"));
      }
    };
    bridge.addEventListener("message", onMessage);
    bridge.postMessage(createReadyMessage(createMessageId("ce")));
    return () => bridge.removeEventListener("message", onMessage);
  }, []);

  const closeWindow = () => post("cad.table.window.close");
  if (error) return <main className="cad-table-entry-loading cad-table-entry-error">{error}</main>;
  if (!table || !payload) return <main className="cad-table-entry-loading">正在准备 CAD 表格编辑/Excel…</main>;

  const run = (type: Parameters<typeof createProjectMessage>[0], nextPayload: Record<string, unknown> = {}) => {
    if (busy) return;
    setBusy(true);
    post(type, nextPayload);
  };

  return <main className="app-shell cad-table-standalone"><ProfessionalTableEditor
      value={[table]}
      fields={[]}
      selectedTableId={table.tableId}
      cadBusy={busy}
      hasOriginalCadSize={payload.hasOriginalCadSize === true}
      suggestedInsertType={payload.suggestedInsertType === "tianzheng" ? "tianzheng" : "autocad"}
      cadStandalone
      cadEditorPayload={payload}
      onExportXlsx={(nextTable) => run("table.xlsx.export", { table: nextTable })}
      onLocateCadSources={(drawingPath, handles) => run("cad.table.locate", { drawingPath, handles })}
      onRepickCadTable={() => run("cad.table.repick")}
      onPickCadTable={() => run("cad.table.pick")}
      onPickCadObjects={(nextTable, row, column) => run("cad.table.cellObjects.pick", {
        ...payload, table: nextTable, pendingCadObjectRow: row, pendingCadObjectColumn: column,
      })}
      onSaveCadTemplate={(name, nextTable) => run("cad.table.template.save", {
        name, editorPayload: { ...payload, table: nextTable },
      })}
      onDeleteCadTemplate={(id) => run("cad.table.template.delete", { id })}
      onOpenCadTemplate={(templatePayload) => {
        const { sourceEdit: _sourceEdit, ...withoutUpdateTarget } = payload;
        const nextPayload = { ...withoutUpdateTarget, ...templatePayload, standaloneEditor: true, sourceEdit: undefined } as Record<string, unknown>;
        const nextTable = nextPayload.table as ArchitectureTable | undefined;
        if (nextTable) setTable(nextTable);
        setPayload(nextPayload);
      }}
      onInsertCad={(nextTable: ArchitectureTable, options: CadTableInsertOptions) => run("cad.table.insert", {
        ...payload, table: nextTable, cadInsertOptions: options,
      })}
      onSave={() => undefined}
      onClose={closeWindow}
    /></main>;
}
