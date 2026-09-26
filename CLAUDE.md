# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A hybrid, modular automated trading agent that runs entirely locally:
- The **.NET 10 engine** (`src/engine`) is deterministic and rule-based. It owns scheduling, the portfolio (cash, positions), the `RiskEngine` that checks proposals against hard rules, and order execution.
- The **Python agent service** (`src/agents`) is a FastAPI app. It exposes `POST /v1/signals` and runs a team of AG2 v1.0+ agents over a computed fact sheet, returning a validated `TradeSignal`: a direction and a conviction, never an amount. It also takes `POST /v1/outcomes`, which is how the engine's measurements reach this side — the database is never the integration point between the two services.
- **Market data** comes from yfinance behind a `MarketDataProvider` port, with a TTL cache and a timeout, and is turned into a `FactSheet` by pure functions before any agent runs. The same provider answers `GET /v1/quotes/{symbol}` and `GET /v1/quotes/{symbol}/history?from=`, which is how the engine prices holdings it is not analysing and how it measures an outcome - one market-data integration, in one service.
- **PostgreSQL + pgvector** (Docker) holds both services' state, in a schema each: `trading` has the portfolio, the append-only order ledger and every decision the engine has ever made (EF Core), and `agent` has this service's own journal of every analysis (`agent.analysis_runs`, `agent.step_outputs`), its copy of what the engine measured (`agent.signal_outcomes`) and the pgvector memory over the two (`agent.analysis_embeddings`).
- **Ollama** is the LLM backend for both text generation (`qwen2.5:14b`) and embeddings (`nomic-embed-text`). The plan is to add Claude or Grok later.

Everything in this repo is written in **English** — code, comments, log messages, exception messages, commit messages and documentation. There are exactly two exceptions:

- **Anything an agent reads** stays in Swedish, so the agents' own text comes back in Swedish. That is the prompt files in `src/agents/app/teams/<team>/prompts/`, and the handful of labels the pipeline puts in a message (`Instrument:`, `Nuvarande innehav:`) in `app/application/pipeline.py`. The rule is about the audience, not the directory: if a model reads it, it is Swedish.
- **`docs/arkitektur-roadmap.md`** is written in Swedish. It holds the architecture assessment and the staged plan — read it before starting work on a new stage.

## Commands

Everything runs inside WSL (Ubuntu-24.04).

```bash
# Tests, lint and formatting - what .github/workflows/ci.yml runs
dotnet build TradingSystem.slnx                          # TreatWarningsAsErrors is on
dotnet test --solution TradingSystem.slnx                # xunit v3 on Microsoft.Testing.Platform
dotnet format TradingSystem.slnx --verify-no-changes

cd src/agents && uv run ruff check app/ tests/ migrations/
cd src/agents && uv run ruff format --check app/ tests/ migrations/
cd src/agents && uv run mypy app/ migrations/
cd src/agents && uv run pytest
```

`global.json` opts `dotnet test` into Microsoft.Testing.Platform, which the .NET 10 SDK
requires for xunit v3. Note the `--solution` flag: the new runner needs it.

**The database tests need Docker, on both sides.** Each starts a
`pgvector/pgvector:0.8.6-pg16` container, runs the checked-in `db/init/01-schema.sh` inside it,
and connects as `engine_svc` or `agent_svc` rather than as a superuser - so a migration is
proved under the grants it actually runs under. The container is shared for the run (a
collection fixture in .NET, a session fixture in `tests/conftest.py`), so a run that touches
only domain or unit tests starts nothing.

```bash
# Migrations. dotnet-ef is a local tool, pinned in dotnet-tools.json.
dotnet tool restore
dotnet dotnet-ef migrations add <Name> --project src/engine --output-dir Infrastructure/Persistence/Migrations
dotnet dotnet-ef migrations script --project src/engine --idempotent   # read it before trusting it
dotnet dotnet-ef migrations has-pending-model-changes --project src/engine
```

```bash
# The agent schema, from src/agents. Alembic is a dev dependency, the way dotnet-ef is a
# local tool: the service never imports it, and migrating is something an operator does.
uv run alembic upgrade head
uv run alembic current                      # which revision this database is on
uv run alembic upgrade head --sql           # read it before trusting it
uv run alembic revision -m "<message>"      # handwritten SQL; --autogenerate does nothing here
uv run alembic -x url=postgresql://... upgrade head   # a database other than your own
```

There are no SQLAlchemy models in this service - it talks to Postgres through asyncpg - so
`env.py` passes `target_metadata = None` and every migration is handwritten SQL. That is what
makes `--autogenerate` useless rather than dangerous: with no metadata to compare against, it
would propose dropping every table it found.

The engine will not start against a database that is behind it, so a new migration has to be
applied before `dotnet run` works again. Two more things about generated migrations. `dotnet ef`
writes its files with a **UTF-8 BOM**,
which `.editorconfig` forbids, so strip it or `dotnet format` fails. And EF 10 refuses to
`Migrate()` when the model has drifted from the last migration, which means the database
tests double as a drift guard: change a configuration without adding a migration and every
one of them goes red.

```bash
# Database: compose owns the trading-db container. Needs .env in the repo root (see .env.example).
docker compose up -d
docker exec -it trading-db psql -U postgres -d tradingdb   # superuser, via the container's local socket

# Python agent service — run from src/agents (Python 3.12, managed with uv)
uv sync
uv run uvicorn app.main:app --reload --host 127.0.0.1 --port 8000
curl -X POST http://127.0.0.1:8000/v1/signals -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d @../../contracts/examples/request.json

# Smoke-test memory. AnalysisMemory takes a pool and an embeddings client, both built by the
# FastAPI lifespan in app/main.py; a script builds its own the same way. `recall` needs no
# data to exercise every part of it - with an empty journal it answers the sentence that
# says nothing has been measured yet, having already embedded the query and run the SQL.
uv run python -c "import asyncio, asyncpg, httpx2
from openai import AsyncOpenAI
from pgvector.asyncpg import register_vector
from app.infrastructure.db.memory import AnalysisMemory
from app.settings import get_settings
async def main():
    s = get_settings()
    async with httpx2.AsyncClient(trust_env=False) as http, asyncpg.create_pool(
            dsn=s.database_url.get_secret_value(), init=register_vector) as pool:
        memory = AnalysisMemory(pool, AsyncOpenAI(base_url=str(s.embeddings_base_url),
            api_key=s.embeddings_api_key.get_secret_value(), http_client=http))
        print(await memory.recall('AAPL', 'stabil uppgång', correlation_id='smoke-test'))
asyncio.run(main())"

# .NET engine (net10.0 Worker SDK)
dotnet build src/engine
dotnet run --project src/engine
```

The working directory must be `src/agents` for the `app.*` imports to resolve.

**Configuration:** `src/agents/app/settings.py` defines every setting as a typed, **required** field and reads `src/agents/.env` itself, so it applies to uvicorn, scripts and `python -c`. Variables already set in the shell take precedence. Nothing has a default: an incomplete environment stops the service at startup rather than falling back to OpenAI's cloud API or the wrong database role. Read them with `get_settings()`, never `os.getenv`. See `.env.example` for the keys:
- `DATABASE_URL` (`SecretStr` - it carries the `agent_svc` password)
- `TAS_EMBEDDINGS_BASE_URL` and `TAS_EMBEDDINGS_API_KEY` (`SecretStr`). The embedding *model* is pinned in `memory.py`, because nomic-embed-text's 768 dimensions are the column width.
- `TAS_AGENT_API_KEY` (`SecretStr`) - what a caller must present as `X-Api-Key` to start an analysis
- `TAS_LLM__DEFAULT__*` - one `ModelSpec`: `PROVIDER`, `MODEL`, `BASE_URL`, `API_KEY`, `TEMPERATURE`, `TIMEOUT_S`, and optionally `SEED`. `TIMEOUT_S` caps one LLM call; without it the openai client waits 600 s to read a response, which makes a 504 unreachable in practice. `TEMPERATURE` is 0.0 and `SEED` is set, which pins **the decision, not the run**: Ollama returns the same bytes when the same request is repeated in the same state, but a different request in between changes them, because the numerics depend on batching and KV-cache state outside the request. What that buys is that a contradiction can no longer be blamed on the draw. Note the interaction when swapping providers - `anthropic` has no seed field, so `settings.py` refuses the combination at startup rather than dropping it silently, and switching to it means removing `SEED` too.
- `TAS_LLM__ROLES__<ROLE>__*` - the same fields, overriding one step's model. The roles are the steps of a team (`market_analyst`, `risk_manager`, `portfolio_manager`), and startup refuses a name no step uses, so a typo cannot fall back to the default.
- `TAS_MARKET_DATA_TIMEOUT_S` and `TAS_MARKET_DATA_TTL_S` - one yfinance call is synchronous network I/O run in a thread; the TTL is how long a quote may be reused.

**Everything is prefixed `TAS_`** because `OPENAI_API_KEY` is what openai's own SDK reads, and a shell value beats `.env` - without the prefix, a real cloud key in your shell would quietly become this service's.

**Shared resources are built once**, in the FastAPI `lifespan` in `app/main.py`: the `httpx2` client, the `AsyncOpenAI` embeddings client, one AG2 model configuration per role, an `asyncpg` pool, and the whole `SignalPipeline` - every team validated, every prompt file read, every `team_version` hashed and every agent constructed. They reach a route as `Resources` through `app/dependencies.py`. Nothing creates a client at import time, so no client is bound to the wrong event loop. The pool opens a connection at startup, which means **`docker compose up -d` has to have run before `uvicorn`**.

**`/analyze` requires `X-Api-Key`**, compared with `hmac.compare_digest` so the comparison takes the same time whichever byte differs first. A missing key and a wrong key both answer 401 `unauthorized`, saying nothing about which it was. `/health` and `/ready` stay open, because a load balancer has to be able to ask whether the service is up.

**Every request carries a correlation id.** `CorrelationIdMiddleware` reads `X-Correlation-Id`, or invents one, echoes it on the response and puts it in every log line. Logs are JSON, configured in the lifespan, so uvicorn's own lines are formatted too - except the two banner lines it prints before startup. `/health` is liveness and checks nothing else on purpose; `/ready` checks the database and the LLM backend and answers 503 until both do.

The engine reads `AgentService:BaseUrl`, `RequestTimeoutSeconds`, `RiskPolicy` and `Trading` (including `OpeningBalance`, the balance the account is opened with on the very first cycle) from `appsettings.json`, all validated at startup. It also **refuses to start when the database is behind the build**, naming the pending migrations and the command that applies them - and it never migrates itself, because applying at startup would move the schema before anyone could decide to. That check is its first connection, so an unreachable database fails there too. **`AgentService:ApiKey` and `Database:ConnectionString` are not there**, because both are secrets: locally they live in the user secrets store, outside the repository, and elsewhere they come from the environment. Neither has a default - an engine that cannot reach its database refuses to start, because from stage 4 a cycle it cannot store is a cycle whose evidence is lost.

```bash
# Both sides need the same value. Generate one, then give it to each:
python -c "import secrets; print(secrets.token_urlsafe(32))"
# -> TAS_AGENT_API_KEY=<key> in src/agents/.env
dotnet user-secrets set "AgentService:ApiKey" "<key>" --project src/engine

# The engine's own connection string carries the engine_svc password from the repo root .env.
dotnet user-secrets set "Database:ConnectionString" \
  "Host=127.0.0.1;Port=5432;Database=tradingdb;Username=engine_svc;Password=<engine password>" \
  --project src/engine
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

**It creates no tables.** It makes only what a migration tool cannot make for itself: the `vector` extension, the two login roles, the two schemas and who owns them. Every table is then its owner's migration tool's: EF Core for `trading`, Alembic for `agent`. That is what makes "runs once" harmless — nothing in that file changes as the schemas grow. On a fresh volume, `docker compose up -d` therefore leaves an empty `agent` schema until `uv run alembic upgrade head` has run.

| Schema | Owner | Holds |
|---|---|---|
| `trading` | `engine_svc` | `portfolios`, `positions`, `orders`, `decisions`, `signal_outcomes`, `outcome_deliveries` and the `hit_rate` view, through EF Core |
| `agent` | `agent_svc` | `analysis_runs`, `step_outputs`, `signal_outcomes` and `analysis_embeddings` — the journal and the pgvector memory over it, through Alembic |

Each role owns its schema, so its migration tool can create tables there, and has **no access to the other's** — not even to see what tables exist. Neither can create anything in `public`. Only the two roles and the superuser may connect. The `vector` extension stays in `public`, because `pgvector.asyncpg.register_vector` looks it up there.

`trading` is EF Core's, migrated from `src/engine/Infrastructure/Persistence/Migrations` with
its history table inside the same schema - `engine_svc` may not create anything in `public`, so
EF's default location would fail as permission denied. `portfolios` carries a `positions`
collection keyed on `(portfolio_id, symbol)`, so "one holding per instrument" is a primary key
rather than a convention, and it uses Postgres's `xmin` as its concurrency token. `orders` and
`decisions` are **append-only, enforced by a trigger** that raises on `UPDATE` and `DELETE`,
including a `DELETE` that would match no rows; `TRUNCATE` is deliberately left alone, because
that is the owner clearing the table on purpose rather than a cycle rewriting history.
`decisions.correlation_id` is unique, so one analysis cannot become two rows.

`signal_outcomes` is one row per signal per horizon, append-only as well, unique on
`(decision_id, horizon_unit, horizon_days)` so a sweep that runs twice cannot write the same
measurement again. The horizon takes two columns because five trading days and five calendar
days are different measurements. A row that could never be measured is still a row, with the
reason and null returns, so the sweep stops retrying it and the report still knows it existed.
`trading.hit_rate` is the minimum report: hit rate against the index per `team_version`,
conviction tier and stance, with gross and net edge side by side.

`outcome_deliveries` is one row per measurement the agent service has been told about, and
no row at all until it lands. It is a table rather than a column on `signal_outcomes`
because that table refuses `UPDATE` - but the separation is also truer: whether a
measurement has been copied somewhere is a fact about a side effect. It is deliberately
**not** append-only, unlike everything around it. A delivery receipt is not evidence, and
deleting one is the supported way to ask for a resend, which is safe because storing is
idempotent on the other side.

`agent` is Alembic's, migrated from `src/agents/migrations/versions`, with its version table inside the same schema. `agent_svc`'s `search_path` already resolves there, so naming it is not what makes it work — it is what stops the location depending on a role attribute set once, by a script that runs once. A database that predates the migrations holds the table but no version row, and is `alembic stamp <revision>`-ed rather than migrated.

`analysis_runs` and `step_outputs` are what one analysis leaves behind: the fact sheet the agents started from, and every step's answer as `jsonb`, against the same `correlation_id` the engine stores in `trading.decisions`. Nothing joins the two schemas in the database - the join is made when a question is asked, which is what keeps two services out of one schema. Both are **append-only**, by the same statement-level trigger the engine uses, because a replay is only worth running if the inputs it replays are the inputs that were used. `correlation_id` is unique, so a retry above the layer cannot turn one analysis into two rows. The steps are `jsonb` rather than columns because a step's schema belongs to its `team_version`: modelling it here would mean a migration every time a prompt's output grew a field, and a table that could not hold two teams at once.

`signal_outcomes` is this service's copy of what the engine measured, posted to `POST /v1/outcomes` after a sweep. The engine owns the measurement; the copy exists so memory can say what happened afterwards rather than only what was argued at the time. It has **no foreign key** to `analysis_runs` — the engine measures decisions this service never produced a signal for — and the write is `ON CONFLICT DO NOTHING`, so a sweep that is retried does not have to know what landed. Append-only too: a correction is a new measurement in the engine and a new row here, never an edit.

`analysis_embeddings` is the memory, and it is **one vector per journalled analysis** rather than a store of its own: `agent_memories` was retired because three of its four columns already existed in the journal, and the one thing it lacked — the `correlation_id` — is what memory needs to join an analysis to what happened afterwards. It is a table beside `analysis_runs` rather than a column on it, because an embedding is **derived data, not evidence**: the journal is append-only, and embeddings have to be rebuildable when the model changes. The model's name is stored on every row, since a mixture of two models in one index is a similarity score that means nothing. The index is HNSW with `vector_cosine_ops`, matching the `<=>` that `AnalysisMemory.recall` orders by. Always schema-qualify table names.

**What reaches an agent is only what has been measured.** `recall` joins through `signal_outcomes` with an inner `LATERAL`, so an analysis the engine has not yet scored takes none of the three places. That makes memory empty — and say so — until a horizon has passed, which is the honest state and the point: reasoning without an outcome teaches a model to agree with itself, and a thesis it repeated three times reads as a well-founded one. What is embedded is the analyst's `MarketRead`, not the thesis, because the query available when the risk manager runs is today's `MarketRead` — matching a reading against a reading asks "when things looked like this before, what did we conclude and how did it go?".

## Architecture

### Request flow

1. `TradingWorker` (`src/engine/Hosting/Workers`) is a `BackgroundService` that holds **no** portfolio. Every `Trading:CycleIntervalSeconds`, for each ticker in `Trading:Tickers` (ERIC-B.ST and VOLV-B.ST), it opens a scope, reads the portfolio through `IPortfolioRepository`, runs `ProcessProposalUseCase`, and commits through `IUnitOfWork` - so one cycle is one change tracker and one transaction. The account is opened at `Trading:OpeningBalance` only when nothing is stored. The correlation id is generated and logged first, and the outcome is logged *after* the commit, so a line in the log means a row in the database. A failed commit is an error line and the loop carries on: the buy is in the same transaction as the decision, so nothing was traded.
2. `PythonAgentClient` posts a `TradeSignalRequestDto` to `POST /v1/signals`. Before sizing, it also asks `GET /v1/quotes/{symbol}` for every *other* holding, carrying the same correlation id; the analysed instrument's price always comes from the signal, so an order is never sized against a quote the agents never saw. The instrument travels in the body as a typed object, so nothing is interpolated into a path. The correlation id goes on the `X-Correlation-Id` header, taken from the body so the two cannot disagree.
3. `SignalPipeline.run` (`app/application/pipeline.py`) computes a `FactSheet` from market data — price, P/E, returns over 1/3/12 months, volatility, distance to the 52-week high — and then runs the team's steps in order. Each step is given **only the earlier results its `reads` names**, as a delimited JSON block; there is no shared transcript.

   | Step | reads | produces |
   |---|---|---|
   | `market_analyst` | `FactSheet` | `MarketRead` (trend, valuation, up to three notes) |
   | `risk_manager` | `FactSheet`, `MarketRead` (+ memory, on `default-memory`) | `RiskAssessment` (downside, veto, up to three risks) |
   | `portfolio_manager` | `MarketRead`, `RiskAssessment` | `TradeView` (stance, conviction, thesis, risks, horizon) |

   The portfolio manager never sees the fact sheet, and `TradeView` has no price field, so the pipeline supplies the instrument from the request and the price from the fact sheet. **There is no fallback HOLD**: a failure that looks like a decision is one the engine cannot tell apart from a real one.

   A run that produced a signal is then written to `agent.analysis_runs` and `agent.step_outputs` through the `AnalysisJournal` port. That write **cannot withhold an answer**: it happens after the signal is built, and a failure is an error line naming the correlation id, not a 500. The engine cannot tell a journal outage from the agents failing, so raising would read as "no decision this cycle" and stop trading over bookkeeping - and the hole is findable, as a correlation id in `decisions` with no run on this side.
4. `app/api/errors.py` turns a failure into an honest status code, and a body of `{error_code, correlation_id}` and nothing else. The detail is logged, never returned.

   | Failure | Status | `error_code` |
   |---|---|---|
   | Decision made, including HOLD | 200 | - |
   | No or wrong `X-Api-Key` | 401 | `unauthorized` |
   | The request is not the contract | 422 | `invalid_request` |
   | No such `team_id` | 422 | `unknown_team` |
   | The team does not cover that instrument | 422 | `instrument_not_supported` |
   | No market data exists for the symbol | 422 | `instrument_not_found` |
   | Market data could not be reached | 503 | `market_data_unavailable` |
   | A quote symbol that is not a symbol | 404 | - (the path never matches) |
   | LLM answered with an error status | 502 | `llm_failed` |
   | Model never matched the schema, after AG2's retries | 502 | `agent_response_invalid` |
   | A step failed for a reason of AG2's own | 502 | `agent_chain_failed` |
   | LLM backend unreachable | 503 | `llm_unreachable` |
   | LLM did not answer within `TAS_LLM__DEFAULT__TIMEOUT_S` | 504 | `llm_timeout` |

5. Back in the engine: a non-2xx becomes `AgentUnavailable`; `TradeSignalMapper` checks every value and throws `AgentResponseInvalidException` if one makes no sense; an answer about another instrument is refused; anything that is not `BUY` is `NoAction`. Then `PositionSizer` turns the view into a quantity — `budget = min(position headroom, cash above the buffer) × conviction tier`, then `floor(budget / the signal's own price)` — and `RiskEngine.Evaluate` **re-derives the same limits from the other direction** and can still say no. Sizing producing nothing is `NotSized`, which is kept apart from `RejectedByRisk` because one says something about the team and the other about the portfolio.

### Cross-service contract

`contracts/trade-signal.schema.json`, `contracts/quote.schema.json` and `contracts/quote-history.schema.json` are the agreement, with seven examples in `contracts/examples/`. Neither side generates the other; both read the checked-in files in their tests, so drift fails a test rather than a live run.

- `src/agents/app/domain/signals.py`: `TradeSignal`, `SignalRequest`, `TradeView`. **`TradeView` is what the last agent step is asked for** — stance, conviction, thesis, key risks, horizon. `TradeSignal` inherits it and adds what code is responsible for: the instrument, the reference price and its timestamp, and the run's identity. The engine sizes an order as `floor(budget / reference_price)`, so a model that could write that number would decide how many shares are bought.
- `src/engine/Application/Contracts/`: the same shape as DTOs, with `TradeSignalMapper` as the seam into the domain. It enforces the schema's length caps, because System.Text.Json does not read JSON Schema.
- `contracts/quote.schema.json`: what `GET /v1/quotes/{symbol}` answers. Deliberately narrower than the fact sheet - P/E and sector are read by agents, a price is read by arithmetic - and it is the one contract that carries a **currency**, because a quote can be for an instrument the engine does not price in the account's currency - which is the seam a mismatch would surface at, and finding D's remaining half. The symbol travels in the path, validated against the same pattern on both sides *before* any lookup, which is what finding B was actually about.
- `contracts/outcome.schema.json`: what the engine posts to `POST /v1/outcomes` after a sweep. The one contract whose enums are spelled as the *engine* stores them (`TradingDays`, `Measured`) rather than in this contract's usual upper case, so the two copies of a row compare directly without a mapping in anyone's head. Posting is idempotent and the batch is capped, because a request is a unit of work with a timeout rather than a bulk load.
- `contracts/quote-history.schema.json`: what `GET /v1/quotes/{symbol}/history?from=YYYY-MM-DD` answers. It is the engine's **trading calendar** as much as its price series - an outcome is measured by counting bars, so a day with no bar is a day the market was shut. `from` is required, and an empty array is a good answer rather than a 404: it means nothing has traded since that date, which the engine reads as a horizon that has not passed.
- **There is no `amount_usd` anywhere.** The agents give a view; the engine decides how much money moves. That is decision 1, and it is what bounds what a prompt injection can do.
- **The account is in SEK, and so is the universe.** `Money.DefaultCurrency` is `SEK` and `TradeSignalMapper` reads every price in the trade-signal contract as the account's currency. That works because the instruments are Swedish: there is no conversion anywhere in the system. It is a measurement decision as much as a bookkeeping one - with a krona account and dollar instruments, an outcome could not say whether a position did well because of the share or because of the exchange rate, and saying which is the whole point of stage 4. The symbol rule is 16 characters rather than 10 for the same reason: `ESSITY-B.ST` is a real OMXS30 member and `XACT-OMXS30.ST` is the benchmark. `trading.decisions` carries `reference_currency` per row and `trading.signal_outcomes` carries `benchmark_symbol`, so the USD history from before the move stays readable and does not have to be deleted.

### Layering

Both services follow a Clean Architecture / DDD layout: `Domain` → `Application` → `Infrastructure` → `Hosting`/`api`. In the engine, `Portfolio` is the aggregate root, `Money` and `Ticker` are value objects, and `RiskEngine`, `PositionSizer` and `OutcomeCalculator` are domain services. Wiring happens in `Program.cs`.

### Intended design vs. current code

- **Teams are data.** `app/application/teams.py` holds `TeamSpec` as an ordered list of `StepSpec`, validated in `__post_init__` — so an invalid team cannot be constructed and importing the module is the startup validation. `team_version` is a sha256 over the steps, the schemas' contents, the prompt files' contents, both handover flags and each role's resolved model: **include what changes what the model says, exclude what changes how it is reached.** Which team runs is `Trading:TeamId` in the engine's configuration, which is what makes stage 4's comparison possible.
- **There are two teams**, and the second is the first experiment the measurement machinery enables. `default` is the thin three-step team the baseline is of. `default-memory` is the same team with one thing added: its risk manager is shown how similar readings of the instrument turned out. Two of its three steps read **`default`'s own prompt files**, not copies, so the only difference between the two teams is the risk manager's instructions and what it is handed — a difference in outcomes then has one candidate explanation instead of three. `Trading:TeamId` stays `default` until the baseline has measured horizons worth comparing against.
- **No tools.** The new team has none: everything it needs is computed into the `FactSheet`, which is decision 5. The FastMCP server was deleted with the old path — it was only ever used in-process, and its `{"error": ...}` return shape read to a model as a successful call. Tools return when something needs data that cannot be computed in advance, and `StepSpec` will need a port of its own so AG2 stays out of the application layer.
- **The schema bounds a handover's size, not its content.** A live trace showed the portfolio manager quoting figures it never received: the analyst had repeated them in its free-text `observations`, although its prompt asks it not to. What holds structurally is the part that matters — `TradeView` has no price field. Which fields a team hands over is what stage 4 measures.
- **Memory is the journal, filtered to what has been measured.** `AnalysisMemory.recall` joins `analysis_embeddings` → `analysis_runs` → `signal_outcomes` and shows a step up to three past analyses of the same instrument, each with its stance, its thesis and how it went against the index. An analysis the engine has not yet scored takes none of those places, so **memory is empty, and says so, until a horizon has passed** — reasoning without an outcome teaches a model to agree with itself. A step asks for it with `StepSpec.sees_memory`, a flag rather than an entry in `reads`, because `reads` names schemas produced inside the run and memory comes from outside it. `TeamSpec` refuses a step that asks for memory without reading `MarketRead`, since that reading is the query. **Every team feeds memory**, including `default`, which never reads it — so a team switched on later has something to recall from its first cycle rather than from its first measured horizon a week afterwards. Both halves are best-effort: a trading cycle is waiting for an answer, and neither remembering nor recalling is worth one.
- **LLM provider:** switching providers *is* an environment variable now. `app/infrastructure/llm/provider.py` maps a `ModelSpec` to AG2's `ModelConfig` across five providers. Only the OpenAI family has actually been run: AG2 exports a placeholder for every extra that is not installed, and constructing one raises `ImportError: ... Install with "ag2[anthropic]"` - a startup failure with an install hint, since configurations are built in the lifespan. Two arguments do not survive every branch: `AnthropicConfig` has no `seed` (the settings refuse one), and `OllamaConfig` has no `timeout` (the factory warns at startup, and `openai_compatible` against Ollama's `/v1` is the route that keeps it).
- **Model quality:** `qwen2.5:14b`, at temperature 0 with a pinned seed. It replaced `llama3.2` (3B), which answered BUY, HOLD *and* SELL to the same fact sheet - finding G, and the reason the version moved. Three identical requests through the real pipeline now answer SELL, SELL, SELL at conviction 0.85/0.80/0.80: the decision is stable, the wording is not. Its Swedish is grammatically rough in a way its reasoning is not, which is an open question rather than a settled one.
- **Not a thinking model, on purpose.** `qwen3:14b` was tried first and rejected on measurement, not taste. Its reasoning cannot be turned off through the route this project uses: `/v1` accepts `chat_template_kwargs: {"enable_thinking": false}` and ignores it, `/no_think` in the system prompt does nothing, and only Ollama's native `/api/chat` honours `"think": false` - which is provider `ollama`, the one branch that carries no timeout. The cost was 25-37 s per step against 8-9 s, five times the wall clock for reasoning that is never stored: the journal holds `step_outputs`, not the `reasoning` field. And the architecture already decomposed the problem - three steps, one question and one schema each - which is the work a thinking model does inside one turn.
- **No agent is told about money.** The request carries `available_risk_budget` and `max_position_pct` — stage 4 wants to know what the engine was willing to spend — but neither reaches a prompt. Under decision 1 no agent produces an amount, so a budget is a figure it cannot act on, and a figure in a prompt is one a model starts reasoning about.
- **The engine still has no market data integration of its own**, and it no longer needs one: it asks the agent service for a price per holding. A quote that cannot be fetched, or that is older than `RiskPolicy:MaxQuoteAgeSeconds`, is left out rather than substituted - the portfolio then cannot be valued and the sizer names the holding that stopped it. Valuing a holding at what it cost would overstate a loser, raising the position allowance for everything else exactly when the portfolio had shrunk.
- **Timing:** one cycle takes about 20 s warm and 28 s cold with `qwen2.5:14b`, against 7–10 s with `llama3.2` (3B). Three LLM steps at 8–9 s each. `TAS_LLM__DEFAULT__TIMEOUT_S` is 60 and the engine's `AgentService:RequestTimeoutSeconds` is 120, so the engine gives up on a pathological chain before the agent service's own ceiling of three times 60 s.
- **Empty packages:** none left. `app/infrastructure/llm/provider.py` and `app/infrastructure/market_data/` were filled in stage 3.
- **Engine database access:** `TradingDbContext` and the `trading` schema, reached through three ports in `Application/Persistence` - `IPortfolioRepository`, `IDecisionLog` and `IUnitOfWork`. One commit per cycle, so the decision, the position change and the ledger line move together. `FindAsync` takes no id because the engine trades one account, and a second row is reported rather than silently picked. **The portfolio survives a restart**, and there is no shared mutable state left in the worker for a second ticker or a second worker to get wrong.
- **The trading calendar is the bar series.** `Engine.Domain.Outcomes` counts a horizon in bars rather than against a holiday table: a day with a bar is a day the market was open, which is right per exchange without anyone saying which, and a missing day is a fact about the data rather than an assumption. `Horizon` keeps trading days and calendar days apart - the fixed horizons are trading days, the model's own `horizon_days` is calendar days, because that is what the prompt asked it for.
- **`MeasurementWorker` is the second background service.** It sweeps at startup and then every `Outcome:SweepIntervalHours`, fetching one history per instrument plus one for the benchmark however many signals there are, and writing everything in one transaction. A horizon that has not passed gets no row and is asked about again; one that never can gets a row saying why. It writes only `signal_outcomes` and `outcome_deliveries` and never touches the portfolio, which is a test.
- **Delivery is its own step, and it retries itself.** After the sweep, `ReportOutcomesUseCase` posts everything undelivered - up to the contract's 500 - to `POST /v1/outcomes`, and writes the markers *only once the agent service has accepted them*. That order is what makes it safe to repeat: a failed post leaves no markers and the next sweep sends the same batch, and a post that succeeded before a failed commit sends it twice, which the other side ignores. The failure this order cannot produce is a measurement marked delivered that never arrived. Without the markers a thirty-second outage would cost a day of evidence, because a sweep measures only what is still unmeasured. Delivery is attempted even when the sweep failed, since the backlog is not only what today measured.
- **`OutcomeCalculator` is a pure function over bars.** A stored decision and two bar series in, a verdict out, with commission and spread subtracted as a round trip on the instrument leg only. The `Outcome` section holds what it is scored against - commission, spread, the HOLD band, the benchmark symbol and the fixed horizons - kept apart from `RiskPolicy` because a number that changes a measurement should not sit among numbers that change a decision. `MeasurementWorker` is what calls it and writes `trading.signal_outcomes`.
- **The engine stores what the engine saw.** `decisions` holds the request, the signal and the outcome - not the agent service's own working. The fact sheet and the intermediate steps are Python's data, stored on Python's side in `agent.analysis_runs` and `agent.step_outputs` against the same correlation id, so attributing a result to one agent is a join made when the question is asked. Two questions need those tables and cannot be answered afterwards without them: *replay*, running a different team over the exact inputs an old decision had, and *attribution*, which turns "team B did better" into "which step changed its mind". Widening the contract with fields the engine never reads would make it the owner of somebody else's internals, which is the shared-database problem over HTTP.
- **AG2 API version:** AG2 is used through its v1.0+ API (`from ag2 import Agent, tool`, `ag2.config.OpenAIConfig`, `await agent.ask(...)`). The pre-1.0 `autogen`/`ConversableAgent` API is not used here.
