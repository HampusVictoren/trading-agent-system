# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A hybrid, modular automated trading agent that runs entirely locally:
- The **.NET 10 engine** (`src/engine`) is deterministic and rule-based. It owns scheduling, the portfolio (cash, positions), the `RiskEngine` that checks proposals against hard rules, and order execution.
- The **Python agent service** (`src/agents`) is a FastAPI app. It exposes `POST /analyze/{ticker}` and runs a team of AG2 v1.0+ agents that reason over market data and return a validated JSON decision.
- A **FastMCP server** exposes market-data tools such as `get_stock_quote`, backed by yfinance.
- **PostgreSQL + pgvector** (Docker) holds hard facts and the agents' semantic memory (`agent.agent_memories`).
- **Ollama** is the LLM backend for both text generation (`llama3.2`) and embeddings (`nomic-embed-text`). The plan is to add Claude or Grok later.

Everything in this repo is written in **English** — code, comments, log messages, exception messages, commit messages and documentation. There are exactly two exceptions:

- **Agent prompts** in `src/agents/app/infrastructure/ag2/` stay in Swedish, so the agents' `reasoning` comes back in Swedish.
- **`docs/arkitektur-roadmap.md`** is written in Swedish. It holds the architecture assessment and the staged plan — read it before starting work on a new stage.

## Commands

Everything runs inside WSL (Ubuntu-24.04).

```bash
# Tests, lint and formatting - what .github/workflows/ci.yml runs
dotnet build TradingSystem.slnx                          # TreatWarningsAsErrors is on
dotnet test --solution TradingSystem.slnx                # xunit v3 on Microsoft.Testing.Platform
dotnet format TradingSystem.slnx --verify-no-changes

cd src/agents && uv run ruff check app/ tests/
cd src/agents && uv run ruff format --check app/ tests/
cd src/agents && uv run mypy app/
cd src/agents && uv run pytest
```

`global.json` opts `dotnet test` into Microsoft.Testing.Platform, which the .NET 10 SDK
requires for xunit v3. Note the `--solution` flag: the new runner needs it.

```bash
# Database: compose owns the trading-db container. Needs .env in the repo root (see .env.example).
docker compose up -d
docker exec -it trading-db psql -U postgres -d tradingdb   # superuser, via the container's local socket

# Python agent service — run from src/agents (Python 3.12, managed with uv)
uv sync
uv run uvicorn app.main:app --reload --host 127.0.0.1 --port 8000
curl -X POST http://127.0.0.1:8000/analyze/AAPL
uv run python -m app.infrastructure.mcp.market_data_server   # run the FastMCP server standalone

# Smoke-test memory. MemoryStore takes a pool and an embeddings client, both built by the
# FastAPI lifespan in app/main.py; a script builds its own the same way.
uv run python -c "import asyncio, asyncpg, httpx2
from openai import AsyncOpenAI
from pgvector.asyncpg import register_vector
from app.infrastructure.db.memory import MemoryStore
from app.settings import get_settings
async def main():
    s = get_settings()
    async with httpx2.AsyncClient(trust_env=False) as http, asyncpg.create_pool(
            dsn=s.database_url.get_secret_value(), init=register_vector) as pool:
        store = MemoryStore(pool, AsyncOpenAI(base_url=str(s.ollama_base_url), api_key='ollama', http_client=http))
        await store.save('TEST','HOLD','Röktest'); print(await store.search('TEST','röktest'))
asyncio.run(main())"

# .NET engine (net10.0 Worker SDK)
dotnet build src/engine
dotnet run --project src/engine
```

The working directory must be `src/agents` for the `app.*` imports to resolve.

**Configuration:** `src/agents/app/settings.py` defines every setting as a typed, **required** field and reads `src/agents/.env` itself, so it applies to uvicorn, scripts and `python -c`. Variables already set in the shell take precedence. Nothing has a default: an incomplete environment stops the service at startup rather than falling back to OpenAI's cloud API or the wrong database role. Read them with `get_settings()`, never `os.getenv`. See `.env.example` for the keys:
- `DATABASE_URL` (`SecretStr` - it carries the `agent_svc` password)
- `OLLAMA_BASE_URL` (embeddings)
- `LLM_BASE_URL` and `LLM_MODEL` (AG2 through `OpenAIConfig` against Ollama's OpenAI-compatible `/v1`)
- `OPENAI_API_KEY` (`SecretStr`)
- `AGENT_API_KEY` (`SecretStr`) - what a caller must present as `X-Api-Key` to start an analysis
- `LLM_TIMEOUT_SECONDS` - caps one LLM call. Without it the openai client waits 600 s to read a response, which makes a 504 unreachable in practice.

**Shared resources are built once**, in the FastAPI `lifespan` in `app/main.py`: the `httpx2` client, the `AsyncOpenAI` embeddings client, the AG2 model configuration and an `asyncpg` pool. They reach a route as `Resources` through `app/dependencies.py`. Nothing creates a client at import time, so no client is bound to the wrong event loop. The pool opens a connection at startup, which means **`docker compose up -d` has to have run before `uvicorn`**.

**`/analyze` requires `X-Api-Key`**, compared with `hmac.compare_digest` so the comparison takes the same time whichever byte differs first. A missing key and a wrong key both answer 401 `unauthorized`, saying nothing about which it was. `/health` and `/ready` stay open, because a load balancer has to be able to ask whether the service is up.

**Every request carries a correlation id.** `CorrelationIdMiddleware` reads `X-Correlation-Id`, or invents one, echoes it on the response and puts it in every log line. Logs are JSON, configured in the lifespan, so uvicorn's own lines are formatted too - except the two banner lines it prints before startup. `/health` is liveness and checks nothing else on purpose; `/ready` checks the database and the LLM backend and answers 503 until both do.

The engine reads `AgentService:BaseUrl`, `RequestTimeoutSeconds`, `RiskPolicy` and `Trading` from `appsettings.json`, all validated at startup. **`AgentService:ApiKey` is not there**, because it is a secret: locally it lives in the user secrets store, outside the repository, and elsewhere it comes from the environment.

```bash
# Both sides need the same value. Generate one, then give it to each:
python -c "import secrets; print(secrets.token_urlsafe(32))"
# -> AGENT_API_KEY=<key> in src/agents/.env
dotnet user-secrets set "AgentService:ApiKey" "<key>" --project src/engine
```

## Local environment (Windows + WSL2)

- **Ollama runs on Windows**, not in WSL. It listens on `0.0.0.0:11434` (`OLLAMA_HOST=0.0.0.0`). WSL runs in **mirrored networking mode** (`networkingMode=mirrored` in `%USERPROFILE%\.wslconfig`), so `127.0.0.1:11434` inside WSL reaches Windows. Without mirrored mode, `127.0.0.1` in WSL is WSL itself: you get `APIConnectionError` / `ConnectError('All connection attempts failed')` and would need the Windows host IP (`ip route show default`).
- **Use explicit IPv4 `127.0.0.1`, never `localhost`**, for Ollama, FastAPI and Postgres. `localhost` can resolve to `::1` and time out.
- The lifespan creates the Ollama client with `httpx2.AsyncClient(trust_env=False)`, so a system proxy can't intercept local calls. Keep that for any new client that talks to Ollama. It is `httpx2`, not `httpx`, because that is what `openai` 3.x types `http_client` against.
- **Embeddings are 768-dimensional** (`nomic-embed-text`), not OpenAI's 1536. If you switch embedding models, you have to change the column type too.
- **The DB container is `trading-db`, and `docker-compose.yml` owns it.** Never create it by hand. It binds to `127.0.0.1:5432` only. Other Postgres containers (e.g. `stockinvestor-db`, `backend-db-1`) conflict on port 5432 and must be stopped.
- **Secrets live in two gitignored files, on purpose.** `.env` in the repo root holds the superuser, `engine_svc` and `agent_svc` passwords for compose. `src/agents/.env` holds only the agent's own `DATABASE_URL`. The agent service must never see the other two, or the per-service roles mean nothing.

## Database schema

`db/init/01-schema.sh` builds the database. It runs **once**, on the first start of an empty volume, so editing it has no effect until the volume is recreated (`docker compose down -v && docker compose up -d` — this deletes all data).

| Schema | Owner | Holds |
|---|---|---|
| `trading` | `engine_svc` | Portfolio, orders and decisions (EF Core, from stage 4). Empty today. |
| `agent` | `agent_svc` | `agent.agent_memories` — pgvector semantic memory |

Each role owns its schema, so its migration tool can create tables there, and has **no access to the other's** — not even to see what tables exist. Neither can create anything in `public`. Only the two roles and the superuser may connect. The `vector` extension stays in `public`, because `pgvector.asyncpg.register_vector` looks it up there.

`agent.agent_memories` has a `bigint` identity key, a `NOT NULL` 768-dimensional `embedding`, and an HNSW index with `vector_cosine_ops`. `MemoryStore.search` ranks rows by cosine distance (`<=>`), filtered by ticker. Always schema-qualify table names.

## Architecture

### Request flow

1. `TradingWorker` (`src/engine/Hosting/Workers`) is a `BackgroundService` that holds a single in-memory `Portfolio` (10,000 USD). Every `Trading:CycleIntervalSeconds` it runs `ProcessProposalUseCase` for each ticker in `Trading:Tickers`.
2. `PythonAgentClient` calls `POST /analyze/{ticker}` on the agent service.
3. `routes.py` calls `run_agent_analysis` in `app/infrastructure/ag2/team.py`. It runs three AG2 agents **sequentially**, not as a group chat:
   - `MarketAnalyst` fetches market data with a tool.
   - `RiskManager` reviews the analysis for downside and valuation risk.
   - `PortfolioManager` is called with `ask(..., response_schema=InvestmentProposal)`. AG2 sends the schema as `response_format` (json_schema), and `reply.content(retries=2)` validates the answer with pydantic. On a validation error, it asks the model again with the error message.
   - If anything fails, `run_agent_analysis` raises an `AnalysisError` from `app/application/errors.py`. **There is no fallback HOLD**: a failure that looks like a decision is one the engine cannot tell apart from a real one.
4. `app/api/errors.py` turns that into an honest status code, and a body of `{error_code, correlation_id}` and nothing else. The detail is logged, never returned.

   | Failure | Status | `error_code` |
   |---|---|---|
   | Decision made, including HOLD | 200 | - |
   | No or wrong `X-Api-Key` | 401 | `unauthorized` |
   | Ticker is not a ticker | 422 | `invalid_request` |
   | LLM answered with an error status | 502 | `llm_failed` |
   | Model never matched the schema, after AG2's retries | 502 | `agent_response_invalid` |
   | A step failed for a reason of AG2's own | 502 | `agent_chain_failed` |
   | LLM backend unreachable | 503 | `llm_unreachable` |
   | LLM did not answer within `LLM_TIMEOUT_SECONDS` | 504 | `llm_timeout` |

5. Back in the engine, a non-2xx becomes `AgentUnavailable` with the status code in the message; anything that isn't `BUY` is ignored. `RiskEngine.ValidateTrade` enforces a 5% max position size and a cash check (and throws `RiskViolationException`), then `Portfolio.ExecuteBuy` runs with `quantity: 1` and the proposed amount as the price.

### Cross-service contract

The JSON proposal (`ticker`, `action`, `amount_usd`, `confidence`, `reasoning`) is defined in two places that must stay in sync:
- `src/agents/app/domain/models.py`: the `InvestmentProposal` pydantic model. It is used as both the FastAPI `response_model` and the AG2 `response_schema`. Its rules: `action` is one of `BUY`/`SELL`/`HOLD`, `amount_usd >= 0`, `0 <= confidence <= 1`, and `BUY` requires `amount_usd > 0`. Changes to this model change what the LLM is asked to produce.
- `src/engine/Application/Dtos/InvestmentProposalDto.cs`: snake_case via `JsonPropertyName`, with `action` as a plain string.

### Layering

Both services follow a Clean Architecture / DDD layout: `Domain` → `Application` → `Infrastructure` → `Hosting`/`api`. In the engine, `Portfolio` is the aggregate root, `Money` and `Ticker` are value objects, and `RiskEngine` is a domain service. Wiring happens in `Program.cs`.

### Intended design vs. current code

- **MCP calls:** the design has agents calling tools over the MCP protocol. `team.py` actually imports `get_stock_quote` from the MCP server module and calls it in-process.
- **Memory:** `MemoryStore` works, including writes and search against `trading-db`, and the lifespan builds one - but no route uses it yet. The plan is a `search_history_tool` on `RiskManager` and a `save` call after each analysis cycle.
- **LLM provider:** switching providers through an env var is planned but not built. AG2 1.0.5 ships `AnthropicConfig` (needs `ag2[anthropic]`) and `XAIConfig` (needs `xai_sdk`). Grok also works through `OpenAIConfig` with `base_url=https://api.x.ai/v1`. `ag2/config.py` currently always builds an `OpenAIConfig`.
- **Model quality:** `llama3.2` (3B) often gives weak or contradictory reasoning, even though the JSON is valid. A larger local model or Claude/Grok would do better.
- **Risk limit:** `PortfolioManager` isn't told the portfolio's cash or the engine's 5% limit. It often proposes amounts far above the limit (1,000–100,000 USD), and `RiskEngine` rejects them. The rejection is logged as `fail` with a stack trace, even though it's an expected business outcome.
- **Timing:** one analysis cycle takes about 12–15 s with `llama3.2`. The engine's `HttpClient` uses the default 100 s timeout.
- **Empty packages:** `app/infrastructure/llm/` and `app/infrastructure/market_data/` contain only `__init__.py`; the placeholder files in them were deleted in stage 0. The roadmap puts the provider factory in `llm/provider.py` in stage 3.
- **Engine database access:** the `engine_svc` role and the `trading` schema exist, but the engine has no DB access code until stage 4.
- **AG2 API version:** AG2 is used through its v1.0+ API (`from ag2 import Agent, tool`, `ag2.config.OpenAIConfig`, `await agent.ask(...)`). The pre-1.0 `autogen`/`ConversableAgent` API is not used here.
