using ApexERP.Application.Security;
using Xunit;

namespace ApexERP.Tests
{
    public class PermissionTests
    {
        [Fact]
        public void Parse_reads_a_json_array_ignoring_blanks_and_case()
        {
            var set = PermissionEvaluator.Parse("[\"GL.Journals.Post\", \"\", \"  \", \"ar.read\"]");
            Assert.Equal(2, set.Count);
            Assert.Contains("gl.journals.post", set);      // case-insensitive set
            Assert.Contains("ar.read", set);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("[]")]
        [InlineData("[not json")]
        [InlineData("{\"a\":1}")]
        public void Parse_of_missing_or_malformed_data_grants_nothing(string? raw)
        {
            Assert.Empty(PermissionEvaluator.Parse(raw));
            Assert.False(PermissionEvaluator.Has(raw, "gl.journals.post"));
        }

        [Fact]
        public void Parse_treats_a_bare_string_as_one_permission()
        {
            Assert.True(PermissionEvaluator.Has("gl.journals.post", "gl.journals.post"));
        }

        [Fact]
        public void Star_grants_everything()
        {
            Assert.True(PermissionEvaluator.Has("[\"*\"]", "gl.journals.post"));
            Assert.True(PermissionEvaluator.Has("[\"*\"]", "anything.at.all"));
        }

        [Fact]
        public void Exact_permission_grants_only_itself()
        {
            var json = "[\"gl.journals.post\"]";
            Assert.True(PermissionEvaluator.Has(json, "gl.journals.post"));
            Assert.False(PermissionEvaluator.Has(json, "gl.journals.approve"));
            Assert.False(PermissionEvaluator.Has(json, "gl.journals.reverse"));
            Assert.False(PermissionEvaluator.Has(json, "gl.accounts.read"));
        }

        [Fact]
        public void Branch_wildcards_grant_the_whole_branch_but_nothing_outside_it()
        {
            Assert.True(PermissionEvaluator.Has("[\"gl.*\"]", "gl.journals.post"));
            Assert.True(PermissionEvaluator.Has("[\"gl.*\"]", "gl.read"));
            Assert.True(PermissionEvaluator.Has("[\"gl.journals.*\"]", "gl.journals.post"));
            Assert.False(PermissionEvaluator.Has("[\"gl.journals.*\"]", "gl.accounts.read"));
            Assert.False(PermissionEvaluator.Has("[\"gl.*\"]", "ar.read"));
            Assert.False(PermissionEvaluator.Has("[\"gl.*\"]", "glx.read"));   // 'gl' must match a whole segment
        }

        [Fact]
        public void A_blank_requirement_is_never_satisfied()
        {
            Assert.False(PermissionEvaluator.Has("[\"*\"]", ""));
            Assert.False(PermissionEvaluator.Has("[\"*\"]", "  "));
        }

        [Fact]
        public void Several_claim_values_are_merged()
        {
            var set = PermissionEvaluator.Parse(new[] { "[\"ar.read\"]", "[\"ap.read\"]" });
            Assert.True(PermissionEvaluator.Has(set, "ar.read"));
            Assert.True(PermissionEvaluator.Has(set, "ap.read"));
        }

        // ---- privilege-escalation guard ----

        [Fact]
        public void The_all_permissions_caller_may_grant_anything()
        {
            var caller = PermissionEvaluator.Parse("[\"*\"]");
            Assert.Empty(PermissionEvaluator.NotGrantable(caller, new[] { "*", "gl.journals.post", "admin.roles.manage" }));
        }

        [Fact]
        public void A_caller_cannot_grant_what_they_do_not_hold()
        {
            var caller = PermissionEvaluator.Parse("[\"gl.journals.post\", \"admin.roles.manage\"]");
            var denied = PermissionEvaluator.NotGrantable(caller, new[] { "gl.journals.post", "gl.journals.approve", "*", "gl.*" });
            Assert.Equal(3, denied.Count);
            Assert.Contains("gl.journals.approve", denied);
            Assert.Contains("*", denied);
            Assert.Contains("gl.*", denied);
            Assert.DoesNotContain("gl.journals.post", denied);
        }

        [Fact]
        public void A_branch_holder_may_grant_inside_the_branch_only()
        {
            var caller = PermissionEvaluator.Parse("[\"gl.*\"]");
            Assert.Empty(PermissionEvaluator.NotGrantable(caller, new[] { "gl.journals.post", "gl.journals.*", "gl.read" }));
            var denied = PermissionEvaluator.NotGrantable(caller, new[] { "ar.read", "*" });
            Assert.Equal(2, denied.Count);
        }

        // ---- catalogue ----

        [Theory]
        [InlineData("*", true)]
        [InlineData("gl.journals.post", true)]
        [InlineData("reports.financial.read", true)]
        [InlineData("gl.read", true)]
        [InlineData("fixed-assets.write", true)]
        [InlineData("gl.*", true)]
        [InlineData("gl.journals.*", true)]
        [InlineData("gl.jornals.post", false)]   // typo
        [InlineData("banana", false)]
        [InlineData("nosuchmodule.read", false)]
        [InlineData("nosuchmodule.*", false)]
        [InlineData("", false)]
        public void IsKnown_recognises_real_permissions_and_rejects_typos(string key, bool expected)
        {
            Assert.Equal(expected, Permissions.IsKnown(key));
        }

        [Fact]
        public void Every_catalogue_entry_is_known_and_unique()
        {
            Assert.All(Permissions.Catalogue, p => Assert.True(Permissions.IsKnown(p), p));
            Assert.Equal(Permissions.Catalogue.Count, Permissions.Catalogue.Distinct().Count());
        }
    }
}
