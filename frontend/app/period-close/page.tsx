"use client";

import { useState } from "react";
import AppShell from "@/components/AppShell";
import { Badge, DataTable, Loading, Notice, PageHeader, Section, SelectField, Stat } from "@/components/ui";
import { api } from "@/lib/api";
import { fmtDate } from "@/lib/format";
import { useAction, useLoad } from "@/lib/hooks";

type Row = Record<string, any>;

export default function PeriodClosePage() {
  const periods = useLoad<Row[]>("/gl/periods", []);
  const [periodId, setPeriodId] = useState("");
  const tasks = useLoad<Row[]>(periodId ? `/controls/ops/period-close/${periodId}` : null, []);
  const act = useAction();
  const period = periods.data.find((p) => p.id === periodId);
  const done = tasks.data.filter((t) => t.status === "Done" || t.status === "Waived").length;
  const ready = tasks.data.length > 0 && done === tasks.data.length;
  const doIt = (taskId: string, verb: "complete" | "certify" | "waive", promptText: string, msg: string) => {
    const note = window.prompt(promptText); if (!note) return;
    act.run(async () => { await api.post(`/controls/ops/period-close/tasks/${taskId}/${verb}`, { note }); await tasks.reload(); }, msg);
  };
  return (
    <AppShell>
      <PageHeader title="Period close" subtitle="A checklist that must be complete before a period is locked. Automatic checks read live data; manual steps need a named person and a note. The final certification can only be given once everything else is done." />
      <Notice error={act.error ?? periods.error ?? tasks.error} notice={act.notice} />
      <Section title="Choose a period">
        <div className="max-w-sm">
          <SelectField label="Accounting period" value={periodId} onChange={(e) => setPeriodId(e.target.value)} placeholder={periods.loading ? "Loading…" : "Select a period"}
            options={periods.data.filter((p) => p.status === "Open").map((p) => ({ value: p.id, label: `${p.year}-${String(p.periodNo).padStart(2, "0")} (ends ${fmtDate(p.endDate)})` }))} hint="Only open periods are listed." />
        </div>
      </Section>
      {periodId && (
        <>
          <div className="grid sm:grid-cols-3 gap-4 mb-6">
            <Stat label="Tasks complete" value={`${done} / ${tasks.data.length}`} tone={ready ? "good" : "warn"} />
            <Stat label="Blocked" value={tasks.data.filter((t) => t.status === "Blocked").length} tone={tasks.data.some((t) => t.status === "Blocked") ? "bad" : "good"} />
            <Stat label="Period" value={period ? `${period.year}-${String(period.periodNo).padStart(2, "0")}` : ""} hint={period ? `Ends ${fmtDate(period.endDate)}` : undefined} />
          </div>
          <Section title="Checklist">
            {tasks.loading ? <Loading what="checklist" /> : (
              <DataTable caption="Period close checklist" exportName="period-close" rowKey={(r) => r.id} rows={tasks.data} columns={[
                { header: "Step", cell: (r) => r.description }, { header: "Type", cell: (r) => (r.isAutomatic ? "Automatic" : "Manual") },
                { header: "Status", cell: (r) => <Badge text={r.status} />, csv: (r) => r.status }, { header: "Detail", cell: (r) => r.note ?? "" },
                { header: "Action", cell: (r) => (
                  <div className="flex gap-2">
                    {!r.isAutomatic && r.status !== "Done" && r.code !== "MANAGER_CERTIFICATION" && <button className="btn-secondary text-xs" onClick={() => doIt(r.id, "complete", "Note for the record:", "Step completed.")}>Mark done</button>}
                    {r.code === "MANAGER_CERTIFICATION" && r.status !== "Done" && <button className="btn-primary text-xs" onClick={() => doIt(r.id, "certify", "Certification statement:", "Period certified.")}>Certify</button>}
                    {r.status === "Blocked" && <button className="btn-secondary text-xs" onClick={() => doIt(r.id, "waive", "Reason for waiving (at least 15 characters):", "Waived with reason recorded.")}>Waive</button>}
                  </div>) }]} />)}
            <div className="mt-4 flex gap-2">
              <button className="btn-secondary" onClick={() => tasks.reload()}>Re-check automatic steps</button>
              <button className="btn-primary" disabled={!ready || act.busy} title={ready ? "" : "Finish every step first"}
                onClick={() => { if (window.confirm("Close this period? No further postings will be accepted.")) act.run(async () => { await api.post(`/gl/periods/${periodId}/close`); await periods.reload(); setPeriodId(""); }, "Period closed."); }}>Close period</button>
            </div>
          </Section>
        </>)}
    </AppShell>
  );
}
