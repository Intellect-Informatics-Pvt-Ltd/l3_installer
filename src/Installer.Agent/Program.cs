using Installer.Agent;
using Installer.Agent.FileSync;
using Installer.Agent.Heartbeat;
using Installer.Agent.Monitors;
using SharedKernel.Configuration;

var builder = Host.CreateApplicationBuilder(args);

// Bind configuration sections
builder.Services.Configure<InstallerOptions>(builder.Configuration.GetSection(InstallerOptions.SectionName));
builder.Services.Configure<MonitoringOptions>(builder.Configuration.GetSection(MonitoringOptions.SectionName));
builder.Services.Configure<LogRotationOptions>(builder.Configuration.GetSection(LogRotationOptions.SectionName));
builder.Services.Configure<ServicesOptions>(builder.Configuration.GetSection(ServicesOptions.SectionName));
builder.Services.Configure<FileSyncOptions>(builder.Configuration.GetSection(FileSyncOptions.SectionName));
builder.Services.Configure<HeartbeatOptions>(builder.Configuration.GetSection(HeartbeatOptions.SectionName));

// Register HTTP client factory (for heartbeat and HTTPS file sync)
builder.Services.AddHttpClient("Heartbeat");
builder.Services.AddHttpClient("FileSync");

// Register monitors
builder.Services.AddSingleton<IMonitor, DiskSpaceMonitor>();
builder.Services.AddSingleton<IMonitor, ConfigDriftMonitor>();
builder.Services.AddSingleton<IMonitor, LogRotationMonitor>();
builder.Services.AddSingleton<IMonitor, FileSyncMonitor>();
builder.Services.AddSingleton<IMonitor, HeartbeatMonitor>();

// Register the worker service
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
