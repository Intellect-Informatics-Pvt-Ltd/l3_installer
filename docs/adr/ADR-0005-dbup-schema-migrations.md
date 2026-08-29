# ADR-0005: DbUp for Schema Migrations

**Status:** Accepted  
**Date:** 2025-11-01  
**Deciders:** Architecture team

---

## Context

MySQL schema changes must be applied reliably during install and upgrade, with:
- Idempotent execution (safe to re-run after power-cut)
- Version tracking (know which migrations have been applied)
- No external tooling dependency (runs from the installer process)

## Decision

Use **DbUp** as the migration runner, embedded in the installer and harness startup.

## Rationale

- Pure .NET library — no external CLI tool needed
- Tracks applied migrations in a `schemaversions` table
- Supports MySQL via `dbup-mysql` package
- Simple sequential execution model (V001, V002, …)
- Can run inside the installer's transaction/checkpoint flow

## Alternatives Considered

| Alternative | Why rejected |
|---|---|
| Flyway | Java dependency; external CLI |
| EF Core Migrations | Requires EF Core (we use Dapper); model-based approach doesn't fit |
| Liquibase | Java dependency; XML/YAML changelog format is heavyweight |
| Raw SQL scripts | No version tracking; no idempotency guarantee |

## Consequences

- Migration files follow `V{NNN}__{description}.sql` naming convention
- The installer runs migrations as part of the Install/Upgrade state machine phase
- Harness services run migrations at startup (development mode only)
- `Percona pt-online-schema-change` is used for large-table DDL in production (not DbUp)

---

## Implementation status (audited 2026-08-29)

**Not realised in the installer.** `DbUp` appears in `src/` only as a comment in
`IUpgradeEngine.cs:8`. There is no DbUp package reference in any installer project and no
migration runner. (`DbUp-MySql` is pinned in `harness/Directory.Packages.props` and used by
`Harness.IntegrationTests/Infrastructure/MigratorHelper.cs` for the harness's own toy schema —
that is a different thing.)

**This decision must be re-taken against the real payload.** L2-R2 does not use DbUp. Its
schema authority is a single generated file, `db/stable_baseline_ddl.sql` (1,189 tables,
apply-verified on MySQL 8.4 and 9.x), with state-level changes produced by
`build/generate-state-migration.py` and applied through `ops/l2r2 db`. Introducing DbUp as a
second migration corpus would give the estate two sources of schema truth, which is precisely
what the unified-baseline work removed.

**Recommended replacement decision:** the installer *imposes the baseline* on fresh install and
*delegates upgrade migrations to the estate's own generator*, rather than owning a migration
runner. Write that as ADR-0009 and supersede this one.
