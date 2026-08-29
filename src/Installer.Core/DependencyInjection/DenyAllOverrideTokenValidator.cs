using Installer.Actions.Uninstall;

namespace Installer.Core.DependencyInjection;

/// <summary>
/// The override-token validator registered until a real one exists.
///
/// It refuses everything, deliberately.
///
/// WHY A DENY-ALL AND NOT A STUB THAT PASSES. The token authorises destroying a PACS node's
/// business data — <c>UninstallAction</c> deletes <c>DataRoot</c> when it validates. A permissive
/// placeholder would make an irreversible operation available before anyone had written the
/// check that is supposed to gate it, and the failure would be silent and total. A deny-all
/// makes the missing implementation visible the first time somebody tries to use it, which is
/// the only safe direction for this particular gap to fail in.
///
/// Replace with real JWT validation (signature, audience, expiry, bound pacs_id, bound action)
/// as tasks.md §9.5. Deleting this class without providing a replacement will break the DI
/// graph at startup rather than quietly allowing purges — that is intended.
/// </summary>
internal sealed class DenyAllOverrideTokenValidator : IOverrideTokenValidator
{
    public Task<OverrideTokenResult> ValidateAsync(
        string token, string requiredAction, CancellationToken cancellationToken = default) =>
        Task.FromResult(OverrideTokenResult.Failure(
            "Override token validation is not implemented in this build (tasks.md 9.5), so data purge is refused. " +
            "Uninstall without --purge-data preserves business data and works normally."));
}
