"""The one place yfinance is imported, and the one place its payload is whitelisted.

Everything this returns is a Quote or a PriceBar. The provider's own dictionary - hundreds
of keys, including free text written by whoever filed the company profile - stops here.
"""

from collections.abc import Mapping, Sequence
from datetime import UTC, datetime
from typing import Any

import yfinance as yf

from app.application.errors import InstrumentNotFound
from app.domain.facts import MAX_SECTOR_LENGTH, MarketSnapshot, PriceBar, Quote
from app.domain.screening import ScreeningBar

# Two years rather than one. A twelve-month return needs a close from at least 365 days
# back, and "1y" ends 364 days ago - so the field would have been None for every share.
HISTORY_PERIOD = "2y"

# What screening needs, which is much less. The momentum horizon is 90 calendar days and the
# volatility window 30 bars, so six months covers both with room for a long market holiday -
# and it is a fifth of the data, fetched for up to a hundred instruments at a time.
SCREEN_HISTORY_PERIOD = "6mo"


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


def fetch_histories_from_yfinance(
    symbols: Sequence[str],
) -> Mapping[str, tuple[ScreeningBar, ...]]:
    """Closes and volumes for a whole list, in as few requests as yfinance will make.

    Blocking, like everything else in this module; called through `CachingUniverseData`.

    `yf.download` is the batching call, and it is the reason a universe of fifty is
    affordable: the per-instrument route in `fetch_from_yfinance` spends a request on
    `.info` for each symbol, which is what does not scale. Nothing here asks for
    fundamentals, because the ranking does not use them.
    """
    frame = yf.download(
        tickers=list(symbols),
        period=SCREEN_HISTORY_PERIOD,
        interval="1d",
        # One column level per ticker, so a single symbol and fifty parse the same way. It
        # is still not guaranteed - see to_histories - which is why parsing is separate.
        group_by="ticker",
        auto_adjust=True,
        actions=False,
        progress=False,
        threads=True,
    )
    return to_histories(symbols, frame)


def to_histories(symbols: Sequence[str], frame: Any) -> Mapping[str, tuple[ScreeningBar, ...]]:
    """Separated from the call above so the parsing can be tested without a network.

    The parsing is the part worth testing. `yf.download` returns a frame whose column shape
    depends on how many tickers were asked for - two levels for several, sometimes one for a
    single symbol - and a symbol it found nothing for is either an all-empty block or absent
    altogether. A symbol that yields no usable bars is **left out of the result**, which is
    what the port promises and what turns one delisted name into one rejection rather than a
    failed screen.
    """
    histories: dict[str, tuple[ScreeningBar, ...]] = {}

    for symbol in symbols:
        closes = _column(frame, symbol, "Close")
        if closes is None:
            continue

        volumes = _column(frame, symbol, "Volume")

        bars = tuple(
            ScreeningBar(
                on=timestamp.date(),
                close=float(close),
                volume=_optional_int(None if volumes is None else volumes.get(timestamp)),
            )
            # NaN fails this comparison as readily as a negative number does, so a row the
            # source had nothing for is skipped rather than becoming a bar with no close.
            for timestamp, close in closes.items()
            if close > 0
        )

        if bars:
            histories[symbol] = bars

    return histories


def _column(frame: Any, symbol: str, name: str) -> Any:
    """One named series for one symbol, whichever column shape the frame came back in."""
    try:
        if symbol in getattr(frame, "columns", []):
            return frame[symbol][name]
        # A single-ticker download can come back with one column level and no symbol in it.
        if name in getattr(frame, "columns", []):
            return frame[name]
    except (KeyError, IndexError):
        return None
    return None


def _optional_int(value: Any) -> int | None:
    """A volume, or None when the source did not report one.

    None rather than zero, because zero is a claim that nothing traded and that is the claim
    that makes a liquid share look untradeable. NaN is what yfinance actually returns for a
    missing volume, and NaN != NaN is how it is recognised.
    """
    if value is None:
        return None
    number = float(value)
    if number != number or number < 0:
        return None
    return int(number)
