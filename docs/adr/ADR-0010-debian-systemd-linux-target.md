# ADR-0010: Debian and systemd as the Linux target

**Status:** Accepted
**Date:** 2026-08-29
**Deciders:** Programme (distro), Architecture (packaging)
**Relates to:** ADR-0004 (Kestrel self-hosted), ADR-0009 (framework-dependent payload)

---

## Context

`docs/offline-installer-assessment-and-plan.md` §11 closed the runtime-target question as *build
for both, ship Linux first*. That left one thing open: **which Linux**.

The repositories pointed at Debian without ever saying so. Every module Dockerfile is built on
`mcr.microsoft.com/dotnet/*`, which is Debian-based. `ops/ansible` has exactly one OS branch and
it is a no-op — `'mysql-server' if os_family == 'Debian' else 'mysql-server'` — distro-agnostic
by accident rather than by design. Nothing pinned RHEL and nothing pinned Debian either.

**Confirmed by the programme on 2026-08-29: Debian.**

## Decision

**The Linux target is Debian-family, service-managed by systemd, packaged as `.deb`.**

Three consequences follow, and they are the reason this ADR exists rather than a line in a
runbook:

### 1. systemd, not a bespoke service model

`ops/ansible/roles/deployapp` already generates systemd units for all 26 services, and that
template is not naive — it carries the tier ordering, the `ASPNETCORE_ENVIRONMENT` state
selection, `Restart=on-failure` with a burst limit, `LimitNOFILE=65535` sized against the
connection-pool arithmetic, and a deliberately modest hardening profile (`ProtectSystem=full`
rather than `strict`, because these services legitimately write report files).

**The installer's Linux service orchestrator must produce units consistent with that template.**
Two different unit shapes for the same 26 services — one from Ansible online, one from the
installer offline — is how a node behaves differently from the estate in a way nobody can see.
This is the same rule §5h of the platform README states for the schema: one authority.

### 2. `.deb`, and what it is actually for

The `.deb` is **not** how the ERP is installed. The ERP arrives as a verified payload inside the
medium, exactly as on Windows — that is the whole point of the bundling premise. The `.deb`
packages **the installer itself**, so a node can be given the tool through the normal mechanism
of its operating system, and so `dpkg -l` can answer "what installed this".

That keeps the two targets symmetric: a signed EXE on Windows and a signed `.deb` on Debian, each
carrying the same manifest-verified payload set.

### 3. The offline package mirror is `apt`, not `yum`

A PACS node has no internet. Anything the base OS needs — the .NET runtime's native
dependencies, `libicu`, MySQL's runtime libraries — must come from a **local apt repository** on
the medium, or be vendored. This is the piece of offline work that is genuinely distro-specific,
and it is now unambiguous.

## Consequences

### Positive

- The Linux adapter has a concrete target instead of an abstract one, and can be tested on the
  same CI runner the build already uses.
- systemd units, `.deb` packaging and an apt mirror are all well-trodden; none needs invention.
- Consistency with `ops/ansible` means an offline node and an online one differ in *delivery*,
  not in *runtime shape* — so a support engineer reads the same `systemctl status` either way.

### Negative

- A RHEL site would need `.rpm` and a yum mirror. The *content* is unchanged and the packaging
  is mechanical, but it is real work nobody should assume is free.
- Debian's MySQL packaging differs from Oracle's `.deb` bundles; the bundled-MySQL premise
  (ADR-0009, and the whole reason this framework exists) means we ship our own binaries rather
  than `apt install mysql-server`, so the estate's Ansible path and the installer's path diverge
  here on purpose. That divergence should be documented where the DBA will find it.

### Risks

- **Two unit-file authorities.** If the installer's systemd generator and
  `ops/ansible/roles/deployapp/templates/l2r2-service.service.j2` drift, an offline node restarts
  differently from an online one under load. Mitigation: the installer's generator is
  contract-tested against the properties that template carries, and the long-term fix is the
  single generated topology already recommended as W2.

## Alternatives considered

| | Why not |
|---|---|
| RHEL / `.rpm` | Nothing in the estate points at it; the programme confirmed Debian |
| Distro-agnostic tarball + install script | Loses `dpkg -l`, loses dependency declaration, and reinvents what the OS already does |
| Install the ERP itself via `.deb` packages per service | Breaks the bundling premise: the payload must be verified against our signed manifest, not against a distro's package database. Also 26 packages to version in lockstep |
| Containers on the node (Podman) | Another runtime to bundle and administer on a machine nobody administers. ADR-0007 rejected Docker for the same reason |

## References

- `ops/ansible/roles/deployapp/templates/l2r2-service.service.j2` — the shape to be consistent with
- `docs/offline-installer-assessment-and-plan.md` §11 (L2-R2 workspace)
- `src/Installer.Actions/Install/SystemdServiceOrchestrator.cs`
