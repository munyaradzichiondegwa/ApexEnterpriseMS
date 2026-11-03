"use client";

import { clearSession, safeStorage } from "@/lib/session";

/** Last-resort boundary for errors in the root layout. It replaces the whole document, so it must not rely on app styles. */
export default function GlobalError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  console.error(error);
  return (
    <html lang="en">
      <body style={{ fontFamily: "system-ui, sans-serif", display: "flex", minHeight: "100vh", alignItems: "center", justifyContent: "center", background: "#f8fafc", margin: 0 }}>
        <div role="alert" style={{ maxWidth: 420, padding: 24, background: "#fff", border: "1px solid #e2e8f0", borderRadius: 12, textAlign: "center" }}>
          <h1 style={{ fontSize: 18, margin: "0 0 8px" }}>ApexERP could not start</h1>
          <p style={{ fontSize: 14, color: "#475569", margin: "0 0 16px" }}>An unexpected error occurred. Your data has not been affected.</p>
          <button onClick={() => reset()} style={{ marginRight: 8, padding: "8px 14px" }}>Try again</button>
          <button
            onClick={() => {
              clearSession(safeStorage());
              window.location.href = "/login";
            }}
            style={{ padding: "8px 14px" }}
          >
            Sign in again
          </button>
        </div>
      </body>
    </html>
  );
}
