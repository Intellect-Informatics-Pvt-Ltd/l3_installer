-- V008__sync_inbox.sql (NLDR)
-- The inbox dedupe table on the NLDR side. Nldr.Api's ingest pipeline writes it at step 10
-- (NldrIngestService -> Harness.Common SyncInboxStore.TryInsertAsync), and the design asserts on it
-- ("NLDR has 1 business row + 2 DUPLICATE inbox rows", docs/test-harness/00-design-overview.md,
-- demo-duplicate-replay) - but no NLDR migration created it, so every ingest failed with
-- "Table 'epacs_nldr.sync_inbox' doesn't exist". Found 2026-10-02 by the first run of
-- Harness.IntegrationTests. The shape is COPIED from pacs/V002__sync_tables.sql, because the same
-- SyncInboxStore code writes both; it must not drift from that definition.
CREATE TABLE IF NOT EXISTS sync_inbox (
    inbox_id          BIGINT        PRIMARY KEY AUTO_INCREMENT,
    source_system     VARCHAR(50)   NOT NULL,             -- 'PACS' on this side
    event_id          CHAR(36)      NOT NULL,
    sequence_no       BIGINT        NULL,
    payload_hash      CHAR(64)      NOT NULL,
    idempotency_key   VARCHAR(200)  NOT NULL,
    status            ENUM('RECEIVED','APPLIED','DUPLICATE','REJECTED') NOT NULL,
    reject_reason     VARCHAR(500)  NULL,
    received_at       DATETIME(6)   NOT NULL,
    applied_at        DATETIME(6)   NULL,
    correlation_id    VARCHAR(64)   NOT NULL,
    UNIQUE KEY uq_inbox_event (event_id)              -- enforces I-3
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
