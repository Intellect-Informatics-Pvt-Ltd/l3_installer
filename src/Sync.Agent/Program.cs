using Installer.Core.DependencyInjection;
using SharedKernel.Hosting;
using Sync.Agent;

// The ePACS Sync Agent, v0: ledger packs out, policy packs in (ADR-0011).
//
// The composition root is the installer's own, so the exporter and the applier resolve the same
// MySQL access, secret store, fingerprinter and options the installer used to build the node.
// The Kafka->NLDR stream (Sync.Agent/Frozen/) is not registered: Packs:Mode=stream exits 4.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInstaller(builder.Configuration);

// /health/live and /health/ready on the port the service map probes (Services:Sync:HealthPort) - G31.
// Ready flips once the worker has loaded the site pack and knows which society it serves.
builder.Services.AddHealthEndpoint(builder.Configuration.GetValue("Services:Sync:HealthPort", 5080));

builder.Services.AddHostedService<PackSyncWorker>();

var host = builder.Build();
host.Run();
