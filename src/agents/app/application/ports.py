"""What the application layer needs from the world, stated as protocols.

The same direction the engine takes: the interface is declared where it is used and
implemented further out, so nothing in application or domain imports yfinance, AG2 or
asyncpg. The Protocol also means a fake in a test is a fake because it has the right
shape, not because it inherits from anything.
"""

from collections.abc import Mapping, Sequence
from typing import Protocol, runtime_checkable

from pydantic import BaseModel

from app.application.journal import AnalysisRun
from app.domain.facts import MarketSnapshot
from app.domain.outcomes import MeasuredOutcome
from app.domain.screening import ScreeningBar


@runtime_checkable
class UniverseData(Protocol):
    """Bars for many instruments, in as few calls as the source allows.

    Separate from `MarketDataProvider` because the shape of the question is different, and
    the difference is the whole reason screening is affordable. That port is one call per
    instrument and includes fundamentals, which yfinance fetches per symbol; this one asks
    only for closes and volumes, which it will fetch for a whole list at once. Ranking fifty
    instruments through the other port would be fifty round trips every cycle.

    A symbol the source had no data for is **left out of the mapping** rather than raising,
    so one delisted name in a universe of fifty does not cost the cycle its screen. A
    failure to reach the source at all is different, and raises `MarketDataUnavailable`:
    ranking against a fraction of the universe you asked for would be a shortlist that looks
    like a judgement and is an outage.
    """

    async def histories(self, symbols: Sequence[str]) -> Mapping[str, tuple[ScreeningBar, ...]]: ...


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

    async def record(self, run: AnalysisRun) -> int: ...


@runtime_checkable
class OutcomeStore(Protocol):
    """Keeps a copy of what the engine measured, so memory can say what happened.

    Unlike the journal, a failure here is reported. The engine is telling this service
    something it already has written down and can send again - so a 500 that makes it
    retry is better than a success that quietly loses a measurement.
    """

    async def store(self, outcomes: list[MeasuredOutcome]) -> None: ...


@runtime_checkable
class Memory(Protocol):
    """Past analyses of the same instrument that the engine has already measured.

    Both halves are best-effort by design, and for the same reason the journal is: a
    trading cycle is waiting for an answer, and neither remembering nor recalling is worth
    an analysis. `recall` therefore returns a sentence rather than raising - the text it
    returns is read by a model, so "memory could not be fetched" has to be sayable in it.
    """

    async def remember(self, analysis_run_id: int, text: str) -> None: ...

    async def recall(
        self, symbol: str, query: str, correlation_id: str, limit: int = ...
    ) -> str: ...

    # What the readiness probe asks. It belongs on the port rather than beside it: whether
    # a store can be reached is something a caller may need to know about the capability,
    # not about which class happens to implement it. Unlike the two above, this one raises
    # - a probe that answered "fine" whatever happened would not be a probe.
    async def ping(self) -> None: ...
