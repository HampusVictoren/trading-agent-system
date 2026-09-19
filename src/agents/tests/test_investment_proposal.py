"""The InvestmentProposal model is the cross-service contract: it is the FastAPI
response_model, the AG2 response_schema and the shape the .NET engine deserialises.
These tests pin the rules that keep an invalid decision from reaching the engine."""

import pytest
from pydantic import ValidationError

from app.domain.models import ActionEnum, InvestmentProposal


def a_proposal(**overrides) -> InvestmentProposal:
    defaults = {
        "ticker": "AAPL",
        "action": ActionEnum.HOLD,
        "amount_usd": 0.0,
        "confidence": 0.5,
        "reasoning": "n/a",
    }
    return InvestmentProposal(**{**defaults, **overrides})


def test_buy_requires_a_positive_amount():
    # The rule that stops a malformed BUY from reaching RiskEngine.
    with pytest.raises(ValidationError, match="amount_usd"):
        a_proposal(action=ActionEnum.BUY, amount_usd=0.0)


def test_buy_with_an_amount_is_accepted():
    assert a_proposal(action=ActionEnum.BUY, amount_usd=500.0).amount_usd == 500.0


def test_hold_may_have_a_zero_amount():
    assert a_proposal(action=ActionEnum.HOLD, amount_usd=0.0).action is ActionEnum.HOLD


def test_negative_amount_is_rejected():
    with pytest.raises(ValidationError):
        a_proposal(amount_usd=-1.0)


@pytest.mark.parametrize("confidence", [-0.01, 1.01])
def test_confidence_must_be_a_probability(confidence):
    with pytest.raises(ValidationError):
        a_proposal(confidence=confidence)


@pytest.mark.parametrize("confidence", [0.0, 1.0])
def test_confidence_bounds_are_inclusive(confidence):
    assert a_proposal(confidence=confidence).confidence == confidence


def test_action_is_parsed_from_its_string_form():
    # AG2 returns JSON, so the enum has to accept the wire representation.
    assert a_proposal(action="BUY", amount_usd=1.0).action is ActionEnum.BUY


def test_unknown_action_is_rejected():
    with pytest.raises(ValidationError):
        a_proposal(action="YOLO")


def test_action_serialises_back_to_a_plain_string():
    # What the .NET DTO reads off the wire.
    assert a_proposal(action="SELL").model_dump()["action"] == "SELL"
