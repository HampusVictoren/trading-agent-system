# Worklog

A running record of what has been done, what was learned along the way, and what comes next. It sits between two other documents:

- [`docs/arkitektur-roadmap.md`](arkitektur-roadmap.md) (Swedish) is the plan: the decisions, the stages, and how each stage is verified.
- [`CLAUDE.md`](../CLAUDE.md) describes the repo as it is today: commands, environment, architecture.

This file answers "where are we, how did we get here, and what is next". When resuming, read *Current state* and *Next steps* first, then the roadmap section for the next stage.

**Last updated:** 2026-10-09. **Stage 6 is done**, with all five pull requests merged (#58-#62).
**Stage 7 has started.** Its first pull request is `stage-7-trading-mode`, which adds
`Trading:Mode` (Shadow by default) and a kill switch in `trading.kill_switch`. *Current state* and
*Next steps* are current. The paragraphs directly below are the 2026-10-01 record of stage 5 and
are kept as they were written.

**2026-10-01: Stage 5's fourth pull request is merged as #51, and verified
live.** The engine no longer trades a list somebody typed: it screened 31 OMXS30 names with no LLM
call, analysed the best ten plus the one holding, and **bought four of them on its own** -
HEXA-B.ST, SEB-A.ST, EVO.ST and KINV-B.ST. The two cycles that followed cost **zero** LLM calls,
because an instrument is now analysed once a trading day. See *The live run* under the stage 5 log.

**That closes the last count of finding G.** The system had been asking two hand-picked tickers the
same question every fifteen seconds; it now asks eleven instruments it chose itself, once a day
each. The population a baseline needs exists as of today.

**Stage 5 is complete and merged**, all five pull requests: #42, #43, #44/#45, #48, #51 and #54,
with the security hardening as #52 and the live run as #53. **The stage is not verified**, though:
two of the roadmap's five conditions need the market to move against a position, and the first
`shortlist_edge` row needs a horizon to pass. Read *Next steps*.
**Stage 4's code is complete and merged** - all eight pull requests. The machinery runs end to end: decisions are stored, the portfolio survives a
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

- **Stage 0 done** 2026-09-19, **stage 1 done** 2026-09-20, **stage 2 done** 2026-09-21, **stage 3 done** 2026-09-23. **Stage 4 started** 2026-09-23. PR 5 and PR 6 were each split in two, so the stage is eight pull requests, and **all eight are merged** as of 2026-09-25. **Stage 5 is merged in full** - #42 to #51, the shortlist edge as #54 and the daily deployment cap as #56. **Stage 6 is done**: all five PRs are merged (#58-#62). **Stage 7 is in progress**: PR 1 is `stage-7-trading-mode`, with five more planned (see the stage 7 log). Stage 8 exists only as a plan.
- **`master` is at PR #62** (the compose bring-up in CI, merged 2026-10-09). All five of stage 6's pull requests are on it, plus Dependabot's seven Python bumps as #57 - which merged *after* #58. Stage 4's eight pull requests and all five of stage 5's are on it, plus the live-run record as #53, the security hardening as #52 and the `CLAUDE.md` levelling as #55. **Which CI jobs block a merge is branch protection**, and three were added during stage 6 - `Agents (image)`, `Engine (image)` and `Compose`. This file has twice assumed the required list keeps up with the workflow; it does not, and nobody here can read that setting. Check it under *Settings - Branches*.
- **Dependabot's bumps are in.** #46 (setup-uv) and #47 (six Python packages) merged to `master` on 2026-09-26 and were merged *into* `stage-5-selling` rather than rebased onto, because the branch was already pushed and a rebase would need a force-push. Two of the six matter behaviourally - **ag2 1.0.5 to 1.0.6** and **openai 3.16.1 to 3.19.1** - and the lock also *downgraded* SQLAlchemy from 2.1.1 to 2.0.54, which the Alembic fixture exercises on every database test. 476 Python tests green on all of it.
- **Stage 5's merge history:** `stage-5-model-and-horizon` as #42, `stage-5-screening` as #43, `stage-5-sek` as #44, `stage-5-symbol-columns` as #45 - the last of those repairing #44, which merged four of its six commits and left `master` with the widened pattern and the old columns for a few minutes - `stage-5-selling` as #48 and `stage-5-cycle` as #51.
- **Two pull requests are open as of 2026-10-09:** stage 7's PR 1 (`stage-7-trading-mode`) and #50, `plan/jev-placement` - a docs-only branch adding `docs/arkitektur-jev-roadmap.md`, **not reviewed here, and left to the owner** - nothing in this repository's work merges, rebases or pushes to it. **Four** remote branches are stale and can go, all merged or superseded: `security/hardening-f01-f14` (the superseded first attempt at #52), `docs/pr4-live-run` (#53), `stage-5-shortlist-edge` (#54) and `stage-5-cycle` (#51). The previous version of this sentence said five and named `security/hardening-master-bdf5bf1`, which was deleted long ago - checked against `git ls-remote` this time, because the one thing the list promises is that deleting them is a reading exercise rather than a search.
- **The security hardening is merged as #52 and verified live** on 2026-10-01: `POST /v1/outcomes` accepted its HMAC signature end to end (`Delivery 8097ab0f: sent 1 outcomes`), 25 market-scope calls answered 200, `/openapi.json` and `/docs` both answer 404, `/ready` returns `{"status":"ready"}` with no dependency detail, and an unauthenticated signal is refused with 401. Nothing was rate-limited. **Both of its required secrets had to be created first** - neither existed after the merge, so neither service would start.
- **Two of its paths are not verified live:** `POST /v1/signals` and `POST /v1/screen` with their scope keys, because the verification ran on the same trading day as the live run, so no analysis was due and the shortlist was read from the database. All five call sites go through the same `AddApiKey`/`ApiKeyFor` code and no scoped keys are configured, so all five resolve to the legacy key that the 25 successful calls used - but the first new trading day is what proves it, and that is where the blocker found in review would have shown.
- **The portfolio holds five instruments as of 2026-10-01**, up from one: ERIC-B.ST (26 at 94.96, from 2026-09-26) plus HEXA-B.ST (25 at 99.02), SEB-A.ST (10 at 229.20), EVO.ST (3 at 791.80) and KINV-B.ST (40 at 61.70), all four bought by the engine's own first screened cycle. Cash is 87 920.14 kr. Every one of the four is a *measurable* decision with `selection = Shortlist`, which is what stage 5 existed to produce.
- **`trading.shortlists` has its first row set**: ten candidates for 2026-10-01, no rejections. The 43 decisions that predate the column are backfilled as `FixedList`, so the two populations never pool - which is the whole reason the column exists.
- **Stage 5 gained a fifth pull request**, split out of the fourth on 2026-09-27: the shortlist as a benchmark. It was split on the belief that it needed a new measured population, which turned out to be false - stage 4 already scores every signal, bought or not - so it is `trading.shortlist_edge`, a view, rather than a worker change. The split was still right: it touches no decision path and cannot be verified until a horizon has passed, so it reviews on its own.
- **The account and the universe are Swedish, and there is no currency conversion anywhere.** `Money.DefaultCurrency` is SEK, the universe is 31 OMXS30 names, and outcomes are measured against XACT-OMXS30.ST. The opening balance is 100 000 kr, which is what makes the conviction tiers differ in share counts rather than both rounding to one. The 36 USD decisions and 21 SPY measurements from before the move are **kept**: `decisions.reference_currency` and `signal_outcomes.benchmark_symbol` make them self-describing, and finding G's reproduction reads them.
- **The system finds candidates and acts on them now.** `POST /v1/screen` ranks a universe with no LLM call at all: risk-adjusted momentum from bars, filtered on liquidity, with everything it left out named and the reason attached. The stage's stated practical risk turned out to be a measurement rather than a worry - **50 instruments in 2.0 seconds**, the same as 8, because `yf.download` batches and the ranking asks for no fundamentals. As of #51 the engine drives the cycle from it, and as of 2026-10-01 it has done so against the real universe.
- **`b1234878670a` never reached a row, and that is worth knowing rather than forgetting.** On 2026-09-25 `default`'s hash moved from `6c6da0e6edad` to `b1234878670a` without a word of the team changing: adding `sees_memory` to the payload wrote `sees_memory: false` onto every step. The payload was made sparse the same day, before the engine ran again, so the accidental hash was never stored - `SELECT team_id, team_version, count(*) FROM trading.decisions` returns only `6c6da0e6edad` and `79dfb7307b57`. Nothing has to be pooled and nothing has to be written down; the earlier warning in this file that the two hashes had to be reconciled by hand is obsolete, not wrong at the time. The lesson survives the hash: a version payload that lists every flag with its default ties the version to the shape of the payload rather than to the team.
- **There is one path now.** `POST /v1/signals` is the only endpoint that costs money, the engine calls it every cycle, and the old three-agent chain, `InvestmentProposal`, `ValidateTrade`, `RiskViolationException` and the FastMCP server are gone. Running the engine today produces real quantities at real prices, with the position cap holding across cycles.
- **Every environment variable was renamed on 2026-09-23.** The local `src/agents/.env` was renamed in place and still works; a fresh clone follows `.env.example`. Nothing outside this repo reads them.
- **Measurement has produced its first numbers.** A sweep on 2026-09-25 **measured 21** signals at one trading day, left 63 horizons not due, abandoned none, and delivered all 21 to the agent service. `trading.hit_rate` has four rows. They mean nothing yet, and it is worth saying so plainly: one trading day is noise, and all sixteen BUYs "hit" because both names happened to rise that day. The two HOLDs that missed did so with an excess of 3.5 %, which is the band doing its job rather than the model doing well.
- **Both schemas now hold a copy of the same measurement**, joined by nothing: 21 rows in `trading.signal_outcomes`, 21 in `trading.outcome_deliveries`, 21 in `agent.signal_outcomes`. The correlation id is the only thing they share, and it crosses over HTTP.
- **Python writes down what its agents were given.** `agent.analysis_runs` and `agent.step_outputs` hold the fact sheet an analysis started from and each step's answer - 8 runs and 24 step rows after one engine session, which is three steps per run exactly as the team specifies.
- **Memory is wired in and was correctly empty when PR 6b landed.** 8 runs were embedded then; none had a measured outcome, so `recall` answers *"Inga tidigare analyser av AAPL har hunnit mätas färdigt."* - which is the designed behaviour rather than a fault. Worth knowing: **the first 21 measurements can never become memory**, because the analyses behind them predate the journal. Memory starts from the runs journalled since PR 6a, and the first becomes recallable when its one-trading-day horizon is measured.
- **There are two teams now.** `default` is the baseline and reads no memory; `default-memory` is the same team with its risk manager shown past measured analyses. For their versions, which moved with the model on 2026-09-25, see the bullet above rather than a copy here - two places holding the same hash is how this file came to assert one that existed nowhere. `Trading:TeamId` stays `default`; the memory team was run once by environment override to prove it works, which is where those 8 embedded runs came from.
- **The model asked for horizons of a year, and no longer can.** The full distribution over all 36 stored signals was 6 days (3), 30 (1), 90 (6), 180 (**15**) and 365 (**11**) - 26 of 36 at half a year or more, although the system looks for short-term opportunities. Nobody had told it otherwise: the prompt said "be honest" without naming a range and the schema allowed 365. Capped at 30 on 2026-09-25, in pydantic, in the schema and in the engine's mapper, with the range named in the prompt. The first live signal afterwards asked for 15.
- **The model is `qwen2.5:14b`**, at temperature 0 with seed 42, replacing `llama3.2` (3B). A cycle is about 20 s warm and 28 s cold, against 7-10 s before. `TAS_LLM__DEFAULT__TIMEOUT_S` is 60 and `AgentService:RequestTimeoutSeconds` is 120; the old 30 was below a single step on any 14B model.
- **Two team_versions are in the data; two more are only in the code.** `default` ran 28 decisions as `6c6da0e6edad` and `default-memory` 8 as `79dfb7307b57`. The model change makes them `5926c629dcbe` and `b856e3edf611`, but the engine has not run since, so neither has a row yet. The stored rows keep their old values, which is the point of putting the version on the row - and the distinction between a version that exists and one that has been *used* is what this file got wrong about `b1234878670a` above.
- **The engine trades a screened shortlist, not a list.** `Trading:Tickers` was deleted in #51: a cycle is now the portfolio's holdings plus the ten best of 31 OMXS30 names, each analysed at most once a trading day. The quote endpoint is used for the exits every cycle and for the fact-sheet rule when a day is new.
- **The `trading` schema is live and has real rows in it.** The account was opened at 10 000 USD on 2026-09-24, moved to SEK in #44, and holds 100 000 kr of opening balance with five Swedish positions as of 2026-10-01. As of 2026-10-08 the local database had **all eleven** engine migrations and all **five** Alembic revisions applied, so it was level with `master` then. **Stage 7's PR 1 adds four more** (`DecisionTradingMode`, `KillSwitch`, `DecisionShadowCost`, `ShortlistEdgeByMode`), so there are fifteen after it merges, and they apply with `dotnet dotnet-ef database update` as usual. Both numbers in the previous version of this sentence were wrong - twelve and three - which is the hazard this file keeps rediscovering: a count written into prose is a count nobody updates. Stage 6's CI now reads the engine's off the migration files. The USD rows from before the move are kept and self-describing - `decisions.reference_currency` and `signal_outcomes.benchmark_symbol` say which world each belongs to. Clear everything with `TRUNCATE trading.shortlists, trading.signal_outcomes, trading.decisions, trading.orders, trading.positions, trading.portfolios RESTART IDENTITY CASCADE` if a clean baseline ever matters; the append-only triggers deliberately do not block that.
- **The engine now needs `Database:ConnectionString`** or it refuses to start. It is in the user secrets store on this machine, set 2026-09-23. `dotnet user-secrets list --project src/engine` prints it, so do not run that where anyone can see the screen.
- **Migrations are applied by hand, and the engine refuses to start without them.** Decided 2026-09-24: `dotnet dotnet-ef database update` stays a deploy step, but startup names the pending migrations and the command instead of failing on a missing column mid-cycle.
- **What is now impossible** rather than merely unlikely: the agents cannot name an amount (the contract has no `amount_usd`, and a test refuses one that reappears); an answer that is not the contract cannot deserialise into nulls; a position cannot be sized against cash instead of net asset value; an order cannot be placed on a quote that is stale or dated in the future; nothing can sell shares it does not hold, or sell a holding the agents bought less than three days ago on a new opinion; and a HOLD cannot extend the clock the exits read, because only a purchase moves it.

## Next steps

### Resume here — stage 7, PR 1 of 6 (2026-10-09)

Stage 6 is merged in full: #58-#62. **Stage 7 has started.** Read that stage in the roadmap
(*Etapp 7*) and the stage 7 log below before continuing. Five measurements changed the plan:
there is no broker, so Live has nothing to do; rate limiting already exists; and the engine's
healthcheck belongs with the metrics.

1. **`stage-7-trading-mode` is the branch in hand.** It adds `Trading:Mode` (Shadow, Paper, or
   Live refused at startup), records the mode on every decision, and adds a kill switch: the
   append-only table `trading.kill_switch`, read at cycle start, before each analysis and
   immediately before each order, failing closed. It brings four migrations.
   **Before it merges, set `Trading:Mode` to `Paper` on the machine that runs the engine**
   (`dotnet user-secrets set Trading:Mode Paper --project src/engine`, or `TRADING_MODE=Paper`
   in the root `.env`). Otherwise the engine restarts in Shadow, and Shadow places nothing,
   **including the stop-loss and time-limit sales of the positions it already holds**. It warns
   about that at startup, but a warning does not close a position.
   Review Bot accepted it with nits on 2026-10-09, and they are fixed on the branch (see the
   review section in the stage 7 log).
2. **Next: the engine's metrics and the cycle trace** (PR 2), then Python tracing (PR 3), which
   is the PR the stage's check reads.
3. **Six decisions were the owner's**, and they are listed in PR 1's description:
   - whether Live should ever exist;
   - lifting D2 (the engine behind `--profile trade`);
   - the deploy target;
   - the running engine goes to Paper before the merge (decided: Hampus sets it himself);
   - whether the kill switch should stop exits;
   - whether `shortlist_edge` should count a shadowed buy as the agents' pick (it is now
     grouped by mode, so the question is only what a Shadow row's `bought` means).

**Stage 6's closing note, kept for the record:** the compose job's bring-up had not run on
`stage-6-compose-smoke` before merge, because dispatching it needed *Actions: write*. The token
has that permission now.

---

### Resume here — stage 6, PR 5 of 5, the last (2026-10-08) (superseded)

Stage 5 is merged in full, follow-ups included: `CLAUDE.md` level as #55 and the trading day's
deployment limit as #56. **Stage 6 has started** - read that stage in the roadmap and the stage 6
log below before continuing, because it carries four decisions and three measurements that changed
the plan.

1. **PR 1 merged as #58.** The agent service as two images plus the module entrypoint whose
   first log line is JSON.

2. **PR 2 merged as #59.** The engine as two images, the Worker and the EF migration bundle.

3. **PR 3 merged as #60** on 2026-10-08. The whole system in one compose file, with the
   engine behind `--profile trade`. **The stage's success criterion is met and measured**:
   `docker compose up -d` from nothing is 31.8 s to a provisioned healthy system, 6 s more for
   the engine, four minutes for a first cycle. See the PR 3 section of the stage 6 log.

4. **PR 4 merged as #61**, built by another session and reviewed here - see *The review of #61*
   in the stage 6 log for what was verified and the two findings it left. The drift check of
   decision D3: the agent service's OpenAPI document committed as `contracts/openapi.json`,
   Python failing when it stops generating that file, and the engine failing when its DTOs stop
   agreeing with it. It also closes the `TAS_ENABLE_DOCS` defect.

5. **`stage-6-compose-smoke` is the branch in hand, and it is the stage's last.** `docker
   compose up` mechanised in CI, the `team_version` comparison PR 3 asked for, and #61's two
   findings. **After it merges, stage 6 is done** - next is stage 7, observability and drift,
   which the roadmap opens with OpenTelemetry and `TradingMode: Shadow | Paper | Live`. Read that
   stage before starting; the kill switch it brings is what lets the engine out from behind
   `--profile trade`.

**One claim in the previous version of this block was wrong, and it is worth saying which.** It
said the drift check was blocked on the `TAS_ENABLE_DOCS` defect, because `openapi_url` defaults
to `None` since #52. It is not: `create_app().openapi()` returns the whole document with no
server, no database and no flag, because the flag controls the *route* and not the generator. The
defect was real - `create_app` read `os.environ` and never `settings.enable_docs`, so only a shell
variable worked - and PR 4 fixes it, as planned. It was never in the way.

**The first `shortlist_edge` row needs the one-trading-day horizon to pass** on 2026-10-01's
eleven decisions, so it appears after the next sweep. When it does, read `agents_edge_gross`:
positive means the agents picked better than the ranking that handed them the ten candidates. One
row is an anecdote; the number means something after a few weeks of them.

**Branches are stale again** and the rule is master plus one. Deleting them is refused by this
session's tooling as a destructive git action, so it is two commands by hand - *Current state*
names the five, all merged or superseded. `plan/jev-placement` is not one of them: it is #50,
open, and not to be touched from here.

---

**Stage 4 - persistence and outcome measurement** is merged, all eight pull requests. **Stage
5 - finding candidates and selling** is next, in five pull requests; read that stage in
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

**Stage 5 goes out as five pull requests.** Three were decided 2026-09-25; the second was
inserted on 2026-09-26 when the account moved to kronor, because a contract rename and a
migration have no business sharing a review with selling logic. The roadmap calls the stage
3-4 days, which is too much for one review:

| | What | Why it is its own review |
|---|---|---|
| 1 | `app/screening/` and `POST /v1/screen` - a universe, the factors from `facts.py` over all of it, filters and a ranking | Pure functions over fixed datasets, TDD, and **no engine changes at all**. It can be run and judged before anything else moves. The stage's stated practical risk lives here: yfinance is unofficial and rate-limited, and fundamentals are fetched per instrument |
| 2 | **SEK end to end**: the symbol rule widens for Swedish tickers, the account currency becomes SEK, `available_risk_budget_usd` is renamed on the wire and in the database, and the engine points at Stockholm | A contract rename plus a migration. Inserted before selling because everything after it is written against whichever currency world exists, and because mixing it into the selling review would make it impossible to tell which change broke what |
| 3 | Selling: the SELL branch in `PositionSizer`, `Portfolio.ExecuteSell` with realised profit and loss, and the deterministic exits - stop-loss, time limit, minimum holding period | Test-first with the same table technique as stage 2. The exits are the half that does not depend on the LLM answering, which is decision 1 applied to selling. **In progress**, four commits: the aggregate, sizing and the gate, the exits, then the wiring |
| 4 ✅ | The engine drives the cycle: universe to shortlist, shortlist union holdings, one analysis per fact-sheet change, and the shortlist stored per trading day | This is where the regime column goes. **Merged as #51 and verified live** 2026-10-01 - see the stage 5 log |
| 5 | The shortlist as a benchmark: `trading.shortlist_edge`, the buys of a screened day against the shortlist itself | Split out of PR 4 on 2026-09-27, written 2026-10-01. It is where "do the agents beat the screening that picked their candidates?" becomes answerable. It turned out **not** to need a new measured population - stage 4 already scores every signal, bought or not - so it is a view rather than a worker change, and it cannot be judged until a horizon has passed |

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
- **Uvicorn's two pre-startup lines are JSON now** (fixed in stage 6's PR 1, 2026-10-01). The guess written here was a `--log-config` at deploy time; what it turned out to want was an entrypoint. `python -m app` configures logging and *then* calls `uvicorn.run(..., log_config=None)`, so uvicorn never replaces the handler and its own loggers propagate to it. A JSON log-config file would have been a second copy of the formatter, drifting from the first. The item stays here because the reasoning that kept it open - moving `configure_logging` to import time would reconfigure logging in the middle of pytest - was right, and the way out was a third option neither of us had listed.
- **The contract carries no currency.** Every price in it is USD and so is the portfolio. Fine until stage 5 widens the universe, and noted in `TradeSignalMapper`. It goes with finding D's remaining half.
- **`PositionSizer` and `RiskEngine.Evaluate` are registered but nothing resolves them.** Deliberate: registering them means the options-to-domain mapping is covered by `ValidateOnStart` now, and stage 3 becomes wiring rather than new code.
- **Memory is wired in as of PR 6b**, and `agent_memories` is gone with the `TEST` row that used to sit in it. `AnalysisMemory` reads the journal rather than a store of its own, and only the part of it the engine has measured.
- **The first account was opened on 2026-09-24** at 10 000 USD, and `trading` now holds real rows from a live run against `llama3.2`. They are a smoke test, not a baseline: the baseline starts when the outcome job in PR 5 exists.
- **`orders.placed_at` is a shadow property** filled by the database's `now()`. It is audit metadata today; stage 5 counts a holding period from the last purchase, and that is when it becomes domain data and has to come from the engine's injected clock instead.
- **Quotes share the analysis client's resilience policy**, which does not retry a failing response. That rule was written for a call costing 12-15 s of LLM time and is stricter than an idempotent GET needs; the cost of leaving it is one cycle without a price for one holding, and the cost of a second typed client is a second place for the key and the timeouts to drift. Revisit if missing quotes ever show up in the decision rows.
- **A quote for a symbol that is not a symbol answers 404, not 422.** FastAPI rejects it at routing, before validation, so it never reaches the error vocabulary. Honest but inconsistent with every other refusal in the contract; worth a `Path` converter or a catch-all route if the difference ever matters to a caller.
- **How much one trading day may deploy is now capped** - built 2026-10-01 as `RiskPolicy:MaxDailyDeploymentPercentage`, 20 % of net asset value. **Counted per day, not per cycle, which is a change from how this item was first written.** The two are nearly the same thing since #51 - an instrument is analysed once a day, so a day has one buying cycle and the rest buy nothing - but the day is both the truer unit for the risk being controlled and the robust one: a cycle that failed halfway would otherwise be handed a fresh budget fifteen minutes later. What remains open is only the number, which wants measurements rather than argument.
- **`Trading:MinDollarVolume` filters nothing, and that is settled as correct** - decided 2026-10-01: **keep 10 000 000 SEK, unchanged.** The live screen put all 31 OMXS30 names through it and rejected none, which is the evidence this item was waiting for. **A guard that does not fire on healthy data is a guard working.** Its job is to catch a symbol whose listing has gone inactive or whose data has gone stale, not to filter live large caps; raising it until it bites would be optimising a number against the wrong objective, and removing it would let a delisted name with a stale thirty-day volume rank. The condition to revisit it is the account size rather than the market: at 100 000 kr a 5 % position is about 5 000 kr against a 10 MSEK floor - 0.05 % of a day's turnover - so liquidity starts to matter somewhere above a ten-million-krona account, and the floor should move with it rather than on its own.
- **A fastapi or pydantic bump now fails CI until `contracts/openapi.json` is regenerated.** Added by #61 and correct: those two packages decide what the service's OpenAPI document looks like, so a bump that changes it has changed the served contract. What it means in practice is that those Dependabot pull requests need a regeneration commit - `uv run python -m app.openapi_snapshot > ../../contracts/openapi.json` from `src/agents` - and that whoever makes it should **read the diff**, because the engine's `OpenApiContractTests` then judge the new document against the DTOs. Prose is stripped from the file precisely so that the diff is worth reading; a document that moved with every docstring would train regenerating without looking.
- **Stage 7's open decisions belong to the owner** and are listed in the stage 7 log (E1, E5, E7) and in PR 1's description: whether Live should ever exist; lifting D2; the deploy target; whether the kill switch should stop exits; and whether `shortlist_edge` should count a shadowed buy as the agents' pick (the view is grouped by mode since the review of #63, and its "agents' picks" are the `Executed` buys only, so a Shadow row reports no buys, though its signals are still scored). The running engine going to Paper is settled: Hampus sets it before the merge.
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
    `Trading:OpeningBalance`, because it is used once and then sets the size of every trade
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

- **PR 1 of 4 - the screen** (branch `stage-5-screening`, 2026-09-26). Four commits. The
  selection rule, the contract, and the batched market-data path. **No engine changes** apart
  from one count guard, which is what made it the right first piece: it can be run and judged
  before anything in the engine moves.

  - **Four decisions, taken 2026-09-26.**

    | Decision | Taken | Why |
    |---|---|---|
    | What the ranking computes | **Risk-adjusted momentum from bars only - the 3-month return over realised volatility - filtered on liquidity** | It needs no fundamentals, and fundamentals are the only part of the source that does not batch. Raw momentum would rank an illiquid share that doubled on one headline first, which is the candidate that cannot be bought at the price that ranked it |
    | Who owns the universe | **The engine sends it in the request**, with the shortlist size and the liquidity floor | The engine owns what it trades. It also makes `/v1/screen` a pure function of its input and a stored shortlist reproducible from the request - which is what stage 5's own question needs |
    | Where liquidity comes from | **`volume` on a new `ScreeningBar`**, and the median of price x volume over 30 bars | Volume arrives in the same response as the closes, so it costs no extra request. Market cap was dropped: it needs the per-instrument call and it is a number that almost never moves a candidate across a line |
    | A symbol that cannot be fetched | **Skipped and named in `rejected`** | A screen is a ranking, not an all-or-nothing fetch. 49 of 50 ranked is a good answer; a 503 for one delisted name would close the cycle. Naming them is the point - a universe that quietly shrinks reads, months later, as a decision not to hold anything in that sector |

  - **Valuation is deliberately not in the ranking**, although the roadmap offers it as an
    example. It is not lost: the shortlist goes through the ordinary `FactSheet` path, so
    every agent still sees P/E for the instruments it is actually asked about. The screen is
    a sieve, not the analysis.

  - **The liquidity decision had to move, and verifying why was the useful part.** The plan
    was `volume` on `PriceBar`. Three checked-in facts say no: `InstrumentHistory.bars` is
    `tuple[PriceBar, ...]`, `contracts/quote-history.schema.json` declares the bar with
    `unevaluatedProperties: false`, and the engine's `BarDto` carries
    `[JsonUnmappedMemberHandling(Disallow)]`. A field added there would make every history
    response unreadable to the engine and stop outcome measurement dead. `ScreeningBar` is
    its own type, and `facts.py` grew a `ClosingBar` protocol so the returns and the
    volatility are reused rather than copied - read-only properties, because a protocol
    attribute is mutable and invariant and a frozen pydantic field does not satisfy one.

  - *Verified live, and it settles the stage's stated risk.* The roadmap warned that
    yfinance is unofficial, rate-limited, and fetches fundamentals per instrument. Measured:
    **50 instruments, 127 bars each, volumes throughout, 2.0 seconds** - the same 2.0 seconds
    as 8 instruments, which is what proves it is one request rather than fifty. Through the
    endpoint: 12 instruments in 2.4 s, the same 12 again in 1.2 s, 12 plus one new holding in
    0.3 s. A shortlist of four ranked 1.6536 (MSFT) down to 0.4585 (KO), with
    0.3866 / 0.2338 = 1.6536 checked by hand.

  - *Mutation-tested:* median to mean turns the spike test red; dropping the tie-break turns
    the determinism test red; counting a missing volume as zero turns three red, which is
    right - that rule is load-bearing in three places.

  - **Two new required settings**, `TAS_SCREEN_TIMEOUT_S` and `TAS_SCREEN_TTL_S`. Their own
    pair rather than reusing the market-data two, because one quote and six months of bars
    for a hundred instruments are not comparable calls. The settings suite was the first
    caller to run against a stale environment and failed exactly as designed - the hole
    written into the resuming checklist the day before.

  - **Two things left open rather than fixed.** A symbol that came back empty is not cached
    as empty, because "not listed today" and "the batch dropped it" look the same from here
    and only one is permanent - so a universe that permanently holds a dead symbol pays one
    failed lookup per screen. And `annualised_volatility` guards on closes rather than on
    returns, so `window=1` passes the check and then raises inside `statistics.stdev`;
    unreachable, since every caller passes a fixed window, and widening a shared function's
    guard belongs in its own change.


- **PR 2 of 4 - SEK end to end** (branch `stage-5-sek`, 2026-09-26). Four commits. The account
  and the universe both move to Sweden, which turns out to be a measurement decision rather
  than a locale one: with a krona account and dollar instruments, an outcome cannot say
  whether a position did well because of the share or because of the exchange rate, and
  saying which is the whole point of stage 4. There is now no currency conversion anywhere in
  the system.

  - **Asked for as "default currency should be SEK", which had three readings.** Swedish
    universe with SEK throughout; a SEK account still holding dollar instruments, needing an
    FX source and making every measurement ambiguous; or flipping `Money.DefaultCurrency`
    alone, which cannot work on its own because prices arrive through
    `TradeSignalMapper.ContractCurrency` and the first buy would throw. The first was chosen.
    Verified before asking: all 30 OMXS30 tickers fetch, 128 bars each, volumes, P/E and
    sector, everything in SEK.

  - **Two symbols would have been rejected, and neither was found by trying one.** Checking
    every OMXS30 ticker against the pattern showed `ESSITY-B.ST` at 11 characters - a real
    index member, so one instrument in thirty would have been silently unbuyable - and
    `XACT-OMXS30.ST` at 14, which is the benchmark. A benchmark the pattern rejects is an
    outcome that can never be measured at all, and it would have failed in the nightly sweep
    rather than at startup. The cap is 16: fourteen is the longest in play, and that leaves
    room without leaving the field unbounded.

  - **`^OMX` stays refused on purpose.** It is the index itself and the purer comparison, but
    a leading `^` is not something a tradeable instrument has, and the symbol rule is shared
    with the instruments the engine can own. The fund tracking it passes as it stands, and its
    management fee is about 0.008 % over twenty trading days - two orders of magnitude below
    the 3 bps of commission and spread already subtracted.

  - **The rule was spelled two ways in five contracts**, `[A-Z0-9.-]` in one and
    `[A-Z0-9.\\-]` in four. Behaviourally identical, textually not. All five now carry the
    unescaped spelling, which is the one that decodes to exactly the Python constant, and a
    test asserts string equality against it; a second test finds every contract carrying a
    symbol, so a sixth growing one cannot quietly differ.

  - **The opening balance is the change with a real argument behind it.** 10 000 was dollars;
    as kronor it would have broken the measurement rather than just being small. At a 5 %
    position cap that is 500 kr, which at the half conviction tier buys **zero** shares of
    Volvo or Investor and one at the full tier - so two thirds of the universe would be
    unbuyable at moderate conviction and conviction would stop affecting size at all, which is
    the one thing stage 8's calibration needs it to do. 100 000 gives 7 shares against 15. The
    arithmetic sits in `appsettings.json` next to the number.

  - **Nothing was deleted, and that was worth checking rather than assuming.**
    `trading.decisions` carries `reference_currency` per row and `trading.signal_outcomes`
    carries `benchmark_symbol`, so the 36 USD decisions and the 21 SPY measurements stay
    readable beside SEK rows. That matters because **finding G's reproduction reads exactly
    those rows** - truncating would have left the conclusion in this file with no way to
    re-derive it. The `RenameColumn` migration was applied locally and all 36 kept their
    values.

  - **The portfolio state does have to be reset**, and only that: `portfolios` and `positions`
    are the two trading tables without an append-only trigger, and nothing references
    `positions`. So `DELETE FROM trading.positions` plus an `UPDATE` of the cash balance to
    100 000 SEK leaves every append-only table intact. It is a fresh start for a paper
    account, not a conversion - the ledger still says an AAPL share was bought for dollars,
    which is what happened.

  - *Verified live:* 473 Python and 318 .NET green. The benchmark was checked on **both** paths
    it is used on, not only the convenient one - `yf.download` for the screen and the
    per-instrument fetch that the history endpoint runs on, 490.65 SEK and 500 bars, with P/E
    and sector correctly None rather than fabricated.

  - **Three tests had stopped testing anything**, the third time this week the same mistake has
    appeared: a boundary written as a literal rather than derived from the constant it is
    about. `TOOLONGSYMBOL` (13), `toolongsymbol` (13) and `ABCDEFGHIJK` (11) all sat in
    "refuses" lists and all became valid symbols the moment the cap moved.

  - **The branch as first pushed would not have run.** Every symbol column in both schemas was
    `varchar(10)` while the pattern now allows 16, so `XACT-OMXS30.ST` at 14 characters would
    have made every sweep fail with `22001: value too long for type character varying(10)` and
    `POST /v1/outcomes` refuse every row - nightly, in the one job nobody watches. Found by a
    test written for something else, which stored the real benchmark. Six columns widened
    across two migration tools, and the guards are round trips against real columns, because
    nothing else would have caught it: no test in either suite had ever written a symbol longer
    than four characters to a column. Mutation-tested both ways.

  - **`trading.hit_rate` groups by `benchmark_symbol` now**, which is a latent defect closed by
    the change that would have triggered it. The unique index is on `(decision_id,
    horizon_unit, horizon_days)`, so one decision cannot carry two benchmarks at one horizon -
    but two decisions alike in every column the view groups by certainly can, and they pooled
    into one hit rate. Postgres then refuses to alter a column a view selects, so the widening
    had to drop and rebuild the view around itself: a dependency this branch created for itself
    one migration earlier.

  - *Verified live, end to end.* The reset ran, the service came up on unchanged team versions
    - the currency touches nothing in the hash - and the engine traded. Three decisions under
    `5926c629dcbe`, all `reference_currency = SEK`: ERIC-B.ST HOLD, VOLV-B.ST HOLD, then
    ERIC-B.ST BUY at conviction 0.60 for **26 shares at 94.96 kr**. 26 x 94.96 = 2 468.96 and
    the cash balance is 100 000 - 2 468.96 = 97 531.04, which checks out - and 26 is exactly
    what the half conviction tier predicts at a 5 000 kr headroom, where 10 000 kr would have
    bought 2. The opening-balance argument is therefore measured rather than reasoned. The
    sweep found 123 horizons not due and measured none, which is right. The journal took four
    Swedish runs.

  - **Finding G's count 1 is improved, not closed.** The same fact sheet - ERIC-B.ST at 94.96 -
    answered HOLD at conviction 0.50 and then BUY at 0.60 within two minutes. Temperature 0
    removed the draw, and this is the residue documented in PR 2 of the model change: Ollama is
    byte-identical only when the preceding request state is identical, and a cycle sends three
    different prompts. So the decision is *more* stable than `llama3.2`'s BUY/HOLD/SELL without
    being stable.

  - **Two mistakes of my own, both from replacing text without reading its context.** A blanket
    `"USD"` to `Money.DefaultCurrency` rewrite hit a JSON payload inside a raw string literal
    and produced an unquoted `"currency":Money.DefaultCurrency`; it is now a constant
    interpolated string, so the fixture tracks the constant rather than repeating it. And the
    `using` added for that file went after the last one instead of in order. `MoneyTests` and
    `QuoteContractTests` were deliberately skipped by that rewrite, because their `"USD"` is
    about a specific currency rather than about the account's.

- **PR 3 - selling, and the exits that do not need an LLM** (branch `stage-5-selling`,
  2026-09-26, **three of four commits made, nothing pushed**). The half of trading the agents
  cannot do. They are asked about one instrument at a time with no memory of having bought it,
  and HOLD is the answer they give most often, so until now a thesis that stopped being true had
  no way to end.

  - **Commit 1 `8984e22` - the portfolio can sell.** `Position.ReduceQuantity` returns what was
    realised and refuses to sell more than is held; `Portfolio.ExecuteSell` reduces *before* the
    cash moves, so a refusal cannot half-happen, and removes a holding that is sold out rather
    than leaving a row of no shares. `Order.RealisedProfitAndLoss` is owned rather than a
    complex property, because it is optional - null on a buy - and it is the only number in the
    ledger that cannot be recomputed from its own row, since the average purchase price it was
    measured against is gone once the holding closes. **The average price is deliberately not
    recomputed on a sale:** selling does not change what the remaining shares cost, and an
    average that moved would make every later realised figure wrong. `Position` gained
    `LastPurchasedAt` and `ThesisHorizonDays`, and **only `AddQuantity` moves them**, which is
    what makes "a HOLD does not extend the clock" a property of the design rather than a rule
    someone has to remember.
  - **The migration backfilled from the ledger instead of accepting EF's defaults.** Generated,
    they were `0001-01-01` and horizon 0 - which the time limit would have read as "older than
    every thesis" and the first cycle would have sold the live ERIC-B.ST position. A migration
    that *invents* a purchase date is worse than one that looks it up, so both values come from
    `trading.orders` and `trading.decisions`. Verified on the real row: the true timestamp and
    horizon 15.
  - **Commit 2 `ba072c8` - sizing and the gate.** `OrderIntent.Sell` carries the price's
    timestamp and an `OrderTrigger`. `PositionSizer` scales the *holding* by the same
    `ConvictionTier` that scales a buy's budget, rounded down, so a single share at moderate
    conviction stays where it is. `RiskEngine.Evaluate` gained an overload that takes **no
    signal and no prices**, which is the design rather than a shortcut: a sale has two authors,
    and a sale needs no net asset value. `RiskPolicy.MinHoldingPeriod` is three days and **only
    a sale the agents asked for waits for it** - that exemption is the whole reason an order
    carries a trigger.
  - **Commit 3 `38c2f34` - the exits.** `ExitRules` is a pure domain function: a stop-loss at
    `RiskPolicy.StopLossPct` (10 %) below the average purchase price, and a time limit once the
    thesis's own horizon has passed since the last buy. The stop-loss is checked first, so a
    holding that has both fallen and expired reads as cut rather than as expired - which changes
    the ledger and not the trade, and the ledger is what stage 8 compares from.
    `ApplyExitsUseCase` sells **whole** positions: a stop-loss that sold half would leave the
    position it just judged to be wrong, and a thesis is not half expired.
    `PricesForOtherHoldingsAsync` became `HoldingQuoteReader`, shared by both use cases, and the
    difference between the two callers is one parameter.
  - **The stop is measured against what the shares cost, not the high since purchase.** A
    trailing stop needs a high-water mark the position does not keep, and keeping one means a
    column updated every cycle from a price the engine does not always manage to fetch - so a
    missed quote would quietly lower the mark and the stop with it.
  - **An exit writes no row in `trading.decisions`, on purpose.** That table is one row per
    analysis: it requires a team, a request and the room the engine had at the time, and an exit
    asked nobody anything. A row there would need a `team_id` that is not true, in the one table
    stage 8 groups teams by. What an exit leaves behind is a ledger line whose `triggered_by`
    says which rule fired and whose `realised_pnl` says what it cost or made. **The consequence
    is a real gap:** an exit is not scored against the index the way a signal is, because a
    measurement hangs off a decision. Closing it means first deciding whether a sale nobody
    argued for is a thing to score at all.
  - *Mutation-tested, three per commit, each turning exactly the intended tests red.* The ones
    worth naming: cash before shares on a sale (1 red), a buy that no longer restarts the clock
    (1), a minimum hold that also blocks a stop-loss (2), half a holding rounding up (2), the
    time limit winning over the stop-loss (1), only the first exit firing (1), and an exit
    claiming the agents asked for it, which puts it behind the minimum holding period (4).
  - *Both migrations applied to the live database*, and the three historical buys read as
    `Signal`.
  - **Commit 4 `5e70e63` - the wiring.** `ProcessProposalUseCase` stops at HOLD rather than at
    "not a buy", and each direction is judged by the gate written for it - a sale is not put
    through the buy overload with the arguments it does not need. **A SELL cycle makes no quote
    calls at all**, which is not only saved work: it is what keeps a holding the engine cannot
    price from standing between the agents and a position they have argued should be closed.
    `TradeDecisionResult.Executed` carries the side so a log line can say which way it went, and
    deliberately adds no column: `decisions.stance` already says which way, and `order_id` points
    at the ledger line that records the side as a fact about what was done. `TradingWorker`
    applies the exits once per cycle **before** the ticker loop, in its own scope, transaction and
    correlation id, and a failure there is logged without stopping the analyses - the alternative
    is an outage that stops all trading rather than the half of it that needed prices.
  - **The ordering is proved by what the analysis was told**, not by reading a log. The second
    cycle in `The_exits_run_before_the_analyses` runs six days after a five day thesis, so the
    exits sell first and the request that follows carries **no existing position** - an assertion
    that cannot pass in the other order. The released headroom shows up in the same test: the
    half tier buys 2 again rather than the 1 it would have managed with 200 still held.
  - **A test that had stopped testing anything, again.** A theory asserted that both HOLD and
    SELL were `NoAction`. SELL of a holding is now `Executed`, and SELL of nothing held is
    `NotSized` - which is exactly the distinction those two outcomes exist for, one about the team
    and one about what it was asked to act on. Split into two tests rather than edited into one.
  - **Running it found the one thing the tests could not.** A pass where the exits judged their
    holdings and were content wrote **nothing at all** - and with no decision row and no order,
    that silence was indistinguishable from the exits never having run, and from every holding
    being unpriceable. It now logs both counts, so a judged count below the held count names the
    difference. This is the whole argument for running the thing rather than only testing it.
  - *Verified live, 2026-09-27.* Two engine sessions against the real database, the real agent
    service and `qwen2.5:14b`. **`GET /v1/quotes/ERIC-B.ST` on log line 37, the first `POST
    /v1/signals` on line 70** - the exits fetch a price for the one holding and judge it before
    any analysis runs. `The exits judged 1 of 1 holding(s) and sold 0.` is the line, and it is
    correct: ERIC-B.ST was bought the previous day at 94.96, so the stop-loss floor is 85.46 and
    one day of a fifteen day thesis has passed. Four decisions recorded, all `NoAction` - **the
    model answered HOLD every time, so no live sale happened.** The sale path's proof is
    `A_sale_survives_being_stored_and_read_back`, which round-trips a partial sale, a stop-loss
    sale and a closed holding through a real Postgres under the `engine_svc` grants.
  - *Green:* 401 .NET and 476 Python, `dotnet format` clean, no model drift, both migrations
    applied to the live database.

  **Two decisions I asked about twice and never got an answer to**, so I took them and said so:
  a sale reuses `ConvictionTier` (above 0.7 the whole position, 0.4-0.7 a half, below nothing),
  and the stop-loss is measured against the average purchase price. Both are reversible and both
  are argued where the code is.

  **Left for later, deliberately.** An exit is not scored against the index, because a
  measurement hangs off a decision and an exit writes none. The risk gate's price-age and
  quantity checks are unreachable *from the exits*, because the quote reader has already filtered
  on the same policy - correct as a second gate, but it means that warning branch is tested
  directly on `RiskEngine` rather than through the use case.

  **An unexplained transient, recorded rather than fixed.** Twice while commit 4 was being
  written, a full `dotnet test` run failed the *entire* database collection - 44 tests the first
  time, 1 the second - including tests the branch never touched. Fifteen consecutive runs
  afterwards were clean, including one forced straight after a rebuild, and no run captured a
  reason. The shape says the shared testcontainers fixture rather than any assertion. If CI shows
  it, this is a known thing and not a new one.

- **PR 4 of 5 - the engine drives the cycle** (branch `stage-5-cycle`, 2026-09-27). Four commits,
  all green at **473 .NET** and 476 Python, `dotnet format` clean, no model drift, every step
  re-run in a throwaway worktree. **Merged as #51** on 2026-10-01 and verified live the same day.

  The stage's last review, and the one that turns "bedöm en ticker som någon annan valt" into
  the target: a universe of 31 OMXS30 names, ranked without an LLM, ten of them analysed, and an
  instrument asked about once a trading day instead of once every fifteen seconds.

  | Commit | What |
  |---|---|
  | `0a1a35e` | The screen reaches the engine: DTOs, `ScreenMapper`, `GetScreenAsync`, `Trading:Universe`/`ShortlistSize`/`MinDollarVolume` |
  | `6ef7ab7` | `trading.shortlists` and `SelectShortlistUseCase` - one screen per trading day, read back on every cycle after the first |
  | `84bcabd` | The cycle is the holdings ∪ today's shortlist. `Trading:Tickers` deleted, the cadence moved to minutes |
  | `c589c7c` | An instrument is analysed once a day and once a price. `decisions.selection`, and `hit_rate` grouped by it |

  - **Three decisions, all taken the recommended way 2026-09-27.**

    | Decision | Taken | Why |
    |---|---|---|
    | What counts as the fact sheet having changed | **Neither today nor this price**: skip if the last analysis was today, or was at the price the quote shows now | Gives one analysis per instrument per trading day during market hours, and silence at night and at weekends - without a calendar. A closed market cannot move a price, so the engine waits for the open by itself |
    | Whether the screen runs every cycle | **Once per trading day**, read back from `trading.shortlists` otherwise | The contract already says two screens on the same day rank the same way, because the factors are daily bars. It makes yfinance's rate limit a non-issue and the cycle idempotent per day |
    | The shortlist as a benchmark | **Split into its own pull request** | Comparing buys against the shortlist average needs the *unbought* members measured too, which is a new population in `MeasurementWorker`. PR 4 is what the engine does; PR 5 is how it is scored. Six to eight commits in one review otherwise. **The reason was wrong** (2026-10-01): those measurements already existed, so PR 5 was two commits rather than most of a day. The decision stands, the premise did not |

    Two more I took without asking, both argued in the code: `trading.shortlists` stores the
    rejections as well (nullable rank and score, nullable reason), because a universe that quietly
    rots is otherwise invisible; and the cycle interval is **15 minutes**, which is now the slack
    in a stop-loss rather than the pace of the analyses.

  - **The roadmap was half wrong about daily data, and reading the source is what showed it.**
    It says an instrument need only be analysed when its `FactSheet` changes, "vilket med dagsdata
    blir en gång per handelsdag". The returns, the volatility and the turnover are daily - but the
    *price* is `currentPrice`/`regularMarketPrice` from `.info`, and `pct_below_52w_high` is
    computed from it. So the fact sheet moves continuously while the market is open, and a rule
    written on the price alone would never skip anything between nine and half past five. Hence
    two conditions, each covering the other's blind spot.

  - **The contract claimed more than the code does.** `contracts/screen.schema.json` said
    `rejected` holds "every instrument that was looked at and left out". It does not:
    `screening.py` truncates to the requested limit, so an instrument that ranked 15th of 31
    appears in neither array. The description now says so and names the consequence - the agents
    can be compared against the shortlist, but **the ranking itself cannot be checked against the
    names it passed over**. Whether to fix that (by adding the un-shortlisted to `rejected` with
    their ranks) is a decision for PR 5, because it is that PR's question.

  - **Mutation testing found two holes rather than confirming the tests**, which had not happened
    on this branch before:

    | Mutation | What it revealed |
    |---|---|
    | Swapping the order of the two conditions turned **nothing** red | The commonest case of all was missing: analysed today **and** the price unchanged. Both verdicts skip, so nothing about trading depends on which - but the cycle's summary line counts them separately, and that line exists to tell "already done today" apart from "the market is shut" |
    | Dropping the `ReferencePrice != null` filter turned **nothing** red | So the rule that keeps an agent-service outage from costing a whole trading day was untested. An attempt is a row - it has to be - but it is not an analysis |

    Both have tests now, and the mutations turn exactly those red. The other mutations behaved:
    trusting the promised ranking order, a duplicate check over one list, universe duplicates
    compared as raw strings, a rank counted from zero, rejections not stored (five red, two of
    them looking like they were about something else), a table created without its trigger,
    dropping the holdings from the selection (eight red, including two about restarting), and
    analysing the shortlist before the holdings.

  - **Four worker tests were rewritten rather than fixed.** They analysed one instrument twice on
    the same day at the same price, which is exactly what no longer happens - so they now run a
    day apart at a moved price. One was removed: the harness waits on committed decisions, and the
    cycle it wanted to observe deliberately produces none, so it could only ever have proved its
    point by waiting on something it did not control.

  - **The fixture leaked rows** until `trading.shortlists` was named in its `TRUNCATE`. It is the
    one table with no foreign key into the portfolio graph, so `CASCADE` never reached it.

  - **`git checkout --` destroyed uncommitted work again.** Same file-level mistake as PR 3, same
    lesson already written down, used this time to revert a mutation on `DecisionLog.cs` - which
    took `LastAnalysisOfAsync` with it. Rewritten verbatim from my own heredoc, and the guard is
    not "remember": it is to copy the file to the scratchpad *before* mutating, every time, which
    is what the other mutations in this branch did do.

  **Merged as #51** on 2026-10-01, and the pull request's own text said it had not been run live.
  It has been now - see below. Nothing in the review needed answering.

### The live run (2026-10-01) — the stage's own claim, measured

The fourth pull request's main claim was that a cycle does **less**, and that is the one claim no
test can make convincing: every test proves what happens, and this one is about what stops
happening. Three cycles against the real universe, with `Trading__CycleIntervalMinutes=1` so the
second arrived in a minute rather than in fifteen - the rule is about the day and the price, not
the interval, so shortening it changes nothing under test.

```
Cycle 1:  The exits judged 1 of 1 holding(s) and sold 0.
          Screened 31 instrument(s) for 10/01/2026: 10 shortlisted, 0 rejected.
          Cycle over 11 instrument(s): 11 analysed, 0 already done today, 0 unchanged in price.

Cycle 2:  The exits judged 5 of 5 holding(s) and sold 0.
          Cycle over 11 instrument(s): 0 analysed, 11 already done today, 0 unchanged in price.

Cycle 3:  The exits judged 5 of 5 holding(s) and sold 0.
          Cycle over 11 instrument(s): 0 analysed, 11 already done today, 0 unchanged in price.
```

**No screen line in cycles 2 and 3**, and the agent service's own log says the same thing
independently: **one** `POST /v1/screen` and **eleven** `POST /v1/signals` across all three
cycles, every signal in the first. Two cycles that would have cost 22 analyses before this change
cost nothing at all. The order holds too: the exits first, then `ERIC-B.ST (Holding)` ahead of the
shortlist, then the shortlist in rank order.

**The engine found four buys by itself**, which is the first time that has happened and the thing
the whole stage existed for:

| Rank | Symbol | Score | Outcome |
|---|---|---|---|
| 1 | SCA-B.ST | 1.226 | HOLD |
| 2 | HEXA-B.ST | 1.107 | **Bought 25 at 99.02** |
| 3 | SEB-A.ST | 1.106 | **Bought 10 at 229.20** |
| 4 | GETI-B.ST | 1.040 | HOLD |
| 5 | EVO.ST | 0.868 | **Bought 3 at 791.80** |
| 6 | NIBE-B.ST | 0.770 | HOLD |
| 7 | SWED-A.ST | 0.556 | HOLD |
| 8 | KINV-B.ST | 0.439 | **Bought 40 at 61.70** |
| 9 | SHB-A.ST | 0.432 | HOLD |
| 10 | SAAB-B.ST | 0.430 | `No order: nothing is held of SAAB-B.ST` |

That last row is worth stopping at: **the agents answered SELL on something the portfolio does not
hold, and the sizer refused it.** "Blankning - SELL utan innehav blir ingen order" from the
roadmap's *Medvetna nej* had never fired outside a test before.

`trading.decisions` for the day: `Holding/NoAction` 1, `Shortlist/Executed` 4, `Shortlist/NoAction`
5, `Shortlist/NotSized` 1. The `selection` column does exactly what it was added for.

**3.1 signals a minute, measured** - eleven over 211 seconds, from the agent service's own
timestamps, against the security branch's limit of ten per minute per key. The token bucket refills
at 10/min and consumption is one every nineteen seconds, so it **fills faster than it empties** and
cannot run dry on this model. The earlier guess in this log that `ShortlistSize` was the setting to
watch was **wrong**: at nineteen seconds an analysis the ceiling is about 3.2 a minute whatever the
shortlist's length, because fifty instruments only take longer. What would breach the limit is a
*faster model* - something in `llama3.2`'s class at three seconds a step would be around 20 a
minute. The limit is bound to the model choice, not to the universe.

**Against the roadmap's own verification for stage 5**, which is the list that says when the stage
is done, the run settles three of five:

| Condition | Status |
|---|---|
| `POST /v1/screen` ranks the whole universe with no LLM call | ✅ 31 names, no model touched the ranking |
| A cycle analyses the shortlist plus the holdings | ✅ 11 = 10 shortlisted + 1 held |
| An unchanged `FactSheet` produces no new analysis | ✅ cycles 2 and 3, zero analyses |
| **A SELL on a holding reduces the position in the log** | ❌ the only SELL was on something *not* held |
| **A holding that falls through the stop-loss is sold with no agent asked** | ❌ the exits judged 5 of 5 and sold 0 |

The two that are open cannot be run on demand: both need the market to move against a position, and
PR 3's run did not produce them either. They are the honest remainder of the stage's definition of
done - **not** a reason to hold stage 6, but a reason not to call stage 5 verified. The cheapest
path to the fourth row is the time-limit exit rather than the stop-loss: the four new positions
carry a fifteen-day thesis, so one of them will reach it without anything unusual happening.

**Three things the run found that no test had:**

- **The liquidity floor filtered nothing.** All 31 OMXS30 names cleared `MinDollarVolume`, nothing
  was rejected, and no dead symbol cost a failed lookup. This log called it "a filter with no
  evidence behind it"; it now has evidence, and the evidence is that it does nothing at this
  account size. That is what it was designed to do, but it is measured rather than assumed now.
- **The log's date format is ambiguous.** `Screened 31 instrument(s) for 10/01/2026` is the first
  of October, formatted with the current culture, and reads as the tenth of January to a Swedish
  reader. One format string, and the kind of thing that only costs anything when somebody reads an
  old log.
- **Nothing caps how much a single cycle deploys.** Four buys took the cash from 97 531 to 87 920 -
  ten percent of the portfolio in four minutes. Each position is capped at 5 % and the cash buffer
  holds 10 % back, so nothing was breached; but with ten BUYs at the full conviction tier a single
  cycle could put out half the account. That is what the rules say today and it is the first time
  the *pace* has been visible. Recorded under *Open decisions* rather than changed.

### The security hardening review (2026-10-01)

Reviewed on request, twice: `security/hardening-f01-f14` first, then
`security/hardening-master-bdf5bf1` after it was rebased onto #51 and a new finding added. Both are
somebody else's work; what follows is what the review found, because the findings outlive the
branches.

**F-18 was a hole in PR 4's own code, and it is the most valuable finding in either branch.**
`ScreenMapper` checked sort order, uniqueness, ticker format, volatility, turnover and reason
length - but not that the answer was *about the universe the engine sent*. The chain: a screen
answers with a symbol outside `Trading:Universe`, it is stored in `trading.shortlists`,
`CycleSelection` puts it in the cycle, `ProcessProposalUseCase` asks about it and the
instrument check **passes** because the answer is about what was asked, the sizer sizes it, the risk
gate approves - and the engine buys an instrument its owner never authorised. One field in one HTTP
response walks past the configuration that decision 1 rests on. The same rule was already applied to
the signal (*"a model that replies TSLA to a question about AAPL makes the engine buy TSLA"*) and
simply not to the screen, which is what decides what gets asked about at all.

Its quote half is correct but milder than its commit message claims. `ApplyExitsUseCase` looks the
position up by `quote.Ticker` and skips what it cannot match, and `PricesForSizingAsync` keys the
snapshot on the answer's own ticker so a wrong symbol leaves the holding unpriced and sizing
refuses - both already fail closed. The place it actually fixes is **`AnalysisDueCheck`**, which
compared `quote?.Price.Amount` against the last analysis's price with no symbol check, so a quote
about another instrument could decide whether this one was analysed. That costs an analysis rather
than money, and the commit message does not mention it.

**The blocker the first review found, and what it says about the fix.** The first branch deleted
`client.DefaultRequestHeaders.Add(ApiKeyHeader, ...)` - whose comment read *"It is set once here
rather than per request, so no code path can forget it"* - and set the key at each call site
instead. A trial merge proved the consequence: `GetScreenAsync`, written in parallel in PR 4, sent
no `X-Api-Key` at all, so every screen would have answered 401 and `ScreenOrNothingAsync` would have
swallowed it as a warning - the engine running holdings-only, every day, with nothing visibly
broken. The second branch fixes it, with a test per scope. **It fixes the instance, not the class:**
the key is still a convention at five call sites, and nothing would catch a sixth one forgetting it.

**Carried and unaddressed**, all three now in *Open decisions*: `TAS_ENABLE_DOCS` is read from
`os.environ` while `settings.enable_docs` is declared and never used, so the switch does nothing in
the `.env` file that `.env.example` points at - proved by probing it; `agent_api_key` is
`[Required]` on both sides and grants every scope, so a full-access key always exists and the
scopes cannot be adopted in any configuration; and `CLAUDE.md`, `contracts/` and this log are
untouched by either branch.

**What is good in it, because it is:** the outcomes HMAC signs the exact bytes that are sent
(`SerializeToUtf8Bytes` into `ByteArrayContent`, with the reasoning in the comment) rather than
re-serialising, which is the mistake most implementations make; the closed-by-default router was
kept with scopes narrowing on top, so a route added later still needs a key; and the rate limiter
runs *after* authentication - twenty-five requests with random keys allocated **zero** buckets,
which I checked because I expected the opposite.

- **PR 5 of 5 - the shortlist as a benchmark** (branch `stage-5-shortlist-edge`, 2026-10-01). Two
  commits. **489 .NET** and 476 Python green, `dotnet format` clean, no model drift.

  `trading.shortlist_edge` answers the question the project turns on: per screened day, horizon and
  team version, the average excess return of the whole shortlist beside the average of the subset
  the engine actually bought, and the difference between them. Positive means the agents picked
  better than the ranking that handed them the candidates; negative means the LLM is cost rather
  than value, and the roadmap already says what follows from that - *"då är en bättre rankning värd
  mer än ett bättre team"*.

  - **It needed no new measurement, and that is the finding.** Both this log and the review of PR 4
    said PR 5 would add a second population to `MeasurementWorker` - the shortlisted instruments
    nobody bought. **They are already measured.** Stage 4's PR 5b scores *every* signal at the
    fixed horizons, including HOLD and everything the risk gate refused, on the explicit grounds
    that measuring only the trades that went through measures the wrong population. The database
    says so: 20 `NoAction` and 41 `NotSized` measurements were already stored before this branch
    existed. What was missing was never the data - it was a join from a measurement back to the
    shortlist it came from, and that is a view. The planned pull request was most of a day's work;
    the real one is two commits. **Checking the premise cost one SQL query and saved the rest.**

  - **Three decisions, taken rather than asked** (the owner said to do what seemed best):

    | Decision | Taken | Why |
    |---|---|---|
    | What to average | **`excess_return`, not `net_edge`** | `net_edge` is null for every HOLD - checked against all 67 stored measurements, 20 of 20 HOLDs null - because a HOLD has no edge to compute, only a band it stays inside. Averaging it across a shortlist would silently average the buys and sells alone, which is this view's own comparison inverted into a number that reads like data. `excess_return` is a fact about prices rather than about a stance |
    | Gross or net | **Gross against gross, with net beside it** | The shortlist average is a paper portfolio that paid no commission and no spread, so subtracting costs from the bought side alone would flatter the screen by about three basis points a round trip. `bought_edge_net` is what the account really earned, reported next to the comparison rather than inside it - the same reasoning that put both in `hit_rate` |
    | A new view or a column on `hit_rate` | **A new view** | Different grain. `hit_rate` groups by a decision's own attributes; this groups by a screened day and compares two subsets of it. Forcing them together would make both harder to read and neither more useful |

  - **Deferred, with the design named rather than left vague:** whether to store the ranks of the
    instruments the screen passed over. `rank(candidates, limit)` truncates, so the 21 names that
    ranked 11th to 31st on 2026-10-01 exist in no row - which means the agents can be compared
    against the shortlist, but **the ranking itself can never be validated**. Nobody can ask whether
    rank 15 would have done better than rank 3. The fix is not a line in this view: the contract has
    to carry every ranked instrument with a flag for the ones selected, `trading.shortlists` needs
    that flag as a column, and the engine has to analyse only the flagged ones. That is a pull
    request, and burying a contract widening inside a measurement change is the mistake that
    splitting SEK out of selling avoided. **The half-measure is worse than either:** putting the
    rank in a rejection's free-text reason would cost twenty rows a day and answer nothing, because
    a measurement cannot parse prose.

  - **Every test fabricates its rows**, which is not convenience. The view cannot be checked against
    real data until a horizon has passed on a day that was screened, and the first screened day is
    today - so a test with made-up measurements is the only thing standing between this view and a
    number nobody has ever verified. It is also the only way to put a HOLD, a buy and an
    unmeasurable row in one shortlist on purpose.

  - *Mutation-tested:* averaging `net_edge` instead of `excess_return` turns **seven** tests red,
    which is the central mistake and the one worth the most coverage; counting the rejected
    instruments as shortlist members turns exactly the rejection test red; dropping the trading day
    from the join turns exactly the cross-day test red.

  - **Reviewed externally 2026-10-01**, verdict *accept with nits*, no code blockers, CI green.
    Four findings, all real, and two of them changed the view:

    | Finding | Answered |
    |---|---|
    | `bought` was `outcome = 'Executed'` with no stance, so an executed SELL on a shortlisted holding could inflate the edge | **Fixed.** A sale's `excess_return` is still the instrument's forward return, so a well-timed exit from a share that then fell would have arrived as a *negative* contribution to how the bought instruments did - in a column it was never part of. `AND d.stance = 'Buy'` on all four aggregates, plus a test whose numbers show the difference: the edge reads 0.14 with the filter and 0.00 without it, and 0.00 is the shape of a result that means nothing while looking like agreement |
    | No test for `NotSized` or `RejectedByRisk`, although 41 `NotSized` measurements were the premise of the whole pull request | **Fixed.** A theory over both. Not hypothetical either: SAAB-B.ST was shortlisted on 2026-10-01, answered SELL and was refused because the portfolio held none of it |
    | *Current state* still contradicted itself | **Fixed**, and it was worse than the review said - see below |
    | The inner join drops a shortlisted instrument that was never analysed, biasing the control | **Documented.** It cannot happen while a cycle completes, because every shortlisted instrument is analysed once a day, so it is a property to know rather than a guard to write. The remarks had covered the midnight straddle and not this |

    The empty pull request body is this session's: the text is generated into a pre-filled compare
    URL and into `pr5-body.md`, so opening the plain compare page gives a blank one.

  - **The stale-claim sweep missed a whole section, which is the second time this file has caught
    me at the same thing.** The lesson written after PR #42's review says a resume document
    describing a plan that no longer holds is one you stop trusting, and that writing a new
    sentence beside a stale one leaves the file worse. The sweep I ran was a grep for phrases I
    expected to be stale - which finds what you already suspect and nothing else. *Current state*
    still said `Trading:Tickers` was AAPL and MSFT, a setting **deleted** in #51, and that the
    database had "all three" engine migrations when it had eleven - and the correction written here at the time said twelve, which was also wrong. **A section that describes the
    present has to be read, not searched**, and the review found in minutes what the grep could not
    find by construction.

  - **The backup discipline failed a third time, and differently.** The mutation harness copies the
    file to the scratchpad in the same command as the edit, which is the guard this log wrote after
    PR 4. This time the `cp` itself failed - the scratchpad's `mutations/` directory did not exist
    in a new session - and because it was chained with `&&` after a `cd` that succeeded, the mutation
    ran anyway on a file that was **not yet committed**, so there was no copy anywhere. It was
    recoverable only because the mutation was a single known string replacement that could be
    reversed exactly. The guard that actually works is `mkdir -p` before the copy and checking that
    the copy exists before touching the original - a backup step that can fail silently is not a
    backup step.


## Stage 5 follow-ups (2026-10-01 ->)

Work the stage produced rather than work the stage planned. Stage 5 itself is merged; these are the
things running it made visible.

- **`CLAUDE.md` brought level with eleven merges** (branch `docs/claude-md-level`, merged #55). It
  had not changed since 2026-09-26 while #48, #51, #52, #53 and #54 landed, and its stated job is
  to describe the repo as it is today. **Following it gave an engine that would not start** -
  `AgentService:OutcomesHmacSecret` became required in #52 and the document still named two secrets
  where there are six - which is how this session found out, by following it.

  The levelling itself was **mechanical, because a grep had already failed once.** A script diffed
  every `TAS_` variable the document names against `.env.example`, and every options property of
  `TradingOptions`, `AgentServiceOptions` and `RiskPolicyOptions` against the text. It found four
  things careful reading had not: the document named `DATABASE_URL` **without the `TAS_` prefix its
  own rule demands**, `TAS_SCREEN_TIMEOUT_S` and `TAS_SCREEN_TTL_S` had never been documented at
  all since PR 1, `MaxPositionPercentage` and `CashBufferPct` were described in prose but never
  named, and the four scoped keys were written as a shorthand nobody could grep for.

- **The trading day's deployment limit** (branch `stage-5-cycle-budget`, 2026-10-01). **507 .NET**
  and 495 Python green. `RiskPolicy:MaxDailyDeploymentPercentage` at 20 % of net asset value - four
  positions at the full conviction tier, or eight at the half tier.

  - **It is not the position limit again.** `MaxPositionPercentage` bounds any one holding and
    holds whatever this says. This bounds a *day*, because since #51 a day's buying is up to ten
    decisions from one model on one screen, taken within a few minutes of each other: they share
    whatever that day's bias is, and a momentum ranking in a rising market hands the agents ten
    names that move together. **Ten positions is less diversification than it looks, because the
    correlation is the screen's own factor.** The first real screened cycle deployed ten percent in
    four minutes; ten BUYs at the full tier would have been half the account.
  - **Per day rather than per cycle**, which is a change from how the decision was first written.
    The engine has no cycle-level state and that is deliberate - each analysis is its own scope and
    transaction, and the worker was left with no shared mutable state on purpose - so the budget
    has to be asked of something that already knows. `orders.placed_at` makes it a query, which is
    the same move as counting bars instead of keeping a holiday table: **the ledger is the
    accumulator, so nothing has to remember.** A day is also the robust unit, because a cycle that
    dies halfway would otherwise get a fresh budget on the next one.
  - **The sizer shrinks and the gate refuses**, which is this engine's standing arrangement for
    every limit: a third term in the same `min` the cash buffer already lives in, so an order
    shrinks against the day exactly the way it shrinks against the buffer, and then the gate
    re-derives the limit because the sizer's arithmetic is not evidence about the sizer's
    arithmetic. The position limit is reported *before* the day when both are breached - "this
    position is too big" tells an operator more than "the day is spent", and only one of the two
    can be fixed by waiting.
  - **A sale ignores it entirely.** Selling frees capital rather than committing it, and the sell
    gate takes no deployment figure at all - so a daily *purchase* budget can never trap a
    position, which is the same reasoning that keeps prices out of the sell gate.
  - **A daily limit below the position limit is refused**, in the options range and again in the
    domain. It would make the position limit unreachable, and the two numbers would be quietly
    fighting each other.
  - **No optional parameters on the real signatures.** `deployedToday` is required on
    `PositionSizer.Size` and the buy overload of `RiskEngine.Evaluate`, and the tests get
    four-argument overloads through an extension class instead. An optional parameter would mean a
    production call site that forgot it silently said "nothing spent today" - which is the shape of
    mistake that cost #52 its screen key, where a property that made forgetting impossible was
    traded for a convention and the first new call site broke it.
  - *Mutation-tested:* the sizer ignoring the day's budget turns three tests red; the gate trusting
    the sizer instead of re-deriving turns exactly the gate test red; the ledger query counting
    sales as purchases turns exactly the ledger test red.
  - **The cost, stated:** this slows the portfolio's formation and therefore the baseline the
    measurement needs. It is configuration for that reason, and the number is the part that wants
    measurements rather than argument.

  - **Reviewed externally 2026-10-01**, verdict *accept with nits*, no blockers, CI green. Five
    findings, all real, and **the first one was wrong about more than its wording**:

    | Finding | Answered |
    |---|---|
    | The `RiskPolicyOptions` remark said the `[Range]` starts at the position limit; it is 0.01-1.0, and the cross-condition lives in the domain | **Fixed, and the remark had been describing behaviour that did not exist.** It also claimed the domain's guard "fails at startup", which is false: `RiskPolicy` is a singleton built by a factory, so it is first resolved when a *cycle* asks for it - the refusal would have arrived as an "Unexpected failure" line from inside the worker's own catch, minutes after a deploy. That is precisely the failure this project builds configuration to avoid. So the fix is a `RiskPolicyOptionsValidator` carrying the cross-condition into `ValidateOnStart`, which is the pattern `TradingOptionsValidator` already set, and the domain keeps its own guard because it does not trust that configuration was validated |
    | `DeployedOnAsync` has no `portfolio_id` filter | **Documented**, with the invariant named: it leans on the same one `FindAsync` enforces, that the engine trades one account and a second row is a fault rather than a silent pick. The remark now says that if that ever stops being true, this sum has to be scoped before anything else is - a shared daily budget across two accounts would let each spend the other's |
    | No use-case test with a non-zero `deployedToday` | **Fixed**, and it was a dangling affordance: the parameter had been added to the test builder and never used. Four tests now cover what is only testable there - that the day's spend is read **once**, for the date the request names, given to both halves, and not read at all for a sale |
    | `CLAUDE.md`'s formula still named two `min` terms | **Fixed** |
    | Theoretical check-then-act between concurrent workers | **Answered rather than guarded.** Two buys decided at once would read the same spend, but both change the portfolio's cash, so the row version the aggregate already carries fails the second commit as a `ConcurrentChangeException` - the same mechanism that stops two writers spending the same krona. Written into the policy's remarks, because a reader should not have to re-derive it |

    *Mutation-tested again:* a validator that passes everything turns exactly the startup test red;
    asking the ledger for a sale as well turns exactly the sale test red.

  - **The review's most useful nit was about a comment.** It said the remark and the attribute
    disagreed, which was true - and following that disagreement showed the remark was describing a
    guarantee the code did not give. **A comment that is wrong about the code beside it is worth
    reading as a question about the code, not only about the comment.**


---

## Stage 6 log (2026-10-01 -> 2026-10-09, done)

Read the stage in `docs/arkitektur-roadmap.md` first. It is two days of work on paper:
multi-stage Dockerfiles for both services, compose with healthchecks and
`depends_on: condition: service_healthy`, `docker build` in CI, and NSwag generating the
.NET client from FastAPI's `/openapi.json` with CI failing on drift.

### Three measurements taken before the plan was written

Each of them changed it.

1. **A container reaches Ollama on Windows.** `--add-host=host.docker.internal:host-gateway`
   plus `http://host.docker.internal:11434/v1` answered `{"version":"0.35.0"}` from inside a
   throwaway container. No firewall in the way and no host IP to look up. This was the stage's
   largest unknown: a container's `127.0.0.1` is the container, so mirrored networking does
   not help by itself - but the bridge gateway reaches the WSL host, and mirrored networking
   has already put that host on Windows.
2. **The OpenAPI document needs no server.** `create_app().openapi()` returns 7 paths and 20
   schemas with no database, no Ollama and **no `TAS_ENABLE_DOCS`** - the flag controls the
   `/openapi.json` *route*, not the generator. The resume note in this file said the drift
   check was blocked on that defect. It was not. The defect is still real and still worth
   fixing; it is simply not in the way.
3. **The wheel carries the prompt files.** All four `.md` files are in it, so installing the
   project with `--no-editable` works. That also makes `team_version` a **containerisation
   invariant**: the hash is over the prompt files' contents, so a build that mangled line
   endings would not fail - it would answer as a different team and split the measured
   population in two. It is therefore checked rather than assumed.

### The four decisions

| # | Question | Taken |
|---|---|---|
| D1 | Who applies the migrations under compose? | One short-lived container per schema - `efbundle` for `trading`, `alembic upgrade head` for `agent` - gated with `service_completed_successfully`. The engine keeps its refusal to start against a database that is behind it, and that refusal becomes the *proof* the migration container ran. |
| D2 | Does `docker compose up` start the engine? | **No.** Database, agent service and both migrations by default; the engine behind `--profile trade`. It is the only service that spends money and the kill switch does not arrive until stage 7, so until then "not starting it" is the only way to stop it - and that should cost a word on the command line. |
| D3 | NSwag-generated client, or a drift check? | **A drift check, not generation.** Generating the engine's DTOs from Python's specification would make Python the contract's owner, which inverts contract-first and contradicts CLAUDE.md's *"Neither side generates the other"*; the generated types would also lose `[JsonUnmappedMemberHandling(Disallow)]` and the mappers' length caps, which are the engine's actual defences against a wrong answer. What the roadmap asks for - *"CI fails on drift"* - is obtainable without the inversion. **This is a deviation from the roadmap's text and was raised as one.** |
| D4 | Where do compose's secrets live? | The root `.env` becomes its single source and gains two keys. Compose hands each container only the variables that are its business, so the rule that the agent service never sees the other two passwords survives. |

### The pull requests

Five, in this order: the agent image, the engine image plus its migration bundle, one compose
file for the whole system, the contract drift check, and `docker compose up` mechanised in CI.

### PR 1 - the agent service as two images (`stage-6-agent-image`)

- **An entrypoint whose first log line is already JSON.** `python -m app` configures logging
  and *then* hands the process to uvicorn, which is the opposite of what a uvicorn command
  line does: uvicorn installs its own logging configuration before the application starts and
  `configure_logging` runs in the FastAPI lifespan, so the two lines that say whether startup
  happened came out in uvicorn's format while every line after them was JSON. `log_config=None`
  is what closes it - uvicorn calls `dictConfig` only when it has a configuration. This was an
  open item in this file, assigned to stage 6 and described there as "the real fix is a
  `--log-config` at deploy time"; the module turned out to be smaller and better than a second
  copy of the formatter in a JSON file.
- **The socket is no longer in two places.** `TAS_BIND_HOST` was an operator's claim about a
  socket somebody else opened, which is how a warning comes to describe a bind nobody made.
  Through the entrypoint the claim *is* the socket, and `TAS_PORT` joins it.
- **Four tests, and the fake uvicorn logs from inside `run()` on purpose.** A line emitted
  after `main()` returned would prove nothing about which of the two ran first.
  *Mutation-tested:* dropping `log_config=None` turns exactly the mechanism test red; moving
  `configure_logging` after `uvicorn.run` turns exactly the JSON test red; hardcoding the
  socket turns exactly the bind test red.
- **Two targets, not one image with two commands.** A service that can migrate the database it
  reads is a service that can migrate it by accident - the same separation the engine already
  has. `alembic` is the migrate image's entrypoint, so `current` and `upgrade head --sql` are
  available to an operator who wants to look before applying.
- **The migration container does not need an LLM key to create a table.** Its URL travels as
  `-x url=`, which is `env.py`'s documented path, because `get_settings()` requires *every*
  setting the service needs. A `migrate` dependency group splits alembic out of `dev`, so the
  image carries SQLAlchemy without carrying pytest, mypy, ruff and testcontainers.
- **The healthcheck asks `/health`, not `/ready`**, and that is a decision about what compose
  does with the answer rather than about which endpoint is more informative. `/ready` is 503
  until the database and the LLM backend both answer, so a blinking Ollama would make the
  container unhealthy - and anything gated on this service would then refuse to start over an
  outage the engine already handles by taking no decision that cycle.
- **Verified by running it, not by building it.** Every log line JSON including uvicorn's first
  two; `/ready` reached the real database *and* Ollama on Windows; `/health` 200; an
  unauthenticated quote 401; `/openapi.json` still 404; a real quote for ERIC-B.ST at 91.56
  through yfinance from inside the container; and **one real three-step analysis of
  VOLV-B.ST in 32 s**, a validated HOLD at conviction 0.50 with a 15-day horizon, whose three
  rows are in `agent.step_outputs`. The image computes `team_version` `5926c629dcbe`, which is
  the host's. The migrate image applied all five revisions to a throwaway database under
  `agent_svc`'s own grants, with `alembic_version` landing in the `agent` schema.
- **Ten CI steps, run locally as written.** Both targets built; uid 10001; the application
  builds with `-w /`; the image's prompt hashes diffed against the repository's; one migration
  head; alembic present and the test tools absent; no `.env` in either image.
  *Mutation-tested:* one trailing newline on a repository prompt file turns the prompt check
  red, and an image built with a `.env` in it turns the leak check red.
- **One CI step had a sharp edge and lost it.** `echo ... > src/agents/.env` is harmless on a
  runner, where there is no such file, and destroys a developer's real secrets the first time
  anyone runs the job by hand. It is `test -f ... ||` now. **A step that overwrites a file is
  a worse bug than the one it was guarding.**
- Image sizes: 563 MB for the service, 602 MB for the migration step. Most of it is pandas,
  numpy and ag2, which is what a yfinance integration costs.

### PR 2 - the engine as two images, plus its migration bundle (`stage-6-engine-image`)

- **`runtime:10.0`, not `aspnet`.** The engine is a Worker and never opens a socket, so an
  aspnet image would carry a web server nothing starts. Non-root as the base image's own uid
  1654, through `$APP_UID` rather than the number, so it stays right if Microsoft moves it.
- **The migration bundle is a second target**, for the same reason the agent service's Alembic
  step is: the engine already refuses to migrate itself, and an image carrying the ability to
  do it would make that refusal a matter of discipline rather than of fact. It is built in the
  same stage as the service from the same restore, so the two cannot disagree about which
  migrations exist - which is the failure the engine's startup check exists to catch and would
  rather not have to. **No CMD**, because the fallback is the design-time factory's deliberate
  `Host=design.invalid`: a run without `--connection` fails to resolve a hostname instead of
  migrating something nobody meant to.
- **No HEALTHCHECK, as a decision.** Nothing is gated on this container, and what a useful
  check would ask is not "is the process alive" - Docker knows that from the process exiting -
  but "did a cycle finish in the last fifteen minutes", which needs the engine to publish that
  somewhere. Stage 7.

**Two things the first build found, neither theoretical.**

- **It published a gitignored `appsettings.Development.json`.** `.dockerignore` knew about
  `.env` and not about the file sitting beside it on the next line of `.gitignore`. This one
  held log levels, so nothing leaked - but `appsettings.Local.json` is in that same section,
  and that is where a connection string goes when user secrets are a nuisance. The fix is two
  patterns; the guard is better than the patterns, see below.
- **Npgsql probes for GSSAPI the runtime image does not carry.** Every run began with two
  unstructured lines on stderr about `libgssapi_krb5.so.2`, and the migration container printed
  them too. The connection works regardless, because this system authenticates with a password,
  so it is noise rather than a fault - and still worth three megabytes of `libgssapi-krb5-2` to
  remove. In a service whose whole logging discipline is that a line means something, the first
  two lines an operator reads should not be a library that was never needed.

**The CI job asks git instead of keeping a list.** For every file published into `/app` it asks
`git check-ignore` whether the repository refuses to track it. `.gitignore` and `.dockerignore`
are two lists of "this must not leave the machine" and nothing holds them together, so a check
written as filenames would be one more thing to keep in step - and it was precisely the drift
between those two lists that published the file above. This formulation needs no editing when
the next local-only file is invented. *Mutation-tested:* an image with that file in `/app` fails
the step and names it. The first attempt at that mutation **could not build**, because
`.dockerignore` now refuses the file into the context at all - which is the fix working, and a
false "survived" until I noticed the image had never existed.

**The schema goes there and back.** The bundle applies every migration to the database the
checked-in init script builds, runs a second time to prove a retried deploy is not a failed one
(*"No migrations were applied"*), and then reverts to nothing but an empty history table. The
roadmap asks for reversible migrations tested rather than assumed, under *Förvaltning*; this is
where it is cheap. Verified against **a database with rows in it** as well as an empty one -
four positions, ten decisions and a shortlist - and the revert dropped all eight tables and both
views. **The append-only triggers do not stand in the way, because dropping a table is DDL and
not the `DELETE` they refuse.** That is better than the agent side, where a widen-in-place
migration cannot reverse while a stored value needs the extra width.

**Eleven migrations, not twelve.** The count is read off the migration files in CI rather than
written down, which settled a number this file had had wrong in two places - including inside
the lesson about counts going stale. The local database has eleven applied and Alembic at its
fifth revision, both checked rather than restated.

**The whole system ran in containers, against a throwaway database.** This is PR 3's success
criterion reached by hand before compose exists, and it is the verification the images are
worth: a network of its own, a fresh pgvector built by the checked-in init script, the agent
schema applied by the Alembic container, the trading schema by the EF bundle, then the agent
service and the engine.

- The agent service was **healthy in 8 s**; the engine's startup schema check passed against
  the bundle's work and the workers started.
- **`Screened 31 instrument(s) for 10/04/2026: 10 shortlisted, 0 rejected`** - the screen ran
  through the containerised agent service to yfinance.
- `No portfolio was stored, so one was opened with 100000 SEK.`
- **Ten analyses, all through Ollama on Windows from inside a container.** Four buys - SCA-B.ST
  20 at 119.60, EVO.ST 3 at 802.40, SHB-A.ST 16 at 152.85, KINV-B.ST 40 at 62.24 - four HOLDs,
  and two SELLs on instruments not held, which became no order at all. That last is the
  no-shorting rule firing in a containerised engine.
- `Cycle over 10 instrument(s): 10 analysed, 0 already done today, 0 unchanged in price.`
- The database afterwards: 11 migrations, 1 portfolio, 4 positions, 4 orders, 10 decisions, 10
  shortlist rows, and on the agent side 10 runs, 30 step rows and 10 embeddings. Cash
  100 000 -> 90 265.60, which is **9.7 % of net asset value deployed** - under the 20 % daily
  cap, which therefore did not bind. The same shape as the first real screened cycle, which
  deployed 10 %.
- **ag2 1.1.1 was exercised end to end on the way.** Dependabot's bump merged as #57 while this
  was being built, so the agent image was built from it. Ten analyses answered the contract.
- Image sizes: 315 MB for the engine, 352 MB for the bundle.

**The concern raised about #57 did not happen, and the reason is worth knowing.** Its first
branch was cut from `a1ccfb3`, before the agent image landed, and carried a `pyproject.toml`
with no `migrate` group - merging that would have stopped `--target migrate` building. Dependabot
regenerated the branch against the new master instead, kept the group and bumped `sqlalchemy`
*inside* it. **A rebase by the bot closed a hazard that reading the old branch had found.**

### PR 3 - one compose file for the whole system (`stage-6-compose`)

**The stage's own success criterion, measured.** `docker compose up -d` from nothing: **31.8
seconds** to a provisioned, healthy system, and 6 seconds more for the engine. A first full
cycle - screen, account opened, ten analyses - is about four minutes.

- **The engine is behind `--profile trade`**, which was decision D2. Everything else comes up
  by default, both migration steps included, because a provisioned database is not trading:
  after a plain `up` the schemas are current and a host-run engine can point at the same
  database. The engine is the only service here that spends money and the kill switch does not
  arrive until stage 7, so until then "not starting it" is the only way to stop it.
- **Each schema is applied by a container of its own**, and whatever needs it waits on
  `service_completed_successfully` rather than on a port. The log of a clean `up` reads in the
  right order: db started, db healthy, both migrations started, `agent-migrate` **exited**,
  then the agent service started. Neither service can migrate its own schema from inside
  itself - already true of the engine, which refuses to - and these containers make that a
  property of the deployment rather than of anyone's discipline.
- **The engine's bundle takes `ENGINE_DATABASE_URL`, not `--connection`**, so the password is
  not in the container's rendered command. Alembic's takes `-x url=` because its other route is
  `get_settings()`, which would need an LLM API key to create a table. The asymmetry is the
  agent side's, not a preference.

**The Dockerfile's claim about the bundle was incomplete, and finding that out is what chose
the wiring.** It said a run without `--connection` fails to resolve a hostname. Tested: it does
- *unless* `ENGINE_DATABASE_URL` is set, which the design-time factory reads and **the bundle
  invokes that factory at run time**. So there are two routes, they are not equivalent, and the
  one the comment did not mention is the better one for compose.

**The secrets separation changed shape, and the old sentence had to go.** CLAUDE.md said the
agent service never sees the other two database passwords *because they are in a different
file*. Compose has to hand the same API key and HMAC secret to both sides, so the root `.env`
now holds those too and that sentence is no longer the mechanism. The mechanism is each
service's explicit `environment:` list: compose interpolates the file itself and never passes
it to a container, so a password that is not on a service's list cannot arrive. An `env_file:`
would have been shorter and would have given the agent service everything in the file.

- The two shared values were copied into the root `.env` from `src/agents/.env` rather than
  regenerated, so nothing had to be rotated in three places. Verified by fingerprint:
  `576bfc53` for the API key and `809f98a6` for the HMAC secret, the latter being the same
  fingerprint this file recorded on 2026-10-01. Values never printed.
- **`${VAR:?message}` fail-fast works**, and the first `docker compose config` proved it by
  refusing with four lines naming exactly which variables were missing and what to do.

**`team_version` is the drift risk in a compose file, and it is now checked rather than
hoped.** Provider, model, temperature and seed are part of the hash, and they are spelled out
in the compose file because a clean checkout has no `src/agents/.env` and `up` has to work from
one. Two places holding four values is how the same team comes to answer under two versions,
with two populations that cannot be pooled accumulating under one name. The clean stack logged
`5926c629dcbe` and `b856e3edf611` - the host's own - so they agree today. A CI step comparing
the two belongs with PR 5.

**Tested against a clean volume without touching the real one.** A compose project of its own
(`-p stage6clean`) gets its own volume, so `up` from nothing is a genuine clean state while the
volume holding 54 real decisions keeps running beside it. That needed the container name and
both published ports to become variables with today's values as defaults - which is not a
feature looking for a use but the only way to test this file honestly. Confirmed after the run:
the real database still had its 54 decisions and 5 positions.

**What the clean stack produced:** 11 engine migrations, Alembic at its fifth revision, 10
objects in `trading` (eight tables and both views) and 5 in `agent`. `/ready` answered ready
through the published port, so the container reached the database *and* Ollama on Windows. An
unauthenticated signal was refused with 401. Then, with the trade profile: 31 instruments
screened, an account opened at 100 000 kr, **ten analyses** - four buys, six HOLDs - 4 orders,
10 decisions all at `selection = Shortlist`, 10 runs and 30 step rows on the agent side, and
cash at 90 168.96, which is 9.8 % of net asset value deployed.

- **The stances were not identical to the hand-wired run of PR 2**, which bought SCA-B.ST, EVO,
  SHB-A and KINV-B and answered SELL twice on instruments not held; this one bought SWED-A.ST
  among others and answered HOLD six times. That is the known shape of the seed: it pins the
  decision when the same request is repeated in the same state, and a different request in
  between changes the numerics. Worth recording because it looks like a difference between the
  two ways of running the system and is not one.
- **The gssapi noise is gone**, confirmed here rather than only in the migration container: the
  engine's first log line under compose is its migration-history query.

### PR 4 - the contract drift check (`stage-6-contract-drift`)

**Decision D3, built: a drift check, not NSwag.** The roadmap asked for the engine's client to be
generated from FastAPI's `/openapi.json` with CI failing on drift. Generation would make Python the
contract's owner and cost the engine `[JsonUnmappedMemberHandling(Disallow)]` and the mappers'
caps; the failing-on-drift half is obtainable without that, and this is it.

- **`contracts/openapi.json` is the agent service's own OpenAPI document, committed.** It is
  written by `uv run python -m app.openapi_snapshot > ../../contracts/openapi.json`, from
  `create_app().openapi()` - measurement 2 held: no server, no database, no Ollama, no flag. It
  is a *record*, not an input: nothing is generated from it, and the hand-written
  `contracts/*.schema.json` remain the agreement.
- **Python fails when the file stops being true.** `tests/test_openapi_snapshot.py` regenerates
  it and compares byte for byte, so an API change cannot reach `master` without the file
  changing in the same diff - which is what makes the file worth reading from the other side.
- **The engine fails when its DTOs stop agreeing with the file.** `OpenApiContractTests` walks
  every endpoint `PythonAgentClient` calls - route, method, path and query parameters, request
  and answer - and compares each DTO with its schema by reflection, following `$ref`, reading
  pydantic's `anyOf [X, null]` as nullable X, and treating `InstrumentDto` as the union it is.
  **The rules are directional**: for an answer, every field the service may send must exist on
  the DTO (Disallow would refuse the whole answer), every `required` member must be one the
  service always sends, and a null it may send must fit the engine's type; for a request, the
  mirror image. Beside it, the stances against `Stance`, the outcome enums against the engine's
  stored spelling, the mappers' caps, and the screen limits `TradingOptions` validates.
- **The checker is tested for going red.** Eight cases each apply one realistic drift to a copy
  of the document - `amount_usd` appearing (decision 1's own regression), a newly required
  request field, a dropped answer field, an answer field turning nullable, a number turning into
  a string, a moved route, a renamed query parameter, a second instrument variant - and assert
  the failure names it. They report only what their drift *added*, so a real drift in the
  committed file shows up once, in the main test, rather than nine times.
- **Prose is stripped from the file.** `description` and `summary` go everywhere except under
  `properties`, where keys are field names; keys are sorted. A docstring edit is not a contract
  change, and a snapshot that moved with every one would train people to regenerate without
  reading the diff - the habit that lets a real change through.
- **No workflow change, and no new required check.** The Python half runs inside `uv run
  pytest` in `Agents (Python)` and the engine half inside `dotnet test` in `Engine (.NET)`, both
  of which branch protection already requires - so a drift fails a required check as things
  stand. **A named CI step was written and could not be pushed**: it regenerates the file with
  the documented command and `git diff --exit-code`s, so a failure is named for the contract and
  the command cannot rot. GitHub refuses a change under `.github/workflows/` from a token without
  the `workflow` scope, which this session's token lacks. It adds visibility, not protection, so
  the pull request went without it; the commit is kept as a patch for the owner to apply.
- **The `TAS_ENABLE_DOCS` defect is fixed**, as the plan assigned it here. A `DocsSwitch`
  settings class reads that one field from the same `src/agents/.env` and prefix as `Settings`,
  which inherits it rather than declaring it twice; it has to be its own class because
  `create_app` runs at import time, where `Settings` - every field required - cannot be read. A
  value that is not a bool now stops the import rather than meaning "off".

**The drift was proved end to end, not only by the eight cases.** Making
`TradeSignal.reference_price` nullable in pydantic - a change **no other test in either suite
notices**, because the field stays required and every example still reads - turned exactly one
Python test red (the snapshot comparison). Regenerating the file turned Python green and exactly
one engine test red: *"POST /v1/signals answer.reference_price: the agent service may send null,
and the engine's type cannot hold it"* - which in production would have been a refused answer the
first time the market data had no price. Two more, both reverted: a field added to
`InstrumentQuote` failed the snapshot comparison and then, once the file was regenerated, the
engine test, naming `exchange`; an optional `cash_buffer` added to the engine's `ScreenRequestDto` failed the engine
test, naming it as a field the service does not declare - the 422 every screen would have got.

**Verified as CI runs it, in a clean worktree:** `dotnet build -warnaserror` clean, `dotnet
format` clean, **528 .NET** tests green (513 before; the Testcontainers database tests included),
ruff and mypy clean, **507 Python** tests green (499 before), the regenerated file identical, 42 files in
the wheel. Both agent images built, and **the service image generates the committed file byte for
byte** - so the snapshot does not depend on anything only this machine has. The engine image could
not be built on this box: BuildKit fails to prepare the snapshot for the `migrations bundle` layer
(`failed to prepare ... invalid argument`) before the command runs, twice, once with the builder
cache disabled - an environment fault, in a Dockerfile this pull request does not touch.

### The review of #61 (2026-10-08)

PR 4 was built by another session. Reviewed here, **approved**, and merged. What follows is
what the review actually established, because re-running somebody's own tests establishes
very little.

**The chain was tested rather than the checker.** The author mutation-tested their walker with
eight drifts applied to an in-memory *copy* of the document - good, and it cannot show that
the two halves compose. So the review made `thesis` optional in the pydantic model instead:
Python's snapshot test went red; regenerating the document made it green again; and then the
**engine's** suite went red with two sentences -

```
POST /v1/signals answer: TradeSignalDto requires 'thesis', which the agent service may leave out
POST /v1/signals answer.thesis: the agent service may send null, and the engine's type cannot hold it
```

The other direction too: a field added to `TradeSignalRequestDto` produced *"sends
'operator_hint', which the agent service does not declare"*. That is the design working.

**Three things the review checked because they could have been hollow.**

- `IsRequired` reads `RequiredMemberAttribute`, so two of the rules depend on the DTOs using
  C#'s `required`. They do, throughout - the rules are live, not decoration.
- `contracts/openapi.json` reaches the test's output directory through the csproj's existing
  `contracts/**/*.json` wildcard, so no project change was needed. Correct, not an omission.
- The committed document carries no `servers` block, no hostname and zero prose keys.

**The engine image builds here.** The PR 4 section above records that BuildKit refused the
`migrations bundle` layer on the machine that wrote it. Both engine targets have been built
repeatedly in this session, so "an environment fault" is confirmed rather than assumed.

**Two findings, neither blocking, both now fixed in PR 5.** The caps test indexed straight
into `properties.thesis.maxLength` and arrived as a `NullReferenceException`; and this file's
list of stale remote branches named five where four exist.

**And one consequence nobody has to fix, but somebody has to know:** a fastapi or pydantic bump
now fails CI until the document is regenerated. That is correct - the served contract did
change - but it means those Dependabot pull requests need a regeneration commit, and whoever
makes it should read the diff rather than regenerate blindly. Carried into *Open decisions*.

### PR 5 - the criterion in CI, and #61's findings (`stage-6-compose-smoke`)

The stage's last pull request.

- **A `compose` job.** Cheap on every pull request - the file parses, every required variable
  has a value, and `engine` is absent from the default service list and present with
  `--profile trade`, which is **decision D2 as an assertion rather than a paragraph**. The rest
  runs on master and nightly: both migration containers exited 0, eleven engine migrations
  applied (counted off the files), Alembic stamped, `/health` 200, an unauthenticated signal
  401, and then behind the profile the engine logging `Application started` - which is logged
  only after `EnsureTheSchemaIsCurrentAsync` has passed, so one line is the whole chain.
- **A nightly trigger**, which the roadmap asks for and this job is the reason for: it is the
  only check that starts the system, and so the only one that would notice a base image moving
  or a port being taken on a day when nothing was pushed.
- **`docker compose up --wait` cannot be used, and finding that out was the work.** It reports
  failure when a container exits, **even with code 0** - `container engine-migrate-1 exited
  (0)`, exit status 1 - so a compose file that provisions through short-lived containers cannot
  be waited on as a whole. Waiting on the long-running service instead does work, and silently
  skips `engine-migrate`, since nothing but the engine depends on it: the step would have
  stopped testing half of what a plain `up` provisions while still passing. So it runs the
  command a person types and waits by hand.
- **No Ollama on a runner, and the agent service is healthy anyway.** PR 1's healthcheck
  decision turning out to be load-bearing rather than tidy: `/health` is liveness, so a missing
  LLM backend does not make the container unhealthy - and if it did, nothing gated on this
  service could start in CI at all. Verified with the backend pointed at a closed port, which
  is the condition CI actually has; the engine then logs `llm_unreachable`, answers no decision
  that cycle, and keeps running, exactly as designed.
- **`team_version` is compared every run now.** PR 3 read the hash out of a container's logs
  once and found the two configurations agreed *that day*. A test now builds a `ModelSpec` from
  the compose file's four hash-bearing values and another from `.env.example`'s, computes
  `compute_team_version` for every team, and compares the hashes - so it keeps asking the right
  question if the hash starts covering a fifth thing - then names which of the four differs,
  because a hash that differs says nothing about why. Only those four are resolved out of the
  compose file; the rest of that environment is secrets written `${VAR:?...}` with no default,
  which is right for a secret and would have nothing to compare against.
  *Mutation-tested:* a different model gives `Differing: {'MODEL': ('qwen3:14b', 'qwen2.5:14b')}`
  with both hash sets; a dropped seed fails both tests, one naming the missing key.
- **`pyyaml` is declared** rather than relied on through alembic and testcontainers, which both
  pull it in today. A test leaning on somebody else's transitive dependency breaks on a bump
  that had nothing to do with it.
- **#61's caps finding was more than a message.** With the constraints read through the walker's
  own `Unwrap`, a field that became optional no longer fails the caps test at all - the cap did
  not move, the nullability did, and that is the other test's business. Measured on the drift
  that found it: **two failures before, one after**, and the one that remains is the one that
  matters.

**Verified by running all nine steps of the new job**, in a git worktree - which compose names
a project of its own, so its volume was `ci-verify-pr5_trading-db-data` and `down -v` could not
reach the one holding 54 real decisions. The real container had to be removed for the run,
because `container_name` cannot be held by two projects; the volume was untouched and the
database came back with its 54 decisions, 5 positions and 87 920.14 kr.

**Review Bot on #62 (2026-10-09): accept-with-nits, one must-fix, all fixed on the branch.**
- **The D2 assertion could never fail.** `! docker compose config --services | grep -qx engine`
  was not the step's last line, and `bash -e` ignores a command negated with `!`. Shown in a
  scratch copy with the engine's `profiles:` line removed: the old step exited 0, the new
  `if ...; then exit 1; fi` form exits 1 with "engine is not behind its profile". **The check
  meant to be the stage's decision as an assertion had been a paragraph after all.**
- **The job has a project of its own** - `COMPOSE_PROJECT_NAME=tas-ci`, `DB_CONTAINER=tas-ci-db`,
  and `down -v` names `-p tas-ci` literally - so its steps pasted into a terminal cannot reach
  `trading-db-data`, and no longer need the real container removed first.
- `timeout-minutes: 30`; the engine-log check captures the log before grepping (`logs | grep -q`
  under pipefail reports an early match as a miss via SIGPIPE); `workflow_dispatch:` so the
  bring-up can run on a branch before merge. **It has not run on this branch yet:** dispatching
  needs the token's *Actions: write*, which it lacks (`HTTP 403`), so the first heavy run is
  still the owner's to trigger.
- Minor: the screen caps go through `Constraints`/`Unwrap` like the others, the cap message no
  longer reads "declares no a thesis cap", and the `.env.example` reader strips matching quotes.
- **Verified locally** short of the bring-up: D2 both ways, the SIGPIPE miss reproduced (status
  `141 0` on a match) and gone with the captured form, actionlint clean on the changed steps,
  .NET 528/528, Python 515/515. The job's steps ran under `tas-ci` beside the live stack and
  tore down only their own volume; the bring-up itself stopped at the migrations, because
  containers on this dev box cannot reach each other over a compose network (a plain TCP
  connect between two containers times out) - an environment limit, not the file's.

## Stage 7 log (2026-10-09 ->, in progress)

Read the stage in `docs/arkitektur-roadmap.md` first. On paper it is two days of work and seven
items:
- OpenTelemetry in the engine with its own metrics;
- `ag2[tracing]` and `TelemetryMiddleware` in Python, with `traceparent` carried from the engine
  so that one cycle is **one** trace;
- `capture_content=False`;
- `TradingMode: Shadow | Paper | Live` plus a kill switch;
- rate limiting;
- a deploy;
- a runbook.

The roadmap's check for the stage is *"en analyscykel syns som ett sammanhängande trace från
motorn genom agentkedjan"* ("an analysis cycle shows up as one connected trace, from the engine
through the agent chain").

### Five measurements taken before the plan was written

1. **There is no broker anywhere.** `Portfolio.ExecuteBuy` and `ExecuteSell` are the only paths
   that execute anything, and they write a simulated order into `trading.orders`. So **Paper is
   what the engine does today**, and *Live* has nothing to send an order to. That settles what
   the three modes can mean before any of them is built.
2. **Rate limiting is already done.** #52 added a token bucket per key and per scope on the agent
   service (`TAS_RATE_LIMIT_*`, `app/api/rate_limit.py`). What stage 7 still owes is a sentence
   in the runbook, not code.
3. **OpenTelemetry is half-present.** `opentelemetry-api` is already in `uv.lock` as a transitive
   dependency. ag2 1.1.1 is installed with the `openai` extra only, not `tracing`. The engine has
   no OTel package at all.
4. **A pulled switch costs at most one analysis.** A cycle is about eleven analyses at 20-30 s
   each, so a switch checked once per cycle could let up to five minutes of decisions through.
   Checking it before each analysis *and* immediately before each order bounds the damage to the
   analysis already in flight, and that analysis's order is still stopped.
5. **The engine's healthcheck was deferred to this stage.** `CLAUDE.md` says so: a Worker has no
   endpoint, so health needs a cycle heartbeat. That belongs with the metrics in PR 2.

### The decisions

| # | Question | Taken |
|---|---|---|
| E1 | What do the three modes mean when there is no broker? | **Shadow** analyses, sizes, risk-gates and records the decision, and places nothing. **Paper** is the simulated portfolio the engine has always run. **Live** exists in the enum so the word means one thing, but **startup refuses it** ("this system has no broker to send an order to"). Whether Live should ever exist is left to the owner. |
| E2 | What is the default? | **Shadow**, in `appsettings.json` and in compose (`${TRADING_MODE:-Shadow}`). The setting is required, so a configuration without it fails at startup. `OrderGate` still treats a missing value as Shadow, in case options validation is bypassed in a test host. The mode is read at startup; changing it means a restart, and that is deliberate. |
| E3 | Where does the kill switch live? | **The append-only table `trading.kill_switch`, where the latest row wins.** An operator INSERTs a row with psql, and the history records who pulled the switch, when and why. Rejected: a file or an environment variable (both need a container exec or a restart) and an HTTP endpoint (it would be the engine's first inbound surface). |
| E4 | What happens when the switch can't be read? | **It fails closed.** A read error or an empty table counts as engaged. The migration seeds one released row, so a fresh database trades as before. |
| E5 | Where is it checked? | **In three places:** at cycle start (exits, screening and analyses are all skipped and no agent is asked); before each analysis; and in `OrderGate`, immediately before each order. **It stops exits too**, because "stop trading" should mean no orders, sales included. Left open for review. |
| E6 | Are shadowed and halted decisions measured? | **Yes.** They keep their signal, so the outcome sweep scores them like any other. The mode is recorded on every decision (`decisions.trading_mode`), history is backfilled as `Paper`, and `trading.hit_rate` groups by it, so the two populations never pool. |
| E7 | Does the engine leave `--profile trade` now that the switch exists? | **Not in this PR.** D2 kept the engine behind the profile because not starting it was the only way to stop it. That reason is now gone, but lifting D2 changes what `docker compose up` does for the owner, so it is the owner's call and its own PR. |

### The pull requests

Six, in this order:
1. `TradingMode` and the kill switch.
2. Engine metrics (`decisions_total{outcome,mode}`, `risk_rejections_total`,
   `agent_latency_seconds`), plus an `ActivitySource` per cycle so that `HttpClient` carries
   `traceparent`, plus the cycle heartbeat and the engine's healthcheck.
3. Python tracing: `ag2[tracing]`, `TelemetryMiddleware`, `traceparent` extraction,
   `capture_content=False`, and a collector in compose. This is the PR the stage's check reads.
4. The engine leaves the profile (D2 revisited), if the owner says so.
5. `docs/runbook.md`: restarting, where the logs are, and how to stop trading (the kill switch),
   with rate limiting described rather than built.
6. The deploy, once a target is chosen: a VPS with compose, Azure Container Apps, or Fly.io.

### PR 1 - Trading:Mode and the kill switch (`stage-7-trading-mode`)

- **`Trading:Mode` and `OrderGate`.** The mode is a required option, validated at startup with
  Live refused. Between the risk engine's approval and the portfolio, the use case asks
  `OrderGate`, a single question, whether this approved order may be placed. It gets back
  `Granted`, `ShadowOnly` or `Halted(reason)`. A shadowed buy is recorded as
  `DecisionOutcome.Shadowed` with a reason like *"Shadow mode: would have bought 5 AAPL at 100
  SEK"*. Exits ask the same gate, sale by sale. The worker logs the mode at start, because a
  mode nobody can see is a mode nobody can check.
  *Mutation-tested:* Shadow answering `Granted` turns exactly four tests red, including the
  database test that a shadow engine records its decision and leaves cash and positions alone.
- **`decisions.trading_mode`** (migration `DecisionTradingMode`). It is added with a `'Paper'`
  default that is then dropped, because the table is append-only and an `UPDATE` backfill would
  be refused. The default fills the existing rows, and once it is dropped every new row has to
  name its mode. `trading.hit_rate` is recreated with the mode as a grouping column.
  *Tested both ways* on a row inserted before the migration: up gives `Paper`, an insert without
  a mode is refused, down removes the column and the row survives.
  *Mutation-tested:* keeping the default turns the migration test red.
- **The kill switch** (migration `KillSwitch`): the table, its append-only trigger, a
  not-blank-reason constraint, `changed_by` defaulting to `current_user`, and the seeded released
  row. `KillSwitch` reads the latest row with no tracking, and any exception reads as engaged
  with the exception's type in the reason.
- **The proof**, as two worker tests against a real database:
  - *Engaged before the cycle:* the engine is in Paper with a holding below its stop at 80 and an
    analysis due. It makes **no agent call at all**, places no order and no sale, and the
    portfolio is unchanged (still 1 order, 1 decision, position 2, cash 9 800).
  - *Pulled while the agents are thinking:* the switch is engaged from inside the fake agent's
    `GetSignalAsync`. The one decision in flight is recorded as `Halted`, no order exists, and
    the second instrument in the cycle is **never analysed**.

  *Mutation-tested:*
  - the gate ignoring the switch turns five tests red, including the mid-cycle proof;
  - the worker skipping both of its checks turns both worker proofs red;
  - `KillSwitch` failing open turns the unreachable-database test red.
- **The operator's commands** are in `CLAUDE.md`. Engage:
  `INSERT INTO trading.kill_switch (engaged, reason) VALUES (true, '<why>')`. Release: the same
  with `false`. The engine picks up either within one check, with no restart.
- **Local runs:**
  - .NET: 562/562, up from 528, including Testcontainers. `dotnet build -warnaserror` and
    `dotnet format --verify-no-changes` are clean, and `has-pending-model-changes` reports none.
  - Python: 515/515, with ruff check, ruff format and mypy clean as CI runs them. Python is
    untouched by this PR.
- **What changes for a running engine after the merge:** a host-run engine reads Shadow from
  `appsettings.json` and **stops placing orders, sales included**. The exits on the positions it
  already holds stop selling too: a stop-loss that fires is logged and not placed, so nothing
  closes those positions. To keep paper-trading, set
  `dotnet user-secrets set Trading:Mode Paper --project src/engine`, or `TRADING_MODE=Paper` in
  the root `.env` under compose. **Do this before the merge.** Shadow is the safe default, but it
  is a change of behaviour, and that is why this paragraph is here.

### The review of #63 (2026-10-09)

Review Bot: **accept-with-nits, no blockers, CI green.** Each fix below is its own commit on the
branch.

- **Shadow leaves held positions unmanaged, and only an Information line said so.** The default
  stays Shadow. Hampus sets `Trading:Mode=Paper` on his own machine before the merge. What
  changed:
  - at startup, in any mode but Paper, the worker reads the portfolio once, and if it holds
    anything logs a **Warning** naming the mode, the holdings and the setting to change;
  - tested both ways against a real database, and the mutation that silences it turns the test
    red;
  - the note is in `CLAUDE.md`, here, and at the top of the PR description as a pre-merge step.
- **"Would have bought" overstated, because Shadow consumed no budget.** Partly fixed:
  - **The fixed part:** `decisions.shadow_cost` (migration `DecisionShadowCost`) stores what a
    shadow buy would have cost. In Shadow, the use case adds the day's shadow costs to the
    ledger's before sizing and risk-gating, so the daily deployment limit binds as it would have
    in Paper. A check constraint keeps the cost on `Shadowed` rows only, and above zero.
  - **Documented, not fixed:** Shadow still does not consume cash or position headroom. It holds
    nothing, so that would take a second portfolio. At today's balances the daily limit (20 % of
    NAV) is reached long before the cash above the buffer, and across days a Shadow engine
    re-sizes against untouched cash.
  - *Mutation-tested:* inverting the mode check turns all three budget tests red.
- **`shortlist_edge` did not group on the mode.** Migration `ShortlistEdgeByMode` recreates it with
  `trading_mode` beside the day, and Down restores the previous definition word for word. Tested
  both ways, plus a day split across the two modes giving two rows with separate controls.
  `bought` still means an executed buy, so a Shadow row reports none. Whether a shadowed buy
  should count is left open.
- **E5 (the switch stops exits too) is Hampus's call.** Behaviour unchanged, and listed as an
  open decision.
- **Minor:**
  - the snapshot's BOM is restored;
  - a halted decision's reason carries the price, as a shadowed one does;
  - a shadow exit is logged when it starts firing, when the quantity changes, or when it fires
    again after stopping, not every fifteen minutes. A singleton compares pass with pass, and a
    repeat is a Debug line. *Mutation-tested* both ways: reporting every time, and remembering
    forever.
- **Local runs after the fixes:**
  - .NET: 573/573, including Testcontainers. The `-warnaserror` build, `dotnet format` and
    `has-pending-model-changes` are clean.
  - Python: 515/515, with ruff and mypy clean.

---

## Lessons and gotchas

Things that cost time or were not obvious. Most are also recorded where they apply.

**Containers (stage 6)**
- **A snapshot is only worth what reads it, and a checker is only worth what it can fail on.** A committed OpenAPI file that only Python compares against itself proves that Python is consistent with Python. It became a drift check when the engine's suite started reading the same file - and the engine's checker became trustworthy when eight tests applied a drift to a copy of the document and watched it go red. The one drift that convinced was the one nothing else caught: an answer field turning nullable while staying required.
- **A flag that controls a route does not control the generator behind it.** `TAS_ENABLE_DOCS=false` makes `/openapi.json` answer 404, and this file concluded from that that the specification could not be exported without turning the flag on. `create_app().openapi()` returns the whole document regardless - with no server, no database and no flag - because the flag is passed to `FastAPI(openapi_url=...)` and the generator is a method on the app. **A day of plan hung on confusing the door with the room behind it.**
- **A hash over file contents is a containerisation invariant, whether or not anyone meant it to be.** `team_version` is a sha256 over the prompt files' contents, so a build that changed a line ending would not fail - it would answer as a different team, and two populations that cannot be pooled would start accumulating under one name. Nothing in the build would look wrong. It is checked in CI by diffing the image's hashes against the repository's, which costs one step and closes a failure with no symptom.
- **A container's `127.0.0.1` is the container.** Mirrored networking puts WSL's localhost on Windows, which is why everything on this machine reaches Ollama at `127.0.0.1:11434` - and that stops being true one layer in. `--add-host=host.docker.internal:host-gateway` reaches the WSL host, which mirrored networking has already put on Windows, so the two mechanisms compose. Worth measuring before planning around: it was the stage's largest unknown and it took one `docker run`.
- **A CI step that writes a file can destroy the thing it guards.** To prove that a `.env` in the build context does not reach the image, the step first has to put one there - and `echo ... > src/agents/.env` is harmless on a runner and destroys a developer's real secrets the first time anyone runs the job by hand. `test -f ... ||` is the whole fix. The guard was worth keeping; the way it was written was worse than what it guarded against.
- **`docker compose up --wait` reports failure when a container exits, even with code 0.** A compose file that provisions through short-lived containers therefore cannot be waited on as a whole, which is most of the point of `--wait`. Waiting on the long-running service instead works and is the trap: its `depends_on` pulls in only *its* dependencies, so `engine-migrate` was silently skipped and the step would have kept passing while testing half of what a plain `up` provisions. **The version of a check that still passes while measuring less is the dangerous one.**
- **Reviewing somebody else's tests by running them establishes almost nothing.** #61 mutation-tested its checker against an in-memory copy of the document - correct, and unable to show that the two halves composed. Changing the pydantic model instead showed the whole chain: Python red, regenerate, engine red with two sentences. **The question to ask a test suite is not "does it pass" but "what would have to be true for it to be wrong", and then do that thing.**
- **A check that reports a stack trace in a file whose other failures are sentences is a defect, not a style complaint.** And following it paid: resolving the caps through the walker's own `Unwrap` did not merely reword the failure, it stopped the test failing for something that was not its business. Two failures became one, and the one left was the one that mattered.
- **A required-checks list does not follow the workflow file.** Three CI jobs were added during stage 6 and nothing in the repository can say whether branch protection requires them - that setting is only visible in GitHub's UI. This file asserted twice that the required set had kept up. A job that runs and is not required is a job whose red cross somebody can merge past.
- **A separation enforced by two files stops being a separation the moment one tool reads both.** The agent service never saw the engine's database password because they lived in different files - true, and it stopped being the mechanism the day compose needed to hand one shared secret to both services. What enforces it now is each service's explicit `environment:` list, which is a thing you can read rather than a thing you have to remember. The lesson is not "don't use files"; it is that **a safety property should be stated where it is enforced**, and CLAUDE.md was still describing the old enforcement.
- **A comment that is true in one branch and silent about the other is an incomplete comment.** The engine Dockerfile said a bundle run without `--connection` fails to resolve a hostname. It does - unless `ENGINE_DATABASE_URL` is set, which the design-time factory reads and the bundle invokes at run time. Testing the claim found the second route, and the second route is the better one for compose, because it keeps the password out of the container's rendered command. **The comment did not just need fixing; following it is what chose the design.**
- **A fixed `container_name` is what stops a second stack existing.** That matters because the only honest way to test "`up` from a clean state" is a second stack: a compose project of its own gets its own volume, so a clean start does not mean deleting the volume holding real decisions. The name and the published ports became variables with today's values as defaults - not a feature looking for a use, but the thing that made the test possible.
- **Four values in a compose file are part of `team_version`.** Provider, model, temperature and seed feed the hash, and the compose file has to spell them out because a clean checkout has no `src/agents/.env`. Two places holding the same four values is how the same team comes to answer under two versions, with two unpoolable populations accumulating under one name - and nothing would look wrong. Checked by reading the hash out of the clean stack's logs; worth a CI step rather than a check somebody remembers.
- **`.gitignore` and `.dockerignore` are two lists of the same thing, and nothing keeps them in step.** Both say "this must not leave the machine"; one is about commits and the other about layers. The engine image's first build published a gitignored `appsettings.Development.json`, because the ignore file knew about `.env` and not about the line beside it. The durable fix is not two more patterns - it is a check that asks `git check-ignore` about every file the image published, which needs no editing when the next local-only file is invented.
- **A mutation that fails to build is not a surviving mutation.** The attempt to prove the leak check worked tried to `COPY` the gitignored file into a test image, and the build failed - because `.dockerignore` now refuses it into the context at all. The loop then ran against an image that did not exist and printed "survived", which reads like the check being blind. Second attempt created the file *inside* the image and it was killed immediately. Same shape as the .NET mutations that did not compile: **the thing to check first is that the mutant exists.**
- **A number written into prose is a number nobody updates.** This file claimed twelve engine migrations in two places, including inside the lesson about sections going stale, and three Alembic revisions when there are five. There are eleven and five, both now checked against the database. CI reads the engine's count off the migration files rather than holding it, which is the only version of this that stays true.
- **Dropping a table is DDL, not a `DELETE`.** The append-only triggers refuse `UPDATE` and `DELETE`, so it was an open question whether the engine's migrations could reverse against a database with rows in it. They can: a full revert dropped all eight tables and both views with four positions and ten decisions in them. The agent side is the harder case for an unrelated reason - a widen-in-place migration cannot reverse while a stored value needs the extra width.
- **An image that installs its own source cannot hide a missing file.** `uv sync --no-editable` puts `app` in site-packages instead of pointing at a copied directory, so there is no working directory for an import to resolve against. That is the same regression CI already guards with a wheel check - `app` was once missing from the wheel and the service ran anyway, because the directory it started in held the source - and the image closes it structurally rather than by assertion.

**Running it, not only testing it**
- **A code path that does nothing successfully should still say so.** The deterministic exits wrote no log line on a pass where every holding was fine, and they write no decision row by design - so a quiet cycle was indistinguishable from the exits never running and from every holding being unpriceable. Only a live run shows you that, because a test asserts on what happened and an operator has to read what did not. Two counts fixed it: judged, and of how many held.
- **The log is where an ordering becomes checkable by a human.** `GET /v1/quotes/ERIC-B.ST` on line 37 and the first `POST /v1/signals` on line 70 is the proof that the exits run before the analyses, in a form no test produces. The test that proves the same thing asserts that the analysis was told about *no* existing position - which is stronger, and unreadable to anyone who has not read the test.

**Working on uncommitted code**
- **`git checkout -- <file>` on a file that is only in the working tree deletes the work.** It restores from `HEAD`, which for uncommitted work means "before I started". Used as the revert step of a mutation test, it silently wiped the sell branch of `PositionSizer` and the sell overload of `RiskEngine`; the next mutation then failed to build, and its own revert wiped the second file too. **Back the file up to the scratchpad and copy it back.** The tell that something was wrong was a test run that printed no summary at all - a build failure, not a failing test.
- **Mutate one thing, run, restore, and check the restore.** `grep` for the original text after copying back costs nothing and is the difference between noticing this in a minute and noticing it at the commit.
- **It happened again in PR 4**, on `DecisionLog.cs`, with this lesson already written down - so the guard is not remembering it. The guard is that the *first* command of every mutation copies the file to the scratchpad, in the same shell line as the edit, so there is never a moment where the only copy is the one about to be overwritten. Every mutation on that branch that did so was fine; the one that reached for `git checkout --` instead lost a method.
- **A mutation that does not compile is not a passing mutation.** `if (false)` trips unreachable-code analysis under `TreatWarningsAsErrors`, and `GroupBy(x => x)` changes the key's type. In both cases the test run printed no summary at all, which reads exactly like "nothing went red" if you are only grepping for failures. Grep for `Build succeeded` too.

**Time, ids and append-only tables**
- **A version 7 GUID is ordered to the millisecond and no further.** .NET fills the bits after the timestamp at random, so two ids created in the same millisecond sort arbitrarily. A test that ordered the ledger by id passed in a full run and failed when it ran alone. `placed_at` cannot break the tie either: it defaults to `now()`, which is the *transaction's* clock and identical for every row the transaction writes. Nothing in the engine reads the ledger in order today, and the configuration comment claiming the primary key gives it one is now honest about the limit.
- **`ADD COLUMN ... DEFAULT` is how you backfill an append-only table.** `trading.orders` raises on `UPDATE`, so a migration's backfill statement would be refused by the table's own trigger - but adding a column with a default is not an UPDATE and never fires it. Drop the default in the next statement, or an `INSERT` that forgets the column silently records the default as though somebody meant it.
- **A generated migration's default is a placeholder, not a value.** EF proposed `defaultValue: ""` for the new enum column - a value the enum cannot produce and nothing downstream could read. The right default was the one that is *true about the history*: every order in the table was a buy the agents argued for, so `Signal`.

**Running the real thing**
- **`dotnet ef database update` needs `ENGINE_DATABASE_URL`, and says something else when it is missing.** The design-time factory deliberately uses `Host=design.invalid`, because `migrations add` and `migrations script` never open a connection and going through the host would demand the whole engine's configuration on a machine that only wants to write a file. A command that *does* connect therefore fails with `Name or service not known`, which reads like a broken database rather than a missing variable. The working form is `ENGINE_DATABASE_URL="Host=127.0.0.1;...;Password=$(grep '^ENGINE_DB_PASSWORD=' .env | cut -d= -f2-)" dotnet dotnet-ef database update --project src/engine`, which keeps the password out of the terminal and out of the shell history.
- **There is a stale `ConnectionStrings:Database` user secret with `Host=localhost`.** Nothing reads it - the engine reads `Database:ConnectionString` and the design-time factory reads the environment variable - but it sent this session looking for a configuration bug that did not exist, because listing the secrets showed two connection strings and one of them used the host CLAUDE.md warns against. Worth deleting.
- **A claim about what a cycle does *not* do needs a log, not a test.** Every test in the suite proves something happens; the fourth pull request's whole point was that two cycles out of three stop happening, and the only honest evidence was three cycles of real output plus the agent service's own request counts as an independent second witness. Shortening the interval to a minute made it observable without touching the rule under test, because the rule is about the day and the price.
- **A measured number beat a reasoned one twice in a day.** The rate-limit headroom was argued from `ShortlistSize` and the real constraint turned out to be per-analysis latency; the liquidity floor was argued as a safeguard and turned out to filter nothing. Both arguments were sound and both were about the wrong variable.

- **Cross-check documentation against the code mechanically, not by reading harder.** After the review caught stale claims a grep could not, levelling `CLAUDE.md` used a script instead: extract every `TAS_` variable the document names and diff it against `.env.example`, and extract every options property from `TradingOptions`, `AgentServiceOptions` and `RiskPolicyOptions` and check each appears in the text. It found four things no amount of careful reading had: the document named `DATABASE_URL` without the `TAS_` prefix its own rule demands, two settings the screen introduced (`TAS_SCREEN_TIMEOUT_S`, `TAS_SCREEN_TTL_S`) had never been documented at all, `MaxPositionPercentage` and `CashBufferPct` were described in prose but never named, and four scoped keys were written as a shorthand nobody could grep for. **A document about configuration can be diffed against the configuration.**

- **A grep is not a review of a section that describes the present.** Twice now this file has gone stale in a part I did not touch, and twice I swept for it by grepping phrases I expected to be wrong - which finds what you already suspect and, by construction, nothing else. An external review read *Current state* top to bottom and found in minutes that it still named a configuration setting deleted two pull requests earlier. **Sections that describe today get read; sections that describe history get grepped.**

**Checking the premise**
- **A whole pull request disappeared into one SQL query.** Two documents and a review all said the shortlist comparison needed a second measured population in `MeasurementWorker`. One `GROUP BY d.outcome` over `signal_outcomes` showed 20 HOLD and 41 NotSized measurements already stored, because stage 4 had deliberately measured every signal rather than every trade. The plan had been repeated often enough to stop being questioned. **Before building what a plan calls for, ask the database whether it is already there** - it is one query, and it is the cheapest piece of work available.
- **`net_edge` is null for every HOLD, and a report that averages it lies quietly.** Three columns in `signal_outcomes` look interchangeable and are not: `instrument_return` and `excess_return` are facts about prices and populated on every measured row, while `net_edge` needs a stance to be computed against and is null for the answer the agents give most often. Averaging it across a mixed population silently averages the buys alone. `SELECT stance, count(col) ... GROUP BY stance` over every candidate column, before writing the view, is what caught it.
- **`UseSnakeCaseNamingConvention` applies to a query type too.** `SqlQueryRaw<T>` looks for the snake_case column each property maps to, so aliasing the columns to the property names in the SQL is what *breaks* it - nine tests failed on `The required column 'agents_edge_gross' was not present`.
- **A backup step that can fail silently is not a backup step.** The mutation harness copies the file to the scratchpad in the same command as the edit, which is the guard written after PR 4 - and it failed anyway, because `mkdir` had never run in a new session and the `cp` was chained after a `cd` that succeeded. The mutation then ran on a file that was not yet committed, so no copy existed anywhere, and only the fact that it was one known string replacement made it reversible. `mkdir -p` first, and check the copy exists before touching the original.

**Rules that only bite outside a test's imagination**
- **Mutation testing earns its keep when it fails to kill a mutation.** Twice in PR 4 a mutation left the suite green, and both times the tests were wrong rather than the mutation harmless: the missing cases were "analysed today *and* the price unchanged" and "a cycle that never reached an answer". Both are the *commonest* states the rule meets in production, and both were invisible because every existing test happened to vary two things at once.
- **A pure function's two conditions need a test where both are true.** Ordering two guards is itself a rule, and it is unobservable from cases where only one of them fires. If swapping two `if`s changes nothing, there is a missing test rather than a redundant check.
- **A new table with no foreign key escapes `TRUNCATE ... CASCADE`.** The test fixture cleared the portfolio graph and `shortlists` sat outside it, so rows leaked between tests and three assertions failed for reasons that had nothing to do with what they were testing. Name every table in the reset, or make the reset enumerate the schema.
- **Read the implementation before trusting a contract's prose.** `contracts/screen.schema.json` said `rejected` holds every instrument that was looked at and left out; the service truncates to the shortlist size, so a name that ranked 15th of 31 is in neither array. The schema validated everything either way - prose is not a constraint, and only reading `screening.py` showed the difference.
- **"Daily data" is not the same as "a daily fact sheet".** The returns, the volatility and the turnover come from daily bars, but the price comes from the provider's live quote and one derived field is computed from it. A rule written on the roadmap's summary of the data would have been wrong in exactly the hours it mattered.

**Widths, and the tests that never wrote anything wide**
- **Widening a validation rule is not widening the column behind it.** The symbol pattern went from 10 characters to 16 for Swedish tickers, and six database columns across two schemas stayed at `varchar(10)`. The first real write would have been `22001: value too long for type character varying(10)` - and for the benchmark it would have been every night, in the sweep. Grep for every column that holds the value, not only the places that validate it.
- **A suite can be green because it never used the interesting value.** No test in either service had written a symbol longer than four characters to a real column, so nothing noticed. The guard that works is a round trip at the maximum the rule admits, and it belongs next to the column rather than next to the rule.
- **A migration that widens in place cannot always be reversed.** Narrowing refuses while a stored value needs the extra width, which is correct - losing information quietly is worse than stopping - but it means a test fixture running `downgrade base` has to clear the tables first. Append-only tables cannot be cleared by a `DELETE`, and `TRUNCATE` is the statement the triggers deliberately allow.
- **Postgres refuses to alter the type of a column a view selects.** Adding a column to a view's grouping and widening that column in the next migration is a dependency you created for yourself; the widening has to drop and rebuild the view around itself.

**Verifying an external symbol**
- **Check a benchmark on every path it is used on, not the convenient one.** `XACT-OMXS30.ST` works through `yf.download`, which is what the screen uses - but the nightly sweep reaches it through the per-instrument fetch, which raises `InstrumentNotFound` when `.info` carries no price. A benchmark that worked on one path and not the other would have failed at night, in the one job nobody is watching.
- **Check the whole list against the rule, not one member of it.** Two of thirty OMXS30 tickers would have been rejected by the ten-character symbol cap, and neither was among the ones tried by hand. Generating the full list and matching every entry took one command and found both.

**Editing files with a script**
- **Assert on a whole line, not a prefix of one.** A replacement anchored on `from app.application.pipeline import SignalPipeline` matched a line that continued `, TeamRuntime`, so the insertion landed mid-statement and moved a name onto the wrong module. The match count was 1 and the assertion passed, because a substring is a match. Anchor on text that reaches the end of the line, or include the following line.
- **Check every edit before writing any of them.** The pattern that keeps this safe: build the whole list of (path, old, new), assert every `old` appears exactly once, and only then write. A heredoc that dies halfway has otherwise left some edits applied and some not, and once a commit has gone out on top of that the difference is invisible.

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
