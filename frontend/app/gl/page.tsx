"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function GlPage() {
  const [accounts, setAccounts] = useState<any[]>([]);
  const [entries, setEntries] = useState<any[]>([]);
  const [tab, setTab] = useState<"accounts" | "journals">("accounts");
  const [error, setError] = useState<string | null>(null);
  const [newAccount, setNewAccount] = useState({ code: "", name: "", type: "Asset", currency: "USD", isBankAccount: false, isControlAccount: false });

  function load() {
    api.get("/gl/accounts").then((r) => setAccounts(r.data)).catch((e) => setError(extractErrorMessage(e)));
    api.get("/gl/journal-entries").then((r) => setEntries(r.data)).catch(() => {});
  }
  useEffect(load, []);

  async function createAccount(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/gl/accounts", newAccount);
      setNewAccount({ code: "", name: "", type: "Asset", currency: "USD", isBankAccount: false, isControlAccount: false });
      load();
    } catch (err) {
      setError(extractErrorMessage(err));
    }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">General Ledger</h1>
      <p className="text-slate-500 mb-6">Chart of accounts, journal entries, and postings. Every sub-ledger auto-posts here.</p>

      <div className="flex gap-2 mb-6">
        <button className={tab === "accounts" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("accounts")}>Chart of Accounts</button>
        <button className={tab === "journals" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("journals")}>Journal Entries</button>
      </div>

      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      {tab === "accounts" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <table className="table-base">
              <thead><tr><th>Code</th><th>Name</th><th>Type</th><th>Currency</th><th>Balance</th></tr></thead>
              <tbody>
                {accounts.map((a) => (
                  <tr key={a.id}>
                    <td className="font-mono">{a.code}</td>
                    <td>{a.name}</td>
                    <td>{a.type}</td>
                    <td>{a.currency}</td>
                    <td className="text-right">{Number(a.currentBalance).toLocaleString(undefined, { minimumFractionDigits: 2 })}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <form onSubmit={createAccount} className="card space-y-3 h-fit">
            <h2 className="font-semibold">New GL Account</h2>
            <div><label className="label">Code</label><input className="input" required value={newAccount.code} onChange={(e) => setNewAccount({ ...newAccount, code: e.target.value })} /></div>
            <div><label className="label">Name</label><input className="input" required value={newAccount.name} onChange={(e) => setNewAccount({ ...newAccount, name: e.target.value })} /></div>
            <div>
              <label className="label">Type</label>
              <select className="input" value={newAccount.type} onChange={(e) => setNewAccount({ ...newAccount, type: e.target.value })}>
                {["Asset", "Liability", "Equity", "Income", "Expense"].map((t) => <option key={t}>{t}</option>)}
              </select>
            </div>
            <div><label className="label">Currency</label><input className="input" value={newAccount.currency} onChange={(e) => setNewAccount({ ...newAccount, currency: e.target.value })} /></div>
            <button className="btn-primary w-full">Add Account</button>
          </form>
        </div>
      )}

      {tab === "journals" && (
        <div className="card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>Entry No</th><th>Date</th><th>Source</th><th>Description</th><th>Status</th><th>Lines</th></tr></thead>
            <tbody>
              {entries.map((j) => (
                <tr key={j.id}>
                  <td className="font-mono">{j.entryNo}</td>
                  <td>{new Date(j.entryDate).toLocaleDateString()}</td>
                  <td>{j.source}</td>
                  <td>{j.description}</td>
                  <td>{j.status}</td>
                  <td>{j.lines?.length ?? 0}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </AppShell>
  );
}
