"use client";

import { useState } from "react";
import AppShell from "@/components/AppShell";
import { AccountSelect, AssetSelect, CustomerSelect, ItemSelect } from "@/components/Pickers";
import { Badge, DataTable, Loading, Notice, PageHeader, Panel, Section, SelectField, Tabs, TextField } from "@/components/ui";
import { api } from "@/lib/api";
import { fmtDate, fmtMoney, today } from "@/lib/format";
import { useAction, useLoad } from "@/lib/hooks";

type Row = Record<string, any>;
const TABS = [
  { id: "accruals", label: "Accruals" }, { id: "prepayments", label: "Prepayments" }, { id: "revenue", label: "Revenue (IFRS 15)" },
  { id: "impairment", label: "Impairment (IAS 36)" }, { id: "inventory", label: "Inventory value (IAS 2)" },
] as const;
type TabId = (typeof TABS)[number]["id"];

export default function AutomationPage() {
  const [tab, setTab] = useState<TabId>("accruals");
  return (
    <AppShell>
      <PageHeader title="Accounting automation" subtitle="Accruals, prepayments, revenue contracts, impairment and inventory valuation. Each item is prepared by one person, approved by another, and only then posted to the ledger." />
      <Tabs tabs={TABS} active={tab} onChange={setTab} />
      <Panel id="accruals" active={tab}><Accruals /></Panel>
      <Panel id="prepayments" active={tab}><Prepayments /></Panel>
      <Panel id="revenue" active={tab}><Revenue /></Panel>
      <Panel id="impairment" active={tab}><Impairment /></Panel>
      <Panel id="inventory" active={tab}><InventoryValuation /></Panel>
    </AppShell>
  );
}

/** Approve/post buttons shared by the list tables. */
function Steps({ base, row, reload, run }: { base: string; row: Row; reload: () => void; run: ReturnType<typeof useAction>["run"] }) {
  const step = (path: string, msg: string) => run(async () => { await api.post(`${base}/${row.id}/${path}`); await reload(); }, msg);
  return (
    <div className="flex gap-2">
      {row.status === "Draft" && <button className="btn-secondary text-xs" onClick={() => step("approve", "Approved.")}>Approve</button>}
      {row.status === "Approved" && <button className="btn-primary text-xs" onClick={() => step("post", "Posted to the ledger.")}>Post</button>}
    </div>
  );
}

function Accruals() {
  const { data, loading, reload } = useLoad<Row[]>("/automation/accruals", []);
  const act = useAction();
  const [f, setF] = useState({ description: "", amount: "", date: today(), expense: "", liability: "", autoReverse: true });
  const submit = (e: React.FormEvent) => { e.preventDefault(); act.run(async () => {
    await api.post("/automation/accruals", { description: f.description, amount: Number(f.amount), currency: "USD", accrualDate: f.date, expenseAccountId: f.expense, accruedLiabilityAccountId: f.liability, autoReverse: f.autoReverse });
    setF({ ...f, description: "", amount: "" }); await reload();
  }, "Accrual created as a draft."); };
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="New accrual" description="Records an expense you have incurred but not yet been invoiced for. It reverses automatically on the first day of the next month, or when you match it to the supplier's invoice.">
        <form onSubmit={submit} className="grid md:grid-cols-3 gap-4">
          <TextField label="Description" required value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} />
          <TextField label="Amount" type="number" step="0.01" min="0.01" required value={f.amount} onChange={(e) => setF({ ...f, amount: e.target.value })} />
          <TextField label="Accrued to (period end)" type="date" required value={f.date} onChange={(e) => setF({ ...f, date: e.target.value })} />
          <AccountSelect label="Expense account" required value={f.expense} onChange={(v) => setF({ ...f, expense: v })} />
          <AccountSelect label="Accrued liability account" required value={f.liability} onChange={(v) => setF({ ...f, liability: v })} />
          <label className="flex items-center gap-2 mt-6 text-sm"><input type="checkbox" checked={f.autoReverse} onChange={(e) => setF({ ...f, autoReverse: e.target.checked })} /> Reverse automatically next period</label>
          <div className="md:col-span-3 flex gap-2"><button className="btn-primary" disabled={act.busy}>Create accrual</button>
            <button type="button" className="btn-secondary" disabled={act.busy} onClick={() => act.run(async () => { const r = await api.post("/automation/accruals/reverse-due"); await reload(); return r.data; }, (r: any) => `${r.posted} reversed, ${r.skipped} not yet due.`)}>Run reversals now</button></div>
        </form>
      </Section>
      <Section title="Accruals">
        {loading ? <Loading what="accruals" /> : <DataTable caption="Accruals" exportName="accruals" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Description", cell: (r) => r.description }, { header: "Accrued to", cell: (r) => fmtDate(r.accrualDate), csv: (r) => fmtDate(r.accrualDate) },
          { header: "Amount", align: "right", cell: (r) => fmtMoney(r.amount, r.currency), csv: (r) => r.amount }, { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status },
          { header: "Action", cell: (r) => <Steps base="/automation/accruals" row={r} reload={reload} run={act.run} /> }]} />}
      </Section>
    </>
  );
}

function Prepayments() {
  const { data, loading, reload } = useLoad<Row[]>("/automation/prepayments", []);
  const act = useAction();
  const [f, setF] = useState({ description: "", total: "", start: today(), months: "12", asset: "", expense: "" });
  const submit = (e: React.FormEvent) => { e.preventDefault(); act.run(async () => {
    await api.post("/automation/prepayments", { description: f.description, totalAmount: Number(f.total), currency: "USD", startDate: f.start, months: Number(f.months), prepaidAssetAccountId: f.asset, expenseAccountId: f.expense });
    setF({ ...f, description: "", total: "" }); await reload();
  }, "Schedule created. Approve it to start the monthly amortisation."); };
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="New prepayment schedule" description="Spreads a payment made in advance (insurance, rent, licences) over the months it covers. Each month's charge is posted once, even if the job runs again.">
        <form onSubmit={submit} className="grid md:grid-cols-3 gap-4">
          <TextField label="Description" required value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} />
          <TextField label="Total amount" type="number" step="0.01" min="0.01" required value={f.total} onChange={(e) => setF({ ...f, total: e.target.value })} />
          <TextField label="First month" type="date" required value={f.start} onChange={(e) => setF({ ...f, start: e.target.value })} />
          <TextField label="Number of months" type="number" min="1" max="120" required value={f.months} onChange={(e) => setF({ ...f, months: e.target.value })} />
          <AccountSelect label="Prepaid asset account" required value={f.asset} onChange={(v) => setF({ ...f, asset: v })} />
          <AccountSelect label="Expense account" required value={f.expense} onChange={(v) => setF({ ...f, expense: v })} />
          <div className="md:col-span-3 flex gap-2"><button className="btn-primary" disabled={act.busy}>Create schedule</button>
            <button type="button" className="btn-secondary" disabled={act.busy} onClick={() => act.run(async () => { const r = await api.post("/automation/prepayments/run-due"); await reload(); return r.data; }, (r: any) => `${r.posted} month(s) posted.${r.problems?.length ? " Problems: " + r.problems.join(" | ") : ""}`)}>Post due months now</button></div>
        </form>
      </Section>
      <Section title="Schedules">
        {loading ? <Loading what="schedules" /> : <DataTable caption="Prepayment schedules" exportName="prepayments" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Description", cell: (r) => r.description }, { header: "Starts", cell: (r) => fmtDate(r.startDate), csv: (r) => fmtDate(r.startDate) },
          { header: "Months", align: "right", cell: (r) => r.months }, { header: "Total", align: "right", cell: (r) => fmtMoney(r.totalAmount, r.currency), csv: (r) => r.totalAmount },
          { header: "Posted", align: "right", cell: (r) => `${(r.lines ?? []).filter((l: Row) => l.journalEntryId).length}/${(r.lines ?? []).length}` },
          { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status },
          { header: "Action", cell: (r) => r.status === "Draft" ? <button className="btn-secondary text-xs" onClick={() => act.run(async () => { await api.post(`/automation/prepayments/${r.id}/approve`); await reload(); }, "Approved and active.")}>Approve</button> : null }]} />}
      </Section>
    </>
  );
}

function Revenue() {
  const contracts = useLoad<Row[]>("/automation/revenue/contracts", []);
  const position = useLoad<Row[]>("/automation/revenue/position", []);
  const act = useAction();
  const [f, setF] = useState({ no: "", customer: "", start: today(), end: "", price: "", receivable: "", liability: "", asset: "", obDesc: "", obRevenue: "", pattern: "Ratable" });
  const reload = async () => { await contracts.reload(); await position.reload(); };
  const submit = (e: React.FormEvent) => { e.preventDefault(); act.run(async () => {
    await api.post("/automation/revenue/contracts", {
      contractNo: f.no, customerId: f.customer, startDate: f.start, endDate: f.end || null, transactionPrice: Number(f.price), currency: "USD",
      receivableAccountId: f.receivable, contractLiabilityAccountId: f.liability, contractAssetAccountId: f.asset || null,
      obligations: [{ description: f.obDesc || "Performance obligation", standaloneSellingPrice: Number(f.price), pattern: f.pattern, startDate: f.start, endDate: f.pattern === "Ratable" ? f.end : null, revenueAccountId: f.obRevenue }],
    });
    await reload();
  }, "Contract created as a draft."); };
  const post = (id: string, path: string, body: object | undefined, msg: string) => act.run(async () => { await api.post(`/automation/revenue/contracts/${id}/${path}`, body); await reload(); }, msg);
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="New contract" description="A contract with one obligation recognised evenly over time, or at a point in time. Billing in advance creates a contract liability that is released as revenue is earned. Add more obligations by creating the contract through the API.">
        <form onSubmit={submit} className="grid md:grid-cols-3 gap-4">
          <TextField label="Contract number" required value={f.no} onChange={(e) => setF({ ...f, no: e.target.value })} />
          <CustomerSelect required value={f.customer} onChange={(v) => setF({ ...f, customer: v })} />
          <TextField label="Transaction price" type="number" step="0.01" min="0.01" required value={f.price} onChange={(e) => setF({ ...f, price: e.target.value })} />
          <TextField label="Start" type="date" required value={f.start} onChange={(e) => setF({ ...f, start: e.target.value })} />
          <SelectField label="Recognised" value={f.pattern} onChange={(e) => setF({ ...f, pattern: e.target.value })} options={[{ value: "Ratable", label: "Evenly over time" }, { value: "PointInTime", label: "At a point in time" }]} placeholder="Choose" />
          <TextField label="End (needed when over time)" type="date" required={f.pattern === "Ratable"} value={f.end} onChange={(e) => setF({ ...f, end: e.target.value })} />
          <TextField label="Obligation description" value={f.obDesc} onChange={(e) => setF({ ...f, obDesc: e.target.value })} />
          <AccountSelect label="Revenue account" required value={f.obRevenue} onChange={(v) => setF({ ...f, obRevenue: v })} />
          <AccountSelect label="Receivable account" required value={f.receivable} onChange={(v) => setF({ ...f, receivable: v })} />
          <AccountSelect label="Contract liability account" required value={f.liability} onChange={(v) => setF({ ...f, liability: v })} />
          <AccountSelect label="Contract asset account (optional)" value={f.asset} onChange={(v) => setF({ ...f, asset: v })} hint="Needed only if revenue may be earned before billing." />
          <div className="md:col-span-3 flex gap-2"><button className="btn-primary" disabled={act.busy}>Create contract</button>
            <button type="button" className="btn-secondary" disabled={act.busy} onClick={() => act.run(async () => { const r = await api.post("/automation/revenue/recognise-due"); await reload(); return r.data; }, (r: any) => `${r.posted} recognition(s) posted.${r.problems?.length ? " Problems: " + r.problems.join(" | ") : ""}`)}>Recognise due revenue now</button></div>
        </form>
      </Section>
      <Section title="Contract position" description="Billed, recognised, and what sits in the balance sheet as a contract liability (billed but not yet earned) or contract asset (earned but not yet billed).">
        <DataTable caption="Contract position" exportName="contract-position" rowKey={(r) => r.id} rows={position.data} columns={[
          { header: "Contract", cell: (r) => r.contractNo }, { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status },
          { header: "Price", align: "right", cell: (r) => fmtMoney(r.transactionPrice), csv: (r) => r.transactionPrice }, { header: "Billed", align: "right", cell: (r) => fmtMoney(r.billedToDate), csv: (r) => r.billedToDate },
          { header: "Recognised", align: "right", cell: (r) => fmtMoney(r.recognised), csv: (r) => r.recognised },
          { header: "Contract liability", align: "right", cell: (r) => fmtMoney(r.contractLiability), csv: (r) => r.contractLiability }, { header: "Contract asset", align: "right", cell: (r) => fmtMoney(r.contractAsset), csv: (r) => r.contractAsset }]} />
      </Section>
      <Section title="Contracts">
        {contracts.loading ? <Loading what="contracts" /> : <DataTable caption="Revenue contracts" rowKey={(r) => r.id} rows={contracts.data} columns={[
          { header: "Contract", cell: (r) => r.contractNo }, { header: "Price", align: "right", cell: (r) => fmtMoney(r.transactionPrice, r.currency) }, { header: "Status", cell: (r) => <Badge text={r.status} /> },
          { header: "Action", cell: (r) => (
            <div className="flex gap-2 flex-wrap">
              {r.status === "Draft" && <button className="btn-secondary text-xs" onClick={() => post(r.id, "approve", undefined, "Contract approved and active.")}>Approve</button>}
              {r.status === "Active" && <button className="btn-secondary text-xs" onClick={() => { const a = window.prompt("Amount to bill the customer now:"); if (a) post(r.id, "bill", { amount: Number(a), date: today() }, "Billed."); }}>Bill</button>}
              {r.status === "Active" && (r.obligations ?? []).filter((o: Row) => o.pattern === "PointInTime" && !o.satisfiedOn).map((o: Row) =>
                <button key={o.id} className="btn-secondary text-xs" onClick={() => post(r.id, `obligations/${o.id}/satisfy`, { date: today() }, "Obligation satisfied; revenue recognised.")}>Deliver: {o.description}</button>)}
            </div>) }]} />}
      </Section>
    </>
  );
}

function Impairment() {
  const { data, loading, reload } = useLoad<Row[]>("/automation/impairments", []);
  const act = useAction();
  const [f, setF] = useState({ asset: "", date: today(), fvlcts: "", viu: "", flows: "", rate: "", indicators: "", assumptions: "", loss: "" });
  const submit = (e: React.FormEvent) => { e.preventDefault(); act.run(async () => {
    const flows = f.flows.split(",").map((x) => x.trim()).filter(Boolean).map(Number);
    await api.post("/automation/impairments", { fixedAssetId: f.asset, assessmentDate: f.date, fairValueLessCostsToSell: Number(f.fvlcts || 0), valueInUse: f.viu ? Number(f.viu) : null, annualCashFlows: flows.length ? flows : null, discountRatePct: Number(f.rate || 0), indicators: f.indicators, assumptions: f.assumptions, lossAccountId: f.loss });
    await reload();
  }, "Assessment recorded. Review the calculated loss, then approve it."); };
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="New impairment assessment" description="Compare an asset's carrying amount with its recoverable amount: the higher of fair value less costs to sell and value in use. Record why you are testing it and what you assumed; both are required.">
        <form onSubmit={submit} className="grid md:grid-cols-3 gap-4">
          <AssetSelect required value={f.asset} onChange={(v) => setF({ ...f, asset: v })} />
          <TextField label="Assessment date" type="date" required value={f.date} onChange={(e) => setF({ ...f, date: e.target.value })} />
          <TextField label="Fair value less costs to sell" type="number" step="0.01" min="0" value={f.fvlcts} onChange={(e) => setF({ ...f, fvlcts: e.target.value })} />
          <TextField label="Value in use (or leave blank and enter cash flows)" type="number" step="0.01" min="0" value={f.viu} onChange={(e) => setF({ ...f, viu: e.target.value })} />
          <TextField label="Yearly cash flows, comma separated" placeholder="1000, 1200, 1100" value={f.flows} onChange={(e) => setF({ ...f, flows: e.target.value })} />
          <TextField label="Discount rate %" type="number" step="0.01" min="0" value={f.rate} onChange={(e) => setF({ ...f, rate: e.target.value })} />
          <TextField label="Impairment indicators" required value={f.indicators} onChange={(e) => setF({ ...f, indicators: e.target.value })} hint="What triggered this test?" />
          <TextField label="Assumptions and evidence" required value={f.assumptions} onChange={(e) => setF({ ...f, assumptions: e.target.value })} hint="Cash-flow sources, discount-rate basis, valuation report." />
          <AccountSelect label="Impairment loss account" required value={f.loss} onChange={(v) => setF({ ...f, loss: v })} />
          <div className="md:col-span-3"><button className="btn-primary" disabled={act.busy}>Calculate and save</button></div>
        </form>
      </Section>
      <Section title="Assessments">
        {loading ? <Loading what="assessments" /> : <DataTable caption="Impairment assessments" exportName="impairments" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Date", cell: (r) => fmtDate(r.assessmentDate), csv: (r) => fmtDate(r.assessmentDate) }, { header: "Carrying", align: "right", cell: (r) => fmtMoney(r.carryingAmount), csv: (r) => r.carryingAmount },
          { header: "Recoverable", align: "right", cell: (r) => fmtMoney(r.recoverableAmount), csv: (r) => r.recoverableAmount }, { header: "Loss", align: "right", cell: (r) => fmtMoney(r.impairmentLoss), csv: (r) => r.impairmentLoss },
          { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status }, { header: "Action", cell: (r) => <Steps base="/automation/impairments" row={r} reload={reload} run={act.run} /> }]} />}
      </Section>
    </>
  );
}

function InventoryValuation() {
  const { data, loading, reload } = useLoad<Row[]>("/automation/inventory-valuation", []);
  const act = useAction();
  const [f, setF] = useState({ item: "", date: today(), kind: "NrvWriteDown", price: "", costs: "", qty: "", reason: "", expense: "" });
  const submit = (e: React.FormEvent) => { e.preventDefault(); act.run(async () => {
    await api.post("/automation/inventory-valuation", { inventoryItemId: f.item, assessmentDate: f.date, estimatedSellingPrice: Number(f.price || 0), costsToSell: Number(f.costs || 0), kind: f.kind, writeOffQuantity: f.qty ? Number(f.qty) : null, reason: f.reason, writeDownExpenseAccountId: f.expense });
    await reload();
  }, "Assessment recorded. Review the write-down, then approve it."); };
  return (
    <>
      <Notice error={act.error} notice={act.notice} />
      <Section title="New stock valuation" description="Stock is carried at the lower of cost and net realisable value. Write-downs and write-offs need a second person's approval before they touch the ledger.">
        <form onSubmit={submit} className="grid md:grid-cols-3 gap-4">
          <ItemSelect required value={f.item} onChange={(v) => setF({ ...f, item: v })} />
          <SelectField label="Type" value={f.kind} onChange={(e) => setF({ ...f, kind: e.target.value })} placeholder="Choose" options={[{ value: "NrvWriteDown", label: "Net realisable value write-down" }, { value: "Obsolescence", label: "Obsolescence provision" }, { value: "WriteOff", label: "Write-off (scrap or loss)" }]} />
          <TextField label="Date" type="date" required value={f.date} onChange={(e) => setF({ ...f, date: e.target.value })} />
          <TextField label="Estimated selling price (per unit)" type="number" step="0.01" min="0" value={f.price} onChange={(e) => setF({ ...f, price: e.target.value })} />
          <TextField label="Costs to sell (per unit)" type="number" step="0.01" min="0" value={f.costs} onChange={(e) => setF({ ...f, costs: e.target.value })} />
          <TextField label="Quantity to write off" type="number" step="0.0001" min="0" value={f.qty} onChange={(e) => setF({ ...f, qty: e.target.value })} hint="Only for write-offs. Blank = all stock." />
          <TextField label="Reason" required value={f.reason} onChange={(e) => setF({ ...f, reason: e.target.value })} />
          <AccountSelect label="Write-down expense account" required value={f.expense} onChange={(v) => setF({ ...f, expense: v })} />
          <div className="md:col-span-3"><button className="btn-primary" disabled={act.busy}>Calculate and save</button></div>
        </form>
      </Section>
      <Section title="Valuations">
        {loading ? <Loading what="valuations" /> : <DataTable caption="Inventory valuation assessments" exportName="inventory-valuation" rowKey={(r) => r.id} rows={data} columns={[
          { header: "Date", cell: (r) => fmtDate(r.assessmentDate), csv: (r) => fmtDate(r.assessmentDate) }, { header: "Type", cell: (r) => r.kind }, { header: "Quantity", align: "right", cell: (r) => r.quantity },
          { header: "Write-down", align: "right", cell: (r) => fmtMoney(r.writeDownAmount), csv: (r) => r.writeDownAmount }, { header: "Reason", cell: (r) => r.reason },
          { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status }, { header: "Action", cell: (r) => <Steps base="/automation/inventory-valuation" row={r} reload={reload} run={act.run} /> }]} />}
      </Section>
    </>
  );
}
