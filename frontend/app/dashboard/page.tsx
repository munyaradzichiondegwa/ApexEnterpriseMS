"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import Link from "next/link";
import { api } from "@/lib/api";
import { Stat, Section, DataTable, Badge } from "@/components/ui";
import { fmtMoney, fmtDate } from "@/lib/format";

export default function DashboardPage() {
  const [balanceSheet, setBalanceSheet] = useState<any>(null);
  const [incomeStatement, setIncomeStatement] = useState<any>(null);
  const [lowStock, setLowStock] = useState<any[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [exec, setExec] = useState<any>(null);
  const [execError, setExecError] = useState<string | null>(null);

  useEffect(() => {
    const now = new Date();
    const from = new Date(now.getFullYear(), 0, 1).toISOString();
    const to = now.toISOString();

    Promise.allSettled([
      api.get("/reports/balance-sheet"),
      api.get(`/reports/income-statement?from=${from}&to=${to}`),
      api.get("/inventory/items/low-stock"),
      api.get("/reports/dashboard"),
    ]).then(([bs, is, ls, ex]) => {
      if (ex.status === "fulfilled") setExec(ex.value.data);
      else setExecError("The control and cash dashboard is not available to your role or could not be loaded.");
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

      <ExecutiveDashboard exec={exec} error={execError} />
    </AppShell>
  );
}

type Ageing = { current: number; days1to30: number; days31to60: number; days61to90: number; over90: number; total: number };

/** Financial position, control health, period close, compliance and operations (spec 14). Each tile links to the screen that fixes it. */
function ExecutiveDashboard({ exec, error }: { exec: any; error: string | null }) {
  if (error) return <p className="text-sm text-slate-500 mt-8">{error}</p>;
  if (!exec) return null;
  const f = exec.financial, c = exec.controls, k = exec.compliance, o = exec.operations, close = exec.periodClose;
  const ageingRows = (label: string, a: Ageing) => [{ label, ...a }];
  return (
    <div className="mt-10" aria-label="Executive dashboard">
      <h2 className="text-xl font-bold mb-1">Cash, working capital and control health</h2>
      <p className="text-slate-500 mb-4 text-sm">As at {fmtDate(exec.asOf)}. Select a tile's link to go straight to the screen that resolves it.</p>

      <h3 className="font-semibold mb-2">Financial position</h3>
      <div className="grid grid-cols-2 md:grid-cols-5 gap-4 mb-4">
        <Stat label="Cash at bank" value={fmtMoney(f.cash)} /><Stat label="Receivables" value={fmtMoney(f.receivables)} hint={f.daysSalesOutstanding != null ? `${f.daysSalesOutstanding} days sales outstanding` : undefined} />
        <Stat label="Payables" value={fmtMoney(f.payables)} hint={f.daysPayablesOutstanding != null ? `${f.daysPayablesOutstanding} days payables outstanding` : undefined} />
        <Stat label="Net working capital" value={fmtMoney(f.netWorkingCapital)} tone={f.netWorkingCapital < 0 ? "bad" : "good"} />
        <Stat label="VAT payable this month" value={fmtMoney(k.vatOutputThisMonth - k.vatInputThisMonth)} hint={`Withholding tax this month ${fmtMoney(k.withholdingThisMonth)}`} />
      </div>
      <div className="grid md:grid-cols-2 gap-4 mb-8">
        {([["Receivables ageing", f.receivablesAgeing, "/receivables-controls"], ["Payables ageing", f.payablesAgeing, "/payables-controls"]] as const).map(([title, a, href]) => (
          <Section key={title} title={title}>
            <DataTable caption={title} rowKey={(r) => r.label} rows={ageingRows("Outstanding", a as Ageing)} columns={[
              { header: "Current", align: "right", cell: (r) => fmtMoney(r.current) }, { header: "1-30", align: "right", cell: (r) => fmtMoney(r.days1to30) }, { header: "31-60", align: "right", cell: (r) => fmtMoney(r.days31to60) },
              { header: "61-90", align: "right", cell: (r) => fmtMoney(r.days61to90) }, { header: "Over 90", align: "right", cell: (r) => fmtMoney(r.over90) }]} />
            <Link className="text-sm text-brand-700 underline" href={href}>Open controls</Link>
          </Section>))}
      </div>

      <h3 className="font-semibold mb-2">Controls</h3>
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-4">
        <Stat label="Open exceptions" value={c.openExceptions} tone={c.critical ? "bad" : c.high ? "warn" : "good"} hint={`${c.critical} critical, ${c.high} high, ${c.overdue} overdue`} />
        <Stat label="Integrity tests failing" value={c.selfTestsFailing} tone={c.selfTestsFailing ? "bad" : "good"} />
        <Stat label="Sub-ledgers unreconciled" value={c.unreconciledAreas} tone={c.unreconciledAreas ? "bad" : "good"} />
        <Stat label="Approvals waiting" value={c.pendingJournalApprovals + c.paymentRunsAwaitingAuthorisation + c.supplierBankChangesPending} hint={`${c.pendingJournalApprovals} journals, ${c.paymentRunsAwaitingAuthorisation} payment runs, ${c.supplierBankChangesPending} bank changes`} />
      </div>
      <div className="flex flex-wrap gap-4 text-sm mb-8">
        <Link className="text-brand-700 underline" href="/audit">Audit workspace</Link><Link className="text-brand-700 underline" href="/reconciliation">Reconciliation</Link>
        <Link className="text-brand-700 underline" href="/governance">Governance</Link>
        {c.emergencyAccessUnreviewed > 0 && <span className="text-red-700 font-medium">{c.emergencyAccessUnreviewed} emergency access grant(s) not yet reviewed</span>}
      </div>

      <div className="grid md:grid-cols-2 gap-4">
        <Section title="Period close">
          {close ? <p className="text-sm">Period {close.period} ends {fmtDate(close.endDate)}: {close.done} of {close.tasks} checklist steps done{close.blocked ? `, ${close.blocked} blocked` : ""}. <Link className="text-brand-700 underline" href="/period-close">Open checklist</Link></p> : <p className="text-sm text-slate-500">No open period.</p>}
        </Section>
        <Section title="Integrations and jobs">
          <p className="text-sm">{o.integrationPending} message(s) pending, <span className={o.integrationDeadLetter ? "text-red-700 font-medium" : ""}>{o.integrationDeadLetter} dead-lettered</span>, {o.backgroundJobFailures7d} job failure(s) in 7 days. <Link className="text-brand-700 underline" href="/integrations">Open</Link></p>
        </Section>
      </div>
    </div>
  );
}
