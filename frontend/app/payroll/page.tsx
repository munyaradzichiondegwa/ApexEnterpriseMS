"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function PayrollPage() {
  const [employees, setEmployees] = useState<any[]>([]);
  const [runs, setRuns] = useState<any[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [newEmployee, setNewEmployee] = useState({ employeeNo: "", firstName: "", lastName: "", position: "", startDate: "", basicSalary: 0 });
  const [runForm, setRunForm] = useState({ month: new Date().getMonth() + 1, year: new Date().getFullYear() });

  function load() {
    api.get("/payroll/employees").then((r) => setEmployees(r.data)).catch((e) => setError(extractErrorMessage(e)));
    api.get("/payroll/runs").then((r) => setRuns(r.data)).catch(() => {});
  }
  useEffect(load, []);

  async function createEmployee(e: React.FormEvent) {
    e.preventDefault();
    try {
      await api.post("/payroll/employees", newEmployee);
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  async function runPayroll(e: React.FormEvent) {
    e.preventDefault();
    try {
      const { data } = await api.post("/payroll/run", { month: runForm.month, year: runForm.year, allowances: {} });
      setMsg(`Payroll run created with ${data.lines.length} employee lines. Total net pay: ${data.lines.reduce((s: number, l: any) => s + l.netPay, 0).toFixed(2)}`);
      load();
    } catch (err) { setError(extractErrorMessage(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Payroll & HR</h1>
      <p className="text-slate-500 mb-6">PAYE via ZIMRA tax bands, NSSA 3%/3%, AIDS Levy 3% of PAYE.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}
      {msg && <div className="text-sm text-brand-700 bg-brand-50 border border-brand-200 rounded-lg px-3 py-2 mb-4">{msg}</div>}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6 mb-6">
        <div className="lg:col-span-2 card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>Employee No</th><th>Name</th><th>Position</th><th>Basic Salary</th></tr></thead>
            <tbody>
              {employees.map((e) => (
                <tr key={e.id}><td className="font-mono">{e.employeeNo}</td><td>{e.firstName} {e.lastName}</td><td>{e.position}</td><td className="text-right">{Number(e.basicSalary).toFixed(2)}</td></tr>
              ))}
            </tbody>
          </table>
        </div>
        <form onSubmit={createEmployee} className="card space-y-3 h-fit">
          <h2 className="font-semibold">New Employee</h2>
          <div><label className="label">Employee No</label><input className="input" required value={newEmployee.employeeNo} onChange={(e) => setNewEmployee({ ...newEmployee, employeeNo: e.target.value })} /></div>
          <div className="grid grid-cols-2 gap-2">
            <div><label className="label">First Name</label><input className="input" required value={newEmployee.firstName} onChange={(e) => setNewEmployee({ ...newEmployee, firstName: e.target.value })} /></div>
            <div><label className="label">Last Name</label><input className="input" required value={newEmployee.lastName} onChange={(e) => setNewEmployee({ ...newEmployee, lastName: e.target.value })} /></div>
          </div>
          <div><label className="label">Position</label><input className="input" value={newEmployee.position} onChange={(e) => setNewEmployee({ ...newEmployee, position: e.target.value })} /></div>
          <div><label className="label">Start Date</label><input className="input" type="date" required value={newEmployee.startDate} onChange={(e) => setNewEmployee({ ...newEmployee, startDate: e.target.value })} /></div>
          <div><label className="label">Basic Salary (USD)</label><input className="input" type="number" required value={newEmployee.basicSalary} onChange={(e) => setNewEmployee({ ...newEmployee, basicSalary: Number(e.target.value) })} /></div>
          <button className="btn-primary w-full">Add Employee</button>
        </form>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 card overflow-x-auto">
          <h2 className="font-semibold mb-3">Payroll Runs</h2>
          <table className="table-base">
            <thead><tr><th>Period</th><th>Status</th></tr></thead>
            <tbody>
              {runs.map((r) => (<tr key={r.id}><td>{r.periodMonth}/{r.periodYear}</td><td>{r.status}</td></tr>))}
            </tbody>
          </table>
        </div>
        <form onSubmit={runPayroll} className="card space-y-3 h-fit">
          <h2 className="font-semibold">Run Payroll</h2>
          <div><label className="label">Month</label><input className="input" type="number" min={1} max={12} value={runForm.month} onChange={(e) => setRunForm({ ...runForm, month: Number(e.target.value) })} /></div>
          <div><label className="label">Year</label><input className="input" type="number" value={runForm.year} onChange={(e) => setRunForm({ ...runForm, year: Number(e.target.value) })} /></div>
          <button className="btn-primary w-full">Run Payroll</button>
          <p className="text-xs text-slate-400">Configure PAYE bands under ZW Tax Compliance before your first run.</p>
        </form>
      </div>
    </AppShell>
  );
}
