// Browser session storage: validated reads, safe writes, and no assumptions that storage exists or contains good data.
//
// The previous implementation did JSON.parse(localStorage.getItem(...)) with no guard. One corrupt value (a manual edit,
// an interrupted write, an older app version) threw inside an effect, so the "loading" flag was never cleared and the app
// sat on "Loading ApexERP…" forever (audit finding 10). Everything here is defensive and returns a result instead of throwing.

export const STORAGE_KEYS = {
  accessToken: "apexerp_access_token",
  refreshToken: "apexerp_refresh_token",
  user: "apexerp_user",
} as const;

export interface AuthUser {
  userId: string;
  companyId: string;
  email: string;
  role: string;
  /** Permission keys granted by the user's role (see the API's permission catalogue). */
  permissions: string[];
  /** When the access token expires (ISO 8601), or null if unknown. */
  expiresAt: string | null;
}

/** The subset of the login/register response the session needs. */
export interface LoginResponse {
  accessToken: string;
  refreshToken: string;
  expiresAt?: string | null;
  userId: string;
  companyId: string;
  role: string;
  permissions?: string[];
}

export type StorageLike = Pick<Storage, "getItem" | "setItem" | "removeItem">;

export type InvalidReason = "corrupt" | "legacy" | "expired" | "unavailable";

export type SessionReadResult =
  | { status: "none" }
  | { status: "ok"; user: AuthUser; accessToken: string }
  | { status: "invalid"; reason: InvalidReason };

/** Access to localStorage can itself throw (blocked cookies, private mode, SSR), so never touch it unguarded. */
export function safeStorage(): StorageLike | null {
  try {
    return typeof window !== "undefined" ? window.localStorage : null;
  } catch {
    return null;
  }
}

export function isExpired(expiresAt: string | null | undefined, now: number = Date.now(), skewMs = 5_000): boolean {
  if (!expiresAt) return false;
  const when = Date.parse(expiresAt);
  return Number.isNaN(when) ? true : when - skewMs <= now;
}

const isNonEmptyString = (v: unknown): v is string => typeof v === "string" && v.trim() !== "";

export function readSession(storage: StorageLike | null, now: number = Date.now()): SessionReadResult {
  if (!storage) return { status: "none" };

  let rawUser: string | null;
  let accessToken: string | null;
  try {
    rawUser = storage.getItem(STORAGE_KEYS.user);
    accessToken = storage.getItem(STORAGE_KEYS.accessToken);
  } catch {
    return { status: "invalid", reason: "unavailable" };
  }

  if (rawUser == null && accessToken == null) return { status: "none" };
  if (rawUser == null || !isNonEmptyString(accessToken)) return { status: "invalid", reason: "corrupt" };

  let parsed: any;
  try {
    parsed = JSON.parse(rawUser);
  } catch {
    return { status: "invalid", reason: "corrupt" };
  }

  if (!parsed || typeof parsed !== "object" || !isNonEmptyString(parsed.userId) ||
      !isNonEmptyString(parsed.companyId) || !isNonEmptyString(parsed.role) ||
      (parsed.email != null && typeof parsed.email !== "string")) {
    return { status: "invalid", reason: "corrupt" };
  }

  // Sessions saved before permissions existed have no permission list: ask the user to sign in again to get one.
  if (!Array.isArray(parsed.permissions) || !parsed.permissions.every((p: unknown) => typeof p === "string")) {
    return { status: "invalid", reason: "legacy" };
  }

  const expiresAt: string | null = typeof parsed.expiresAt === "string" ? parsed.expiresAt : null;
  if (isExpired(expiresAt, now)) return { status: "invalid", reason: "expired" };

  return {
    status: "ok",
    accessToken,
    user: {
      userId: parsed.userId,
      companyId: parsed.companyId,
      email: parsed.email ?? "",
      role: parsed.role,
      permissions: parsed.permissions,
      expiresAt,
    },
  };
}

export function writeSession(storage: StorageLike, tokens: LoginResponse, email: string): AuthUser {
  const user: AuthUser = {
    userId: tokens.userId,
    companyId: tokens.companyId,
    email,
    role: tokens.role,
    permissions: Array.isArray(tokens.permissions) ? tokens.permissions : [],
    expiresAt: tokens.expiresAt ?? null,
  };
  try {
    storage.setItem(STORAGE_KEYS.accessToken, tokens.accessToken);
    storage.setItem(STORAGE_KEYS.refreshToken, tokens.refreshToken);
    storage.setItem(STORAGE_KEYS.user, JSON.stringify(user));
  } catch {
    clearSession(storage);
    throw new Error("Your browser would not let ApexERP save your sign-in (private mode or blocked storage). Allow site data and try again.");
  }
  return user;
}

export function clearSession(storage: StorageLike | null): void {
  if (!storage) return;
  for (const key of Object.values(STORAGE_KEYS)) {
    try {
      storage.removeItem(key);
    } catch {
      /* nothing more can be done */
    }
  }
}

export function readAccessToken(storage: StorageLike | null): string | null {
  try {
    return storage?.getItem(STORAGE_KEYS.accessToken) ?? null;
  } catch {
    return null;
  }
}

export const SESSION_NOTICES: Record<InvalidReason, string> = {
  corrupt: "Your saved session could not be read, so you have been signed out. Please sign in again.",
  legacy: "ApexERP has been updated. Please sign in again to continue.",
  expired: "Your session has expired. Please sign in again.",
  unavailable: "Your browser blocked access to saved data. Allow site data for this site, then sign in again.",
};
