import { describe, expect, it } from "vitest";
import { csvCell, toCsv } from "../lib/exports";
import { fmtDate, fmtMoney, fmtNumber, humanise, splitCsv } from "../lib/format";

describe("csvCell", () => {
  it("quotes commas, quotes and line breaks", () => {
    expect(csvCell("a,b")).toBe('"a,b"');
    expect(csvCell('say "hi"')).toBe('"say ""hi"""');
    expect(csvCell("two\nlines")).toBe('"two\nlines"');
    expect(csvCell("plain")).toBe("plain");
  });

  it("neutralises spreadsheet formulas (CSV injection)", () => {
    for (const evil of ["=HYPERLINK(\"http://x\")", "+1+1", "-2+3", "@SUM(A1)", "\tcmd"]) {
      expect(csvCell(evil).replace(/^"/, "").startsWith("'")).toBe(true);
    }
  });

  it("leaves real numbers alone, including negatives", () => {
    expect(csvCell(-12.5)).toBe("-12.5");
    expect(csvCell(0)).toBe("0");
    expect(csvCell(null)).toBe("");
    expect(csvCell(undefined)).toBe("");
    expect(csvCell(true)).toBe("true");
  });
});

describe("toCsv", () => {
  it("writes a header row and one line per record", () => {
    const csv = toCsv([{ a: "x", n: 1 }, { a: "=bad", n: 2 }], [{ header: "A", value: (r) => r.a }, { header: "N", value: (r) => r.n }]);
    expect(csv.split("\r\n")).toEqual(["A,N", "x,1", "'=bad,2"]);
  });
});

describe("format helpers", () => {
  it("formats money and numbers, and shows a dash for missing values", () => {
    expect(fmtMoney(1234.5)).toBe("$1,234.50");
    expect(fmtMoney(null)).toBe("-");
    expect(fmtMoney(Number.NaN)).toBe("-");
    expect(fmtNumber(1234.567, 1)).toBe("1,234.6");
    expect(fmtNumber(undefined)).toBe("-");
  });

  it("formats dates defensively", () => {
    expect(fmtDate("2026-11-05T10:20:00Z")).toBe("2026-11-05");
    expect(fmtDate("not a date")).toBe("-");
    expect(fmtDate(null)).toBe("-");
  });

  it("splits lists and humanises codes", () => {
    expect(splitCsv(" AR, Manual ,, AP ")).toEqual(["AR", "Manual", "AP"]);
    expect(humanise("NrvWriteDown")).toBe("Nrv Write Down");
    expect(humanise("INDEPENDENTLY_REVIEWED")).toBe("INDEPENDENTLY REVIEWED");
    expect(humanise(null)).toBe("");
  });
});
