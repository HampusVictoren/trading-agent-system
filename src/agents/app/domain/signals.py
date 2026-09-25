"""The cross-service contract, in Python.

`contracts/trade-signal.schema.json` is the agreement; this file is one side of it, and
`src/engine/Application/Contracts/` is the other. Neither generates the other, so the test
in `tests/test_signal_contract.py` reads the checked-in examples and fails on drift.

The split that matters is between `TradeView` and `TradeSignal`. The agents produce the
view - a direction, a conviction and an argument. Everything a number is computed from
comes from code: the instrument from the request, the price from the fact sheet, the
identity of the run from the team. See "The agents give a view, code supplies the facts".
"""

from datetime import datetime
from enum import StrEnum
from typing import Annotated, Literal, Self

from pydantic import AwareDatetime, BaseModel, ConfigDict, Field

# Caps on the free-text fields, mirrored in contracts/trade-signal.schema.json. A field
# with no cap is one an agent can fill with anything, and every reader downstream - the
# next step's prompt, the engine, stage 4's decision table - pays for it.
MAX_THESIS_LENGTH = 2000
MAX_RISK_LENGTH = 300
MAX_RISKS = 5

# How far ahead a view may reach, in calendar days. 30 rather than a year, for three
# reasons that all point the same way. The system looks for short-term opportunities, so a
# six-month thesis is not an answer to the question that was asked. 30 calendar days is
# roughly the 20 trading days of the longest fixed horizon, so the model's own horizon is
# measured in the same window as the ones it is compared against rather than in 2027. And
# stage 5's time-limit exit sells when this many days have passed since the buy - a horizon
# of 365 is a rule that never fires.
#
# It is a cap, not a default: within the range the model still chooses, and being wrong
# about timing is something the measurement is meant to show.
MAX_HORIZON_DAYS = 30


class Stance(StrEnum):
    """The direction the agents argue for. The engine decides whether anything is traded."""

    BUY = "BUY"
    SELL = "SELL"
    HOLD = "HOLD"


# The one place the rule lives on this side. The quote endpoint takes a symbol in its path
# rather than in a body, so the pattern has to be reachable from there too - and a second
# copy of a validation rule is a second chance to get it wrong.
SYMBOL_PATTERN = r"^[A-Z][A-Z0-9.-]{0,9}$"

# What the pattern allows at most, stated separately because a path parameter is rejected
# on length before the regex is ever run.
MAX_SYMBOL_LENGTH = 10

# Both services store the correlation id and the team id in a varchar(64) - the engine in
# `trading.decisions`, this one in `agent.analysis_runs`. Capping the contract at the same
# width is what stops a value that one side accepts and the other refuses with a 500.
MAX_IDENTIFIER_LENGTH = 64


class EquityInstrument(BaseModel):
    # frozen, because an instrument is an identity rather than a state; extra="forbid" so a
    # field the contract does not have is refused instead of ignored.
    model_config = ConfigDict(extra="forbid", frozen=True)

    # No default, although "equity" is the only value there is. The contract lists `type` as
    # required, and a default would let a request without it be read as an equity rather
    # than refused - which is the whole point of a discriminator.
    type: Literal["equity"]

    # The same pattern the engine's Ticker enforces: uppercase, as it normalises it.
    symbol: Annotated[str, Field(pattern=SYMBOL_PATTERN)]


# A discriminated union on "type", with one variant so far. Written as a plain alias rather
# than Annotated[..., Field(discriminator="type")], because pydantic needs a union of at
# least two members for a discriminator. Literal["equity"] above does the same job in the
# meantime: an unknown type is refused rather than guessed at. When a second instrument
# arrives this becomes Annotated[EquityInstrument | FutureInstrument, Field(...)] and
# nothing that reads Instrument has to change.
type Instrument = EquityInstrument


class RunInfo(BaseModel):
    """How the answer was produced. The engine decides on none of it, but it stores it, so
    that stage 4 can compare outcomes per team setup."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    team_id: Annotated[str, Field(min_length=1)]
    team_version: Annotated[str, Field(min_length=1)]
    revisions: Annotated[int, Field(ge=0)]


class TradeView(BaseModel):
    """What the agents actually decide, and the last step's response_schema.

    Deliberately smaller than `TradeSignal`. A model asked for `reference_price` can answer
    with a price that was never quoted, and the engine sizes the order from that number -
    the same hole decision 1 closed for the amount, one level down. A model asked for
    `instrument` can answer TSLA to a question about AAPL, which it has done before. So
    neither is asked for: the pipeline supplies both from what it already knows.
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    stance: Stance
    conviction: Annotated[float, Field(ge=0, le=1)]
    thesis: Annotated[str, Field(min_length=1, max_length=MAX_THESIS_LENGTH)]
    key_risks: Annotated[
        list[Annotated[str, Field(min_length=1, max_length=MAX_RISK_LENGTH)]],
        Field(max_length=MAX_RISKS),
    ]
    horizon_days: Annotated[int, Field(ge=1, le=MAX_HORIZON_DAYS)]


class TradeSignal(TradeView):
    """The answer on the wire: the agents' view, plus the facts code is responsible for.

    Inherits from TradeView rather than repeating it, so the two cannot drift apart and the
    fields the agents own are visible as exactly the ones that are not declared here.
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    instrument: Instrument

    # float, not Decimal: JSON has one number type, pydantic serialises Decimal as a string,
    # and the engine's `required decimal` would refuse that. Python never does arithmetic on
    # this - it reads a quote and passes it on - so there is nothing here to lose precision.
    reference_price: Annotated[float, Field(gt=0)]

    # Aware, or the engine's staleness check compares a quote against the wrong clock.
    quote_as_of: AwareDatetime

    run: RunInfo

    @classmethod
    def from_view(
        cls,
        view: TradeView,
        *,
        instrument: Instrument,
        reference_price: float,
        quote_as_of: datetime,
        run: RunInfo,
    ) -> Self:
        """The one place the two halves are joined, so the boundary stays a single line."""
        return cls(
            **view.model_dump(),
            instrument=instrument,
            reference_price=reference_price,
            quote_as_of=quote_as_of,
            run=run,
        )


class ExistingPosition(BaseModel):
    """What the portfolio already holds of this instrument. The engine knows it; it is sent
    so that the agents reason about adding to a position rather than opening one."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    quantity: Annotated[float, Field(gt=0)]
    average_price: Annotated[float, Field(gt=0)]


class SignalRequest(BaseModel):
    """What the engine asks. It carries the limits, so that the agents reason inside them
    instead of against them - on the old contract they proposed amounts far above the cap
    and the risk rules rejected nearly all of them."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    instrument: Instrument
    team_id: Annotated[str, Field(min_length=1, max_length=MAX_IDENTIFIER_LENGTH)]
    as_of: AwareDatetime
    # Required and nullable, not optional: "no position" is something the engine states,
    # not something it may leave out.
    existing_position: ExistingPosition | None
    available_risk_budget_usd: Annotated[float, Field(ge=0)]
    max_position_pct: Annotated[float, Field(gt=0, le=1)]
    correlation_id: Annotated[str, Field(min_length=1, max_length=MAX_IDENTIFIER_LENGTH)]
