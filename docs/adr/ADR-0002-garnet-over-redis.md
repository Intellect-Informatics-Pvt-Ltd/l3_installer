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

---

## Implementation status (audited 2026-08-29)

**Status 2026-08-29: contested.** `Components:Cache:Provider` now defaults to `redis`, matching what L2-R2 runs, and `samples/service-map.yaml` carries an `⚠ UNRESOLVED` note where it still names `GarnetServer.exe`. The reasoning in this ADR remains sound — there is no official Redis build for Windows — but the substitution is not free: `l3_ERPClient/Security/RateLimiting/RedisRateLimitStore.cs` uses `LoadedLuaScript` (SCRIPT LOAD / EVALSHA) for rate limiting, which is a security control on Garnet's weakest compatibility surface. Resolve by running `utils-caching`, `RedisCaching.Tests` and ERPClient's rate-limiter tests against Garnet and recording the result here.

**Not otherwise realised.** Garnet appears in the repository only as a service definition in
`samples/service-map.yaml` and a directory-name comment in `DataRootInitializer.cs:62`. No
Garnet binary is packaged, and no code connects to it.

**Open point for the real payload.** Every L2-R2 module that caches uses `StackExchange.Redis`
(`utils-caching`). Garnet is wire-compatible with the Redis protocol, so this is *probably*
fine — but "probably" is not a decision. Before packaging Garnet, run the estate's own caching
tests (`utils-caching`, `RedisCaching.Tests`) against a Garnet instance and record the result
here. The things that historically break wire-compatible substitutes are Lua scripting,
keyspace notifications, and `SCAN` semantics; check those specifically.
