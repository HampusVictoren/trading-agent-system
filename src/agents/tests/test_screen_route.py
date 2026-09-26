"""The screen from the engine's side of the wire.

The ranking is covered in test_screening.py and the orchestration in test_screen_contract.py;
this is about what HTTP does with it - the shape accepted, the status codes, and the fact that
the endpoint which decides what gets analysed is as closed as the one that analyses.
"""

from datetime import UTC, datetime
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient
from pydantic import SecretStr

from app.api.security import HEADER as API_KEY_HEADER
from app.application.errors import MarketDataUnavailable
from app.dependencies import get_resources
from app.domain.screening import Candidate, Rejection, ScreenResult
from app.domain.signals import EquityInstrument
from app.main import app
from app.settings import get_settings

API_KEY = "a-test-key-of-some-length"

A_REQUEST = {
    "universe": [
        {"type": "equity", "symbol": "AAPL"},
        {"type": "equity", "symbol": "GONE"},
    ],
    "limit": 5,
    "min_dollar_volume": 5000000.0,
    "correlation_id": "cycle-1",
}

A_RESULT = ScreenResult(
    candidates=(
        Candidate(
            instrument=EquityInstrument(type="equity", symbol="AAPL"),
            score=0.5714,
            return_3m=0.08,
            volatility_30d=0.14,
            median_dollar_volume=12480000.0,
        ),
    ),
    rejected=(
        Rejection(
            instrument=EquityInstrument(type="equity", symbol="GONE"),
            reason="no price history came back for this symbol",
        ),
    ),
    as_of=datetime(2026, 9, 26, 13, 45, 2, 117000, tzinfo=UTC),
)


@pytest.fixture
def client():
    """A client whose screen does whatever the test says, with no network behind it."""
    calls: list = []

    def build(result=A_RESULT, error: Exception | None = None):
        async def screen(request):
            calls.append(request)
            if error is not None:
                raise error
            return result

        app.dependency_overrides[get_settings] = lambda: SimpleNamespace(
            agent_api_key=SecretStr(API_KEY)
        )
        app.dependency_overrides[get_resources] = lambda: SimpleNamespace(
            screening=SimpleNamespace(screen=screen),
            pipeline=None,
            models=None,
            memory=SimpleNamespace(ping=AsyncMock()),
            http_client=SimpleNamespace(get=AsyncMock()),
            llm_base_url=None,
        )
        return TestClient(app, raise_server_exceptions=False, headers={API_KEY_HEADER: API_KEY})

    build.calls = calls  # type: ignore[attr-defined]
    yield build
    app.dependency_overrides.clear()


class TestAShortlistIsReturned:
    def test_a_well_formed_request_gets_a_ranking(self, client):
        response = client().post("/v1/screen", json=A_REQUEST)

        assert response.status_code == 200
        assert response.json() == {
            "candidates": [
                {
                    "instrument": {"type": "equity", "symbol": "AAPL"},
                    "score": 0.5714,
                    "return_3m": 0.08,
                    "volatility_30d": 0.14,
                    "median_dollar_volume": 12480000.0,
                }
            ],
            "rejected": [
                {
                    "instrument": {"type": "equity", "symbol": "GONE"},
                    "reason": "no price history came back for this symbol",
                }
            ],
            "as_of": "2026-09-26T13:45:02.117000Z",
        }

    def test_the_request_reaches_the_service_as_a_validated_model(self, client):
        client().post("/v1/screen", json=A_REQUEST)

        received = client.calls[-1]
        assert [instrument.symbol for instrument in received.universe] == ["AAPL", "GONE"]
        assert received.limit == 5

    def test_an_empty_shortlist_is_a_two_hundred(self, client):
        # A screen that ranks nothing is a fact about the universe today, so the engine gets
        # an answer and analyses its holdings - not an error that stalls the cycle.
        empty = ScreenResult(candidates=(), rejected=(), as_of=A_RESULT.as_of)

        response = client(result=empty).post("/v1/screen", json=A_REQUEST)

        assert response.status_code == 200
        assert response.json()["candidates"] == []


class TestTheEndpointIsAsClosedAsTheOneThatCostsMoney:
    def test_no_api_key_is_refused(self, client):
        built = client()
        del built.headers[API_KEY_HEADER]

        response = built.post("/v1/screen", json=A_REQUEST)

        assert response.status_code == 401
        assert response.json()["error_code"] == "unauthorized"

    def test_a_wrong_api_key_is_refused(self, client):
        response = client().post(
            "/v1/screen", json=A_REQUEST, headers={API_KEY_HEADER: "not-the-key"}
        )

        assert response.status_code == 401

    def test_a_refused_request_never_reaches_the_service(self, client):
        # The screen costs a market-data fetch for up to a hundred instruments, so an
        # unauthenticated caller must not be able to make the service spend one.
        build = client()
        del build.headers[API_KEY_HEADER]
        build.post("/v1/screen", json=A_REQUEST)

        assert client.calls == []


class TestARequestThatIsNotTheContractIsRefusedBeforeAnyFetch:
    @pytest.mark.parametrize(
        "change",
        [
            {"universe": []},
            {"universe": [{"type": "future", "symbol": "ESZ5"}]},
            {"universe": [{"type": "equity", "symbol": "lowercase"}]},
            {"limit": 0},
            {"limit": 51},
            {"min_dollar_volume": -1},
            {"correlation_id": ""},
        ],
    )
    def test_a_value_the_contract_forbids_is_a_422(self, client, change):
        response = client().post("/v1/screen", json=A_REQUEST | change)

        assert response.status_code == 422
        assert response.json()["error_code"] == "invalid_request"

    def test_a_field_the_contract_does_not_have_is_refused(self, client):
        response = client().post("/v1/screen", json=A_REQUEST | {"sector": "Technology"})

        assert response.status_code == 422

    def test_a_universe_past_the_cap_is_refused(self, client):
        too_many = [{"type": "equity", "symbol": f"S{index}"} for index in range(101)]

        response = client().post("/v1/screen", json=A_REQUEST | {"universe": too_many})

        assert response.status_code == 422


class TestAFailureToReachTheSourceIsHonest:
    def test_market_data_unavailable_is_a_503(self, client):
        # Not an empty shortlist. Ranking against a fraction of the universe would be a
        # shortlist that reads as a judgement and is an outage.
        response = client(error=MarketDataUnavailable("the source is down")).post(
            "/v1/screen", json=A_REQUEST
        )

        assert response.status_code == 503
        assert response.json()["error_code"] == "market_data_unavailable"

    def test_the_body_carries_the_code_and_the_correlation_id_and_nothing_else(self, client):
        response = client(error=MarketDataUnavailable("the source is down")).post(
            "/v1/screen", json=A_REQUEST
        )

        assert set(response.json()) == {"error_code", "correlation_id"}
