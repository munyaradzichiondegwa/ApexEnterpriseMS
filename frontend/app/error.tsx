"use client";

import { useEffect } from "react";
import { clearSession, safeStorage } from "@/lib/session";

/** Route-level error boundary: an unexpected client error shows a way forward instead of a blank screen. */
export default function ErrorPage({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  useEffect(() => {
    console.error(error);
  }, [error]);

  return (
    <div className="min-h-screen flex items-center justify-center px-4 bg-slate-50">
      <div className="card max-w-md space-y-4 text-center" role="alert">
        <h1 className="text-lg font-bold">Something went wrong</h1>
        <p className="text-sm text-slate-600">An unexpected error stopped this page from loading. Your data has not been affected.</p>
        <div className="flex justify-center gap-2">
          <button className="btn-primary" onClick={() => reset()}>Try again</button>
          <button
            className="btn-secondary"
            onClick={() => {
              clearSession(safeStorage());
              window.location.href = "/login";
            }}
          >
            Sign in again
          </button>
        </div>
      </div>
    </div>
  );
}
