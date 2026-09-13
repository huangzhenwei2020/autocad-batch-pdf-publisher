import type { ArchitectureTable } from "./editor-model";

type Token = { type: "number" | "cell" | "name" | "operator" | "left" | "right" | "comma" | "colon"; value: string };
type FormulaValue = number | number[];

const cellAddress = /^([A-Z]+)([1-9]\d*)$/;

function tokens(source: string): Token[] {
  const result: Token[] = [];
  let value = source.trim().replace(/^=/, "");
  while (value.length) {
    const whitespace = value.match(/^\s+/)?.[0];
    if (whitespace) { value = value.slice(whitespace.length); continue; }
    const number = value.match(/^(?:\d+(?:\.\d*)?|\.\d+)/)?.[0];
    if (number) { result.push({ type: "number", value: number }); value = value.slice(number.length); continue; }
    const word = value.match(/^[A-Za-z]+\d*/)?.[0];
    if (word) {
      const upper = word.toUpperCase();
      result.push({ type: cellAddress.test(upper) ? "cell" : "name", value: upper });
      value = value.slice(word.length); continue;
    }
    const operator = value.match(/^(>=|<=|<>|!=|=|>|<|[+\-*/])/)?.[0];
    if (operator) { result.push({ type: "operator", value: operator }); value = value.slice(operator.length); continue; }
    const punctuation: Record<string, Token["type"]> = { "(": "left", ")": "right", ",": "comma", ":": "colon" };
    const type = punctuation[value[0]];
    if (!type) throw new Error(`公式包含不支持的字符“${value[0]}”。`);
    result.push({ type, value: value[0] }); value = value.slice(1);
  }
  return result;
}

function columnIndex(letters: string) {
  return [...letters].reduce((total, letter) => total * 26 + letter.charCodeAt(0) - 64, 0) - 1;
}

function coordinates(address: string) {
  const match = address.match(cellAddress);
  if (!match) throw new Error(`单元格地址 ${address} 无效。`);
  return { row: Number(match[2]) - 1, column: columnIndex(match[1]) };
}

class Parser {
  private index = 0;
  constructor(private readonly input: Token[], private readonly resolve: (address: string) => number) {}
  parse() { const value = this.comparison(); if (this.index !== this.input.length) throw new Error("公式格式不完整。"); return this.scalar(value); }
  private current() { return this.input[this.index]; }
  private take(type: Token["type"], value?: string) { const token = this.current(); if (!token || token.type !== type || value && token.value !== value) return null; this.index++; return token; }
  private scalar(value: FormulaValue) { if (Array.isArray(value)) throw new Error("区域只能作为函数参数。"); return value; }
  private comparison(): FormulaValue {
    let left = this.additive(); const token = this.current();
    if (token?.type === "operator" && ["=", "!=", "<>", ">", "<", ">=", "<="].includes(token.value)) {
      this.index++; const a = this.scalar(left); const b = this.scalar(this.additive());
      left = Number(token.value === "=" ? a === b : token.value === "!=" || token.value === "<>" ? a !== b : token.value === ">" ? a > b : token.value === "<" ? a < b : token.value === ">=" ? a >= b : a <= b);
    }
    return left;
  }
  private additive(): FormulaValue { let value = this.multiplicative(); while (this.current()?.type === "operator" && ["+", "-"].includes(this.current().value)) { const op = this.input[this.index++].value; value = op === "+" ? this.scalar(value) + this.scalar(this.multiplicative()) : this.scalar(value) - this.scalar(this.multiplicative()); } return value; }
  private multiplicative(): FormulaValue { let value = this.unary(); while (this.current()?.type === "operator" && ["*", "/"].includes(this.current().value)) { const op = this.input[this.index++].value; const right = this.scalar(this.unary()); value = op === "*" ? this.scalar(value) * right : this.scalar(value) / right; } return value; }
  private unary(): FormulaValue { if (this.take("operator", "+")) return this.unary(); if (this.take("operator", "-")) return -this.scalar(this.unary()); return this.primary(); }
  private primary(): FormulaValue {
    const number = this.take("number"); if (number) return Number(number.value);
    const cell = this.take("cell");
    if (cell) {
      if (!this.take("colon")) return this.resolve(cell.value);
      const end = this.take("cell"); if (!end) throw new Error("区域地址不完整。");
      const firstAt = coordinates(cell.value); const lastAt = coordinates(end.value); const values: number[] = [];
      for (let row = Math.min(firstAt.row, lastAt.row); row <= Math.max(firstAt.row, lastAt.row); row++)
        for (let column = Math.min(firstAt.column, lastAt.column); column <= Math.max(firstAt.column, lastAt.column); column++) {
          let letters = ""; for (let n = column + 1; n > 0; n = Math.floor((n - 1) / 26)) letters = String.fromCharCode(65 + (n - 1) % 26) + letters;
          values.push(this.resolve(`${letters}${row + 1}`));
        }
      return values;
    }
    const name = this.take("name");
    if (name) {
      if (!this.take("left")) throw new Error("函数缺少左括号。"); const args: FormulaValue[] = [];
      if (!this.take("right")) { do { args.push(this.comparison()); } while (this.take("comma")); if (!this.take("right")) throw new Error("函数缺少右括号。"); }
      const flat = args.flatMap((item) => Array.isArray(item) ? item : [item]);
      if (name.value === "SUM") return flat.reduce((sum, item) => sum + item, 0);
      if (name.value === "AVERAGE") return flat.length ? flat.reduce((sum, item) => sum + item, 0) / flat.length : 0;
      if (name.value === "MIN") return Math.min(...flat); if (name.value === "MAX") return Math.max(...flat);
      if (name.value === "COUNT") return flat.filter(Number.isFinite).length;
      if (name.value === "ROUND" && args.length === 2) return Number(this.scalar(args[0]).toFixed(Math.max(0, Math.min(10, Math.trunc(this.scalar(args[1]))))));
      if (name.value === "IF" && args.length === 3) return this.scalar(args[0]) !== 0 ? this.scalar(args[1]) : this.scalar(args[2]);
      throw new Error(`不支持函数 ${name.value} 或参数数量不正确。`);
    }
    if (this.take("left")) { const value = this.comparison(); if (!this.take("right")) throw new Error("缺少右括号。"); return value; }
    throw new Error("公式缺少数字、单元格地址或函数。");
  }
}

export function recalculateSpreadsheetTable(table: ArchitectureTable): ArchitectureTable {
  const cache = new Map<string, number>(); const resolving = new Set<string>();
  const resolve = (address: string): number => {
    if (cache.has(address)) return cache.get(address)!;
    if (resolving.has(address)) throw new Error(`公式存在循环引用：${address}`);
    resolving.add(address); const at = coordinates(address); const cell = table.rows[at.row]?.cells[at.column];
    if (!cell) throw new Error(`找不到单元格 ${address}。`);
    const value = cell.formula?.trim() ? new Parser(tokens(cell.formula), resolve).parse() : Number(cell.displayValue);
    const result = Number.isFinite(value) ? value : 0; cache.set(address, result); resolving.delete(address); return result;
  };
  return { ...table, rows: table.rows.map((row, rowIndex) => ({ ...row, cells: row.cells.map((cell, columnIndex) => {
    if (!cell.formula?.trim()) return cell;
    let letters = ""; for (let n = columnIndex + 1; n > 0; n = Math.floor((n - 1) / 26)) letters = String.fromCharCode(65 + (n - 1) % 26) + letters;
    const value = resolve(`${letters}${rowIndex + 1}`);
    return { ...cell, displayValue: Number.isInteger(value) ? String(value) : String(Number(value.toFixed(8))), numericValue: value, source: "公式" };
  }) })) };
}
