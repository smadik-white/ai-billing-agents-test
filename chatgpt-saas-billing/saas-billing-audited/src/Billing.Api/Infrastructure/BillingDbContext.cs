using Billing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Billing.Api.Infrastructure;

public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<PaymentRefund> PaymentRefunds => Set<PaymentRefund>();
    public DbSet<SubscriptionPeriodGrant> SubscriptionPeriodGrants => Set<SubscriptionPeriodGrant>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<WebhookConflict> WebhookConflicts => Set<WebhookConflict>();
    public DbSet<ProductEntitlement> ProductEntitlements => Set<ProductEntitlement>();
    public DbSet<BillingAuditLog> BillingAuditLogs => Set<BillingAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>(e =>
        {
            e.ToTable("Organizations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<Subscription>(e =>
        {
            e.ToTable("Subscriptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.ProductCode).HasMaxLength(100).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Price).HasPrecision(18, 2);
            e.Property(x => x.Status).HasMaxLength(32);
            e.HasOne(x => x.Organization).WithMany(x => x.Subscriptions)
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OrganizationId, x.ProductCode }).IsUnique();
        });

        modelBuilder.Entity<Invoice>(e =>
        {
            e.ToTable("Invoices", table =>
            {
                table.HasCheckConstraint("CK_Invoices_Amounts_NonNegative", "\"TotalAmount\" > 0 AND \"AmountPaid\" >= 0 AND \"AmountRefunded\" >= 0 AND \"AmountRefunded\" <= \"AmountPaid\"");
                table.HasCheckConstraint("CK_Invoices_Period_Positive", "\"BillingPeriodDays\" BETWEEN 1 AND 366");
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Number).IsUnique();
            e.Property(x => x.Number).HasMaxLength(50);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.TotalAmount).HasPrecision(18, 2);
            e.Property(x => x.AmountPaid).HasPrecision(18, 2);
            e.Property(x => x.AmountRefunded).HasPrecision(18, 2);
            e.Property(x => x.Status).HasMaxLength(32);
            e.HasOne(x => x.Subscription).WithMany(x => x.Invoices)
                .HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<InvoiceLine>(e =>
        {
            e.ToTable("InvoiceLines");
            e.HasKey(x => x.Id);
            e.Property(x => x.Description).HasMaxLength(500).IsRequired();
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.Property(x => x.LineTotal).HasPrecision(18, 2);
            e.HasOne(x => x.Invoice).WithMany(x => x.Lines)
                .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PaymentAttempt>(e =>
        {
            e.ToTable("PaymentAttempts", table => table.HasCheckConstraint(
                "CK_PaymentAttempts_Amounts_NonNegative", "\"Amount\" > 0 AND \"RefundedAmount\" >= 0 AND \"RefundedAmount\" <= \"Amount\""));
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(50).IsRequired();
            e.Property(x => x.ProviderPaymentId).HasMaxLength(100);
            e.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.RequestFingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.RefundedAmount).HasPrecision(18, 2);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.CheckoutUrl).HasMaxLength(1000);
            e.Property(x => x.ProviderLastError).HasMaxLength(2000);
            e.HasIndex(x => new { x.Provider, x.ProviderPaymentId }).IsUnique();
            e.HasIndex(x => new { x.InvoiceId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.Status, x.ProviderRetryAfterUtc });
            e.HasOne(x => x.Invoice).WithMany(x => x.PaymentAttempts)
                .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PaymentRefund>(e =>
        {
            e.ToTable("PaymentRefunds", table => table.HasCheckConstraint("CK_PaymentRefunds_Amount_Positive", "\"Amount\" > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(50).IsRequired();
            e.Property(x => x.ProviderRefundId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.HasIndex(x => new { x.Provider, x.ProviderRefundId }).IsUnique();
            e.HasOne(x => x.PaymentAttempt).WithMany()
                .HasForeignKey(x => x.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SubscriptionPeriodGrant>(e =>
        {
            e.ToTable("SubscriptionPeriodGrants", table => table.HasCheckConstraint(
                "CK_SubscriptionPeriodGrants_Period", "\"PeriodEndUtc\" > \"PeriodStartUtc\""));
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InvoiceId).IsUnique();
            e.HasIndex(x => new { x.SubscriptionId, x.IsActive, x.PeriodEndUtc });
            e.HasOne(x => x.Subscription).WithMany(x => x.PeriodGrants)
                .HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WebhookEvent>(e =>
        {
            e.ToTable("WebhookEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(50).IsRequired();
            e.Property(x => x.ProviderEventId).HasMaxLength(200).IsRequired();
            e.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.ProviderRefundId).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.RawPayload).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasIndex(x => new { x.Provider, x.ProviderEventId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
        });

        modelBuilder.Entity<WebhookConflict>(e =>
        {
            e.ToTable("WebhookConflicts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(50).IsRequired();
            e.Property(x => x.ProviderEventId).HasMaxLength(200).IsRequired();
            e.Property(x => x.ExistingPayloadHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.IncomingPayloadHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.IncomingRawPayload).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.HasIndex(x => new { x.Provider, x.ProviderEventId, x.ReceivedAtUtc });
        });

        modelBuilder.Entity<ProductEntitlement>(e =>
        {
            e.ToTable("ProductEntitlements");
            e.HasKey(x => x.Id);
            e.Property(x => x.ProductCode).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.ProductCode }).IsUnique();
        });

        modelBuilder.Entity<BillingAuditLog>(e =>
        {
            e.ToTable("BillingAuditLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(100).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(150).IsRequired();
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAtUtc });
        });
    }
}
