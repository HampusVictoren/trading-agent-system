"""The copy of the engine's measurements, against a real database.

A copy that disagrees with the original in the last digits is worse than no copy, so most
of what is checked here is that the numbers survive the trip. The rest is the two rules
that make posting safe to retry.
"""

from collections.abc import AsyncIterator
from datetime import date
from decimal import Decimal

import asyncpg
import pytest
import pytest_asyncio

from app.domain.outcomes import HorizonUnit, MeasuredOutcome, OutcomeStatus
from app.infrastructure.db.outcomes import PostgresOutcomeStore

MEASURED = MeasuredOutcome(
    correlation_id="c-1",
    horizon_unit=HorizonUnit.TRADING_DAYS,
    horizon_days=5,
    status=OutcomeStatus.MEASURED,
    reason=None,
    benchmark_symbol="SPY",
    measured_on=date(2026, 10, 1),
    measured_price=344.12,
    instrument_return=0.019795,
    benchmark_return=0.004311,
    excess_return=0.015484,
    cost_fraction=0.0006,
    net_edge=0.014884,
    hit=True,
)

NOT_MEASURABLE = MeasuredOutcome(
    correlation_id="c-2",
    horizon_unit=HorizonUnit.CALENDAR_DAYS,
    horizon_days=365,
    status=OutcomeStatus.NOT_MEASURABLE,
    reason="No bar on or before the horizon date for the benchmark.",
    benchmark_symbol="SPY",
)


@pytest_asyncio.fixture
async def pool(migrated: str) -> AsyncIterator[asyncpg.Pool]:
    async with asyncpg.create_pool(dsn=migrated, min_size=1, max_size=2) as pool:
        yield pool


async def test_every_figure_comes_back_as_the_engine_sent_it(pool: asyncpg.Pool) -> None:
    """A copy that disagrees with the original in the last digits is worse than no copy.

    The figures travel as Python floats into numeric columns, and this is what says that is
    safe: the engine rounds every return to six decimals before it sends one, and these
    columns hold six - eight for a price. A float carries far more significant digits than
    that, so nothing the contract can express is lost on the way in.

    An earlier version converted each number to text first to avoid a float8 rounding step.
    A mutation test showed nothing depended on it, so the conversion was removed; this test
    is what would notice if the premise behind removing it ever stopped holding.
    """
    await PostgresOutcomeStore(pool).store([MEASURED])

    row = await pool.fetchrow("SELECT * FROM agent.signal_outcomes")

    assert row["correlation_id"] == "c-1"
    assert row["horizon_unit"] == "TradingDays"
    assert row["status"] == "Measured"
    assert row["measured_on"] == date(2026, 10, 1)
    assert row["measured_price"] == Decimal("344.12000000")
    assert row["instrument_return"] == Decimal("0.019795")
    assert row["excess_return"] == Decimal("0.015484")
    assert row["net_edge"] == Decimal("0.014884")
    assert row["hit"] is True


async def test_a_row_that_could_not_be_measured_keeps_its_reason_and_no_numbers(
    pool: asyncpg.Pool,
) -> None:
    """Null rather than zero: a return of zero is a real answer, and a report must not
    average it in with the ones that never happened."""
    await PostgresOutcomeStore(pool).store([NOT_MEASURABLE])

    row = await pool.fetchrow("SELECT * FROM agent.signal_outcomes")

    assert row["status"] == "NotMeasurable"
    assert row["reason"].startswith("No bar")
    assert row["instrument_return"] is None
    assert row["net_edge"] is None
    assert row["hit"] is None


async def test_posting_the_same_sweep_again_changes_nothing(pool: asyncpg.Pool) -> None:
    """What makes a retry safe. This table is a copy, so a duplicate carries no new
    information - and making the engine track what landed would be a second bookkeeping
    problem invented to solve the first."""
    store = PostgresOutcomeStore(pool)
    await store.store([MEASURED, NOT_MEASURABLE])

    await store.store([MEASURED, NOT_MEASURABLE])

    assert await pool.fetchval("SELECT count(*) FROM agent.signal_outcomes") == 2


async def test_an_outcome_for_an_analysis_this_service_never_saw_is_still_stored(
    pool: asyncpg.Pool,
) -> None:
    """Deliberately no foreign key to analysis_runs. The engine measures every decision it
    made, including cycles this service failed and decisions from before the journal
    existed; a constraint would turn "no record of that analysis" into a rejected batch."""
    await PostgresOutcomeStore(pool).store([MEASURED])

    assert await pool.fetchval("SELECT count(*) FROM agent.analysis_runs") == 0
    assert await pool.fetchval("SELECT count(*) FROM agent.signal_outcomes") == 1


async def test_a_batch_lands_whole_or_not_at_all(pool: asyncpg.Pool) -> None:
    """A half-landed batch leaves the caller with nothing to do about it: the engine
    cannot tell which rows it still owes. model_construct skips validation, which is how a
    value the contract would have refused gets as far as the database."""
    too_long = MeasuredOutcome.model_construct(
        **(MEASURED.model_dump() | {"correlation_id": "x" * 65})
    )

    with pytest.raises(asyncpg.StringDataRightTruncationError):
        await PostgresOutcomeStore(pool).store([MEASURED, too_long])

    assert await pool.fetchval("SELECT count(*) FROM agent.signal_outcomes") == 0


@pytest.mark.parametrize(
    "statement",
    [
        "UPDATE agent.signal_outcomes SET hit = false",
        "DELETE FROM agent.signal_outcomes",
        "DELETE FROM agent.signal_outcomes WHERE correlation_id = 'no-such-analysis'",
    ],
)
async def test_a_measurement_cannot_be_edited_after_the_fact(
    pool: asyncpg.Pool, statement: str
) -> None:
    """The engine's row is the authority, so a correction arrives as a new measurement
    there and a new row here - never as an edit to a number somebody has already read."""
    await PostgresOutcomeStore(pool).store([MEASURED])

    with pytest.raises(asyncpg.RaiseError, match="append-only"):
        await pool.execute(statement)
