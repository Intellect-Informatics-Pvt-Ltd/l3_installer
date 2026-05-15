-- =========================================================================
-- V003__orchestration_compat.sql
-- Compatibility tables/views for utils-orchestration NuGet (G-01).
-- The harness owns sync_outbox; we create a VIEW mapping it to the
-- canonical OutboxMessages shape so any platform tooling that reads
-- that table name still works.
-- =========================================================================

-- OutboxMessages VIEW (read-only) mapping sync_outbox → orchestration shape.
CREATE OR REPLACE VIEW OutboxMessages AS
SELECT
    outbox_id        AS message_id,
    event_id,
    CONCAT(entity_type, '.', change_type) AS event_type,
    topic,
    schema_version,
    payload_json,
    entity_id        AS message_key,
    correlation_id,
    causation_id,
    NULL             AS saga_id,
    idempotency_key,
    status,
    retry_count      AS attempt_count,
    last_error,
    created_at       AS created_at_utc,
    sent_at          AS published_at_utc,
    ack_at           AS updated_at_utc
FROM sync_outbox;

-- InboxMessages table used by utils-orchestration for dedupe.
-- sync_inbox is the harness-owned table; InboxMessages is the orchestration table.
-- Both are maintained in lock-step inside the handler transaction.
CREATE TABLE IF NOT EXISTS InboxMessages (
    event_id         CHAR(36)     NOT NULL,
    event_type       VARCHAR(255) NOT NULL,
    schema_version   VARCHAR(20)  NOT NULL DEFAULT 'v1',
    topic            VARCHAR(255) NOT NULL,
    partition_id     INT          NULL,
    offset_value     BIGINT       NULL,
    consumer_group   VARCHAR(200) NULL,
    correlation_id   VARCHAR(64)  NOT NULL,
    saga_id          VARCHAR(64)  NULL,
    idempotency_key  VARCHAR(200) NOT NULL,
    status           VARCHAR(30)  NOT NULL,
    received_at_utc  DATETIME(6)  NOT NULL,
    processed_at_utc DATETIME(6)  NULL,
    error            VARCHAR(2000) NULL,
    attempt_count    INT          NOT NULL DEFAULT 0,
    PRIMARY KEY (event_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- SagaInstances table (minimal) for multi-step amendment workflow.
CREATE TABLE IF NOT EXISTS SagaInstances (
    saga_id        CHAR(36)     NOT NULL,
    saga_type      VARCHAR(200) NOT NULL,
    current_state  VARCHAR(100) NOT NULL,
    payload_json   LONGTEXT     NULL,
    created_at     DATETIME(6)  NOT NULL,
    updated_at     DATETIME(6)  NOT NULL,
    PRIMARY KEY (saga_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
