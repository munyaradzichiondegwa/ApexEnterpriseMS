"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function InventoryPage() {
  const [items, setItems] = useState<any[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [newItem, setNewItem] = useState({ sku: "", name: "", unitOfMeasure: "EA", reorderLevel: 0, reorderQuantity: 0, valuationMethod: "AVCO", assetAccountId: "", cogsAccountId: "" });

  function load() {
    api.get("/inventory/items").then((r) => setItems(r.data)).catch((e) => setError(extractErrorMessage(e)));
  }
  useEffect(load, []);

  async function createItem(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/inventory/items", newItem);
      setNewItem({ sku: "", name: "", unitOfMeasure: "EA", reorderLevel: 0, reorderQuantity: 0, valuationMethod: "AVCO", assetAccountId: "", cogsAccountId: "" });
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Inventory</h1>
      <p className="text-slate-500 mb-6">Perpetual stock with FIFO/AVCO costing and automatic GL posting.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>SKU</th><th>Name</th><th>UoM</th><th>On Hand</th><th>Avg Cost</th><th>Method</th><th>Reorder Lvl</th></tr></thead>
            <tbody>
              {items.map((i) => (
                <tr key={i.id} className={i.quantityOnHand <= i.reorderLevel ? "bg-red-50" : ""}>
                  <td className="font-mono">{i.sku}</td><td>{i.name}</td><td>{i.unitOfMeasure}</td>
                  <td className="text-right">{i.quantityOnHand}</td><td className="text-right">{Number(i.averageCost).toFixed(4)}</td>
                  <td>{i.valuationMethod}</td><td className="text-right">{i.reorderLevel}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <form onSubmit={createItem} className="card space-y-3 h-fit">
          <h2 className="font-semibold">New Item</h2>
          <div><label className="label">SKU</label><input className="input" required value={newItem.sku} onChange={(e) => setNewItem({ ...newItem, sku: e.target.value })} /></div>
          <div><label className="label">Name</label><input className="input" required value={newItem.name} onChange={(e) => setNewItem({ ...newItem, name: e.target.value })} /></div>
          <div>
            <label className="label">Valuation Method</label>
            <select className="input" value={newItem.valuationMethod} onChange={(e) => setNewItem({ ...newItem, valuationMethod: e.target.value })}>
              <option value="AVCO">AVCO (Weighted Average)</option>
              <option value="FIFO">FIFO</option>
            </select>
          </div>
          <div><label className="label">Reorder Level</label><input className="input" type="number" value={newItem.reorderLevel} onChange={(e) => setNewItem({ ...newItem, reorderLevel: Number(e.target.value) })} /></div>
          <div><label className="label">Inventory Asset GL Account ID</label><input className="input" required value={newItem.assetAccountId} onChange={(e) => setNewItem({ ...newItem, assetAccountId: e.target.value })} /></div>
          <div><label className="label">COGS GL Account ID</label><input className="input" required value={newItem.cogsAccountId} onChange={(e) => setNewItem({ ...newItem, cogsAccountId: e.target.value })} /></div>
          <button className="btn-primary w-full">Add Item</button>
        </form>
      </div>
    </AppShell>
  );
}
