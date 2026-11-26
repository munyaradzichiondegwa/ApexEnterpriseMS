"use client";

import { useState } from "react";
import AppShell from "@/components/AppShell";
import { BankAccountSelect, AccountSelect } from "@/components/Pickers";
import { Badge, DataTable, Loading, Notice, PageHeader, Panel, Section, Stat, Tabs, TextField } from "@/components/ui";
import { api } from "@/lib/api";
import { fmtDate, fmtMoney, today } from "@/lib/format";
import { useAction, useLoad } from "@/lib/hooks";

type Row = Record<string, any>;
const TABS = [{ id: "subledgers", label: "Sub-ledgers to GL" }, { id: "bank", label: "Bank certification" }, { id: "petty", label: "Petty cash" }, { id: "forecast", label: "Cash forecast" }] as const;
type TabId = (typeof TABS)[number]["id"];
const AREAS = ["AP", "AR", "Inventory", "FixedAssets", "Payroll", "Bank"];

export default function ReconciliationPage() {
  const [tab, setTab] = useState<TabId>("subledgers");
  return (
    <AppShell>
      <PageHeader title="Reconciliation workspace" subtitle="Prove that every sub-ledger agrees with the general ledger and every bank account agrees with its statement. A difference is never hidden: it must be explained, then signed off by someone other than the person who ran it." />
      <Tabs tabs={TABS} active={tab} onChange={setTab} />
      <Panel id="subledgers" active={tab}><SubLedgers /></Panel>
      <Panel id="bank" active={tab}><BankCertification /></Panel>
      <Panel id="petty" active={tab}><PettyCash /></Panel>
      <Panel id="forecast" active={tab}><Forecast /></Panel>
    </AppShell>
  );
}

function SubLedgers() {
  const { data, loading, reload } = useLoad<Row[]>("/controls/ops/reconciliations", []);
  const act = useAction();
  const [asOf, setAsOf] = useState(today());
  // The most recent run per area is the current position.
  const latest = AREAS.map((a) => data.find((r) => r.area === a));
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Run reconciliations" description="Compares each sub-ledger with its control accounts in the general ledger as at the chosen date.">
        <div className="flex flex-wrap items-end gap-3">
          <TextField label="As at" type="date" value={asOf} onChange={(e) => setAsOf(e.target.value)} />
          <button className="btn-primary" disabled={act.busy} onClick={() => act.run(async () => { await api.post("/controls/ops/reconciliations/run", { asOf }); await reload(); }, "All areas reconciled to the ledger.")}>Run all areas</button>
        </div>
      </Section>
      <div className="grid sm:grid-cols-2 lg:grid-cols-3 gap-4 mb-6">
        {AREAS.map((a, i) => { const r = latest[i]; return (
          <Stat key={a} label={a} value={r ? <Badge text={r.status} /> : "Not run"} tone={r?.status === "Unreconciled" ? "bad" : r ? "good" : "neutral"}
            hint={r ? `Difference ${fmtMoney(r.difference)} as at ${fmtDate(r.asOfDate)}` : "Run the reconciliation to see the position."} />); })}
      </div>
      <Section title="History">
        {loading ? <Loading what="reconciliations" /> : <DataTable caption="Reconciliation runs" exportName="reconciliations" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Area", cell: (r) => r.area }, { header: "As at", cell: (r) => fmtDate(r.asOfDate), csv: (r) => fmtDate(r.asOfDate) },
          { header: "Sub-ledger", align: "right", cell: (r) => fmtMoney(r.subledgerTotal), csv: (r) => r.subledgerTotal }, { header: "General ledger", align: "right", cell: (r) => fmtMoney(r.glTotal), csv: (r) => r.glTotal },
          { header: "Difference", align: "right", cell: (r) => fmtMoney(r.difference), csv: (r) => r.difference }, { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status },
          { header: "Action", cell: (r) => (
            <div className="flex gap-2">
              {r.status === "Unreconciled" && <button className="btn-secondary text-xs" onClick={() => { const t = window.prompt("Explain the difference (at least 15 characters):"); if (t) act.run(async () => { await api.post(`/controls/ops/reconciliations/${r.id}/explain`, { explanation: t }); await reload(); }, "Explanation recorded."); }}>Explain</button>}
              {(r.status === "Reconciled" || r.status === "Explained") && <button className="btn-primary text-xs" onClick={() => act.run(async () => { await api.post(`/controls/ops/reconciliations/${r.id}/sign-off`); await reload(); }, "Signed off.")}>Sign off</button>}
            </div>) }]} />}
      </Section>
    </>
  );
}

function BankCertification() {
  const { data, loading, reload } = useLoad<Row[]>("/controls/ops/bank-reconciliations", []);
  const act = useAction();
  const [f, setF] = useState({ bank: "", date: today(), balance: "", notes: "" });
  const submit = (e: React.FormEvent) => { e.preventDefault(); act.run(async () => {
    await api.post("/controls/ops/bank-reconciliations", { bankAccountId: f.bank, statementDate: f.date, statementBalance: Number(f.balance), notes: f.notes || null });
    await reload();
  }, "Reconciliation prepared. A second person must certify it."); };
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Prepare a bank reconciliation" description="Enter the closing balance from the bank statement. The system compares it with the ledger balance plus statement items not yet matched to the books.">
        <form onSubmit={submit} className="grid md:grid-cols-4 gap-4 items-end">
          <BankAccountSelect required value={f.bank} onChange={(v) => setF({ ...f, bank: v })} />
          <TextField label="Statement date" type="date" required value={f.date} onChange={(e) => setF({ ...f, date: e.target.value })} />
          <TextField label="Statement closing balance" type="number" step="0.01" required value={f.balance} onChange={(e) => setF({ ...f, balance: e.target.value })} />
          <TextField label="Notes" value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} />
          <div className="md:col-span-4"><button className="btn-primary" disabled={act.busy}>Prepare</button></div>
        </form>
      </Section>
      <Section title="Bank reconciliations">
        {loading ? <Loading what="reconciliations" /> : <DataTable caption="Bank reconciliations" exportName="bank-reconciliations" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Statement date", cell: (r) => fmtDate(r.statementDate), csv: (r) => fmtDate(r.statementDate) }, { header: "Statement", align: "right", cell: (r) => fmtMoney(r.statementBalance), csv: (r) => r.statementBalance },
          { header: "Book", align: "right", cell: (r) => fmtMoney(r.bookBalance), csv: (r) => r.bookBalance }, { header: "Unmatched items", align: "right", cell: (r) => fmtMoney(r.unreconciledItemsTotal), csv: (r) => r.unreconciledItemsTotal },
          { header: "Difference", align: "right", cell: (r) => fmtMoney(r.difference), csv: (r) => r.difference }, { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status },
          { header: "Action", cell: (r) => r.status === "Draft" ? <button className="btn-primary text-xs" onClick={() => { const n = Math.abs(r.difference) > 0.005 ? window.prompt(`Unexplained difference of ${fmtMoney(r.difference)}. Reviewer's explanation:`) : ""; if (n !== null) act.run(async () => { await api.post(`/controls/ops/bank-reconciliations/${r.id}/certify`, { reviewerNotes: n || null }); await reload(); }, "Certified."); }}>Certify</button> : null }]} />}
      </Section>
    </>
  );
}

function PettyCash() {
  const funds = useLoad<Row[]>("/controls/ops/petty-cash/funds", []);
  const vouchers = useLoad<Row[]>("/controls/ops/petty-cash/vouchers", []);
  const act = useAction();
  const [fund, setFund] = useState({ name: "", account: "", imprest: "" });
  const [v, setV] = useState({ fund: "", date: today(), amount: "", expense: "", description: "" });
  const reload = async () => { await funds.reload(); await vouchers.reload(); };
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Petty cash funds" description="An imprest fund holds a fixed float. Vouchers are approved by someone other than the person who raised them, and the fund is topped back up to its float from the bank.">
        <form className="grid md:grid-cols-4 gap-4 items-end mb-4" onSubmit={(e) => { e.preventDefault(); act.run(async () => { await api.post("/controls/ops/petty-cash/funds", { name: fund.name, glAccountId: fund.account, imprestAmount: Number(fund.imprest) }); await reload(); }, "Fund created."); }}>
          <TextField label="Fund name" required value={fund.name} onChange={(e) => setFund({ ...fund, name: e.target.value })} />
          <AccountSelect label="Cash account" required value={fund.account} onChange={(x) => setFund({ ...fund, account: x })} />
          <TextField label="Float (imprest)" type="number" step="0.01" min="0.01" required value={fund.imprest} onChange={(e) => setFund({ ...fund, imprest: e.target.value })} />
          <button className="btn-primary" disabled={act.busy}>Create fund</button>
        </form>
        <DataTable caption="Petty cash funds" rowKey={(r) => r.id} rows={funds.data} columns={[{ header: "Fund", cell: (r) => r.name }, { header: "Float", align: "right", cell: (r) => fmtMoney(r.imprestAmount) },
          { header: "Action", cell: (r) => <button className="btn-secondary text-xs" onClick={() => { const bank = window.prompt("Bank GL account id to draw the top-up from:"); if (bank) act.run(async () => { await api.post(`/controls/ops/petty-cash/funds/${r.id}/replenish`, { bankGlAccountId: bank }); await reload(); }, "Fund replenished to its float."); }}>Replenish</button> }]} />
      </Section>
      <Section title="Vouchers">
        <form className="grid md:grid-cols-5 gap-4 items-end mb-4" onSubmit={(e) => { e.preventDefault(); act.run(async () => { await api.post("/controls/ops/petty-cash/vouchers", { pettyCashFundId: v.fund, voucherDate: v.date, amount: Number(v.amount), expenseAccountId: v.expense, description: v.description }); await reload(); }, "Voucher raised. A second person must approve it."); }}>
          <div><label className="label" htmlFor="pc-fund">Fund</label><select id="pc-fund" className="input" required value={v.fund} onChange={(e) => setV({ ...v, fund: e.target.value })}><option value="">Select…</option>{funds.data.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}</select></div>
          <TextField label="Date" type="date" required value={v.date} onChange={(e) => setV({ ...v, date: e.target.value })} />
          <TextField label="Amount" type="number" step="0.01" min="0.01" required value={v.amount} onChange={(e) => setV({ ...v, amount: e.target.value })} />
          <AccountSelect label="Expense account" required value={v.expense} onChange={(x) => setV({ ...v, expense: x })} />
          <TextField label="What was it for?" required value={v.description} onChange={(e) => setV({ ...v, description: e.target.value })} />
          <div className="md:col-span-5"><button className="btn-primary" disabled={act.busy}>Raise voucher</button></div>
        </form>
        <DataTable caption="Petty cash vouchers" exportName="petty-cash-vouchers" rowKey={(r) => r.id} rows={vouchers.data} columns={[
          { header: "Date", cell: (r) => fmtDate(r.voucherDate), csv: (r) => fmtDate(r.voucherDate) }, { header: "Description", cell: (r) => r.description }, { header: "Amount", align: "right", cell: (r) => fmtMoney(r.amount), csv: (r) => r.amount },
          { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status },
          { header: "Action", cell: (r) => r.status === "Draft" ? <button className="btn-primary text-xs" onClick={() => act.run(async () => { await api.post(`/controls/ops/petty-cash/vouchers/${r.id}/approve`); await reload(); }, "Approved and posted.")}>Approve and post</button> : null }]} />
      </Section>
    </>
  );
}

function Forecast() {
  const [weeks, setWeeks] = useState("13");
  const { data, loading, error } = useLoad<Row | null>(`/controls/ops/cash-forecast?weeks=${encodeURIComponent(weeks)}`, null);
  return (
    <Section title="Cash forecast" description="Receipts from open invoices and payments to suppliers by due date. Overdue items fall in week 1. It does not include payroll, tax or other unscheduled flows.">
      <div className="max-w-xs mb-4"><TextField label="Weeks ahead" type="number" min="1" max="26" value={weeks} onChange={(e) => setWeeks(e.target.value)} /></div>
      <Notice error={error} />
      {loading || !data ? <Loading what="forecast" /> : (
        <>
          <div className="grid sm:grid-cols-3 gap-4 mb-4">
            <Stat label="Opening cash" value={fmtMoney(data.openingCash)} />
            <Stat label="Lowest closing balance" value={fmtMoney(data.lowestClosing)} tone={data.lowestClosing < 0 ? "bad" : "good"} hint={data.lowestClosing < 0 ? "Cash is forecast to run out." : "Stays positive."} />
          </div>
          <DataTable caption="Weekly cash forecast" exportName="cash-forecast" rowKey={(r) => String(r.week)} rows={(data.weeks ?? []) as Row[]} columns={[
            { header: "Week", cell: (r) => r.week }, { header: "Receipts", align: "right", cell: (r) => fmtMoney(r.inflow), csv: (r) => r.inflow }, { header: "Payments", align: "right", cell: (r) => fmtMoney(r.outflow), csv: (r) => r.outflow },
            { header: "Net", align: "right", cell: (r) => fmtMoney(r.net), csv: (r) => r.net }, { header: "Closing", align: "right", cell: (r) => fmtMoney(r.closing), csv: (r) => r.closing }]} />
        </>)}
    </Section>
  );
}
