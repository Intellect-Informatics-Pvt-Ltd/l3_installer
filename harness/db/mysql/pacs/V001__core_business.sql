-- =========================================================================
-- V001__core_business.sql
-- PACS core business tables.
-- =========================================================================

CREATE TABLE IF NOT EXISTS voucher (
    voucher_id        BIGINT        PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)   NOT NULL,
    voucher_no        VARCHAR(50)   NOT NULL,             -- monotonic per pacs_id
    voucher_date      DATE          NOT NULL,
    voucher_type      VARCHAR(50)   NOT NULL,             -- 'CR','DB','JV','PV','RV'
    narration         VARCHAR(500)  NULL,
    total_amount      DECIMAL(18,2) NOT NULL,
    status            VARCHAR(30)   NOT NULL,             -- 'DRAFT','POSTED','DELETED'
    is_deleted        TINYINT(1)    NOT NULL DEFAULT 0,
    created_by        VARCHAR(100)  NOT NULL,
    created_at        DATETIME(6)   NOT NULL,
    updated_at        DATETIME(6)   NULL,
    correlation_id    VARCHAR(64)   NOT NULL,
    UNIQUE KEY uq_pacs_voucher_no (pacs_id, voucher_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS voucher_line (
    voucher_line_id   BIGINT        PRIMARY KEY AUTO_INCREMENT,
    voucher_id        BIGINT        NOT NULL,
    account_code      VARCHAR(50)   NOT NULL,
    debit_amount      DECIMAL(18,2) NOT NULL DEFAULT 0,
    credit_amount     DECIMAL(18,2) NOT NULL DEFAULT 0,
    line_narration    VARCHAR(500)  NULL,
    FOREIGN KEY (voucher_id) REFERENCES voucher(voucher_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Matches docs/deletionsenerio.md "fa_voucherdeletionmain" pattern.
CREATE TABLE IF NOT EXISTS voucher_deletion_audit (
    deletion_id       BIGINT        PRIMARY KEY AUTO_INCREMENT,
    voucher_id        BIGINT        NOT NULL,
    pacs_id           VARCHAR(50)   NOT NULL,
    voucher_no        VARCHAR(50)   NOT NULL,
    deleted_by        VARCHAR(100)  NOT NULL,
    deleted_at        DATETIME(6)   NOT NULL,
    reason            VARCHAR(500)  NULL,
    before_state_json LONGTEXT      NOT NULL,             -- mandatory; NEG-007
    correlation_id    VARCHAR(64)   NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS loan_application (
    loan_app_id       BIGINT        PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)   NOT NULL,
    loan_app_no       VARCHAR(50)   NOT NULL,
    member_no         VARCHAR(50)   NOT NULL,
    member_name       VARCHAR(200)  NOT NULL,             -- PII: redact in logs
    requested_amount  DECIMAL(18,2) NOT NULL,
    approved_amount   DECIMAL(18,2) NULL,
    purpose           VARCHAR(500)  NULL,
    status            VARCHAR(30)   NOT NULL,             -- 'DRAFT','SUBMITTED','APPROVED','REJECTED','DISBURSED','AMENDED','CANCELLED'
    is_deleted        TINYINT(1)    NOT NULL DEFAULT 0,
    maker             VARCHAR(100)  NOT NULL,
    checker           VARCHAR(100)  NULL,
    created_at        DATETIME(6)   NOT NULL,
    updated_at        DATETIME(6)   NULL,
    correlation_id    VARCHAR(64)   NOT NULL,
    UNIQUE KEY uq_pacs_loan_app_no (pacs_id, loan_app_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS loan_amendment_history (
    amendment_id      BIGINT        PRIMARY KEY AUTO_INCREMENT,
    loan_app_id       BIGINT        NOT NULL,
    amended_at        DATETIME(6)   NOT NULL,
    amended_by        VARCHAR(100)  NOT NULL,
    approver          VARCHAR(100)  NOT NULL,             -- NEG-009: mandatory
    reason            VARCHAR(1000) NOT NULL,             -- NEG-009: mandatory
    before_state_json LONGTEXT      NOT NULL,
    after_state_json  LONGTEXT      NOT NULL,
    correlation_id    VARCHAR(64)   NOT NULL,
    FOREIGN KEY (loan_app_id) REFERENCES loan_application(loan_app_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS file_sync_registry (
    file_id           BIGINT        PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)   NOT NULL,
    entity_type       VARCHAR(100)  NOT NULL,             -- 'voucher_attachment','loan_doc'
    entity_id         VARCHAR(100)  NOT NULL,
    file_name         VARCHAR(500)  NOT NULL,
    file_size_bytes   BIGINT        NOT NULL,
    file_sha256       CHAR(64)      NOT NULL,
    total_chunks      INT           NOT NULL,
    chunks_acked      INT           NOT NULL DEFAULT 0,
    priority          TINYINT       NOT NULL DEFAULT 100, -- lower = higher priority
    status            VARCHAR(30)   NOT NULL,             -- 'PENDING','UPLOADING','ACKED','FAILED'
    created_at        DATETIME(6)   NOT NULL,
    completed_at      DATETIME(6)   NULL,
    correlation_id    VARCHAR(64)   NOT NULL,
    UNIQUE KEY uq_pacs_file_hash (pacs_id, file_sha256)   -- NEG-020 dedup
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
