using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Billing.Api.Data;

public static class DbExtensions
{
    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Takes a row lock (SELECT ... FOR UPDATE) inside the current transaction.
    /// Always lock in the order: event -> payment attempt -> invoice -> subscription.
    /// </summary>
    public static Task LockRowAsync(this BillingDbContext db, FormattableString sql, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(sql, ct);
}
