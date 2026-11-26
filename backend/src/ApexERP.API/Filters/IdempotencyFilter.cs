using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Filters
{
    /// <summary>
    /// Idempotent mutating requests (spec GL-007). A client sends an <c>Idempotency-Key</c> header; the first request is
    /// processed and its response stored in the SAME transaction as the work, so a replay with the same key returns that
    /// response instead of posting twice. The same key with a different request is refused. Requests without the header
    /// behave exactly as before.
    /// </summary>
    public sealed class IdempotencyFilter : IAsyncActionFilter, IOrderedFilter
    {
        public const string HeaderName = "Idempotency-Key";
        public int Order => 100;     // after the transactional filter (order 0), so it runs inside its transaction

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static bool IsValidKey(string? key) =>
            !string.IsNullOrEmpty(key) && key.Length is >= 8 and <= 120 && key.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var http = context.HttpContext;
            if (!TransactionPolicy.IsMutating(http.Request.Method) || !http.Request.Headers.TryGetValue(HeaderName, out var values))
            { await next(); return; }

            var key = values.ToString();
            if (!IsValidKey(key))
            {
                context.Result = new BadRequestObjectResult(new { error = "Idempotency-Key must be 8-120 characters: letters, digits, '-', '_', '.' or ':'." });
                return;
            }
            if (!Guid.TryParse(http.User.FindFirst("company_id")?.Value, out var companyId)) { await next(); return; }

            var db = http.RequestServices.GetRequiredService<ApexErpDbContext>();
            var scope = (http.Request.Method + " " + http.Request.Path.Value).ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(context.ActionArguments.Where(a => a.Value is not CancellationToken).ToDictionary(a => a.Key, a => a.Value), Json))));

            var existing = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Scope == scope && r.Key == key, http.RequestAborted);
            if (existing != null)
            {
                if (existing.RequestHash != hash)
                {
                    context.Result = new ObjectResult(new { error = "This Idempotency-Key was already used for a different request." }) { StatusCode = StatusCodes.Status422UnprocessableEntity };
                    return;
                }
                http.Response.Headers["Idempotent-Replayed"] = "true";
                context.Result = new ContentResult { StatusCode = existing.ResponseStatus, Content = existing.ResponseBody, ContentType = "application/json" };
                return;
            }

            var executed = await next();
            if (executed.Exception != null && !executed.ExceptionHandled) return;
            if (executed.Result is not ObjectResult { StatusCode: null or < 400 } ok) return;     // errors are not remembered: the client may retry them

            Guid.TryParse((http.User.FindFirst("sub") ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))?.Value, out var userId);
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                CompanyId = companyId, Scope = scope, Key = key, RequestHash = hash, ResponseStatus = ok.StatusCode ?? 200,
                ResponseBody = JsonSerializer.Serialize(ok.Value, Json), UserId = userId == Guid.Empty ? null : userId
            });
            try { await db.SaveChangesAsync(http.RequestAborted); }
            catch (DbUpdateException)
            {
                // A concurrent request with the same key won the race: refuse this one so its duplicate work rolls back.
                executed.Result = new ObjectResult(new { error = "A request with this Idempotency-Key is already being processed." }) { StatusCode = StatusCodes.Status409Conflict };
            }
        }
    }
}
