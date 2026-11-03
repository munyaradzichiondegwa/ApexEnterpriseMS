// Permission checks for the UI. These mirror the API's PermissionEvaluator so the interface can hide what a user cannot
// use. They are a convenience only: the API enforces every permission itself and is the authority.

/** True when the granted keys satisfy `required` (exact key, "*", or a branch wildcard such as "gl.*"). */
export function hasPermission(granted: readonly string[] | null | undefined, required: string): boolean {
  const need = (required ?? "").trim().toLowerCase();
  if (!need) return false;

  const set = new Set((granted ?? []).map((p) => p.trim().toLowerCase()).filter(Boolean));
  if (set.has("*") || set.has(need)) return true;

  const parts = need.split(".");
  for (let i = parts.length - 1; i >= 1; i--) {
    if (set.has(parts.slice(0, i).join(".") + ".*")) return true;
  }
  return false;
}

/** True when the user holds any permission inside a module (used to decide whether to show it in the navigation). */
export function hasAnyInModule(granted: readonly string[] | null | undefined, module: string): boolean {
  const prefix = module.toLowerCase() + ".";
  return (granted ?? []).some((p) => {
    const key = p.trim().toLowerCase();
    return key === "*" || key.startsWith(prefix);
  });
}

/** Navigation path -> API module whose permissions unlock it (null = every signed-in user). */
export const PATH_MODULES: Record<string, string | null> = {
  "/dashboard": null,
  "/gl": "gl",
  "/ar": "ar",
  "/ap": "ap",
  "/cash-bank": "cashbank",
  "/inventory": "inventory",
  "/fixed-assets": "fixed-assets",
  "/payroll": "payroll",
  "/tax": "tax",
  "/reports": "reports",
  "/supply-chain": "supply-chain",
  "/crm": "crm",
  "/projects": "projects",
  "/manufacturing": "manufacturing",
  "/group": "group",
  "/public-sector": "public-sector",
  "/grants": "grants",
  "/exceptions": "exceptions",
  "/admin": "admin",
};

/** Can the user open this page? Unknown paths (login, register, ...) are not gated here. */
export function canView(pathname: string, granted: readonly string[] | null | undefined): boolean {
  const first = "/" + (pathname.split("/").filter(Boolean)[0] ?? "");
  if (!(first in PATH_MODULES)) return true;
  const module = PATH_MODULES[first];
  return module === null || hasAnyInModule(granted, module);
}
