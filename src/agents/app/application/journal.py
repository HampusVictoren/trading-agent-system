"""What one analysis leaves behind.

The engine stores what the engine saw: the request it sent, the signal it got back and
what it did about it. This is the other half, and it stays on this side. Widening the
contract with the fact sheet and every step's working would make the engine the owner of
this service's internals - the shared-database problem, moved to HTTP.

What joins them is the correlation id, and the join is made when somebody asks a question
rather than kept as a foreign key. That is the whole of the integration: two schemas, two
roles, one identifier that both happen to write down.
"""

from dataclasses import dataclass

from pydantic import BaseModel

from app.domain.facts import FactSheet


@dataclass(frozen=True)
class RecordedStep:
    """One step's answer, with enough about the step to make sense of it later."""

    # Its place in the team's order. Stored rather than derived, because the order is part
    # of what a team_version means and a later team may not have the same steps at all.
    ordinal: int

    role: str

    output: BaseModel

    @property
    def schema_name(self) -> str:
        """Which schema the answer was validated against.

        The type's own name, not a string anyone maintains. Stored beside the JSON because
        the JSON alone cannot say what it was meant to be - and a step that produced a
        different schema under an older team_version is exactly the thing worth seeing.
        """
        return type(self.output).__name__


@dataclass(frozen=True)
class AnalysisRun:
    """One call to POST /v1/signals that produced an answer, as it will be stored."""

    correlation_id: str
    team_id: str
    team_version: str
    instrument_type: str
    symbol: str

    # The inputs. Everything the first step started from, which is what makes a replay the
    # same question rather than today's question.
    facts: FactSheet

    steps: tuple[RecordedStep, ...]
