"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function FixedAssetsPage() {
  const [assets, setAssets] = useState<any[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [newAsset, setNewAsset] = useState({ assetNo: "", name: "", categoryId: "", acquisitionDate: "", acquisitionCost: 0, method: "StraightLine", usefulLifeMonths: 60, residualValue: 0 });

  function load() {
    api.get("/fixed-assets/assets").then((r) => setAssets(r.data)).catch((e) => setError(extractErrorMessage(e)));
  }
  useEffect(load, []);

  async function createAsset(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/fixed-assets/assets", newAsset);
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function runDepreciation() {
    try {
      const { data } = await api.post("/fixed-assets/run-depreciation", { periodDate: new Date().toISOString() });
      setMsg(`Posted depreciation for ${data.count} assets, total ${Number(data.totalAmount).toFixed(2)}.`);
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Fixed Assets</h1>
      <p className="text-slate-500 mb-6">Asset register with straight-line and reducing-balance depreciation.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}
      {msg && <div className="text-sm text-brand-700 bg-brand-50 border border-brand-200 rounded-lg px-3 py-2 mb-4">{msg}</div>}

      <div className="mb-4"><button className="btn-secondary" onClick={runDepreciation}>Run Monthly Depreciation Batch</button></div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>Asset No</th><th>Name</th><th>Cost</th><th>Acc. Depr.</th><th>NBV</th><th>Status</th></tr></thead>
            <tbody>
              {assets.map((a) => (
                <tr key={a.id}>
                  <td className="font-mono">{a.assetNo}</td><td>{a.name}</td>
                  <td className="text-right">{Number(a.acquisitionCost).toFixed(2)}</td>
                  <td className="text-right">{Number(a.accumulatedDepreciation).toFixed(2)}</td>
                  <td className="text-right">{Number(a.currentNbv).toFixed(2)}</td>
                  <td>{a.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <form onSubmit={createAsset} className="card space-y-3 h-fit">
          <h2 className="font-semibold">Register Asset</h2>
          <div><label className="label">Asset No</label><input className="input" required value={newAsset.assetNo} onChange={(e) => setNewAsset({ ...newAsset, assetNo: e.target.value })} /></div>
          <div><label className="label">Name</label><input className="input" required value={newAsset.name} onChange={(e) => setNewAsset({ ...newAsset, name: e.target.value })} /></div>
          <div><label className="label">Category ID</label><input className="input" required value={newAsset.categoryId} onChange={(e) => setNewAsset({ ...newAsset, categoryId: e.target.value })} /></div>
          <div><label className="label">Acquisition Date</label><input className="input" type="date" required value={newAsset.acquisitionDate} onChange={(e) => setNewAsset({ ...newAsset, acquisitionDate: e.target.value })} /></div>
          <div><label className="label">Cost</label><input className="input" type="number" required value={newAsset.acquisitionCost} onChange={(e) => setNewAsset({ ...newAsset, acquisitionCost: Number(e.target.value) })} /></div>
          <div>
            <label className="label">Method</label>
            <select className="input" value={newAsset.method} onChange={(e) => setNewAsset({ ...newAsset, method: e.target.value })}>
              <option value="StraightLine">Straight Line</option>
              <option value="ReducingBalance">Reducing Balance</option>
            </select>
          </div>
          <div><label className="label">Useful Life (months)</label><input className="input" type="number" value={newAsset.usefulLifeMonths} onChange={(e) => setNewAsset({ ...newAsset, usefulLifeMonths: Number(e.target.value) })} /></div>
          <button className="btn-primary w-full">Register Asset</button>
        </form>
      </div>
    </AppShell>
  );
}
