"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function ManufacturingPage() {
  const [boms, setBoms] = useState<any[]>([]);
  const [workOrders, setWorkOrders] = useState<any[]>([]);
  const [tab, setTab] = useState<"wo" | "bom">("wo");
  const [error, setError] = useState<string | null>(null);

  function load() {
    api.get("/manufacturing/boms").then((r) => setBoms(r.data)).catch((e) => setError(extractErrorMessage(e)));
    api.get("/manufacturing/work-orders").then((r) => setWorkOrders(r.data)).catch(() => {});
  }
  useEffect(load, []);

  async function release(id: string) {
    try { await api.post(`/manufacturing/work-orders/${id}/release`); load(); } catch (err) { setError(extractErrorMessage(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Manufacturing & Production</h1>
      <p className="text-slate-500 mb-6">Bill of Materials costing and shop-floor work orders.</p>
      <div className="flex gap-2 mb-6">
        {(["wo", "bom"] as const).map((t) => (<button key={t} className={tab === t ? "btn-primary" : "btn-secondary"} onClick={() => setTab(t)}>{t === "wo" ? "Work Orders" : "Bills of Material"}</button>))}
      </div>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      {tab === "wo" && (
        <div className="card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>WO #</th><th>Qty to Produce</th><th>Qty Produced</th><th>Status</th><th>Material Cost</th><th></th></tr></thead>
            <tbody>
              {workOrders.map((w) => (
                <tr key={w.id}>
                  <td className="font-mono">{w.woNo}</td><td className="text-right">{w.qtyToProduce}</td><td className="text-right">{w.qtyProduced}</td>
                  <td>{w.status}</td><td className="text-right">{Number(w.totalMaterialCost).toFixed(2)}</td>
                  <td>{w.status === "Draft" && <button className="btn-secondary text-xs" onClick={() => release(w.id)}>Release</button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {tab === "bom" && (
        <div className="card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>Finished Item</th><th>Version</th><th>Batch Qty</th><th>Status</th><th>Components</th></tr></thead>
            <tbody>{boms.map((b) => (<tr key={b.id}><td className="font-mono">{b.finishedItemId}</td><td>{b.version}</td><td>{b.standardBatchQty}</td><td>{b.status}</td><td>{b.lines?.length ?? 0}</td></tr>))}</tbody>
          </table>
        </div>
      )}
    </AppShell>
  );
}
