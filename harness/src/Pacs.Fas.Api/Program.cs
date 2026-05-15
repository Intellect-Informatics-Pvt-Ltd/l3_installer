using Harness.Common.Extensions;
using Harness.Common.Options;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pacs.Fas.Api.Vouchers;

var builder = WebApplication.CreateBuilder(args);

// ── Core services ───────────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── Harness.Common (clock, error factory, options, app-logger, fault hooks) ─
builder.Services.AddHarnessCommon(builder.Configuration);

// ── Database ─────────────────────────────────────────────────────────────────
var pacsConnStr = builder.Configuration.GetConnectionString("PacsDb")
    ?? throw new InvalidOperationException("ConnectionStrings:PacsDb is required.");

builder.Services.AddTransient<MySqlConnection>(_ => new MySqlConnection(pacsConnStr));

// ── Caching (Redis, fail-open) ───────────────────────────────────────────────
var redisConnStr = builder.Configuration.GetConnectionString("PacsRedis") ?? "localhost:6380,abortConnect=false";
builder.Services.AddStackExchangeRedisCache(opts => opts.Configuration = redisConnStr);
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
    StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnStr));

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddMySql(pacsConnStr, name: "pacs-mysql", tags: ["ready"])
    .AddRedis(redisConnStr, name: "pacs-redis", tags: ["ready"]);

// ── Business services ─────────────────────────────────────────────────────────
builder.Services.AddScoped<IVoucherRepository, VoucherRepository>();
builder.Services.AddScoped<IVoucherService, VoucherService>();

var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────
app.UseRouting();
app.MapControllers();

app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = hc => hc.Tags.Contains("ready")
});

// Guard: validate PacsId on startup
using (var scope = app.Services.CreateScope())
{
    var opts = scope.ServiceProvider.GetRequiredService<IOptions<PacsOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.PacsId))
        throw new InvalidOperationException("Pacs:PacsId must be configured. Check appsettings.json or environment variables.");
}

app.Run();

// Make the program class accessible to integration test projects
public partial class Program { }
