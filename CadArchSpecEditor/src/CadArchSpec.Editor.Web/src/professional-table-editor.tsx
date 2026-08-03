import { useMemo, useState } from "react";
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

type Props = {
  value: ArchitectureTable[];
  fields: ProjectField[];
  onSave(value: ArchitectureTable[]): void;
  onClose(): void;
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
  let index = table.columns.length + 1;
  while (table.columns.some((column) => column.key === `column${index}`)) index += 1;
  return `column${index}`;
};

export function ProfessionalTableEditor({ value, fields, onSave, onClose }: Props) {
  const initial = value.map(normalizeProfessionalTable);
  const [tables, setTables] = useState<ArchitectureTable[]>(initial);
  const [selectedId, setSelectedId] = useState(initial[0]?.tableId ?? "");
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
        title: `新列${table.columns.length + 1}`,
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
    updateSelected((table) => ({
      ...table,
      columns: table.columns.filter((column) => column.key !== definition.key),
      rows: table.rows.map((row) => ({
        ...row,
        cells: row.cells.filter((cell) => cell.columnKey !== definition.key),
      })),
    }));
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
      <section className="professional-table-dialog" onMouseDown={(event) => event.stopPropagation()}>
        <header>
          <div>
            <span className="eyebrow">阶段 2 · 专业表格</span>
            <h2>建筑专业表格编辑器</h2>
          </div>
          <button className="dialog-close" onClick={onClose}>×</button>
        </header>

        <div className="table-manager-body">
          <aside className="table-library">
            <div className="table-library-actions">
              <button className="button" onClick={() => addTemplate("technicalEconomicIndicators")}>
                + 技术经济指标表
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
          </aside>

          <section className="table-workbench">
            {!selected ? (
              <div className="table-empty-canvas">
                <strong>尚未创建专业表格</strong>
                <span>从左侧选择技术经济指标表或防水设计表开始。</span>
              </div>
            ) : (
              <>
                <div className="table-meta">
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
                </div>

                <div className="table-commandbar">
                  <button className="button" disabled={undoStack.length === 0} onClick={undo}>撤销</button>
                  <button className="button" disabled={redoStack.length === 0} onClick={redo}>重做</button>
                  <span className="command-separator" />
                  <button
                    className="button"
                    onClick={() =>
                      updateSelected((table) => ({
                        ...table,
                        rows: [...table.rows, createEmptyRow(table.columns)],
                      }))
                    }
                  >
                    添加行
                  </button>
                  <button
                    className="button"
                    onClick={() => {
                      if (!selected.rows[selectedCell.row]) return;
                      updateSelected((table) => ({
                        ...table,
                        rows: table.rows.filter((_, index) => index !== selectedCell.row),
                      }));
                      setSelectedCell((cell) => ({ ...cell, row: Math.max(0, cell.row - 1) }));
                    }}
                  >
                    删除行
                  </button>
                  <button className="button" onClick={addColumn}>添加列</button>
                  <button className="button" onClick={removeSelectedColumn}>删除列</button>
                  <span className="command-separator" />
                  <button
                    className="button"
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
                    合并单元格
                  </button>
                  <button
                    className="button"
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
                    拆分单元格
                  </button>
                  <span className="command-separator" />
                  <button className="button" onClick={exportCsv}>导出 CSV</button>
                  <button
                    className="button"
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
                  <span className="paste-hint">
                    Shift+单击可框选；可从 Excel 粘贴
                  </span>
                </div>
                {syncNotice && <div className="binding-sync-notice">{syncNotice}</div>}

                {selectedColumn && (
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
                    <label>
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
                    </label>
                    <label className="formula-field">
                      <span>公式</span>
                      <input
                        aria-label="单元格公式"
                        placeholder="例如 ROUND([design]-[planning], 2)"
                        value={formulaDraft}
                        onChange={(event) => {
                          setFormulaDraft(event.target.value);
                          setFormulaError("");
                        }}
                      />
                    </label>
                    <button
                      className="button"
                      disabled={!formulaDraft.trim()}
                      onClick={() => {
                        try {
                          updateSelected((table) =>
                            applyTableFormula(
                              table,
                              selectedCell.row,
                              selectedCell.column,
                              formulaDraft,
                            ),
                          );
                          setFormulaError("");
                        } catch (error) {
                          setFormulaError(
                            error instanceof Error ? error.message : "公式计算失败。",
                          );
                        }
                      }}
                    >
                      计算
                    </button>
                    {formulaError ? (
                      <em className="formula-error">{formulaError}</em>
                    ) : (
                      <small>白名单：SUM、MIN、MAX、ROUND、IF、ABS、COUNT</small>
                    )}
                  </div>
                )}

                <div className="professional-grid-scroll">
                  <table className="professional-grid">
                    <colgroup>
                      <col style={{ width: 44 }} />
                      {selected.columns.map((column) => (
                        <col
                          key={column.key}
                          style={{ width: `${Math.max(72, column.widthMillimeters * 3)}px` }}
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
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {selected.rows.map((row, rowIndex) => (
                        <tr key={row.rowId}>
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
                                onClick={(event) =>
                                  selectCell(rowIndex, columnIndex, event.shiftKey)
                                }
                              >
                                <input
                                  className={invalid ? "invalid" : ""}
                                  value={cell.displayValue}
                                  title={
                                    issues.find(
                                      (issue) =>
                                        issue.rowId === row.rowId &&
                                        issue.columnKey === column.key,
                                    )?.message ?? ""
                                  }
                                  onChange={(event) =>
                                    updateSelected((table) => {
                                      const updated = setTableCellValue(
                                        table,
                                        rowIndex,
                                        columnIndex,
                                        event.target.value,
                                      );
                                      return ["planning", "design"].includes(column.key)
                                        ? recalculateTechnicalTable(updated)
                                        : updated;
                                    })
                                  }
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

                <div className={`table-validation ${issues.length ? "has-issues" : "valid"}`}>
                  <strong>{issues.length ? `${issues.length} 项待完善` : "表格校验通过"}</strong>
                  <span>
                    {issues.length
                      ? issues.slice(0, 3).map((issue) => issue.message).join("；")
                      : "必填项和当前模板的数据类型均符合要求。"}
                  </span>
                </div>
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

        <footer>
          <span>表格将随项目文件保存；当前还未写入 CAD。</span>
          <button className="button" onClick={onClose}>取消</button>
          <button className="button primary" onClick={() => onSave(tables)}>保存表格</button>
        </footer>
      </section>
    </div>
  );
}
