"use client";

import clsx from "clsx";
import { ReactNode, useId, useRef } from "react";
import { CsvColumn, downloadCsv } from "@/lib/exports";
import { STATUS_TONE, humanise } from "@/lib/format";

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: string; actions?: ReactNode }) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-3 mb-6">
      <div>
        <h1 className="text-2xl font-bold">{title}</h1>
        {subtitle && <p className="text-slate-500 mt-1 max-w-3xl">{subtitle}</p>}
      </div>
      {actions && <div className="flex gap-2">{actions}</div>}
    </div>
  );
}

/** Success and error messages. Errors are announced immediately (role=alert); successes politely (role=status). */
export function Notice({ error, notice }: { error?: string | null; notice?: string | null }) {
  return (
    <div aria-live="polite">
      {error && <div role="alert" className="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>}
      {notice && !error && <div role="status" className="mb-4 rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-800">{notice}</div>}
    </div>
  );
}

export function Tabs<T extends string>({ tabs, active, onChange }: { tabs: readonly { id: T; label: string }[]; active: T; onChange: (id: T) => void }) {
  const refs = useRef<Record<string, HTMLButtonElement | null>>({});
  function onKey(e: React.KeyboardEvent, i: number) {
    if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
    const next = (i + (e.key === "ArrowRight" ? 1 : -1) + tabs.length) % tabs.length;
    onChange(tabs[next].id); refs.current[tabs[next].id]?.focus();
  }
  return (
    <div role="tablist" className="flex flex-wrap gap-1 border-b border-slate-200 mb-6">
      {tabs.map((t, i) => (
        <button
          key={t.id} ref={(el) => { refs.current[t.id] = el; }} role="tab" id={`tab-${t.id}`} aria-selected={active === t.id} aria-controls={`panel-${t.id}`}
          tabIndex={active === t.id ? 0 : -1} onClick={() => onChange(t.id)} onKeyDown={(e) => onKey(e, i)}
          className={clsx("px-4 py-2 text-sm font-medium rounded-t-lg border-b-2 -mb-px focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-500",
            active === t.id ? "border-brand-600 text-brand-700" : "border-transparent text-slate-500 hover:text-slate-800")}
        >{t.label}</button>
      ))}
    </div>
  );
}

export function Panel<T extends string>({ id, active, children }: { id: T; active: T; children: ReactNode }) {
  if (id !== active) return null;
  return <div role="tabpanel" id={`panel-${id}`} aria-labelledby={`tab-${id}`}>{children}</div>;
}

export function Stat({ label, value, hint, tone = "neutral" }: { label: string; value: ReactNode; hint?: string; tone?: "good" | "warn" | "bad" | "neutral" }) {
  return (
    <div className="card">
      <div className="text-xs uppercase text-slate-400 font-medium">{label}</div>
      <div className={clsx("text-2xl font-bold mt-1", tone === "bad" && "text-red-600", tone === "warn" && "text-amber-600", tone === "good" && "text-emerald-700")}>{value}</div>
      {hint && <div className="text-xs text-slate-500 mt-1">{hint}</div>}
    </div>
  );
}

export function Badge({ text }: { text: string }) {
  const tone = STATUS_TONE[text] ?? "neutral";
  const classes = { good: "bg-emerald-100 text-emerald-800", warn: "bg-amber-100 text-amber-800", bad: "bg-red-100 text-red-800", info: "bg-sky-100 text-sky-800", neutral: "bg-slate-100 text-slate-700" }[tone];
  return <span className={clsx("inline-block rounded-full px-2 py-0.5 text-xs font-medium", classes)}>{humanise(text)}</span>;
}

export function Field({ label, children, hint, htmlFor }: { label: string; children: ReactNode; hint?: string; htmlFor?: string }) {
  return (
    <div>
      <label className="label" htmlFor={htmlFor}>{label}</label>
      {children}
      {hint && <p className="text-xs text-slate-500 mt-1">{hint}</p>}
    </div>
  );
}

/** A labelled input that wires the label to the control for screen readers. */
export function TextField({ label, hint, className, ...props }: { label: string; hint?: string } & React.InputHTMLAttributes<HTMLInputElement>) {
  const id = useId();
  return <Field label={label} hint={hint} htmlFor={id}><input id={id} className={clsx("input", className)} {...props} /></Field>;
}

export function SelectField({ label, hint, options, placeholder, ...props }: { label: string; hint?: string; options: readonly { value: string; label: string }[]; placeholder?: string } & React.SelectHTMLAttributes<HTMLSelectElement>) {
  const id = useId();
  return (
    <Field label={label} hint={hint} htmlFor={id}>
      <select id={id} className="input" {...props}>
        <option value="">{placeholder ?? "Select…"}</option>
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </Field>
  );
}

export interface Column<T> { header: string; cell: (row: T) => ReactNode; csv?: (row: T) => string | number | null | undefined; align?: "right" }

/** An accessible table with a caption, scoped headers, an empty state and CSV export of exactly what is shown. */
export function DataTable<T>({ caption, columns, rows, empty = "Nothing to show yet.", exportName, rowKey }: {
  caption: string; columns: readonly Column<T>[]; rows: readonly T[]; empty?: string; exportName?: string; rowKey: (row: T) => string;
}) {
  const csvColumns: CsvColumn<T>[] = columns.map((c) => ({ header: c.header, value: (r) => (c.csv ? c.csv(r) : (typeof c.cell(r) === "string" || typeof c.cell(r) === "number" ? (c.cell(r) as string | number) : "")) }));
  return (
    <div>
      {exportName && rows.length > 0 && (
        <div className="flex justify-end mb-2">
          <button type="button" className="btn-secondary text-sm" onClick={() => downloadCsv(exportName, rows, csvColumns)}>Export CSV</button>
        </div>
      )}
      <div className="overflow-x-auto">
        <table className="table-base">
          <caption className="sr-only">{caption}</caption>
          <thead><tr>{columns.map((c) => <th key={c.header} scope="col" className={clsx(c.align === "right" && "text-right")}>{c.header}</th>)}</tr></thead>
          <tbody>
            {rows.length === 0 && <tr><td colSpan={columns.length} className="text-slate-500 py-6 text-center">{empty}</td></tr>}
            {rows.map((r) => <tr key={rowKey(r)}>{columns.map((c) => <td key={c.header} className={clsx(c.align === "right" && "text-right tabular-nums")}>{c.cell(r)}</td>)}</tr>)}
          </tbody>
        </table>
      </div>
    </div>
  );
}

export function Section({ title, description, children }: { title: string; description?: string; children: ReactNode }) {
  return (
    <section className="card mb-6" aria-label={title}>
      <h2 className="text-lg font-semibold">{title}</h2>
      {description && <p className="text-sm text-slate-500 mt-1 mb-4">{description}</p>}
      {!description && <div className="mb-3" />}
      {children}
    </section>
  );
}

export function Loading({ what = "data" }: { what?: string }) {
  return <p className="text-sm text-slate-500" role="status">Loading {what}…</p>;
}
