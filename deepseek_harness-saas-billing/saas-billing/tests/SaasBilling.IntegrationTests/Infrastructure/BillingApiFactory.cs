using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SaasBilling.IntegrationTests.Infrastructure;

public sealed class BillingApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string WebhookSecret = "integration_test_webhook_secret_123456789";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(Path.GetDirectoryName(typeof(Program).Assembly.Location)!);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
                ["Database:AutoMigrate"] = "true",
                ["Billing:Recovery:Enabled"] = "false",
                ["Payments:Test:Secret"] = WebhookSecret,
                ["Payments:Test:ToleranceSeconds"] = "300",
                ["Logging:LogLevel:Default"] = "Warning"
            });
        });
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Warning);
        });
    }
}
