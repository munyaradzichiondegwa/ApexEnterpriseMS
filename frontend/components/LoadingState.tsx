"use client";

import { useEffect, useState } from "react";
import { clearSession, safeStorage } from "@/lib/session";

/**
 * A loading screen that can never trap the user: if loading takes unusually long it offers a way out
 * (reload, or discard the saved session and sign in again).
 */
export default function LoadingState({ message = "Loading ApexERP…", slowAfterMs = 8000 }: { message?: string; slowAfterMs?: number }) {
  const [slow, setSlow] = useState(false);

  useEffect(() => {
    const timer = setTimeout(() => setSlow(true), slowAfterMs);
    return () => clearTimeout(timer);
  }, [slowAfterMs]);

  return (
    <div className="min-h-screen flex flex-col items-center justify-center gap-4 px-4 text-center text-slate-400">
      <div role="status">{message}</div>
      {slow && (
        <div className="max-w-sm space-y-3 text-sm text-slate-500">
          <p>This is taking longer than expected. The server may be unreachable, or your saved session may be damaged.</p>
          <div className="flex justify-center gap-2">
            <button className="btn-secondary" onClick={() => window.location.reload()}>Reload</button>
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
      )}
    </div>
  );
}
