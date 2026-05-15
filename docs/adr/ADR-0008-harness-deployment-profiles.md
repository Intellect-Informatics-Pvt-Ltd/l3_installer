# ADR-0008: Harness Deployment Profiles

**Status:** Accepted  
**Date:** 2026-05-15  
**Deciders:** Architecture team  
**Relates to:** ADR-0007, Design Overview §14

---

## Context

The harness must run in multiple environments with different infrastructure configurations:
- Developer laptop (Docker infra, `dotnet run` services)
- CI pipeline (Testcontainers, ephemeral)
- Pilot PACS site (native Windows services, single machine)
- Demo lab (two machines: PACS + NLDR)
- Two-laptop field demo (one laptop per side)

Each environment has different connection strings, ports, feature flags, and service topology.

## Decision

Use a **profile-based configuration overlay** system with four named profiles:

| Profile | `Harness:Profile` | TestMode | Infra | Services |
|---|---|---|---|---|
| `Default` | `"Default"` | `true` | Docker Compose | `dotnet run` or Docker |
| `Installer` | `"Installer"` | `false` | Native Windows services | Native EXEs |
| `MultiPacs` | `"MultiPacs"` | `true` | Docker Compose | Multiple PACS instances |
| `TwoLaptop` | `"TwoLaptop"` | `true` | Split across machines | Native or Docker |

Configuration loading order (later wins):
1. `appsettings.json` — compiled defaults
2. `appsettings.{Environment}.json` — ASP.NET environment (Development/Production)
3. `appsettings.Profile.{Harness:Profile}.json` — profile overlay
4. `.epcfg` site config pack — installer-generated overrides
5. Environment variables — final override

## Profile Behaviors

### Default (development)
- `TestMode = true` — all `/api/test/*` routes active
- Docker ports: MySQL 3307/3308, Redis 6380/6381, Kafka 9092
- Services bind to `localhost:5101–5401`

### Installer (pilot/production)
- `TestMode = false` — test routes return 404
- Single MySQL instance (port 3306), single Redis (port 6379, DB isolation by prefix)
- Services registered as Windows services via `service-map.yaml`
- Logs to `D:\ePACSData\logs\harness\`
- NLDR URL points to central server (not localhost)

### MultiPacs (testing)
- `TestMode = true`
- Multiple PACS IDs configured (`PACS-AP-0001`, `PACS-AP-0002`)
- Separate schema prefixes per PACS
- Tests SEQ-009, SEQ-010 (cross-PACS sequence isolation)

### TwoLaptop (field demo)
- `TestMode = true`
- PACS laptop: runs PACS services, MySQL_PACS, Redis_PACS, Kafka
- NLDR laptop: runs NLDR services, MySQL_NLDR, Redis_NLDR
- Kafka shared (PACS laptop hosts it; NLDR connects over LAN)
- Network partition simulated by unplugging Ethernet cable

## Consequences

- Profile selection is a single config value — no code branching
- The `AddHarnessConfiguration()` extension method handles the overlay chain
- CI uses `Default` profile with Testcontainers (no profile file needed)
- The installer's `ConfigGenerator` produces the `Installer` profile overlay

## References

- `harness/samples/appsettings.Installer.json` — sample installer profile
- `harness/src/Harness.Common/HostBuilderExtensions.cs` — configuration loading
- `docs/test-harness/00-design-overview.md` §14
