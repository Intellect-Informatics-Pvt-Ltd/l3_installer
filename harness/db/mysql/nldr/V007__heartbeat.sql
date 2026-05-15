-- V007__heartbeat.sql (NLDR)
CREATE TABLE IF NOT EXISTS heartbeat (
    heartbeat_id      BIGINT        PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)   NOT NULL,
    received_at       DATETIME(6)   NOT NULL,
    payload_json      LONGTEXT      NOT NULL,    -- outbox depth, last seq, build version
    KEY ix_pacs_received (pacs_id, received_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
