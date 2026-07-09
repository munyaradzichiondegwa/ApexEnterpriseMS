"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api } from "@/lib/api";

export default function DashboardPage() {
  const [balanceSheet, setBalanceSheet] = useState<any>(null);
  const [incomeStatement, setIncomeStatement] = useState<any>(null);
  const [lowStock, setLowStock] = useState<any[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);

  useEffect(() => {
    const now = new Date();
    const from = new Date(now.getFullYear(), 0, 1).toISOString();
    const to = now.toISOString();

    Promise.allSettled([
      api.get("/reports/balance-sheet"),
      api.get(`/reports/income-statement?from=${from}&to=${to}`),
      api.get("/inventory/items/low-stock"),
    ]).then(([bs, is, ls]) => {
      if (bs.status === "fulfilled") setBalanceSheet(bs.value.data);
      if (is.status === "fulfilled") setIncomeStatement(is.value.data);
      if (ls.status === "fulfilled") setLowStock(ls.value.data);
      if (bs.status === "rejected" && is.status === "rejected") {
        setLoadError("Could not reach the ApexERP API. Make sure the backend is running.");
      }
    });
  }, []);

  const fmt = (n: number | undefined) => (n ?? 0).toLocaleString(undefined, { style: "currency", currency: "USD" });

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Dashboard</h1>
      <p className="text-slate-500 mb-6">Real-time snapshot across all posted modules.</p>

      {loadError && <div className="card mb-6 text-amber-700 bg-amber-50 border-amber-200">{loadError}</div>}

      <div className="grid grid-cols-1 md:grid-cols-4 gap-4 mb-8">
        <div className="card">
          <div className="text-xs uppercase text-slate-400 font-medium">Total Assets</div>
          <div className="text-2xl font-bold mt-1">{fmt(balanceSheet?.totalAssets)}</div>
        </div>
        <div className="card">
          <div className="text-xs uppercase text-slate-400 font-medium">Total Liabilities</div>
          <div className="text-2xl font-bold mt-1">{fmt(balanceSheet?.totalLiabilities)}</div>
        </div>
        <div className="card">
          <div className="text-xs uppercase text-slate-400 font-medium">YTD Net Profit</div>
          <div className="text-2xl font-bold mt-1">{fmt(incomeStatement?.netProfit)}</div>
        </div>
        <div className="card">
          <div className="text-xs uppercase text-slate-400 font-medium">Low Stock Items</div>
          <div className="text-2xl font-bold mt-1">{lowStock.length}</div>
        </div>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
        <div className="card">
          <h2 className="font-semibold mb-3">YTD Income vs Expense</h2>
          <div className="flex justify-between text-sm py-1"><span className="text-slate-500">Income</span><span className="font-medium">{fmt(incomeStatement?.totalIncome)}</span></div>
          <div className="flex justify-between text-sm py-1"><span className="text-slate-500">Expense</span><span className="font-medium">{fmt(incomeStatement?.totalExpense)}</span></div>
          <div className="flex justify-between text-sm py-1 border-t border-slate-200 mt-1 pt-2"><span className="font-semibold">Net Profit</span><span className="font-semibold">{fmt(incomeStatement?.netProfit)}</span></div>
        </div>
        <div className="card">
          <h2 className="font-semibold mb-3">Items Below Reorder Level</h2>
          {lowStock.length === 0 && <p className="text-sm text-slate-400">All stock levels healthy.</p>}
          <ul className="text-sm divide-y divide-slate-100">
            {lowStock.slice(0, 6).map((item) => (
              <li key={item.id} className="py-1.5 flex justify-between">
                <span>{item.name} ({item.sku})</span>
                <span className="text-red-600 font-medium">{item.quantityOnHand} / {item.reorderLevel}</span>
              </li>
            ))}
          </ul>
        </div>
      </div>
    </AppShell>
  );
}
