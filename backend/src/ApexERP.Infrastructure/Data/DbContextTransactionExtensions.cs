using Microsoft.EntityFrameworkCore;

namespace ApexERP.Infrastructure.Data
{
    /// <summary>
    /// Transaction and locking helpers for the posting engine (design document: "atomic ledger posting",
    /// repository audit findings 4 and 5).
    /// </summary>
    public static class DbContextTransactionExtensions
    {
        private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

        /// <summary>
        /// Runs <paramref name="work"/> inside a database transaction and commits only if it completes.
        /// If a transaction is already open (the API opens one per mutating request) the work simply joins
        /// it, so nested calls never create a second, independent transaction. Non-relational providers
        /// (in-memory test doubles) just run the work.
        /// </summary>
        public static async Task<T> InTransactionAsync<T>(this DbContext db, Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
        {
            if (!db.Database.IsRelational() || db.Database.CurrentTransaction != null)
                return await work(ct);

            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var result = await work(ct);
                await tx.CommitAsync(ct);   // leaving the scope without committing rolls everything back
                return result;
            });
        }

        public static Task InTransactionAsync(this DbContext db, Func<CancellationToken, Task> work, CancellationToken ct = default) =>
            db.InTransactionAsync<bool>(async token => { await work(token); return true; }, ct);

        /// <summary>
        /// Takes a transaction-scoped PostgreSQL advisory lock for <paramref name="key"/>; it is released
        /// automatically when the surrounding transaction commits or rolls back. Concurrent requests that use
        /// the same key therefore run one after another (used for journal numbering, period close versus
        /// posting, and double-reversal protection). Other providers: no-op.
        /// </summary>
        public static async Task AcquireTransactionLockAsync(this DbContext db, string key, CancellationToken ct = default)
        {
            if (db.Database.ProviderName != NpgsqlProvider) return;

            if (db.Database.CurrentTransaction == null)
                throw new InvalidOperationException(
                    "A transaction lock can only be taken inside a transaction (wrap the operation in InTransactionAsync).");

            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
        }

        /// <summary>
        /// Like <see cref="AcquireTransactionLockAsync"/> but never throws: it takes the lock when a PostgreSQL
        /// transaction is open (every mutating API request has one) and otherwise does nothing. Used for
        /// document-number generators, which are only ever called while handling a request.
        /// </summary>
        public static async Task<bool> TryAcquireTransactionLockAsync(this DbContext db, string key, CancellationToken ct = default)
        {
            if (db.Database.ProviderName != NpgsqlProvider || db.Database.CurrentTransaction == null) return false;
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            return true;
        }
    }
}
