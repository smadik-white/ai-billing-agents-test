namespace SaasBilling.Domain;

public sealed class ProductAccess
{
    private ProductAccess()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Organization Organization { get; private set; } = null!;
    public string ProductCode { get; private set; } = string.Empty;
    public Guid InvoiceId { get; private set; }
    public AccessStatus Status { get; private set; }
    public DateTimeOffset GrantedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static ProductAccess Grant(Guid organizationId, string productCode, Guid invoiceId, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new BillingValidationException("OrganizationId is required for product access.");
        }

        if (string.IsNullOrWhiteSpace(productCode) || productCode.Trim().Length > 100)
        {
            throw new BillingValidationException("Product code is required and must be at most 100 characters.");
        }

        return new ProductAccess
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ProductCode = productCode.Trim().ToLowerInvariant(),
            InvoiceId = invoiceId,
            Status = AccessStatus.Active,
            GrantedAt = now
        };
    }

    /// <summary>Re-grants existing access (e.g. a new paid invoice after a previous revocation).</summary>
    public bool ReGrant(Guid invoiceId, DateTimeOffset now)
    {
        var changed = Status != AccessStatus.Active || InvoiceId != invoiceId;
        Status = AccessStatus.Active;
        InvoiceId = invoiceId;
        GrantedAt = now;
        RevokedAt = null;
        return changed;
    }

    public bool Revoke(DateTimeOffset now)
    {
        if (Status == AccessStatus.Revoked)
        {
            return false;
        }

        Status = AccessStatus.Revoked;
        RevokedAt = now;
        return true;
    }
}
