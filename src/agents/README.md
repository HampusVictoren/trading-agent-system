# agents

FastAPI service that runs a team of AG2 agents for stock analysis and returns a
validated JSON decision to the .NET engine.

## Running locally

```bash
uv sync
uv run uvicorn app.main:app --host 127.0.0.1 --port 8000
```

Configuration is read from `.env`; see `.env.example` for the keys.
The architecture assessment and staged plan live in `docs/arkitektur-roadmap.md`.

## Bind address

Bind to loopback unless you have a deliberate reason not to:

```bash
uv run uvicorn app.main:app --host 127.0.0.1 --port 8000
```

`--host 0.0.0.0` (or any non-loopback address) exposes `/health`, `/ready`, and — if
enabled — OpenAPI docs without an API key to every interface. That is appropriate only
behind Docker/compose on a private network or a reverse proxy that enforces TLS and
network policy.

Set `TAS_BIND_HOST` to the same value you pass to uvicorn. Outside
`TAS_ENVIRONMENT=development`, a non-loopback value logs a startup warning. The service
does **not** refuse to start: intentional container publishes must keep working.
