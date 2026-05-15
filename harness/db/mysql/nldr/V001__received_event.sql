-- =========================================================================
-- V001__received_event.sql  (NLDR schema)
-- =========================================================================

CREATE TABLE IF NOT EXISTS received_event (
    received_id       BIGINT        PRIMARY KEY AUTO_INCREMENT,
    event_id          CHAR(36)      NOT NULL,
    pacs_id           VARCHAR(50)   NOT NULL,
    sequence_no       BIGINT        NOT NULL,
    change_type       ENUM('INSERT','UPDATE','DELETE','AMENDMENT') NOT NULL,
    entity_type       VARCHAR(100)  NOT NULL,
    entity_id         VARCHAR(100)  NOT NULL,
    payload_json      LONGTEXT      NULL,
    before_state_json LONGTEXT      NULL,
    payload_hash      CHAR(64)      NOT NULL,
    received_at       DATETIME(6)   NOT NULL,
    apply_status      ENUM('APPLIED','DUPLICATE','REJECTED','GAP_WAITING') NOT NULL,
    reject_reason     VARCHAR(500)  NULL,
    correlation_id    VARCHAR(64)   NOT NULL,
    UNIQUE KEY uq_event (event_id),
    UNIQUE KEY uq_pacs_seq (pacs_id, sequence_no)      -- catches duplicate sequence (NEG-010)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Business tables on NLDR side (soft-delete only, never physical delete).
CREATE TABLE IF NOT EXISTS nldr_business_voucher (
    voucher_id        BIGINT        PRIMARY KEY,
    pacs_id           VARCHAR(50)   NOT NULL,
    voucher_no        VARCHAR(50)   NOT NULL,
    voucher_date      DATE          NOT NULL,
    voucher_type      VARCHAR(50)   NOT NULL,
    total_amount      DECIMAL(18,2) NOT NULL,
    is_deleted        TINYINT(1)    NOT NULL DEFAULT 0,
    deleted_at        DATETIME(6)   NULL,
    deletion_reason   VARCHAR(500)  NULL,
    deletion_correlation_id VARCHAR(64) NULL,
    entity_state_version BIGINT     NOT NULL DEFAULT 0, -- for conflict detection (§19.1)
    UNIQUE KEY uq_pacs_voucher_no (pacs_id, voucher_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS nldr_business_loan (
    loan_app_id       BIGINT        PRIMARY KEY,
    pacs_id           VARCHAR(50)   NOT NULL,
    loan_app_no       VARCHAR(50)   NOT NULL,
    member_no         VARCHAR(50)   NOT NULL,
    member_name       VARCHAR(200)  NOT NULL,
    requested_amount  DECIMAL(18,2) NOT NULL,
    approved_amount   DECIMAL(18,2) NULL,
    status            VARCHAR(30)   NOT NULL,
    is_deleted        TINYINT(1)    NOT NULL DEFAULT 0,
    entity_state_version BIGINT     NOT NULL DEFAULT 0,
    UNIQUE KEY uq_pacs_loan_app_no (pacs_id, loan_app_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Gap tracking
CREATE TABLE IF NOT EXISTS sequence_gap (
    gap_id            BIGINT        PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)   NOT NULL,
    missing_sequence  BIGINT        NOT NULL,
    detected_at       DATETIME(6)   NOT NULL,
    resolved_at       DATETIME(6)   NULL,
    UNIQUE KEY uq_pacs_seq (pacs_id, missing_sequence)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
