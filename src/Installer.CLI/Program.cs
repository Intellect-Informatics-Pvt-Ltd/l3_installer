using SharedKernel.Contracts;

namespace Installer.CLI;

/// <summary>
/// ePACS Installer CLI — Silent/unattended mode entry point.
/// Usage: Installer.CLI.exe /quiet /config:&lt;path-to-epcfg&gt; [/mode:install|upgrade|repair|uninstall]
///
/// Exit codes:
///   0 = Success
///   1 = Precheck failure (blocking prerequisite not met)
///   2 = Install/upgrade failure
///   3 = Health check failure (services not healthy after install)
///  99 = Unknown/unhandled error
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = ParseArguments(args);

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        try
        {
            Console.WriteLine($"ePACS Installer CLI — Mode: {options.Mode}, Quiet: {options.Quiet}");

            if (options.ConfigPath is not null)
            {
                Console.WriteLine($"Config: {options.ConfigPath}");
            }

            // TODO: Wire up full installer pipeline with DI
            // For now, validate arguments and return success
            if (options.Mode == InstallerMode.Install)
            {
                Console.WriteLine("Fresh install mode selected.");
            }

            await Task.CompletedTask; // Placeholder for async pipeline

            return ExitCodes.Success;
        }
        catch (Exception ex) when (ex.Message.Contains("precheck", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.Quiet)
            {
                Console.Error.WriteLine($"Precheck failed: {ex.Message}");
            }

            return ExitCodes.PrecheckFailure;
        }
        catch (Exception ex) when (ex.Message.Contains("install", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.Quiet)
            {
                Console.Error.WriteLine($"Install failed: {ex.Message}");
            }

            return ExitCodes.InstallFailure;
        }
        catch (Exception ex)
        {
            if (!options.Quiet)
            {
                Console.Error.WriteLine($"Unexpected error: {ex.Message}");
            }

            return ExitCodes.Unknown;
        }
    }

    private static CliOptions ParseArguments(string[] args)
    {
        var options = new CliOptions();

        foreach (var arg in args)
        {
            if (arg.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase))
            {
                options.Quiet = true;
            }
            else if (arg.StartsWith("/config:", StringComparison.OrdinalIgnoreCase))
            {
                options.ConfigPath = arg[8..];
            }
            else if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
            {
                options.ConfigPath = arg[9..];
            }
            else if (arg.StartsWith("/mode:", StringComparison.OrdinalIgnoreCase))
            {
                options.Mode = ParseMode(arg[6..]);
            }
            else if (arg.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))
            {
                options.Mode = ParseMode(arg[7..]);
            }
            else if (arg.Equals("/help", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                options.ShowHelp = true;
            }
        }

        return options;
    }

    private static InstallerMode ParseMode(string mode) => mode.ToLowerInvariant() switch
    {
        "install" => InstallerMode.Install,
        "upgrade" => InstallerMode.Upgrade,
        "repair" => InstallerMode.Repair,
        "uninstall" => InstallerMode.Uninstall,
        "backup" => InstallerMode.Backup,
        "restore" => InstallerMode.Restore,
        _ => InstallerMode.Install
    };

    private static void PrintUsage()
    {
        Console.WriteLine("""
            ePACS Offline Installer CLI
            
            Usage:
              Installer.CLI.exe [options]
            
            Options:
              /quiet              Run in silent mode (no console output)
              /config:<path>      Path to site configuration pack (.epcfg)
              /mode:<mode>        Operation mode: install, upgrade, repair, uninstall, backup, restore
              /help               Show this help message
            
            Exit Codes:
              0   Success
              1   Precheck failure
              2   Install/upgrade failure
              3   Health check failure
              99  Unknown error
            
            Examples:
              Installer.CLI.exe /quiet /config:D:\site-config.epcfg /mode:install
              Installer.CLI.exe /mode:upgrade
              Installer.CLI.exe /mode:backup
            """);
    }
}

internal sealed class CliOptions
{
    public bool Quiet { get; set; }
    public string? ConfigPath { get; set; }
    public InstallerMode Mode { get; set; } = InstallerMode.Install;
    public bool ShowHelp { get; set; }
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int PrecheckFailure = 1;
    public const int InstallFailure = 2;
    public const int HealthFailure = 3;
    public const int Unknown = 99;
}
