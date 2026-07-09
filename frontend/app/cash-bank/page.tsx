"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function CashBankPage() {
  const [accounts, setAccounts] = useState<any[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [transactions, setTransactions] = useState<any[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [newAccount, setNewAccount] = useState({ name: "", accountNo: "", bankName: "", currency: "USD", glAccountId: "" });

  function load() {
    api.get("/cashbank/accounts").then((r) => setAccounts(r.data)).catch((e) => setError(extractErrorMessage(e)));
  }
  useEffect(load, []);

  useEffect(() => {
    if (selected) api.get(`/cashbank/accounts/${selected}/transactions`).then((r) => setTransactions(r.data)).catch(() => {});
  }, [selected]);

  async function createAccount(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/cashbank/accounts", newAccount);
      setNewAccount({ name: "", accountNo: "", bankName: "", currency: "USD", glAccountId: "" });
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Cash & Bank</h1>
      <p className="text-slate-500 mb-6">Bank accounts, statement import, and reconciliation matching.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 space-y-6">
          <div className="card overflow-x-auto">
            <table className="table-base">
              <thead><tr><th>Name</th><th>Bank</th><th>Account No</th><th>Currency</th><th>Balance</th></tr></thead>
              <tbody>
                {accounts.map((a) => (
                  <tr key={a.id} className="cursor-pointer hover:bg-slate-50" onClick={() => setSelected(a.id)}>
                    <td>{a.name}</td><td>{a.bankName}</td><td>{a.accountNo}</td><td>{a.currency}</td>
                    <td className="text-right">{Number(a.currentBalance).toFixed(2)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {selected && (
            <div className="card overflow-x-auto">
              <h2 className="font-semibold mb-3">Transactions</h2>
              <table className="table-base">
                <thead><tr><th>Date</th><th>Description</th><th>Debit</th><th>Credit</th><th>Reconciled</th></tr></thead>
                <tbody>
                  {transactions.map((t) => (
                    <tr key={t.id}>
                      <td>{new Date(t.transactionDate).toLocaleDateString()}</td><td>{t.description}</td>
                      <td className="text-right">{Number(t.debit).toFixed(2)}</td><td className="text-right">{Number(t.credit).toFixed(2)}</td>
                      <td>{t.isReconciled ? "✓" : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
        <form onSubmit={createAccount} className="card space-y-3 h-fit">
          <h2 className="font-semibold">New Bank Account</h2>
          <div><label className="label">Name</label><input className="input" required value={newAccount.name} onChange={(e) => setNewAccount({ ...newAccount, name: e.target.value })} /></div>
          <div><label className="label">Bank Name</label><input className="input" required value={newAccount.bankName} onChange={(e) => setNewAccount({ ...newAccount, bankName: e.target.value })} /></div>
          <div><label className="label">Account No</label><input className="input" required value={newAccount.accountNo} onChange={(e) => setNewAccount({ ...newAccount, accountNo: e.target.value })} /></div>
          <div><label className="label">Linked GL Account ID</label><input className="input" placeholder="GUID from Chart of Accounts" required value={newAccount.glAccountId} onChange={(e) => setNewAccount({ ...newAccount, glAccountId: e.target.value })} /></div>
          <button className="btn-primary w-full">Add Bank Account</button>
        </form>
      </div>
    </AppShell>
  );
}
