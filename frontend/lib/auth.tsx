"use client";

import { createContext, useContext, useEffect, useState, ReactNode } from "react";
import { useRouter } from "next/navigation";
import { api, extractErrorMessage } from "./api";

interface AuthUser {
  userId: string;
  companyId: string;
  email: string;
  role: string;
}

interface AuthContextValue {
  user: AuthUser | null;
  loading: boolean;
  login: (email: string, password: string) => Promise<void>;
  register: (companyName: string, email: string, password: string, firstName: string, lastName: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [loading, setLoading] = useState(true);
  const router = useRouter();

  useEffect(() => {
    const stored = typeof window !== "undefined" ? localStorage.getItem("apexerp_user") : null;
    if (stored) setUser(JSON.parse(stored));
    setLoading(false);
  }, []);

  function persistSession(tokens: any, role: string) {
    localStorage.setItem("apexerp_access_token", tokens.accessToken);
    localStorage.setItem("apexerp_refresh_token", tokens.refreshToken);
    const u: AuthUser = { userId: tokens.userId, companyId: tokens.companyId, email: tokens.email ?? "", role };
    localStorage.setItem("apexerp_user", JSON.stringify(u));
    setUser(u);
  }

  async function login(email: string, password: string) {
    try {
      const { data } = await api.post("/auth/login", { email, password });
      persistSession({ ...data, email }, data.role);
      router.push("/dashboard");
    } catch (err) {
      throw new Error(extractErrorMessage(err));
    }
  }

  async function register(companyName: string, email: string, password: string, firstName: string, lastName: string) {
    try {
      const { data } = await api.post("/auth/register", { companyName, email, password, firstName, lastName });
      persistSession({ ...data, email }, data.role);
      router.push("/dashboard");
    } catch (err) {
      throw new Error(extractErrorMessage(err));
    }
  }

  function logout() {
    localStorage.removeItem("apexerp_access_token");
    localStorage.removeItem("apexerp_refresh_token");
    localStorage.removeItem("apexerp_user");
    setUser(null);
    router.push("/login");
  }

  return <AuthContext.Provider value={{ user, loading, login, register, logout }}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
