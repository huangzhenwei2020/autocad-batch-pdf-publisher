import { useEffect, useRef } from "react";
import { baseKeymap, setBlockType, toggleMark, wrapIn } from "prosemirror-commands";
import { history, redo, undo } from "prosemirror-history";
import { keymap } from "prosemirror-keymap";
import { Schema, type DOMOutputSpec, type Node as ProseMirrorNode } from "prosemirror-model";
import { addListNodes, wrapInList } from "prosemirror-schema-list";
import { schema as basicSchema } from "prosemirror-schema-basic";
import { EditorState, NodeSelection } from "prosemirror-state";
import { EditorView } from "prosemirror-view";
import type { ArchitectureTable, ProjectField } from "./editor-model";

function tableDom(table: ArchitectureTable | null, widthPercent = 100): DOMOutputSpec {
  if (!table) return ["figure", { class: "professional-table-node", contenteditable: "false" }, "表格数据不可用"];
  const header = ["tr", ...table.columns.map((column) => ["th", `${column.title}${column.unit ? `（${column.unit}）` : ""}`])];
  const rows = table.rows.map((row) => [
    "tr",
    ...table.columns.map((column) => {
      const cell = row.cells.find((item) => item.columnKey === column.key);
      return ["td", cell?.displayValue ?? ""];
    }),
  ]);
  return [
    "figure",
    {
      class: "professional-table-node",
      contenteditable: "false",
      "data-table-id": table.tableId,
      style: `width:${Math.max(25, Math.min(200, widthPercent))}%`,
    },
    ["figcaption", `${table.tableNumber || ""} ${table.title}`.trim()],
    ["table", ["thead", header], ["tbody", ...rows]],
  ] as unknown as DOMOutputSpec;
}

const nodes = addListNodes(basicSchema.spec.nodes, "paragraph block*", "block").append({
  projectField: {
    inline: true,
    group: "inline",
    atom: true,
    selectable: true,
    attrs: {
      path: { default: "" },
      label: { default: "项目字段" },
      value: { default: "" },
      unit: { default: "" },
    },
    toDOM(node) {
      const text = `${node.attrs.value || "待填写"}${node.attrs.unit || ""}`;
      return [
        "span",
        {
          class: "project-field-node",
          "data-field-path": node.attrs.path,
          title: `${node.attrs.label} · ${node.attrs.path}`,
          contenteditable: "false",
        },
        text,
      ];
    },
    parseDOM: [
      {
        tag: "span[data-field-path]",
        getAttrs(element) {
          const html = element as HTMLElement;
          return {
            path: html.dataset.fieldPath ?? "",
            label: html.title.split(" · ")[0] ?? "项目字段",
            value: html.textContent ?? "",
            unit: "",
          };
        },
      },
    ],
  },
  standardCitation: {
    inline: true,
    group: "inline",
    atom: true,
    selectable: true,
    attrs: {
      code: { default: "" },
      name: { default: "" },
    },
    toDOM(node) {
      return [
        "span",
        {
          class: "standard-citation-node",
          title: "规范引用仅保存元数据，不内置规范全文",
          contenteditable: "false",
        },
        `《${node.attrs.name}》${node.attrs.code ? `（${node.attrs.code}）` : ""}`,
      ];
    },
    parseDOM: [{ tag: "span.standard-citation-node" }],
  },
  professionalTable: {
    group: "block",
    atom: true,
    selectable: true,
    attrs: {
      table: { default: null },
      widthPercent: { default: 100 },
    },
    toDOM(node) {
      return tableDom(
        node.attrs.table as ArchitectureTable | null,
        Number(node.attrs.widthPercent) || 100,
      );
    },
    parseDOM: [{ tag: "figure.professional-table-node" }],
  },
});

export const editorSchema = new Schema({
  nodes,
  marks: basicSchema.spec.marks,
});

type EditorCommand = (view: EditorView) => boolean;

export type ProseMirrorEditorHandle = {
  command(command: EditorCommand): boolean;
  insertField(field: ProjectField): void;
  insertStandard(code: string, name: string): void;
  insertStandards(standards: Array<{ code: string; name: string }>): void;
  insertTable(table: ArchitectureTable): void;
  resizeSelectedTable(deltaPercent: number): boolean;
  deleteSelectedTable(): boolean;
  selectedTableId(): string;
  synchronizeTables(tables: ArchitectureTable[]): number;
  replaceAll(search: string, replacement: string): number;
  focus(): void;
};

type Props = {
  sectionId: string;
  content: Record<string, unknown>;
  onReady(handle: ProseMirrorEditorHandle): void;
  onChange(content: Record<string, unknown>, plainText: string): void;
  onEditTable?(tableId: string): void;
};

function createHandle(view: EditorView): ProseMirrorEditorHandle {
  return {
    command(command) {
      const applied = command(view);
      if (applied) {
        view.focus();
      }
      return applied;
    },
    insertField(field) {
      const node = editorSchema.nodes.projectField.create({
        path: field.path,
        label: field.label,
        value: field.value,
        unit: field.unit ?? "",
      });
      view.dispatch(view.state.tr.replaceSelectionWith(node).scrollIntoView());
      view.focus();
    },
    insertStandard(code, name) {
      const node = editorSchema.nodes.standardCitation.create({ code, name });
      view.dispatch(view.state.tr.replaceSelectionWith(node).scrollIntoView());
      view.focus();
    },
    insertStandards(standards) {
      if (standards.length === 0) return;
      const items = standards.map((standard) => {
        const citation = editorSchema.nodes.standardCitation.create({ code: standard.code, name: standard.name });
        return editorSchema.nodes.list_item.create(null, editorSchema.nodes.paragraph.create(null, citation));
      });
      const list = editorSchema.nodes.ordered_list.create(null, items);
      view.dispatch(view.state.tr.replaceSelectionWith(list).scrollIntoView());
      view.focus();
    },
    insertTable(table) {
      const node = editorSchema.nodes.professionalTable.create({ table, widthPercent: 100 });
      view.dispatch(view.state.tr.replaceSelectionWith(node).scrollIntoView());
      view.focus();
    },
    resizeSelectedTable(deltaPercent) {
      const selection = view.state.selection;
      if (!(selection instanceof NodeSelection) || selection.node.type !== editorSchema.nodes.professionalTable) {
        return false;
      }
      const current = Number(selection.node.attrs.widthPercent) || 100;
      const widthPercent = Math.max(25, Math.min(200, current + deltaPercent));
      view.dispatch(view.state.tr.setNodeMarkup(selection.from, undefined, {
        ...selection.node.attrs,
        widthPercent,
      }).scrollIntoView());
      view.focus();
      return true;
    },
    deleteSelectedTable() {
      const selection = view.state.selection;
      if (!(selection instanceof NodeSelection) || selection.node.type !== editorSchema.nodes.professionalTable) {
        return false;
      }
      view.dispatch(view.state.tr.delete(selection.from, selection.to).scrollIntoView());
      view.focus();
      return true;
    },
    selectedTableId() {
      const selection = view.state.selection;
      if (!(selection instanceof NodeSelection) || selection.node.type !== editorSchema.nodes.professionalTable) {
        return "";
      }
      return String((selection.node.attrs.table as ArchitectureTable | null)?.tableId ?? "");
    },
    synchronizeTables(tables) {
      const byId = new Map(tables.map((table) => [table.tableId, table]));
      const updates: Array<{ position: number; node: ProseMirrorNode; table: ArchitectureTable }> = [];
      view.state.doc.descendants((node, position) => {
        if (node.type !== editorSchema.nodes.professionalTable) return;
        const tableId = String((node.attrs.table as ArchitectureTable | null)?.tableId ?? "");
        const table = byId.get(tableId);
        if (table) updates.push({ position, node, table });
      });
      if (!updates.length) return 0;
      let transaction = view.state.tr;
      for (const update of updates) {
        transaction = transaction.setNodeMarkup(update.position, undefined, {
          ...update.node.attrs,
          table: update.table,
        });
      }
      view.dispatch(transaction);
      return updates.length;
    },
    replaceAll(search, replacement) {
      if (!search) {
        return 0;
      }

      const replacements: Array<{ from: number; to: number }> = [];
      view.state.doc.descendants((node, position) => {
        if (!node.isText || !node.text) {
          return;
        }
        let offset = node.text.indexOf(search);
        while (offset >= 0) {
          replacements.push({
            from: position + offset,
            to: position + offset + search.length,
          });
          offset = node.text.indexOf(search, offset + search.length);
        }
      });

      let transaction = view.state.tr;
      for (const item of replacements.reverse()) {
        transaction = transaction.insertText(replacement, item.from, item.to);
      }
      if (replacements.length > 0) {
        view.dispatch(transaction.scrollIntoView());
      }
      view.focus();
      return replacements.length;
    },
    focus() {
      view.focus();
    },
  };
}

export function ProseMirrorEditor({ sectionId, content, onReady, onChange, onEditTable }: Props) {
  const mountRef = useRef<HTMLDivElement>(null);
  const editTableRef = useRef(onEditTable);

  useEffect(() => {
    editTableRef.current = onEditTable;
  }, [onEditTable]);

  useEffect(() => {
    if (!mountRef.current) {
      return;
    }

    let documentNode: ProseMirrorNode;
    try {
      documentNode = editorSchema.nodeFromJSON(content);
    } catch {
      documentNode = editorSchema.topNodeType.createAndFill()!;
    }

    const state = EditorState.create({
      schema: editorSchema,
      doc: documentNode,
      plugins: [
        history(),
        keymap({
          "Mod-z": undo,
          "Mod-y": redo,
          "Shift-Mod-z": redo,
        }),
        keymap(baseKeymap),
      ],
    });

    const view = new EditorView(mountRef.current, {
      state,
      dispatchTransaction(transaction) {
        const nextState = view.state.apply(transaction);
        view.updateState(nextState);
        if (transaction.docChanged) {
          onChange(nextState.doc.toJSON(), nextState.doc.textContent);
        }
      },
      attributes: {
        class: "spec-editor-document",
        spellcheck: "false",
        "aria-label": "建筑设计说明正文编辑区",
      },
      handleDOMEvents: {
        dblclick(_view, event) {
          const target = event.target as Element | null;
          const figure = target?.closest?.("figure.professional-table-node") as HTMLElement | null;
          const tableId = figure?.dataset.tableId;
          if (!tableId) return false;
          editTableRef.current?.(tableId);
          return true;
        },
      },
    });

    onReady(createHandle(view));
    return () => view.destroy();
  }, [sectionId]);

  return <div className="editor-mount" ref={mountRef} />;
}

export const editorCommands = {
  undo: (view: EditorView) => undo(view.state, view.dispatch),
  redo: (view: EditorView) => redo(view.state, view.dispatch),
  bold: (view: EditorView) =>
    toggleMark(editorSchema.marks.strong)(view.state, view.dispatch),
  heading1: (view: EditorView) =>
    setBlockType(editorSchema.nodes.heading, { level: 1 })(view.state, view.dispatch),
  heading2: (view: EditorView) =>
    setBlockType(editorSchema.nodes.heading, { level: 2 })(view.state, view.dispatch),
  heading3: (view: EditorView) =>
    setBlockType(editorSchema.nodes.heading, { level: 3 })(view.state, view.dispatch),
  paragraph: (view: EditorView) =>
    setBlockType(editorSchema.nodes.paragraph)(view.state, view.dispatch),
  bulletList: (view: EditorView) =>
    wrapInList(editorSchema.nodes.bullet_list)(view.state, view.dispatch),
  orderedList: (view: EditorView) =>
    wrapInList(editorSchema.nodes.ordered_list)(view.state, view.dispatch),
  blockquote: (view: EditorView) =>
    wrapIn(editorSchema.nodes.blockquote)(view.state, view.dispatch),
};
