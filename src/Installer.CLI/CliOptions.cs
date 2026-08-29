using SharedKernel.Contracts;

namespace Installer.CLI;

/// <summary>
/// Parsed command line.
///
/// Its own type rather than inline parsing in Main so it can be tested without running an
/// installer. Both the Windows-idiomatic <c>/flag:value</c> and the POSIX <c>--flag=value</c>
/// forms are accepted: the installer is driven by hand from a Windows console at a PACS site
/// and by scripts from a rollout tool, and neither audience should have to learn the other's
/// convention.
///
/// An unrecognised argument is a parse ERROR, never ignored. A silently dropped
/// <c>--apply</c> means an operator believes they installed something and did not; a silently
/// dropped <c>--purge-data</c> means the opposite kind of surprise.
/// </summary>
internal sealed class CliOptions
{
    public bool Quiet { get; private set; }
    public bool Verbose { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool Apply { get; private set; }
    public bool AllowUnsignedConfig { get; private set; }
    public bool PurgeData { get; private set; }
    public bool RegenerateConfiguration { get; private set; }
    public bool ReplaceBinaries { get; private set; }
    public string? ConfigPath { get; private set; }
    public string? MediaDirectory { get; private set; }

    /// <summary>The backup to restore from. Required for --mode=restore; never guessed.</summary>
    public string? BackupPath { get; private set; }
    public string? OverrideToken { get; private set; }
    public string? TypedConfirmation { get; private set; }

    /// <summary>Null means auto-detect from what is already installed.</summary>
    public InstallerMode? Mode { get; private set; }

    public string? ParseError { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var o = new CliOptions();

        foreach (var arg in args)
        {
            if (!o.TryApply(arg))
            {
                o.ParseError ??= $"unrecognised argument '{arg}'";
            }
        }

        if (o.ShowHelp || o.ParseError is not null)
        {
            return o;
        }

        Validate(o);
        return o;
    }

    private bool TryApply(string arg)
    {
        var (name, value) = Split(arg);

        switch (name)
        {
            case "quiet": Quiet = true; return true;
            case "verbose": Verbose = true; return true;
            case "help" or "h" or "?": ShowHelp = true; return true;
            case "apply": Apply = true; return true;
            case "dry-run": Apply = false; return true;
            case "allow-unsigned-config": AllowUnsignedConfig = true; return true;
            case "purge-data": PurgeData = true; return true;
            case "regenerate-config": RegenerateConfiguration = true; return true;
            case "replace-binaries": ReplaceBinaries = true; return true;

            case "config": ConfigPath = value; return value is not null;
            case "media": MediaDirectory = value; return value is not null;
            case "backup": BackupPath = value; return value is not null;
            case "override-token": OverrideToken = value; return value is not null;
            case "confirm": TypedConfirmation = value; return value is not null;

            case "mode":
                if (value is null) return false;
                if (!Enum.TryParse<InstallerMode>(value, ignoreCase: true, out var mode))
                {
                    ParseError ??= $"unknown mode '{value}'. Expected one of: {string.Join(", ", Enum.GetNames<InstallerMode>()).ToLowerInvariant()}";
                    return true; // consumed; the error is more specific than "unrecognised"
                }
                Mode = mode;
                return true;

            default: return false;
        }
    }

    /// <summary>Accepts <c>/name:value</c>, <c>--name=value</c>, <c>-name</c> and bare flags.</summary>
    private static (string Name, string? Value) Split(string arg)
    {
        var trimmed = arg.TrimStart('/', '-');
        var sep = trimmed.IndexOfAny([':', '=']);
        return sep < 0
            ? (trimmed.ToLowerInvariant(), null)
            : (trimmed[..sep].ToLowerInvariant(), trimmed[(sep + 1)..].Trim('"'));
    }

    /// <summary>
    /// Cross-argument rules. Caught here rather than deep in the pipeline so an operator who
    /// mistyped a purge invocation is told at the prompt, before anything is stopped.
    /// </summary>
    private static void Validate(CliOptions o)
    {
        if (o.PurgeData && o.Mode != InstallerMode.Uninstall)
        {
            o.ParseError ??= "--purge-data is only valid with --mode=uninstall.";
            return;
        }

        if (o.PurgeData && o.Apply && (o.OverrideToken is null || o.TypedConfirmation is null))
        {
            o.ParseError ??=
                "--purge-data destroys this node's business data and needs both --override-token=<jwt> and " +
                "--confirm=\"PURGE <pacs_id>\". Run without --apply first to see exactly what would be removed.";
            return;
        }

        if (o.Mode == InstallerMode.Restore && o.BackupPath is null)
        {
            o.ParseError ??= "--mode=restore needs --backup=<path>. The installer will not guess which backup to " +
                             "overwrite this node's data with.";
            return;
        }

        if (o.BackupPath is not null && o.Mode != InstallerMode.Restore)
        {
            o.ParseError ??= "--backup is only valid with --mode=restore.";
            return;
        }

        if (o.Quiet && o.Verbose)
        {
            o.ParseError ??= "--quiet and --verbose contradict each other.";
        }
    }

    public static string Usage => """
        ePACS Offline Installer

        Usage:
          Installer.CLI [options]

        DRY RUN IS THE DEFAULT. Nothing on this machine changes until you pass --apply.

        Options:
          --mode=<mode>            install | upgrade | repair | uninstall | backup | restore
                                   Omit to auto-detect from what is already installed.
          --config=<path>          Site configuration pack (.epcfg). Required for install.
          --media=<dir>            Directory holding the release manifest and payloads.
                                   Defaults to the manifest's own directory.
          --backup=<path>          Restore only. The backup package to restore from. Required:
                                   the installer will not guess which backup to overwrite this
                                   node's data with.
          --apply                  Perform the operation. Without it, nothing is changed.
          --quiet                  No console output (for unattended rollout).
          --verbose                Debug-level logging.
          --allow-unsigned-config  Accept an unsigned .epcfg. DEVELOPMENT ONLY.
          --regenerate-config      Repair only. Regenerate configuration from templates even
                                   if it looks intact. DISCARDS hand edits.
          --replace-binaries       Repair only. Re-lay binaries even if they look intact.
          --purge-data             Uninstall only. Destroys business data. Requires
                                   --override-token and --confirm.
          --override-token=<jwt>   Signed governance token authorising a purge.
          --confirm="PURGE <id>"   Typed confirmation matching the token's pacs_id.
          --help                   This message.

        Windows-style /flag:value is accepted everywhere --flag=value is.

        Exit codes:
          0    Success
          1    Precheck failure — a prerequisite was not met; nothing was changed
          2    Operation failure
          3    Health check failure after install
          4    Mode not implemented in this build — NOTHING WAS DONE. Do not treat the
               node as upgraded, restored or backed up.
          5    Refused — another installer is running, or a required input was missing
          64   Usage error
          99   Unexpected error
          130  Cancelled (Ctrl+C); the run is checkpointed and resumable

        Examples:
          Installer.CLI --config=D:\site.epcfg                     # dry run: what would happen
          Installer.CLI --config=D:\site.epcfg --apply             # install
          Installer.CLI --quiet --config=D:\site.epcfg --apply     # unattended
          Installer.CLI --mode=uninstall --apply                   # remove, keep the data
          Installer.CLI --mode=upgrade --media=E:\media --apply    # upgrade, backing up first
          Installer.CLI --mode=restore --backup=D:\ePACSData\backups\bk-123 --apply
          Installer.CLI --mode=repair --media=E:\media            # what is wrong
          Installer.CLI --mode=repair --media=E:\media --apply    # fix it; data untouched
        """;
}
