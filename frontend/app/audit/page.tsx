"use client";

import { useState } from "react";
import AppShell from "@/components/AppShell";
import { AccountSelect } from "@/components/Pickers";
import { Badge, DataTable, Loading, Notice, PageHeader, Panel, Section, Stat, Tabs, TextField } from "@/components/ui";
import { api } from "@/lib/api";
import { fmtDate, fmtDateTime, fmtMoney, today } from "@/lib/format";
import { useAction, useLoad } from "@/lib/hooks";

type Row = Record<string, any>;
const TABS = [{ id: "exceptions", label: "Exceptions" }, { id: "tests", label: "Control self-tests" }, { id: "trace", label: "Trace and evidence" }] as const;
type TabId = (typeof TABS)[number]["id"];

export default function AuditPage() {
  const [tab, setTab] = useState<TabId>("exceptions");
  return (
    <AppShell>
      <PageHeader title="Audit workspace" subtitle="Control exceptions and their life-cycle, automated tests of the ledger's integrity, and the evidence trail behind every figure." />
      <Tabs tabs={TABS} active={tab} onChange={setTab} />
      <Panel id="exceptions" active={tab}><Exceptions /></Panel>
      <Panel id="tests" active={tab}><SelfTests /></Panel>
      <Panel id="trace" active={tab}><Trace /></Panel>
    </AppShell>
  );
}

function Exceptions() {
  const [status, setStatus] = useState("");
  const { data, loading, reload } = useLoad<Row[]>(`/controls/exceptions${status ? `?status=${encodeURIComponent(status)}` : ""}`, []);
  const act = useAction();
  const users = useLoad<Row[]>("/admin/users", []);
  const call = (id: string, verb: string, body: object | undefined, msg: string) => act.run(async () => { await api.post(`/controls/exceptions/${id}/${verb}`, body); await reload(); }, msg);
  const ask = (q: string) => window.prompt(q) || null;
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Run detectors" description="Looks for risky manual journals, duplicate invoices and payments, payments right after a bank-detail change, stale bank items, overdue approvals, credit-limit breaches, negative stock and unexplained reconciliation differences. A finding is a prompt to investigate, never a conclusion of wrongdoing.">
        <button className="btn-primary" disabled={act.busy} onClick={() => act.run(async () => { const r = await api.post("/controls/monitoring/run"); await reload(); return r.data; }, (r: any) => `${r.raised} new exception(s) raised, ${r.alreadyOpen} already tracked.`)}>Run detectors now</button>
      </Section>
      <Section title="Exceptions">
        <div className="max-w-xs mb-4">
          <label className="label" htmlFor="exc-status">Show</label>
          <select id="exc-status" className="input" value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All open and closed</option>
            {["CLASSIFIED", "ASSIGNED", "INVESTIGATED", "RESOLVED", "INDEPENDENTLY_REVIEWED", "CLOSED"].map((s) => <option key={s} value={s}>{s.replace(/_/g, " ")}</option>)}
          </select>
        </div>
        {loading ? <Loading what="exceptions" /> : <DataTable caption="Control exceptions" exportName="exceptions" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Severity", cell: (r) => <Badge text={r.severity} />, csv: (r) => r.severity }, { header: "Score", align: "right", cell: (r) => r.riskScore },
          { header: "What was found", cell: (r) => r.description }, { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status },
          { header: "Owner", cell: (r) => users.data.find((u) => u.id === r.ownerUserId)?.email ?? (r.ownerUserId ? "Assigned" : "Unassigned") }, { header: "Due", cell: (r) => fmtDate(r.dueDate), csv: (r) => fmtDate(r.dueDate) },
          { header: "Next step", cell: (r) => (
            <div className="flex gap-2 flex-wrap">
              {(r.status === "CLASSIFIED" || r.status === "DETECTED") && <button className="btn-secondary text-xs" onClick={() => { const id = window.prompt("Owner user id (see Administration > Users):"); if (id) call(r.id, "assign", { ownerUserId: id }, "Assigned."); }}>Assign</button>}
              {(r.status === "ASSIGNED") && <button className="btn-secondary text-xs" onClick={() => { const n = ask("Investigation notes:"); if (n) call(r.id, "investigate", { note: n }, "Investigation recorded."); }}>Investigate</button>}
              {(r.status === "INVESTIGATED") && <button className="btn-secondary text-xs" onClick={() => { const n = ask("How was it resolved?"); if (n) call(r.id, "resolve", { note: n }, "Resolved."); }}>Resolve</button>}
              {(r.status === "RESOLVED") && <button className="btn-primary text-xs" onClick={() => call(r.id, "review", undefined, "Independently reviewed.")}>Review</button>}
              {(r.status === "INDEPENDENTLY_REVIEWED") && <button className="btn-primary text-xs" onClick={() => call(r.id, "close", undefined, "Closed.")}>Close</button>}
            </div>) }]} />}
      </Section>
    </>
  );
}

function SelfTests() {
  const act = useAction();
  const [results, setResults] = useState<Row[] | null>(null);
  const failing = results?.filter((r) => !r.passed).length ?? 0;
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Ledger integrity tests" description="Eight read-only tests over live data: journals balance, no duplicate postings, nothing posted after close, tenant isolation, no self-approval, no journal without lines, reversals recorded once, and the trial balance balances.">
        <button className="btn-primary" disabled={act.busy} onClick={() => act.run(async () => { const r = await api.post("/controls/self-tests/run"); setResults(r.data); return r.data; }, (r: any) => `${r.filter((x: Row) => x.passed).length} of ${r.length} passed.`)}>Run tests</button>
      </Section>
      {results && (
        <>
          <div className="grid sm:grid-cols-3 gap-4 mb-4"><Stat label="Passed" value={results.length - failing} tone="good" /><Stat label="Failed" value={failing} tone={failing ? "bad" : "good"} /></div>
          <DataTable caption="Self-test results" exportName="self-tests" rowKey={(r) => r.code} rows={results} columns={[
            { header: "Test", cell: (r) => r.code }, { header: "What it checks", cell: (r) => r.title }, { header: "Result", cell: (r) => <Badge text={r.passed ? "Done" : "Blocked"} />, csv: (r) => (r.passed ? "Passed" : "Failed") }, { header: "Detail", cell: (r) => r.details }]} />
        </>)}
    </>
  );
}

function Trace() {
  const [account, setAccount] = useState("");
  const [from, setFrom] = useState(`${new Date().getUTCFullYear()}-01-01`);
  const [to, setTo] = useState(today());
  const [page, setPage] = useState(1);
  const act = useAction();
  const [activity, setActivity] = useState<Row | null>(null);
  const [journal, setJournal] = useState<Row | null>(null);
  const [ev, setEv] = useState({ entityType: "JournalEntry", entityId: "", description: "" });
  const [file, setFile] = useState<File | null>(null);
  const [evidence, setEvidence] = useState<Row[]>([]);

  const loadActivity = (p: number) => act.run(async () => { const r = await api.get(`/controls/ops/trace/account/${account}`, { params: { from, to, page: p, pageSize: 50 } }); setActivity(r.data); setPage(p); });
  const openJournal = (id: string) => act.run(async () => { const r = await api.get(`/controls/ops/trace/journal/${id}`); setJournal(r.data); setEv((e) => ({ ...e, entityId: id })); const l = await api.get(`/controls/ops/evidence/JournalEntry/${id}`); setEvidence(l.data); });
  const upload = (e: React.FormEvent) => { e.preventDefault(); if (!file) return; act.run(async () => {
    const fd = new FormData(); fd.append("file", file); if (ev.description) fd.append("description", ev.description);
    await api.post(`/controls/ops/evidence/${ev.entityType}/${ev.entityId}`, fd, { headers: { "Content-Type": "multipart/form-data" } });
    const l = await api.get(`/controls/ops/evidence/${ev.entityType}/${ev.entityId}`); setEvidence(l.data); setFile(null);
  }, "Evidence attached and fingerprinted."); };
  const download = (id: string, name: string) => act.run(async () => {
    const r = await api.get(`/controls/ops/evidence/file/${id}`, { responseType: "blob" });
    const verified = r.headers["x-content-integrity"] === "verified";
    const url = URL.createObjectURL(r.data); const a = document.createElement("a"); a.href = url; a.download = name; a.click(); URL.revokeObjectURL(url);
    if (!verified) throw new Error("WARNING: this file no longer matches the fingerprint recorded when it was uploaded. Do not rely on it.");
  }, "Downloaded; integrity verified.");

  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Drill down from an account" description="List the postings behind an account's balance, then open any journal to see its source document, audit trail and attached evidence.">
        <form className="grid md:grid-cols-4 gap-4 items-end" onSubmit={(e) => { e.preventDefault(); loadActivity(1); }}>
          <AccountSelect label="Account" required value={account} onChange={setAccount} />
          <TextField label="From" type="date" required value={from} onChange={(e) => setFrom(e.target.value)} />
          <TextField label="To" type="date" required value={to} onChange={(e) => setTo(e.target.value)} />
          <button className="btn-primary" disabled={act.busy}>Show postings</button>
        </form>
        {activity && (
          <div className="mt-4">
            <p className="text-sm text-slate-600 mb-2">{activity.account?.code} - {activity.account?.name}. Opening balance {fmtMoney(activity.openingBalance)}. {activity.total} posting(s).</p>
            <DataTable caption="Account postings" exportName="account-postings" rowKey={(r) => r.id + r.entryNo + r.baseDebit + r.baseCredit} rows={(activity.items ?? []) as Row[]} columns={[
              { header: "Date", cell: (r) => fmtDate(r.entryDate), csv: (r) => fmtDate(r.entryDate) }, { header: "Entry", cell: (r) => <button className="text-brand-700 underline" onClick={() => openJournal(r.id)}>{r.entryNo}</button>, csv: (r) => r.entryNo },
              { header: "Source", cell: (r) => r.source }, { header: "Description", cell: (r) => r.description }, { header: "Debit", align: "right", cell: (r) => fmtMoney(r.baseDebit), csv: (r) => r.baseDebit }, { header: "Credit", align: "right", cell: (r) => fmtMoney(r.baseCredit), csv: (r) => r.baseCredit }]} />
            <div className="flex gap-2 mt-3">
              <button className="btn-secondary text-sm" disabled={page <= 1 || act.busy} onClick={() => loadActivity(page - 1)}>Previous</button>
              <button className="btn-secondary text-sm" disabled={page * 50 >= activity.total || act.busy} onClick={() => loadActivity(page + 1)}>Next</button>
            </div>
          </div>)}
      </Section>
      {journal && (
        <Section title={`Journal ${journal.entryNo}`} description={`${journal.description ?? ""} Posted ${fmtDate(journal.entryDate)} from ${journal.source}. Status: ${journal.status}.`}>
          <DataTable caption="Journal lines" rowKey={(r) => r.glAccountId + r.baseDebit + r.baseCredit} rows={(journal.lines ?? []) as Row[]} columns={[{ header: "Account", cell: (r) => r.account }, { header: "Debit", align: "right", cell: (r) => fmtMoney(r.baseDebit) }, { header: "Credit", align: "right", cell: (r) => fmtMoney(r.baseCredit) }]} />
          {journal.sourceDocument && <p className="text-sm mt-3">Source document: <strong>{journal.sourceDocument.type}</strong> {journal.sourceDocument.invoiceNo ?? journal.sourceDocument.receiptNo}</p>}
          <h3 className="font-semibold mt-5 mb-2">Audit trail</h3>
          <DataTable caption="Audit trail" rowKey={(r) => r.timestamp + r.action} rows={(journal.auditTrail ?? []) as Row[]} columns={[{ header: "When", cell: (r) => fmtDateTime(r.timestamp) }, { header: "Action", cell: (r) => r.action }, { header: "User", cell: (r) => r.userId ?? "system" }, { header: "From IP", cell: (r) => r.ipAddress ?? "" }]} />
        </Section>)}
      <Section title="Evidence" description="Attach supporting documents (PDF, image, CSV, Excel, Word; up to 5 MB). Each file is fingerprinted on upload; if it is ever altered, the download warns you.">
        <form className="grid md:grid-cols-4 gap-4 items-end" onSubmit={upload}>
          <div><label className="label" htmlFor="ev-type">Record type</label>
            <select id="ev-type" className="input" value={ev.entityType} onChange={(e) => setEv({ ...ev, entityType: e.target.value })}>
              {["JournalEntry", "ApInvoice", "ArInvoice", "ApPayment", "ControlException", "ReconciliationRun", "BankReconciliation", "ImpairmentAssessment", "ArCreditNote", "SupplierBankChange"].map((t) => <option key={t}>{t}</option>)}</select></div>
          <TextField label="Record id" required value={ev.entityId} onChange={(e) => setEv({ ...ev, entityId: e.target.value })} />
          <TextField label="Description" value={ev.description} onChange={(e) => setEv({ ...ev, description: e.target.value })} />
          <div><label className="label" htmlFor="ev-file">File</label><input id="ev-file" className="input" type="file" required onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></div>
          <div className="md:col-span-4 flex gap-2"><button className="btn-primary" disabled={act.busy || !file}>Attach</button>
            <button type="button" className="btn-secondary" disabled={!ev.entityId} onClick={() => act.run(async () => { const l = await api.get(`/controls/ops/evidence/${ev.entityType}/${ev.entityId}`); setEvidence(l.data); })}>List attachments</button></div>
        </form>
        {evidence.length > 0 && <div className="mt-4"><DataTable caption="Attachments" rowKey={(r) => r.id} rows={evidence} columns={[
          { header: "File", cell: (r) => r.fileName }, { header: "Size", align: "right", cell: (r) => `${Math.ceil(r.sizeBytes / 1024)} KB` }, { header: "Fingerprint (SHA-256)", cell: (r) => <code className="text-xs">{String(r.sha256).slice(0, 16)}…</code> },
          { header: "Uploaded", cell: (r) => fmtDateTime(r.createdAt) }, { header: "Action", cell: (r) => <button className="btn-secondary text-xs" onClick={() => download(r.id, r.fileName)}>Download</button> }]} /></div>}
      </Section>
    </>
  );
}
