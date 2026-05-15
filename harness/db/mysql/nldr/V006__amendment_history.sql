-- V006__amendment_history.sql (NLDR)
CREATE TABLE IF NOT EXISTS nldr_amendment_history (
    amendment_id      BIGINT        PRIMARY KEY AUTO_INCREMENT,
    loan_app_id       BIGINT        NOT NULL,
    amended_at        DATETIME(6)   NOT NULL,
    amended_by        VARCHAR(100)  NOT NULL,
    approver          VARCHAR(100)  NOT NULL,
    reason            VARCHAR(1000) NOT NULL,
    before_state_json LONGTEXT      NOT NULL,
    after_state_json  LONGTEXT      NOT NULL,
    source_event_id   CHAR(36)      NOT NULL,
    correlation_id    VARCHAR(64)   NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
