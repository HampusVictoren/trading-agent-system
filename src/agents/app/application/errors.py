"""The vocabulary of expected failures in an analysis.

Every one of these is something the agent chain can legitimately run into. They exist so
that the API layer can answer honestly instead of returning a HOLD that looks like a
decision - the engine cannot tell "the model chose HOLD" from "Ollama is down" otherwise.

The error_code is part of the cross-service contract and is safe to show a caller. The
message is for the server's own log and may name internals, so it never reaches a response.
Mapping a code to an HTTP status is the API layer's job, in app/api/errors.py.
"""


class AnalysisError(Exception):
    """Base class for an analysis that could not produce a decision."""

    error_code = "analysis_failed"


class LlmUnreachable(AnalysisError):
    """The LLM backend could not be reached at all."""

    error_code = "llm_unreachable"


class LlmTimeout(AnalysisError):
    """The LLM backend accepted the request but did not answer in time."""

    error_code = "llm_timeout"


class LlmFailed(AnalysisError):
    """The LLM backend answered with an error status."""

    error_code = "llm_failed"


class AgentResponseInvalid(AnalysisError):
    """The model answered, but never in the agreed shape, even after retries."""

    error_code = "agent_response_invalid"


class AgentChainFailed(AnalysisError):
    """A step in the chain failed for a reason of AG2's own, such as a tool."""

    error_code = "agent_chain_failed"
