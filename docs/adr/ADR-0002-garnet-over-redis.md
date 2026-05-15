# ADR-0002: Microsoft Garnet as Cache Layer

**Status:** Accepted  
**Date:** 2025-11-01  
**Deciders:** Architecture team

---

## Context

ePACS needs a Redis-compatible cache that runs as a native Windows service without Docker,
supports the StackExchange.Redis client, and has no licensing concerns for offline deployment.

## Decision

Use **Microsoft Garnet** as the cache layer.

## Rationale

- MIT-licensed, no Redis Ltd. licensing concerns
- Native Windows binary (no WSL/Docker required)
- Wire-compatible with StackExchange.Redis client
- Lower memory footprint than Redis on Windows (via Dragonfly or Memurai alternatives)
- Actively maintained by Microsoft Research

## Alternatives Considered

| Alternative | Why rejected |
|---|---|
| Redis (official) | No official Windows build; SSPL license concerns |
| Memurai | Commercial license required for production |
| Dragonfly | Linux-only; no native Windows support |
| In-process MemoryCache | No distributed cache semantics; no persistence |

## Consequences

- Must validate Garnet compatibility with all StackExchange.Redis features used
- Garnet config file (`garnet.conf`) must be templated by the installer
- Health check uses TCP probe (same as Redis)
