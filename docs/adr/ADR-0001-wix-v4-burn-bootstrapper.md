# ADR-0001: WiX v4 Burn as Installer Framework

**Status:** **Proposed** — reverted from Accepted on 2026-08-29  
**Date:** 2025-11-01 · **Status revised:** 2026-08-29  
**Deciders:** Architecture team

---

## Context

The ePACS offline installer needs a framework to orchestrate installation of multiple payloads
(MySQL, Garnet, Kafka/JRE, .NET services) as a single signed EXE on Windows 10/11 machines
with no internet access.

## Decision

Use **WiX v4 Burn** with a C# Managed BootstrapperApplication (WPF UI).

## Rationale

- WiX Burn natively supports chained payload installation with dependency ordering
- Managed BA allows full C# control over the install flow (state machine, prechecks)
- Authenticode signing of the outer bundle EXE is straightforward
- WiX v4 is actively maintained and supports .NET 8 tooling *(the repo moved to .NET 10 on 2026-08-29; confirm WiX v4/v5 tooling support for net10.0 before accepting this ADR)*
- No runtime dependency on the target machine (the bundle is self-extracting)

## Alternatives Considered

| Alternative | Why rejected |
|---|---|
| Inno Setup | No native .NET integration; Pascal scripting is limiting |
| NSIS | Same limitations as Inno; poor Windows service management |
| MSIX | Requires Store or sideloading policy; doesn't support raw service registration |
| Custom .NET console app only | No standard uninstall/repair UX; no Add/Remove Programs entry |

## Consequences

- Requires WiX v4 toolset in CI pipeline
- Bundle.wxs must declare all payloads and their install conditions
- The Managed BA (WPF) adds ~5 MB to the bundle size

---

## Implementation status (audited 2026-08-29)

**Nothing described in this ADR has been built.** The repository contains:

- no `.wxs` file (no `Bundle.wxs`)
- no `.wixproj`
- no `packaging/wix/` directory
- no Managed BootstrapperApplication, WPF or otherwise
- no WiX toolset step in `.github/workflows/build.yml`

The status is therefore reverted to **Proposed**. This decision has been *recorded*, not *taken*:
nothing depends on it yet and it can still be revisited at no cost.

### What this ADR is silently load-bearing for

`SignatureVerifier.VerifyAuthenticode()` returns
`Failure("Authenticode verification not yet implemented. Will be handled by WiX Burn engine.")`
on **every** call, including on Windows. The comment above it says the check is deferred "to the
WiX Burn engine which handles this natively". Since the Burn engine does not exist, **there is
today no Authenticode verification anywhere in the product.** Detached CMS verification of the
release manifest (`VerifyDetachedSignature`) is real and does work — that is the mechanism
currently providing tamper-evidence, and it is worth knowing that it is the only one.

### Before accepting this ADR, settle

1. **Is a bootstrapper still the right shape?** The chassis in `src/` already does payload
   verification, extraction, service registration, ordered start and rollback in C#. What Burn
   adds over `Installer.CLI` is the Add/Remove Programs entry, the chained prerequisite install
   (VC++ redist) and native Authenticode on the outer EXE. That is a real but narrow list.
2. **Bundle size.** The payload set in `samples/release-manifest.yaml` totals ~1.2 GB before the
   application. With the real L2-R2 stack it is closer to 3 GB. Confirm Burn's behaviour at that
   size on the target hardware before committing.
3. **Whether the framework targets Windows at all.** See the runtime-target question raised in
   `docs/offline-installer-assessment-and-plan.md` §4/D2 in the L2-R2 workspace. If the answer
   is not Windows, this ADR is superseded rather than accepted.
