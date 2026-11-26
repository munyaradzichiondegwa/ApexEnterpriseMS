"use client";

import axios, { AxiosError, InternalAxiosRequestConfig } from "axios";
import { API_BASE_URL, API_CONFIG_ERROR, BROWSER_API_BASE } from "./config";
import { extractErrorMessage as describeError } from "./errors";
import { LoginResponse, clearSession, getAccessToken, readSession, safeStorage, setAccessToken, writeSession } from "./session";

export { API_BASE_URL, API_CONFIG_ERROR };

const API_ROOT = `${BROWSER_API_BASE}/api/v1`;

export const api = axios.create({
  baseURL: API_ROOT,
  headers: { "Content-Type": "application/json" },
  // The refresh-token cookie is sent with every call (it is only used by the auth endpoints).
  withCredentials: true,
  // Never leave a screen waiting forever on a server that is down or unreachable.
  timeout: 20_000,
});

// A plain client for the refresh call itself, so it can never trigger the 401-retry logic below (no loops).
const bare = axios.create({
  baseURL: API_ROOT,
  headers: { "Content-Type": "application/json", "X-Requested-With": "apexerp" },
  withCredentials: true,
  timeout: 20_000,
});

api.interceptors.request.use((config) => {
  // A build with no usable API URL cannot work; say so instead of sending requests to the wrong place.
  if (API_CONFIG_ERROR) return Promise.reject(new Error(API_CONFIG_ERROR));
  const token = getAccessToken();
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

/** Result of asking the API for a new access token using the HttpOnly refresh cookie. */
export type RefreshOutcome = { ok: true; data: LoginResponse } | { ok: false; reason: "rejected" | "unreachable" };

let inFlight: Promise<RefreshOutcome> | null = null;

/** Exchanges the refresh cookie for a new access token. Calls made at the same moment share one request (the token rotates on use). */
export function refreshSession(): Promise<RefreshOutcome> {
  if (inFlight) return inFlight;
  inFlight = bare
    .post<LoginResponse>("/auth/refresh", {})
    .then<RefreshOutcome>((res) => {
      setAccessToken(res.data.accessToken);
      const storage = safeStorage();
      const saved = storage ? readSession(storage) : null;
      if (storage && saved?.status === "ok") writeSession(storage, res.data, saved.user.email);
      return { ok: true, data: res.data };
    })
    .catch<RefreshOutcome>((err: AxiosError) => ({ ok: false, reason: err.response?.status === 401 || err.response?.status === 400 ? "rejected" : "unreachable" }))
    .finally(() => { inFlight = null; });
  return inFlight;
}

function sendToLogin() {
  clearSession(safeStorage());
  if (typeof window === "undefined") return;
  const path = window.location.pathname;
  if (!path.startsWith("/login") && !path.startsWith("/register")) window.location.href = "/login?reason=expired";
}

// A 401 on a normal request usually means the 15-minute access token ran out. Renew it once with the refresh cookie and repeat the
// request; only when that is refused is the person signed out. A 401 from the auth endpoints themselves just means "wrong password".
api.interceptors.response.use(
  (res) => res,
  async (error: AxiosError) => {
    const status = error?.response?.status;
    const original = error?.config as (InternalAxiosRequestConfig & { _retried?: boolean }) | undefined;
    const url: string = original?.url ?? "";
    if (typeof window === "undefined" || status !== 401 || !original || original._retried || url.startsWith("/auth/")) return Promise.reject(error);

    original._retried = true;
    const outcome = await refreshSession();
    if (outcome.ok) {
      original.headers.Authorization = `Bearer ${outcome.data.accessToken}`;
      return api(original);
    }
    if (outcome.reason === "rejected") sendToLogin();
    return Promise.reject(error);
  }
);

export function extractErrorMessage(err: unknown): string {
  return describeError(err, API_BASE_URL || BROWSER_API_BASE);
}

/** True when the API answered a sign-in with "password right, now give me the code from your authenticator app". */
export function isMfaRequired(err: unknown): boolean {
  return (err as AxiosError<{ mfaRequired?: boolean }>)?.response?.data?.mfaRequired === true;
}
