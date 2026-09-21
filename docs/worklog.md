# Worklog

A running record of what has been done, what was learned along the way, and what comes next. It sits between two other documents:

- [`docs/arkitektur-roadmap.md`](arkitektur-roadmap.md) (Swedish) is the plan: the decisions, the stages, and how each stage is verified.
- [`CLAUDE.md`](../CLAUDE.md) describes the repo as it is today: commands, environment, architecture.

This file answers "where are we, how did we get here, and what is next". When resuming, read *Current state* and *Next steps* first, then the roadmap section for the next stage.

**Last updated:** 2026-09-21, after PR #25. **Stage 2 is done.**

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
| `AGENT_API_KEY`, `LLM_TIMEOUT_SECONDS` and the rest | `src/agents/.env`, gitignored - see `.env.example` | yes, 2026-09-20 |
| `AgentService:ApiKey`, the same value | .NET user secrets, `~/.microsoft/usersecrets`, outside the repo | yes, 2026-09-20 |

`dotnet user-secrets list --project src/engine` shows whether the engine has its key, and prints the value, so do not run it where anyone can see the screen. Both sides must hold the *same* key or every cycle ends in 401.

Two things that are easy to misread as broken:

- **Docker Desktop's WSL integration can be off** while Docker itself runs on Windows. `docker` then fails in WSL, but `trading-db` may well be running and reachable at `127.0.0.1:5432` anyway, because WSL is in mirrored mode. Check the port before assuming the database is down.
- **A dead port hangs rather than refuses** in mirrored mode, so anything without an explicit timeout looks like a freeze. Both services now have those timeouts; remember it when adding a new client.

---

## Current state

- **Stage 0 done** 2026-09-19, **stage 1 done** 2026-09-20, **stage 2 done** 2026-09-21. Stages 3-8 exist only as plan.
- **`master` is at PR #25.** Nothing reaches it without the three required checks passing, so what is there is green by construction.
- **No open pull requests. Branches:** `master` only, locally and on GitHub.
- **Stage 2 changed nothing you can see when you run it**, on purpose. The whole new path - contract, instrument union, sizing, the risk gate - exists behind the seam and is exercised only by tests. Stage 3 switches it on. Running the engine today still gives the stage 1 behaviour: three agents on the old `InvestmentProposal` contract, `quantity: 1` at the proposed amount, and `RiskEngine.ValidateTrade` rejecting most of it.
- **What is now impossible** rather than merely unlikely: the agents cannot name an amount (the contract has no `amount_usd`, and a test refuses one that reappears); an answer that is not the contract cannot deserialise into nulls; a position cannot be sized against cash instead of net asset value; and an order cannot be placed on a quote that is stale or dated in the future.

## Next steps

**Stage 3 - the agent service on the new contract** (roadmap estimate: 3-4 days). Read `docs/arkitektur-roadmap.md` first; PR #22 added two paragraphs at the top of that stage which change what it delivers.

The stage's own framing matters more than its parts: *what it delivers is the experiment cycle, not the team.* The machinery - `TeamSpec`, typed handoffs, prompt files, `team_version` - is what gets built. Which team is actually good is settled by stage 4's outcomes, not by guessing now.

Roughly, in the order the roadmap puts them:

1. **Provider factory** in `app/infrastructure/llm/provider.py`, returning AG2's own `ModelConfig`, with a lazy import per branch. Switching provider becomes an environment variable.
2. **`TeamSpec` and typed handoffs** - a step reads named earlier results rather than a shared transcript, validated at startup: the last step must produce `TradeSignal`, a step may not read a later step's schema, and two steps may not share an `output_schema`.
3. **`FactSheet` in `app/domain/facts.py`** - pure functions over market data. Its shape is a decision the roadmap now flags explicitly: free text versus numbers is expensive to change afterwards.
4. **Prompts as files**, hashed into `team_version` together with the spec, so a changed prompt is a new version.
5. **The switch-over**: `POST /v1/signals`, the engine calls the new endpoint, and the old path plus `RiskViolationException` and `ValidateTrade` are deleted. Finding B goes with them.

Before stage 4, decide the cost model in the outcome function (finding F) and how attribution will work. Before stage 5, decide whether a trading calendar is a domain concept (finding E), and turn finding D's currency mismatch into an outcome rather than a throw.

## Open findings

Six findings from a review of the repository on 2026-09-20. Every one was reproduced before it
was written down, and the reproduction is the *Verified* line. They live here rather than in the
roadmap on purpose: the plan should change when a stage starts, not every time a finding arrives.

| | Finding | Status |
|---|---|---|
| A | A failed analysis is sent twice | **Fixed** in PR #17 |
| B | A ticker is interpolated into the URL without escaping | Open - **stage 3**, when the old path is switched off |
| C | The proposal DTO accepts an answer that is not the contract | **Fixed** in PR #20 |
| D | A currency mix is reported as a bug, not as an outcome | **Fixed** in PR #23 |
| E | There is no trading calendar anywhere in the plan | Open - decide before stage 5 |
| F | Outcome measurement ignores transaction costs | Open - decide before stage 4 |

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

### B — a ticker is interpolated into the URL without escaping (moved to stage 3)

**Still open, and deliberately not fixed in stage 2.** The new contract carries an
`instrument` object rather than a ticker in the path, so the interpolation disappears with the
old endpoint rather than being patched. Stage 3 switches that over; until then the tickers
still come from `appsettings.json`. The format rule on the value object is worth doing anyway,
since screening will produce symbols from market data.

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

### E — there is no trading calendar anywhere in the plan

The system analyses every 15 seconds, at night and at weekends, on stale quotes. Stage 5 fixes the
*cadence* ("the cycle follows the data"), and stage 2 checks `quote_as_of` for freshness, but
market hours, holidays and half days do not exist as a domain concept. For a system whose whole
goal is short-term movement, a trading calendar is a first-class domain concept, not a detail —
and "is the market open" is a pure function, so it is cheap to get right.

### F — outcome measurement ignores transaction costs

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
- `pkill -f "[p]attern"` still kills the shell that runs it if the literal text appears **later on the same command line**, for example when the same command restarts the process it just stopped. Stopping and starting belong in separate commands.
- Ollama runs on Windows, and WSL reaches it at `127.0.0.1` only in mirrored networking mode. Use `127.0.0.1`, never `localhost`.
- Searching for `[åäö]` misses Swedish words without those letters. Two exception messages survived PR #2 that way.
