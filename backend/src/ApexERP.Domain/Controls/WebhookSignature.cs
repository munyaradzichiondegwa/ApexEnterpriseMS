using System.Security.Cryptography;
using System.Text;

namespace ApexERP.Domain.Controls
{
    /// <summary>HMAC-SHA256 verification for inbound provider webhooks (spec 25.5). Constant-time; rejects stale timestamps (replay).</summary>
    public static class WebhookSignature
    {
        public static string Sign(string secret, string timestamp, string body) =>
            Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant();

        public static bool Verify(string secret, string? timestamp, string body, string? signature, DateTime utcNow, TimeSpan? tolerance = null)
        {
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature)) return false;
            if (!long.TryParse(timestamp, out var unix)) return false;
            var sent = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            if ((utcNow - sent).Duration() > (tolerance ?? TimeSpan.FromMinutes(5))) return false;
            var expected = Encoding.ASCII.GetBytes(Sign(secret, timestamp, body));
            var given = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
            return expected.Length == given.Length && CryptographicOperations.FixedTimeEquals(expected, given);
        }

        /// <summary>Retry back-off for integration messages: 1, 5, 15, 60, 240 minutes, then dead-letter.</summary>
        public static DateTime? NextAttempt(int attemptsSoFar, DateTime utcNow)
        {
            var minutes = new[] { 1, 5, 15, 60, 240 };
            return attemptsSoFar >= minutes.Length ? null : utcNow.AddMinutes(minutes[attemptsSoFar]);
        }
    }
}
