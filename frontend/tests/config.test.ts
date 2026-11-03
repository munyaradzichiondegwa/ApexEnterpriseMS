import { describe, expect, it } from "vitest";
import { resolveApiConfig } from "../lib/config";

describe("resolveApiConfig", () => {
  it("uses the configured URL, trimmed and without a trailing slash", () => {
    expect(resolveApiConfig(" https://api.example.co.zw/ ", true)).toEqual({ baseUrl: "https://api.example.co.zw", error: null });
    expect(resolveApiConfig("http://localhost:8080//", true)).toEqual({ baseUrl: "http://localhost:8080", error: null });
  });

  it("falls back to localhost ONLY in development", () => {
    expect(resolveApiConfig(undefined, false)).toEqual({ baseUrl: "http://localhost:8080", error: null });
    expect(resolveApiConfig("", false).baseUrl).toBe("http://localhost:8080");
  });

  it("reports a missing URL in a production build instead of silently pointing at localhost", () => {
    for (const missing of [undefined, null, "", "   "]) {
      const result = resolveApiConfig(missing, true);
      expect(result.baseUrl).toBe("");
      expect(result.error).toMatch(/NEXT_PUBLIC_API_URL was not set/);
    }
  });

  it("rejects values that are not http(s) URLs", () => {
    for (const bad of ["not a url", "ftp://api.example.com", "api.example.com", "javascript:alert(1)"]) {
      const result = resolveApiConfig(bad, true);
      expect(result.baseUrl).toBe("");
      expect(result.error).toMatch(/not a valid http\(s\) URL/);
    }
  });
});
