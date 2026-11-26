using ApexERP.Application.Security;
using ApexERP.Domain.Controls;
using Xunit;

namespace ApexERP.Tests
{
    public class AutomationRulesTests
    {
        private const string Rfc6238Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"; // ASCII "12345678901234567890"
        private static DateTime Unix(long s) => DateTime.UnixEpoch.AddSeconds(s);

        [Fact] public void Totp_matches_the_RFC_6238_test_vectors()
        {
            Assert.Equal("287082", Totp.Compute(Rfc6238Secret, Unix(59)));
            Assert.Equal("081804", Totp.Compute(Rfc6238Secret, Unix(1111111109)));
            Assert.Equal("005924", Totp.Compute(Rfc6238Secret, Unix(1234567890)));
            Assert.Equal("279037", Totp.Compute(Rfc6238Secret, Unix(2000000000)));
        }

        [Fact] public void Totp_verify_allows_one_step_of_drift_but_not_more()
        {
            var now = Unix(1234567890);
            Assert.True(Totp.Verify(Rfc6238Secret, "005924", now));
            Assert.True(Totp.Verify(Rfc6238Secret, "005924", now.AddSeconds(25)));
            Assert.False(Totp.Verify(Rfc6238Secret, "005924", now.AddSeconds(120)));
            Assert.False(Totp.Verify(Rfc6238Secret, "12345", now));
            Assert.False(Totp.Verify(Rfc6238Secret, null, now));
        }

        [Fact] public void Base32_round_trips_and_recovery_codes_hash_consistently()
        {
            var bytes = new byte[] { 1, 2, 3, 250, 251, 252, 0, 99 };
            Assert.Equal(bytes.Length, Totp.Base32Decode(Totp.Base32Encode(bytes)).Length);
            var codes = Totp.GenerateRecoveryCodes();
            Assert.Equal(8, codes.Count);
            Assert.Equal(Totp.HashRecoveryCode(codes[0]), Totp.HashRecoveryCode(codes[0].ToLowerInvariant().Replace("-", "")));
        }

        [Fact] public void Webhook_signature_accepts_valid_and_rejects_tampered_stale_or_missing()
        {
            var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
            var ts = new DateTimeOffset(now).ToUnixTimeSeconds().ToString();
            var sig = WebhookSignature.Sign("s3cret", ts, "{\"a\":1}");
            Assert.True(WebhookSignature.Verify("s3cret", ts, "{\"a\":1}", sig, now));
            Assert.False(WebhookSignature.Verify("s3cret", ts, "{\"a\":2}", sig, now));
            Assert.False(WebhookSignature.Verify("wrong", ts, "{\"a\":1}", sig, now));
            Assert.False(WebhookSignature.Verify("s3cret", ts, "{\"a\":1}", sig, now.AddMinutes(10)));
            Assert.False(WebhookSignature.Verify("s3cret", ts, "{\"a\":1}", null, now));
            Assert.False(WebhookSignature.Verify("", ts, "{\"a\":1}", sig, now));
        }

        [Fact] public void Retry_backoff_ends_in_dead_letter()
        {
            var now = DateTime.UtcNow;
            Assert.Equal(now.AddMinutes(1), WebhookSignature.NextAttempt(0, now));
            Assert.Equal(now.AddMinutes(240), WebhookSignature.NextAttempt(4, now));
            Assert.Null(WebhookSignature.NextAttempt(5, now));
        }

        [Fact] public void Transaction_price_is_allocated_by_standalone_selling_price_and_sums_exactly()
        {
            var a = ValuationRules.AllocateTransactionPrice(1000m, new[] { new ObligationPrice("license", 600m), new ObligationPrice("support", 300m), new ObligationPrice("training", 100m) });
            Assert.Equal(600m, a["license"]); Assert.Equal(300m, a["support"]); Assert.Equal(100m, a["training"]);
            var b = ValuationRules.AllocateTransactionPrice(100m, new[] { new ObligationPrice("x", 1m), new ObligationPrice("y", 1m), new ObligationPrice("z", 1m) });
            Assert.Equal(100m, b.Values.Sum());
            Assert.Throws<InvalidOperationException>(() => ValuationRules.AllocateTransactionPrice(100m, Array.Empty<ObligationPrice>()));
        }

        [Fact] public void Impairment_uses_the_higher_recoverable_amount_and_never_goes_negative()
        {
            Assert.Equal((70m, 30m), ValuationRules.Impairment(100m, 70m, 60m));
            Assert.Equal((90m, 10m), ValuationRules.Impairment(100m, 70m, 90m));
            Assert.Equal((150m, 0m), ValuationRules.Impairment(100m, 150m, 10m));
        }

        [Fact] public void Value_in_use_discounts_each_year()
        {
            Assert.Equal(90.91m, ValuationRules.PresentValue(new[] { 100m }, 10m));
            Assert.Equal(173.55m, ValuationRules.PresentValue(new[] { 100m, 100m }, 10m));
        }

        [Fact] public void Inventory_is_written_down_only_when_nrv_is_below_cost()
        {
            Assert.Equal((8m, 20m), ValuationRules.NrvWriteDown(10m, 10m, 9m, 1m));
            Assert.Equal((15m, 0m), ValuationRules.NrvWriteDown(10m, 10m, 20m, 5m));
        }

        [Fact] public void Credit_limit_check_honours_unlimited_and_overrides()
        {
            Assert.True(ValuationRules.WithinCreditLimit(0m, null, 99999m, 1m));
            Assert.True(ValuationRules.WithinCreditLimit(1000m, null, 600m, 400m));
            Assert.False(ValuationRules.WithinCreditLimit(1000m, null, 600m, 401m));
            Assert.True(ValuationRules.WithinCreditLimit(1000m, 2000m, 600m, 1000m));
        }

        [Fact] public void Duplicate_supplier_invoices_are_recognised_despite_formatting()
        {
            Assert.Equal(ValuationRules.NormaliseInvoiceNo("INV-001"), ValuationRules.NormaliseInvoiceNo("inv 001"));
            var d = new DateTime(2026, 11, 5);
            Assert.True(ValuationRules.LikelyDuplicate("INV-001", 100m, d, "inv001", 105m, d.AddDays(3)));
            Assert.True(ValuationRules.LikelyDuplicate("A1", 100m, d, "B2", 100m, d));
            Assert.False(ValuationRules.LikelyDuplicate("A1", 100m, d, "B2", 101m, d));
            Assert.False(ValuationRules.LikelyDuplicate(null, 100m, d, "", 101m, d.AddDays(1)));
        }

        [Fact] public void Withholding_is_a_percentage_of_the_vat_exclusive_amount()
        {
            Assert.Equal(100m, ValuationRules.Withholding(1000m, 10m));
        }

        [Fact] public void Cash_forecast_puts_overdue_items_in_week_one_and_rolls_the_balance()
        {
            var asOf = new DateTime(2026, 11, 2);
            var f = ValuationRules.CashForecast(1000m, asOf,
                new[] { (asOf.AddDays(-5), 200m), (asOf.AddDays(8), 300m) },
                new[] { (asOf.AddDays(2), 150m) }, weeks: 3);
            Assert.Equal(3, f.Count);
            Assert.Equal(200m, f[0].Inflow); Assert.Equal(150m, f[0].Outflow); Assert.Equal(1050m, f[0].Closing);
            Assert.Equal(300m, f[1].Inflow); Assert.Equal(1350m, f[1].Closing);
            Assert.Equal(1350m, f[2].Closing);
        }

        [Fact] public void Secret_protector_round_trips_and_rejects_tampering_or_a_different_key()
        {
            var p = new SecretProtector("a-long-test-master-secret-0123456789");
            var sealedValue = p.Protect("JBSWY3DPEHPK3PXP");
            Assert.True(sealedValue != "JBSWY3DPEHPK3PXP");
            Assert.Equal("JBSWY3DPEHPK3PXP", p.Unprotect(sealedValue));
            Assert.True(p.Protect("JBSWY3DPEHPK3PXP") != sealedValue);              // fresh nonce every time
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => new SecretProtector("another-master-secret-9876543210").Unprotect(sealedValue));
            var raw = Convert.FromBase64String(sealedValue.Substring(3)); raw[raw.Length - 1] ^= 1;
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => p.Unprotect("v1:" + Convert.ToBase64String(raw)));
        }
    }
}
