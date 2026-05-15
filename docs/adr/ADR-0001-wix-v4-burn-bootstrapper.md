# ADR-0001: WiX v4 Burn as Installer Framework

**Status:** Accepted  
**Date:** 2025-11-01  
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
- WiX v4 is actively maintained and supports .NET 8 tooling
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
