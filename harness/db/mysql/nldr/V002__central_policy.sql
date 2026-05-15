-- V002__central_policy.sql (NLDR)
CREATE TABLE IF NOT EXISTS central_policy (
    policy_id         BIGINT        PRIMARY KEY AUTO_INCREMENT,
    policy_code       VARCHAR(100)  NOT NULL UNIQUE,
    policy_name       VARCHAR(200)  NOT NULL,
    payload_json      LONGTEXT      NOT NULL,
    effective_from    DATETIME(6)   NOT NULL,
    created_at        DATETIME(6)   NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
