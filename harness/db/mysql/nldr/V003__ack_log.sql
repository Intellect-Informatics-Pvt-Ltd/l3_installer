-- V003__ack_log.sql (NLDR)
CREATE TABLE IF NOT EXISTS ack_log (
    ack_id            BIGINT        PRIMARY KEY AUTO_INCREMENT,
    event_id          CHAR(36)      NOT NULL,
    pacs_id           VARCHAR(50)   NOT NULL,
    sequence_no       BIGINT        NOT NULL,
    ack_status        ENUM('ACK','NACK') NOT NULL,
    nack_reason       VARCHAR(500)  NULL,
    acked_at          DATETIME(6)   NOT NULL,
    correlation_id    VARCHAR(64)   NOT NULL,
    KEY ix_pacs_seq (pacs_id, sequence_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- NLDR outbox for ACKs/NACKs/commands to be published by Nldr.SyncWorker.
CREATE TABLE IF NOT EXISTS nldr_outbox (
    outbox_id         BIGINT        PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)   NOT NULL,
    event_id          CHAR(36)      NOT NULL,
    event_type        VARCHAR(100)  NOT NULL,    -- 'nldr.ack','nldr.nack','nldr.command'
    topic             VARCHAR(255)  NOT NULL,
    payload_json      LONGTEXT      NOT NULL,
    status            ENUM('PENDING','PUBLISHED','FAILED') NOT NULL DEFAULT 'PENDING',
    created_at        DATETIME(6)   NOT NULL,
    published_at      DATETIME(6)   NULL,
    correlation_id    VARCHAR(64)   NOT NULL,
    KEY ix_status_created (status, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
