using Harness.Common.Extensions;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Pacs.SyncWorker.Kafka;
using Pacs.SyncWorker.Workers;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureHostConfiguration(cfg =>
        cfg.AddJsonFile("appsettings.json", optional: false)
           .AddEnvironmentVariables())
    .ConfigureServices((ctx, services) =>
    {
        services.AddHarnessCommon(ctx.Configuration);

        // DB (transient — each scope gets its own open connection)
        var connStr = ctx.Configuration.GetConnectionString("PacsDb")
            ?? throw new InvalidOperationException("ConnectionStrings:PacsDb is required.");
        services.AddTransient<MySqlConnection>(_ => new MySqlConnection(connStr));

        // Redis
        var redisStr = ctx.Configuration.GetConnectionString("PacsRedis") ?? "localhost:6380,abortConnect=false";
        services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
            StackExchange.Redis.ConnectionMultiplexer.Connect(redisStr));

        // Kafka producer (singleton, reused across cycles)
        services.Configure<MessagingOptions>(ctx.Configuration.GetSection(MessagingOptions.SectionName));
        services.AddSingleton<IKafkaEnvelopeProducer, KafkaEnvelopeProducer>();
        services.Configure<MessagingOptions>(ctx.Configuration.GetSection(MessagingOptions.SectionName));

        // Workers
        services.AddHostedService<OutboundRelayService>();
        services.AddHostedService<InboundConsumerService>();
    })
    .Build();

await host.RunAsync();
