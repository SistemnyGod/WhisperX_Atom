"""Apply the production SQL migration set to an explicitly isolated test DB.

This helper mirrors the server migration runner's ordering, checksum and
transaction behaviour.  It is intentionally opt-in through
``WHISPERX_TEST_DATABASE`` so local commands cannot modify a LAN runtime.
"""

from __future__ import annotations

import hashlib
import os
from pathlib import Path

import psycopg


def main() -> None:
    conninfo = os.getenv("WHISPERX_TEST_DATABASE")
    if not conninfo:
        raise RuntimeError("WHISPERX_TEST_DATABASE_REQUIRED")

    root = Path(__file__).resolve().parents[1]
    migrations = sorted((root / "apps/server/WhisperX.Atom.Api/Migrations").glob("*.sql"))
    if not migrations:
        raise RuntimeError("TEST_MIGRATIONS_MISSING")

    with psycopg.connect(conninfo, autocommit=False) as connection:
        with connection.cursor() as cursor:
            cursor.execute(
                "CREATE TABLE IF NOT EXISTS schema_migrations("
                "version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())"
            )
            cursor.execute(
                "CREATE TABLE IF NOT EXISTS schema_migration_checksums("
                "version text PRIMARY KEY REFERENCES schema_migrations(version) ON DELETE CASCADE, "
                "sha256 text NOT NULL, recorded_at timestamptz NOT NULL DEFAULT now())"
            )
        connection.commit()

        for migration in migrations:
            version = migration.stem
            payload = migration.read_bytes()
            checksum = hashlib.sha256(payload).hexdigest()
            with connection.transaction():
                with connection.cursor() as cursor:
                    cursor.execute("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=%s)", (version,))
                    already_applied = cursor.fetchone()[0]
                    if already_applied:
                        cursor.execute("SELECT sha256 FROM schema_migration_checksums WHERE version=%s", (version,))
                        recorded = cursor.fetchone()
                        if recorded is not None and recorded[0] != checksum:
                            raise RuntimeError(f"TEST_MIGRATION_CHECKSUM_MISMATCH:{version}")
                        if recorded is None:
                            cursor.execute(
                                "INSERT INTO schema_migration_checksums(version,sha256) VALUES(%s,%s) "
                                "ON CONFLICT(version) DO NOTHING",
                                (version, checksum),
                            )
                        continue
                    cursor.execute(payload.decode("utf-8"))
                    cursor.execute("INSERT INTO schema_migrations(version) VALUES(%s)", (version,))
                    cursor.execute(
                        "INSERT INTO schema_migration_checksums(version,sha256) VALUES(%s,%s)",
                        (version, checksum),
                    )

    print(f"TEST_MIGRATIONS_APPLIED={len(migrations)}")


if __name__ == "__main__":
    main()
