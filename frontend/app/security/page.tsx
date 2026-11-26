"use client";

import QRCode from "qrcode";
import { useState } from "react";
import AppShell from "@/components/AppShell";
import { Notice, PageHeader, Section, TextField } from "@/components/ui";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { useAction } from "@/lib/hooks";

type Setup = { secret: string; provisioningUri: string };

export default function SecurityPage() {
  const { user, logout } = useAuth();
  const pwd = useAction();
  const mfa = useAction();
  const [p, setP] = useState({ current: "", next: "", confirm: "" });
  const [setup, setSetup] = useState<Setup | null>(null);
  const [qr, setQr] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [recovery, setRecovery] = useState<string[] | null>(null);
  const [off, setOff] = useState({ password: "", code: "" });

  const change = (e: React.FormEvent) => { e.preventDefault();
    if (p.next !== p.confirm) { pwd.run(async () => { throw new Error("The new passwords do not match."); }); return; }
    pwd.run(async () => { await api.post("/auth/change-password", { currentPassword: p.current, newPassword: p.next }); setTimeout(logout, 1500); }, "Password changed. You have been signed out everywhere and will be taken to the sign-in page."); };

  const begin = () => mfa.run(async () => { const r = await api.post<Setup>("/auth/mfa/setup"); setSetup(r.data); setQr(await QRCode.toDataURL(r.data.provisioningUri, { margin: 1, width: 220 })); });
  const confirm = (e: React.FormEvent) => { e.preventDefault(); mfa.run(async () => { const r = await api.post("/auth/mfa/confirm", { code }); setRecovery(r.data.recoveryCodes); setSetup(null); setQr(null); setCode(""); }, "Two-step verification is on. Save your recovery codes now."); };
  const disable = (e: React.FormEvent) => { e.preventDefault(); mfa.run(async () => { await api.post("/auth/mfa/disable", { password: off.password, code: off.code }); setOff({ password: "", code: "" }); setRecovery(null); }, "Two-step verification is off."); };

  return (
    <AppShell>
      <PageHeader title="Account security" subtitle={`Signed in as ${user?.email ?? ""}. Protect your account with a strong password and a second step at sign-in.`} />

      <Section title="Change password" description="At least 12 characters, not containing your e-mail address. Changing it signs you out on every device.">
        <Notice error={pwd.error} notice={pwd.notice} />
        <form onSubmit={change} className="grid md:grid-cols-3 gap-4 max-w-4xl">
          <TextField label="Current password" type="password" autoComplete="current-password" required value={p.current} onChange={(e) => setP({ ...p, current: e.target.value })} />
          <TextField label="New password" type="password" autoComplete="new-password" minLength={12} required value={p.next} onChange={(e) => setP({ ...p, next: e.target.value })} />
          <TextField label="Repeat new password" type="password" autoComplete="new-password" minLength={12} required value={p.confirm} onChange={(e) => setP({ ...p, confirm: e.target.value })} />
          <div className="md:col-span-3"><button className="btn-primary" disabled={pwd.busy}>Change password</button></div>
        </form>
      </Section>

      <Section title="Two-step verification" description="After your password, sign-in asks for a 6-digit code from an authenticator app (Microsoft Authenticator, Google Authenticator, Authy, 1Password and others).">
        <Notice error={mfa.error} notice={mfa.notice} />
        {!setup && !recovery && (
          <div className="flex flex-wrap gap-3">
            <button className="btn-primary" disabled={mfa.busy} onClick={begin}>Set up two-step verification</button>
            <span className="text-sm text-slate-500 self-center">Already on? Turn it off below.</span>
          </div>)}
        {setup && (
          <form onSubmit={confirm} className="grid md:grid-cols-2 gap-6 max-w-3xl">
            <div>
              <p className="text-sm mb-2">1. Scan this code with your authenticator app.</p>
              {qr && <img src={qr} alt="QR code for your authenticator app" width={220} height={220} className="border rounded" />}
              <p className="text-xs text-slate-500 mt-2">Cannot scan? Enter this key instead: <code className="break-all select-all">{setup.secret}</code></p>
            </div>
            <div className="space-y-3">
              <p className="text-sm">2. Enter the 6-digit code it shows to finish.</p>
              <TextField label="Code" inputMode="numeric" autoComplete="one-time-code" required pattern="[0-9]{6}" value={code} onChange={(e) => setCode(e.target.value)} />
              <button className="btn-primary" disabled={mfa.busy}>Turn on</button>
            </div>
          </form>)}
        {recovery && (
          <div className="max-w-xl">
            <p className="text-sm mb-2"><strong>Recovery codes.</strong> Each works once if you lose your phone. Store them somewhere safe; they are not shown again.</p>
            <ul className="grid grid-cols-2 gap-2 font-mono bg-slate-50 border rounded p-4">{recovery.map((c) => <li key={c}>{c}</li>)}</ul>
            <div className="flex gap-2 mt-3"><button className="btn-secondary" onClick={() => navigator.clipboard?.writeText(recovery.join("\n"))}>Copy</button><button className="btn-secondary print:hidden" onClick={() => window.print()}>Print</button><button className="btn-primary" onClick={() => setRecovery(null)}>I have saved them</button></div>
          </div>)}
        <hr className="my-6" />
        <h3 className="font-semibold mb-2">Turn off</h3>
        <form onSubmit={disable} className="grid md:grid-cols-3 gap-4 items-end max-w-3xl">
          <TextField label="Password" type="password" autoComplete="current-password" required value={off.password} onChange={(e) => setOff({ ...off, password: e.target.value })} />
          <TextField label="Current code or a recovery code" required value={off.code} onChange={(e) => setOff({ ...off, code: e.target.value })} />
          <button className="btn-secondary" disabled={mfa.busy}>Turn off</button>
        </form>
      </Section>
    </AppShell>
  );
}
