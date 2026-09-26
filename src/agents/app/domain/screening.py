"""Which instruments are worth asking an agent about, decided without asking one.

Decision 5 again, one level out: code selects, the LLM interprets. Screening is the largest
token saving in the system - the agents see ten instruments instead of fifty - and it is
also the control the project is ultimately judged against. If the shortlist does as well as
the decisions made from it, the LLM is adding cost rather than value, and a better ranking
is worth more than a better team.

**Nothing here touches the network.** These are pure functions over bars, so the whole of
the selection rule is testable against fixed data, and the ranking can be changed with
outcomes in hand rather than by argument.

The ranking is deliberately simple, and that is not a placeholder apology: it is the
baseline the measurement in stage 4 exists to improve on. It ranks on **risk-adjusted
momentum** - the three-month return divided by realised volatility - and filters on
liquidity. Raw momentum alone would put an illiquid share that doubled on one headline at
the top of the list, which is exactly the candidate that ruins a measurement: it cannot be
bought at the price that ranked it.

Valuation is deliberately *not* in the ranking, although the roadmap offers it as an
example. P/E arrives from a per-instrument fundamentals call, which is the one part of the
market-data source that does not batch, so ranking on it would cost one request per
instrument every cycle. It is not lost: the shortlist goes through the ordinary `FactSheet`
path, so every agent still sees P/E on the instruments it is actually asked about. The
screen is a sieve, not the analysis.
"""

import statistics
from collections.abc import Sequence
from datetime import date
from typing import Annotated

from pydantic import BaseModel, ConfigDict, Field

from app.domain.facts import (
    DERIVED_PRECISION,
    annualised_volatility,
    trailing_return,
)

# The window the liquidity figure is measured over, in bars. Thirty trading days is about a
# calendar month, long enough that one quiet week does not disqualify a share.
LIQUIDITY_WINDOW = 30

# How much of that window must actually carry a volume. A median over twenty of thirty days
# is still a median; a median over two is a guess wearing the same name. Half is the line,
# stated rather than left to whatever the data happened to contain.
MIN_LIQUIDITY_BARS = LIQUIDITY_WINDOW // 2

# The momentum horizon the ranking uses, in calendar days, and the volatility window it is
# divided by, in bars. Three months is long enough to be a trend rather than a week's news.
MOMENTUM_DAYS = 90
VOLATILITY_WINDOW = 30


class ScreeningBar(BaseModel):
    """A day's close and the volume behind it.

    Separate from `PriceBar` on purpose, and the separation is load-bearing rather than
    tidy. `PriceBar` is the shape `contracts/quote-history.schema.json` describes, where the
    bar is declared with `unevaluatedProperties: false` and the engine's `BarDto` refuses an
    unmapped member - so a `volume` field added there would make every history response
    unreadable to the engine and stop outcome measurement dead.

    It still satisfies `ClosingBar`, which is what lets the factor functions in `facts.py`
    be reused here rather than copied.
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    on: date
    close: Annotated[float, Field(gt=0)]

    # None rather than zero: a source that did not report a volume is not a day nothing
    # traded. Zero would be a claim, and it is the claim that makes a share look illiquid.
    volume: Annotated[int, Field(ge=0)] | None


class Candidate(BaseModel):
    """One instrument as the screen sees it: a score, and the figures behind it.

    The figures travel with the score because a ranking nobody can check is a ranking
    nobody will question. They are also what the engine stores per cycle, which is what
    makes "did the agents beat the screen that picked them?" answerable afterwards.
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    symbol: str
    score: float
    return_3m: float
    volatility_30d: float
    median_dollar_volume: float


class Rejection(BaseModel):
    """An instrument that was looked at and left out, with the reason.

    Named rather than dropped. A universe that quietly shrinks is one where a systematic
    data failure reads, months later, as a decision not to hold anything in that sector.
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    symbol: str
    reason: str


def median_dollar_volume(
    bars: Sequence[ScreeningBar], *, window: int = LIQUIDITY_WINDOW
) -> float | None:
    """Turnover per day over the last `window` bars: price times volume, median.

    The median rather than the mean, because one earnings day can carry ten times a normal
    session and a mean would let that single day qualify a share that cannot be traded on
    any other. What this measures is "could a position be built and unwound here", which is
    a property of the typical day.

    Bars with no reported volume are skipped rather than counted as zero; if fewer than
    `MIN_LIQUIDITY_BARS` remain there is nothing honest to return.
    """
    recent = bars[-window:]
    turnover = [bar.close * bar.volume for bar in recent if bar.volume is not None]

    if len(turnover) < MIN_LIQUIDITY_BARS:
        return None

    return round(statistics.median(turnover), DERIVED_PRECISION)


def risk_adjusted_momentum(momentum: float, volatility: float) -> float | None:
    """Return per unit of realised volatility.

    Undefined at zero volatility rather than infinite: a share whose close has not moved in
    thirty sessions is either halted or untraded, and both are reasons to leave it out
    rather than to rank it first.
    """
    if volatility <= 0:
        return None

    return round(momentum / volatility, DERIVED_PRECISION)


def score_candidate(
    symbol: str, bars: Sequence[ScreeningBar], *, min_dollar_volume: float
) -> Candidate | Rejection:
    """One instrument in, either a ranked candidate or a stated reason it is not one.

    A union return rather than `Candidate | None`, so a caller cannot accidentally discard
    the reason - which is the whole of `Rejection`'s purpose.
    """
    momentum = trailing_return(bars, days=MOMENTUM_DAYS)
    if momentum is None:
        return Rejection(
            symbol=symbol, reason=f"no close from {MOMENTUM_DAYS} days ago to compare against"
        )

    volatility = annualised_volatility(bars, window=VOLATILITY_WINDOW)
    if volatility is None:
        return Rejection(
            symbol=symbol, reason=f"fewer than {VOLATILITY_WINDOW + 1} closes to measure volatility"
        )

    turnover = median_dollar_volume(bars)
    if turnover is None:
        return Rejection(
            symbol=symbol, reason=f"fewer than {MIN_LIQUIDITY_BARS} days report a volume"
        )

    if turnover < min_dollar_volume:
        return Rejection(
            symbol=symbol,
            reason=(
                f"typical daily turnover {turnover:.0f} is below the {min_dollar_volume:.0f} floor"
            ),
        )

    score = risk_adjusted_momentum(momentum, volatility)
    if score is None:
        return Rejection(symbol=symbol, reason="the close has not moved, so there is no volatility")

    return Candidate(
        symbol=symbol,
        score=score,
        return_3m=momentum,
        volatility_30d=volatility,
        median_dollar_volume=turnover,
    )


def rank(candidates: Sequence[Candidate], *, limit: int) -> tuple[Candidate, ...]:
    """The best `limit` candidates, highest score first.

    Ties break on symbol, so the same universe and the same bars always produce the same
    shortlist. Without that, two instruments with an identical score would swap places
    between cycles and the engine would see a new shortlist with no new information in it.
    """
    ordered = sorted(candidates, key=lambda candidate: (-candidate.score, candidate.symbol))
    return tuple(ordered[:limit])
