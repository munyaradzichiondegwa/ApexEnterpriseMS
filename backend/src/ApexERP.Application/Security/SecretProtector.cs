using System.Security.Cryptography;
using System.Text;

namespace ApexERP.Application.Security
{
    /// <summary>
    /// Authenticated encryption (AES-256-GCM) for secrets stored in the database, such as MFA seeds. The key is derived from the
    /// application's JWT secret with a purpose label, so a database leak on its own does not reveal the seeds.
    /// </summary>
    public sealed class SecretProtector
    {
        private readonly byte[] _key;
        public SecretProtector(string masterSecret, string purpose = "apexerp-mfa-seed-v1")
        {
            if (string.IsNullOrEmpty(masterSecret)) throw new ArgumentException("A master secret is required.", nameof(masterSecret));
            _key = SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "|" + masterSecret));
        }

        public string Protect(string plaintext)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var data = Encoding.UTF8.GetBytes(plaintext);
            var cipher = new byte[data.Length]; var tag = new byte[16];
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, data, cipher, tag);
            return "v1:" + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
        }

        public string Unprotect(string protectedValue)
        {
            if (!protectedValue.StartsWith("v1:", StringComparison.Ordinal)) throw new CryptographicException("Unrecognised secret format.");
            var raw = Convert.FromBase64String(protectedValue[3..]);
            if (raw.Length < 28) throw new CryptographicException("Secret is truncated.");
            var nonce = raw[..12]; var tag = raw[12..28]; var cipher = raw[28..];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);       // throws if the value was altered or the key differs
            return Encoding.UTF8.GetString(plain);
        }
    }
}
