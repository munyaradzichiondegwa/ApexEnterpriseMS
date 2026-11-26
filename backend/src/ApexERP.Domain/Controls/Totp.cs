using System.Security.Cryptography;
using System.Text;

namespace ApexERP.Domain.Controls
{
    /// <summary>RFC 6238 time-based one-time passwords (HMAC-SHA1, 6 digits, 30 s step), compatible with Google/Microsoft Authenticator.</summary>
    public static class Totp
    {
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string GenerateSecret(int bytes = 20) => Base32Encode(RandomNumberGenerator.GetBytes(bytes));

        public static string ProvisioningUri(string issuer, string account, string secret) =>
            $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";

        public static string Compute(string base32Secret, DateTime utcNow, int stepSeconds = 30)
        {
            var counter = (long)Math.Floor((utcNow.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds / stepSeconds);
            return ComputeForCounter(Base32Decode(base32Secret), counter);
        }

        /// <summary>Accepts the current step and one step either side (clock drift). Constant-time comparison.</summary>
        public static bool Verify(string base32Secret, string? code, DateTime utcNow, int window = 1)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            code = code.Trim().Replace(" ", "");
            if (code.Length != 6 || !code.All(char.IsDigit)) return false;
            var key = Base32Decode(base32Secret);
            var counter = (long)Math.Floor((utcNow.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds / 30);
            var ok = false;
            for (var i = -window; i <= window; i++)
                ok |= CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ComputeForCounter(key, counter + i)), Encoding.ASCII.GetBytes(code));
            return ok;
        }

        public static IReadOnlyList<string> GenerateRecoveryCodes(int count = 8) =>
            Enumerable.Range(0, count).Select(_ =>
            {
                var b = RandomNumberGenerator.GetBytes(5);
                var s = Base32Encode(b);                         // 8 chars
                return s.Substring(0, 4) + "-" + s.Substring(4, 4);
            }).ToList();

        public static string HashRecoveryCode(string code) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant().Replace("-", ""))));

        private static string ComputeForCounter(byte[] key, long counter)
        {
            var msg = BitConverter.GetBytes(counter);
            if (BitConverter.IsLittleEndian) Array.Reverse(msg);
            var hash = HMACSHA1.HashData(key, msg);
            var offset = hash[^1] & 0x0F;
            var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
            return (binary % 1_000_000).ToString("D6");
        }

        public static string Base32Encode(byte[] data)
        {
            var sb = new StringBuilder();
            int buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b; bits += 8;
                while (bits >= 5) { sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
            }
            if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        public static byte[] Base32Decode(string s)
        {
            var output = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (var c in s.Trim().TrimEnd('=').ToUpperInvariant())
            {
                var v = Base32Alphabet.IndexOf(c);
                if (v < 0) throw new FormatException("Invalid base32 character.");
                buffer = (buffer << 5) | v; bits += 5;
                if (bits >= 8) { output.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
            }
            return output.ToArray();
        }
    }
}
