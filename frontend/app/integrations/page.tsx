"use client";

import { useState } from "react";
import AppShell from "@/components/AppShell";
import { AccountSelect } from "@/components/Pickers";
import { Badge, DataTable, Loading, Notice, PageHeader, Panel, Section, SelectField, Stat, Tabs, TextField } from "@/components/ui";
import { api } from "@/lib/api";
import { fmtDateTime, fmtMoney, today } from "@/lib/format";
import { useAction, useLoad } from "@/lib/hooks";
import { useAuth } from "@/lib/auth";

type Row = Record<string, any>;
const TABS = [{ id: "providers", label: "Providers" }, { id: "messages", label: "Messages and dead letters" }, { id: "recon", label: "Reconciliation" }, { id: "jobs", label: "Background jobs" }] as const;
type TabId = (typeof TABS)[number]["id"];
const PROVIDERS = ["EcoCash", "OneMoney", "PayNow", "ZimSwitch", "BankApi"];

export default function IntegrationsPage() {
  const [tab, setTab] = useState<TabId>("providers");
  return (
    <AppShell>
      <PageHeader title="Integrations and jobs" subtitle="Payment notifications from mobile-money providers, switches and banks arrive here as signed messages. A genuine message is posted once as a customer receipt; anything that fails is retried, then held in a dead-letter queue for a person to resolve." />
      <Tabs tabs={TABS} active={tab} onChange={setTab} />
      <Panel id="providers" active={tab}><Providers /></Panel>
      <Panel id="messages" active={tab}><Messages /></Panel>
      <Panel id="recon" active={tab}><Recon /></Panel>
      <Panel id="jobs" active={tab}><Jobs /></Panel>
    </AppShell>
  );
}

function Providers() {
  const { user } = useAuth();
  const { data, loading, reload } = useLoad<Row[]>("/integrations/endpoints", []);
  const act = useAction();
  const [f, setF] = useState({ provider: "EcoCash", name: "", secret: "", bank: "", url: "" });
  const hook = (p: string) => `${typeof window !== "undefined" ? window.location.origin : ""}/backend/api/v1/integrations/webhooks/${user?.companyId ?? "<company id>"}/${p}`;
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Connect a provider" description="Give the provider the callback address below and agree a shared secret with them. The secret itself is never entered here: store it as an environment variable on the server and enter only that variable's name.">
        <form className="grid md:grid-cols-3 gap-4" onSubmit={(e) => { e.preventDefault(); act.run(async () => { await api.put("/integrations/endpoints", { provider: f.provider, name: f.name || f.provider, baseUrl: f.url || null, secretName: f.secret, settlementBankGlAccountId: f.bank || null, isActive: true }); await reload(); }, "Saved."); }}>
          <SelectField label="Provider" value={f.provider} onChange={(e) => setF({ ...f, provider: e.target.value })} options={PROVIDERS.map((p) => ({ value: p, label: p }))} placeholder="Choose" />
          <TextField label="Display name" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} />
          <TextField label="Secret's environment variable name" required placeholder="ECOCASH_WEBHOOK_SECRET" pattern="[A-Za-z0-9_]+" value={f.secret} onChange={(e) => setF({ ...f, secret: e.target.value })} />
          <AccountSelect label="Settlement bank account (GL)" required value={f.bank} onChange={(v) => setF({ ...f, bank: v })} hint="Where money from this provider lands." />
          <TextField label="Provider base URL (optional)" type="url" value={f.url} onChange={(e) => setF({ ...f, url: e.target.value })} />
          <div className="md:col-span-3"><p className="text-sm text-slate-600 mb-2">Callback address for {f.provider}: <code className="break-all">{hook(f.provider)}</code></p>
            <p className="text-xs text-slate-500 mb-3">Messages must carry <code>X-Apex-Timestamp</code> (unix seconds) and <code>X-Apex-Signature</code> = HMAC-SHA256 (hex) of <code>timestamp.body</code> with the shared secret. A provider whose own format differs needs a small adapter once its specification and credentials are available.</p>
            <button className="btn-primary" disabled={act.busy}>Save</button></div>
        </form>
      </Section>
      <Section title="Configured providers">
        {loading ? <Loading what="providers" /> : <DataTable caption="Integration endpoints" rowKey={(r) => r.id} rows={data} columns={[{ header: "Provider", cell: (r) => r.provider }, { header: "Name", cell: (r) => r.name }, { header: "Secret variable", cell: (r) => r.secretName }, { header: "Active", cell: (r) => (r.isActive ? "Yes" : "No") }]} />}
      </Section>
    </>
  );
}

function Messages() {
  const [status, setStatus] = useState("");
  const { data, loading, reload } = useLoad<Row[]>(`/integrations/messages${status ? `?status=${status}` : ""}`, []);
  const act = useAction();
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Messages">
        <div className="max-w-xs mb-4"><label className="label" htmlFor="msg-status">Show</label>
          <select id="msg-status" className="input" value={status} onChange={(e) => setStatus(e.target.value)}><option value="">Everything</option>{["Processed", "Pending", "Failed", "DeadLetter"].map((s) => <option key={s}>{s}</option>)}</select></div>
        {loading ? <Loading what="messages" /> : <DataTable caption="Integration messages" exportName="integration-messages" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Received", cell: (r) => fmtDateTime(r.createdAt), csv: (r) => fmtDateTime(r.createdAt) }, { header: "Provider", cell: (r) => r.provider }, { header: "Reference", cell: (r) => r.externalRef },
          { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status }, { header: "Attempts", align: "right", cell: (r) => r.attempts }, { header: "Problem", cell: (r) => r.lastError ?? "" },
          { header: "Action", cell: (r) => (r.status === "DeadLetter" || r.status === "Failed") ? <button className="btn-secondary text-xs" onClick={() => act.run(async () => { await api.post(`/integrations/messages/${r.id}/requeue`); await reload(); }, "Queued for another attempt.")}>Try again</button> : null }]} />}
      </Section>
    </>
  );
}

function Recon() {
  const [from, setFrom] = useState(`${new Date().getUTCFullYear()}-${String(new Date().getUTCMonth() + 1).padStart(2, "0")}-01`); const [to, setTo] = useState(today());
  const { data, loading, error } = useLoad<Row | null>(`/integrations/reconciliation?from=${from}&to=${to}T23:59:59Z`, null);
  return (
    <Section title="Provider messages against the ledger" description="Every message in the period, how many became receipts, and anything that still needs a person.">
      <div className="grid sm:grid-cols-2 gap-4 max-w-lg mb-4"><TextField label="From" type="date" value={from} onChange={(e) => setFrom(e.target.value)} /><TextField label="To" type="date" value={to} onChange={(e) => setTo(e.target.value)} /></div>
      <Notice error={error} />
      {loading || !data ? <Loading what="reconciliation" /> : (
        <>
          <div className="grid sm:grid-cols-2 lg:grid-cols-4 gap-4 mb-4">
            <Stat label="Messages" value={data.messages} /><Stat label="Posted as receipts" value={data.processed} tone="good" hint={fmtMoney(data.postedTotal)} />
            <Stat label="Failed" value={data.failed} tone={data.failed ? "warn" : "good"} /><Stat label="Dead letters" value={data.deadLetter} tone={data.deadLetter ? "bad" : "good"} />
          </div>
          <DataTable caption="Needs attention" rowKey={(r) => r.id} rows={(data.needsAttention ?? []) as Row[]} empty="Nothing needs attention." columns={[{ header: "Provider", cell: (r) => r.provider }, { header: "Reference", cell: (r) => r.externalRef }, { header: "Status", cell: (r) => <Badge text={r.status} /> }, { header: "Problem", cell: (r) => r.lastError }]} />
        </>)}
    </Section>
  );
}

function Jobs() {
  const { data, loading, reload, error } = useLoad<Row | null>("/admin/jobs", null);
  const act = useAction();
  return (
    <>
      <Notice error={act.error ?? error} notice={act.notice} />
      <Section title="Scheduled jobs" description="These run automatically; each period or day is applied once even if several servers are running. A failed job is retried up to three times.">
        <button className="btn-primary mb-4" disabled={act.busy} onClick={() => act.run(async () => { const r = await api.post("/admin/jobs/run-due"); await reload(); return r.data; }, (r: any) => `${r.executed} job(s) ran.`)}>Run due jobs now</button>
        {loading || !data ? <Loading what="jobs" /> : (
          <>
            <DataTable caption="Job definitions" rowKey={(r) => r.code} rows={(data.definitions ?? []) as Row[]} columns={[{ header: "Job", cell: (r) => r.code }, { header: "What it does", cell: (r) => r.title }]} />
            <h3 className="font-semibold mt-6 mb-2">Recent runs</h3>
            <DataTable caption="Recent job runs" exportName="job-runs" rowKey={(r) => r.id} rows={(data.recent ?? []) as Row[]} columns={[
              { header: "Started", cell: (r) => fmtDateTime(r.startedAt), csv: (r) => fmtDateTime(r.startedAt) }, { header: "Job", cell: (r) => r.jobCode }, { header: "Run key", cell: (r) => r.runKey },
              { header: "Status", cell: (r) => <Badge text={r.status === "Succeeded" ? "Done" : r.status === "Failed" ? "Blocked" : "Open"} />, csv: (r) => r.status }, { header: "Attempt", align: "right", cell: (r) => r.attempt }, { header: "Result", cell: (r) => r.message ?? "" }]} />
          </>)}
      </Section>
    </>
  );
}
