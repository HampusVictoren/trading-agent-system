"""The OutcomeStore port, against agent.signal_outcomes."""

import asyncpg

from app.domain.outcomes import MeasuredOutcome

# ON CONFLICT DO NOTHING, so posting the same sweep twice is not an error. This table is a
# copy of the engine's; a duplicate carries no new information, and making the engine track
# what landed would be a second bookkeeping problem invented to solve the first.
_INSERT = """
    INSERT INTO agent.signal_outcomes (
        correlation_id, horizon_unit, horizon_days, status, reason, benchmark_symbol,
        measured_on, measured_price, instrument_return, benchmark_return, excess_return,
        cost_fraction, net_edge, hit
    )
    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
    ON CONFLICT (correlation_id, horizon_unit, horizon_days) DO NOTHING
"""


class PostgresOutcomeStore:
    """One instance per process, built by the lifespan on the shared pool."""

    def __init__(self, pool: asyncpg.Pool) -> None:
        self._pool = pool

    async def store(self, outcomes: list[MeasuredOutcome]) -> None:
        """Writes a whole sweep, or none of it.

        One transaction, because a batch that half-lands leaves the caller with nothing to
        do about it: the engine cannot tell which rows it still owes, and the retry that
        would fix it is the thing ON CONFLICT already makes safe.

        The figures travel as Python floats into numeric columns, which is safe here for a
        reason worth stating rather than assuming: the engine rounds every return to six
        decimals before it sends one, and these columns hold six - eight for a price. A
        float carries far more significant digits than that, so nothing the contract can
        express is lost on the way in. An earlier version of this file converted each
        number to text first; a mutation test showed that nothing depended on it, which is
        how a defence against an impossible rounding error got removed.
        """
        rows = [
            (
                outcome.correlation_id,
                outcome.horizon_unit.value,
                outcome.horizon_days,
                outcome.status.value,
                outcome.reason,
                outcome.benchmark_symbol,
                outcome.measured_on,
                outcome.measured_price,
                outcome.instrument_return,
                outcome.benchmark_return,
                outcome.excess_return,
                outcome.cost_fraction,
                outcome.net_edge,
                outcome.hit,
            )
            for outcome in outcomes
        ]

        async with self._pool.acquire() as connection, connection.transaction():
            await connection.executemany(_INSERT, rows)
