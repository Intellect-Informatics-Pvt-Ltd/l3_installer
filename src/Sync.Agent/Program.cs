using SharedKernel.Configuration;
using Sync.Agent;
using Sync.Agent.Connectivity;
using Sync.Agent.Inbox;
using Sync.Agent.Outbox;

var builder = Host.CreateApplicationBuilder(args);

// Bind configuration
builder.Services.Configure<InstallerOptions>(builder.Configuration.GetSection(InstallerOptions.SectionName));
builder.Services.Configure<ServicesOptions>(builder.Configuration.GetSection(ServicesOptions.SectionName));

// Register HTTP client for NLDR communication
builder.Services.AddHttpClient("NLDR");

// Register connectivity state (singleton — shared across components)
builder.Services.AddSingleton(new ConnectivityState(
    failureThreshold: builder.Configuration.GetValue("Services:Sync:CircuitBreakerFailureThreshold", 5),
    cooldownSeconds: builder.Configuration.GetValue("Services:Sync:CircuitBreakerHalfOpenSeconds", 300)));

// Register components
builder.Services.AddSingleton<IOutboxRelay, OutboxRelay>();
builder.Services.AddSingleton<IInboxProcessor, InboxProcessor>();
builder.Services.AddSingleton<ConnectivityMonitor>();

// Register the worker
builder.Services.AddHostedService<SyncAgentWorker>();

var host = builder.Build();
host.Run();
