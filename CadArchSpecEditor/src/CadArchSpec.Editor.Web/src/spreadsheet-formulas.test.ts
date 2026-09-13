import { describe, expect, it } from "vitest";
import type { ArchitectureTable } from "./editor-model";
import { deleteTableColumn, deleteTableRow, normalizeProfessionalTable } from "./professional-tables";
import { recalculateSpreadsheetTable } from "./spreadsheet-formulas";

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
