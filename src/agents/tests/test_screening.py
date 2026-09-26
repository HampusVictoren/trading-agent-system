"""The screen decides what an agent is asked about, so the screen is a specification.

Everything here is a pure function over bars, which is the case where writing the test first
pays: a known series has one right answer for a turnover and for a rank. It is also the part
of the system the project is judged against - if the shortlist does as well as the decisions
made from it, the ranking matters more than the team - so a ranking nobody can check is the
one thing this file exists to prevent.
"""

from datetime import date, timedelta

import pytest

from app.domain.facts import PriceBar, annualised_volatility, trailing_return
from app.domain.screening import (
    LIQUIDITY_WINDOW,
    MIN_LIQUIDITY_BARS,
    MOMENTUM_DAYS,
    VOLATILITY_WINDOW,
    Candidate,
    Rejection,
    ScreeningBar,
    median_dollar_volume,
    rank,
    risk_adjusted_momentum,
    score_candidate,
)

START = date(2026, 1, 1)

# Comfortably below any turnover the fixtures produce, so a test about momentum is not
# quietly a test about liquidity.
NO_FLOOR = 0.0


def bars(
    *closes: float, volume: int | None = 1_000_000, start: date = START
) -> tuple[ScreeningBar, ...]:
    """A daily series, oldest first, with the same volume on every bar unless stated."""
    return tuple(
        ScreeningBar(on=start + timedelta(days=offset), close=close, volume=volume)
        for offset, close in enumerate(closes)
    )


def a_rankable_series(*, close: float = 100.0, volume: int | None = 1_000_000):
    """Long enough and varied enough that every factor is defined.

    `MOMENTUM_DAYS + 1` calendar days of bars so there is a close to compare back to, and
    the closes alternate so realised volatility is not zero.
    """
    return tuple(
        ScreeningBar(
            on=START + timedelta(days=offset),
            close=close + (offset % 2),
            volume=volume,
        )
        for offset in range(MOMENTUM_DAYS + 2)
    )


class TestTheFactorFunctionsAreShared:
    def test_a_screening_bar_is_accepted_by_the_fact_sheets_own_arithmetic(self):
        # This is the reason ClosingBar exists. Screening needs the same returns and the same
        # volatility the fact sheet computes, and a second copy of that arithmetic would be
        # a second place for it to be wrong.
        series = bars(100.0, 101.0, 102.0, start=date(2026, 1, 1))

        assert trailing_return(series, days=1) is not None
        # window=2 rather than 1: a deviation needs two returns, and `annualised_volatility`
        # guards on closes rather than on returns, so window=1 passes its length check and
        # then raises inside statistics.stdev. Unreachable in this service - every caller
        # passes a fixed window of 30 - but recorded in the worklog rather than fixed here,
        # because widening a shared function's guard belongs in its own change.
        assert annualised_volatility(series, window=2) is not None

    def test_a_price_bar_still_works_too(self):
        # The widened signatures must not have narrowed anything: PriceBar is what the
        # history contract carries and what the fact sheet is built from.
        series = tuple(
            PriceBar(on=START + timedelta(days=offset), close=100.0 + offset) for offset in range(3)
        )

        assert trailing_return(series, days=1) is not None


class TestTurnoverIsTheTypicalDay:
    def test_it_is_price_times_volume(self):
        assert median_dollar_volume(bars(*[10.0] * LIQUIDITY_WINDOW, volume=100)) == 1000.0

    def test_one_enormous_day_does_not_qualify_a_share(self):
        # The reason it is a median and not a mean. A share that trades 1 000 shares a day
        # and 10 million on results day cannot be traded on any day but that one, and a mean
        # would say otherwise.
        quiet = list(bars(*[10.0] * (LIQUIDITY_WINDOW - 1), volume=1_000))
        spike = ScreeningBar(on=START + timedelta(days=LIQUIDITY_WINDOW), close=10.0, volume=10**7)

        assert median_dollar_volume(tuple(quiet) + (spike,)) == 10_000.0

    def test_only_the_window_counts(self):
        # An instrument that was liquid a year ago and is not now must not pass on history.
        old = bars(*[10.0] * 200, volume=10**6)
        recent = tuple(
            ScreeningBar(on=START + timedelta(days=200 + offset), close=10.0, volume=1_000)
            for offset in range(LIQUIDITY_WINDOW)
        )

        assert median_dollar_volume(old + recent) == 10_000.0

    def test_a_day_with_no_reported_volume_is_skipped_rather_than_counted_as_zero(self):
        # Zero would be a claim that nothing traded, which is not what a missing field says -
        # and counting it would drag the median of a liquid share below any floor.
        with_gaps = list(bars(*[10.0] * LIQUIDITY_WINDOW, volume=100))
        with_gaps[0] = ScreeningBar(on=with_gaps[0].on, close=10.0, volume=None)

        assert median_dollar_volume(tuple(with_gaps)) == 1000.0

    def test_too_few_days_with_a_volume_is_no_answer_rather_than_a_thin_one(self):
        series = bars(*[10.0] * LIQUIDITY_WINDOW, volume=None)
        enough = list(series)
        for index in range(MIN_LIQUIDITY_BARS - 1):
            enough[index] = ScreeningBar(on=series[index].on, close=10.0, volume=100)

        assert median_dollar_volume(tuple(enough)) is None

    def test_exactly_the_minimum_is_enough(self):
        # The boundary in the direction that must keep working.
        series = bars(*[10.0] * LIQUIDITY_WINDOW, volume=None)
        enough = list(series)
        for index in range(MIN_LIQUIDITY_BARS):
            enough[index] = ScreeningBar(on=series[index].on, close=10.0, volume=100)

        assert median_dollar_volume(tuple(enough)) == 1000.0

    def test_no_bars_at_all_is_no_answer(self):
        assert median_dollar_volume(()) is None


class TestTheScoreIsMomentumPerUnitOfRisk:
    def test_the_same_return_scores_lower_when_it_was_a_rougher_ride(self):
        # The whole point of dividing by volatility. Two shares up 30 % are not the same
        # opportunity if one of them got there sideways.
        calm = risk_adjusted_momentum(0.30, 0.15)
        wild = risk_adjusted_momentum(0.30, 0.60)

        assert calm is not None and wild is not None
        assert calm > wild

    def test_a_loss_scores_below_a_gain(self):
        assert risk_adjusted_momentum(-0.10, 0.20) < risk_adjusted_momentum(0.10, 0.20)  # type: ignore[operator]

    @pytest.mark.parametrize("volatility", [0.0, -0.1])
    def test_a_share_that_has_not_moved_is_not_ranked_first(self, volatility):
        # Dividing by zero would make an unmoving share infinitely attractive. A close that
        # has not changed in thirty sessions is halted or untraded, and both are reasons to
        # leave it out.
        assert risk_adjusted_momentum(0.30, volatility) is None


class TestAnInstrumentIsEitherRankedOrRefusedForAStatedReason:
    def test_a_complete_series_becomes_a_candidate_carrying_its_figures(self):
        # The figures travel with the score because a ranking nobody can check is a ranking
        # nobody will question - and because the engine stores them per cycle, which is what
        # makes "did the agents beat the screen?" answerable later.
        result = score_candidate("AAPL", a_rankable_series(), min_dollar_volume=NO_FLOOR)

        assert isinstance(result, Candidate)
        assert result.symbol == "AAPL"
        assert result.median_dollar_volume > 0
        assert result.volatility_30d > 0

    def test_a_series_too_short_for_the_momentum_horizon_is_refused_by_name(self):
        result = score_candidate("NEW", bars(100.0, 101.0), min_dollar_volume=NO_FLOOR)

        assert isinstance(result, Rejection)
        assert str(MOMENTUM_DAYS) in result.reason

    def test_a_series_too_short_for_volatility_is_refused_by_name(self):
        # Long enough in calendar terms to have a momentum figure, but with only a handful of
        # closes in it - a share that trades rarely rather than one that is newly listed.
        sparse = tuple(
            ScreeningBar(on=START + timedelta(days=offset * 30), close=100.0 + offset, volume=10**6)
            for offset in range(4)
        )

        result = score_candidate("THIN", sparse, min_dollar_volume=NO_FLOOR)

        assert isinstance(result, Rejection)
        assert str(VOLATILITY_WINDOW + 1) in result.reason

    def test_an_illiquid_share_is_refused_with_both_numbers_in_the_reason(self):
        # The reason names the turnover and the floor, so a universe that suddenly shrinks
        # can be diagnosed from the response rather than from the source.
        result = score_candidate(
            "TINY", a_rankable_series(close=1.0, volume=10), min_dollar_volume=1_000_000
        )

        assert isinstance(result, Rejection)
        assert "1000000" in result.reason.replace(" ", "")

    def test_a_share_with_no_volumes_at_all_is_refused_before_the_floor_is_applied(self):
        result = score_candidate("DARK", a_rankable_series(volume=None), min_dollar_volume=NO_FLOOR)

        assert isinstance(result, Rejection)
        assert "volume" in result.reason


class TestTheShortlistIsDeterministic:
    def candidate(self, symbol: str, score: float) -> Candidate:
        return Candidate(
            symbol=symbol,
            score=score,
            return_3m=0.1,
            volatility_30d=0.2,
            median_dollar_volume=10**7,
        )

    def test_the_highest_scores_come_first(self):
        shortlist = rank(
            [self.candidate("A", 0.5), self.candidate("B", 1.5), self.candidate("C", 1.0)],
            limit=3,
        )

        assert [c.symbol for c in shortlist] == ["B", "C", "A"]

    def test_only_the_limit_is_returned(self):
        shortlist = rank([self.candidate(s, float(i)) for i, s in enumerate("ABCDE")], limit=2)

        assert len(shortlist) == 2

    def test_a_tie_breaks_on_symbol_rather_than_on_arrival(self):
        # Without this, two instruments with the same score would swap places between cycles
        # and the engine would store a new shortlist containing no new information.
        first = rank([self.candidate("Z", 1.0), self.candidate("A", 1.0)], limit=2)
        second = rank([self.candidate("A", 1.0), self.candidate("Z", 1.0)], limit=2)

        assert [c.symbol for c in first] == ["A", "Z"]
        assert first == second

    def test_asking_for_more_than_exists_returns_what_exists(self):
        assert len(rank([self.candidate("A", 1.0)], limit=10)) == 1

    def test_nothing_rankable_is_an_empty_shortlist_rather_than_an_error(self):
        # A screen that finds nothing is a fact about the universe today, not a failure.
        assert rank([], limit=10) == ()
