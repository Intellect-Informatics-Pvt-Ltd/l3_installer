-- V005__file_received.sql (NLDR)
CREATE TABLE IF NOT EXISTS file_received (
    file_id           BIGINT        PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)   NOT NULL,
    entity_type       VARCHAR(100)  NOT NULL,
    entity_id         VARCHAR(100)  NOT NULL,
    file_name         VARCHAR(500)  NOT NULL,
    file_sha256       CHAR(64)      NOT NULL,
    chunks_received   INT           NOT NULL DEFAULT 0,
    total_chunks      INT           NOT NULL,
    status            VARCHAR(30)   NOT NULL,    -- 'ASSEMBLING','COMPLETED','REJECTED'
    received_at       DATETIME(6)   NOT NULL,
    UNIQUE KEY uq_pacs_file_hash (pacs_id, file_sha256)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
