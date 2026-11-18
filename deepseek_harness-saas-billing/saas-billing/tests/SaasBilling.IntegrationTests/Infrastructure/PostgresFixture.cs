using Testcontainers.PostgreSql;
using Xunit;

namespace SaasBilling.IntegrationTests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public BillingApiFactory? Factory { get; private set; }
    public HttpClient? Client { get; private set; }
    public bool Available => TestEnvironment.IsAvailable;

    public async ValueTask InitializeAsync()
    {
        if (!Available)
        {
            return;
        }

        var connectionString = TestEnvironment.ExternalConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("saas_billing")
                .WithUsername("billing")
                .WithPassword("billing")
                .Build();

            await _container.StartAsync();
            connectionString = _container.GetConnectionString();
        }

        Factory = new BillingApiFactory(connectionString);
        Client = Factory.CreateClient();

        var health = await Client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        Factory?.Dispose();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
