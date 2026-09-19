import os

from ag2.config import OpenAIConfig


def get_llm_config():
    """Returnerar konfiguration för AG2 v1.0+."""
    api_key = os.getenv("OPENAI_API_KEY", "dummy-key")
    model = os.getenv("LLM_MODEL", "gpt-4o-mini")
    base_url = os.getenv("LLM_BASE_URL", None)

    kwargs = {"model": model, "api_key": api_key}
    if base_url:
        kwargs["base_url"] = base_url

    return OpenAIConfig(**kwargs)
