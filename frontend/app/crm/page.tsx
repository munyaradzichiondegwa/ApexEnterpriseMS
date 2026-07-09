"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

const STAGES = ["New", "Contacted", "Qualified", "Won", "Lost"];

export default function CrmPage() {
  const [leads, setLeads] = useState<any[]>([]);
  const [orders, setOrders] = useState<any[]>([]);
  const [tab, setTab] = useState<"leads" | "orders">("leads");
  const [error, setError] = useState<string | null>(null);
  const [newLead, setNewLead] = useState({ name: "", email: "", phone: "", companyName: "", source: "", valueEstimate: 0 });

  function load() {
    api.get("/crm/leads").then((r) => setLeads(r.data)).catch((e) => setError(extractErrorMessage(e)));
    api.get("/crm/sales-orders").then((r) => setOrders(r.data)).catch(() => {});
  }
  useEffect(load, []);

  async function createLead(e: React.FormEvent) {
    e.preventDefault();
    try { await api.post("/crm/leads", newLead); load(); } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function updateStatus(id: string, status: string) {
    try { await api.post(`/crm/leads/${id}/status`, JSON.stringify(status), { headers: { "Content-Type": "application/json" } }); load(); }
    catch (err) { setError(extractErrorMessage(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">CRM & Sales</h1>
      <p className="text-slate-500 mb-6">Lead pipeline and sales orders converting to AR invoices.</p>
      <div className="flex gap-2 mb-6">
        {(["leads", "orders"] as const).map((t) => (<button key={t} className={tab === t ? "btn-primary" : "btn-secondary"} onClick={() => setTab(t)}>{t === "leads" ? "Leads" : "Sales Orders"}</button>))}
      </div>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      {tab === "leads" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <table className="table-base">
              <thead><tr><th>Name</th><th>Company</th><th>Value Est.</th><th>Status</th></tr></thead>
              <tbody>
                {leads.map((l) => (
                  <tr key={l.id}>
                    <td>{l.name}</td><td>{l.companyName}</td><td className="text-right">{Number(l.valueEstimate).toFixed(2)}</td>
                    <td>
                      <select className="input py-1" value={l.status} onChange={(e) => updateStatus(l.id, e.target.value)}>
                        {STAGES.map((s) => <option key={s}>{s}</option>)}
                      </select>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <form onSubmit={createLead} className="card space-y-3 h-fit">
            <h2 className="font-semibold">New Lead</h2>
            <div><label className="label">Name</label><input className="input" required value={newLead.name} onChange={(e) => setNewLead({ ...newLead, name: e.target.value })} /></div>
            <div><label className="label">Company</label><input className="input" value={newLead.companyName} onChange={(e) => setNewLead({ ...newLead, companyName: e.target.value })} /></div>
            <div><label className="label">Value Estimate</label><input className="input" type="number" value={newLead.valueEstimate} onChange={(e) => setNewLead({ ...newLead, valueEstimate: Number(e.target.value) })} /></div>
            <button className="btn-primary w-full">Add Lead</button>
          </form>
        </div>
      )}

      {tab === "orders" && (
        <div className="card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>Order #</th><th>Date</th><th>Status</th><th>Total</th></tr></thead>
            <tbody>{orders.map((o) => (<tr key={o.id}><td className="font-mono">{o.orderNo}</td><td>{new Date(o.orderDate).toLocaleDateString()}</td><td>{o.status}</td><td className="text-right">{Number(o.total).toFixed(2)}</td></tr>))}</tbody>
          </table>
        </div>
      )}
    </AppShell>
  );
}
