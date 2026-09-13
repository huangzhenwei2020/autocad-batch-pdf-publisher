import {
  useEffect,
  useMemo,
  useState,
  type ButtonHTMLAttributes,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from "react";
import {
  AlignCenter,
  AlignLeft,
  AlignRight,
  BoxSelect,
  Calculator,
  Columns3,
  FileDown,
  FileSpreadsheet,
  FolderOpen,
  ImageMinus,
  ImagePlus,
  Link2,
  LocateFixed,
  Merge,
  Pencil,
  Plus,
  Redo2,
  RotateCcw,
  Rows3,
  Save,
  Sigma,
  Split,
  Table2,
  TableProperties,
  Trash2,
  Undo2,
  Unlink2,
} from "lucide-react";
import type {
  ArchitectureTable,
  ArchitectureTableColumn,
  ProjectField,
  ProfessionalTableType,
} from "./editor-model";
import {
  applyTableFormula,
  bindTableCellToProjectField,
  createEmptyCell,
  createEmptyRow,
  createProfessionalTableTemplate,
  deleteTableColumn,
  deleteTableRow,
  getSelectionBounds,
  isCellInSelection,
  mergeSelectedCells,
  normalizeProfessionalTable,
  pasteTableCells,
  recalculateTechnicalTable,
  setTableCellValue,
  splitMergedCell,
  synchronizeBoundTableCells,
  tableToCsv,
  type TableBindingSyncResult,
  type TableSelection,
  validateProfessionalTable,
} from "./professional-tables";
import { recalculateSpreadsheetTable } from "./spreadsheet-formulas";

type Props = {
  value: ArchitectureTable[];
  fields: ProjectField[];
  selectedTableId?: string;
  onExportXlsx?(table: ArchitectureTable): void;
  onLocateCadSources?(drawingPath: string, handles: string[]): void;
  onInsertCad?(table: ArchitectureTable, options: CadTableInsertOptions): void;
  cadBusy?: boolean;
  hasOriginalCadSize?: boolean;
  suggestedInsertType?: "autocad" | "tianzheng";
  cadStandalone?: boolean;
  cadEditorPayload?: Record<string, unknown>;
  onRepickCadTable?(): void;
  onPickCadTable?(): void;
  onPickCadObjects?(table: ArchitectureTable, row: number, column: number): void;
  onSaveCadTemplate?(name: string, table: ArchitectureTable): void;
  onDeleteCadTemplate?(id: string): void;
  onOpenCadTemplate?(payload: Record<string, unknown>): void;
  onSave(value: ArchitectureTable[]): void;
  onClose(): void;
};

export type CadTableInsertOptions = {
  insertType: "autocad" | "tianzheng";
  useOriginalCadSize: boolean;
  scale: number;
  textHeightMillimeters: number;
  textStyle?: string;
};

const cloneTables = (tables: ArchitectureTable[]) =>
  tables.map((table) => ({
    ...table,
    columns: table.columns.map((column) => ({ ...column })),
    rows: table.rows.map((row) => ({
      ...row,
      cells: row.cells.map((cell) => ({ ...cell })),
    })),
    formulaAudits: [...table.formulaAudits],
  }));

const newColumnKey = (table: ArchitectureTable) => {
  let index = table.columns.length;
  while (table.columns.some((column) => column.key === spreadsheetColumnName(index))) index += 1;
  return spreadsheetColumnName(index);
};

const aciColor = (value?: number | null) => {
  const colors: Record<number, string> = { 1: "#ff0000", 2: "#ffff00", 3: "#00ff00", 4: "#00ffff", 5: "#0000ff", 6: "#ff00ff", 7: "#111111", 8: "#777777", 9: "#bbbbbb" };
  return colors[value ?? 7] ?? `hsl(${((value ?? 7) * 137.508) % 360} 70% 42%)`;
};

const spreadsheetColumnName = (index: number) => {
  let value = index + 1; let name = "";
  while (value > 0) { value--; name = String.fromCharCode(65 + value % 26) + name; value = Math.floor(value / 26); }
  return name;
};

function RibbonIconButton({ label, children, className = "", ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { label: string; children: ReactNode }) {
  return <button {...props} className={`ribbon-icon-button ${className}`.trim()} aria-label={label} title={label} data-tooltip={label}>{children}</button>;
}

export function ProfessionalTableEditor({ value, fields, selectedTableId, onExportXlsx, onLocateCadSources, onInsertCad, cadBusy = false, hasOriginalCadSize = false, suggestedInsertType = "autocad", cadStandalone = false, cadEditorPayload, onRepickCadTable, onPickCadTable, onPickCadObjects, onSaveCadTemplate, onDeleteCadTemplate, onOpenCadTemplate, onSave, onClose }: Props) {
  const initial = value.map(normalizeProfessionalTable);
  const cadDefaults = (cadEditorPayload?.cadTableDefaults ?? {}) as Record<string, unknown>;
  const [tables, setTables] = useState<ArchitectureTable[]>(initial);
  const [selectedId, setSelectedId] = useState(
    initial.some((table) => table.tableId === selectedTableId)
      ? selectedTableId!
      : initial[0]?.tableId ?? "",
  );
  const [selectedCell, setSelectedCell] = useState({ row: 0, column: 0 });
  const [selection, setSelection] = useState<TableSelection>({
    startRow: 0,
    startColumn: 0,
    endRow: 0,
    endColumn: 0,
  });
  const [formulaDraft, setFormulaDraft] = useState("");
  const [formulaError, setFormulaError] = useState("");
  const [syncNotice, setSyncNotice] = useState("");
  const [pendingSync, setPendingSync] = useState<TableBindingSyncResult | null>(null);
  const [undoStack, setUndoStack] = useState<ArchitectureTable[][]>([]);
  const [redoStack, setRedoStack] = useState<ArchitectureTable[][]>([]);
  const [cadOptions, setCadOptions] = useState<CadTableInsertOptions>({
    insertType: suggestedInsertType === "tianzheng" || cadDefaults.insertType === "tianzheng" ? "tianzheng" : "autocad",
    useOriginalCadSize: hasOriginalCadSize && cadDefaults.useOriginalCadSize !== false,
    scale: Math.max(0.01, Number(cadDefaults.scale) || 1),
    textHeightMillimeters: Math.max(0.1, Number(cadDefaults.textHeightMillimeters) || 3.5),
    textStyle: String(cadDefaults.textStyle ?? cadEditorPayload?.currentTextStyle ?? "Standard"),
  });
  const [templateId, setTemplateId] = useState("");
  const [cellEditorOpen, setCellEditorOpen] = useState(false);
  const [cellEditorValue, setCellEditorValue] = useState("");
  const [formulaBuilderOpen, setFormulaBuilderOpen] = useState(false);
  const [formulaFunction, setFormulaFunction] = useState("SUM");
  const [startChooserOpen, setStartChooserOpen] = useState(cadStandalone && cadEditorPayload?.showStartChooser === true);
  const [ribbonTab, setRibbonTab] = useState<"home" | "layout" | "format" | "formula" | "cad">("home");
  const [dragSelecting, setDragSelecting] = useState(false);
  const [previewZoom, setPreviewZoom] = useState(() => {
    const stored = Number(window.localStorage.getItem("cad-table-preview-zoom"));
    return Number.isFinite(stored) && stored >= 10 && stored <= 400 ? stored : 100;
  });
  const [blankTableOpen, setBlankTableOpen] = useState(false);
  const [blankRows, setBlankRows] = useState(8);
  const [blankColumns, setBlankColumns] = useState(4);
  const selected = tables.find((table) => table.tableId === selectedId) ?? null;
  const issues = useMemo(
    () => (selected ? validateProfessionalTable(selected) : []),
    [selected],
  );

  const commit = (next: ArchitectureTable[]) => {
    setUndoStack((stack) => [...stack.slice(-39), cloneTables(tables)]);
    setRedoStack([]);
    setTables(next);
  };

  const updateSelected = (update: (table: ArchitectureTable) => ArchitectureTable) => {
    if (!selected) return;
    commit(
      tables.map((table) =>
        table.tableId === selected.tableId
          ? normalizeProfessionalTable(update(table))
          : table,
      ),
    );
  };

  const addTemplate = (tableType: ProfessionalTableType) => {
    const next = createProfessionalTableTemplate(tableType, `表${tables.length + 1}`);
    commit([...tables, next]);
    setSelectedId(next.tableId);
    setSelectedCell({ row: 0, column: 0 });
    setSelection({ startRow: 0, startColumn: 0, endRow: 0, endColumn: 0 });
    setFormulaDraft("");
  };

  const undo = () => {
    const previous = undoStack[undoStack.length - 1];
    if (!previous) return;
    setRedoStack((stack) => [...stack, cloneTables(tables)]);
    setTables(previous);
    setUndoStack((stack) => stack.slice(0, -1));
  };

  const redo = () => {
    const next = redoStack[redoStack.length - 1];
    if (!next) return;
    setUndoStack((stack) => [...stack, cloneTables(tables)]);
    setTables(next);
    setRedoStack((stack) => stack.slice(0, -1));
  };

  const addColumn = () =>
    updateSelected((table) => {
      const definition: ArchitectureTableColumn = {
        key: newColumnKey(table),
        title: newColumnKey(table),
        unit: "",
        widthMillimeters: 24,
        decimalPlaces: 0,
        required: false,
      };
      return {
        ...table,
        columns: [...table.columns, definition],
        rows: table.rows.map((row) => ({
          ...row,
          cells: [...row.cells, createEmptyCell(definition)],
        })),
      };
    });

  const removeSelectedColumn = () => {
    if (!selected || selected.columns.length <= 2) {
      window.alert("表格至少保留两列。");
      return;
    }
    const definition = selected.columns[selectedCell.column];
    if (!definition || !window.confirm(`删除“${definition.title}”整列吗？`)) return;
    updateSelected((table) => deleteTableColumn(table, selectedCell.column));
    setSelectedCell((cell) => ({
      row: cell.row,
      column: Math.max(0, Math.min(cell.column - 1, selected.columns.length - 2)),
    }));
  };

  const exportCsv = () => {
    if (!selected) return;
    const blob = new Blob([`\ufeff${tableToCsv(selected)}`], {
      type: "text/csv;charset=utf-8",
    });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = `${selected.tableNumber || "表格"}_${selected.title || "专业表格"}.csv`;
    anchor.click();
    URL.revokeObjectURL(url);
  };

  const selectedColumn = selected?.columns[selectedCell.column];
  const activeCell = selected?.rows[selectedCell.row]?.cells[selectedCell.column];
  const selectionBounds = getSelectionBounds(selection);
  const selectedCellCount =
    (selectionBounds.lastRow - selectionBounds.firstRow + 1) *
    (selectionBounds.lastColumn - selectionBounds.firstColumn + 1);
  const selectionAddress = `${spreadsheetColumnName(selectionBounds.firstColumn)}${selectionBounds.firstRow + 1}` +
    (selectedCellCount > 1 ? `:${spreadsheetColumnName(selectionBounds.lastColumn)}${selectionBounds.lastRow + 1}` : "");

  const cadTemplates = Array.isArray(cadEditorPayload?.cadTableTemplates)
    ? cadEditorPayload.cadTableTemplates as Array<{ id: string; name: string; updatedAt?: string; payload?: { table?: ArchitectureTable } }>
    : [];
  const cadTextStyles = Array.isArray(cadEditorPayload?.cadTextStyles)
    ? cadEditorPayload.cadTextStyles.map(String)
    : ["Standard"];

  useEffect(() => {
    if (!cadStandalone) return;
    const incoming = cadEditorPayload?.table as ArchitectureTable | undefined;
    if (!incoming?.tableId) return;
    setTables((current) => [
      ...current.filter((table) => table.tableId !== incoming.tableId),
      normalizeProfessionalTable(incoming),
    ]);
    setSelectedId(incoming.tableId);
    setSelectedCell({ row: 0, column: 0 });
    setSelection({ startRow: 0, startColumn: 0, endRow: 0, endColumn: 0 });
    if (cadEditorPayload?.showStartChooser !== true) setStartChooserOpen(false);
  }, [cadEditorPayload?.table, cadStandalone]);

  useEffect(() => {
    if (!cadStandalone) return;
    const handler = (event: KeyboardEvent) => {
      if (!event.ctrlKey || event.key.toLowerCase() !== "z") return;
      event.preventDefault();
      if (event.shiftKey) redo(); else undo();
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  });

  useEffect(() => {
    if (!dragSelecting) return;
    const end = () => setDragSelecting(false);
    window.addEventListener("pointerup", end, { once: true });
    return () => window.removeEventListener("pointerup", end);
  }, [dragSelecting]);

  const patchSelectedCells = (patch: Partial<NonNullable<typeof activeCell>>) => {
    updateSelected((table) => ({
      ...table,
      rows: table.rows.map((row, rowIndex) => ({
        ...row,
        cells: row.cells.map((cell, columnIndex) =>
          rowIndex >= selectionBounds.firstRow && rowIndex <= selectionBounds.lastRow &&
          columnIndex >= selectionBounds.firstColumn && columnIndex <= selectionBounds.lastColumn &&
          cell.rowSpan > 0 && cell.columnSpan > 0 ? { ...cell, ...patch } : cell),
      })),
    }));
  };

  const createBlankTable = () => {
    const rowCount = Math.max(1, Math.min(500, Math.trunc(blankRows) || 1));
    const columnCount = Math.max(1, Math.min(100, Math.trunc(blankColumns) || 1));
    const base = createProfessionalTableTemplate("custom", `表${tables.length + 1}`);
    const columns: ArchitectureTableColumn[] = Array.from({ length: columnCount }, (_, index) => ({
      key: spreadsheetColumnName(index),
      title: spreadsheetColumnName(index),
      unit: "",
      widthMillimeters: 24,
      decimalPlaces: 0,
      required: false,
    }));
    const next: ArchitectureTable = {
      ...base,
      title: "空白表格",
      columns,
      rows: Array.from({ length: rowCount }, () => createEmptyRow(columns)),
    };
    commit([...tables, next]);
    setSelectedId(next.tableId);
    setSelectedCell({ row: 0, column: 0 });
    setSelection({ startRow: 0, startColumn: 0, endRow: 0, endColumn: 0 });
    setFormulaDraft("");
    setBlankTableOpen(false);
    setStartChooserOpen(false);
  };

  const changePreviewZoom = (value: number) => {
    const next = Math.max(10, Math.min(400, Math.round(value)));
    setPreviewZoom(next);
    window.localStorage.setItem("cad-table-preview-zoom", String(next));
  };

  const linkSelectedCells = () => {
    if (!activeCell || selectedCellCount < 2) return;
    const linkGroupId = activeCell.linkGroupId || `link-${globalThis.crypto?.randomUUID?.() ?? Date.now()}`;
    patchSelectedCells({ linkGroupId, displayValue: activeCell.displayValue, formula: activeCell.formula });
  };

  const editCellValue = (value: string) => {
    updateSelected((table) => {
      let updated = setTableCellValue(table, selectedCell.row, selectedCell.column, value);
      const source = updated.rows[selectedCell.row]?.cells[selectedCell.column];
      if (source?.linkGroupId) updated = {
          ...updated,
          rows: updated.rows.map((row) => ({ ...row, cells: row.cells.map((cell) =>
            cell.cellId !== source.cellId && cell.linkGroupId === source.linkGroupId
              ? { ...cell, displayValue: source.displayValue, formula: source.formula }
              : cell) })),
        };
      if (!cadStandalone) return updated;
      try { return recalculateSpreadsheetTable(updated); } catch { return updated; }
    });
  };

  const calculateFormula = () => {
    try {
      updateSelected((table) => {
        if (!cadStandalone) return applyTableFormula(table, selectedCell.row, selectedCell.column, formulaDraft);
        const withFormula = {
          ...table,
          rows: table.rows.map((row, rowIndex) => ({ ...row, cells: row.cells.map((cell, columnIndex) =>
            rowIndex === selectedCell.row && columnIndex === selectedCell.column
              ? { ...cell, formula: formulaDraft.startsWith("=") ? formulaDraft : `=${formulaDraft}` }
              : cell) })),
        };
        return recalculateSpreadsheetTable(withFormula);
      });
      setFormulaError("");
      return true;
    } catch (error) {
      setFormulaError(error instanceof Error ? error.message : "公式计算失败。");
      return false;
    }
  };

  const openCadTemplate = (templateIdToOpen: string) => {
    const template = cadTemplates.find((item) => item.id === templateIdToOpen);
    const table = template?.payload?.table;
    if (!table) return;
    const opened = normalizeProfessionalTable({ ...table, tableId: selected?.tableId ?? table.tableId });
    commit([opened]);
    setSelectedId(opened.tableId);
    setSelectedCell({ row: 0, column: 0 });
    setSelection({ startRow: 0, startColumn: 0, endRow: 0, endColumn: 0 });
    setStartChooserOpen(false);
    onOpenCadTemplate?.({ ...(template?.payload ?? {}), table: opened });
  };

  const setSelectedAlignment = (alignment: "left" | "center" | "right") => {
    updateSelected((table) => ({
      ...table,
      rows: table.rows.map((row, rowIndex) => ({
        ...row,
        cells: row.cells.map((cell, columnIndex) =>
          rowIndex >= selectionBounds.firstRow && rowIndex <= selectionBounds.lastRow &&
          columnIndex >= selectionBounds.firstColumn && columnIndex <= selectionBounds.lastColumn
            ? { ...cell, alignment }
            : cell,
        ),
      })),
    }));
  };

  const resizeColumn = (columnIndex: number, event: ReactPointerEvent<HTMLSpanElement>) => {
    if (!selected) return;
    event.preventDefault();
    event.stopPropagation();
    const startX = event.clientX;
    const startWidth = selected.columns[columnIndex]?.widthMillimeters ?? 24;
    const before = cloneTables(tables);
    let changed = false;
    const move = (moveEvent: PointerEvent) => {
      const width = Math.max(4, Math.round((startWidth + (moveEvent.clientX - startX) / 3) * 10) / 10);
      changed = Math.abs(width - startWidth) >= .1;
      setTables((current) => current.map((table) => table.tableId !== selected.tableId ? table : {
        ...table,
        columns: table.columns.map((column, index) => index === columnIndex ? { ...column, widthMillimeters: width } : column),
      }));
    };
    const end = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", end);
      if (changed) {
        setUndoStack((stack) => [...stack.slice(-39), before]);
        setRedoStack([]);
      }
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", end);
  };

  const resizeRow = (rowIndex: number, event: ReactPointerEvent<HTMLSpanElement>) => {
    if (!selected) return;
    event.preventDefault();
    event.stopPropagation();
    const startY = event.clientY;
    const startHeight = selected.rows[rowIndex]?.heightMillimeters ?? 8;
    const before = cloneTables(tables);
    let changed = false;
    const move = (moveEvent: PointerEvent) => {
      const height = Math.max(3, Math.round((startHeight + (moveEvent.clientY - startY) / 3) * 10) / 10);
      changed = Math.abs(height - startHeight) >= .1;
      setTables((current) => current.map((table) => table.tableId !== selected.tableId ? table : {
        ...table,
        rows: table.rows.map((row, index) => index === rowIndex ? { ...row, heightMillimeters: height } : row),
      }));
    };
    const end = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", end);
      if (changed) {
        setUndoStack((stack) => [...stack.slice(-39), before]);
        setRedoStack([]);
      }
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", end);
  };

  const equalizeSelectedColumns = () => updateSelected((table) => {
    const first = selectionBounds.firstColumn;
    const last = selectionBounds.lastColumn;
    const average = table.columns.slice(first, last + 1)
      .reduce((sum, column) => sum + column.widthMillimeters, 0) / Math.max(1, last - first + 1);
    return { ...table, columns: table.columns.map((column, index) =>
      index >= first && index <= last ? { ...column, widthMillimeters: Math.round(average * 10) / 10 } : column) };
  });

  const equalizeSelectedRows = () => updateSelected((table) => {
    const first = selectionBounds.firstRow;
    const last = selectionBounds.lastRow;
    const average = table.rows.slice(first, last + 1)
      .reduce((sum, row) => sum + (row.heightMillimeters ?? 8), 0) / Math.max(1, last - first + 1);
    return { ...table, rows: table.rows.map((row, index) =>
      index >= first && index <= last ? { ...row, heightMillimeters: Math.round(average * 10) / 10 } : row) };
  });

  const selectCell = (
    row: number,
    column: number,
    extendSelection: boolean,
  ) => {
    setSelectedCell({ row, column });
    setSelection((current) =>
      extendSelection
        ? { ...current, endRow: row, endColumn: column }
        : {
            startRow: row,
            startColumn: column,
            endRow: row,
            endColumn: column,
          },
    );
    setFormulaDraft(selected?.rows[row]?.cells[column]?.formula ?? "");
    setFormulaError("");
  };

  const applySyncResult = (result: TableBindingSyncResult) => {
    if (result.updatedCount > 0 || result.skippedConflicts.length > 0) {
      commit(result.tables);
    }
    const missing = result.missingFieldPaths.length
      ? `；${result.missingFieldPaths.length} 个字段路径已不存在`
      : "";
    const skipped = result.skippedConflicts.length
      ? `，保留 ${result.skippedConflicts.length} 个人工修改`
      : "";
    setSyncNotice(
      result.updatedCount || skipped || missing
        ? `已同步 ${result.updatedCount} 个单元格${skipped}${missing}`
        : "当前没有需要同步的绑定单元格",
    );
    setPendingSync(null);
  };

  return (
    <div className="dialog-backdrop table-dialog-backdrop" onMouseDown={onClose}>
      <section className={cadStandalone ? "professional-table-dialog cad-table-editor-dialog" : "professional-table-dialog"} onMouseDown={(event) => event.stopPropagation()}>
        <header>
          <div>
            {!cadStandalone && <span className="eyebrow">阶段 2 · 专业表格</span>}
            <h2>{cadStandalone ? "CAD 表格编辑 / Excel" : "建筑专业表格编辑器"}</h2>
          </div>
          <button className="dialog-close" onClick={onClose}>×</button>
        </header>

        {cadStandalone && startChooserOpen && (
          <div className="cad-table-start-overlay">
            <section className="cad-table-start-dialog" role="dialog" aria-label="选择 CAD 表格编辑方式">
              <header>
                <div>
                  <h3>选择表格来源</h3>
                  <p>先选择本次要编辑的表格，进入后仍可重新拾取或打开其他常用表格。</p>
                </div>
              </header>
              <div className="cad-table-start-actions">
                <button className="cad-table-start-action" disabled={!onPickCadTable || cadBusy} onClick={() => { setStartChooserOpen(false); onPickCadTable?.(); }}>
                  <BoxSelect />
                  <strong>拾取 CAD 表格</strong>
                  <span>框选线框、文字或普通 CAD 表格，修改后作为新表格插入。</span>
                </button>
                <button className="cad-table-start-action" disabled={!onRepickCadTable || cadBusy} onClick={() => { setStartChooserOpen(false); onRepickCadTable?.(); }}>
                  <TableProperties />
                  <strong>拾取现有表格修改</strong>
                  <span>读取已有表格，修改完成后原位更新当前表格。</span>
                </button>
                <button className="cad-table-start-action" onClick={() => { setStartChooserOpen(false); setBlankTableOpen(true); }}>
                  <Table2 />
                  <strong>创建空白表格</strong>
                  <span>指定行数和列数，从空表开始编辑并插入 CAD。</span>
                </button>
              </div>
              <div className="cad-table-start-library">
                <label htmlFor="cad-start-template">打开以前保存的常用表格</label>
                <div>
                  <select id="cad-start-template" value={templateId} onChange={(event) => setTemplateId(event.target.value)}>
                    <option value="">{cadTemplates.length ? "请选择常用表格" : "暂时没有保存的常用表格"}</option>
                    {cadTemplates.map((template) => <option key={template.id} value={template.id}>{template.name}</option>)}
                  </select>
                  <button className="button primary" disabled={!templateId} onClick={() => openCadTemplate(templateId)}>打开表格</button>
                </div>
              </div>
              <footer>
                <button className="button" onClick={onClose}>取消</button>
              </footer>
            </section>
          </div>
        )}

        {blankTableOpen && (
          <div className="sync-conflict-overlay" onMouseDown={() => setBlankTableOpen(false)}>
            <section className="cad-blank-table-dialog" role="dialog" aria-label="创建空白表格" onMouseDown={(event) => event.stopPropagation()}>
              <header><Table2 /><div><h3>创建空白表格</h3><p>以后仍可继续添加、删除或调整行列尺寸。</p></div></header>
              <div className="cad-blank-table-fields">
                <label><span>行数</span><input autoFocus type="number" min="1" max="500" value={blankRows} onChange={(event) => setBlankRows(Number(event.target.value))} /></label>
                <label><span>列数</span><input type="number" min="1" max="100" value={blankColumns} onChange={(event) => setBlankColumns(Number(event.target.value))} /></label>
              </div>
              <footer>
                <button className="button" onClick={() => setBlankTableOpen(false)}>取消</button>
                <button className="button primary" onClick={createBlankTable}>创建表格</button>
              </footer>
            </section>
          </div>
        )}

        <div className={cadStandalone ? "table-manager-body cad-table-manager-body" : "table-manager-body"}>
          {!cadStandalone && <aside className="table-library">
            <div className="table-library-actions">
              <button className="button" onClick={() => addTemplate("technicalEconomicIndicators")}>
                + 技术经济指标表
              </button>
              <button className="button" onClick={() => addTemplate("custom")}>
                + 自定义表格
              </button>
              <button className="button" onClick={() => addTemplate("waterproofDesign")}>
                + 防水设计表
              </button>
              <button className="button" onClick={() => addTemplate("interiorFinish")}>
                + 室内装修做法表
              </button>
              <button className="button" onClick={() => addTemplate("buildingSafetyMeasures")}>
                + 建筑安全措施表
              </button>
              <button className="button" onClick={() => addTemplate("accessibilityFacilities")}>
                + 无障碍设施表
              </button>
            </div>
            <div className="table-library-list">
              {tables.map((table) => (
                <button
                  className={table.tableId === selectedId ? "selected" : ""}
                  key={table.tableId}
                  onClick={() => {
                    setSelectedId(table.tableId);
                    setSelectedCell({ row: 0, column: 0 });
                    setSelection({ startRow: 0, startColumn: 0, endRow: 0, endColumn: 0 });
                    setFormulaDraft(table.rows[0]?.cells[0]?.formula ?? "");
                    setFormulaError("");
                  }}
                >
                  <strong>{table.tableNumber || "未编号"} · {table.title}</strong>
                  <span>{table.rows.length} 行 × {table.columns.length} 列</span>
                </button>
              ))}
              {tables.length === 0 && (
                <div className="empty-state">请选择上方模板创建第一张专业表格。</div>
              )}
            </div>
            {selected && (
              <button
                className="button danger"
                onClick={() => {
                  if (!window.confirm(`删除“${selected.title}”吗？`)) return;
                  const next = tables.filter((table) => table.tableId !== selected.tableId);
                  commit(next);
                  setSelectedId(next[0]?.tableId ?? "");
                }}
              >
                删除当前表
              </button>
            )}
          </aside>}

          <section className="table-workbench">
            {!selected ? (
              <div className="table-empty-canvas">
                <strong>尚未创建专业表格</strong>
                <span>从左侧选择模板创建表格，或从 CAD 读取现有表格。</span>
              </div>
            ) : (
              <>
                {!cadStandalone && <div className="table-meta">
                  <label>
                    <span>表号</span>
                    <input
                      value={selected.tableNumber}
                      onChange={(event) =>
                        updateSelected((table) => ({ ...table, tableNumber: event.target.value }))
                      }
                    />
                  </label>
                  <label className="wide">
                    <span>表格标题</span>
                    <input
                      value={selected.title}
                      onChange={(event) =>
                        updateSelected((table) => ({ ...table, title: event.target.value }))
                      }
                    />
                  </label>
                  <label className="check">
                    <input
                      type="checkbox"
                      checked={selected.repeatHeader}
                      onChange={(event) =>
                        updateSelected((table) => ({ ...table, repeatHeader: event.target.checked }))
                      }
                    />
                    <span>跨页重复表头</span>
                  </label>
                  <label className="check">
                    <input
                      type="checkbox"
                      checked={selected.allowSplitAcrossPages}
                      onChange={(event) =>
                        updateSelected((table) => ({
                          ...table,
                          allowSplitAcrossPages: event.target.checked,
                        }))
                      }
                    />
                    <span>允许跨页拆表</span>
                  </label>
                </div>}

                {cadStandalone && activeCell && (
                  <div className="cad-office-ribbon">
                    <nav className="cad-ribbon-tabs" aria-label="表格功能区">
                      {([[
                        "home", "开始"], ["layout", "行列与尺寸"], ["format", "单元格格式"],
                        ["formula", "公式与关联"], ["cad", "CAD 与文件"],
                      ] as const).map(([key, label]) => (
                        <button key={key} className={ribbonTab === key ? "active" : ""} onClick={() => setRibbonTab(key)}>{label}</button>
                      ))}
                    </nav>
                    <div className="cad-ribbon-panel">
                      {ribbonTab === "home" && <>
                        <fieldset><legend>历史</legend><RibbonIconButton label="撤销 (Ctrl+Z)" disabled={!undoStack.length} onClick={undo}><Undo2 /></RibbonIconButton><RibbonIconButton label="重做 (Ctrl+Shift+Z)" disabled={!redoStack.length} onClick={redo}><Redo2 /></RibbonIconButton></fieldset>
                        <fieldset><legend>单元格</legend>
                          <RibbonIconButton label="合并所选单元格" disabled={selectedCellCount < 2} onClick={() => {
                            try { updateSelected((table) => mergeSelectedCells(table, selection)); }
                            catch (error) { window.alert(error instanceof Error ? error.message : "合并失败。"); }
                          }}><Merge /></RibbonIconButton>
                          <RibbonIconButton label="拆分当前合并单元格" disabled={activeCell.rowSpan <= 1 && activeCell.columnSpan <= 1} onClick={() => {
                            try { updateSelected((table) => splitMergedCell(table, selectedCell.row, selectedCell.column)); }
                            catch (error) { window.alert(error instanceof Error ? error.message : "拆分失败。"); }
                          }}><Split /></RibbonIconButton>
                        </fieldset>
                        <fieldset><legend>对齐</legend><div className="alignment-controls">
                          <RibbonIconButton label="左对齐" className={activeCell.alignment === "left" ? "active" : ""} onClick={() => setSelectedAlignment("left")}><AlignLeft /></RibbonIconButton>
                          <RibbonIconButton label="居中对齐" className={!activeCell.alignment || activeCell.alignment === "center" ? "active" : ""} onClick={() => setSelectedAlignment("center")}><AlignCenter /></RibbonIconButton>
                          <RibbonIconButton label="右对齐" className={activeCell.alignment === "right" ? "active" : ""} onClick={() => setSelectedAlignment("right")}><AlignRight /></RibbonIconButton>
                        </div></fieldset>
                        <fieldset><legend>编辑</legend><RibbonIconButton label="修改单元格文字" onClick={() => { setCellEditorValue(activeCell.displayValue); setCellEditorOpen(true); }}><Pencil /></RibbonIconButton><RibbonIconButton label="恢复所选单元格默认样式" onClick={() => patchSelectedCells({ alignment: "center", horizontalPaddingMillimeters: 1, borderColorIndex: null, fillColorIndex: null, textColorIndex: null })}><RotateCcw /></RibbonIconButton></fieldset>
                      </>}
                      {ribbonTab === "layout" && <>
                        <fieldset><legend>行</legend>
                          <RibbonIconButton label="在表尾添加行" onClick={() => updateSelected((table) => ({ ...table, rows: [...table.rows, createEmptyRow(table.columns)] }))}><Rows3 /><Plus className="ribbon-corner-icon" /></RibbonIconButton>
                          <RibbonIconButton label="删除当前行" onClick={() => { try { updateSelected((table) => deleteTableRow(table, selectedCell.row)); } catch (error) { window.alert(error instanceof Error ? error.message : "删除行失败。"); } }}><Rows3 /><Trash2 className="ribbon-corner-icon" /></RibbonIconButton>
                          <label>行高 mm<input type="number" min="3" max="500" step="0.1" value={selected.rows[selectedCell.row]?.heightMillimeters ?? 8} onChange={(event) => updateSelected((table) => ({ ...table, rows: table.rows.map((row, index) => index === selectedCell.row ? { ...row, heightMillimeters: Math.max(3, Number(event.target.value) || 3) } : row) }))} /></label>
                          <RibbonIconButton label="平均所选行高" onClick={equalizeSelectedRows}><Rows3 /></RibbonIconButton>
                        </fieldset>
                        <fieldset><legend>列</legend>
                          <RibbonIconButton label="在表尾添加列" onClick={addColumn}><Columns3 /><Plus className="ribbon-corner-icon" /></RibbonIconButton><RibbonIconButton label="删除当前列" onClick={removeSelectedColumn}><Columns3 /><Trash2 className="ribbon-corner-icon" /></RibbonIconButton>
                          <label>列宽 mm<input type="number" min="4" max="500" step="0.1" value={selectedColumn?.widthMillimeters ?? 24} onChange={(event) => updateSelected((table) => ({ ...table, columns: table.columns.map((column, index) => index === selectedCell.column ? { ...column, widthMillimeters: Math.max(4, Number(event.target.value) || 4) } : column) }))} /></label>
                          <RibbonIconButton label="平均所选列宽" onClick={equalizeSelectedColumns}><Columns3 /></RibbonIconButton>
                        </fieldset>
                        <fieldset><legend>列标题</legend><label>当前列<input value={selectedColumn?.title ?? ""} onChange={(event) => updateSelected((table) => ({ ...table, columns: table.columns.map((column, index) => index === selectedCell.column ? { ...column, title: event.target.value } : column) }))} /></label><span className="ribbon-help">也可拖动列头或行号边缘调整尺寸</span></fieldset>
                      </>}
                      {ribbonTab === "format" && <>
                        <fieldset><legend>内容</legend><label>左右留白 mm<input type="number" min="0" max="30" step="0.5" value={activeCell.horizontalPaddingMillimeters ?? 1} onChange={(event) => patchSelectedCells({ horizontalPaddingMillimeters: Math.max(0, Number(event.target.value) || 0) })} /></label>
                          {(["边框", "底色", "文字"] as const).map((label, index) => { const property = (["borderColorIndex", "fillColorIndex", "textColorIndex"] as const)[index]; return <label key={property} className="cad-color-field"><span>{label}</span><i style={{ background: aciColor(activeCell[property]) }} /><input type="number" min="1" max="255" value={activeCell[property] ?? 7} onChange={(event) => patchSelectedCells({ [property]: Math.max(1, Math.min(255, Number(event.target.value) || 7)) })} /></label>; })}
                          <RibbonIconButton label="清除所选单元格底色" onClick={() => patchSelectedCells({ fillColorIndex: null })}><ImageMinus /></RibbonIconButton>
                        </fieldset>
                        <fieldset><legend>表格线</legend>
                          <label className="cad-color-field"><span>外框</span><i style={{ background: aciColor(selected.outerBorderColorIndex) }} /><input type="number" min="1" max="255" value={selected.outerBorderColorIndex ?? 7} onChange={(event) => updateSelected((table) => ({ ...table, outerBorderColorIndex: Math.max(1, Math.min(255, Number(event.target.value) || 7)) }))} /></label>
                          <label>线宽<select value={selected.outerBorderWeightMillimeters ?? .25} onChange={(event) => updateSelected((table) => ({ ...table, outerBorderWeightMillimeters: Number(event.target.value) }))}>{[.05,.09,.13,.18,.25,.35,.5,.7,1].map((value) => <option key={value}>{value}</option>)}</select></label>
                          <label className="cad-color-field"><span>内框</span><i style={{ background: aciColor(selected.innerBorderColorIndex) }} /><input type="number" min="1" max="255" value={selected.innerBorderColorIndex ?? 7} onChange={(event) => updateSelected((table) => ({ ...table, innerBorderColorIndex: Math.max(1, Math.min(255, Number(event.target.value) || 7)) }))} /></label>
                          <label>线宽<select value={selected.innerBorderWeightMillimeters ?? .13} onChange={(event) => updateSelected((table) => ({ ...table, innerBorderWeightMillimeters: Number(event.target.value) }))}>{[.05,.09,.13,.18,.25,.35,.5,.7,1].map((value) => <option key={value}>{value}</option>)}</select></label>
                        </fieldset>
                      </>}
                      {ribbonTab === "formula" && <>
                        <fieldset><legend>关联</legend><RibbonIconButton label="关联所选单元格" disabled={selectedCellCount < 2} onClick={linkSelectedCells}><Link2 /></RibbonIconButton><RibbonIconButton label="取消所选单元格关联" onClick={() => patchSelectedCells({ linkGroupId: "" })}><Unlink2 /></RibbonIconButton></fieldset>
                        <fieldset className="formula-ribbon-group"><legend>公式</legend><label className="formula-field">fx<input value={formulaDraft} placeholder="例如 =SUM(B2:B8)" onChange={(event) => { setFormulaDraft(event.target.value); setFormulaError(""); }} /></label><RibbonIconButton label="打开函数向导" onClick={() => setFormulaBuilderOpen(true)}><Sigma /></RibbonIconButton><RibbonIconButton label="计算当前公式" disabled={!formulaDraft.trim()} onClick={calculateFormula}><Calculator /></RibbonIconButton>{formulaError && <em className="formula-error">{formulaError}</em>}</fieldset>
                      </>}
                      {ribbonTab === "cad" && <>
                        <fieldset><legend>来源</legend><RibbonIconButton label="创建空白表格" onClick={() => setBlankTableOpen(true)}><Table2 /></RibbonIconButton><RibbonIconButton label="框选 CAD 表格" disabled={!onPickCadTable || cadBusy} onClick={onPickCadTable}><BoxSelect /></RibbonIconButton><RibbonIconButton label="拾取现有表格并原位修改" disabled={!onRepickCadTable || cadBusy} onClick={onRepickCadTable}><TableProperties /></RibbonIconButton><RibbonIconButton label="在 CAD 中定位当前单元格来源" disabled={!onLocateCadSources || !activeCell.sourceHandles?.length} onClick={() => onLocateCadSources?.(selected.sourceDrawingPath ?? "", activeCell.sourceHandles ?? [])}><LocateFixed /></RibbonIconButton></fieldset>
                        <fieldset><legend>单元格对象</legend><RibbonIconButton label="向当前单元格插入 CAD 对象" disabled={!onPickCadObjects || cadBusy} onClick={() => selected && onPickCadObjects?.(selected, selectedCell.row, selectedCell.column)}><ImagePlus /></RibbonIconButton><RibbonIconButton label="移除当前单元格 CAD 对象" disabled={!activeCell.cadObjectAssetPath} onClick={() => patchSelectedCells({ cadObjectAssetPath: "", cadObjectPreviewPath: "", cadObjectPreviewDataUrl: "", cadObjectCount: 0 })}><ImageMinus /></RibbonIconButton></fieldset>
                        <fieldset className="template-ribbon-group"><legend>常用表格库</legend><select value={templateId} onChange={(event) => setTemplateId(event.target.value)}><option value="">选择常用表格</option>{cadTemplates.map((template) => <option key={template.id} value={template.id}>{template.name}</option>)}</select><RibbonIconButton label="打开所选常用表格" disabled={!templateId} onClick={() => openCadTemplate(templateId)}><FolderOpen /></RibbonIconButton><RibbonIconButton label="保存当前表格到常用表格库" disabled={!onSaveCadTemplate} onClick={() => { const name = window.prompt("常用表格名称", selected.title || "未命名表格")?.trim(); if (name) onSaveCadTemplate?.(name, selected); }}><Save /></RibbonIconButton><RibbonIconButton label="删除所选常用表格" className="danger" disabled={!templateId || !onDeleteCadTemplate} onClick={() => { const template = cadTemplates.find((item) => item.id === templateId); if (template && window.confirm(`删除常用表格“${template.name}”？`)) onDeleteCadTemplate?.(template.id); }}><Trash2 /></RibbonIconButton></fieldset>
                        <fieldset><legend>导出</legend><RibbonIconButton label="导出 CSV" onClick={exportCsv}><FileDown /></RibbonIconButton><RibbonIconButton label="导出 Excel" disabled={!onExportXlsx} onClick={() => onExportXlsx?.(selected)}><FileSpreadsheet /></RibbonIconButton></fieldset>
                      </>}
                    </div>
                  </div>
                )}

                <div className="table-commandbar">
                  <button className="button icon-text" disabled={undoStack.length === 0} onClick={undo}><Undo2 />撤销</button>
                  <button className="button icon-text" disabled={redoStack.length === 0} onClick={redo}><Redo2 />重做</button>
                  <span className="command-separator" />
                  <button
                    className="button icon-text"
                    onClick={() =>
                      updateSelected((table) => ({
                        ...table,
                        rows: [...table.rows, createEmptyRow(table.columns)],
                      }))
                    }
                  >
                    <Rows3 />添加行
                  </button>
                  <button
                    className="button icon-text"
                    onClick={() => {
                      if (!selected.rows[selectedCell.row]) return;
                      try { updateSelected((table) => deleteTableRow(table, selectedCell.row)); }
                      catch (error) { window.alert(error instanceof Error ? error.message : "删除行失败。"); return; }
                      setSelectedCell((cell) => ({ ...cell, row: Math.max(0, cell.row - 1) }));
                    }}
                  >
                    <Rows3 />删除行
                  </button>
                  <button className="button icon-text" onClick={addColumn}><Columns3 />添加列</button>
                  <button className="button icon-text" onClick={removeSelectedColumn}><Columns3 />删除列</button>
                  <span className="command-separator" />
                  <button
                    className="button icon-text"
                    disabled={selectedCellCount < 2}
                    onClick={() => {
                      try {
                        updateSelected((table) => mergeSelectedCells(table, selection));
                        setSelectedCell({
                          row: selectionBounds.firstRow,
                          column: selectionBounds.firstColumn,
                        });
                        setSelection({
                          startRow: selectionBounds.firstRow,
                          startColumn: selectionBounds.firstColumn,
                          endRow: selectionBounds.firstRow,
                          endColumn: selectionBounds.firstColumn,
                        });
                      } catch (error) {
                        window.alert(error instanceof Error ? error.message : "合并失败。");
                      }
                    }}
                  >
                    <Merge />合并单元格
                  </button>
                  <button
                    className="button icon-text"
                    disabled={!activeCell || activeCell.rowSpan <= 1 && activeCell.columnSpan <= 1}
                    onClick={() => {
                      try {
                        updateSelected((table) =>
                          splitMergedCell(table, selectedCell.row, selectedCell.column),
                        );
                      } catch (error) {
                        window.alert(error instanceof Error ? error.message : "拆分失败。");
                      }
                    }}
                  >
                    <Split />拆分单元格
                  </button>
                  {cadStandalone && <>
                    <button className="button icon-text" disabled={selectedCellCount < 2} onClick={linkSelectedCells}><Link2 />关联单元格</button>
                    <button className="button icon-text" onClick={() => patchSelectedCells({ linkGroupId: "" })}><Unlink2 />取消关联</button>
                    <button className="button icon-text" disabled={!onPickCadTable || cadBusy} onClick={onPickCadTable}><BoxSelect />拾取 CAD 表格</button>
                    <button className="button icon-text" disabled={!onRepickCadTable || cadBusy} onClick={onRepickCadTable}><TableProperties />拾取现有表格修改</button>
                    <div className="cad-template-tools">
                      <strong>常用表格库</strong>
                      <select value={templateId} onChange={(event) => setTemplateId(event.target.value)}>
                        <option value="">选择常用表格</option>
                        {cadTemplates.map((template) => <option key={template.id} value={template.id}>{template.name}</option>)}
                      </select>
                      <div className="two-button-row">
                        <button className="button compact" disabled={!templateId} onClick={() => {
                          openCadTemplate(templateId);
                        }}>打开</button>
                        <button className="button compact" disabled={!selected || !onSaveCadTemplate} onClick={() => {
                          const name = window.prompt("常用表格名称", selected?.title || "未命名表格")?.trim();
                          if (name && selected) onSaveCadTemplate?.(name, selected);
                        }}>保存当前</button>
                        <button className="button compact danger" disabled={!templateId || !onDeleteCadTemplate} onClick={() => {
                          const template = cadTemplates.find((item) => item.id === templateId);
                          if (template && window.confirm(`删除常用表格“${template.name}”？`)) onDeleteCadTemplate?.(template.id);
                        }}>删除</button>
                      </div>
                    </div>
                  </>}
                  <span className="command-separator" />
                  <button className="button" onClick={exportCsv}>导出 CSV</button>
                  <button className="button icon-text" disabled={!onExportXlsx} onClick={() => selected && onExportXlsx?.(selected)}><FileSpreadsheet />导出 Excel</button>
                  <button
                    className={cadStandalone ? "button project-table-only" : "button"}
                    onClick={() => {
                      const result = synchronizeBoundTableCells(tables, fields, false);
                      if (result.skippedConflicts.length > 0) {
                        setPendingSync(result);
                        return;
                      }
                      applySyncResult(result);
                    }}
                  >
                    同步项目字段
                  </button>
                  <span className={cadStandalone ? "paste-hint project-table-only" : "paste-hint"}>
                    Shift+单击可框选；可从 Excel 粘贴
                  </span>
                </div>
                {syncNotice && <div className="binding-sync-notice">{syncNotice}</div>}

                {!cadStandalone && selectedColumn && (
                  <div className="column-settings">
                    <strong>当前列</strong>
                    <label>
                      <span>标题</span>
                      <input
                        value={selectedColumn.title}
                        onChange={(event) =>
                          updateSelected((table) => ({
                            ...table,
                            columns: table.columns.map((column) =>
                              column.key === selectedColumn.key
                                ? { ...column, title: event.target.value }
                                : column,
                            ),
                          }))
                        }
                      />
                    </label>
                    <label>
                      <span>单位</span>
                      <input
                        value={selectedColumn.unit}
                        onChange={(event) =>
                          updateSelected((table) => ({
                            ...table,
                            columns: table.columns.map((column) =>
                              column.key === selectedColumn.key
                                ? { ...column, unit: event.target.value }
                                : column,
                            ),
                            rows: table.rows.map((row) => ({
                              ...row,
                              cells: row.cells.map((cell) =>
                                cell.columnKey === selectedColumn.key
                                  ? { ...cell, unit: event.target.value }
                                  : cell,
                              ),
                            })),
                          }))
                        }
                      />
                    </label>
                    <label>
                      <span>列宽 mm</span>
                      <input
                        type="number"
                        min={10}
                        max={200}
                        value={selectedColumn.widthMillimeters}
                        onChange={(event) =>
                          updateSelected((table) => ({
                            ...table,
                            columns: table.columns.map((column) =>
                              column.key === selectedColumn.key
                                ? { ...column, widthMillimeters: Number(event.target.value) }
                                : column,
                            ),
                          }))
                        }
                      />
                    </label>
                    <label>
                      <span>小数位</span>
                      <input
                        type="number"
                        min={0}
                        max={6}
                        value={selectedColumn.decimalPlaces}
                        onChange={(event) =>
                          updateSelected((table) => ({
                            ...table,
                            columns: table.columns.map((column) =>
                              column.key === selectedColumn.key
                                ? { ...column, decimalPlaces: Number(event.target.value) }
                                : column,
                            ),
                          }))
                        }
                      />
                    </label>
                    <label className="check">
                      <input
                        type="checkbox"
                        checked={selectedColumn.required}
                        onChange={(event) =>
                          updateSelected((table) => ({
                            ...table,
                            columns: table.columns.map((column) =>
                              column.key === selectedColumn.key
                                ? { ...column, required: event.target.checked }
                                : column,
                            ),
                          }))
                        }
                      />
                      <span>必填</span>
                    </label>
                  </div>
                )}

                {activeCell && activeCell.rowSpan > 0 && activeCell.columnSpan > 0 && (
                  <div className="cell-settings">
                    <strong>
                      当前单元格 R{selectedCell.row + 1}C{selectedCell.column + 1}
                    </strong>
                    <span className="selection-count">
                      {selectedCellCount > 1 ? `已选择 ${selectedCellCount} 格` : "单格选择"}
                    </span>
                    <div className="alignment-controls" aria-label="单元格对齐">
                      <button className={activeCell.alignment === "left" ? "button compact active" : "button compact"} onClick={() => setSelectedAlignment("left")}>左对齐</button>
                      <button className={!activeCell.alignment || activeCell.alignment === "center" ? "button compact active" : "button compact"} onClick={() => setSelectedAlignment("center")}>居中</button>
                      <button className={activeCell.alignment === "right" ? "button compact active" : "button compact"} onClick={() => setSelectedAlignment("right")}>右对齐</button>
                    </div>
                    {cadStandalone && <>
                      <label>
                        <span>左右留白 mm</span>
                        <input type="number" min="0" max="30" step="0.5" value={activeCell.horizontalPaddingMillimeters ?? 1}
                          onChange={(event) => patchSelectedCells({ horizontalPaddingMillimeters: Math.max(0, Number(event.target.value) || 0) })} />
                      </label>
                      <div className="cad-color-fields">
                        {([
                          ["边框", "borderColorIndex"],
                          ["底色", "fillColorIndex"],
                          ["文字", "textColorIndex"],
                        ] as const).map(([label, property]) => <label key={property} className="cad-color-field">
                          <span>{label}</span>
                          <i style={{ background: aciColor(activeCell[property]) }} />
                          <input type="number" min="1" max="255" value={activeCell[property] ?? 7}
                            onChange={(event) => patchSelectedCells({ [property]: Math.max(1, Math.min(255, Number(event.target.value) || 7)) })} />
                        </label>)}
                      </div>
                      <div className="two-button-row">
                        <button className="button compact" onClick={() => patchSelectedCells({ fillColorIndex: null })}>清除底色</button>
                        <button className="button compact" onClick={() => patchSelectedCells({ alignment: "center", horizontalPaddingMillimeters: 1, borderColorIndex: null, fillColorIndex: null, textColorIndex: null })}>恢复所选样式</button>
                      </div>
                      <div className="two-button-row">
                        <button className="button compact" disabled={!onPickCadObjects || cadBusy}
                          onClick={() => selected && onPickCadObjects?.(selected, selectedCell.row, selectedCell.column)}>插入 CAD 对象</button>
                        <button className="button compact" disabled={!activeCell.cadObjectAssetPath}
                          onClick={() => patchSelectedCells({ cadObjectAssetPath: "", cadObjectPreviewPath: "", cadObjectPreviewDataUrl: "", cadObjectCount: 0 })}>移除对象</button>
                      </div>
                      {selectedColumn && <label>
                        <span>当前列标题</span>
                        <input value={selectedColumn.title} onChange={(event) => updateSelected((table) => ({
                          ...table,
                          columns: table.columns.map((column) => column.key === selectedColumn.key ? { ...column, title: event.target.value } : column),
                        }))} />
                      </label>}
                      {selectedColumn && <label>
                        <span>当前列宽 mm</span>
                        <input type="number" min="4" max="500" step="1" value={selectedColumn.widthMillimeters}
                          onChange={(event) => updateSelected((table) => ({ ...table, columns: table.columns.map((column) => column.key === selectedColumn.key ? { ...column, widthMillimeters: Math.max(4, Number(event.target.value) || 4) } : column) }))} />
                      </label>}
                      <label>
                        <span>当前行高 mm</span>
                        <input type="number" min="3" max="500" step="1" value={selected.rows[selectedCell.row]?.heightMillimeters ?? 8}
                          onChange={(event) => updateSelected((table) => ({ ...table, rows: table.rows.map((row, rowIndex) => rowIndex === selectedCell.row ? { ...row, heightMillimeters: Math.max(3, Number(event.target.value) || 3) } : row) }))} />
                      </label>
                      <div className="two-button-row">
                        <button className="button compact icon-text" onClick={equalizeSelectedColumns}><Columns3 />平均列宽</button>
                        <button className="button compact icon-text" onClick={equalizeSelectedRows}><Rows3 />平均行高</button>
                      </div>
                      <fieldset className="cad-border-settings">
                        <legend>表格边框</legend>
                        <label className="cad-color-field"><span>外边框</span><i style={{ background: aciColor(selected.outerBorderColorIndex) }} /><input type="number" min="1" max="255" value={selected.outerBorderColorIndex ?? 7} onChange={(event) => updateSelected((table) => ({ ...table, outerBorderColorIndex: Math.max(1, Math.min(255, Number(event.target.value) || 7)) }))} /></label>
                        <label><span>外框线宽</span><select value={selected.outerBorderWeightMillimeters ?? 0.25} onChange={(event) => updateSelected((table) => ({ ...table, outerBorderWeightMillimeters: Number(event.target.value) }))}>{[0.05,0.09,0.13,0.18,0.25,0.35,0.5,0.7,1].map((value) => <option key={value}>{value}</option>)}</select></label>
                        <label className="cad-color-field"><span>内边框</span><i style={{ background: aciColor(selected.innerBorderColorIndex) }} /><input type="number" min="1" max="255" value={selected.innerBorderColorIndex ?? 7} onChange={(event) => updateSelected((table) => ({ ...table, innerBorderColorIndex: Math.max(1, Math.min(255, Number(event.target.value) || 7)) }))} /></label>
                        <label><span>内框线宽</span><select value={selected.innerBorderWeightMillimeters ?? 0.13} onChange={(event) => updateSelected((table) => ({ ...table, innerBorderWeightMillimeters: Number(event.target.value) }))}>{[0.05,0.09,0.13,0.18,0.25,0.35,0.5,0.7,1].map((value) => <option key={value}>{value}</option>)}</select></label>
                      </fieldset>
                    </>}
                    <button
                      className="button compact"
                      disabled={!onLocateCadSources || !activeCell.sourceHandles?.length}
                      title={activeCell.sourceHandles?.length
                        ? `定位 ${activeCell.sourceHandles.length} 个 CAD 来源对象`
                        : "该单元格不是从 CAD 读取，或没有可用来源"}
                      onClick={() => onLocateCadSources?.(
                        selected.sourceDrawingPath ?? "",
                        activeCell.sourceHandles ?? [],
                      )}
                    >
                      定位 CAD 来源
                    </button>
                    {!cadStandalone && <label>
                      <span>绑定字段</span>
                      <select
                        aria-label="绑定项目字段"
                        value={activeCell.fieldPath}
                        onChange={(event) => {
                          const field = fields.find((item) => item.path === event.target.value);
                          updateSelected((table) => {
                            const bound = bindTableCellToProjectField(
                              table,
                              selectedCell.row,
                              selectedCell.column,
                              field ?? null,
                            );
                            return ["planning", "design"].includes(
                              table.columns[selectedCell.column]?.key,
                            )
                              ? recalculateTechnicalTable(bound)
                              : bound;
                          });
                        }}
                      >
                        <option value="">不绑定</option>
                        {fields.map((field) => (
                          <option key={field.path} value={field.path}>
                            {field.label}{field.unit ? `（${field.unit}）` : ""}
                          </option>
                        ))}
                      </select>
                    </label>}
                    <label className="formula-field">
                      <span>公式</span>
                      <input
                        aria-label="单元格公式"
                        placeholder={cadStandalone ? "例如 =SUM(B2:B8)" : "例如 ROUND([design]-[planning], 2)"}
                        value={formulaDraft}
                        onChange={(event) => {
                          setFormulaDraft(event.target.value);
                          setFormulaError("");
                        }}
                      />
                    </label>
                    {cadStandalone && <button className="button" onClick={() => setFormulaBuilderOpen(true)}>fx 插入公式</button>}
                    <button
                      className="button"
                      disabled={!formulaDraft.trim()}
                      onClick={calculateFormula}
                    >
                      计算
                    </button>
                    {(formulaError ? (
                      <em className="formula-error">{formulaError}</em>
                    ) : (
                      <small>{cadStandalone ? "支持：单元格引用、SUM、AVERAGE、MIN、MAX、COUNT、ROUND、IF" : "白名单：SUM、MIN、MAX、ROUND、IF、ABS、COUNT"}</small>
                    ))}
                    {cadStandalone && activeCell.linkGroupId && <small>当前单元格已关联，修改内容会同步到关联组。</small>}
                  </div>
                )}

                <div className="professional-grid-scroll" tabIndex={0} onKeyDown={(event) => {
                  if (!cadStandalone || event.key !== "Delete") return;
                  event.preventDefault();
                  patchSelectedCells({ displayValue: "", numericValue: null, formula: "" });
                }} onPaste={(event) => {
                  if (!cadStandalone) return;
                  const text = event.clipboardData.getData("text/plain");
                  if (!text) return;
                  event.preventDefault();
                  updateSelected((table) => pasteTableCells(table, selectedCell.row, selectedCell.column, text));
                }}>
                  <div className="professional-grid-zoom-layer" style={{ zoom: previewZoom / 100 }}>
                  <table className="professional-grid">
                    <colgroup>
                      <col style={{ width: 44 }} />
                      {selected.columns.map((column) => (
                        <col
                          key={column.key}
                          style={{ width: `${Math.max(12, column.widthMillimeters * 3)}px` }}
                        />
                      ))}
                    </colgroup>
                    <thead>
                      <tr>
                        <th>序</th>
                        {selected.columns.map((column, columnIndex) => (
                          <th
                            className={selectedCell.column === columnIndex ? "selected-column" : ""}
                            key={column.key}
                            onClick={() => {
                              selectCell(0, columnIndex, false);
                              setSelection({
                                startRow: 0,
                                startColumn: columnIndex,
                                endRow: Math.max(0, selected.rows.length - 1),
                                endColumn: columnIndex,
                              });
                            }}
                          >
                            {column.title}
                            {column.unit && <small>{column.unit}</small>}
                            {column.required && <em>*</em>}
                            {cadStandalone && <span className="column-resize-handle" title="拖动修改列宽" onPointerDown={(event) => resizeColumn(columnIndex, event)} />}
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {selected.rows.map((row, rowIndex) => (
                        <tr key={row.rowId} style={{ height: `${Math.max(12, (row.heightMillimeters ?? 8) * 3)}px` }}>
                          <th
                            className={selectedCell.row === rowIndex ? "selected-row" : ""}
                            onClick={() => {
                              selectCell(rowIndex, 0, false);
                              setSelection({
                                startRow: rowIndex,
                                startColumn: 0,
                                endRow: rowIndex,
                                endColumn: Math.max(0, selected.columns.length - 1),
                              });
                            }}
                          >
                            {rowIndex + 1}
                            {cadStandalone && <span className="row-resize-handle" title="拖动修改行高" onPointerDown={(event) => resizeRow(rowIndex, event)} />}
                          </th>
                          {selected.columns.map((column, columnIndex) => {
                            const cell = row.cells.find((item) => item.columnKey === column.key)!;
                            const invalid = issues.some(
                              (issue) =>
                                issue.rowId === row.rowId && issue.columnKey === column.key,
                            );
                            if (cell.rowSpan === 0 || cell.columnSpan === 0) return null;
                            const inSelection = isCellInSelection(
                              rowIndex,
                              columnIndex,
                              selection,
                            );
                            return (
                              <td
                                className={[
                                  inSelection ? "selected-range" : "",
                                  selectedCell.row === rowIndex &&
                                  selectedCell.column === columnIndex
                                    ? "selected-cell"
                                    : "",
                                ].filter(Boolean).join(" ")}
                                key={cell.cellId}
                                rowSpan={cell.rowSpan}
                                colSpan={cell.columnSpan}
                                onPointerDown={(event) => {
                                  if (!cadStandalone || event.button !== 0) return;
                                  event.preventDefault();
                                  selectCell(rowIndex, columnIndex, event.shiftKey);
                                  setDragSelecting(true);
                                  (event.currentTarget.closest(".professional-grid-scroll") as HTMLElement | null)?.focus();
                                }}
                                onPointerEnter={() => {
                                  if (!cadStandalone || !dragSelecting) return;
                                  setSelectedCell({ row: rowIndex, column: columnIndex });
                                  setSelection((current) => ({ ...current, endRow: rowIndex, endColumn: columnIndex }));
                                }}
                                onClick={(event) => { if (!cadStandalone) selectCell(rowIndex, columnIndex, event.shiftKey); }}
                                onDoubleClick={() => {
                                  selectCell(rowIndex, columnIndex, false);
                                  setCellEditorValue(cell.displayValue);
                                  setCellEditorOpen(true);
                                }}
                                style={{
                                  textAlign: cell.alignment ?? "center",
                                  background: cell.fillColorIndex ? aciColor(cell.fillColorIndex) : undefined,
                                  color: cell.textColorIndex ? aciColor(cell.textColorIndex) : undefined,
                                  borderColor: cell.borderColorIndex ? aciColor(cell.borderColorIndex) : undefined,
                                }}
                              >
                                {(cell.cadObjectPreviewDataUrl || cell.cadObjectPreviewPath) && <img className="cad-cell-object-preview" src={cell.cadObjectPreviewDataUrl || `file:///${cell.cadObjectPreviewPath!.replace(/\\/g, "/")}`} alt="CAD 单元格对象" />}
                                <textarea
                                  className={invalid ? "invalid" : ""}
                                  readOnly={cadStandalone}
                                  style={{ textAlign: cell.alignment ?? "center", paddingLeft: `${Math.max(3, (cell.horizontalPaddingMillimeters ?? 1) * 3)}px`, paddingRight: `${Math.max(3, (cell.horizontalPaddingMillimeters ?? 1) * 3)}px` }}
                                  value={cell.displayValue}
                                  rows={Math.min(
                                    8,
                                    Math.max(1, cell.displayValue.split(/\r?\n/).length),
                                  )}
                                  title={
                                    issues.find(
                                      (issue) =>
                                        issue.rowId === row.rowId &&
                                        issue.columnKey === column.key,
                                    )?.message ?? ""
                                  }
                                  onChange={(event) => editCellValue(event.target.value)}
                                  onPaste={(event) => {
                                    const text = event.clipboardData.getData("text/plain");
                                    if (!text.includes("\t") && !text.includes("\n")) return;
                                    event.preventDefault();
                                    updateSelected((table) =>
                                      pasteTableCells(table, rowIndex, columnIndex, text),
                                    );
                                  }}
                                />
                              </td>
                            );
                          })}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  </div>
                </div>

                {cadStandalone && <div className="cad-preview-statusbar">
                  <span>{selected.rows.length} 行 × {selected.columns.length} 列</span>
                  <div className="cad-preview-zoom" aria-label="预览缩放">
                    <button type="button" title="缩小预览" onClick={() => changePreviewZoom(previewZoom - 10)}>−</button>
                    <input type="range" min="10" max="400" step="10" value={previewZoom} onChange={(event) => changePreviewZoom(Number(event.target.value))} />
                    <input aria-label="预览缩放百分比" type="number" min="10" max="400" step="10" value={previewZoom} onChange={(event) => changePreviewZoom(Number(event.target.value))} />
                    <span>%</span>
                    <button type="button" title="放大预览" onClick={() => changePreviewZoom(previewZoom + 10)}>＋</button>
                    <button type="button" title="恢复 100%" onClick={() => changePreviewZoom(100)}>100%</button>
                  </div>
                </div>}

                {!cadStandalone && <div className={`table-validation ${issues.length ? "has-issues" : "valid"}`}>
                  <strong>{issues.length ? `${issues.length} 项待完善` : "表格校验通过"}</strong>
                  <span>
                    {issues.length
                      ? issues.slice(0, 3).map((issue) => issue.message).join("；")
                      : "必填项和当前模板的数据类型均符合要求。"}
                  </span>
                </div>}
              </>
            )}
          </section>
        </div>

        {pendingSync && (
          <div className="sync-conflict-overlay">
            <section className="sync-conflict-dialog" role="dialog" aria-label="绑定字段同步冲突">
              <header>
                <div>
                  <span className="eyebrow">项目字段同步</span>
                  <h3>发现 {pendingSync.skippedConflicts.length} 个人工修改</h3>
                </div>
              </header>
              <p>
                以下单元格含人工修改或公式。请选择保留当前内容，或者使用项目数据中心的值覆盖。
              </p>
              <div className="sync-conflict-list">
                {pendingSync.skippedConflicts.slice(0, 8).map((conflict) => (
                  <article key={conflict.cellId}>
                    <strong>{conflict.tableTitle} · {conflict.fieldLabel}</strong>
                    <span>当前：{conflict.currentValue || "（空）"}</span>
                    <span>项目字段：{conflict.projectValue || "（空）"}</span>
                  </article>
                ))}
                {pendingSync.skippedConflicts.length > 8 && (
                  <small>另有 {pendingSync.skippedConflicts.length - 8} 项未展开显示。</small>
                )}
              </div>
              <footer>
                <button className="button" onClick={() => applySyncResult(pendingSync)}>
                  保留人工修改
                </button>
                <button
                  className="button primary"
                  onClick={() =>
                    applySyncResult(synchronizeBoundTableCells(tables, fields, true))
                  }
                >
                  覆盖并同步
                </button>
              </footer>
            </section>
          </div>
        )}

        {cellEditorOpen && activeCell && (
          <div className="sync-conflict-overlay" onMouseDown={() => setCellEditorOpen(false)}>
            <section className="cad-cell-text-dialog" onMouseDown={(event) => event.stopPropagation()}>
              <header><h3>修改单元格文字</h3></header>
              <textarea autoFocus value={cellEditorValue} onChange={(event) => setCellEditorValue(event.target.value)} />
              <footer>
                <button className="button" onClick={() => setCellEditorOpen(false)}>取消</button>
                <button className="button primary" onClick={() => { editCellValue(cellEditorValue); setCellEditorOpen(false); }}>确定</button>
              </footer>
            </section>
          </div>
        )}

        {formulaBuilderOpen && activeCell && (
          <div className="sync-conflict-overlay" onMouseDown={() => setFormulaBuilderOpen(false)}>
            <section className="cad-cell-text-dialog cad-formula-dialog" onMouseDown={(event) => event.stopPropagation()}>
              <header><h3>fx 插入公式</h3></header>
              <div className="cad-formula-body">
                <label><span>目标单元格</span><strong>{spreadsheetColumnName(selectedCell.column)}{selectedCell.row + 1}</strong></label>
                <label><span>函数</span><select value={formulaFunction} onChange={(event) => setFormulaFunction(event.target.value)}>
                  <option value="REFERENCE">直接引用</option><option value="SUM">SUM 求和</option><option value="AVERAGE">AVERAGE 平均值</option>
                  <option value="MIN">MIN 最小值</option><option value="MAX">MAX 最大值</option><option value="COUNT">COUNT 数量</option>
                  <option value="ROUND">ROUND 四舍五入</option><option value="IF">IF 条件</option>
                </select></label>
                <label><span>当前选区</span><strong>{selectionAddress}</strong></label>
                <label><span>公式</span><input value={formulaDraft} onChange={(event) => setFormulaDraft(event.target.value)} /></label>
                <button className="button" onClick={() => {
                  const first = selectionAddress.split(":")[0];
                  setFormulaDraft(formulaFunction === "REFERENCE" ? `=${first}` : formulaFunction === "ROUND" ? `=ROUND(${first},2)` : formulaFunction === "IF" ? `=IF(${first}>0,1,0)` : `=${formulaFunction}(${selectionAddress})`);
                }}>使用当前选区</button>
              </div>
              <footer>
                <button className="button" onClick={() => setFormulaBuilderOpen(false)}>关闭</button>
                <button className="button primary" disabled={!formulaDraft.trim()} onClick={() => { if (calculateFormula()) setFormulaBuilderOpen(false); }}>应用公式</button>
              </footer>
            </section>
          </div>
        )}

        <footer>
          {onInsertCad ? (
            <div className="cad-table-insert-controls">
              <select value={cadOptions.insertType} onChange={(event) => setCadOptions((current) => ({ ...current, insertType: event.target.value as CadTableInsertOptions["insertType"] }))}>
                <option value="autocad">AutoCAD 原生表格</option>
                <option value="tianzheng">天正表格</option>
              </select>
              <label><input type="checkbox" checked={cadOptions.useOriginalCadSize} disabled={!hasOriginalCadSize} onChange={(event) => setCadOptions((current) => ({ ...current, useOriginalCadSize: event.target.checked }))} />保持原 CAD 尺寸</label>
              <label>预设比例 <select value={cadOptions.scale} disabled={cadOptions.useOriginalCadSize} onChange={(event) => setCadOptions((current) => ({ ...current, scale: Number(event.target.value) }))}>{[1,2,5,10,20,50,100,200,500].map((scale) => <option key={scale} value={scale}>1:{scale}</option>)}</select></label>
              <label>字高 <input type="number" min="0.1" step="0.1" value={cadOptions.textHeightMillimeters} onChange={(event) => setCadOptions((current) => ({ ...current, textHeightMillimeters: Math.max(0.1, Number(event.target.value) || 3.5) }))} /></label>
              {cadStandalone && <label>文字样式 <select value={cadOptions.textStyle} onChange={(event) => setCadOptions((current) => ({ ...current, textStyle: event.target.value }))}>{cadTextStyles.map((style) => <option key={style}>{style}</option>)}</select></label>}
            </div>
          ) : <span>表格将随项目文件保存；当前还未写入 CAD。</span>}
          <button className="button" onClick={onClose}>{cadStandalone ? "关闭" : "取消"}</button>
          {!cadStandalone && <button className="button primary" onClick={() => onSave(tables)}>保存表格</button>}
          {onInsertCad && <button className="button primary" disabled={!selected || cadBusy} onClick={() => selected && onInsertCad(selected, cadOptions)}>{cadBusy ? "正在写入…" : cadEditorPayload?.sourceEdit ? "更新当前表格" : "按设置插入 CAD"}</button>}
        </footer>
      </section>
    </div>
  );
}
