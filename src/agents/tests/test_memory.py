"""Memory, against a real database.

The rule the whole design turns on is the one in the first test: a past analysis reaches an
agent **only once the engine has measured it**. Everything else here is about what a model
is handed when it does.
"""

from collections.abc import AsyncIterator
from datetime import UTC, datetime

import asyncpg
import pytest_asyncio

from app.application.journal import AnalysisRun, RecordedStep
from app.domain.facts import FactSheet
from app.domain.outcomes import HorizonUnit, MeasuredOutcome, OutcomeStatus
from app.domain.signals import Stance, TradeView
from app.domain.steps import MarketRead, RiskAssessment, Severity, Trend, Valuation
from app.infrastructure.db.journal import PostgresJournal
from app.infrastructure.db.memory import (
    EMBEDDING_DIMENSIONS,
    NOTHING_MEASURED,
    UNAVAILABLE,
    AnalysisMemory,
)
from app.infrastructure.db.outcomes import PostgresOutcomeStore

FACTS = FactSheet(
    symbol="AAPL",
    currency="USD",
    price=337.445,
    as_of=datetime(2026, 9, 24, 14, 0, tzinfo=UTC),
    pe_ratio=35.35,
    return_1m=0.0412,
    return_3m=None,
    return_12m=0.1903,
    volatility_30d=0.2216,
    pct_below_52w_high=0.0731,
    sector="Technology",
)

A_READ = MarketRead(trend=Trend.UP, valuation=Valuation.FAIR, observations=["Stabil trend."])
AN_ASSESSMENT = RiskAssessment(downside=Severity.MEDIUM, veto=False, risks=["Utsträckt."])


class FakeEmbeddings:
    """Returns a vector that depends only on the text, so "similar" is decidable in a test.

    A real embedding model would make the ordering assertions below a statement about
    nomic-embed-text rather than about the query.
    """

    def __init__(self) -> None:
        self.embeddings = self
        self.asked: list[str] = []
        self.failure: Exception | None = None

    async def create(self, *, input: str, model: str):  # noqa: A002 - the openai signature
        self.asked.append(input)
        if self.failure is not None:
            raise self.failure

        # One axis per known phrase. Texts sharing a phrase point the same way.
        axes = ["uppgång", "nedgång", "sidledes"]
        vector = [0.0] * EMBEDDING_DIMENSIONS
        for index, axis in enumerate(axes):
            vector[index] = 1.0 if axis in input else 0.0
        if not any(vector):
            vector[-1] = 1.0

        return type("Response", (), {"data": [type("Item", (), {"embedding": vector})()]})()


@pytest_asyncio.fixture
async def pool(migrated: str) -> AsyncIterator[asyncpg.Pool]:
    from pgvector.asyncpg import register_vector

    async with asyncpg.create_pool(
        dsn=migrated, min_size=1, max_size=2, init=register_vector
    ) as pool:
        yield pool


def a_run(correlation_id: str, stance: Stance, thesis: str) -> AnalysisRun:
    return AnalysisRun(
        correlation_id=correlation_id,
        team_id="default",
        team_version="a" * 64,
        instrument_type="equity",
        symbol="AAPL",
        facts=FACTS,
        steps=(
            RecordedStep(ordinal=0, role="market_analyst", output=A_READ),
            RecordedStep(ordinal=1, role="risk_manager", output=AN_ASSESSMENT),
            RecordedStep(
                ordinal=2,
                role="portfolio_manager",
                output=TradeView(
                    stance=stance,
                    conviction=0.7,
                    thesis=thesis,
                    key_risks=["Värdering."],
                    horizon_days=5,
                ),
            ),
        ),
    )


def an_outcome(correlation_id: str, *, excess: float, hit: bool) -> MeasuredOutcome:
    return MeasuredOutcome(
        correlation_id=correlation_id,
        horizon_unit=HorizonUnit.TRADING_DAYS,
        horizon_days=5,
        status=OutcomeStatus.MEASURED,
        benchmark_symbol="SPY",
        measured_on=None,
        instrument_return=excess,
        benchmark_return=0.0,
        excess_return=excess,
        cost_fraction=0.0006,
        net_edge=excess - 0.0006,
        hit=hit,
    )


async def _remembered(
    pool: asyncpg.Pool, memory: AnalysisMemory, run: AnalysisRun, text: str
) -> None:
    run_id = await pool.fetchval(
        "SELECT id FROM agent.analysis_runs WHERE correlation_id = $1", run.correlation_id
    )
    await memory.remember(run_id, text)


async def test_an_analysis_nobody_has_measured_is_not_recalled(pool: asyncpg.Pool) -> None:
    """The decision the whole design turns on. Reasoning alone teaches a model to agree with
    itself - a thesis it repeated three times reads as a well-founded one - so an analysis
    reaches an agent only once the engine has measured it and said so."""
    memory = AnalysisMemory(pool, FakeEmbeddings())
    run = a_run("c-1", Stance.BUY, "Momentum i uppgång.")
    await PostgresJournal(pool).record(run)
    await _remembered(pool, memory, run, "uppgång")

    recalled = await memory.recall("AAPL", "uppgång", correlation_id="c-now")

    assert recalled == NOTHING_MEASURED.format(symbol="AAPL")


async def test_a_measured_analysis_comes_back_with_how_it_went(pool: asyncpg.Pool) -> None:
    memory = AnalysisMemory(pool, FakeEmbeddings())
    run = a_run("c-1", Stance.BUY, "Momentum i uppgång och stabil marginal.")
    await PostgresJournal(pool).record(run)
    await _remembered(pool, memory, run, "uppgång")
    await PostgresOutcomeStore(pool).store([an_outcome("c-1", excess=-0.031, hit=False)])

    recalled = await memory.recall("AAPL", "uppgång", correlation_id="c-now")

    assert "BUY" in recalled
    assert "5 handelsdagar" in recalled
    # The decimal comma the rest of the agent-facing text uses, and a sign that says which
    # way it went without the model having to work it out.
    assert "-3,1 %" in recalled
    assert "miss" in recalled
    assert "Momentum i uppgång" in recalled


async def test_the_run_being_analysed_cannot_recall_itself(pool: asyncpg.Pool) -> None:
    """It has no measured outcome today, so it cannot match - but it will once a horizon
    passes, and a run that recalls itself is the most confident-looking memory there is."""
    memory = AnalysisMemory(pool, FakeEmbeddings())
    run = a_run("c-1", Stance.BUY, "Momentum i uppgång.")
    await PostgresJournal(pool).record(run)
    await _remembered(pool, memory, run, "uppgång")
    await PostgresOutcomeStore(pool).store([an_outcome("c-1", excess=0.02, hit=True)])

    recalled = await memory.recall("AAPL", "uppgång", correlation_id="c-1")

    assert recalled == NOTHING_MEASURED.format(symbol="AAPL")


async def test_the_closest_reading_comes_first(pool: asyncpg.Pool) -> None:
    memory = AnalysisMemory(pool, FakeEmbeddings())
    journal = PostgresJournal(pool)
    store = PostgresOutcomeStore(pool)

    for correlation_id, text, thesis in [
        ("c-up", "uppgång", "Tes om uppgång."),
        ("c-down", "nedgång", "Tes om nedgång."),
    ]:
        run = a_run(correlation_id, Stance.BUY, thesis)
        await journal.record(run)
        await _remembered(pool, memory, run, text)
        await store.store([an_outcome(correlation_id, excess=0.01, hit=True)])

    recalled = await memory.recall("AAPL", "nedgång", correlation_id="c-now")

    assert recalled.index("Tes om nedgång.") < recalled.index("Tes om uppgång.")


async def test_an_embedding_can_be_rebuilt_without_a_migration(pool: asyncpg.Pool) -> None:
    """The reason this is a table of its own rather than a column on the append-only
    journal: changing the embedding model means recomputing every vector."""
    memory = AnalysisMemory(pool, FakeEmbeddings())
    run = a_run("c-1", Stance.BUY, "Tes.")
    await PostgresJournal(pool).record(run)

    await _remembered(pool, memory, run, "uppgång")
    await _remembered(pool, memory, run, "nedgång")

    assert await pool.fetchval("SELECT count(*) FROM agent.analysis_embeddings") == 1


async def test_a_memory_that_cannot_be_read_does_not_end_the_analysis(
    pool: asyncpg.Pool,
) -> None:
    """The agents' own failures are handled at the route. A memory that cannot be fetched
    must not end an analysis that would otherwise have succeeded - and the sentence says so,
    so a model is not left to infer that nothing has ever happened."""
    embeddings = FakeEmbeddings()
    embeddings.failure = RuntimeError("the embedding backend went away")

    recalled = await AnalysisMemory(pool, embeddings).recall(
        "AAPL", "uppgång", correlation_id="c-now"
    )

    assert recalled == UNAVAILABLE


async def test_the_model_that_produced_a_vector_is_stored_beside_it(pool: asyncpg.Pool) -> None:
    """A mixture of two models in one index is a similarity score that means nothing, and it
    has to be possible to see which rows are which."""
    memory = AnalysisMemory(pool, FakeEmbeddings())
    run = a_run("c-1", Stance.BUY, "Tes.")
    await PostgresJournal(pool).record(run)
    await _remembered(pool, memory, run, "uppgång")

    assert await pool.fetchval("SELECT model FROM agent.analysis_embeddings") == "nomic-embed-text"
