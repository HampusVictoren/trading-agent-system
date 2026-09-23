"""The HTTP contract with the engine.

Stage 1 exists because a failed analysis used to answer 200 OK with a HOLD, which the
engine cannot tell apart from a real decision. These tests pin the honest answers, and
they pin them now, before team.py is rewritten in stages 2-3.
"""

from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient
from pydantic import SecretStr

from app.api.security import HEADER as API_KEY_HEADER
from app.application.errors import (
    AgentChainFailed,
    AgentResponseInvalid,
    LlmFailed,
    LlmTimeout,
    LlmUnreachable,
)
from app.dependencies import get_resources
from app.domain.models import ActionEnum, InvestmentProposal
from app.main import app
from app.observability.correlation import HEADER
from app.settings import get_settings

A_PROPOSAL = InvestmentProposal(
    ticker="AAPL", action=ActionEnum.BUY, amount_usd=500.0, confidence=0.8, reasoning="because"
)

# Stands for anything an exception message might carry that a caller has no business
# seeing: hostnames, roles, ports, query fragments.
INTERNAL_DETAIL = "connect failed for agent_svc at 127.0.0.1:5432"

API_KEY = "a-test-key-of-some-length"


@pytest.fixture
def client(monkeypatch):
    """A client whose analysis step does whatever the test says, with no LLM behind it."""

    def build(analysis):
        async def run(ticker, models):
            return analysis(ticker)

        monkeypatch.setattr("app.api.routes.run_agent_analysis", run)
        app.dependency_overrides[get_settings] = lambda: SimpleNamespace(
            agent_api_key=SecretStr(API_KEY)
        )
        app.dependency_overrides[get_resources] = lambda: SimpleNamespace(
            models=None,
            pipeline=None,
            memory=SimpleNamespace(ping=AsyncMock(side_effect=OSError("no database"))),
            http_client=SimpleNamespace(get=AsyncMock(side_effect=OSError("no llm"))),
            llm_base_url="http://127.0.0.1:11434/v1",
        )
        # No `with`, so the lifespan does not run and no database is needed.
        return TestClient(app, raise_server_exceptions=False, headers={API_KEY_HEADER: API_KEY})

    yield build
    app.dependency_overrides.clear()


def raises(error):
    def analysis(ticker):
        raise error

    return analysis


def test_a_decision_is_returned_as_200(client):
    response = client(lambda ticker: A_PROPOSAL).post("/analyze/AAPL")

    assert response.status_code == 200
    assert response.json()["action"] == "BUY"


def test_a_hold_is_still_a_decision(client):
    hold = A_PROPOSAL.model_copy(update={"action": ActionEnum.HOLD, "amount_usd": 0.0})

    response = client(lambda ticker: hold).post("/analyze/AAPL")

    assert response.status_code == 200
    assert response.json()["action"] == "HOLD"


@pytest.mark.parametrize(
    ("error", "status", "error_code"),
    [
        (LlmUnreachable("down"), 503, "llm_unreachable"),
        (LlmTimeout("slow"), 504, "llm_timeout"),
        (LlmFailed("500"), 502, "llm_failed"),
        (AgentResponseInvalid("nonsense"), 502, "agent_response_invalid"),
        (AgentChainFailed("tool blew up"), 502, "agent_chain_failed"),
    ],
)
def test_a_failed_analysis_is_not_a_decision(client, error, status, error_code):
    # The whole point of stage 1: none of these may look like a HOLD.
    response = client(raises(error)).post("/analyze/AAPL")

    assert response.status_code == status
    assert response.json()["error_code"] == error_code
    assert "action" not in response.json()


def test_an_unexpected_failure_is_a_500_and_says_nothing_else(client):
    response = client(raises(RuntimeError(INTERNAL_DETAIL))).post("/analyze/AAPL")

    assert response.status_code == 500
    assert response.json()["error_code"] == "internal_error"
    assert INTERNAL_DETAIL not in response.text


def test_an_error_body_never_repeats_the_exception_message(client):
    response = client(raises(LlmFailed(INTERNAL_DETAIL))).post("/analyze/AAPL")

    assert INTERNAL_DETAIL not in response.text
    assert set(response.json()) == {"error_code", "correlation_id"}


@pytest.mark.parametrize("ticker", ["..", "A" * 11, "1AAPL", "AA PL", "AA$PL", ""])
def test_a_ticker_that_is_not_a_ticker_is_rejected(client, ticker):
    # Cheaper than asking a model about it. Note that a query string such as "AAPL?x=1"
    # is not this service's problem: the path is then /analyze/AAPL and x=1 is a query
    # parameter. Escaping a ticker into a URL is the engine's side of that.
    response = client(lambda t: A_PROPOSAL).post(f"/analyze/{ticker}")

    assert response.status_code in (404, 422)
    if response.status_code == 422:
        assert response.json()["error_code"] == "invalid_request"


def test_a_supplied_correlation_id_is_kept(client):
    response = client(lambda ticker: A_PROPOSAL).post(
        "/analyze/AAPL", headers={HEADER: "engine-cycle-42"}
    )

    assert response.headers[HEADER] == "engine-cycle-42"


def test_a_correlation_id_is_invented_when_none_is_supplied(client):
    response = client(lambda ticker: A_PROPOSAL).post("/analyze/AAPL")

    assert len(response.headers[HEADER]) == 36


def test_a_correlation_id_that_could_forge_a_log_line_is_replaced(client):
    response = client(lambda ticker: A_PROPOSAL).post(
        "/analyze/AAPL", headers={HEADER: 'x", "level": "INFO\nfake'}
    )

    assert response.headers[HEADER] != 'x", "level": "INFO\nfake'


def test_an_error_body_carries_the_same_correlation_id_as_the_header(client):
    response = client(raises(LlmTimeout("slow"))).post(
        "/analyze/AAPL", headers={HEADER: "engine-cycle-7"}
    )

    assert response.json()["correlation_id"] == "engine-cycle-7"
    assert response.headers[HEADER] == "engine-cycle-7"


def test_health_checks_nothing_but_the_process(client):
    response = client(lambda ticker: A_PROPOSAL).get("/health")

    assert response.status_code == 200
    assert response.json()["status"] == "alive"


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
def test_an_analysis_needs_the_api_key(client, supplied):
    # /analyze starts an LLM run, so an unauthenticated one lets anything that can reach
    # the port spend the machine's time.
    headers = {} if supplied is None else {API_KEY_HEADER: supplied}
    built = client(lambda ticker: A_PROPOSAL)

    response = built.post("/analyze/AAPL", headers={**{API_KEY_HEADER: ""}, **headers})

    assert response.status_code == 401
    assert response.json()["error_code"] == "unauthorized"


def test_a_rejection_does_not_say_whether_the_key_was_missing_or_wrong(client):
    built = client(lambda ticker: A_PROPOSAL)

    missing = built.post("/analyze/AAPL", headers={API_KEY_HEADER: ""})
    wrong = built.post("/analyze/AAPL", headers={API_KEY_HEADER: "wrong"})

    assert missing.json()["error_code"] == wrong.json()["error_code"]
    assert missing.status_code == wrong.status_code == 401


def test_the_right_key_is_let_through(client):
    response = client(lambda ticker: A_PROPOSAL).post(
        "/analyze/AAPL", headers={API_KEY_HEADER: API_KEY}
    )

    assert response.status_code == 200


def test_liveness_needs_no_key(client):
    response = client(lambda ticker: A_PROPOSAL).get("/health", headers={API_KEY_HEADER: ""})

    assert response.status_code == 200


def test_readiness_needs_no_key(client):
    # A load balancer has to be able to ask whether the service is up. Both dependencies
    # are stubbed as broken here, so a 503 proves the probe ran rather than being rejected.
    response = client(lambda ticker: A_PROPOSAL).get("/ready", headers={API_KEY_HEADER: ""})

    assert response.status_code == 503
    assert response.json()["checks"] == {"database": "unavailable", "llm": "unavailable"}
