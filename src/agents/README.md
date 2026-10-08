# agents

FastAPI service that runs a team of AG2 agents for stock analysis and returns a
validated JSON decision to the .NET engine.

## Running locally

```bash
uv sync
uv run python -m app
```

`python -m app` is the service's own entrypoint, and what the container runs. It does two
things uvicorn's command line cannot: it configures JSON logging **before** handing the
process over, so the stream is structured from its first line rather than from the first
line after startup, and it binds what `TAS_BIND_HOST` and `TAS_PORT` say instead of a
command line sitting beside them.

uvicorn directly is still the right thing for `--reload`:

```bash
uv run uvicorn app.main:app --reload --host 127.0.0.1 --port 8000
```

Its first two lines - "Started server process" and "Waiting for application startup" - are
then in uvicorn's own format rather than JSON, because `configure_logging` runs in the
FastAPI lifespan, which is after them. That is a fine trade in a terminal and the wrong one
in a deployment.

Configuration is read from `.env`; see `.env.example` for the keys.
The architecture assessment and staged plan live in `docs/arkitektur-roadmap.md`.

## Bind address

Bind to loopback unless you have a deliberate reason not to. Through `python -m app` that
is `TAS_BIND_HOST`, which defaults to `127.0.0.1`.

`0.0.0.0` (or any non-loopback address) exposes `/health`, `/ready`, and - if enabled -
OpenAPI docs without an API key to every interface. That is appropriate only behind
Docker/compose on a private network, or a reverse proxy that enforces TLS and network
policy.

Outside `TAS_ENVIRONMENT=development`, a non-loopback value logs a startup warning. The
service does **not** refuse to start: intentional container publishes must keep working.
When uvicorn is started by hand, set `TAS_BIND_HOST` to the same value passed on the
command line - the setting is then a claim about a socket the service did not open, and the
warning can only read the claim.
