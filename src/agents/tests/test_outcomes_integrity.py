"""POST /v1/outcomes refuses unsigned or forged measurements."""

from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient

from app.api.outcomes_integrity import SIGNATURE_HEADER, sign_outcomes_body
from app.api.security import HEADER as API_KEY_HEADER
from app.dependencies import get_resources
from app.main import app
from app.settings import get_settings
from tests.api_support import API_KEY, OUTCOMES_HMAC_SECRET, api_settings

EXAMPLES = Path(__file__).resolve().parents[3] / "contracts" / "examples"


@pytest.fixture(autouse=True)
def _clear():
    yield
    app.dependency_overrides.clear()


@pytest.fixture
def outcomes():
    store = AsyncMock()
    app.dependency_overrides[get_settings] = lambda: api_settings()
    app.dependency_overrides[get_resources] = lambda: SimpleNamespace(outcomes=store)
    yield TestClient(app, raise_server_exceptions=False), store


def _body() -> bytes:
    return (EXAMPLES / "outcomes-measured.json").read_bytes()


def test_a_missing_signature_is_refused(outcomes):
    client, store = outcomes
    response = client.post(
        "/v1/outcomes",
        content=_body(),
        headers={API_KEY_HEADER: API_KEY, "Content-Type": "application/json"},
    )
    assert response.status_code == 401
    assert response.json()["error_code"] == "unauthorized"
    store.store.assert_not_awaited()


def test_a_wrong_signature_is_refused(outcomes):
    client, store = outcomes
    response = client.post(
        "/v1/outcomes",
        content=_body(),
        headers={
            API_KEY_HEADER: API_KEY,
            SIGNATURE_HEADER: "sha256=" + ("ab" * 32),
            "Content-Type": "application/json",
        },
    )
    assert response.status_code == 401
    store.store.assert_not_awaited()


def test_a_valid_signature_is_accepted(outcomes):
    client, store = outcomes
    body = _body()
    response = client.post(
        "/v1/outcomes",
        content=body,
        headers={
            API_KEY_HEADER: API_KEY,
            SIGNATURE_HEADER: sign_outcomes_body(OUTCOMES_HMAC_SECRET, body),
            "Content-Type": "application/json",
        },
    )
    assert response.status_code == 200
    assert store.store.await_count == 1


def test_sign_helper_is_stable():
    body = b'{"outcomes":[]}'
    assert sign_outcomes_body("secret", body) == sign_outcomes_body("secret", body)
    assert sign_outcomes_body("secret", body).startswith("sha256=")
