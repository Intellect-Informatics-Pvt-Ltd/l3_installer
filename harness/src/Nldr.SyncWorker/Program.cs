using Harness.Common.Extensions;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Nldr.SyncWorker.Kafka;
using Nldr.SyncWorker.Workers;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureHostConfiguration(cfg =>
        cfg.AddJsonFile("appsettings.json", optional: false)
           .AddEnvironmentVariables())
    .ConfigureServices((ctx, services) =>
    {
        services.AddHarnessCommon(ctx.Configuration);

        var connStr = ctx.Configuration.GetConnectionString("NldrDb")
            ?? throw new InvalidOperationException("ConnectionStrings:NldrDb is required.");
        services.AddTransient<MySqlConnection>(_ => new MySqlConnection(connStr));

        services.Configure<NldrKafkaOptions>(ctx.Configuration.GetSection("Messaging:Kafka"));
        services.AddSingleton<INldrKafkaProducer, NldrKafkaProducer>();

        services.Configure<NldrWorkerOptions>(ctx.Configuration.GetSection(NldrWorkerOptions.SectionName));
        services.AddHostedService<AckPublisherService>();
    })
    .Build();

await host.RunAsync();
