# ADR-0002: Microsoft Garnet as Cache Layer

**Status:** **Accepted — verified by measurement 2026-08-29**  
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

## Verification (2026-08-29) — Garnet tested against Redis, and it passes

This ADR was previously marked "contested": Garnet was chosen because there is no official Redis
build for Windows, but nobody had checked it against what this estate actually asks of a cache —
and the sharpest question was ERPClient's rate limiter, which uses server-side Lua on a security
control. It has now been checked.

### Method

Two containers, side by side: `redis:7` (7.4.11) as the control and `ghcr.io/microsoft/garnet`
(2.1.5) as the candidate. Three probes, each run against both and diffed:

1. **Command surface** — every command the estate issues, enumerated from the 34 files that
   `using StackExchange.Redis`: `SET`/`SET EX`/`SET PX`, `GET`, `EXISTS`, `DEL`, `EXPIRE`,
   `PEXPIRE`, `TTL`, `PTTL`, `INCR`, `INCRBY`, `DECR`, `SETNX`, `MGET`, `TYPE`, `HSET`, `HGET`,
   `HGETALL`, `ZADD`, `ZCARD`, `ZRANGE ... WITHSCORES`, `ZREMRANGEBYSCORE`, `DBSIZE`.
2. **Lua** — the three scripts from `l3_ERPClient/Security/RateLimiting/LuaScripts.cs`, extracted
   verbatim, loaded with `SCRIPT LOAD` and executed with a fixed clock so both are deterministic:
   sliding window to its limit and past window expiry, concurrency acquire through both refusal
   codes, release through underflow. Plus `redis.status_reply`, `redis.error_reply`,
   `redis.pcall` error handling, `cjson`, and `SCRIPT EXISTS`.
3. **The real client** — a console app referencing **StackExchange.Redis 2.10.1**, the version the
   estate pins, doing exactly what `RedisRateLimitStore` does: `ConnectionMultiplexer.Connect`
   with `abortConnect=false`, `GetServer` / `IsConnected` / `IsReplica`,
   `LuaScript.Prepare(...).Load(server)`, then `db.ScriptEvaluateAsync(text, keys, values)`.
   This matters because a wire-compatible server usually fails at the multiplexer handshake, not
   at the commands.

### Result

**Zero differences.** Identical script SHA1s (`8bf7685b…`, `291ed2a4…`, `eb3e0d24…`), identical
return tuples including the string members (`OK`, `CONCURRENCY_LIMIT`), identical retry
milliseconds, identical residual state and TTLs. The `diff` of both probe outputs is empty.
StackExchange.Redis connects and reports `version=7.4.3` — Garnet advertises Redis 7.4
compatibility, which is why the handshake succeeds unchanged.

**Decision: Accepted.** Garnet is a valid substitute for the surface this estate uses.

### ⚠ The finding that matters operationally

**Garnet ships with Lua scripting DISABLED. It must be started with `--lua`.**

Without it, `SCRIPT LOAD` returns `ERR This instance has Lua scripting support disabled`.
`RedisRateLimitStore.LoadScripts()` catches that, logs once, and sets `_degradedFlag`. Every
subsequent call then returns `SlidingWindowResult.FailOpen` — and `FailOpen` is
`new SlidingWindowResult(true, 0, 0, true)`, i.e. **allowed**.

So a node running Garnet with default settings has **no rate limiting at all**, on a control
whose whole purpose is to be there under attack, and the only evidence is one log line at
startup. `samples/service-map.yaml` therefore carries `--lua` in the service arguments rather
than leaving it to a runbook step.

### What was NOT tested, and should be before a pilot

Stated so nobody reads this as a broader clearance than it is:

- Persistence and recovery (AOF / checkpointing) after a hard power cut — which is this
  product's normal case, not an edge case
- Behaviour under memory pressure and eviction
- Sustained load and latency against the estate's real key volumes
- Long-running stability
- The `abortConnect=false` circuit-breaker path against a *failing* Garnet, as opposed to an
  absent one

---

## Implementation status (audited 2026-08-29)

**Resolved 2026-08-29** — see the Verification section above. `Components:Cache:Provider` still
defaults to `redis` because that is what the online estate runs and a default should follow
reality; on a Windows node, where no official Redis build exists, Garnet is now a verified choice
rather than an assumed one.

**Not otherwise realised.** Garnet appears in the repository only as a service definition in
`samples/service-map.yaml` and a directory-name comment in `DataRootInitializer.cs:62`. No
Garnet binary is packaged, and no code connects to it.

**Open point for the real payload.** Every L2-R2 module that caches uses `StackExchange.Redis`
(`utils-caching`). Garnet is wire-compatible with the Redis protocol, so this is *probably*
fine — but "probably" is not a decision. Before packaging Garnet, run the estate's own caching
tests (`utils-caching`, `RedisCaching.Tests`) against a Garnet instance and record the result
here. The things that historically break wire-compatible substitutes are Lua scripting,
keyspace notifications, and `SCAN` semantics; check those specifically.
