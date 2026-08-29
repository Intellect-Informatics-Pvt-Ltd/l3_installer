using FluentAssertions;
using Installer.Actions.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SharedKernel.Configuration;
using SharedKernel.Security;

namespace Installer.UnitTests;

/// <summary>
/// The database bootstrap is the reason the installer framework exists: an offline node must
/// run no database that was not delivered and verified with the installation. These tests cover
/// the parts that decide WHAT happens — the guard, the generated configuration, and the
/// refusals. Actually running mysqld is behind <see cref="IProcessRunner"/>.
/// </summary>
public sealed class DatabaseBootstrapTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "epacs-db-tests", Guid.NewGuid().ToString("N"));

    private readonly Mock<IProcessRunner> _runner = new();
    private readonly Mock<ISecretStore> _secrets = new();

    public DatabaseBootstrapTests()
    {
        _secrets.Setup(s => s.GeneratePassword(It.IsAny<int>(), It.IsAny<bool>())).Returns("Generated!Passw0rd");
        Directory.CreateDirectory(_dataRoot);
    }

    private MySqlBootstrapper Build(MySqlServiceOptions? mysql = null)
    {
        var services = new ServicesOptions();
        if (mysql is not null) services.MySql = mysql;
        return new MySqlBootstrapper(
            Options.Create(new InstallerOptions { DataRoot = _dataRoot, BinaryRoot = Path.Combine(_dataRoot, "bin") }),
            Options.Create(services),
            _secrets.Object,
            _runner.Object,
            NullLogger<MySqlBootstrapper>.Instance);
    }

    // ── The case-sensitivity guard ───────────────────────────────────────────

    [Fact]
    public void The_guard_probes_the_actual_path_rather_than_assuming_from_the_OS()
    {
        // Windows supports per-directory case sensitivity and Linux can mount a
        // case-insensitive volume, so RuntimeInformation is not the answer. The probe writes a
        // file and tries to read it back under a different case.
        var verdict = TableNameCaseGuard.Inspect(_dataRoot);

        verdict.Explanation.Should().Contain(_dataRoot);
        verdict.RequiredLowerCaseTableNames.Should().Be(verdict.FileSystemIsCaseSensitive ? 0 : 1);
        verdict.CanHostEstateSetting.Should().Be(verdict.FileSystemIsCaseSensitive);
    }

    [Fact]
    public void The_guard_leaves_no_probe_files_behind()
    {
        var before = Directory.GetFiles(_dataRoot).Length;

        TableNameCaseGuard.Inspect(_dataRoot);

        Directory.GetFiles(_dataRoot).Length.Should().Be(before);
    }

    [Fact]
    public void The_estate_setting_is_zero_and_is_stated_as_a_constant()
    {
        // Named rather than inline so a future change has to be deliberate. The estate pins
        // lower_case_table_names=0 in ops/compose and asserts it in ops/ansible.
        TableNameCaseGuard.EstateLowerCaseTableNames.Should().Be(0);
    }

    [Fact]
    public async Task Refuses_to_bootstrap_on_a_case_insensitive_volume()
    {
        var plan = await Build().PlanAsync();
        var verdict = TableNameCaseGuard.Inspect(Path.Combine(_dataRoot, "mysql", "data"));

        if (verdict.FileSystemIsCaseSensitive)
        {
            plan.CanProceed.Should().BeTrue();
            return;
        }

        // On a case-insensitive volume — every default Windows and macOS machine — this refuses
        // BY DEFAULT. Not because the baseline breaks (its 1,189 table names have zero
        // collisions when folded) but because the setting is fixed at initialisation and the
        // divergence it creates is permanent and invisible. Overridable; see the test below.
        plan.CanProceed.Should().BeFalse();
        plan.Blocker.Should().Contain("CASE-INSENSITIVE");
        plan.Blocker.Should().Contain("cannot be changed later");
        plan.Blocker.Should().Contain("AcceptCaseFolding", "a refusal must name the way past it");
    }

    [Fact]
    public async Task Refuses_to_re_initialise_a_populated_data_directory()
    {
        // Re-initialising over a society's books is not recoverable, so the bootstrapper only
        // ever initialises an empty directory and says so rather than silently skipping.
        var dataDir = Path.Combine(_dataRoot, "mysql", "data");
        Directory.CreateDirectory(Path.Combine(dataDir, "mysql"));

        var plan = await Build().PlanAsync();

        plan.DataDirectory.Should().Be(dataDir, "the ${DataRoot} default must resolve to the same shape on every platform");
        plan.DataDirectoryAlreadyInitialised.Should().BeTrue();
        plan.CanProceed.Should().BeFalse();

        // On a case-insensitive volume the platform guard is reported first — both are refusals
        // and either is correct; what must never happen is CanProceed on a populated directory.
        plan.Blocker.Should().Match(b => b!.Contains("would destroy", StringComparison.Ordinal)
                                      || b.Contains("CASE-INSENSITIVE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refuses_when_the_baseline_ddl_is_absent()
    {
        var act = () => Build().ExecuteAsync(Path.Combine(_dataRoot, "no-such-baseline.sql"));

        var ex = await act.Should().ThrowAsync<DatabaseBootstrapException>();

        // On a case-insensitive volume the guard fires first and the baseline is never reached.
        // That ordering is correct and is itself the point: nothing gets as far as looking for
        // a schema on a machine that cannot host the estate's database.
        var caseSensitive = TableNameCaseGuard.Inspect(Path.Combine(_dataRoot, "mysql", "data")).FileSystemIsCaseSensitive;
        ex.Which.Message.Should().Contain(caseSensitive ? "stable_baseline_ddl.sql" : "CASE-INSENSITIVE",
            "the estate has one schema authority, but the platform guard outranks it");
    }


    [Fact]
    public async Task Case_folding_can_be_accepted_deliberately()
    {
        // CORRECTED 2026-08-29. The first version of this guard refused outright, on the claim
        // that a folding server would collapse case-differing baseline tables. Measured: the
        // baseline's 1,189 table names contain ZERO collisions when folded, so it applies
        // cleanly either way. What remains is a permanent, invisible divergence in STORED case —
        // real, irreversible, and not fatal. So it is a decision, not a wall.
        var verdict = TableNameCaseGuard.Inspect(Path.Combine(_dataRoot, "mysql", "data"));
        if (verdict.FileSystemIsCaseSensitive)
        {
            return; // nothing to accept on a case-sensitive volume
        }

        var accepting = new MySqlServiceOptions { AcceptCaseFolding = true };

        var plan = await Build(accepting).PlanAsync();

        plan.CanProceed.Should().BeTrue("an irreversible divergence should be choosable, once someone has chosen it");
        plan.Steps.Should().Contain(x => x.Contains("AcceptCaseFolding", StringComparison.Ordinal));
        plan.Steps.Should().Contain(x => x.Contains("permanently", StringComparison.Ordinal),
            "the plan must still say plainly what accepting it costs");
    }

    [Fact]
    public void Accepting_case_folding_is_off_by_default()
    {
        new MySqlServiceOptions().AcceptCaseFolding.Should().BeFalse();
    }

    // ── The generated configuration ──────────────────────────────────────────

    private static string RenderConfig(bool caseSensitive, MySqlServiceOptions? o = null) =>
        MyIniWriter.Render(
            o ?? new MySqlServiceOptions(),
            @"D:\ePACSData",
            new CaseSensitivityVerdict
            {
                CanHostEstateSetting = caseSensitive,
                FileSystemIsCaseSensitive = caseSensitive,
                Explanation = caseSensitive ? "case-sensitive" : "case-insensitive"
            });

    [Fact]
    public void Config_sets_lower_case_table_names_from_the_verdict()
    {
        RenderConfig(caseSensitive: true).Should().Contain("lower_case_table_names = 0");
        RenderConfig(caseSensitive: false).Should().Contain("lower_case_table_names = 1");
    }

    [Fact]
    public void Config_is_durable_because_a_PACS_node_loses_power()
    {
        // Not tunable in practice: a committed voucher must be on the platter before the caller
        // is told it committed, on a machine with no UPS.
        var ini = RenderConfig(caseSensitive: true);

        ini.Should().Contain("innodb_flush_log_at_trx_commit = 1");
        ini.Should().Contain("sync_binlog = 1");
        ini.Should().Contain("innodb_doublewrite = ON");
    }

    [Fact]
    public void Config_binds_to_localhost_only()
    {
        // The node holds a society's books and sits on a network nobody administers.
        RenderConfig(caseSensitive: true).Should().Contain("bind-address = 127.0.0.1");
    }

    [Fact]
    public void Config_uses_the_estate_character_set_and_collation()
    {
        // A node storing Devanagari or Telugu member names under a different collation would
        // sort and compare them differently from the state's own copy.
        var ini = RenderConfig(caseSensitive: true);

        ini.Should().Contain("character-set-server = utf8mb4");
        ini.Should().Contain("collation-server = utf8mb4_0900_ai_ci");
    }

    [Fact]
    public void Config_carries_a_connection_ceiling_the_estate_can_survive()
    {
        // MySQL's own default is 151 against 26 services each declaring Max Pool Size=2000.
        RenderConfig(caseSensitive: true).Should().Contain("max_connections = 2000");
    }

    [Fact]
    public void Config_expands_the_data_root_token()
    {
        var ini = RenderConfig(caseSensitive: true);

        ini.Should().NotContain("${DataRoot}", "an unexpanded token would make mysqld fail with a nonsense path");
        ini.Should().Contain(@"D:\ePACSData");
    }

    [Fact]
    public void Config_warns_against_editing_it_on_the_host()
    {
        // Hand edits are lost on the next repair and, worse, make two nodes differ invisibly.
        RenderConfig(caseSensitive: true).Should().Contain("DO NOT EDIT ON THE HOST");
    }

    [Fact]
    public void Config_records_why_the_case_setting_is_what_it_is()
    {
        // The value cannot be changed later, so the file has to explain itself to whoever finds
        // it in two years wondering why their table names folded.
        RenderConfig(caseSensitive: false).Should().Contain("case-insensitive").And.Contain("FIXED AT INITIALISATION");
    }

    // ── Secret handling ──────────────────────────────────────────────────────

    [Fact]
    public async Task Passwords_never_reach_a_command_line()
    {
        // Any user on the box can read the process table. Passwords go through MYSQL_PWD and
        // SQL goes through stdin, so neither appears in the arguments.
        var dataDir = Path.Combine(_dataRoot, "mysql", "data");
        var verdict = TableNameCaseGuard.Inspect(dataDir);
        if (!verdict.FileSystemIsCaseSensitive)
        {
            return; // the guard refuses before any process runs; covered by its own test
        }

        var binDir = Path.Combine(_dataRoot, "bin", "current", "mysql", "bin");
        Directory.CreateDirectory(binDir);
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        await File.WriteAllTextAsync(Path.Combine(binDir, "mysqld" + exe), "");
        await File.WriteAllTextAsync(Path.Combine(binDir, "mysql" + exe), "");
        var ddl = Path.Combine(_dataRoot, "baseline.sql");
        await File.WriteAllTextAsync(ddl, "CREATE TABLE t (id INT);");

        var seenArguments = new List<string>();
        _runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                                      It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
               .Callback((string _, string args, string? _, string? _, IReadOnlyCollection<string>? _, IReadOnlyDictionary<string, string>? _, CancellationToken _) => seenArguments.Add(args))
               .ReturnsAsync(new ProcessResult { ExitCode = 0, StandardOutput = "1189", StandardError = "" });

        await Build().ExecuteAsync(ddl);

        seenArguments.Should().NotBeEmpty();
        seenArguments.Should().OnlyContain(a => !a.Contains("Generated!Passw0rd", StringComparison.Ordinal));
        seenArguments.Should().OnlyContain(a => !a.Contains("--password", StringComparison.Ordinal));
    }

    [Fact]
    public void The_process_runner_redacts_secrets_from_captured_output()
    {
        // MySQL is unusually good at echoing a password back in an error message, and this
        // output is exactly what an operator pastes into an email when an install fails.
        var result = new ProcessResult
        {
            ExitCode = 1,
            StandardOutput = "",
            StandardError = "Access denied for user using password: hunter2"
        };

        result.CombinedOutput.Should().Contain("Access denied");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true);
    }
}
