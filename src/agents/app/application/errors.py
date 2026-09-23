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


class UnknownTeam(AnalysisError):
    """The engine asked for a team setup that does not exist.

    A 422 rather than a fallback to the default team: answering with a different team than
    the one that was asked for would put the wrong team_version in stage 4's statistics.
    """

    error_code = "unknown_team"


class InstrumentNotSupported(AnalysisError):
    """The team does not cover this kind of instrument.

    Equity is the only kind today. A derivative reaching an equity team would get an
    answer built on ratios that do not apply to it.
    """

    error_code = "instrument_not_supported"


class MarketDataUnavailable(AnalysisError):
    """The market-data source could not be reached, or did not answer in time.

    Separate from the LLM failures above because it is a different dependency: the engine
    can act on "prices are down" differently from "the model is down", and stage 4 will
    want to tell the two apart when it explains a gap in the decision history.
    """

    error_code = "market_data_unavailable"


class InstrumentNotFound(AnalysisError):
    """A well-formed symbol that no market-data source knows.

    A caller error rather than a service failure, so it answers 422 - but not the same 422
    as a malformed ticker, because "MSFT typed as MSF" and "MSFT does not exist" call for
    different fixes.
    """

    error_code = "instrument_not_found"
