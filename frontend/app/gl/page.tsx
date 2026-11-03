"use client";

import { Fragment, useEffect, useMemo, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth";

type JournalStatus = "Draft" | "Submitted" | "Approved" | "Posted" | "Reversed" | "Rejected" | "Cancelled";

interface JournalLine { glAccountId: string; debit: number; credit: number; currency: string; exchangeRate: number; narrative?: string | null }
interface Journal {
  id: string; entryNo: string; entryDate: string; source: string; description?: string | null;
  status: JournalStatus; reversalOfId?: string | null; lines?: JournalLine[];
}
interface HistoryItem { id: string; action: string; userId?: string | null; timestamp: string; afterValueJson?: string | null }

const STATUSES: JournalStatus[] = ["Draft", "Submitted", "Approved", "Posted", "Reversed", "Rejected", "Cancelled"];

const STATUS_STYLE: Record<JournalStatus, string> = {
  Draft: "bg-slate-100 text-slate-700",
  Submitted: "bg-amber-100 text-amber-800",
  Approved: "bg-sky-100 text-sky-800",
  Posted: "bg-emerald-100 text-emerald-800",
  Reversed: "bg-purple-100 text-purple-800",
  Rejected: "bg-red-100 text-red-800",
  Cancelled: "bg-slate-200 text-slate-500",
};

const emptyLine = () => ({ glAccountId: "", debit: "", credit: "", currency: "USD", exchangeRate: "1", narrative: "" });

const money = (n: number) => n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export default function GlPage() {
  const { can } = useAuth();
  const [accounts, setAccounts] = useState<any[]>([]);
  const [entries, setEntries] = useState<Journal[]>([]);
  const [tab, setTab] = useState<"accounts" | "journals">("accounts");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [filter, setFilter] = useState<JournalStatus | "All">("All");
  const [busyId, setBusyId] = useState<string | null>(null);
  const [reasonFor, setReasonFor] = useState<{ id: string; action: "reject" | "reverse" | "cancel"; label: string } | null>(null);
  const [reason, setReason] = useState("");
  const [historyFor, setHistoryFor] = useState<string | null>(null);
  const [history, setHistory] = useState<HistoryItem[]>([]);
  const [newAccount, setNewAccount] = useState({ code: "", name: "", type: "Asset", currency: "USD", isBankAccount: false, isControlAccount: false });
  const [draft, setDraft] = useState({ entryDate: new Date().toISOString().slice(0, 10), description: "", reference: "", lines: [emptyLine(), emptyLine()] });

  function load() {
    api.get("/gl/accounts").then((r) => setAccounts(r.data)).catch((e) => setError(extractErrorMessage(e)));
    api.get("/gl/journal-entries").then((r) => setEntries(r.data)).catch((e) => setError(extractErrorMessage(e)));
  }
  useEffect(load, []);

  const activeAccounts = useMemo(() => accounts.filter((a) => a.isActive !== false), [accounts]);
  const accountName = (id: string) => {
    const a = accounts.find((x) => x.id === id);
    return a ? `${a.code} ${a.name}` : id;
  };
  const visibleEntries = useMemo(() => entries.filter((j) => filter === "All" || j.status === filter), [entries, filter]);

  // ---- new journal: live balance check in base currency (the API re-validates everything) ----
  const totals = useMemo(() => {
    let debit = 0, credit = 0, filled = 0;
    for (const l of draft.lines) {
      const rate = Number(l.exchangeRate) || 0;
      const d = Number(l.debit) || 0, c = Number(l.credit) || 0;
      if (d > 0 || c > 0) filled++;
      debit += d * rate;
      credit += c * rate;
    }
    return { debit, credit, filled, difference: Math.round((debit - credit) * 1e6) / 1e6 };
  }, [draft.lines]);
  const canSaveDraft = totals.filled >= 2 && totals.difference === 0 && draft.lines.every((l) => (Number(l.debit) > 0 || Number(l.credit) > 0) ? !!l.glAccountId && Number(l.exchangeRate) > 0 : true);

  function updateLine(index: number, patch: Partial<ReturnType<typeof emptyLine>>) {
    setDraft((d) => ({ ...d, lines: d.lines.map((l, i) => (i === index ? { ...l, ...patch } : l)) }));
  }

  async function createAccount(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await api.post("/gl/accounts", newAccount);
      setNewAccount({ code: "", name: "", type: "Asset", currency: "USD", isBankAccount: false, isControlAccount: false });
      load();
    } catch (err) {
      setError(extractErrorMessage(err));
    }
  }

  async function toggleActive(account: any) {
    setError(null);
    try {
      await api.put(`/gl/accounts/${account.id}/active`, { isActive: account.isActive === false });
      load();
    } catch (err) {
      setError(extractErrorMessage(err));
    }
  }

  async function saveDraft(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setNotice(null);
    try {
      const lines = draft.lines
        .filter((l) => Number(l.debit) > 0 || Number(l.credit) > 0)
        .map((l) => ({
          glAccountId: l.glAccountId, debit: Number(l.debit) || 0, credit: Number(l.credit) || 0,
          currency: l.currency, exchangeRate: Number(l.exchangeRate), costCentreId: null, narrative: l.narrative || null,
        }));
      await api.post("/gl/journal-entries", { entryDate: draft.entryDate, description: draft.description || null, reference: draft.reference || null, lines });
      setDraft({ entryDate: draft.entryDate, description: "", reference: "", lines: [emptyLine(), emptyLine()] });
      setNotice("Draft saved. Submit it for approval from the list below.");
      setFilter("All");
      load();
    } catch (err) {
      setError(extractErrorMessage(err));
    }
  }

  async function act(id: string, action: string, body?: unknown, done?: string) {
    setBusyId(id);
    setError(null);
    setNotice(null);
    try {
      await api.post(`/gl/journal-entries/${id}/${action}`, body ?? {});
      if (done) setNotice(done);
      load();
    } catch (err) {
      setError(extractErrorMessage(err));
    } finally {
      setBusyId(null);
    }
  }

  async function confirmReason(e: React.FormEvent) {
    e.preventDefault();
    if (!reasonFor) return;
    const { id, action } = reasonFor;
    if (action === "reverse") await act(id, "reverse", JSON.stringify(reason), "Journal reversed.");
    else await act(id, action, { reason }, action === "reject" ? "Journal rejected." : "Journal cancelled.");
    setReasonFor(null);
    setReason("");
  }

  async function toggleHistory(id: string) {
    if (historyFor === id) return setHistoryFor(null);
    setHistoryFor(id);
    setHistory([]);
    try {
      const r = await api.get(`/gl/journal-entries/${id}/history`);
      setHistory(r.data);
    } catch (err) {
      setError(extractErrorMessage(err));
    }
  }

  function actionsFor(j: Journal) {
    const busy = busyId === j.id;
    const buttons: React.ReactNode[] = [];
    const btn = (key: string, label: string, onClick: () => void, primary = false) => (
      <button key={key} className={primary ? "btn-primary !px-2 !py-1 text-xs" : "btn-secondary !px-2 !py-1 text-xs"} disabled={busy} onClick={onClick}>{label}</button>
    );
    if (j.status === "Draft" && can("gl.journals.create")) buttons.push(btn("submit", "Submit", () => act(j.id, "submit", undefined, "Submitted for approval."), true));
    if (j.status === "Submitted" && can("gl.journals.approve")) {
      buttons.push(btn("approve", "Approve", () => act(j.id, "approve", {}, "Journal approved."), true));
      buttons.push(btn("reject", "Reject", () => setReasonFor({ id: j.id, action: "reject", label: "Why is this journal being rejected?" })));
    }
    if (j.status === "Approved" && can("gl.journals.post")) buttons.push(btn("post", "Post", () => act(j.id, "post", undefined, "Journal posted to the ledger."), true));
    if (j.status === "Posted" && can("gl.journals.reverse")) buttons.push(btn("reverse", "Reverse", () => setReasonFor({ id: j.id, action: "reverse", label: "Why is this journal being reversed?" })));
    if (["Draft", "Submitted", "Approved", "Rejected"].includes(j.status) && can("gl.journals.create")) {
      buttons.push(btn("cancel", "Cancel", () => setReasonFor({ id: j.id, action: "cancel", label: "Reason for cancelling (optional)" })));
    }
    buttons.push(btn("history", historyFor === j.id ? "Hide history" : "History", () => toggleHistory(j.id)));
    return <div className="flex flex-wrap gap-1">{buttons}</div>;
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">General Ledger</h1>
      <p className="text-slate-500 mb-6">Chart of accounts, journal entries, and postings. Every sub-ledger auto-posts here.</p>

      <div className="flex gap-2 mb-6">
        <button className={tab === "accounts" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("accounts")}>Chart of Accounts</button>
        <button className={tab === "journals" ? "btn-primary" : "btn-secondary"} onClick={() => setTab("journals")}>Journal Entries</button>
      </div>

      {error && <div className="text-sm text-red-600 mb-4" role="alert">{error}</div>}
      {notice && <div className="text-sm text-emerald-700 mb-4" role="status">{notice}</div>}

      {tab === "accounts" && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 card overflow-x-auto">
            <table className="table-base">
              <thead><tr><th>Code</th><th>Name</th><th>Type</th><th>Currency</th><th>Balance</th><th></th></tr></thead>
              <tbody>
                {accounts.map((a) => (
                  <tr key={a.id} className={a.isActive === false ? "text-slate-400" : ""}>
                    <td className="font-mono">{a.code}</td>
                    <td>{a.name}{a.isActive === false && <span className="ml-2 text-xs rounded bg-slate-200 px-1.5 py-0.5">Inactive</span>}</td>
                    <td>{a.type}</td>
                    <td>{a.currency}</td>
                    <td className="text-right">{Number(a.currentBalance).toLocaleString(undefined, { minimumFractionDigits: 2 })}</td>
                    <td className="text-right">
                      {can("gl.accounts.manage") && (
                        <button className="btn-secondary !px-2 !py-1 text-xs" onClick={() => toggleActive(a)}>
                          {a.isActive === false ? "Activate" : "Deactivate"}
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {can("gl.accounts.manage") && (
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
          )}
        </div>
      )}

      {tab === "journals" && (
        <div className="space-y-6">
          {can("gl.journals.create") && (
            <form onSubmit={saveDraft} className="card space-y-4">
              <div>
                <h2 className="font-semibold">New journal entry</h2>
                <p className="text-xs text-slate-500">Saved as a draft first. It reaches the ledger only after it is submitted, approved by someone else, and posted.</p>
              </div>
              <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                <div><label className="label">Date</label><input className="input" type="date" required value={draft.entryDate} onChange={(e) => setDraft({ ...draft, entryDate: e.target.value })} /></div>
                <div><label className="label">Reference</label><input className="input" value={draft.reference} onChange={(e) => setDraft({ ...draft, reference: e.target.value })} /></div>
                <div><label className="label">Description</label><input className="input" value={draft.description} onChange={(e) => setDraft({ ...draft, description: e.target.value })} /></div>
              </div>
              <div className="overflow-x-auto">
                <table className="table-base min-w-[44rem]">
                  <thead><tr><th>Account</th><th>Debit</th><th>Credit</th><th>Currency</th><th>Rate</th><th>Narrative</th><th></th></tr></thead>
                  <tbody>
                    {draft.lines.map((l, i) => (
                      <tr key={i}>
                        <td>
                          <select className="input" value={l.glAccountId} onChange={(e) => updateLine(i, { glAccountId: e.target.value, currency: activeAccounts.find((a) => a.id === e.target.value)?.currency ?? l.currency })}>
                            <option value="">Select account…</option>
                            {activeAccounts.map((a) => <option key={a.id} value={a.id}>{a.code} — {a.name}</option>)}
                          </select>
                        </td>
                        <td><input className="input text-right" type="number" min="0" step="0.01" value={l.debit} onChange={(e) => updateLine(i, { debit: e.target.value, credit: e.target.value ? "" : l.credit })} /></td>
                        <td><input className="input text-right" type="number" min="0" step="0.01" value={l.credit} onChange={(e) => updateLine(i, { credit: e.target.value, debit: e.target.value ? "" : l.debit })} /></td>
                        <td><input className="input w-20" maxLength={3} value={l.currency} onChange={(e) => updateLine(i, { currency: e.target.value.toUpperCase() })} /></td>
                        <td><input className="input w-24 text-right" type="number" min="0" step="any" value={l.exchangeRate} onChange={(e) => updateLine(i, { exchangeRate: e.target.value })} /></td>
                        <td><input className="input" value={l.narrative} onChange={(e) => updateLine(i, { narrative: e.target.value })} /></td>
                        <td>{draft.lines.length > 2 && <button type="button" className="text-slate-400 hover:text-red-600" aria-label="Remove line" onClick={() => setDraft({ ...draft, lines: draft.lines.filter((_, x) => x !== i) })}>✕</button>}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div className="flex flex-wrap items-center justify-between gap-3">
                <button type="button" className="btn-secondary" onClick={() => setDraft({ ...draft, lines: [...draft.lines, emptyLine()] })}>Add line</button>
                <div className="text-sm" role="status">
                  Debits {money(totals.debit)} · Credits {money(totals.credit)} ·{" "}
                  {totals.difference === 0 && totals.filled >= 2
                    ? <span className="text-emerald-700 font-medium">Balanced</span>
                    : <span className="text-red-600 font-medium">Out of balance by {money(Math.abs(totals.difference))}</span>}
                </div>
                <button className="btn-primary" disabled={!canSaveDraft}>Save draft</button>
              </div>
            </form>
          )}

          <div className="card">
            <div className="flex flex-wrap gap-2 mb-4" role="group" aria-label="Filter journals by status">
              {(["All", ...STATUSES] as const).map((s) => (
                <button key={s} className={filter === s ? "btn-primary !px-3 !py-1 text-xs" : "btn-secondary !px-3 !py-1 text-xs"} onClick={() => setFilter(s)}>
                  {s}{s !== "All" ? ` (${entries.filter((j) => j.status === s).length})` : ""}
                </button>
              ))}
            </div>

            {reasonFor && (
              <form onSubmit={confirmReason} className="mb-4 rounded-lg border border-slate-200 bg-slate-50 p-3 space-y-2">
                <label className="label">{reasonFor.label}</label>
                <input className="input" required={reasonFor.action !== "cancel"} value={reason} onChange={(e) => setReason(e.target.value)} autoFocus />
                <div className="flex gap-2">
                  <button className="btn-primary">Confirm</button>
                  <button type="button" className="btn-secondary" onClick={() => { setReasonFor(null); setReason(""); }}>Back</button>
                </div>
              </form>
            )}

            <div className="overflow-x-auto">
              <table className="table-base">
                <thead><tr><th>Entry No</th><th>Date</th><th>Source</th><th>Description</th><th className="text-right">Amount</th><th>Status</th><th>Actions</th></tr></thead>
                <tbody>
                  {visibleEntries.length === 0 && <tr><td colSpan={7} className="text-slate-400">No journal entries{filter !== "All" ? ` with status ${filter}` : ""}.</td></tr>}
                  {visibleEntries.map((j) => (
                    <Fragment key={j.id}>
                      <tr>
                        <td className="font-mono">{j.entryNo}</td>
                        <td>{new Date(j.entryDate).toLocaleDateString()}</td>
                        <td>{j.source}</td>
                        <td>{j.description}</td>
                        <td className="text-right">{money((j.lines ?? []).reduce((s, l) => s + Number(l.debit), 0))}</td>
                        <td><span className={`rounded px-2 py-0.5 text-xs font-medium ${STATUS_STYLE[j.status] ?? "bg-slate-100"}`}>{j.status}</span></td>
                        <td>{actionsFor(j)}</td>
                      </tr>
                      {historyFor === j.id && (
                        <tr>
                          <td colSpan={7} className="bg-slate-50">
                            <div className="space-y-3 py-2">
                              <div>
                                <div className="text-xs font-semibold uppercase text-slate-500 mb-1">Lines</div>
                                {(j.lines ?? []).map((l, i) => (
                                  <div key={i} className="text-xs text-slate-700">{accountName(l.glAccountId)} — Dr {money(Number(l.debit))} / Cr {money(Number(l.credit))} {l.currency}{l.narrative ? ` · ${l.narrative}` : ""}</div>
                                ))}
                              </div>
                              <div>
                                <div className="text-xs font-semibold uppercase text-slate-500 mb-1">Audit trail</div>
                                {history.length === 0 && <div className="text-xs text-slate-400">No recorded events.</div>}
                                {history.map((h) => {
                                  let note = "";
                                  try { note = h.afterValueJson ? (JSON.parse(h.afterValueJson).note ?? "") : ""; } catch { /* ignore malformed history payloads */ }
                                  return <div key={h.id} className="text-xs text-slate-700">{new Date(h.timestamp).toLocaleString()} — {h.action}{note ? ` — ${note}` : ""}</div>;
                                })}
                              </div>
                            </div>
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      )}
    </AppShell>
  );
}
