-- =========================================================================
-- V004__seed.sql
-- Seed data: sync_sequence base rows and lookup data.
-- =========================================================================

-- Sequence rows for both PACS profiles.
-- SequenceAllocator reads and increments these inside the business transaction.
INSERT IGNORE INTO sync_sequence (pacs_id, stream_name, next_sequence, updated_at)
VALUES
    ('PACS-AP-0001', 'pacs.outbound',  1, NOW(6)),
    ('PACS-AP-0001', 'pacs.heartbeat', 1, NOW(6)),
    ('PACS-AP-0002', 'pacs.outbound',  1, NOW(6)),
    ('PACS-AP-0002', 'pacs.heartbeat', 1, NOW(6));

-- Seed initial checkpoints (both acked and received sequences start at 0).
INSERT IGNORE INTO sync_checkpoints
    (pacs_id, stream_name, last_acked_sequence, last_received_sequence, updated_at)
VALUES
    ('PACS-AP-0001', 'pacs.outbound',  0, 0, NOW(6)),
    ('PACS-AP-0001', 'nldr.commands',  0, 0, NOW(6)),
    ('PACS-AP-0002', 'pacs.outbound',  0, 0, NOW(6)),
    ('PACS-AP-0002', 'nldr.commands',  0, 0, NOW(6));
