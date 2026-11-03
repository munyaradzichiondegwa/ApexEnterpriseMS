"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth";

interface ExceptionItem {
  id: string;
  category: string;
  sourceType: string;
  sourceId: string;
  message: string;
  status: "Open" | "Resolved" | "Dismissed";
  retryCount: number;
  lastAttemptAt?: string | null;
  createdAt: string;
  resolvedAt?: string | null;
  resolutionNote?: string | null;
}

const CATEGORY_LABEL: Record<string, string> = {
  InterCompanyMirror: "Inter-company mirror",
  Fiscalisation: "Fiscalisation",
  Posting: "Posting",
  Reconciliation: "Reconciliation",
  ScheduledJob: "Scheduled job",
  Other: "Other",
};

export default function ExceptionsPage() {
  const { can } = useAuth();
  const [items, setItems] = useState<ExceptionItem[]>([]);
  const [status, setStatus] = useState<"Open" | "Resolved" | "Dismissed" | "All">("Open");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [closing, setClosing] = useState<{ id: string; dismiss: boolean } | null>(null);
  const [note, setNote] = useState("");

  function load() {
    const query = status === "All" ? "" : `?status=${status}`;
    api.get(`/exceptions${query}`).then((r) => setItems(r.data)).catch((e) => setError(extractErrorMessage(e)));
  }
  useEffect(load, [status]);

  async function retry(item: ExceptionItem) {
    setBusyId(item.id);
    setError(null);
    setNotice(null);
    try {
      const { data } = await api.post(`/exceptions/${item.id}/retry`);
      if (data.status === "Completed") setNotice("The inter-company mirror was posted and the exception is closed.");
      else setError(data.message ?? "The retry failed again; the exception stays open.");
      load();
    } catch (err) {
      setError(extractErrorMessage(err));
    } finally {
      setBusyId(null);
    }
  }

  async function close(e: React.FormEvent) {
    e.preventDefault();
    if (!closing) return;
    setError(null);
    try {
      await api.post(`/exceptions/${closing.id}/close`, { note, dismiss: closing.dismiss });
      setClosing(null);
      setNote("");
      load();
    } catch (err) {
      setError(extractErrorMessage(err));
    }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Exceptions</h1>
      <p className="text-slate-500 mb-6">
        Work that could not complete automatically, such as an inter-company mirror posting that failed after the invoice was
        posted. Nothing here is hidden: each item stays open until it is retried successfully or closed with a note.
      </p>

      <div className="flex flex-wrap gap-2 mb-4" role="group" aria-label="Filter by status">
        {(["Open", "Resolved", "Dismissed", "All"] as const).map((s) => (
          <button key={s} className={status === s ? "btn-primary !px-3 !py-1 text-xs" : "btn-secondary !px-3 !py-1 text-xs"} onClick={() => setStatus(s)}>{s}</button>
        ))}
      </div>

      {error && <div className="text-sm text-red-600 mb-4" role="alert">{error}</div>}
      {notice && <div className="text-sm text-emerald-700 mb-4" role="status">{notice}</div>}

      {closing && (
        <form onSubmit={close} className="card mb-4 space-y-2">
          <label className="label">{closing.dismiss ? "Why is this being dismissed?" : "How was this resolved?"}</label>
          <input className="input" required value={note} onChange={(e) => setNote(e.target.value)} autoFocus />
          <div className="flex gap-2">
            <button className="btn-primary">{closing.dismiss ? "Dismiss" : "Mark resolved"}</button>
            <button type="button" className="btn-secondary" onClick={() => { setClosing(null); setNote(""); }}>Back</button>
          </div>
        </form>
      )}

      <div className="card overflow-x-auto">
        <table className="table-base">
          <thead><tr><th>Type</th><th>Problem</th><th>Retries</th><th>Last attempt</th><th>Status</th><th></th></tr></thead>
          <tbody>
            {items.length === 0 && <tr><td colSpan={6} className="text-slate-400">No {status === "All" ? "" : status.toLowerCase() + " "}exceptions.</td></tr>}
            {items.map((x) => (
              <tr key={x.id}>
                <td>{CATEGORY_LABEL[x.category] ?? x.category}<div className="text-xs text-slate-400 font-mono">{x.sourceType} {x.sourceId.slice(0, 8)}</div></td>
                <td className="max-w-md">{x.message}{x.resolutionNote && <div className="text-xs text-slate-500 mt-1">Resolution: {x.resolutionNote}</div>}</td>
                <td>{x.retryCount}</td>
                <td>{x.lastAttemptAt ? new Date(x.lastAttemptAt).toLocaleString() : "—"}</td>
                <td>{x.status}</td>
                <td>
                  {x.status === "Open" && can("exceptions.manage") && (
                    <div className="flex flex-wrap gap-1">
                      {x.category === "InterCompanyMirror" && (
                        <button className="btn-primary !px-2 !py-1 text-xs" disabled={busyId === x.id} onClick={() => retry(x)}>Retry</button>
                      )}
                      <button className="btn-secondary !px-2 !py-1 text-xs" onClick={() => setClosing({ id: x.id, dismiss: false })}>Resolve</button>
                      <button className="btn-secondary !px-2 !py-1 text-xs" onClick={() => setClosing({ id: x.id, dismiss: true })}>Dismiss</button>
                    </div>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </AppShell>
  );
}
