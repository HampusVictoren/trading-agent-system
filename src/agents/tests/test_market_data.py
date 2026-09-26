"""The policy around a market-data source, tested without touching a network.

CachingMarketData holds every decision worth testing - when a cached answer is still good,
what a hang becomes, what an unknown symbol becomes - and takes the fetch as an argument,
so all of it runs against a fake.
"""

import asyncio
import time
from datetime import UTC, date, datetime, timedelta

import pandas as pd
import pytest

from app.application.errors import InstrumentNotFound, MarketDataUnavailable
from app.application.ports import MarketDataProvider
from app.domain.facts import MarketSnapshot, PriceBar, Quote
from app.domain.screening import ScreeningBar
from app.infrastructure.market_data.caching import CachingMarketData, CachingUniverseData
from app.infrastructure.market_data.yfinance_source import to_histories, to_snapshot

# A short series is enough: these tests are about caching and parsing, never about whether a
# factor could be computed from it.
A_BAR_SERIES = tuple(
    ScreeningBar(on=date(2026, 1, 1) + timedelta(days=offset), close=100.0 + offset, volume=1_000)
    for offset in range(3)
)


def snapshot(symbol: str = "AAPL", price: float = 100.0) -> MarketSnapshot:
    return MarketSnapshot(
        quote=Quote(
            symbol=symbol,
            currency="USD",
            price=price,
            pe_ratio=None,
            sector=None,
            as_of=datetime(2026, 1, 1, tzinfo=UTC),
        ),
        history=(PriceBar(on=datetime(2026, 1, 1).date(), close=price),),
    )


class FakeClock:
    def __init__(self) -> None:
        self.now = 0.0

    def __call__(self) -> float:
        return self.now


class CountingFetch:
    """Records what it was asked for, so a cache hit is observable rather than assumed."""

    def __init__(self, price: float = 100.0) -> None:
        self.calls: list[str] = []
        self.price = price

    def __call__(self, symbol: str) -> MarketSnapshot:
        self.calls.append(symbol)
        return snapshot(symbol, self.price)


def provider(fetch, *, ttl_s: float = 60.0, timeout_s: float = 5.0, clock=None):
    return CachingMarketData(fetch, timeout_s=timeout_s, ttl_s=ttl_s, clock=clock or FakeClock())


class TestTheCache:
    async def test_a_second_ask_inside_the_ttl_does_not_hit_the_source(self):
        fetch = CountingFetch()
        market = provider(fetch)

        await market.snapshot("AAPL")
        await market.snapshot("AAPL")

        assert fetch.calls == ["AAPL"]

    async def test_an_expired_entry_is_fetched_again(self):
        fetch, clock = CountingFetch(), FakeClock()
        market = provider(fetch, ttl_s=60.0, clock=clock)

        await market.snapshot("AAPL")
        clock.now = 61.0
        await market.snapshot("AAPL")

        assert fetch.calls == ["AAPL", "AAPL"]

    async def test_the_entry_is_still_good_one_tick_before_it_expires(self):
        fetch, clock = CountingFetch(), FakeClock()
        market = provider(fetch, ttl_s=60.0, clock=clock)

        await market.snapshot("AAPL")
        clock.now = 59.9
        await market.snapshot("AAPL")

        assert fetch.calls == ["AAPL"]

    async def test_each_symbol_is_cached_on_its_own(self):
        fetch = CountingFetch()
        market = provider(fetch)

        await market.snapshot("AAPL")
        await market.snapshot("MSFT")

        assert fetch.calls == ["AAPL", "MSFT"]

    async def test_a_stale_entry_is_dropped_rather_than_kept_forever(self):
        # The dictionary is bounded by the symbols seen inside one TTL. Without the prune
        # a long-running service would hold every symbol it had ever been asked about.
        fetch, clock = CountingFetch(), FakeClock()
        market = provider(fetch, ttl_s=60.0, clock=clock)

        await market.snapshot("AAPL")
        clock.now = 61.0
        await market.snapshot("MSFT")

        assert list(market._cache) == ["MSFT"]


class TestWhatAFailureBecomes:
    async def test_a_source_that_hangs_becomes_unavailable_rather_than_a_held_request(self):
        def hangs(symbol: str) -> MarketSnapshot:
            import time

            time.sleep(0.3)
            return snapshot(symbol)

        with pytest.raises(MarketDataUnavailable):
            await provider(hangs, timeout_s=0.01).snapshot("AAPL")

    async def test_a_source_that_raises_becomes_unavailable(self):
        def fails(symbol: str) -> MarketSnapshot:
            raise ConnectionError("no route to host")

        with pytest.raises(MarketDataUnavailable):
            await provider(fails).snapshot("AAPL")

    async def test_an_unknown_symbol_is_not_folded_into_an_outage(self):
        # A typo and a broken source are different problems with different fixes, and the
        # engine answers 422 for one and 503 for the other.
        def missing(symbol: str) -> MarketSnapshot:
            raise InstrumentNotFound(symbol)

        with pytest.raises(InstrumentNotFound):
            await provider(missing).snapshot("AAPLL")

    async def test_a_failure_is_not_cached(self):
        calls: list[str] = []

        def fails_once(symbol: str) -> MarketSnapshot:
            calls.append(symbol)
            if len(calls) == 1:
                raise ConnectionError("no route to host")
            return snapshot(symbol)

        market = provider(fails_once)
        with pytest.raises(MarketDataUnavailable):
            await market.snapshot("AAPL")

        assert (await market.snapshot("AAPL")).quote.symbol == "AAPL"

    async def test_the_event_loop_keeps_running_while_the_source_blocks(self):
        # The point of asyncio.to_thread. Calling yfinance straight from a coroutine would
        # stop every other request for the length of the call.
        ticks = 0

        async def tick() -> None:
            nonlocal ticks
            for _ in range(5):
                await asyncio.sleep(0.01)
                ticks += 1

        def slow(symbol: str) -> MarketSnapshot:
            import time

            time.sleep(0.1)
            return snapshot(symbol)

        await asyncio.gather(provider(slow).snapshot("AAPL"), tick())

        assert ticks == 5


class TestTheYfinancePayloadIsWhitelisted:
    def frame(self, *closes: float) -> pd.DataFrame:
        index = pd.date_range("2026-01-01", periods=len(closes), tz="America/New_York")
        return pd.DataFrame({"Close": list(closes)}, index=index)

    def info(self, **overrides: object) -> dict:
        return {
            "currentPrice": 338.98,
            "currency": "USD",
            "forwardPE": 35.352398,
            "sector": "Technology",
            "longBusinessSummary": "Apple Inc. designs, manufactures and markets...",
            "irrelevantKey": "whatever else yfinance decides to send",
        } | overrides

    def test_only_the_whitelisted_fields_survive(self):
        # The summary is the field that used to go straight into a prompt. It has nowhere
        # to go now: Quote forbids extras, so an attempt to pass it through fails here.
        result = to_snapshot("AAPL", self.info(), self.frame(100.0, 101.0))

        assert result.quote.model_dump().keys() == {
            "symbol",
            "currency",
            "price",
            "pe_ratio",
            "sector",
            "as_of",
        }

    def test_the_series_becomes_bars_oldest_first(self):
        result = to_snapshot("AAPL", self.info(), self.frame(100.0, 101.0, 102.0))

        assert [bar.close for bar in result.history] == [100.0, 101.0, 102.0]
        assert result.history[0].on < result.history[-1].on

    def test_it_falls_back_to_the_regular_market_price(self):
        info = self.info(currentPrice=None, regularMarketPrice=250.0)

        assert to_snapshot("AAPL", info, self.frame(100.0)).quote.price == 250.0

    def test_a_symbol_with_no_data_is_not_an_instrument(self):
        with pytest.raises(InstrumentNotFound):
            to_snapshot("NOPE", self.info(), self.frame())

    def test_a_symbol_with_no_price_is_not_an_instrument(self):
        info = self.info(currentPrice=None, regularMarketPrice=None)

        with pytest.raises(InstrumentNotFound):
            to_snapshot("NOPE", info, self.frame(100.0))

    def test_a_nan_ratio_becomes_missing_rather_than_the_word_nan(self):
        # yfinance returns NaN as readily as None, and NaN reaches a prompt as "nan",
        # which a model is free to read as a number.
        result = to_snapshot("AAPL", self.info(forwardPE=float("nan")), self.frame(100.0))

        assert result.quote.pe_ratio is None

    def test_a_sector_longer_than_the_cap_is_cut_rather_than_refused(self):
        # The cap protects the prompt; a long sector name is not a reason to fail an
        # analysis, unlike a missing price.
        result = to_snapshot("AAPL", self.info(sector="x" * 200), self.frame(100.0))

        assert result.quote.sector is not None
        assert len(result.quote.sector) == 40


def test_the_caching_provider_is_a_market_data_provider():
    assert isinstance(provider(CountingFetch()), MarketDataProvider)


class TestABatchOfHistoriesIsParsedFromWhateverShapeYfinanceReturns:
    """`yf.download`'s column layout depends on how many tickers were asked for, which is
    exactly the kind of upstream detail that must not reach the domain untested."""

    def frame(self, data: dict, *, multi: bool) -> pd.DataFrame:
        index = pd.to_datetime(["2026-01-02", "2026-01-05", "2026-01-06"])
        if not multi:
            symbol = next(iter(data))
            return pd.DataFrame(data[symbol], index=index)
        return pd.DataFrame(
            {
                (symbol, field): values
                for symbol, fields in data.items()
                for field, values in fields.items()
            },
            index=index,
        )

    def test_several_tickers_come_back_grouped_by_symbol(self):
        frame = self.frame(
            {
                "AAPL": {"Close": [100.0, 101.0, 102.0], "Volume": [1_000, 1_100, 1_200]},
                "MSFT": {"Close": [400.0, 401.0, 402.0], "Volume": [500, 510, 520]},
            },
            multi=True,
        )

        histories = to_histories(["AAPL", "MSFT"], frame)

        assert set(histories) == {"AAPL", "MSFT"}
        assert histories["AAPL"][0].close == 100.0
        assert histories["AAPL"][0].volume == 1_000
        assert histories["MSFT"][-1].close == 402.0

    def test_a_single_ticker_with_one_column_level_parses_the_same_way(self):
        # The shape that would otherwise be discovered in production, on the first cycle
        # where the universe and the holdings happen to overlap down to one symbol.
        frame = self.frame(
            {"AAPL": {"Close": [100.0, 101.0, 102.0], "Volume": [1_000, 1_100, 1_200]}},
            multi=False,
        )

        histories = to_histories(["AAPL"], frame)

        assert len(histories["AAPL"]) == 3

    def test_bars_come_back_oldest_first(self):
        # Every factor function takes the last bar as the latest one, so a reversed series
        # would make all of them quietly wrong rather than fail.
        frame = self.frame(
            {"AAPL": {"Close": [100.0, 101.0, 102.0], "Volume": [1, 1, 1]}}, multi=False
        )

        dates = [bar.on for bar in to_histories(["AAPL"], frame)["AAPL"]]

        assert dates == sorted(dates)

    def test_a_symbol_the_frame_has_nothing_for_is_left_out_rather_than_empty(self):
        # What the port promises, and what turns one delisted name into one rejection
        # instead of a failed screen.
        frame = self.frame(
            {"AAPL": {"Close": [100.0, 101.0, 102.0], "Volume": [1, 1, 1]}}, multi=True
        )

        histories = to_histories(["AAPL", "GONE"], frame)

        assert "GONE" not in histories

    def test_a_row_with_no_close_is_skipped(self):
        # NaN fails a > 0 comparison as readily as a negative number, which is what stops a
        # day the source had nothing for becoming a bar with no price.
        frame = self.frame(
            {"AAPL": {"Close": [100.0, float("nan"), 102.0], "Volume": [1, 1, 1]}}, multi=False
        )

        assert len(to_histories(["AAPL"], frame)["AAPL"]) == 2

    def test_a_missing_volume_becomes_none_rather_than_zero(self):
        # Zero is a claim that nothing traded, and it is the claim that makes a liquid share
        # look untradeable once the median is taken.
        frame = self.frame(
            {"AAPL": {"Close": [100.0, 101.0, 102.0], "Volume": [1_000, float("nan"), 1_200]}},
            multi=False,
        )

        volumes = [bar.volume for bar in to_histories(["AAPL"], frame)["AAPL"]]

        assert volumes == [1_000, None, 1_200]

    def test_a_frame_with_no_columns_at_all_yields_nothing(self):
        assert to_histories(["AAPL"], pd.DataFrame()) == {}


class TestTheUniverseCacheFetchesOnlyWhatItIsMissing:
    def source(self, *, fails: bool = False, hangs: bool = False):
        calls: list[list[str]] = []

        def fetch(symbols):
            calls.append(list(symbols))
            if fails:
                raise RuntimeError("upstream said no")
            if hangs:
                time.sleep(5)
            return {symbol: A_BAR_SERIES for symbol in symbols}

        fetch.calls = calls  # type: ignore[attr-defined]
        return fetch

    def cache(self, fetch, *, ttl_s: float = 300, timeout_s: float = 1.0, clock=None):
        return CachingUniverseData(
            fetch,
            timeout_s=timeout_s,
            ttl_s=ttl_s,
            clock=clock or (lambda: 0.0),
        )

    @pytest.mark.asyncio
    async def test_the_first_call_fetches_everything(self):
        fetch = self.source()

        await self.cache(fetch).histories(["AAPL", "MSFT"])

        assert fetch.calls == [["AAPL", "MSFT"]]

    @pytest.mark.asyncio
    async def test_a_second_call_within_the_ttl_fetches_nothing(self):
        fetch = self.source()
        cache = self.cache(fetch)

        await cache.histories(["AAPL", "MSFT"])
        await cache.histories(["AAPL", "MSFT"])

        assert len(fetch.calls) == 1

    @pytest.mark.asyncio
    async def test_only_the_symbols_not_already_held_are_fetched(self):
        # The reason this caches per symbol rather than per call. A screen of the universe
        # followed by a screen of the universe plus one holding must cost one extra series,
        # not the whole list again.
        fetch = self.source()
        cache = self.cache(fetch)

        await cache.histories(["AAPL", "MSFT"])
        await cache.histories(["AAPL", "MSFT", "NVDA"])

        assert fetch.calls == [["AAPL", "MSFT"], ["NVDA"]]

    @pytest.mark.asyncio
    async def test_an_expired_entry_is_fetched_again(self):
        now = {"t": 0.0}
        fetch = self.source()
        cache = self.cache(fetch, ttl_s=100, clock=lambda: now["t"])

        await cache.histories(["AAPL"])
        now["t"] = 101.0
        await cache.histories(["AAPL"])

        assert len(fetch.calls) == 2

    @pytest.mark.asyncio
    async def test_a_symbol_that_came_back_empty_is_not_remembered_as_empty(self):
        # "Not listed today" and "the batch dropped it" look the same from here, and only one
        # of them is permanent - so the next screen asks again rather than inheriting a hole.
        calls: list[list[str]] = []

        def fetch(symbols):
            calls.append(list(symbols))
            return {}

        cache = self.cache(fetch)
        await cache.histories(["GONE"])
        await cache.histories(["GONE"])

        assert calls == [["GONE"], ["GONE"]]

    @pytest.mark.asyncio
    async def test_a_hang_becomes_market_data_unavailable(self):
        with pytest.raises(MarketDataUnavailable):
            await self.cache(self.source(hangs=True), timeout_s=0.05).histories(["AAPL"])

    @pytest.mark.asyncio
    async def test_a_source_that_raises_becomes_market_data_unavailable(self):
        # No InstrumentNotFound branch here, unlike the per-instrument provider: a symbol the
        # source has nothing for is simply absent from the answer, so only reaching it fails.
        with pytest.raises(MarketDataUnavailable):
            await self.cache(self.source(fails=True)).histories(["AAPL"])

    @pytest.mark.asyncio
    async def test_the_answer_holds_cached_and_freshly_fetched_series_alike(self):
        fetch = self.source()
        cache = self.cache(fetch)

        await cache.histories(["AAPL"])
        both = await cache.histories(["AAPL", "MSFT"])

        assert set(both) == {"AAPL", "MSFT"}
