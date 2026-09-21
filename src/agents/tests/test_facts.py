"""The fact sheet is computed, never asked for.

Decision 5 says code calculates and the LLM interprets. These are the calculations, so the
test is the specification: a known price series has one right answer for a return and for a
volatility, which is exactly the case where writing the test first pays.
"""

import math
from datetime import UTC, date, datetime, timedelta

import pytest

from app.domain.facts import (
    FactSheet,
    MarketSnapshot,
    PriceBar,
    Quote,
    annualised_volatility,
    build_fact_sheet,
    pct_below_high,
    trailing_return,
)

START = date(2026, 1, 1)


def bars(*closes: float, start: date = START) -> tuple[PriceBar, ...]:
    """A daily series, oldest first. Calendar days rather than trading days: the functions
    under test do not care, and a test that has to model a holiday calendar tests that."""
    return tuple(
        PriceBar(on=start + timedelta(days=offset), close=close)
        for offset, close in enumerate(closes)
    )


def flat(count: int, close: float = 100.0) -> tuple[PriceBar, ...]:
    return bars(*([close] * count))


def quote(**overrides: object) -> Quote:
    defaults: dict[str, object] = {
        "symbol": "AAPL",
        "currency": "USD",
        "price": 100.0,
        "pe_ratio": 35.352398,
        "sector": "Technology",
        "as_of": datetime(2026, 1, 31, 20, 0, tzinfo=UTC),
    }
    return Quote.model_validate(defaults | overrides)


class TestTrailingReturn:
    def test_a_rise_over_the_window_is_the_fraction_gained(self):
        # 100 on day 0, 110 on day 30: a tenth, counted from the oldest bar at or before
        # the cutoff rather than from a fixed number of rows.
        series = bars(*([100.0] * 30 + [110.0]))

        assert trailing_return(series, days=30) == pytest.approx(0.1)

    def test_a_fall_is_negative(self):
        assert trailing_return(bars(*([200.0] * 30 + [150.0])), days=30) == pytest.approx(-0.25)

    def test_a_series_that_does_not_reach_back_far_enough_has_no_answer(self):
        # A newly listed share has no twelve-month return. None says so; zero would be a
        # claim that it went nowhere.
        assert trailing_return(flat(10), days=30) is None

    def test_the_cutoff_falls_back_to_the_last_bar_before_it(self):
        # A market is shut on the exact cutoff date more often than not. The last close on
        # or before it is the honest comparison, not the next one after.
        series = (
            PriceBar(on=date(2026, 1, 1), close=100.0),
            # nothing between the 2nd and the 30th: a long holiday
            PriceBar(on=date(2026, 1, 31), close=120.0),
        )

        assert trailing_return(series, days=30) == pytest.approx(0.2)

    def test_an_empty_series_has_no_answer(self):
        assert trailing_return((), days=30) is None


class TestAnnualisedVolatility:
    def test_a_price_that_never_moves_has_no_volatility(self):
        assert annualised_volatility(flat(40), window=30) == pytest.approx(0.0)

    def test_it_annualises_the_daily_deviation(self):
        # A series that alternates between two prices has a known daily deviation, so the
        # expected number can be derived rather than recorded from a previous run.
        series = bars(*([100.0, 101.0] * 20))
        up, down = math.log(101 / 100), math.log(100 / 101)
        daily = math.sqrt(sum((r - 0) ** 2 for r in [up, down] * 15) / 29)

        assert annualised_volatility(series, window=30) == pytest.approx(
            round(daily * math.sqrt(252), 4), abs=1e-4
        )

    def test_it_needs_one_more_bar_than_the_window(self):
        # Thirty returns need thirty-one closes. Thirty would quietly compute twenty-nine.
        assert annualised_volatility(flat(30), window=30) is None
        assert annualised_volatility(flat(31), window=30) is not None

    def test_it_uses_only_the_most_recent_window(self):
        # A crash a year ago must not still be reported as today's volatility.
        calm = [100.0] * 31
        turbulent = [50.0, 150.0] * 10

        assert annualised_volatility(bars(*(turbulent + calm)), window=30) == pytest.approx(0.0)


class TestPctBelowHigh:
    def test_a_price_at_the_high_is_zero_below_it(self):
        assert pct_below_high(100.0, flat(300), weeks=52) == pytest.approx(0.0)

    def test_a_price_a_fifth_under_the_high_says_so(self):
        assert pct_below_high(80.0, flat(300), weeks=52) == pytest.approx(0.2)

    def test_a_new_high_is_zero_rather_than_a_negative_distance(self):
        # The live quote can be above every close in the window. "Minus four per cent below
        # the high" is a sentence no agent should have to read.
        assert pct_below_high(104.0, flat(300), weeks=52) == pytest.approx(0.0)

    def test_a_peak_older_than_the_window_is_not_the_52_week_high(self):
        old_peak = [500.0] * 10
        recent = [100.0] * 400

        assert pct_below_high(100.0, bars(*(old_peak + recent)), weeks=52) == pytest.approx(0.0)

    def test_an_empty_series_has_no_answer(self):
        assert pct_below_high(100.0, (), weeks=52) is None


class TestTheSnapshotsOwnInvariant:
    def test_a_history_out_of_order_is_refused(self):
        # Every function here takes the last bar as the latest. A provider that returned
        # newest first would make all of them quietly wrong instead of failing.
        with pytest.raises(ValueError):
            MarketSnapshot(quote=quote(), history=bars(100.0, 101.0)[::-1])


class TestBuildFactSheet:
    def snapshot(self, history: tuple[PriceBar, ...], **quote_overrides: object) -> MarketSnapshot:
        return MarketSnapshot(quote=quote(**quote_overrides), history=history)

    def test_a_full_year_fills_every_field(self):
        sheet = build_fact_sheet(self.snapshot(flat(400)))

        assert sheet.symbol == "AAPL"
        assert sheet.currency == "USD"
        assert sheet.price == 100.0
        assert sheet.sector == "Technology"
        assert sheet.pe_ratio == 35.35
        assert sheet.return_1m == pytest.approx(0.0)
        assert sheet.return_3m == pytest.approx(0.0)
        assert sheet.return_12m == pytest.approx(0.0)
        assert sheet.volatility_30d == pytest.approx(0.0)
        assert sheet.pct_below_52w_high == pytest.approx(0.0)

    def test_a_short_history_leaves_the_long_horizons_empty_rather_than_guessing(self):
        sheet = build_fact_sheet(self.snapshot(flat(40)))

        assert sheet.return_1m is not None
        assert sheet.return_3m is None
        assert sheet.return_12m is None
        assert sheet.volatility_30d is not None

    def test_a_missing_pe_stays_missing(self):
        # A share with no earnings has no P/E. Substituting zero would read as "very cheap".
        assert build_fact_sheet(self.snapshot(flat(40), pe_ratio=None)).pe_ratio is None

    def test_the_price_is_passed_through_untouched(self):
        # Everything derived is rounded to keep it short in a prompt, but the price becomes
        # reference_price and then an order size, so it is not re-rounded here.
        sheet = build_fact_sheet(self.snapshot(flat(40), price=0.0123456))

        assert sheet.price == 0.0123456

    def test_the_derived_numbers_are_rounded(self):
        # Four decimals is a basis point. Beyond that it is tokens in every prompt that
        # reads the sheet, for precision the input never had.
        sheet = build_fact_sheet(self.snapshot(bars(*([100.0] * 30 + [103.33333]))))

        assert sheet.return_1m == 0.0333


class TestTheFactSheetCarriesNoProse:
    """The decision taken for stage 3: numbers and categories, never free text."""

    def test_there_is_no_free_text_field(self):
        # longBusinessSummary was 300 characters of external text going straight into a
        # prompt. Nothing here replaces it: when a news step arrives it gets its own schema
        # with its own caps, visible in the step's reads.
        assert "summary" not in FactSheet.model_fields
        assert "description" not in FactSheet.model_fields
        assert "news" not in FactSheet.model_fields

    def test_the_one_string_that_is_not_an_identifier_is_capped(self):
        # A sector is a category, not prose - but it still arrives from outside.
        with pytest.raises(ValueError):
            build_fact_sheet(MarketSnapshot(quote=quote(sector="x" * 41), history=flat(40)))
