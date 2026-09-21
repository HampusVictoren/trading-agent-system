"""What one agent step hands to the next.

Decision 4: a step reads named earlier results, never a transcript. These are those
results, and they are the whole of what crosses a step boundary - which makes the schema
the ceiling on the cost, and on what external text can travel.

Every field an agent fills is either a category or a capped string. Categories are not
just cheaper: a model choosing between three labels is more reliable than the same model
producing a calibrated number, and the number would invite arithmetic that code already
did in the fact sheet.
"""

from enum import StrEnum
from typing import Annotated

from pydantic import BaseModel, ConfigDict, Field

# Shorter than the thesis on the wire, because these are working notes between steps and
# every one of them is paid for again in the next step's prompt.
MAX_NOTE_LENGTH = 200
MAX_NOTES = 3

type Note = Annotated[str, Field(min_length=1, max_length=MAX_NOTE_LENGTH)]
type Notes = Annotated[list[Note], Field(max_length=MAX_NOTES)]


class Trend(StrEnum):
    UP = "UP"
    DOWN = "DOWN"
    SIDEWAYS = "SIDEWAYS"


class Valuation(StrEnum):
    CHEAP = "CHEAP"
    FAIR = "FAIR"
    EXPENSIVE = "EXPENSIVE"
    # A share with no P/E has no valuation to read. Without this the model would have to
    # pick one of the three and pretend.
    UNKNOWN = "UNKNOWN"


class Severity(StrEnum):
    LOW = "LOW"
    MEDIUM = "MEDIUM"
    HIGH = "HIGH"


class MarketRead(BaseModel):
    """The analyst's reading of the fact sheet."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    trend: Trend
    valuation: Valuation
    # The part that is not computable: what the numbers mean together. The numbers
    # themselves are not repeated here - the next step that needs them reads the fact
    # sheet, and copying them through a model is how they get changed on the way.
    observations: Notes


class RiskAssessment(BaseModel):
    """The risk manager's reading of the analyst's case."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    downside: Severity

    # Advisory, not a control. The engine's RiskEngine is what can actually stop a trade;
    # an agent can only decline to argue for one. Kept because a portfolio manager that
    # ignores a flagged veto is a thing stage 4 should be able to see in the outcomes.
    veto: bool

    risks: Notes
