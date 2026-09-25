"""What the application layer needs from the world, stated as protocols.

The same direction the engine takes: the interface is declared where it is used and
implemented further out, so nothing in application or domain imports yfinance, AG2 or
asyncpg. The Protocol also means a fake in a test is a fake because it has the right
shape, not because it inherits from anything.
"""

from typing import Protocol, runtime_checkable

from pydantic import BaseModel

from app.application.journal import AnalysisRun
from app.domain.facts import MarketSnapshot


@runtime_checkable
class MarketDataProvider(Protocol):
    """One call per instrument: a quote and the series behind it.

    It raises rather than returning an error value. The old get_stock_quote returned
    {"error": ...}, which to a model looks exactly like a successful tool call with
    unusual contents - so a source being down read as a fact about the share.
    """

    async def snapshot(self, symbol: str) -> MarketSnapshot: ...


@runtime_checkable
class StepRunner(Protocol):
    """Runs one agent turn and returns the result already validated against its schema.

    The seam that keeps AG2 out of the application layer. The pipeline decides *what* to
    ask and in which order; this decides *how*, which is where an openai timeout or an AG2
    failure lives. An implementation is therefore responsible for translating those into
    the AnalysisError vocabulary, so nothing above it ever catches an openai exception.
    """

    async def run_step[T: BaseModel](self, role: str, message: str, schema: type[T]) -> T: ...


@runtime_checkable
class AnalysisJournal(Protocol):
    """Writes down what one analysis was given and what each step answered.

    A port rather than a call to asyncpg, for the usual reason - but also because this is
    the one write in the request path that must not be able to stop an answer. An
    implementation may raise; the pipeline logs it and returns the signal anyway. The
    engine's own `decisions` row is the record of what was traded, and refusing to answer
    because a journal table was unreachable would stop trading over bookkeeping.

    The hole that leaves is findable rather than silent: a correlation id in the engine's
    `decisions` with no `analysis_runs` row on this side is an analysis whose working was
    lost, and the log line says so at the moment it happens.
    """

    async def record(self, run: AnalysisRun) -> None: ...
