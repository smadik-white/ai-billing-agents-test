using System.Text.Json.Serialization;
using Billing.Api;
using Billing.Api.Data;
using Billing.Api.Payments;
using Billing.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<BillingOptions>()
    .BindConfiguration("Billing")
    .Validate(o => !string.IsNullOrWhiteSpace(o.WebhookSecret), "Billing:WebhookSecret must be set")
    .ValidateOnStart();

// Configuration is read lazily so integration tests can override it via WebApplicationFactory.
builder.Services.AddDbContextFactory<BillingDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Billing")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPaymentProvider, TestPaymentProvider>();
builder.Services.AddSingleton<BillingService>();
builder.Services.AddSingleton<WebhookProcessor>();
builder.Services.AddHostedService<WebhookRecoveryService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (DomainException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = ex.StatusCode;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Code, message = ex.Message });
    }
});

// Demo-grade schema management; a production setup would use EF migrations (dotnet ef migrations add Initial).
var dbFactory = app.Services.GetRequiredService<IDbContextFactory<BillingDbContext>>();
for (var attempt = 1; ; attempt++)
{
    try
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        break;
    }
    catch (Exception ex) when (attempt < 10)
    {
        app.Logger.LogWarning(ex, "Database is not ready (attempt {Attempt}), retrying...", attempt);
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
}

app.MapBillingEndpoints();
app.Run();

public partial class Program;
