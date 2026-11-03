using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>
    /// Pure parent/child graph rules for company groups (no database access, so they are unit-tested directly).
    /// </summary>
    public static class GroupGraph
    {
        public static HashSet<Guid> Descendants(IReadOnlyDictionary<Guid, Guid?> parentOf, Guid root)
        {
            var children = parentOf
                .Where(kv => kv.Value.HasValue)
                .GroupBy(kv => kv.Value!.Value)
                .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());

            var result = new HashSet<Guid>();
            var queue = new Queue<Guid>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!children.TryGetValue(current, out var kids)) continue;
                foreach (var kid in kids)
                    if (kid != root && result.Add(kid)) queue.Enqueue(kid);
            }
            return result;
        }

        public static List<Guid> Ancestors(IReadOnlyDictionary<Guid, Guid?> parentOf, Guid node)
        {
            var result = new List<Guid>();
            var seen = new HashSet<Guid> { node };
            var current = node;
            while (parentOf.TryGetValue(current, out var parent) && parent.HasValue && seen.Add(parent.Value))
            {
                result.Add(parent.Value);
                current = parent.Value;
            }
            return result;
        }

        public static bool WouldCreateCycle(IReadOnlyDictionary<Guid, Guid?> parentOf, Guid child, Guid proposedParent) =>
            child == proposedParent || Descendants(parentOf, child).Contains(proposedParent);

        /// <summary>Companies a caller may see: itself, its parents and its subsidiaries -- never strangers.</summary>
        public static HashSet<Guid> Visible(IReadOnlyDictionary<Guid, Guid?> parentOf, Guid companyId)
        {
            var visible = Descendants(parentOf, companyId);
            foreach (var ancestor in Ancestors(parentOf, companyId)) visible.Add(ancestor);
            visible.Add(companyId);
            return visible;
        }

        /// <summary>
        /// Decides whether <paramref name="caller"/> may attach <paramref name="target"/> under <paramref name="newParent"/>
        /// (null = detach). Returns an error message, or null when allowed. The rules close the cross-tenant hole where any
        /// user could re-parent ANY company and then read its books through consolidation:
        ///   * a caller may only change its own company or one of its subsidiaries;
        ///   * a subsidiary may only be moved within the caller's own tree;
        ///   * a company may place ITSELF under an outside parent only if that parent has registered it as a linked
        ///     customer/supplier -- i.e. both sides have consented (<paramref name="parentHasLinkedRecord"/>).
        /// </summary>
        public static string? ValidateStructureChange(IReadOnlyDictionary<Guid, Guid?> parentOf, Guid caller, Guid target,
            Guid? newParent, Func<bool> parentHasLinkedRecord)
        {
            if (!parentOf.ContainsKey(target)) return "Company not found.";

            var ownTree = Descendants(parentOf, caller);
            ownTree.Add(caller);
            if (!ownTree.Contains(target))
                return "You can only change the group structure of your own company and its subsidiaries.";

            if (newParent == null) return null;

            if (!parentOf.ContainsKey(newParent.Value)) return "The parent company was not found.";
            if (WouldCreateCycle(parentOf, target, newParent.Value)) return "That change would create a circular group structure.";

            if (target == caller)
            {
                var unchanged = parentOf[target] == newParent;
                if (!unchanged && !ownTree.Contains(newParent.Value) && !parentHasLinkedRecord())
                    return "The parent company has not registered your company as a linked customer or supplier, so it has not agreed " +
                           "to the link. Ask them to create that linked record first.";
                return null;
            }

            return ownTree.Contains(newParent.Value)
                ? null
                : "A subsidiary can only be moved under your own company or another of your subsidiaries.";
        }
    }

    /// <summary>Database-backed group scoping (which companies a caller may see or change).</summary>
    public class GroupScopeService
    {
        private readonly ApexErpDbContext _db;

        public GroupScopeService(ApexErpDbContext db)
        {
            _db = db;
        }

        /// <summary>Id -> parent id for every company. Companies are the tenants themselves, so this table has no
        /// tenant filter; only these two columns leave this method.</summary>
        public async Task<Dictionary<Guid, Guid?>> LoadParentMapAsync(CancellationToken ct = default) =>
            await _db.Companies.Select(c => new { c.Id, c.ParentCompanyId })
                .ToDictionaryAsync(c => c.Id, c => c.ParentCompanyId, ct);

        public async Task<HashSet<Guid>> GetVisibleCompanyIdsAsync(Guid companyId, CancellationToken ct = default) =>
            GroupGraph.Visible(await LoadParentMapAsync(ct), companyId);

        public async Task<bool> IsVisibleAsync(Guid companyId, Guid otherCompanyId, CancellationToken ct = default) =>
            (await GetVisibleCompanyIdsAsync(companyId, ct)).Contains(otherCompanyId);

        public async Task<bool> ParentHasLinkedRecordAsync(Guid parentCompanyId, Guid childCompanyId, CancellationToken ct = default) =>
            await _db.Customers.IgnoreQueryFilters().AnyAsync(c => c.CompanyId == parentCompanyId && c.LinkedCompanyId == childCompanyId, ct)
            || await _db.Suppliers.IgnoreQueryFilters().AnyAsync(s => s.CompanyId == parentCompanyId && s.LinkedCompanyId == childCompanyId, ct);

        /// <summary>Returns an error message when the change is not allowed, otherwise null.</summary>
        public async Task<string?> ValidateStructureChangeAsync(Guid caller, Guid target, Guid? newParent, CancellationToken ct = default)
        {
            var map = await LoadParentMapAsync(ct);
            // Evaluate the (async) consent lookup up front, only when it can matter.
            var needsConsentCheck = newParent.HasValue && target == caller && map.TryGetValue(target, out var current) && current != newParent;
            var hasLink = needsConsentCheck && await ParentHasLinkedRecordAsync(newParent!.Value, caller, ct);
            return GroupGraph.ValidateStructureChange(map, caller, target, newParent, () => hasLink);
        }
    }
}
