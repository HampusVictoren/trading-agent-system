"""The new endpoint, from the engine's side of the wire.

The pipeline itself is covered in test_pipeline.py; this is about what HTTP does with it -
the shape that is accepted, the status codes, and the fact that a bad request costs no
model call at all.
"""

from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient
from pydantic import SecretStr

from app.api.security import HEADER as API_KEY_HEADER
from app.application.errors import (
    InstrumentNotSupported,
    LlmTimeout,
    MarketDataUnavailable,
    UnknownTeam,
)
from app.dependencies import get_resources
from app.domain.signals import EquityInstrument, RunInfo, Stance, TradeSignal, TradeView
from app.main import app
from app.settings import get_settings

API_KEY = "a-test-key-of-some-length"

A_SIGNAL = TradeSignal.from_view(
    TradeView(
        stance=Stance.BUY,
        conviction=0.78,
        thesis="Stark tjänsteintäkt.",
        key_risks=["P/E över sektorsnittet."],
        horizon_days=5,
    ),
    instrument=EquityInstrument(type="equity", symbol="AAPL"),
    reference_price=233.12,
    quote_as_of="2026-09-23T14:03:00Z",  # type: ignore[arg-type]
    run=RunInfo(team_id="default", team_version="8f3a1c9e", revisions=0),
)

A_REQUEST = {
    "instrument": {"type": "equity", "symbol": "AAPL"},
    "team_id": "default",
    "as_of": "2026-09-23T14:02:55Z",
    "existing_position": None,
    "available_risk_budget_usd": 500.0,
    "max_position_pct": 0.05,
    "correlation_id": "0f2d7c11-3b48-4e9a-8c15-77ab2e4d6f30",
}


@pytest.fixture
def client():
    """A client whose pipeline does whatever the test says, with no model behind it."""
    calls: list = []

    def build(result=A_SIGNAL, error: Exception | None = None):
        async def run(request):
            calls.append(request)
            if error is not None:
                raise error
            return result

        app.dependency_overrides[get_settings] = lambda: SimpleNamespace(
            agent_api_key=SecretStr(API_KEY)
        )
        app.dependency_overrides[get_resources] = lambda: SimpleNamespace(
            pipeline=SimpleNamespace(run=run),
            models=None,
            memory=SimpleNamespace(ping=AsyncMock()),
            http_client=SimpleNamespace(get=AsyncMock()),
            llm_base_url=None,
        )
        return TestClient(app, raise_server_exceptions=False, headers={API_KEY_HEADER: API_KEY})

    build.calls = calls  # type: ignore[attr-defined]
    yield build
    app.dependency_overrides.clear()


class TestASignalIsReturned:
    def test_a_well_formed_request_gets_a_signal(self, client):
        response = client().post("/v1/signals", json=A_REQUEST)

        assert response.status_code == 200
        assert response.json() == {
            "stance": "BUY",
            "conviction": 0.78,
            "thesis": "Stark tjänsteintäkt.",
            "key_risks": ["P/E över sektorsnittet."],
            "horizon_days": 5,
            "instrument": {"type": "equity", "symbol": "AAPL"},
            "reference_price": 233.12,
            "quote_as_of": "2026-09-23T14:03:00Z",
            "run": {"team_id": "default", "team_version": "8f3a1c9e", "revisions": 0},
        }

    def test_the_request_reaches_the_pipeline_as_a_typed_object(self, client):
        build = client
        build().post("/v1/signals", json=A_REQUEST)

        request = build.calls[-1]
        assert request.instrument.symbol == "AAPL"
        assert request.team_id == "default"

    def test_the_answer_carries_no_amount(self, client):
        # Decision 1, checked at the wire: whatever else changes, an amount must not
        # appear here, because the engine is what decides how much money moves.
        body = client().post("/v1/signals", json=A_REQUEST).json()

        assert "amount_usd" not in body


class TestARequestThatIsNotTheContract:
    @pytest.mark.parametrize(
        "change",
        [
            {"instrument": {"type": "equity", "symbol": "aapl"}},
            {"instrument": {"type": "future", "symbol": "ESZ5"}},
            {"instrument": {"type": "equity", "symbol": "../internal/shutdown"}},
            {"team_id": ""},
            {"max_position_pct": 1.5},
            {"available_risk_budget_usd": -1},
        ],
        ids=["lowercase", "unknown type", "path traversal", "empty team", "pct", "negative"],
    )
    def test_it_is_refused_before_anything_is_run(self, client, change):
        build = client
        response = build().post("/v1/signals", json=A_REQUEST | change)

        assert response.status_code == 422
        assert response.json()["error_code"] == "invalid_request"
        assert build.calls == []

    def test_a_missing_field_is_refused(self, client):
        response = client().post("/v1/signals", json={"team_id": "default"})

        assert response.status_code == 422

    def test_an_extra_field_is_refused(self, client):
        # The contract is closed on both sides. A field the engine invents must fail here
        # rather than be silently dropped.
        response = client().post("/v1/signals", json=A_REQUEST | {"amount_usd": 5000})

        assert response.status_code == 422


class TestAFailureIsHonest:
    @pytest.mark.parametrize(
        ("error", "status", "code"),
        [
            (UnknownTeam("no such team"), 422, "unknown_team"),
            (InstrumentNotSupported("not an equity team"), 422, "instrument_not_supported"),
            (MarketDataUnavailable("yfinance down"), 503, "market_data_unavailable"),
            (LlmTimeout("slow"), 504, "llm_timeout"),
        ],
        ids=lambda v: getattr(v, "error_code", str(v)),
    )
    def test_the_status_says_what_went_wrong(self, client, error, status, code):
        response = client(error=error).post("/v1/signals", json=A_REQUEST)

        assert response.status_code == status
        assert response.json() == {
            "error_code": code,
            "correlation_id": response.headers["X-Correlation-Id"],
        }

    def test_no_failure_is_answered_with_a_signal(self, client):
        # The whole reason stage 1 existed: a failure that looks like a decision is one the
        # engine cannot tell apart from a real one.
        response = client(error=MarketDataUnavailable("down")).post("/v1/signals", json=A_REQUEST)

        assert "stance" not in response.json()


class TestTheEndpointIsClosed:
    def test_it_needs_the_api_key(self, client):
        built = client()
        response = built.post("/v1/signals", json=A_REQUEST, headers={API_KEY_HEADER: "wrong"})

        assert response.status_code == 401
        assert response.json()["error_code"] == "unauthorized"

    def test_it_is_on_the_router_that_carries_the_dependency(self, client):
        # The key check sits on the router, so a route added later is closed by default.
        # This asserts the new route actually landed on that router.
        built = client()
        built.headers.pop(API_KEY_HEADER)

        assert built.post("/v1/signals", json=A_REQUEST).status_code == 401
