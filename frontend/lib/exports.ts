// CSV export for any table on screen (spec 24.3). Cells that begin with = + - @ or a tab/CR can be run as formulas by spreadsheet
// programs ("CSV injection"), so they are prefixed with an apostrophe. Pure functions so they can be tested.

export interface CsvColumn<T> {
  header: string;
  value: (row: T) => string | number | boolean | null | undefined;
}

export function csvCell(raw: string | number | boolean | null | undefined): string {
  if (raw === null || raw === undefined) return "";
  let text = typeof raw === "string" ? raw : String(raw);
  if (typeof raw === "string" && /^[=+\-@\t\r]/.test(text)) text = "'" + text;
  return /[",\n\r]/.test(text) ? '"' + text.replace(/"/g, '""') + '"' : text;
}

export function toCsv<T>(rows: readonly T[], columns: readonly CsvColumn<T>[]): string {
  const lines = [columns.map((c) => csvCell(c.header)).join(",")];
  for (const row of rows) lines.push(columns.map((c) => csvCell(c.value(row))).join(","));
  return lines.join("\r\n");
}

export function downloadCsv<T>(filename: string, rows: readonly T[], columns: readonly CsvColumn<T>[]): void {
  const blob = new Blob(["\ufeff" + toCsv(rows, columns)], { type: "text/csv;charset=utf-8" });   // BOM so Excel reads UTF-8
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url; a.download = filename.endsWith(".csv") ? filename : filename + ".csv";
  document.body.appendChild(a); a.click(); a.remove();
  URL.revokeObjectURL(url);
}
