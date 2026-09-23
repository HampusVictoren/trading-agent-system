"""What the lifespan builds, and how a route gets hold of it."""

from dataclasses import dataclass

import httpx2
from fastapi import Request

from app.application.pipeline import SignalPipeline
from app.infrastructure.db.memory import MemoryStore
from app.infrastructure.llm.provider import ModelConfigs


@dataclass(frozen=True)
class Resources:
    """Everything that is expensive to build and safe to share for the process's lifetime."""

    models: ModelConfigs

    # Built at startup with every team validated, every prompt read and every model
    # configuration constructed. No route calls it until the switch-over adds /v1/signals.
    pipeline: SignalPipeline
    memory: MemoryStore
    http_client: httpx2.AsyncClient

    # None when the configured provider has no OpenAI-shaped model list to probe.
    llm_base_url: str | None


def get_resources(request: Request) -> Resources:
    resources: Resources = request.app.state.resources
    return resources
