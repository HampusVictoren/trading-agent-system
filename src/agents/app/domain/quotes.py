"""The other half of the cross-service contract: a price, and nothing else.

The engine has no market data of its own, on purpose - one integration, in one service.
But it cannot value a holding it is not analysing, which means a portfolio with more than
one position cannot be valued at all, and the position limit is a share of that value. This
is what closes that.

It is deliberately narrower than `Quote`. P/E and sector are fact-sheet material, read by
agents; the engine reads a price to multiply by a quantity. A contract carries what its
reader needs, because every field in it is a field the other side has to keep accepting.
"""

from typing import Annotated, Self

from pydantic import AwareDatetime, BaseModel, ConfigDict, Field

from app.domain.facts import Currency, PriceBar, Quote
from app.domain.signals import Instrument


class InstrumentQuote(BaseModel):
    """One instrument's price, as the engine is allowed to see it."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    instrument: Instrument

    price: Annotated[float, Field(gt=0)]

    currency: Currency

    # When the price was taken, not when it was asked for. The engine refuses to value a
    # holding on a quote older than its policy allows, which it cannot do without this.
    as_of: AwareDatetime

    @classmethod
    def from_quote(cls, quote: Quote, *, instrument: Instrument) -> Self:
        """The instrument comes from the validated request, never from the provider's echo.

        The same rule the signal pipeline follows: what the caller asked about is known to
        be well formed, and what a provider returns is not.
        """
        return cls(
            instrument=instrument,
            price=quote.price,
            currency=quote.currency,
            as_of=quote.as_of,
        )


class InstrumentHistory(BaseModel):
    """The closes behind an instrument, from a date the caller names.

    The engine measures an outcome by counting bars: five trading days is the fifth bar
    after the signal, and a day with no bar is a day the market was shut. That makes this
    the engine's trading calendar as well as its price history, which is why it carries the
    days rather than only the closes.
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    instrument: Instrument

    # Oldest first, and the same PriceBar the fact sheet's ratios are computed from - one
    # shape for a close, whoever reads it.
    bars: tuple[PriceBar, ...]
