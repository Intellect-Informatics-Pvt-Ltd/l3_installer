using FluentAssertions;
using Installer.Core.Upgrade;
using SharedKernel.Contracts;

namespace Installer.UnitTests;

/// <summary>
/// Upgrade path validation — the gate that decides whether a jump is one the release was built
/// and tested for.
///
/// Pure, so it is worth covering exhaustively: it is the cheapest place to stop an upgrade, and
/// every case it lets through is one that will be found out on a state's production data.
/// </summary>
public sealed class UpgradePathTests
{
    private static UpgradePathValidation Validate(string from, string to, CompatibilityInfo? compat = null)
    {
        return UpgradePath.Validate(from, to, compat);
    }

    [Fact]
    public void A_forward_step_is_allowed()
    {
        Validate("3.2.0", "3.3.0").Valid.Should().BeTrue();
    }

    [Fact]
    public void Reinstalling_the_same_version_is_refused()
    {
        var result = Validate("3.3.0", "3.3.0");

        result.Valid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already installed");
    }

    [Fact]
    public void A_downgrade_is_refused_and_says_what_to_do_instead()
    {
        // Not a conservative default — there is genuinely no path. Old code would run against a
        // schema a newer migration has already changed, and migrations do not run backwards.
        var result = Validate("3.3.0", "3.2.0");

        result.Valid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Downgrade is not an upgrade path");
        result.ErrorMessage.Should().Contain("Restore a backup instead");
    }

    [Theory]
    [InlineData("not-a-version", "3.3.0")]
    [InlineData("3.2.0", "not-a-version")]
    public void An_unparseable_version_is_refused_rather_than_guessed(string from, string to)
    {
        Validate(from, to).Valid.Should().BeFalse();
    }

    // ── The window the release declares ──────────────────────────────────────

    private static CompatibilityInfo Window(string min, string max, bool sideBySide = false, bool breaking = false) =>
        new() { MinUpgradeFrom = min, MaxUpgradeFrom = max, RequiresSideBySide = sideBySide, BreakingSchemaChange = breaking };

    [Fact]
    public void Inside_the_declared_window_is_allowed()
    {
        Validate("3.2.5", "3.3.0", Window("3.2.0", "3.2.9")).Valid.Should().BeTrue();
    }

    [Theory]
    [InlineData("3.1.0")]  // below the window
    [InlineData("3.2.99")] // above it
    public void Outside_the_declared_window_is_refused_with_the_window_named(string installed)
    {
        var result = Validate(installed, "3.3.0", Window("3.2.0", "3.2.9"));

        result.Valid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("3.2.0").And.Contain("3.2.9");
        result.ErrorMessage.Should().Contain("has not been tested",
            "an operator needs to know this is an untested path, not a broken installer");
    }

    [Fact]
    public void The_window_boundaries_are_inclusive()
    {
        Validate("3.2.0", "3.3.0", Window("3.2.0", "3.2.9")).Valid.Should().BeTrue();
        Validate("3.2.9", "3.3.0", Window("3.2.0", "3.2.9")).Valid.Should().BeTrue();
    }

    [Fact]
    public void Flags_from_the_manifest_are_carried_through()
    {
        var result = Validate("3.2.0", "3.3.0", Window("3.2.0", "3.2.9", sideBySide: true, breaking: true));

        result.RequiresSideBySide.Should().BeTrue();
        result.HasBreakingSchemaChange.Should().BeTrue();
    }

    [Fact]
    public void A_downgrade_is_refused_even_when_the_window_would_allow_it()
    {
        // Order matters: the window check must not be reached for a backwards jump, because a
        // sloppily-written window could otherwise admit one.
        var result = Validate("3.3.0", "3.2.0", Window("0.0.0", "9.9.9"));

        result.Valid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Downgrade is not an upgrade path");
    }

    [Fact]
    public void With_no_declared_window_a_forward_step_is_allowed()
    {
        // An older manifest, or one that does not constrain. Forward movement still has to be
        // forward, which the earlier checks already enforce.
        Validate("3.2.0", "3.3.0", null).Valid.Should().BeTrue();
    }
}
