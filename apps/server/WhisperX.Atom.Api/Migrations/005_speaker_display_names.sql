-- Repair fallback speaker labels that were persisted with a damaged code page.
-- Only technical labels containing replacement/question marks are touched;
-- user-supplied display names remain unchanged.
UPDATE meeting_speakers
SET display_name = 'Спикер ' || substring(stable_key FROM 9)
WHERE stable_key ~ '^SPEAKER_[0-9]+$'
  AND display_name LIKE '%?%';
