"use client";

import { useEffect, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";

export default function AdminPage() {
  const [company, setCompany] = useState<any>(null);
  const [users, setUsers] = useState<any[]>([]);
  const [roles, setRoles] = useState<any[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.get("/admin/company").then((r) => setCompany(r.data)).catch((e) => setError(extractErrorMessage(e)));
    api.get("/admin/users").then((r) => setUsers(r.data)).catch(() => {});
    api.get("/admin/roles").then((r) => setRoles(r.data)).catch(() => {});
  }, []);

  return (
    <AppShell>
      <h1 className="text-2xl font-bold mb-1">Admin & Settings</h1>
      <p className="text-slate-500 mb-6">Company profile, users, and role-based access control.</p>
      {error && <div className="text-sm text-red-600 mb-4">{error}</div>}

      {company && (
        <div className="card mb-6">
          <h2 className="font-semibold mb-3">Company</h2>
          <div className="grid grid-cols-2 gap-3 text-sm">
            <div><span className="text-slate-400">Name:</span> {company.name}</div>
            <div><span className="text-slate-400">Base Currency:</span> {company.baseCurrency}</div>
            <div><span className="text-slate-400">Sector:</span> {company.sector || "—"}</div>
            <div><span className="text-slate-400">Registration No:</span> {company.registrationNo || "—"}</div>
          </div>
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <div className="card overflow-x-auto">
          <h2 className="font-semibold mb-3">Users</h2>
          <table className="table-base">
            <thead><tr><th>Name</th><th>Email</th><th>Active</th></tr></thead>
            <tbody>{users.map((u) => (<tr key={u.id}><td>{u.firstName} {u.lastName}</td><td>{u.email}</td><td>{u.isActive ? "Yes" : "No"}</td></tr>))}</tbody>
          </table>
        </div>
        <div className="card overflow-x-auto">
          <h2 className="font-semibold mb-3">Roles</h2>
          <table className="table-base">
            <thead><tr><th>Name</th><th>System Role</th></tr></thead>
            <tbody>{roles.map((r) => (<tr key={r.id}><td>{r.name}</td><td>{r.isSystemRole ? "Yes" : "No"}</td></tr>))}</tbody>
          </table>
        </div>
      </div>
    </AppShell>
  );
}
