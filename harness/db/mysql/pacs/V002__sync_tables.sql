-- =========================================================================
-- V002__sync_tables.sql
-- PACS transactional sync tables.
-- =========================================================================

CREATE TABLE IF NOT EXISTS sync_sequence (
    pacs_id        VARCHAR(50) NOT NULL,
    stream_name    VARCHAR(50) NOT NULL,               -- 'pacs.outbound','pacs.heartbeat'
    next_sequence  BIGINT      NOT NULL,
    updated_at     DATETIME(6) NOT NULL,
    PRIMARY KEY (pacs_id, stream_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS sync_outbox (
    outbox_id         BIGINT        PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)   NOT NULL,
    sequence_no       BIGINT        NOT NULL,
    event_id          CHAR(36)      NOT NULL,
    idempotency_key   VARCHAR(200)  NOT NULL,
    change_type       ENUM('INSERT','UPDATE','DELETE','AMENDMENT') NOT NULL,
    entity_type       VARCHAR(100)  NOT NULL,
    entity_id         VARCHAR(100)  NOT NULL,
    topic             VARCHAR(255)  NOT NULL,
    schema_version    VARCHAR(20)   NOT NULL DEFAULT 'v1',
    payload_json      LONGTEXT      NULL,
    before_state_json LONGTEXT      NULL,             -- mandatory for DELETE/AMENDMENT
    payload_hash      CHAR(64)      NOT NULL,
    priority          TINYINT       NOT NULL DEFAULT 100,
    status            ENUM('PENDING','IN_FLIGHT','ACKED','FAILED','DEADLETTER') NOT NULL DEFAULT 'PENDING',
    retry_count       INT           NOT NULL DEFAULT 0,
    last_error        VARCHAR(2000) NULL,
    created_at        DATETIME(6)   NOT NULL,
    sent_at           DATETIME(6)   NULL,
    ack_at            DATETIME(6)   NULL,
    correlation_id    VARCHAR(64)   NOT NULL,
    causation_id      VARCHAR(64)   NULL,
    UNIQUE KEY uq_event (event_id),
    UNIQUE KEY uq_pacs_seq (pacs_id, sequence_no),   -- enforces I-4
    KEY ix_status_priority_created (status, priority, created_at),
    KEY ix_correlation (correlation_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS sync_inbox (
    inbox_id          BIGINT        PRIMARY KEY AUTO_INCREMENT,
    source_system     VARCHAR(50)   NOT NULL,             -- 'NLDR'
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

CREATE TABLE IF NOT EXISTS sync_checkpoints (
    checkpoint_id          BIGINT      PRIMARY KEY AUTO_INCREMENT,
    pacs_id                VARCHAR(50) NOT NULL,
    stream_name            VARCHAR(50) NOT NULL,
    last_acked_sequence    BIGINT      NOT NULL,
    last_received_sequence BIGINT      NOT NULL,
    updated_at             DATETIME(6) NOT NULL,
    UNIQUE KEY uq_checkpoint (pacs_id, stream_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS conflict_log (
    conflict_id       BIGINT        PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)   NOT NULL,
    entity_type       VARCHAR(100)  NOT NULL,
    entity_id         VARCHAR(100)  NOT NULL,
    local_state_json  LONGTEXT      NOT NULL,
    remote_state_json LONGTEXT      NOT NULL,
    detected_at       DATETIME(6)   NOT NULL,
    resolution        VARCHAR(30)   NULL,              -- 'LOCAL','REMOTE','MANUAL','PENDING'
    resolved_at       DATETIME(6)   NULL,
    resolved_by       VARCHAR(100)  NULL,
    correlation_id    VARCHAR(64)   NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
