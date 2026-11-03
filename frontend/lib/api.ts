"use client";

import axios from "axios";
import { API_BASE_URL, API_CONFIG_ERROR } from "./config";
import { extractErrorMessage as describeError } from "./errors";
import { clearSession, readAccessToken, safeStorage } from "./session";

export { API_BASE_URL, API_CONFIG_ERROR };

export const api = axios.create({
  baseURL: `${API_BASE_URL}/api/v1`,
  headers: { "Content-Type": "application/json" },
  // Never leave a screen waiting forever on a server that is down or unreachable.
  timeout: 20_000,
});

api.interceptors.request.use((config) => {
  // A build with no usable API URL cannot work; say so instead of sending requests to the wrong place.
  if (API_CONFIG_ERROR) return Promise.reject(new Error(API_CONFIG_ERROR));

  const token = readAccessToken(safeStorage());
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

// A 401 on a normal request means the saved session is no longer valid (the access token lasts 15 minutes).
// A 401 from the auth endpoints themselves just means "wrong e-mail or password" and must not log anyone out.
api.interceptors.response.use(
  (res) => res,
  (error) => {
    const status = error?.response?.status;
    const url: string = error?.config?.url ?? "";
    if (typeof window !== "undefined" && status === 401 && !url.startsWith("/auth/")) {
      clearSession(safeStorage());
      const path = window.location.pathname;
      if (!path.startsWith("/login") && !path.startsWith("/register")) {
        window.location.href = "/login?reason=expired";
      }
    }
    return Promise.reject(error);
  }
);

export function extractErrorMessage(err: unknown): string {
  return describeError(err, API_BASE_URL);
}
