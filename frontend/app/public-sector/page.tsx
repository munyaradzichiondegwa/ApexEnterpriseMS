"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function PublicSectorPage() {
  const [tab, setTab] = useState<"funds" | "tenders" | "assets">("funds");
  const [fundCentres, setFundCentres] = useState<any[]>([]);
  const [utilization, setUtilization] = useState<any[]>([]);
  const [tenders, setTenders] = useState<any[]>([]);
  const [batches, setBatches] = useState<any[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [year] = useState(new Date().getFullYear());
  const [newFund, setNewFund] = useState({ code: "", name: "", level: "Vote", financialYear: year, appropriationAmount: 0, carriedForward: 0 });
  const [newTender, setNewTender] = useState({ title: "", specifications: "", submissionDeadline: "" });

  function load() {
    api.get(`/public-sector/fund-centres?year=${year}`).then((r) => setFundCentres(r.data)).catch((e) => setError(extractErrorMessage(e)));
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
