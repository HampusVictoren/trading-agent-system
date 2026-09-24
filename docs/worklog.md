# Worklog

A running record of what has been done, what was learned along the way, and what comes next. It sits between two other documents:

- [`docs/arkitektur-roadmap.md`](arkitektur-roadmap.md) (Swedish) is the plan: the decisions, the stages, and how each stage is verified.
- [`CLAUDE.md`](../CLAUDE.md) describes the repo as it is today: commands, environment, architecture.

This file answers "where are we, how did we get here, and what is next". When resuming, read *Current state* and *Next steps* first, then the roadmap section for the next stage.

**Last updated:** 2026-09-24, after stage 4's third pull request. Decisions are stored, the
portfolio survives a restart, and the engine can price a holding it is not analysing.

## Resuming checklist

```bash
cd ~/repos/trading-agent-system            # the WSL clone; the Windows clone is stale
git status && git branch -a                # expect master plus at most one working branch
git pull --ff-only
docker compose up -d                       # trading-db; needs .env in the repo root
cd src/agents && uv sync                   # Python dependencies from uv.lock
curl -s http://127.0.0.1:11434/api/tags    # is Ollama on Windows reachable from WSL?
```

Then run the CI checks listed in CLAUDE.md before changing anything, so that a failure is known to be pre-existing.

**Both services now refuse to start on an incomplete environment**, which is deliberate. On this machine everything is already in place; on a new clone it is not:

| Needed | Where it lives | Set on this machine |
|---|---|---|
| `TAS_AGENT_API_KEY`, `TAS_LLM__DEFAULT__*` and the rest | `src/agents/.env`, gitignored - see `.env.example` | yes; **every name changed 2026-09-23** |
| `AgentService:ApiKey`, the same value | .NET user secrets, `~/.microsoft/usersecrets`, outside the repo | yes, 2026-09-20 |
| `Database:ConnectionString`, carrying the `engine_svc` password from the repo root `.env` | the same user secrets store | yes, 2026-09-23 |

`dotnet user-secrets list --project src/engine` shows whether the engine has its key, and prints the value, so do not run it where anyone can see the screen. Both sides must hold the *same* key or every cycle ends in 401.

Two things that are easy to misread as broken:

- **Docker Desktop's WSL integration can be off** while Docker itself runs on Windows. `docker` then fails in WSL, but `trading-db` may well be running and reachable at `127.0.0.1:5432` anyway, because WSL is in mirrored mode. Check the port before assuming the database is down.
- **A dead port hangs rather than refuses** in mirrored mode, so anything without an explicit timeout looks like a freeze. Both services now have those timeouts; remember it when adding a new client.

---

## Current state

- **Stage 0 done** 2026-09-19, **stage 1 done** 2026-09-20, **stage 2 done** 2026-09-21, **stage 3 done** 2026-09-23. **Stage 4 started** 2026-09-23, three of its six pull requests done. Stages 5-8 exist only as plan.
- **`master` is at PR #34.** Nothing reaches it without the three required checks passing, so what is there is green by construction.
- **Branches:** `master` plus whatever branch is being worked on.
- **There is one path now.** `POST /v1/signals` is the only endpoint that costs money, the engine calls it every cycle, and the old three-agent chain, `InvestmentProposal`, `ValidateTrade`, `RiskViolationException` and the FastMCP server are gone. Running the engine today produces real quantities at real prices, with the position cap holding across cycles.
- **Every environment variable was renamed on 2026-09-23.** The local `src/agents/.env` was renamed in place and still works; a fresh clone follows `.env.example`. Nothing outside this repo reads them.
- **The engine trades two instruments.** `Trading:Tickers` is AAPL and MSFT, so a cycle is two analyses and the quote endpoint is used in a real run rather than only by tests.
- **The `trading` schema is live and has real rows in it.** Runs on 2026-09-24 opened the account at 10 000 USD and bought one AAPL at 337.445 and one MSFT at 497.56, with a second engine process picking the same portfolio up rather than opening another. Delete them with `TRUNCATE trading.decisions, trading.orders, trading.positions, trading.portfolios RESTART IDENTITY CASCADE` if a clean baseline matters - the append-only triggers deliberately do not block that.
- **The engine now needs `Database:ConnectionString`** or it refuses to start. It is in the user secrets store on this machine, set 2026-09-23. `dotnet user-secrets list --project src/engine` prints it, so do not run that where anyone can see the screen.
- **Migrations are applied by hand, and the engine refuses to start without them.** Decided 2026-09-24: `dotnet dotnet-ef database update` stays a deploy step, but startup names the pending migrations and the command instead of failing on a missing column mid-cycle.
- **What is now impossible** rather than merely unlikely: the agents cannot name an amount (the contract has no `amount_usd`, and a test refuses one that reappears); an answer that is not the contract cannot deserialise into nulls; a position cannot be sized against cash instead of net asset value; and an order cannot be placed on a quote that is stale or dated in the future.

## Next steps

**Stage 4 - persistence and outcome measurement** (roadmap estimate: 4 days, the longest so
far). Read the stage in `docs/arkitektur-roadmap.md` before starting; it carries several
decisions that are easy to miss.

**This is the stage the project exists for.** Everything built so far produces decisions that
vanish on restart. Stage 4 stores them and measures what came of them, which is what turns
*"are the agents any good?"* from an opinion into a number - grouped by `team_version`, which
stage 3 built precisely so that this comparison would be possible.

> **Backtesting proves nothing here.** The model may already know from its training data how
> AAPL went in 2024, so a backtest flatters itself. The only honest measure is decisions logged
> *forward* in time and compared against what happened. That is why measurement starts now and
> not in stage 8.

### The five decisions, taken 2026-09-23

All four that were put as a choice went the recommended way; the fifth was stated rather than
asked. Three of them are not code yet - the cost model, the calendar and the hit definition
all land in PR 4, and are repeated here so they are not re-derived from scratch.

| Decision | Taken | Why |
|---|---|---|
| What a `decisions` row holds | The engine stores **what the engine saw**: request, signal, sizing and risk outcome, correlation id, `team_id`/`team_version`/`revisions` | The fact sheet and the intermediate steps are Python's data, stored on Python's side against the same correlation id. Attribution is then a join made when the question is asked, instead of a contract widened with fields the engine never reads - which would make the engine the owner of somebody else's internals |
| Cost model (finding F) | Commission and spread as **configured basis points per side**, applied in the outcome function, with gross *and* net stored | A real broker's fee schedule would be more precise fiction: there is no broker. Storing both makes the cost's share of the result visible instead of baked in |
| Trading calendar (finding E) | **Count bars in the instrument's own history**: five trading days is the fifth bar at or after the signal date | No holiday table to rot, right per exchange automatically, and a missing bar becomes a data fact rather than a calendar assumption. It does not answer "is the market open now" - that is stage 5's cadence question |
| Hit definition | **SPY, fixed ±2 %**, per stance, as a pure function; raw returns stored beside it | The band and the benchmark are parameters, so storing the raw returns means changing either one later does not throw away old measurements. A volatility-scaled band stays available as a refinement |
| Row version | Postgres **`xmin`** | Npgsql supports it, there is no column anyone can forget to bump, and `Portfolio` gains no field that is not domain |

### The work, as six pull requests

| # | What | Why it is its own |
|---|---|---|
| 1 ✅ | `trading` schema through EF Core: `portfolios`, `positions`, `orders` (append-only), `decisions`. `IPortfolioRepository`/`IUnitOfWork`, a reconstitution constructor on `Portfolio`. Testcontainers, and the migration tested **both ways**. | The shape is the decision. Everything after it writes rows in this form. |
| 2 ✅ | `TradingWorker` loads the portfolio per cycle and saves the decision. | This is where restart-survival becomes visible - and **the thread-safety problem disappears structurally** rather than being guarded against. |
| 3 ✅ | `GET /v1/quotes/{symbol}` on the agent service, deterministic and with no LLM, plus the engine's client for it. | It is what an outcome is measured against, and it also closes the gap left in stage 3: `PositionSizer` gets `PriceSnapshot.Empty` today, so a portfolio holding more than one instrument cannot be valued. |
| 4 | The outcome function: return over a horizon, comparison against an index, hit per stance, a horizon landing on a non-trading day. Pure functions, TDD. | Findings E and F land here. Indata and expected figure are the specification, which is exactly where writing the test first pays. |
| 5 | The scheduled job and `trading.signal_outcomes`: the fixed horizons (1, 5, 20 trading days) and the model's own, **for every signal** - including HOLD, risk rejections and everything that was never bought. A SQL view for the minimum report. | Measuring only the trades that went through measures the wrong population. |
| 6 | Python's side: Alembic for the `agent` schema, the pool leak in `memory.py`, memory wired into the pipeline, each signal's `FactSheet` stored, and `POST /v1/outcomes` so the engine can tell Python what happened. | **The database is never the integration point** - that is what separates "two schemas" from the shared-database anti-pattern. |

**After PR 5, the baseline is a deliverable, not a by-product.** The thin three-step team has to
run long enough to produce measured outcomes at the fixed horizons before anything is added to
it. Without that, nobody can say whether a news agent helped or only cost tokens - the same
reasoning that made the review rounds conditional in PR #11. A `team_version` with outcomes at
the fixed horizons is part of the stage's definition of done.

Before stage 5, turn finding D's currency mismatch into an outcome rather than a throw.

### Next up: PR 4 - the outcome function

Return over a horizon, compared against an index, and a hit defined per stance. Pure
functions, written test-first: input and expected figure *are* the specification, which is
exactly where writing the test first pays. Findings E and F land here, and the decisions
behind them were already taken on 2026-09-23 - fixed basis points per side, trading days
counted as bars, SPY with a fixed ±2 % band.

**The thing to settle first, and it is bigger than it looks: where the engine gets historical
bars.** The calendar decision says a horizon of five trading days is the fifth *bar* at or
after the signal date, and the hit definition needs the index's bars over the same period.
`GET /v1/quotes/{symbol}` answers with one price now, which is not enough for either. Three
ways out, and this is a checkpoint question rather than something to assume:

1. **A history endpoint on the agent service**, `GET /v1/quotes/{symbol}/history`, over the
   same `MarketDataProvider` that already fetches two years of bars for every fact sheet. It
   keeps market data in one service, which is the rule the whole architecture rests on. The
   cost is a second contract and a payload that is a list rather than a value.
2. **Measure when the horizon passes, from the daily quote.** The job runs daily and writes
   an outcome the day a horizon comes due, using the quote of that day. No new endpoint - but
   it cannot count trading days without knowing which days were trading days, so the calendar
   decision would have to be revisited, and a job that misses a day silently measures the
   wrong horizon.
3. **Let Python compute the outcome** and have the engine ask for it. Keeps bars where the
   data is, but puts the measurement in the service being measured, and the roadmap is
   deliberate that the engine owns `trading.signal_outcomes`.

Recommendation: (1). It is the one that leaves the calendar decision intact, and the provider
already fetches the bars.

**Also to settle:** where the cost model's basis points live. `RiskPolicy` is about what may
be traded, not about how a result is scored, so they probably want an `Outcome` section of
their own - which also keeps a number that changes a *measurement* apart from numbers that
change a *decision*.

**What its tests have to prove.** A horizon that lands on a non-trading day resolves to
something stated rather than to whatever the data happened to have. A cost model that turns a
positive gross return negative at short horizons, because that is the case finding F is about.
And a hit per stance: BUY beats the index, SELL does worse, HOLD stays inside the band -
with the raw returns stored beside the verdict, so changing the band later does not throw away
what was already measured.

## Open findings

Six findings from a review of the repository on 2026-09-20. Every one was reproduced before it
was written down, and the reproduction is the *Verified* line. They live here rather than in the
roadmap on purpose: the plan should change when a stage starts, not every time a finding arrives.

| | Finding | Status |
|---|---|---|
| A | A failed analysis is sent twice | **Fixed** in PR #17 |
| B | A ticker is interpolated into the URL without escaping | **Fixed** in PR #30, both halves |
| C | The proposal DTO accepts an answer that is not the contract | **Fixed** in PR #20 |
| D | A currency mix is reported as a bug, not as an outcome | **Fixed** in PR #23 |
| E | There is no trading calendar anywhere in the plan | **Decided** 2026-09-23; lands in stage 4's PR 4 |
| F | Outcome measurement ignores transaction costs | **Decided** 2026-09-23; lands in stage 4's PR 4 |

### A — a failed analysis is sent twice (fixed, PR #17)

**Resolved on 2026-09-20.** The client registration moved out of `Program.cs` into
`AddAgentClient`, so the rule could be tested at all, and `Retry.ShouldHandle` now fires only for
`HttpRequestException { HttpRequestError: ConnectionError }` — a connection that never came up.
A timeout is excluded for the same reason as a response: the first request may still be running
on the far side. Verified live as well: three engine cycles against a failing agent service
produced exactly three HTTP requests. Three tests in `AgentClientResilienceTests` hold it.

The same pull request also removed two hidden multipliers on the Python side: the openai client's
own `max_retries=2`, which under AG2's schema retries is up to nine calls for one decision, and
its 600 s read timeout, which made a 504 unreachable in practice.

The original finding, for context:

The resilience pipeline retries on a failing *response*, not just on a failing connection, so a
5xx costs two full analyses. A 5xx arrives after the agents have already spent 12–15 s of LLM
time, so the retry buys nothing and pays twice. From stage 4 it is worse than wasteful: one
logical cycle becomes two rows in `decisions`, `orders` and `agent_memories`, and the attempt
timeout abandons the first request without stopping the work behind it on the Python side.

This is harmless today only because the agent service answers HTTP 200 to everything, so a 5xx
never happens. **PR 4 is what makes it reachable**, which is why the policy belongs there and not
in stage 4: retry a connection-level failure, never a response. The next cycle is the real retry.
Idempotency keyed on `correlation_id` is the heavier alternative if retrying a response ever
turns out to be worth it.

*Verified:* a counting primary handler behind the configured pipeline recorded **2 HTTP attempts**
for one call that ended in `AgentServiceUnavailableException`. After the fix the same probe
records 1 for a failing response and 2 for a refused connection.

### B — a ticker is interpolated into the URL without escaping (fixed, PR #30)

**Both halves, and neither was a patch.** The interpolation went away with the endpoint: the
contract carries an `instrument` object in a request body, so there is no path to walk out of.
And `Ticker` finally has the format rule - the same pattern as the contract's `symbol`, checked
after normalising, so `aapl` passes and `../internal/shutdown` does not. All three recorded
exploits are now tests.

The format rule is the half that still matters. Stage 5's screening produces symbols from
market data rather than from `appsettings.json`, and a value object that accepts anything
non-blank is no rule at all.

The original finding, for context:

### B — a ticker is interpolated into the URL without escaping

`PythonAgentClient` builds `analyze/{ticker}` by interpolation, and `Ticker` only checks that the
string is not blank, so a ticker can walk out of the path or open a query string. Not exploitable
today, because tickers come from `appsettings.json` and the one place that parses an untrusted
ticker (the agent's answer) only compares it. It becomes real in stages 2–3, when screening
produces tickers from market data. `TradingOptionsValidator` uses the same weak check, so
configuration validation does not catch it either.

The fix is two-sided: a format rule in the value object (roadmap security point 4) *and* escaping
at the call site. The roadmap mentions only the first.

*Verified:* `"../internal/shutdown"` resolved to `http://127.0.0.1:8000/INTERNAL/SHUTDOWN`,
`"AAPL/../../admin"` to `http://127.0.0.1:8000/ADMIN`, and `"AAPL?x=1"` to
`http://127.0.0.1:8000/analyze/AAPL?X=1`. `Ticker.TryCreate` accepted all three.

### C — the proposal DTO accepts an answer that is not the contract (fixed, PR #20)

**Resolved on 2026-09-21.** Every member of `InvestmentProposalDto` is `required`, and unknown
members are refused with `JsonUnmappedMemberHandling.Disallow` — a deliberate choice rather than
the minimum: both services live in one repository and merge together, so a field the engine does
not know about is a mistake, not a version skew. The properties became init-only, because
`required` cannot go on a positional record's parameters without `[SetsRequiredMembers]`, which
would defeat the point.

No new plumbing was needed. Verified through the engine against a stub: a recorded real answer
bought, while a missing field, an extra field and the stage 3 shape were each discarded with a
warning. The two halves of the contract were also compared by hand — the pydantic model and the
DTO carry the same five fields, all required on both sides — so `Disallow` cannot reject what the
service actually sends. Stage 2 automates that comparison as a two-way contract test.

The original finding, for context:

### C — the proposal DTO accepts an answer that is not the contract

`InvestmentProposalDto` is a positional record with non-nullable `string` properties, but
System.Text.Json fills a missing member with `null` without complaining. A body that honours none
of the contract deserialises cleanly, `Action` becomes `null`, and the engine logs "No action" —
forever, quietly, at information level. It is the same failure as the HTTP 200 fallback, on the
.NET side.

Stage 3 plans exactly this migration (`stance`/`conviction` replacing `action`/`amount_usd`). If
Python ships first, the engine goes silent instead of raising. The fix is `required` members, and
the plumbing already exists: System.Text.Json throws `JsonException` for a missing required
member, which `PythonAgentClient` already translates into `AgentResponseInvalidException` and the
worker already logs as a warning. `JsonUnmappedMemberHandling.Disallow` catches unexpected extra
fields as well, but it couples the two services' release order and is a separate decision.

*Verified:* `{"ticker":"AAPL","stance":"BUY","conviction":0.8}` deserialised to
`action=<null> amount=0 reasoning=<null>`, and `{}` deserialised to `ticker=<null>`.

### D — a currency mix is reported as a bug, not as an outcome (fixed, PR #23)

**Resolved on 2026-09-21.** Mixing currencies raises `CurrencyMismatchException` rather than
`InvalidOperationException`, so a domain rule and a crash no longer look the same in a log;
`Money` normalises a currency code, so `usd` and `USD` stopped being different currencies;
`Position.AddQuantity` refuses a price in another currency instead of silently redenominating
the position; and `TradingWorker.LogOutcome` gained a `default`. The two tests that pinned the
old behaviour as known gaps now pin the rules.

**One half is deliberately still open.** The finding's point was that a currency mix becomes an
*expected outcome* once stage 5's universe holds an instrument not priced in USD. Today the
engine is USD-only, so it is a bug, and a named domain exception is the honest answer. When
stage 5 widens the universe this has to become a `RiskDecision.Rejected` rather than a throw.

The original finding, for context:

### D — a currency mix is reported as a bug, not as an outcome

`ProcessProposalUseCase` promises in its own doc comment that "every expected outcome is
returned", but `Portfolio.ExecuteBuy` reaches `Money.EnsureSameCurrency`, which throws
`InvalidOperationException`. Nothing catches it, so it lands in the worker's generic handler as
`LogError("Unexpected failure")` with a stack trace. The same is true of the insufficient-cash
guard in `ExecuteBuy`. Neither is reachable while every instrument is priced in USD, so this waits
for stage 5's wider universe — but it belongs with the `Money` currency guard that stage 2 already
plans, along with the two tests that currently pin the wrong behaviour.

While in that code: `TradingWorker.LogOutcome` is a `switch` statement with no `default`, so a
sixth result type would be logged as nothing at all. A `switch` expression would have been a
compile error instead.

*Verified:* `portfolio.ExecuteBuy(new Ticker("NESN"), 1m, new Money(100m, "CHF"))` threw
`InvalidOperationException: Cannot mix currencies USD and CHF.`

### E — there is no trading calendar anywhere in the plan (decided, not yet built)

**Decided 2026-09-23:** stage 4 counts trading days as *bars in the instrument's own history* -
five trading days is the fifth bar at or after the signal date. No holiday table to maintain,
right per exchange automatically, and a missing bar becomes a data fact rather than a calendar
assumption. It deliberately does not answer "is the market open now"; that is stage 5's cadence
question, and it may still want the explicit calendar described below.

The system analyses every 15 seconds, at night and at weekends, on stale quotes. Stage 5 fixes the
*cadence* ("the cycle follows the data"), and stage 2 checks `quote_as_of` for freshness, but
market hours, holidays and half days do not exist as a domain concept. For a system whose whole
goal is short-term movement, a trading calendar is a first-class domain concept, not a detail —
and "is the market open" is a pure function, so it is cheap to get right.

### F — outcome measurement ignores transaction costs (decided, not yet built)

**Decided 2026-09-23:** commission and spread as configured basis points per side, applied
inside the outcome function, with gross *and* net return both stored so the cost's share of the
result stays visible. A real broker's fee schedule would only be more precise fiction while
there is no broker.

Stage 4 measures return against an index from `reference_price`, with no commission, no spread and
no slippage against a quote that may be minutes old. Over short horizons that cost is often the
entire difference between a positive and a negative edge, so the measurement will systematically
overstate the agents — in precisely the number that is supposed to decide whether the project is
worth continuing. A cost model belongs in the outcome function from the start; it is a pure
function and therefore an ideal test-first target.

### Considered and rejected for now

- **Start persisting decisions before stage 4, to stop losing evidence.** The argument is that a
  day not recorded cannot be reconstructed without lookahead bias, which is true. But what runs
  today is llama3.2 3B on one hard-coded ticker, with no sell logic and a HOLD-lie on failure.
  Recording that is recording noise, and a worthless baseline is worse than none, because it is
  tempting. The data becomes worth keeping once PR 4 makes failures honest and stage 2 makes the
  `FactSheet` deterministic.
- **"The roadmap may only change as a result of something that was built."** The right instinct —
  five commits have changed only the roadmap — but too strict a rule. PRs #10 and #11 came from the
  goal itself being sharpened (buy *and* sell), and a plan should follow a changed goal. Writing
  findings down here instead of in the roadmap is the softer version of the same idea.

## Open decisions and loose ends

- **Orphan Docker volume** `trading-agent-system_postgres_data`, left from the original compose file. It holds no user tables (checked on a copy). Deleting it is the owner's call: `docker volume rm trading-agent-system_postgres_data`.
- **Dependabot:** the first uv run failed (run 35465220355), but every Dependabot run on 2026-09-19 succeeded, so it looks like a one-off. Nothing to do unless it returns.
- **`github-advanced-security` fails on most pull requests** (#4, #5, #6, #10, #11, #13, #14 and on through stage 1; it passed on #7). It is GitHub's Copilot "Code scanning AI findings" job, it is not a required check, and it blocks nothing — but a check that is usually red trains you to ignore red. Decide whether to switch it off under *Settings → Advanced Security*.
- **Swedish outside the agreed exceptions:** the `description` in `src/agents/pyproject.toml`, the `Field` descriptions and validator message in `app/domain/models.py`, and the strings `MemoryStore.search` returns. The log message in `team.py` was translated in stage 1. The rest are all read by the model — in the JSON schema, in the retry error, or as a tool result — so they arguably count as prompt text. `models.py` is replaced in stages 2-3 anyway. Decide whether "prompt text" means the `ag2/` directory or wherever the words reach the model.
- **Known gaps pinned by tests,** documenting current behaviour rather than asserting the right one: `Money` treats `usd` and `USD` as different currencies, and `Position.AddQuantity` adopts the incoming price's currency. Stage 2 gives `Money` a currency guard, together with finding D.
- **Empty packages:** `app/infrastructure/llm/` and `app/infrastructure/market_data/` hold only `__init__.py`. `app/application/` now holds the error vocabulary.
- **The engine reads only the status code, not `error_code`.** 502, 503 and 504 all become `AgentUnavailable`, with the status in the log message. The engine's decision is the same in all three cases, so this was left alone in stage 1 — but it is a choice, not an oversight, and worth revisiting when the contract is versioned in stage 3.
- **Uvicorn prints two lines before startup that are not JSON**, because `configure_logging()` runs in the lifespan. Moving it to import time would catch them but would also reconfigure logging in the middle of pytest. The real fix is a `--log-config` at deploy time, which belongs to stage 6.
- **The contract carries no currency.** Every price in it is USD and so is the portfolio. Fine until stage 5 widens the universe, and noted in `TradeSignalMapper`. It goes with finding D's remaining half.
- **`PositionSizer` and `RiskEngine.Evaluate` are registered but nothing resolves them.** Deliberate: registering them means the options-to-domain mapping is covered by `ValidateOnStart` now, and stage 3 becomes wiring rather than new code.
- **`MemoryStore` still is not wired into the flow.** The lifespan builds one and nothing uses it. The roadmap puts a `search_history_tool` on `RiskManager` and a `save` after each cycle in stages 2-3.
- **One row with ticker `TEST`** sits in `agent.agent_memories` from the smoke test on 2026-09-20. Harmless; delete it if a clean table matters.
- **The first account was opened on 2026-09-24** at 10 000 USD, and `trading` now holds real rows from a live run against `llama3.2`. They are a smoke test, not a baseline: the baseline starts when the outcome job in PR 5 exists.
- **`orders.placed_at` is a shadow property** filled by the database's `now()`. It is audit metadata today; stage 5 counts a holding period from the last purchase, and that is when it becomes domain data and has to come from the engine's injected clock instead.
- **Quotes share the analysis client's resilience policy**, which does not retry a failing response. That rule was written for a call costing 12-15 s of LLM time and is stricter than an idempotent GET needs; the cost of leaving it is one cycle without a price for one holding, and the cost of a second typed client is a second place for the key and the timeouts to drift. Revisit if missing quotes ever show up in the decision rows.
- **A quote for a symbol that is not a symbol answers 404, not 422.** FastAPI rejects it at routing, before validation, so it never reaches the error vocabulary. Honest but inconsistent with every other refusal in the contract; worth a `Path` converter or a catch-all route if the difference ever matters to a caller.
- **Nothing translates a duplicate `correlation_id`.** `UnitOfWork` turns EF's concurrency exception into `ConcurrentChangeException`, but a unique-index violation still surfaces as `DbUpdateException` and lands in the worker's general handler with a stack trace. That is arguably right - the ids are fresh Guids, so a duplicate is a bug - but it has never been seen, so it has never been read.

---

## Working agreements

How the owner wants the work done. Apart from the language rule, none of this is in CLAUDE.md.

- **A decision checkpoint before any code.** List the concrete choices that could go either way, with a recommendation for each, and wait for a yes. Agreed 2026-09-21: the owner wants to see a decision before it becomes code, not read about it afterwards.
- **Two to four commits per pull request**, against `master`, grouped so the diff stays readable in a few minutes. Claude chooses the grouping. This replaced the earlier one-commit-per-PR rule on 2026-09-21, which produced too many merge rounds. Only `master` and the current working branch exist; a branch is deleted on both sides after its PR merges.
- **Motivate every change** and keep the hand-over short: what it does and why it matters, details on request. Stage 1's PR #17 was the counter-example - 21 files and +662 lines, with several design decisions explained only afterwards.
- **Build and test before every push:** every CI step, locally.
- **Language:** replies to the owner are in Swedish. Everything in the repo is English, except the agent prompts and the roadmap.
- **No `gh` CLI or token in WSL.** Pull requests are opened from pre-filled compare links: `https://github.com/HampusVictoren/trading-agent-system/compare/master...<branch>?expand=1&title=...&body=...`.
- **Attribution:** commits written with Claude end with `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`, and PR descriptions end with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- **Secrets are never printed,** not even to a terminal, and `.env` files are never committed. No volume is deleted and nothing is force-pushed without the owner's explicit OK.
- **More than one Claude session can work in this clone** (PR #10 came from a second session). Check `git status` and the current branch before starting. A staged file goes into whichever session commits next.

---

## Stage 0 log (2026-09-18 → 2026-09-19)

### Before stage 0

- **2026-09-17:** initial structure: the engine's DDD layout, the agent service and docker-compose.
- **`dc59b94` *Add MCP*:** the FastMCP server with `get_stock_quote`, backed by yfinance.
- **PR #1 `add-vector-db`** (merged 2026-09-18):
  - `0df01fd` pgvector memory: `memory.py` with `get_embedding`, `save_memory` and `search_past_memories`, using 768-dimensional `nomic-embed-text` embeddings.
  - `2aecd70` WSL/Ollama connectivity (`.env` loaded automatically, `127.0.0.1` everywhere), structured output with retries for the PortfolioManager, and a fallback of HOLD 0 USD instead of BUY 250 USD. Added CLAUDE.md. The underlying bug was WSL's NAT networking, fixed by switching WSL to mirrored mode.
  - `1302d80` the architecture review and roadmap, with decision 1 (the engine owns the money) and decision 2 (the engine owns trading data, Python owns memory).
- **`ed9c30f`** (pushed straight to `master`, before the ruleset existed): testing moved from the last stages to stage 0, and the contract moved before the pipeline.

### The work of stage 0

**PR #2 — repo hygiene** (`c4500fe`, *Make the repo reproducible and switch the codebase to English*)
- The packaging bug: `uv_build` assumed a `src/` layout, so the wheel held only a 52-byte stub, and the service ran only because the working directory was `src/agents`. Fixed with `module-name = "app"` and `module-root = ""`.
- Deleted `requirements.txt` (it contradicted `pyproject.toml`), four empty placeholder modules, the unused `stock_client.py` and the dead `ConnectionStrings` section.
- Added `TradingSystem.slnx`, `Directory.Build.props` with `TreatWarningsAsErrors`, `.editorconfig`, and ruff and mypy configuration.
- Comments, log messages and exception messages switched to English.

**PR #3 — test harness** (`3d9a8ab`, *Add the test harness and the first 30 tests*)
- `tests/Engine.Tests` on xunit v3 with Shouldly and NSubstitute: 19 tests over `Money`, `Ticker` and `Position.AddQuantity`.
- `src/agents/tests` on pytest: 11 tests that pin the `InvestmentProposal` contract.
- Two tests document known gaps (see *Open decisions*).

**PR #4 — CI** (`db3d154`, *Add CI with secret scanning and a packaging regression guard*)
- Four jobs: `Engine (.NET)`, `Agents (Python)`, `Secret scan` (gitleaks, with the binary checked against a SHA-256 pinned in the workflow) and `Vulnerability scan (advisory)` (`dotnet list package --vulnerable` and pip-audit).
- A guard that fails if `app/` is missing from the built wheel. It was verified to fail when the packaging bug is put back.
- Actions pinned to commit SHAs. Dependabot opens grouped weekly PRs for github-actions, nuget and uv.
- The PR also carried `2aa77d5` *Roadmap update* from a second session: decision 4 at the time (an instrument object and `TeamSpec`) and "no derivatives before the last stage".

**PR #6 — database** (`3c75c20`, *Let compose own the database and give each service its own role*)
- Compose owns `trading-db`: `pgvector/pgvector:0.8.6-pg16`, bound to `127.0.0.1:5432` only, with a healthcheck.
- `db/init/01-schema.sh`: schema `trading` owned by `engine_svc` and schema `agent` owned by `agent_svc`, with no access across them and nothing in `public`. Passwords reach SQL as psql variables.
- Random passwords in two gitignored `.env` files. The one in the repo root is for compose; `src/agents/.env` holds only the agent's `DATABASE_URL`.
- `memory.py` lost its `postgres:postgres` default and fails fast without `DATABASE_URL`.
- Verified: 8 of 8 isolation checks over TCP, and the memory smoke test as `agent_svc`.

**PR #5 — Dependabot** (merged after #6)
- *Bump the python group*: `mcp` 1.30 → 2.2 and `uv_build`, plus, not mentioned in the title, `fastmcp` 3.4.7 → 4.0.5.
- Green CI was not enough, because the MCP path has no tests. Verified by hand: `@mcp.tool()` still returns a plain function, `get_stock_quote('AAPL')` returned real data, and the full flow produced a real decision, not a fallback, in 13.7 s.

**Branch protection:** the ruleset *Protect master* (id 23708635), created in the GitHub UI.
- A pull request is required, with 0 approvals (a solo project). Deleting and force-pushing `master` is blocked.
- Required checks, with the branch up to date: `Engine (.NET)`, `Agents (Python)` and `Secret scan`. The vulnerability scan is advisory and not required.

**Verification** (PR #7, closed unmerged, plus a direct push):
- PR #7 broke one assertion (150 → 151). `Engine (.NET)` went red, the other checks stayed green, and the merge was blocked without any option to bypass it.
- An empty commit pushed straight to `master` was rejected with `GH013: Repository rule violations found`, citing both "Changes must be made through a pull request" and "3 of 3 required status checks are expected".

### After stage 0

- **PR #8 `roadmap-follow-up`** (`ba26bbe`): applied the review of `2aa77d5`. `TeamSpec` split into "the contract now, the internals when needed", which became decision 3; the System.Text.Json discriminator pitfall; instrument equality; stage 0 marked as done. CLAUDE.md's stale list of leftovers replaced.
- **PR #9 `ci-pin-runner`** (`7db8047`): runners pinned to `ubuntu-24.04`, because `ubuntu-latest` moves to Ubuntu 26 from 2026-10-19. The vulnerability job's uv cache turned off, because it raced the agents job for the same cache key.
- **PR #10 `roadmap-handoffs-screening`** (`8d102d1`, from a second session): the goal became "find buy and sell opportunities in stocks that should do well in the short term". Decision 4 became typed handoffs through `StepSpec.reads` instead of a shared `MemoryStream`, and decision 5 that code computes a `FactSheet` and screens while the LLM only interprets a shortlist. Stage 3 gained review rounds and `team_version`, stage 4 measures outcomes from the first decision, and a new stage 5 covers screening and selling; the later stages were renumbered (now 0–8). See *Open decisions* for the review.
- **PR #11 `roadmap-pr10-follow-up`** (`a2fe1b2`): applied the review of PR #10. Review rounds became a condition instead of a part of stage 3, so the first measured `team_version` is a baseline without them. Stage 5 gained deterministic exits in `RiskPolicy` (a stop-loss, a time limit counted from the last purchase, and a minimum holding period), and its cycle now follows the data: an instrument is analysed only when its `FactSheet` has changed since its last signal. Stage 4 measures every signal at fixed horizons and defines a hit per stance, and stage 5 reports purchases against the shortlist average. Verified along the way that AG2 runs `ask()` without `stream=` on a fresh `MemoryStream`, so decision 4 holds.
- **CodeQL default setup** was switched on in the GitHub UI for Python, C# and Actions. Its first run was on `dfe20e8`.

---

## Stage 1 log (2026-09-20, done)

- **PR #12 `deps-pin-fastmcp`** (`e0bc847`): `fastmcp>=4.0.5,<5`, so the next major version arrives as its own visible Dependabot PR, and `mcp[cli]` removed — the code never imports `mcp`, and fastmcp pulls it in anyway. The lockfile lost only `typer` and `shellingham`. Verified with a full run against Ollama rather than only the tests, because the MCP path has none and the HTTP 200 fallback hides a broken chain.
- **PR #13 `stage-1-trade-decision-result`** (`bb114aa`): `ProcessProposalUseCase` returns a `TradeDecisionResult` instead of throwing or logging. It is a closed hierarchy — a private constructor keeps every case in one file — with `Executed`, `RejectedByRisk`, `NoAction`, `InvalidResponse` and `AgentUnavailable`. `Ticker.TryCreate` parses an agent's answer without throwing, and an answer about another ticker is refused: without that check a model replying "TSLA" to a question about AAPL made the engine buy TSLA. The worker now picks a log level per outcome, so a risk rejection is information rather than an error with a stack trace behind it. Written test-first: 12 tests red before the code existed.
- **PR #14 `stage-1-typed-options`** (`c962c2e`): `AgentServiceOptions`, `RiskPolicyOptions` and `TradingOptions`, each `ValidateDataAnnotations().ValidateOnStart()`, plus a `TradingOptionsValidator` for the rule data annotations cannot express. **None of them has a default value**, so a mistyped section stops startup instead of running on a guessed risk limit. `AddStandardResilienceHandler` takes its attempt timeout from configuration and `HttpClient.Timeout` is `InfiniteTimeSpan`, so the pipeline owns the time — which matters more than it looks, because in WSL's mirrored networking a dead port hangs instead of refusing. The worker moved to `IServiceScopeFactory` and loops over the configured tickers.
  - A live run caught a regression the tests did not: a resilience timeout reached the worker as `Polly.Timeout.TimeoutRejectedException` and was logged as an unknown bug, undoing what PR #13 had just fixed. Corrected by translating transport failures inside `PythonAgentClient` into the two exceptions the port declares, and by demoting Polly's own telemetry to information. Afterwards the same run logged zero entries at error level.
- **Review of the repository (2026-09-20).** Six findings reproduced and recorded under *Open findings*; two suggestions rejected with reasons. Written up in PR #15, which also brought this file onto `master`.
- **PR #16 `stage-1-python-settings`** (`04ad352`): `app/settings.py` with pydantic-settings. Every value is required, because here the silent defaults were dangerous rather than merely wrong — a missing `LLM_BASE_URL` meant "call OpenAI's cloud API with your key" and a missing `LLM_MODEL` meant `gpt-4o-mini`. `SecretStr` on the two secrets, `HttpUrl` on the two base URLs, read through a cached `get_settings()` so that importing a module no longer depends on a `.env` file. A FastAPI `lifespan` builds the httpx2 client, the embeddings client, the AG2 configuration and an asyncpg pool once, and `memory.py` became a `MemoryStore` that is handed them — which is what fixes the clients being bound to whichever event loop imported the module first.
  - `httpx2` replaced `httpx` as a declared dependency. It is pydantic's successor to httpx, and `openai` 3.x requires it, so the `type: ignore` in `memory.py` disappeared rather than moving. **Nothing was added to or removed from the lockfile**: all 112 packages were already installed, and only the declarations changed.
  - The pool opening a connection at startup means an unreachable database now stops the service. asyncpg waits 60 s by default and a dead port hangs in WSL, so the connect timeout is 10 s and the failure names `docker compose`.
- **PR #17 `stage-1-honest-errors`** (`7ca7ad2`): the reason the stage exists. `team.py` raises instead of falling back to HOLD, `app/application/errors.py` names the failures, and `app/api/errors.py` maps them to 422, 502, 503 and 504. A response body is `{error_code, correlation_id}` and nothing else — never `str(exception)`, which is how hostnames and roles leak to a caller. A ticker is validated against a pattern, so a malformed one costs 422 rather than 12-15 s of LLM time. `X-Correlation-Id` is read or invented and put in every log line; logs are JSON. `/health` is liveness, `/ready` checks the database and the LLM backend. Carries finding A. 20 HTTP-contract tests pin the behaviour before `team.py` is rewritten in stages 2-3.
- **PR #18 `stage-1-api-key`** (`30271ef`): `X-Api-Key` on `/analyze`, compared with `hmac.compare_digest`. A missing key and a wrong key both answer 401, so the response says nothing about which it was. The dependency sits on the router rather than the route, so a route added later is closed by default; the two probes are defined outside it and stay open. The engine's key is required and validated at startup but deliberately absent from `appsettings.json`: locally it comes from .NET user secrets, outside the repository.
- **Stage 1 closed 2026-09-20.** All twelve points in the roadmap's list are done, and the stage's own criterion was verified end to end in both halves. 52 Python tests and 61 .NET tests, from 11 and 19 when the stage started.

---

## Stage 2 log (2026-09-21, done)

Four pull requests, and **not one of them changed what the running system does.** That was the
point: the only component that touches money should be finished and proven before the agents
are allowed to talk to it.

- **PR #21 `stage-2-contract`** (`8498948`): the contract as files. `contracts/trade-signal.schema.json` holds the signal at the root and the request under `$defs`, with five examples. The signal carries a view and **no amount** - sizing is the engine's job, which is what keeps a prompt injection from becoming a large order. `instrument` is a discriminated union so a derivative becomes a new variant rather than a breaking change; `run` carries `team_id`/`team_version`/`revisions` that the engine stores and never decides on; the request carries the limits so the agents reason inside them. Wire types in `Application/Contracts`, domain types in `Domain/Signals`, and `TradeSignalMapper` as the seam - System.Text.Json checks the shape, not whether a value makes sense.
  - The contract test reads the checked-in files with the same options object production uses. It holds that the discriminator-last example reads *and* that reading it without `AllowOutOfOrderMetadataProperties` throws, that an added `amount_usd` is refused, and that the schema's `required` list matches the DTO's fields. Renaming `horizon_days` on the engine's type turns six tests red, so the suite is not vacuous.
- **PR #23 `stage-2-money-portfolio`** (`3f72bd5`): `Money` gained `Multiply`, `Divide` and `Min`, and a normalised currency code. `Portfolio.Value` returns `Valued` or `PriceMissing`: the engine cannot price a holding it is not analysing until stage 4 adds a quote endpoint, and valuing one at what it cost would overstate a loser - raising the position limit exactly when the portfolio had shrunk. Closed finding D.
- **PR #24 `stage-2-position-sizer`** (`d5233ce`): `PositionSizer`, written as a table of sixteen rows before the code existed. `budget = min(position headroom, cash above the buffer) x conviction tier`, then `floor(budget / the signal's own price)`. `ConvictionTier` is deliberately discrete - none, half, all - because an LLM's conviction is not calibrated and scaling an amount by it reads a precision that is not there.
- **PR #25 `stage-2-risk-decision`** (`001c711`): `RiskEngine.Evaluate` returns a `RiskDecision` instead of throwing, and **re-derives the limits the sizer already enforced** rather than trusting them. One test feeds the sizer's output straight into the gate: they compute from different directions, so they have to agree or one is wrong. It also refuses a quote that is too old *or dated in the future* - `quote_as_of` comes from the agent service, so without that check a future date would make every quote look fresh for ever.
- **Stage 2 closed 2026-09-21.** 154 .NET tests, from 71 when the stage started. The old path is untouched and still throws; stage 3 deletes `ValidateTrade` and `RiskViolationException` with the old endpoint.

**A habit that started here: check that a test can fail.** Every table in this stage was mutated
deliberately - measuring headroom against cash, removing the cash buffer, rounding up, trusting
the sizer, dropping the future-date check - and the failures were counted. A table that cannot go
red is decoration.

---

## Stage 3 log (2026-09-21 ->, in progress)

**What the stage delivers is the experiment cycle, not the team.** The roadmap's own framing,
added in PR #22, is what drives the order: the machinery is built to be cheap to replace, and
which team is actually good is settled by stage 4's outcomes rather than guessed at now.

**Five decisions were taken before any code was written.** Four were put as a choice with a
recommendation; the fifth came out of reading the engine's DTOs and is the one that changed
the most.

| Decision | Taken | Why |
|---|---|---|
| `FactSheet`'s shape | Numbers and categories, no free text | `longBusinessSummary` was 300 characters of external text going straight into a prompt, and there is no number in it. A news step will bring prose under its own schema and its own caps. |
| Environment variables | `TAS_` prefix, nested `TAS_LLM__*` | `OPENAI_API_KEY` is read by openai's own SDK, and a shell value beats `.env`. A real cloud key in a developer's shell would quietly become this service's key. |
| The analyst's tools | None in stage 3 | The fact sheet holds everything it needs, so the tool was a second route to the same numbers - and its `{"error": ...}` shape looks to a model like a successful call. |
| Where things live | Split across layers | The pipeline is a use case, the agent construction is AG2-specific, the teams are data. `app/domain` stays free of AG2. |
| **What the model is asked for** | Only the view | Not offered as a choice, because it is forced: `team_version` is a startup-computed hash. But the line fell further than that - see below. |

**The fifth decision is the one worth remembering.** The engine sizes an order as
`floor(budget / reference_price)`. A model asked to fill in `reference_price` therefore decides
how many shares are bought - the same hole decision 1 closed for the amount, one level down. So
the contract is split: `TradeView` is what the last step is asked for (`stance`, `conviction`,
`thesis`, `key_risks`, `horizon_days`), and `TradeSignal` inherits it and adds what code is
responsible for - the instrument from the request, the price and its timestamp from the fact
sheet, the run's identity from the team. Three tests exist only to guard that line.

- **PR #26 `stage-3-contract-models`** (`8ec85bd`): the Python half of `contracts/trade-signal.schema.json`, and the `TradeView` split above. The engine has read the checked-in examples since PR #21; now both sides do, so drift fails a test rather than turning up as a 502. Also capped the free-text fields **in the schema itself** - `thesis` at 2000 characters, `key_risks` at 5 of 300 - because the roadmap's principle is that the schema is the ceiling on what is handed over, and an uncapped field is no ceiling. The caps live once per side and a test compares them.
- **`stage-3-fact-sheet`** (open): three commits.
  - `facts.py`, written test-first: a known price series has one right answer for a return and a volatility, which is the case where writing the test first pays. Returns count back to a **calendar date** so a holiday week does not turn a one-month return into a five-week one; volatility counts **bars**, because it is a property of the observations. `None` rather than zero throughout - a share with no earnings has no P/E, and zero would be a claim. The price passes through unrounded, since it becomes `reference_price` and then an order size; everything derived is rounded to a basis point, which is tokens saved in every prompt that reads the sheet.
  - `MarketDataProvider` in `app/application/ports.py`, implemented as `CachingMarketData` (TTL, timeout, `asyncio.to_thread`, error translation - all of it tested against a fake) over `yfinance_source` (the only file that imports yfinance, and the only place its payload is whitelisted). It **raises** rather than returning `{"error": ...}`. An unknown symbol is 422 `instrument_not_found`; a source that is down is 503 `market_data_unavailable`, because a typo and an outage call for different fixes.
  - `MarketRead` and `RiskAssessment` as the typed handovers, categories rather than scores: a model choosing between three labels is more reliable than the same model producing a calibrated number. Neither repeats a figure from the fact sheet, because copying a number through a model is how it gets changed on the way.

- **`stage-3-provider-factory`** (open): four commits. Switching to Claude or Grok is now an environment variable. `ModelSpec` and `LlmSettings` describe a model; `app/infrastructure/llm/provider.py` turns one into AG2's own `ModelConfig`, with no wrapper type of this project's own.
  - **The lazy import the roadmap asked for turned out to be AG2's.** `ag2.config` exports a `Mock` placeholder for every provider whose extra is missing, and constructing one raises `ImportError: ... Install with "ag2[anthropic]"`. Because configurations are built in the lifespan, that is a startup failure carrying an install hint. mypy still reads the real dataclass signature behind the placeholder, so the calls are type-checked even though nothing can run them.
  - **The four configurations do not take the same arguments**, which is why the factory is more than a lookup, and it changed two decisions. `AnthropicConfig` has no `seed` field, so the settings refuse a seed for that provider rather than dropping it - a dropped seed looks like reproducibility. `OllamaConfig` has neither `api_key` nor `timeout`, and the timeout is what makes a 504 reachable at all, so that branch warns at startup and points at `openai_compatible` against Ollama's `/v1`, which is the route this project already takes.
  - **`ModelSpec` has no defaults**, against the roadmap's sketch of it, which had several. The rule is settings.py's and the reason is the same one level down: with per-role overrides a mistyped role name falls back to the default, and a default model means the service then runs a model nobody chose.
  - **The role check is the thing that makes an override safe.** `LlmSettings.for_role` cannot know which roles exist, so `TAS_LLM__ROLES__PORTFILIO_MANAGER__MODEL` would start the service, run the default, and show no symptom but a wrong answer. `build_model_configs` takes the roles that exist and refuses anything else. It takes them as an argument rather than importing them, so PR 4 swaps the source from the current team's three names to `TeamSpec` without touching the file.
  - Verified live, not only in tests: the service starts on the renamed variables and answers `POST /analyze/AAPL` with BUY in 18 s; a mistyped role stops startup naming both the typo and the real spellings; an override reaches only its own role.

- **`stage-3-team-spec`** (open): five commits, and the stage's largest. A team is now data - an ordered list of `StepSpec`, each naming who runs, what it may read and what it must produce.
  - **`reads` is where decision 4 becomes visible.** Looking at the spec tells you exactly what each agent sees, which is the thing that used to be buried in f-strings. What the portfolio manager does *not* read is the point: it gets the analyst's reading and the risk manager's assessment, never the fact sheet, so it weighs two judgements and is handed no figure at all.
  - **The structure is checked in `__post_init__`**, so an invalid team cannot be constructed and importing the module *is* the startup validation. Refused: a team that does not end in `TradeView` (identity, not `isinstance` - a subclass would serialise fields the engine's DTO forbids), two steps producing the same schema, a repeated role, and a step reading a later step's result or its own.
  - **Nothing in `app/domain/` or `app/application/` imports AG2.** A step runs through the `StepRunner` port, and `Ag2StepRunner` is the only file that catches an openai exception - which is also the only place the mapping can go wrong. The ordering of its `except` clauses is itself a test: `APITimeoutError` subclasses `APIConnectionError`, so caught the other way round a timeout becomes a 503 rather than a 504.
  - **The message is built by the pipeline; the prompt file holds the instructions.** No placeholders, so nothing can be injected through a template and the file is the whole of a role's instruction - which is what makes it meaningful to hash. The risk budget and the position cap reach no step at all: under decision 1 no agent produces an amount, so a budget is a figure it cannot act on. Both stay in the contract, because stage 4 will want to know what the engine was willing to spend.
  - **`team_version` is sha256 over a canonical payload**, never `hash()`, which is salted per process. The rule: include what changes what the model says, exclude what changes how it is reached. So the steps, the schemas' contents, the prompt files' contents and each role's resolved provider/model/temperature/seed are in; `base_url`, `timeout` and `api_key` are out. A test runs it in two subprocesses under different `PYTHONHASHSEED` values, and another asserts the key appears nowhere in the payload.
  - Dropped from the roadmap's sketch: `StepSpec.model`. `LlmSettings.roles` already overrides a step's model from the environment, and two ways to change the same thing is one too many. `StepSpec.tools` is also absent - the new team has no tools, and an AG2-typed field would drag AG2 into the application layer. It returns when tools do, and it will need a port of its own.
  - Live against Ollama and yfinance: AAPL in 10.1 s and MSFT in 6.8 s, both valid signals at version `6c6da0e6edad`, `reference_price` from the fact sheet. An unknown `team_id` is refused by name.

- **`stage-3-switch-over`** (PR #30): four commits, and the only one in the stage that changes what the system does. `PositionSizer` and `RiskEngine.Evaluate` - built and tested in stage 2, called by nobody - are finally wired in.
  - `POST /v1/signals` replaces `POST /analyze/{ticker}`. Versioned in the path from the first day it exists, because the two services deploy separately. The old chain, `InvestmentProposal`, `InvestmentProposalDto`, `ValidateTrade`, `RiskViolationException` and the FastMCP server are all gone; `RiskEngine` now holds no state at all.
  - **`NotSized` is a new outcome**, kept apart from `RejectedByRisk`. "The conviction was below the floor" says something about the team; "the quote is stale" says something about the portfolio. Stage 4 needs to tell them apart.
  - **`Trading:TeamId` is configuration**, with no default. That is what makes the experiment cycle an experiment.
  - **The correlation id is generated per cycle and logged before the call**, and set on the header from the request body so the two cannot disagree. One cycle can now be followed across both services.
  - Finding B closed, both halves - see the finding for why the format rule is the half that still matters.

**Stage 3 closed 2026-09-23.** 241 Python tests and 172 .NET, from 52 and 154 when the stage
started. The live run, four cycles against Ollama and yfinance:

```
Requesting analysis for AAPL as 35d48b67-...
No order for AAPL: the budget of 250.000 USD does not reach one share at 336.85
Bought 1 AAPL for $336.85. Cash left: $9663.15.
No order for AAPL: the budget of 163.1500 USD does not reach one share at 336.85
No action for AAPL: the agents answered HOLD.
```

Every line of that is new. The price is a real quote rather than a proposed amount. The
conviction tiers are visible - 250 is half the 500 allowance, 500 is all of it. **The position
cap holds across cycles**: 163.15 is what was left of the 5 %, and the old path had no such
limit at all, because it measured against cash and never against the position. Zero
error-level entries on either side.

**A limit of the design, found by tracing a live run.** The portfolio manager's thesis quoted
figures it never received: they reached it through the analyst's free-text `observations`,
which repeated them although the prompt asked it not to. **The schema bounds the size of a
handover, not its content.** What holds structurally is the part that matters - `TradeView`
has no price field, so `reference_price` came from the fact sheet and nothing a model wrote
could become an order. Left as it is on purpose: which fields a team hands over is what stage
4 measures, and `team_version` is what makes changing it safe.

**Mutation testing earned its keep here.** Dropping the `result is None` guard in the AG2
runner turned *nothing* red - with a `response_schema`, an empty answer raises
`ValidationError` instead, so the None branch is unreachable through `TestConfig`. It is still
reachable through the type AG2 declares, so a test now stubs at the AG2 boundary. Without the
guard the literal `None` would be serialised into the next step's data block as a result.

**The generic test is the one to copy elsewhere.** Rather than asserting a cap per field, it walks
every schema an agent fills, resolves pydantic's `$defs`, and asserts that no string anywhere in it
lacks a `maxLength`. A field added later is covered without anyone remembering. A second test feeds
the walk a deliberately leaky model, so a bug in the walk cannot make the first one vacuous.

**Counts:** 241 Python tests and 172 .NET, from 52 and 154 when the stage started. The Python
number went down at the end: the old contract's tests left with the old contract.

**Mutation counts**, continuing the habit from stage 2: asking the view for the price 3 red,
dropping `extra="forbid"` 1, changing a cap in the schema file 2, allowing a naive timestamp 1;
off-by-one in the volatility window 1, counting rows instead of calendar days 1, dropping the
rounding 1, leaving the live price out of the 52-week high 1, population instead of sample
deviation 1, removing the oldest-first guard 1; caching for ever 1, dropping the cache prune 1,
folding an unknown symbol into an outage 1, calling the source without `to_thread` 1; dropping
the unknown-role check 2, not building the overrides at startup 2, turning the client's own
retries back on 1, allowing a seed for Anthropic 1, allowing `openai_compatible` with no
`base_url` 2, removing the `TAS_` prefix 10; giving every step everything produced so far 2,
taking the price from somewhere other than the fact sheet 1, falling back to the first team 2,
telling every step about the holding 1, putting the budget in the message 1, dropping the rule
that the last step produces `TradeView` 1, catching `APIConnectionError` first 2, letting
`ValidationError` bubble 1, leaving the step's name out of the message 1, dropping the None
guard 1 (0 before the test that covers it was written); dropping the check that the answer is
about the right instrument 1, skipping the risk gate 1, removing the mapper's length guard 1,
dropping `Ticker`'s format rule 7, treating HOLD as an unsized buy 2.

---

## Stage 4 log (2026-09-23 ->, in progress)

**This is the stage the project exists for.** Everything before it produced decisions that
vanished on restart. The five decisions that had to be taken first are recorded under *Next
steps* rather than here, because they are still being spent.

- **PR #32 `stage-4-persistence`** (`82ee168`, four commits): the `trading`
  schema through EF Core 10 and Npgsql - `portfolios`, `positions`, `orders`, `decisions` -
  plus `IPortfolioRepository`, `IDecisionLog` and `IUnitOfWork` over it. Nothing calls any of
  it yet; the shape was the point.

  - **The aggregate gained a way back in and a ledger.** EF rebuilds a stored portfolio
    through a private constructor and writes to the backing fields. `ExecuteBuy` now returns
    the `Order` it appended, built inside the aggregate so that cash moving and the record of
    it cannot come apart, and appended *after* the balance check so a buy the cash cannot
    cover leaves nothing behind. The collection is called `NewOrders` because that is what it
    holds - the repository does not read the stored ledger back, since nothing in the domain
    needs it and loading every order ever placed to append one more grows with the account's
    history.
  - **Four rules were made structural rather than written down:** `orders` and `decisions`
    refuse `UPDATE` and `DELETE` through a trigger; `positions` is keyed on
    `(portfolio_id, symbol)`; `decisions.correlation_id` is unique; and `DecisionOutcome` is
    an *abstract member* on `TradeDecisionResult` rather than a switch elsewhere, because a
    switch expression over a hierarchy needs a discard arm that silently absorbs a new case.
  - **The trigger is statement-level on purpose.** A row-level one lets
    `DELETE FROM trading.orders` succeed quietly against an empty table, which reads as
    permission to try again once there is something in it. `TRUNCATE` is deliberately left
    alone: that is the owner clearing a table, not a cycle rewriting history.
  - **The tests run as `engine_svc`, not as the superuser.** The container runs the
    checked-in `db/init/01-schema.sh` rather than a copy, so the roles, the schema ownership
    and the grants are the ones a fresh volume gets. That is what makes them able to catch a
    migration that works for `postgres` and is refused for the role that actually runs it.
  - **The migration is tested both ways.** Down is where the trigger function hides: dropping
    the tables does not take it with them, so a down that forgot it would fail the next up on
    "function already exists".

  *Mutation testing, red tests per broken rule:* trigger function made a no-op 3; order
  appended before the balance check 1; row version removed **15**, because EF 10 refuses to
  `Migrate()` a model that has drifted from the last migration - so the database tests double
  as a drift guard, which was not the intention.

  *Two things the tests found that review had not.* Npgsql refuses to write a
  `DateTimeOffset` whose offset is not zero, and `quote_as_of` comes from a service free to
  express an instant in whatever offset it likes - a production failure waiting for the first
  non-UTC timestamp, now normalised by a value converter. And the concurrency test passed for
  the wrong reason at first: both writers bought a *new* instrument and collided on the
  positions key long before they reached the row version.

  *Verified before pushing:* 193 .NET tests, 241 Python, `dotnet format` clean, gitleaks
  clean, no vulnerable packages. The migration was applied to the real `trading-db` as
  `engine_svc` - four tables owned by that role, both triggers present, `agent_svc` still
  with no access to the schema - and the engine starts and runs a cycle with the new required
  setting in place.

- **PR `stage-4-worker`** (three commits, 2026-09-24): the worker stops holding a portfolio.
  Each cycle reads it through the repository, changes it and commits it inside one scope.

  - **Three decisions, all taken the recommended way.** Migrations stay a deploy step, but
    the engine refuses to start when the database is behind the build - applying at startup
    would move the schema before anyone could decide to, and the migration was tested in both
    directions precisely so that undoing it stays possible. The opening balance becomes
    `Trading:OpeningBalanceUsd`, because it is used once and then sets the size of every trade
    that follows. A failed commit is an error line and the loop carries on.
  - **A failed commit loses nothing but an LLM call.** The buy is in the same transaction as
    the decision, so a commit that fails means no trade happened - there is no evidence hole,
    which is what made "carry on" the honest answer rather than the convenient one.
  - **The row is written outside the decision.** `DecideAsync` has six early returns; writing
    the record in its caller is what makes it impossible for one of them to skip it. Mutating
    the write to fire only when there was a signal turns exactly one test red.
  - **The thread-safety problem is gone rather than guarded.** There is no shared mutable
    state left for a second ticker or a second worker to get wrong.

  *Mutation testing:* worker reopens the account every cycle **2** (both restart tests);
  decision recorded only when a signal existed **1**.

  *Verified live, not only in tests.* Both services up, `llama3.2` answering: the account was
  opened at 10 000 USD, AAPL bought at 337.445, and the next two cycles recorded as `NotSized`
  because the remaining budget did not reach one share. A second engine process then found the
  same portfolio - one row, same balance, decisions growing from three to five - rather than
  opening another. 208 .NET tests and 241 Python green, format and gitleaks clean.

  *Still open:* nothing translates a duplicate `correlation_id`; the ids are fresh Guids, so
  one would be a bug rather than a condition, and it falls into the worker's general handler
  with a stack trace.

- **PR `stage-4-quotes`** (four commits, 2026-09-24): `GET /v1/quotes/{symbol}`, and the
  engine using it to value the holdings it is not analysing. This closes the gap stage 3
  recorded as a test rather than a comment.

  - **Three decisions, all taken the recommended way.** The symbol travels in the path, the
    endpoint answers one symbol at a time, and `Trading:Tickers` gains MSFT so the endpoint
    is exercised by a real run rather than only by tests.
  - **Finding B was never about the path.** It was about a value nobody checked. FastAPI now
    validates the symbol against the same pattern the contract and `Ticker` enforce *before*
    the route body runs, so `../internal/shutdown` never reaches a lookup; the engine escapes
    it into the URL anyway, so neither check is the only thing standing between a value and a
    URL. Five such symbols are tests on the Python side and three on the engine's.
  - **The contract is narrower than the fact sheet.** P/E and sector are read by agents; a
    price is read by arithmetic. Every field in a contract is a field the other side has to
    keep accepting, and the engine's DTOs refuse unknown members by design.
  - **It is the first contract with a currency.** A quote can be for an instrument the engine
    does not price in dollars, so a SEK price arrives as SEK and `Money` refuses to add it to
    a dollar total. That is half of what finding D's remainder needs.
  - **A missing or stale quote is left out, never substituted.** The portfolio then cannot be
    valued and the sizer names the holding that stopped it. Valuing a holding at what it cost
    would overstate a loser, raising the allowance for everything else exactly when the
    portfolio had shrunk - the same argument that produced `PortfolioValuation.PriceMissing`
    in stage 2.

  *Mutation testing:* the path pattern removed **3** (Python); the staleness window widened
  to ten years **1**. The first attempt at that second mutation was `if (false)`, which is
  unreachable code and therefore a build error here - the lesson from stage 3, met again.

  *Found by running it, not by review.* The agent service was logging every quote under a
  correlation id it had invented, because the engine set the header only on the signal call.
  A decision that cannot be traced back to the prices it was made on is a decision stage 4
  cannot explain. After the fix, four cycles in one run had their signal and their quote under
  one id.

  *Verified live:* a real quote for MSFT at 497.56 over HTTP, a traversal symbol answered 404,
  no key answered 401, and the engine bought one MSFT while holding AAPL - which is only
  possible if the AAPL holding was priced through the endpoint. 227 .NET tests and 255 Python
  green.

---

## Lessons and gotchas

Things that cost time or were not obvious. Most are also recorded where they apply.

**.NET**
- The .NET 10 SDK no longer runs xunit v3 through VSTest. `global.json` opts into Microsoft.Testing.Platform, and `dotnet test` then needs `--solution`.
- `dotnet new sln` creates an `.slnx` file on .NET 10.
- System.Text.Json polymorphism expects the discriminator first unless `AllowOutOfOrderMetadataProperties = true`. This was tested with a file-based app, which needs `#:property JsonSerializerIsReflectionEnabledByDefault=true`.
- `AddStandardResilienceHandler` decides what to retry from the *response*, not from the HTTP method, so it retries a POST. For an expensive, non-idempotent call that has to be narrowed deliberately.
- A resilience pipeline throws Polly's own exception types. Translate them where the adapter meets the transport, or they reach the application layer looking like bugs.
- System.Text.Json fills a missing member of a positional record with `null` no matter how non-nullable the property is. Only `required` turns that into an error.
- Microsoft.Testing.Platform does not print `ITestOutputHelper` output for a test that passes. To read a value out of a probe, assert it into the failure message.
- Configuration that is only wired in `Program.cs` cannot be tested. Moving the client registration into an extension method was what made the retry rule testable at all.
- `HttpRequestException` carries an `HttpRequestError`, so "the connection never came up" can be told apart from "the server answered badly" without string matching.
- A `with` expression bypasses the constructor. A rule that normalises a value has to live in the property's `init` accessor, or a copy escapes it.
- `required` cannot go on a positional record's parameters without `[SetsRequiredMembers]` on the generated constructor, which defeats the point. Strictness means init-only properties.
- "No setting has a default" only catches a missing value when zero is outside its valid range. Zero is a legitimate cash buffer, so that one needs `[Required]` on a nullable to tell *absent* from *none*.
- System.Text.Json refuses an out-of-order type discriminator with `NotSupportedException`, not `JsonException`. `AllowOutOfOrderMetadataProperties` fixes it, and an example with the discriminator last is what stops the setting being deleted by accident.
- `[JsonUnmappedMemberHandling(Disallow)]` on a polymorphic variant does not reject the discriminator itself.
- Mutating code to check a test can fail does not work with `if (false)`: CS0162 is a warning, and warnings are errors here. Change a comparison instead.
- `dotnet format --verify-no-changes` reports a failure on stdout *and* through its exit code. Piping it into `tail` or `head` throws the exit code away, so a `&&` chain after it keeps going and a formatting error reaches a commit. Check `$?` rather than reading the output.
- A mutation has to stay *syntactically valid* to mean anything. Commenting out the first line of a multi-line SQL statement broke the whole migration and turned every database test red at 0 ms, which says nothing about the tests. Making the trigger function a no-op said everything.

**EF Core 10 and Npgsql**
- `UseXminAsConcurrencyToken()` is gone in Npgsql 10. A convention now recognises a `uint` property that is `OnAddOrUpdate` and a concurrency token, so `Property<uint>("xmin").IsRowVersion()` is the replacement. No column is created - `xmin` is a system column.
- EF 10 **refuses to `Migrate()` when the model has drifted from the last migration**. This is free drift detection: any configuration change without a new migration turns every database test red. `dotnet ef migrations has-pending-model-changes` asks the same question directly.
- `dotnet ef` writes generated files with a **UTF-8 BOM**, which `.editorconfig`'s `charset = utf-8` forbids, so `dotnet format --verify-no-changes` fails until it is stripped.
- EF's migrations history table defaults to the provider's default schema. A role that owns only its own schema cannot create it there, so `MigrationsHistoryTable(name, schema)` is required, not decoration - otherwise the first migration fails as permission denied.
- Npgsql refuses to write a `DateTimeOffset` whose offset is not zero to `timestamptz`, because the column stores an instant. A timestamp that arrives from another service needs a value converter to `ToUniversalTime()`, or it throws on the first non-UTC value.
- `Microsoft.EntityFrameworkCore.Design` ships with `compile` **excluded** from `IncludeAssets` by default, so `IDesignTimeDbContextFactory` cannot be implemented until `compile` is added back.
- Npgsql 10.0.3 depends on EF Core 10.0.4 while the design-time package depends on 10.0.12, which leaves a test project resolving one version and the referenced assembly compiled against the other (MSB3277). Naming `Microsoft.EntityFrameworkCore.Relational` explicitly settles it.
- A statement-level trigger fires even when the statement matches no rows, which is what makes `DELETE FROM t` refusable on an empty table. A row-level one does not.
- Testcontainers can run a repository's real init script with `WithResourceMapping(new FileInfo(...), "/docker-entrypoint-initdb.d/")`, so the test database gets the production roles and grants instead of a copy that drifts.

**Python**
- `uv_build` assumes a `src/` layout. This repo needs `module-name = "app"` and `module-root = ""`.
- openai 3.x types its client against `httpx2`, so `memory.py` has a documented `type: ignore` until stage 1 moves client creation into the lifespan.
- AG2 1.0.5: `ask()` without `stream=` runs on a fresh `MemoryStream` (`stream or MemoryStream()` in `Agent._open_run`), so agent objects shared between requests do not leak history.
- `httpx2` is not a typosquat. It is pydantic's successor to httpx, and `openai` 3.x requires it (`httpx2<3,>=2.7.0`), which is why passing an `httpx` client needed a `type: ignore`.
- AG2 does not wrap the LLM client's exceptions, so openai's come through unchanged. `APITimeoutError` is a **subclass of** `APIConnectionError`, so it has to be caught first or a timeout is reported as unreachable.
- `reply.content(retries=2)` raises pydantic's `ValidationError` once the retries are spent, and costs 3 HTTP calls for one decision.
- The openai client defaults to a **600 s read timeout and 2 retries of its own**. Stacked under AG2's schema retries that is up to nine calls for one decision, and it makes a 504 unreachable in practice. Both are set explicitly now.
- A `ContextVar` set in a `BaseHTTPMiddleware.dispatch` does not reliably reach the endpoint, because the endpoint runs in another task. Plain ASGI middleware runs in the same task and does.
- mypy reads `BaseSettings` fields as required constructor arguments unless `plugins = ["pydantic.mypy"]` is set. The plugin is the fix; a `type: ignore` would have been the wrong one.
- FastAPI validates a path parameter against `Path(pattern=...)` and raises `RequestValidationError`, so a 422 needs a handler to come back in the same `{error_code, correlation_id}` shape as everything else.
- Pydantic serialises `Decimal` as a JSON **string** in JSON mode, and the engine's `required decimal` refuses that. A price on the wire is a `float`; the engine converts on arrival, where the arithmetic actually happens.
- A pydantic discriminated union needs **at least two** members - `Field(discriminator=...)` on a one-member union is an error. A `Literal` on the discriminator field does the same job until the second variant exists.
- Ruff's `UP040` rejects `X: TypeAlias = Y` and wants PEP 695's `type X = Y`. Pydantic handles the latter fine on 3.12.
- `zip(xs, xs[1:], strict=True)` always raises: the second argument is one shorter by construction. Offset pairs need `zip(xs[:-1], xs[1:], strict=True)`.
- `@runtime_checkable` makes `isinstance` work against a `Protocol` by method name, which is enough to assert that an implementation still fits its port.
- yfinance's `period="1y"` ends **364** days back, so a twelve-month return is impossible to compute from it. `"2y"` is the smallest period that works.
- yfinance returns `NaN` as readily as `None` for a figure it does not have, and `NaN` reaches a prompt as the word `nan`, which a model is free to read as a number.
- `ag2.config` exports a `unittest.mock.Mock` for every provider whose extra is not installed. It imports fine and raises `ImportError` with an install hint on construction - so the "lazy import per branch" a design might ask for is already the library's. mypy still resolves the real dataclass behind it, so keyword arguments are checked even when nothing can run them.
- AG2's four provider configurations differ more than they look: `AnthropicConfig` has no `seed`, `OllamaConfig` has neither `api_key` nor `timeout`, and the endpoint argument is `base_url`, `api_host` (a bare host) or `host` depending on which one.
- Passing conditional keyword arguments as `**({"k": v} if cond else {})` defeats mypy, which then checks the dict against every parameter. Build the object and apply the optional argument through `ModelConfig.copy()` instead.
- pydantic-settings lowercases the segments of a nested environment variable, so `TAS_LLM__ROLES__PORTFOLIO_MANAGER__MODEL` lands under the key `portfolio_manager`.
- `ag2.testing.TestConfig(*turns)` scripts a model from strings, and a `BaseException` among the turns is raised untouched. So a scripted `ConnectionError` is a *builtin* one, not openai's - the exception translation has to be tested with `openai.APIConnectionError` and friends, constructed against an `httpx2.Request`.
- With a `response_schema`, `reply.content()` raises pydantic's `ValidationError` for an empty or malformed answer rather than returning `None`. The `None` the type declares is not reachable that way, so a guard for it needs a stub at the AG2 boundary to be tested at all.
- Python's `hash()` is salted per process. Anything that has to be stable across restarts - a version, a cache key written to disk - needs `hashlib` over a canonical serialisation, and the cheapest proof is running it in two subprocesses under different `PYTHONHASHSEED` values.
- A pydantic model's docstring becomes its JSON schema's `description`, so it is part of what the model is asked for - and part of anything that hashes the schema.
- Ruff's `S603` flags every `subprocess.run`, including one whose argument is a literal three lines above. A `# noqa: S603` with the reason is the honest answer.

**.NET, continued**
- A test that asserts "a second buy adds to the position" is wrong once a real position cap exists: the first cycle takes the whole allowance, so the second is correctly refused. The test had to change, not the code - and the corrected version is a better test, because it pins the cap holding across cycles.
- `[GeneratedRegex]` needs the containing type to be `partial`. Making a `record` partial for it costs nothing and keeps the regex compiled at build time rather than at first use.
- `TimeProvider` is in the base library, so a clock can be injected without a package. A three-line `FixedClock : TimeProvider` is enough for a test, and it makes a quote-age rule testable without waiting.

**CI and GitHub**
- A Dependabot PR title names only the packages whose requirement changed. Read the `uv.lock` diff: a dependency without an upper bound can move a major version silently, as fastmcp did in PR #5.
- Green CI is only as good as the tests behind it, and a fallback that answers HTTP 200 hides a broken chain.
- `setup-uv` caches by default (`enable-cache: auto`) and never saves again on an exact key hit, so two jobs that share a key race, and the loser lives with the winner's cache.
- `github-advanced-security` is GitHub's Copilot "Code scanning AI findings" job. It fails on most pull requests here and passed only on #7; switching CodeQL on did not fix it. It is not required and blocks nothing.
- A new ruleset starts with enforcement *Disabled*, and required approvals must be 0 for a solo developer, or nothing can ever merge.

**Database and environment**
- Postgres init scripts run only on an empty volume. A change to `db/init/` takes `docker compose down -v && docker compose up -d`, which deletes all data.
- `pgvector.asyncpg.register_vector` looks for the extension in `public`, so `vector` stays there.
- The psql variables `:'x'` and `:"x"` quote a literal and an identifier safely, so the shell never splices a password into SQL.
- Docker Desktop's WSL integration must be enabled for Ubuntu-24.04, or `docker` fails even though the binary exists. The container can still be running and reachable on `127.0.0.1` through mirrored networking, so a failing `docker` command does not mean the database is down.
- `pkill -f` matches the running shell's own command line, so a plain `pkill -f 'uvicorn app.main:app'` kills the shell that issued it - exit 144 - even when it is the only thing in the command. Always write the pattern as `'[u]vicorn app.main:app'`, and keep stopping and starting in separate commands so the start does not put the pattern back on the line.
- Ollama runs on Windows, and WSL reaches it at `127.0.0.1` only in mirrored networking mode. Use `127.0.0.1`, never `localhost`.
- Searching for `[åäö]` misses Swedish words without those letters. Two exception messages survived PR #2 that way.
