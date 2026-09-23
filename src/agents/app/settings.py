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

    # What a caller has to present to start an analysis. Without it the machine runs an
    # unauthenticated endpoint that spends LLM time for anyone who can reach the port.
    agent_api_key: SecretStr

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

    llm: LlmSettings


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    """The settings, parsed once. The lifespan builds every client from this."""
    return Settings()
