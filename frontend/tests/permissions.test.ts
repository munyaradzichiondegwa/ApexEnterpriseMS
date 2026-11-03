import { describe, expect, it } from "vitest";
import { canView, hasAnyInModule, hasPermission } from "../lib/permissions";

describe("hasPermission (mirrors the API's evaluator)", () => {
  it("grants everything to *", () => {
    expect(hasPermission(["*"], "gl.journals.post")).toBe(true);
  });

  it("grants an exact permission only", () => {
    expect(hasPermission(["gl.journals.post"], "gl.journals.post")).toBe(true);
    expect(hasPermission(["gl.journals.post"], "gl.journals.approve")).toBe(false);
    expect(hasPermission(["gl.journals.post"], "gl.journals.reverse")).toBe(false);
  });

  it("supports branch wildcards on whole segments", () => {
    expect(hasPermission(["gl.*"], "gl.journals.post")).toBe(true);
    expect(hasPermission(["gl.journals.*"], "gl.journals.post")).toBe(true);
    expect(hasPermission(["gl.journals.*"], "gl.accounts.read")).toBe(false);
    expect(hasPermission(["gl.*"], "glx.read")).toBe(false);
    expect(hasPermission(["gl.*"], "ar.read")).toBe(false);
  });

  it("is case-insensitive and ignores blanks", () => {
    expect(hasPermission(["GL.Journals.POST", "  "], "gl.journals.post")).toBe(true);
  });

  it("denies by default", () => {
    expect(hasPermission([], "gl.read")).toBe(false);
    expect(hasPermission(undefined, "gl.read")).toBe(false);
    expect(hasPermission(null, "gl.read")).toBe(false);
    expect(hasPermission(["*"], "")).toBe(false);
  });

  it("a read-only role can read but not post, reverse or close", () => {
    const readOnly = ["gl.accounts.read", "gl.journals.read", "reports.financial.read"];
    expect(hasPermission(readOnly, "reports.financial.read")).toBe(true);
    for (const p of ["gl.journals.post", "gl.journals.reverse", "gl.journals.approve", "accounting.period.close"]) {
      expect(hasPermission(readOnly, p)).toBe(false);
    }
  });
});

describe("module navigation", () => {
  it("shows a module when the user holds any permission inside it", () => {
    expect(hasAnyInModule(["gl.journals.read"], "gl")).toBe(true);
    expect(hasAnyInModule(["gl.*"], "gl")).toBe(true);
    expect(hasAnyInModule(["*"], "payroll")).toBe(true);
    expect(hasAnyInModule(["gl.journals.read"], "payroll")).toBe(false);
    expect(hasAnyInModule(["glx.read"], "gl")).toBe(false);
  });

  it("always allows the dashboard and unknown (non-module) pages", () => {
    expect(canView("/dashboard", [])).toBe(true);
    expect(canView("/login", [])).toBe(true);
    expect(canView("/", [])).toBe(true);
  });

  it("gates module pages, including their sub-pages", () => {
    expect(canView("/gl", ["gl.accounts.read"])).toBe(true);
    expect(canView("/gl/anything", ["gl.accounts.read"])).toBe(true);
    expect(canView("/payroll", ["gl.accounts.read"])).toBe(false);
    expect(canView("/admin", ["gl.accounts.read"])).toBe(false);
    expect(canView("/admin", ["admin.users.manage"])).toBe(true);
  });

  it("maps the cash-bank page to the API's cashbank module", () => {
    expect(canView("/cash-bank", ["cashbank.read"])).toBe(true);
    expect(canView("/cash-bank", ["cash-bank.read"])).toBe(false);
  });
});
