namespace ApexERP.Domain.Controls
{
    public sealed record SoDConflict(string PermissionA, string PermissionB, string Reason);

    /// <summary>Incompatible-permission detection (spec section 7.3). Wildcards are expanded by the caller.</summary>
    public static class SegregationOfDuties
    {
        /// <summary>Default rules over the real permission keys. Tenants may add more (SoDConflictRule).</summary>
        public static readonly IReadOnlyList<SoDConflict> DefaultRules = new SoDConflict[]
        {
            new("ap.write", "ap.payments.create", "Supplier maintenance and payment creation"),
            new("ap.invoices.post", "ap.payments.create", "Posting supplier invoices and creating payments"),
            new("ap.payments.create", "ap.payments.authorise", "Preparing and authorising a payment"),
            new("ap.write", "ap.suppliers.verify-bank", "Maintaining suppliers and verifying their bank details"),
            new("gl.journals.create", "gl.journals.approve", "Journal preparation and approval"),
            new("gl.journals.approve", "gl.journals.post", "Journal approval and posting"),
            new("accounting.period.close", "gl.journals.create", "Period closure and journal preparation"),
            new("cashbank.write", "controls.bank-rec.certify", "Bank transactions and reconciliation certification"),
            new("controls.governance.manage", "controls.governance.approve", "Proposing and approving control configuration"),
        };

        /// <summary>Does the granted set satisfy this permission? Understands "*" and branch wildcards ("gl.*", "gl.journals.*").</summary>
        public static bool Holds(ISet<string> granted, string permission)
        {
            if (granted.Contains("*") || granted.Contains(permission)) return true;
            var parts = permission.Split('.');
            for (var i = parts.Length - 1; i >= 1; i--)
                if (granted.Contains(string.Join('.', parts.Take(i)) + ".*")) return true;
            return false;
        }

        public static IReadOnlyList<SoDConflict> Detect(IEnumerable<string> permissions, IEnumerable<SoDConflict>? rules = null)
        {
            var held = new HashSet<string>(permissions.Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase);
            return (rules ?? DefaultRules).Where(r => Holds(held, r.PermissionA) && Holds(held, r.PermissionB)).ToList();
        }

        /// <summary>The preparer of a restricted transaction may not approve it (spec 7.5). Emergency override must be logged by the caller.</summary>
        public static bool MayApprove(Guid? preparedBy, Guid approver, bool allowSelfApproval) =>
            allowSelfApproval || preparedBy is null || preparedBy.Value != approver;
    }
}
