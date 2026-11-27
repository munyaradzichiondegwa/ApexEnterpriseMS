"use client";

import { useEffect, useState } from "react";
import {
  CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from "recharts";
import { api } from "@/lib/api";

type Point = { year: number; month: number; period: string; income: number; expense: number; netProfit: number };

export default function FinancialTrendPanel({ from, to }: { from: string; to: string }) {
  const [rows, setRows] = useState<Point[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);
    api.get<Point[]>("/reports/financial-trend", { params: { from, to } })
      .then((response) => { if (active) setRows(response.data); })
      .catch(() => { if (active) setError("The financial trend could not be loaded. Check your reporting permission and try again."); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [from, to]);

  const number = (value: number) => new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 }).format(value);

  return (
    <section className="card mb-6" aria-label="Monthly financial performance trend">
      <div className="flex flex-wrap items-start justify-between gap-2 mb-3">
        <div>
          <h2 className="font-semibold">Financial performance trend</h2>
          <p className="text-xs text-slate-500">Monthly income, expenses and net profit from posted ledger entries, in the company functional currency.</p>
        </div>
        <span className="text-xs text-slate-500">Interactive · Hover for values</span>
      </div>
      {loading ? <p className="text-sm text-slate-500 py-10 text-center">Loading financial trend…</p> :
        error ? <p role="alert" className="text-sm text-amber-700 py-10 text-center">{error}</p> :
        rows.length === 0 ? <p className="text-sm text-slate-500 py-10 text-center">No posted financial activity in this period.</p> :
        <div className="h-72 w-full">
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={rows} margin={{ top: 8, right: 18, left: 4, bottom: 4 }}>
              <CartesianGrid strokeDasharray="3 3" />
              <XAxis dataKey="period" tick={{ fontSize: 11 }} />
              <YAxis tickFormatter={number} tick={{ fontSize: 11 }} width={76} />
              <Tooltip formatter={(value: number | string) => number(Number(value))} labelFormatter={(label) => `Period: ${label}`} />
              <Legend />
              <Line type="monotone" dataKey="income" name="Income" stroke="#1597d4" strokeWidth={2.5} dot={false} activeDot={{ r: 5 }} />
              <Line type="monotone" dataKey="expense" name="Expenses" stroke="#f28b28" strokeWidth={2.5} dot={false} activeDot={{ r: 5 }} />
              <Line type="monotone" dataKey="netProfit" name="Net profit" stroke="#20b6a5" strokeWidth={2.5} dot={false} activeDot={{ r: 5 }} />
            </LineChart>
          </ResponsiveContainer>
        </div>}
    </section>
  );
}
