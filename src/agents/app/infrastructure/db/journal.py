"""The AnalysisJournal port, against agent.analysis_runs and agent.step_outputs."""

import json

import asyncpg

from app.application.journal import AnalysisRun

_INSERT_RUN = """
    INSERT INTO agent.analysis_runs (
        correlation_id, team_id, team_version, instrument_type, symbol, fact_sheet
    )
    VALUES ($1, $2, $3, $4, $5, $6::jsonb)
    RETURNING id
"""

_INSERT_STEP = """
    INSERT INTO agent.step_outputs (run_id, ordinal, role, schema_name, output)
    VALUES ($1, $2, $3, $4, $5::jsonb)
"""


class PostgresJournal:
    """One instance per process, built by the lifespan on the shared pool."""

    def __init__(self, pool: asyncpg.Pool) -> None:
        self._pool = pool

    async def record(self, run: AnalysisRun) -> int:
        """Writes the run and its steps, or neither, and returns the row's id.

        One transaction, because a run without its steps is a row that says an analysis
        happened and cannot say what it concluded - which reads as a team that produced
        nothing rather than as a write that was interrupted.

        A repeated correlation id raises UniqueViolationError rather than adding a second
        row. That is the same rule decisions.correlation_id gives the engine: one analysis
        is one row, whatever retries happen above.
        """
        # mode="json" so dates and datetimes become strings pydantic can read back. The
        # default dumps datetime objects, which json.dumps then refuses.
        facts = json.dumps(run.facts.model_dump(mode="json"))

        async with self._pool.acquire() as connection, connection.transaction():
            run_id = await connection.fetchval(
                _INSERT_RUN,
                run.correlation_id,
                run.team_id,
                run.team_version,
                run.instrument_type,
                run.symbol,
                facts,
            )

            await connection.executemany(
                _INSERT_STEP,
                [
                    (
                        run_id,
                        step.ordinal,
                        step.role,
                        step.schema_name,
                        json.dumps(step.output.model_dump(mode="json")),
                    )
                    for step in run.steps
                ],
            )

        # The id, so the caller can hang derived data off the run - today an embedding,
        # written outside this transaction because it is not evidence and must not be able
        # to take the journal down with it.
        return int(run_id)
