"""The one place yfinance is imported, and the one place its payload is whitelisted.

Everything this returns is a Quote or a PriceBar. The provider's own dictionary - hundreds
of keys, including free text written by whoever filed the company profile - stops here.
"""

from datetime import UTC, datetime
from typing import Any

import yfinance as yf

from app.application.errors import InstrumentNotFound
from app.domain.facts import MAX_SECTOR_LENGTH, MarketSnapshot, PriceBar, Quote

# Two years rather than one. A twelve-month return needs a close from at least 365 days
# back, and "1y" ends 364 days ago - so the field would have been None for every share.
HISTORY_PERIOD = "2y"


def fetch_from_yfinance(symbol: str) -> MarketSnapshot:
    """Blocking. Called through CachingMarketData, never directly from a coroutine."""
    ticker = yf.Ticker(symbol)
    return to_snapshot(symbol, ticker.info, ticker.history(period=HISTORY_PERIOD, interval="1d"))


def to_snapshot(symbol: str, info: dict[str, Any], history: Any) -> MarketSnapshot:
    """Separated from the call above so that the mapping can be tested without a network."""
    price = info.get("currentPrice") or info.get("regularMarketPrice")

    if price is None or len(history) == 0:
        # yfinance answers a nonsense symbol with an empty frame rather than an error, so
        # "no data" is the only signal there is that the instrument does not exist.
        raise InstrumentNotFound(f"No market data exists for {symbol}.")

    quote = Quote(
        symbol=symbol,
        currency=info.get("currency") or "USD",
        price=float(price),
        # forwardPE over trailingPE: the agents are asked about the next few weeks, and a
        # trailing multiple describes a year that has already happened.
        pe_ratio=_optional_float(info.get("forwardPE")),
        sector=_clipped(info.get("sector")),
        as_of=datetime.now(UTC),
    )

    bars = tuple(
        PriceBar(on=timestamp.date(), close=float(close))
        for timestamp, close in history["Close"].items()
        if close > 0
    )

    return MarketSnapshot(quote=quote, history=bars)


def _optional_float(value: Any) -> float | None:
    # yfinance returns NaN as readily as None for a number it does not have, and NaN would
    # travel all the way into a prompt as "nan".
    if value is None:
        return None
    number = float(value)
    return None if number != number else number


def _clipped(value: Any) -> str | None:
    """A category from outside, cut to the length the schema allows."""
    return None if value is None else str(value)[:MAX_SECTOR_LENGTH]
