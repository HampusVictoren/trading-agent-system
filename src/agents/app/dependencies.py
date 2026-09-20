"""What the lifespan builds, and how a route gets hold of it."""

from dataclasses import dataclass

import httpx2
from ag2.config import OpenAIConfig
from fastapi import Request

from app.infrastructure.db.memory import MemoryStore


@dataclass(frozen=True)
class Resources:
    """Everything that is expensive to build and safe to share for the process's lifetime."""

    llm_config: OpenAIConfig
    memory: MemoryStore
    http_client: httpx2.AsyncClient
    llm_base_url: str


def get_resources(request: Request) -> Resources:
    resources: Resources = request.app.state.resources
    return resources
