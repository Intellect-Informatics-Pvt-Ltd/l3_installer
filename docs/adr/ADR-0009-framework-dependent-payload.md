# ADR-0009: Framework-dependent payload with one bundled runtime

**Status:** Accepted
**Date:** 2026-08-29
**Deciders:** Architecture team
**Supersedes:** ADR-0007 §1 (self-contained single-file publish)

---

## Context

ADR-0007 §1 chose **self-contained, single-file** publishing, and gave two reasons that were good
ones at the time:

> *"Simplifies the service-map — each service is one EXE with no adjacent DLLs to manage."*
> *"Zero runtime dependency on the target machine (no .NET SDK, no Docker)."*

Both were argued against the payload that existed then: the four PACS-side harness services. The
size note in that ADR — *"~80 MB per EXE … total PACS payload ~320 MB"* — is the arithmetic of a
four-service stand-in.

The real payload is **26 services** (25 middleware plus the `l3_ERPClient` UI, per
`ops/ansible/group_vars/all.yml`). That changes the arithmetic enough to change the decision.

## Measured

`l3_configurationsAPI`, published `win-x64` both ways on .NET 10:

| | Per service | × 26 |
|---|---|---|
| Self-contained | **159 MB** | **4.0 GB** |
| Framework-dependent | **54 MB** | **1.4 GB** + ~80 MB for one shared runtime |

A harness service, which carries fewer dependencies, shows the ratio even more starkly:
**111 MB → 12 MB**. The difference is almost entirely the duplicated runtime.

**A 2.6 GB difference**, because self-contained bundles the .NET runtime twenty-six times onto a
medium carried by hand to a village.

For context, the whole medium:

| | |
|---|---|
| Application, framework-dependent | 1.4 GB |
| MySQL + cache | 0.5 GB |
| Kafka + JRE (conditional, off by default — ADR-0003) | 0.3 GB |
| **Minimum viable medium** | **~1.8 GB** |
| Same, self-contained, with eventing | ~4.8 GB |

## Decision

**Publish the application payload framework-dependent, and bundle the .NET runtime once as its
own payload entry.**

The runtime becomes a manifest payload like MySQL or the JRE: verified by SHA-256, installed by
`PayloadExtractor`, and versioned in the release manifest. It is not a dependency on the target
machine — it still arrives on the medium — so ADR-0007's offline-first requirement is preserved
exactly. What changes is that it arrives once instead of twenty-six times.

**Test projects are excluded from any publish profile** (`harness/Directory.Build.targets`); a
test project is never a payload.

## Consequences

### Positive

- **2.6 GB smaller.** On removable media delivered by hand, that is the difference between a
  common USB stick and a large one, and it shortens every copy, hash and verify step.
- **Delta upgrades become plausible.** A patch to one service is ~54 MB rather than ~159 MB, and
  a patch that does not touch the runtime does not ship 80 MB of runtime. ADR-0007's model made
  `hotfix_base_version` close to pointless.
- **One runtime to patch.** A .NET security release is one payload to re-verify, not twenty-six
  binaries to rebuild. On air-gapped nodes patched by USB, that is the difference between a
  feasible security response and an infeasible one.
- **Faster publish and faster CI.**

### Negative

- **Adjacent DLLs return** — but less painfully than expected. Measured: `dotnet publish` still
  emits the native apphost (`Pacs.Fas.Api.exe`), so the service map's `executable` still points
  at an `.exe` and **needs no change at all**. What changes is that the directory beside it now
  holds the app's own DLLs instead of one 111 MB bundle. ADR-0007's "no adjacent DLLs to manage"
  is lost; its "each service is one EXE the orchestrator registers directly" is not.
- **Runtime/app version coupling.** A framework-dependent app requires a compatible runtime on
  the node. Both come from the same manifest, so the medium is internally consistent — but the
  manifest must now assert that pairing, and an upgrade that changes the runtime band has to ship
  both.
- **Roll-forward is pinned** to `LatestPatch` in `harness/Directory.Build.props`, and verified
  present in the generated `*.runtimeconfig.json`. The default would let a node silently run a
  newer runtime than anything this release was tested against — on a machine nobody administers
  and nobody will visit again soon.

### Risks

- A partial upgrade that replaces services without the runtime, or the reverse, produces a node
  that fails at startup. The upgrade engine (unimplemented, tasks.md §17) must treat the runtime
  as a payload with its own compatibility check.

## Alternatives considered

| | Why not |
|---|---|
| Keep self-contained (ADR-0007 §1) | 4.0 GB of application, 2.6 GB of it duplicated runtime, and no practical delta upgrade |
| Self-contained **trimmed** | ADR-0007 already rejected trimming, and the reason still holds: Dapper, `System.Text.Json` polymorphism and `Confluent.Kafka` are reflection-heavy. It would also have to be re-validated per service across 26 services |
| Install the .NET runtime from a Microsoft installer as a prerequisite | Needs internet, or another payload anyway — this is that payload, just verified by our own manifest |
| ReadyToRun / AOT | Orthogonal to this decision, and AOT is incompatible with the same reflection ADR-0007 cites |

## References

- `harness/Directory.Build.props`, `harness/Directory.Build.targets`
- `harness/scripts/publish-win-x64.ps1`
- `.github/workflows/ci.yml` — the publish job prints artefact sizes on every run, so growth is
  visible before it is a problem
- `docs/offline-installer-assessment-and-plan.md` §11.4 in the L2-R2 workspace
