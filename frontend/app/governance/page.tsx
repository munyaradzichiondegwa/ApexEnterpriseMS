"use client";

import { useState } from "react";
import AppShell from "@/components/AppShell";
import { UserSelect } from "@/components/Pickers";
import { Badge, DataTable, Loading, Notice, PageHeader, Panel, Section, Tabs, TextField } from "@/components/ui";
import { api } from "@/lib/api";
import { fmtDate, fmtDateTime, fmtMoney, splitCsv, today } from "@/lib/format";
import { useAction, useLoad } from "@/lib/hooks";

type Row = Record<string, any>;
const TABS = [
  { id: "matrix", label: "Approval limits" }, { id: "delegation", label: "Delegation and emergency access" }, { id: "sod", label: "Segregation of duties" },
  { id: "thresholds", label: "Materiality and policy" }, { id: "rates", label: "Statutory rates" }, { id: "settings", label: "Settings" },
] as const;
type TabId = (typeof TABS)[number]["id"];
const ENTITY_TYPES = ["JournalEntry", "ApPayment", "Accrual", "Prepayment", "RevenueContract", "Impairment", "InventoryWriteOff", "ArCreditNote", "CreditLimitOverride", "EclProvision", "PettyCash", "MatchOverride"];

export default function GovernancePage() {
  const [tab, setTab] = useState<TabId>("matrix");
  return (
    <AppShell>
      <PageHeader title="Governance and approvals" subtitle="Who may approve what, up to what amount; who may stand in for whom; which duties must never sit with one person; and the thresholds and rates the controls rely on. Every change is recorded in the audit trail." />
      <Tabs tabs={TABS} active={tab} onChange={setTab} />
      <Panel id="matrix" active={tab}><Matrix /></Panel>
      <Panel id="delegation" active={tab}><Delegation /></Panel>
      <Panel id="sod" active={tab}><Sod /></Panel>
      <Panel id="thresholds" active={tab}><Thresholds /></Panel>
      <Panel id="rates" active={tab}><Rates /></Panel>
      <Panel id="settings" active={tab}><Settings /></Panel>
    </AppShell>
  );
}

function Matrix() {
  const [type, setType] = useState("JournalEntry");
  const { data, loading, reload } = useLoad<Row[]>(`/controls/approval-matrix/${type}`, []);
  const act = useAction();
  const [text, setText] = useState("1000=Supervisor\n10000=Finance Manager\n1000000000=Finance Director");
  const parse = () => text.split("\n").map((l) => l.trim()).filter(Boolean).map((l) => { const [amt, role] = l.split("="); return { upToAmount: Number(amt), requiredRoleName: (role ?? "").trim() }; });
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Approval limits" description="Each line is 'up to this amount = this role'. A higher role can approve smaller amounts too. End with a very large catch-all line. If no limits are set for a type, only the usual rules apply (the preparer cannot approve their own work).">
        <div className="max-w-sm mb-4"><label className="label" htmlFor="m-type">Applies to</label>
          <select id="m-type" className="input" value={type} onChange={(e) => setType(e.target.value)}>{ENTITY_TYPES.map((t) => <option key={t}>{t}</option>)}</select></div>
        <div className="mb-4">{loading ? <Loading what="limits" /> : <DataTable caption="Current approval limits" rowKey={(r) => String(r.upToAmount)} rows={data} empty="No limits set for this type." columns={[
          { header: "Up to", align: "right", cell: (r) => (r.upToAmount > 1e12 ? "No limit" : fmtMoney(r.upToAmount)), csv: (r) => r.upToAmount }, { header: "Approver role", cell: (r) => r.requiredRoleName }, { header: "SLA (hours)", align: "right", cell: (r) => r.slaHours }]} />}</div>
        <label className="label" htmlFor="m-text">New limits (one per line: amount=Role name)</label>
        <textarea id="m-text" className="input font-mono text-sm" rows={5} value={text} onChange={(e) => setText(e.target.value)} />
        <button className="btn-primary mt-3" disabled={act.busy} onClick={() => act.run(async () => { await api.put(`/controls/approval-matrix/${type}`, { tiers: parse() }); await reload(); }, "Approval limits replaced.")}>Replace limits</button>
      </Section>
    </>
  );
}

function Delegation() {
  const { data, loading, reload } = useLoad<Row[]>("/controls/delegations", []);
  const act = useAction();
  const [f, setF] = useState({ delegate: "", entity: "", max: "", from: today(), to: today(), reason: "", emergency: false });
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Grant a delegation" description="Lets someone approve on your behalf for a limited time. Delegations last at most 90 days; emergency access at most 3 days and must be reviewed afterwards by someone else.">
        <form className="grid md:grid-cols-3 gap-4" onSubmit={(e) => { e.preventDefault(); act.run(async () => { await api.post("/controls/delegations", { delegateUserId: f.delegate, entityType: f.entity || null, maxAmount: f.max ? Number(f.max) : null, validFrom: f.from, validTo: f.to, reason: f.reason, isEmergency: f.emergency }); await reload(); }, "Delegation granted."); }}>
          <UserSelect label="Delegate to" required value={f.delegate} onChange={(v) => setF({ ...f, delegate: v })} />
          <div><label className="label" htmlFor="d-type">Applies to</label><select id="d-type" className="input" value={f.entity} onChange={(e) => setF({ ...f, entity: e.target.value })}><option value="">Everything</option>{ENTITY_TYPES.map((t) => <option key={t}>{t}</option>)}</select></div>
          <TextField label="Amount limit (optional)" type="number" step="0.01" min="0" value={f.max} onChange={(e) => setF({ ...f, max: e.target.value })} />
          <TextField label="From" type="date" required value={f.from} onChange={(e) => setF({ ...f, from: e.target.value })} />
          <TextField label="To" type="date" required value={f.to} onChange={(e) => setF({ ...f, to: e.target.value })} />
          <TextField label="Reason" required value={f.reason} onChange={(e) => setF({ ...f, reason: e.target.value })} />
          <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={f.emergency} onChange={(e) => setF({ ...f, emergency: e.target.checked })} /> Emergency access</label>
          <div className="md:col-span-3"><button className="btn-primary" disabled={act.busy}>Grant</button></div>
        </form>
      </Section>
      <Section title="Delegations">
        {loading ? <Loading what="delegations" /> : <DataTable caption="Delegations" exportName="delegations" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Delegate", cell: (r) => r.delegateUserId }, { header: "Applies to", cell: (r) => r.entityType ?? "Everything" }, { header: "Limit", align: "right", cell: (r) => (r.maxAmount ? fmtMoney(r.maxAmount) : "None"), csv: (r) => r.maxAmount ?? "" },
          { header: "Valid", cell: (r) => `${fmtDate(r.validFrom)} to ${fmtDate(r.validTo)}`, csv: (r) => `${fmtDate(r.validFrom)} to ${fmtDate(r.validTo)}` },
          { header: "Kind", cell: (r) => (r.isEmergency ? <Badge text="High" /> : "Normal"), csv: (r) => (r.isEmergency ? "Emergency" : "Normal") },
          { header: "Status", cell: (r) => (r.revokedAt ? "Revoked" : new Date(r.validTo) < new Date() ? "Expired" : "Active") },
          { header: "Action", cell: (r) => (
            <div className="flex gap-2">
              {!r.revokedAt && <button className="btn-secondary text-xs" onClick={() => act.run(async () => { await api.post(`/controls/delegations/${r.id}/revoke`); await reload(); }, "Revoked.")}>Revoke</button>}
              {r.isEmergency && !r.reviewedAt && <button className="btn-primary text-xs" onClick={() => act.run(async () => { await api.post(`/controls/delegations/${r.id}/review`); await reload(); }, "Reviewed.")}>Review</button>}
            </div>) }]} />}
      </Section>
    </>
  );
}

function Sod() {
  const { data, loading, reload } = useLoad<Row[]>("/controls/sod/report", []);
  const act = useAction();
  const [rule, setRule] = useState({ a: "", b: "", reason: "" });
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Who holds conflicting access" description="Users whose role combines permissions that should sit with different people (for example preparing and approving a payment). Fix the role, or record why the risk is accepted and what compensates for it.">
        {loading ? <Loading what="report" /> : <DataTable caption="Users with conflicting permissions" exportName="sod-conflicts" rowKey={(r) => r.id} rows={data} empty="No user holds conflicting permissions." columns={[
          { header: "User", cell: (r) => r.email }, { header: "Role", cell: (r) => r.role }, { header: "Conflicts", cell: (r) => (r.conflicts ?? []).map((c: Row) => c.reason).join("; "), csv: (r) => (r.conflicts ?? []).map((c: Row) => c.reason).join("; ") }]} />}
      </Section>
      <Section title="Add a company rule" description="Adds to the built-in rules. Use permission keys such as ap.payments.create.">
        <form className="grid md:grid-cols-3 gap-4 items-end" onSubmit={(e) => { e.preventDefault(); act.run(async () => { await api.post("/controls/sod/rules", { permissionA: rule.a, permissionB: rule.b, reason: rule.reason }); await reload(); setRule({ a: "", b: "", reason: "" }); }, "Rule added."); }}>
          <TextField label="First permission" required value={rule.a} onChange={(e) => setRule({ ...rule, a: e.target.value })} />
          <TextField label="Second permission" required value={rule.b} onChange={(e) => setRule({ ...rule, b: e.target.value })} />
          <TextField label="Why they conflict" required value={rule.reason} onChange={(e) => setRule({ ...rule, reason: e.target.value })} />
          <div className="md:col-span-3"><button className="btn-primary" disabled={act.busy}>Add rule</button></div>
        </form>
      </Section>
    </>
  );
}

function Thresholds() {
  const mat = useLoad<Row[]>("/controls/materiality", []);
  const act = useAction();
  const [m, setM] = useState({ medium: "1000", high: "10000", critical: "100000" });
  const [p, setP] = useState({ area: "", description: "", from: today() });
  const [policy, setPolicy] = useState<Row[]>([]);
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Materiality thresholds" description="The amounts that set how seriously a transaction is treated. A change is proposed by one person and approved by another; each approval creates a new version.">
        <form className="grid md:grid-cols-4 gap-4 items-end mb-4" onSubmit={(e) => { e.preventDefault(); act.run(async () => { await api.post("/controls/materiality", { medium: Number(m.medium), high: Number(m.high), critical: Number(m.critical) }); await mat.reload(); }, "Proposed. A second person must approve it."); }}>
          <TextField label="Medium from" type="number" step="0.01" min="0" required value={m.medium} onChange={(e) => setM({ ...m, medium: e.target.value })} />
          <TextField label="High from" type="number" step="0.01" min="0" required value={m.high} onChange={(e) => setM({ ...m, high: e.target.value })} />
          <TextField label="Critical from" type="number" step="0.01" min="0" required value={m.critical} onChange={(e) => setM({ ...m, critical: e.target.value })} />
          <button className="btn-primary" disabled={act.busy}>Propose</button>
        </form>
        <DataTable caption="Materiality versions" rowKey={(r) => r.id} rows={mat.data} columns={[
          { header: "Version", align: "right", cell: (r) => r.version }, { header: "Medium", align: "right", cell: (r) => fmtMoney(r.mediumThreshold) }, { header: "High", align: "right", cell: (r) => fmtMoney(r.highThreshold) }, { header: "Critical", align: "right", cell: (r) => fmtMoney(r.criticalThreshold) },
          { header: "Status", cell: (r) => <Badge text={r.isActive ? "Active" : "Pending"} /> }, { header: "Action", cell: (r) => !r.isActive ? <button className="btn-primary text-xs" onClick={() => act.run(async () => { await api.post(`/controls/materiality/${r.id}/approve`); await mat.reload(); }, "Approved and now in force.")}>Approve</button> : null }]} />
      </Section>
      <Section title="Accounting policy versions" description="Records which policy applied from when (depreciation, revenue recognition, credit-loss rates). Reports can then be reproduced exactly as they were issued.">
        <form className="grid md:grid-cols-4 gap-4 items-end mb-4" onSubmit={(e) => { e.preventDefault(); act.run(async () => { const r = await api.post("/controls/policies", { area: p.area, description: p.description, effectiveFrom: p.from }); setPolicy((x) => [r.data, ...x]); }, "Proposed. A second person must approve it."); }}>
          <TextField label="Area" required placeholder="Depreciation" value={p.area} onChange={(e) => setP({ ...p, area: e.target.value })} />
          <TextField label="What the policy says" required value={p.description} onChange={(e) => setP({ ...p, description: e.target.value })} />
          <TextField label="Effective from" type="date" required value={p.from} onChange={(e) => setP({ ...p, from: e.target.value })} />
          <button className="btn-primary" disabled={act.busy}>Propose</button>
        </form>
        <DataTable caption="Policies proposed in this session" rowKey={(r) => r.id} rows={policy} empty="Proposals made now appear here." columns={[
          { header: "Area", cell: (r) => r.area }, { header: "Version", align: "right", cell: (r) => r.version }, { header: "Effective", cell: (r) => fmtDate(r.effectiveFrom) },
          { header: "Action", cell: (r) => <button className="btn-primary text-xs" onClick={() => act.run(async () => { await api.post(`/controls/policies/${r.id}/approve`); setPolicy((x) => x.filter((y) => y.id !== r.id)); }, "Approved and in force.")}>Approve</button> }]} />
      </Section>
    </>
  );
}

function Rates() {
  const { data, loading, reload } = useLoad<Row[]>("/controls/statutory-rates", []);
  const act = useAction();
  const [f, setF] = useState({ code: "NSSA_EMPLOYEE", rate: "", ceiling: "", from: today(), source: "" });
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Payroll statutory rates" description="NSSA, AIDS levy and similar rates change by notice. Enter each change with its effective date and the notice it comes from; a second person approves it. Payroll uses the rate in force on the pay date. Until a company records a rate, payroll falls back to the built-in default.">
        <form className="grid md:grid-cols-5 gap-4 items-end mb-4" onSubmit={(e) => { e.preventDefault(); act.run(async () => { await api.post("/controls/statutory-rates", { code: f.code, ratePct: Number(f.rate), ceiling: f.ceiling ? Number(f.ceiling) : null, currency: "USD", effectiveFrom: f.from, sourceReference: f.source }); await reload(); }, "Proposed. A second person must approve it."); }}>
          <div><label className="label" htmlFor="r-code">Rate</label><select id="r-code" className="input" value={f.code} onChange={(e) => setF({ ...f, code: e.target.value })}>{["NSSA_EMPLOYEE", "NSSA_EMPLOYER", "AIDS_LEVY", "ZIMDEF", "NEC_LEVY"].map((c) => <option key={c}>{c}</option>)}</select></div>
          <TextField label="Rate %" type="number" step="0.0001" min="0" max="100" required value={f.rate} onChange={(e) => setF({ ...f, rate: e.target.value })} />
          <TextField label="Monthly ceiling (optional)" type="number" step="0.01" min="0" value={f.ceiling} onChange={(e) => setF({ ...f, ceiling: e.target.value })} />
          <TextField label="Effective from" type="date" required value={f.from} onChange={(e) => setF({ ...f, from: e.target.value })} />
          <TextField label="Source notice" required value={f.source} onChange={(e) => setF({ ...f, source: e.target.value })} />
          <div className="md:col-span-5"><button className="btn-primary" disabled={act.busy}>Propose</button></div>
        </form>
        {loading ? <Loading what="rates" /> : <DataTable caption="Statutory rates" exportName="statutory-rates" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Rate", cell: (r) => r.code }, { header: "%", align: "right", cell: (r) => r.ratePct }, { header: "Ceiling", align: "right", cell: (r) => (r.ceiling ? fmtMoney(r.ceiling) : "None"), csv: (r) => r.ceiling ?? "" },
          { header: "From", cell: (r) => fmtDate(r.effectiveFrom) }, { header: "To", cell: (r) => (r.effectiveTo ? fmtDate(r.effectiveTo) : "Open") }, { header: "Source", cell: (r) => r.sourceReference },
          { header: "Status", cell: (r) => <Badge text={r.approvedBy ? "Approved" : "Pending"} /> }, { header: "Action", cell: (r) => !r.approvedBy ? <button className="btn-primary text-xs" onClick={() => act.run(async () => { await api.post(`/controls/statutory-rates/${r.id}/approve`); await reload(); }, "Approved.")}>Approve</button> : null }]} />}
      </Section>
    </>
  );
}

const KNOWN_SETTINGS = [
  ["controls.allowSelfApproval", "true/false. Allow someone to approve their own work. Leave false."],
  ["ap.match.quantityTolerancePct", "Percent. Quantity difference tolerated in a three-way match."],
  ["ap.match.priceTolerancePct", "Percent. Price difference tolerated in a three-way match."],
  ["ap.directPayment.maxAmount", "Amount. Payments above this must go through an authorised payment run."],
  ["period.close.requireChecklist", "true/false. Require the close checklist before any period can be closed."],
  ["gl.fx.rateTolerancePct", "Percent. Foreign-currency rates must be within this of the rate on file."],
  ["gl.fx.roundingAccountId", "Account id that receives tiny foreign-currency rounding differences."],
  ["integrations.arControlAccountId", "Trade debtors account id used when posting provider payments."],
  ["recon.ap.accounts", "Comma-separated account ids that make up the AP control accounts."],
  ["recon.ar.accounts", "Comma-separated account ids that make up the AR control accounts."],
] as const;

function Settings() {
  const { data, loading, reload } = useLoad<Row[]>("/controls/settings", []);
  const act = useAction();
  const [f, setF] = useState({ key: "", value: "" });
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Control settings" description="Company-wide switches and tolerances. Changes take effect immediately and are audited.">
        <form className="grid md:grid-cols-3 gap-4 items-end mb-4" onSubmit={(e) => { e.preventDefault(); act.run(async () => { await api.put(`/controls/settings/${encodeURIComponent(f.key)}`, { value: f.value }); await reload(); }, "Saved."); }}>
          <div><label className="label" htmlFor="s-key">Setting</label><input id="s-key" list="known-settings" className="input" required value={f.key} onChange={(e) => setF({ ...f, key: e.target.value })} />
            <datalist id="known-settings">{KNOWN_SETTINGS.map(([k]) => <option key={k} value={k} />)}</datalist></div>
          <TextField label="Value" required value={f.value} onChange={(e) => setF({ ...f, value: e.target.value })} />
          <button className="btn-primary" disabled={act.busy}>Save</button>
        </form>
        <p className="text-sm text-slate-500 mb-3">{KNOWN_SETTINGS.find(([k]) => k === f.key)?.[1] ?? "Type or pick a setting name to see what it does."}</p>
        {loading ? <Loading what="settings" /> : <DataTable caption="Current settings" exportName="settings" rowKey={(r) => r.id} rows={data} columns={[{ header: "Setting", cell: (r) => r.key }, { header: "Value", cell: (r) => r.value }, { header: "Changed", cell: (r) => fmtDateTime(r.updatedAt ?? r.createdAt) }]} />}
      </Section>
    </>
  );
}
