"""Cache, timeout and error translation around a blocking market-data source.

Kept apart from the source itself so that the policy is testable without a network: every
decision here - when a cached answer is still good, what a hang becomes, what an unknown
symbol becomes - is exercised against a fake fetch in tests/test_market_data.py.
"""

import asyncio
import logging
import time
from collections.abc import Callable, Mapping, Sequence

from app.application.errors import InstrumentNotFound, MarketDataUnavailable
from app.domain.facts import MarketSnapshot
from app.domain.screening import ScreeningBar

logger = logging.getLogger(__name__)


class CachingMarketData:
    """A MarketDataProvider over any blocking fetch.

    yfinance is synchronous and does network I/O, so calling it directly from a coroutine
    would block the event loop for the whole request - every other request included.
    asyncio.to_thread moves it off, and wait_for bounds it: a thread that never returns
    would otherwise hold the request until the caller gave up.
    """

    def __init__(
        self,
        fetch: Callable[[str], MarketSnapshot],
        *,
        timeout_s: float,
        ttl_s: float,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._fetch = fetch
        self._timeout_s = timeout_s
        self._ttl_s = ttl_s
        # Injected so a test can move time without sleeping. monotonic rather than time():
        # a clock adjustment must not make a cached quote look older or newer than it is.
        self._clock = clock
        self._cache: dict[str, tuple[float, MarketSnapshot]] = {}

    async def snapshot(self, symbol: str) -> MarketSnapshot:
        cached = self._cache.get(symbol)
        if cached is not None and self._clock() - cached[0] < self._ttl_s:
            return cached[1]

        try:
            snapshot = await asyncio.wait_for(
                asyncio.to_thread(self._fetch, symbol), timeout=self._timeout_s
            )
        except TimeoutError as e:
            raise MarketDataUnavailable(
                f"The market-data source did not answer within {self._timeout_s}s for {symbol}."
            ) from e
        except InstrumentNotFound:
            # The caller's problem, not the source's. Passed through rather than folded
            # into "unavailable", or a typo would read as an outage.
            raise
        except Exception as e:
            raise MarketDataUnavailable(f"The market-data source failed for {symbol}: {e}") from e

        self._store(symbol, snapshot)
        return snapshot

    def _store(self, symbol: str, snapshot: MarketSnapshot) -> None:
        now = self._clock()
        # Expired entries are dropped on write, which bounds the dictionary by the number
        # of distinct symbols seen within one TTL. Enough for a handful of tickers per cycle;
        # a universe goes through CachingUniverseData below, which is bounded the same way
        # but by the universe cap rather than by however many tickers a cycle happened to
        # touch.
        self._cache = {
            key: entry for key, entry in self._cache.items() if now - entry[0] < self._ttl_s
        }
        self._cache[symbol] = (now, snapshot)


class CachingUniverseData:
    """A UniverseData over any blocking batch fetch.

    The same shape as CachingMarketData above, for the same reason - yfinance blocks and a
    hang has to become a timeout - with one difference that matters: it caches **per symbol**
    rather than per call. A screen of fifty and a screen of the same fifty plus one holding
    then share forty-nine cached series, where a cache keyed on the request would miss
    entirely. Only the symbols that are actually missing are fetched.

    The dictionary is bounded by the universe cap: expired entries are dropped on write, so
    it holds at most the distinct symbols seen within one TTL.
    """

    def __init__(
        self,
        fetch: Callable[[Sequence[str]], Mapping[str, tuple[ScreeningBar, ...]]],
        *,
        timeout_s: float,
        ttl_s: float,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._fetch = fetch
        self._timeout_s = timeout_s
        self._ttl_s = ttl_s
        self._clock = clock
        self._cache: dict[str, tuple[float, tuple[ScreeningBar, ...]]] = {}

    async def histories(self, symbols: Sequence[str]) -> Mapping[str, tuple[ScreeningBar, ...]]:
        now = self._clock()
        fresh = {
            symbol: entry[1]
            for symbol in symbols
            if (entry := self._cache.get(symbol)) is not None and now - entry[0] < self._ttl_s
        }

        missing = [symbol for symbol in symbols if symbol not in fresh]
        if not missing:
            return fresh

        try:
            fetched = await asyncio.wait_for(
                asyncio.to_thread(self._fetch, missing), timeout=self._timeout_s
            )
        except TimeoutError as e:
            raise MarketDataUnavailable(
                f"The market-data source did not answer within {self._timeout_s}s "
                f"for {len(missing)} instruments."
            ) from e
        except Exception as e:
            # No InstrumentNotFound branch, unlike the per-instrument provider. A symbol the
            # source has nothing for is not an error here - it is simply absent from the
            # answer, and the screen rejects it by name. Only reaching the source can fail.
            raise MarketDataUnavailable(
                f"The market-data source failed for {len(missing)} instruments: {e}"
            ) from e

        self._store(fetched, now)
        return fresh | dict(fetched)

    def _store(self, fetched: Mapping[str, tuple[ScreeningBar, ...]], now: float) -> None:
        self._cache = {
            key: entry for key, entry in self._cache.items() if now - entry[0] < self._ttl_s
        }
        # Only what came back is cached. A symbol the source had nothing for is retried next
        # time rather than remembered as empty, because "not listed today" and "the batch
        # dropped it" look the same from here and only one of them is permanent.
        for symbol, bars in fetched.items():
            self._cache[symbol] = (now, bars)
