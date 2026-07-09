"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function ArPage() {
  const [customers, setCustomers] = useState<any[]>([]);
  const [invoices, setInvoices] = useState<any[]>([]);
  const [ageing, setAgeing] = useState<any[]>([]);
  const [tab, setTab] = useState<"customers" | "invoices" | "ageing">("invoices");
  const [error, setError] = useState<string | null>(null);
  const [newCustomer, setNewCustomer] = useState({ code: "", name: "", email: "", currency: "USD", creditLimit: 0, paymentTermsDays: 30 });

  function load() {
    api.get("/ar/customers").then((r) => setCustomers(r.data)).catch((e) => setError(extractErrorMessage(e)));
    api.get("/ar/invoices").then((r) => setInvoices(r.data)).catch(() => {});
    api.get("/ar/ageing").then((r) => setAgeing(r.data)).catch(() => {});
  }
  useEffect(load, []);

  async function createCustomer(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/ar/customers", newCustomer);
      setNewCustomer({ code: "", name: "", email: "", currency: "USD", creditLimit: 0, paymentTermsDays: 30 });
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Accounts Receivable</h1>
      <p className="text-slate-500 mb-6">Customers, sales invoices with VAT, receipts, and ageing analysis.</p>

      <div className="flex gap-2 mb-6">
        {(["invoices", "customers", "ageing"] as const).map((t) => (
          <button key={t} className={tab === t ? "btn-primary" : "btn-secondary"} onClick={() => setTab(t)}>{t[0].toUpperCase() + t.slice(1)}</button>
        ))}
      </div>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      {tab === "invoices" && (
        <div className="card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>Invoice #</th><th>Customer</th><th>Date</th><th>Due</th><th>Total</th><th>Outstanding</th><th>Status</th></tr></thead>
            <tbody>
              {invoices.map((i) => (
                <tr key={i.id}>
                  <td className="font-mono">{i.invoiceNo}</td>
                  <td>{i.customer?.name}</td>
                  <td>{new Date(i.invoiceDate).toLocaleDateString()}</td>
                  <td>{new Date(i.dueDate).toLocaleDateString()}</td>
                  <td className="text-right">{Number(i.total).toFixed(2)}</td>
                  <td className="text-right">{Number(i.outstanding).toFixed(2)}</td>
                  <td>{i.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {tab === "customers" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <table className="table-base">
              <thead><tr><th>Code</th><th>Name</th><th>Email</th><th>Credit Limit</th><th>Terms (days)</th></tr></thead>
              <tbody>
                {customers.map((c) => (
                  <tr key={c.id}><td>{c.code}</td><td>{c.name}</td><td>{c.email}</td><td className="text-right">{c.creditLimit}</td><td>{c.paymentTermsDays}</td></tr>
                ))}
              </tbody>
            </table>
          </div>
          <form onSubmit={createCustomer} className="card space-y-3 h-fit">
            <h2 className="font-semibold">New Customer</h2>
            <div><label className="label">Code</label><input className="input" required value={newCustomer.code} onChange={(e) => setNewCustomer({ ...newCustomer, code: e.target.value })} /></div>
            <div><label className="label">Name</label><input className="input" required value={newCustomer.name} onChange={(e) => setNewCustomer({ ...newCustomer, name: e.target.value })} /></div>
            <div><label className="label">Email</label><input className="input" type="email" value={newCustomer.email} onChange={(e) => setNewCustomer({ ...newCustomer, email: e.target.value })} /></div>
            <div><label className="label">Credit Limit</label><input className="input" type="number" value={newCustomer.creditLimit} onChange={(e) => setNewCustomer({ ...newCustomer, creditLimit: Number(e.target.value) })} /></div>
            <button className="btn-primary w-full">Add Customer</button>
          </form>
        </div>
      )}

      {tab === "ageing" && (
        <div className="card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>Customer</th><th>Current</th><th>30 Days</th><th>60 Days</th><th>90+ Days</th><th>Total</th></tr></thead>
            <tbody>
              {ageing.map((a) => (
                <tr key={a.customerId}>
                  <td>{a.customerName}</td>
                  <td className="text-right">{a.current.toFixed(2)}</td>
                  <td className="text-right">{a.days30.toFixed(2)}</td>
                  <td className="text-right">{a.days60.toFixed(2)}</td>
                  <td className="text-right text-red-600">{a.days90Plus.toFixed(2)}</td>
                  <td className="text-right font-semibold">{a.total.toFixed(2)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </AppShell>
  );
}
