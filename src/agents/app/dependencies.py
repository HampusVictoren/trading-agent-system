"""What the lifespan builds, and how a route gets hold of it."""

from dataclasses import dataclass

from ag2.config import OpenAIConfig
from fastapi import Request

from app.infrastructure.db.memory import MemoryStore


@dataclass(frozen=True)
class Resources:
    """Everything that is expensive to build and safe to share for the process's lifetime."""

    llm_config: OpenAIConfig
    memory: MemoryStore


def get_resources(request: Request) -> Resources:
    resources: Resources = request.app.state.resources
    return resources
