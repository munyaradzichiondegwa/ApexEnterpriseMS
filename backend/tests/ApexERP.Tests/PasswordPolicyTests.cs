using ApexERP.Application.Security;
using Xunit;

namespace ApexERP.Tests
{
    public class PasswordPolicyTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("          ")]
        public void A_blank_password_is_refused(string? password) => Assert.NotNull(PasswordPolicy.Validate(password));

        [Fact]
        public void A_short_password_is_refused() => Assert.NotNull(PasswordPolicy.Validate("Short1!"));

        [Fact]
        public void A_password_equal_to_the_email_is_refused() =>
            Assert.NotNull(PasswordPolicy.Validate("flexi@example.com", "FLEXI@example.com"));

        [Fact]
        public void A_password_longer_than_bcrypt_can_use_is_refused()
        {
            Assert.NotNull(PasswordPolicy.Validate(new string('x', 73)));
            Assert.Null(PasswordPolicy.Validate(new string('x', 72)));
            // 40 characters but 80 UTF-8 bytes: too long for BCrypt even though it looks short.
            Assert.NotNull(PasswordPolicy.Validate(new string('é', 37) + "abc"));
        }

        [Fact]
        public void A_reasonable_password_is_accepted() => Assert.Null(PasswordPolicy.Validate("Correct-Horse-9!", "someone@example.com"));
    }
}
