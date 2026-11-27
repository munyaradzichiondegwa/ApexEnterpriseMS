"use client";

import {
  Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from "recharts";

type Ageing = {
  current: number;
  days1to30: number;
  days31to60: number;
  days61to90: number;
  over90: number;
};

type FinancialDashboardData = {
  cash: number;
  receivables: number;
  payables: number;
  receivablesAgeing: Ageing;
  payablesAgeing: Ageing;
};

const BUCKETS: { key: keyof Ageing; label: string }[] = [
  { key: "current", label: "Current" },
  { key: "days1to30", label: "1–30 days" },
  { key: "days31to60", label: "31–60 days" },
  { key: "days61to90", label: "61–90 days" },
  { key: "over90", label: "Over 90" },
];

export default function DashboardCharts({ financial }: { financial: FinancialDashboardData }) {
  const ageing = BUCKETS.map((bucket) => ({
    period: bucket.label,
    receivables: financial.receivablesAgeing?.[bucket.key] ?? 0,
    payables: financial.payablesAgeing?.[bucket.key] ?? 0,
  }));
  const workingCapital = [
    { item: "Cash at bank", amount: financial.cash ?? 0 },
    { item: "Receivables", amount: financial.receivables ?? 0 },
    { item: "Payables", amount: financial.payables ?? 0 },
  ];
  const number = (value: number) => new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 }).format(value);

  return (
    <section className="mb-8" aria-label="Graphical financial analysis">
      <h3 className="font-semibold mb-3">Graphical financial analysis</h3>
      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
        <div className="card">
          <div className="mb-3">
            <h4 className="font-semibold">Receivables vs payables ageing</h4>
            <p className="text-xs text-slate-500">Outstanding balances by overdue period, in the company reporting currency.</p>
          </div>
          <div className="h-72 w-full">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={ageing} margin={{ top: 8, right: 12, left: 4, bottom: 4 }}>
                <CartesianGrid strokeDasharray="3 3" />
                <XAxis dataKey="period" tick={{ fontSize: 10 }} />
                <YAxis tickFormatter={number} tick={{ fontSize: 10 }} width={62} />
                <Tooltip formatter={(value: number | string) => number(Number(value))} />
                <Legend />
                <Bar dataKey="receivables" name="Receivables" fill="#1597d4" radius={[4, 4, 0, 0]} />
                <Bar dataKey="payables" name="Payables" fill="#f28b28" radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>
        <div className="card">
          <div className="mb-3">
            <h4 className="font-semibold">Working capital components</h4>
            <p className="text-xs text-slate-500">Cash, outstanding customer balances and supplier balances from live sub-ledger records.</p>
          </div>
          <div className="h-72 w-full">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={workingCapital} margin={{ top: 8, right: 12, left: 4, bottom: 4 }}>
                <CartesianGrid strokeDasharray="3 3" />
                <XAxis dataKey="item" tick={{ fontSize: 10 }} />
                <YAxis tickFormatter={number} tick={{ fontSize: 10 }} width={62} />
                <Tooltip formatter={(value: number | string) => number(Number(value))} />
                <Bar dataKey="amount" name="Balance" fill="#20b6a5" radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>
      </div>
    </section>
  );
}
