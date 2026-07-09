"use client";

import { useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

type Tab = "tb" | "is" | "bs" | "equity" | "cashflow" | "notes";

const TABS: { key: Tab; label: string }[] = [
  { key: "bs", label: "1. Financial Position" },
  { key: "is", label: "2. Profit or Loss & OCI" },
  { key: "equity", label: "3. Changes in Equity" },
  { key: "cashflow", label: "4. Cash Flows" },
  { key: "notes", label: "5. Notes" },
  { key: "tb", label: "Trial Balance" },
];

export default function ReportsPage() {
  const [tab, setTab] = useState<Tab>("bs");
  const [trialBalance, setTrialBalance] = useState<any>(null);
  const [incomeStatement, setIncomeStatement] = useState<any>(null);
  const [balanceSheet, setBalanceSheet] = useState<any>(null);
  const [equity, setEquity] = useState<any>(null);
  const [cashFlow, setCashFlow] = useState<any>(null);
  const [notes, setNotes] = useState<any>(null);
  const [error, setError] = useState<string | null>(null);
  const now = new Date();
  const [range, setRange] = useState({ from: new Date(now.getFullYear(), 0, 1).toISOString().slice(0, 10), to: now.toISOString().slice(0, 10) });

  const fmt = (n: number) => (n ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  async function loadTb() { try { setTrialBalance((await api.get(`/reports/trial-balance?asOf=${range.to}`)).data); } catch (e) { setError(extractErrorMessage(e)); } }
  async function loadIs() { try { setIncomeStatement((await api.get(`/reports/income-statement?from=${range.from}&to=${range.to}`)).data); } catch (e) { setError(extractErrorMessage(e)); } }
  async function loadBs() { try { setBalanceSheet((await api.get(`/reports/balance-sheet?asOf=${range.to}`)).data); } catch (e) { setError(extractErrorMessage(e)); } }
  async function loadEquity() { try { setEquity((await api.get(`/reports/statement-of-changes-in-equity?from=${range.from}&to=${range.to}`)).data); } catch (e) { setError(extractErrorMessage(e)); } }
  async function loadCashFlow() { try { setCashFlow((await api.get(`/reports/cash-flow-statement?from=${range.from}&to=${range.to}`)).data); } catch (e) { setError(extractErrorMessage(e)); } }
  async function loadNotes() { try { setNotes((await api.get(`/reports/notes?from=${range.from}&to=${range.to}`)).data); } catch (e) { setError(extractErrorMessage(e)); } }

  function selectTab(t: Tab) {
    setTab(t);
    setError(null);
    if (t === "tb") loadTb();
    if (t === "is") loadIs();
    if (t === "bs") loadBs();
    if (t === "equity") loadEquity();
    if (t === "cashflow") loadCashFlow();
    if (t === "notes") loadNotes();
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Reporting & BI</h1>
      <p className="text-slate-500 mb-6">The five Necessary Financial Reports, computed live from posted journals — not templates.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      <div className="card mb-6 flex flex-wrap items-end gap-3">
        <div><label className="label">From</label><input className="input" type="date" value={range.from} onChange={(e) => setRange({ ...range, from: e.target.value })} /></div>
        <div><label className="label">To / As Of</label><input className="input" type="date" value={range.to} onChange={(e) => setRange({ ...range, to: e.target.value })} /></div>
        <button className="btn-primary" onClick={() => selectTab(tab)}>Refresh</button>
      </div>

      <div className="flex flex-wrap gap-2 mb-6">
        {TABS.map((t) => (
          <button key={t.key} className={tab === t.key ? "btn-primary" : "btn-secondary"} onClick={() => selectTab(t.key)}>{t.label}</button>
        ))}
      </div>

      {tab === "tb" && trialBalance && (
        <div className="card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>Code</th><th>Account</th><th>Debit</th><th>Credit</th></tr></thead>
            <tbody>
              {trialBalance.lines.map((l: any, idx: number) => (
                <tr key={idx}><td>{l.code}</td><td>{l.name}</td><td className="text-right">{fmt(l.debit)}</td><td className="text-right">{fmt(l.credit)}</td></tr>
              ))}
              <tr className="font-semibold"><td colSpan={2}>Total</td><td className="text-right">{fmt(trialBalance.totalDebit)}</td><td className="text-right">{fmt(trialBalance.totalCredit)}</td></tr>
            </tbody>
          </table>
        </div>
      )}

      {tab === "is" && incomeStatement && (
        <div className="card">
          <h3 className="font-semibold mb-2">Income</h3>
          <table className="table-base mb-4"><tbody>{incomeStatement.incomeLines.map((l: any, i: number) => (<tr key={i}><td>{l.name}</td><td className="text-right">{fmt(l.credit)}</td></tr>))}</tbody></table>
          <h3 className="font-semibold mb-2">Expenses</h3>
          <table className="table-base mb-4"><tbody>{incomeStatement.expenseLines.map((l: any, i: number) => (<tr key={i}><td>{l.name}</td><td className="text-right">{fmt(l.debit)}</td></tr>))}</tbody></table>
          <div className="border-t pt-3 font-semibold flex justify-between"><span>Net Profit for the Period</span><span>{fmt(incomeStatement.netProfit)}</span></div>

          <h3 className="font-semibold mb-2 mt-6">Other Comprehensive Income</h3>
          {incomeStatement.ociLines.length === 0 && <p className="text-sm text-slate-400">No OCI movements this period (e.g. asset revaluations, foreign operation translation).</p>}
          <table className="table-base mb-2"><tbody>{incomeStatement.ociLines.map((l: any, i: number) => (<tr key={i}><td>{l.accountName}</td><td className="text-right">{fmt(l.amount)}</td></tr>))}</tbody></table>
          <div className="border-t pt-3 font-semibold flex justify-between text-brand-700"><span>Total Comprehensive Income</span><span>{fmt(incomeStatement.totalComprehensiveIncome)}</span></div>
        </div>
      )}

      {tab === "bs" && balanceSheet && (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          {[["Assets", balanceSheet.assets, balanceSheet.totalAssets], ["Liabilities", balanceSheet.liabilities, balanceSheet.totalLiabilities], ["Equity", balanceSheet.equity, balanceSheet.totalEquity]].map(([title, lines, total]: any) => (
            <div key={title} className="card">
              <h3 className="font-semibold mb-2">{title}</h3>
              <table className="table-base"><tbody>{lines.map((l: any, i: number) => (<tr key={i}><td>{l.name}</td><td className="text-right">{fmt(l.debit || l.credit)}</td></tr>))}</tbody></table>
              <div className="border-t pt-2 mt-2 font-semibold flex justify-between"><span>Total</span><span>{fmt(total)}</span></div>
            </div>
          ))}
        </div>
      )}

      {tab === "equity" && equity && (
        <div className="card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>Account</th><th>Opening</th><th>Profit</th><th>OCI</th><th>Dividends</th><th>Share Capital</th><th>Other</th><th>Closing</th></tr></thead>
            <tbody>
              {equity.accounts.map((a: any, i: number) => (
                <tr key={i}>
                  <td>{a.accountName}</td><td className="text-right">{fmt(a.openingBalance)}</td><td className="text-right">{fmt(a.profitForPeriod)}</td>
                  <td className="text-right">{fmt(a.ociMovement)}</td><td className="text-right">{fmt(a.dividendsDeclared)}</td>
                  <td className="text-right">{fmt(a.shareCapitalMovement)}</td><td className="text-right">{fmt(a.otherMovement)}</td><td className="text-right font-medium">{fmt(a.closingBalance)}</td>
                </tr>
              ))}
              <tr className="font-semibold"><td>Total</td><td className="text-right">{fmt(equity.totalOpening)}</td><td colSpan={5}></td><td className="text-right">{fmt(equity.totalClosing)}</td></tr>
            </tbody>
          </table>
        </div>
      )}

      {tab === "cashflow" && cashFlow && (
        <div className="card space-y-4">
          <div>
            <h3 className="font-semibold mb-2">Operating Activities</h3>
            <div className="flex justify-between text-sm py-1"><span>Net Profit</span><span>{fmt(cashFlow.netProfit)}</span></div>
            {cashFlow.operatingAdjustments.map((l: any, i: number) => (<div key={i} className="flex justify-between text-sm py-1"><span>{l.label}</span><span>{fmt(l.amount)}</span></div>))}
            <div className="border-t pt-2 mt-1 font-semibold flex justify-between"><span>Net Cash from Operating Activities</span><span>{fmt(cashFlow.netCashFromOperating)}</span></div>
          </div>
          <div>
            <h3 className="font-semibold mb-2">Investing Activities</h3>
            {cashFlow.investingActivities.map((l: any, i: number) => (<div key={i} className="flex justify-between text-sm py-1"><span>{l.label}</span><span>{fmt(l.amount)}</span></div>))}
            <div className="border-t pt-2 mt-1 font-semibold flex justify-between"><span>Net Cash from Investing Activities</span><span>{fmt(cashFlow.netCashFromInvesting)}</span></div>
          </div>
          <div>
            <h3 className="font-semibold mb-2">Financing Activities</h3>
            {cashFlow.financingActivities.map((l: any, i: number) => (<div key={i} className="flex justify-between text-sm py-1"><span>{l.label}</span><span>{fmt(l.amount)}</span></div>))}
            <div className="border-t pt-2 mt-1 font-semibold flex justify-between"><span>Net Cash from Financing Activities</span><span>{fmt(cashFlow.netCashFromFinancing)}</span></div>
          </div>
          <div className="border-t-2 pt-3 space-y-1">
            <div className="flex justify-between font-semibold"><span>Net Increase/(Decrease) in Cash</span><span>{fmt(cashFlow.netIncreaseInCash)}</span></div>
            <div className="flex justify-between text-sm"><span>Cash at Start of Period</span><span>{fmt(cashFlow.cashAtStart)}</span></div>
            <div className="flex justify-between text-sm font-semibold"><span>Cash at End of Period</span><span>{fmt(cashFlow.cashAtEnd)}</span></div>
            {Math.abs(cashFlow.reconciliationDifference) > 0.01 && (
              <div className="text-xs text-amber-600 mt-2">Reconciliation difference: {fmt(cashFlow.reconciliationDifference)} — check for accounts not yet tagged with a CashFlowCategory.</div>
            )}
          </div>
        </div>
      )}

      {tab === "notes" && notes && (
        <div className="space-y-6">
          <div className="card"><h3 className="font-semibold mb-2">1. Basis of Preparation & Accounting Policies</h3><p className="text-sm text-slate-600 leading-relaxed">{notes.accountingPoliciesNote}</p></div>

          <div className="card overflow-x-auto">
            <h3 className="font-semibold mb-2">2. Property, Plant & Equipment</h3>
            <table className="table-base">
              <thead><tr><th>Category</th><th>Opening Cost</th><th>Additions</th><th>Disposals</th><th>Revaluations</th><th>Closing Cost</th><th>Dep. Charge</th><th>Closing Acc. Dep</th><th>Closing NBV</th></tr></thead>
              <tbody>{notes.propertyPlantEquipment.map((p: any, i: number) => (
                <tr key={i}><td>{p.categoryName}</td><td className="text-right">{fmt(p.openingCost)}</td><td className="text-right">{fmt(p.additions)}</td>
                  <td className="text-right">{fmt(p.disposalsAtCost)}</td><td className="text-right">{fmt(p.revaluations)}</td><td className="text-right">{fmt(p.closingCost)}</td>
                  <td className="text-right">{fmt(p.depreciationCharge)}</td><td className="text-right">{fmt(p.closingAccDep)}</td><td className="text-right font-medium">{fmt(p.closingNbv)}</td></tr>
              ))}</tbody>
            </table>
          </div>

          <div className="card overflow-x-auto">
            <h3 className="font-semibold mb-2">3. Inventory</h3>
            <table className="table-base"><thead><tr><th>Item</th><th>Method</th><th>Qty on Hand</th><th>Avg Cost</th><th>Value</th></tr></thead>
              <tbody>{notes.inventory.map((n: any, i: number) => (<tr key={i}><td>{n.itemName}</td><td>{n.valuationMethod}</td><td className="text-right">{n.quantityOnHand}</td><td className="text-right">{n.averageCost.toFixed(4)}</td><td className="text-right">{fmt(n.totalValue)}</td></tr>))}</tbody>
            </table>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div className="card overflow-x-auto">
              <h3 className="font-semibold mb-2">4a. Trade Receivables Ageing</h3>
              <table className="table-base"><thead><tr><th>Customer</th><th>Current</th><th>30d</th><th>60d</th><th>90d+</th></tr></thead>
                <tbody>{notes.tradeReceivablesAgeing.map((r: any, i: number) => (<tr key={i}><td>{r.customerName}</td><td className="text-right">{fmt(r.current)}</td><td className="text-right">{fmt(r.days30)}</td><td className="text-right">{fmt(r.days60)}</td><td className="text-right">{fmt(r.days90Plus)}</td></tr>))}</tbody>
              </table>
            </div>
            <div className="card overflow-x-auto">
              <h3 className="font-semibold mb-2">4b. Trade Payables Ageing</h3>
              <table className="table-base"><thead><tr><th>Supplier</th><th>Current</th><th>30d</th><th>60d</th><th>90d+</th></tr></thead>
                <tbody>{notes.tradePayablesAgeing.map((r: any, i: number) => (<tr key={i}><td>{r.supplierName}</td><td className="text-right">{fmt(r.current)}</td><td className="text-right">{fmt(r.days30)}</td><td className="text-right">{fmt(r.days60)}</td><td className="text-right">{fmt(r.days90Plus)}</td></tr>))}</tbody>
              </table>
            </div>
          </div>

          <div className="card overflow-x-auto">
            <h3 className="font-semibold mb-2">5. Share Capital Movements</h3>
            <table className="table-base"><tbody>{notes.shareCapital.map((s: any, i: number) => (<tr key={i}><td>{new Date(s.date).toLocaleDateString()}</td><td>{s.description}</td><td className="text-right">{fmt(s.amount)}</td></tr>))}</tbody></table>
          </div>

          <div className="card overflow-x-auto">
            <h3 className="font-semibold mb-2">6. Related Party Transactions (Inter-Company)</h3>
            {notes.relatedPartyTransactions.length === 0 && <p className="text-sm text-slate-400">No inter-company transactions this period.</p>}
            <table className="table-base"><thead><tr><th>Group Entity</th><th>Amount</th><th>Direction</th><th>Status</th></tr></thead>
              <tbody>{notes.relatedPartyTransactions.map((r: any, i: number) => (<tr key={i}><td>{r.counterpartyCompanyName}</td><td className="text-right">{fmt(r.amount)}</td><td>{r.direction}</td><td>{r.status}</td></tr>))}</tbody>
            </table>
          </div>

          {notes.segmentReporting.length > 0 && (
            <div className="card overflow-x-auto">
              <h3 className="font-semibold mb-2">7. Segment Reporting (IFRS 8)</h3>
              <table className="table-base"><thead><tr><th>Segment</th><th>Revenue</th><th>Expense</th><th>Result</th></tr></thead>
                <tbody>{notes.segmentReporting.map((s: any, i: number) => (<tr key={i}><td>{s.segmentName}</td><td className="text-right">{fmt(s.revenue)}</td><td className="text-right">{fmt(s.expense)}</td><td className="text-right">{fmt(s.result)}</td></tr>))}</tbody>
              </table>
            </div>
          )}

          {notes.otherDisclosures.length > 0 && (
            <div className="card">
              <h3 className="font-semibold mb-2">Other Disclosures</h3>
              {notes.otherDisclosures.map((d: any) => (<div key={d.id} className="mb-3"><div className="font-medium text-sm">{d.noteTitle}</div><div className="text-sm text-slate-600">{d.noteBody}</div></div>))}
            </div>
          )}
        </div>
      )}
    </AppShell>
  );
}
