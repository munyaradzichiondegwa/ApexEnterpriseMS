"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function GrantsPage() {
  const [tab, setTab] = useState<"grants" | "beneficiaries">("grants");
  const [grants, setGrants] = useState<any[]>([]);
  const [beneficiaries, setBeneficiaries] = useState<any[]>([]);
  const [selectedGrant, setSelectedGrant] = useState<any>(null);
  const [budgetVsActual, setBudgetVsActual] = useState<any[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [newGrant, setNewGrant] = useState({
    grantNo: "", title: "", donorCustomerId: "", awardAmount: 0, currency: "USD",
    startDate: "", endDate: "", indirectCostRatePct: 10, restrictionsJson: "{}", enforceRestriction: true,
  });
  const [newBeneficiary, setNewBeneficiary] = useState({ name: "", idNo: "", location: "", gender: "", vulnerabilityCategory: "" });

  function load() {
    api.get("/grants").then((r) => setGrants(r.data)).catch((e) => setError(extractErrorMessage(e)));
    api.get("/grants/beneficiaries").then((r) => setBeneficiaries(r.data)).catch(() => {});
  }
  useEffect(load, []);

  async function createGrant(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/grants", newGrant);
      setMsg("Grant created.");
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function createBeneficiary(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/grants/beneficiaries", newBeneficiary);
      setMsg("Beneficiary registered.");
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function viewGrant(grant: any) {
    setSelectedGrant(grant);
    try { setBudgetVsActual((await api.get(`/grants/${grant.id}/budget-vs-actual`)).data); } catch (err) { setError(extractErrorMessage(err)); }
  }

  const fmt = (n: number) => (n ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Grants, Donors & NGO Management</h1>
      <p className="text-slate-500 mb-6">Grant budgets with restriction enforcement, indirect cost recovery, sub-awards, and beneficiary tracking.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}
      {msg && <div className="text-sm text-brand-700 bg-brand-50 border border-brand-200 rounded-lg px-3 py-2 mb-4">{msg}</div>}

      <div className="flex gap-2 mb-6">
        <button className={tab === "grants" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("grants")}>Grants</button>
        <button className={tab === "beneficiaries" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("beneficiaries")}>Beneficiaries</button>
      </div>

      {tab === "grants" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 space-y-6">
            <div className="card overflow-x-auto">
              <table className="table-base">
                <thead><tr><th>Grant #</th><th>Title</th><th>Award Amount</th><th>Status</th><th></th></tr></thead>
                <tbody>
                  {grants.map((g) => (
                    <tr key={g.id} className="cursor-pointer hover:bg-slate-50">
                      <td className="font-mono">{g.grantNo}</td><td>{g.title}</td><td className="text-right">{fmt(g.awardAmount)} {g.currency}</td><td>{g.status}</td>
                      <td><button className="btn-secondary text-xs" onClick={() => viewGrant(g)}>Budget vs Actual</button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {selectedGrant && (
              <div className="card overflow-x-auto">
                <h2 className="font-semibold mb-3">Budget vs Actual — {selectedGrant.title}</h2>
                <table className="table-base">
                  <thead><tr><th>Account</th><th>Budget</th><th>Actual</th><th>Variance</th><th>% Used</th></tr></thead>
                  <tbody>
                    {budgetVsActual.map((l: any, i: number) => (
                      <tr key={i} className={l.overBudget ? "bg-red-50" : ""}>
                        <td>{l.accountName}</td><td className="text-right">{fmt(l.budget)}</td><td className="text-right">{fmt(l.actual)}</td>
                        <td className="text-right">{fmt(l.variance)}</td><td className={`text-right ${l.overBudget ? "text-red-600 font-medium" : ""}`}>{l.percentConsumed}%</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
          <form onSubmit={createGrant} className="card space-y-3 h-fit">
            <h2 className="font-semibold">New Grant</h2>
            <div><label className="label">Grant No</label><input className="input" required value={newGrant.grantNo} onChange={(e) => setNewGrant({ ...newGrant, grantNo: e.target.value })} /></div>
            <div><label className="label">Title</label><input className="input" required value={newGrant.title} onChange={(e) => setNewGrant({ ...newGrant, title: e.target.value })} /></div>
            <div><label className="label">Donor (Customer ID)</label><input className="input" required value={newGrant.donorCustomerId} onChange={(e) => setNewGrant({ ...newGrant, donorCustomerId: e.target.value })} /></div>
            <div><label className="label">Award Amount</label><input className="input" type="number" required value={newGrant.awardAmount} onChange={(e) => setNewGrant({ ...newGrant, awardAmount: Number(e.target.value) })} /></div>
            <div className="grid grid-cols-2 gap-2">
              <div><label className="label">Start Date</label><input className="input" type="date" required value={newGrant.startDate} onChange={(e) => setNewGrant({ ...newGrant, startDate: e.target.value })} /></div>
              <div><label className="label">End Date</label><input className="input" type="date" required value={newGrant.endDate} onChange={(e) => setNewGrant({ ...newGrant, endDate: e.target.value })} /></div>
            </div>
            <div><label className="label">Indirect Cost Rate %</label><input className="input" type="number" value={newGrant.indirectCostRatePct} onChange={(e) => setNewGrant({ ...newGrant, indirectCostRatePct: Number(e.target.value) })} /></div>
            <div className="flex items-center gap-2">
              <input type="checkbox" checked={newGrant.enforceRestriction} onChange={(e) => setNewGrant({ ...newGrant, enforceRestriction: e.target.checked })} />
              <label className="text-sm">Block postings that exceed budget</label>
            </div>
            <button className="btn-primary w-full">Create Grant</button>
          </form>
        </div>
      )}

      {tab === "beneficiaries" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <table className="table-base">
              <thead><tr><th>Name</th><th>Location</th><th>Gender</th><th>Category</th></tr></thead>
              <tbody>{beneficiaries.map((b) => (<tr key={b.id}><td>{b.name}</td><td>{b.location}</td><td>{b.gender}</td><td>{b.vulnerabilityCategory}</td></tr>))}</tbody>
            </table>
          </div>
          <form onSubmit={createBeneficiary} className="card space-y-3 h-fit">
            <h2 className="font-semibold">Register Beneficiary</h2>
            <div><label className="label">Name</label><input className="input" required value={newBeneficiary.name} onChange={(e) => setNewBeneficiary({ ...newBeneficiary, name: e.target.value })} /></div>
            <div><label className="label">Location</label><input className="input" value={newBeneficiary.location} onChange={(e) => setNewBeneficiary({ ...newBeneficiary, location: e.target.value })} /></div>
            <div><label className="label">Gender</label><input className="input" value={newBeneficiary.gender} onChange={(e) => setNewBeneficiary({ ...newBeneficiary, gender: e.target.value })} /></div>
            <div><label className="label">Vulnerability Category</label><input className="input" value={newBeneficiary.vulnerabilityCategory} onChange={(e) => setNewBeneficiary({ ...newBeneficiary, vulnerabilityCategory: e.target.value })} /></div>
            <button className="btn-primary w-full">Register</button>
          </form>
        </div>
      )}
    </AppShell>
  );
}
