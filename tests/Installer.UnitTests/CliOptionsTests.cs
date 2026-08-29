using FluentAssertions;
using Installer.CLI;
using SharedKernel.Contracts;

namespace Installer.UnitTests;

/// <summary>
/// Argument parsing, tested without running an installer.
///
/// Two properties matter more than the rest. An unrecognised argument must be an ERROR — a
/// silently dropped <c>--apply</c> means an operator believes they installed something and did
/// not. And the destructive combination must be caught at the prompt, not three phases in.
/// </summary>
public sealed class CliOptionsTests
{
    [Fact]
    public void Dry_run_is_the_default()
    {
        // The single most important default in this program. The safe invocation is the short one.
        var o = CliOptions.Parse(["--config=x.epcfg"]);

        o.Apply.Should().BeFalse();
        o.ParseError.Should().BeNull();
    }

    [Theory]
    [InlineData("--apply")]
    [InlineData("/apply")]
    public void Apply_arms_the_run_in_either_syntax(string arg)
    {
        CliOptions.Parse([arg]).Apply.Should().BeTrue();
    }

    [Theory]
    [InlineData("--config=D:\\site.epcfg")]
    [InlineData("/config:D:\\site.epcfg")]
    [InlineData("--config=\"D:\\site.epcfg\"")]
    public void Accepts_windows_and_posix_argument_syntax(string arg)
    {
        // The installer is driven by hand from a Windows console at a PACS site and by scripts
        // from a rollout tool. Neither audience should have to learn the other's convention.
        CliOptions.Parse([arg]).ConfigPath.Should().Be(@"D:\site.epcfg");
    }

    [Theory]
    [InlineData("install", InstallerMode.Install)]
    [InlineData("UNINSTALL", InstallerMode.Uninstall)]
    [InlineData("Upgrade", InstallerMode.Upgrade)]
    public void Parses_mode_case_insensitively(string text, InstallerMode expected)
    {
        CliOptions.Parse([$"--mode={text}"]).Mode.Should().Be(expected);
    }

    [Fact]
    public void Omitting_mode_means_auto_detect()
    {
        CliOptions.Parse([]).Mode.Should().BeNull();
    }

    [Fact]
    public void An_unknown_mode_is_an_error_that_lists_the_valid_ones()
    {
        var o = CliOptions.Parse(["--mode=reinstall"]);

        o.ParseError.Should().Contain("unknown mode 'reinstall'").And.Contain("install");
    }

    [Fact]
    public void An_unrecognised_argument_is_an_error_and_never_ignored()
    {
        // A typo'd --apply that parses as "no flags given" is an operator who believes the
        // install happened. Refusing to run is the only safe reading.
        CliOptions.Parse(["--aply"]).ParseError.Should().Contain("--aply");
    }

    [Fact]
    public void A_flag_that_needs_a_value_and_has_none_is_an_error()
    {
        CliOptions.Parse(["--config"]).ParseError.Should().NotBeNull();
    }

    [Fact]
    public void Purge_data_outside_uninstall_is_refused()
    {
        CliOptions.Parse(["--mode=install", "--purge-data"]).ParseError.Should().Contain("uninstall");
    }

    [Fact]
    public void Purge_data_with_apply_demands_a_token_and_a_typed_confirmation()
    {
        var o = CliOptions.Parse(["--mode=uninstall", "--purge-data", "--apply"]);

        o.ParseError.Should().Contain("--override-token").And.Contain("--confirm");
    }

    [Fact]
    public void Purge_data_is_allowed_in_a_dry_run_without_a_token()
    {
        // Deliberate: an operator must be able to SEE what a purge would remove before being
        // asked to produce a governance token for it.
        var o = CliOptions.Parse(["--mode=uninstall", "--purge-data"]);

        o.ParseError.Should().BeNull();
        o.Apply.Should().BeFalse();
    }

    [Fact]
    public void A_fully_specified_purge_parses()
    {
        var o = CliOptions.Parse([
            "--mode=uninstall", "--purge-data", "--apply",
            "--override-token=eyJhbGciOi", "--confirm=PURGE AP-XYZ-0001"]);

        o.ParseError.Should().BeNull();
        o.PurgeData.Should().BeTrue();
        o.TypedConfirmation.Should().Be("PURGE AP-XYZ-0001");
    }

    [Fact]
    public void Quiet_and_verbose_contradict_each_other()
    {
        CliOptions.Parse(["--quiet", "--verbose"]).ParseError.Should().Contain("contradict");
    }

    [Fact]
    public void Help_short_circuits_validation()
    {
        // --help must work even alongside nonsense, or an operator who mistyped cannot find out how.
        var o = CliOptions.Parse(["--help", "--nonsense"]);

        o.ShowHelp.Should().BeTrue();
    }

    [Fact]
    public void Usage_documents_every_exit_code_the_program_can_return()
    {
        // Exit codes are an interface: the rollout tooling branches on them. If a code exists
        // and is undocumented, a script author invents their own meaning for it.
        foreach (var code in new[] { "0", "1", "2", "3", "4", "5", "64", "99", "130" })
        {
            CliOptions.Usage.Should().Contain(code);
        }
    }

    [Fact]
    public void Usage_leads_with_the_dry_run_default()
    {
        CliOptions.Usage.Should().Contain("DRY RUN IS THE DEFAULT");
    }
}
