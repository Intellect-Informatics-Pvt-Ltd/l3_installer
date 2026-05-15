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
