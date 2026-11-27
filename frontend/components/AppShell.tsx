"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useAuth } from "@/lib/auth";
import { canView } from "@/lib/permissions";
import { api, extractErrorMessage } from "@/lib/api";
import ConfigError from "@/components/ConfigError";
import LoadingState from "@/components/LoadingState";
import clsx from "clsx";
import BrandMark from "@/components/BrandMark";

const NAV = [
  { href: "/dashboard", label: "Dashboard" },
  { href: "/gl", label: "General Ledger" },
  { href: "/ar", label: "Accounts Receivable" },
  { href: "/ap", label: "Accounts Payable" },
  { href: "/cash-bank", label: "Cash & Bank" },
  { href: "/inventory", label: "Inventory" },
  { href: "/fixed-assets", label: "Fixed Assets" },
  { href: "/payroll", label: "Payroll & HR" },
  { href: "/tax", label: "ZW Tax Compliance" },
  { href: "/reports", label: "Reporting & BI" },
  { href: "/supply-chain", label: "Supply Chain" },
  { href: "/crm", label: "CRM & Sales" },
  { href: "/projects", label: "Projects" },
  { href: "/manufacturing", label: "Manufacturing" },
  { href: "/group", label: "Group & Consolidation" },
  { href: "/public-sector", label: "Public Sector" },
  { href: "/grants", label: "Grants & NGO" },
  { href: "/exceptions", label: "Exceptions" },
  { href: "/automation", label: "Accounting Automation" },
  { href: "/payables-controls", label: "Payables Controls" },
  { href: "/receivables-controls", label: "Receivables Controls" },
  { href: "/reconciliation", label: "Reconciliation" },
  { href: "/period-close", label: "Period Close" },
  { href: "/audit", label: "Audit Workspace" },
  { href: "/governance", label: "Governance & Approvals" },
  { href: "/integrations", label: "Integrations & Jobs" },
  { href: "/security", label: "Account Security" },
  { href: "/admin", label: "Admin & Settings" },
  { href: "/organisation", label: "Organisation & Licensing" },
];

function NavLinks({ pathname, items, onNavigate }: { pathname: string; items: typeof NAV; onNavigate?: () => void }) {
  return (
    <>
      {items.map((item) => (
        <Link
          key={item.href}
          href={item.href}
          onClick={onNavigate}
          className={clsx(
            "block px-5 py-2.5 text-sm transition-colors",
            pathname === item.href ? "bg-brand-700 text-white font-medium" : "text-brand-200 hover:bg-brand-800"
          )}
        >
          {item.label}
        </Link>
      ))}
    </>
  );
}

export default function AppShell({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const { user, loading, logout, configError, refreshProfile } = useAuth();
  const [companies, setCompanies] = useState<Array<{ id: string; name: string; isActive: boolean }>>([]);
  const [switchingCompany, setSwitchingCompany] = useState(false);
  const [companyError, setCompanyError] = useState<string | null>(null);
  const router = useRouter();
  const [drawerOpen, setDrawerOpen] = useState(false);

  useEffect(() => {
    if (!loading && !user && !configError) router.replace("/login");
  }, [loading, user, configError, router]);

  useEffect(() => {
    if (!user) return;
    let active = true;
    api.get("/auth/companies")
      .then((response) => { if (active) setCompanies(response.data); })
      .catch(() => { if (active) setCompanyError("Company list is currently unavailable."); });
    return () => { active = false; };
  }, [user?.userId]);

  async function changeCompany(companyId: string) {
    if (!companyId || companyId === user?.companyId || switchingCompany) return;
    setSwitchingCompany(true);
    setCompanyError(null);
    try {
      await api.post("/auth/switch-company", { companyId });
      await refreshProfile();
      router.refresh();
    } catch (error) {
      setCompanyError(extractErrorMessage(error));
    } finally {
      setSwitchingCompany(false);
    }
  }

  const companySelector = companies.length > 1 ? (
    <div className="mt-3">
      <label className="block text-xs text-brand-300 mb-1" htmlFor="active-company">Active legal entity</label>
      <select id="active-company" value={user?.companyId ?? ""} disabled={switchingCompany}
        onChange={(event) => void changeCompany(event.target.value)}
        className="w-full rounded-md border border-brand-700 bg-brand-800 text-white text-xs px-2 py-2 disabled:opacity-60">
        {companies.filter((company) => company.isActive).map((company) =>
          <option key={company.id} value={company.id}>{company.name}</option>)}
      </select>
      {switchingCompany && <p className="text-xs text-brand-300 mt-1">Switching company…</p>}
      {companyError && <p role="alert" className="text-xs text-amber-300 mt-1">{companyError}</p>}
    </div>
  ) : null;


  // Close the mobile drawer automatically whenever the route changes.
  useEffect(() => {
    setDrawerOpen(false);
  }, [pathname]);

  if (configError) return <ConfigError message={configError} />;
  if (loading) return <LoadingState />;
  if (!user) return <LoadingState message="Redirecting to sign in…" />;

  // Only show the modules this user's role can use. (The API enforces permissions regardless of what is shown here.)
  const visibleNav = NAV.filter((item) => canView(item.href, user.permissions));
  const allowed = canView(pathname, user.permissions);

  return (
    <div className="min-h-screen flex flex-col md:flex-row">
      {/* Mobile top bar -- visible only below the md breakpoint */}
      <header className="md:hidden flex items-center justify-between bg-brand-900 text-white px-4 py-3 sticky top-0 z-30">
        <button
          onClick={() => setDrawerOpen(true)}
          aria-label="Open navigation menu"
          className="p-2 -ml-2 rounded-lg hover:bg-brand-800"
        >
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <line x1="3" y1="6" x2="21" y2="6" />
            <line x1="3" y1="12" x2="21" y2="12" />
            <line x1="3" y1="18" x2="21" y2="18" />
          </svg>
        </button>
        <BrandMark compact />
        <div className="w-9" /> {/* spacer to keep the title centered */}
      </header>

      {/* Mobile off-canvas drawer + backdrop */}
      {drawerOpen && (
        <div className="md:hidden fixed inset-0 z-40 flex">
          <div className="absolute inset-0 bg-black/50" onClick={() => setDrawerOpen(false)} />
          <aside className="relative w-72 max-w-[80vw] bg-brand-900 text-brand-50 flex flex-col h-full overflow-y-auto">
            <div className="px-5 py-5 border-b border-brand-800 flex items-center justify-between">
              <div>
                <BrandMark compact />
                <div className="text-xs text-brand-300 truncate">{user.email}</div>
                {companySelector}
              </div>
              <button onClick={() => setDrawerOpen(false)} aria-label="Close navigation menu" className="p-1 rounded hover:bg-brand-800">
                <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                  <line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" />
                </svg>
              </button>
            </div>
            <nav className="flex-1 py-3">
              <NavLinks pathname={pathname} items={visibleNav} onNavigate={() => setDrawerOpen(false)} />
            </nav>
            <div className="p-4 border-t border-brand-800">
              <button onClick={logout} className="text-sm text-brand-200 hover:text-white">Sign out</button>
            </div>
          </aside>
        </div>
      )}

      {/* Persistent desktop sidebar -- hidden below the md breakpoint */}
      <aside className="hidden md:flex w-64 bg-brand-900 text-brand-50 flex-col shrink-0">
        <div className="px-5 py-5 border-b border-brand-800">
          <BrandMark />
          <div className="text-xs text-brand-300 truncate">{user.email}</div>
          {companySelector}
        </div>
        <nav className="flex-1 overflow-y-auto py-3">
          <NavLinks pathname={pathname} items={visibleNav} />
        </nav>
        <div className="p-4 border-t border-brand-800">
          <button onClick={logout} className="text-sm text-brand-200 hover:text-white">Sign out</button>
        </div>
      </aside>

      <main className="flex-1 min-w-0 bg-slate-50">
        <div className="max-w-6xl mx-auto p-4 sm:p-6 md:p-8">
          {allowed ? (
            children
          ) : (
            <div className="card max-w-lg" role="alert">
              <h1 className="text-lg font-bold mb-1">You don&apos;t have access to this area</h1>
              <p className="text-sm text-slate-600">
                Your role ({user.role}) does not include permission to use this module. Ask an administrator if you need it.
              </p>
            </div>
          )}
        </div>
      </main>
    </div>
  );
}
