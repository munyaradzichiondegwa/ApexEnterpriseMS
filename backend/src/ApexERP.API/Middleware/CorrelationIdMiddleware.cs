using System.Diagnostics;

namespace ApexERP.API.Middleware
{
    /// <summary>
    /// Gives every request a correlation id (spec 22.3): taken from the caller's X-Correlation-Id when it is a safe value, otherwise generated.
    /// It is returned on the response, attached to every log line written during the request, and stored with the audit client IP in the DbContext.
    /// </summary>
    public sealed class CorrelationIdMiddleware
    {
        public const string HeaderName = "X-Correlation-Id";
        private readonly RequestDelegate _next;
        private readonly ILogger<CorrelationIdMiddleware> _logger;

        public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger) { _next = next; _logger = logger; }

        public static bool IsSafe(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 64 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

        public async Task Invoke(HttpContext context)
        {
            var incoming = context.Request.Headers[HeaderName].ToString();
            var id = IsSafe(incoming) ? incoming : (Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"));
            context.Items[HeaderName] = id;
            context.Response.OnStarting(() => { context.Response.Headers[HeaderName] = id; return Task.CompletedTask; });

            using (_logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = id, ["Path"] = context.Request.Path.Value }))
            {
                var sw = Stopwatch.StartNew();
                await _next(context);
                sw.Stop();
                _logger.LogInformation("{Method} {Path} -> {Status} in {Elapsed} ms", context.Request.Method, context.Request.Path.Value, context.Response.StatusCode, sw.ElapsedMilliseconds);
            }
        }
    }
}
