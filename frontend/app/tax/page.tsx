"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function TaxPage() {
  const [taxCodes, setTaxCodes] = useState<any[]>([]);
  const [vatReturn, setVatReturn] = useState<any>(null);
  const [whtReport, setWhtReport] = useState<any[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [range, setRange] = useState({ from: new Date(new Date().getFullYear(), new Date().getMonth(), 1).toISOString().slice(0, 10), to: new Date().toISOString().slice(0, 10) });

  useEffect(() => {
    api.get("/tax/codes").then((r) => setTaxCodes(r.data)).catch((e) => setError(extractErrorMessage(e)));
  }, []);

  async function loadVat() {
    try {
      const { data } = await api.get(`/tax/vat-return?from=${range.from}&to=${range.to}`);
      setVatReturn(data);
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function loadWht() {
    try {
      const { data } = await api.get(`/tax/wht-report?from=${range.from}&to=${range.to}`);
      setWhtReport(data);
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">ZW Tax Compliance</h1>
      <p className="text-slate-500 mb-6">VAT return generation, WHT reporting, and tax code configuration. E-filing submission to the ZIMRA portal is a placeholder pending API access.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      <div className="card mb-6 flex flex-wrap items-end gap-3">
        <div><label className="label">From</label><input className="input" type="date" value={range.from} onChange={(e) => setRange({ ...range, from: e.target.value })} /></div>
        <div><label className="label">To</label><input className="input" type="date" value={range.to} onChange={(e) => setRange({ ...range, to: e.target.value })} /></div>
        <button className="btn-primary" onClick={loadVat}>Generate VAT Return</button>
        <button className="btn-secondary" onClick={loadWht}>Generate WHT Report</button>
      </div>

      {vatReturn && (
        <div className="card mb-6">
          <h2 className="font-semibold mb-3">VAT Return</h2>
          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4 text-sm">
            <div><div className="text-slate-400">Output VAT</div><div className="text-lg font-semibold">{vatReturn.outputVat.toFixed(2)}</div></div>
            <div><div className="text-slate-400">Input VAT</div><div className="text-lg font-semibold">{vatReturn.inputVat.toFixed(2)}</div></div>
            <div><div className="text-slate-400">Net Payable</div><div className="text-lg font-semibold">{vatReturn.netVatPayable.toFixed(2)}</div></div>
          </div>
        </div>
      )}

      {whtReport.length > 0 && (
        <div className="card mb-6 overflow-x-auto">
          <h2 className="font-semibold mb-3">WHT Report</h2>
          <table className="table-base">
            <thead><tr><th>Supplier</th><th>Invoice</th><th>Gross</th><th>Rate</th><th>WHT</th></tr></thead>
            <tbody>
              {whtReport.map((w, idx) => (
                <tr key={idx}><td>{w.supplierName}</td><td>{w.invoiceNo}</td><td className="text-right">{w.grossAmount.toFixed(2)}</td><td>{(w.whtRate * 100).toFixed(0)}%</td><td className="text-right">{w.whtAmount.toFixed(2)}</td></tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="card overflow-x-auto">
        <h2 className="font-semibold mb-3">Tax Codes</h2>
        <table className="table-base">
          <thead><tr><th>Code</th><th>Name</th><th>Rate</th><th>Type</th></tr></thead>
          <tbody>
            {taxCodes.map((t) => (<tr key={t.id}><td>{t.code}</td><td>{t.name}</td><td>{(t.rate * 100).toFixed(0)}%</td><td>{t.type}</td></tr>))}
          </tbody>
        </table>
      </div>
    </AppShell>
  );
}
