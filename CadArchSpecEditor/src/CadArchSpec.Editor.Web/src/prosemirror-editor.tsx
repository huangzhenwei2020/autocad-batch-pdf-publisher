import { useEffect, useRef } from "react";
import { baseKeymap, setBlockType, toggleMark, wrapIn } from "prosemirror-commands";
import { history, redo, undo } from "prosemirror-history";
import { keymap } from "prosemirror-keymap";
import { Schema, type Node as ProseMirrorNode } from "prosemirror-model";
import { addListNodes, wrapInList } from "prosemirror-schema-list";
import { schema as basicSchema } from "prosemirror-schema-basic";
import { EditorState } from "prosemirror-state";
import { EditorView } from "prosemirror-view";
import type { ProjectField } from "./editor-model";

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
  replaceAll(search: string, replacement: string): number;
  focus(): void;
};

type Props = {
  sectionId: string;
  content: Record<string, unknown>;
  onReady(handle: ProseMirrorEditorHandle): void;
  onChange(content: Record<string, unknown>, plainText: string): void;
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

export function ProseMirrorEditor({ sectionId, content, onReady, onChange }: Props) {
  const mountRef = useRef<HTMLDivElement>(null);

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
  heading2: (view: EditorView) =>
    setBlockType(editorSchema.nodes.heading, { level: 2 })(view.state, view.dispatch),
  paragraph: (view: EditorView) =>
    setBlockType(editorSchema.nodes.paragraph)(view.state, view.dispatch),
  bulletList: (view: EditorView) =>
    wrapInList(editorSchema.nodes.bullet_list)(view.state, view.dispatch),
  orderedList: (view: EditorView) =>
    wrapInList(editorSchema.nodes.ordered_list)(view.state, view.dispatch),
  blockquote: (view: EditorView) =>
    wrapIn(editorSchema.nodes.blockquote)(view.state, view.dispatch),
};
