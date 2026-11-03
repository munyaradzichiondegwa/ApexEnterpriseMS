using ApexERP.Application.Services;
using Xunit;

namespace ApexERP.Tests
{
    public class GroupGraphTests
    {
        // A (parent) <- B <- C ;  D is an unrelated company (another tenant).
        private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid(), E = Guid.NewGuid();

        private static void AssertSameMembers(IEnumerable<Guid> expected, IEnumerable<Guid> actual) =>
            Assert.Equal(expected.OrderBy(g => g).ToList(), actual.OrderBy(g => g).ToList());

        private static Dictionary<Guid, Guid?> Map() => new()
        {
            [A] = null, [B] = A, [C] = B, [D] = null, [E] = null
        };

        [Fact]
        public void Descendants_walks_down_the_tree()
        {
            AssertSameMembers(new[] { B, C }, GroupGraph.Descendants(Map(), A));
            AssertSameMembers(new[] { C }, GroupGraph.Descendants(Map(), B));
            Assert.Empty(GroupGraph.Descendants(Map(), C));
        }

        [Fact]
        public void Ancestors_walks_up_the_tree()
        {
            Assert.Equal(new[] { B, A }, GroupGraph.Ancestors(Map(), C));
            Assert.Empty(GroupGraph.Ancestors(Map(), A));
        }

        [Fact]
        public void A_circular_structure_does_not_hang()
        {
            var cyclic = new Dictionary<Guid, Guid?> { [A] = B, [B] = A };
            Assert.Equal(new[] { B }, GroupGraph.Ancestors(cyclic, A));
            Assert.Contains(B, GroupGraph.Descendants(cyclic, A));
        }

        [Fact]
        public void Visible_is_self_parents_and_subsidiaries_but_never_strangers()
        {
            AssertSameMembers(new[] { A, B, C }, GroupGraph.Visible(Map(), B));
            AssertSameMembers(new[] { D }, GroupGraph.Visible(Map(), D));
            Assert.DoesNotContain(D, GroupGraph.Visible(Map(), A));
        }

        [Fact]
        public void Cycle_detection()
        {
            Assert.True(GroupGraph.WouldCreateCycle(Map(), A, C));   // making A a child of its own grandchild
            Assert.True(GroupGraph.WouldCreateCycle(Map(), A, A));
            Assert.False(GroupGraph.WouldCreateCycle(Map(), C, A));
            Assert.False(GroupGraph.WouldCreateCycle(Map(), D, A));
        }

        // ---- the cross-tenant hole: re-parenting someone else's company, or attaching to a stranger ----

        [Fact]
        public void A_caller_cannot_change_another_tenants_company()
        {
            // D is a stranger to A: previously any signed-in user could re-parent it under themselves and then read its books.
            var error = GroupGraph.ValidateStructureChange(Map(), caller: A, target: D, newParent: A, () => true);
            Assert.NotNull(error);
        }

        [Fact]
        public void A_missing_company_is_reported_as_not_found()
        {
            Assert.Equal("Company not found.", GroupGraph.ValidateStructureChange(Map(), A, Guid.NewGuid(), null, () => true));
        }

        [Fact]
        public void A_parent_may_reorganise_its_own_subsidiaries()
        {
            Assert.Null(GroupGraph.ValidateStructureChange(Map(), A, C, A, () => false));      // move C directly under A
            Assert.Null(GroupGraph.ValidateStructureChange(Map(), A, C, null, () => false));   // detach C
        }

        [Fact]
        public void A_subsidiary_cannot_be_moved_under_an_outside_company()
        {
            Assert.NotNull(GroupGraph.ValidateStructureChange(Map(), A, C, D, () => true));
        }

        [Fact]
        public void A_company_cannot_attach_itself_to_a_parent_that_has_not_agreed()
        {
            // D tries to place itself under A, but A never registered D as a linked customer/supplier.
            var error = GroupGraph.ValidateStructureChange(Map(), caller: D, target: D, newParent: A, parentHasLinkedRecord: () => false);
            Assert.NotNull(error);
            Assert.Contains("linked", error);
        }

        [Fact]
        public void A_company_may_attach_itself_to_a_parent_that_registered_it()
        {
            Assert.Null(GroupGraph.ValidateStructureChange(Map(), D, D, A, () => true));
        }

        [Fact]
        public void Keeping_the_same_parent_needs_no_new_consent()
        {
            // B already sits under A; updating B's own record (e.g. ownership %) while keeping A needs no link record.
            Assert.Null(GroupGraph.ValidateStructureChange(Map(), B, B, A, () => false));
        }

        [Fact]
        public void A_change_that_would_create_a_cycle_is_refused()
        {
            var error = GroupGraph.ValidateStructureChange(Map(), A, A, C, () => true);
            Assert.NotNull(error);
            Assert.Contains("circular", error);
        }

        [Fact]
        public void An_unknown_parent_is_refused()
        {
            Assert.NotNull(GroupGraph.ValidateStructureChange(Map(), D, D, Guid.NewGuid(), () => true));
        }
    }
}
