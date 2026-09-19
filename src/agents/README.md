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
