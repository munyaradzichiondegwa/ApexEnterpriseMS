import { describe, expect, it } from "vitest";
import { extractErrorMessage } from "../lib/errors";

const http = (status: number, data?: unknown) => ({ response: { status, data } });

describe("extractErrorMessage", () => {
  it("prefers the API's own message", () => {
    expect(extractErrorMessage(http(400, { error: "Journal entry is not balanced." }))).toBe("Journal entry is not balanced.");
  });

  it("reads ASP.NET validation errors", () => {
    const body = { title: "One or more validation errors occurred.", errors: { Email: ["The Email field is not a valid e-mail address."] } };
    expect(extractErrorMessage(http(400, body))).toBe("The Email field is not a valid e-mail address.");
  });

  it("falls back to detail, title, message, or plain text", () => {
    expect(extractErrorMessage(http(500, { detail: "boom" }))).toBe("boom");
    expect(extractErrorMessage(http(500, { title: "An unexpected error occurred." }))).toBe("An unexpected error occurred.");
    expect(extractErrorMessage(http(500, "plain text failure"))).toBe("plain text failure");
  });

  it("ignores HTML error pages from proxies", () => {
    expect(extractErrorMessage(http(502, "<html><body>Bad gateway</body></html>"))).toBe("The server returned an error (502).");
  });

  it("gives plain-language messages for the status codes users meet", () => {
    expect(extractErrorMessage(http(401))).toMatch(/session has expired/i);
    expect(extractErrorMessage(http(403))).toMatch(/do not have permission/i);
    expect(extractErrorMessage(http(404))).toMatch(/not found/i);
    expect(extractErrorMessage(http(409))).toMatch(/conflicts/i);
    expect(extractErrorMessage(http(500))).toBe("The server returned an error (500).");
  });

  it("explains a timeout", () => {
    expect(extractErrorMessage({ code: "ECONNABORTED", message: "timeout of 20000ms exceeded" })).toMatch(/took too long/i);
  });

  it("explains an unreachable API, naming it when known", () => {
    const networkError = { code: "ERR_NETWORK", message: "Network Error" };
    expect(extractErrorMessage(networkError, "https://api.example.co.zw")).toContain("https://api.example.co.zw");
    expect(extractErrorMessage(networkError, "https://api.example.co.zw")).toMatch(/CORS/);
    expect(extractErrorMessage(networkError)).toBe("Cannot reach the ApexERP API.");
  });

  it("never returns an empty message", () => {
    expect(extractErrorMessage(undefined)).toBe("Something went wrong.");
    expect(extractErrorMessage(new Error(""))).toBe("Something went wrong.");
    expect(extractErrorMessage(new Error("custom"))).toBe("custom");
  });
});
