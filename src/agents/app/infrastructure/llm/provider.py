"""One model specification in, one AG2 model configuration out.

AG2's own `ModelConfig` is the return type - there is no wrapper of this project's own,
because AG2 already declares the protocol the pipeline needs. That is also what lets
`ag2.testing.TestConfig` stand in for a real provider without any code noticing.

**The lazy import the roadmap asks for is already AG2's.** `ag2.config` exports a
placeholder for every provider whose extra is not installed, and constructing one raises
`ImportError: AnthropicConfig requires optional dependencies. Install with
"ag2[anthropic]"`. Because configurations are built once in the lifespan, that arrives as
a startup failure with an install hint rather than a broken request an hour later.

**The four configurations do not take the same arguments**, which is the whole reason this
file is more than a dictionary lookup. mypy checks each call against the real dataclass
even while the extra is missing, so the table below is read off the signatures rather than
guessed:

| Provider  | endpoint argument | api_key | timeout | seed | max_retries |
|-----------|-------------------|---------|---------|------|-------------|
| OpenAI    | base_url          | yes     | yes     | yes  | yes         |
| Anthropic | base_url          | yes     | yes     | no   | yes         |
| xAI       | api_host (host)   | yes     | yes     | yes  | no          |
| Ollama    | host              | no      | **no**  | yes  | no          |

Only the OpenAI family has actually been run. The others are written against their real
signatures but their extras are not installed, so nothing has constructed one.
"""

import logging

from ag2.config import (
    AnthropicConfig,
    ModelConfig,
    OllamaConfig,
    OpenAIConfig,
    XAIConfig,
)

from app.settings import ModelSpec, Provider

logger = logging.getLogger(__name__)

# The openai client retries twice of its own accord. Stacked under AG2's schema retries
# that is up to nine calls for one decision, and it turns a hung backend into three
# timeouts in a row. Retrying is the engine's job, in one place.
CLIENT_RETRIES = 0


def build_model_config(spec: ModelSpec) -> ModelConfig:
    """Built once per role in the lifespan, never per request."""
    key = spec.api_key.get_secret_value()

    match spec.provider:
        case Provider.OPENAI_COMPATIBLE | Provider.OPENAI:
            # One branch for both: the difference is only whether a base URL is set, and
            # the settings already refuse openai_compatible without one.
            return OpenAIConfig(
                model=spec.model,
                api_key=key,
                base_url=str(spec.base_url) if spec.base_url else None,
                temperature=spec.temperature,
                seed=spec.seed,
                timeout=spec.timeout_s,
                max_retries=CLIENT_RETRIES,
            )

        case Provider.ANTHROPIC:
            # No seed here; the settings refuse one for this provider rather than letting
            # it be dropped, because a dropped seed looks like reproducibility.
            return AnthropicConfig(
                model=spec.model,
                api_key=key,
                base_url=str(spec.base_url) if spec.base_url else None,
                temperature=spec.temperature,
                timeout=spec.timeout_s,
                max_retries=CLIENT_RETRIES,
            )

        case Provider.XAI:
            xai = XAIConfig(
                model=spec.model,
                api_key=key,
                temperature=spec.temperature,
                seed=spec.seed,
                timeout=spec.timeout_s,
            )
            # api_host is a bare host, not a URL, so the scheme is dropped rather than
            # carried into something that would read as part of the hostname. Applied
            # through copy() so the provider's own default survives when none is set;
            # copy() is part of the ModelConfig protocol, so it costs nothing extra.
            if spec.base_url is not None and spec.base_url.host is not None:
                return xai.copy(api_host=spec.base_url.host)
            return xai

        case Provider.OLLAMA:
            # AG2's native Ollama client takes neither an api_key nor a timeout. The
            # missing timeout matters: it is what makes a 504 reachable at all, and
            # without it a hung backend holds the request until the caller gives up. Said
            # out loud at startup rather than left to be discovered during an incident.
            logger.warning(
                "Provider 'ollama' does not carry timeout_s=%.0fs: AG2's native Ollama "
                "client has no timeout, so a hung backend will not become a 504. Reach "
                "Ollama through provider 'openai_compatible' at its /v1 endpoint to keep "
                "the timeout.",
                spec.timeout_s,
            )
            ollama = OllamaConfig(
                model=spec.model,
                temperature=spec.temperature,
                seed=spec.seed,
            )
            if spec.base_url is not None:
                return ollama.copy(host=str(spec.base_url))
            return ollama
