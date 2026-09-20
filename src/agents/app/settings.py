"""Typed configuration for the agent service.

Every value is required. The engine learned the same lesson in PR #14: a setting with a
default is a setting that can be wrong without anyone noticing. Here the silent defaults
were dangerous rather than merely wrong - a missing LLM_BASE_URL meant "call OpenAI's
cloud API with your key", and a missing LLM_MODEL meant "use gpt-4o-mini". An incomplete
environment now stops the service at startup instead.

Settings are read lazily through get_settings(), never at import time, so that importing
a module does not depend on a .env file being present.
"""

from functools import lru_cache
from pathlib import Path

from pydantic import HttpUrl, SecretStr
from pydantic_settings import BaseSettings, SettingsConfigDict

ENV_FILE = Path(__file__).resolve().parent.parent / ".env"


class Settings(BaseSettings):
    """The service's contract with its environment. See .env.example for the keys."""

    model_config = SettingsConfigDict(
        env_file=ENV_FILE,
        env_file_encoding="utf-8",
        # Only the fields below are read from the environment; everything else in it
        # (PATH and the rest) is none of this service's business.
        extra="ignore",
    )

    # Connects as agent_svc and therefore carries a password. SecretStr keeps it out of
    # a repr, a log line or an exception message that happens to include the settings.
    database_url: SecretStr

    # Any placeholder works while LLM_BASE_URL points at Ollama, but it is still a key.
    openai_api_key: SecretStr

    # What a caller has to present to start an analysis. Without it the machine runs an
    # unauthenticated endpoint that spends LLM time for anyone who can reach the port.
    agent_api_key: SecretStr

    # HttpUrl rejects a malformed URL at startup, the same way the engine's
    # AgentServiceOptions.BaseUrl does. Note that it appends a trailing slash to a bare
    # host, so give these a path, as .env.example does.
    llm_base_url: HttpUrl
    ollama_base_url: HttpUrl

    llm_model: str

    # Without this the openai client waits 600 s to read a response, so a hung backend
    # would hold a request for ten minutes and a 504 would be unreachable in practice.
    llm_timeout_seconds: float


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    """The settings, parsed once. The lifespan builds every client from this."""
    return Settings()
