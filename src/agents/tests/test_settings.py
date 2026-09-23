"""Settings are the service's contract with its environment.

A wrong value here is worse than a crash: a missing base URL used to mean "call OpenAI's
cloud API with your key", and a missing DATABASE_URL used to mean "connect as the wrong
role". These tests pin the rule that every value must be supplied, so a typo stops the
service at startup rather than changing what it does.
"""

import pytest
from pydantic import ValidationError

from app.settings import LlmSettings, ModelSpec, Provider, Settings, get_settings

ENVIRONMENT = {
    "TAS_DATABASE_URL": "postgresql://agent_svc:placeholder@127.0.0.1:5432/tradingdb",
    "TAS_AGENT_API_KEY": "placeholder-agent-key",
    "TAS_EMBEDDINGS_BASE_URL": "http://127.0.0.1:11434/v1",
    "TAS_EMBEDDINGS_API_KEY": "placeholder-embeddings",
    "TAS_MARKET_DATA_TIMEOUT_S": "30",
    "TAS_MARKET_DATA_TTL_S": "300",
    "TAS_LLM__DEFAULT__PROVIDER": "openai_compatible",
    "TAS_LLM__DEFAULT__MODEL": "llama3.2",
    "TAS_LLM__DEFAULT__BASE_URL": "http://127.0.0.1:11434/v1",
    "TAS_LLM__DEFAULT__API_KEY": "placeholder-llm",
    "TAS_LLM__DEFAULT__TEMPERATURE": "0.2",
    "TAS_LLM__DEFAULT__TIMEOUT_S": "30",
}

# What the error must name when that key is missing. The env var and the field path are
# not the same string, and a test that matched the wrong one would pass on any failure.
NAMED_IN_THE_ERROR = {
    "TAS_DATABASE_URL": "database_url",
    "TAS_AGENT_API_KEY": "agent_api_key",
    "TAS_EMBEDDINGS_BASE_URL": "embeddings_base_url",
    "TAS_EMBEDDINGS_API_KEY": "embeddings_api_key",
    "TAS_MARKET_DATA_TIMEOUT_S": "market_data_timeout_s",
    "TAS_MARKET_DATA_TTL_S": "market_data_ttl_s",
    "TAS_LLM__DEFAULT__PROVIDER": "provider",
    "TAS_LLM__DEFAULT__MODEL": "model",
    "TAS_LLM__DEFAULT__BASE_URL": "base_url",
    "TAS_LLM__DEFAULT__API_KEY": "api_key",
    "TAS_LLM__DEFAULT__TEMPERATURE": "temperature",
    "TAS_LLM__DEFAULT__TIMEOUT_S": "timeout_s",
}


@pytest.fixture
def environment(monkeypatch):
    """Applies a complete environment, minus whatever the caller asks to leave out.
    The developer's own shell may have these set, so every key is cleared first."""

    def apply(without: str | None = None, **extra: str):
        for key in ENVIRONMENT:
            monkeypatch.delenv(key, raising=False)
        for key, value in (ENVIRONMENT | extra).items():
            if key != without:
                monkeypatch.setenv(key, value)

    return apply


def a_settings() -> Settings:
    # _env_file=None keeps the developer's own src/agents/.env out of the test.
    return Settings(_env_file=None)


def a_spec(**overrides) -> ModelSpec:
    defaults = {
        "provider": Provider.OPENAI_COMPATIBLE,
        "model": "llama3.2",
        "base_url": "http://127.0.0.1:11434/v1",
        "api_key": "placeholder",
        "temperature": 0.2,
        "timeout_s": 30.0,
    }
    return ModelSpec.model_validate(defaults | overrides)


def test_every_value_is_read_from_the_environment(environment):
    environment()

    settings = a_settings()

    assert settings.database_url.get_secret_value() == ENVIRONMENT["TAS_DATABASE_URL"]
    assert str(settings.embeddings_base_url) == ENVIRONMENT["TAS_EMBEDDINGS_BASE_URL"]
    assert settings.llm.default.provider is Provider.OPENAI_COMPATIBLE
    assert settings.llm.default.model == "llama3.2"
    assert settings.llm.default.temperature == 0.2
    assert settings.llm.default.timeout_s == 30.0
    assert settings.llm.default.api_key.get_secret_value() == "placeholder-llm"


@pytest.mark.parametrize("missing", sorted(ENVIRONMENT))
def test_a_missing_value_is_rejected(environment, missing):
    # No setting has a default. A silent fallback here reaches a paid API or the wrong
    # database role, so an incomplete environment must fail loudly instead.
    environment(without=missing)

    with pytest.raises(ValidationError, match=NAMED_IN_THE_ERROR[missing]):
        a_settings()


def test_an_unprefixed_variable_does_not_reach_the_settings(environment, monkeypatch):
    # The entire reason for TAS_. OPENAI_API_KEY is what openai's own SDK reads, and a
    # developer may well have a real cloud key in their shell for something else. A shell
    # value beats the .env file, so without the prefix that key would quietly become this
    # service's - and the bill would arrive later.
    environment()
    monkeypatch.setenv("OPENAI_API_KEY", "sk-a-real-key-from-another-project")
    monkeypatch.setenv("LLM_MODEL", "gpt-4o")

    settings = a_settings()

    assert settings.llm.default.api_key.get_secret_value() == "placeholder-llm"
    assert settings.llm.default.model == "llama3.2"


@pytest.mark.parametrize("key", ["TAS_EMBEDDINGS_BASE_URL", "TAS_LLM__DEFAULT__BASE_URL"])
def test_a_malformed_url_is_rejected(environment, monkeypatch, key):
    # The engine validates its own base URL the same way. A typo should not survive
    # until the first request.
    environment()
    monkeypatch.setenv(key, "127.0.0.1:11434")

    with pytest.raises(ValidationError, match="url"):
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


class TestAModelSpecHasToFitItsProvider:
    def test_openai_compatible_without_a_base_url_is_refused(self):
        # Refused rather than allowed to default, because the default destination is
        # api.openai.com and the key is real. This is the exact bug the prefix guards
        # against, one level down.
        with pytest.raises(ValidationError, match="base_url"):
            a_spec(provider=Provider.OPENAI_COMPATIBLE, base_url=None)

    def test_another_provider_may_leave_the_base_url_out(self):
        assert a_spec(provider=Provider.ANTHROPIC, base_url=None).base_url is None

    def test_a_seed_is_refused_where_it_would_be_ignored(self):
        # A seed that is quietly dropped looks like reproducibility and delivers none,
        # which matters because stage 4 compares runs.
        with pytest.raises(ValidationError, match="seed"):
            a_spec(provider=Provider.ANTHROPIC, base_url=None, seed=42)

    def test_a_seed_is_accepted_where_it_works(self):
        assert a_spec(seed=42).seed == 42

    def test_a_field_the_spec_does_not_have_is_refused(self):
        with pytest.raises(ValidationError):
            a_spec(max_tokens=100)

    @pytest.mark.parametrize("temperature", [-0.1, 2.1])
    def test_a_temperature_outside_the_range_is_refused(self, temperature):
        with pytest.raises(ValidationError):
            a_spec(temperature=temperature)

    def test_a_timeout_of_zero_is_refused(self):
        with pytest.raises(ValidationError):
            a_spec(timeout_s=0)


class TestPerRoleOverrides:
    def test_a_role_is_read_from_a_nested_variable(self, environment, monkeypatch):
        environment()
        monkeypatch.setenv("TAS_LLM__ROLES__PORTFOLIO_MANAGER__PROVIDER", "anthropic")
        monkeypatch.setenv("TAS_LLM__ROLES__PORTFOLIO_MANAGER__MODEL", "claude-opus-5")
        monkeypatch.setenv("TAS_LLM__ROLES__PORTFOLIO_MANAGER__API_KEY", "placeholder")
        monkeypatch.setenv("TAS_LLM__ROLES__PORTFOLIO_MANAGER__TEMPERATURE", "0.0")
        monkeypatch.setenv("TAS_LLM__ROLES__PORTFOLIO_MANAGER__TIMEOUT_S", "60")

        llm = a_settings().llm

        # Lowercased by pydantic-settings, which is the same spelling a role has in a team.
        assert list(llm.roles) == ["portfolio_manager"]
        assert llm.for_role("portfolio_manager").model == "claude-opus-5"

    def test_a_role_without_an_override_gets_the_default(self, environment):
        environment()

        assert a_settings().llm.for_role("market_analyst").model == "llama3.2"

    def test_an_unknown_role_silently_gets_the_default(self):
        # Recorded rather than fixed here: for_role cannot know which roles exist. It is
        # why app/infrastructure/llm/provider.py refuses a key no team uses, because
        # otherwise a mistyped role name reads as "the override did not take effect".
        llm = LlmSettings(default=a_spec(), roles={"portfilio_manager": a_spec(model="other")})

        assert llm.for_role("portfolio_manager").model == "llama3.2"

    def test_no_overrides_is_a_legitimate_answer(self):
        assert LlmSettings(default=a_spec()).roles == {}
