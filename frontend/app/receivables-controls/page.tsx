"use client";

import { useState } from "react";
import AppShell from "@/components/AppShell";
import { AccountSelect, CustomerSelect } from "@/components/Pickers";
import { Badge, DataTable, Loading, Notice, PageHeader, Panel, Section, Tabs, TextField } from "@/components/ui";
import { api } from "@/lib/api";
import { fmtDate, fmtMoney, today } from "@/lib/format";
import { useAction, useLoad } from "@/lib/hooks";

type Row = Record<string, any>;
const TABS = [{ id: "credit", label: "Credit limits" }, { id: "notes", label: "Credit notes" }, { id: "ecl", label: "Expected credit loss" }, { id: "statement", label: "Customer statement" }] as const;
type TabId = (typeof TABS)[number]["id"];

export default function ReceivablesControlsPage() {
  const [tab, setTab] = useState<TabId>("credit");
  return (
    <AppShell>
      <PageHeader title="Receivables controls" subtitle="Credit limits that stop a sale, credit notes that need approval, the allowance for expected credit losses, and statements to send to customers." />
      <Tabs tabs={TABS} active={tab} onChange={setTab} />
      <Panel id="credit" active={tab}><Credit /></Panel>
      <Panel id="notes" active={tab}><Notes /></Panel>
      <Panel id="ecl" active={tab}><Ecl /></Panel>
      <Panel id="statement" active={tab}><CustomerStatement /></Panel>
    </AppShell>
  );
}

function Credit() {
  const act = useAction();
  const [f, setF] = useState({ customer: "", limit: "", reason: "", until: "" });
  const [last, setLast] = useState<Row | null>(null);
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Temporary credit-limit override" description="A sale that would take a customer over their limit is refused when the invoice is posted. To allow it, request a temporary higher limit with a reason and an end date; a second person approves it.">
        <form className="grid md:grid-cols-4 gap-4 items-end" onSubmit={(e) => { e.preventDefault(); act.run(async () => { const r = await api.post("/ar/controls/credit-overrides", { customerId: f.customer, temporaryLimit: Number(f.limit), reason: f.reason, expiresAt: f.until }); setLast(r.data); }, "Requested. A second person must approve it."); }}>
          <CustomerSelect required value={f.customer} onChange={(v) => setF({ ...f, customer: v })} />
          <TextField label="Temporary limit" type="number" step="0.01" min="0.01" required value={f.limit} onChange={(e) => setF({ ...f, limit: e.target.value })} />
          <TextField label="Valid until" type="date" required value={f.until} onChange={(e) => setF({ ...f, until: e.target.value })} />
          <TextField label="Reason" required value={f.reason} onChange={(e) => setF({ ...f, reason: e.target.value })} />
          <div className="md:col-span-4"><button className="btn-primary" disabled={act.busy}>Request override</button></div>
        </form>
        {last && !last.approvedBy && <div className="mt-4"><button className="btn-primary" onClick={() => act.run(async () => { const r = await api.post(`/ar/controls/credit-overrides/${last.id}/approve`); setLast(r.data); }, "Approved.")}>Approve the override just requested</button></div>}
      </Section>
    </>
  );
}

function Notes() {
  const { data, loading, reload } = useLoad<Row[]>("/ar/controls/credit-notes", []);
  const invoices = useLoad<Row[]>("/ar/invoices", []);
  const act = useAction();
  const [f, setF] = useState({ invoice: "", amount: "", reason: "", revenue: "", ar: "", vat: "" });
  const open = invoices.data.filter((i) => i.outstanding > 0 && i.status !== "Draft" && i.status !== "Cancelled");
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Raise a credit note" description="Reduces what a customer owes. It reverses the revenue (and VAT) and the receivable only after a second person approves it.">
        <form className="grid md:grid-cols-3 gap-4" onSubmit={(e) => { e.preventDefault(); act.run(async () => { await api.post("/ar/controls/credit-notes", { arInvoiceId: f.invoice, amount: Number(f.amount), reason: f.reason, revenueReversalAccountId: f.revenue, arControlAccountId: f.ar, vatOutputAccountId: f.vat || null }); await reload(); await invoices.reload(); }, "Credit note drafted."); }}>
          <div><label className="label" htmlFor="cn-inv">Invoice</label><select id="cn-inv" className="input" required value={f.invoice} onChange={(e) => setF({ ...f, invoice: e.target.value })}><option value="">Select…</option>{open.map((i) => <option key={i.id} value={i.id}>{i.invoiceNo} - {i.customer?.name} (owes {fmtMoney(i.outstanding)})</option>)}</select></div>
          <TextField label="Credit amount" type="number" step="0.01" min="0.01" required value={f.amount} onChange={(e) => setF({ ...f, amount: e.target.value })} />
          <TextField label="Reason" required value={f.reason} onChange={(e) => setF({ ...f, reason: e.target.value })} />
          <AccountSelect label="Revenue (or sales returns) account" required value={f.revenue} onChange={(v) => setF({ ...f, revenue: v })} />
          <AccountSelect label="Trade debtors account" required value={f.ar} onChange={(v) => setF({ ...f, ar: v })} />
          <AccountSelect label="VAT output account" value={f.vat} onChange={(v) => setF({ ...f, vat: v })} hint="Required if the invoice carries VAT." />
          <div className="md:col-span-3"><button className="btn-primary" disabled={act.busy}>Draft credit note</button></div>
        </form>
      </Section>
      <Section title="Credit notes">
        {loading ? <Loading what="credit notes" /> : <DataTable caption="Credit notes" exportName="credit-notes" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Number", cell: (r) => r.creditNoteNo }, { header: "Amount", align: "right", cell: (r) => fmtMoney(r.amount), csv: (r) => r.amount }, { header: "Reason", cell: (r) => r.reason }, { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status },
          { header: "Action", cell: (r) => (
            <div className="flex gap-2">
              {r.status === "Draft" && <button className="btn-secondary text-xs" onClick={() => act.run(async () => { await api.post(`/ar/controls/credit-notes/${r.id}/approve`); await reload(); }, "Approved.")}>Approve</button>}
              {r.status === "Approved" && <button className="btn-primary text-xs" onClick={() => act.run(async () => { await api.post(`/ar/controls/credit-notes/${r.id}/post`); await reload(); await invoices.reload(); }, "Posted to the ledger.")}>Post</button>}
            </div>) }]} />}
      </Section>
    </>
  );
}

function Ecl() {
  const { data, loading, reload } = useLoad<Row[]>("/ar/controls/ecl/runs", []);
  const act = useAction();
  const [rates, setRates] = useState("0-30=1\n31-60=3\n61-90=10\n91-180=30\n181-3650=60");
  const [f, setF] = useState({ asOf: today(), expense: "", allowance: "" });
  const parse = () => rates.split("\n").map((l) => l.trim()).filter(Boolean).map((l) => { const [range, pct] = l.split("="); const [a, b] = range.split("-"); return { fromDays: Number(a), toDays: Number(b), lossRatePct: Number(pct) }; });
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Loss-rate matrix" description="The percentage of receivables expected to be lost, by how many days they are overdue. One line per band: 'from-to=percent'. Bands must start at 0 and be continuous. Set these from your own credit-loss history; the lines shown are only an example.">
        <label className="label" htmlFor="ecl-rates">Bands</label>
        <textarea id="ecl-rates" className="input font-mono text-sm" rows={5} value={rates} onChange={(e) => setRates(e.target.value)} />
        <button className="btn-secondary mt-3" disabled={act.busy} onClick={() => act.run(async () => { await api.put("/ar/controls/ecl/loss-rates", { buckets: parse() }); }, "Loss-rate matrix saved.")}>Save matrix</button>
      </Section>
      <Section title="Calculate the allowance" description="Applies the matrix to open invoices. The movement against the last posted allowance is booked as bad-debt expense (increase) or released (decrease) once a second person approves.">
        <form className="grid md:grid-cols-4 gap-4 items-end" onSubmit={(e) => { e.preventDefault(); act.run(async () => { await api.post("/ar/controls/ecl/calculate", { asOf: f.asOf, badDebtExpenseAccountId: f.expense, allowanceAccountId: f.allowance }); await reload(); }, "Calculated as a draft."); }}>
          <TextField label="As at" type="date" required value={f.asOf} onChange={(e) => setF({ ...f, asOf: e.target.value })} />
          <AccountSelect label="Bad-debt expense account" required value={f.expense} onChange={(v) => setF({ ...f, expense: v })} />
          <AccountSelect label="Allowance (contra-receivable) account" required value={f.allowance} onChange={(v) => setF({ ...f, allowance: v })} />
          <button className="btn-primary" disabled={act.busy}>Calculate</button>
        </form>
      </Section>
      <Section title="Provision runs">
        {loading ? <Loading what="runs" /> : <DataTable caption="Expected credit loss runs" exportName="ecl-runs" rowKey={(r) => r.id} rows={data} columns={[
          { header: "As at", cell: (r) => fmtDate(r.asOfDate), csv: (r) => fmtDate(r.asOfDate) }, { header: "Receivables", align: "right", cell: (r) => fmtMoney(r.totalReceivables), csv: (r) => r.totalReceivables },
          { header: "Required allowance", align: "right", cell: (r) => fmtMoney(r.requiredAllowance), csv: (r) => r.requiredAllowance }, { header: "Movement", align: "right", cell: (r) => fmtMoney(r.movement), csv: (r) => r.movement },
          { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status },
          { header: "Action", cell: (r) => (
            <div className="flex gap-2">
              {r.status === "Draft" && <button className="btn-secondary text-xs" onClick={() => act.run(async () => { await api.post(`/ar/controls/ecl/runs/${r.id}/approve`); await reload(); }, "Approved.")}>Approve</button>}
              {r.status === "Approved" && <button className="btn-primary text-xs" onClick={() => act.run(async () => { await api.post(`/ar/controls/ecl/runs/${r.id}/post`); await reload(); }, "Posted to the ledger.")}>Post</button>}
            </div>) }]} />}
      </Section>
    </>
  );
}

function CustomerStatement() {
  const act = useAction();
  const [customer, setCustomer] = useState(""); const [asOf, setAsOf] = useState(today());
  const [data, setData] = useState<Row | null>(null);
  return (
    <>
      <Notice error={act.error} />
      <Section title="Customer statement" description="Open invoices with how overdue each one is. Print it or export it to send to the customer.">
        <form className="grid md:grid-cols-3 gap-4 items-end" onSubmit={(e) => { e.preventDefault(); act.run(async () => { const r = await api.get(`/ar/controls/statement/${customer}`, { params: { asOf } }); setData(r.data); }); }}>
          <CustomerSelect required value={customer} onChange={setCustomer} />
          <TextField label="As at" type="date" value={asOf} onChange={(e) => setAsOf(e.target.value)} />
          <button className="btn-primary" disabled={act.busy}>Show statement</button>
        </form>
        {data && (
          <div className="mt-4">
            <p className="text-sm mb-2"><strong>{data.customer?.name}</strong> as at {fmtDate(data.asOf)}. Credit limit {fmtMoney(data.creditLimit)}. Total outstanding <strong>{fmtMoney(data.totalOutstanding)}</strong>.</p>
            <DataTable caption="Open invoices" exportName="customer-statement" rowKey={(r) => r.invoiceNo} rows={(data.items ?? []) as Row[]} empty="Nothing outstanding." columns={[
              { header: "Invoice", cell: (r) => r.invoiceNo }, { header: "Date", cell: (r) => fmtDate(r.invoiceDate) }, { header: "Due", cell: (r) => fmtDate(r.dueDate) }, { header: "Total", align: "right", cell: (r) => fmtMoney(r.total), csv: (r) => r.total },
              { header: "Outstanding", align: "right", cell: (r) => fmtMoney(r.outstanding), csv: (r) => r.outstanding }, { header: "Days overdue", align: "right", cell: (r) => r.daysOverdue }]} />
            <button className="btn-secondary mt-3 print:hidden" onClick={() => window.print()}>Print</button>
          </div>)}
      </Section>
    </>
  );
}
