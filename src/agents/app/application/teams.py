"""A team is a typed list of steps, and the list is the whole of the flow.

Decision 4: a step reads named earlier results, never a transcript. `reads` is where that
becomes visible - looking at a TeamSpec tells you exactly what each agent sees, which is
the thing that used to be buried in f-strings.

Kept in Python rather than YAML on purpose. With one team a configuration format would be
guessing at what distinguishes two teams; with a second team that is known. The roadmap's
condition for moving is a real need, not a stage number.

The structure is checked in __post_init__, so an invalid team cannot be constructed at
all - importing this module is the startup validation. Whether the prompt files exist is
checked where they are read, because that is a fact about the filesystem rather than about
the specification.
"""

from collections.abc import Collection
from dataclasses import dataclass, field
from pathlib import Path

from pydantic import BaseModel

from app.domain.facts import FactSheet
from app.domain.signals import TradeView
from app.domain.steps import MarketRead, RiskAssessment

PROMPT_ROOT = Path(__file__).resolve().parent.parent / "teams"


@dataclass(frozen=True)
class StepSpec:
    """One agent turn: who, what it reads, and what it must produce."""

    # A free name, unique within the team. It is also the key in
    # TAS_LLM__ROLES__<ROLE>__*, which is why it is lowercase with underscores.
    role: str

    # The role's instructions, and nothing else - no placeholders. The run's data travels
    # in the message as a delimited JSON block, so nothing can be injected through the
    # file, and the file is the whole of the role's instruction, which is what makes it
    # meaningful to hash.
    prompt_file: Path

    output_schema: type[BaseModel]

    # Which earlier results this step is allowed to see. The default is the fact sheet
    # alone, so a step that wants more has to say so where anyone can read it.
    reads: tuple[type[BaseModel], ...] = (FactSheet,)

    # Whether this step is shown past analyses of the same instrument. A flag rather
    # than an entry in `reads`, because `reads` names schemas produced *inside* this run
    # and memory comes from outside it - a different kind of input, and worth being able
    # to see as one. Validated below against MarketRead, which is what memory is matched
    # on: a step that cannot see today's reading has no query to recall with.
    sees_memory: bool = False

    # Whether the portfolio's existing holding reaches this step. A flag rather than a
    # hardcoded role name, so the flow stays readable in the spec. The risk budget is
    # deliberately not here: under decision 1 no agent produces an amount, so a budget is
    # a figure it cannot act on, and a figure in a prompt is one a model starts reasoning
    # about and sometimes quotes back.
    sees_position: bool = False

    # No per-step model override. LlmSettings.roles already does that from the
    # environment, and two ways to change the same thing is one too many.

    def __post_init__(self) -> None:
        if not self.role or self.role != self.role.strip():
            raise ValueError(f"a step's role must be a name, not {self.role!r}")

        # Memory is matched on the analyst's reading, at both ends: a run is embedded by
        # its MarketRead and recalled with today's. A step given memory without being able
        # to see that reading would be asking a question it cannot phrase.
        if self.sees_memory and MarketRead not in self.reads:
            raise ValueError(
                f"step '{self.role}' is given memory but does not read "
                f"{MarketRead.__name__}, which is what memory is matched on"
            )


@dataclass(frozen=True)
class TeamSpec:
    """An ordered list of steps that ends in a view the engine can act on."""

    id: str
    # Which instruments this team may be asked about. {"equity"} until derivatives exist;
    # a team asked about something it does not cover is a 422 rather than a bad answer.
    instrument_types: frozenset[str]
    steps: tuple[StepSpec, ...] = field(default=())

    def __post_init__(self) -> None:
        if not self.id:
            raise ValueError("a team needs an id")
        if not self.instrument_types:
            raise ValueError(f"team '{self.id}' covers no instrument types")
        if not self.steps:
            raise ValueError(f"team '{self.id}' has no steps")

        roles = [step.role for step in self.steps]
        if len(set(roles)) != len(roles):
            raise ValueError(f"team '{self.id}' repeats a role: {sorted(roles)}")

        # Results are indexed by schema type, so two steps producing the same one would
        # silently overwrite each other and the second would win for no stated reason.
        produced = [step.output_schema for step in self.steps]
        if len(set(produced)) != len(produced):
            raise ValueError(
                f"team '{self.id}' has two steps producing the same schema: "
                f"{sorted(s.__name__ for s in produced)}"
            )

        if self.steps[-1].output_schema is not TradeView:
            raise ValueError(
                f"team '{self.id}' ends with {self.steps[-1].output_schema.__name__}, but the "
                f"last step has to produce {TradeView.__name__} - that is the answer."
            )

        # A step may read the fact sheet or anything an *earlier* step produced. Reading a
        # later step's schema would be a cycle; reading its own would be circular too.
        available: set[type[BaseModel]] = {FactSheet}
        for step in self.steps:
            missing = [schema for schema in step.reads if schema not in available]
            if missing:
                raise ValueError(
                    f"step '{step.role}' in team '{self.id}' reads "
                    f"{sorted(s.__name__ for s in missing)}, which no earlier step produces"
                )
            available.add(step.output_schema)

    @property
    def roles(self) -> tuple[str, ...]:
        return tuple(step.role for step in self.steps)


def _prompt(team_id: str, role: str) -> Path:
    return PROMPT_ROOT / team_id / "prompts" / f"{role}.md"


# The thin team the stage delivers. What it is made of is deliberately not settled here:
# stage 4 measures outcomes per team_version, and that is what decides the shape. Note
# what the portfolio manager does *not* read - the fact sheet. It weighs two assessments
# and never sees a raw number, which is also why it cannot invent one.
DEFAULT_TEAM = TeamSpec(
    id="default",
    instrument_types=frozenset({"equity"}),
    steps=(
        StepSpec(
            role="market_analyst",
            prompt_file=_prompt("default", "market_analyst"),
            output_schema=MarketRead,
            reads=(FactSheet,),
        ),
        StepSpec(
            role="risk_manager",
            prompt_file=_prompt("default", "risk_manager"),
            output_schema=RiskAssessment,
            reads=(FactSheet, MarketRead),
        ),
        StepSpec(
            role="portfolio_manager",
            prompt_file=_prompt("default", "portfolio_manager"),
            output_schema=TradeView,
            reads=(MarketRead, RiskAssessment),
            sees_position=True,
        ),
    ),
)

TEAMS: dict[str, TeamSpec] = {DEFAULT_TEAM.id: DEFAULT_TEAM}


def load_prompts(spec: TeamSpec) -> dict[str, str]:
    """Every step's instructions, read once at startup.

    This is where a missing prompt file is caught. It is not part of TeamSpec's own
    validation, because whether a file is on disk is a fact about the filesystem rather
    than about the specification - and the specification has to stay constructible in a
    test without touching one.
    """
    prompts: dict[str, str] = {}

    for step in spec.steps:
        try:
            text = step.prompt_file.read_text(encoding="utf-8")
        except OSError as e:
            raise ValueError(
                f"team '{spec.id}' step '{step.role}': cannot read {step.prompt_file}"
            ) from e

        if not text.strip():
            # An empty file would leave the role with no instructions at all, and the
            # model would answer from the schema alone.
            raise ValueError(f"team '{spec.id}' step '{step.role}': {step.prompt_file} is empty")

        prompts[step.role] = text

    return prompts


def all_roles(teams: Collection[TeamSpec]) -> frozenset[str]:
    """Every role any team uses. This is what a per-role model override is checked against."""
    return frozenset(role for team in teams for role in team.roles)
