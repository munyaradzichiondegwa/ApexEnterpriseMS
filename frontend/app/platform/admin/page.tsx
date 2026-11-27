"use client";

import axios from "axios";
import { FormEvent, useState } from "react";
import { API_CONFIG_ERROR, BROWSER_API_BASE } from "@/lib/config";

type Plan = { id: string; code: string; name: string; maxSeats: number; maxCompanies: number; moduleCodesJson: string; monthlyPrice?: number; currency: string; isActive: boolean };
type Tenant = { id: string; name: string; legalName?: string; isActive: boolean; companyCount: number };

export default function PlatformAdminPage() {
  const [tokenInput, setTokenInput] = useState("");
  const [token, setToken] = useState("");
  const [overview, setOverview] = useState<any>(null);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [plans, setPlans] = useState<Plan[]>([]);
  const [selectedTenant, setSelectedTenant] = useState<any>(null);
  const [planForm, setPlanForm] = useState({ code: "", name: "", maxSeats: "10", maxCompanies: "1", modules: "*", monthlyPrice: "0", currency: "USD" });
  const [tenantForm, setTenantForm] = useState({ organisationName: "", initialCompanyName: "", administratorEmail: "", planId: "", currency: "USD", fiscalYearStartMonth: "1", trialDays: "30" });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  function client() {
    return axios.create({
      baseURL: BROWSER_API_BASE,
      timeout: 20000,
      headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
    });
  }

  async function load(nextToken = token) {
    setBusy(true); setError(null); setNotice(null);
    try {
      const api = axios.create({ baseURL: BROWSER_API_BASE, timeout: 20000, headers: { Authorization: "Bearer " + nextToken } });
      const [overviewResult, tenantsResult, plansResult] = await Promise.all([
        api.get("/api/platform/overview"),
        api.get("/api/platform/tenants"),
        api.get("/api/platform/plans"),
      ]);
      setOverview(overviewResult.data);
      setTenants(tenantsResult.data.items);
      setPlans(plansResult.data);
      setToken(nextToken);
      setTenantForm((current) => ({ ...current, planId: current.planId || plansResult.data[0]?.id || "" }));
    } catch (e: any) {
      setError(e?.response?.status === 401 || e?.response?.status === 403
        ? "Platform token rejected. It must be issued by the configured platform identity issuer with platform_admin=true."
        : e?.response?.data?.error || e.message || "Unable to load platform data.");
    } finally { setBusy(false); }
  }

  async function createPlan(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError(null); setNotice(null);
    try {
      const modules = planForm.modules.split(",").map((x) => x.trim()).filter(Boolean);
      await client().post("/api/platform/plans", {
        code: planForm.code, name: planForm.name, description: "Platform-managed subscription plan",
        maxSeats: Number(planForm.maxSeats), maxCompanies: Number(planForm.maxCompanies),
        moduleCodes: modules, monthlyPrice: Number(planForm.monthlyPrice), currency: planForm.currency,
        isPubliclyAvailable: true,
      });
      await load();
      setPlanForm({ code: "", name: "", maxSeats: "10", maxCompanies: "1", modules: "*", monthlyPrice: "0", currency: "USD" });
      setNotice("Subscription plan created.");
    } catch (e: any) { setError(e?.response?.data?.error || e.message); } finally { setBusy(false); }
  }

  async function provisionTenant(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError(null); setNotice(null);
    try {
      const response = await client().post("/api/platform/tenants", {
        organisationName: tenantForm.organisationName, initialCompanyName: tenantForm.initialCompanyName,
        administratorEmail: tenantForm.administratorEmail, planId: tenantForm.planId,
        currency: tenantForm.currency, fiscalYearStartMonth: Number(tenantForm.fiscalYearStartMonth),
        trialDays: Number(tenantForm.trialDays),
      });
      const tokenText = response.data.invitationToken ? " Initial administrator token (one-time): " + response.data.invitationToken : "";
      const successMessage = "Tenant " + response.data.tenantId + " provisioned. Email sent: " + response.data.invitationEmailSent + "." + tokenText;
      setTenantForm({ organisationName: "", initialCompanyName: "", administratorEmail: "", planId: plans[0]?.id || "", currency: "USD", fiscalYearStartMonth: "1", trialDays: "30" });
      await load();
      setNotice(successMessage);
    } catch (e: any) { setError(e?.response?.data?.error || e.message); } finally { setBusy(false); }
  }

  async function inspectTenant(id: string) {
    setBusy(true); setError(null);
    try { const response = await client().get("/api/platform/tenants/" + id); setSelectedTenant(response.data); }
    catch (e: any) { setError(e?.response?.data?.error || e.message); }
    finally { setBusy(false); }
  }

  if (API_CONFIG_ERROR) return <main className="min-h-screen p-8"><p role="alert">{API_CONFIG_ERROR}</p></main>;

  if (!token) return (
    <main className="min-h-screen bg-slate-100 p-4 sm:p-8">
      <section className="max-w-2xl mx-auto card">
        <h1 className="text-2xl font-bold">ApexERP Platform Administration</h1>
        <p className="text-sm text-slate-600 mt-2">This control plane uses a separate RSA-signed platform identity. Obtain a platform access token from the configured identity issuer. Tenant administrator credentials are not accepted here.</p>
        <form className="mt-5 space-y-3" onSubmit={(e) => { e.preventDefault(); void load(tokenInput.trim()); }}>
          <label className="block text-sm font-medium">Platform access token
            <textarea className="input mt-1 min-h-32 font-mono text-xs" required value={tokenInput} onChange={(e) => setTokenInput(e.target.value)} placeholder="Paste platform-issued JWT" />
          </label>
          <button className="btn-primary" disabled={busy}>{busy ? "Verifying…" : "Open control plane"}</button>
          {error && <p role="alert" className="text-sm text-red-600">{error}</p>}
        </form>
      </section>
    </main>
  );

  return (
    <main className="min-h-screen bg-slate-100 p-4 sm:p-8">
      <div className="max-w-7xl mx-auto">
        <div className="flex flex-wrap justify-between gap-3 items-center mb-6">
          <div><h1 className="text-2xl font-bold">Platform control plane</h1><p className="text-sm text-slate-500">Customer, licensing and service operations</p></div>
          <button className="btn-secondary" onClick={() => { setToken(""); setTokenInput(""); setOverview(null); }}>End platform session</button>
        </div>
        {error && <div role="alert" className="rounded-lg bg-red-50 border border-red-200 p-3 text-sm text-red-700 mb-4">{error}</div>}
        {notice && <div role="status" className="rounded-lg bg-emerald-50 border border-emerald-200 p-3 text-sm text-emerald-800 mb-4 break-words">{notice}</div>}
        {overview && <div className="grid gap-3 grid-cols-2 md:grid-cols-4 mb-6">
          {[["Tenants", overview.tenants],["Active tenants", overview.activeTenants],["Failed jobs", overview.failedLifecycleJobs],["Pending support", overview.pendingSupportApprovals]].map(([label,value]) =>
            <section key={String(label)} className="card"><p className="text-xs text-slate-500">{label}</p><p className="text-2xl font-bold">{value}</p></section>)}
        </div>}
        <div className="grid gap-5 lg:grid-cols-2 mb-6">
          <section className="card">
            <h2 className="font-semibold mb-3">Create subscription plan</h2>
            <form onSubmit={createPlan} className="grid gap-3 sm:grid-cols-2">
              <label className="text-sm">Plan code<input className="input mt-1" required value={planForm.code} onChange={(e) => setPlanForm({ ...planForm, code: e.target.value.toUpperCase() })} /></label>
              <label className="text-sm">Plan name<input className="input mt-1" required value={planForm.name} onChange={(e) => setPlanForm({ ...planForm, name: e.target.value })} /></label>
              <label className="text-sm">Maximum seats<input className="input mt-1" type="number" min="1" value={planForm.maxSeats} onChange={(e) => setPlanForm({ ...planForm, maxSeats: e.target.value })} /></label>
              <label className="text-sm">Maximum companies<input className="input mt-1" type="number" min="1" value={planForm.maxCompanies} onChange={(e) => setPlanForm({ ...planForm, maxCompanies: e.target.value })} /></label>
              <label className="text-sm sm:col-span-2">Module codes (comma-separated; * grants all)<input className="input mt-1" value={planForm.modules} onChange={(e) => setPlanForm({ ...planForm, modules: e.target.value })} /></label>
              <label className="text-sm">Monthly price<input className="input mt-1" type="number" min="0" step="0.01" value={planForm.monthlyPrice} onChange={(e) => setPlanForm({ ...planForm, monthlyPrice: e.target.value })} /></label>
              <label className="text-sm">Currency<input className="input mt-1" maxLength={3} value={planForm.currency} onChange={(e) => setPlanForm({ ...planForm, currency: e.target.value.toUpperCase() })} /></label>
              <button className="btn-primary sm:col-span-2" disabled={busy}>Create plan</button>
            </form>
          </section>
          <section className="card">
            <h2 className="font-semibold mb-3">Provision customer tenant</h2>
            <form onSubmit={provisionTenant} className="grid gap-3 sm:grid-cols-2">
              <label className="text-sm">Organisation name<input className="input mt-1" required value={tenantForm.organisationName} onChange={(e) => setTenantForm({ ...tenantForm, organisationName: e.target.value })} /></label>
              <label className="text-sm">Initial legal company<input className="input mt-1" required value={tenantForm.initialCompanyName} onChange={(e) => setTenantForm({ ...tenantForm, initialCompanyName: e.target.value })} /></label>
              <label className="text-sm sm:col-span-2">Initial administrator email<input className="input mt-1" type="email" required value={tenantForm.administratorEmail} onChange={(e) => setTenantForm({ ...tenantForm, administratorEmail: e.target.value })} /></label>
              <label className="text-sm">Plan<select className="input mt-1" required value={tenantForm.planId} onChange={(e) => setTenantForm({ ...tenantForm, planId: e.target.value })}>{plans.map((plan) => <option key={plan.id} value={plan.id}>{plan.name}</option>)}</select></label>
              <label className="text-sm">Trial days<input className="input mt-1" type="number" min="0" max="365" value={tenantForm.trialDays} onChange={(e) => setTenantForm({ ...tenantForm, trialDays: e.target.value })} /></label>
              <label className="text-sm">Currency<input className="input mt-1" maxLength={3} value={tenantForm.currency} onChange={(e) => setTenantForm({ ...tenantForm, currency: e.target.value.toUpperCase() })} /></label>
              <label className="text-sm">Fiscal year start month<input className="input mt-1" type="number" min="1" max="12" value={tenantForm.fiscalYearStartMonth} onChange={(e) => setTenantForm({ ...tenantForm, fiscalYearStartMonth: e.target.value })} /></label>
              <button className="btn-primary sm:col-span-2" disabled={busy || !tenantForm.planId}>Provision tenant</button>
            </form>
          </section>
        </div>
        <section className="card overflow-x-auto">
          <h2 className="font-semibold mb-3">Customer tenants</h2>
          <table className="table-base"><thead><tr><th>Organisation</th><th>Legal name</th><th>Companies</th><th>Status</th><th></th></tr></thead>
            <tbody>{tenants.map((tenant) => <tr key={tenant.id}><td>{tenant.name}</td><td>{tenant.legalName || "—"}</td><td>{tenant.companyCount}</td><td>{tenant.isActive ? "Active" : "Inactive"}</td><td><button className="text-brand-700 underline" onClick={() => void inspectTenant(tenant.id)}>Inspect</button></td></tr>)}</tbody>
          </table>
        </section>
        {selectedTenant && <section className="card mt-5">
          <div className="flex justify-between"><h2 className="font-semibold">Tenant detail: {selectedTenant.tenant.name}</h2><button className="text-sm underline" onClick={() => setSelectedTenant(null)}>Close</button></div>
          <pre className="text-xs bg-slate-50 p-3 rounded mt-3 overflow-auto">{JSON.stringify(selectedTenant, null, 2)}</pre>
        </section>}
      </div>
    </main>
  );
}
