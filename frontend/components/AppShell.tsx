"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useAuth } from "@/lib/auth";
import clsx from "clsx";

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
  { href: "/admin", label: "Admin & Settings" },
];

function NavLinks({ pathname, onNavigate }: { pathname: string; onNavigate?: () => void }) {
  return (
    <>
      {NAV.map((item) => (
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
  const { user, loading, logout } = useAuth();
  const router = useRouter();
  const [drawerOpen, setDrawerOpen] = useState(false);

  useEffect(() => {
    if (!loading && !user) router.push("/login");
  }, [loading, user, router]);

  // Close the mobile drawer automatically whenever the route changes.
  useEffect(() => {
    setDrawerOpen(false);
  }, [pathname]);

  if (loading || !user) {
    return <div className="min-h-screen flex items-center justify-center text-slate-400">Loading ApexERP…</div>;
  }

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
        <span className="font-bold tracking-tight">ApexERP</span>
        <div className="w-9" /> {/* spacer to keep the title centered */}
      </header>

      {/* Mobile off-canvas drawer + backdrop */}
      {drawerOpen && (
        <div className="md:hidden fixed inset-0 z-40 flex">
          <div className="absolute inset-0 bg-black/50" onClick={() => setDrawerOpen(false)} />
          <aside className="relative w-72 max-w-[80vw] bg-brand-900 text-brand-50 flex flex-col h-full overflow-y-auto">
            <div className="px-5 py-5 border-b border-brand-800 flex items-center justify-between">
              <div>
                <div className="text-lg font-bold tracking-tight">ApexERP</div>
                <div className="text-xs text-brand-300 truncate">{user.email}</div>
              </div>
              <button onClick={() => setDrawerOpen(false)} aria-label="Close navigation menu" className="p-1 rounded hover:bg-brand-800">
                <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                  <line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" />
                </svg>
              </button>
            </div>
            <nav className="flex-1 py-3">
              <NavLinks pathname={pathname} onNavigate={() => setDrawerOpen(false)} />
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
          <div className="text-lg font-bold tracking-tight">ApexERP</div>
          <div className="text-xs text-brand-300 truncate">{user.email}</div>
        </div>
        <nav className="flex-1 overflow-y-auto py-3">
          <NavLinks pathname={pathname} />
        </nav>
        <div className="p-4 border-t border-brand-800">
          <button onClick={logout} className="text-sm text-brand-200 hover:text-white">Sign out</button>
        </div>
      </aside>

      <main className="flex-1 min-w-0 bg-slate-50">
        <div className="max-w-6xl mx-auto p-4 sm:p-6 md:p-8">{children}</div>
      </main>
    </div>
  );
}
