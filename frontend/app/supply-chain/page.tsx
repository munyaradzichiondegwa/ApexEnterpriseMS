"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function SupplyChainPage() {
  const [vendors, setVendors] = useState<any[]>([]);
  const [pos, setPos] = useState<any[]>([]);
  const [tab, setTab] = useState<"pos" | "vendors">("pos");
  const [error, setError] = useState<string | null>(null);
  const [newVendor, setNewVendor] = useState({ code: "", name: "", email: "", currency: "USD", category: "" });

  function load() {
    api.get("/supply-chain/vendors").then((r) => setVendors(r.data)).catch((e) => setError(extractErrorMessage(e)));
    api.get("/supply-chain/purchase-orders").then((r) => setPos(r.data)).catch(() => {});
  }
  useEffect(load, []);

  async function createVendor(e: React.FormEvent) {
    e.preventDefault();
    try { await api.post("/supply-chain/vendors", newVendor); load(); } catch (err) { setError(extractErrorMessage(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Supply Chain & Procurement</h1>
      <p className="text-slate-500 mb-6">Vendors, purchase orders, and goods receipt into inventory (three-way match).</p>
      <div className="flex gap-2 mb-6">
        {(["pos", "vendors"] as const).map((t) => (<button key={t} className={tab === t ? "btn-primary" : "btn-secondary"} onClick={() => setTab(t)}>{t === "pos" ? "Purchase Orders" : "Vendors"}</button>))}
      </div>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      {tab === "pos" && (
        <div className="card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>PO #</th><th>Issue Date</th><th>Status</th><th>Total</th></tr></thead>
            <tbody>{pos.map((p) => (<tr key={p.id}><td className="font-mono">{p.poNo}</td><td>{new Date(p.issueDate).toLocaleDateString()}</td><td>{p.status}</td><td className="text-right">{Number(p.total).toFixed(2)}</td></tr>))}</tbody>
          </table>
        </div>
      )}

      {tab === "vendors" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <table className="table-base">
              <thead><tr><th>Code</th><th>Name</th><th>Email</th><th>Category</th><th>Approved</th></tr></thead>
              <tbody>{vendors.map((v) => (<tr key={v.id}><td>{v.code}</td><td>{v.name}</td><td>{v.email}</td><td>{v.category}</td><td>{v.isApproved ? "Yes" : "No"}</td></tr>))}</tbody>
            </table>
          </div>
          <form onSubmit={createVendor} className="card space-y-3 h-fit">
            <h2 className="font-semibold">New Vendor</h2>
            <div><label className="label">Code</label><input className="input" required value={newVendor.code} onChange={(e) => setNewVendor({ ...newVendor, code: e.target.value })} /></div>
            <div><label className="label">Name</label><input className="input" required value={newVendor.name} onChange={(e) => setNewVendor({ ...newVendor, name: e.target.value })} /></div>
            <div><label className="label">Category</label><input className="input" value={newVendor.category} onChange={(e) => setNewVendor({ ...newVendor, category: e.target.value })} /></div>
            <button className="btn-primary w-full">Add Vendor</button>
          </form>
        </div>
      )}
    </AppShell>
  );
}
