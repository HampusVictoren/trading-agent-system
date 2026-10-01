"""Typed configuration for the agent service.

Every value is required. The engine learned the same lesson in PR #14: a setting with a
default is a setting that can be wrong without anyone noticing. Here the silent defaults
were dangerous rather than merely wrong - a missing base URL meant "call OpenAI's cloud
API with your key", and a missing model meant "use gpt-4o-mini". An incomplete
environment stops the service at startup instead.

That rule is why ModelSpec has no defaults either, although the roadmap's sketch of it
did. A default model is the same class of bug one level down: with per-role overrides, a
mistyped role name falls back to the default and the service runs a model nobody chose.

Every variable is prefixed TAS_. Without it, OPENAI_API_KEY - which openai's own SDK
reads, and which a developer may well have in their shell for something else - silently
becomes this service's key, because a shell value beats the .env file.

Settings are read lazily through get_settings(), never at import time, so that importing
a module does not depend on a .env file being present.
"""

from enum import StrEnum
from functools import lru_cache
from pathlib import Path
from typing import Annotated, Self

from pydantic import BaseModel, ConfigDict, Field, HttpUrl, SecretStr, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

ENV_FILE = Path(__file__).resolve().parent.parent / ".env"


class Provider(StrEnum):
    """Which AG2 model configuration to build. The value is what goes in the environment."""

    # Anything speaking OpenAI's wire format at a base URL of its own: Ollama's /v1,
    # Grok's /v1, LM Studio. This is what the project runs on today.
    OPENAI_COMPATIBLE = "openai_compatible"
    OPENAI = "openai"
    ANTHROPIC = "anthropic"
    OLLAMA = "ollama"
    XAI = "xai"


# Read off the real dataclasses, not guessed: AnthropicConfig has no seed field at all,
# while OpenAIConfig, XAIConfig and OllamaConfig do. A seed that is silently dropped looks
# like reproducibility and delivers none, so it is refused rather than ignored.
SEEDABLE = frozenset(set(Provider) - {Provider.ANTHROPIC})


class ModelSpec(BaseModel):
    """One model, fully specified. No field has a default; see the module docstring."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    provider: Provider
    model: Annotated[str, Field(min_length=1)]

    # The two exceptions to "no defaults", and both are real choices rather than absences:
    # "this provider has its own endpoint" and "do not pin a seed". The validator below is
    # what stops base_url going missing where it actually matters.
    base_url: HttpUrl | None = None
    seed: int | None = None

    api_key: SecretStr

    # Explicit rather than defaulted, because it changes what the model answers and stage
    # 4 compares outcomes across runs. A silent 0.2 would be an uncontrolled variable.
    temperature: Annotated[float, Field(ge=0.0, le=2.0)]

    # Without this the openai client waits 600 s to read a response, so a hung backend
    # would hold a request for ten minutes and a 504 would be unreachable in practice.
    timeout_s: Annotated[float, Field(gt=0)]

    @model_validator(mode="after")
    def fields_fit_the_provider(self) -> Self:
        if self.provider is Provider.OPENAI_COMPATIBLE and self.base_url is None:
            raise ValueError(
                "provider 'openai_compatible' requires base_url - that is what makes it "
                "compatible with something. Without it the calls go to api.openai.com."
            )
        if self.seed is not None and self.provider not in SEEDABLE:
            raise ValueError(
                f"seed is not supported by provider '{self.provider}', and a seed that is "
                f"quietly ignored looks like reproducibility without being it."
            )
        return self


class LlmSettings(BaseModel):
    """The default model, and any per-role override of it."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    default: ModelSpec

    # A role is just the name of a step in a team, so there are no fixed fields here: a new
    # agent is a new step and a new prompt file, not a new setting. Empty is a real answer
    # ("no overrides"), which is why this one may default. app/infrastructure/llm/provider
    # refuses a key no team uses, so a mistyped role cannot fall back to the default.
    roles: dict[str, ModelSpec] = {}

    def for_role(self, role: str) -> ModelSpec:
        return self.roles.get(role, self.default)


class Settings(BaseSettings):
    """The service's contract with its environment. See .env.example for the keys."""

    model_config = SettingsConfigDict(
        env_file=ENV_FILE,
        env_file_encoding="utf-8",
        env_prefix="TAS_",
        # TAS_LLM__DEFAULT__MODEL reaches Settings.llm.default.model. Nested keys arrive
        # lowercased, which is the same spelling a role has in a team.
        env_nested_delimiter="__",
        # Only the fields below are read from the environment; everything else in it
        # (PATH and the rest) is none of this service's business.
        extra="ignore",
    )

    # Connects as agent_svc and therefore carries a password. SecretStr keeps it out of
    # a repr, a log line or an exception message that happens to include the settings.
    database_url: SecretStr

    # Legacy full-access key. Still required so existing deployments keep working; it
    # grants every scope. Prefer the scoped keys below once both sides have rotated.
    # Deprecated for new deployments - see SECURITY.md and .env.example.
    agent_api_key: SecretStr

    # Optional single-scope keys. When set, each grants only that scope. A request
    # presenting one of these cannot reach an endpoint outside its scope even though
    # the legacy key still can. Generate each with secrets.token_urlsafe(32).
    agent_api_key_signals: SecretStr | None = None
    agent_api_key_screen: SecretStr | None = None
    agent_api_key_outcomes: SecretStr | None = None
    agent_api_key_market: SecretStr | None = None

    # Separate from the API key on purpose: a stolen key alone must not be enough to forge
    # measurements into agent memory. The engine HMAC-SHA256-signs the raw POST body and
    # sends the digest as X-Outcomes-Signature; the agent refuses unsigned or mismatched
    # bodies. Generate with secrets.token_urlsafe(32).
    outcomes_hmac_secret: SecretStr

    # Embeddings are a separate concern from the team's models: the model is pinned in
    # memory.py because changing it means changing the column width, so only the endpoint
    # and the key are configurable. Named for the job rather than for Ollama, so the name
    # still fits if the endpoint moves.
    embeddings_base_url: HttpUrl
    embeddings_api_key: SecretStr

    # Market data. The timeout bounds one yfinance call, which is synchronous network I/O
    # run in a thread; the TTL is how long a quote may be reused within a cycle.
    market_data_timeout_s: Annotated[float, Field(gt=0)]
    market_data_ttl_s: Annotated[float, Field(ge=0)]

    # Screening. Its own pair rather than reusing the two above, because the calls are not
    # comparable: one is a quote for one instrument, the other is six months of bars for up
    # to a hundred. A timeout sized for the first would fail every screen, and a TTL sized
    # for a quote would refetch a universe that only changes once a trading day.
    screen_timeout_s: Annotated[float, Field(gt=0)]
    screen_ttl_s: Annotated[float, Field(ge=0)]

    # OpenAPI/Swagger surfaces. Default False: they sit outside the authenticated router,
    # so leaving them on is a free map of the attack surface. Opt in with TAS_ENABLE_DOCS
    # for local exploration only; create_app reads the same flag at process start.
    enable_docs: bool = False

    # What the service binds. Through `python -m app` - which is what the container runs -
    # this is the real listen address, because that entrypoint passes it to uvicorn. Start
    # uvicorn by hand and it goes back to being an operator-declared hint, since the
    # service cannot see from inside FastAPI what the command line asked for. Either way it
    # is what the startup warning reads. Prefer 127.0.0.1 for local paper trading; set
    # TAS_BIND_HOST=0.0.0.0 deliberately for Docker/compose and accept the warning in
    # non-development environments.
    bind_host: str = "127.0.0.1"

    # "development" skips the non-loopback bind warning so intentional docker setups are
    # not noisy. Set TAS_ENVIRONMENT=production (or staging) to surface the warning.
    environment: str = "development"

    # The port `python -m app` binds. Defaulted for the same reason bind_host is: it is a
    # claim about a socket rather than a secret or a model choice. 8000 is what the engine's
    # AgentService:BaseUrl expects, and inside a container it stays 8000 because compose
    # maps it - so this setting is for running a second instance beside the first.
    port: Annotated[int, Field(ge=1, le=65535)] = 8000

    # When False (default), /ready returns only {"status": "ready"|"not ready"}.
    # Dependency names (database/llm) stay in server logs. Set TAS_READY_DETAIL=true
    # for local debugging when a load balancer is not scraping the probe.
    ready_detail: bool = False

    # Rate limits for the costly endpoints. Defaults are budgets, not guesses about
    # traffic: a paper-trading cycle asking for one signal a minute sits well under them,
    # and a stolen key that tries to drain the LLM still hits a wall. Units are requests
    # per rolling minute; see app/api/rate_limit.py.
    rate_limit_signals_per_minute: Annotated[int, Field(ge=1)] = 10
    rate_limit_signals_global_per_minute: Annotated[int, Field(ge=1)] = 30
    rate_limit_screen_per_minute: Annotated[int, Field(ge=1)] = 30
    rate_limit_screen_global_per_minute: Annotated[int, Field(ge=1)] = 60

    llm: LlmSettings


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    """The settings, parsed once. The lifespan builds every client from this."""
    return Settings()
