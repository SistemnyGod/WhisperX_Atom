ALTER TABLE meetings ADD COLUMN IF NOT EXISTS owner_id uuid REFERENCES users(id);
ALTER TABLE meetings ADD COLUMN IF NOT EXISTS room_id uuid;
ALTER TABLE meetings ADD COLUMN IF NOT EXISTS finished_at timestamptz;

CREATE TABLE IF NOT EXISTS rooms(
  id uuid PRIMARY KEY,
  name text NOT NULL UNIQUE,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE meetings ADD CONSTRAINT fk_meetings_room FOREIGN KEY (room_id) REFERENCES rooms(id);

CREATE TABLE IF NOT EXISTS recorder_agents(
  id uuid PRIMARY KEY,
  room_id uuid REFERENCES rooms(id),
  name text NOT NULL,
  enrollment_hash text NOT NULL UNIQUE,
  version text NOT NULL DEFAULT '0.1.0',
  status text NOT NULL DEFAULT 'OFFLINE',
  last_seen_at timestamptz,
  capabilities jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS agent_commands(
  id uuid PRIMARY KEY,
  agent_id uuid NOT NULL REFERENCES recorder_agents(id),
  command_type text NOT NULL,
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  status text NOT NULL DEFAULT 'PENDING',
  cursor bigint NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  delivered_at timestamptz,
  completed_at timestamptz,
  result jsonb
);
CREATE INDEX IF NOT EXISTS ix_agent_commands_cursor ON agent_commands(agent_id, cursor);

CREATE TABLE IF NOT EXISTS recording_sessions(
  id uuid PRIMARY KEY,
  meeting_id uuid NOT NULL REFERENCES meetings(id),
  agent_id uuid REFERENCES recorder_agents(id),
  state text NOT NULL DEFAULT 'CREATED',
  started_at timestamptz,
  finished_at timestamptz,
  total_samples bigint NOT NULL DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS recording_tracks(
  id uuid PRIMARY KEY,
  session_id uuid NOT NULL REFERENCES recording_sessions(id),
  track_type text NOT NULL,
  device_id text,
  sample_rate integer NOT NULL,
  channels integer NOT NULL,
  codec text NOT NULL DEFAULT 'flac',
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS recording_chunks(
  id uuid PRIMARY KEY,
  session_id uuid NOT NULL REFERENCES recording_sessions(id),
  track_id uuid NOT NULL REFERENCES recording_tracks(id),
  sequence integer NOT NULL,
  storage_key text NOT NULL,
  start_sample bigint NOT NULL,
  sample_count bigint NOT NULL,
  size_bytes bigint NOT NULL,
  sha256 text NOT NULL,
  status text NOT NULL DEFAULT 'READY',
  created_at timestamptz NOT NULL DEFAULT now(),
  confirmed_at timestamptz,
  UNIQUE(track_id, sequence),
  UNIQUE(track_id, sequence, sha256)
);

CREATE TABLE IF NOT EXISTS recording_events(
  id uuid PRIMARY KEY,
  session_id uuid NOT NULL REFERENCES recording_sessions(id),
  event_type text NOT NULL,
  media_time_ms bigint,
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS summaries(
  id uuid PRIMARY KEY,
  meeting_id uuid NOT NULL REFERENCES meetings(id),
  transcript_id uuid REFERENCES transcripts(id),
  version integer NOT NULL,
  status text NOT NULL DEFAULT 'DRAFT',
  model_name text NOT NULL,
  prompt_version text NOT NULL,
  source_hash text NOT NULL,
  content jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(meeting_id, version)
);

CREATE TABLE IF NOT EXISTS summary_evidence(
  id uuid PRIMARY KEY,
  summary_id uuid NOT NULL REFERENCES summaries(id) ON DELETE CASCADE,
  entity_type text NOT NULL,
  entity_key text NOT NULL,
  segment_id uuid NOT NULL REFERENCES transcript_segments(id),
  start_ms bigint NOT NULL,
  end_ms bigint NOT NULL
);

CREATE TABLE IF NOT EXISTS decisions(
  id uuid PRIMARY KEY,
  meeting_id uuid NOT NULL REFERENCES meetings(id),
  summary_id uuid REFERENCES summaries(id),
  text text NOT NULL,
  status text NOT NULL DEFAULT 'DRAFT',
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS action_items(
  id uuid PRIMARY KEY,
  meeting_id uuid NOT NULL REFERENCES meetings(id),
  summary_id uuid REFERENCES summaries(id),
  task text NOT NULL,
  responsible text,
  deadline timestamptz,
  status text NOT NULL DEFAULT 'NEEDS_REVIEW',
  evidence_segment_id uuid REFERENCES transcript_segments(id),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS assistant_queries(
  id uuid PRIMARY KEY,
  user_id uuid REFERENCES users(id),
  meeting_id uuid REFERENCES meetings(id),
  query text NOT NULL,
  answer text,
  evidence jsonb NOT NULL DEFAULT '[]'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now()
);
