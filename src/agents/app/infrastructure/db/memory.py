"""Semantic memory over this service's own journal.

What an agent is given is not "things we have said" but **things we have said that turned
out**. A past analysis that can only recall its own argument teaches a model to agree with
itself: a thesis it repeated three times reads as a well-founded one. So a run reaches
memory only once the engine has measured it and reported the measurement back - which is
why `agent.signal_outcomes` exists and why the join below is an inner one.

The consequence is that memory is **empty until something has been measured**, and says so.
That is the honest state for the first day of any new deployment, and hiding it behind
unmeasured rows would be exactly the confirmation loop this design was chosen to avoid.

*What is embedded is the analyst's reading, not the thesis.* The query available when the
risk manager runs is today's `MarketRead`, and matching a market read against a market read
asks "when things looked like this before, what did we conclude and how did it go?".
Embedding the thesis instead would mean comparing two different kinds of text and calling
the number a similarity.

The clients and the pool are built once by the FastAPI lifespan and handed in, rather than
created at import time - creating them at import bound them to whichever event loop
imported the module first.
"""

import json
import logging
from decimal import Decimal
from typing import Any

import asyncpg
from openai import AsyncOpenAI

logger = logging.getLogger(__name__)

# nomic-embed-text produces 768 dimensions, which is what the embedding column is declared
# as. Changing the model means changing the column, so this is deliberately not a setting -
# and it is stored on every row, because a mixture of two models in one index is a
# similarity score that means nothing.
EMBEDDING_MODEL = "nomic-embed-text"
EMBEDDING_DIMENSIONS = 768

# How many past analyses an agent is shown. Three is enough to see a pattern and few enough
# that the block stays small; every one of them is paid for in the prompt of every cycle.
DEFAULT_RECALL = 3

# The thesis is capped at 2000 characters on the wire. Reading three of those in full would
# cost more prompt than the fact sheet does, so what memory shows is the opening of one -
# the same reasoning that caps a step's notes at 200.
MAX_THESIS_SHOWN = 200

_REMEMBER = """
    INSERT INTO agent.analysis_embeddings (analysis_run_id, model, embedding)
    VALUES ($1, $2, $3)
    ON CONFLICT (analysis_run_id) DO UPDATE
        SET model = EXCLUDED.model, embedding = EXCLUDED.embedding, created_at = now()
"""

# The LATERAL is what makes "only measured" a join rather than a filter applied afterwards:
# a run with nothing measured produces a NULL aggregate and is dropped by the ON clause, so
# it never takes one of the three places.
_RECALL = """
    SELECT r.created_at,
           s.output ->> 'stance' AS stance,
           s.output ->> 'thesis' AS thesis,
           1 - (e.embedding <=> $1) AS similarity,
           m.horizons
    FROM agent.analysis_embeddings e
    JOIN agent.analysis_runs r ON r.id = e.analysis_run_id
    JOIN agent.step_outputs s ON s.run_id = r.id AND s.schema_name = 'TradeView'
    JOIN LATERAL (
        SELECT jsonb_agg(
                   jsonb_build_object(
                       'unit', o.horizon_unit,
                       'days', o.horizon_days,
                       'excess', o.excess_return,
                       'hit', o.hit)
                   ORDER BY o.horizon_unit, o.horizon_days) AS horizons
        FROM agent.signal_outcomes o
        WHERE o.correlation_id = r.correlation_id AND o.status = 'Measured'
    ) m ON m.horizons IS NOT NULL
    WHERE r.symbol = $2 AND r.correlation_id <> $3
    ORDER BY e.embedding <=> $1
    LIMIT $4
"""

# Agent-facing text, so Swedish, like the prompt files. A model reads every line below.
NOTHING_MEASURED = "Inga tidigare analyser av {symbol} har hunnit mätas färdigt."
UNAVAILABLE = "Kunde inte hämta historiskt minne."
UNIT_LABEL = {"TradingDays": "handelsdagar", "CalendarDays": "kalenderdagar"}
HIT_LABEL = {True: "träff", False: "miss"}


def _percent(value: Decimal | float | None) -> str:
    """A fraction as a signed percentage, with the decimal comma the rest of the text uses."""
    if value is None:
        return "okänt"
    return f"{float(value) * 100:+.1f} %".replace(".", ",")


def _horizon(entry: dict[str, Any]) -> str:
    unit = UNIT_LABEL.get(entry["unit"], entry["unit"])
    verdict = HIT_LABEL.get(entry["hit"], "okänt")
    return f"{entry['days']} {unit} {_percent(entry['excess'])} ({verdict})"


class AnalysisMemory:
    """Reads and writes agent.analysis_embeddings. One instance per process."""

    def __init__(self, pool: asyncpg.Pool, embeddings: AsyncOpenAI) -> None:
        self._pool = pool
        self._embeddings = embeddings

    async def ping(self) -> None:
        """Raises if the database cannot be reached. Used by the readiness probe."""
        async with self._pool.acquire() as conn:
            await conn.fetchval("SELECT 1")

    async def embed(self, text: str) -> list[float]:
        response = await self._embeddings.embeddings.create(input=text, model=EMBEDDING_MODEL)
        return response.data[0].embedding

    async def remember(self, analysis_run_id: int, text: str) -> None:
        """Embeds one analysis's market read and stores it against the run.

        Upserts rather than inserts, because an embedding is derived: recomputing one after
        a model change has to be an ordinary write rather than a migration.
        """
        embedding = await self.embed(text)

        async with self._pool.acquire() as conn:
            await conn.execute(_REMEMBER, analysis_run_id, EMBEDDING_MODEL, embedding)

    async def recall(
        self, symbol: str, query: str, correlation_id: str, limit: int = DEFAULT_RECALL
    ) -> str:
        """The closest past analyses *that have been measured*, written for a model to read.

        A failure becomes a sentence rather than an exception: the agents' own errors are
        handled at the route, and a memory that cannot be fetched must not end an analysis
        that would otherwise have succeeded. The sentence says so plainly, so a model is not
        left to infer that nothing has ever happened.

        The current run is excluded by its correlation id. It has no measured outcome yet,
        so it could not match today - but it will once a horizon passes, and a run that
        recalls itself is the most confident-looking memory there is.
        """
        try:
            embedding = await self.embed(query)

            async with self._pool.acquire() as conn:
                rows = await conn.fetch(_RECALL, embedding, symbol, correlation_id, limit)
        except Exception:
            logger.warning("Could not read memory for %s", symbol, exc_info=True)
            return UNAVAILABLE

        if not rows:
            return NOTHING_MEASURED.format(symbol=symbol)

        return "\n".join(self._describe(row) for row in rows)

    @staticmethod
    def _describe(row: asyncpg.Record) -> str:
        horizons = ", ".join(_horizon(entry) for entry in json.loads(row["horizons"]))
        thesis = row["thesis"][:MAX_THESIS_SHOWN]

        return (
            f"[{row['created_at'].strftime('%Y-%m-%d')}] {row['stance']} "
            f"(likhet {row['similarity']:.2f}) - utfall mot index: {horizons}\n"
            f"  Tes: {thesis}"
        )
