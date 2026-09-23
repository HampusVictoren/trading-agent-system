"""The HTTP behaviour that is not about any one endpoint.

The signal endpoint's own contract is in test_signals_route.py. This is the cross-cutting
part: the correlation id, the rule that an error body says nothing it should not, the key
that closes the expensive route, and the two probes that stay open.
"""

from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient
from pydantic import SecretStr

from app.api.security import HEADER as API_KEY_HEADER
from app.application.errors import LlmFailed, LlmTimeout
from app.dependencies import get_resources
from app.domain.signals import EquityInstrument, RunInfo, Stance, TradeSignal, TradeView
from app.main import app
from app.observability.correlation import HEADER
from app.settings import get_settings

# Stands for anything an exception message might carry that a caller has no business
# seeing: hostnames, roles, ports, query fragments.
INTERNAL_DETAIL = "connect failed for agent_svc at 127.0.0.1:5432"

API_KEY = "a-test-key-of-some-length"

A_SIGNAL = TradeSignal.from_view(
    TradeView(
        stance=Stance.HOLD,
        conviction=0.31,
        thesis="Ingen tydlig katalysator.",
        key_risks=["Rapporten kan flytta kursen."],
        horizon_days=20,
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

    def build(error: Exception | None = None):
        async def run(request):
            if error is not None:
                raise error
            return A_SIGNAL

        app.dependency_overrides[get_settings] = lambda: SimpleNamespace(
            agent_api_key=SecretStr(API_KEY)
        )
        app.dependency_overrides[get_resources] = lambda: SimpleNamespace(
            pipeline=SimpleNamespace(run=run),
            models=None,
            memory=SimpleNamespace(ping=AsyncMock(side_effect=OSError("no database"))),
            http_client=SimpleNamespace(get=AsyncMock(side_effect=OSError("no llm"))),
            llm_base_url="http://127.0.0.1:11434/v1",
        )
        # No `with`, so the lifespan does not run and no database is needed.
        return TestClient(app, raise_server_exceptions=False, headers={API_KEY_HEADER: API_KEY})

    yield build
    app.dependency_overrides.clear()


def signals(built, **kwargs):
    return built.post("/v1/signals", json=A_REQUEST, **kwargs)


class TestAnErrorBodySaysNothingItShouldNot:
    def test_an_unexpected_failure_is_a_500_and_nothing_else(self, client):
        response = signals(client(error=RuntimeError(INTERNAL_DETAIL)))

        assert response.status_code == 500
        assert response.json()["error_code"] == "internal_error"
        assert INTERNAL_DETAIL not in response.text

    def test_it_never_repeats_the_exception_message(self, client):
        response = signals(client(error=LlmFailed(INTERNAL_DETAIL)))

        assert INTERNAL_DETAIL not in response.text
        assert set(response.json()) == {"error_code", "correlation_id"}


class TestTheCorrelationId:
    def test_a_supplied_one_is_kept(self, client):
        response = signals(client(), headers={HEADER: "engine-cycle-42"})

        assert response.headers[HEADER] == "engine-cycle-42"

    def test_one_is_invented_when_none_is_supplied(self, client):
        assert len(signals(client()).headers[HEADER]) == 36

    def test_one_that_could_forge_a_log_line_is_replaced(self, client):
        forged = 'x", "level": "INFO\nfake'

        response = signals(client(), headers={HEADER: forged})

        assert response.headers[HEADER] != forged

    def test_an_error_body_carries_the_same_one_as_the_header(self, client):
        response = signals(client(error=LlmTimeout("slow")), headers={HEADER: "engine-cycle-7"})

        assert response.json()["correlation_id"] == "engine-cycle-7"
        assert response.headers[HEADER] == "engine-cycle-7"


class TestTheApiKey:
    @pytest.mark.parametrize(
        "supplied",
        [
            None,
            "",
            "wrong",
            "a-test-key-of-some-lengtH",  # same length, one byte different
            API_KEY + "x",
        ],
    )
    def test_an_analysis_needs_it(self, client, supplied):
        # The signal endpoint starts a run of model calls, so an unauthenticated one lets
        # anything that can reach the port spend the machine's time.
        headers = {} if supplied is None else {API_KEY_HEADER: supplied}

        response = signals(client(), headers={**{API_KEY_HEADER: ""}, **headers})

        assert response.status_code == 401
        assert response.json()["error_code"] == "unauthorized"

    def test_a_rejection_does_not_say_whether_it_was_missing_or_wrong(self, client):
        built = client()

        missing = signals(built, headers={API_KEY_HEADER: ""})
        wrong = signals(built, headers={API_KEY_HEADER: "wrong"})

        assert missing.json()["error_code"] == wrong.json()["error_code"]
        assert missing.status_code == wrong.status_code == 401

    def test_the_right_key_is_let_through(self, client):
        assert signals(client(), headers={API_KEY_HEADER: API_KEY}).status_code == 200


class TestTheProbesStayOpen:
    def test_health_checks_nothing_but_the_process(self, client):
        response = client().get("/health", headers={API_KEY_HEADER: ""})

        assert response.status_code == 200
        assert response.json()["status"] == "alive"

    def test_readiness_needs_no_key(self, client):
        # A load balancer has to be able to ask whether the service is up. Both
        # dependencies are stubbed as broken here, so a 503 proves the probe ran rather
        # than being rejected.
        response = client().get("/ready", headers={API_KEY_HEADER: ""})

        assert response.status_code == 503
        assert response.json()["checks"] == {"database": "unavailable", "llm": "unavailable"}
