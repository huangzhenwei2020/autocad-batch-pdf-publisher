export const protocolVersion = 1;

export type HostCommand =
  | "project.new" | "project.open" | "project.openRecent" | "project.save" | "project.saveAs"
  | "project.historyList" | "project.historyLoad" | "project.historyRestore" | "review.run"
  | "cad.frame.pick" | "cad.text.read" | "cad.table.read" | "image.table.read" | "image.table.cancel"
  | "cad.table.locate" | "cad.table.insert" | "cad.table.repick" | "cad.table.pick"
  | "cad.table.cellObjects.pick" | "cad.table.template.save" | "cad.table.template.delete"
  | "cad.table.visible" | "cad.table.window.close" | "table.xlsx.export" | "cad.section.insert";

export function createMessageId(prefix = "editor") {
  return globalThis.crypto?.randomUUID?.() ?? `${prefix}-${Date.now()}`;
}

export function createReadyMessage(messageId: string) {
  return { protocolVersion, messageId, type: "editor.ready", payload: { phase: 2 } };
}

export function createProjectMessage(type: HostCommand, payload: Record<string, unknown> = {}) {
  return { protocolVersion, messageId: createMessageId(), type, payload };
}
