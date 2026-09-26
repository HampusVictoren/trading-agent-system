"""What the agents are told, computed rather than asked for.

Decision 5: code calculates and selects, the LLM interprets. A model has no advantage on
arithmetic, and every number it produces is a number that could have been invented. So the
ratios here are pure functions over a price series, and the agents receive the result.

The fact sheet carries numbers and categories, never free text. That was the shape decided
for stage 3: the 300 characters of longBusinessSummary that used to go straight into a
prompt are gone, and nothing replaces them. When a news step arrives it gets a schema of
its own, with its own caps, visible in that step's `reads`.
"""

import math
import statistics
from collections.abc import Sequence
from datetime import date, timedelta
from typing import Annotated, Protocol

from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, model_validator

# Annualising a daily deviation. The conventional count of trading days in a year; it is a
# convention rather than a measurement, which is why it is named rather than inlined.
TRADING_DAYS_PER_YEAR = 252

# Four decimals is a basis point. Past that it is tokens in every prompt that reads the
# sheet, spent on precision the input never had.
DERIVED_PRECISION = 4

# A sector is a category rather than prose, but it still arrives from outside. So does a
# currency code, which is why both are capped: the same principle as the step schemas.
MAX_SECTOR_LENGTH = 40
MAX_CURRENCY_LENGTH = 8

type Sector = Annotated[str, Field(max_length=MAX_SECTOR_LENGTH)]
type Currency = Annotated[str, Field(min_length=1, max_length=MAX_CURRENCY_LENGTH)]


class ClosingBar(Protocol):
    """A day and a close, structurally - whatever else the bar happens to carry.

    The three factor functions below take this rather than `PriceBar`, so screening can
    pass its own richer bar and reuse the arithmetic instead of copying it. A protocol
    rather than a base class because `PriceBar` is a wire shape: it is what
    `contracts/quote-history.schema.json` describes, field for field, and a shared parent
    would tie that contract to whatever a subclass added next.
    """

    # Read-only properties rather than plain annotations. A protocol attribute is mutable
    # and invariant, which a frozen pydantic field does not satisfy; a property says what is
    # actually wanted here, which is only ever to read the two values.
    @property
    def on(self) -> date: ...

    @property
    def close(self) -> float: ...


class PriceBar(BaseModel):
    """One day's close, and the shape the history contract carries.

    Only the close, because that is all an outcome is measured on - and because this is
    what `/v1/quotes/{symbol}/history` serialises. The contract declares the bar with
    `unevaluatedProperties: false`, and the engine's `BarDto` refuses an unmapped member,
    so a field added here would make every history response unreadable to the engine. When
    something in this service needs more per day, it gets its own type; see
    `app.domain.screening.ScreeningBar`.
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    on: date
    close: Annotated[float, Field(gt=0)]


class Quote(BaseModel):
    """The whitelisted fields of a market-data response.

    A provider's raw payload never leaves its adapter. This is the list of what the rest of
    the service is allowed to see, which is also the list of what an upstream change can
    affect.
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    symbol: str
    currency: Currency
    price: Annotated[float, Field(gt=0)]
    pe_ratio: float | None
    sector: Sector | None
    as_of: AwareDatetime


class MarketSnapshot(BaseModel):
    """A quote and the series behind it, as one provider call."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    quote: Quote
    history: tuple[PriceBar, ...]

    @model_validator(mode="after")
    def history_is_oldest_first(self) -> "MarketSnapshot":
        # Every function below takes the last bar as the latest one. A provider that
        # returned newest first would make all of them quietly wrong rather than fail.
        dates = [bar.on for bar in self.history]
        if dates != sorted(dates):
            raise ValueError("history must be oldest first")
        return self


class FactSheet(BaseModel):
    """What every agent step starts from. Deliberately small, and entirely computed."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    symbol: str
    currency: Currency
    price: Annotated[float, Field(gt=0)]
    as_of: AwareDatetime

    # None rather than zero throughout: a share with no earnings has no P/E, and a share
    # listed last month has no twelve-month return. Zero would be a claim.
    pe_ratio: float | None
    return_1m: float | None
    return_3m: float | None
    return_12m: float | None
    volatility_30d: float | None
    pct_below_52w_high: float | None
    sector: Sector | None


def _round(value: float) -> float:
    return round(value, DERIVED_PRECISION)


def trailing_return(history: Sequence[ClosingBar], *, days: int) -> float | None:
    """The fraction gained since the last close at or before `days` ago.

    Counted from a calendar date rather than a fixed number of rows, so a holiday week does
    not turn a one-month return into a five-week one. A market that was shut on the cutoff
    date itself is the normal case, so the last close before it is what gets compared.
    """
    if not history:
        return None

    latest = history[-1]
    cutoff = latest.on - timedelta(days=days)
    earlier = [bar for bar in history if bar.on <= cutoff]
    if not earlier:
        # The series does not reach back that far. Saying nothing beats extrapolating.
        return None

    return _round((latest.close - earlier[-1].close) / earlier[-1].close)


def annualised_volatility(history: Sequence[ClosingBar], *, window: int) -> float | None:
    """The standard deviation of daily log returns over the last `window` bars, annualised.

    Measured in bars rather than calendar days, unlike the returns above: volatility is a
    property of the observations, and a week with no trading contributes none.
    """
    # n returns need n + 1 closes. Using `window` closes would quietly compute window - 1.
    if len(history) < window + 1:
        return None

    recent = history[-(window + 1) :]
    log_returns = [
        math.log(later.close / earlier.close)
        for earlier, later in zip(recent[:-1], recent[1:], strict=True)
    ]

    return _round(statistics.stdev(log_returns) * math.sqrt(TRADING_DAYS_PER_YEAR))


def pct_below_high(price: float, history: Sequence[ClosingBar], *, weeks: int) -> float | None:
    """How far under its highest close of the period the price is, as a fraction.

    The live price is included in the high, so a share at a new high reads as 0.0 rather
    than a negative distance - "minus four per cent below the high" is a sentence no agent
    should have to interpret.
    """
    if not history:
        return None

    window_start = history[-1].on - timedelta(weeks=weeks)
    high = max([bar.close for bar in history if bar.on >= window_start] + [price])

    return _round((high - price) / high)


def build_fact_sheet(snapshot: MarketSnapshot) -> FactSheet:
    """The one place a snapshot becomes the thing an agent reads."""
    quote, history = snapshot.quote, snapshot.history

    return FactSheet(
        symbol=quote.symbol,
        currency=quote.currency,
        # Not re-rounded. This becomes reference_price and then an order size, so the only
        # rounding it should ever get is the one the market already applied.
        price=quote.price,
        as_of=quote.as_of,
        pe_ratio=None if quote.pe_ratio is None else round(quote.pe_ratio, 2),
        return_1m=trailing_return(history, days=30),
        return_3m=trailing_return(history, days=91),
        return_12m=trailing_return(history, days=365),
        volatility_30d=annualised_volatility(history, window=30),
        pct_below_52w_high=pct_below_high(quote.price, history, weeks=52),
        sector=quote.sector,
    )
