"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function GroupPage() {
  const [tab, setTab] = useState<"structure" | "intercompany" | "consolidated">("structure");
  const [companies, setCompanies] = useState<any[]>([]);
  const [reconciliation, setReconciliation] = useState<any[]>([]);
  const [consolidated, setConsolidated] = useState<any>(null);
  const [error, setError] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [structureForm, setStructureForm] = useState({ companyId: "", parentCompanyId: "", ownershipPercentage: 100 });
  const [ruleForm, setRuleForm] = useState({ toCompanyId: "", autoMirror: true, defaultSenderArAccountId: "", defaultReceiverApAccountId: "" });

  function load() {
    api.get("/group/companies").then((r) => setCompanies(r.data)).catch((e) => setError(extractErrorMessage(e)));
  }
  useEffect(load, []);

  async function updateStructure(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.put(`/group/companies/${structureForm.companyId}/structure`, {
        parentCompanyId: structureForm.parentCompanyId || null,
        ownershipPercentage: structureForm.ownershipPercentage,
      });
      setMsg("Group structure updated.");
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function createRule(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/group/intercompany-rules", ruleForm);
      setMsg("Inter-company rule created.");
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function loadReconciliation() {
    try { setReconciliation((await api.get("/group/intercompany/reconciliation")).data); } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function loadConsolidated(companyId: string) {
    try { setConsolidated((await api.get(`/group/consolidated-statements?asOf=${new Date().toISOString()}`)).data); }
    catch (err) { setError(extractErrorMessage(err)); }
  }

  const fmt = (n: number) => (n ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Group, Inter-Company & Consolidation</h1>
      <p className="text-slate-500 mb-6">Parent/subsidiary structure, mirror posting, netting, and consolidated statements with non-controlling interest.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}
      {msg && <div className="text-sm text-brand-700 bg-brand-50 border border-brand-200 rounded-lg px-3 py-2 mb-4">{msg}</div>}

      <div className="flex gap-2 mb-6">
        <button className={tab === "structure" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("structure")}>Group Structure</button>
        <button className={tab === "intercompany" ? "btn-primary" : "btn-secondary"} onClick={() => { setTab("intercompany"); loadReconciliation(); }}>Inter-Company</button>
        <button className={tab === "consolidated" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("consolidated")}>Consolidated Statements</button>
      </div>

      {tab === "structure" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <table className="table-base">
              <thead><tr><th>Company</th><th>Parent</th><th>Ownership %</th></tr></thead>
              <tbody>
                {companies.map((c) => (
                  <tr key={c.id}>
                    <td>{c.name}</td>
                    <td>{companies.find((p) => p.id === c.parentCompanyId)?.name ?? "—"}</td>
                    <td className="text-right">{c.ownershipPercentage ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <form onSubmit={updateStructure} className="card space-y-3 h-fit">
            <h2 className="font-semibold">Set Parent / Ownership</h2>
            <div>
              <label className="label">Company</label>
              <select className="input" required value={structureForm.companyId} onChange={(e) => setStructureForm({ ...structureForm, companyId: e.target.value })}>
                <option value="">Select…</option>
                {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
              </select>
            </div>
            <div>
              <label className="label">Parent Company</label>
              <select className="input" value={structureForm.parentCompanyId} onChange={(e) => setStructureForm({ ...structureForm, parentCompanyId: e.target.value })}>
                <option value="">None (top of group)</option>
                {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
              </select>
            </div>
            <div><label className="label">Ownership %</label><input className="input" type="number" min={0} max={100} value={structureForm.ownershipPercentage} onChange={(e) => setStructureForm({ ...structureForm, ownershipPercentage: Number(e.target.value) })} /></div>
            <button className="btn-primary w-full">Save</button>
          </form>
        </div>
      )}

      {tab === "intercompany" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <h2 className="font-semibold mb-3">Reconciliation</h2>
            <table className="table-base">
              <thead><tr><th>Date</th><th>Description</th><th>Amount</th><th>Status</th></tr></thead>
              <tbody>{reconciliation.map((t: any) => (<tr key={t.id}><td>{new Date(t.transactionDate).toLocaleDateString()}</td><td>{t.description}</td><td className="text-right">{fmt(t.amount)}</td><td>{t.status}</td></tr>))}</tbody>
            </table>
          </div>
          <form onSubmit={createRule} className="card space-y-3 h-fit">
            <h2 className="font-semibold">New Inter-Company Rule</h2>
            <div>
              <label className="label">To Company</label>
              <select className="input" required value={ruleForm.toCompanyId} onChange={(e) => setRuleForm({ ...ruleForm, toCompanyId: e.target.value })}>
                <option value="">Select…</option>
                {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
              </select>
            </div>
            <div><label className="label">Default Sender AR Account ID</label><input className="input" required value={ruleForm.defaultSenderArAccountId} onChange={(e) => setRuleForm({ ...ruleForm, defaultSenderArAccountId: e.target.value })} /></div>
            <div><label className="label">Default Receiver AP Account ID</label><input className="input" required value={ruleForm.defaultReceiverApAccountId} onChange={(e) => setRuleForm({ ...ruleForm, defaultReceiverApAccountId: e.target.value })} /></div>
            <button className="btn-primary w-full">Create Rule</button>
          </form>
        </div>
      )}

      {tab === "consolidated" && (
        <div>
          <button className="btn-primary mb-4" onClick={() => loadConsolidated("")}>Run Consolidation (this company as parent)</button>
          {consolidated && (
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <div className="card">
                <div className="flex justify-between text-sm py-1"><span className="text-slate-500">Total Assets</span><span className="font-medium">{fmt(consolidated.totalAssets)}</span></div>
                <div className="flex justify-between text-sm py-1"><span className="text-slate-500">Total Liabilities</span><span className="font-medium">{fmt(consolidated.totalLiabilities)}</span></div>
                <div className="flex justify-between text-sm py-1"><span className="text-slate-500">Group Equity</span><span className="font-medium">{fmt(consolidated.totalGroupEquity)}</span></div>
                <div className="flex justify-between text-sm py-1"><span className="text-slate-500">Non-Controlling Interest</span><span className="font-medium">{fmt(consolidated.totalNonControllingInterest)}</span></div>
                <div className="flex justify-between text-sm py-1 border-t mt-1 pt-2"><span className="text-slate-500">Total Eliminations</span><span className="font-medium">{fmt(consolidated.totalEliminations)}</span></div>
              </div>
              <div className="card overflow-x-auto max-h-96 overflow-y-auto">
                <table className="table-base">
                  <thead><tr><th>Code</th><th>Account</th><th>Group</th><th>Eliminated</th><th>Consolidated</th></tr></thead>
                  <tbody>{consolidated.lines.map((l: any, i: number) => (<tr key={i}><td>{l.code}</td><td>{l.name}</td><td className="text-right">{fmt(l.groupAmount)}</td><td className="text-right">{fmt(l.eliminatedAmount)}</td><td className="text-right font-medium">{fmt(l.consolidatedAmount)}</td></tr>))}</tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      )}
    </AppShell>
  );
}
