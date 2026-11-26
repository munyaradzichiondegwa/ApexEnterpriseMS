using System.Text.Json;
using ApexERP.Domain.Common;
using ApexERP.Domain.Controls;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public sealed record ParsedPayment(string ExternalRef, string Status, decimal Amount, string Currency, string Reference, DateTime? PaidAt);

    /// <summary>
    /// A payment provider's way of proving a message is genuine and of describing a payment. Every supported provider shares the
    /// HMAC-signed JSON envelope below. A provider whose real format differs (its own hash scheme, form-encoded callbacks, a
    /// different status vocabulary) is supported by adding one class that implements this interface, once its specification and
    /// credentials are in hand; nothing else in the pipeline changes.
    /// </summary>
    public interface IPaymentProviderAdapter
    {
        string Provider { get; }
        bool VerifySignature(string secret, string? timestamp, string body, string? signature, DateTime utcNow);
        ParsedPayment Parse(string body);
    }

    /// <summary>Signed JSON: { "externalRef": "...", "status": "Paid|Failed|Pending", "amount": 12.5, "currency": "USD", "reference": "INV-2026-00001", "paidAt": "..." }.</summary>
    public sealed class SignedJsonAdapter : IPaymentProviderAdapter
    {
        public SignedJsonAdapter(string provider) { Provider = provider; }
        public string Provider { get; }

        public bool VerifySignature(string secret, string? timestamp, string body, string? signature, DateTime utcNow) =>
            WebhookSignature.Verify(secret, timestamp, body, signature, utcNow);

        public ParsedPayment Parse(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            string Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var externalRef = Str("externalRef");
            if (string.IsNullOrWhiteSpace(externalRef)) throw new InvalidOperationException("externalRef is required.");
            if (!r.TryGetProperty("amount", out var amt) || !amt.TryGetDecimal(out var amount) || amount <= 0) throw new InvalidOperationException("A positive amount is required.");
            var status = Str("status"); if (status is not ("Paid" or "Failed" or "Pending")) throw new InvalidOperationException("status must be Paid, Failed or Pending.");
            DateTime? paidAt = DateTime.TryParse(Str("paidAt"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
            return new ParsedPayment(externalRef.Trim(), status, amount, string.IsNullOrWhiteSpace(Str("currency")) ? "USD" : Str("currency").ToUpperInvariant(), Str("reference").Trim(), paidAt);
        }
    }

    /// <summary>
    /// Inbound payment notifications from EcoCash, OneMoney, PayNow, ZimSwitch and bank APIs (spec 25.3, 25.5): signature check, idempotent
    /// storage, posting a customer receipt, retry with back-off and a dead-letter queue.
    /// </summary>
    public class IntegrationService
    {
        public static readonly string[] Providers = { "EcoCash", "OneMoney", "PayNow", "ZimSwitch", "BankApi" };
        private readonly ApexErpDbContext _db;
        private readonly ArService _ar;
        private readonly SettingsService _settings;
        private readonly AuditService _audit;
        private readonly Func<string, string?> _secretResolver;
        private readonly Dictionary<string, IPaymentProviderAdapter> _adapters;

        /// <param name="secretResolver">Maps a secret name to its value (environment variable by default). Secrets are never stored in the database.</param>
        public IntegrationService(ApexErpDbContext db, ArService ar, SettingsService settings, AuditService audit, Func<string, string?>? secretResolver = null, IEnumerable<IPaymentProviderAdapter>? adapters = null)
        {
            _db = db; _ar = ar; _settings = settings; _audit = audit;
            _secretResolver = secretResolver ?? (name => Environment.GetEnvironmentVariable(name));
            var supplied = adapters?.ToList() ?? new List<IPaymentProviderAdapter>();
            // Providers without a dedicated adapter use the signed-JSON envelope.
            var all = supplied.Concat(Providers.Where(p => supplied.All(a => !a.Provider.Equals(p, StringComparison.OrdinalIgnoreCase))).Select(p => (IPaymentProviderAdapter)new SignedJsonAdapter(p)));
            _adapters = all.ToDictionary(a => a.Provider, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<IntegrationEndpoint> UpsertEndpointAsync(Guid companyId, string provider, string name, string? baseUrl, string secretName, Guid? settlementBankGlAccountId, bool active, Guid userId, CancellationToken ct = default)
        {
            if (!Providers.Contains(provider, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Provider must be one of: " + string.Join(", ", Providers));
            if (string.IsNullOrWhiteSpace(secretName) || !secretName.All(c => char.IsLetterOrDigit(c) || c == '_')) throw new InvalidOperationException("SecretName must be the name of an environment variable (letters, digits, underscore).");
            if (baseUrl != null && !Uri.TryCreate(baseUrl, UriKind.Absolute, out var u)) throw new InvalidOperationException("BaseUrl must be an absolute URL.");
            var canonical = Providers.First(p => p.Equals(provider, StringComparison.OrdinalIgnoreCase));
            var e = await _db.IntegrationEndpoints.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.Provider == canonical, ct);
            if (e == null) { e = new IntegrationEndpoint { CompanyId = companyId, Provider = canonical, CreatedBy = userId }; _db.IntegrationEndpoints.Add(e); }
            e.Name = name; e.BaseUrl = baseUrl; e.SecretName = secretName; e.IsActive = active; e.SettlementBankGlAccountId = settlementBankGlAccountId?.ToString();
            _audit.Record(companyId, userId, "Integration.EndpointSaved", "Integrations", nameof(IntegrationEndpoint), e.Id, null, new { canonical, name, secretName, active });
            await _db.SaveChangesAsync(ct); return e;
        }

        public Task<List<IntegrationEndpoint>> ListEndpointsAsync(Guid companyId, CancellationToken ct = default) =>
            _db.IntegrationEndpoints.Where(e => e.CompanyId == companyId).OrderBy(e => e.Provider).ToListAsync(ct);

        /// <summary>Entry point for a webhook. Returns (httpStatus, message). Never throws for bad input: the provider must get a clean answer.</summary>
        public async Task<(int Status, string Message)> ReceiveWebhookAsync(Guid companyId, string provider, string body, string? timestamp, string? signature, CancellationToken ct = default)
        {
            if (body.Length > 64 * 1024) return (413, "Payload too large.");
            var endpoint = await _db.IntegrationEndpoints.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.CompanyId == companyId && e.Provider == provider && e.IsActive, ct);
            if (endpoint == null || !_adapters.TryGetValue(endpoint.Provider, out var adapter)) return (404, "Unknown integration.");
            var secret = _secretResolver(endpoint.SecretName);
            if (string.IsNullOrEmpty(secret)) return (503, "Integration is not configured.");

            if (!adapter.VerifySignature(secret, timestamp, body, signature, DateTime.UtcNow))
                return (401, "Invalid signature.");          // the API logs the rejection; this request's transaction rolls back, so nothing is written here

            // The signature proves the message came from this company's provider, so work may now proceed in that company's context.
            _db.CurrentCompanyId = companyId;

            ParsedPayment parsed;
            try { parsed = adapter.Parse(body); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return (400, "Malformed payload: " + ex.Message); }

            var existing = await _db.IntegrationMessages.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.CompanyId == companyId && m.Provider == endpoint.Provider && m.ExternalRef == parsed.ExternalRef, ct);
            if (existing != null) return (200, "Already received.");      // providers retry; the second delivery is acknowledged and ignored

            var msg = new IntegrationMessage { CompanyId = companyId, Provider = endpoint.Provider, Direction = "Inbound", ExternalRef = parsed.ExternalRef, PayloadJson = body, SignatureValid = true };
            _db.IntegrationMessages.Add(msg);
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return (200, "Already received."); }     // lost a race with a concurrent duplicate delivery

            await ProcessAsync(msg, parsed, endpoint, ct);
            return (200, msg.Status);
        }

        private static PaymentMethod MethodFor(string provider) => provider switch
        {
            "EcoCash" => PaymentMethod.EcoCash, "OneMoney" => PaymentMethod.OneMoney, "ZimSwitch" => PaymentMethod.Card, "BankApi" => PaymentMethod.BankTransfer, _ => PaymentMethod.Other
        };

        private async Task ProcessAsync(IntegrationMessage msg, ParsedPayment p, IntegrationEndpoint endpoint, CancellationToken ct)
        {
            msg.Attempts++;
            try
            {
                if (p.Status == "Failed") { msg.Status = "Processed"; msg.LastError = "Provider reported the payment as failed; nothing posted."; }
                else if (p.Status == "Pending") { msg.Status = "Pending"; msg.NextAttemptAt = null; }
                else
                {
                    var companyId = msg.CompanyId;
                    var invoice = await _db.ArInvoices.Where(i => i.CompanyId == companyId && i.InvoiceNo == p.Reference && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).FirstOrDefaultAsync(ct)
                        ?? throw new InvalidOperationException($"No open invoice with number '{p.Reference}'.");
                    if (invoice.Currency != p.Currency) throw new InvalidOperationException($"Currency {p.Currency} does not match the invoice currency {invoice.Currency}.");
                    var apply = Math.Min(p.Amount, invoice.Outstanding);
                    if (apply <= 0) throw new InvalidOperationException("The invoice is already settled.");
                    if (!Guid.TryParse(endpoint.SettlementBankGlAccountId, out var bankGl)) throw new InvalidOperationException("No settlement bank account is configured for this integration.");
                    var arAccountText = await _settings.GetAsync(companyId, "integrations.arControlAccountId", ct);
                    if (!Guid.TryParse(arAccountText, out var arControl)) throw new InvalidOperationException("Set the company setting integrations.arControlAccountId to the trade debtors account.");
                    var receipt = await _db.InTransactionAsync(async token =>
                        await _ar.ReceiveAndAllocateAsync(companyId, invoice.CustomerId, (p.PaidAt ?? DateTime.UtcNow).Date, p.Currency, apply, MethodFor(endpoint.Provider),
                            $"{endpoint.Provider}:{p.ExternalRef}", bankGl, arControl, new Dictionary<Guid, decimal> { [invoice.Id] = apply }, token), ct);
                    msg.ResultReceiptId = receipt.Id; msg.Status = "Processed"; msg.LastError = p.Amount > apply ? $"Overpayment of {p.Amount - apply:N2} not applied; refund or allocate manually." : null;
                }
            }
            catch (InvalidOperationException ex)
            {
                msg.LastError = ex.Message;
                msg.NextAttemptAt = WebhookSignature.NextAttempt(msg.Attempts - 1, DateTime.UtcNow);
                msg.Status = msg.NextAttemptAt == null ? "DeadLetter" : "Failed";
            }
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>Retries failed messages whose back-off has elapsed. Run by the background scheduler.</summary>
        public async Task<int> RetryDueAsync(Guid companyId, CancellationToken ct = default)
        {
            var due = await _db.IntegrationMessages.Where(m => m.CompanyId == companyId && m.Status == "Failed" && m.NextAttemptAt != null && m.NextAttemptAt <= DateTime.UtcNow).ToListAsync(ct);
            var n = 0;
            foreach (var m in due)
            {
                var endpoint = await _db.IntegrationEndpoints.FirstOrDefaultAsync(e => e.CompanyId == companyId && e.Provider == m.Provider, ct);
                if (endpoint == null || !_adapters.TryGetValue(endpoint.Provider, out var adapter)) continue;
                await ProcessAsync(m, adapter.Parse(m.PayloadJson), endpoint, ct); n++;
            }
            return n;
        }

        /// <summary>Puts a dead-lettered message back in the queue after the cause has been fixed (e.g. the invoice now exists).</summary>
        public async Task<IntegrationMessage> RequeueAsync(Guid companyId, Guid id, Guid userId, CancellationToken ct = default)
        {
            var m = await _db.IntegrationMessages.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Message not found.");
            if (m.Status is not ("DeadLetter" or "Failed")) throw new InvalidOperationException("Only failed or dead-lettered messages can be requeued.");
            m.Status = "Failed"; m.Attempts = 0; m.NextAttemptAt = DateTime.UtcNow;
            _audit.Record(companyId, userId, "Integration.MessageRequeued", "Integrations", nameof(IntegrationMessage), m.Id, null, new { m.Provider, m.ExternalRef });
            await _db.SaveChangesAsync(ct); return m;
        }

        public Task<List<IntegrationMessage>> ListMessagesAsync(Guid companyId, string? status, CancellationToken ct = default)
        {
            var q = _db.IntegrationMessages.Where(m => m.CompanyId == companyId);
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(m => m.Status == status);
            return q.OrderByDescending(m => m.CreatedAt).Take(300).ToListAsync(ct);
        }

        /// <summary>Provider transactions the bank never received, or receipts with no provider message: the integration-to-ledger reconciliation (spec 25.3).</summary>
        public async Task<object> ReconcileAsync(Guid companyId, DateTime from, DateTime to, CancellationToken ct = default)
        {
            var msgs = await _db.IntegrationMessages.Where(m => m.CompanyId == companyId && m.CreatedAt >= from && m.CreatedAt <= to).ToListAsync(ct);
            var receiptIds = msgs.Where(m => m.ResultReceiptId != null).Select(m => m.ResultReceiptId!.Value).ToList();
            var receipts = await _db.ArReceipts.Where(r => r.CompanyId == companyId && receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            return new
            {
                From = from, To = to, Messages = msgs.Count,
                Processed = msgs.Count(m => m.Status == "Processed"), Pending = msgs.Count(m => m.Status == "Pending"),
                Failed = msgs.Count(m => m.Status == "Failed"), DeadLetter = msgs.Count(m => m.Status == "DeadLetter"),
                PostedTotal = receipts.Values.Sum(r => r.Amount),
                NeedsAttention = msgs.Where(m => m.Status is "Failed" or "DeadLetter").Select(m => new { m.Id, m.Provider, m.ExternalRef, m.Status, m.LastError, m.Attempts }).ToList()
            };
        }
    }
}
