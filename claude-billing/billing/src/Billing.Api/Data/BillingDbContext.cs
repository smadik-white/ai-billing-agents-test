using Billing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api.Data;

public class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<ProductAccess> ProductAccesses => Set<ProductAccess>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<SubscriptionStatus>().HaveConversion<string>().HaveMaxLength(32);
        c.Properties<InvoiceStatus>().HaveConversion<string>().HaveMaxLength(32);
        c.Properties<PaymentAttemptStatus>().HaveConversion<string>().HaveMaxLength(32);
        c.Properties<WebhookEventStatus>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Plan>().HasIndex(p => p.Code).IsUnique();

        m.Entity<Subscription>().HasOne(s => s.Plan).WithMany().HasForeignKey(s => s.PlanId);
        m.Entity<Subscription>().HasOne<Organization>().WithMany().HasForeignKey(s => s.OrganizationId);

        m.Entity<Invoice>().HasIndex(i => i.Number).IsUnique();
        m.Entity<Invoice>().HasMany(i => i.Items).WithOne().HasForeignKey(i => i.InvoiceId);
        m.Entity<Invoice>().HasOne<Organization>().WithMany().HasForeignKey(i => i.OrganizationId);
        m.Entity<Invoice>().HasOne<Subscription>().WithMany().HasForeignKey(i => i.SubscriptionId);

        m.Entity<PaymentAttempt>().HasIndex(a => a.ProviderPaymentId).IsUnique();
        // NULLs are distinct in PostgreSQL, so attempts without a key never collide.
        m.Entity<PaymentAttempt>().HasIndex(a => new { a.InvoiceId, a.IdempotencyKey }).IsUnique();
        m.Entity<PaymentAttempt>().HasOne<Invoice>().WithMany().HasForeignKey(a => a.InvoiceId);

        m.Entity<Refund>().HasIndex(r => r.ProviderRefundId).IsUnique();
        m.Entity<Refund>().HasOne<PaymentAttempt>().WithMany().HasForeignKey(r => r.PaymentAttemptId);

        m.Entity<ProductAccess>().HasIndex(a => new { a.OrganizationId, a.ProductCode }).IsUnique();
        m.Entity<ProductAccess>().HasOne<Organization>().WithMany().HasForeignKey(a => a.OrganizationId);
        m.Entity<ProductAccess>().HasOne<Subscription>().WithMany().HasForeignKey(a => a.SubscriptionId);

        // Idempotency anchor: one row per provider event, enforced by the database.
        m.Entity<WebhookEvent>().HasIndex(e => e.ProviderEventId).IsUnique();
        m.Entity<WebhookEvent>().HasIndex(e => new { e.Status, e.NextAttemptAt });

        m.Entity<AuditLog>().HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
