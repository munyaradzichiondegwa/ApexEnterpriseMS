import { describe, expect, it } from "vitest";
import {
  STORAGE_KEYS, SESSION_NOTICES, StorageLike, clearSession, isExpired, readAccessToken, readSession, writeSession,
} from "../lib/session";

/** An in-memory stand-in for localStorage. */
function fakeStorage(initial: Record<string, string> = {}): StorageLike & { data: Record<string, string> } {
  const data = { ...initial };
  return {
    data,
    getItem: (k: string) => (k in data ? data[k] : null),
    setItem: (k: string, v: string) => { data[k] = v; },
    removeItem: (k: string) => { delete data[k]; },
  };
}

const NOW = Date.parse("2026-10-25T10:00:00Z");
const FUTURE = "2026-10-25T10:15:00Z";
const PAST = "2026-10-25T09:00:00Z";

const validUser = (over: Record<string, unknown> = {}) =>
  JSON.stringify({ userId: "u1", companyId: "c1", email: "a@b.co", role: "Administrator", permissions: ["*"], expiresAt: FUTURE, ...over });

const withSession = (user: string, token: string | null = "token") =>
  fakeStorage({ [STORAGE_KEYS.user]: user, ...(token ? { [STORAGE_KEYS.accessToken]: token } : {}) });

describe("readSession", () => {
  it("returns none when nothing is stored, or when storage is unavailable", () => {
    expect(readSession(fakeStorage(), NOW)).toEqual({ status: "none" });
    expect(readSession(null, NOW)).toEqual({ status: "none" });
  });

  it("restores a valid session", () => {
    const result = readSession(withSession(validUser()), NOW);
    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.user.userId).toBe("u1");
      expect(result.user.permissions).toEqual(["*"]);
      expect(result.accessToken).toBe("token");
    }
  });

  // The audit's finding 10: JSON.parse of a damaged value used to throw inside an effect and leave the app on "Loading" forever.
  it.each([
    ["not json at all", "{broken"],
    ["empty string", ""],
    ["a JSON string, not an object", JSON.stringify("hello")],
    ["null", "null"],
    ["an array", "[]"],
    ["a user with no id", validUser({ userId: "" })],
    ["a user with no company", validUser({ companyId: undefined })],
    ["a user with no role", validUser({ role: null })],
    ["a non-string e-mail", validUser({ email: 42 })],
  ])("a corrupt saved user (%s) is reported, never thrown", (_label, raw) => {
    expect(() => readSession(withSession(raw), NOW)).not.toThrow();
    expect(readSession(withSession(raw), NOW)).toEqual({ status: "invalid", reason: "corrupt" });
  });

  it("treats a user without a token (or a token without a user) as corrupt", () => {
    expect(readSession(withSession(validUser(), null), NOW)).toEqual({ status: "invalid", reason: "corrupt" });
    expect(readSession(withSession(validUser(), "  "), NOW)).toEqual({ status: "invalid", reason: "corrupt" });
    expect(readSession(fakeStorage({ [STORAGE_KEYS.accessToken]: "token" }), NOW)).toEqual({ status: "invalid", reason: "corrupt" });
  });

  it("asks sessions saved before permissions existed to sign in again", () => {
    const legacy = JSON.stringify({ userId: "u1", companyId: "c1", email: "a@b.co", role: "Administrator" });
    expect(readSession(withSession(legacy), NOW)).toEqual({ status: "invalid", reason: "legacy" });
    expect(readSession(withSession(validUser({ permissions: [1, 2] })), NOW)).toEqual({ status: "invalid", reason: "legacy" });
  });

  it("rejects an expired session, and one with an unreadable expiry", () => {
    expect(readSession(withSession(validUser({ expiresAt: PAST })), NOW)).toEqual({ status: "invalid", reason: "expired" });
    expect(readSession(withSession(validUser({ expiresAt: "yesterday-ish" })), NOW)).toEqual({ status: "invalid", reason: "expired" });
  });

  it("accepts a session with no recorded expiry", () => {
    expect(readSession(withSession(validUser({ expiresAt: undefined })), NOW).status).toBe("ok");
  });

  it("does not throw when storage itself throws (blocked cookies / private mode)", () => {
    const hostile: StorageLike = {
      getItem: () => { throw new DOMException("blocked", "SecurityError"); },
      setItem: () => { throw new Error("blocked"); },
      removeItem: () => { throw new Error("blocked"); },
    };
    expect(readSession(hostile, NOW)).toEqual({ status: "invalid", reason: "unavailable" });
    expect(() => clearSession(hostile)).not.toThrow();
    expect(readAccessToken(hostile)).toBeNull();
  });

  it("has a user-facing notice for every invalid reason", () => {
    for (const reason of ["corrupt", "legacy", "expired", "unavailable"] as const) {
      expect(SESSION_NOTICES[reason].length).toBeGreaterThan(10);
    }
  });
});

describe("writeSession / clearSession", () => {
  const response = { accessToken: "a", refreshToken: "r", expiresAt: FUTURE, userId: "u1", companyId: "c1", role: "Accountant", permissions: ["gl.read"] };

  it("round-trips through readSession", () => {
    const storage = fakeStorage();
    const user = writeSession(storage, response, "a@b.co");
    expect(user.permissions).toEqual(["gl.read"]);
    const read = readSession(storage, NOW);
    expect(read.status).toBe("ok");
    if (read.status === "ok") expect(read.user).toEqual(user);
    expect(storage.data[STORAGE_KEYS.refreshToken]).toBe("r");
  });

  it("stores no permissions when the server sent none (least privilege)", () => {
    expect(writeSession(fakeStorage(), { ...response, permissions: undefined }, "a@b.co").permissions).toEqual([]);
  });

  it("explains itself, and leaves nothing behind, when the browser refuses to save", () => {
    const store: Record<string, string> = {};
    const failing: StorageLike = {
      getItem: (k) => store[k] ?? null,
      setItem: (k, v) => { if (k === STORAGE_KEYS.user) throw new Error("quota"); store[k] = v; },
      removeItem: (k) => { delete store[k]; },
    };
    expect(() => writeSession(failing, response, "a@b.co")).toThrow(/would not let ApexERP save/);
    expect(Object.keys(store)).toEqual([]);
  });

  it("clearSession removes every key", () => {
    const storage = withSession(validUser());
    storage.setItem(STORAGE_KEYS.refreshToken, "r");
    clearSession(storage);
    expect(storage.data).toEqual({});
  });
});

describe("isExpired", () => {
  it("handles missing, future, past and invalid values", () => {
    expect(isExpired(null, NOW)).toBe(false);
    expect(isExpired(undefined, NOW)).toBe(false);
    expect(isExpired(FUTURE, NOW)).toBe(false);
    expect(isExpired(PAST, NOW)).toBe(true);
    expect(isExpired("garbage", NOW)).toBe(true);
  });

  it("allows a small clock-skew margin before the real expiry", () => {
    expect(isExpired("2026-10-25T10:00:03Z", NOW)).toBe(true);   // 3 s left < 5 s margin
    expect(isExpired("2026-10-25T10:00:30Z", NOW)).toBe(false);
  });
});
