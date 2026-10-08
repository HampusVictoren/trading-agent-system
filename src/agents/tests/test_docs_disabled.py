"""OpenAPI/Swagger stay off unless a developer opts in.

They are registered on the app itself, outside the authenticated router, so leaving
them on hands anyone who can reach the port a map of every endpoint. The default is
therefore off; TAS_ENABLE_DOCS - in the shell or in src/agents/.env - or
create_app(enable_docs=True) turns them back on for local exploration only.
"""

from fastapi.testclient import TestClient

from app.main import create_app
from app.settings import DocsSwitch


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


class TestTheSwitchIsReadWhereTheOtherSettingsAre:
    """TAS_ENABLE_DOCS used to be read from os.environ alone, while .env.example told people
    to set it in src/agents/.env - where it did nothing. These pin that the file counts, that
    a shell value still beats it the way it does for every other setting, and that nothing
    set at all means off.
    """

    @staticmethod
    def _env_file(monkeypatch, tmp_path, content: str | None) -> None:
        path = tmp_path / ".env"
        if content is not None:
            path.write_text(content, encoding="utf-8")
        monkeypatch.setitem(DocsSwitch.model_config, "env_file", path)
        monkeypatch.delenv("TAS_ENABLE_DOCS", raising=False)

    def test_the_env_file_turns_the_docs_on(self, monkeypatch, tmp_path):
        self._env_file(monkeypatch, tmp_path, "TAS_ENABLE_DOCS=true\n")

        client = TestClient(create_app(), raise_server_exceptions=False)

        assert client.get("/openapi.json").status_code == 200

    def test_a_shell_value_beats_the_env_file(self, monkeypatch, tmp_path):
        self._env_file(monkeypatch, tmp_path, "TAS_ENABLE_DOCS=true\n")
        monkeypatch.setenv("TAS_ENABLE_DOCS", "false")

        client = TestClient(create_app(), raise_server_exceptions=False)

        assert client.get("/openapi.json").status_code == 404

    def test_nothing_set_anywhere_means_off(self, monkeypatch, tmp_path):
        self._env_file(monkeypatch, tmp_path, None)

        client = TestClient(create_app(), raise_server_exceptions=False)

        assert client.get("/openapi.json").status_code == 404
        assert client.get("/docs").status_code == 404
