-- V004__conflict_log.sql (NLDR)
CREATE TABLE IF NOT EXISTS conflict_log (
    conflict_id       BIGINT        PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)   NOT NULL,
    entity_type       VARCHAR(100)  NOT NULL,
    entity_id         VARCHAR(100)  NOT NULL,
    local_state_json  LONGTEXT      NOT NULL,
    remote_state_json LONGTEXT      NOT NULL,
    detected_at       DATETIME(6)   NOT NULL,
    resolution        VARCHAR(30)   NULL,
    resolved_at       DATETIME(6)   NULL,
    correlation_id    VARCHAR(64)   NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
