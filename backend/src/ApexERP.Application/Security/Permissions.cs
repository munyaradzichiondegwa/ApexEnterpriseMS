using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ApexERP.Application.Security
{
    /// <summary>
    /// The permission catalogue. A role's <c>PermissionsJson</c> is a JSON array of these keys, e.g.
    /// <c>["gl.journals.create","reports.financial.read"]</c>. The Administrator role created at
    /// registration holds <c>["*"]</c>. A key may also end in <c>.*</c> to grant a whole branch
    /// (<c>"gl.*"</c> grants every <c>gl.</c> permission).
    ///
    /// Naming: sensitive actions have explicit keys (listed below). Every other endpoint is covered by
    /// the module default <c>{module}.read</c> (GET) or <c>{module}.write</c> (anything else), applied by
    /// ModulePermissionConvention in the API project, so no endpoint is left open to "any signed-in user".
    /// </summary>
    public static class Permissions
    {
        public const string All = "*";

        // ---- General ledger -------------------------------------------------------------------
        public const string GlAccountsRead = "gl.accounts.read";
        public const string GlAccountsManage = "gl.accounts.manage";
        public const string GlStructureManage = "gl.structure.manage";   // cost centres, segments
        public const string GlJournalsRead = "gl.journals.read";
        public const string GlJournalsCreate = "gl.journals.create";     // create / submit / cancel own drafts
        public const string GlJournalsApprove = "gl.journals.approve";   // approve or reject submitted journals
        public const string GlJournalsPost = "gl.journals.post";         // post an approved journal
        public const string GlJournalsReverse = "gl.journals.reverse";
        public const string AccountingPeriodClose = "accounting.period.close";
        public const string AccountingPeriodReopen = "accounting.period.reopen";

        // ---- Reporting ------------------------------------------------------------------------
        public const string ReportsFinancialRead = "reports.financial.read";

        // ---- Sub-ledger posting ---------------------------------------------------------------
        public const string ArInvoicesPost = "ar.invoices.post";
        public const string ApInvoicesPost = "ap.invoices.post";
        public const string ApPaymentsCreate = "ap.payments.create";
        public const string PayrollRunsPost = "payroll.runs.post";
        public const string PayrollConfigManage = "payroll.config.manage";     // PAYE bands
        public const string EquityDividendsManage = "equity.dividends.manage"; // declare / pay dividends
        public const string PublicSectorYearEndClose = "public-sector.yearend.close";
        public const string PublicSectorTendersAward = "public-sector.tenders.award";
        public const string FixedAssetsDepreciate = "fixed-assets.depreciate";
        public const string FixedAssetsDispose = "fixed-assets.dispose";
        public const string FixedAssetsRevalue = "fixed-assets.revalue";

        // ---- Group / inter-company ------------------------------------------------------------
        public const string GroupStructureManage = "group.structure.manage";
        public const string GroupConsolidationRead = "group.consolidation.read";
        public const string GroupNettingExecute = "group.netting.execute";

        // ---- Controls: exceptions, workflow ----------------------------------------------------
        public const string ExceptionsRead = "exceptions.read";
        public const string ExceptionsManage = "exceptions.manage";
        public const string WorkflowDecide = "workflow.decide";

        // ---- Administration ---------------------------------------------------------------------
        public const string AdminCompanyManage = "admin.company.manage";
        public const string AdminUsersManage = "admin.users.manage";
        public const string AdminRolesManage = "admin.roles.manage";
        public const string AdminAuditRead = "admin.audit.read";

        /// <summary>Explicit (non-default) permission keys, for the role editor and documentation.</summary>
        public static readonly IReadOnlyList<string> Catalogue = new[]
        {
            GlAccountsRead, GlAccountsManage, GlStructureManage, GlJournalsRead, GlJournalsCreate,
            GlJournalsApprove, GlJournalsPost, GlJournalsReverse, AccountingPeriodClose, AccountingPeriodReopen,
            ReportsFinancialRead, ArInvoicesPost, ApInvoicesPost, ApPaymentsCreate, PayrollRunsPost, PayrollConfigManage,
            EquityDividendsManage, PublicSectorYearEndClose, PublicSectorTendersAward,
            FixedAssetsDepreciate, FixedAssetsDispose, FixedAssetsRevalue,
            GroupStructureManage, GroupConsolidationRead, GroupNettingExecute,
            ExceptionsRead, ExceptionsManage, WorkflowDecide,
            AdminCompanyManage, AdminUsersManage, AdminRolesManage, AdminAuditRead
        };

        /// <summary>
        /// True for a key the system understands: "*", a catalogue key, a module default (<c>gl.read</c>, <c>ar.write</c>) or a
        /// branch wildcard over any of those (<c>gl.*</c>, <c>gl.journals.*</c>). Used to reject typos when roles are created,
        /// because a misspelt permission would otherwise silently grant nothing.
        /// </summary>
        public static bool IsKnown(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            key = key.Trim();
            if (key == All) return true;

            var catalogue = Catalogue.Concat(Modules.SelectMany(m => new[] { m + ".read", m + ".write" }));
            if (catalogue.Contains(key, StringComparer.OrdinalIgnoreCase)) return true;

            if (key.EndsWith(".*", StringComparison.Ordinal))
            {
                var branch = key.Substring(0, key.Length - 1);   // keeps the trailing dot: "gl." / "gl.journals."
                return catalogue.Any(k => k.StartsWith(branch, StringComparison.OrdinalIgnoreCase));
            }
            return false;
        }

        /// <summary>Module names that get the <c>{module}.read</c> / <c>{module}.write</c> defaults.</summary>
        public static readonly IReadOnlyList<string> Modules = new[]
        {
            "gl", "ar", "ap", "cashbank", "inventory", "fixed-assets", "payroll", "tax", "reports", "crm",
            "projects", "supply-chain", "manufacturing", "fdms", "equity", "grants", "public-sector",
            "group", "workflow", "admin", "exceptions"
        };
    }

    /// <summary>
    /// Pure permission logic (no ASP.NET or EF dependencies) so it can be unit-tested directly.
    /// Deny by default: malformed JSON, an empty list or a missing claim all grant nothing.
    /// </summary>
    public static class PermissionEvaluator
    {
        /// <summary>
        /// Parses one or more permission claim values. Each value is normally a JSON array of strings;
        /// a bare (non-JSON) string is treated as a single permission. Invalid JSON yields no permissions.
        /// </summary>
        public static HashSet<string> Parse(IEnumerable<string?> claimValues)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in claimValues)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var value = raw.Trim();

                if (value.StartsWith("[", StringComparison.Ordinal))
                {
                    try
                    {
                        var items = JsonSerializer.Deserialize<string[]>(value);
                        if (items == null) continue;
                        foreach (var item in items)
                            if (!string.IsNullOrWhiteSpace(item)) set.Add(item.Trim());
                    }
                    catch (JsonException)
                    {
                        // malformed permission data must never accidentally grant access
                    }
                }
                else if (IsPermissionToken(value))
                {
                    set.Add(value);
                }
                // anything else (stray JSON, free text) is ignored: malformed data must never grant access
            }
            return set;
        }

        public static HashSet<string> Parse(string? permissionsJson) => Parse(new[] { permissionsJson });

        /// <summary>A bare (non-JSON) permission must look like a key: letters, digits, '.', '-', '_' or a '*' wildcard.</summary>
        private static bool IsPermissionToken(string value) =>
            value.Length <= 100 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '*');

        /// <summary>True when <paramref name="granted"/> satisfies <paramref name="required"/>.</summary>
        public static bool Has(ISet<string> granted, string required)
        {
            if (string.IsNullOrWhiteSpace(required)) return false;
            if (granted.Contains(Permissions.All)) return true;
            if (granted.Contains(required)) return true;

            // Branch wildcards: "gl.*" grants "gl.journals.post"; "gl.journals.*" grants "gl.journals.post".
            var parts = required.Split('.');
            for (var i = parts.Length - 1; i >= 1; i--)
            {
                var wildcard = string.Join('.', parts.Take(i)) + ".*";
                if (granted.Contains(wildcard)) return true;
            }
            return false;
        }

        public static bool Has(string? permissionsJson, string required) => Has(Parse(permissionsJson), required);

        /// <summary>
        /// Privilege-escalation guard for role editing: a caller may only hand out permissions they
        /// hold themselves (the all-permissions caller may grant anything). Returns the keys that are NOT allowed.
        /// </summary>
        public static IReadOnlyList<string> NotGrantable(ISet<string> callerPermissions, IEnumerable<string> requested)
        {
            if (callerPermissions.Contains(Permissions.All)) return Array.Empty<string>();
            // Has() already understands wildcards: a caller holding "gl.*" may grant "gl.journals.post"
            // or "gl.journals.*", but a caller holding only "gl.journals.post" may not grant "gl.*" or "*".
            return requested
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Where(r => !Has(callerPermissions, r.Trim()))
                .ToList();
        }
    }
}
