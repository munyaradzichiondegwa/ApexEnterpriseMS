"use client";

import { FormEvent, useEffect, useMemo, useState } from "react";
import AppShell from "@/components/AppShell";
import { api, extractErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth";

type Company = { id: string; name: string; functionalCurrency: string; baseCurrency: string; fiscalYearStartMonth: number; sector?: string | null; isActive: boolean; parentCompanyId?: string | null };
type Branch = { id: string; code: string; name: string; parentBranchId?: string | null; isActive: boolean };
type Department = { id: string; name: string; branchId?: string | null; costCentreId?: string | null; managerId?: string | null; isActive: boolean };
type CostCentre = { id: string; code: string; name: string; parentId?: string | null; branchId?: string | null; isActive: boolean };
type Member = { id: string; userId: string; email: string; firstName: string; lastName: string; membershipRole: string; isActive: boolean };
type Role = { id: string; name: string; isSystemRole: boolean };
type Profile = { id: string; code: string; name: string; sector: string; configurationJson: string };
type Country = { countryCode: string; countryName: string; defaultCurrency: string; defaultTimeZone: string; defaultReportingFramework: string };
type Subscription = { status: string; plan?: { code: string; name: string; maxSeats: number; maxCompanies: number; moduleCodesJson: string; monthlyPrice?: number; currency: string }; trialEndsAt?: string; currentPeriodEndsAt?: string; usage: { seats: number; companyCount: number }; limits: { seats: number; companies: number } };
type Onboarding = { status: string; completedStepsJson: string; deploymentProfileId?: string | null; completedAt?: string | null };

const STEPS = [
  ["organisation", "Organisation profile"],
  ["companies", "Legal entities"],
  ["branches", "Branches"],
  ["members", "Users and memberships"],
  ["jurisdiction", "Jurisdiction and currency"],
  ["modules", "Licensed modules"],
  ["workflow", "Approval workflows"],
  ["opening-balances", "Opening balances"],
  ["review", "Final review"],
] as const;

export default function OrganisationPage() {
  const { user } = useAuth();
  const [companies, setCompanies] = useState<Company[]>([]);
  const [selectedCompanyId, setSelectedCompanyId] = useState("");
  const [branches, setBranches] = useState<Branch[]>([]);
  const [departments, setDepartments] = useState<Department[]>([]);
  const [costCentres, setCostCentres] = useState<CostCentre[]>([]);
  const [members, setMembers] = useState<Member[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [countries, setCountries] = useState<Country[]>([]);
  const [jurisdictions, setJurisdictions] = useState<any[]>([]);
  const [subscription, setSubscription] = useState<Subscription | null>(null);
  const [onboarding, setOnboarding] = useState<Onboarding | null>(null);
  const [completedSteps, setCompletedSteps] = useState<string[]>([]);
  const [profileId, setProfileId] = useState("");
  const [tab, setTab] = useState("overview");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const [companyForm, setCompanyForm] = useState({ name: "", currency: "USD", fiscalYearStartMonth: "1" });
  const [branchForm, setBranchForm] = useState({ code: "", name: "", parentBranchId: "" });
  const [departmentForm, setDepartmentForm] = useState({ name: "", branchId: "", costCentreId: "" });
  const [costCentreForm, setCostCentreForm] = useState({ code: "", name: "", parentId: "", branchId: "" });
  const [editingBranchId, setEditingBranchId] = useState("");
  const [editingDepartmentId, setEditingDepartmentId] = useState("");
  const [editingCostCentreId, setEditingCostCentreId] = useState("");
  const [inviteForm, setInviteForm] = useState({ email: "", roleId: "" });
  const [jurisdictionForm, setJurisdictionForm] = useState({ countryCode: "ZW", functionalCurrency: "ZWG", baseCurrency: "ZWG", fiscalYearStartMonth: "1", reportingFramework: "IFRS", timezone: "Africa/Harare" });

  const selectedCompany = useMemo(() => companies.find((x) => x.id === selectedCompanyId) ?? null, [companies, selectedCompanyId]);

  async function loadCore() {
    const [companyResult, subscriptionResult, profileResult, countryResult, jurisdictionResult, onboardingResult] = await Promise.allSettled([
      api.get("/organisation/companies"),
      api.get("/organisation/subscription"),
      api.get("/organisation/deployment-profiles"),
      api.get("/organisation/jurisdiction-catalogue"),
      api.get("/organisation/jurisdictions"),
      api.get("/organisation/onboarding"),
    ]);
    if (companyResult.status === "fulfilled") {
      const items = companyResult.value.data as Company[];
      setCompanies(items);
      setSelectedCompanyId((current) => current || items.find((x) => x.id === user?.companyId)?.id || items[0]?.id || "");
    } else throw companyResult.reason;
    if (subscriptionResult.status === "fulfilled") setSubscription(subscriptionResult.value.data);
    if (profileResult.status === "fulfilled") setProfiles(profileResult.value.data);
    if (countryResult.status === "fulfilled") setCountries(countryResult.value.data);
    if (jurisdictionResult.status === "fulfilled") setJurisdictions(jurisdictionResult.value.data);
    if (onboardingResult.status === "fulfilled") {
      const value = onboardingResult.value.data as Onboarding;
      setOnboarding(value);
      try { setCompletedSteps(JSON.parse(value.completedStepsJson || "[]")); } catch { setCompletedSteps([]); }
      setProfileId(value.deploymentProfileId || "");
    }
  }

  async function loadCompany(companyId: string) {
    if (!companyId) return;
    const [branchResult, memberResult, roleResult, departmentResult, costCentreResult] = await Promise.allSettled([
      api.get(`/organisation/companies/${companyId}/branches`),
      api.get(`/organisation/companies/${companyId}/members`),
      api.get(`/organisation/companies/${companyId}/roles`),
      api.get(`/organisation/companies/${companyId}/departments`),
      api.get(`/organisation/companies/${companyId}/cost-centres`),
    ]);
    if (branchResult.status === "fulfilled") setBranches(branchResult.value.data);
    if (departmentResult.status === "fulfilled") setDepartments(departmentResult.value.data);
    if (costCentreResult.status === "fulfilled") setCostCentres(costCentreResult.value.data);
    if (memberResult.status === "fulfilled") setMembers(memberResult.value.data);
    if (roleResult.status === "fulfilled") {
      setRoles(roleResult.value.data);
      setInviteForm((current) => ({ ...current, roleId: current.roleId || roleResult.value.data[0]?.id || "" }));
    }
  }

  useEffect(() => {
    loadCore().catch((e) => setError(extractErrorMessage(e)));
  }, [user?.userId]);

  useEffect(() => {
    loadCompany(selectedCompanyId).catch((e) => setError(extractErrorMessage(e)));
  }, [selectedCompanyId]);

  function begin() { setError(null); setNotice(null); setBusy(true); }
  function finish() { setBusy(false); }

  async function createCompany(event: FormEvent) {
    event.preventDefault(); begin();
    try {
      const response = await api.post("/organisation/companies", {
        name: companyForm.name, currency: companyForm.currency,
        fiscalYearStartMonth: Number(companyForm.fiscalYearStartMonth),
      });
      await loadCore();
      setSelectedCompanyId(response.data.id);
      setCompanyForm({ name: "", currency: "USD", fiscalYearStartMonth: "1" });
      setNotice("Legal entity created.");
    } catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function createBranch(event: FormEvent) {
    event.preventDefault(); if (!selectedCompanyId) return; begin();
    try {
      await api.post(`/organisation/companies/${selectedCompanyId}/branches`, {
        code: branchForm.code, name: branchForm.name, parentBranchId: branchForm.parentBranchId || null,
      });
      await loadCompany(selectedCompanyId);
      setBranchForm({ code: "", name: "", parentBranchId: "" });
      setNotice("Branch created.");
    } catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function createDepartment(event: FormEvent) {
    event.preventDefault(); if (!selectedCompanyId) return; begin();
    try {
      await api.post(`/organisation/companies/${selectedCompanyId}/departments`, {
        name: departmentForm.name, branchId: departmentForm.branchId || null,
        costCentreId: departmentForm.costCentreId || null,
      });
      await loadCompany(selectedCompanyId);
      setDepartmentForm({ name: "", branchId: "", costCentreId: "" });
      setNotice("Department created.");
    } catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function createCostCentre(event: FormEvent) {
    event.preventDefault(); if (!selectedCompanyId) return; begin();
    try {
      await api.post(`/organisation/companies/${selectedCompanyId}/cost-centres`, {
        code: costCentreForm.code, name: costCentreForm.name,
        parentId: costCentreForm.parentId || null, branchId: costCentreForm.branchId || null,
      });
      await loadCompany(selectedCompanyId);
      setCostCentreForm({ code: "", name: "", parentId: "", branchId: "" });
      setNotice("Cost centre created.");
    } catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function saveBranch(event: FormEvent) {
    event.preventDefault(); if (!selectedCompanyId || !editingBranchId) return; begin();
    try {
      await api.put(`/organisation/companies/${selectedCompanyId}/branches/${editingBranchId}`, {
        code: branchForm.code, name: branchForm.name, parentBranchId: branchForm.parentBranchId || null,
      });
      await loadCompany(selectedCompanyId); setEditingBranchId("");
      setBranchForm({ code: "", name: "", parentBranchId: "" }); setNotice("Branch updated.");
    } catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function deactivateBranch(id: string) {
    if (!selectedCompanyId || !window.confirm("Deactivate this branch? Active child records must be reassigned first.")) return;
    begin();
    try { await api.put(`/organisation/companies/${selectedCompanyId}/branches/${id}/deactivate`); await loadCompany(selectedCompanyId); setNotice("Branch deactivated."); }
    catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function saveDepartment(event: FormEvent) {
    event.preventDefault(); if (!selectedCompanyId || !editingDepartmentId) return; begin();
    try {
      await api.put(`/organisation/companies/${selectedCompanyId}/departments/${editingDepartmentId}`, {
        name: departmentForm.name, branchId: departmentForm.branchId || null, costCentreId: departmentForm.costCentreId || null,
      });
      await loadCompany(selectedCompanyId); setEditingDepartmentId("");
      setDepartmentForm({ name: "", branchId: "", costCentreId: "" }); setNotice("Department updated.");
    } catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function deactivateDepartment(id: string) {
    if (!selectedCompanyId || !window.confirm("Deactivate this department? Active employees must be reassigned first.")) return;
    begin();
    try { await api.put(`/organisation/companies/${selectedCompanyId}/departments/${id}/deactivate`); await loadCompany(selectedCompanyId); setNotice("Department deactivated."); }
    catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function saveCostCentre(event: FormEvent) {
    event.preventDefault(); if (!selectedCompanyId || !editingCostCentreId) return; begin();
    try {
      await api.put(`/organisation/companies/${selectedCompanyId}/cost-centres/${editingCostCentreId}`, {
        code: costCentreForm.code, name: costCentreForm.name, parentId: costCentreForm.parentId || null, branchId: costCentreForm.branchId || null,
      });
      await loadCompany(selectedCompanyId); setEditingCostCentreId("");
      setCostCentreForm({ code: "", name: "", parentId: "", branchId: "" }); setNotice("Cost centre updated.");
    } catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function deactivateCostCentre(id: string) {
    if (!selectedCompanyId || !window.confirm("Deactivate this cost centre? Active child cost centres and departments must be reassigned first.")) return;
    begin();
    try { await api.put(`/organisation/companies/${selectedCompanyId}/cost-centres/${id}/deactivate`); await loadCompany(selectedCompanyId); setNotice("Cost centre deactivated."); }
    catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function inviteMember(event: FormEvent) {
    event.preventDefault(); if (!selectedCompanyId) return; begin();
    try {
      const response = await api.post(`/organisation/companies/${selectedCompanyId}/invitations`, inviteForm);
      const token = response.data.invitationToken;
      setNotice(response.data.invitationEmailSent
        ? "Invitation email sent."
        : `Invitation created. Copy this one-time token and deliver it securely: ${token}`);
      setInviteForm((current) => ({ ...current, email: "" }));
    } catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function saveJurisdiction(event: FormEvent) {
    event.preventDefault(); if (!selectedCompanyId) return; begin();
    try {
      await api.put("/organisation/jurisdictions", {
        ...jurisdictionForm, fiscalYearStartMonth: Number(jurisdictionForm.fiscalYearStartMonth),
        isPrimary: true, companyId: selectedCompanyId,
      });
      const response = await api.get("/organisation/jurisdictions");
      setJurisdictions(response.data);
      setNotice("Jurisdiction configuration saved. Statutory tax configuration remains pending legal review.");
    } catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function saveOnboarding(completed: boolean) {
    begin();
    try {
      const response = await api.put("/organisation/onboarding", {
        completedSteps, deploymentProfileId: profileId || null, completed,
      });
      setOnboarding({ ...onboarding!, ...response.data });
      setNotice(completed ? "Onboarding marked complete." : "Onboarding progress saved.");
    } catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  async function requestRenewal() {
    begin();
    try {
      const response = await api.post("/organisation/subscription/renewal-request");
      setNotice(`Renewal request ${response.data.requestId} submitted.`);
    } catch (e) { setError(extractErrorMessage(e)); } finally { finish(); }
  }

  function toggleStep(step: string) {
    setCompletedSteps((current) => current.includes(step) ? current.filter((x) => x !== step) : [...current, step]);
  }

  return (
    <AppShell>
      <div className="flex flex-wrap items-start justify-between gap-3 mb-6">
        <div>
          <h1 className="text-2xl font-bold">Organisation & Licensing</h1>
          <p className="text-sm text-slate-500">Manage legal entities, branches, memberships, jurisdictions and subscription usage.</p>
        </div>
        <select aria-label="Company being managed" className="input max-w-xs" value={selectedCompanyId}
          onChange={(e) => setSelectedCompanyId(e.target.value)}>
          {companies.map((company) => <option key={company.id} value={company.id}>{company.name}</option>)}
        </select>
      </div>

      {error && <div role="alert" className="rounded-lg border border-red-200 bg-red-50 text-red-700 p-3 mb-4 text-sm">{error}</div>}
      {notice && <div role="status" className="rounded-lg border border-emerald-200 bg-emerald-50 text-emerald-800 p-3 mb-4 text-sm break-words">{notice}</div>}

      <div className="flex flex-wrap gap-2 mb-5">
        {["overview", "companies", "branches", "departments", "cost-centres", "members", "jurisdiction", "onboarding"].map((item) =>
          <button key={item} type="button" onClick={() => setTab(item)}
            className={`rounded-lg px-3 py-2 text-sm font-medium ${tab === item ? "bg-brand-700 text-white" : "bg-white border text-slate-600"}`}>
            {item[0].toUpperCase() + item.slice(1)}
          </button>)}
      </div>

      {tab === "overview" && (
        <div className="grid gap-4 md:grid-cols-2">
          <section className="card">
            <h2 className="font-semibold mb-3">Subscription</h2>
            {subscription ? <>
              <p className="text-sm"><span className="text-slate-500">Plan:</span> {subscription.plan?.name ?? "—"} ({subscription.status})</p>
              <p className="text-sm mt-1"><span className="text-slate-500">Seats:</span> {subscription.usage.seats} / {subscription.limits.seats}</p>
              <p className="text-sm mt-1"><span className="text-slate-500">Companies:</span> {subscription.usage.companyCount} / {subscription.limits.companies}</p>
              <p className="text-sm mt-1"><span className="text-slate-500">Period ends:</span> {subscription.currentPeriodEndsAt ? new Date(subscription.currentPeriodEndsAt).toLocaleDateString() : "—"}</p>
              <button type="button" onClick={requestRenewal} disabled={busy} className="btn-primary mt-4">Request renewal / upgrade</button>
            </> : <p className="text-sm text-slate-500">Subscription details unavailable.</p>}
          </section>
          <section className="card">
            <h2 className="font-semibold mb-3">Tenant structure</h2>
            <p className="text-sm">Active legal entities: <strong>{companies.filter((x) => x.isActive).length}</strong></p>
            <p className="text-sm mt-1">Selected company: <strong>{selectedCompany?.name ?? "—"}</strong></p>
            <p className="text-sm mt-1">Sector profile: <strong>{selectedCompany?.sector ?? "Not configured"}</strong></p>
            <p className="text-sm mt-1">Branches: <strong>{branches.length}</strong></p>
            <p className="text-sm mt-1">Active memberships: <strong>{members.filter((x) => x.isActive).length}</strong></p>
          </section>
        </div>
      )}

      {tab === "companies" && (
        <section className="card">
          <h2 className="font-semibold mb-3">Legal entities</h2>
          <div className="overflow-x-auto mb-5">
            <table className="table-base"><thead><tr><th>Name</th><th>Functional currency</th><th>Fiscal year start</th><th>Status</th></tr></thead>
              <tbody>{companies.map((x) => <tr key={x.id}><td>{x.name}</td><td>{x.functionalCurrency}</td><td>{x.fiscalYearStartMonth}</td><td>{x.isActive ? "Active" : "Inactive"}</td></tr>)}</tbody>
            </table>
          </div>
          <form onSubmit={createCompany} className="grid gap-3 md:grid-cols-4 items-end">
            <label className="text-sm">Company name<input className="input mt-1" required value={companyForm.name} onChange={(e) => setCompanyForm({ ...companyForm, name: e.target.value })} /></label>
            <label className="text-sm">Currency<input className="input mt-1" maxLength={3} required value={companyForm.currency} onChange={(e) => setCompanyForm({ ...companyForm, currency: e.target.value.toUpperCase() })} /></label>
            <label className="text-sm">Fiscal year start month<input className="input mt-1" type="number" min={1} max={12} required value={companyForm.fiscalYearStartMonth} onChange={(e) => setCompanyForm({ ...companyForm, fiscalYearStartMonth: e.target.value })} /></label>
            <button className="btn-primary" disabled={busy}>Add company</button>
          </form>
        </section>
      )}

      {tab === "branches" && (
        <section className="card">
          <h2 className="font-semibold mb-3">Branches — {selectedCompany?.name}</h2>
          <div className="overflow-x-auto mb-5">
            <table className="table-base"><thead><tr><th>Code</th><th>Name</th><th>Parent</th><th>Status</th><th>Actions</th></tr></thead>
              <tbody>{branches.map((x) => <tr key={x.id}><td>{x.code}</td><td>{x.name}</td><td>{branches.find((b) => b.id === x.parentBranchId)?.name ?? "—"}</td><td>{x.isActive ? "Active" : "Inactive"}</td><td className="whitespace-nowrap"><button type="button" className="text-brand-700 mr-3" onClick={() => { setEditingBranchId(x.id); setBranchForm({ code: x.code, name: x.name, parentBranchId: x.parentBranchId ?? "" }); }}>Edit</button>{x.isActive && <button type="button" className="text-red-700" disabled={busy} onClick={() => deactivateBranch(x.id)}>Deactivate</button>}</td></tr>)}</tbody>
            </table>
          </div>
          <form onSubmit={editingBranchId ? saveBranch : createBranch} className="grid gap-3 md:grid-cols-4 items-end">
            <label className="text-sm">Branch code<input className="input mt-1" required value={branchForm.code} onChange={(e) => setBranchForm({ ...branchForm, code: e.target.value.toUpperCase() })} /></label>
            <label className="text-sm">Branch name<input className="input mt-1" required value={branchForm.name} onChange={(e) => setBranchForm({ ...branchForm, name: e.target.value })} /></label>
            <label className="text-sm">Parent branch<select className="input mt-1" value={branchForm.parentBranchId} onChange={(e) => setBranchForm({ ...branchForm, parentBranchId: e.target.value })}><option value="">No parent</option>{branches.filter((b) => b.isActive && b.id !== editingBranchId).map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}</select></label>
            <button className="btn-primary" disabled={busy}>{editingBranchId ? "Save branch" : "Add branch"}</button>
            {editingBranchId && <button type="button" className="btn-secondary" onClick={() => { setEditingBranchId(""); setBranchForm({ code: "", name: "", parentBranchId: "" }); }}>Cancel edit</button>}
          </form>
        </section>
      )}

      {tab === "departments" && (
        <section className="card">
          <h2 className="font-semibold mb-3">Departments — {selectedCompany?.name}</h2>
          <div className="overflow-x-auto mb-5">
            <table className="table-base"><thead><tr><th>Name</th><th>Branch</th><th>Cost centre</th><th>Status</th><th>Actions</th></tr></thead>
              <tbody>{departments.map((x) => <tr key={x.id}><td>{x.name}</td><td>{branches.find((b) => b.id === x.branchId)?.name ?? "—"}</td><td>{costCentres.find((c) => c.id === x.costCentreId)?.code ?? "—"}</td><td>{x.isActive ? "Active" : "Inactive"}</td><td className="whitespace-nowrap"><button type="button" className="text-brand-700 mr-3" onClick={() => { setEditingDepartmentId(x.id); setDepartmentForm({ name: x.name, branchId: x.branchId ?? "", costCentreId: x.costCentreId ?? "" }); }}>Edit</button>{x.isActive && <button type="button" className="text-red-700" disabled={busy} onClick={() => deactivateDepartment(x.id)}>Deactivate</button>}</td></tr>)}</tbody>
            </table>
          </div>
          <form onSubmit={editingDepartmentId ? saveDepartment : createDepartment} className="grid gap-3 md:grid-cols-4 items-end">
            <label className="text-sm">Department name<input className="input mt-1" required value={departmentForm.name} onChange={(e) => setDepartmentForm({ ...departmentForm, name: e.target.value })} /></label>
            <label className="text-sm">Branch<select className="input mt-1" value={departmentForm.branchId} onChange={(e) => setDepartmentForm({ ...departmentForm, branchId: e.target.value })}><option value="">No branch</option>{branches.filter((b) => b.isActive).map((b) => <option key={b.id} value={b.id}>{b.code} — {b.name}</option>)}</select></label>
            <label className="text-sm">Cost centre<select className="input mt-1" value={departmentForm.costCentreId} onChange={(e) => setDepartmentForm({ ...departmentForm, costCentreId: e.target.value })}><option value="">No cost centre</option>{costCentres.filter((x) => x.isActive).map((x) => <option key={x.id} value={x.id}>{x.code} — {x.name}</option>)}</select></label>
            <button className="btn-primary" disabled={busy}>{editingDepartmentId ? "Save department" : "Add department"}</button>
            {editingDepartmentId && <button type="button" className="btn-secondary" onClick={() => { setEditingDepartmentId(""); setDepartmentForm({ name: "", branchId: "", costCentreId: "" }); }}>Cancel edit</button>}
          </form>
        </section>
      )}

      {tab === "cost-centres" && (
        <section className="card">
          <h2 className="font-semibold mb-3">Cost centres — {selectedCompany?.name}</h2>
          <div className="overflow-x-auto mb-5">
            <table className="table-base"><thead><tr><th>Code</th><th>Name</th><th>Parent</th><th>Branch</th><th>Status</th><th>Actions</th></tr></thead>
              <tbody>{costCentres.map((x) => <tr key={x.id}><td>{x.code}</td><td>{x.name}</td><td>{costCentres.find((p) => p.id === x.parentId)?.name ?? "—"}</td><td>{branches.find((b) => b.id === x.branchId)?.name ?? "—"}</td><td>{x.isActive ? "Active" : "Inactive"}</td><td className="whitespace-nowrap"><button type="button" className="text-brand-700 mr-3" onClick={() => { setEditingCostCentreId(x.id); setCostCentreForm({ code: x.code, name: x.name, parentId: x.parentId ?? "", branchId: x.branchId ?? "" }); }}>Edit</button>{x.isActive && <button type="button" className="text-red-700" disabled={busy} onClick={() => deactivateCostCentre(x.id)}>Deactivate</button>}</td></tr>)}</tbody>
            </table>
          </div>
          <form onSubmit={editingCostCentreId ? saveCostCentre : createCostCentre} className="grid gap-3 md:grid-cols-5 items-end">
            <label className="text-sm">Code<input className="input mt-1" required value={costCentreForm.code} onChange={(e) => setCostCentreForm({ ...costCentreForm, code: e.target.value.toUpperCase() })} /></label>
            <label className="text-sm">Name<input className="input mt-1" required value={costCentreForm.name} onChange={(e) => setCostCentreForm({ ...costCentreForm, name: e.target.value })} /></label>
            <label className="text-sm">Parent<select className="input mt-1" value={costCentreForm.parentId} onChange={(e) => setCostCentreForm({ ...costCentreForm, parentId: e.target.value })}><option value="">No parent</option>{costCentres.filter((x) => x.isActive && x.id !== editingCostCentreId).map((x) => <option key={x.id} value={x.id}>{x.code} — {x.name}</option>)}</select></label>
            <label className="text-sm">Branch<select className="input mt-1" value={costCentreForm.branchId} onChange={(e) => setCostCentreForm({ ...costCentreForm, branchId: e.target.value })}><option value="">No branch</option>{branches.filter((b) => b.isActive).map((b) => <option key={b.id} value={b.id}>{b.code} — {b.name}</option>)}</select></label>
            <button className="btn-primary" disabled={busy}>{editingCostCentreId ? "Save cost centre" : "Add cost centre"}</button>
            {editingCostCentreId && <button type="button" className="btn-secondary" onClick={() => { setEditingCostCentreId(""); setCostCentreForm({ code: "", name: "", parentId: "", branchId: "" }); }}>Cancel edit</button>}
          </form>
        </section>
      )}

      {tab === "members" && (
        <section className="card">
          <h2 className="font-semibold mb-3">Company memberships — {selectedCompany?.name}</h2>
          <div className="overflow-x-auto mb-5">
            <table className="table-base"><thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Status</th></tr></thead>
              <tbody>{members.map((x) => <tr key={x.id}><td>{x.firstName} {x.lastName}</td><td>{x.email}</td><td>{x.membershipRole}</td><td>{x.isActive ? "Active" : "Inactive"}</td></tr>)}</tbody>
            </table>
          </div>
          <form onSubmit={inviteMember} className="grid gap-3 md:grid-cols-3 items-end">
            <label className="text-sm">Work email<input className="input mt-1" type="email" required value={inviteForm.email} onChange={(e) => setInviteForm({ ...inviteForm, email: e.target.value })} /></label>
            <label className="text-sm">Company role<select className="input mt-1" required value={inviteForm.roleId} onChange={(e) => setInviteForm({ ...inviteForm, roleId: e.target.value })}>{roles.map((role) => <option key={role.id} value={role.id}>{role.name}</option>)}</select></label>
            <button className="btn-primary" disabled={busy || !inviteForm.roleId}>Invite user</button>
          </form>
        </section>
      )}

      {tab === "jurisdiction" && (
        <section className="card">
          <h2 className="font-semibold mb-3">Jurisdiction — {selectedCompany?.name}</h2>
          <p className="text-sm text-slate-500 mb-4">Select country defaults, then confirm the company reporting currency and fiscal calendar. Statutory tax rules require separate legal verification.</p>
          <form onSubmit={saveJurisdiction} className="grid gap-3 md:grid-cols-3">
            <label className="text-sm">Country<select className="input mt-1" value={jurisdictionForm.countryCode} onChange={(e) => {
              const country = countries.find((x) => x.countryCode === e.target.value);
              setJurisdictionForm({ ...jurisdictionForm, countryCode: e.target.value,
                functionalCurrency: country?.defaultCurrency ?? jurisdictionForm.functionalCurrency,
                baseCurrency: country?.defaultCurrency ?? jurisdictionForm.baseCurrency,
                reportingFramework: country?.defaultReportingFramework ?? jurisdictionForm.reportingFramework,
                timezone: country?.defaultTimeZone ?? jurisdictionForm.timezone });
            }}>{countries.map((x) => <option key={x.countryCode} value={x.countryCode}>{x.countryName} ({x.countryCode})</option>)}</select></label>
            <label className="text-sm">Functional currency<input className="input mt-1" maxLength={3} required value={jurisdictionForm.functionalCurrency} onChange={(e) => setJurisdictionForm({ ...jurisdictionForm, functionalCurrency: e.target.value.toUpperCase() })} /></label>
            <label className="text-sm">Base currency<input className="input mt-1" maxLength={3} required value={jurisdictionForm.baseCurrency} onChange={(e) => setJurisdictionForm({ ...jurisdictionForm, baseCurrency: e.target.value.toUpperCase() })} /></label>
            <label className="text-sm">Fiscal year start month<input className="input mt-1" type="number" min={1} max={12} value={jurisdictionForm.fiscalYearStartMonth} onChange={(e) => setJurisdictionForm({ ...jurisdictionForm, fiscalYearStartMonth: e.target.value })} /></label>
            <label className="text-sm">Reporting framework<input className="input mt-1" required value={jurisdictionForm.reportingFramework} onChange={(e) => setJurisdictionForm({ ...jurisdictionForm, reportingFramework: e.target.value })} /></label>
            <label className="text-sm">Time zone<input className="input mt-1" required value={jurisdictionForm.timezone} onChange={(e) => setJurisdictionForm({ ...jurisdictionForm, timezone: e.target.value })} /></label>
            <button className="btn-primary md:col-span-3" disabled={busy}>Save jurisdiction</button>
          </form>
          <div className="mt-5 text-sm">
            <h3 className="font-semibold mb-2">Configured jurisdictions</h3>
            {jurisdictions.filter((x) => x.companyId === selectedCompanyId).map((x) => <p key={x.id}>{x.countryCode} · {x.functionalCurrency} · FY month {x.fiscalYearStartMonth} · {x.reportingFramework} {x.isPrimary ? "· Primary" : ""}</p>)}
          </div>
        </section>
      )}

      {tab === "onboarding" && (
        <section className="card">
          <h2 className="font-semibold mb-2">Configuration wizard</h2>
          <p className="text-sm text-slate-500 mb-4">Progress: {onboarding?.status ?? "Loading"}</p>
          <label className="text-sm block mb-4">Deployment profile
            <select className="input mt-1" value={profileId} onChange={(e) => setProfileId(e.target.value)}>
              <option value="">Select profile</option>
              {profiles.map((profile) => <option key={profile.id} value={profile.id}>{profile.name}</option>)}
            </select>
          </label>
          <div className="grid gap-2 sm:grid-cols-2 mb-5">
            {STEPS.map(([key, label]) => <label key={key} className="flex items-center gap-2 text-sm border rounded-lg p-3">
              <input type="checkbox" checked={completedSteps.includes(key)} onChange={() => toggleStep(key)} />
              <span>{label}</span>
            </label>)}
          </div>
          <div className="flex flex-wrap gap-2">
            <button type="button" className="btn-secondary" disabled={busy} onClick={() => saveOnboarding(false)}>Save progress</button>
            <button type="button" className="btn-primary" disabled={busy} onClick={() => saveOnboarding(true)}>Complete onboarding</button>
          </div>
        </section>
      )}
    </AppShell>
  );
}
