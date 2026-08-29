# ADR-0007: Harness Native Windows Deployment via Offline Installer

**Status:** **Partially implemented** — see Implementation status, added 2026-08-29  
**Date:** 2026-05-15 · **Status revised:** 2026-08-29  
**Deciders:** Architecture team  
**Relates to:** Design Overview §14.3, §23.5, §29 (M12)

---

## Context

The sync test harness (`harness/`) currently runs in two modes:

1. **Docker Compose** — all 7 .NET services + infra containerised (development/CI).
2. **Local `dotnet run`** — infra in Docker, services run natively on the developer machine.

Neither mode supports the **pilot-site deployment** scenario described in the SAD v1.2 and the
design overview §14.3: a clean Windows 10/11 machine at a rural PACS site where the offline
installer deploys the harness as native Windows services for near-realtime simulation and CxO demos.

The offline installer already has the machinery to:
- Extract payloads from a signed archive (`PayloadExtractor`)
- Register and start Windows services from a `service-map.yaml` (`ServiceOrchestrator`)
- Generate `appsettings.Production.json` from `.epcfg` site config packs (`ConfigGenerator`)
- Resume after power-cut via checkpoint state machine (`InstallerStateMachine`)

We need to bridge the harness into this existing installer pipeline.

---

## Decision

### 1. Self-contained single-file publish (win-x64)

All publishable harness projects produce a single `.exe` when built in Release configuration.
This is configured centrally in `harness/Directory.Build.props`:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
  <PublishTrimmed>false</PublishTrimmed>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
</PropertyGroup>
```

**Why not trimmed?** The harness uses reflection-heavy libraries (Dapper, System.Text.Json
polymorphism, Confluent.Kafka) that are incompatible with aggressive trimming. The size penalty
(~80 MB per EXE vs ~30 MB trimmed) is acceptable for an offline installer delivered via USB.

**Why single-file?** Simplifies the service-map — each service is one EXE with no adjacent DLLs
to manage. The installer's `PayloadExtractor` drops them into `${BinaryRoot}\harness\` and
`ServiceOrchestrator` registers them directly.

### 2. Two payload ZIPs (PACS-side + NLDR-side)

The harness is split into two installer payloads:

| Payload | Contents | When installed |
|---------|----------|----------------|
| `harness-pacs-win-x64.zip` | `Pacs.Fas.Api.exe`, `Pacs.Loans.Api.exe`, `Pacs.SyncWorker.exe`, `Pacs.OperatorUi.exe` + config | Always (every PACS node) |
| `harness-nldr-win-x64.zip` | `Nldr.Api.exe`, `Nldr.SyncWorker.exe`, `Nldr.DashboardUi.exe` + config | Only with `--demo` flag |

This matches the design overview §23.5: "The installer never packages the NLDR-side projects on
the PACS node. The pilot site only runs PACS-side processes; the central NLDR mock lives at the
demo-central VM."

### 3. Service map with dependency ordering

A dedicated `harness/packaging/service-map.yaml` defines all 7 harness services with:
- `start_order` values 110–170 (after infra services at 10–70)
- `stop_order` in reverse
- `group` field (`pacs` or `nldr`) for conditional installation
- `dependencies` array for the installer to validate before starting
- Health checks (HTTP `/health/ready`) with timeouts
- Recovery actions (restart with escalating delays, support bundle on repeated failure)

The installer's `ServiceOrchestrator` already supports this shape — no code changes needed.

### 4. Configuration generation from `.epcfg`

When running under the installer, the `ConfigGenerator` produces:

```json
{
  "Pacs": { "PacsId": "<from .epcfg>", "DataRoot": "D:\\ePACSData" },
  "Harness": { "TestMode": false, "Profile": "Installer" },
  "ConnectionStrings": { "PacsDb": "<generated>", "PacsRedis": "<generated>" },
  "Sync": { "Outbox": { "PollIntervalMs": 500 } }
}
```

This is dropped at `${DataRoot}\config\harness\appsettings.Production.json` and each harness
EXE picks it up via the standard `AddHarnessConfiguration()` host builder extension.

### 5. Demo mode (`--demo` flag)

When the installer CLI receives `--demo`:
- Both payload ZIPs are extracted
- All 7 services (PACS + NLDR) are registered
- A second MySQL schema (`epacs_nldr`) is created on the same MySQL instance (port 3307)
- A second Redis instance is not needed — NLDR uses key-prefix isolation on the same Redis
- `Harness:TestMode = true` is set (enables fault injection for live demos)

---

## Consequences

### Positive
- Zero runtime dependency on the target machine (no .NET SDK, no Docker)
- Leverages existing installer infrastructure (no new deployment tooling)
- Single USB stick delivers everything: infra + harness + config
- Power-cut resilient (installer state machine handles interrupted installs)
- Matches the production deployment model (Windows services, health checks, recovery)

### Negative
- Each harness EXE is ~80 MB (total PACS payload ~320 MB, NLDR ~240 MB) — acceptable for USB
- Single-file extraction adds ~2s startup latency on first run (one-time self-extract to temp)
- Trimming disabled means larger attack surface — mitigated by Authenticode signing + localhost binding

### Risks
- If a harness project adds a native dependency that conflicts with single-file bundling,
  we fall back to `PublishSingleFile=false` and ship adjacent DLLs (minor service-map change)
- The `--demo` flag sharing one MySQL instance for both PACS and NLDR schemas may hit
  connection pool limits under load — monitor during M12 acceptance testing

---

## Alternatives Considered

| Alternative | Why rejected |
|---|---|
| Docker on pilot sites | No Docker Desktop on offline Windows machines; licensing concerns |
| Framework-dependent publish | Requires the .NET runtime pre-installed (net10.0 since 2026-08-29) — violates offline-first |
| Trimmed publish | Dapper + Confluent.Kafka + System.Text.Json polymorphism break under trimming |
| Single monolithic EXE (all services in one process) | Violates module isolation; can't restart individual services; doesn't match production topology |
| NSSM (Non-Sucking Service Manager) wrapper | Extra dependency; `sc.exe` is sufficient and already implemented |

---

## References

- `docs/test-harness/00-design-overview.md` §14.3, §23.5, §29
- `harness/Directory.Build.props` — publish configuration
- `harness/packaging/service-map.yaml` — service definitions
- `harness/packaging/installer-manifest-stub.yaml` — CI payload manifest
- `src/Installer.Actions/Install/ServiceOrchestrator.cs` — existing service registration
- `src/Installer.Actions/Install/PayloadExtractor.cs` — existing payload extraction

---

## Implementation status (audited 2026-08-29)

| Decision | Status | Evidence |
|---|---|---|
| §1 Self-contained single-file `win-x64` publish | **Built, verified** | Produces a 53 MB single-file `.exe` on .NET 10 (this ADR estimated ~80 MB on .NET 8 — compression improved). **Amended 2026-08-29:** `RuntimeIdentifier` is no longer set in `harness/Directory.Build.props`; it is passed by the publish step. The blanket `Configuration == Release` condition made every Release *build* win-x64, including test projects, which inherit the RID through their `ProjectReference` to `Harness.Common` — so `dotnet test -c Release` aborted on any non-Windows host with *"Could not find 'dotnet' host for the 'X64' architecture"*. A RID belongs to `dotnet publish`, not `dotnet build`. `SelfContained`, `PublishSingleFile` and compression stay in the props file, since they are publish-only and have no build effect. |
| §2 Two payload ZIPs (PACS / NLDR) | **Now built in CI** *(2026-08-29)* | `.github/workflows/ci.yml` runs `publish-win-x64.ps1 -CreateZip` on a Windows runner, uploads both ZIPs as artefacts, and prints their sizes each run. Still open: `harness/packaging/installer-manifest-stub.yaml` carries `sha256: PLACEHOLDER_COMPUTED_BY_CI` and nothing yet writes the real hashes back — that belongs with the media pipeline (tasks.md W6). |
| §3 Service map with dependency ordering | **Built** | `harness/packaging/service-map.yaml` exists and `HarnessServiceMapLoader` parses it |
| §4 Configuration generation from `.epcfg` | **Not reachable** | `HarnessConfigGenerator` is implemented, but nothing calls it: `Installer.CLI` never loads the `.epcfg` and there is no composition root (tasks.md F1) |
| §5 Demo mode (`--demo`) | **Not built** | The flag is parsed and printed. No second schema is created, no NLDR payload is extracted, no service is registered. |

The claim *"The installer's `ServiceOrchestrator` already supports this shape — no code changes
needed"* is correct as far as it goes, but incomplete: the orchestrator consumes a
`IReadOnlyList<ServiceMapEntry>`, and **the only producer of that list is
`HarnessServiceMapLoader`**. The canonical `samples/service-map.yaml` has no loader at all.
See tasks.md gap **F2**.

## Standing caution about scope

This ADR is written as though the harness is a deployable. It is not, and should not become
one. `harness/src/Pacs.Fas.Api` is a 435-line stand-in for `l3_FAS`; `harness/src/Pacs.Loans.Api`
is nine lines. They exist to exercise the chassis before the real stack is pointed at it — which
is a sound way to build a framework — but installing them onto a node that also runs the real
ePACS would put a simulated FAS and a real FAS on the same machine.

**Recommendation:** keep §1 and §3 (they are how any payload gets packaged and described).
Re-scope §2 and §5 from "install the harness at pilot sites" to "install the harness in a lab,
under an explicitly separate mode that refuses to run where a real installation is detected."
