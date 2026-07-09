"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function ApPage() {
  const [suppliers, setSuppliers] = useState<any[]>([]);
  const [invoices, setInvoices] = useState<any[]>([]);
  const [tab, setTab] = useState<"invoices" | "suppliers">("invoices");
  const [error, setError] = useState<string | null>(null);
  const [newSupplier, setNewSupplier] = useState({ code: "", name: "", email: "", currency: "USD", paymentTermsDays: 30, whtApplicable: false, whtRate: 0.10 });

  function load() {
    api.get("/ap/suppliers").then((r) => setSuppliers(r.data)).catch((e) => setError(extractErrorMessage(e)));
    api.get("/ap/invoices").then((r) => setInvoices(r.data)).catch(() => {});
  }
  useEffect(load, []);

  async function createSupplier(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/ap/suppliers", newSupplier);
      setNewSupplier({ code: "", name: "", email: "", currency: "USD", paymentTermsDays: 30, whtApplicable: false, whtRate: 0.10 });
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Accounts Payable</h1>
      <p className="text-slate-500 mb-6">Suppliers, purchase invoices with VAT input and WHT, and payments.</p>

      <div className="flex gap-2 mb-6">
        {(["invoices", "suppliers"] as const).map((t) => (
          <button key={t} className={tab === t ? "btn-primary" : "btn-secondary"} onClick={() => setTab(t)}>{t[0].toUpperCase() + t.slice(1)}</button>
        ))}
      </div>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      {tab === "invoices" && (
        <div className="card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>Invoice #</th><th>Supplier</th><th>Date</th><th>Total</th><th>WHT</th><th>Outstanding</th><th>Status</th></tr></thead>
            <tbody>
              {invoices.map((i) => (
                <tr key={i.id}>
                  <td className="font-mono">{i.invoiceNo}</td>
                  <td>{i.supplier?.name}</td>
                  <td>{new Date(i.invoiceDate).toLocaleDateString()}</td>
                  <td className="text-right">{Number(i.total).toFixed(2)}</td>
                  <td className="text-right">{Number(i.whtAmount).toFixed(2)}</td>
                  <td className="text-right">{Number(i.outstanding).toFixed(2)}</td>
                  <td>{i.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {tab === "suppliers" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <table className="table-base">
              <thead><tr><th>Code</th><th>Name</th><th>Email</th><th>WHT Applicable</th><th>WHT Rate</th></tr></thead>
              <tbody>
                {suppliers.map((s) => (
                  <tr key={s.id}><td>{s.code}</td><td>{s.name}</td><td>{s.email}</td><td>{s.whtApplicable ? "Yes" : "No"}</td><td>{(s.whtRate * 100).toFixed(0)}%</td></tr>
                ))}
              </tbody>
            </table>
          </div>
          <form onSubmit={createSupplier} className="card space-y-3 h-fit">
            <h2 className="font-semibold">New Supplier</h2>
            <div><label className="label">Code</label><input className="input" required value={newSupplier.code} onChange={(e) => setNewSupplier({ ...newSupplier, code: e.target.value })} /></div>
            <div><label className="label">Name</label><input className="input" required value={newSupplier.name} onChange={(e) => setNewSupplier({ ...newSupplier, name: e.target.value })} /></div>
            <div><label className="label">Email</label><input className="input" type="email" value={newSupplier.email} onChange={(e) => setNewSupplier({ ...newSupplier, email: e.target.value })} /></div>
            <div className="flex items-center gap-2">
              <input type="checkbox" checked={newSupplier.whtApplicable} onChange={(e) => setNewSupplier({ ...newSupplier, whtApplicable: e.target.checked })} />
              <label className="text-sm">WHT applicable (non-compliant supplier)</label>
            </div>
            <button className="btn-primary w-full">Add Supplier</button>
          </form>
        </div>
      )}
    </AppShell>
  );
}
