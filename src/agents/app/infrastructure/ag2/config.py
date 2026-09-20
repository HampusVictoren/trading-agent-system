from ag2.config import OpenAIConfig

from app.settings import Settings


def build_llm_config(settings: Settings) -> OpenAIConfig:
    """The AG2 model configuration, built once by the lifespan.

    Stage 3 turns this into a provider factory. Until then everything goes through
    OpenAIConfig, which also covers Ollama's OpenAI-compatible endpoint.
    """
    return OpenAIConfig(
        model=settings.llm_model,
        api_key=settings.openai_api_key.get_secret_value(),
        base_url=str(settings.llm_base_url),
        timeout=settings.llm_timeout_seconds,
        # The openai client retries twice of its own accord. Stacked under AG2's schema
        # retries that is up to nine calls for one decision, and it turns a hung backend
        # into three timeouts in a row. Retrying is the engine's job, in one place.
        max_retries=0,
    )
