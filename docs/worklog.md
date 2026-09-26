# Worklog

A running record of what has been done, what was learned along the way, and what comes next. It sits between two other documents:

- [`docs/arkitektur-roadmap.md`](arkitektur-roadmap.md) (Swedish) is the plan: the decisions, the stages, and how each stage is verified.
- [`CLAUDE.md`](../CLAUDE.md) describes the repo as it is today: commands, environment, architecture.

This file answers "where are we, how did we get here, and what is next". When resuming, read *Current state* and *Next steps* first, then the roadmap section for the next stage.

**Last updated:** 2026-09-25. **Stage 4's code is complete and merged** - all eight pull
requests. The machinery runs end to end: decisions are stored, the portfolio survives a
restart, a sweep scores every signal whose horizon has passed, the engine posts what it
measured to the agent service, and memory reads the journal back. 21 signals are scored and
`trading.hit_rate` has rows in it.

**Stage 5 is next, and the ground was cleared for it today.** Finding G said waiting cannot
produce a baseline, because the team contradicted itself on identical input. Both halves of
that are now settled in one change, while it was still cheap: the model is `qwen2.5:14b` at
temperature 0 with a pinned seed, and the horizon is capped at 30 days on both sides of the
contract. Three identical requests through the real pipeline now answer SELL, SELL, SELL.
See *Before stage 5*. The rest of finding G - one question re-asked every fifteen seconds,
and a population of two instruments - is what stage 5 itself fixes.

## Resuming checklist

```bash
cd ~/repos/trading-agent-system            # the WSL clone; the Windows clone is stale
git status && git branch -a                # normally master plus one working branch; today two, see Current state
git pull --ff-only
docker compose up -d                       # trading-db; needs .env in the repo root
cd src/agents && uv sync                   # Python dependencies from uv.lock
curl -s http://127.0.0.1:11434/api/tags    # is Ollama on Windows reachable from WSL?
diff <(grep -o '^[A-Z_]*' .env.example | sort -u) <(grep -o '^[A-Z_]*' .env | sort -u)
```

**Compare the local `src/agents/.env` against `.env.example` after pulling**, and not only
for keys that are missing. Every setting is required and **nothing has a default**, which is
deliberate - an incomplete environment stops the service rather than falling back to the
wrong database or OpenAI's cloud. The cost is that a *stale* value is silent: an `.env`
written before 2026-09-25 still says `llama3.2`, temperature 0.2, no seed and a 30 s timeout,
and the service starts happily on it. Nothing breaks and nothing is mixed up - the model is
in `team_version`, so those outcomes never pool with anyone else's - but you are then running
a third team that matches neither the documentation nor the current hash, and the only sign
is the startup line `Agent service ready: <model> via <provider>`. Read it.

Then run the CI checks listed in CLAUDE.md before changing anything, so that a failure is known to be pre-existing. **CI is reproducible locally** with a throwaway worktree, which is what catches anything that only passes because this machine has something CI does not:

```bash
git worktree add --detach /tmp/ci HEAD       # tracked files only: no .env, no .venv, no bin/
cd /tmp/ci/src/agents && uv sync --locked && uv run pytest
cd /tmp/ci && dotnet test --solution TradingSystem.slnx
git worktree remove --force /tmp/ci
```

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

- **Stage 0 done** 2026-09-19, **stage 1 done** 2026-09-20, **stage 2 done** 2026-09-21, **stage 3 done** 2026-09-23. **Stage 4 started** 2026-09-23. PR 5 and PR 6 were each split in two, so the stage is eight pull requests, and **all eight are merged** as of 2026-09-25. **Stage 5 has started**, with the model and the horizon settled first; stages 6-8 exist only as plan.
- **`master` is at PR #41**, and stage 4 is fully merged: 6a (#39), 6b (#40) and the docs-only record of finding G (#41), all on 2026-09-25. Nothing reaches `master` without the three required checks passing, so what is there is green by construction.
- **One branch is open:** `stage-5-model-and-horizon` - the model, the clock and the horizon cap. Not stage 5 itself; the thing finding G said to do before it.
- **`b1234878670a` never reached a row, and that is worth knowing rather than forgetting.** On 2026-09-25 `default`'s hash moved from `6c6da0e6edad` to `b1234878670a` without a word of the team changing: adding `sees_memory` to the payload wrote `sees_memory: false` onto every step. The payload was made sparse the same day, before the engine ran again, so the accidental hash was never stored - `SELECT team_id, team_version, count(*) FROM trading.decisions` returns only `6c6da0e6edad` and `79dfb7307b57`. Nothing has to be pooled and nothing has to be written down; the earlier warning in this file that the two hashes had to be reconciled by hand is obsolete, not wrong at the time. The lesson survives the hash: a version payload that lists every flag with its default ties the version to the shape of the payload rather than to the team.
- **There is one path now.** `POST /v1/signals` is the only endpoint that costs money, the engine calls it every cycle, and the old three-agent chain, `InvestmentProposal`, `ValidateTrade`, `RiskViolationException` and the FastMCP server are gone. Running the engine today produces real quantities at real prices, with the position cap holding across cycles.
- **Every environment variable was renamed on 2026-09-23.** The local `src/agents/.env` was renamed in place and still works; a fresh clone follows `.env.example`. Nothing outside this repo reads them.
- **Measurement has produced its first numbers.** A sweep on 2026-09-25 **measured 21** signals at one trading day, left 63 horizons not due, abandoned none, and delivered all 21 to the agent service. `trading.hit_rate` has four rows. They mean nothing yet, and it is worth saying so plainly: one trading day is noise, and all sixteen BUYs "hit" because both names happened to rise that day. The two HOLDs that missed did so with an excess of 3.5 %, which is the band doing its job rather than the model doing well.
- **Both schemas now hold a copy of the same measurement**, joined by nothing: 21 rows in `trading.signal_outcomes`, 21 in `trading.outcome_deliveries`, 21 in `agent.signal_outcomes`. The correlation id is the only thing they share, and it crosses over HTTP.
- **Python writes down what its agents were given.** `agent.analysis_runs` and `agent.step_outputs` hold the fact sheet an analysis started from and each step's answer - 8 runs and 24 step rows after one engine session, which is three steps per run exactly as the team specifies.
- **Memory is wired in and correctly empty.** 8 runs are embedded; none of them has a measured outcome yet, so `recall` answers *"Inga tidigare analyser av AAPL har hunnit mätas färdigt."* - which is the designed behaviour rather than a fault. Worth knowing: **the first 21 measurements can never become memory**, because the analyses behind them predate the journal. Memory starts from the runs journalled since PR 6a, and the first becomes recallable when its one-trading-day horizon is measured.
- **There are two teams now.** `default` is the baseline and reads no memory; `default-memory` is the same team with its risk manager shown past measured analyses. For their versions, which moved with the model on 2026-09-25, see the bullet above rather than a copy here - two places holding the same hash is how this file came to assert one that existed nowhere. `Trading:TeamId` stays `default`; the memory team was run once by environment override to prove it works, which is where those 8 embedded runs came from.
- **The model asked for horizons of a year, and no longer can.** The full distribution over all 36 stored signals was 6 days (3), 30 (1), 90 (6), 180 (**15**) and 365 (**11**) - 26 of 36 at half a year or more, although the system looks for short-term opportunities. Nobody had told it otherwise: the prompt said "be honest" without naming a range and the schema allowed 365. Capped at 30 on 2026-09-25, in pydantic, in the schema and in the engine's mapper, with the range named in the prompt. The first live signal afterwards asked for 15.
- **The model is `qwen2.5:14b`**, at temperature 0 with seed 42, replacing `llama3.2` (3B). A cycle is about 20 s warm and 28 s cold, against 7-10 s before. `TAS_LLM__DEFAULT__TIMEOUT_S` is 60 and `AgentService:RequestTimeoutSeconds` is 120; the old 30 was below a single step on any 14B model.
- **Two team_versions are in the data; two more are only in the code.** `default` ran 28 decisions as `6c6da0e6edad` and `default-memory` 8 as `79dfb7307b57`. The model change makes them `5926c629dcbe` and `b856e3edf611`, but the engine has not run since, so neither has a row yet. The stored rows keep their old values, which is the point of putting the version on the row - and the distinction between a version that exists and one that has been *used* is what this file got wrong about `b1234878670a` above.
- **The engine trades two instruments.** `Trading:Tickers` is AAPL and MSFT, so a cycle is two analyses and the quote endpoint is used in a real run rather than only by tests.
- **The `trading` schema is live and has real rows in it.** Runs on 2026-09-24 opened the account at 10 000 USD and bought one AAPL at 337.445 and one MSFT at 497.56, with a second engine process picking the same portfolio up rather than opening another. The local database has **all three** engine migrations and **all three** Alembic revisions applied, which `master` has carried since PR 6a (#39) merged on 2026-09-25 - so the two are level. Delete the rows with `TRUNCATE trading.signal_outcomes, trading.decisions, trading.orders, trading.positions, trading.portfolios RESTART IDENTITY CASCADE` if a clean baseline matters; the append-only triggers deliberately do not block that.
- **The engine now needs `Database:ConnectionString`** or it refuses to start. It is in the user secrets store on this machine, set 2026-09-23. `dotnet user-secrets list --project src/engine` prints it, so do not run that where anyone can see the screen.
- **Migrations are applied by hand, and the engine refuses to start without them.** Decided 2026-09-24: `dotnet dotnet-ef database update` stays a deploy step, but startup names the pending migrations and the command instead of failing on a missing column mid-cycle.
- **What is now impossible** rather than merely unlikely: the agents cannot name an amount (the contract has no `amount_usd`, and a test refuses one that reappears); an answer that is not the contract cannot deserialise into nulls; a position cannot be sized against cash instead of net asset value; and an order cannot be placed on a quote that is stale or dated in the future.

## Next steps

**Stage 4 - persistence and outcome measurement** is merged, all eight pull requests. **Stage
5 - finding candidates and selling** is next, in three pull requests; read that stage in
`docs/arkitektur-roadmap.md` before continuing, since it carries several decisions that are
easy to miss.

**This is the stage the project exists for**, and most of it now exists. Decisions survive a
restart, every signal is stored with the room it was decided in, and a nightly sweep scores
each one against the index at fixed horizons and at the model's own. What that turns *"are
the agents any good?"* into is a number grouped by `team_version` - which stage 3 built
precisely so this comparison would be possible, and which nothing can answer until a trading
day has passed.

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

### The work, as eight pull requests

| # | What | Why it is its own |
|---|---|---|
| 1 ✅ | `trading` schema through EF Core: `portfolios`, `positions`, `orders` (append-only), `decisions`. `IPortfolioRepository`/`IUnitOfWork`, a reconstitution constructor on `Portfolio`. Testcontainers, and the migration tested **both ways**. | The shape is the decision. Everything after it writes rows in this form. |
| 2 ✅ | `TradingWorker` loads the portfolio per cycle and saves the decision. | This is where restart-survival becomes visible - and **the thread-safety problem disappears structurally** rather than being guarded against. |
| 3 ✅ | `GET /v1/quotes/{symbol}` on the agent service, deterministic and with no LLM, plus the engine's client for it. | It is what an outcome is measured against, and it also closes the gap left in stage 3: `PositionSizer` gets `PriceSnapshot.Empty` today, so a portfolio holding more than one instrument cannot be valued. |
| 4 ✅ | The outcome function: return over a horizon, comparison against an index, hit per stance, a horizon landing on a non-trading day. Pure functions, TDD. | Findings E and F land here. Indata and expected figure are the specification, which is exactly where writing the test first pays. |
| 5a ✅ | The history endpoint, the engine's client for it, and the `Outcome` configuration. | Split out because the whole of PR 5 was five commits: this half is *how the engine gets bars*, and it runs on its own. |
| 5b ✅ | The scheduled job and `trading.signal_outcomes`: the fixed horizons (1, 5, 20 trading days) and the model's own, **for every signal** - including HOLD, risk rejections and everything that was never bought. A SQL view for the minimum report. | Measuring only the trades that went through measures the wrong population. |
| 6a ✅ | Alembic for the `agent` schema, `agent.analysis_runs` + `step_outputs`, `POST /v1/outcomes` with `agent.signal_outcomes` behind it, and the engine's delivery of measurements with `trading.outcome_deliveries` to make it retry itself. | **The database is never the integration point** - that is what separates "two schemas" from the shared-database anti-pattern. Split out because none of it changes what a model sees: `team_version` is untouched, so the baseline is not disturbed. |
| 6b ✅ | Memory in the loop: `agent.analysis_embeddings` replacing `agent_memories`, a `sees_memory` step flag, and `default-memory` - the baseline team with its risk manager shown past **measured** analyses. | This *does* change what a model sees, so it is a new `team_version` - and it stays out of `default` on purpose, so the baseline keeps accumulating while the two can be compared. |

The pool leak in `memory.py` that the roadmap lists under PR 6 was already fixed: all three
methods use `async with self._pool.acquire()`. Found by reading, not by testing.

**The baseline is a deliverable, not a by-product.** Nobody can say whether a news agent
helped or only cost tokens without one - the same reasoning that made the review rounds
conditional in PR #11 - and a `team_version` with outcomes at the fixed horizons is part of
the stage's definition of done.

What this said until 2026-09-25 was that the team "has to run long enough". **That was the
wrong shape of the problem.** Long enough on two instruments, re-asked every fifteen seconds,
with a model that answers the same fact sheet three different ways, is not a baseline however
long it runs. See **finding G**. What a baseline needs is a population, and a population is
what stage 5 builds.

Before stage 5, turn finding D's currency mismatch into an outcome rather than a throw.

### Next up: stage 5, because waiting does not work

**Stage 4's code is done and merged**, all eight pull requests. Nothing else in the stage is
a pull request - but what remains is not a matter of waiting, which is what **finding G**
corrects. The rows already written show the team giving BUY, HOLD *and* SELL on one fact
sheet, 36 signals across four (instrument, trading day) pairs, and nothing scheduling the
engine at all.

The model and the horizon are now settled - see *Before stage 5*, and the measured result:
SELL, SELL, SELL where `llama3.2` gave three different answers. What is left of finding G is
the population, and that is **stage 5** itself.

**Stage 5 goes out as three pull requests**, decided 2026-09-25. The roadmap calls it 3-4
days, which is too much for one review:

| | What | Why it is its own review |
|---|---|---|
| 1 | `app/screening/` and `POST /v1/screen` - a universe, the factors from `facts.py` over all of it, filters and a ranking | Pure functions over fixed datasets, TDD, and **no engine changes at all**. It can be run and judged before anything else moves. The stage's stated practical risk lives here: yfinance is unofficial and rate-limited, and fundamentals are fetched per instrument |
| 2 | Selling: the SELL branch in `PositionSizer`, `Portfolio.ExecuteSell` with realised profit and loss, and the deterministic exits - stop-loss, time limit, minimum holding period | Test-first with the same table technique as stage 2. The exits are the half that does not depend on the LLM answering, which is decision 1 applied to selling |
| 3 | The engine drives the cycle: universe to shortlist, shortlist union holdings, one analysis per fact-sheet change, and the shortlist stored per cycle | This is where the regime column goes, and where "do the agents beat the screening that picked their candidates?" becomes answerable |

Stage 8's condition survives untouched, because stage 5
does not touch the team.

**What 6b settled** (decisions taken 2026-09-25, all four the recommended way, then a fifth
forced by a live run):

| Decision | Taken | Why |
|---|---|---|
| Where the embedding lives | **`agent.analysis_embeddings`, keyed on a journalled run. `agent_memories` retired** | Three of its four columns were already in the journal, and the one thing it lacked - the correlation id - is what joins an analysis to what happened afterwards. A table beside `analysis_runs` rather than a column on it, because an embedding is derived data and the journal is append-only |
| Which step reads memory | **The risk manager** | It already reads two things, so a third does not change its shape, and a past miss is risk information. It reaches the portfolio manager anyway, as a line in `risks`, without that step gaining its first input from outside the run |
| Unmeasured memories | **Not shown at all** | Reasoning without an outcome teaches a model to agree with itself. For the first days *every* memory is unmeasured, so showing them would be pure self-confirmation exactly while the baseline forms. Memory is empty, and says so |
| What is embedded | **The analyst's `MarketRead`, at both ends** | The query available when the risk manager runs is today's reading, so matching a reading against a reading asks "when things looked like this before, how did it go?". One function does both ends, so the symmetry cannot be edited apart |
| Handover flags in `team_version` | **Sparse: a flag appears only when set** | Found by reading the startup log: adding `sees_memory` to the payload moved `default`'s hash although nothing about it had changed. The rule is "include what changes what the model says", and a flag nobody set changes nothing |

**Carried in from the review of 6a**, and still open - all three were deferred here and remain
for a follow-up, since 6b turned out to touch the memory path rather than the delivery path:

| | What | Note |
|---|---|---|
| `Remaining` is not a count | `ReportOutcomesUseCase` reads `MaxPerRequest + 1` rows, so a backlog of 5 000 reports "1 still waiting" | Rename to `MoreWaiting`, and drain with a tick budget |
| No status conditions in the contract | `Measured` with no figures, or `NotMeasurable` with `hit: true`, both validate | The obvious rule is wrong: **`net_edge` is null for HOLD**, because a HOLD has no edge to compute, only a band it stays inside |
| `MeasurementWorker` has no test | A comment claims delivery is attempted even when the sweep failed; that branch is tested nowhere | An untested error path in a service that swallows exceptions |

Also still open: a repeated `correlation_id` in the journal logs "its working is lost" when
the working is already there. Nearly unreachable, one line to fix.

### Before stage 5 — the model, the clock and the horizon (2026-09-25)

Finding G named two things to settle while they were still cheap, both of which move
`team_version`, so they were done as one change. Doing them at four independent events costs
four; doing them after stage 5 has run for a month costs a month.

**The hardware was the first surprise, and it decided the rest.** Windows' WMI reports the
GPU as 4 GB, which is a 32-bit field saturating rather than a fact -
`HardwareInformation.qwMemorySize` in the registry says the RX 7600 XT has **16 GB**.
`llama3.2` was using 4.1 of them at 100 tok/s. So "use a bigger model" was never a hardware
question, and the machine had been treated as smaller than it is for two weeks.

**`qwen3:14b` was tried first and rejected on measurement.** It reasons better, and Ollama
puts its thinking in a separate `reasoning` field so `content` stays clean JSON - structured
output works. The problem is the cost and that it cannot be turned off through the route this
project uses:

| Attempt | Result |
|---|---|
| `/v1` with `chat_template_kwargs: {"enable_thinking": false}` | Accepted without complaint, **ignored** - 1448 characters of reasoning to answer `{"ok": true}` |
| `/no_think` in the system prompt | No effect on this build |
| native `/api/chat` with `"think": false` | **Works** - 0 characters, 4.9 s - but that is provider `ollama`, the one branch in `provider.py` that carries no timeout |

25-37 s per step against 8-9 s: five times the wall clock for reasoning that is never
stored, since the journal holds `step_outputs` and not the `reasoning` field. And the
architecture had already decomposed the problem - three steps, one question and one schema
each - which is the work a thinking model does inside a single turn.

So: **`qwen2.5:14b`**, same size class, same family, no thinking mode. 8-9 s per step warm,
15.6 s cold, `reasoning` empty. Its Swedish is grammatically rough in a way its reasoning is
not; see *Open decisions*.

**Temperature 0 and a seed pin the decision, not the run.** This was worth measuring rather
than assuming, because the claim was about to be written down. Repeat an identical request
and Ollama returns byte-identical output. Put a *different* request in between and the same
request returns different bytes - the numerics depend on batching and KV-cache state outside
the request. So replay will reproduce a distribution, not a run, and stage 8's replay idea
has to be read that way.

What it does buy is that a contradiction can no longer be blamed on the draw:

| | `llama3.2` 3B, temp 0.2, no seed | `qwen2.5:14b`, temp 0, seed 42 |
|---|---|---|
| Same fact sheet, repeated | **BUY, HOLD and SELL** | SELL, SELL, SELL |
| Conviction | 0.50-0.80 | 0.85, 0.80, 0.80 |
| Wording | varies | varies |
| Cycle | 7-10 s | 20 s warm, 28 s cold |

**Timeouts had to move with the model.** 30 s per call was below one step on any 14B model,
so every call would have timed out. `TAS_LLM__DEFAULT__TIMEOUT_S` is 60 and
`AgentService:RequestTimeoutSeconds` is 120 - the engine gives up on a pathological chain
before the agent service's own ceiling of three times 60, which is the right way round.

**The horizon is capped at 30 days.** The full distribution over 36 stored signals was 26 at
180 days or more and only 3 at a week or less. The cap is in pydantic, in
`contracts/trade-signal.schema.json` and in the engine's `TradeSignalMapper`, and the prompt
now names the range - the schema is what holds when the prompt is ignored, which it has been
before. `contracts/outcome.schema.json` stays **uncapped** and says why: decisions stored
before the cap asked for up to 365 days, and a measurement belongs at the horizon its
decision actually asked for. The first live signal afterwards asked for 15.

The engine's other three contract caps were made public and pulled into the same test while
the file was open. Each was written in three places with only the first two held together,
and the drift is quiet rather than loud: the agent service validates its own answer first, so
a cap the engine set lower would surface as the agents "answering with something unusable" -
a 502 at the seam furthest from the number that is actually wrong.

## Open findings

Six findings from a review of the repository on 2026-09-20, and one from reading the data on
2026-09-25. Every one was reproduced before it was written down, and the reproduction is the
*Verified* line. They live here rather than in the roadmap on purpose: the plan should change
when a stage starts, not every time a finding arrives.

| | Finding | Status |
|---|---|---|
| A | A failed analysis is sent twice | **Fixed** in PR #17 |
| B | A ticker is interpolated into the URL without escaping | **Fixed** in PR #30, both halves |
| C | The proposal DTO accepts an answer that is not the contract | **Fixed** in PR #20 |
| D | A currency mix is reported as a bug, not as an outcome | **Fixed** in PR #23 |
| E | There is no trading calendar anywhere in the plan | **Fixed** in stage 4's PR 4 |
| F | Outcome measurement ignores transaction costs | **Fixed** in stage 4's PR 4 |
| G | The team contradicts itself on identical input, so waiting cannot produce a baseline | **Partly addressed** 2026-09-25 - the model and the draw are settled; the population is stage 5's job |

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

### E — there is no trading calendar anywhere in the plan (fixed, PR 4)

**Built 2026-09-24.** `BarSeries` in `Engine.Domain.Outcomes` counts horizons in bars, and
`Horizon` keeps trading days and calendar days apart. A horizon that lands on a holiday needs
no special case, because such a day has no bar to land on.

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

### F — outcome measurement ignores transaction costs (fixed, PR 4)

**Built 2026-09-24.** `OutcomeCalculator` subtracts a round trip on the instrument leg and
stores the cost on the measurement. The single test `Costs_are_what_turn_a_thin_win_into_a_loss`
scores the same bars twice, with costs and without, and gets opposite verdicts.

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

### G — the team contradicts itself on identical input (partly addressed)

**Found 2026-09-25**, by querying the rows the engine has already written rather than by
reading code. It is the most useful thing in the database, and it invalidates a plan this
file repeated for two days.

*Verified:* group `trading.decisions` by symbol and `reference_price` - the same price means
the same fact sheet, since the sheet is computed from the same snapshot.

| Instrument | Price | Signals | Stance on identical input |
|---|---|---|---|
| MSFT | 516.57 | 4 | **BUY, HOLD and SELL** |
| MSFT | 497.56 | 5 | BUY, HOLD, conviction 0.50-0.80 |
| AAPL | 337.715 | 7 | BUY, HOLD |

The whole population is 36 signals with a stance, across **four** (instrument, trading day)
pairs. Running a cycle every fifteen seconds produced fourteen AAPL signals on one fact
sheet, and all fourteen get the same measured outcome - so 21 measured rows are about four
independent events. "15 of 16 BUYs hit" is really "both shares rose on one day".

**Why it matters more than it looks.** This file has said since 2026-09-24 that the baseline
is a deliverable that can only be waited for. That is wrong, on three counts and in that
order of severity:

1. The team disagrees with itself on the same input, so no amount of data separates it from
   chance. This is a measurement of `llama3.2` at 3B, not of the market.
2. The measurements are not independent. A cycle every fifteen seconds re-asks one question.
3. The population is two instruments.

And a practical fourth: nothing runs the engine. It is started by hand for a minute or two at
a time, so even the waiting is not happening.

**What it changes.** Stage 5 is not a detour around the baseline, it is what makes one
possible - a universe of 30-50 instruments, one analysis per instrument per fact-sheet change,
and the shortlist stored so the question that decides the project can be asked. The roadmap
already said so: *"det här är steget från 'bedöm AAPL var 15:e sekund' till målet"*. Stage 8's
condition is untouched, because stage 5 does not touch the team.

**What to settle first**, while both are still free: the model, and the prompt's horizons.
Both change `team_version`, so they belong in one change - and doing them now costs four
independent events rather than a month of them.

**One thing stage 5 has to carry.** Nothing in `decisions` records *how* an instrument was
chosen. After stage 5 there will be two regimes under one `team_version` - "AAPL every
fifteen seconds" and "screened shortlist" - and no way to tell them apart afterwards. The
shortlist is stored per cycle by the roadmap's own design; the decision row also needs to say
which regime produced it. That is one column, and it is free now.

**What was done about it, 2026-09-25.** **Count 1 is settled**, and only count 1; see *Before
stage 5* for the measurements. It had two causes and only one of them was the model: no seed
was set and the temperature was 0.2, so part of the contradiction was the draw. Temperature
0 with a pinned seed removes that, which makes the experiment *sharper* rather than merely
quieter - a contradiction that survives greedy decoding is the model's. On `qwen2.5:14b`,
three identical requests through the real pipeline answered SELL, SELL, SELL at conviction
0.85/0.80/0.80 and horizon 15, against `llama3.2` answering BUY, HOLD and SELL to one fact
sheet. Three runs is a small sample; it is three-for-three on the thing that was previously
three-for-three against.

Counts 2 and 3 - one question re-asked every fifteen seconds, and a population of two
instruments - are untouched, and they are exactly what stage 5 builds. The finding stays
open until then.

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

- **English prompts, as a third team rather than an edit.** Permitted by the owner on 2026-09-25: the agents may talk to each other in English if it gives better results. Not taken up in the same change as the model, because that change already moved four variables - model, temperature, seed and horizon - and a fifth would make the next difference in outcomes unattributable. The right form is a third `TeamSpec` with English instructions and a Swedish answer, sharing what it can with `default` the way `default-memory` shares two of three prompt files; `team_version` then keeps the outcomes apart automatically. Worth doing when there is a population to measure it against, which is after stage 5.
- **`qwen2.5:14b`'s Swedish is rough.** Observed in the first live signals: *"P/E-ratios är acceptabelt"*, *"den nuvarande beslutet"*, *"kärnaaffärer"*. The reasoning behind the theses reads better than the Swedish they are written in, which is the concrete argument for the English-prompt experiment above. It is an observation, not a finding - nothing has measured whether the language affects the decision.
- **`Trading:CycleIntervalSeconds` is 15 and no longer describes the cadence.** Raised by a review of PR #42. `TradingWorker` delays *after* the loop over tickers, so nothing overlaps and nothing queues - the period is simply (analysis time x tickers) + 15 s, which the model change moved from about 30 s to 55-70 s. Deliberately not bumped: stage 5 replaces the cadence entirely with one analysis per fact-sheet change, so tuning a number that is about to be deleted is churn. It belongs to **stage 5's third pull request**, where the cycle is rewritten, and it is the same thing as count 2 of finding G.
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
- **Memory is wired in as of PR 6b**, and `agent_memories` is gone with the `TEST` row that used to sit in it. `AnalysisMemory` reads the journal rather than a store of its own, and only the part of it the engine has measured.
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

- **PR `stage-4-outcomes`** (three commits, 2026-09-24): the outcome function, as pure
  domain code wired to nothing. Findings E and F are both closed here.

  - **Three decisions, all taken the recommended way.** The engine computes the outcome and
    Python grows a history endpoint in PR 5; the model's own horizon is calendar days,
    because that is what it was asked for; costs are charged to the instrument leg only.
  - **Finding E got a rule instead of a calendar.** A day with a bar is a day the market was
    open. No holiday table to maintain, right per exchange without configuration, and a
    missing day is a fact about the data rather than an assumption about the world. What it
    deliberately does not answer is "is the market open now" - stage 5's question.
  - **Finding F is one test.** The same bars and the same signal scored twice, with costs and
    without, give opposite verdicts. The cost is stored on the measurement rather than left
    in configuration, so a row is still interpretable after the settings change.
  - **Three results, not two.** "Not due" means ask again tomorrow; "not measurable" means a
    hole that waiting will not fill. Collapsing them would either lose signals silently or
    retry them forever.
  - **The comparison is deliberately asymmetric.** The instrument's base is the reference
    price - what the engine would actually have paid - while the benchmark's is its close on
    the signal's day. Using that day's close for the instrument too would measure a trade
    nobody made.

  *Mutation testing:* the signal's own day counted towards the horizon **3**; the "data has
  not arrived" guard removed **1**; costs not charged **2**; the sell direction not flipped
  **2**; HOLD charged a round trip **1**; "not due" collapsed into "not measurable" **1**.

  *Nothing runs it.* No configuration, no endpoint, no job - which is the point: the roadmap
  asks for pure functions written test-first, and wiring would have made them harder to
  write, not easier. 254 .NET tests green.

- **PR `stage-4-measure`** (four commits, 2026-09-24): how the engine gets bars. The plan's
  PR 5 was five commits, so it was split: this half is the inputs, the next is where
  measurements go.

  - **Three decisions.** Split the pull request in two; a signal that can never be measured
    still gets a row (next half); the job will be a second `BackgroundService` in the
    engine's own process, which is the second writer the `xmin` row version was put in for.
  - **`GET /v1/quotes/{symbol}/history?from=` is a trading calendar as much as a price
    series.** An empty array is a good answer rather than a 404 - it means nothing has traded
    since that date, which the calculator already reads as a horizon that has not passed.
    `from` is required, because an unbounded history would be a different, larger endpoint
    every time the provider's window changed.
  - **The mapper is where a wrong measurement is stopped.** It refuses a close of zero, days
    out of order or repeated, a field the contract does not have, and a history about another
    instrument - which would otherwise be measured as if it were this one.
  - **The `Outcome` section is separate from `RiskPolicy` on purpose.** One says what may be
    traded, the other how what was traded is judged. Commission and spread are nullable and
    required, because zero commission is a real arrangement; the band is not, because a band
    of zero is not.
  - **One basis point of commission and two of spread**, per side, so six basis points a
    round trip. A plausible figure for a liquid US large cap and nothing more - the first
    thing to replace when there is a broker.

  *Mutation testing:* the window filter removed **2** (Python); the instrument check removed
  **1**.

  *Verified live:* nine SPY bars from 14 to 24 September with the weekend of the 19th and
  20th simply absent - the trading calendar working on real data rather than on a fixture. A
  future window answered with an empty array, a missing window 422, a date that is not a date
  422. 271 .NET tests and 270 Python green.

- **PR `stage-4-outcome-job`** (four commits, 2026-09-24): the sweep, the table and the
  report. **The machinery of stage 4 is complete after this**; what was left was written here
  as waiting, which finding G later corrected.

  - **Three decisions.** A signal that can never be measured gets a row; one row per
    (decision, horizon) with a unique index; the sweep is a second `BackgroundService` that
    runs at startup and then on its interval.
  - **Three results, three behaviours.** Scored, abandoned with a reason, or left for the
    next sweep. There is a test where a horizon is not due on Monday and measured on Tuesday,
    and another where an abandoned one is never asked about again.
  - **An outage is not a hole.** An instrument whose history could not be fetched waits;
    writing "unmeasurable" would stop anyone ever asking again about a signal that is
    perfectly measurable tomorrow. Without the benchmark the sweep stops rather than filling
    the table with rows that say the index was down.
  - **The window starts ten days early.** A comparison needs the benchmark's last close on or
    before the signal, and a signal made on the Friday of a long weekend has no such bar in a
    window beginning on its own date.
  - **`trading.hit_rate` repeats `ConvictionTier`'s thresholds in SQL**, which is a
    duplication worth having - a report needing a deploy to change is a report nobody runs -
    and it is guarded by a test that drives each boundary from both sides.

  *Mutation testing:* the view's conviction boundary moved **1**; not-measurable rows not
  written **2**; the signal filter removed **1**. That last one took three attempts: the
  first two mutations did not change behaviour at all, which is its own reminder that a
  mutation has to bite to mean anything.

  *A claim from the plan was wrong.* The sweep is **not** the second writer the portfolio's
  row version was put in for: it writes only `signal_outcomes` and never touches the
  portfolio. Corrected rather than tested around, and replaced by a test that says the sweep
  moves no money. The row version still has no contender.

  *Verified live:* one sweep, 68 horizons across 17 signals, every one not due because every
  signal was made that day - and exactly three history calls for AAPL, MSFT and SPY, with the
  window at the signal date less ten days. 286 .NET tests and 270 Python green.

- **PR 6a - Python owns its schema, and the two sides exchange measurements** (branch
  `stage-4-agent-schema`, 2026-09-25). Four commits, and the first three touch no prompt at
  all, which is the reason the pull request was split: `team_version` is unchanged, so none of
  this disturbs the baseline that has just started to accumulate.

  - **Alembic for the `agent` schema.** `agent_memories` was created by
    `db/init/01-schema.sh`, which runs once on an empty volume, so every change to the agents'
    schema so far meant destroying the database. The init script now creates **no tables at
    all** - only the extension, the roles, the schemas and who owns them - which is what makes
    "runs once" harmless rather than a trap. A fresh volume needs `uv run alembic upgrade head`
    before the agent service has anywhere to write; the existing database was `alembic stamp`-ed.
  - **Python has database tests at last.** A throwaway pgvector container built by the
    checked-in init script, connected to as `agent_svc` - the same shape as the engine's
    fixture, and for the same reason. Every test downgrades on its way out, so "migrations are
    tested both ways" is a property of the suite rather than of one deletable test.
  - **`agent.analysis_runs` and `agent.step_outputs`** hold the fact sheet an analysis started
    from and each step's answer as `jsonb`. Append-only, unique on the correlation id. The
    journal write happens *after* the signal is built and **cannot withhold an answer**: a
    failure is an error line, because the engine cannot tell a storage failure here from the
    agents failing, and raising would stop trading over bookkeeping.
  - **`POST /v1/outcomes` and `agent.signal_outcomes`**, the copy of what the engine measured.
    No foreign key to `analysis_runs` - the engine measures decisions this service never
    produced a signal for - and `ON CONFLICT DO NOTHING`, so a retry is safe. Here a failure
    *is* reported, which is the opposite of the journal: nothing is waiting for the answer, and
    the engine can send it again.
  - **`trading.outcome_deliveries`** on the engine's side, so delivery is a state rather than a
    moment. Post, then mark, then commit: a failed post leaves no markers and the next sweep
    sends the same batch. Without it a thirty-second outage would cost a day of evidence,
    because a sweep measures only what is still unmeasured.
  - Also capped `team_id` and `correlation_id` at 64 characters in the contract. Both services
    already stored them in a `varchar(64)`; only one side knew.

  *Mutation testing:* the journal's transaction removed **1**; marking deliveries before the
  post instead of after **1**. Two mutations did **not** bite, and both taught something:
  deleting `version_table_schema` from `env.py` changes nothing, because `agent_svc`'s
  `search_path` already resolves to `agent` - so the comment claiming Alembic defaults into
  `public` like EF Core was simply wrong and was rewritten. And sending floats straight into
  the `numeric` columns instead of converting them to text first changes nothing either,
  because the engine rounds to six decimals and the columns hold six. That defence was
  deleted rather than kept.

  *Reviewed externally, 2026-09-25*, verdict "approve with nits" and no blockers. Two
  findings were fixed on the branch. The first was a **stale docstring** in
  `test_outcome_store.py` still explaining the float-to-text conversion that the mutation
  test had caused to be deleted - which is worse than no explanation, because the docstring
  is what a reader reaches first. The second the review did not raise and is the more serious
  of the two: **the batch cap 500 existed in three places** - the engine's constant, the
  agent service's, and the contract's `maxItems` - with each side testing only its own copy.
  Drift there is not a slow recovery but a **stop**: the engine would send the same oversized
  batch every sweep, take a 422, mark nothing and repeat, silently apart from one error line a
  day. Both constants are now asserted against the checked-in contract, which is the same
  guard `trading.hit_rate`'s conviction thresholds already had and this duplication had not.
  Lowering `maxItems` turns both suites red, which is how it was checked. Three findings are
  deferred to 6b and written up under *Next up*.

  *Verified live:* the engine and the agent service both running, against the real database.
  One sweep **measured 21** signals at one trading day, 63 not due, and delivered all 21 -
  21 rows in `trading.signal_outcomes`, 21 in `trading.outcome_deliveries`, 21 in
  `agent.signal_outcomes`, and `trading.hit_rate` populated for the first time. Python's
  journal picked up 8 runs and 24 step outputs from the same session. 302 .NET tests and 309
  Python green, all of them re-run in a throwaway worktree of tracked files only.

- **PR 6b - the memory, and the team that reads it** (branch `stage-4-memory`, 2026-09-25).
  The last pull request of the stage.

  - **`agent_memories` is retired.** It held a ticker, a stance, a piece of reasoning and a
    vector; three of those had been in the journal since 6a, and the one thing it lacked -
    the correlation id - is what joins an analysis to what happened afterwards. Replaced by
    `agent.analysis_embeddings`: one vector per journalled run. A table beside
    `analysis_runs` rather than a column on it, because an embedding is **derived data, not
    evidence** - the journal is append-only and embeddings have to be rebuildable when the
    model changes. The model's name is stored on every row, since two models in one index
    is a similarity score that means nothing.
  - **Only measured analyses reach an agent.** `recall` joins through `signal_outcomes` with
    an inner `LATERAL`, so a run nobody has scored takes none of the three places. Memory is
    therefore empty - and says so - until a horizon passes. That was the decision, and the
    reason is that reasoning without an outcome teaches a model to agree with itself.
  - **What is embedded is the analyst's reading, at both ends.** One function, used to embed
    a finished run and to query with today's reading, so the symmetry cannot be edited
    apart. A vector written from one kind of text and queried with another is a number that
    looks like a similarity and is not one.
  - **`sees_memory` on `StepSpec`**, a flag rather than an entry in `reads`, because `reads`
    names schemas produced inside the run and memory comes from outside it. `TeamSpec`
    refuses a step that asks for memory without reading `MarketRead` - it would have no
    query to recall with.
  - **`default-memory`**, the baseline team with one thing added. Two of its three steps read
    **`default`'s own prompt files**, not copies, so a difference in outcomes has one
    candidate explanation instead of three.

  *A hole found by starting the service and reading the log:* `default`'s `team_version` had
  changed although nothing about the team had. Adding `sees_memory` to the hash payload wrote
  `sees_memory: false` onto every step of every team. The payload is now **sparse** - a
  handover flag appears only when it is set - so adding a flag nobody uses re-hashes nothing.
  The old hash cannot be restored, because the original payload carried `sees_position:
  false` on two steps; reproducing it would mean keeping one flag always-present as a
  historical exception, and that comment would be an apology rather than a reason.

  *Mutation testing:* the measured-only join turned into a LEFT JOIN **1**; the memory key
  added unconditionally **1** (four tests, two of which predate memory); `sees_memory`
  deleted from the version payload **1**; the payload made dense again **1**.

  *Reviewed externally, 2026-09-25*, verdict "accept with nits" and no blockers. Five things
  fixed on the branch, and one deferred.

  - **The journal's error line was lying.** `remember` shared the journal's `try`, so a
    failed embedding - an embedding model reached over the network, which fails routinely -
    logged "was not journalled, so its working is lost" although the row was sitting there.
    That line is not decoration: it is how a hole in the journal is found at all, by a
    correlation id the engine has in `decisions` with no run on this side. Reporting an
    embedding failure as one sent a reader looking for something that was not missing. Two
    `try` blocks now, with two severities - error for evidence, warning for derived data
    that can be rebuilt.
  - **`recall`'s promise was wider than its code.** The docstring said a memory that cannot
    be fetched must not end an analysis; the `try` covered the fetch and not the formatting
    of what came back. No reachable crash today - `thesis` is required on `TradeView` - but
    the rows being formatted are written by *older versions of this service*, which is
    precisely the material that stops matching the code that reads it. The whole of it is
    inside the `try` now, and a test stores a TradeView with no thesis to prove it.
  - **The model column was stored and not used.** The migration explains that two embedding
    models in one index give a similarity score that means nothing - and then `recall` did
    not filter on it. A motivation written down and its consequence not implemented, which
    is the same shape as the batch cap in 6a.
  - `Resources.memory` was typed as the concrete class while the two fields around it used
    their ports. The port gained `ping`, which the readiness probe needs: whether a store
    can be reached is a fact about the capability, not about the class behind it.
  - Two stale documentation claims, one of which the review missed: `CLAUDE.md` still named
    `agent.agent_memories` as the agents' memory, and this file still listed a `TEST` row in
    a table that no longer exists.

  *Deferred:* a backfill job for embeddings. The table exists so vectors can be rebuilt when
  the model changes, and nothing can rebuild them. It buys nothing today - the eight runs
  that lack vectors also lack measured outcomes, and `recall` needs both - so it is a task
  of its own rather than a line in this pull request.

  *Mutation testing:* the model filter removed **1**; the formatting moved back outside the
  `try` **1**; both failure paths logging the same sentence **1**.

  *Worth stating about what memory will actually do:* it is filtered by instrument, and only
  measured analyses count. At stage 5's scale - thirty to fifty instruments, each analysed
  about once a trading day - each instrument accumulates measured analyses slowly, so memory
  stays near-empty per instrument for weeks. The code landing is not the same thing as the
  feedback loop starting.

  *Verified live:* both teams built at startup with distinct versions, the engine run once
  under `Trading__TeamId=default-memory`, 8 runs stored under `default-memory` /
  `79dfb7307b57` and 8 embeddings written. `recall` through the real path - real pgvector,
  real Ollama embedding - answered *"Inga tidigare analyser av AAPL har hunnit mätas
  färdigt."*, which is right: none of the embedded runs has a measured outcome yet. And a
  fact worth knowing, from a diagnostic query rather than a guess - **the first 21
  measurements can never become memory**, because the analyses behind them predate the
  journal. Memory starts from the runs journalled since 6a.

---

## Stage 5 log (2026-09-25 ->, in progress)

- **PR - the model, the clock and the horizon** (branch `stage-5-model-and-horizon`,
  2026-09-25). Not stage 5's own work: the thing finding G said to settle before it, while
  four independent events was the whole cost of moving `team_version`. Five commits: three,
  then two answering a review.

  - **The horizon is capped at 30 calendar days**, in pydantic, in the schema and in the
    engine's mapper, with the range named in the prompt. 26 of 36 stored signals had asked
    for 180 days or more. Three reasons, one direction: the system looks for short-term
    opportunities, 30 days is about the 20 trading days of the longest fixed horizon, and
    stage 5's time-limit exit fires on this number - at 365 it is a rule that never fires.
    `contracts/outcome.schema.json` stays uncapped and now says why.
  - **`qwen2.5:14b` at temperature 0 with seed 42**, after `qwen3:14b` was tried and
    rejected on measurement. The detail is under *Before stage 5*; the short version is that
    a thinking model costs five times the wall clock for reasoning this system never stores,
    and cannot have it turned off through the endpoint that carries a timeout.
  - **Timeouts moved with it.** 30 s per call was below a single step on any 14B model.
  - *Mutation-tested:* the engine's cap changed from 30 to 365 turns exactly two tests red;
    the Python constant drifting from the schema turns the agreement test red; the field
    ignoring the constant turns the behaviour test red. The last two fail on different
    mistakes, which is why both exist.
  - *Verified live:* 307 .NET and 346 Python tests green, re-run in a throwaway worktree of
    tracked files only; a real `POST /v1/signals` answered in 28.4 s cold and 19-20 s warm,
    asking for a 15 day horizon; three identical requests answered SELL, SELL, SELL at
    conviction 0.85/0.80/0.80.

  *Reviewed externally, 2026-09-25*, verdict "accept with nits" and no blockers in contract
  or wiring. Everything raised was either fixed on the branch or recorded; nothing was
  deferred to a later pull request.

  - **The worklog contradicted itself about its own corrections**, which is the finding worth
    keeping. The commit that introduced the contradictions argued in its own message that a
    resume document describing a plan which no longer holds is one you stop trusting - and
    then corrected four places and left six. Two of them named `b1234878670a` as `default`'s
    version, a hash this session had already proved exists in no row. Writing a new sentence
    beside a stale one leaves the file *worse* than it was, because now a reader has to
    decide which to believe. Fixed in the commit above; the sixth was found by re-running the
    reviewer's own check over the whole file rather than the parts I had touched.
  - **A test that fails on a correct change.** The seam test hardcoded `31` and `"past the 30
    day limit"`, so moving the cap - consistently, in all three places - turned it red on a
    string. Both now come from `MaxHorizonDays`. The mutation that proves it is the one where
    *everything* stays green.
  - **`Trading:CycleIntervalSeconds` was called a structural mismatch**, and it is not:
    `TradingWorker` delays *after* the loop over tickers, so nothing overlaps and nothing
    queues. The period is simply (analysis time x tickers) + 15 s, which the model change
    moved from about 30 s to 55-70 s. So the number no longer describes the cadence, which is
    a documentation problem and count 2 of finding G - recorded under *Open decisions*, not
    bumped, because stage 5's third pull request deletes the cadence.
  - **The stale-`.env` hole** was the most useful thing in the review after the worklog. No
    setting has a default, which makes an incomplete environment fail fast but makes a *stale*
    value silent: an `.env` from before today still says `llama3.2`, and the service starts on
    it happily, running a third team that matches neither the docs nor the current hash. The
    resuming checklist now diffs the two files and says why.
  - **Not acted on:** the git author identity, at the owner's instruction.

---

## Lessons and gotchas

Things that cost time or were not obvious. Most are also recorded where they apply.

**Keeping this file honest**
- **Correcting a document means finding every place, not the places you remember.** A commit that argued this point in its own message then corrected four claims and left six, including two naming a `team_version` the same session had proved existed in no database row. A new sentence beside a stale one is worse than the stale one alone, because a reader now has to pick. Grep the specific phrase and the specific value across the whole file, then read the neighbouring bullets, before claiming a correction is done.
- **A test that fails on a correct change is a liability.** Hardcoding both the input and the expected message (`31`, `"past the 30 day limit"`) meant a consistent cap change turned it red for a string reason, while the test actually guarding consistency stayed green. The mutation worth running is the one where everything should stay green - that is what tells you a test is pinning behaviour rather than pinning a literal.
- **"Structural" is a strong word to check before repeating.** A review called the cycle interval a structural mismatch; `TradingWorker` delays after the loop, so nothing overlaps or queues. The finding was real, the severity was not, and accepting the framing would have bought a config change that stage 5 deletes.

**Models and Ollama**
- **`Win32_VideoController.AdapterRAM` is a 32-bit field and saturates at 4 GB.** It reported 4 GB for a 16 GB card, which would have ruled out every model worth switching to. `HardwareInformation.qwMemorySize` under the display class key in the registry is the real number. Two weeks of treating the machine as smaller than it is.
- **A thinking model's thinking cannot always be turned off.** `qwen3:14b` on Ollama 0.34.4 ignores `/no_think` in the prompt and accepts `chat_template_kwargs: {"enable_thinking": false}` on `/v1` without applying it - accepted, not refused, which is the worst of the three outcomes. Only the native `/api/chat` honours `"think": false`. Check the `reasoning` field's length rather than trusting the switch.
- **Ollama separates thinking from content on `/v1`.** `message.reasoning` holds it and `message.content` stays clean JSON, so structured output works with a thinking model. The cost is wall clock and tokens, not correctness.
- **Temperature 0 plus a seed is not reproducibility.** Repeating an identical request gives byte-identical output; inserting a *different* request in between changes the bytes, because the numerics depend on batching and KV-cache state outside the request. Test it by interleaving, not by repeating - repeating alone will tell you it is deterministic.
- **Raise the timeouts before switching to a larger model, not after.** 30 s per call was below a single step on any 14B, so the first run would have been a wall of 504s that looks like a broken integration.

**Python and Alembic**
- `alembic init -t async` is the *fewer*-dependencies option here, not the more: asyncpg is already a dependency, whereas a synchronous engine would mean adding psycopg for migrations alone. It does need `sqlalchemy[asyncio]` for greenlet.
- `fileConfig(config.config_file_name)` defaults to `disable_existing_loggers=True`, which switches off every logger already created in the process. Harmless for the `alembic` command, wrong inside a test suite. Pass `disable_existing_loggers=False`.
- pytest-asyncio's auto mode has an event loop running by the time a fixture is set up, so `command.upgrade` - which calls `asyncio.run` inside `env.py` - raises. Run Alembic on a thread of its own rather than changing `env.py` to suit the tests.
- `alembic -x url=...` reaches `env.py` through `config.cmd_opts.x`, so a test supplies it as `Config(..., cmd_opts=Namespace(x=[f"url={dsn}"]))`. Setting `config.attributes` instead would need a second code path in `env.py` that exists only for tests.
- Testcontainers for Python has `with_copy_into_container`, which is the equivalent of .NET's `WithResourceMapping`: it copies bytes after `create` and before `start`, so an initdb script arrives in time. A volume mount would make the test depend on a path on this machine.
- Waiting for "ready to accept connections" in the log races: the official Postgres entrypoint prints it once before the init scripts run and once after. Polling until the *role the script creates* can connect is a stronger signal.

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
- C# has no "init block": validation cannot go in a positional record's constructor body, and `public MyRecord { ... }` is a syntax error rather than the Kotlin-shaped thing it looks like. The rule belongs in an `init` accessor anyway, because a `with` expression bypasses the constructor - and that is now a test rather than a comment.
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
- **Entering `TestClient(app)` as a context manager runs the app's lifespan**, which opens a database pool and probes the LLM backend. A unit test must *construct* the client instead. The mistake is invisible on this machine, where both are up, and fails only in CI - eleven errors at setup, with the real reason ten frames down.
- **CI is reproducible locally with a throwaway worktree:** `git worktree add --detach <tmp> HEAD` gives a tree with only tracked files, so no `.env`, no `.venv` and no build output. Running `uv sync --locked && uv run pytest` there is what CI actually does, and it catches anything that only passes because this machine has something CI does not.
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
