"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function ProjectsPage() {
  const [projects, setProjects] = useState<any[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [newProject, setNewProject] = useState({ projectNo: "", name: "", managerId: "", startDate: "", budget: 0 });

  function load() {
    api.get("/projects").then((r) => setProjects(r.data)).catch((e) => setError(extractErrorMessage(e)));
  }
  useEffect(load, []);

  async function createProject(e: React.FormEvent) {
    e.preventDefault();
    try { await api.post("/projects", newProject); load(); } catch (err) { setError(extractErrorMessage(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Project Management</h1>
      <p className="text-slate-500 mb-6">Projects, tasks, and timesheet-driven cost tracking against budget.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 card overflow-x-auto">
          <table className="table-base">
            <thead><tr><th>Project #</th><th>Name</th><th>Status</th><th>Budget</th></tr></thead>
            <tbody>{projects.map((p) => (<tr key={p.id}><td className="font-mono">{p.projectNo}</td><td>{p.name}</td><td>{p.status}</td><td className="text-right">{Number(p.budget).toFixed(2)}</td></tr>))}</tbody>
          </table>
        </div>
        <form onSubmit={createProject} className="card space-y-3 h-fit">
          <h2 className="font-semibold">New Project</h2>
          <div><label className="label">Project No</label><input className="input" required value={newProject.projectNo} onChange={(e) => setNewProject({ ...newProject, projectNo: e.target.value })} /></div>
          <div><label className="label">Name</label><input className="input" required value={newProject.name} onChange={(e) => setNewProject({ ...newProject, name: e.target.value })} /></div>
          <div><label className="label">Manager (User) ID</label><input className="input" required value={newProject.managerId} onChange={(e) => setNewProject({ ...newProject, managerId: e.target.value })} /></div>
          <div><label className="label">Start Date</label><input className="input" type="date" required value={newProject.startDate} onChange={(e) => setNewProject({ ...newProject, startDate: e.target.value })} /></div>
          <div><label className="label">Budget</label><input className="input" type="number" value={newProject.budget} onChange={(e) => setNewProject({ ...newProject, budget: Number(e.target.value) })} /></div>
          <button className="btn-primary w-full">Add Project</button>
        </form>
      </div>
    </AppShell>
  );
}
