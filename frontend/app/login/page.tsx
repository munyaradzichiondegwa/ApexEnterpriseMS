"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { MfaRequiredError, useAuth } from "@/lib/auth";
import ConfigError from "@/components/ConfigError";
import BrandMark from "@/components/BrandMark";

export default function LoginPage() {
  const { login, sessionNotice, configError } = useAuth();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [totp, setTotp] = useState("");
  const [needsCode, setNeedsCode] = useState(false);
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
      await login(email, password, needsCode ? totp : undefined);
    } catch (err: any) {
      if (err instanceof MfaRequiredError) { setNeedsCode(true); setError(null); }
      else setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-brand-900 px-4 py-8">
      <div className="w-full max-w-6xl grid grid-cols-1 lg:grid-cols-2 gap-8 lg:gap-12 items-center">
        <div className="text-center mb-8">
          <div className="flex justify-center"><BrandMark /></div>
          <div className="text-brand-300 text-sm mt-1">Zimbabwe-first Enterprise Resource Planning</div>
        </div>
        <form onSubmit={onSubmit} className="card space-y-4">
          <div>
            <label className="label" htmlFor="email">Email</label>
            <input id="email" className="input" type="email" required value={email} onChange={(e) => setEmail(e.target.value)} placeholder="you@company.co.zw" />
          </div>
          <div>
            <label className="label" htmlFor="password">Password</label>
            <input id="password" className="input" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
          </div>
          {needsCode && (
            <div>
              <label className="label" htmlFor="totp">Verification code</label>
              <input id="totp" className="input" inputMode="numeric" autoComplete="one-time-code" autoFocus required value={totp}
                onChange={(e) => setTotp(e.target.value)} placeholder="6-digit code or a recovery code" />
              <p className="text-xs text-slate-500 mt-1">Open your authenticator app, or use one of your recovery codes.</p>
            </div>
          )}
          {!error && (sessionNotice || expiredFromUrl) && (
            <p className="text-sm text-amber-700 bg-amber-50 border border-amber-200 rounded-lg px-3 py-2" role="status">
              {sessionNotice ?? "Your session has expired. Please sign in again."}
            </p>
          )}
          {error && <p className="text-sm text-red-600" role="alert">{error}</p>}
          <button className="btn-primary w-full" disabled={busy}>{busy ? "Signing in…" : needsCode ? "Verify and sign in" : "Sign in"}</button>
          <p className="text-sm text-center text-slate-500">
            No account? <Link href="/register" className="text-brand-600 font-medium">Register your company</Link>
          </p>
        </form>
      </div>
      <div className="hidden lg:block">
        <div className="overflow-hidden rounded-2xl bg-white shadow-2xl ring-1 ring-white/10">
          <img
            src="/apexerp-hero.png"
            alt="ApexERP dashboard displayed on a desktop computer in a modern office"
            className="block w-full h-auto"
          />
        </div>
        <p className="mt-4 text-center text-sm text-brand-200">Integrate. Optimize. Scale.</p>
      </div>
    </div>
  );
}
