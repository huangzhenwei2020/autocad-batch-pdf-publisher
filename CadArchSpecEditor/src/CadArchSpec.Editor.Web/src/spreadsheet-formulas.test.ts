import { describe, expect, it } from "vitest";
import type { ArchitectureTable } from "./editor-model";
import { deleteTableColumn, deleteTableRow, normalizeProfessionalTable } from "./professional-tables";
import { recalculateSpreadsheetTable, translateSpreadsheetFormula } from "./spreadsheet-formulas";
import { fillTableDown } from "./professional-table-editor";

const table = (): ArchitectureTable => ({
  tableId: "test", schemaVersion: 1, tableType: "custom", tableNumber: "", title: "test",
  repeatHeader: false, allowSplitAcrossPages: false, formulaAudits: [],
  columns: ["A", "B", "C"].map((title) => ({ key: title, title, unit: "", widthMillimeters: 30, decimalPlaces: 2, required: false })),
  rows: Array.from({ length: 3 }, (_, row) => ({ rowId: `r${row}`, rowType: "Data", keepTogether: true,
    cells: ["A", "B", "C"].map((key, column) => ({ cellId: `${row}-${column}`, columnKey: key, displayValue: String(row * 3 + column + 1), numericValue: row * 3 + column + 1, unit: "", fieldPath: "", formula: "", state: "unknown", source: "", rowSpan: 1, columnSpan: 1 })) })),
});

describe("CAD spreadsheet formulas", () => {
  it("calculates A1 references, ranges and common functions", () => {
    const source = table();
    source.rows[2].cells[2].formula = "=SUM(A1:B2)+AVERAGE(A1:A3)";
    expect(recalculateSpreadsheetTable(source).rows[2].cells[2].displayValue).toBe("16");
  });

  it("reports circular references", () => {
    const source = table();
    source.rows[0].cells[0].formula = "=B1";
    source.rows[0].cells[1].formula = "=A1";
    expect(() => recalculateSpreadsheetTable(source)).toThrow(/循环引用/);
  });

  it("moves relative references but preserves absolute and mixed references", () => {
    expect(translateSpreadsheetFormula("=A1+$B$2+C$3+$D4", 2, 1)).toBe("=B3+$B$2+D$3+$D6");
  });

  it("calculates formulas containing absolute references", () => {
    const source = table();
    source.rows[2].cells[2].formula = "=$A$1+B$2+$C1";
    expect(recalculateSpreadsheetTable(source).rows[2].cells[2].displayValue).toBe("9");
  });
});

describe("CAD merged cells", () => {
  it("keeps a merged area when a row inside it is deleted", () => {
    const source = table();
    source.rows[0].cells[0] = { ...source.rows[0].cells[0], displayValue: "标题", rowSpan: 3, columnSpan: 2 };
    for (let row = 0; row < 3; row++) for (let column = 0; column < 2; column++) if (row || column)
      source.rows[row].cells[column] = { ...source.rows[row].cells[column], rowSpan: 0, columnSpan: 0 };
    const result = deleteTableRow(source, 1);
    expect(result.rows[0].cells[0]).toMatchObject({ displayValue: "标题", rowSpan: 2, columnSpan: 2 });
  });

  it("moves a merged anchor when its first column is deleted", () => {
    const source = table();
    source.rows[0].cells[0] = { ...source.rows[0].cells[0], displayValue: "标题", rowSpan: 2, columnSpan: 3 };
    for (let row = 0; row < 2; row++) for (let column = 0; column < 3; column++) if (row || column)
      source.rows[row].cells[column] = { ...source.rows[row].cells[column], rowSpan: 0, columnSpan: 0 };
    const result = deleteTableColumn(source, 0);
    expect(result.rows[0].cells[0]).toMatchObject({ displayValue: "标题", rowSpan: 2, columnSpan: 2 });
  });
});

describe("CAD source dimensions", () => {
  it("keeps original CAD dimensions while normalizing an editor table", () => {
    const source = table();
    source.columns[0].sourceWidthCadUnits = 1200;
    source.rows[0].sourceHeightCadUnits = 300;
    const normalized = normalizeProfessionalTable(source);
    expect(normalized.columns[0].sourceWidthCadUnits).toBe(1200);
    expect(normalized.rows[0].sourceHeightCadUnits).toBe(300);
  });
});

describe("CAD fill handle", () => {
  it("continues a two-number series and copies cell formatting", () => {
    const source = table();
    source.rows[0].cells[0] = { ...source.rows[0].cells[0], displayValue: "1", numericValue: 1, fillColorIndex: 3 };
    source.rows[1].cells[0] = { ...source.rows[1].cells[0], displayValue: "2", numericValue: 2, fillColorIndex: 4 };
    const result = fillTableDown(source, { startRow: 0, startColumn: 0, endRow: 1, endColumn: 0 }, 2);
    expect(result.rows[2].cells[0]).toMatchObject({ displayValue: "3", numericValue: 3, fillColorIndex: 3 });
  });

  it("copies formulas with relative references adjusted", () => {
    const source = table();
    source.rows[0].cells[1].formula = "=A1";
    const result = fillTableDown(source, { startRow: 0, startColumn: 1, endRow: 0, endColumn: 1 }, 2);
    expect(result.rows[1].cells[1].formula).toBe("=A2");
    expect(result.rows[2].cells[1].formula).toBe("=A3");
    expect(result.rows[2].cells[1].displayValue).toBe("7");
  });
});
