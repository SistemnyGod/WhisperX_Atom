-- Migration 030 owns the canonical Russian FTS expression index.  Migration
-- 040 accidentally introduced a duplicate with the same expression; remove
-- only that duplicate and keep the original index for rolling upgrades.
DROP INDEX IF EXISTS ix_transcript_segments_russian_fts;
