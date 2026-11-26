"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api, extractErrorMessage } from "./api";

/** Loads a GET endpoint and exposes data, a loading flag, an error message and a reload function. Safe against unmounting mid-request. */
export function useLoad<T>(path: string | null, initial: T) {
  const [data, setData] = useState<T>(initial);
  const [loading, setLoading] = useState<boolean>(path !== null);
  const [error, setError] = useState<string | null>(null);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  const reload = useCallback(async () => {
    if (path === null) return;
    setLoading(true);
    try {
      const res = await api.get<T>(path);
      if (alive.current) { setData(res.data); setError(null); }
    } catch (e) {
      if (alive.current) setError(extractErrorMessage(e));
    } finally {
      if (alive.current) setLoading(false);
    }
  }, [path]);

  useEffect(() => { reload(); }, [reload]);
  return { data, loading, error, reload, setData };
}

/** Wraps a write action with a busy flag and success/error messages, so every button behaves the same way. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const run = useCallback(async <R,>(work: () => Promise<R>, success?: string | ((r: R) => string)): Promise<R | undefined> => {
    setBusy(true); setError(null); setNotice(null);
    try {
      const result = await work();
      if (success) setNotice(typeof success === "function" ? success(result) : success);
      return result;
    } catch (e) {
      setError(extractErrorMessage(e));
      return undefined;
    } finally {
      setBusy(false);
    }
  }, []);

  const clear = useCallback(() => { setError(null); setNotice(null); }, []);
  return { busy, error, notice, run, clear };
}
