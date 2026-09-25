"""What the lifespan builds, and how a route gets hold of it."""

from dataclasses import dataclass

import httpx2
from fastapi import Request

from app.application.pipeline import SignalPipeline
from app.application.ports import MarketDataProvider, Memory, OutcomeStore
from app.infrastructure.llm.provider import ModelConfigs


@dataclass(frozen=True)
class Resources:
    """Everything that is expensive to build and safe to share for the process's lifetime."""

    models: ModelConfigs

    # Built at startup with every team validated, every prompt read and every model
    # configuration constructed.
    pipeline: SignalPipeline

    # The same instance the pipeline runs on, so the quote endpoint and the fact sheet share
    # one cache: a price fetched for an analysis is the price a valuation gets, and the TTL
    # is spent once rather than twice.
    market: MarketDataProvider

    # Semantic memory over this service's own journal, and only over the part of it the
    # engine has measured. The port rather than the class, like the two fields around
    # it: what a route is handed is a capability, not an implementation.
    memory: Memory

    # Written by the engine after a measurement sweep, read by memory. Separate from the
    # journal because one is this service's own working and the other is a copy of
    # somebody else's record.
    outcomes: OutcomeStore
    http_client: httpx2.AsyncClient

    # None when the configured provider has no OpenAI-shaped model list to probe.
    llm_base_url: str | None


def get_resources(request: Request) -> Resources:
    resources: Resources = request.app.state.resources
    return resources
