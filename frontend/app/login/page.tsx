"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useAuth } from "@/lib/auth";
import ConfigError from "@/components/ConfigError";

export default function LoginPage() {
  const { login, sessionNotice, configError } = useAuth();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [expiredFromUrl, setExpiredFromUrl] = useState(false);

  // The API client redirects here with ?reason=expired when a request finds the session no longer valid.
  useEffect(() => {
    setExpiredFromUrl(new URLSearchParams(window.location.search).get("reason") === "expired");
  }, []);

  if (configError) return <ConfigError message={configError} />;

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await login(email, password);
    } catch (err: any) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-brand-900 px-4">
      <div className="w-full max-w-sm">
        <div className="text-center mb-8">
          <div className="text-3xl font-bold text-white">ApexERP</div>
          <div className="text-brand-300 text-sm mt-1">Zimbabwe-first Enterprise Resource Planning</div>
        </div>
        <form onSubmit={onSubmit} className="card space-y-4">
          <div>
            <label className="label">Email</label>
            <input className="input" type="email" required value={email} onChange={(e) => setEmail(e.target.value)} placeholder="you@company.co.zw" />
          </div>
          <div>
            <label className="label">Password</label>
            <input className="input" type="password" required value={password} onChange={(e) => setPassword(e.target.value)} />
          </div>
          {!error && (sessionNotice || expiredFromUrl) && (
            <p className="text-sm text-amber-700 bg-amber-50 border border-amber-200 rounded-lg px-3 py-2" role="status">
              {sessionNotice ?? "Your session has expired. Please sign in again."}
            </p>
          )}
          {error && <p className="text-sm text-red-600" role="alert">{error}</p>}
          <button className="btn-primary w-full" disabled={busy}>{busy ? "Signing in…" : "Sign in"}</button>
          <p className="text-sm text-center text-slate-500">
            No account? <Link href="/register" className="text-brand-600 font-medium">Register your company</Link>
          </p>
        </form>
      </div>
    </div>
  );
}
