using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Filters
{
    /// <summary>Decides when the request-level unit of work commits. Pure so it can be unit-tested.</summary>
    public static class TransactionPolicy
    {
        public static bool IsMutating(string httpMethod) =>
            !(HttpMethods.IsGet(httpMethod) || HttpMethods.IsHead(httpMethod) ||
              HttpMethods.IsOptions(httpMethod) || HttpMethods.IsTrace(httpMethod));

        /// <summary>
        /// Commit only when the action finished without an unhandled exception and did not return an
        /// error status (4xx/5xx). A controller that catches a business-rule exception and returns
        /// 400 therefore rolls back every partial write made earlier in the same request.
        /// </summary>
        public static bool ShouldCommit(int? statusCode, bool unhandledException) =>
            !unhandledException && (statusCode is null || statusCode < 400);
    }

    /// <summary>
    /// One database transaction per mutating request (POST/PUT/PATCH/DELETE). Multi-step operations that
    /// post to the ledger and then update a business document (invoice, payroll run, asset...) are
    /// therefore atomic: either everything commits or nothing does (design document, "atomic ledger
    /// posting"; audit finding 4). Services that need their own boundary use
    /// <see cref="DbContextTransactionExtensions.InTransactionAsync{T}"/>, which joins this transaction.
    /// GET requests are left alone.
    /// </summary>
    public sealed class TransactionalActionFilter : IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (!TransactionPolicy.IsMutating(context.HttpContext.Request.Method))
            {
                await next();
                return;
            }

            var db = context.HttpContext.RequestServices.GetRequiredService<ApexErpDbContext>();
            if (!db.Database.IsRelational() || db.Database.CurrentTransaction != null)
            {
                await next();
                return;
            }

            await using var tx = await db.Database.BeginTransactionAsync(context.HttpContext.RequestAborted);

            var executed = await next();

            var status = (executed.Result as IStatusCodeActionResult)?.StatusCode;
            var failed = executed.Exception != null && !executed.ExceptionHandled;

            // Once the action has finished, always complete the commit/rollback even if the client has
            // disconnected, so a request is never left half-committed.
            if (TransactionPolicy.ShouldCommit(status, failed))
                await tx.CommitAsync(CancellationToken.None);
            else
                await tx.RollbackAsync(CancellationToken.None);
        }
    }
}
