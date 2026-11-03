using System.Text;

namespace ApexERP.Application.Security
{
    /// <summary>
    /// Minimal password rules applied wherever a password is set (registration, user creation). Pure logic.
    /// BCrypt only looks at the first 72 bytes, so longer passwords are refused rather than silently truncated.
    /// </summary>
    public static class PasswordPolicy
    {
        public const int MinLength = 10;
        public const int MaxBytes = 72;

        /// <summary>Returns an error message, or null when the password is acceptable.</summary>
        public static string? Validate(string? password, string? email = null)
        {
            if (string.IsNullOrWhiteSpace(password))
                return "A password is required.";
            if (password.Length < MinLength)
                return $"The password must be at least {MinLength} characters long.";
            if (Encoding.UTF8.GetByteCount(password) > MaxBytes)
                return $"The password is too long (maximum {MaxBytes} bytes).";
            if (!string.IsNullOrWhiteSpace(email) && string.Equals(password, email, StringComparison.OrdinalIgnoreCase))
                return "The password must not be the same as the e-mail address.";
            return null;
        }
    }
}
