"""Settings are the service's contract with its environment. A wrong value here is worse
than a crash: a missing LLM_BASE_URL used to mean "call OpenAI's cloud API with your key",
and a missing DATABASE_URL used to mean "connect as the wrong role". These tests pin the
rule that every value must be supplied, so that a typo stops the service at startup."""

import pytest
from pydantic import ValidationError

from app.settings import Settings, get_settings

ENVIRONMENT = {
    "DATABASE_URL": "postgresql://agent_svc:placeholder@127.0.0.1:5432/tradingdb",
    "OPENAI_API_KEY": "placeholder",
    "LLM_BASE_URL": "http://127.0.0.1:11434/v1",
    "LLM_MODEL": "llama3.2",
    "OLLAMA_BASE_URL": "http://127.0.0.1:11434/v1",
}


@pytest.fixture
def environment(monkeypatch):
    """Applies a complete environment, minus whatever the caller asks to leave out.
    The developer's own shell may have these set, so every key is cleared first."""

    def apply(without: str | None = None):
        for key in ENVIRONMENT:
            monkeypatch.delenv(key, raising=False)
        for key, value in ENVIRONMENT.items():
            if key != without:
                monkeypatch.setenv(key, value)

    return apply


def a_settings() -> Settings:
    # _env_file=None keeps the developer's own src/agents/.env out of the test.
    return Settings(_env_file=None)


def test_every_value_is_read_from_the_environment(environment):
    environment()

    settings = a_settings()

    assert settings.database_url.get_secret_value() == ENVIRONMENT["DATABASE_URL"]
    assert settings.openai_api_key.get_secret_value() == ENVIRONMENT["OPENAI_API_KEY"]
    assert str(settings.llm_base_url) == ENVIRONMENT["LLM_BASE_URL"]
    assert str(settings.ollama_base_url) == ENVIRONMENT["OLLAMA_BASE_URL"]
    assert settings.llm_model == "llama3.2"


@pytest.mark.parametrize("missing", sorted(ENVIRONMENT))
def test_a_missing_value_is_rejected(environment, missing):
    # No setting has a default. A silent fallback here reaches a paid API or the wrong
    # database role, so an incomplete environment must fail loudly instead.
    environment(without=missing)

    with pytest.raises(ValidationError, match=missing.lower()):
        a_settings()


@pytest.mark.parametrize("key", ["LLM_BASE_URL", "OLLAMA_BASE_URL"])
def test_a_malformed_url_is_rejected(environment, monkeypatch, key):
    # The engine validates its own base URL the same way. A typo should not survive
    # until the first request.
    environment()
    monkeypatch.setenv(key, "127.0.0.1:11434")

    with pytest.raises(ValidationError, match=key.lower()):
        a_settings()


def test_secrets_do_not_appear_in_a_repr(environment):
    # Settings end up in logs and exception messages by accident. SecretStr is what
    # keeps the agent_svc password out of them.
    environment()

    rendered = repr(a_settings())

    assert "placeholder" not in rendered
    assert "**********" in rendered


def test_get_settings_returns_the_same_instance(environment):
    # The lifespan builds resources from the settings once; reading them twice must
    # not reparse the environment and hand out a second, possibly different, object.
    environment()
    get_settings.cache_clear()

    assert get_settings() is get_settings()

    get_settings.cache_clear()
