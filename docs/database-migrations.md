# Database migrations

Migration identity is the complete SQL filename stem, for example
`025_recording_bind_idempotency` or `025_recording_track_audio_format`.
The three-digit prefix is an ordering hint only. Two additive migrations may
therefore share a prefix; their full IDs remain unique and are applied in
`(prefix, id)` order.

The API migration runner stores every applied ID in `schema_migrations` and a
SHA-256 of its exact SQL in `schema_migration_checksums`. A migration that was
already applied is never executed again. If its SQL changes, startup fails
with `MIGRATION_CHECKSUM_MISMATCH:<id>` instead of silently changing an
existing schema.

Release verification (`scripts/verify-backend-deployment.ps1`) checks the same
rules before packaging: valid immutable IDs, unique complete stems, stable
ordering and non-empty SQL. Existing migrations are not renamed after they
may have reached a database; follow-up changes get a new full ID.

To inspect the applied history:

```sql
SELECT m.version, m.applied_at, c.sha256
FROM schema_migrations m
LEFT JOIN schema_migration_checksums c ON c.version = m.version
ORDER BY m.version;
```

