-- Durable, idempotent lineage for the recorder-to-transcript pipeline.
-- One row belongs to one server recording session.  Imported files do not
-- manufacture a recording_session_id and therefore remain outside this table.
CREATE TABLE IF NOT EXISTS recording_pipeline_runs(
  recording_session_id uuid PRIMARY KEY REFERENCES recording_sessions(id) ON DELETE CASCADE,
  meeting_id uuid NOT NULL REFERENCES meetings(id),
  media_asset_id uuid REFERENCES media_assets(id),
  asr_job_id uuid REFERENCES jobs(id),
  transcript_v1_id uuid REFERENCES transcripts(id),
  enrichment_job_id uuid REFERENCES jobs(id),
  transcript_v2_id uuid REFERENCES transcripts(id),
  summary_job_id uuid REFERENCES jobs(id),
  summary_id uuid REFERENCES summaries(id),
  pipeline_correlation_id text,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_recording_pipeline_runs_correlation
  ON recording_pipeline_runs(pipeline_correlation_id)
  WHERE pipeline_correlation_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_recording_pipeline_runs_meeting
  ON recording_pipeline_runs(meeting_id, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_recording_pipeline_runs_asr_job
  ON recording_pipeline_runs(asr_job_id);
CREATE INDEX IF NOT EXISTS ix_recording_pipeline_runs_enrichment_job
  ON recording_pipeline_runs(enrichment_job_id);
CREATE INDEX IF NOT EXISTS ix_recording_pipeline_runs_transcript_v1
  ON recording_pipeline_runs(transcript_v1_id);
CREATE INDEX IF NOT EXISTS ix_recording_pipeline_runs_transcript_v2
  ON recording_pipeline_runs(transcript_v2_id);
CREATE INDEX IF NOT EXISTS ix_recording_pipeline_runs_summary_job
  ON recording_pipeline_runs(summary_job_id);

-- Backfill sessions created before this migration.  Every lateral lookup is
-- bounded to one deterministic row, so applying the migration is safe on a
-- large history and cannot manufacture duplicate lineage records.
INSERT INTO recording_pipeline_runs(
  recording_session_id,meeting_id,media_asset_id,asr_job_id,transcript_v1_id,
  enrichment_job_id,transcript_v2_id,summary_job_id,summary_id,pipeline_correlation_id)
SELECT rs.id,rs.meeting_id,a.id,aj.id,v1.id,ej.id,v2.id,sj.id,s.id,rs.pipeline_correlation_id
FROM recording_sessions rs
LEFT JOIN media_assets a
  ON a.storage_key='/data/recordings/' || replace(rs.id::text,'-','')
 AND a.source_type='recorder_session'
LEFT JOIN LATERAL (
  SELECT j.id FROM jobs j WHERE j.media_asset_id=a.id AND j.type IN ('TRANSCRIBE_ASR','TRANSCRIBE') ORDER BY j.created_at LIMIT 1
) aj ON true
LEFT JOIN LATERAL (
  SELECT t.id FROM transcripts t WHERE t.meeting_id=rs.meeting_id AND t.version_kind='ASR_DRAFT'
    AND t.quality_metadata->>'processing_job_id'=aj.id::text ORDER BY t.version LIMIT 1
) v1 ON true
LEFT JOIN LATERAL (
  SELECT j.id FROM jobs j WHERE j.input_transcript_id=v1.id AND j.type='TRANSCRIPT_ENRICH' ORDER BY j.created_at LIMIT 1
) ej ON true
LEFT JOIN LATERAL (
  SELECT t.id FROM transcripts t WHERE t.meeting_id=rs.meeting_id AND t.version_kind='ENRICHED'
    AND (t.source_transcript_id=v1.id OR t.quality_metadata->>'processing_job_id'=ej.id::text) ORDER BY t.version LIMIT 1
) v2 ON true
LEFT JOIN LATERAL (
  SELECT j.id FROM jobs j WHERE j.input_transcript_id=v2.id AND j.type='SUMMARIZE' ORDER BY j.created_at LIMIT 1
) sj ON true
LEFT JOIN summaries s ON s.job_id=sj.id
ON CONFLICT(recording_session_id) DO NOTHING;
