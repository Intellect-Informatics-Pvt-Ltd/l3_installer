# ADR-0004: Kestrel Self-Hosted (No IIS)

**Status:** Accepted  
**Date:** 2025-11-01  
**Deciders:** Architecture team

---

## Context

ePACS web services need an HTTP server that runs as a Windows service without IIS,
supports health endpoints, and can bind to localhost-only.

## Decision

Use **ASP.NET Core Kestrel** self-hosted, registered as Windows services via `sc.exe`.

## Rationale

- No IIS dependency (simpler install, fewer moving parts)
- Native Windows service hosting via `UseWindowsService()`
- Localhost-only binding (`--urls http://127.0.0.1:PORT`) for security
- Built-in health check middleware
- Self-contained publish means no .NET runtime dependency on target

## Alternatives Considered

| Alternative | Why rejected |
|---|---|
| IIS + ASP.NET Core Module | Extra IIS installation step; more attack surface |
| HTTP.sys | Requires URL reservation (netsh); more complex for localhost-only |
| nginx reverse proxy | Extra process; no native Windows service support |

## Consequences

- Each service binds to a unique port (configured via service-map.yaml)
- Firewall rules restrict ports to localhost (installer applies these)
- No HTTPS between internal services (localhost trust); HTTPS only for external-facing endpoints
