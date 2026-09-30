""" /ready stays open but does not advertise dependency detail by default."""

from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient

from app.dependencies import get_resources
from app.main import app
from app.settings import get_settings
from tests.api_support import api_settings


@pytest.fixture(autouse=True)
def _clear():
    yield
    app.dependency_overrides.clear()


def _client(**overrides):
    app.dependency_overrides[get_settings] = lambda: api_settings(**overrides)
    app.dependency_overrides[get_resources] = lambda: SimpleNamespace(
        memory=SimpleNamespace(ping=AsyncMock(side_effect=OSError("no database"))),
        http_client=SimpleNamespace(get=AsyncMock(side_effect=OSError("no llm"))),
        llm_base_url="http://127.0.0.1:11434/v1",
        pipeline=None,
        models=None,
    )
    return TestClient(app, raise_server_exceptions=False)


def test_ready_omits_checks_by_default():
    response = _client().get("/ready")
    assert response.status_code == 503
    assert response.json() == {"status": "not ready"}


def test_ready_can_include_checks_when_detail_is_on():
    response = _client(ready_detail=True).get("/ready")
    assert response.status_code == 503
    assert response.json()["checks"] == {"database": "unavailable", "llm": "unavailable"}


def test_health_stays_a_bare_liveness_probe():
    response = _client().get("/health")
    assert response.status_code == 200
    assert response.json()["status"] == "alive"
