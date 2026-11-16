namespace SaasBilling.IntegrationTests.Infrastructure;

/// <summary>
/// Integration infrastructure can use Testcontainers (preferred) or an explicitly supplied
/// PostgreSQL instance for constrained environments:
/// BILLING_TEST_CONNECTION="Host=...;Port=...;Database=...;Username=...;Password=..."
/// </summary>
public static class TestEnvironment
{
    public static string? ExternalConnectionString =>
        Environment.GetEnvironmentVariable("BILLING_TEST_CONNECTION");

    public static bool IsAvailable =>
        !string.IsNullOrWhiteSpace(ExternalConnectionString) || DockerProbe.IsAvailable;

    public const string SkipReason =
        "Docker/Testcontainers is unavailable. Set BILLING_TEST_CONNECTION to run against an external PostgreSQL.";
}
