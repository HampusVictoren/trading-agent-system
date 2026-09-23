"""The factory turns one ModelSpec into the AG2 configuration for its provider.

The four configurations do not take the same arguments, which is why this is more than a
lookup. mypy checks each call against the real dataclass even while an extra is missing;
these tests cover what mypy cannot - the values that actually arrive, and what happens
when the extra is genuinely absent.
"""

import logging

import pytest
from ag2.config import ModelProvider, OpenAIConfig

from app.infrastructure.ag2.team import ROLES as KNOWN_ROLES
from app.infrastructure.llm.provider import (
    CLIENT_RETRIES,
    build_model_config,
    build_model_configs,
)
from app.settings import LlmSettings, ModelSpec, Provider

# Installed here. Everything else in Provider is a placeholder until its extra is added.
RUNNABLE = {Provider.OPENAI_COMPATIBLE, Provider.OPENAI}
PLACEHOLDER = sorted(set(Provider) - RUNNABLE)


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


class TestTheOpenAiFamily:
    def test_an_openai_compatible_spec_becomes_an_openai_config(self):
        config = build_model_config(a_spec(seed=7))

        assert isinstance(config, OpenAIConfig)
        assert config.provider is ModelProvider.OPENAI
        assert config.model == "llama3.2"
        assert config.base_url == "http://127.0.0.1:11434/v1"
        assert config.temperature == 0.2
        assert config.seed == 7
        assert config.timeout == 30.0

    def test_openai_itself_needs_no_base_url(self):
        config = build_model_config(a_spec(provider=Provider.OPENAI, base_url=None))

        assert isinstance(config, OpenAIConfig)
        assert config.base_url is None

    def test_the_clients_own_retries_are_switched_off(self):
        # Two client retries under AG2's two schema retries is up to nine HTTP calls for
        # one decision, and it turns a hung backend into three timeouts in a row.
        # Retrying belongs in the engine, in one place.
        assert CLIENT_RETRIES == 0
        assert build_model_config(a_spec()).max_retries == 0

    def test_the_secret_is_unwrapped_exactly_once(self):
        # SecretStr reaching the client as an object would authenticate as the literal
        # "**********", which fails in a way that says nothing about the cause.
        assert build_model_config(a_spec()).api_key == "placeholder"


class TestAProviderWhoseExtraIsMissing:
    @pytest.mark.parametrize("provider", PLACEHOLDER, ids=lambda p: p.value)
    def test_it_fails_with_an_install_hint_rather_than_silently(self, provider):
        # AG2 exports a placeholder for every provider whose extra is absent, and
        # constructing one raises this. Because configurations are built in the lifespan,
        # it lands as a startup failure that says what to install - which is the "lazy
        # import per branch" the roadmap asked for, already provided by the library.
        spec = a_spec(provider=provider, base_url=None)

        with pytest.raises(ImportError, match="ag2\\["):
            build_model_config(spec)

    @pytest.mark.parametrize("provider", PLACEHOLDER, ids=lambda p: p.value)
    def test_the_branch_exists_at_all(self, provider):
        # A provider with no branch would fall out of the match and return None, which
        # would surface much later as an attribute error on a NoneType config.
        with pytest.raises(ImportError):
            build_model_config(a_spec(provider=provider, base_url=None))


class TestOllamaLosesTheTimeout:
    def test_it_says_so_at_startup(self, caplog):
        # AG2's native Ollama client takes no timeout, and the timeout is what makes a 504
        # reachable at all. Rather than discover that during an incident, the factory says
        # it out loud when the configuration is built - which is at startup.
        with caplog.at_level(logging.WARNING), pytest.raises(ImportError):
            build_model_config(a_spec(provider=Provider.OLLAMA, base_url=None))

        assert "does not carry timeout_s" in caplog.text
        assert "openai_compatible" in caplog.text

    def test_the_openai_compatible_route_does_carry_it(self):
        # The supported way to reach Ollama, and the one this project runs on.
        assert build_model_config(a_spec()).timeout == 30.0


class TestRoleOverridesAreCheckedAgainstRealRoles:
    """The check that makes a per-role override safe to use."""

    def roles(self, **overrides) -> LlmSettings:
        return LlmSettings(default=a_spec(), roles=overrides)

    def test_a_mistyped_role_stops_the_service(self):
        # Without this the service starts, runs the default model, and the only symptom is
        # an answer that is not the one that was configured. LlmSettings.for_role cannot
        # catch it - it has no way to know which roles exist - so the check lives here.
        with pytest.raises(ValueError, match="portfilio_manager"):
            build_model_configs(self.roles(portfilio_manager=a_spec()), KNOWN_ROLES)

    def test_the_error_lists_the_roles_that_do_exist(self):
        # A message that only says "unknown" leaves the reader guessing at the spelling.
        with pytest.raises(ValueError, match="market_analyst"):
            build_model_configs(self.roles(analyst=a_spec()), KNOWN_ROLES)

    def test_a_real_role_is_built(self):
        configs = build_model_configs(
            self.roles(portfolio_manager=a_spec(model="qwen3")), KNOWN_ROLES
        )

        assert configs.for_role("portfolio_manager").model == "qwen3"

    def test_a_role_without_an_override_gets_the_default(self):
        configs = build_model_configs(
            self.roles(portfolio_manager=a_spec(model="qwen3")), KNOWN_ROLES
        )

        assert configs.for_role("market_analyst").model == "llama3.2"

    def test_no_overrides_is_fine(self):
        configs = build_model_configs(self.roles(), KNOWN_ROLES)

        assert configs.by_role == {}
        assert configs.for_role("risk_manager") is configs.default

    def test_every_configured_model_is_built_at_startup(self):
        # Building them here rather than per request is what turns a missing extra into a
        # startup failure. anthropic has no extra installed, so this is what it looks like.
        with pytest.raises(ImportError, match="ag2\\["):
            build_model_configs(
                self.roles(portfolio_manager=a_spec(provider=Provider.ANTHROPIC, base_url=None)),
                KNOWN_ROLES,
            )

    def test_the_teams_roles_are_what_gets_checked(self):
        # ROLES is passed in rather than imported by the factory, so the rule does not
        # depend on where roles come from. Today it is the team module; once teams are
        # data it is TeamSpec, and nothing in provider.py changes.
        assert set(KNOWN_ROLES) == {"market_analyst", "risk_manager", "portfolio_manager"}
