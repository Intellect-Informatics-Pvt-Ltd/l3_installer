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
