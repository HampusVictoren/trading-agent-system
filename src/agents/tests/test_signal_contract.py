"""The contract file is the agreement between the two services; this side reads it.

The engine has the mirror of this in tests/Engine.Tests/TradeSignalContractTests.cs. Both
read the same checked-in files in contracts/, so a field added on one side only shows up as
a failure rather than as a 502 during a live run.
"""

import json
from datetime import UTC, datetime
from pathlib import Path

import pytest
from pydantic import ValidationError

from app.domain.signals import (
    MAX_HORIZON_DAYS,
    MAX_RISK_LENGTH,
    MAX_RISKS,
    MAX_THESIS_LENGTH,
    EquityInstrument,
    RunInfo,
    SignalRequest,
    Stance,
    TradeSignal,
    TradeView,
)

CONTRACTS = Path(__file__).resolve().parents[3] / "contracts"
EXAMPLES = CONTRACTS / "examples"

SIGNAL_EXAMPLES = ["signal-buy.json", "signal-hold.json", "signal-discriminator-last.json"]
REQUEST_EXAMPLES = ["request.json", "request-no-position.json"]


def example(name: str) -> dict:
    return json.loads((EXAMPLES / name).read_text(encoding="utf-8"))


def schema() -> dict:
    return json.loads((CONTRACTS / "trade-signal.schema.json").read_text(encoding="utf-8"))


def test_the_checked_in_examples_are_where_the_test_expects_them():
    # A directory that quietly moves would make every test below vacuous.
    assert len(list(EXAMPLES.glob("*.json"))) == 9


@pytest.mark.parametrize("name", SIGNAL_EXAMPLES)
def test_every_signal_example_reads_into_the_models(name):
    signal = TradeSignal.model_validate(example(name))

    assert isinstance(signal.instrument, EquityInstrument)
    assert signal.thesis.strip()
    assert signal.run.team_version


def test_a_signal_example_reads_field_for_field():
    signal = TradeSignal.model_validate(example("signal-buy.json"))

    assert signal.instrument.symbol == "AAPL"
    assert signal.stance is Stance.BUY
    assert signal.conviction == 0.78
    assert signal.horizon_days == 5
    assert signal.reference_price == 233.12
    assert len(signal.key_risks) == 2
    assert signal.run.team_id == "default"
    assert signal.run.revisions == 0


def test_the_order_of_the_keys_does_not_matter():
    # The engine needs AllowOutOfOrderMetadataProperties for this file, because
    # System.Text.Json otherwise demands the discriminator first. Pydantic does not care,
    # and this records that the example is read by both sides rather than only by the one
    # that needs it.
    signal = TradeSignal.model_validate(example("signal-discriminator-last.json"))

    assert signal.instrument.symbol == "BRK.B"
    assert signal.stance is Stance.SELL


@pytest.mark.parametrize("name", REQUEST_EXAMPLES)
def test_every_request_example_reads_into_the_models(name):
    request = SignalRequest.model_validate(example(name))

    assert request.team_id == "default"
    assert request.correlation_id


def test_a_request_without_a_position_says_so_rather_than_leaving_it_out():
    assert (
        SignalRequest.model_validate(example("request-no-position.json")).existing_position is None
    )
    assert SignalRequest.model_validate(example("request.json")).existing_position is not None


def test_what_goes_out_is_what_the_contract_describes():
    signal = TradeSignal.model_validate(example("signal-buy.json"))

    # Serialised and read back: a field that only survives inside Python would pass the
    # tests above and still reach the engine as something else.
    assert json.loads(signal.model_dump_json()) == example("signal-buy.json")


class TestTheModelsAndTheSchemaAgree:
    """The two halves of the contract are written by hand, so drift is the failure mode."""

    def test_the_signal_has_exactly_the_contracts_fields(self):
        assert set(TradeSignal.model_json_schema()["required"]) == set(schema()["required"])

    def test_the_request_has_exactly_the_contracts_fields(self):
        assert set(SignalRequest.model_json_schema()["required"]) == set(
            schema()["$defs"]["request"]["required"]
        )

    def test_the_caps_on_free_text_are_the_same_on_both_sides(self):
        properties = schema()["properties"]

        assert properties["thesis"]["maxLength"] == MAX_THESIS_LENGTH
        assert properties["key_risks"]["maxItems"] == MAX_RISKS
        assert properties["key_risks"]["items"]["maxLength"] == MAX_RISK_LENGTH

    def test_the_horizon_reaches_exactly_as_far_on_both_sides(self):
        # Not free text, so it gets its own test. The number is in three places - here, the
        # schema, and the engine's TradeSignalMapper - and the engine's suite reads the same
        # file, so all three are held together rather than pairwise.
        assert schema()["properties"]["horizon_days"]["maximum"] == MAX_HORIZON_DAYS


class TestTheAgentsAreNotAskedForFacts:
    """What the model may decide, and what it may not, is the point of the TradeView split."""

    def test_the_view_holds_only_what_the_agents_decide(self):
        assert set(TradeView.model_fields) == {
            "stance",
            "conviction",
            "thesis",
            "key_risks",
            "horizon_days",
        }

    @pytest.mark.parametrize("field", ["instrument", "reference_price", "quote_as_of", "run"])
    def test_the_view_never_asks_for_a_fact_code_already_has(self, field):
        # This is the test that guards the decision. The engine sizes an order as
        # floor(budget / reference_price); a model that writes that number decides how many
        # shares are bought. Adding any of these to TradeView puts it in the prompt.
        assert field not in TradeView.model_json_schema()["properties"]

    def test_joining_the_two_halves_produces_the_whole_contract(self):
        view = TradeView(
            stance=Stance.HOLD,
            conviction=0.31,
            thesis=(
                "Ingen tydlig katalysator före nästa rapport, och kursen ligger mitt i intervallet."
            ),
            key_risks=["Rapporten kan flytta kursen åt båda håll"],
            horizon_days=20,
        )

        signal = TradeSignal.from_view(
            view,
            instrument=EquityInstrument(type="equity", symbol="MSFT"),
            reference_price=421.5,
            quote_as_of=datetime(2026, 9, 21, 14, 3, 5, tzinfo=UTC),
            run=RunInfo(team_id="default", team_version="8f3a1c9e", revisions=0),
        )

        assert json.loads(signal.model_dump_json()) == example("signal-hold.json")


class TestAnAnswerThatIsNotTheContractIsRefused:
    def test_an_unknown_instrument_type_is_not_guessed_at(self):
        payload = example("signal-buy.json")
        payload["instrument"] = {"type": "future", "symbol": "ESZ5"}

        with pytest.raises(ValidationError):
            TradeSignal.model_validate(payload)

    def test_a_field_the_contract_does_not_have_is_refused_rather_than_ignored(self):
        # amount_usd is the field decision 1 removed. If it ever reappears it has to fail
        # here rather than be silently dropped on the way to the engine.
        payload = example("signal-buy.json") | {"amount_usd": 5000}

        with pytest.raises(ValidationError):
            TradeSignal.model_validate(payload)

    def test_a_lowercase_symbol_is_refused_rather_than_uppercased(self):
        # The engine's Ticker normalises; the contract says the wire value is already
        # uppercase. Normalising here too would hide which side got it wrong.
        payload = example("signal-buy.json")
        payload["instrument"]["symbol"] = "aapl"

        with pytest.raises(ValidationError):
            TradeSignal.model_validate(payload)

    @pytest.mark.parametrize("conviction", [-0.1, 1.1])
    def test_a_conviction_outside_zero_to_one_is_refused(self, conviction):
        with pytest.raises(ValidationError):
            TradeSignal.model_validate(example("signal-buy.json") | {"conviction": conviction})

    def test_a_naive_timestamp_is_refused(self):
        # Without an offset the engine's staleness check compares against the wrong clock,
        # and a quote from another timezone would look fresh.
        payload = example("signal-buy.json") | {"quote_as_of": "2026-09-21T14:03:00"}

        with pytest.raises(ValidationError):
            TradeSignal.model_validate(payload)

    def test_an_unbounded_thesis_is_refused(self):
        payload = example("signal-buy.json") | {"thesis": "x" * (MAX_THESIS_LENGTH + 1)}

        with pytest.raises(ValidationError):
            TradeSignal.model_validate(payload)

    def test_more_risks_than_the_cap_are_refused(self):
        payload = example("signal-buy.json") | {"key_risks": ["risk"] * (MAX_RISKS + 1)}

        with pytest.raises(ValidationError):
            TradeSignal.model_validate(payload)

    def test_a_horizon_past_the_cap_is_refused(self):
        # The cap exists because the model reached for a year: of 36 signals stored before
        # it, 26 asked for 180 days or more, which is a measurement that lands in 2027 and a
        # time-limit exit that never fires. The prompt now names the range; this is what
        # holds when the prompt is ignored, which it has been before.
        payload = example("signal-buy.json") | {"horizon_days": MAX_HORIZON_DAYS + 1}

        with pytest.raises(ValidationError):
            TradeSignal.model_validate(payload)

    def test_the_cap_itself_is_still_an_answer(self):
        # The boundary in the direction that must keep working: off by one here would
        # silently narrow what the model may say.
        payload = example("signal-buy.json") | {"horizon_days": MAX_HORIZON_DAYS}

        assert TradeSignal.model_validate(payload).horizon_days == MAX_HORIZON_DAYS
