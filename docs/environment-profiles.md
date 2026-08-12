# API environment profiles

WhisperX Atom has explicit Development and Production configurations. Do not use the Development compose file as a Production deployment.

## Development

`compose.dev.yml` sets:

- `ASPNETCORE_ENVIRONMENT=Development`;
- `DOTNET_ENVIRONMENT=Development`;
- `COOKIE_SECURE=false`;
- API binding only to `127.0.0.1:8080`.

Start the local Desktop runtime:

```powershell
docker compose --env-file .env -f compose.dev.yml --profile core up -d --build
Invoke-WebRequest http://127.0.0.1:8080/ready
```

Development may use the placeholder values from `.env.example`; those values are not acceptable for Production.

## Production

Production is an explicit override with secure cookies, no direct API host port, required non-placeholder secrets, and an HTTPS/TLS Caddy gateway:

```powershell
docker compose --env-file .env.production -f compose.dev.yml -f compose.prod.yml --profile core --profile prod up -d --build
```

`.env.production` must provide real values for `POSTGRES_PASSWORD`, `BOOTSTRAP_ADMIN_PASSWORD`, `TUS_HOOK_SECRET`, `IMPORT_WORKER_TOKEN`, `AGENT_ENROLLMENT_SECRET`, `VOICE_HOST_TOKEN`, `PUBLIC_HOST` and `TLS_CERTS_HOST`. The certificate directory must contain `fullchain.pem` and `privkey.pem`.

The API refuses to start in Production when `COOKIE_SECURE=false` or when a guarded secret is blank or uses `generate-*`, `replace-with-*`, `changeme`, or `password`.
