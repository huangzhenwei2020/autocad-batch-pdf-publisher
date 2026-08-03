import type {
  ArchitectureTable,
  ArchitectureTableCell,
  ArchitectureTableColumn,
  ArchitectureTableRow,
  FieldState,
  ProjectField,
  ProfessionalTableType,
} from "./editor-model";

export type TableValidationIssue = {
  rowId: string;
  columnKey: string;
  message: string;
};

export type TableSelection = {
  startRow: number;
  startColumn: number;
  endRow: number;
  endColumn: number;
};

export type FormulaEvaluation = {
  result: number;
  inputs: Record<string, number>;
};

export type TableBindingConflict = {
  tableId: string;
  tableTitle: string;
  rowId: string;
  cellId: string;
  fieldPath: string;
  fieldLabel: string;
  currentValue: string;
  projectValue: string;
};

export type TableBindingSyncResult = {
  tables: ArchitectureTable[];
  updatedCount: number;
  skippedConflicts: TableBindingConflict[];
  missingFieldPaths: string[];
};

const id = (prefix: string) =>
  `${prefix}-${globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`}`;

const column = (
  key: string,
  title: string,
  widthMillimeters: number,
  required = true,
  unit = "",
  decimalPlaces = 0,
): ArchitectureTableColumn => ({
  key,
  title,
  unit,
  widthMillimeters,
  decimalPlaces,
  required,
});

export function createEmptyCell(columnDefinition: ArchitectureTableColumn): ArchitectureTableCell {
  return {
    cellId: id("cell"),
    columnKey: columnDefinition.key,
    displayValue: "",
    numericValue: null,
    unit: columnDefinition.unit,
    fieldPath: "",
    formula: "",
    state: "unknown",
    source: "",
    rowSpan: 1,
    columnSpan: 1,
  };
}

export function createEmptyRow(columns: ArchitectureTableColumn[]): ArchitectureTableRow {
  return {
    rowId: id("row"),
    rowType: "Data",
    keepTogether: true,
    cells: columns.map(createEmptyCell),
  };
}

const seedRow = (
  columns: ArchitectureTableColumn[],
  values: Record<string, string>,
  states: Partial<Record<string, FieldState>> = {},
): ArchitectureTableRow => {
  const row = createEmptyRow(columns);
  return {
    ...row,
    cells: row.cells.map((cell) => {
      const displayValue = values[cell.columnKey] ?? "";
      const numericValue =
        displayValue.trim() !== "" && Number.isFinite(Number(displayValue))
          ? Number(displayValue)
          : null;
      return {
        ...cell,
        displayValue,
        numericValue,
        state: states[cell.columnKey] ?? (displayValue ? "pending" : "unknown"),
      };
    }),
  };
};

export function createProfessionalTableTemplate(
  tableType: ProfessionalTableType,
  tableNumber: string,
): ArchitectureTable {
  if (tableType === "interiorFinish") {
    const columns = [
      column("room", "房间或部位", 32),
      column("floor", "楼地面", 42),
      column("skirting", "踢脚/墙裙", 34, false),
      column("wall", "内墙面", 44),
      column("ceiling", "顶棚", 40),
      column("fireRating", "燃烧性能", 24, false),
      column("reference", "做法索引", 28, false),
      column("remark", "备注", 38, false),
    ];
    return {
      tableId: id("table"),
      schemaVersion: 1,
      tableType,
      tableNumber,
      title: "室内装修做法表",
      repeatHeader: true,
      allowSplitAcrossPages: true,
      columns,
      rows: [
        seedRow(columns, { room: "门厅、走道" }),
        seedRow(columns, { room: "楼梯间" }),
        seedRow(columns, { room: "卫生间" }),
        seedRow(columns, { room: "设备用房" }),
      ],
      formulaAudits: [],
    };
  }

  if (tableType === "buildingSafetyMeasures") {
    const columns = [
      column("location", "部位", 30),
      column("risk", "安全风险", 38),
      column("measure", "防护措施", 68),
      column("dimension", "控制尺寸", 28, false),
      column("material", "材料及构造", 42, false),
      column("drawing", "节点索引", 28, false),
      column("responsibility", "责任专业", 28, false),
      column("status", "确认状态", 24),
    ];
    return {
      tableId: id("table"),
      schemaVersion: 1,
      tableType,
      tableNumber,
      title: "建筑安全措施表",
      repeatHeader: true,
      allowSplitAcrossPages: true,
      columns,
      rows: [
        seedRow(columns, { location: "屋面临边", risk: "人员坠落" }),
        seedRow(columns, { location: "低窗及落地窗", risk: "人员坠落" }),
        seedRow(columns, { location: "楼梯及平台", risk: "滑倒、坠落" }),
      ],
      formulaAudits: [],
    };
  }

  if (tableType === "accessibilityFacilities") {
    const columns = [
      column("facility", "设施项目", 34),
      column("location", "设置位置", 38),
      column("requirement", "设计要求", 62),
      column("design", "设计参数/做法", 58),
      column("drawing", "图纸索引", 28, false),
      column("status", "确认状态", 24),
      column("remark", "备注", 34, false),
    ];
    return {
      tableId: id("table"),
      schemaVersion: 1,
      tableType,
      tableNumber,
      title: "无障碍设施表",
      repeatHeader: true,
      allowSplitAcrossPages: true,
      columns,
      rows: [
        seedRow(columns, { facility: "无障碍出入口" }),
        seedRow(columns, { facility: "无障碍通行流线" }),
        seedRow(columns, { facility: "无障碍停车位" }),
        seedRow(columns, { facility: "无障碍卫生间" }),
        seedRow(columns, { facility: "无障碍电梯" }),
      ],
      formulaAudits: [],
    };
  }

  if (tableType === "waterproofDesign") {
    const columns = [
      column("location", "部位", 28),
      column("grade", "防水等级", 24),
      column("requirement", "设防要求", 42),
      column("material", "防水材料", 45),
      column("layers", "道数", 16, true, "道"),
      column("detail", "细部及排水措施", 66),
      column("drawing", "节点索引", 28, false),
    ];
    return {
      tableId: id("table"),
      schemaVersion: 1,
      tableType,
      tableNumber,
      title: "防水设计表",
      repeatHeader: true,
      allowSplitAcrossPages: true,
      columns,
      rows: [
        seedRow(columns, { location: "屋面" }),
        seedRow(columns, { location: "地下室外墙" }),
      ],
      formulaAudits: [],
    };
  }

  const columns = [
    column("item", "指标名称", 45),
    column("planning", "规划值", 30, false, "", 2),
    column("design", "设计值", 30, true, "", 2),
    column("approved", "核准值", 30, false, "", 2),
    column("source", "数据来源", 48),
    column("difference", "差异", 25, false, "", 2),
  ];
  return {
    tableId: id("table"),
    schemaVersion: 1,
    tableType,
    tableNumber,
    title: "主要技术经济指标表",
    repeatHeader: true,
    allowSplitAcrossPages: true,
    columns,
    rows: [
      seedRow(columns, { item: "总建筑面积" }),
      seedRow(columns, { item: "计容建筑面积" }),
      seedRow(columns, { item: "容积率" }),
      seedRow(columns, { item: "建筑密度" }),
    ],
    formulaAudits: [],
  };
}

export function normalizeProfessionalTable(table: ArchitectureTable): ArchitectureTable {
  const columns = table.columns.map((item, index) => ({
    key: item.key || `column${index + 1}`,
    title: item.title || `列${index + 1}`,
    unit: item.unit ?? "",
    widthMillimeters: Math.max(10, Number(item.widthMillimeters) || 24),
    decimalPlaces: Math.max(0, Math.min(6, Number(item.decimalPlaces) || 0)),
    required: item.required ?? false,
  }));
  return {
    ...table,
    columns,
    rows: table.rows.map((row) => ({
      ...row,
      keepTogether: row.keepTogether ?? true,
      cells: columns.map((definition) => {
        const current = row.cells.find((cell) => cell.columnKey === definition.key);
        return current
          ? {
              ...createEmptyCell(definition),
              ...current,
              columnKey: definition.key,
            }
          : createEmptyCell(definition);
      }),
    })),
    formulaAudits: table.formulaAudits ?? [],
  };
}

export function setTableCellValue(
  table: ArchitectureTable,
  rowIndex: number,
  columnIndex: number,
  displayValue: string,
): ArchitectureTable {
  const columnDefinition = table.columns[columnIndex];
  const targetCell = table.rows[rowIndex]?.cells.find(
    (cell) => cell.columnKey === columnDefinition.key,
  );
  const overridesFormula = Boolean(targetCell?.formula);
  const overridesBinding = Boolean(targetCell?.fieldPath);
  return {
    ...table,
    rows: table.rows.map((row, currentRowIndex) =>
      currentRowIndex !== rowIndex
        ? row
        : {
            ...row,
            cells: row.cells.map((cell) =>
              cell.columnKey !== columnDefinition.key
                ? cell
                : {
                    ...cell,
                    displayValue,
                    numericValue:
                      displayValue.trim() !== "" && Number.isFinite(Number(displayValue))
                        ? Number(displayValue)
                        : null,
                    state: overridesFormula || overridesBinding
                      ? "overridden"
                      : displayValue.trim()
                        ? "pending"
                        : "unknown",
                    source: overridesFormula
                      ? "人工覆盖公式结果"
                      : overridesBinding
                        ? "人工修改绑定值"
                        : cell.source,
                  },
            ),
          },
    ),
    formulaAudits: table.formulaAudits.map((audit) =>
      audit.cellId === targetCell?.cellId
        ? {
            ...audit,
            isManuallyOverridden: true,
            overrideReason: "用户直接修改公式结果",
          }
        : audit,
    ),
  };
}

export function pasteTableCells(
  table: ArchitectureTable,
  startRow: number,
  startColumn: number,
  clipboardText: string,
): ArchitectureTable {
  const matrix = clipboardText
    .replace(/\r/g, "")
    .split("\n")
    .filter((line, index, lines) => line.length > 0 || index < lines.length - 1)
    .map((line) => line.split("\t"));
  if (matrix.length === 0) return table;

  let next = table;
  const requiredRows = startRow + matrix.length;
  while (next.rows.length < requiredRows) {
    next = { ...next, rows: [...next.rows, createEmptyRow(next.columns)] };
  }
  matrix.forEach((row, rowOffset) => {
    row.forEach((value, columnOffset) => {
      const columnIndex = startColumn + columnOffset;
      if (columnIndex < next.columns.length) {
        next = setTableCellValue(next, startRow + rowOffset, columnIndex, value);
      }
    });
  });
  return recalculateTechnicalTable(next);
}

export function recalculateTechnicalTable(table: ArchitectureTable): ArchitectureTable {
  if (table.tableType !== "technicalEconomicIndicators") return table;
  const planningIndex = table.columns.findIndex((item) => item.key === "planning");
  const designIndex = table.columns.findIndex((item) => item.key === "design");
  const differenceIndex = table.columns.findIndex((item) => item.key === "difference");
  if (planningIndex < 0 || designIndex < 0 || differenceIndex < 0) return table;

  return {
    ...table,
    rows: table.rows.map((row) => {
      const planning = row.cells[planningIndex]?.numericValue;
      const design = row.cells[designIndex]?.numericValue;
      if (planning == null || design == null) return row;
      const precision = table.columns[differenceIndex].decimalPlaces;
      const difference = Number((design - planning).toFixed(precision));
      return {
        ...row,
        cells: row.cells.map((cell, index) =>
          index === differenceIndex
            ? {
                ...cell,
                displayValue: difference.toFixed(precision),
                numericValue: difference,
                formula: "design - planning",
                source: "公式",
                state: "confirmed",
              }
            : cell,
        ),
      };
    }),
  };
}

export function validateProfessionalTable(table: ArchitectureTable): TableValidationIssue[] {
  const issues: TableValidationIssue[] = [];
  table.rows.forEach((row, rowIndex) => {
    table.columns.forEach((definition, columnIndex) => {
      const cell = row.cells[columnIndex];
      if (cell?.rowSpan === 0 || cell?.columnSpan === 0) return;
      const value = cell?.displayValue.trim() ?? "";
      if (definition.required && !value) {
        issues.push({
          rowId: row.rowId,
          columnKey: definition.key,
          message: `第 ${rowIndex + 1} 行“${definition.title}”不能为空`,
        });
      }
      if (
        value &&
        table.tableType === "technicalEconomicIndicators" &&
        ["planning", "design", "approved", "difference"].includes(definition.key) &&
        !Number.isFinite(Number(value))
      ) {
        issues.push({
          rowId: row.rowId,
          columnKey: definition.key,
          message: `第 ${rowIndex + 1} 行“${definition.title}”应填写数字`,
        });
      }
      if (
        value &&
        table.tableType === "waterproofDesign" &&
        definition.key === "layers" &&
        (!Number.isInteger(Number(value)) || Number(value) <= 0)
      ) {
        issues.push({
          rowId: row.rowId,
          columnKey: definition.key,
          message: `第 ${rowIndex + 1} 行“道数”应填写正整数`,
        });
      }
    });
  });
  return issues;
}

export function tableToCsv(table: ArchitectureTable): string {
  const escape = (value: string) => `"${value.replace(/"/g, '""')}"`;
  return [
    table.columns.map((item) => escape(`${item.title}${item.unit ? `（${item.unit}）` : ""}`)).join(","),
    ...table.rows.map((row) =>
      table.columns
        .map((definition) =>
          escape(row.cells.find((cell) => cell.columnKey === definition.key)?.displayValue ?? ""),
        )
        .join(","),
    ),
  ].join("\r\n");
}

export function getSelectionBounds(selection: TableSelection) {
  return {
    firstRow: Math.min(selection.startRow, selection.endRow),
    lastRow: Math.max(selection.startRow, selection.endRow),
    firstColumn: Math.min(selection.startColumn, selection.endColumn),
    lastColumn: Math.max(selection.startColumn, selection.endColumn),
  };
}

export function isCellInSelection(
  rowIndex: number,
  columnIndex: number,
  selection: TableSelection,
) {
  const bounds = getSelectionBounds(selection);
  return (
    rowIndex >= bounds.firstRow &&
    rowIndex <= bounds.lastRow &&
    columnIndex >= bounds.firstColumn &&
    columnIndex <= bounds.lastColumn
  );
}

export function mergeSelectedCells(
  table: ArchitectureTable,
  selection: TableSelection,
): ArchitectureTable {
  const bounds = getSelectionBounds(selection);
  if (
    bounds.firstRow === bounds.lastRow &&
    bounds.firstColumn === bounds.lastColumn
  ) {
    throw new Error("请至少选择两个单元格。");
  }
  for (let rowIndex = bounds.firstRow; rowIndex <= bounds.lastRow; rowIndex += 1) {
    for (
      let columnIndex = bounds.firstColumn;
      columnIndex <= bounds.lastColumn;
      columnIndex += 1
    ) {
      const cell = table.rows[rowIndex]?.cells[columnIndex];
      if (!cell || cell.rowSpan !== 1 || cell.columnSpan !== 1) {
        throw new Error("选区包含已合并单元格，请先拆分后再合并。");
      }
    }
  }
  return {
    ...table,
    rows: table.rows.map((row, rowIndex) => ({
      ...row,
      cells: row.cells.map((cell, columnIndex) => {
        if (!isCellInSelection(rowIndex, columnIndex, selection)) return cell;
        if (rowIndex === bounds.firstRow && columnIndex === bounds.firstColumn) {
          return {
            ...cell,
            rowSpan: bounds.lastRow - bounds.firstRow + 1,
            columnSpan: bounds.lastColumn - bounds.firstColumn + 1,
          };
        }
        return { ...cell, rowSpan: 0, columnSpan: 0 };
      }),
    })),
  };
}

export function splitMergedCell(
  table: ArchitectureTable,
  rowIndex: number,
  columnIndex: number,
): ArchitectureTable {
  const anchor = table.rows[rowIndex]?.cells[columnIndex];
  if (!anchor || anchor.rowSpan <= 1 && anchor.columnSpan <= 1) {
    throw new Error("当前单元格不是合并单元格。");
  }
  const lastRow = rowIndex + anchor.rowSpan - 1;
  const lastColumn = columnIndex + anchor.columnSpan - 1;
  return {
    ...table,
    rows: table.rows.map((row, currentRow) => ({
      ...row,
      cells: row.cells.map((cell, currentColumn) =>
        currentRow >= rowIndex &&
        currentRow <= lastRow &&
        currentColumn >= columnIndex &&
        currentColumn <= lastColumn
          ? { ...cell, rowSpan: 1, columnSpan: 1 }
          : cell,
      ),
    })),
  };
}

type FormulaToken = {
  type: "number" | "reference" | "identifier" | "operator" | "left" | "right" | "comma";
  value: string;
};

function tokenizeFormula(formula: string): FormulaToken[] {
  const source = formula.trim().replace(/^=/, "");
  const tokens: FormulaToken[] = [];
  let index = 0;
  while (index < source.length) {
    const rest = source.slice(index);
    const whitespace = rest.match(/^\s+/);
    if (whitespace) {
      index += whitespace[0].length;
      continue;
    }
    const reference = rest.match(/^\[([A-Za-z][A-Za-z0-9_]*)\]/);
    if (reference) {
      tokens.push({ type: "reference", value: reference[1] });
      index += reference[0].length;
      continue;
    }
    const number = rest.match(/^(?:\d+(?:\.\d*)?|\.\d+)/);
    if (number) {
      tokens.push({ type: "number", value: number[0] });
      index += number[0].length;
      continue;
    }
    const identifier = rest.match(/^[A-Za-z][A-Za-z0-9_]*/);
    if (identifier) {
      tokens.push({ type: "identifier", value: identifier[0].toUpperCase() });
      index += identifier[0].length;
      continue;
    }
    const comparator = rest.match(/^(>=|<=|==|!=|>|<)/);
    if (comparator) {
      tokens.push({ type: "operator", value: comparator[0] });
      index += comparator[0].length;
      continue;
    }
    const character = source[index];
    if ("+-*/".includes(character)) tokens.push({ type: "operator", value: character });
    else if (character === "(") tokens.push({ type: "left", value: character });
    else if (character === ")") tokens.push({ type: "right", value: character });
    else if (character === ",") tokens.push({ type: "comma", value: character });
    else throw new Error(`公式包含不允许的字符“${character}”。`);
    index += 1;
  }
  return tokens;
}

class SafeFormulaParser {
  private index = 0;
  readonly inputs: Record<string, number> = {};

  constructor(
    private readonly tokens: FormulaToken[],
    private readonly values: Readonly<Record<string, number>>,
  ) {}

  parse() {
    if (this.tokens.length === 0) throw new Error("公式不能为空。");
    const result = this.parseComparison();
    if (this.index !== this.tokens.length) throw new Error("公式格式不完整。");
    if (!Number.isFinite(result)) throw new Error("公式结果不是有效数字。");
    return result;
  }

  private current() {
    return this.tokens[this.index];
  }

  private take(type?: FormulaToken["type"], value?: string) {
    const token = this.current();
    if (!token || type && token.type !== type || value && token.value !== value) return null;
    this.index += 1;
    return token;
  }

  private require(type: FormulaToken["type"], value?: string) {
    const token = this.take(type, value);
    if (!token) throw new Error("公式括号或参数格式不正确。");
    return token;
  }

  private parseComparison(): number {
    let value = this.parseAdditive();
    const operator = this.current();
    if (operator?.type === "operator" && [">", "<", ">=", "<=", "==", "!="].includes(operator.value)) {
      this.index += 1;
      const right = this.parseAdditive();
      value = Number(
        operator.value === ">" ? value > right :
        operator.value === "<" ? value < right :
        operator.value === ">=" ? value >= right :
        operator.value === "<=" ? value <= right :
        operator.value === "==" ? value === right :
        value !== right,
      );
    }
    return value;
  }

  private parseAdditive(): number {
    let value = this.parseMultiplicative();
    while (this.current()?.type === "operator" && ["+", "-"].includes(this.current().value)) {
      const operator = this.current().value;
      this.index += 1;
      const right = this.parseMultiplicative();
      value = operator === "+" ? value + right : value - right;
    }
    return value;
  }

  private parseMultiplicative(): number {
    let value = this.parseUnary();
    while (this.current()?.type === "operator" && ["*", "/"].includes(this.current().value)) {
      const operator = this.current().value;
      this.index += 1;
      const right = this.parseUnary();
      if (operator === "/" && right === 0) throw new Error("公式不能除以零。");
      value = operator === "*" ? value * right : value / right;
    }
    return value;
  }

  private parseUnary(): number {
    if (this.take("operator", "+")) return this.parseUnary();
    if (this.take("operator", "-")) return -this.parseUnary();
    return this.parsePrimary();
  }

  private parsePrimary(): number {
    const number = this.take("number");
    if (number) return Number(number.value);
    const reference = this.take("reference");
    if (reference) {
      const value = this.values[reference.value];
      if (!Number.isFinite(value)) throw new Error(`字段 [${reference.value}] 没有有效数字。`);
      this.inputs[reference.value] = value;
      return value;
    }
    const identifier = this.take("identifier");
    if (identifier) {
      this.require("left");
      const args: number[] = [];
      if (!this.take("right")) {
        do {
          args.push(this.parseComparison());
        } while (this.take("comma"));
        this.require("right");
      }
      return this.callFunction(identifier.value, args);
    }
    if (this.take("left")) {
      const value = this.parseComparison();
      this.require("right");
      return value;
    }
    throw new Error("公式缺少数字、列引用或函数。");
  }

  private callFunction(name: string, args: number[]) {
    if (name === "SUM") return args.reduce((sum, value) => sum + value, 0);
    if (name === "MIN" && args.length > 0) return Math.min(...args);
    if (name === "MAX" && args.length > 0) return Math.max(...args);
    if (name === "ABS" && args.length === 1) return Math.abs(args[0]);
    if (name === "COUNT") return args.filter(Number.isFinite).length;
    if (name === "ROUND" && args.length === 2) {
      const precision = Math.max(0, Math.min(6, Math.trunc(args[1])));
      return Number(args[0].toFixed(precision));
    }
    if (name === "IF" && args.length === 3) return args[0] !== 0 ? args[1] : args[2];
    if (!["SUM", "MIN", "MAX", "ROUND", "IF", "ABS", "COUNT"].includes(name)) {
      throw new Error(`函数 ${name} 不在白名单中。`);
    }
    throw new Error(`函数 ${name} 的参数数量不正确。`);
  }
}

export function evaluateTableFormula(
  formula: string,
  row: ArchitectureTableRow,
): FormulaEvaluation {
  const values = Object.fromEntries(
    row.cells
      .map((cell) => [
        cell.columnKey,
        cell.numericValue ?? Number(cell.displayValue),
      ] as const)
      .filter((entry) => Number.isFinite(entry[1])),
  );
  const parser = new SafeFormulaParser(tokenizeFormula(formula), values);
  return { result: parser.parse(), inputs: parser.inputs };
}

export function applyTableFormula(
  table: ArchitectureTable,
  rowIndex: number,
  columnIndex: number,
  formula: string,
): ArchitectureTable {
  const row = table.rows[rowIndex];
  const cell = row?.cells[columnIndex];
  if (!row || !cell) throw new Error("没有找到要计算的单元格。");
  const evaluation = evaluateTableFormula(formula, row);
  const precision = table.columns[columnIndex]?.decimalPlaces ?? 2;
  const result = Number(evaluation.result.toFixed(precision));
  const nextCell = {
    ...cell,
    displayValue: result.toFixed(precision),
    numericValue: result,
    formula,
    source: "公式",
    state: "confirmed" as const,
  };
  return {
    ...table,
    rows: table.rows.map((current, currentIndex) =>
      currentIndex === rowIndex
        ? {
            ...current,
            cells: current.cells.map((item, index) =>
              index === columnIndex ? nextCell : item,
            ),
          }
        : current,
    ),
    formulaAudits: [
      ...table.formulaAudits.filter((audit) => audit.cellId !== cell.cellId),
      {
        auditId: id("audit"),
        cellId: cell.cellId,
        formula,
        formulaVersion: "1" as const,
        inputs: evaluation.inputs,
        result,
        calculatedAt: new Date().toISOString(),
        isManuallyOverridden: false,
        overrideReason: "",
      },
    ].slice(-500),
  };
}

export function bindTableCellToProjectField(
  table: ArchitectureTable,
  rowIndex: number,
  columnIndex: number,
  field: ProjectField | null,
): ArchitectureTable {
  const row = table.rows[rowIndex];
  const cell = row?.cells[columnIndex];
  if (!row || !cell) throw new Error("没有找到要绑定的单元格。");
  return {
    ...table,
    rows: table.rows.map((currentRow, currentRowIndex) =>
      currentRowIndex !== rowIndex
        ? currentRow
        : {
            ...currentRow,
            cells: currentRow.cells.map((currentCell, currentColumnIndex) =>
              currentColumnIndex !== columnIndex
                ? currentCell
                : {
                    ...currentCell,
                    fieldPath: field?.path ?? "",
                    displayValue: field?.value ?? currentCell.displayValue,
                    numericValue:
                      field?.value && Number.isFinite(Number(field.value))
                        ? Number(field.value)
                        : field
                          ? null
                          : currentCell.numericValue,
                    unit: field?.unit ?? currentCell.unit,
                    source: field?.source ?? currentCell.source,
                    state: field?.state ?? currentCell.state,
                  },
            ),
          },
    ),
  };
}

export function synchronizeBoundTableCells(
  tables: ArchitectureTable[],
  fields: ProjectField[],
  overwriteConflicts = false,
): TableBindingSyncResult {
  const fieldsByPath = new Map(fields.map((field) => [field.path, field]));
  const skippedConflicts: TableBindingConflict[] = [];
  const missingPaths = new Set<string>();
  let updatedCount = 0;

  const nextTables = tables.map((table) => ({
    ...table,
    rows: table.rows.map((row) => ({
      ...row,
      cells: row.cells.map((cell) => {
        if (!cell.fieldPath) return cell;
        const field = fieldsByPath.get(cell.fieldPath);
        if (!field) {
          missingPaths.add(cell.fieldPath);
          return cell;
        }
        const hasConflict =
          cell.state === "overridden" ||
          cell.source === "人工修改绑定值" ||
          Boolean(cell.formula);
        if (hasConflict && !overwriteConflicts) {
          skippedConflicts.push({
            tableId: table.tableId,
            tableTitle: table.title,
            rowId: row.rowId,
            cellId: cell.cellId,
            fieldPath: field.path,
            fieldLabel: field.label,
            currentValue: cell.displayValue,
            projectValue: field.value,
          });
          return cell;
        }
        const nextNumericValue =
          field.value.trim() !== "" && Number.isFinite(Number(field.value))
            ? Number(field.value)
            : null;
        const unchanged =
          cell.displayValue === field.value &&
          cell.numericValue === nextNumericValue &&
          cell.unit === (field.unit ?? "") &&
          cell.source === field.source &&
          cell.state === field.state;
        if (unchanged) return cell;
        updatedCount += 1;
        return {
          ...cell,
          displayValue: field.value,
          numericValue: nextNumericValue,
          unit: field.unit ?? "",
          source: field.source,
          state: field.state,
          formula: hasConflict && overwriteConflicts ? "" : cell.formula,
        };
      }),
    })),
  }));

  return {
    tables: nextTables.map(recalculateTechnicalTable),
    updatedCount,
    skippedConflicts,
    missingFieldPaths: [...missingPaths].sort(),
  };
}
