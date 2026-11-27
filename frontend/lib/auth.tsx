"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState, ReactNode } from "react";
import { useRouter } from "next/navigation";
import { api, extractErrorMessage, isMfaRequired, refreshSession } from "./api";
import { API_CONFIG_ERROR } from "./config";
import { hasPermission } from "./permissions";
import { AuthUser, LoginResponse, SESSION_NOTICES, clearSession, readSession, safeStorage, writeSession } from "./session";

/** Thrown by login when the account has two-step verification on and no code was supplied yet. */
export class MfaRequiredError extends Error {
  constructor() { super("Enter the 6-digit code from your authenticator app."); this.name = "MfaRequiredError"; }
}

export type { AuthUser } from "./session";

interface AuthContextValue {
  user: AuthUser | null;
  /** True only while the saved session is being checked on first load; it is ALWAYS cleared, even if reading fails. */
  loading: boolean;
  /** Why a saved session was discarded (corrupt, expired, old format), so the sign-in page can explain it. */
  sessionNotice: string | null;
  /** Set when the app was built without a usable API address; nothing can work until that is fixed. */
  configError: string | null;
  /** Does the signed-in user's role grant this permission? (UI convenience; the API enforces it for real.) */
  can: (permission: string) => boolean;
  login: (email: string, password: string, totpCode?: string) => Promise<void>;
  register: (companyName: string, email: string, password: string, firstName: string, lastName: string) => Promise<void>;
  logout: () => void;
  /** Replace the cached profile (e.g. after the role or permissions changed). */
  refreshProfile: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

// setTimeout cannot take a delay above 2^31-1 ms.
const MAX_TIMER_MS = 2_147_483_647;

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [loading, setLoading] = useState(true);
  const [sessionNotice, setSessionNotice] = useState<string | null>(null);
  const router = useRouter();

  // Restore the saved session. The profile is only a cache: the HttpOnly refresh cookie decides whether the session is still alive, and
  // it also supplies the (memory-only) access token. Whatever happens, loading ends.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const storage = safeStorage();
        const result = readSession(storage);
        if (result.status === "invalid") {
          clearSession(storage);
          if (!cancelled) setSessionNotice(SESSION_NOTICES[result.reason]);
          return;
        }
        if (result.status === "none") return;

        const outcome = await refreshSession();
        if (cancelled) return;
        if (outcome.ok) {
          const saved = readSession(storage);
          setUser(saved.status === "ok" ? saved.user : result.user);
        } else if (outcome.reason === "rejected") {
          clearSession(storage);
          setSessionNotice(SESSION_NOTICES.expired);
        } else {
          setUser(result.user);   // the API is unreachable: keep the screen; requests will report the connection problem
        }
      } catch {
        clearSession(safeStorage());
        if (!cancelled) setSessionNotice(SESSION_NOTICES.corrupt);
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  const logout = useCallback(() => {
    // Ask the API to end the session (it revokes the refresh token and clears the cookie), then forget everything locally.
    api.post("/auth/logout", {}, { headers: { "X-Requested-With": "apexerp" } }).catch(() => undefined).finally(() => {
      clearSession(safeStorage());
      setUser(null);
      router.push("/login");
    });
  }, [router]);

  // Renew the access token a minute before it runs out. If the API refuses (the session was ended elsewhere, the password changed,
  // the account was locked), end the session cleanly instead of letting the next request fail with a confusing error.
  useEffect(() => {
    if (!user?.expiresAt) return;
    const remaining = Date.parse(user.expiresAt) - Date.now() - 60_000;
    if (Number.isNaN(remaining)) return;

    const timer = setTimeout(async () => {
      const outcome = await refreshSession();
      if (outcome.ok) {
        const saved = readSession(safeStorage());
        if (saved.status === "ok") setUser(saved.user);
      } else if (outcome.reason === "rejected") {
        clearSession(safeStorage());
        setUser(null);
        setSessionNotice(SESSION_NOTICES.expired);
        router.push("/login?reason=expired");
      }
    }, Math.min(Math.max(remaining, 0), MAX_TIMER_MS));
    return () => clearTimeout(timer);
  }, [user, router]);

  const startSession = useCallback(
    (data: LoginResponse, email: string) => {
      const storage = safeStorage();
      if (!storage) throw new Error("This browser does not allow ApexERP to save your sign-in.");
      const next = writeSession(storage, data, email);
      setSessionNotice(null);
      setUser(next);
      router.push("/dashboard");
    },
    [router]
  );

  const login = useCallback(
    async (email: string, password: string, totpCode?: string) => {
      try {
        const { data } = await api.post<LoginResponse>("/auth/login", { email, password, totpCode: totpCode || null });
        startSession(data, email);
      } catch (err) {
        if (isMfaRequired(err)) throw new MfaRequiredError();
        throw new Error(extractErrorMessage(err));
      }
    },
    [startSession]
  );

  const register = useCallback(
    async (companyName: string, email: string, password: string, firstName: string, lastName: string) => {
      try {
        const { data } = await api.post<LoginResponse>("/auth/register", { companyName, email, password, firstName, lastName });
        startSession(data, email);
      } catch (err) {
        throw new Error(extractErrorMessage(err));
      }
    },
    [startSession]
  );


  const refreshProfile = useCallback(async () => {
    const outcome = await refreshSession();
    if (outcome.ok) {
      const saved = readSession(safeStorage());
      if (saved.status === "ok") setUser(saved.user);
    }
  }, []);

  const can = useCallback((permission: string) => hasPermission(user?.permissions, permission), [user]);

  const value = useMemo<AuthContextValue>(
    () => ({ user, loading, sessionNotice, configError: API_CONFIG_ERROR, can, login, register, logout, refreshProfile }),
    [user, loading, sessionNotice, can, login, register, logout, refreshProfile]
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
