"""Runs one agent turn with AG2, and turns its failures into this service's vocabulary.

The StepRunner port exists so that the pipeline never sees an openai exception. This is
the only file that catches one, which is also the only place the mapping can go wrong -
in stage 1 a Polly timeout reached the engine's worker looking like a bug for exactly this
reason, one service over.
"""

import logging
from collections.abc import Mapping

from ag2 import Agent
from ag2.exceptions import AG2Error
from openai import APIConnectionError, APIStatusError, APITimeoutError
from pydantic import BaseModel, ValidationError

from app.application.errors import (
    AgentChainFailed,
    AgentResponseInvalid,
    LlmFailed,
    LlmTimeout,
    LlmUnreachable,
)
from app.application.teams import TeamSpec
from app.infrastructure.llm.provider import ModelConfigs

logger = logging.getLogger(__name__)

# AG2 asks the model again with the validation error attached. Two retries is three HTTP
# calls for one step in the worst case, which is why the client's own retries are off.
SCHEMA_RETRIES = 2


def build_agents(
    spec: TeamSpec, prompts: Mapping[str, str], models: ModelConfigs
) -> dict[str, Agent]:
    """One agent per step, built once at startup.

    Safe to share across requests: ask() runs on a fresh MemoryStream unless one is passed
    in, so no history leaks from one analysis into the next.
    """
    return {
        step.role: Agent(
            step.role,
            prompt=prompts[step.role],
            config=models.for_role(step.role),
        )
        for step in spec.steps
    }


class Ag2StepRunner:
    """A StepRunner over a team's agents."""

    def __init__(self, agents: Mapping[str, Agent]) -> None:
        self._agents = agents

    async def run_step[T: BaseModel](self, role: str, message: str, schema: type[T]) -> T:
        agent = self._agents[role]

        try:
            reply = await agent.ask(message, response_schema=schema)
            result = await reply.content(retries=SCHEMA_RETRIES)

        # APITimeoutError is a subclass of APIConnectionError, so it has to come first or
        # a timeout would be reported as a backend that could not be reached at all.
        except APITimeoutError as e:
            raise LlmTimeout(f"Step '{role}' did not get an answer in time.") from e
        except APIConnectionError as e:
            raise LlmUnreachable(f"Step '{role}' could not reach the LLM backend.") from e
        except APIStatusError as e:
            raise LlmFailed(f"The LLM backend answered {e.status_code} for step '{role}'.") from e
        except ValidationError as e:
            # ask(response_schema=...) has already asked again with the validation error
            # attached; this is what is left after those retries.
            raise AgentResponseInvalid(
                f"Step '{role}' never answered in the shape of {schema.__name__}: {e}"
            ) from e
        except AG2Error as e:
            raise AgentChainFailed(f"Step '{role}' failed inside AG2: {e}") from e

        if result is None:
            # Otherwise the literal None would be handed to the next step as its input.
            raise AgentResponseInvalid(f"Step '{role}' returned no answer at all.")

        return result
