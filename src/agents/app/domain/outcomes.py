"""What the engine measured, as it arrives on the wire.

contracts/outcome.schema.json is the agreement; this is one side of it. The engine owns
the measurement - its OutcomeCalculator is the definition of a hit, and
trading.signal_outcomes is the record. What arrives here is a copy, sent because the
database is never the integration point between the two services.

The point of the copy is memory. A past analysis that can only say what was argued teaches
a model to agree with itself; one that can say what happened afterwards is the only kind
that can make the next decision better.
"""

from datetime import date
from enum import StrEnum
from typing import Annotated

from pydantic import BaseModel, ConfigDict, Field

from app.domain.signals import MAX_IDENTIFIER_LENGTH, SYMBOL_PATTERN

# A request is a unit of work with a timeout, not a bulk load. A sweep with more than this
# to report sends more than one request; the engine's first live sweep found 68 horizons.
MAX_OUTCOMES_PER_REQUEST = 500

MAX_REASON_LENGTH = 200


class HorizonUnit(StrEnum):
    """Spelled as the engine stores it, not in this contract's usual upper case.

    The two services each keep a copy of the same row. Spelling them differently would mean
    a mapping in every head that ever compares the two tables, in exchange for a naming
    convention nobody reads these values through.
    """

    TRADING_DAYS = "TradingDays"
    CALENDAR_DAYS = "CalendarDays"


class OutcomeStatus(StrEnum):
    MEASURED = "Measured"

    # A hole that waiting will not fill. It is reported rather than left out, because a
    # signal that is quietly never measured is one missing from the population a report
    # speaks about.
    NOT_MEASURABLE = "NotMeasurable"


class MeasuredOutcome(BaseModel):
    """One signal at one horizon."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    # The engine keys its own row on the decision's id. On the wire it is the correlation
    # id, because that is the one identifier both services wrote down.
    correlation_id: Annotated[str, Field(min_length=1, max_length=MAX_IDENTIFIER_LENGTH)]

    horizon_unit: HorizonUnit
    horizon_days: Annotated[int, Field(ge=1)]

    status: OutcomeStatus
    reason: Annotated[str, Field(max_length=MAX_REASON_LENGTH)] | None = None

    benchmark_symbol: Annotated[str, Field(pattern=SYMBOL_PATTERN)]

    # Null rather than zero throughout, for a row that was not measured: a return of zero
    # is a real answer, and a report must not average it in with the ones that never were.
    measured_on: date | None = None
    measured_price: Annotated[float, Field(gt=0)] | None = None
    instrument_return: float | None = None
    benchmark_return: float | None = None
    excess_return: float | None = None
    cost_fraction: Annotated[float, Field(ge=0)] | None = None
    net_edge: float | None = None
    hit: bool | None = None


class OutcomeReport(BaseModel):
    """A sweep's findings, as one request."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    outcomes: Annotated[
        list[MeasuredOutcome], Field(min_length=1, max_length=MAX_OUTCOMES_PER_REQUEST)
    ]
