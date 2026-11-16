using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SaasBilling.Api.Data;
using SaasBilling.Api.Endpoints;
using SaasBilling.Api.Infrastructure;
using SaasBilling.Api.Payments;
using SaasBilling.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// A console sink is enough for the container and avoids Windows EventLog permissions.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BillingExceptionHandler>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<RequestContext>();

builder.Services.AddDbContextFactory<BillingDbContext>((serviceProvider, options) =>
{
    var connectionString = serviceProvider.GetRequiredService<IConfiguration>()
        .GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("ConnectionStrings:Default is required.");
    }

    options.UseNpgsql(connectionString);
});

// Scoped context for request handlers; factory supports webhook processing and background recovery.
builder.Services.AddScoped<BillingDbContext>(serviceProvider =>
    serviceProvider.GetRequiredService<IDbContextFactory<BillingDbContext>>().CreateDbContext());

builder.Services.Configure<TestPaymentOptions>(
    builder.Configuration.GetSection(TestPaymentOptions.SectionName));
builder.Services.Configure<WebhookOptions>(
    builder.Configuration.GetSection(WebhookOptions.SectionName));
builder.Services.Configure<RecoveryOptions>(
    builder.Configuration.GetSection(RecoveryOptions.SectionName));

builder.Services.AddSingleton<IPaymentProvider, TestPaymentProvider>();
builder.Services.AddScoped<BillingService>();
builder.Services.AddScoped<WebhookService>();
builder.Services.AddHostedService<WebhookRecoveryService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

app.UseMiddleware<CorrelationMiddleware>();
app.UseExceptionHandler();

app.MapBillingEndpoints();

if (app.Configuration.GetValue("Database:AutoMigrate", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("Database migrations applied");
}

app.Run();

public partial class Program;
