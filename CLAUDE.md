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

# Smoke-test memory. Use a single asyncio.run: the module-level httpx client in memory.py can't be reused across event loops.
uv run python -c "import asyncio; from app.infrastructure.db.memory import save_memory, search_past_memories
async def main():
    await save_memory('TEST','HOLD','Röktest'); print(await search_past_memories('TEST','röktest'))
asyncio.run(main())"

# .NET engine (net10.0 Worker SDK)
dotnet build src/engine
dotnet run --project src/engine
```

The working directory must be `src/agents` for the `app.*` imports to resolve.

**Configuration:** `src/agents/app/__init__.py` loads `src/agents/.env` automatically, so it applies to uvicorn, scripts and `python -c`. Variables already set in the shell take precedence. See `.env.example` for the keys:
- `DATABASE_URL`
- `OLLAMA_BASE_URL` (embeddings)
- `LLM_BASE_URL` and `LLM_MODEL` (AG2 through `OpenAIConfig` against Ollama's OpenAI-compatible `/v1`)
- `OPENAI_API_KEY`

The engine reads `AgentService:BaseUrl` from `appsettings.json`.

## Local environment (Windows + WSL2)

- **Ollama runs on Windows**, not in WSL. It listens on `0.0.0.0:11434` (`OLLAMA_HOST=0.0.0.0`). WSL runs in **mirrored networking mode** (`networkingMode=mirrored` in `%USERPROFILE%\.wslconfig`), so `127.0.0.1:11434` inside WSL reaches Windows. Without mirrored mode, `127.0.0.1` in WSL is WSL itself: you get `APIConnectionError` / `ConnectError('All connection attempts failed')` and would need the Windows host IP (`ip route show default`).
- **Use explicit IPv4 `127.0.0.1`, never `localhost`**, for Ollama, FastAPI and Postgres. `localhost` can resolve to `::1` and time out.
- `memory.py` creates its Ollama client with `httpx.AsyncClient(trust_env=False)`, so a system proxy can't intercept local calls. Keep that for any new client that talks to Ollama.
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

`agent.agent_memories` has a `bigint` identity key, a `NOT NULL` 768-dimensional `embedding`, and an HNSW index with `vector_cosine_ops`. `search_past_memories` ranks rows by cosine distance (`<=>`), filtered by ticker. Always schema-qualify table names.

## Architecture

### Request flow

1. `TradingWorker` (`src/engine/Hosting/Workers`) is a `BackgroundService` that holds a single in-memory `Portfolio` (10,000 USD). Every 15 seconds it runs `ProcessProposalUseCase` for a hard-coded ticker (`AAPL`).
2. `PythonAgentClient` calls `POST /analyze/{ticker}` on the agent service.
3. `routes.py` calls `run_agent_analysis` in `app/infrastructure/ag2/team.py`. It runs three AG2 agents **sequentially**, not as a group chat:
   - `MarketAnalyst` fetches market data with a tool.
   - `RiskManager` reviews the analysis for downside and valuation risk.
   - `PortfolioManager` is called with `ask(..., response_schema=InvestmentProposal)`. AG2 sends the schema as `response_format` (json_schema), and `reply.content(retries=2)` validates the answer with pydantic. On a validation error, it asks the model again with the error message.
   - If anything fails, the function logs the error and returns **`HOLD` with `amount_usd=0`**, with `reasoning` starting with `"Fallback (HOLD)"`. An error can never lead to a buy.
4. Back in the engine, anything that isn't `BUY` is ignored. `RiskEngine.ValidateTrade` enforces a 5% max position size and a cash check (and throws `RiskViolationException`), then `Portfolio.ExecuteBuy` runs with `quantity: 1` and the proposed amount as the price.

### Cross-service contract

The JSON proposal (`ticker`, `action`, `amount_usd`, `confidence`, `reasoning`) is defined in two places that must stay in sync:
- `src/agents/app/domain/models.py`: the `InvestmentProposal` pydantic model. It is used as both the FastAPI `response_model` and the AG2 `response_schema`. Its rules: `action` is one of `BUY`/`SELL`/`HOLD`, `amount_usd >= 0`, `0 <= confidence <= 1`, and `BUY` requires `amount_usd > 0`. Changes to this model change what the LLM is asked to produce.
- `src/engine/Application/Dtos/InvestmentProposalDto.cs`: snake_case via `JsonPropertyName`, with `action` as a plain string.

### Layering

Both services follow a Clean Architecture / DDD layout: `Domain` → `Application` → `Infrastructure` → `Hosting`/`api`. In the engine, `Portfolio` is the aggregate root, `Money` and `Ticker` are value objects, and `RiskEngine` is a domain service. Wiring happens in `Program.cs`.

### Intended design vs. current code

- **MCP calls:** the design has agents calling tools over the MCP protocol. `team.py` actually imports `get_stock_quote` from the MCP server module and calls it in-process.
- **Memory:** `memory.py` works, including writes and search against `trading-db`, but it isn't wired into the flow yet. The plan is a `search_history_tool` on `RiskManager` and a `save_memory` call after each analysis cycle.
- **LLM provider:** switching providers through an env var is planned but not built. AG2 1.0.5 ships `AnthropicConfig` (needs `ag2[anthropic]`) and `XAIConfig` (needs `xai_sdk`). Grok also works through `OpenAIConfig` with `base_url=https://api.x.ai/v1`. `ag2/config.py` currently always builds an `OpenAIConfig`.
- **Model quality:** `llama3.2` (3B) often gives weak or contradictory reasoning, even though the JSON is valid. A larger local model or Claude/Grok would do better.
- **Risk limit:** `PortfolioManager` isn't told the portfolio's cash or the engine's 5% limit. It often proposes amounts far above the limit (1,000–100,000 USD), and `RiskEngine` rejects them. The rejection is logged as `fail` with a stack trace, even though it's an expected business outcome.
- **Timing:** one analysis cycle takes about 12–15 s with `llama3.2`. The engine's `HttpClient` uses the default 100 s timeout.
- **Leftovers:** `application/analysis_service.py`, `ag2/analyst_agent.py`, `ag2/risk_agent.py` and `llm/config.py` are empty placeholders. `market_data/stock_client.py` duplicates the MCP quote logic and is unused.
- **Engine database access:** the `engine_svc` role and the `trading` schema exist, but the engine has no DB access code until stage 4.
- **AG2 API version:** AG2 is used through its v1.0+ API (`from ag2 import Agent, tool`, `ag2.config.OpenAIConfig`, `await agent.ask(...)`). The pre-1.0 `autogen`/`ConversableAgent` API is not used here.
