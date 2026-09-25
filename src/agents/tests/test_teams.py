"""A team is data, and the rules about it are enforced where it is constructed.

Importing app.application.teams runs every check below against the real team, so a team
that breaks one of them cannot even be imported - which is the startup validation the
roadmap asks for, without a separate startup step.
"""

from pathlib import Path

import pytest
from pydantic import BaseModel

from app.application.teams import (
    DEFAULT_TEAM,
    MEMORY_TEAM,
    TEAMS,
    StepSpec,
    TeamSpec,
    all_roles,
    load_prompts,
)
from app.domain.facts import FactSheet
from app.domain.signals import TradeView
from app.domain.steps import MarketRead, RiskAssessment

SOMEWHERE = Path("prompts/whatever.md")


class Other(BaseModel):
    pass


def step(role: str, output_schema: type[BaseModel], **overrides) -> StepSpec:
    return StepSpec(role=role, prompt_file=SOMEWHERE, output_schema=output_schema, **overrides)


def team(*steps: StepSpec, team_id: str = "t") -> TeamSpec:
    return TeamSpec(id=team_id, instrument_types=frozenset({"equity"}), steps=steps)


class TestTheDefaultTeam:
    def test_it_is_three_steps_ending_in_a_view(self):
        assert DEFAULT_TEAM.roles == ("market_analyst", "risk_manager", "portfolio_manager")
        assert DEFAULT_TEAM.steps[-1].output_schema is TradeView

    def test_every_prompt_file_exists(self):
        # Checked here rather than in __post_init__: whether a file is on disk is a fact
        # about the filesystem, not about the specification. It is checked again where the
        # prompts are loaded, which is at startup.
        for spec in DEFAULT_TEAM.steps:
            assert spec.prompt_file.is_file(), spec.prompt_file

    def test_the_portfolio_manager_never_sees_the_raw_numbers(self):
        # The sharpest thing `reads` buys. The portfolio manager weighs two assessments and
        # is given no figure at all, which is also why it cannot invent one - and the
        # engine sizes the order from the fact sheet's price, not from anything it says.
        pm = DEFAULT_TEAM.steps[-1]

        assert FactSheet not in pm.reads
        assert set(pm.reads) == {MarketRead, RiskAssessment}

    def test_only_the_portfolio_manager_is_told_about_a_holding(self):
        assert [spec.role for spec in DEFAULT_TEAM.steps if spec.sees_position] == [
            "portfolio_manager"
        ]

    def test_it_is_registered_under_its_id(self):
        assert TEAMS["default"] is DEFAULT_TEAM

    def test_it_only_covers_equities(self):
        assert DEFAULT_TEAM.instrument_types == frozenset({"equity"})


class TestATeamThatCouldNotWork:
    def test_a_team_that_does_not_end_in_a_view_is_refused(self):
        # The pipeline returns the last step's result. A team ending in a RiskAssessment
        # would get all the way to the end and then have nothing to answer with.
        with pytest.raises(ValueError, match="TradeView"):
            team(step("a", MarketRead), step("b", RiskAssessment, reads=(MarketRead,)))

    def test_even_a_subclass_of_the_view_is_refused_as_the_last_step(self):
        # Identity, not isinstance. A subclass would serialise fields the engine's
        # TradeSignalDto forbids, and the answer would be refused on arrival instead of
        # here, where the reason is visible.
        class WiderView(TradeView):
            note: str = ""

        with pytest.raises(ValueError, match="TradeView"):
            team(step("a", WiderView))

    def test_two_steps_producing_the_same_schema_are_refused(self):
        # Results are indexed by schema type, so the second would overwrite the first and
        # win for no stated reason.
        with pytest.raises(ValueError, match="same schema"):
            team(
                step("a", MarketRead),
                step("b", MarketRead),
                step("c", TradeView, reads=(MarketRead,)),
            )

    def test_a_repeated_role_is_refused(self):
        with pytest.raises(ValueError, match="repeats a role"):
            team(step("a", MarketRead), step("a", TradeView, reads=(MarketRead,)))

    def test_reading_a_later_steps_result_is_refused(self):
        # It is a cycle: the analyst would need the risk manager's answer to produce the
        # input the risk manager needs.
        with pytest.raises(ValueError, match="RiskAssessment"):
            team(
                step("a", MarketRead, reads=(FactSheet, RiskAssessment)),
                step("b", RiskAssessment, reads=(MarketRead,)),
                step("c", TradeView, reads=(RiskAssessment,)),
            )

    def test_reading_its_own_result_is_refused(self):
        with pytest.raises(ValueError, match="MarketRead"):
            team(
                step("a", MarketRead, reads=(FactSheet, MarketRead)),
                step("b", TradeView, reads=(MarketRead,)),
            )

    def test_reading_something_nobody_produces_is_refused(self):
        with pytest.raises(ValueError, match="Other"):
            team(step("a", TradeView, reads=(Other,)))

    def test_a_team_with_no_steps_is_refused(self):
        with pytest.raises(ValueError, match="no steps"):
            TeamSpec(id="t", instrument_types=frozenset({"equity"}))

    def test_a_team_covering_no_instrument_is_refused(self):
        with pytest.raises(ValueError, match="no instrument types"):
            TeamSpec(id="t", instrument_types=frozenset(), steps=(step("a", TradeView),))

    def test_a_team_without_an_id_is_refused(self):
        with pytest.raises(ValueError, match="needs an id"):
            TeamSpec(id="", instrument_types=frozenset({"equity"}), steps=(step("a", TradeView),))

    @pytest.mark.parametrize("role", ["", " ", "trailing "])
    def test_a_role_that_is_not_a_name_is_refused(self, role):
        # The role is a key in TAS_LLM__ROLES__<ROLE>__*, so a stray space would make an
        # override impossible to spell.
        with pytest.raises(ValueError, match="role must be a name"):
            step(role, TradeView)


class TestTheFactSheetIsAlwaysAvailable:
    def test_a_first_step_may_read_it_without_anyone_producing_it(self):
        # It is computed before any agent runs, so it is the one input that is not a
        # step's output.
        assert team(step("a", TradeView, reads=(FactSheet,))).roles == ("a",)

    def test_a_step_may_read_nothing_at_all(self):
        assert team(step("a", TradeView, reads=())).steps[0].reads == ()


class TestAllRoles:
    def test_it_collects_every_role_across_teams(self):
        # This is what a per-role model override is checked against, so a role missing
        # here would make a valid override look like a typo.
        assert all_roles(TEAMS.values()) == frozenset(DEFAULT_TEAM.roles)

    def test_it_merges_teams_that_share_a_role(self):
        second = team(step("market_analyst", TradeView), team_id="other")

        assert all_roles([DEFAULT_TEAM, second]) == frozenset(DEFAULT_TEAM.roles)


class TestMemoryIsMatchedOnTheAnalystsReading:
    """A step is embedded by its MarketRead and recalled with today's. A step that cannot
    see that reading has no query to recall with, so the specification refuses it rather
    than the pipeline discovering it at run time."""

    def test_a_step_given_memory_without_the_reading_is_refused(self):
        with pytest.raises(ValueError, match="memory is matched on"):
            step("risk_manager", RiskAssessment, reads=(FactSheet,), sees_memory=True)

    def test_a_step_given_memory_with_the_reading_is_fine(self):
        allowed = step(
            "risk_manager", RiskAssessment, reads=(FactSheet, MarketRead), sees_memory=True
        )

        assert allowed.sees_memory

    def test_the_baseline_team_reads_no_memory_at_all(self):
        # The baseline is of the thin three-step team. Adding memory to it would change
        # team_version and restart the measurement the whole stage exists to collect.
        assert not any(s.sees_memory for s in DEFAULT_TEAM.steps)


class TestTheMemoryTeam:
    """`default-memory` is `default` with one thing added, and nothing else different.

    That is what makes comparing their outcomes an experiment rather than an observation:
    if three things differed, a difference in hit rate would have three candidate causes.
    """

    def test_it_is_registered_beside_the_baseline_team(self):
        assert TEAMS["default-memory"] is MEMORY_TEAM
        assert TEAMS["default"] is DEFAULT_TEAM

    def test_only_the_risk_manager_is_given_memory(self):
        given = [s.role for s in MEMORY_TEAM.steps if s.sees_memory]

        assert given == ["risk_manager"]

    def test_it_has_the_same_steps_in_the_same_order(self):
        assert MEMORY_TEAM.roles == DEFAULT_TEAM.roles
        assert [s.output_schema for s in MEMORY_TEAM.steps] == [
            s.output_schema for s in DEFAULT_TEAM.steps
        ]
        assert [s.reads for s in MEMORY_TEAM.steps] == [s.reads for s in DEFAULT_TEAM.steps]

    def test_the_two_unchanged_steps_read_the_very_same_prompt_file(self):
        # The same file, not a copy of it. Two copies are two files free to drift, and the
        # drift would land inside the one comparison this team exists to make.
        unchanged = {"market_analyst", "portfolio_manager"}
        for memory_step, baseline_step in zip(MEMORY_TEAM.steps, DEFAULT_TEAM.steps, strict=True):
            if memory_step.role in unchanged:
                assert memory_step.prompt_file == baseline_step.prompt_file

    def test_the_risk_manager_has_a_prompt_of_its_own_that_explains_the_memory(self):
        prompts = load_prompts(MEMORY_TEAM)
        instructions = prompts["risk_manager"]

        assert instructions != load_prompts(DEFAULT_TEAM)["risk_manager"]
        # The two things a model gets wrong about this block if nobody says them: that the
        # figure is measured against the index, and that an empty memory is ignorance
        # rather than good news.
        assert "mot index" in instructions
        assert "tomt minne" in instructions.lower()
