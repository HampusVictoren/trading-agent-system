"""What each step is told, and what comes out the other end.

The pipeline is run against a fake StepRunner here - no AG2, no network. That is the point
of the port: the ordering, the handovers and the message building are decisions of this
layer and can be checked without a model. tests/test_ag2_runner.py covers the other half.
"""

import json
import logging
from datetime import UTC, datetime

import pytest

from app.application.errors import InstrumentNotSupported, MarketDataUnavailable, UnknownTeam
from app.application.pipeline import (
    DATA_CLOSE,
    DATA_OPEN,
    MEMORY_KEY,
    SignalPipeline,
    TeamRuntime,
    describe_reading,
)
from app.application.teams import DEFAULT_TEAM, StepSpec, TeamSpec
from app.domain.facts import FactSheet, MarketSnapshot, PriceBar, Quote
from app.domain.signals import (
    ExistingPosition,
    SignalRequest,
    Stance,
    TradeView,
)
from app.domain.steps import MarketRead, RiskAssessment, Severity, Trend, Valuation

VERSION = "abc123def456"
QUOTE_TIME = datetime(2026, 9, 23, 14, 0, tzinfo=UTC)

A_READ = MarketRead(trend=Trend.UP, valuation=Valuation.FAIR, observations=["Stabilt."])
AN_ASSESSMENT = RiskAssessment(downside=Severity.MEDIUM, veto=False, risks=["Utsträckt."])
A_VIEW = TradeView(
    stance=Stance.BUY, conviction=0.7, thesis="Momentum.", key_risks=["Värdering."], horizon_days=5
)

SCRIPT = {"market_analyst": A_READ, "risk_manager": AN_ASSESSMENT, "portfolio_manager": A_VIEW}


class RecordingRunner:
    """Answers from a script and keeps every message, so the handovers are inspectable."""

    def __init__(self, script=None, failure: Exception | None = None) -> None:
        self.script = script or SCRIPT
        self.failure = failure
        self.calls: list[tuple[str, str, type]] = []

    async def run_step(self, role, message, schema):
        self.calls.append((role, message, schema))
        if self.failure is not None:
            raise self.failure
        return self.script[role]

    def message_to(self, role: str) -> str:
        return next(message for called, message, _ in self.calls if called == role)

    def data_given_to(self, role: str) -> dict:
        message = self.message_to(role)
        block = message.split(DATA_OPEN, 1)[1].split(DATA_CLOSE, 1)[0]
        return json.loads(block)


class RecordingJournal:
    """Keeps what it was asked to store, or fails on purpose."""

    def __init__(self, failure: Exception | None = None) -> None:
        self.failure = failure
        self.runs = []

    async def record(self, run) -> int:
        if self.failure is not None:
            raise self.failure
        self.runs.append(run)
        return len(self.runs)

    @property
    def only(self):
        assert len(self.runs) == 1, f"expected one run, got {len(self.runs)}"
        return self.runs[0]


class RecallingMemory:
    """Answers with fixed text and keeps what it was asked and told."""

    def __init__(self, recalled: str = "Inget mätt än.") -> None:
        self.recalled = recalled
        self.queries: list[tuple[str, str, str]] = []
        self.remembered: list[tuple[int, str]] = []

    async def recall(self, symbol, query, correlation_id, limit=3) -> str:
        self.queries.append((symbol, query, correlation_id))
        return self.recalled

    async def remember(self, analysis_run_id, text) -> None:
        self.remembered.append((analysis_run_id, text))


class StubMarket:
    def __init__(self, price: float = 338.98, failure: Exception | None = None) -> None:
        self.price = price
        self.failure = failure
        self.asked: list[str] = []

    async def snapshot(self, symbol: str) -> MarketSnapshot:
        self.asked.append(symbol)
        if self.failure is not None:
            raise self.failure
        return MarketSnapshot(
            quote=Quote(
                symbol=symbol,
                currency="USD",
                price=self.price,
                pe_ratio=35.35,
                sector="Technology",
                as_of=QUOTE_TIME,
            ),
            history=tuple(
                PriceBar(on=datetime(2026, 1, 1).date().replace(day=day), close=100.0 + day)
                for day in range(1, 29)
            ),
        )


def a_request(**overrides) -> SignalRequest:
    defaults = {
        "instrument": {"type": "equity", "symbol": "AAPL"},
        "team_id": "default",
        "as_of": "2026-09-23T14:00:00Z",
        "existing_position": None,
        "available_risk_budget_usd": 412.75,
        "max_position_pct": 0.05,
        "correlation_id": "c-1",
    }
    return SignalRequest.model_validate(defaults | overrides)


def a_pipeline(
    runner=None, market=None, team=DEFAULT_TEAM, journal=None, memory=None
) -> tuple[SignalPipeline, RecordingRunner]:
    runner = runner or RecordingRunner()
    runtime = TeamRuntime(spec=team, version=VERSION, runner=runner)
    pipeline = SignalPipeline(
        {team.id: runtime},
        market or StubMarket(),
        journal or RecordingJournal(),
        memory or RecallingMemory(),
    )
    return pipeline, runner


class TestAFullRun:
    async def test_it_returns_the_agents_view_joined_with_the_facts(self):
        pipeline, _ = a_pipeline()

        signal = await pipeline.run(a_request())

        assert signal.stance is Stance.BUY
        assert signal.conviction == 0.7
        assert signal.thesis == "Momentum."
        assert signal.horizon_days == 5

    async def test_the_price_comes_from_the_fact_sheet_and_not_from_a_model(self):
        # The guard. The engine sizes the order as floor(budget / reference_price), and no
        # step is ever asked for that number - TradeView does not have the field.
        pipeline, _ = a_pipeline(market=StubMarket(price=499.5))

        signal = await pipeline.run(a_request())

        assert signal.reference_price == 499.5
        assert signal.quote_as_of == QUOTE_TIME

    async def test_the_instrument_comes_from_the_request(self):
        # Not from the model either: asked to echo it, a model has answered TSLA to a
        # question about AAPL in this repo before.
        pipeline, _ = a_pipeline()

        signal = await pipeline.run(a_request(instrument={"type": "equity", "symbol": "MSFT"}))

        assert signal.instrument.symbol == "MSFT"

    async def test_the_run_carries_the_teams_identity(self):
        pipeline, _ = a_pipeline()

        run = (await pipeline.run(a_request())).run

        assert run.team_id == "default"
        assert run.team_version == VERSION
        assert run.revisions == 0

    async def test_every_step_runs_once_in_order(self):
        pipeline, runner = a_pipeline()

        await pipeline.run(a_request())

        assert [role for role, _, _ in runner.calls] == list(DEFAULT_TEAM.roles)

    async def test_each_step_is_asked_for_its_own_schema(self):
        pipeline, runner = a_pipeline()

        await pipeline.run(a_request())

        assert [schema for _, _, schema in runner.calls] == [
            step.output_schema for step in DEFAULT_TEAM.steps
        ]


class TestWhatEachStepIsTold:
    async def test_a_step_is_given_exactly_what_it_reads_and_nothing_more(self):
        # This is what makes the cost of a step constant instead of growing: the handover
        # is the schemas in `reads`, not everything that has happened so far.
        pipeline, runner = a_pipeline()

        await pipeline.run(a_request())

        for step in DEFAULT_TEAM.steps:
            assert set(runner.data_given_to(step.role)) == {s.__name__ for s in step.reads}

    async def test_the_portfolio_manager_is_given_no_raw_numbers(self):
        pipeline, runner = a_pipeline()

        await pipeline.run(a_request())

        data = runner.data_given_to("portfolio_manager")
        assert FactSheet.__name__ not in data
        assert "338.98" not in runner.message_to("portfolio_manager")

    async def test_the_risk_budget_reaches_no_step(self):
        # Under decision 1 no agent produces an amount, so a budget is a figure it cannot
        # act on - and a figure in a prompt is one a model starts reasoning about. The
        # field stays in the contract for stage 4; it just never reaches a model.
        pipeline, runner = a_pipeline()

        await pipeline.run(a_request(available_risk_budget_usd=412.75, max_position_pct=0.05))

        for _, message, _ in runner.calls:
            assert "412.75" not in message
            assert "0.05" not in message

    async def test_only_the_step_that_asks_is_told_about_a_holding(self):
        pipeline, runner = a_pipeline()

        await pipeline.run(a_request(existing_position={"quantity": 3, "average_price": 210.4}))

        assert "210.4" in runner.message_to("portfolio_manager")
        assert "210.4" not in runner.message_to("market_analyst")
        assert "210.4" not in runner.message_to("risk_manager")

    async def test_no_holding_is_said_out_loud_rather_than_left_out(self):
        # A missing line reads to a model as an oversight; "inget" is an answer.
        pipeline, runner = a_pipeline()

        await pipeline.run(a_request(existing_position=None))

        assert "Nuvarande innehav: inget" in runner.message_to("portfolio_manager")

    async def test_the_data_is_delimited(self):
        # The prompts tell the model that everything between these markers is data rather
        # than instructions, so the markers have to actually be there.
        pipeline, runner = a_pipeline()

        await pipeline.run(a_request())

        for _, message, _ in runner.calls:
            assert message.count(DATA_OPEN) == 1
            assert message.count(DATA_CLOSE) == 1

    async def test_the_instrument_reaches_every_step(self):
        pipeline, runner = a_pipeline()

        await pipeline.run(a_request())

        for _, message, _ in runner.calls:
            assert "Instrument: AAPL" in message

    async def test_a_later_step_is_not_told_more_than_an_earlier_one(self):
        # The cost of a handover is set by the schemas, so a three-step team must not
        # hand the last step three times as much as the first.
        pipeline, runner = a_pipeline()

        await pipeline.run(a_request())

        analyst = len(json.dumps(runner.data_given_to("market_analyst")))
        manager = len(json.dumps(runner.data_given_to("portfolio_manager")))
        assert manager < analyst * 2


class TestWhatCanGoWrong:
    async def test_an_unknown_team_is_refused_rather_than_defaulted(self):
        pipeline, _ = a_pipeline()

        with pytest.raises(UnknownTeam, match="experimental"):
            await pipeline.run(a_request(team_id="experimental"))

    async def test_no_step_runs_for_an_unknown_team(self):
        pipeline, runner = a_pipeline()

        with pytest.raises(UnknownTeam):
            await pipeline.run(a_request(team_id="experimental"))

        assert runner.calls == []

    async def test_an_instrument_the_team_does_not_cover_is_refused(self):
        narrow = TeamSpec(
            id="default",
            instrument_types=frozenset({"future"}),
            steps=DEFAULT_TEAM.steps,
        )
        pipeline, runner = a_pipeline(team=narrow)

        with pytest.raises(InstrumentNotSupported, match="equity"):
            await pipeline.run(a_request())

        assert runner.calls == []

    async def test_market_data_failing_stops_the_run_before_any_model_is_paid_for(self):
        market = StubMarket(failure=MarketDataUnavailable("yfinance down"))
        pipeline, runner = a_pipeline(market=market)

        with pytest.raises(MarketDataUnavailable):
            await pipeline.run(a_request())

        assert runner.calls == []

    async def test_a_failing_step_stops_the_run(self):
        runner = RecordingRunner(failure=MarketDataUnavailable("anything"))
        pipeline, _ = a_pipeline(runner=runner)

        with pytest.raises(MarketDataUnavailable):
            await pipeline.run(a_request())

        assert len(runner.calls) == 1


class TestASingleStepTeam:
    async def test_it_works_and_reads_only_the_fact_sheet(self):
        # The smallest legal team. Worth having, because the loop's bookkeeping is easy to
        # write in a way that only happens to work for three steps.
        solo = TeamSpec(
            id="default",
            instrument_types=frozenset({"equity"}),
            steps=(
                StepSpec(
                    role="portfolio_manager",
                    prompt_file=DEFAULT_TEAM.steps[-1].prompt_file,
                    output_schema=TradeView,
                ),
            ),
        )
        pipeline, runner = a_pipeline(team=solo)

        signal = await pipeline.run(a_request())

        assert signal.stance is Stance.BUY
        assert set(runner.data_given_to("portfolio_manager")) == {FactSheet.__name__}


def test_an_existing_position_is_a_real_model():
    # Guards the fixture above from drifting into a dict that the request would reject.
    assert ExistingPosition(quantity=3, average_price=210.4).quantity == 3


class TestWhatIsWrittenDown:
    """What makes a decision answerable afterwards. See app/application/journal.py."""

    async def test_it_records_the_facts_the_analysis_actually_started_from(self):
        journal = RecordingJournal()
        pipeline, _ = a_pipeline(journal=journal)

        signal = await pipeline.run(a_request())

        run = journal.only
        assert run.correlation_id == "c-1"
        assert run.team_id == "default"
        assert run.team_version == VERSION
        assert run.symbol == "AAPL"
        assert run.instrument_type == "equity"
        # The same sheet the first step was given, not a fresh one fetched afterwards.
        # A replay is only the same question if this is the number the agents saw.
        assert run.facts.price == signal.reference_price

    async def test_it_records_every_step_in_the_order_the_team_ran_them(self):
        journal = RecordingJournal()
        pipeline, _ = a_pipeline(journal=journal)

        await pipeline.run(a_request())

        steps = journal.only.steps
        assert [step.role for step in steps] == list(DEFAULT_TEAM.roles)
        assert [step.ordinal for step in steps] == [0, 1, 2]
        # Which schema each answer was validated against, so an older team_version whose
        # steps produced something else stays readable.
        assert [step.schema_name for step in steps] == [
            "MarketRead",
            "RiskAssessment",
            "TradeView",
        ]
        assert steps[0].output == A_READ
        assert steps[-1].output == A_VIEW

    async def test_a_journal_that_fails_does_not_cost_the_engine_its_answer(self):
        """Deliberate, and the argument is in AnalysisJournal's docstring: the engine
        cannot tell a storage failure here from the agents failing, so raising would read
        as "no decision this cycle" and stop trading over bookkeeping."""
        pipeline, _ = a_pipeline(journal=RecordingJournal(failure=RuntimeError("no database")))

        signal = await pipeline.run(a_request())

        assert signal.stance is Stance.BUY

    async def test_a_run_that_never_reached_an_answer_is_not_journalled(self):
        """The population is "analyses that produced a signal". A half-written run would
        read as a team that answered with nothing, which is a different thing entirely -
        and the engine records the failure on its side under the same id."""
        journal = RecordingJournal()
        runner = RecordingRunner(failure=RuntimeError("the model went away"))

        with pytest.raises(RuntimeError):
            pipeline, _ = a_pipeline(runner=runner, journal=journal)
            await pipeline.run(a_request())

        assert journal.runs == []


class TestMemory:
    """Who is shown past analyses, and what the two ends of memory agree on."""

    @staticmethod
    def a_remembering_team() -> TeamSpec:
        """The default team with one flag changed, so a difference in a message below is
        memory and not some other thing about the team."""
        steps = tuple(
            StepSpec(
                role=step.role,
                prompt_file=step.prompt_file,
                output_schema=step.output_schema,
                reads=step.reads,
                sees_memory=step.role == "risk_manager",
                sees_position=step.sees_position,
            )
            for step in DEFAULT_TEAM.steps
        )
        return TeamSpec(id="default", instrument_types=frozenset({"equity"}), steps=steps)

    async def test_a_step_that_does_not_ask_is_told_nothing(self):
        # The message has to be byte-for-byte what it was before memory existed, or
        # `default` stops being comparable to itself across this change.
        memory = RecallingMemory()
        pipeline, runner = a_pipeline(memory=memory)

        await pipeline.run(a_request())

        for role in DEFAULT_TEAM.roles:
            assert MEMORY_KEY not in runner.data_given_to(role)
        assert memory.queries == []

    async def test_the_step_that_asks_is_given_past_analyses(self):
        memory = RecallingMemory(recalled="[2026-09-20] BUY - utfall mot index: 5 hd -3,1 %")
        pipeline, runner = a_pipeline(team=self.a_remembering_team(), memory=memory)

        await pipeline.run(a_request())

        assert runner.data_given_to("risk_manager")[MEMORY_KEY] == memory.recalled
        # Inside the delimiters, like everything else a model is handed.
        assert memory.recalled in runner.message_to("risk_manager").split(DATA_OPEN, 1)[1]
        # And nobody else sees it.
        assert MEMORY_KEY not in runner.data_given_to("market_analyst")
        assert MEMORY_KEY not in runner.data_given_to("portfolio_manager")

    async def test_memory_is_asked_about_this_instrument_with_todays_reading(self):
        memory = RecallingMemory()
        pipeline, _ = a_pipeline(team=self.a_remembering_team(), memory=memory)

        await pipeline.run(a_request())

        symbol, query, correlation_id = memory.queries[0]
        assert symbol == "AAPL"
        assert correlation_id == "c-1"
        assert query == describe_reading(A_READ)

    async def test_what_is_written_is_what_the_next_run_will_ask_with(self):
        """The symmetry memory depends on. A vector written from one kind of text and
        queried with another is a number that looks like a similarity and is not one."""
        memory = RecallingMemory()
        pipeline, _ = a_pipeline(team=self.a_remembering_team(), memory=memory)

        await pipeline.run(a_request())

        _, query, _ = memory.queries[0]
        _, written = memory.remembered[0]
        assert written == query

    async def test_a_team_that_never_reads_memory_still_feeds_it(self):
        """So a team switched on later has something to recall from its first cycle, rather
        than from its first measured horizon a week afterwards."""
        memory = RecallingMemory()
        pipeline, _ = a_pipeline(memory=memory)

        await pipeline.run(a_request())

        assert memory.remembered == [(1, describe_reading(A_READ))]

    async def test_a_memory_that_cannot_be_written_does_not_cost_the_answer(self):
        pipeline, _ = a_pipeline(memory=self.Failing())

        signal = await pipeline.run(a_request())

        assert signal.stance is Stance.BUY

    class Failing(RecallingMemory):
        async def remember(self, analysis_run_id, text) -> None:
            raise RuntimeError("the embedding backend went away")

    async def test_a_failed_embedding_is_not_reported_as_a_lost_journal(self, caplog):
        """The journal's error line is how a hole in the journal is found at all: a
        correlation id the engine has in `decisions` with no run on this side. An embedding
        that failed is not such a hole - the row is sitting right there - and saying it is
        sends a reader looking for something that is not missing."""
        pipeline, _ = a_pipeline(memory=self.Failing())

        with caplog.at_level(logging.DEBUG):
            await pipeline.run(a_request())

        assert "was not journalled" not in caplog.text
        assert "journalled but not embedded" in caplog.text

    async def test_a_failed_journal_is_not_embedded_against_a_row_that_is_not_there(self):
        """There is no id to hang a vector off, and the foreign key would refuse it."""
        memory = RecallingMemory()
        journal = RecordingJournal(failure=RuntimeError("no database"))
        pipeline, _ = a_pipeline(journal=journal, memory=memory)

        signal = await pipeline.run(a_request())

        assert signal.stance is Stance.BUY
        assert memory.remembered == []

    async def test_a_failed_journal_still_says_the_working_is_lost(self, caplog):
        pipeline, _ = a_pipeline(journal=RecordingJournal(failure=RuntimeError("no database")))

        with caplog.at_level(logging.DEBUG):
            await pipeline.run(a_request())

        assert "was not journalled" in caplog.text
