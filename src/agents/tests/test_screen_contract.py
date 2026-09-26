"""The screen contract, and the orchestration that fills it.

`contracts/screen.schema.json` is the agreement for `POST /v1/screen`. The engine's side of
it arrives with stage 5's third pull request, when the engine starts driving the cycle; until
then this is the only reader, and these tests are what stop the file becoming documentation
that quietly goes stale.
"""

import json
from collections.abc import Mapping, Sequence
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

from app.application.errors import MarketDataUnavailable
from app.application.ports import UniverseData
from app.application.screening import NO_DATA, ScreeningService
from app.domain.screening import (
    MAX_REASON_LENGTH,
    MAX_SHORTLIST,
    MAX_UNIVERSE,
    MOMENTUM_DAYS,
    Candidate,
    Rejection,
    ScreeningBar,
    ScreenRequest,
    ScreenResult,
    risk_adjusted_momentum,
)
from app.domain.signals import EquityInstrument

CONTRACTS = Path(__file__).resolve().parents[3] / "contracts"
EXAMPLES = CONTRACTS / "examples"


def example(name: str) -> dict:
    return json.loads((EXAMPLES / name).read_text(encoding="utf-8"))


def schema() -> dict:
    return json.loads((CONTRACTS / "screen.schema.json").read_text(encoding="utf-8"))


def equity(symbol: str) -> EquityInstrument:
    return EquityInstrument(type="equity", symbol=symbol)


def series(*, close: float = 100.0, volume: int | None = 10**6, days: int = MOMENTUM_DAYS + 2):
    """A series long enough and varied enough that every factor is defined."""
    start = datetime(2026, 1, 1, tzinfo=UTC).date()
    return tuple(
        ScreeningBar(on=start + timedelta(days=offset), close=close + (offset % 2), volume=volume)
        for offset in range(days)
    )


class FakeUniverse(UniverseData):
    """Returns what it was given, and nothing for symbols it was not."""

    def __init__(
        self, histories: Mapping[str, tuple[ScreeningBar, ...]], *, fails: bool = False
    ) -> None:
        self._histories = histories
        self._fails = fails
        self.asked_for: tuple[str, ...] = ()

    async def histories(self, symbols: Sequence[str]) -> Mapping[str, tuple[ScreeningBar, ...]]:
        self.asked_for = tuple(symbols)
        if self._fails:
            raise MarketDataUnavailable("the source could not be reached")
        return {symbol: self._histories[symbol] for symbol in symbols if symbol in self._histories}


def a_request(*symbols: str, limit: int = 10, floor: float = 0.0) -> ScreenRequest:
    return ScreenRequest(
        universe=tuple(equity(symbol) for symbol in symbols),
        limit=limit,
        min_dollar_volume=floor,
        correlation_id="cycle-1",
    )


class TestTheCheckedInExamplesAreTheContract:
    def test_the_request_example_reads_into_the_model(self):
        request = ScreenRequest.model_validate(example("screen-request.json"))

        assert [instrument.symbol for instrument in request.universe] == [
            "AAPL",
            "MSFT",
            "NVDA",
            "TINY",
        ]
        assert request.limit == 2
        assert request.min_dollar_volume == 5_000_000

    def test_the_result_example_reads_into_the_model(self):
        result = ScreenResult.model_validate(example("screen-result.json"))

        assert [c.instrument.symbol for c in result.candidates] == ["NVDA", "AAPL"]
        assert [r.instrument.symbol for r in result.rejected] == ["MSFT", "TINY"]
        assert result.as_of.tzinfo is not None

    def test_the_result_example_is_ordered_best_first(self):
        # The example is read by a person deciding what the endpoint does, so an example in
        # the wrong order would teach the wrong thing.
        scores = [
            c.score for c in ScreenResult.model_validate(example("screen-result.json")).candidates
        ]

        assert scores == sorted(scores, reverse=True)

    def test_every_score_in_the_example_is_the_one_the_function_would_compute(self):
        # A hand-written example drifts from the code it illustrates, silently, and then
        # documents a formula that is not the formula. This caught a wrong digit when the
        # example was first written.
        for candidate in ScreenResult.model_validate(example("screen-result.json")).candidates:
            assert candidate.score == risk_adjusted_momentum(
                candidate.return_3m, candidate.volatility_30d
            )

    def test_a_result_round_trips_to_exactly_the_example(self):
        payload = example("screen-result.json")

        assert json.loads(ScreenResult.model_validate(payload).model_dump_json()) == payload


class TestTheModelAndTheSchemaAgree:
    def test_the_result_has_exactly_the_contracts_fields(self):
        assert set(ScreenResult.model_json_schema()["required"]) == set(schema()["required"])

    def test_the_request_has_exactly_the_contracts_fields(self):
        assert set(ScreenRequest.model_json_schema()["required"]) == set(
            schema()["$defs"]["request"]["required"]
        )

    def test_the_caps_are_the_same_on_both_sides(self):
        # Four numbers written in two places each. The engine's copy arrives in the third
        # pull request and reads this same file, the way the outcome batch's 500 does.
        request = schema()["$defs"]["request"]["properties"]

        assert request["universe"]["maxItems"] == MAX_UNIVERSE
        assert request["limit"]["maximum"] == MAX_SHORTLIST
        assert schema()["properties"]["candidates"]["maxItems"] == MAX_SHORTLIST
        assert schema()["$defs"]["rejection"]["properties"]["reason"]["maxLength"] == (
            MAX_REASON_LENGTH
        )

    def test_the_rejections_are_deliberately_uncapped(self):
        # The universe is already bounded, and a screen where everything was rejected is
        # exactly the one whose reasons you need in full.
        assert "maxItems" not in schema()["properties"]["rejected"]

    def test_a_universe_past_the_cap_is_refused(self):
        with pytest.raises(ValueError):
            a_request(*[f"S{index}" for index in range(MAX_UNIVERSE + 1)])

    def test_an_empty_universe_is_refused_rather_than_answered_with_nothing(self):
        # "Rank these zero instruments" is a caller bug, not a screen that found nothing.
        with pytest.raises(ValueError):
            ScreenRequest(universe=(), limit=5, min_dollar_volume=0, correlation_id="c-1")


class TestTheServiceFetchesOnceAndExplainsEveryOmission:
    @pytest.mark.asyncio
    async def test_the_whole_universe_is_asked_for_in_one_call(self):
        # The reason UniverseData exists. Ranking fifty instruments through the per-instrument
        # port would be fifty round trips every cycle.
        source = FakeUniverse({"AAPL": series(), "MSFT": series()})

        await ScreeningService(source).screen(a_request("AAPL", "MSFT"))

        assert source.asked_for == ("AAPL", "MSFT")

    @pytest.mark.asyncio
    async def test_a_symbol_the_source_had_nothing_for_is_rejected_by_name(self):
        # One delisted name in a universe of fifty must not cost the cycle its screen, and it
        # must not vanish either: a universe that quietly shrinks looks like a decision.
        source = FakeUniverse({"AAPL": series()})

        result = await ScreeningService(source).screen(a_request("AAPL", "GONE"))

        assert [c.instrument.symbol for c in result.candidates] == ["AAPL"]
        assert [(r.instrument.symbol, r.reason) for r in result.rejected] == [("GONE", NO_DATA)]

    @pytest.mark.asyncio
    async def test_a_source_that_cannot_be_reached_at_all_raises(self):
        # Not a rejection. Ranking against a fraction of the universe would be a shortlist
        # that reads as a judgement and is an outage.
        with pytest.raises(MarketDataUnavailable):
            await ScreeningService(FakeUniverse({}, fails=True)).screen(a_request("AAPL"))

    @pytest.mark.asyncio
    async def test_every_instrument_is_accounted_for_either_way(self):
        # The invariant worth stating: nothing in the universe disappears. A candidate that
        # scored but fell outside the limit is the one exception, and the limit is the
        # caller's own number.
        source = FakeUniverse({"AAPL": series(), "SHORT": series(days=3)})

        result = await ScreeningService(source).screen(a_request("AAPL", "SHORT", "GONE"))

        seen = {c.instrument.symbol for c in result.candidates} | {
            r.instrument.symbol for r in result.rejected
        }
        assert seen == {"AAPL", "SHORT", "GONE"}

    @pytest.mark.asyncio
    async def test_the_shortlist_honours_the_limit_and_the_rest_are_not_called_rejected(self):
        source = FakeUniverse(
            {symbol: series(close=float(100 + i)) for i, symbol in enumerate("ABC")}
        )

        result = await ScreeningService(source).screen(a_request("A", "B", "C", limit=1))

        assert len(result.candidates) == 1
        assert result.rejected == ()

    @pytest.mark.asyncio
    async def test_the_liquidity_floor_from_the_request_is_the_one_applied(self):
        source = FakeUniverse({"TINY": series(close=1.0, volume=10)})

        result = await ScreeningService(source).screen(a_request("TINY", floor=1_000_000))

        assert result.candidates == ()
        assert "floor" in result.rejected[0].reason

    @pytest.mark.asyncio
    async def test_a_universe_where_nothing_can_be_ranked_is_an_empty_shortlist(self):
        # A fact about the universe today, not a failure. The engine then analyses its
        # holdings and nothing else, which is the right behaviour rather than a stalled cycle.
        source = FakeUniverse({"SHORT": series(days=3)})

        result = await ScreeningService(source).screen(a_request("SHORT"))

        assert result.candidates == ()
        assert len(result.rejected) == 1

    @pytest.mark.asyncio
    async def test_the_rejection_types_are_what_the_contract_carries(self):
        source = FakeUniverse({"AAPL": series()})

        result = await ScreeningService(source).screen(a_request("AAPL", "GONE"))

        assert all(isinstance(c, Candidate) for c in result.candidates)
        assert all(isinstance(r, Rejection) for r in result.rejected)
