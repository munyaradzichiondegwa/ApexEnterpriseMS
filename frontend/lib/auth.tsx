"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState, ReactNode } from "react";
import { useRouter } from "next/navigation";
import { api, extractErrorMessage } from "./api";
import { API_CONFIG_ERROR } from "./config";
import { hasPermission } from "./permissions";
import { AuthUser, LoginResponse, SESSION_NOTICES, clearSession, readSession, safeStorage, writeSession } from "./session";

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
  login: (email: string, password: string) => Promise<void>;
  register: (companyName: string, email: string, password: string, firstName: string, lastName: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

// setTimeout cannot take a delay above 2^31-1 ms.
const MAX_TIMER_MS = 2_147_483_647;

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [loading, setLoading] = useState(true);
  const [sessionNotice, setSessionNotice] = useState<string | null>(null);
  const router = useRouter();

  // Restore the saved session. Whatever happens (missing, corrupt, expired, blocked storage), loading ends.
  useEffect(() => {
    try {
      const storage = safeStorage();
      const result = readSession(storage);
      if (result.status === "ok") {
        setUser(result.user);
      } else if (result.status === "invalid") {
        clearSession(storage);
        setSessionNotice(SESSION_NOTICES[result.reason]);
      }
    } catch {
      clearSession(safeStorage());
      setSessionNotice(SESSION_NOTICES.corrupt);
    } finally {
      setLoading(false);
    }
  }, []);

  const logout = useCallback(() => {
    clearSession(safeStorage());
    setUser(null);
    router.push("/login");
  }, [router]);

  // There is no silent refresh yet, so when the access token runs out, end the session cleanly instead of
  // letting the next request fail with a confusing error.
  useEffect(() => {
    if (!user?.expiresAt) return;
    const remaining = Date.parse(user.expiresAt) - Date.now();
    if (Number.isNaN(remaining)) return;

    const timer = setTimeout(() => {
      clearSession(safeStorage());
      setUser(null);
      setSessionNotice(SESSION_NOTICES.expired);
      router.push("/login?reason=expired");
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
    async (email: string, password: string) => {
      try {
        const { data } = await api.post<LoginResponse>("/auth/login", { email, password });
        startSession(data, email);
      } catch (err) {
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

  const can = useCallback((permission: string) => hasPermission(user?.permissions, permission), [user]);

  const value = useMemo<AuthContextValue>(
    () => ({ user, loading, sessionNotice, configError: API_CONFIG_ERROR, can, login, register, logout }),
    [user, loading, sessionNotice, can, login, register, logout]
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
