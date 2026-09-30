"""OpenAPI/Swagger stay off unless a developer opts in.

They are registered on the app itself, outside the authenticated router, so leaving
them on hands anyone who can reach the port a map of every endpoint. The default is
therefore off; TAS_ENABLE_DOCS (or create_app(enable_docs=True)) turns them back on
for local exploration only.
"""

from fastapi.testclient import TestClient

from app.main import create_app


def test_docs_are_disabled_by_default():
    client = TestClient(create_app(enable_docs=False), raise_server_exceptions=False)

    assert client.get("/docs").status_code == 404
    assert client.get("/redoc").status_code == 404
    assert client.get("/openapi.json").status_code == 404


def test_docs_can_be_enabled_for_local_exploration():
    client = TestClient(create_app(enable_docs=True), raise_server_exceptions=False)

    assert client.get("/docs").status_code == 200
    assert client.get("/redoc").status_code == 200
    assert client.get("/openapi.json").status_code == 200
    assert client.get("/openapi.json").json()["info"]["title"] == "Trading Agent Service"
