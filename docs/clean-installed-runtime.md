# Clean installed runtime

## Supported production path

The supported Windows path is one runtime assembled from one clean commit:

```text
WinUI Desktop
  -> AudioGraph Recorder Host (current user, named pipe v6)
  -> SQLite spool / durable PCM / FLAC delivery
  -> Docker core (API, PostgreSQL, NATS, Media)
  -> Host GPU Worker (WhisperX large-v3)
  -> Voice Host (managed by Desktop)
```

Root `app.py`, `app/` and the legacy Python watcher remain in the repository
only for compatibility and regression tests. They are not copied into the
installer and must not be used as a production entry point.

## Identity contract

The release identity is generated once from the full Git commit:

```text
1.0.1+<40 hexadecimal commit characters>
```

Desktop, AudioGraph Recorder Host, Voice Host and Updater are published with
the same `WhisperXBuildIdentity`. The server bundle carries the same identity
in its manifest and in the OCI image labels. `dev`, an empty identity, and
`-dirty` are development states and cannot pass the clean-runtime gate.

`scripts/verify-clean-runtime.ps1` validates:

- artifact and installed component identities and SHA-256 values;
- the server bundle identity when a manifest is supplied;
- running process paths and identities when requested;
- absence of `app.py`, Python bytecode and a Python executable in the
  production payload;
- a safe JSON acceptance record without audio, credentials or transcript text.

## Build and install sequence

1. Keep the worktree clean and create one release commit.
2. Run `scripts/publish-desktop.ps1`; it refuses dirty or incomplete commits.
3. Run `scripts/build-server-bundle.ps1` from the same commit when a server
   bundle is required.
4. Run `scripts/verify-clean-runtime.ps1` against `artifacts/desktop` and the
   server manifest.
5. Compile `apps/desktop/Installer/WhisperXAtom.iss` with
   `scripts/build-installer.ps1`.
6. Before installing, the Inno preflight checks Recorder health and blocks on
   `STARTING`, `RECORDING`, `PAUSED`, `FINALIZING`, an unknown state, or an
   uninspectable/foreign process. It never stops an active recording.
7. Install and run the verifier again with `-RequireInstalled
   -CheckRunningProcesses`.

The installer updates `{app}` only. `%ProgramData%`, `%LocalAppData%`, the
SQLite spool, the audio archive and DPAPI credentials are not release payload
and are preserved across updates.

## Rollback and diagnostics

If post-install identity or service checks fail, the updater/installer keeps
the previous version available for rollback. A failed gate is recorded as a
diagnostic status; it is not converted into a successful release. Docker
volumes are preserved and the Server Bundle is updated separately from the
client installer.
