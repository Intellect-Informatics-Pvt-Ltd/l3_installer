# ADR-0003: Apache Kafka in KRaft Mode (Single Node)

**Status:** Accepted  
**Date:** 2025-11-01  
**Deciders:** Architecture team

---

## Context

ePACS requires a durable event transport for the transactional outbox pattern. The transport
must run offline on a single Windows machine without ZooKeeper.

## Decision

Use **Apache Kafka 3.7.x LTS in KRaft mode** (single-node, no ZooKeeper).

## Rationale

- KRaft eliminates ZooKeeper dependency (one fewer JVM process)
- Kafka provides durable, ordered, partitioned event streams
- The `Confluent.Kafka` .NET client is mature and well-supported
- Single-node KRaft is sufficient for a PACS site (no HA requirement at node level)
- 7-day retention + compaction covers the offline window

## Alternatives Considered

| Alternative | Why rejected |
|---|---|
| RabbitMQ | No native Windows service; Erlang dependency |
| NATS JetStream | Less mature .NET client; smaller ecosystem |
| SQLite-based queue | No partitioning; no consumer groups; reinventing the wheel |
| Azure Service Bus | Requires internet connectivity |

## Consequences

- Requires Eclipse Temurin JRE 17 as a payload (Kafka is JVM-based)
- Kafka startup adds ~10s to the service chain
- `kafka.properties` must be templated with data/log directories from config
- Topic auto-creation disabled; topics created by `KafkaTopicInitializer` at startup

---

## Implementation status (audited 2026-08-29)

**Status 2026-08-29: made conditional.** `Components:Eventing:Enabled` defaults to **false** and the service map tags Kafka `group: "eventing"`, so it is neither installed nor registered unless a site asks for it. The sizing argument below is why.

**Not otherwise realised.** Kafka appears only as a service definition in `samples/service-map.yaml` and
as a manifest payload entry. `Sync.Agent/Outbox/OutboxRelay.cs:36` — the one place that would
produce to it — is `// TODO: Actual MySQL query + Kafka publish implementation`.

**Sizing point for the real payload.** Kafka (110 MB) plus the JRE it needs (180 MB) is ~290 MB
of a USB medium, and both are marked `required: true` in `samples/release-manifest.yaml`. In
L2-R2, Kafka use is **flag-gated**: `FAS/ServiceRegistration.cs:286` reads
`Orchestration.Enabled && FasVoucherMessaging.Enabled`, and the orchestration flag is described
in that file as "the master Kafka kill-switch". A single-node offline PACS with orchestration
off does not need Kafka at all. Make the payload conditional on the flag rather than mandatory,
or justify carrying 290 MB to every site.
