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
    )
