namespace SaasBilling.Domain;

public sealed class Organization
{
    private Organization()
    {
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public ICollection<Subscription> Subscriptions { get; private set; } = new List<Subscription>();

    public static Organization Create(string name, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            throw new BillingValidationException("Organization name is required and must be at most 200 characters.");
        }

        return new Organization
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            CreatedAt = now
        };
    }
}
