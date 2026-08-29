using Installer.Core.DependencyInjection;
using Installer.Core.Pipeline;
using Installer.Core.SiteConfig;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharedKernel.Contracts;

namespace Installer.CLI;

/// <summary>
/// ePACS Installer CLI — the entry point for every installer operation.
///
/// This file used to be a <c>Console.WriteLine</c> and
/// <c>// TODO: Wire up full installer pipeline with DI</c>: it parsed arguments, printed them,
/// and returned 0. It now builds the composition root, runs the pipeline, and maps the outcome
/// onto an exit code.
///
/// TWO BEHAVIOURS WORTH KNOWING ABOUT BEFORE YOU RUN IT:
///
/// 1. <b>Dry run is the default.</b> Every mode reports what it would do and changes nothing
///    until <c>--apply</c> is passed. This mirrors the estate's own <c>ops/l2r2</c> contract,
///    and it means the safe invocation is the short one.
///
/// 2. <b>A mode with no engine fails, loudly.</b> Upgrade, restore, repair and backup exit 4
///    rather than 0. The previous behaviour — <c>/mode:upgrade</c> returning success having
///    done nothing — is the single most dangerous thing this program used to do, because the
///    next action after a successful upgrade is to decommission the old media.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);

        if (options.ShowHelp)
        {
            Console.WriteLine(CliOptions.Usage);
            return ExitCodes.Success;
        }

        if (options.ParseError is not null)
        {
            Console.Error.WriteLine($"error: {options.ParseError}");
            Console.Error.WriteLine("Run with --help for usage.");
            return ExitCodes.Usage;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Cancel cooperatively. The state machine has checkpointed every phase, so an
            // interrupted run resumes; killing the process outright would leave the lock held.
            e.Cancel = true;
            Console.Error.WriteLine("Interrupt received — finishing the current step and stopping. The run is resumable.");
            cts.Cancel();
        };

        try
        {
            using var provider = BuildProvider(options);
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Installer.CLI");

            SiteConfigPack? siteConfig = null;
            if (options.ConfigPath is not null)
            {
                var loader = provider.GetRequiredService<ISiteConfigLoader>();
                siteConfig = await loader.LoadAsync(options.ConfigPath, options.AllowUnsignedConfig, cts.Token);
            }

            var request = new PipelineRequest
            {
                Mode = options.Mode,
                SiteConfig = siteConfig,
                MediaDirectory = options.MediaDirectory,
                BackupPath = options.BackupPath,
                RegenerateConfiguration = options.RegenerateConfiguration,
                ReplaceBinaries = options.ReplaceBinaries,
                PurgeData = options.PurgeData,
                OverrideToken = options.OverrideToken,
                TypedConfirmation = options.TypedConfirmation,
                DryRun = !options.Apply
            };

            var pipeline = provider.GetRequiredService<IInstallerPipeline>();
            var result = await pipeline.RunAsync(request, cts.Token);

            Report(result, options.Quiet);
            return ExitCodes.From(result.Outcome);
        }
        catch (SiteConfigException ex)
        {
            Console.Error.WriteLine($"Site configuration: {ex.Message}");
            return ExitCodes.Usage;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled. The run is checkpointed and can be resumed by invoking the same command again.");
            return ExitCodes.Cancelled;
        }
#pragma warning disable CA1031 // Top of the process: an unhandled exception here would print a
        catch (Exception ex) // stack trace to a field operator and return an undefined code.
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"Unexpected error: {ex.Message}");
            Console.Error.WriteLine("Generate a support bundle and send it with this message.");
            return ExitCodes.Unknown;
        }
    }

    /// <summary>
    /// Builds the composition root.
    ///
    /// Configuration precedence, lowest first: appsettings.json, appsettings.Production.json,
    /// then environment variables prefixed <c>EPACS_</c>. The .epcfg is deliberately NOT a
    /// configuration source — it is data the pipeline receives, so a site pack can never
    /// silently redefine an installer path or a threshold.
    /// </summary>
    private static ServiceProvider BuildProvider(CliOptions options)
    {
        var baseDir = AppContext.BaseDirectory;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("EPACS_")
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => ConfigureLogging(builder, options));
        services.AddInstaller(configuration);

        // Validate the graph now rather than on first resolve: a missing registration should
        // fail before the installer has touched the machine, not three phases in.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    /// <summary>
    /// AC-2.4 requires that quiet mode writes no console output. Until a file sink is wired
    /// (Serilog via Intellect.Erp.Observability, tasks.md 12.4), quiet mode is honoured by
    /// emitting nothing rather than by pretending the file log exists — an operator who is told
    /// "see the log file" and finds none is worse off than one told there is no log yet.
    /// </summary>
    private static void ConfigureLogging(ILoggingBuilder builder, CliOptions options)
    {
        builder.ClearProviders();

        if (!options.Quiet)
        {
            builder.AddSimpleConsole(c =>
            {
                c.SingleLine = true;
                c.TimestampFormat = "HH:mm:ss ";
            });
        }

        builder.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information);
    }

    private static void Report(PipelineResult result, bool quiet)
    {
        if (quiet)
        {
            return;
        }

        var writer = result.Outcome == PipelineOutcome.Success ? Console.Out : Console.Error;

        writer.WriteLine();
        foreach (var step in result.Steps)
        {
            writer.WriteLine($"  - {step}");
        }

        writer.WriteLine();
        writer.WriteLine(result.Outcome == PipelineOutcome.Success
            ? $"OK  {result.Message}"
            : $"FAILED ({result.Outcome})  {result.Message}");
    }
}

/// <summary>
/// Exit codes. Stable: scripts and the enterprise rollout tooling branch on these, so treat a
/// code as an interface — add, never renumber.
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int PrecheckFailure = 1;
    public const int InstallFailure = 2;
    public const int HealthFailure = 3;
    public const int NotImplemented = 4;
    public const int Refused = 5;
    public const int Usage = 64;   // sysexits.h EX_USAGE — distinguishable from an operation failure
    public const int Cancelled = 130; // 128 + SIGINT, the shell convention
    public const int Unknown = 99;

    public static int From(PipelineOutcome outcome) => outcome switch
    {
        PipelineOutcome.Success => Success,
        PipelineOutcome.PrecheckFailed => PrecheckFailure,
        PipelineOutcome.OperationFailed => InstallFailure,
        PipelineOutcome.HealthFailed => HealthFailure,
        PipelineOutcome.NotImplemented => NotImplemented,
        PipelineOutcome.Refused => Refused,
        _ => Unknown
    };
}
