"""Cache, timeout and error translation around a blocking market-data source.

Kept apart from the source itself so that the policy is testable without a network: every
decision here - when a cached answer is still good, what a hang becomes, what an unknown
symbol becomes - is exercised against a fake fetch in tests/test_market_data.py.
"""

import asyncio
import logging
import time
from collections.abc import Callable

from app.application.errors import InstrumentNotFound, MarketDataUnavailable
from app.domain.facts import MarketSnapshot

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
        # of distinct symbols seen within one TTL. Enough for a handful of tickers per
        # cycle; stage 5 screens a universe and will want a real cache with a size limit.
        self._cache = {
            key: entry for key, entry in self._cache.items() if now - entry[0] < self._ttl_s
        }
        self._cache[symbol] = (now, snapshot)
