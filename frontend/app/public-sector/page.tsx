"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function PublicSectorPage() {
  const [tab, setTab] = useState<"funds" | "tenders" | "assets" | "ipsas24">("funds");
  const [fundCentres, setFundCentres] = useState<any[]>([]);\n  const [budgetAccounts, setBudgetAccounts] = useState<any[]>([]);
  const [utilization, setUtilization] = useState<any[]>([]);
  const [tenders, setTenders] = useState<any[]>([]);
  const [batches, setBatches] = useState<any[]>([]);
  const [budgetComparison, setBudgetComparison] = useState<any | null>(null);
  const [budgetComparisonLoading, setBudgetComparisonLoading] = useState(false);\n  const [budgetDraftId, setBudgetDraftId] = useState<string | null>(null);\n  const [budgetDraft, setBudgetDraft] = useState({ kind: "Original", currency: "USD", accountingBasis: "Accrual", classificationBasis: "", entityScope: "", periodStart: `${year}-01-01T00:00:00.000Z`, periodEnd: `${year + 1}-01-01T00:00:00.000Z`, authorityReference: "", amendmentReason: "" });\n  const [budgetLines, setBudgetLines] = useState([{ fundCentreId: "", glAccountId: "", costCentreId: null as string | null, description: "", classificationCode: "", amount: 0 }]);
  const [error, setError] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [year] = useState(new Date().getFullYear());
  const [newFund, setNewFund] = useState({ code: "", name: "", level: "Vote", financialYear: year, appropriationAmount: 0, carriedForward: 0 });
  const [newTender, setNewTender] = useState({ title: "", specifications: "", submissionDeadline: "" });

  function load() {
    api.get(`/public-sector/fund-centres?year=${year}`).then((r) => setFundCentres(r.data)).catch((e) => setError(extractErrorMessage(e)));\n    api.get("/gl/accounts").then((r) => setBudgetAccounts(r.data.filter((a: any) => a.isActive && (a.type === "Income" || a.type === "Expense")))).catch(() => {});
    api.get(`/public-sector/fund-centres/utilization?year=${year}`).then((r) => setUtilization(r.data)).catch(() => {});
    api.get("/public-sector/tenders").then((r) => setTenders(r.data)).catch(() => {});
    api.get("/public-sector/asset-verification/batches").then((r) => setBatches(r.data)).catch(() => {});
  }
  useEffect(load, []);

  async function createFund(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/public-sector/fund-centres", newFund);
      setMsg("Fund centre created.");
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function createBudgetVersion(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const response = await api.post("/public-sector/budget-versions", {
        financialYear: year,
        ...budgetDraft,
        lines: budgetLines.map((line) => ({ ...line, amount: Number(line.amount) })),
      });
      setBudgetDraftId(response.data.id);
      setMsg(`${budgetDraft.kind} budget draft created. Submit it for independent approval before it appears in the statement.`);
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function submitBudgetVersion() {
    if (!budgetDraftId) return;
    setError(null);
    try {
      await api.post(`/public-sector/budget-versions/${budgetDraftId}/submit-for-approval`);
      setMsg("Budget version submitted for independent approval.");
      setBudgetDraftId(null);
      loadBudgetComparison();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  function updateBudgetLine(index: number, field: string, value: string | number) {
    setBudgetLines((current) => current.map((line, i) => i === index ? { ...line, [field]: value } : line));
  }

  async function loadBudgetComparison() {
    setBudgetComparisonLoading(true);
    setError(null);
    try {
      const response = await api.get(`/public-sector/budget-versions/comparison?year=${year}`);
      setBudgetComparison(response.data);
    } catch (err) {
      setBudgetComparison(null);
      setError(extractErrorMessage(err));
    } finally {
      setBudgetComparisonLoading(false);
    }
  }

  async function createTender(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/public-sector/tenders", newTender);
      setMsg("Tender notice created.");
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  const fmt = (n: number) => (n ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Public Sector & Government</h1>
      <p className="text-slate-500 mb-6">Vote/Program/Sub-Program fund hierarchy, commitment accounting, e-tendering, and asset verification.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}
      {msg && <div className="text-sm text-brand-700 bg-brand-50 border border-brand-200 rounded-lg px-3 py-2 mb-4">{msg}</div>}

      <div className="flex gap-2 mb-6">
        <button className={tab === "funds" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("funds")}>Fund Centres & Budget</button>
        <button className={tab === "tenders" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("tenders")}>E-Tendering</button>
        <button className={tab === "assets" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("assets")}>Asset Verification</button>
        <button className={tab === "ipsas24" ? "btn-primary" : "btn-secondary"} onClick={() => { setTab("ipsas24"); loadBudgetComparison(); }}>IPSAS 24 Budget Statement</button>
      </div>

      {tab === "funds" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <h2 className="font-semibold mb-3">Budget Utilization ({year})</h2>
            <table className="table-base">
              <thead><tr><th>Code</th><th>Name</th><th>Appropriation</th><th>Commitments</th><th>Actuals</th><th>Available</th><th>% Used</th></tr></thead>
              <tbody>
                {utilization.map((u: any) => (
                  <tr key={u.fundCentreId} className={u.overNinetyPercent ? "bg-red-50" : ""}>
                    <td className="font-mono">{u.code}</td><td>{u.name}</td>
                    <td className="text-right">{fmt(u.appropriation)}</td><td className="text-right">{fmt(u.commitments)}</td>
                    <td className="text-right">{fmt(u.actuals)}</td><td className="text-right">{fmt(u.available)}</td>
                    <td className={`text-right font-medium ${u.overNinetyPercent ? "text-red-600" : ""}`}>{u.percentUtilised}%</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <form onSubmit={createFund} className="card space-y-3 h-fit">
            <h2 className="font-semibold">New Fund Centre</h2>
            <div><label className="label">Code</label><input className="input" required value={newFund.code} onChange={(e) => setNewFund({ ...newFund, code: e.target.value })} /></div>
            <div><label className="label">Name</label><input className="input" required value={newFund.name} onChange={(e) => setNewFund({ ...newFund, name: e.target.value })} /></div>
            <div>
              <label className="label">Level</label>
              <select className="input" value={newFund.level} onChange={(e) => setNewFund({ ...newFund, level: e.target.value })}>
                <option value="Vote">Vote</option><option value="Program">Program</option><option value="SubProgram">Sub-Program</option>
              </select>
            </div>
            <div><label className="label">Appropriation Amount</label><input className="input" type="number" value={newFund.appropriationAmount} onChange={(e) => setNewFund({ ...newFund, appropriationAmount: Number(e.target.value) })} /></div>
            <button className="btn-primary w-full">Create Fund Centre</button>
          </form>
        </div>
      )}

      {tab === "tenders" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <table className="table-base">
              <thead><tr><th>Tender #</th><th>Title</th><th>Deadline</th><th>Status</th><th>Bids</th></tr></thead>
              <tbody>{tenders.map((t: any) => (<tr key={t.id}><td className="font-mono">{t.tenderNo}</td><td>{t.title}</td><td>{new Date(t.submissionDeadline).toLocaleString()}</td><td>{t.status}</td><td>{t.bids?.length ?? 0}</td></tr>))}</tbody>
            </table>
          </div>
          <form onSubmit={createTender} className="card space-y-3 h-fit">
            <h2 className="font-semibold">New Tender Notice</h2>
            <div><label className="label">Title</label><input className="input" required value={newTender.title} onChange={(e) => setNewTender({ ...newTender, title: e.target.value })} /></div>
            <div><label className="label">Specifications</label><textarea className="input" value={newTender.specifications} onChange={(e) => setNewTender({ ...newTender, specifications: e.target.value })} /></div>
            <div><label className="label">Submission Deadline</label><input className="input" type="datetime-local" required value={newTender.submissionDeadline} onChange={(e) => setNewTender({ ...newTender, submissionDeadline: e.target.value })} /></div>
            <button className="btn-primary w-full">Publish Tender</button>
            <p className="text-xs text-slate-400">Bids submitted after the deadline are rejected automatically by the server clock.</p>
          </form>
        </div>
      )}

      {tab === "ipsas24" && (
        <section className="card space-y-4">
          <div className="flex items-center justify-between gap-3 flex-wrap">
            <div>
              <h2 className="font-semibold text-lg">Statement of Comparison of Budget and Actual Amounts</h2>
              <p className="text-xs text-slate-500">IPSAS 24 · Financial year {year}</p>
            </div>
            <button className="btn-secondary" onClick={loadBudgetComparison} disabled={budgetComparisonLoading}>
              {budgetComparisonLoading ? "Loading…" : "Refresh statement"}
            </button>
          </div>
          {budgetComparisonLoading && <p className="text-sm text-slate-500">Loading approved budget versions and ledger actuals…</p>}
          <form onSubmit={createBudgetVersion} className="rounded-lg border p-4 space-y-3">
            <h3 className="font-semibold">Create an original or revised budget version</h3>
            <p className="text-xs text-slate-500">The draft is not included in the financial statement until an independent approver finalises its approval workflow.</p>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
              <div><label className="label">Version type</label><select className="input" value={budgetDraft.kind} onChange={(e) => setBudgetDraft({ ...budgetDraft, kind: e.target.value })}><option value="Original">Original</option><option value="Revised">Revised</option></select></div>
              <div><label className="label">Currency</label><input className="input" maxLength={3} required value={budgetDraft.currency} onChange={(e) => setBudgetDraft({ ...budgetDraft, currency: e.target.value.toUpperCase() })} /></div>
              <div><label className="label">Budget accounting basis</label><select className="input" value={budgetDraft.accountingBasis} onChange={(e) => setBudgetDraft({ ...budgetDraft, accountingBasis: e.target.value })}><option value="Accrual">Accrual</option><option value="Cash">Cash</option></select></div>
              <div><label className="label">Classification basis</label><input className="input" required value={budgetDraft.classificationBasis} onChange={(e) => setBudgetDraft({ ...budgetDraft, classificationBasis: e.target.value })} placeholder="Approved Treasury classification" /></div>
              <div><label className="label">Entity scope</label><input className="input" required value={budgetDraft.entityScope} onChange={(e) => setBudgetDraft({ ...budgetDraft, entityScope: e.target.value })} placeholder="Reporting entity / MDA" /></div>
              <div><label className="label">Authority reference</label><input className="input" required value={budgetDraft.authorityReference} onChange={(e) => setBudgetDraft({ ...budgetDraft, authorityReference: e.target.value })} placeholder="Appropriation Act / authority" /></div>
              <div><label className="label">Period start</label><input className="input" type="date" required value={budgetDraft.periodStart.slice(0, 10)} onChange={(e) => setBudgetDraft({ ...budgetDraft, periodStart: `${e.target.value}T00:00:00.000Z` })} /></div>
              <div><label className="label">Period end (exclusive)</label><input className="input" type="date" required value={budgetDraft.periodEnd.slice(0, 10)} onChange={(e) => setBudgetDraft({ ...budgetDraft, periodEnd: `${e.target.value}T00:00:00.000Z` })} /></div>
              {budgetDraft.kind === "Revised" && <div className="md:col-span-3"><label className="label">Reason for revision</label><textarea className="input" required value={budgetDraft.amendmentReason} onChange={(e) => setBudgetDraft({ ...budgetDraft, amendmentReason: e.target.value })} /></div>}
            </div>
            <div className="space-y-2">
              <div className="flex items-center justify-between"><h4 className="font-medium text-sm">Budget lines</h4><button type="button" className="btn-secondary" onClick={() => setBudgetLines((current) => [...current, { fundCentreId: "", glAccountId: "", costCentreId: null, description: "", classificationCode: "", amount: 0 }])}>Add line</button></div>
              {budgetLines.map((line, index) => (
                <div key={index} className="grid grid-cols-1 md:grid-cols-6 gap-2 border-t pt-2">
                  <select className="input" required value={line.fundCentreId} onChange={(e) => updateBudgetLine(index, "fundCentreId", e.target.value)}><option value="">Fund centre</option>{fundCentres.map((f: any) => <option key={f.id} value={f.id}>{f.code} — {f.name}</option>)}</select>
                  <select className="input" required value={line.glAccountId} onChange={(e) => updateBudgetLine(index, "glAccountId", e.target.value)}><option value="">Income / expense account</option>{budgetAccounts.map((a: any) => <option key={a.id} value={a.id}>{a.code} — {a.name} ({a.type})</option>)}</select>
                  <input className="input" required placeholder="Description" value={line.description} onChange={(e) => updateBudgetLine(index, "description", e.target.value)} />
                  <input className="input" required placeholder="Classification code" value={line.classificationCode} onChange={(e) => updateBudgetLine(index, "classificationCode", e.target.value)} />
                  <input className="input" type="number" min="0" step="0.01" required placeholder="Amount" value={line.amount} onChange={(e) => updateBudgetLine(index, "amount", Number(e.target.value))} />
                  <button type="button" className="btn-secondary" disabled={budgetLines.length === 1} onClick={() => setBudgetLines((current) => current.filter((_, i) => i !== index))}>Remove</button>
                </div>
              ))}
            </div>
            <div className="flex gap-2 flex-wrap">
              <button className="btn-primary" disabled={budgetAccounts.length === 0 || fundCentres.length === 0}>Save budget draft</button>
              {budgetDraftId && <button type="button" className="btn-secondary" onClick={submitBudgetVersion}>Submit draft for approval</button>}
            </div>
            {budgetAccounts.length === 0 && <p className="text-xs text-amber-700">Configure active income and expense GL accounts before entering budget lines.</p>}
          </form>
          {!budgetComparisonLoading && !budgetComparison && (
            <p className="text-sm text-slate-500">No comparison is available. An approved original budget version is required before the statement can be generated.</p>
          )}
          {budgetComparison && (
            <>
              <div className="grid grid-cols-1 md:grid-cols-3 gap-3 text-sm">
                <div><span className="text-slate-500">Currency</span><p className="font-medium">{budgetComparison.currency}</p></div>
                <div><span className="text-slate-500">Budget basis</span><p className="font-medium">{budgetComparison.accountingBasis}</p></div>
                <div><span className="text-slate-500">Classification basis</span><p className="font-medium">{budgetComparison.classificationBasis}</p></div>
                <div><span className="text-slate-500">Entity scope</span><p className="font-medium">{budgetComparison.entityScope}</p></div>
                <div><span className="text-slate-500">Original budget</span><p className="font-medium">{budgetComparison.originalBudgetVersionId}</p></div>
                <div><span className="text-slate-500">Final budget</span><p className="font-medium">{budgetComparison.finalBudgetAvailable ? budgetComparison.finalBudgetVersionId : "Not approved"}</p></div>
              </div>
              {!budgetComparison.comparableActualsAvailable && (
                <div className="rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
                  {budgetComparison.comparabilityMessage}
                </div>
              )}
              <div className="overflow-x-auto">
                <table className="table-base min-w-[900px]">
                  <thead><tr><th>Classification</th><th>Fund centre</th><th>Account</th><th className="text-right">Original</th><th className="text-right">Final</th><th className="text-right">Actual (comparable)</th><th className="text-right">Variance</th><th>Explanation</th></tr></thead>
                  <tbody>
                    {budgetComparison.lines.map((line: any, index: number) => (
                      <tr key={`${line.accountCode}-${line.fundCentreCode}-${index}`}>
                        <td>{line.classificationCode}</td>
                        <td><span className="font-mono">{line.fundCentreCode}</span><br />{line.fundCentreName}</td>
                        <td><span className="font-mono">{line.accountCode}</span><br />{line.accountName}</td>
                        <td className="text-right">{fmt(line.originalBudget)}</td>
                        <td className="text-right">{line.finalBudget == null ? "—" : fmt(line.finalBudget)}</td>
                        <td className="text-right">{line.actualOnComparableBasis == null ? "—" : fmt(line.actualOnComparableBasis)}</td>
                        <td className="text-right">{line.variance == null ? "—" : fmt(line.variance)}</td>
                        <td>{line.originalToFinalExplanation ?? "—"}</td>
                      </tr>
                    ))}
                    {budgetComparison.lines.length === 0 && <tr><td colSpan={8} className="text-center text-slate-500">No budget lines are available.</td></tr>}
                  </tbody>
                </table>
              </div>
              {budgetComparison.reconciliations?.length > 0 && (
                <div>
                  <h3 className="font-semibold mb-2">Reconciliation to financial statements</h3>
                  <table className="table-base">
                    <thead><tr><th>Difference type</th><th>Description</th><th>Statement section</th><th className="text-right">Operating</th><th className="text-right">Investing</th><th className="text-right">Financing</th></tr></thead>
                    <tbody>{budgetComparison.reconciliations.map((item: any) => (
                      <tr key={item.id}><td>{item.type}</td><td>{item.description}</td><td>{item.statementSection}</td><td className="text-right">{fmt(item.operatingAmount)}</td><td className="text-right">{fmt(item.investingAmount)}</td><td className="text-right">{fmt(item.financingAmount)}</td></tr>
                    ))}</tbody>
                  </table>
                </div>
              )}
            </>
          )}
        </section>
      )}

      {tab === "assets" && (
        <div className="card overflow-x-auto">
          <h2 className="font-semibold mb-3">Verification Batches</h2>
          <table className="table-base">
            <thead><tr><th>Batch #</th><th>Date</th><th>Location</th><th>Complete</th><th>Scans</th></tr></thead>
            <tbody>{batches.map((b: any) => (<tr key={b.id}><td className="font-mono">{b.batchNo}</td><td>{new Date(b.verificationDate).toLocaleDateString()}</td><td>{b.location}</td><td>{b.isComplete ? "Yes" : "No"}</td><td>{b.scans?.length ?? 0}</td></tr>))}</tbody>
          </table>
          <p className="text-xs text-slate-400 mt-3">Barcode/RFID hardware scanning itself isn't integrated in this build — start a batch and submit each scanned tag value via the API to record Found/Missing/Condition-Changed results.</p>
        </div>
      )}
    </AppShell>
  );
}
