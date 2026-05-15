using Harness.Common.Extensions;
using Harness.Common.Options;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Nldr.Api.Sync;
using Nldr.Api.TestControl;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHarnessCommon(builder.Configuration);

// ── Database ──────────────────────────────────────────────────────────────────
var nldrConnStr = builder.Configuration.GetConnectionString("NldrDb")
    ?? throw new InvalidOperationException("ConnectionStrings:NldrDb is required.");
builder.Services.AddTransient<MySqlConnection>(_ => new MySqlConnection(nldrConnStr));

// ── Caching (Redis, fail-open) ────────────────────────────────────────────────
var redisConnStr = builder.Configuration.GetConnectionString("NldrRedis") ?? "localhost:6381,abortConnect=false";
builder.Services.AddStackExchangeRedisCache(opts => opts.Configuration = redisConnStr);
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
    StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnStr));

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddMySql(nldrConnStr, name: "nldr-mysql", tags: ["ready"])
    .AddRedis(redisConnStr, name: "nldr-redis", tags: ["ready"]);

// ── Business services ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<NldrTestState>();
builder.Services.AddScoped<INldrIngestRepository, NldrIngestRepository>();
builder.Services.AddScoped<INldrIngestService, NldrIngestService>();

var app = builder.Build();

app.UseRouting();
app.MapControllers();
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = hc => hc.Tags.Contains("ready")
});

app.Run();
public partial class Program { }
