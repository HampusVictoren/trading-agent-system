"""The journal against a real database.

What is checked here is what only a server can answer: that the JSON survives the round
trip unchanged, that a repeated analysis cannot become two rows, and that nothing can go
back and edit what an analysis was given. tests/test_pipeline.py covers when the journal
is called; this covers what happens when it is.
"""

import json
from collections.abc import AsyncIterator
from datetime import UTC, datetime

import asyncpg
import pytest
import pytest_asyncio

from app.application.journal import AnalysisRun, RecordedStep
from app.domain.facts import FactSheet
from app.domain.signals import MAX_SYMBOL_LENGTH, Stance, TradeView
from app.domain.steps import MarketRead, RiskAssessment, Severity, Trend, Valuation
from app.infrastructure.db.journal import PostgresJournal

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

STEPS = (
    RecordedStep(
        ordinal=0,
        role="market_analyst",
        output=MarketRead(trend=Trend.UP, valuation=Valuation.FAIR, observations=["Stabilt."]),
    ),
    RecordedStep(
        ordinal=1,
        role="risk_manager",
        output=RiskAssessment(downside=Severity.MEDIUM, veto=False, risks=["Utsträckt."]),
    ),
    RecordedStep(
        ordinal=2,
        role="portfolio_manager",
        output=TradeView(
            stance=Stance.BUY,
            conviction=0.7,
            thesis="Momentum.",
            key_risks=["Värdering."],
            horizon_days=5,
        ),
    ),
)


def a_run(
    correlation_id: str = "c-1",
    steps: tuple[RecordedStep, ...] = STEPS,
    symbol: str = "AAPL",
) -> AnalysisRun:
    return AnalysisRun(
        correlation_id=correlation_id,
        team_id="default",
        team_version="a" * 64,
        instrument_type="equity",
        symbol=symbol,
        facts=FACTS,
        steps=steps,
    )


@pytest_asyncio.fixture
async def pool(migrated: str) -> AsyncIterator[asyncpg.Pool]:
    async with asyncpg.create_pool(dsn=migrated, min_size=1, max_size=2) as pool:
        yield pool


async def test_the_fact_sheet_comes_back_exactly_as_it_went_in(pool: asyncpg.Pool) -> None:
    """Stored to be replayed, so an equal-looking sheet is not enough: it has to read back
    into the same model. A float that became a string would pass a `==` on the JSON and
    fail a replay months later."""
    await PostgresJournal(pool).record(a_run())

    stored = await pool.fetchval("SELECT fact_sheet FROM agent.analysis_runs")

    assert FactSheet.model_validate(json.loads(stored)) == FACTS


async def test_the_longest_symbol_the_contract_allows_fits_the_column(
    pool: asyncpg.Pool,
) -> None:
    """The same guard the outcome store has, for the other column that holds a symbol.

    `analysis_runs.symbol` was `varchar(10)` while the contract allowed sixteen, and nothing
    in this suite had ever written a symbol longer than four characters to a real column - so
    the mismatch was invisible until `ESSITY-B.ST` entered the universe. Every symbol the
    contract admits has to survive the trip, not only the short ones.
    """
    for symbol in ("A" * MAX_SYMBOL_LENGTH, "ESSITY-B.ST", "XACT-OMXS30.ST"):
        await PostgresJournal(pool).record(a_run(correlation_id=f"c-{symbol}", symbol=symbol))

        stored = await pool.fetchval(
            "SELECT symbol FROM agent.analysis_runs WHERE correlation_id = $1", f"c-{symbol}"
        )

        assert stored == symbol


async def test_every_step_is_stored_with_its_place_and_its_schema(pool: asyncpg.Pool) -> None:
    await PostgresJournal(pool).record(a_run())

    rows = await pool.fetch("""
        SELECT s.ordinal, s.role, s.schema_name, s.output
        FROM agent.step_outputs s
        JOIN agent.analysis_runs r ON r.id = s.run_id
        WHERE r.correlation_id = 'c-1'
        ORDER BY s.ordinal
    """)

    assert [r["role"] for r in rows] == ["market_analyst", "risk_manager", "portfolio_manager"]
    assert [r["schema_name"] for r in rows] == ["MarketRead", "RiskAssessment", "TradeView"]
    assert json.loads(rows[0]["output"])["trend"] == "UP"


async def test_one_analysis_cannot_become_two_rows(pool: asyncpg.Pool) -> None:
    """The same rule decisions.correlation_id gives the engine. A retry above this layer
    must not turn one analysis into two, or every count per team_version is wrong."""
    journal = PostgresJournal(pool)
    await journal.record(a_run())

    with pytest.raises(asyncpg.UniqueViolationError):
        await journal.record(a_run())

    assert await pool.fetchval("SELECT count(*) FROM agent.analysis_runs") == 1


async def test_a_run_and_its_steps_move_together(pool: asyncpg.Pool) -> None:
    """A run row with no steps says an analysis happened and cannot say what it concluded,
    which reads as a team that produced nothing. Two steps with the same role break the
    unique constraint, and the run must not survive it."""
    repeated = (STEPS[0], RecordedStep(ordinal=1, role="market_analyst", output=STEPS[1].output))

    with pytest.raises(asyncpg.UniqueViolationError):
        await PostgresJournal(pool).record(a_run(steps=repeated))

    assert await pool.fetchval("SELECT count(*) FROM agent.analysis_runs") == 0


@pytest.mark.parametrize(
    "statement",
    [
        "UPDATE agent.analysis_runs SET symbol = 'TSLA'",
        "DELETE FROM agent.analysis_runs",
        "UPDATE agent.step_outputs SET output = '{}'::jsonb",
        "DELETE FROM agent.step_outputs",
        # Matches nothing, and is still refused. A trigger that only fires when it finds a
        # row is a rule that passes its own test by accident.
        "DELETE FROM agent.analysis_runs WHERE correlation_id = 'no-such-analysis'",
    ],
)
async def test_nothing_can_go_back_and_change_what_the_agents_were_given(
    pool: asyncpg.Pool, statement: str
) -> None:
    await PostgresJournal(pool).record(a_run())

    with pytest.raises(asyncpg.RaiseError, match="append-only"):
        await pool.execute(statement)
