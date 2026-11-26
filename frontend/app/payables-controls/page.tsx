"use client";

import { useState } from "react";
import AppShell from "@/components/AppShell";
import { AccountSelect, SupplierSelect } from "@/components/Pickers";
import { Badge, DataTable, Loading, Notice, PageHeader, Panel, Section, Tabs, TextField } from "@/components/ui";
import { api } from "@/lib/api";
import { fmtDate, fmtMoney, today } from "@/lib/format";
import { useAction, useLoad } from "@/lib/hooks";

type Row = Record<string, any>;
const TABS = [{ id: "runs", label: "Payment runs" }, { id: "bank", label: "Supplier bank changes" }, { id: "match", label: "Three-way match" }, { id: "statement", label: "Supplier statement" }, { id: "wht", label: "Withholding tax" }] as const;
type TabId = (typeof TABS)[number]["id"];

export default function PayablesControlsPage() {
  const [tab, setTab] = useState<TabId>("runs");
  return (
    <AppShell>
      <PageHeader title="Payables controls" subtitle="Payments are prepared by one person and authorised by another. Bank details change only after a second person verifies them. Invoices must match what was ordered and received before they can be paid." />
      <Tabs tabs={TABS} active={tab} onChange={setTab} />
      <Panel id="runs" active={tab}><Runs /></Panel>
      <Panel id="bank" active={tab}><BankChanges /></Panel>
      <Panel id="match" active={tab}><Match /></Panel>
      <Panel id="statement" active={tab}><Statement /></Panel>
      <Panel id="wht" active={tab}><Wht /></Panel>
    </AppShell>
  );
}

function Runs() {
  const runs = useLoad<Row[]>("/ap/controls/payment-runs", []);
  const invoices = useLoad<Row[]>("/ap/invoices", []);
  const act = useAction();
  const [bank, setBank] = useState(""); const [apAcct, setApAcct] = useState(""); const [date, setDate] = useState(today());
  const [picked, setPicked] = useState<Record<string, boolean>>({});
  const payable = invoices.data.filter((i) => i.outstanding > 0 && i.status !== "Draft" && i.status !== "Cancelled" && i.currency === "USD" && i.matchStatus !== "Discrepancy");
  const chosen = payable.filter((i) => picked[i.id]);
  const total = chosen.reduce((s, i) => s + i.outstanding, 0);
  const call = (id: string, verb: string, msg: string) => act.run(async () => { await api.post(`/ap/controls/payment-runs/${id}/${verb}`); await runs.reload(); await invoices.reload(); }, msg);
  return (
    <>
      <Notice error={act.error ?? runs.error} notice={act.notice} />
      <Section title="Prepare a payment run" description="Select the invoices to pay. The run then needs authorisation from someone with payment-authorisation rights who did not prepare it, and is checked again when released.">
        <div className="grid md:grid-cols-3 gap-4 mb-4">
          <AccountSelect label="Bank account (GL)" required value={bank} onChange={setBank} />
          <AccountSelect label="Trade creditors account (GL)" required value={apAcct} onChange={setApAcct} />
          <TextField label="Payment date" type="date" required value={date} onChange={(e) => setDate(e.target.value)} />
        </div>
        <DataTable caption="Payable invoices" rowKey={(r) => r.id} rows={payable} empty="No payable USD invoices." columns={[
          { header: "Pay", cell: (r) => <input type="checkbox" aria-label={`Include invoice ${r.invoiceNo}`} checked={!!picked[r.id]} onChange={(e) => setPicked({ ...picked, [r.id]: e.target.checked })} /> },
          { header: "Invoice", cell: (r) => r.invoiceNo }, { header: "Supplier", cell: (r) => r.supplier?.name ?? "" }, { header: "Due", cell: (r) => fmtDate(r.dueDate) },
          { header: "Outstanding", align: "right", cell: (r) => fmtMoney(r.outstanding) }, { header: "Match", cell: (r) => r.matchStatus }]} />
        <div className="flex items-center gap-4 mt-4">
          <strong>Total {fmtMoney(total)}</strong>
          <button className="btn-primary" disabled={act.busy || chosen.length === 0 || !bank || !apAcct} onClick={() => act.run(async () => {
            await api.post("/ap/controls/payment-runs", { paymentDate: date, currency: "USD", bankGlAccountId: bank, apControlAccountId: apAcct, items: chosen.map((i) => ({ invoiceId: i.id, amount: i.outstanding })) });
            setPicked({}); await runs.reload(); await invoices.reload();
          }, "Run prepared. Another person must authorise it.")}>Prepare run</button>
        </div>
      </Section>
      <Section title="Payment runs">
        {runs.loading ? <Loading what="runs" /> : <DataTable caption="Payment runs" exportName="payment-runs" rowKey={(r) => r.id} rows={runs.data} columns={[
          { header: "Run", cell: (r) => r.runNo }, { header: "Date", cell: (r) => fmtDate(r.paymentDate), csv: (r) => fmtDate(r.paymentDate) }, { header: "Invoices", align: "right", cell: (r) => (r.items ?? []).length },
          { header: "Total", align: "right", cell: (r) => fmtMoney(r.totalAmount), csv: (r) => r.totalAmount }, { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status },
          { header: "Action", cell: (r) => (
            <div className="flex gap-2">
              {r.status === "Draft" && <button className="btn-primary text-xs" onClick={() => call(r.id, "authorise", "Authorised.")}>Authorise</button>}
              {r.status === "Authorised" && <button className="btn-primary text-xs" onClick={() => call(r.id, "release", "Released: payments posted to the ledger.")}>Release</button>}
              {(r.status === "Draft" || r.status === "Authorised") && <button className="btn-secondary text-xs" onClick={() => call(r.id, "cancel", "Cancelled.")}>Cancel</button>}
            </div>) }]} />}
      </Section>
    </>
  );
}

function BankChanges() {
  const { data, loading, reload } = useLoad<Row[]>("/ap/controls/bank-changes", []);
  const suppliers = useLoad<Row[]>("/ap/suppliers", []);
  const act = useAction();
  const [f, setF] = useState({ supplier: "", bank: "", account: "", branch: "" });
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="Request a bank-detail change" description="Changing where a supplier is paid is the most common route for payment fraud. Payments to the supplier are held until a different person verifies the new details, for example by phoning a number already on file.">
        <form className="grid md:grid-cols-4 gap-4 items-end" onSubmit={(e) => { e.preventDefault(); act.run(async () => { await api.post("/ap/controls/bank-changes", { supplierId: f.supplier, bankName: f.bank, accountNo: f.account, branch: f.branch || null }); await reload(); await suppliers.reload(); }, "Requested. Payments to this supplier are on hold until the details are verified."); }}>
          <SupplierSelect required value={f.supplier} onChange={(v) => setF({ ...f, supplier: v })} />
          <TextField label="Bank" required value={f.bank} onChange={(e) => setF({ ...f, bank: e.target.value })} />
          <TextField label="Account number" required value={f.account} onChange={(e) => setF({ ...f, account: e.target.value })} />
          <TextField label="Branch" value={f.branch} onChange={(e) => setF({ ...f, branch: e.target.value })} />
          <div className="md:col-span-4"><button className="btn-primary" disabled={act.busy}>Request change</button></div>
        </form>
      </Section>
      <Section title="Requests">
        {loading ? <Loading what="requests" /> : <DataTable caption="Bank-detail change requests" exportName="bank-changes" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Supplier", cell: (r) => suppliers.data.find((s) => s.id === r.supplierId)?.name ?? r.supplierId }, { header: "New bank", cell: (r) => r.newBankName },
          { header: "Account", cell: (r) => "****" + String(r.newBankAccountNo).slice(-4) }, { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status }, { header: "Verification", cell: (r) => r.verificationNote ?? "" },
          { header: "Action", cell: (r) => r.status === "Pending" ? (
            <div className="flex gap-2">
              <button className="btn-primary text-xs" onClick={() => { const n = window.prompt("How did you verify these details?"); if (n) act.run(async () => { await api.post(`/ap/controls/bank-changes/${r.id}/verify`, { note: n, approve: true }); await reload(); }, "Verified. Payments can resume."); }}>Verify</button>
              <button className="btn-secondary text-xs" onClick={() => { const n = window.prompt("Why are you rejecting it?"); if (n) act.run(async () => { await api.post(`/ap/controls/bank-changes/${r.id}/verify`, { note: n, approve: false }); await reload(); }, "Rejected. The old details remain."); }}>Reject</button>
            </div>) : null }]} />}
      </Section>
    </>
  );
}

function Match() {
  const invoices = useLoad<Row[]>("/ap/invoices", []);
  const act = useAction();
  const withPo = invoices.data.filter((i) => i.purchaseOrderId);
  const check = (id: string) => act.run(async () => { const r = await api.post(`/ap/controls/invoices/${id}/match`); await invoices.reload(); return r.data; }, (r: any) => `Result: ${r.outcome}.${r.issues?.length ? " " + r.issues.map((i: Row) => i.message).join(" ") : ""}`);
  return (
    <>
      <Notice error={act.error ?? invoices.error} notice={act.notice} />
      <Section title="Invoices matched to purchase orders" description="An invoice is compared with what was ordered and what was received. Differences beyond the tolerance block payment until fixed or released by someone with authority, with a recorded reason.">
        {invoices.loading ? <Loading what="invoices" /> : <DataTable caption="Invoices with a purchase order" exportName="three-way-match" rowKey={(r) => r.id} rows={withPo} empty="No invoices are linked to a purchase order yet." columns={[
          { header: "Invoice", cell: (r) => r.invoiceNo }, { header: "Supplier ref", cell: (r) => r.supplierInvoiceNo ?? "" }, { header: "Total", align: "right", cell: (r) => fmtMoney(r.total), csv: (r) => r.total },
          { header: "Match", cell: (r) => <Badge text={r.matchStatus === "Discrepancy" ? "Blocked" : r.matchStatus === "Matched" || r.matchStatus === "WithinTolerance" ? "Done" : r.matchStatus === "Overridden" ? "Waived" : "Pending"} />, csv: (r) => r.matchStatus }, { header: "Notes", cell: (r) => r.matchNotes ?? "" },
          { header: "Action", cell: (r) => (
            <div className="flex gap-2">
              <button className="btn-secondary text-xs" onClick={() => check(r.id)}>Check now</button>
              {r.matchStatus === "Discrepancy" && <button className="btn-secondary text-xs" onClick={() => { const n = window.prompt("Reason for releasing this discrepancy:"); if (n) act.run(async () => { await api.post(`/ap/controls/invoices/${r.id}/match-override`, { reason: n }); await invoices.reload(); }, "Released for payment; your reason is recorded."); }}>Override</button>}
            </div>) }]} />}
      </Section>
    </>
  );
}

function Statement() {
  const act = useAction();
  const [supplier, setSupplier] = useState(""); const [asOf, setAsOf] = useState(today());
  const [lines, setLines] = useState("INV-001, 1500.00\nINV-002, 820.50");
  const [result, setResult] = useState<Row | null>(null);
  const parse = () => lines.split("\n").map((l) => l.trim()).filter(Boolean).map((l) => { const i = l.lastIndexOf(","); return { reference: l.slice(0, i).trim(), amount: Number(l.slice(i + 1)) }; });
  return (
    <>
      <Notice error={act.error} />
      <Section title="Reconcile a supplier statement" description="Paste the lines from the supplier's statement (reference, amount). The system shows what matches, what the supplier lists that you do not, and what you owe that they have not listed.">
        <form className="grid md:grid-cols-2 gap-4" onSubmit={(e) => { e.preventDefault(); act.run(async () => { const r = await api.post("/ap/controls/statement-reconciliation", { supplierId: supplier, asOf, lines: parse() }); setResult(r.data); }); }}>
          <div className="space-y-4"><SupplierSelect required value={supplier} onChange={setSupplier} /><TextField label="Statement date" type="date" value={asOf} onChange={(e) => setAsOf(e.target.value)} /></div>
          <div><label className="label" htmlFor="st-lines">Statement lines (reference, amount)</label><textarea id="st-lines" className="input font-mono text-sm" rows={5} value={lines} onChange={(e) => setLines(e.target.value)} /></div>
          <div className="md:col-span-2"><button className="btn-primary" disabled={act.busy}>Reconcile</button></div>
        </form>
      </Section>
      {result && (
        <Section title={`Difference ${fmtMoney(result.difference)}`} description={`Statement ${fmtMoney(result.statementTotal)} against ledger ${fmtMoney(result.ledgerTotal)}.`}>
          <h3 className="font-semibold mb-2">Matched</h3>
          <DataTable caption="Matched items" rowKey={(r) => r.reference} rows={(result.matched ?? []) as Row[]} columns={[{ header: "Reference", cell: (r) => r.reference }, { header: "Statement", align: "right", cell: (r) => fmtMoney(r.statementAmount) }, { header: "Ledger", align: "right", cell: (r) => fmtMoney(r.ledgerAmount) }, { header: "Difference", align: "right", cell: (r) => fmtMoney(r.difference) }]} />
          <h3 className="font-semibold mt-5 mb-2">On the statement but not in your ledger</h3>
          <DataTable caption="Only on statement" rowKey={(r) => r.reference} rows={(result.onlyOnStatement ?? []) as Row[]} columns={[{ header: "Reference", cell: (r) => r.reference }, { header: "Amount", align: "right", cell: (r) => fmtMoney(r.amount) }]} />
          <h3 className="font-semibold mt-5 mb-2">In your ledger but not on the statement</h3>
          <DataTable caption="Only in ledger" rowKey={(r) => r.invoiceNo} rows={(result.onlyInLedger ?? []) as Row[]} columns={[{ header: "Invoice", cell: (r) => r.invoiceNo }, { header: "Supplier ref", cell: (r) => r.supplierInvoiceNo ?? "" }, { header: "Outstanding", align: "right", cell: (r) => fmtMoney(r.outstanding) }]} />
        </Section>)}
    </>
  );
}

function Wht() {
  const [from, setFrom] = useState(`${new Date().getUTCFullYear()}-01-01`); const [to, setTo] = useState(today());
  const { data, loading, error } = useLoad<Row[]>(`/ap/controls/withholding-schedule?from=${from}&to=${to}`, []);
  return (
    <Section title="Withholding tax schedule" description="Tax withheld from suppliers in the period: the schedule behind each withholding certificate and the return to the tax authority.">
      <div className="grid sm:grid-cols-2 gap-4 max-w-lg mb-4"><TextField label="From" type="date" value={from} onChange={(e) => setFrom(e.target.value)} /><TextField label="To" type="date" value={to} onChange={(e) => setTo(e.target.value)} /></div>
      <Notice error={error} />
      {loading ? <Loading what="schedule" /> : <DataTable caption="Withholding tax by supplier" exportName="withholding-schedule" rowKey={(r) => r.supplierId} rows={data} empty="No tax was withheld in this period." columns={[
        { header: "Supplier", cell: (r) => r.supplier }, { header: "Taxable amount", align: "right", cell: (r) => fmtMoney(r.taxableAmount), csv: (r) => r.taxableAmount }, { header: "Tax withheld", align: "right", cell: (r) => fmtMoney(r.withheldAmount), csv: (r) => r.withheldAmount }, { header: "Invoices", cell: (r) => (r.invoices ?? []).join(", "), csv: (r) => (r.invoices ?? []).join(" ") }]} />}
    </Section>
  );
}
